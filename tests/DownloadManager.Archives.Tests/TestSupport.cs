using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;

namespace DownloadManager.Archives.Tests;

/// <summary>The only things these tests use from outside the test process: WinRAR's two console tools.</summary>
internal static class RarTools
{
    private const string WinRarFolder = @"C:\Program Files\WinRAR";

    public const string Password = "Test-Pw_123";

    public static string UnrarPath => Path.Combine(WinRarFolder, "UnRAR.exe");

    public static string RarPath => Path.Combine(WinRarFolder, "Rar.exe");

    public static string? UnrarMissing => File.Exists(UnrarPath) ? null : $"UnRAR.exe is not installed at {UnrarPath}, so extraction cannot run here.";

    public static string? RarMissing =>
        !File.Exists(UnrarPath) ? UnrarMissing
        : File.Exists(RarPath) ? null
        : $"Rar.exe is not installed at {RarPath}, so real RAR5 fixtures cannot be generated here.";

    public static async Task RunRarAsync(string workingDirectory, params string[] arguments)
    {
        var (code, output) = await RunAsync(RarPath, workingDirectory, arguments);
        Assert.True(code == 0, $"Rar.exe exited {code}: {output}");
    }

    public static async Task<(int Code, string Output)> RunAsync(string tool, string workingDirectory, params string[] arguments)
    {
        var info = new ProcessStartInfo(tool)
        {
            WorkingDirectory = workingDirectory,
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        };
        foreach (var argument in arguments) info.ArgumentList.Add(argument);
        using var process = Process.Start(info)!;
        process.StandardInput.Close();
        var outputTask = process.StandardOutput.ReadToEndAsync();
        var errorTask = process.StandardError.ReadToEndAsync();
        await process.WaitForExitAsync();
        return (process.ExitCode, await outputTask + await errorTask);
    }
}

/// <summary>A fact needing UnRAR, skipped with the reason when it is not installed.</summary>
public sealed class RequiresUnrarFactAttribute : FactAttribute
{
    public RequiresUnrarFactAttribute()
    {
        Skip = RarTools.UnrarMissing;
    }
}

/// <summary>A fact needing UnRAR and Rar, skipped with the reason when either is not installed.</summary>
public sealed class RequiresRarFactAttribute : FactAttribute
{
    public RequiresRarFactAttribute()
    {
        Skip = RarTools.RarMissing;
    }
}

public sealed class RequiresUnrarTheoryAttribute : TheoryAttribute
{
    public RequiresUnrarTheoryAttribute()
    {
        Skip = RarTools.UnrarMissing;
    }
}

/// <summary>A temp folder for one test, with a space and a non-ASCII letter so argument handling is exercised everywhere.</summary>
public sealed class Workspace : IDisposable
{
    public Workspace()
    {
        // Under the build output, not %TEMP%: a test host started from the agent sandbox's
        // low-integrity repo folder is refused writes to the user's temp folder.
        Root = Path.Combine(AppContext.BaseDirectory, "work é", Guid.NewGuid().ToString("N")[..12]);
        Directory.CreateDirectory(Root);
        Archives = Directory.CreateDirectory(Path.Combine(Root, "archives")).FullName;
        Source = Directory.CreateDirectory(Path.Combine(Root, "source")).FullName;
        Destination = Path.Combine(Root, "out put");
        Staging = Path.Combine(Root, "staging");
    }

    public string Root { get; }

    public string Archives { get; }

    public string Source { get; }

    public string Destination { get; }

    public string Staging { get; }

    public string Archive(string name) => Path.Combine(Archives, name);

    public string Output(string relative) => Path.Combine(Destination, relative);

    public string WriteArchive(string name, byte[] bytes)
    {
        var path = Archive(name);
        File.WriteAllBytes(path, bytes);
        return path;
    }

    public string WriteSource(string relative, string text) => WriteSource(relative, Encoding.UTF8.GetBytes(text));

    public string WriteSource(string relative, byte[] bytes)
    {
        var path = Path.Combine(Source, relative);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllBytes(path, bytes);
        return path;
    }

    /// <summary>Two small files, one in a folder, and one big enough to span volumes and show progress.</summary>
    public void WriteStandardSource()
    {
        WriteSource("readme.txt", "hello from the archive\n");
        WriteSource(@"sub\deep\note.txt", "nested\n");
        WriteSource(@"sub\big.bin", RandomBytes(600 * 1024, 11));
    }

    /// <summary>Runs Rar on everything in Source. Stored, so it is quick and volumes split predictably.</summary>
    public async Task<string> BuildAsync(string archiveName, params string[] switches)
    {
        var arguments = new List<string> { "a", "-idq", "-ep1", "-r", "-m0", "-cfg-" };
        arguments.AddRange(switches);
        arguments.Add(Archive(archiveName));
        arguments.Add(Path.Combine(Source, "*"));
        await RarTools.RunRarAsync(Archives, [.. arguments]);
        return Archive(archiveName);
    }

    public void AssertSameAsSource()
    {
        var expected = Directory.EnumerateFiles(Source, "*", SearchOption.AllDirectories).Select(file => Path.GetRelativePath(Source, file)).Order().ToList();
        var actual = Directory.EnumerateFiles(Destination, "*", SearchOption.AllDirectories).Select(file => Path.GetRelativePath(Destination, file)).Order().ToList();
        Assert.Equal(expected, actual);
        foreach (var file in expected)
        {
            Assert.Equal(Hash(Path.Combine(Source, file)), Hash(Path.Combine(Destination, file)));
        }
    }

    public void AssertNoStagingLeft() => Assert.Empty(StagingArea.FindLeftovers(Staging));

    public void AssertDestinationUntouched()
    {
        if (Directory.Exists(Destination)) Assert.Empty(Directory.EnumerateFileSystemEntries(Destination));
    }

    public void Dispose()
    {
        try
        {
            foreach (var file in new DirectoryInfo(Root).EnumerateFiles("*", SearchOption.AllDirectories))
            {
                if (file.IsReadOnly) file.IsReadOnly = false;
            }

            Directory.Delete(Root, recursive: true);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            // A leftover under the build output fails nothing.
        }
    }

    public static byte[] RandomBytes(int count, int seed)
    {
        var bytes = new byte[count];
        new Random(seed).NextBytes(bytes);
        return bytes;
    }

    private static string Hash(string path) => Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(path)));
}

/// <summary>An <see cref="IProgress{T}"/> that runs on the reporting thread, so a test can react at an exact moment.</summary>
public sealed class ImmediateProgress<T>(Action<T> report) : IProgress<T>
{
    public void Report(T value) => report(value);
}

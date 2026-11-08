using DownloadManager.Persistence;
using System.Collections.Concurrent;
using System.Diagnostics;
using System.IO.Pipes;
using System.Text;
using System.Text.Json;
using DownloadManager.Archives.Tests;
using DownloadManager.Contracts;

namespace DownloadManager.Integration.Tests;

/// <summary>A fact that runs the real Host and CLI, skipped with the reason when they are not built.</summary>
public sealed class ArchiveHostFactAttribute : FactAttribute
{
    public ArchiveHostFactAttribute() => Skip = ArchiveHost.Unavailable ?? ArchiveTools.RarMissing;
}

/// <summary>The real Host process on its own data folder and pipe, as a client sees it.</summary>
internal sealed class ArchiveHost : IAsyncDisposable
{
    private static readonly string Configuration = new DirectoryInfo(AppContext.BaseDirectory).Parent!.Name;
    private static readonly string RepositoryRoot = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", ".."));

    private readonly string _instance = "archive-test-" + Guid.NewGuid().ToString("N")[..10];
    private readonly ConcurrentQueue<string> _frames = new();
    private readonly ConcurrentDictionary<string, TaskCompletionSource<IpcMessage>> _waiting = new();
    private readonly ConcurrentQueue<ArchiveChangedDto> _changes = new();
    private CancellationTokenSource _readerStop = new();
    private NamedPipeClientStream? _pipe;
    private Task _reader = Task.CompletedTask;
    private Process? _process;

    public ArchiveHost()
    {
        Root = Path.Combine(AppContext.BaseDirectory, "test-work", "host é " + Guid.NewGuid().ToString("N")[..10]);
        Data = Path.Combine(Root, "state");
        Downloads = Path.Combine(Root, "downloads");
        Directory.CreateDirectory(Data);
        Directory.CreateDirectory(Downloads);
    }

    public static string HostExe => Path.Combine(RepositoryRoot, "src", "DownloadManager.Host", "bin", Configuration, "net10.0-windows", "DownloadManager.Host.exe");

    public static string CliExe => Path.Combine(RepositoryRoot, "src", "DownloadManager.Cli", "bin", Configuration, "net10.0", "DownloadManager.Cli.exe");

    public static string? Unavailable =>
        !File.Exists(HostExe) ? $"{HostExe} is not built."
        : !File.Exists(CliExe) ? $"{CliExe} is not built."
        : null;

    public string Root { get; }

    public string Data { get; }

    public string Downloads { get; }

    public IReadOnlyCollection<string> Frames => _frames;

    public IReadOnlyCollection<ArchiveChangedDto> Changes => _changes;

    private string PipeName()
    {
        // The name comes from these two values, so the same code computes it for this instance.
        var oldInstance = Environment.GetEnvironmentVariable("DOWNLOADMANAGER_INSTANCE");
        var oldData = Environment.GetEnvironmentVariable("DOWNLOADMANAGER_DATA");
        try
        {
            Environment.SetEnvironmentVariable("DOWNLOADMANAGER_INSTANCE", _instance);
            Environment.SetEnvironmentVariable("DOWNLOADMANAGER_DATA", Data);
            return PipeNames.HostPipe();
        }
        finally
        {
            Environment.SetEnvironmentVariable("DOWNLOADMANAGER_INSTANCE", oldInstance);
            Environment.SetEnvironmentVariable("DOWNLOADMANAGER_DATA", oldData);
        }
    }

    public async Task StartAsync()
    {
        var info = new ProcessStartInfo(HostExe, "--background") { UseShellExecute = false, CreateNoWindow = true };
        info.Environment["DOWNLOADMANAGER_INSTANCE"] = _instance;
        info.Environment["DOWNLOADMANAGER_DATA"] = Data;
        _process = Process.Start(info)!;

        var name = PipeName();
        var deadline = DateTimeOffset.UtcNow + TimeSpan.FromSeconds(30);
        while (true)
        {
            var pipe = new NamedPipeClientStream(".", name, PipeDirection.InOut, PipeOptions.Asynchronous | PipeOptions.WriteThrough);
            try
            {
                await pipe.ConnectAsync(1000);
                _pipe = pipe;
                break;
            }
            catch (Exception exception) when (exception is TimeoutException or IOException)
            {
                await pipe.DisposeAsync();
                Assert.False(_process.HasExited, $"The Host exited with {(_process.HasExited ? _process.ExitCode : 0)}.");
                if (DateTimeOffset.UtcNow > deadline) throw;
            }
        }

        _readerStop = new CancellationTokenSource();
        _reader = ReadAsync(_pipe, _readerStop.Token);
    }

    private async Task ReadAsync(Stream stream, CancellationToken cancellationToken)
    {
        try
        {
            while (await IpcFraming.ReadAsync(stream, cancellationToken) is { } message)
            {
                _frames.Enqueue(JsonSerializer.Serialize(message, IpcJson.Options));
                if (message.Type == ArchiveMessageTypes.Changed) _changes.Enqueue(message.GetPayload<ArchiveChangedDto>());
                if (message.ReplyTo is { } replyTo && _waiting.TryRemove(replyTo, out var waiter)) waiter.TrySetResult(message);
            }
        }
        catch (Exception exception) when (exception is OperationCanceledException or IOException or EndOfStreamException or ObjectDisposedException)
        {
            // The Host went away, on purpose or because a test killed it.
        }
    }

    public async Task<T> RequestAsync<T>(string type, object payload)
    {
        var request = IpcMessage.Create(type, payload);
        var waiter = new TaskCompletionSource<IpcMessage>(TaskCreationOptions.RunContinuationsAsynchronously);
        _waiting[request.Id] = waiter;
        await IpcFraming.WriteAsync(_pipe!, request);
        var reply = await waiter.Task.WaitAsync(TimeSpan.FromSeconds(20));
        return reply.GetPayload<T>();
    }

    public async Task<ArchiveSetDto> WaitForSetAsync(Func<ArchiveSetDto, bool> predicate, TimeSpan? timeout = null)
    {
        var deadline = DateTimeOffset.UtcNow + (timeout ?? TimeSpan.FromSeconds(60));
        while (DateTimeOffset.UtcNow < deadline)
        {
            var list = await RequestAsync<ArchiveListResult>(ArchiveMessageTypes.List, new { });
            if (list.Sets.FirstOrDefault(predicate) is { } set)
            {
                // Success is on record just before staging goes, so a crash cannot split the two.
                while (set.State == "completed" && Directory.EnumerateDirectories(Downloads, ".inbound-extract-*").Any() && DateTimeOffset.UtcNow < deadline) await Task.Delay(10);
                return set;
            }

            await Task.Delay(50);
        }

        var seen = await RequestAsync<ArchiveListResult>(ArchiveMessageTypes.List, new { });
        throw new TimeoutException("Expected state not reached: " + string.Join("; ", seen.Sets.Select(set => set.StateText)));
    }

    /// <summary>Runs <c>dm</c> with the password, if any, on stdin alone.</summary>
    public async Task<(int Code, string Output)> CliAsync(string? stdin, params string[] arguments)
    {
        var info = new ProcessStartInfo(CliExe)
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8,
        };
        foreach (var argument in arguments) info.ArgumentList.Add(argument);
        info.ArgumentList.Add("--no-start");
        info.Environment["DOWNLOADMANAGER_INSTANCE"] = _instance;
        info.Environment["DOWNLOADMANAGER_DATA"] = Data;
        using var process = Process.Start(info)!;
        var output = process.StandardOutput.ReadToEndAsync();
        var errors = process.StandardError.ReadToEndAsync();
        if (stdin is not null)
        {
            var bytes = new UTF8Encoding(false).GetBytes(stdin);
            await process.StandardInput.BaseStream.WriteAsync(bytes);
            await process.StandardInput.BaseStream.FlushAsync();
        }

        process.StandardInput.Close();
        await process.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(30));
        return (process.ExitCode, await output + await errors);
    }

    /// <summary>What a crash does: no notice, no cleanup.</summary>
    public async Task KillAsync()
    {
        _process!.Kill();
        await SandboxHost.WaitUntilGoneAsync(_process);
        await CloseClientAsync();
    }

    public async Task StopAsync()
    {
        try
        {
            await RequestAsync<HostActionResult>(MessageTypes.HostShutdown, new { });
            await _process!.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(30));
        }
        finally
        {
            await CloseClientAsync();
        }
    }

    private async Task CloseClientAsync()
    {
        await _readerStop.CancelAsync();
        if (_pipe is not null) await _pipe.DisposeAsync();
        _pipe = null;
        await _reader;
    }

    /// <summary>Every byte under the data folder, the logs included, as UTF-8 and as UTF-16.</summary>
    public string EverythingOnDisk()
    {
        var text = new StringBuilder();
        foreach (var file in Directory.EnumerateFiles(Root, "*", SearchOption.AllDirectories))
        {
            byte[] bytes;
            using (var stream = new FileStream(file, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete))
            {
                bytes = new byte[stream.Length];
                stream.ReadExactly(bytes);
            }

            text.Append(Encoding.UTF8.GetString(bytes)).Append('\n').Append(Encoding.Unicode.GetString(bytes)).Append('\n');
        }

        return text.ToString();
    }

    public async ValueTask DisposeAsync()
    {
        await CloseClientAsync();
        if (_process is { HasExited: false })
        {
            _process.Kill(entireProcessTree: true);
            await _process.WaitForExitAsync();
        }

        _process?.Dispose();
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
}

[Trait("Platform", "Windows")]
[Collection("runtime-archive")]
public sealed class RuntimeArchiveHostTests
{
    private const string Secret = "Pipe-Secret_90210 x";
    private static readonly byte[] Text = Encoding.ASCII.GetBytes("from the archive");

    /// <summary>
    /// The password crosses a real pipe from a client to a real Host and on to 7-Zip. Whatever comes
    /// back, and whatever lands on disk, it must not be there. The only frame allowed to hold it is
    /// the request this client itself sent, which is not read here.
    /// </summary>
    [ArchiveHostFact]
    public async Task APasswordAppearsInNoFrameLogOrPlainFile()
    {
        await using var host = new ArchiveHost();
        await host.StartAsync();
        var built = await ArchiveTools.BuildRar5Async(host.Root, "locked.rar", [("locked.txt", Text)], "-hp" + Secret);
        var archive = Path.Combine(host.Downloads, "locked.rar");
        await File.WriteAllBytesAsync(archive, built["locked.rar"]);

        var asked = await host.RequestAsync<ArchiveExtractResult>(ArchiveMessageTypes.Extract, new ArchiveExtractRequest { FilePath = archive });
        Assert.True(asked.Ok, asked.Error);
        var needs = await host.WaitForSetAsync(set => set.FailureKind == "passwordRequired");

        // A wrong one, whose near miss must not show up either.
        await host.RequestAsync<ArchiveActionResult>(ArchiveMessageTypes.SetPassword, new ArchiveSetPasswordRequest { SetId = needs.Id, Password = Secret + "-nope" });
        await host.WaitForSetAsync(set => set.FailureKind == "wrongPassword");

        var given = await host.RequestAsync<ArchiveActionResult>(ArchiveMessageTypes.SetPassword, new ArchiveSetPasswordRequest { SetId = needs.Id, Password = Secret });
        Assert.True(given.Ok, given.Error);
        var done = await host.WaitForSetAsync(set => set.State == "completed");
        Assert.Contains("Extraction completed", done.StateText);
        Assert.Equal(Text, await File.ReadAllBytesAsync(Path.Combine(host.Downloads, "locked.txt")));

        // A protected password at rest, also through a package-free advance record.
        var advance = await host.RequestAsync<ArchiveActionResult>(ArchiveMessageTypes.SetPassword, new ArchiveSetPasswordRequest { FilePath = Path.Combine(host.Downloads, "advance.rar"), Password = Secret });
        Assert.True(advance.Ok, advance.Error);
        var record = await File.ReadAllTextAsync(Path.Combine(host.Data, "archive", "sets", $"{advance.Set!.Id:N}.json"));
        Assert.Contains(SecretProtector.Current.Prefix, record);

        Assert.Contains(host.Changes, change => change.Set?.State == "extracting");
        Assert.Contains(host.Changes, change => change.Set?.State == "completed");
        Assert.Contains(host.Frames, frame => frame.Contains(ArchiveMessageTypes.Changed, StringComparison.Ordinal));
        Assert.All(host.Frames, frame => Assert.DoesNotContain(Secret, frame));
        Assert.All(host.Frames, frame => Assert.DoesNotContain("protectedPassword", frame, StringComparison.OrdinalIgnoreCase));

        await host.StopAsync();

        var disk = host.EverythingOnDisk();
        Assert.Contains("Host stopped", disk);
        Assert.DoesNotContain(Secret, disk);
    }

    [ArchiveHostFact]
    public async Task AHostWhoseArchivePartCanNotStartStillServesOrdinaryDownloads()
    {
        await using var host = new ArchiveHost();

        // A folder where the settings file goes, so the first save fails. Any failure at that point looks the same.
        Directory.CreateDirectory(Path.Combine(host.Data, "archive", "settings.json"));
        await host.StartAsync();

        var snapshot = await host.RequestAsync<SnapshotDto>(MessageTypes.JobsQuery, new { });
        Assert.NotNull(snapshot.Settings);
        var answer = await host.RequestAsync<HostActionResult>(ArchiveMessageTypes.List, new { });
        Assert.False(answer.Ok);

        await host.StopAsync();
        var disk = host.EverythingOnDisk();
        Assert.Contains("Archive extraction could not start", disk);
        Assert.Contains("Host stopped", disk);
    }

    [ArchiveHostFact]
    public async Task TheCliTakesAPasswordOnlyFromStdinAndDrivesEveryCommand()
    {
        await using var host = new ArchiveHost();
        await host.StartAsync();
        var built = await ArchiveTools.BuildRar5Async(host.Root, "locked.rar", [("locked.txt", Text)], "-p" + Secret);
        var archive = Path.Combine(host.Downloads, "locked.rar");
        await File.WriteAllBytesAsync(archive, built["locked.rar"]);
        var plain = Path.Combine(host.Downloads, "plain.rar");
        await File.WriteAllBytesAsync(plain, RarBuilder.Rar5(("plain.txt", Text)));

        var status = await host.CliAsync(null, "archive", "status");
        Assert.Equal(0, status.Code);
        Assert.Contains("auto-extract: off", status.Output);
        Assert.Contains("extractor: ", status.Output);

        var settings = await host.CliAsync(null, "archive", "settings", "--conflict", "skip", "--auto-extract", "on", "--archives", "keep", "--preserve-folders", "on", "--destination", "next");
        Assert.Equal(0, settings.Code);
        Assert.Contains("conflict: skip", settings.Output);
        Assert.Contains("auto-extract: on", settings.Output);

        var refused = await host.CliAsync(null, "archive", "settings", "--conflict", "sideways");
        Assert.Equal(1, refused.Code);

        var noUsage = await host.CliAsync(null, "archive", "nonsense");
        Assert.Equal(2, noUsage.Code);

        var onTheCommandLine = await host.CliAsync(null, "archive", "extract", archive, "--password", "typed-here");
        Assert.Equal(2, onTheCommandLine.Code);
        Assert.Contains("stdin", onTheCommandLine.Output);
        Assert.DoesNotContain("typed-here", onTheCommandLine.Output);

        // Given with the request, on stdin. A password on the command line has no way in.
        var extract = await host.CliAsync(Secret + "\r\n", "archive", "extract", archive, "--password-stdin");
        Assert.Equal(0, extract.Code);
        var locked = await host.WaitForSetAsync(set => set.Name == "locked" && set.State == "completed");
        Assert.DoesNotContain(Secret, extract.Output);

        var plainRun = await host.CliAsync(null, "archive", "extract", plain);
        Assert.Equal(0, plainRun.Code);
        var plainSet = await host.WaitForSetAsync(set => set.Name == "plain" && set.State == "completed");

        var list = await host.CliAsync(null, "archive", "list");
        Assert.Equal(0, list.Code);
        Assert.Contains(locked.Id.ToString(), list.Output);
        Assert.Contains("Extraction completed", list.Output);

        var json = await host.CliAsync(null, "archive", "list", "--json");
        var parsed = JsonSerializer.Deserialize<List<ArchiveSetDto>>(json.Output, IpcJson.Options)!;
        Assert.Equal(2, parsed.Count);

        var password = await host.CliAsync(Secret, "archive", "password", plainSet.Id.ToString());
        Assert.Equal(0, password.Code);
        Assert.DoesNotContain(Secret, password.Output);
        var byPath = await host.CliAsync(Secret, "archive", "password", "--file", Path.Combine(host.Downloads, "advance.rar"));
        Assert.Equal(0, byPath.Code);

        var retry = await host.CliAsync(null, "archive", "retry", plainSet.Id.ToString());
        Assert.Equal(0, retry.Code);
        await host.WaitForSetAsync(set => set.Name == "plain" && set is { State: "completed", Attempts: 2 });

        var cancel = await host.CliAsync(null, "archive", "cancel", plainSet.Id.ToString());
        Assert.Equal(1, cancel.Code);

        var remove = await host.CliAsync(null, "archive", "remove", plainSet.Id.ToString());
        Assert.Equal(0, remove.Code);
        Assert.True(File.Exists(plain));

        var ignored = await host.CliAsync(null, "archive", "extract", Path.Combine(host.Downloads, "not-a-rar.zip"));
        Assert.Equal(1, ignored.Code);

        await host.StopAsync();
        Assert.DoesNotContain(Secret, host.EverythingOnDisk());
    }

    /// <summary>
    /// The real thing: 7-Zip busy, the Host killed. The tool dies with it, the next Host wipes
    /// staging and runs the set again, and exactly one copy of everything comes out.
    /// </summary>
    [ArchiveHostFact]
    public async Task KillingTheHostMidExtractionTakesTheToolWithItAndTheNextHostFinishes()
    {
        await using var host = new ArchiveHost();
        await host.StartAsync();

        var files = new List<(string Name, byte[] Data)>();
        for (var index = 0; index < 400; index++) files.Add(($@"many\file{index:0000}.bin", ArchiveTools.RandomBytes(4096, index)));
        files.Add(("big.bin", ArchiveTools.RandomBytes(120 * 1024 * 1024, 77)));
        var built = await ArchiveTools.BuildRar5Async(host.Root, "huge.rar", files);
        var archive = Path.Combine(host.Downloads, "huge.rar");
        await File.WriteAllBytesAsync(archive, built["huge.rar"]);

        var before = Process.GetProcessesByName("7z").Select(process => process.Id).ToHashSet();
        var asked = await host.RequestAsync<ArchiveExtractResult>(ArchiveMessageTypes.Extract, new ArchiveExtractRequest { FilePath = archive });
        Assert.True(asked.Ok, asked.Error);

        var sawRun = new CancellationTokenSource(TimeSpan.FromSeconds(60));
        while (!host.Changes.Any(change => change.Set is { State: "extracting", Phase: "extracting" or "verifying" or "finalizing" }))
        {
            sawRun.Token.ThrowIfCancellationRequested();
            await Task.Delay(1);
        }

        await host.KillAsync();

        var deadline = DateTimeOffset.UtcNow + TimeSpan.FromSeconds(10);
        while (Process.GetProcessesByName("7z").Any(process => !before.Contains(process.Id)))
        {
            Assert.True(DateTimeOffset.UtcNow < deadline, "7-Zip kept running after the Host died.");
            await Task.Delay(50);
        }

        await host.StartAsync();
        var done = await host.WaitForSetAsync(set => set.State == "completed", TimeSpan.FromSeconds(120));

        // The second Host ran it again, rather than finding the first had already finished.
        Assert.Equal(2, done.Attempts);
        Assert.Empty(Directory.EnumerateDirectories(host.Downloads, ".inbound-extract-*"));
        var written = Directory.EnumerateFiles(host.Downloads, "*", SearchOption.AllDirectories).Select(file => Path.GetRelativePath(host.Downloads, file)).ToList();
        Assert.Equal(files.Count + 1, written.Count);
        Assert.DoesNotContain(written, file => file.Contains(" (1)", StringComparison.Ordinal));
        Assert.Equal(files[^1].Data.Length, new FileInfo(Path.Combine(host.Downloads, "big.bin")).Length);
        Assert.Equal(
            Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(files[^1].Data)),
            Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(await File.ReadAllBytesAsync(Path.Combine(host.Downloads, "big.bin")))));

        await host.StopAsync();
    }
}

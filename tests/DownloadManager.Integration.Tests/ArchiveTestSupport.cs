using System.Collections.Concurrent;
using System.Diagnostics;
using System.Text;
using DownloadManager.Archives;
using DownloadManager.Contracts;
using DownloadManager.Core;
using DownloadManager.Core.Archives;
using DownloadManager.Persistence;
using DownloadManager.Runtime;
using DownloadManager.Runtime.Archives;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging;

namespace DownloadManager.Integration.Tests;

/// <summary>Both classes run 7-Zip, and one counts what is left running, so they do not overlap.</summary>
[CollectionDefinition("runtime-archive")]
public sealed class RuntimeArchiveCollection : ICollectionFixture<ArchiveFileServer>;

/// <summary>A local web server for exact bytes, here the volumes of an archive, with range support.</summary>
public sealed class ArchiveFileServer : IAsyncLifetime
{
    private readonly ConcurrentDictionary<string, byte[]> _files = new(StringComparer.OrdinalIgnoreCase);
    private WebApplication _app = null!;

    public string BaseAddress { get; private set; } = string.Empty;

    public string Serve(string name, byte[] bytes)
    {
        _files[name] = bytes;
        return $"{BaseAddress}/{Uri.EscapeDataString(name)}";
    }

    public async Task InitializeAsync()
    {
        var builder = WebApplication.CreateSlimBuilder();
        builder.Logging.ClearProviders();
        builder.WebHost.UseUrls("http://127.0.0.1:0");
        _app = builder.Build();
        _app.MapGet("/{name}", (string name) => _files.TryGetValue(name, out var bytes)
            ? Results.File(bytes, "application/octet-stream", enableRangeProcessing: true)
            : Results.NotFound());
        await _app.StartAsync();
        BaseAddress = _app.Urls.First().TrimEnd('/');
    }

    public async Task DisposeAsync() => await _app.DisposeAsync();
}

internal static class ArchiveTools
{
    private const string WinRarFolder = @"C:\Program Files\WinRAR";

    /// <summary>The bundled copy on Windows; elsewhere the 7zz that SevenZipTool.Find finds (PATH or DOWNLOADMANAGER_7ZIP).</summary>
    public static string SevenZipPath => DownloadManager.Archives.SevenZipTool.Find() ?? DownloadManager.Archives.SevenZipTool.BundledPath;

    public static string RarPath => Path.Combine(WinRarFolder, "Rar.exe");

    public static string? SevenZipMissing => DownloadManager.Archives.SevenZipTool.Find() is not null ? null : $"7-Zip is not here ({DownloadManager.Archives.SevenZipTool.MissingMessage}), so extraction cannot run.";

    public static string? RarMissing =>
        SevenZipMissing is { } missing ? missing
        : File.Exists(RarPath) ? null
        : $"Rar.exe is not installed at {RarPath}, so real RAR5 fixtures cannot be generated here.";

    public static async Task RunRarAsync(string workingDirectory, params string[] arguments)
    {
        var info = new ProcessStartInfo(RarPath)
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
        var output = process.StandardOutput.ReadToEndAsync();
        var errors = process.StandardError.ReadToEndAsync();
        await process.WaitForExitAsync();
        var text = await output + await errors;
        Assert.True(process.ExitCode == 0, $"Rar.exe exited {process.ExitCode}: {text}");
    }

    /// <summary>Everything Rar.exe makes from one source folder, read into memory and off the disk.</summary>
    public static async Task<Dictionary<string, byte[]>> BuildRar5Async(string root, string archiveName, IEnumerable<(string Name, byte[] Data)> files, params string[] switches)
    {
        var folder = Path.Combine(root, "fixture-" + Guid.NewGuid().ToString("N")[..8]);
        var source = Path.Combine(folder, "source");
        var output = Path.Combine(folder, "output");
        Directory.CreateDirectory(output);
        foreach (var (name, data) in files)
        {
            var path = Path.Combine(source, name);
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            await File.WriteAllBytesAsync(path, data);
        }

        var arguments = new List<string> { "a", "-idq", "-ep1", "-r", "-m0", "-cfg-" };
        arguments.AddRange(switches);
        arguments.Add(Path.Combine(output, archiveName));
        arguments.Add(Path.Combine(source, "*"));
        await RunRarAsync(output, [.. arguments]);

        var result = Directory.EnumerateFiles(output).OrderBy(file => file, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(file => Path.GetFileName(file), File.ReadAllBytes);
        Directory.Delete(folder, recursive: true);
        return result;
    }

    public static byte[] RandomBytes(int count, int seed)
    {
        var bytes = new byte[count];
        new Random(seed).NextBytes(bytes);
        return bytes;
    }
}

public sealed class SevenZipFactAttribute : FactAttribute
{
    public SevenZipFactAttribute() => Skip = ArchiveTools.SevenZipMissing;
}

public sealed class RarFactAttribute : FactAttribute
{
    public RarFactAttribute() => Skip = ArchiveTools.RarMissing;
}

public sealed class SevenZipTheoryAttribute : TheoryAttribute
{
    public SevenZipTheoryAttribute() => Skip = ArchiveTools.SevenZipMissing;
}

public sealed class RarTheoryAttribute : TheoryAttribute
{
    public RarTheoryAttribute() => Skip = ArchiveTools.RarMissing;
}

/// <summary>A service with real stores and engine, its coordinator, and a throwaway folder.</summary>
internal sealed class ArchiveHarness : IAsyncDisposable
{
    private readonly ArchiveFileServer _server;
    private HttpDownloadEngine _engine = null!;

    private ArchiveHarness(ArchiveFileServer server)
    {
        _server = server;
        Root = Path.Combine(AppContext.BaseDirectory, "test-work", "arc é " + Guid.NewGuid().ToString("N")[..10]);
        Paths = new StoragePaths(Path.Combine(Root, "state"));
        Downloads = Path.Combine(Root, "downloads");
        Directory.CreateDirectory(Downloads);
        Paths.EnsureCreated();
    }

    public string Root { get; }

    public StoragePaths Paths { get; }

    public string Downloads { get; }

    public DownloadManagerService Service { get; private set; } = null!;

    public ArchiveCoordinator Archives { get; private set; } = null!;

    public ConcurrentQueue<string> Log { get; } = new();

    public int ToolRuns => Volatile.Read(ref _toolRuns);

    private int _toolRuns;

    public static async Task<ArchiveHarness> StartAsync(
        ArchiveFileServer server,
        ArchiveCoordinatorOptions? options = null,
        Func<IArchiveStore, IArchiveStore>? wrapStore = null,
        bool useMachineTool = false)
    {
        var harness = new ArchiveHarness(server);
        await harness.StartServiceAsync();
        await harness.StartCoordinatorAsync(options, wrapStore, useMachineTool);
        return harness;
    }

    private async Task StartServiceAsync(ArchiveCoordinatorOptions? options = null, Func<IArchiveStore, IArchiveStore>? wrapStore = null, bool useMachineTool = false, bool withCoordinator = false)
    {
        IDownloadCheckpointStore checkpoints = new JsonCheckpointStore(Paths);
        var mediaCheckpoints = new JsonMediaCheckpointStore(Paths);
        _engine = new HttpDownloadEngine(
            new EngineOptions
            {
                MaxAttempts = 3,
                InitialRetryDelay = TimeSpan.FromMilliseconds(20),
                MaxRetryDelay = TimeSpan.FromMilliseconds(100),
                ReadTimeout = TimeSpan.FromSeconds(15),
                AllowLocalNetwork = true,
            },
            checkpoints: checkpoints,
            mediaCheckpoints: mediaCheckpoints);
        Service = new DownloadManagerService(
            Paths,
            new JsonJobStore(Paths),
            checkpoints,
            new JsonSettingsStore(Paths),
            _engine,
            log: Log.Enqueue,
            enableSchedulerTimer: false,
            mediaCheckpoints: mediaCheckpoints)
        {
            PackageSettleDelay = TimeSpan.Zero,
        };

        if (withCoordinator) BuildCoordinator(options, wrapStore, useMachineTool);
        await Service.InitializeAsync();
        var settings = Service.GetSnapshot().Settings;
        settings.DefaultDownloadDirectory = Downloads;
        settings.DefaultConnections = 1;
        settings.MaxConcurrentDownloads = 4;
        var update = await Service.UpdateSettingsAsync(settings);
        Assert.True(update.Ok, update.Error);
    }

    private void BuildCoordinator(ArchiveCoordinatorOptions? options, Func<IArchiveStore, IArchiveStore>? wrapStore, bool useMachineTool)
    {
        IArchiveStore store = new JsonArchiveStore(Paths, Log.Enqueue);
        if (wrapStore is not null) store = wrapStore(store);
        options ??= new ArchiveCoordinatorOptions();
        if (options.ExtractorFactory is null && !useMachineTool) options = options with { ExtractorFactory = _ => CountingTool(options) };
        Archives = new ArchiveCoordinator(Service, store, Log.Enqueue, options);
    }

    /// <summary>Starts a coordinator on the running service, as the next Host start would.</summary>
    public async Task StartCoordinatorAsync(ArchiveCoordinatorOptions? options = null, Func<IArchiveStore, IArchiveStore>? wrapStore = null, bool useMachineTool = false)
    {
        BuildCoordinator(options, wrapStore, useMachineTool);
        await Archives.InitializeAsync();
    }

    /// <summary>A clean stop of everything and a fresh start on the same disk.</summary>
    public async Task RestartAsync(ArchiveCoordinatorOptions? options = null)
    {
        await Archives.ShutdownAsync();
        await Service.ShutdownAsync();
        _engine.Dispose();
        await StartServiceAsync(options, withCoordinator: true);
        await Archives.InitializeAsync();
    }

    private SevenZipExtractor CountingTool(ArchiveCoordinatorOptions options) => new(new SevenZipExtractorOptions
    {
        ToolPath = ArchiveTools.SevenZipPath,
        Limits = options.Limits,
        AvailableBytes = options.AvailableBytes,
        ToolStarted = process =>
        {
            if (process.StartInfo.ArgumentList[0] == "x") Interlocked.Increment(ref _toolRuns);
        },
    });

    /// <summary>Tests name nested files the Windows way (<c>sub\a.txt</c>); on Linux the backslash becomes the separator.</summary>
    public string PathOf(string name) => Path.Combine(Downloads, OperatingSystem.IsWindows() ? name : name.Replace('\\', '/'));

    /// <summary>Another coordinator on the same disk and service, as after a clean restart of it alone.</summary>
    public async Task StartAgainAsync()
    {
        await Archives.ShutdownAsync();
        await StartCoordinatorAsync();
    }

    public string WriteFile(string name, byte[] bytes)
    {
        var path = PathOf(name);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllBytes(path, bytes);
        return path;
    }

    /// <summary>A real download of these bytes from the local server.</summary>
    public async Task<JobDto> DownloadAsync(string name, byte[] bytes, string startMode = "startNow", string? packageKey = null,
        string source = "cli", bool? quietWindows = null)
    {
        var response = await Service.AddJobAsync(new AddJobRequest
        {
            Url = _server.Serve(name, bytes),
            DestinationDirectory = Downloads,
            FileName = name,
            StartMode = startMode,
            Source = source,
            PackageKey = packageKey,
            QuietWindows = quietWindows,
        });
        Assert.True(response.Accepted, response.Error);
        return response.Job!;
    }

    public async Task<JobDto> WaitForJobAsync(Guid id, string state)
    {
        var deadline = DateTimeOffset.UtcNow + TimeSpan.FromSeconds(30);
        while (DateTimeOffset.UtcNow < deadline)
        {
            if (Service.GetJob(id) is { } job && job.State == state) return job;
            await Task.Delay(20);
        }

        throw new TimeoutException($"The download did not become {state}: {Service.GetJob(id)?.State} {Service.GetJob(id)?.Failure?.Message}");
    }

    public async Task<ArchiveSetDto> WaitForSetAsync(Func<ArchiveSetDto, bool> predicate, TimeSpan? timeout = null)
    {
        var deadline = DateTimeOffset.UtcNow + (timeout ?? TimeSpan.FromSeconds(30));
        while (DateTimeOffset.UtcNow < deadline)
        {
            if (Archives.List().FirstOrDefault(predicate) is { } set)
            {
                // Success is on record just before staging goes, so a crash cannot split the two.
                while (set.State == "completed" && StagingFolders().Count > 0 && DateTimeOffset.UtcNow < deadline) await Task.Delay(10);
                return set;
            }

            await Task.Delay(20);
        }

        var seen = string.Join("; ", Archives.List().Select(set => $"{set.Name}: {set.StateText}"));
        throw new TimeoutException($"No archive set reached the expected state. Sets: {seen}. Log: {string.Join(" | ", Log)}");
    }

    public Task<ArchiveSetDto> WaitForStateAsync(string state, TimeSpan? timeout = null) =>
        WaitForSetAsync(set => set.State == state, timeout);

    public Task<ArchiveSetDto> WaitForFailureAsync(string kind) =>
        WaitForSetAsync(set => set.FailureKind == kind);

    /// <summary>Files under the downloads folder, relative and sorted. Staging would show up here too.</summary>
    public List<string> AllFiles() => Directory.EnumerateFiles(Downloads, "*", SearchOption.AllDirectories)
        .Select(file => Path.GetRelativePath(Downloads, file))
        .Order(StringComparer.OrdinalIgnoreCase)
        .ToList();

    public List<string> StagingFolders() => Directory.EnumerateDirectories(Downloads, ".inbound-extract-*", SearchOption.AllDirectories).ToList();

    /// <summary>Every text a person or tool could read from this run's state: logs, records, settings.</summary>
    public string StateText()
    {
        var text = new StringBuilder();
        foreach (var file in Directory.EnumerateFiles(Paths.Root, "*", SearchOption.AllDirectories))
        {
            var bytes = File.ReadAllBytes(file);
            text.Append(Encoding.UTF8.GetString(bytes)).Append('\n').Append(Encoding.Unicode.GetString(bytes)).Append('\n');
        }

        foreach (var line in Log) text.Append(line).Append('\n');
        return text.ToString();
    }

    public string RecordText(Guid id) => File.ReadAllText(Path.Combine(Paths.Root, "archive", "sets", $"{id:N}.json"));

    public async ValueTask DisposeAsync()
    {
        await Archives.ShutdownAsync();
        await Service.ShutdownAsync();
        _engine.Dispose();
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

/// <summary>A store that stops reaching the disk at a chosen save, as a process dying there would.</summary>
internal sealed class FreezableStore(IArchiveStore inner, int? freezeAtSave = null) : IArchiveStore
{
    private int _saves;
    private volatile bool _frozen;

    public TaskCompletionSource Froze { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

    public bool ReachedFreeze => _frozen;

    public void Freeze()
    {
        _frozen = true;
        Froze.TrySetResult();
    }

    private void Check()
    {
        if (_frozen) throw new IOException("This process is dead.");
        if (freezeAtSave is { } at && Interlocked.Increment(ref _saves) == at)
        {
            Freeze();
            throw new IOException("This process just died.");
        }
    }

    public Task<ArchiveSettings?> LoadSettingsAsync(CancellationToken cancellationToken = default) => inner.LoadSettingsAsync(cancellationToken);

    public Task SaveSettingsAsync(ArchiveSettings settings, CancellationToken cancellationToken = default) => inner.SaveSettingsAsync(settings, cancellationToken);

    public Task<IReadOnlyList<ArchiveSetRecord>> LoadSetsAsync(CancellationToken cancellationToken = default) => inner.LoadSetsAsync(cancellationToken);

    public Task SaveSetAsync(ArchiveSetRecord set, CancellationToken cancellationToken = default)
    {
        Check();
        return inner.SaveSetAsync(set, cancellationToken);
    }

    public Task DeleteSetAsync(Guid id, CancellationToken cancellationToken = default)
    {
        Check();
        return inner.DeleteSetAsync(id, cancellationToken);
    }

    public Task<IReadOnlyList<ArchivePackagePassword>> LoadPackagePasswordsAsync(CancellationToken cancellationToken = default) => inner.LoadPackagePasswordsAsync(cancellationToken);

    public Task SavePackagePasswordAsync(ArchivePackagePassword password, CancellationToken cancellationToken = default) => inner.SavePackagePasswordAsync(password, cancellationToken);

    public Task DeletePackagePasswordAsync(Guid packageId, CancellationToken cancellationToken = default) => inner.DeletePackagePasswordAsync(packageId, cancellationToken);
}

/// <summary>
/// Leaves the disk as a kill at a chosen moment would. A live run cannot stop between two moves
/// without its own cleanup running, so this copies the folder at the moment, freezes the store so
/// nothing later is recorded, lets the run end however it ends, and puts the copy back.
/// </summary>
internal sealed class CrashingExtractor(
    IArchiveExtractor inner,
    FreezableStore store,
    string watchedFolder,
    Func<ExtractionProgress, bool>? crashAtProgress = null,
    bool crashAfterCommit = false,
    Action? atCrash = null) : IArchiveExtractor
{
    private readonly string _saved = watchedFolder.TrimEnd('\\') + " saved";
    private int _taken;

    public TaskCompletionSource Crashed { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

    public Task<ArchiveProbe> ProbeAsync(string firstVolume, string? password, CancellationToken cancellationToken) =>
        inner.ProbeAsync(firstVolume, password, cancellationToken);

    public async Task<ExtractionResult> ExtractAsync(ExtractionRequest request, IProgress<ExtractionProgress>? progress, CancellationToken cancellationToken)
    {
        var watcher = new ReportNow<ExtractionProgress>(report =>
        {
            if (crashAtProgress?.Invoke(report) == true) TakeCopy();
            progress?.Report(report);
        });

        var wrapped = new ExtractionRequest
        {
            FirstVolume = request.FirstVolume,
            Destination = request.Destination,
            StagingRoot = request.StagingRoot,
            Password = request.Password,
            ConflictPolicy = request.ConflictPolicy,
            PreserveFolders = request.PreserveFolders,
            KeepFiles = request.KeepFiles,
            MakeDurable = request.MakeDurable,
            Committed = async result =>
            {
                await request.Committed!(result);
                if (crashAfterCommit) TakeCopy();
            },
        };

        try
        {
            await inner.ExtractAsync(wrapped, watcher, cancellationToken);
        }
        catch (Exception exception) when (exception is ExtractionException or IOException)
        {
            // The dead process saw none of this.
        }

        Assert.True(Volatile.Read(ref _taken) == 1, "The run never reached the crash point.");
        Restore();
        Crashed.TrySetResult();
        throw new ExtractionException(ExtractionFailureKind.Unknown, "This process died.");
    }

    private void TakeCopy()
    {
        if (Interlocked.Exchange(ref _taken, 1) == 1) return;
        store.Freeze();
        atCrash?.Invoke();
        CopyTree(watchedFolder, _saved);
    }

    private void Restore()
    {
        ClearReadOnly(watchedFolder);
        Directory.Delete(watchedFolder, recursive: true);
        CopyTree(_saved, watchedFolder);
        ClearReadOnly(_saved);
        Directory.Delete(_saved, recursive: true);
    }

    private static void ClearReadOnly(string folder)
    {
        foreach (var file in new DirectoryInfo(folder).EnumerateFiles("*", SearchOption.AllDirectories))
        {
            if (file.IsReadOnly) file.IsReadOnly = false;
        }
    }

    private static void CopyTree(string from, string to)
    {
        Directory.CreateDirectory(to);
        foreach (var folder in Directory.EnumerateDirectories(from, "*", SearchOption.AllDirectories))
        {
            Directory.CreateDirectory(Path.Combine(to, Path.GetRelativePath(from, folder)));
        }

        foreach (var file in Directory.EnumerateFiles(from, "*", SearchOption.AllDirectories))
        {
            File.Copy(file, Path.Combine(to, Path.GetRelativePath(from, file)), overwrite: true);
        }
    }
}

/// <summary>An extractor that never touches a file. It waits, fails or counts as a test asks.</summary>
internal sealed class FakeExtractor : IArchiveExtractor
{
    private int _active;
    private int _peak;
    private int _runs;

    public TimeSpan Hold { get; init; }

    public bool HoldUntilCancelled { get; init; }

    public ExtractionFailureKind? FailWith { get; set; }

    /// <summary>What this run should fail with, by run number from 1. Null lets it succeed.</summary>
    public Func<int, ExtractionException?>? FailOnRun { get; init; }

    /// <summary>Waits for this before finishing. Null doesn't wait.</summary>
    public TaskCompletionSource? Release { get; init; }

    /// <summary>What the tool would say it read. By default just the first volume, as for a whole archive.</summary>
    public Func<ExtractionRequest, IReadOnlyList<string>>? VolumesRead { get; init; }

    public TaskCompletionSource Started { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

    public int Peak => Volatile.Read(ref _peak);

    public int Runs => Volatile.Read(ref _runs);

    public Task<ArchiveProbe> ProbeAsync(string firstVolume, string? password, CancellationToken cancellationToken) =>
        throw new NotSupportedException();

    public async Task<ExtractionResult> ExtractAsync(ExtractionRequest request, IProgress<ExtractionProgress>? progress, CancellationToken cancellationToken)
    {
        var run = Interlocked.Increment(ref _runs);
        var active = Interlocked.Increment(ref _active);
        int peak;
        while (active > (peak = Volatile.Read(ref _peak)) && Interlocked.CompareExchange(ref _peak, active, peak) != peak)
        {
        }

        Started.TrySetResult();
        try
        {
            if (HoldUntilCancelled)
            {
                try
                {
                    await Task.Delay(Timeout.Infinite, cancellationToken);
                }
                catch (OperationCanceledException)
                {
                    throw new ExtractionException(ExtractionFailureKind.Cancelled, "Extraction was cancelled.");
                }
            }

            if (Hold > TimeSpan.Zero) await Task.Delay(Hold, cancellationToken);
            if (Release is { } release) await release.Task.WaitAsync(cancellationToken);
            if (FailOnRun?.Invoke(run) is { } failure) throw failure;
            if (FailWith is { } kind) throw new ExtractionException(kind, $"Failed as asked: {kind}.");
            return new ExtractionResult(request.Destination, []) { VolumesRead = VolumesRead?.Invoke(request) ?? [request.FirstVolume], FlushedToDisk = true };
        }
        finally
        {
            Interlocked.Decrement(ref _active);
        }
    }
}

internal sealed class ReportNow<T>(Action<T> report) : IProgress<T>
{
    public void Report(T value) => report(value);
}

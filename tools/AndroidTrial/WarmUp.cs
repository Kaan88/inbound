using System.Collections.Concurrent;
using System.Text.Json.Nodes;
using Android.App;
using Android.Runtime;
using DownloadManager.Contracts;
using DownloadManager.Core;
using DownloadManager.Persistence;

namespace AndroidTrial;

/// <summary>
/// ANDROID.2 phone trial, problem 2: start the code paths of the host's cold start on other threads from
/// <see cref="Application.OnCreate"/>, before the Activity exists, so the real start finds them compiled (Mono compiles a method
/// the first time it is called, even under full AOT when the AOT image lacks it). Off unless a flag file
/// <c>/sdcard/Android/data/app.inbound.trial/files/warm.flag</c> exists; its content is a comma list of
/// <c>lazyprot</c> (the protector opens on first use), <c>prot</c> (the real protector opens on its own thread, started at once),
/// <c>settings</c>, <c>jobs</c>, <c>ipc</c> (throw-away loads/serializations on a copy of one file in the cache dir),
/// <c>host</c> (a whole throw-away host on a state copy of 3 jobs), or <c>all</c> = prot,settings,jobs,ipc.
/// </summary>
internal static class WarmUp
{
    public const string FlagFile = "/sdcard/Android/data/app.inbound.trial/files/warm.flag";

    private static readonly ConcurrentDictionary<string, JsonObject> Report = new();
    private static string _flags = "";

    /// <summary>The protector the host should <c>SecretProtector.Use</c> instead of opening its own, or null.</summary>
    public static ISecretProtector? Protector { get; private set; }

    public static bool ProtectorOpened { get; private set; }

    private static long Now() => (long)StartupTrace.Now();

    public static void Begin(Application app)
    {
        try
        {
            if (!File.Exists(FlagFile)) return;
            _flags = File.ReadAllText(FlagFile).Trim();
        }
        catch (Exception) { return; }

        var set = _flags.Split(',', StringSplitOptions.RemoveEmptyEntries).ToHashSet();
        if (set.Contains("all")) { set.Add("prot"); set.Add("settings"); set.Add("jobs"); set.Add("ipc"); }
        var files = app.FilesDir!.AbsolutePath;
        var state = Path.Combine(files, "host-state");
        var scratch = Path.Combine(app.CacheDir!.AbsolutePath, "warm-state");
        StartupTrace.Mark("warm.begin:" + _flags);

        if (set.Contains("lazyprot"))
            Protector = new DeferredProtector(() => OpenProtector(state), onOpen: () => ProtectorOpened = true);
        if (set.Contains("prot"))
        {
            var task = Task.Factory.StartNew(() => Timed("prot", () => OpenProtector(state)), TaskCreationOptions.LongRunning);
            Protector = new DeferredProtector(() => task.GetAwaiter().GetResult(), onOpen: () => ProtectorOpened = true);
        }

        if (set.Contains("settings")) Spawn("settings", () => Timed("settings", () => LoadSettings(state, scratch)));
        if (set.Contains("jobs")) Spawn("jobs", () => Timed("jobs", () => LoadJobs(state, scratch)));
        if (set.Contains("ipc")) Spawn("ipc", () => Timed("ipc", () => { Ipc(); return 0; }));
        if (set.Contains("host")) Spawn("host", () => Timed("host", () => { MiniHost(state, scratch).GetAwaiter().GetResult(); return 0; }));
    }

    private static void Spawn(string name, Action body) => new Thread(() => body()) { IsBackground = true, Name = "warm-" + name }.Start();

    private static T Timed<T>(string name, Func<T> body)
    {
        var start = Now();
        var (u0, k0) = CpuSnapshot.ThreadNow();
        T result = default!;
        string? error = null;
        try { result = body(); }
        catch (Exception ex) { error = ex.GetType().Name + ": " + ex.Message; }
        var (u1, k1) = CpuSnapshot.ThreadNow();
        Report[name] = new JsonObject { ["startMs"] = start, ["endMs"] = Now(), ["threadUserS"] = Math.Round(u1 - u0, 2), ["threadKernelS"] = Math.Round(k1 - k0, 2), ["error"] = error };
        return result;
    }

    public static JsonObject ToJson()
    {
        var o = new JsonObject { ["flags"] = _flags, ["protectorOpenedByTheRun"] = ProtectorOpened };
        foreach (var (k, v) in Report) o[k] = JsonNode.Parse(v.ToJsonString());
        return o;
    }

    private static ISecretProtector OpenProtector(string stateRoot)
    {
        var keyDir = Path.Combine(stateRoot, "key");
        Directory.CreateDirectory(keyDir);
        return AesGcmSecretProtector.OpenForDataFolder(Path.Combine(keyDir, AesGcmSecretProtector.DefaultFileName), keyDir, discardUnreadable: false);
    }

    /// <summary>A scratch state folder with a copy of the real settings and of up to <paramref name="jobCount"/> job files.</summary>
    private static StoragePaths Scratch(string state, string scratch, int jobCount)
    {
        var real = new StoragePaths(state);
        var copy = new StoragePaths(scratch);
        copy.EnsureCreated();
        if (File.Exists(real.SettingsFile)) File.Copy(real.SettingsFile, copy.SettingsFile, overwrite: true);
        if (Directory.Exists(real.JobsDirectory))
            foreach (var f in Directory.EnumerateFiles(real.JobsDirectory, "*.json").Take(jobCount))
                File.Copy(f, Path.Combine(copy.JobsDirectory, Path.GetFileName(f)), overwrite: true);
        return copy;
    }

    private static int LoadSettings(string state, string scratch)
    {
        var paths = Scratch(state, scratch + "-s", 0);
        new JsonSettingsStore(paths).LoadAsync().GetAwaiter().GetResult();
        return 0;
    }

    private static int LoadJobs(string state, string scratch)
    {
        var paths = Scratch(state, scratch + "-j", 3);
        var store = new JsonJobStore(paths);
        store.InitializeAsync().GetAwaiter().GetResult();
        return store.LoadAllAsync().GetAwaiter().GetResult().Count;
    }

    /// <summary>A hello and a snapshot built, serialized and parsed as the dispatcher and a session do.</summary>
    private static void Ipc()
    {
        var jobs = Enumerable.Range(0, 3).Select(i => new JobDto
        {
            Id = Guid.NewGuid(), Url = "http://example.invalid/a" + i, FileName = "a" + i + ".bin", DestinationDirectory = "/sdcard", FinalFilePath = "/sdcard/a.bin",
            PartFilePath = "/sdcard/a.bin.part", TotalBytes = 1000, ReceivedBytes = 10, State = "queued", Progress = 0.01,
        }).ToList();
        var snapshot = new SnapshotDto { Jobs = jobs, Status = new HostStatusDto() };
        foreach (var message in new[]
                 {
                     IpcMessage.Create(MessageTypes.ServerHello, new HostHelloDto { HostVersion = "0", ProtocolVersion = Protocol.Version, StartedAt = DateTimeOffset.UtcNow, Capabilities = ["a"], HostKind = HostKinds.Server }),
                     IpcMessage.Create(MessageTypes.JobsSnapshot, snapshot),
                 })
        {
            var bytes = System.Text.Json.JsonSerializer.SerializeToUtf8Bytes(message, IpcJson.Options);
            var back = System.Text.Json.JsonSerializer.Deserialize<IpcMessage>(bytes, IpcJson.Options)!;
            if (back.Type == MessageTypes.JobsSnapshot) back.GetPayload<SnapshotDto>(); else back.GetPayload<HostHelloDto>();
        }
    }

    /// <summary>The whole host start on a scratch state (settings + 3 jobs), then a session open: every method the real start calls.</summary>
    private static async Task MiniHost(string state, string scratch)
    {
        var paths = Scratch(state, scratch + "-h", 3);
        // The protector is process-wide and chosen once; the scratch host must not choose it (or the real start would find this one).
        var host = await AndroidHost.StartAsync(paths.Root, _ => { }, CancellationToken.None, chooseProtector: false).ConfigureAwait(false);
        var session = new InProcessSession();
        await host.OpenAsync(session, CancellationToken.None).ConfigureAwait(false);
        await host.DisposeAsync().ConfigureAwait(false);
    }
}

/// <summary>An <see cref="ISecretProtector"/> that opens the real one on first use, or waits for a background open.</summary>
internal sealed class DeferredProtector(Func<ISecretProtector> open, Action onOpen) : ISecretProtector
{
    private readonly Lazy<ISecretProtector> _inner = new(() => { onOpen(); return open(); }, LazyThreadSafetyMode.ExecutionAndPublication);

    public string Prefix => AesGcmSecretProtector.BlobPrefix;

    public string Protect(string plaintext, ReadOnlySpan<byte> purpose) => _inner.Value.Protect(plaintext, purpose);

    public string? Unprotect(string protectedValue, ReadOnlySpan<byte> purpose) => _inner.Value.Unprotect(protectedValue, purpose);
}

[Application(Name = "app.inbound.trial.TrialApp")]
public sealed class TrialApp(IntPtr handle, JniHandleOwnership ownership) : Application(handle, ownership)
{
    public override void OnCreate()
    {
        base.OnCreate();
        StartupTrace.Mark("app.OnCreate");
        WarmUp.Begin(this);
    }
}

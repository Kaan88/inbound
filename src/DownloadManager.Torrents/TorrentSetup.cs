using DownloadManager.Core;

namespace DownloadManager.Torrents;

/// <summary>How a Host gets its torrent engine: none with the kill switch set or without the worker beside it.</summary>
public static class TorrentSetup
{
    /// <summary>A worker elsewhere (tests, development builds).</summary>
    public const string WorkerVariable = "INBOUND_TORRENT_WORKER";

    /// <summary>
    /// Tests and app walk-throughs only: a port number makes the worker listen on 127.0.0.1 at that port with DHT off,
    /// so nothing leaves the machine and no firewall prompt appears. Unset in every installed build.
    /// </summary>
    public const string TestListenVariable = "INBOUND_TORRENT_TEST_LISTEN";

    /// <summary>
    /// Test runs only: "1" keeps Options' port but listens on 127.0.0.1 only, with DHT, UPnP and NAT-PMP off (the test
    /// projects set it for every test), so no firewall prompt appears for a worker in a build or worktree folder.
    /// </summary>
    public const string LoopbackOnlyVariable = "INBOUND_TORRENT_LOOPBACK_ONLY";

    /// <summary>The loopback-only listen of <see cref="TestListenVariable"/>, or null when it isn't set to a port.</summary>
    public static TorrentSupervisorOptions? TestListenOptions()
    {
        var value = Environment.GetEnvironmentVariable(TestListenVariable);
        if (!int.TryParse(value, System.Globalization.NumberStyles.None, System.Globalization.CultureInfo.InvariantCulture, out var port) ||
            port is < 1 or > 65535) return null;
        return new TorrentSupervisorOptions
        {
            Listen = () => new System.Text.Json.Nodes.JsonObject
            {
                ["port"] = port,
                ["interfaces"] = new System.Text.Json.Nodes.JsonArray("127.0.0.1"),
                ["dht"] = false,
                ["lsd"] = false,
                ["upnp"] = false,
                ["natpmp"] = false,
            },
        };
    }

    public static string WorkerFileName => WorkerFileNameFor(OperatingSystem.IsWindows());

    public static string WorkerFileNameFor(bool windows) => windows ? "inbound-torrent.exe" : "inbound-torrent";

    /// <summary><see cref="WorkerVariable"/> when set, else the worker next to the Host's own program.</summary>
    public static string? FindWorker(string? baseDirectory = null) => FindWorker(baseDirectory ?? AppContext.BaseDirectory,
        Environment.GetEnvironmentVariable(WorkerVariable), OperatingSystem.IsWindows(), File.Exists);

    /// <summary>
    /// The same lookup with everything given: the Host's folder (on Linux the AppImage's <c>usr/lib/inbound/</c>, where
    /// <c>scripts/package-appimage.sh</c> puts <c>inbound-host</c> and <c>inbound-torrent</c> side by side), the
    /// <see cref="WorkerVariable"/> value, which system's file name, and the file check.
    /// </summary>
    public static string? FindWorker(string baseDirectory, string? fromEnvironment, bool windows, Func<string, bool> exists)
    {
        if (!string.IsNullOrWhiteSpace(fromEnvironment)) return exists(fromEnvironment) ? fromEnvironment : null;
        var name = WorkerFileNameFor(windows);
        // Joined by hand so a Linux folder gives a Linux path even when this runs on Windows (tests).
        var separator = windows ? '\\' : '/';
        var candidate = baseDirectory.EndsWith('/') || baseDirectory.EndsWith('\\') ? baseDirectory + name : baseDirectory + separator + name;
        return exists(candidate) ? candidate : null;
    }

    /// <summary>
    /// The engine, or null (and a log line saying why). Nothing starts until a torrent job runs. <paramref name="findWorker"/>
    /// replaces <see cref="FindWorker"/> (tests).
    /// </summary>
    public static TorrentDownloadEngine? Create(string dataRoot, ITorrentStore store, Action<string>? log,
        TorrentSupervisorOptions? options = null, Func<string?>? findWorker = null)
    {
        if (TorrentAvailability.KillSwitchSet())
        {
            log?.Invoke($"Torrents are unavailable: {TorrentAvailability.KillSwitchVariable}=off.");
            return null;
        }

        var worker = findWorker is null ? FindWorker() : findWorker();
        if (worker is null)
        {
            log?.Invoke("Torrents are unavailable: the torrent worker is not installed.");
            return null;
        }

        var folder = Path.Combine(dataRoot, "torrents");
        var dhtState = Path.Combine(folder, "dht.state");
        var supervisor = new TorrentWorkerSupervisor(() =>
        {
            Directory.CreateDirectory(folder);
            return TorrentWorkerProcess.Start(worker, dhtState, null, line => log?.Invoke("torrent worker: " + line));
        }, options ?? TestListenOptions(), log);
        return new TorrentDownloadEngine(supervisor, store, log);
    }
}

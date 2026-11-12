using DownloadManager.Contracts;
using DownloadManager.Core.Providers;
using DownloadManager.Runtime.Remote;

namespace DownloadManager.Runtime;

/// <summary>
/// T10: a download that finished as a .torrent file (<see cref="JobDto.TorrentFileReady"/>) is offered as a torrent:
/// the Host asks the window to open the usual torrent dialog with that file, through the same <c>--open-torrent</c>
/// launch the Explorer association uses (<see cref="Ipc.IHostPlatform.OpenTorrentFiles"/>). Several that finish close
/// together (Download All of ten torrent links) go in one launch, so they fill one dialog: the launch waits
/// <see cref="Wait"/> after the last such completion. The wait is a one-shot timer started only by such a completion,
/// so nothing runs while nothing finishes. Only where the torrent can go somewhere (<c>offered</c>: torrents on, or an
/// account that takes them), and only for a download someone at this PC asked for
/// (<see cref="DownloadWindows.OpensWindows"/>); the rest stay ordinary finished downloads with Open as Torrent.
/// A download the browser extension asked to keep quiet (<see cref="DownloadWindows.KeptQuiet"/>, EXT.R12) is offered
/// too (T10.B, owner 2026-11-12: "have the desktop app react to torrent files"): a .torrent file from the browser is
/// the one exception to the quiet rule, which keeps every other window away.
/// </summary>
public sealed class TorrentFileOffers : IDisposable
{
    /// <summary>How long after the last .torrent completion the launch waits for more.</summary>
    public static readonly TimeSpan Wait = TimeSpan.FromSeconds(1);

    /// <summary>The most characters of paths one launch's command line carries; more go in another launch.</summary>
    public const int MostCommandLineChars = 24_000;

    private readonly DownloadManagerService _service;
    private readonly Func<bool> _offered;
    private readonly Action<IReadOnlyList<string>> _open;
    private readonly TimeProvider _time;
    private readonly TimeSpan _wait;
    private readonly Action<string>? _log;
    private readonly object _sync = new();
    private readonly List<Guid> _waiting = [];
    private ITimer? _timer;
    private bool _disposed;

    /// <param name="offered">Whether a .torrent file can go anywhere now: torrents on, or an account that takes them.</param>
    /// <param name="open">Must not block. Called on a timer thread with the files' full paths, in the order they finished.</param>
    public TorrentFileOffers(DownloadManagerService service, Func<bool> offered, Action<IReadOnlyList<string>> open,
        TimeProvider? time = null, TimeSpan? wait = null, Action<string>? log = null)
    {
        _service = service;
        _offered = offered;
        _open = open;
        _time = time ?? TimeProvider.System;
        _wait = wait ?? Wait;
        _log = log;
        service.JobChanged += OnJobChanged;
    }

    public void Dispose()
    {
        _service.JobChanged -= OnJobChanged;
        lock (_sync)
        {
            _disposed = true;
            _timer?.Dispose();
            _timer = null;
            _waiting.Clear();
        }
    }

    /// <summary>
    /// Whether a .torrent file can go anywhere: torrents are on here, or a provider account with a key takes torrents.
    /// Torrents off and no such account: a finished .torrent file stays an ordinary download, as before T10.
    /// </summary>
    public static bool TorrentsCanGo(DownloadManagerService service, RemoteProviderRegistry? providers)
        => service.TorrentsOn ||
           providers?.All.Any(provider => provider.Capabilities.HasFlag(RemoteCapabilities.Torrents) && provider.Account?.HasKey == true) == true;

    /// <summary>
    /// Whether this finished download is offered as a torrent here. A quiet browser download is too (T10.B): the torrent
    /// dialog is the one window EXT.R12 doesn't keep away.
    /// </summary>
    public static bool Offers(JobDto job, bool offered)
        => offered && job.State == "completed" && job.TorrentFileReady && job.Torrent is null && DownloadWindows.OpensWindows(job);

    private void OnJobChanged(object? sender, JobChangeEventArgs e)
    {
        try
        {
            if (e.ChangeType != "completed" || !e.Job.TorrentFileReady || !Offers(e.Job, _offered())) return;
            lock (_sync)
            {
                if (_disposed || _waiting.Contains(e.Job.Id)) return;
                _waiting.Add(e.Job.Id);
                // Each one moves the launch on, so a run of them that finish close together goes in one launch.
                if (_timer is null) _timer = _time.CreateTimer(_ => Send(), null, _wait, Timeout.InfiniteTimeSpan);
                else _timer.Change(_wait, Timeout.InfiniteTimeSpan);
            }
        }
        catch (Exception ex)
        {
            _log?.Invoke($"Offering a .torrent download failed: {ex.GetType().Name}");
        }
    }

    private void Send()
    {
        List<Guid> ids;
        lock (_sync)
        {
            if (_disposed) return;
            ids = [.. _waiting];
            _waiting.Clear();
            _timer?.Dispose();
            _timer = null;
        }

        try
        {
            // Still there and still a .torrent file: one removed or run again meanwhile is left out.
            var offered = _offered();
            var paths = ids.Select(_service.GetJob).OfType<JobDto>()
                .Where(job => Offers(job, offered) && !string.IsNullOrWhiteSpace(job.FinalFilePath))
                .Select(job => job.FinalFilePath).Distinct(StringComparer.Ordinal).ToList();
            foreach (var launch in Launches(paths)) _open(launch);
        }
        catch (Exception ex)
        {
            _log?.Invoke($"Offering .torrent downloads failed: {ex.GetType().Name}");
        }
    }

    /// <summary>
    /// The paths split into launches whose command lines stay short enough: each launch has at most
    /// <paramref name="mostChars"/> characters of paths (one longer path still gets a launch of its own).
    /// </summary>
    public static List<IReadOnlyList<string>> Launches(IReadOnlyList<string> paths, int mostChars = MostCommandLineChars)
    {
        var launches = new List<IReadOnlyList<string>>();
        var current = new List<string>();
        var chars = 0;
        foreach (var path in paths)
        {
            // The argument name, quotes and spaces around each path.
            var cost = path.Length + 20;
            if (current.Count > 0 && chars + cost > mostChars)
            {
                launches.Add(current);
                current = [];
                chars = 0;
            }

            current.Add(path);
            chars += cost;
        }

        if (current.Count > 0) launches.Add(current);
        return launches;
    }

    /// <summary>
    /// Marks a launch as this Host's offer of finished downloads, not an Explorer open: a window that shows another
    /// server then leaves them on this PC. An older window ignores it.
    /// </summary>
    public const string FromDownloadArgument = "--from-download";

    /// <summary>The window's command line for one launch: <see cref="FromDownloadArgument"/>, then <c>--open-torrent</c> before each path.</summary>
    public static IReadOnlyList<string> Arguments(IReadOnlyList<string> paths)
        => new[] { FromDownloadArgument }.Concat(paths.SelectMany(path => new[] { "--open-torrent", path })).ToList();
}

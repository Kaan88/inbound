using System.Collections.Concurrent;
using DownloadManager.Contracts;

namespace DownloadManager.Runtime;

/// <summary>A progress window the Host should open: a file's own, or a package's.</summary>
public sealed record DownloadWindowRequest(Guid Id, bool IsPackage);

/// <summary>
/// Decides which progress windows the Host opens by itself. A download on its own gets its own window when it
/// starts, and its completion window when it finishes. A package of any size gets one window when its first file
/// starts, and its completion window once the whole package has finished and settled, never one per file: a
/// package built one add at a time can have only its first file when that file starts or finishes. Downloads the
/// CLI added (one add, a batch or an import) stay headless, a provider's files start by themselves, often many at once,
/// so they stay in the list, and nobody at this PC asked for one added from another device (D8,
/// <see cref="JobDto.AddedFromAnotherDevice"/>). A torrent gets no window when it starts (T9 W4): the torrent dialog
/// already asked where and which files, and a magnet's file choice opens in the main window once its list is in. Its
/// completion window follows the usual rule. A download the browser extension asked to keep quiet opens neither window
/// (<see cref="KeptQuiet"/>).
/// </summary>
public sealed class DownloadWindows : IDisposable
{
    private readonly DownloadManagerService _service;
    private readonly Func<bool> _showCompletionByDefault;
    private readonly Action<DownloadWindowRequest> _open;
    private readonly Action<string>? _log;

    /// <summary>Jobs and packages whose window was opened for this run of transfers, so starting again doesn't repeat it.</summary>
    private readonly ConcurrentDictionary<Guid, byte> _shown = new();

    /// <summary>T10: files in a package whose start window was skipped as a .torrent file, so their progress isn't asked again.</summary>
    private readonly ConcurrentDictionary<Guid, byte> _skippedInPackage = new();

    /// <param name="open">Must not block. Called on whichever thread brought the news.</param>
    public DownloadWindows(DownloadManagerService service, Func<bool> showCompletionByDefault, Action<DownloadWindowRequest> open, Action<string>? log = null)
    {
        _service = service;
        _showCompletionByDefault = showCompletionByDefault;
        _open = open;
        _log = log;
        service.JobChanged += OnJobChanged;
        service.PackageCompleted += OnPackageCompleted;
    }

    public void Dispose()
    {
        _service.JobChanged -= OnJobChanged;
        _service.PackageCompleted -= OnPackageCompleted;
    }

    /// <remarks>The desktop window's TorrentRules.ProgressWindowComing follows this rule: change them together.</remarks>
    public static bool OpensWindows(JobDto job) =>
        !job.AddedFromAnotherDevice && job.Source is not ("cli" or "cliBatch" or "cliImport" or "remote");

    /// <summary>A window opens as it starts: one that opens windows at all, and not a torrent.</summary>
    /// <remarks>The desktop window's TorrentRules.ProgressWindowComing follows this rule: change them together.</remarks>
    public static bool OpensStartWindow(JobDto job) => OpensWindows(job) && job.Torrent is null;

    /// <summary>
    /// EXT.R12: the browser extension sent it and shows its progress on its own toolbar icon, so the app opens no window
    /// for it, neither as it starts nor when it finishes, and the browser keeps the focus (<see cref="JobDto.QuietWindows"/>).
    /// A package with any such file opens none either. That includes the torrent dialog for a .torrent file it turns out
    /// to be (T10, <see cref="TorrentFileOffers.Offers"/>): EXT.R12 wins, and the finished row keeps Open as Torrent.
    /// </summary>
    public static bool KeptQuiet(JobDto job) => job.QuietWindows;

    private bool ShowsCompletion(JobDto job) => job.ShowCompletionDialog ?? _showCompletionByDefault();

    /// <summary>
    /// T10: whether a .torrent file that finishes is offered as a torrent here (torrents on, or an account that takes
    /// them). Then such a download opens no window: none as it starts when it already looks like a torrent, and no
    /// completion window once its file read as one (<see cref="JobDto.TorrentFileReady"/>); the torrent dialog opens
    /// instead (<see cref="TorrentFileOffers"/>). Null or false: today's windows.
    /// </summary>
    public Func<bool>? TorrentFilesOffered { get; init; }

    private bool SkipsForTorrentFile(JobDto job, bool starting)
        => (starting ? _service.LooksLikeTorrentFile(job.Id) : job.TorrentFileReady) && TorrentFilesOffered?.Invoke() == true;

    private void OnJobChanged(object? sender, JobChangeEventArgs e)
    {
        try
        {
            var job = e.Job;
            if (KeptQuiet(job))
            {
                _shown.TryRemove(job.Id, out _);
                return;
            }

            var starting = (e.ChangeType == "added" && job.StartMode == "startNow") ||
                           (e.ChangeType == "progress" && job.State is "connecting" or "downloading");
            if (OpensWindows(job) && (starting || e.ChangeType == "completed"))
            {
                if (_service.FindGroupOf(job.Id) is { } package)
                {
                    // A finished file in a package waits for the package's own completion.
                    // T10: a .torrent file is recorded for itself, not for the package, so another file can still open it.
                    if (starting && OpensStartWindow(job) && !_shown.ContainsKey(package) && !_skippedInPackage.ContainsKey(job.Id))
                    {
                        if (SkipsForTorrentFile(job, starting: true)) _skippedInPackage.TryAdd(job.Id, 0);
                        else if (_shown.TryAdd(package, 0)) _open(new DownloadWindowRequest(package, IsPackage: true));
                    }
                }
                // A start that is skipped for a .torrent file still counts as decided, so its progress isn't asked again.
                else if ((starting && OpensStartWindow(job) && _shown.TryAdd(job.Id, 0) && !SkipsForTorrentFile(job, starting: true)) ||
                         (e.ChangeType == "completed" && ShowsCompletion(job) && !SkipsForTorrentFile(job, starting: false)))
                {
                    _open(new DownloadWindowRequest(job.Id, IsPackage: false));
                }
            }

            if (e.ChangeType is "paused" or "cancelled" or "failed" or "completed" or "removed")
            {
                _shown.TryRemove(job.Id, out _);
                _skippedInPackage.TryRemove(job.Id, out _);
            }
        }
        catch (Exception ex)
        {
            _log?.Invoke($"Deciding a progress window failed: {ex.GetType().Name}");
        }
    }

    private void OnPackageCompleted(object? sender, PackageCompletedEventArgs e)
    {
        try
        {
            _shown.TryRemove(e.PackageId, out _);
            var jobs = e.JobIds.Select(_service.GetJob).OfType<JobDto>().ToList();
            if (jobs.Count == 0 || !jobs.All(OpensWindows) || jobs.Any(KeptQuiet)) return;
            // T10: a package of .torrent files only goes to the torrent dialog.
            if (jobs.All(job => SkipsForTorrentFile(job, starting: false))) return;
            var last = jobs.MaxBy(job => job.CompletedAt ?? DateTimeOffset.MinValue)!;
            if (!ShowsCompletion(last)) return;

            // A package of one is an ordinary row on the list, so it gets an ordinary file's window.
            _open(jobs is [var only] ? new DownloadWindowRequest(only.Id, IsPackage: false) : new DownloadWindowRequest(e.PackageId, IsPackage: true));
        }
        catch (Exception ex)
        {
            _log?.Invoke($"Deciding a package's completion window failed: {ex.GetType().Name}");
        }
    }
}

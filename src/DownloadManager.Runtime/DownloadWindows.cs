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
/// <see cref="JobDto.AddedFromAnotherDevice"/>).
/// </summary>
public sealed class DownloadWindows : IDisposable
{
    private readonly DownloadManagerService _service;
    private readonly Func<bool> _showCompletionByDefault;
    private readonly Action<DownloadWindowRequest> _open;
    private readonly Action<string>? _log;

    /// <summary>Jobs and packages whose window was opened for this run of transfers, so starting again doesn't repeat it.</summary>
    private readonly ConcurrentDictionary<Guid, byte> _shown = new();

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

    public static bool OpensWindows(JobDto job) =>
        !job.AddedFromAnotherDevice && job.Source is not ("cli" or "cliBatch" or "cliImport" or "remote");

    private bool ShowsCompletion(JobDto job) => job.ShowCompletionDialog ?? _showCompletionByDefault();

    private void OnJobChanged(object? sender, JobChangeEventArgs e)
    {
        try
        {
            var job = e.Job;
            var starting = (e.ChangeType == "added" && job.StartMode == "startNow") ||
                           (e.ChangeType == "progress" && job.State is "connecting" or "downloading");
            if (OpensWindows(job) && (starting || e.ChangeType == "completed"))
            {
                if (_service.FindGroupOf(job.Id) is { } package)
                {
                    // A finished file in a package waits for the package's own completion.
                    if (starting && _shown.TryAdd(package, 0)) _open(new DownloadWindowRequest(package, IsPackage: true));
                }
                else if ((starting && _shown.TryAdd(job.Id, 0)) || (e.ChangeType == "completed" && ShowsCompletion(job)))
                {
                    _open(new DownloadWindowRequest(job.Id, IsPackage: false));
                }
            }

            if (e.ChangeType is "paused" or "cancelled" or "failed" or "completed" or "removed") _shown.TryRemove(job.Id, out _);
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
            if (jobs.Count == 0 || !jobs.All(OpensWindows)) return;
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

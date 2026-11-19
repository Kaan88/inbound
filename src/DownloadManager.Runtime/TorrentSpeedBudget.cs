using DownloadManager.Core;

namespace DownloadManager.Runtime;

/// <summary>
/// T2: the overall speed limit is one budget for HTTP downloads, torrent downloads, torrent uploads and site transfers
/// (<see cref="SpeedBudgetSplit"/>). It is split again only while the torrent worker or a site transfer runs, on events
/// that come anyway: the worker starting or stopping, a site transfer starting or every one ending, any download's
/// progress (a torrent's comes about once a second, or every 5 seconds while no window shows torrents), a seeding
/// torrent's progress and a site transfer's, at most once a second. No timer of its own; with neither running, HTTP has
/// the whole limit and a progress report costs one lock.
/// </summary>
public sealed partial class DownloadManagerService
{
    /// <summary>Guards the fields below. Never held while <c>_sync</c> is taken.</summary>
    private readonly object _budgetSync = new();
    /// <summary>What was last sent to the engines; null before the first.</summary>
    private SpeedShares? _appliedShares;
    /// <summary>The limit, upload cap and worker state the applied shares were made for.</summary>
    private (long? Limit, long UploadCap, bool Running, bool Sites) _appliedFor;
    /// <summary>Site transfers (SITES.4): what they run now and how fast, and where their share goes (rclone).</summary>
    private Func<SpeedDemand>? _siteDemand;
    private Action<long?>? _applySiteLimit;
    /// <summary>The overall limit and torrent upload cap in force. Only <see cref="ApplySpeedLimits"/> writes them, so a
    /// split that raced a settings change never puts the old ones back.</summary>
    private (long? Limit, long UploadCap) _budgetSettings;
    private long _lastSplitAt;

    /// <summary>The shares in force now (tests and diagnostics); null while no overall limit is set.</summary>
    public SpeedShares? CurrentSpeedShares
    {
        get { lock (_budgetSync) return _appliedFor.Limit is null ? null : _appliedShares; }
    }

    /// <summary>The overall limit and the torrent upload cap changed, or the Host starts: applied at once.</summary>
    private void ApplySpeedLimits(AppSettings settings) =>
        SplitSpeedBudget((settings.GlobalSpeedLimitBytesPerSecond is > 0 ? settings.GlobalSpeedLimitBytesPerSecond : null,
            Math.Max(0, settings.TorrentUploadLimit)), force: true);

    /// <summary>After a limit change: T3 progress comes every second while the overall limit is split.</summary>
    private void ApplySpeedLimitsAndRate(AppSettings settings)
    {
        ApplySpeedLimits(settings);
        _engine.UpdateQueueSpeedLimits(settings.Queues.ToDictionary(q => q.Id, q => q.SpeedLimitBytesPerSecond));
        UpdateProgressRate();
    }

    /// <summary>
    /// Site transfers take part in the overall limit (SITES.4): <paramref name="demand"/> says whether one runs and how fast
    /// (it must not call back into this service), and <paramref name="apply"/> receives their share (null for no limit).
    /// While none runs they keep only the floor, so one that starts can't overshoot before the split it triggers.
    /// </summary>
    public void AttachSiteTransfers(Func<SpeedDemand> demand, Action<long?> apply)
    {
        lock (_budgetSync) (_siteDemand, _applySiteLimit) = (demand, apply);
        SplitSpeedBudget(null, force: true);
    }

    /// <summary>A site transfer started or every one stopped: the budget is split again at once.</summary>
    public void OnSiteTransfersActivityChanged() => SplitSpeedBudget(null, force: false);

    /// <summary>Site transfers measured their speed (about once a second while they run).</summary>
    public void OnSiteTransfersMeasured() => SplitSpeedBudgetSoon();

    /// <summary>On a download's, a seeding torrent's or a site transfer's progress: splits again while the worker or a
    /// site transfer runs, at most once a second.</summary>
    private void SplitSpeedBudgetSoon()
    {
        var now = _timeProvider.GetTimestamp();
        lock (_budgetSync)
        {
            if (_torrentEngine is null && _siteDemand is null) return;
            if (_appliedFor.Limit is null || !_appliedFor.Running && !_appliedFor.Sites || now - _lastSplitAt < _timeProvider.TimestampFrequency) return; // once a second
        }

        SplitSpeedBudget(null, force: false);
    }

    /// <param name="settings">New limit and upload cap (a settings change or the Host starting); null keeps the ones in force.</param>
    private void SplitSpeedBudget((long? Limit, long UploadCap)? settings, bool force)
    {
        // Demands are measured outside _budgetSync (MeasureDemands takes _sync). Whether to measure is a guess; the inputs
        // themselves are read again under the lock, and a split that finds the worker running without a measurement starts
        // from the even split.
        bool measured = false;
        SpeedDemand http = default, download = default, upload = default;
        bool guessLimit;
        Func<SpeedDemand>? siteDemand;
        lock (_budgetSync) (guessLimit, siteDemand) = ((settings ?? _budgetSettings).Limit is not null, _siteDemand);
        // Read outside the lock too (it takes the site transfers' own lock); whether one runs is decided by this reading.
        var site = siteDemand?.Invoke() ?? default;
        if (guessLimit && (_torrentEngine?.WorkerRunning == true || site.Active))
        {
            (http, download, upload) = MeasureDemands();
            measured = true;
        }

        lock (_budgetSync)
        {
            if (settings is { } fresh) _budgetSettings = fresh;
            var (limit, uploadCap) = _budgetSettings;
            var running = _torrentEngine?.WorkerRunning == true;
            var sites = site.Active;
            _lastSplitAt = _timeProvider.GetTimestamp();
            var sameInputs = _appliedFor == (limit, uploadCap, running, sites);
            if (limit is not { } total)
            {
                if (!force && sameInputs) return;
                _appliedFor = (null, uploadCap, running, sites);
                _appliedShares = null;
                // No overall limit: nothing to share. The torrent upload limit, when set, still caps uploads.
                _engine.UpdateSharedSpeedLimit(null);
                _torrentEngine?.UpdateSpeedLimits(null, uploadCap > 0 ? uploadCap : null);
                _applySiteLimit?.Invoke(null);
                return;
            }

            SpeedShares next;
            long httpShare;
            if (running || sites)
            {
                // A split made for other kinds (or other settings) measured nothing worth keeping.
                next = sameInputs && measured && _appliedShares is { } previous
                    ? SpeedBudgetSplit.Split(total, uploadCap, previous, http, download, upload, sites ? site : null, running)
                    : SpeedBudgetSplit.Initial(total, uploadCap, running, sites);
                httpShare = next.Http;
            }
            else
            {
                // Nothing else runs: HTTP has the whole limit. The worker keeps an even split for when it starts; the
                // start itself splits again at once.
                next = SpeedBudgetSplit.Initial(total, uploadCap);
                httpShare = total;
            }

            var split = running || sites;
            if (!force && sameInputs && _appliedShares is { } applied &&
                (split && !measured || !SpeedBudgetSplit.Differs(applied, split ? next : next with { Http = total }))) return;
            _appliedFor = (limit, uploadCap, running, sites);
            _appliedShares = split ? next : next with { Http = total };
            _engine.UpdateSharedSpeedLimit(httpShare);
            _torrentEngine?.UpdateSpeedLimits(next.TorrentDownload, next.TorrentUpload);
            _applySiteLimit?.Invoke(sites ? next.Sites : Math.Max(1, total / SpeedBudgetSplit.FloorDivisor));
        }
    }

    /// <summary>What runs now and how fast: HTTP downloads, torrent downloads, and torrent uploads (while they
    /// download and while they seed).</summary>
    private (SpeedDemand Http, SpeedDemand Download, SpeedDemand Upload) MeasureDemands()
    {
        bool httpActive = false, downloadActive = false;
        double httpRate = 0, downloadRate = 0, uploadRate = 0;
        lock (_sync)
        {
            foreach (var runtime in _jobs.Values)
            {
                var job = runtime.Job;
                var runs = runtime.Starting || runtime.Runner is { IsCompleted: false };
                if (job.Torrent is { } torrent)
                {
                    if (runs)
                    {
                        downloadActive = true;
                        downloadRate += job.BytesPerSecond ?? 0;
                        uploadRate += runtime.TorrentUploadRate;
                    }
                    else if (torrent.Seeding)
                    {
                        uploadRate += torrent.UploadRate;
                    }
                }
                else if (runs)
                {
                    httpActive = true;
                    httpRate += job.BytesPerSecond ?? 0;
                }
            }
        }

        // While the worker runs, any torrent on it may upload.
        return (new SpeedDemand(httpActive, httpRate), new SpeedDemand(downloadActive, downloadRate), new SpeedDemand(true, uploadRate));
    }

    /// <summary>The worker started or stopped: the budget is split again at once.</summary>
    private void OnTorrentWorkerRunningChanged() => SplitSpeedBudget(null, force: false);
}

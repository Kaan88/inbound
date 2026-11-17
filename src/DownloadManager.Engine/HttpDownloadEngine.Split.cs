namespace DownloadManager.Core;

/// <summary>
/// CONN.2: a download that runs on one stream (started with one connection) is split when its count goes up, at once
/// while it runs and on its next run from a one-stream checkpoint. Before this it stayed on one range for good.
/// </summary>
public sealed partial class HttpDownloadEngine
{
    /// <summary>A file smaller than this is never segmented (see <see cref="TryExecuteSegmentedAsync"/>).</summary>
    private const long SegmentedMinimumBytes = 1024 * 1024;

    // Guarded by _runsSync.
    private readonly Dictionary<Guid, SingleStreamRun> _singleRuns = new();

    /// <summary>Jobs whose server refused a range once a segmented run had begun. Guarded by _runsSync.</summary>
    private readonly HashSet<Guid> _splitRefused = new();

    private bool SplitRefused(Guid jobId)
    {
        lock (_runsSync)
        {
            return _splitRefused.Contains(jobId);
        }
    }

    /// <summary>A one-stream checkpoint whose job now asks for more connections, with enough left to split.</summary>
    private bool WantsSplit(DownloadJob job, DownloadCheckpoint checkpoint)
        => job.RequestedConnections > 1 &&
           job.ResumeSupported != false &&
           checkpoint.FinalizingTo is null &&
           checkpoint.Segments is [{ Start: 0 } only] &&
           checkpoint.TotalBytes is { } total &&
           total >= SegmentedMinimumBytes &&
           only.End == total - 1 &&
           total - only.Completed >= 2 * MinimumStealBytes &&
           !SplitRefused(job.Id);

    /// <summary>
    /// Whether the probe's answer lets a one-stream checkpoint's bytes be the start of a segmented file: the same size
    /// and entity, and a part file holding at least the bytes counted and nothing past the size.
    /// </summary>
    private static bool CanSplitSingleStream(DownloadJob job, DownloadCheckpoint checkpoint, SegmentedPlan plan)
    {
        if (plan.Total < SegmentedMinimumBytes ||
            checkpoint.TotalBytes != plan.Total ||
            (job.ExpectedTotalBytes is > 0 && job.ExpectedTotalBytes != plan.Total) ||
            ValidatorsConflict(checkpoint.ETag, checkpoint.LastModified, plan.ETag, plan.LastModified) ||
            checkpoint.Segments is not [{ Start: 0 } only] ||
            only.End != plan.Total - 1 ||
            only.Completed <= 0 ||
            only.Completed >= plan.Total)
        {
            return false;
        }

        try
        {
            var part = new FileInfo(job.PartFilePath);
            return part.Exists && part.Length >= only.Completed && part.Length <= plan.Total;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return false;
        }
    }

    /// <summary>The bytes from 0 as one finished range, and the rest in up to <paramref name="connections"/> ranges.</summary>
    private static List<SegmentCheckpoint> SplitSingleStreamSegments(long completed, long total, int connections)
    {
        var segments = new List<SegmentCheckpoint>
        {
            new() { Start = 0, End = completed - 1, Completed = completed },
        };
        foreach (var rest in BuildSegments(total - completed, connections))
        {
            segments.Add(new SegmentCheckpoint { Start = rest.Start + completed, End = rest.End + completed, Completed = 0 });
        }

        return segments;
    }

    /// <summary>Whether the part file still holds the first <paramref name="completed"/> bytes a one-stream checkpoint counts.</summary>
    private static bool SingleStreamDataHolds(DownloadJob job, long completed)
    {
        if (completed <= 0) return true;
        try
        {
            var part = new FileInfo(job.PartFilePath);
            return part.Exists && part.Length >= completed;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return false;
        }
    }

    /// <summary>
    /// Asks the server for one byte while the stream keeps running, and returns its answer only when it proves the
    /// stream's bytes can be the start of a segmented file. A failed or refused probe costs the stream nothing, and
    /// leaves what the job knows about range support as it was.
    /// </summary>
    private async Task<SegmentedPlan?> ProbeRunningSplitAsync(DownloadJob job, TransferState state, CancellationToken cancellationToken)
    {
        var probe = await ProbeSegmentedOnceAsync(job, hasLocalData: true, cancellationToken, recordRangeSupport: false)
            .ConfigureAwait(false);
        if (probe.Failure is not null || probe.Plan is not { } plan)
        {
            return null;
        }

        return plan.Total >= SegmentedMinimumBytes &&
               state.Total == plan.Total &&
               (job.ExpectedTotalBytes is not > 0 || job.ExpectedTotalBytes == plan.Total) &&
               !ValidatorsConflict(state.ETag, state.LastModified, plan.ETag, plan.LastModified)
            ? plan
            : null;
    }

    /// <summary>
    /// A segmented run from a one-stream checkpoint found the server refusing a range. The prefix the stream wrote
    /// was never touched by a segment, so the part is cut back to it and the one-stream checkpoint saved again, for
    /// the stream to go on from there rather than from zero.
    /// </summary>
    private async Task KeepSingleStreamPrefixAsync(DownloadJob job, DownloadCheckpoint checkpoint)
    {
        var prefix = checkpoint.CompletedBytes;
        try
        {
            using (var part = new FileStream(job.PartFilePath, FileMode.Open, FileAccess.Write, FileShare.ReadWrite))
            {
                if (part.Length < prefix)
                {
                    throw new IOException("The part file is shorter than the bytes the stream wrote.");
                }

                part.SetLength(prefix);
                part.Flush(flushToDisk: true);
            }

            if (_checkpoints is not null)
            {
                var single = new DownloadCheckpoint
                {
                    JobId = job.Id,
                    Url = job.Url,
                    FinalUrl = job.FinalUrl,
                    ETag = checkpoint.ETag,
                    LastModified = checkpoint.LastModified,
                    TotalBytes = checkpoint.TotalBytes,
                    ContentType = checkpoint.ContentType,
                    UpdatedAt = DateTimeOffset.UtcNow,
                };
                single.Segments.Add(new SegmentCheckpoint { Start = 0, End = checkpoint.Segments[0].End, Completed = prefix });
                await _checkpoints.SaveAsync(single, CancellationToken.None).ConfigureAwait(false);
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            await ResetSegmentedAsync(job).ConfigureAwait(false);
        }
    }

    /// <summary>
    /// The running stream stopped for a raised count after a probe proved the split possible, its bytes flushed and
    /// counted. Hands them to a segmented run, or returns null when that can't be done, for the stream to go on.
    /// </summary>
    private async Task<DownloadResult?> TrySplitSingleStreamAsync(
        DownloadJob job,
        TransferState state,
        SingleStreamRun single,
        IProgress<TransferProgress>? progress,
        CancellationToken cancellationToken)
    {
        var completed = state.RecordedBytes;
        if (single.TakePlan() is not { } plan || state.Total is not { } total || completed <= 0)
        {
            single.Declined();
            return null;
        }

        var checkpoint = new DownloadCheckpoint
        {
            JobId = job.Id,
            Url = job.Url,
            FinalUrl = job.FinalUrl,
            ETag = state.ETag,
            LastModified = state.LastModified,
            TotalBytes = total,
            ContentType = state.ContentType,
            Segments = [new SegmentCheckpoint { Start = 0, End = total - 1, Completed = completed }],
        };
        if (!WantsSplit(job, checkpoint))
        {
            single.Declined();
            return null;
        }

        lock (_runsSync)
        {
            // The segmented run takes the job's connection changes from here.
            if (_singleRuns.TryGetValue(job.Id, out var current) && ReferenceEquals(current, single))
            {
                _singleRuns.Remove(job.Id);
            }
        }

        DownloadResult? result;
        try
        {
            result = await TryExecuteSegmentedAsync(job, checkpoint, progress, cancellationToken, plan).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            // Stopped before the split began: the one-stream checkpoint saved before it stands.
            return DownloadResult.Fail(DownloadState.Cancelled,
                new JobFailure(FailureKind.Cancelled, "The download was cancelled.", false, true, DateTimeOffset.UtcNow),
                completed, total);
        }

        if (result is not null)
        {
            return result;
        }

        if (!SingleStreamDataHolds(job, completed))
        {
            // The prefix could not be kept after the server refused a range, and the part was reset.
            state.RestartFromZero();
        }

        lock (_runsSync)
        {
            _singleRuns[job.Id] = single;
        }

        single.Declined();
        return null;
    }

    /// <summary>
    /// A transfer on one stream, which a raised connection count splits. A raise first probes the server while the
    /// stream runs on; only a probe that proves the split stops the stream (<see cref="SplitToken"/>).
    /// </summary>
    private sealed class SingleStreamRun : IDisposable
    {
        private readonly object _sync = new();
        private readonly DownloadJob _job;
        private readonly TransferState _state;
        private readonly Func<CancellationToken, Task<SegmentedPlan?>> _probe;
        private readonly Func<bool> _refused;
        private readonly CancellationTokenSource _lifetime = new();
        private CancellationTokenSource _split = new();
        private int _connections;
        private int _changes;
        private bool _wanted;
        private bool _probing;
        private SegmentedPlan? _plan;
        private bool _disposed;

        public SingleStreamRun(
            DownloadJob job,
            TransferState state,
            int connectionsAtStart,
            Func<CancellationToken, Task<SegmentedPlan?>> probe,
            Func<bool> refused)
        {
            _job = job;
            _state = state;
            _connections = connectionsAtStart;
            _probe = probe;
            _refused = refused;
        }

        /// <summary>Cancelled when the stream should stop to be split. A fresh one after a declined split.</summary>
        public CancellationToken SplitToken
        {
            get
            {
                lock (_sync)
                {
                    return _split.Token;
                }
            }
        }

        /// <summary>The probe's answer that stopped the stream, taken once by the split.</summary>
        public SegmentedPlan? TakePlan()
        {
            lock (_sync)
            {
                var plan = _plan;
                _plan = null;
                return plan;
            }
        }

        /// <summary>The job's count changed: only a raise asks for a split; a drop to one connection drops the wish.</summary>
        public void ConnectionsChanged()
        {
            lock (_sync)
            {
                var count = _job.RequestedConnections;
                if (count > _connections) _wanted = true;
                else if (count <= 1) _wanted = false;
                _connections = count;
                _changes++;
            }

            SplitIfWanted();
        }

        /// <summary>
        /// Starts the probe for a wanted split once the stream's size is known and it has durable bytes. Called on a
        /// change, at the response headers and after each checkpoint save.
        /// </summary>
        public void SplitIfWanted()
        {
            // Outside the lock: it takes the engine's own.
            if (_refused())
            {
                return;
            }

            int changes;
            lock (_sync)
            {
                if (_disposed || !_wanted || _probing || _split.IsCancellationRequested ||
                    _job.RequestedConnections <= 1 ||
                    _job.ResumeSupported == false ||
                    _state.RecordedBytes <= 0 ||
                    _state.Total is not { } total || total < SegmentedMinimumBytes ||
                    total - _state.RecordedBytes < 2 * MinimumStealBytes)
                {
                    return;
                }

                _probing = true;
                changes = _changes;
            }

            _ = ProbeThenSplitAsync(changes);
        }

        private async Task ProbeThenSplitAsync(int changes)
        {
            SegmentedPlan? plan = null;
            try
            {
                plan = await _probe(_lifetime.Token).ConfigureAwait(false);
            }
            catch (Exception)
            {
                // Stopped with the run, or a probe that failed: no split, and the stream never noticed.
            }

            CancellationTokenSource? toCancel = null;
            var again = false;
            lock (_sync)
            {
                _probing = false;
                if (_disposed)
                {
                    return;
                }

                if (plan is not null && _wanted && _job.RequestedConnections > 1)
                {
                    _plan = plan;
                    toCancel = _split;
                }
                else if (_changes == changes)
                {
                    // Only another change tries again.
                    _wanted = false;
                }
                else
                {
                    again = true;
                }
            }

            if (again)
            {
                SplitIfWanted();
                return;
            }

            // Outside the lock: cancelling runs the transfer's cancellation callbacks.
            try
            {
                toCancel?.Cancel();
            }
            catch (ObjectDisposedException)
            {
                // The run ended meanwhile.
            }
        }

        /// <summary>The split could not be made: the stream goes on, and only another change tries again.</summary>
        public void Declined()
        {
            lock (_sync)
            {
                _wanted = false;
                _plan = null;
                if (_split.IsCancellationRequested)
                {
                    _split.Dispose();
                    _split = new CancellationTokenSource();
                }
            }
        }

        public void Dispose()
        {
            lock (_sync)
            {
                if (_disposed) return;
                _disposed = true;
                _split.Dispose();
            }

            try
            {
                _lifetime.Cancel();
            }
            catch (AggregateException)
            {
                // A probe's cancellation callback failed; the run is over anyway.
            }

            _lifetime.Dispose();
        }
    }
}

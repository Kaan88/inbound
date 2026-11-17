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
    /// The running stream stopped for a raised count, its bytes flushed and counted. Hands them to a segmented run,
    /// or returns null when that can't be done, for the stream to go on.
    /// </summary>
    private async Task<DownloadResult?> TrySplitSingleStreamAsync(
        DownloadJob job,
        TransferState state,
        SingleStreamRun single,
        IProgress<TransferProgress>? progress,
        CancellationToken cancellationToken)
    {
        var completed = state.RecordedBytes;
        if (state.Total is not { } total || completed <= 0)
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
            result = await TryExecuteSegmentedAsync(job, checkpoint, progress, cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            // Stopped during the probe: the one-stream checkpoint saved before it stands.
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
            // The segmented run began and then found the server refusing ranges, and reset the part.
            state.RestartFromZero();
        }

        lock (_runsSync)
        {
            _singleRuns[job.Id] = single;
        }

        single.Declined();
        return null;
    }

    /// <summary>A transfer on one stream, which a raised connection count stops so that it can be split.</summary>
    private sealed class SingleStreamRun(DownloadJob job, TransferState state) : IDisposable
    {
        private readonly object _sync = new();
        private CancellationTokenSource _split = new();
        private bool _wanted;
        private bool _disposed;

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

        /// <summary>The job's count changed: a raise above one splits the stream once its size and range support are known.</summary>
        public void ConnectionsChanged()
        {
            lock (_sync)
            {
                _wanted = job.RequestedConnections > 1;
            }

            SplitIfWanted();
        }

        public void SplitIfWanted()
        {
            CancellationTokenSource? toCancel = null;
            lock (_sync)
            {
                if (!_disposed && _wanted && !_split.IsCancellationRequested &&
                    job.RequestedConnections > 1 &&
                    job.ResumeSupported == true &&
                    state.Total is { } total && total >= SegmentedMinimumBytes &&
                    total - state.Bytes >= 2 * MinimumStealBytes)
                {
                    toCancel = _split;
                }
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
                _disposed = true;
                _split.Dispose();
            }
        }
    }
}

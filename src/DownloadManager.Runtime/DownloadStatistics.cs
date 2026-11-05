using DownloadManager.Contracts;
using DownloadManager.Core;

namespace DownloadManager.Runtime;

public sealed partial class DownloadManagerService
{
    public DownloadStatisticsDto GetStatistics()
    {
        lock (_sync)
        {
            var now = _timeProvider.GetLocalNow();
            var removed = _settings.ArchivedDownloads.Where(record => record.SourceJobId is { } source
                ? !_jobs.TryGetValue(source, out var live) || live.Job.State != DownloadState.Completed || live.Job.CompletedAt != record.CompletedAt
                : !_jobs.ContainsKey(record.Id)).ToArray();
            var records = removed.Concat(_jobs.Values.Select(runtime => new ArchivedDownloadRecord
            {
                Id = runtime.Job.Id, ReceivedBytes = runtime.Job.ReceivedBytes, State = runtime.Job.State,
                CreatedAt = runtime.Job.CreatedAt, CompletedAt = runtime.Job.CompletedAt,
            })).ToArray();
            var completed = records.Where(record => record.State == DownloadState.Completed).ToArray();
            var today = completed.Where(record => record.CompletedAt is { } at &&
                TimeZoneInfo.ConvertTime(at, _timeProvider.LocalTimeZone).Date == now.Date).ToArray();
            var rate = _jobs.Values.Where(runtime => runtime.Job.IsActive)
                .Sum(runtime => runtime.Job.BytesPerSecond is { } speed && double.IsFinite(speed) ? Math.Max(0, speed) : 0);
            return new DownloadStatisticsDto
            {
                CapturedAt = now, FirstRecordedAt = records.Length == 0 ? null : records.Min(record => record.CreatedAt),
                RecordedBytes = Sum(records), CompletedBytes = Sum(completed), CompletedTodayBytes = Sum(today),
                CompletedCount = completed.Length, CompletedTodayCount = today.Length,
                FailedCount = records.Count(record => record.State == DownloadState.Failed),
                CancelledCount = records.Count(record => record.State == DownloadState.Cancelled),
                RetainedCount = _jobs.Count, ArchivedCount = removed.Length,
                ActiveCount = _jobs.Values.Count(runtime => runtime.Job.IsActive),
                QueuedCount = _jobs.Values.Count(runtime => runtime.Job.State == DownloadState.Queued),
                PausedCount = _jobs.Values.Count(runtime => runtime.Job.State == DownloadState.Paused),
                BytesPerSecond = double.IsFinite(rate) ? rate : double.MaxValue,
            };
        }

        static long Sum(IEnumerable<ArchivedDownloadRecord> records)
        {
            long result = 0;
            foreach (var record in records)
            {
                var bytes = Math.Max(0, record.ReceivedBytes);
                result = result > long.MaxValue - bytes ? long.MaxValue : result + bytes;
            }
            return result;
        }
    }
}

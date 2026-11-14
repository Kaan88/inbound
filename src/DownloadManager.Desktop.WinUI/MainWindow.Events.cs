using DownloadManager.Contracts;
using Microsoft.UI.Xaml;

namespace DownloadManager.Desktop.WinUI;

/// <summary>Which family an event belongs to. A new family (Sites, say) adds a value here and a case in ApplyEventBatch.</summary>
internal enum WindowEventFamily
{
    Snapshot,
    Groups,
    Job,
    Connection,
}

/// <summary>Family, the connection it came from, and an id inside the family (a job id; empty for the one-of-a-kind ones).</summary>
internal readonly record struct WindowEventKey(WindowEventFamily Family, object Source, Guid Id);

/// <summary>The payload (a <see cref="JobDto"/>, snapshot, group list, or connection state), with what merging must not lose.</summary>
internal readonly record struct WindowEvent(object? Payload, bool SawUnfinished);

/// <summary>
/// Host events reach the window through one coalescer and are applied once per tick (PERF.16, PERF.19): the first event
/// after an idle moment starts a one-shot timer of <see cref="EventTick"/>; when it fires, the newest state of each job is
/// applied together, then the status bar, buttons and torrent views once. With nothing pending, no timer runs.
/// Flushed at once instead of waiting for the tick: a connection going up or down, a reply to one of this window's own
/// commands (<see cref="HostConnection.CommandReplied"/>), and a snapshot read by hand.
/// </summary>
public sealed partial class MainWindow
{
    internal static readonly TimeSpan EventTick = TimeSpan.FromMilliseconds(100);

    private EventCoalescer<WindowEventKey, string, WindowEvent> _events = null!;

    // Made in the constructor, before the first connection is wired: events arrive on other threads.
    private void InitializeEventTick()
        => _events = new(new DispatcherTickScheduler(DispatcherQueue, EventTick, () => ApplyEventBatch(_events.Drain())),
            MergeJobKind, MergeJobEvent);

    /// <summary>A finish or failure inside a tick is still news when a later update of the same job follows it.</summary>
    private static string MergeJobKind(string older, string newer)
        => older is "completed" or "failed" && newer is not ("completed" or "failed") ? older : newer;

    private static WindowEvent MergeJobEvent(WindowEvent older, WindowEvent newer)
        => newer with { SawUnfinished = older.SawUnfinished || older.Payload is JobDto { State: not "completed" } };

    private static bool CoversJobs(WindowEventKey key, object source)
        => ReferenceEquals(key.Source, source) && key.Family is WindowEventFamily.Job or WindowEventFamily.Groups;

    /// <summary>Takes what is pending and applies it now, on the UI thread: before an order-sensitive step, or for the user's own action.</summary>
    private void FlushEvents() => ApplyEventBatch(_events.FlushNow());

    private void ApplyEventBatch(List<CoalescedItem<WindowEventKey, string, WindowEvent>> items)
    {
        if (items.Count == 0) return;
        var jobsTouched = false;
        var syncTorrents = false;
        foreach (var item in items)
        {
            var connection = (HostConnection)item.Key.Source;
            switch (item.Key.Family)
            {
                case WindowEventFamily.Snapshot:
                    var snapshot = (SnapshotDto)item.Value.Payload!;
                    if (connection == _local && _connection != _local) ApplyLocalSnapshot(snapshot);
                    if (connection == _connection) ApplySnapshot(snapshot);
                    break;
                case WindowEventFamily.Groups:
                    if (connection != _connection) break;
                    _groups = (List<DownloadGroupDto>)item.Value.Payload!;
                    RebuildVisible();
                    break;
                case WindowEventFamily.Job:
                    if (connection != _connection) break;
                    jobsTouched = true;
                    var job = (JobDto)item.Value.Payload!;
                    ApplyJobEvent(job, item.Op == CoalescedOp.Remove ? "removed" : item.Kind, item.Value.SawUnfinished, ref syncTorrents);
                    break;
                case WindowEventFamily.Connection:
                    if (connection != _connection) break;
                    var (connected, error) = ((bool, string?))item.Value.Payload!;
                    ApplyConnection(connected, error);
                    break;
            }
        }

        if (!jobsTouched) return;
        if (syncTorrents) SyncTorrentViews();
        EmptyText.Visibility = _visibleJobs.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        UpdateStatusText();
        UpdateButtons();
    }

    /// <summary>The one-shot timer behind <see cref="EventCoalescer{TKey,TKind,TValue}"/>: created once, running only while events wait.</summary>
    private sealed class DispatcherTickScheduler : ITickScheduler
    {
        private readonly Microsoft.UI.Dispatching.DispatcherQueue _queue;
        private readonly Microsoft.UI.Dispatching.DispatcherQueueTimer _timer;

        public DispatcherTickScheduler(Microsoft.UI.Dispatching.DispatcherQueue queue, TimeSpan after, Action tick)
        {
            _queue = queue;
            _timer = queue.CreateTimer();
            _timer.IsRepeating = false;
            _timer.Interval = after;
            _timer.Tick += (_, _) => tick();
        }

        // Any thread: the timer is only touched on the UI thread.
        public void Schedule() => _queue.TryEnqueue(() =>
        {
            if (!_timer.IsRunning) _timer.Start();
        });

        public void Cancel()
        {
            if (_timer.IsRunning) _timer.Stop();
        }
    }
}

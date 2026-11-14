using DownloadManager.Desktop.WinUI;
using Xunit;

namespace DownloadManager.Core.Tests;

public sealed class EventCoalescerTests
{
    private sealed class FakeScheduler : ITickScheduler
    {
        public int Schedules;
        public int Cancels;

        // What a one-shot timer would be doing: running between Schedule and the drain or Cancel that ends it.
        public bool Running;

        public void Schedule() { Schedules++; Running = true; }

        public void Cancel() { Cancels++; Running = false; }
    }

    private static (EventCoalescer<string, string, int> Events, FakeScheduler Scheduler) Make(
        Func<string, string, string>? mergeKind = null, Func<int, int, int>? mergeValue = null)
    {
        var scheduler = new FakeScheduler();
        return (new EventCoalescer<string, string, int>(scheduler, mergeKind, mergeValue), scheduler);
    }

    private static List<(CoalescedOp Op, string Key, string Kind, int Value)> Take(EventCoalescer<string, string, int> events)
        => events.Drain().Select(item => (item.Op, item.Key, item.Kind, item.Value)).ToList();

    [Fact]
    public void Keeping_place_leaves_keys_in_the_order_first_seen()
    {
        var (events, _) = Make();
        events.Upsert("a", "added", 1, keepPlace: true);
        events.Upsert("b", "added", 1, keepPlace: true);
        events.Upsert("a", "progress", 2, keepPlace: true);

        Assert.Equal([(CoalescedOp.Upsert, "a", "progress", 2), (CoalescedOp.Upsert, "b", "added", 1)], Take(events));

        events.Upsert("a", "added", 1);
        events.Upsert("b", "added", 1);
        events.Upsert("a", "progress", 2);
        Assert.Equal([(CoalescedOp.Upsert, "b", "added", 1), (CoalescedOp.Upsert, "a", "progress", 2)], Take(events));
    }

    [Fact]
    public void Latest_state_per_key_wins_and_one_item_comes_out()
    {
        var (events, _) = Make();
        events.Upsert("a", "progress", 1);
        events.Upsert("a", "progress", 2);
        events.Upsert("a", "progress", 3);

        Assert.Equal([(CoalescedOp.Upsert, "a", "progress", 3)], Take(events));
    }

    [Fact]
    public void Add_then_change_is_one_upsert_with_the_latest_state()
    {
        var (events, _) = Make();
        events.Upsert("a", "added", 1);
        events.Upsert("a", "progress", 2);

        Assert.Equal([(CoalescedOp.Upsert, "a", "progress", 2)], Take(events));
    }

    [Fact]
    public void Remove_after_change_wins()
    {
        var (events, _) = Make();
        events.Upsert("a", "progress", 1);
        events.Remove("a", "removed", 1);

        Assert.Equal([(CoalescedOp.Remove, "a", "removed", 1)], Take(events));
    }

    [Fact]
    public void Change_after_remove_is_a_remove_then_an_upsert()
    {
        var (events, _) = Make();
        events.Remove("a", "removed", 1);
        events.Upsert("a", "added", 2);

        Assert.Equal([(CoalescedOp.Remove, "a", "removed", 1), (CoalescedOp.Upsert, "a", "added", 2)], Take(events));
    }

    [Fact]
    public void A_second_remove_after_that_leaves_just_the_remove()
    {
        var (events, _) = Make();
        events.Remove("a", "removed", 1);
        events.Upsert("a", "added", 2);
        events.Remove("a", "removed", 3);

        Assert.Equal([(CoalescedOp.Remove, "a", "removed", 3)], Take(events));
    }

    [Fact]
    public void Items_come_out_in_the_order_of_their_newest_event()
    {
        var (events, _) = Make();
        events.Upsert("a", "progress", 1);
        events.Upsert("b", "progress", 1);
        events.Upsert("a", "progress", 2);
        events.Remove("c", "removed", 0);

        Assert.Equal(["b", "a", "c"], Take(events).Select(item => item.Key));
    }

    [Fact]
    public void Merge_functions_keep_a_finish_and_what_the_older_one_saw()
    {
        var (events, _) = Make(
            (older, newer) => older == "completed" && newer != "completed" ? older : newer,
            (older, newer) => older + newer);
        events.Upsert("a", "completed", 10);
        events.Upsert("a", "progress", 1);

        Assert.Equal([(CoalescedOp.Upsert, "a", "completed", 11)], Take(events));
    }

    [Fact]
    public void A_reset_replaces_everything_pending_it_covers()
    {
        var (events, _) = Make();
        events.Upsert("job:a", "progress", 1);
        events.Remove("job:b", "removed", 1);
        events.Reset("snapshot", "snapshot", 9, key => key.StartsWith("job:", StringComparison.Ordinal) || key == "snapshot");
        events.Upsert("job:a", "progress", 2);

        Assert.Equal([(CoalescedOp.Reset, "snapshot", "snapshot", 9), (CoalescedOp.Upsert, "job:a", "progress", 2)], Take(events));
    }

    [Fact]
    public void A_reset_leaves_other_families_and_barriers_alone()
    {
        var (events, _) = Make();
        events.Upsert("site:x", "progress", 1);
        events.Barrier("conn", "down", 0);
        events.Upsert("job:a", "progress", 1);
        events.Reset("snapshot", "snapshot", 9, key => key.StartsWith("job:", StringComparison.Ordinal));

        Assert.Equal(["site:x", "conn", "snapshot"], Take(events).Select(item => item.Key));
    }

    [Fact]
    public void A_later_reset_under_the_same_key_replaces_the_earlier_one()
    {
        var (events, _) = Make();
        events.Reset("snapshot", "snapshot", 1);
        events.Reset("snapshot", "snapshot", 2);

        Assert.Equal([(CoalescedOp.Reset, "snapshot", "snapshot", 2)], Take(events));
    }

    [Fact]
    public void A_barrier_keeps_its_place_and_nothing_merges_across_it()
    {
        var (events, _) = Make();
        events.Upsert("a", "progress", 1);
        events.Barrier("conn", "down", 0);
        events.Upsert("a", "progress", 2);

        Assert.Equal([(CoalescedOp.Upsert, "a", "progress", 1), (CoalescedOp.Barrier, "conn", "down", 0), (CoalescedOp.Upsert, "a", "progress", 2)],
            Take(events));
    }

    [Fact]
    public void An_empty_drain_does_nothing_and_schedules_nothing()
    {
        var (events, scheduler) = Make();

        Assert.Empty(events.Drain());
        Assert.Empty(events.FlushNow());
        Assert.False(events.HasPending);
        Assert.Equal(0, scheduler.Schedules);
        Assert.Equal(0, scheduler.Cancels);
        Assert.False(scheduler.Running);
    }

    [Fact]
    public void A_burst_schedules_one_tick_and_a_drain_leaves_no_timer_running()
    {
        var (events, scheduler) = Make();
        for (var i = 0; i < 50; i++) events.Upsert($"job{i % 5}", "progress", i);
        Assert.Equal(1, scheduler.Schedules);
        Assert.True(scheduler.Running);

        // The tick fires: the timer has ended, the batch is taken.
        scheduler.Running = false;
        Assert.Equal(5, events.Drain().Count);
        Assert.False(events.HasPending);
        Assert.False(scheduler.Running);

        // Idle: draining again wakes nothing.
        Assert.Empty(events.Drain());
        Assert.Equal(1, scheduler.Schedules);

        // The next event after idle asks for a new tick.
        events.Upsert("job0", "progress", 99);
        Assert.Equal(2, scheduler.Schedules);
        Assert.True(scheduler.Running);
    }

    [Fact]
    public void An_event_during_the_apply_of_a_batch_schedules_the_next_tick()
    {
        var (events, scheduler) = Make();
        events.Upsert("a", "progress", 1);
        scheduler.Running = false;

        var batch = events.Drain();
        events.Upsert("a", "progress", 2);

        Assert.Single(batch);
        Assert.Equal(2, scheduler.Schedules);
        Assert.Equal([(CoalescedOp.Upsert, "a", "progress", 2)], Take(events));
    }

    [Fact]
    public void Flush_takes_what_is_pending_and_cancels_the_tick_that_was_for_it()
    {
        var (events, scheduler) = Make();
        events.Upsert("a", "progress", 1);

        var items = events.FlushNow();

        Assert.Single(items);
        Assert.Equal(1, scheduler.Cancels);
        Assert.False(scheduler.Running);
        Assert.False(events.HasPending);
    }

    [Fact]
    public void Clear_drops_everything_and_stops_the_timer()
    {
        var (events, scheduler) = Make();
        events.Upsert("a", "progress", 1);
        events.Barrier("conn", "down", 0);

        events.Clear();

        Assert.False(events.HasPending);
        Assert.False(scheduler.Running);
        Assert.Empty(events.Drain());
    }

    [Fact]
    public async Task Posting_from_many_threads_keeps_only_the_latest_per_key()
    {
        var (events, scheduler) = Make();
        var writers = Enumerable.Range(0, 4).Select(writer => Task.Run(() =>
        {
            for (var i = 1; i <= 2000; i++) events.Upsert($"job{writer}", "progress", i);
        })).ToArray();
        await Task.WhenAll(writers);

        var items = Take(events);

        Assert.Equal(4, items.Count);
        Assert.All(items, item => Assert.Equal(2000, item.Value));
        Assert.Equal(1, scheduler.Schedules);
    }
}

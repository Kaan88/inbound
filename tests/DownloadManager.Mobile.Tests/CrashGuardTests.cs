using DownloadManager.Mobile;

namespace DownloadManager.Mobile.Tests;

public class CrashGuardTests
{
    private static readonly byte[] RunA = [1, 2, 3, 4];
    private static readonly byte[] RunB = [9, 9, 9, 9];

    private static ExitRecord Rec(byte[] run, int reason, long t = 1) => new(run, reason, t);

    [Theory]
    [InlineData(4)]
    [InlineData(5)]
    [InlineData(6)]
    public void A_crash_native_crash_or_anr_with_a_session_adds_one(int reason)
    {
        var d = CrashGuard.AtProcessStart(new CrashGuardState(1, RunA), [Rec(RunA, reason)]);
        Assert.Equal(2, d.Next.Count);
        Assert.Null(d.Next.SessionRunId);
    }

    [Theory]
    [InlineData(10)] // user
    [InlineData(3)] // low memory
    [InlineData(1)] // exit self
    [InlineData(8)] // signaled
    public void Any_other_exit_with_a_session_resets(int reason)
    {
        var d = CrashGuard.AtProcessStart(new CrashGuardState(2, RunA), [Rec(RunA, reason)]);
        Assert.Equal(0, d.Next.Count);
        Assert.Null(d.Next.SessionRunId);
    }

    [Fact]
    public void No_record_for_the_run_resets_as_a_reboot_does()
    {
        var d = CrashGuard.AtProcessStart(new CrashGuardState(2, RunA), [Rec(RunB, CrashGuard.ReasonCrash)]);
        Assert.Equal(0, d.Next.Count);
    }

    [Fact]
    public void A_crash_of_a_run_without_a_session_changes_nothing()
    {
        var d = CrashGuard.AtProcessStart(new CrashGuardState(2, null), [Rec(RunA, CrashGuard.ReasonCrash)]);
        Assert.Equal(2, d.Next.Count);
    }

    [Fact]
    public void The_newest_record_of_the_run_decides()
    {
        var d = CrashGuard.AtProcessStart(new CrashGuardState(0, RunA), [Rec(RunA, 4, 5), Rec(RunA, 10, 9)]);
        Assert.Equal(0, d.Next.Count);
    }

    [Fact]
    public void Three_crashes_in_a_row_trip_and_a_record_is_used_once()
    {
        var s = CrashGuardState.Empty;
        for (var i = 1; i <= 3; i++)
        {
            Assert.False(s.Tripped);
            s = CrashGuard.SessionBegan(s, RunA);
            s = CrashGuard.AtProcessStart(s, [Rec(RunA, CrashGuard.ReasonAnr)]).Next;
            Assert.Equal(i, s.Count);
        }

        Assert.True(s.Tripped);
        // The marker was cleared: a later start (no session since) does not count the same record again, and stays tripped.
        var again = CrashGuard.AtProcessStart(s, [Rec(RunA, CrashGuard.ReasonAnr)]).Next;
        Assert.Equal(3, again.Count);
        Assert.True(again.Tripped);
    }

    [Fact]
    public void A_clean_session_end_resets_but_only_for_its_own_run()
    {
        var s = CrashGuard.SessionBegan(new CrashGuardState(2, null), RunA);
        Assert.Equal(2, CrashGuard.SessionEnded(s, RunB).Count);
        var ended = CrashGuard.SessionEnded(s, RunA);
        Assert.Equal(0, ended.Count);
        Assert.Null(ended.SessionRunId);
    }

    [Fact]
    public void Reset_clears_the_count_and_keeps_a_running_session()
    {
        var s = CrashGuard.Reset(new CrashGuardState(3, RunA));
        Assert.Equal(0, s.Count);
        Assert.False(s.Tripped);
        Assert.Equal(RunA, s.SessionRunId);
    }

    [Fact]
    public void State_round_trips_and_damage_is_empty()
    {
        var s = CrashGuard.Parse(CrashGuard.Serialize(new CrashGuardState(2, RunA)));
        Assert.Equal(2, s.Count);
        Assert.Equal(RunA, s.SessionRunId);
        Assert.Null(CrashGuard.Parse(CrashGuard.Serialize(new CrashGuardState(1, null))).SessionRunId);
        Assert.Equal(CrashGuardState.Empty, CrashGuard.Parse(null));
        Assert.Equal(0, CrashGuard.Parse("count=x\nsession=zz\n"u8.ToArray()).Count);
    }
}

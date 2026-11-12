using DownloadManager.Contracts;
using DownloadManager.Runtime;

namespace DownloadManager.Integration.Tests;

/// <summary>T5: "Ready to choose files" for magnets waiting for their choice, on a manual clock.</summary>
public sealed partial class RuntimeServiceTests
{
    private sealed class TorrentNoticeRig
    {
        public ManualTime Time { get; } = new();
        public CountingTime Counted { get; }

        private TorrentNoticeRig() => Counted = new CountingTime(Time);
        public List<TorrentNotices.TorrentNotice> Told { get; } = [];
        public bool TellReady { get; set; } = true;
        public bool InFront { get; set; }
        public TorrentNotices Notices { get; private set; } = null!;

        public static TorrentNoticeRig Start(params JobDto[] atStart)
        {
            var rig = new TorrentNoticeRig();
            rig.Notices = new TorrentNotices(atStart, rig.Counted, () => rig.TellReady, () => rig.InFront, rig.Told.Add);
            return rig;
        }

        public void Wait(int seconds) => Time.Advance(TimeSpan.FromSeconds(seconds));
    }

    private static JobDto TorrentJob(Guid id, bool waiting, string name = "Album") => new()
    {
        Id = id,
        FileName = name,
        State = "paused",
        Torrent = new TorrentDto { InfoHash = new string('a', 40), Name = name, HasMetadata = waiting, AwaitingFileChoice = waiting },
    };

    [Fact]
    public void AMagnetReadyToChooseIsToldOnceAfterTheWait()
    {
        var rig = TorrentNoticeRig.Start();
        rig.Notices.Saw(TorrentJob(NoticeA, waiting: false));
        rig.Notices.Saw(TorrentJob(NoticeA, waiting: true));
        rig.Notices.Saw(TorrentJob(NoticeA, waiting: true)); // the paused change after the metadata one
        rig.Wait(9);
        Assert.Empty(rig.Told);
        rig.Wait(1);
        var notice = Assert.Single(rig.Told);
        Assert.Equal(("Ready to choose files", "Album"), (notice.Title, notice.Message));
        rig.Notices.Saw(TorrentJob(NoticeA, waiting: true));
        rig.Wait(60);
        Assert.Single(rig.Told);
    }

    [Fact]
    public void SeveralAtOnceAreOneNoticeAndAChoiceMadeMeanwhileIsNone()
    {
        var rig = TorrentNoticeRig.Start(TorrentJob(NoticeD, waiting: true)); // already waiting: not news
        rig.Notices.Saw(TorrentJob(NoticeA, waiting: true));
        rig.Notices.Saw(TorrentJob(NoticeB, waiting: true));
        rig.Notices.Saw(TorrentJob(NoticeC, waiting: true));
        rig.Notices.Saw(TorrentJob(NoticeC, waiting: false)); // chosen before the wait ended
        rig.Wait(10);
        Assert.Equal("2 torrents", Assert.Single(rig.Told).Message);
    }

    [Fact]
    public void NothingIsToldWithTheWindowInFrontOrNotificationsOff()
    {
        var rig = TorrentNoticeRig.Start();
        rig.InFront = true;
        rig.Notices.Saw(TorrentJob(NoticeA, waiting: true));
        rig.Wait(10);
        rig.InFront = false;
        rig.TellReady = false;
        rig.Notices.Saw(TorrentJob(NoticeB, waiting: true));
        rig.Wait(10);
        Assert.Empty(rig.Told);

        // Chosen, then waiting again (a new choice asked for): news again.
        rig.TellReady = true;
        rig.Notices.Saw(TorrentJob(NoticeA, waiting: false));
        rig.Notices.Saw(TorrentJob(NoticeA, waiting: true));
        rig.Wait(10);
        Assert.Single(rig.Told);
    }

    [Fact]
    public void NoTimerRunsWhileNothingWaits()
    {
        var rig = TorrentNoticeRig.Start();
        rig.Notices.Saw(TorrentJob(NoticeA, waiting: false));
        rig.Notices.Saw(new JobDto { Id = NoticeB, State = "downloading" });
        Assert.Equal(0, rig.Counted.Live);
        rig.Notices.Saw(TorrentJob(NoticeA, waiting: true));
        Assert.Equal(1, rig.Counted.Live);
        rig.Wait(10);
        Assert.Equal(0, rig.Counted.Live);
    }

    /// <summary>The manual clock, counting the timers not yet disposed.</summary>
    private sealed class CountingTime(TimeProvider inner) : TimeProvider
    {
        private int _live;

        public int Live => Volatile.Read(ref _live);

        public override DateTimeOffset GetUtcNow() => inner.GetUtcNow();

        public override ITimer CreateTimer(TimerCallback callback, object? state, TimeSpan dueTime, TimeSpan period)
        {
            Interlocked.Increment(ref _live);
            return new Counted(this, inner.CreateTimer(callback, state, dueTime, period));
        }

        private sealed class Counted(CountingTime owner, ITimer timer) : ITimer
        {
            private int _disposed;

            public bool Change(TimeSpan dueTime, TimeSpan period) => timer.Change(dueTime, period);

            public void Dispose()
            {
                if (Interlocked.Exchange(ref _disposed, 1) == 0) Interlocked.Decrement(ref owner._live);
                timer.Dispose();
            }

            public ValueTask DisposeAsync()
            {
                Dispose();
                return ValueTask.CompletedTask;
            }
        }
    }
}

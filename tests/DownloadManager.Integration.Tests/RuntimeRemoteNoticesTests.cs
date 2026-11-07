using DownloadManager.Core.Providers;
using DownloadManager.Runtime.Remote;
using TestServer;

namespace DownloadManager.Integration.Tests;

/// <summary>
/// The tray balloon for remote items, on a manual clock: what it counts, when, and when it says nothing. The last
/// test runs it against the real coordinator and the mock, as the Host does. All values are invented.
/// </summary>
public sealed partial class RuntimeServiceTests
{
    private sealed class NoticeRig
    {
        public ManualTime Time { get; } = new();
        public List<RemoteNotice> Told { get; } = [];
        public List<string> Log { get; } = [];
        public bool TellReady { get; set; } = true;
        public bool InFront { get; set; }
        public Action<RemoteNotice>? Instead { get; set; }
        public RemoteNotices Notices { get; private set; } = null!;

        public static NoticeRig Start(params RemoteJobView[] atStart)
        {
            var rig = new NoticeRig();
            rig.Notices = new RemoteNotices(atStart, rig.Time, () => rig.TellReady, () => rig.InFront,
                notice => (rig.Instead ?? rig.Told.Add)(notice), rig.Log.Add);
            return rig;
        }

        public void Saw(Guid id, RemoteJobState state, bool held = false) => Notices.Saw(NoticeJob(id, state, held));

        public void Wait(int seconds) => Time.Advance(TimeSpan.FromSeconds(seconds));
    }

    private static RemoteJobView NoticeJob(Guid id, RemoteJobState state, bool held = false, string provider = "TorBox") => new()
    {
        Id = id,
        Provider = provider.ToLowerInvariant(),
        ProviderName = provider,
        Kind = RemoteItemKind.Torrent,
        State = state,
        Status = state.ToString(),
        HeldForApproval = held,
    };

    private static readonly Guid NoticeA = Guid.Parse("a0000000-0000-4000-8000-000000000001");
    private static readonly Guid NoticeB = Guid.Parse("b0000000-0000-4000-8000-000000000002");
    private static readonly Guid NoticeC = Guid.Parse("c0000000-0000-4000-8000-000000000003");
    private static readonly Guid NoticeD = Guid.Parse("d0000000-0000-4000-8000-000000000004");

    [Fact]
    public void ABalloonSaysNothingAtStartAboutWhatAlreadyNeededTheUser()
    {
        var rig = NoticeRig.Start(
            NoticeJob(NoticeA, RemoteJobState.AwaitingSelection),
            NoticeJob(NoticeB, RemoteJobState.Failed),
            NoticeJob(NoticeC, RemoteJobState.Waiting));
        rig.Wait(60);

        // Heard of again just as they were, as a coordinator's first pass may tell them, they are still not news.
        rig.Saw(NoticeA, RemoteJobState.AwaitingSelection);
        rig.Saw(NoticeB, RemoteJobState.Failed);
        rig.Wait(60);

        Assert.Empty(rig.Told);
    }

    [Fact]
    public void ABurstOfChangesIsOneBalloonWithItsCountsOnceItsWaitIsOver()
    {
        var rig = NoticeRig.Start();
        rig.Saw(NoticeA, RemoteJobState.Waiting);
        rig.Saw(NoticeA, RemoteJobState.AwaitingSelection);
        rig.Wait(10);
        rig.Saw(NoticeB, RemoteJobState.AwaitingSelection);
        rig.Saw(NoticeC, RemoteJobState.Failed);
        rig.Wait(19);
        Assert.Empty(rig.Told);

        // Thirty seconds from the first change, not pushed back by the later ones.
        rig.Wait(1);
        Assert.Equal(new RemoteNotice("TorBox", "2 ready to choose · 1 failed"), Assert.Single(rig.Told));
        rig.Wait(120);
        Assert.Single(rig.Told);
    }

    [Fact]
    public void WhatIsDealtWithBeforeTheWaitIsOverIsNeverSaid()
    {
        var rig = NoticeRig.Start();
        rig.Saw(NoticeA, RemoteJobState.AwaitingSelection);
        rig.Wait(10);
        rig.Saw(NoticeA, RemoteJobState.LocalStarted);
        rig.Wait(30);

        // Taken off the list is dealt with too.
        rig.Saw(NoticeB, RemoteJobState.Failed);
        rig.Wait(10);
        rig.Notices.Gone(NoticeB);
        rig.Wait(30);

        Assert.Empty(rig.Told);
    }

    [Fact]
    public void OnlyARisingCountSpeaksAgain()
    {
        var rig = NoticeRig.Start();
        rig.Saw(NoticeA, RemoteJobState.AwaitingSelection);
        rig.Wait(30);
        Assert.Equal("1 ready to choose", Assert.Single(rig.Told).Message);

        // The same again, or a change that adds nothing to it, says nothing.
        rig.Saw(NoticeA, RemoteJobState.AwaitingSelection);
        rig.Saw(NoticeD, RemoteJobState.Waiting);
        rig.Wait(60);
        Assert.Single(rig.Told);

        rig.Saw(NoticeB, RemoteJobState.AwaitingSelection);
        rig.Wait(30);
        Assert.Equal("2 ready to choose", rig.Told[1].Message);

        // What was dealt with doesn't stay in what the next count has to pass: one chosen and another come is a rise.
        rig.Saw(NoticeA, RemoteJobState.LocalStarted);
        rig.Saw(NoticeC, RemoteJobState.AwaitingSelection);
        rig.Wait(30);
        Assert.Equal("2 ready to choose", rig.Told[2].Message);

        // Everything dealt with says nothing, and the next one after it is said.
        rig.Saw(NoticeB, RemoteJobState.LocalStarted);
        rig.Saw(NoticeC, RemoteJobState.LocalStarted);
        rig.Wait(60);
        Assert.Equal(3, rig.Told.Count);
        rig.Saw(NoticeD, RemoteJobState.Failed);
        rig.Wait(30);
        Assert.Equal("1 failed", rig.Told[3].Message);

        // Retried and failed again is a rise from nothing.
        rig.Saw(NoticeD, RemoteJobState.Waiting);
        rig.Saw(NoticeD, RemoteJobState.Failed);
        rig.Wait(30);
        Assert.Equal(5, rig.Told.Count);
        Assert.Equal("1 failed", rig.Told[4].Message);
    }

    [Fact]
    public void CopiedAndBlockedRowsNeverCount()
    {
        var rig = NoticeRig.Start();
        rig.Saw(NoticeA, RemoteJobState.Intended, held: true);
        rig.Saw(NoticeB, RemoteJobState.Blocked);
        rig.Saw(NoticeC, RemoteJobState.Failed, held: true);
        rig.Wait(60);
        Assert.Empty(rig.Told);

        // Released by Enter, the copied one counts like any other once it asks.
        rig.Saw(NoticeA, RemoteJobState.AwaitingSelection);
        rig.Wait(30);
        Assert.Equal(new RemoteNotice("TorBox", "1 ready to choose"), Assert.Single(rig.Told));
    }

    [Fact]
    public void ReadyToChooseFollowsTheNotificationSettingAndAFailureAlwaysShows()
    {
        var rig = NoticeRig.Start();
        rig.TellReady = false;
        rig.Saw(NoticeA, RemoteJobState.AwaitingSelection);
        rig.Wait(30);
        Assert.Empty(rig.Told);

        rig.Saw(NoticeB, RemoteJobState.Failed);
        rig.Wait(30);
        Assert.Equal(new RemoteNotice("TorBox", "1 failed"), Assert.Single(rig.Told));

        // Turned on later, what was already there isn't news, and what comes after counts it in.
        rig.TellReady = true;
        rig.Saw(NoticeA, RemoteJobState.AwaitingSelection);
        rig.Wait(60);
        Assert.Single(rig.Told);
        rig.Saw(NoticeC, RemoteJobState.AwaitingSelection);
        rig.Wait(30);
        Assert.Equal("2 ready to choose · 1 failed", rig.Told[1].Message);
    }

    [Fact]
    public void NothingIsSaidWhileTheAppIsInFrontAndWhatItShowedIsNoNewsLater()
    {
        var rig = NoticeRig.Start();
        rig.InFront = true;
        rig.Saw(NoticeA, RemoteJobState.AwaitingSelection);
        rig.Wait(30);
        Assert.Empty(rig.Told);

        rig.InFront = false;
        rig.Saw(NoticeA, RemoteJobState.AwaitingSelection);
        rig.Wait(60);
        Assert.Empty(rig.Told);

        rig.Saw(NoticeB, RemoteJobState.Failed);
        rig.Wait(30);
        Assert.Equal("1 ready to choose · 1 failed", Assert.Single(rig.Told).Message);
    }

    [Fact]
    public void EachProviderSpeaksForItselfAndOnlyWhenItsOwnCountRose()
    {
        var rig = NoticeRig.Start(NoticeJob(NoticeA, RemoteJobState.AwaitingSelection, provider: "Other"));
        rig.Notices.Saw(NoticeJob(NoticeB, RemoteJobState.AwaitingSelection));
        rig.Wait(30);
        Assert.Equal(new RemoteNotice("TorBox", "1 ready to choose"), Assert.Single(rig.Told));

        rig.Notices.Saw(NoticeJob(NoticeC, RemoteJobState.Failed, provider: "Other"));
        rig.Wait(30);
        Assert.Equal(new RemoteNotice("Other", "1 ready to choose · 1 failed"), rig.Told[1]);
    }

    [Fact]
    public void ABalloonNeverCarriesANameAnAddressOrAFailuresWords()
    {
        var rig = NoticeRig.Start();
        rig.Notices.Saw(NoticeJob(NoticeA, RemoteJobState.AwaitingSelection) with { Name = "Invented Name https://cdn.invalid/dl?token=invented-token" });
        rig.Notices.Saw(NoticeJob(NoticeB, RemoteJobState.Failed) with
        {
            Name = "Another Invented Name",
            DisplayHost = "hoster.invalid",
            FailureMessage = "invented failure at https://hoster.invalid/file?key=invented-key",
            Notes = ["invented note"],
        });
        rig.Wait(30);

        var told = Assert.Single(rig.Told);
        Assert.Equal(new RemoteNotice("TorBox", "1 ready to choose · 1 failed"), told);
        foreach (var part in new[] { "Invented", "invalid", "token", "key", "note" })
        {
            Assert.DoesNotContain(part, told.Title + "\n" + told.Message, StringComparison.OrdinalIgnoreCase);
        }
    }

    [Fact]
    public void NothingIsSaidOnceItIsDisposed()
    {
        var rig = NoticeRig.Start();
        rig.Saw(NoticeA, RemoteJobState.AwaitingSelection);
        rig.Notices.Dispose();
        rig.Wait(60);
        rig.Saw(NoticeB, RemoteJobState.Failed);
        rig.Wait(60);

        Assert.Empty(rig.Told);
    }

    [Fact]
    public void ABalloonThatCantBeShownIsLoggedAndTheNextOneStillComes()
    {
        var rig = NoticeRig.Start();
        rig.Instead = _ => throw new InvalidOperationException("invented");
        rig.Saw(NoticeA, RemoteJobState.AwaitingSelection);
        rig.Wait(30);
        Assert.Equal(["A remote notice failed: InvalidOperationException"], rig.Log);

        rig.Instead = null;
        rig.Saw(NoticeB, RemoteJobState.Failed);
        rig.Wait(30);
        Assert.Equal("1 ready to choose · 1 failed", Assert.Single(rig.Told).Message);
    }

    [Fact]
    public async Task TheCoordinatorsChangesBecomeOneBalloonAndARestartSaysNothing()
    {
        // The rig listens as the Host does, from when its coordinator has started.
        await using var rig = await RemoteRig.CreateAsync(_server);
        MockTorBoxFile[] Two(string folder, int seed) =>
            [MockTorBoxFile.Of(rig.Mock.NextFileId(), $"{folder}/a.bin", Kb, seed), MockTorBoxFile.Of(rig.Mock.NextFileId(), $"{folder}/b.bin", Kb, seed + 1)];
        var first = rig.Mock.AddItem(RemoteItemKind.Torrent, "Invented First", Two("Invented First", 81));
        var second = rig.Mock.AddItem(RemoteItemKind.Torrent, "Invented Second", Two("Invented Second", 83));

        var one = await rig.AddAsync(new RemoteAddRequest { Input = $"https://torbox.app/download?id={first.Id}&type=torrents" });
        var two = await rig.AddAsync(new RemoteAddRequest { Input = $"https://torbox.app/download?id={second.Id}&type=torrents" });
        await rig.RunAsync();
        Assert.Equal(RemoteJobState.AwaitingSelection, rig.View(one.Id).State);
        Assert.Equal(RemoteJobState.AwaitingSelection, rig.View(two.Id).State);
        Assert.Empty(rig.Told);

        await rig.AdvanceAsync(30);
        Assert.Equal(new RemoteNotice("TorBox", "2 ready to choose"), Assert.Single(rig.Told));

        // After a restart, what still waits is where the count starts.
        await rig.RestartAsync();
        await rig.RunAsync();
        await rig.AdvanceAsync(60);
        Assert.Single(rig.Told);
        await rig.AssertNothingLeakedAsync();
    }
}

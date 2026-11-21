using DownloadManager.Contracts;
using DownloadManager.Mobile.TorBox;

namespace DownloadManager.Mobile.Tests;

public sealed class TorBoxTests
{
    private const string Magnet = "magnet:?xt=urn:btih:0123456789abcdef0123456789abcdef01234567&dn=debian";

    private static RemoteFileDto File(string id, string path, long size, bool selectable = true) =>
        new() { FileId = id, RelativePath = path, Size = size, Selectable = selectable };

    private static RemoteJobDto Job(string state, string kind = "torrents", string? name = "x", string? stage = null, double progress = 0,
        string? itemId = "1", int minute = 0) => new()
    {
        Id = Guid.NewGuid(), Provider = "torbox", Kind = kind, State = state, Name = name, Stage = stage, Progress = progress, ItemId = itemId,
        CreatedAt = new DateTimeOffset(2026, 11, 21, 12, minute, 0, TimeSpan.Zero),
    };

    private static FakeHostConnection Host(RemoteClassifyResult classify, RemoteAccountResult? account = null, RemoteAddResultDto? add = null) => new()
    {
        Reply = request => request.Type switch
        {
            MessageTypes.RemoteAccountGet or MessageTypes.RemoteAccountSetKey or MessageTypes.RemoteAccountClearKey => IpcMessage.Create(MessageTypes.RemoteAccountResult,
                account ?? new RemoteAccountResult { Ok = true, Accounts = [new RemoteAccountDto { Provider = "torbox", HasKey = true }] }, request.Id),
            MessageTypes.RemoteClassify => IpcMessage.Create(MessageTypes.RemoteClassifyResult, classify, request.Id),
            MessageTypes.RemoteAdd => IpcMessage.Create(MessageTypes.RemoteAddResult, add ?? new RemoteAddResultDto { Ok = true, Job = Job("waiting") }, request.Id),
            _ => IpcMessage.Create(MessageTypes.RemoteActionResult, new RemoteActionResultDto { Ok = true }, request.Id),
        }
    };

    private static RemoteClassifyResult Cached() => new()
    {
        Ok = true, Kind = "magnet", Cached = true, Name = "debian",
        Files = [File("1", "debian/disc.iso", 3_900_000_000), File("2", "debian/SHA256SUMS", 1000), File("3", "debian/SHA256SUMS.sign", 1000)],
    };

    [Fact]
    public void Keyboard_spaces_in_a_magnet_are_dropped_but_other_text_is_kept()
    {
        Assert.Equal("magnet:?xt=urn:btih:abc&dn=a%20b", TorBoxAddModel.Normalize(" magnet:? xt=urn:btih:abc\n&dn=a%20b "));
        Assert.True(TorBoxAddModel.IsMagnet("magnet:? xt=urn:btih:0123456789abcdef0123456789abcdef01234567"));
        Assert.Equal("https://example.com/a b", TorBoxAddModel.Normalize("https://example.com/a b"));
    }

    [Fact]
    public void OfferedFollowsTheRemoteCapability()
    {
        Assert.True(TorBoxClient.Offered(new HostHelloDto { Capabilities = ["jobs", "remote"] }));
        Assert.False(TorBoxClient.Offered(new HostHelloDto { Capabilities = ["jobs"] }));
        Assert.False(TorBoxClient.Offered(null));
    }

    [Fact]
    public async Task SetKeySendsTheKeyToTorBoxOnlyAndClearAsksForNoKey()
    {
        var host = Host(new RemoteClassifyResult());
        var client = new TorBoxClient(host);
        await client.SetKeyAsync("  test-key  ");
        await client.ClearKeyAsync();
        var set = host.Sent[0].GetPayload<RemoteSetKeyRequest>();
        Assert.Equal(MessageTypes.RemoteAccountSetKey, host.Sent[0].Type);
        Assert.Equal("torbox", set.Provider);
        Assert.Equal("test-key", set.Key);
        Assert.Equal(MessageTypes.RemoteAccountClearKey, host.Sent[1].Type);
        Assert.Equal("[set key]", set.ToString());
    }

    [Fact]
    public async Task ARefusalFromTheHostIsAnError()
    {
        var host = new FakeHostConnection { Reply = r => IpcMessage.Create(MessageTypes.HostActionResult, new HostActionResult { Message = "no" }, r.Id) };
        await Assert.ThrowsAsync<InvalidOperationException>(() => new TorBoxClient(host).ListAsync());
    }

    [Fact]
    public async Task ACachedMagnetShowsItsFilesAndSendsOnlyTheChosenOnesWithoutApprovingAFetch()
    {
        var host = Host(Cached());
        var model = new TorBoxAddModel(new TorBoxClient(host));
        Assert.True(await model.CheckAsync(Magnet));
        Assert.Equal(TorBoxCheck.Hit, model.Check);
        Assert.Equal("Ready on TorBox · 3 files · 3.9 GB", model.ReadyTitle);
        var tree = model.Tree!;
        foreach (var node in tree.Roots[0].Children.Skip(1)) tree.Toggle(node);

        var sent = await model.SendAsync("/dl");
        Assert.True(sent.Done);
        var request = host.Sent.Last().GetPayload<RemoteAddRequestDto>();
        Assert.False(request.ApproveUncached);
        Assert.Equal("chosen", request.Selection);
        Assert.Equal(["1"], request.FileIds);
        Assert.Equal("startNow", request.StartMode);
        Assert.Equal("/dl", request.Destination);
        Assert.Equal(Magnet, request.Input);
    }

    [Fact]
    public async Task AMissIsApprovedOnlyBySending()
    {
        var host = Host(new RemoteClassifyResult { Ok = true, Kind = "magnet", Cached = false });
        var model = new TorBoxAddModel(new TorBoxClient(host));
        await model.CheckAsync(Magnet);
        Assert.Equal(TorBoxCheck.Miss, model.Check);
        Assert.True(model.CanSend);
        Assert.DoesNotContain(host.Sent, m => m.Type == MessageTypes.RemoteAdd);

        await model.SendAsync(null);
        var request = host.Sent.Last().GetPayload<RemoteAddRequestDto>();
        Assert.True(request.ApproveUncached);
        Assert.Equal("all", request.Selection);
        Assert.Null(request.Destination);
    }

    [Fact]
    public async Task NothingIsSentWithoutAFileChosenOrAMagnet()
    {
        var host = Host(Cached());
        var model = new TorBoxAddModel(new TorBoxClient(host));
        await model.CheckAsync("https://example.test/file.zip");
        Assert.Equal(TorBoxCheck.NotMagnet, model.Check);
        Assert.False(model.CanSend);
        Assert.Empty(host.Sent);

        await model.CheckAsync(Magnet);
        model.Tree!.Toggle(model.Tree.Roots[0]);
        Assert.False(model.CanSend);
        Assert.False((await model.SendAsync(null)).Done);
        Assert.DoesNotContain(host.Sent, m => m.Type == MessageTypes.RemoteAdd);
    }

    [Fact]
    public async Task WithoutAKeyTheMagnetIsNotClassified()
    {
        var host = Host(Cached(), new RemoteAccountResult { Ok = true, Accounts = [new RemoteAccountDto { Provider = "torbox" }] });
        var model = new TorBoxAddModel(new TorBoxClient(host));
        await model.CheckAsync(Magnet);
        Assert.Equal(TorBoxCheck.NoKey, model.Check);
        Assert.DoesNotContain(host.Sent, m => m.Type == MessageTypes.RemoteClassify);
    }

    [Fact]
    public async Task AnAnswerToAnOlderTextIsDropped()
    {
        var gate = new TaskCompletionSource();
        var host = new GatedHost(gate.Task);
        var model = new TorBoxAddModel(new TorBoxClient(host));
        var first = model.CheckAsync(Magnet);
        Assert.True(await model.CheckAsync(""));
        gate.SetResult();
        Assert.False(await first);
        Assert.Equal(TorBoxCheck.None, model.Check);
    }

    [Fact]
    public async Task ResetForgetsTheOldAnswerAndDropsAWaitingCheck()
    {
        var host = Host(Cached());
        var model = new TorBoxAddModel(new TorBoxClient(host));
        await model.CheckAsync(Magnet);
        Assert.True(model.CanSend);
        model.Reset();
        Assert.Equal(TorBoxCheck.None, model.Check);
        Assert.Null(model.Tree);
        Assert.False(model.CanSend);
    }

    private sealed class GatedHost(Task gate) : IHostConnection
    {
        public Task OpenAsync(Action<IpcMessage> onFrame, CancellationToken cancellationToken) => Task.CompletedTask;

        public async Task<IpcMessage> RequestAsync(IpcMessage request, CancellationToken cancellationToken)
        {
            await gate;
            return IpcMessage.Create(MessageTypes.RemoteAccountResult,
                new RemoteAccountResult { Ok = true, Accounts = [new RemoteAccountDto { Provider = "torbox", HasKey = true }] }, request.Id);
        }

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }

    [Fact]
    public async Task AFailedOrWarningAddIsNotDone()
    {
        var failed = Host(new RemoteClassifyResult { Ok = true, Kind = "magnet", Cached = false },
            add: new RemoteAddResultDto { Ok = true, Job = Job("failed") });
        var model = new TorBoxAddModel(new TorBoxClient(failed));
        await model.CheckAsync(Magnet);
        Assert.False((await model.SendAsync(null)).Done);

        var noted = Host(new RemoteClassifyResult { Ok = true, Kind = "magnet", Cached = false },
            add: new RemoteAddResultDto { Ok = true, Existing = true, Note = "Already on the list.", Job = Job("waiting") });
        model = new TorBoxAddModel(new TorBoxClient(noted));
        await model.CheckAsync(Magnet);
        var result = await model.SendAsync(null);
        Assert.False(result.Done);
        Assert.Equal("Already on the list.", result.Note);
    }

    [Fact]
    public void RowsHaveTheWordsGroupsAndActionsOfTheMockup()
    {
        var list = new TorBoxList();
        var fetching = Job("waiting", stage: "processing", progress: 0.37, name: "Blender open movies", itemId: "10", minute: 5);
        var onPhone = Job("localStarted", name: "debian", itemId: "11", minute: 4);
        var get = Job("awaitingSelection", kind: "webdl", name: "talk.mp4", itemId: "12", minute: 3);
        var failed = Job("failed", name: "old", itemId: "13", minute: 2);
        failed.FailureMessage = "no peers";
        list.Load([fetching, onPhone, get, failed, new RemoteJobDto { Id = Guid.NewGuid(), Provider = "other", State = "waiting" }]);
        list.SetItems([new RemoteItemDto { Kind = "torrents", ItemId = "10", Size = 41_700_000_000, FileCount = 30 }]);

        var rows = list.Rows("on this phone");
        Assert.Equal(4, rows.Count);
        Assert.Equal("Fetching · 37% · 30 files · 41.7 GB", rows[0].Line);
        Assert.Equal(0.37, rows[0].Progress);
        Assert.Equal(TorBoxRowAction.OnPhone, rows.Single(r => r.Name == "debian").Action);
        Assert.Equal("Ready · on this phone", rows.Single(r => r.Name == "debian").Line);
        Assert.Equal("Ready · web download", rows.Single(r => r.Name == "talk.mp4").Line);
        Assert.Equal(TorBoxRowAction.Get, rows.Single(r => r.Name == "talk.mp4").Action);
        Assert.Equal("Failed on TorBox · no peers", rows.Single(r => r.Name == "old").Line);
        Assert.Equal(TorBoxRowAction.Delete, rows[^1].Action);
        Assert.Equal(["torrents"], list.KindsToDescribe().Where(k => k == "torrents"));
    }

    [Fact]
    public void ChipsCountAndFilterByGroupAndSearch()
    {
        var list = new TorBoxList();
        list.Load([Job("waiting", name: "a"), Job("awaitingSelection", name: "b"), Job("awaitingSelection", name: "Bc"), Job("failed", name: "d")]);
        var rows = list.Rows("on this phone");
        Assert.Equal("All 4", TorBoxRows.ChipLabel(rows, TorBoxChip.All));
        Assert.Equal("Ready 2", TorBoxRows.ChipLabel(rows, TorBoxChip.Ready));
        Assert.Equal("On TorBox 1", TorBoxRows.ChipLabel(rows, TorBoxChip.OnTorBox));
        Assert.Equal("Failed 1", TorBoxRows.ChipLabel(rows, TorBoxChip.Failed));
        Assert.Equal(["b", "Bc"], TorBoxRows.Filter(rows, TorBoxChip.Ready, "B").Select(r => r.Name).Order());
        Assert.Single(TorBoxRows.Filter(rows, TorBoxChip.All, "bc"));
    }

    [Fact]
    public void PushesUpdateAndRemoveJobs()
    {
        var list = new TorBoxList();
        var job = Job("waiting");
        Assert.True(list.Apply(new RemoteChangedDto { Change = "job", Job = job }));
        job = Job("awaitingSelection");
        job.Id = list.Jobs.Single().Id;
        Assert.True(list.Apply(new RemoteChangedDto { Change = "job", Job = job }));
        Assert.Equal("awaitingSelection", list.Jobs.Single().State);
        Assert.False(list.Apply(new RemoteChangedDto { Change = "account" }));
        Assert.False(list.Apply(new RemoteChangedDto { Change = "job", Job = new RemoteJobDto { Provider = "other", Id = Guid.NewGuid() } }));
        Assert.True(list.Apply(new RemoteChangedDto { Change = "removed", JobId = job.Id }));
        Assert.Empty(list.Jobs);
    }

    [Fact]
    public async Task TheModelPassesRemoteChangedOn()
    {
        var host = new FakeHostConnection();
        using var model = new DownloadsModel(host, a => a(), TimeSpan.Zero);
        RemoteChangedDto? seen = null;
        model.RemoteChanged += c => seen = c;
        await model.StartAsync(CancellationToken.None);
        host.Push(IpcMessage.Create(MessageTypes.RemoteChanged, new RemoteChangedDto { Change = "removed", JobId = Guid.NewGuid() }));
        Assert.Equal("removed", seen?.Change);
    }

    [Fact]
    public void FileTreeTotalsFollowTheChecks()
    {
        var tree = new TorBoxFileTree([
            File("1", "Blender/Sprite/a.mkv", 6_800_000_000), File("2", "Blender/Charge/b.mkv", 5_100_000_000), File("3", "Blender/Spring/c.mkv", 8_900_000_000),
            File("4", "readme.txt", 1000, selectable: false)]);
        Assert.Equal(4, tree.TotalCount);
        Assert.Equal(3, tree.ChoosableCount);
        Assert.Equal(3, tree.SelectedCount);
        var blender = tree.Roots[0];
        Assert.Equal(TorBoxCheckState.Checked, tree.StateOf(blender));
        tree.Toggle(blender.Children[0]);
        Assert.Equal(TorBoxCheckState.Partial, tree.StateOf(blender));
        Assert.Equal(2, tree.SelectedCount);
        Assert.Equal(14_000_000_000, tree.SelectedBytes);
        Assert.Equal("Files · 2 of 3 · 14.0 GB", tree.Summary);
        tree.Toggle(blender);
        Assert.Equal(TorBoxCheckState.Checked, tree.StateOf(blender));
        tree.Toggle(blender);
        Assert.Equal(TorBoxCheckState.Unchecked, tree.StateOf(blender));
        tree.Toggle(tree.Roots[1]);
        Assert.Equal(0, tree.SelectedCount);
    }

    [Fact]
    public void ALongTreeStartsWithItsSubfoldersClosed()
    {
        var files = Enumerable.Range(0, 60).Select(i => File(i.ToString(), $"top/sub{i % 3}/f{i}.bin", 10)).ToList();
        var tree = new TorBoxFileTree(files);
        Assert.Equal(4, tree.Visible().Count);
        tree.Roots[0].Children[0].Expanded = true;
        Assert.Equal(4 + 20, tree.Visible().Count);
    }

    [Fact]
    public void TheChoiceOpensWithTorBoxWhenOfferedAndPhoneTorrentsAreNot()
    {
        var store = new Dictionary<string, string>();
        var choice = new TorBoxChoice(new MapStore(store));
        Assert.False(choice.SendFirst);
        Assert.True(choice.OpensWithTorBox(true, false));
        Assert.False(choice.OpensWithTorBox(false, false));
        Assert.False(choice.OpensWithTorBox(true, true));
        choice.SendFirst = true;
        Assert.True(choice.OpensWithTorBox(true, true));
        Assert.True(new TorBoxChoice(new MapStore(store)).SendFirst);
    }

    private sealed class MapStore(Dictionary<string, string> map) : IKeyValueStore
    {
        public string? GetString(string key) => map.GetValueOrDefault(key);
        public void SetString(string key, string value) => map[key] = value;
    }
}

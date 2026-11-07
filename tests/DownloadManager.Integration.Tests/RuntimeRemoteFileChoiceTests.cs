using System.Security.Cryptography;
using System.Text;
using DownloadManager.Contracts;
using DownloadManager.Core.Providers;
using DownloadManager.Runtime.Remote;
using TestServer;

namespace DownloadManager.Integration.Tests;

/// <summary>
/// Which files a magnet or a .torrent file takes when nobody said, and a choice made before the provider has the item:
/// from a cached magnet's cache check, or from a .torrent file's own list, kept on disk and found again in the
/// provider's list by path and size. All names and values are invented.
/// </summary>
public sealed partial class RuntimeServiceTests
{
    /// <summary>A .torrent file of these files inside one folder, as a torrent client writes one.</summary>
    private static (byte[] File, string InfoHash) FolderTorrent(string name, params (string Path, int Length)[] files)
    {
        static string Text(string value) => $"{Encoding.UTF8.GetByteCount(value)}:{value}";
        var list = string.Concat(files.Select(file => $"d6:lengthi{file.Length}e4:pathl{string.Concat(file.Path.Split('/').Select(Text))}ee"));
        var info = $"d5:filesl{list}e4:name{Text(name)}12:piece lengthi16384e6:pieces20:aaaaaaaaaaaaaaaaaaaae";
        return (Encoding.UTF8.GetBytes($"d4:info{info}e"), Convert.ToHexString(SHA1.HashData(Encoding.UTF8.GetBytes(info))).ToLowerInvariant());
    }

    private static async Task UntilSettledAsync(RemoteRig rig, Guid id)
    {
        for (var i = 0; i < 20 && rig.View(id).State is RemoteJobState.Waiting or RemoteJobState.Submitting or RemoteJobState.Intended; i++)
        {
            await rig.AdvanceAsync(60);
        }
    }

    // ---- When nobody said -------------------------------------------------------------------------

    [Theory]
    [InlineData(false, false)]
    [InlineData(false, true)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    public async Task AMagnetAsksOnlyWhenItWasCachedOrItWasToldTo(bool cached, bool ask)
    {
        await using var rig = await RemoteRig.CreateAsync(_server);
        var hash = Convert.ToHexString(SHA1.HashData(Encoding.UTF8.GetBytes($"box-{cached}-{ask}"))).ToLowerInvariant();
        MockTorBoxFile[] files = [MockTorBoxFile.Of(rig.Mock.NextFileId(), "Box/a.bin", 8 * Kb, 61), MockTorBoxFile.Of(rig.Mock.NextFileId(), "Box/b.bin", 8 * Kb, 62)];
        if (cached)
        {
            rig.Mock.AddCached(hash, "Box", 16 * Kb);
        }

        rig.Mock.ScriptSubmission(hash, files, [cached ? MockTorBoxState.CachedReady : MockTorBoxState.Ready]);

        // "ask" is what the add box sends with "Let me choose files when TorBox is done" ticked.
        var job = await rig.AddAsync(new RemoteAddRequest
        {
            Input = "magnet:?xt=urn:btih:" + hash,
            ApprovedUncached = true,
            Selection = ask ? RemoteSelectionMode.AskWhenReady : null,
        });
        Assert.Equal(ask ? RemoteSelectionMode.AskWhenReady : RemoteSelectionMode.AskIfCached, job.SelectionMode);
        await rig.RunAsync();
        await UntilSettledAsync(rig, job.Id);

        var ready = rig.View(job.Id);
        Assert.Single(rig.Mock.Submissions);
        if (cached || ask)
        {
            Assert.Equal(RemoteSelectionMode.AskWhenReady, ready.SelectionMode);
            Assert.Equal(RemoteJobState.AwaitingSelection, ready.State);
            Assert.Empty(rig.Children());
        }
        else
        {
            Assert.Equal(RemoteSelectionMode.All, ready.SelectionMode);
            Assert.Equal(RemoteJobState.LocalStarted, ready.State);
            Assert.Equal(2, rig.Children().Count);
            await rig.AssertCompletedAsync(files[0], "Box", "a.bin");
            await rig.AssertCompletedAsync(files[1], "Box", "b.bin");
        }
    }

    [Fact]
    public async Task AnUncachedTorrentFileLeftAloneTakesAllAndACachedOneAsks()
    {
        await using var rig = await RemoteRig.CreateAsync(_server);
        var (coldBytes, coldHash) = FolderTorrent("Cold", ("a.bin", 4 * Kb), ("b.bin", 4 * Kb));
        var (warmBytes, warmHash) = FolderTorrent("Warm", ("a.bin", 4 * Kb), ("b.bin", 4 * Kb));
        rig.Mock.ScriptTorrentUpload(coldBytes, coldHash);
        rig.Mock.ScriptTorrentUpload(warmBytes, warmHash);
        rig.Mock.ScriptSubmission(coldHash, [MockTorBoxFile.Of(rig.Mock.NextFileId(), "Cold/a.bin", 4 * Kb, 63), MockTorBoxFile.Of(rig.Mock.NextFileId(), "Cold/b.bin", 4 * Kb, 64)], [MockTorBoxState.Ready]);
        rig.Mock.AddCached(warmHash, "Warm", 8 * Kb);
        rig.Mock.ScriptSubmission(warmHash, [MockTorBoxFile.Of(rig.Mock.NextFileId(), "Warm/a.bin", 4 * Kb, 65), MockTorBoxFile.Of(rig.Mock.NextFileId(), "Warm/b.bin", 4 * Kb, 66)], [MockTorBoxState.CachedReady]);

        var cold = await rig.AddAsync(new RemoteAddRequest { TorrentFile = coldBytes, ApprovedUncached = true });
        var warm = await rig.AddAsync(new RemoteAddRequest { TorrentFile = warmBytes, ApprovedUncached = true });
        Assert.Equal(RemoteSelectionMode.AskIfCached, cold.SelectionMode);
        await rig.RunAsync();
        await UntilSettledAsync(rig, cold.Id);
        await UntilSettledAsync(rig, warm.Id);

        Assert.Equal(RemoteJobState.LocalStarted, rig.View(cold.Id).State);
        Assert.Equal(RemoteSelectionMode.All, rig.View(cold.Id).SelectionMode);
        Assert.Equal(RemoteJobState.AwaitingSelection, rig.View(warm.Id).State);
        Assert.Equal(2, rig.Children().Count);
    }

    [Fact]
    public async Task AFolderLinkLeftAloneStillAsks()
    {
        await using var rig = await RemoteRig.CreateAsync(_server);
        var item = rig.Mock.AddItem(RemoteItemKind.Torrent, "There",
            [MockTorBoxFile.Of(rig.Mock.NextFileId(), "There/a.bin", Kb, 67), MockTorBoxFile.Of(rig.Mock.NextFileId(), "There/b.bin", Kb, 68)]);

        var job = await rig.AddAsync(new RemoteAddRequest { Input = $"https://torbox.app/download?id={item.Id}&type=torrents" });
        Assert.Equal(RemoteSelectionMode.AskWhenReady, job.SelectionMode);
        await rig.RunAsync();
        Assert.Equal(RemoteJobState.AwaitingSelection, rig.View(job.Id).State);
    }

    [Fact]
    public async Task AMagnetAlreadyOnTheAccountAsksIfItIsDoneAndTakesAllIfItIsStillWorking()
    {
        // Done on the account is as good as cached; still working on it is not.
        await using var rig = await RemoteRig.CreateAsync(_server);
        MockTorBoxFile[] Two(string folder, int seed) =>
            [MockTorBoxFile.Of(rig.Mock.NextFileId(), $"{folder}/a.bin", 4 * Kb, seed), MockTorBoxFile.Of(rig.Mock.NextFileId(), $"{folder}/b.bin", 4 * Kb, seed + 1)];
        var done = rig.Mock.AddItem(RemoteItemKind.Torrent, "Done", Two("Done", 69), MockTorBoxState.Ready);
        var busy = rig.Mock.AddItem(RemoteItemKind.Torrent, "Busy", Two("Busy", 71), MockTorBoxState.Downloading(0.5), MockTorBoxState.Ready);

        var asks = await rig.AddAsync(new RemoteAddRequest { Input = "magnet:?xt=urn:btih:" + done.Hash });
        var takes = await rig.AddAsync(new RemoteAddRequest { Input = "magnet:?xt=urn:btih:" + busy.Hash });
        await rig.RunAsync();
        await UntilSettledAsync(rig, asks.Id);
        await UntilSettledAsync(rig, takes.Id);

        Assert.Empty(rig.Mock.Submissions);
        Assert.Equal(RemoteSelectionMode.AskWhenReady, rig.View(asks.Id).SelectionMode);
        Assert.Equal(RemoteJobState.AwaitingSelection, rig.View(asks.Id).State);
        Assert.Equal(RemoteSelectionMode.All, rig.View(takes.Id).SelectionMode);
        Assert.Equal(RemoteJobState.LocalStarted, rig.View(takes.Id).State);
        Assert.Equal(2, rig.Children().Count);
    }

    // ---- A magnet's files before it is added --------------------------------------------------------

    [Fact]
    public async Task ACachedMagnetsFilesAreListedBeforeItIsAddedAndOnlyTheChosenOnesDownload()
    {
        await using var rig = await RemoteRig.CreateAsync(_server);
        var hash = new string('d', 40);
        var magnet = $"magnet:?xt=urn:btih:{hash}&dn=Early";
        var first = MockTorBoxFile.Of(rig.Mock.NextFileId(), "Early/first.bin", 8 * Kb, 73);
        var second = MockTorBoxFile.Of(rig.Mock.NextFileId(), "Early/second.bin", 8 * Kb, 74);
        var third = MockTorBoxFile.Of(rig.Mock.NextFileId(), "Early/third.bin", 8 * Kb, 75);
        rig.Mock.AddCached(hash, "Early", 24 * Kb);
        rig.Mock.ScriptSubmission(hash, [first, second, third], [MockTorBoxState.CachedReady]);

        var classified = await rig.Jobs.ClassifyWithFilesAsync(magnet);
        Assert.True(classified.Cached);
        var files = classified.Files!;
        Assert.Equal([first.Id.ToString(), second.Id.ToString(), third.Id.ToString()], files.Select(file => file.FileId));
        Assert.Equal(Path.Combine("Early", "second.bin"), files[1].RelativePath);
        Assert.All(files, file => Assert.True(file.Selectable));
        Assert.Empty(rig.Mock.Submissions);
        Assert.Empty(rig.Jobs.List());

        var job = await rig.AddAsync(new RemoteAddRequest { Input = magnet, Selection = RemoteSelectionMode.Chosen, FileIds = [first.Id.ToString(), third.Id.ToString()] });
        await rig.RunAsync();

        Assert.Equal(RemoteJobState.LocalStarted, rig.View(job.Id).State);
        Assert.Equal(2, rig.Children().Count);
        await rig.AssertCompletedAsync(first, "Early", "first.bin");
        await rig.AssertCompletedAsync(third, "Early", "third.bin");
        Assert.False(File.Exists(Path.Combine(rig.Destination, "Early", "second.bin")));
    }

    [Fact]
    public async Task AnUncachedMagnetHasNoFilesToShowAndOnlyTheCacheIsAsked()
    {
        await using var rig = await RemoteRig.CreateAsync(_server);
        var magnet = "magnet:?xt=urn:btih:" + new string('e', 40);

        await rig.Jobs.ClassifyAsync(magnet);
        Assert.Equal(0, rig.Mock.Calls("POST torrents/checkcached"));

        var classified = await rig.Jobs.ClassifyWithFilesAsync(magnet);
        Assert.Equal(RemoteInputKind.Magnet, classified.Kind);
        Assert.False(classified.Cached);
        Assert.Null(classified.Files);
        Assert.Equal(1, rig.Mock.Calls("POST torrents/checkcached"));
        Assert.Empty(rig.Mock.Submissions);
        Assert.Empty(rig.Jobs.List());
    }

    [Fact]
    public async Task ACacheCheckThatFailsSaysNothingAboutTheCache()
    {
        await using var rig = await RemoteRig.CreateAsync(_server);
        var magnet = "magnet:?xt=urn:btih:" + new string('f', 40);
        rig.Mock.Fail("POST torrents/checkcached", MockFault.Status5xx());

        var classified = await rig.Jobs.ClassifyWithFilesAsync(magnet);
        Assert.Equal(RemoteInputKind.Magnet, classified.Kind);
        Assert.Null(classified.Cached);
        Assert.Null(classified.Files);
        Assert.Contains(rig.Log, line => line.Contains("A cache check before adding failed", StringComparison.Ordinal));
    }

    [Fact]
    public async Task WithoutAKeyNoCacheIsAsked()
    {
        await using var rig = await RemoteRig.CreateAsync(_server, key: null);
        var classified = await rig.Jobs.ClassifyWithFilesAsync("magnet:?xt=urn:btih:" + new string('e', 40));

        Assert.Null(classified.Cached);
        Assert.Null(classified.Files);
        Assert.Equal(0, rig.Mock.Calls("POST torrents/checkcached"));
    }

    // ---- A .torrent file's own list ----------------------------------------------------------------

    [Fact]
    public async Task ATorrentFilesOwnListIsChosenFromAndFoundAgainInTheProvidersByPathAndSize()
    {
        await using var rig = await RemoteRig.CreateAsync(_server);
        var (bytes, infoHash) = FolderTorrent("Pack", ("sub/a.bin", 4 * Kb), ("b.bin", 8 * Kb), ("c.bin", 12 * Kb));
        var path = Path.Combine(rig.Destination, "pack.torrent");
        await File.WriteAllBytesAsync(path, bytes);

        var classified = await rig.Jobs.ClassifyWithFilesAsync(path);
        Assert.Equal(RemoteInputKind.TorrentFile, classified.Kind);
        Assert.Null(classified.Cached);
        var files = classified.Files!;
        Assert.Equal([Path.Combine("Pack", "sub", "a.bin"), Path.Combine("Pack", "b.bin"), Path.Combine("Pack", "c.bin")], files.Select(file => file.RelativePath));
        Assert.Equal([4 * Kb, 8 * Kb, 12 * Kb], files.Select(file => file.Size));
        Assert.Equal(0, rig.Mock.Calls("POST torrents/checkcached"));
        Assert.Empty(rig.Mock.Submissions);

        // The provider names the folder its own way, and its ids are only its own.
        var a = MockTorBoxFile.Of(rig.Mock.NextFileId(), "Pack.2026/sub/a.bin", 4 * Kb, 76);
        var b = MockTorBoxFile.Of(rig.Mock.NextFileId(), "Pack.2026/b.bin", 8 * Kb, 77);
        var c = MockTorBoxFile.Of(rig.Mock.NextFileId(), "Pack.2026/c.bin", 12 * Kb, 78);
        rig.Mock.ScriptTorrentUpload(bytes, infoHash);
        rig.Mock.ScriptSubmission(infoHash, [a, b, c], [MockTorBoxState.Ready]);

        var job = await rig.AddAsync(new RemoteAddRequest
        {
            Input = path,
            ApprovedUncached = true,
            Selection = RemoteSelectionMode.Chosen,
            FilePaths = [new RemoteChosenPath(files[0].RelativePath!, files[0].Size), new RemoteChosenPath(files[2].RelativePath!, files[2].Size)],
        });
        Assert.Empty(job.SelectedFileIds);
        await rig.RunAsync();

        var started = rig.View(job.Id);
        Assert.Equal(RemoteJobState.LocalStarted, started.State);
        Assert.Equal([a.Id.ToString(), c.Id.ToString()], started.SelectedFileIds);
        Assert.Equal(2, rig.Children().Count);
        await rig.AssertCompletedAsync(a, "Pack.2026", "sub", "a.bin");
        await rig.AssertCompletedAsync(c, "Pack.2026", "c.bin");
        Assert.False(File.Exists(Path.Combine(rig.Destination, "Pack.2026", "b.bin")));
    }

    [Fact]
    public async Task AOneFileTorrentsChoiceIsFoundInTheFolderTheProviderMakesUpForIt()
    {
        await using var rig = await RemoteRig.CreateAsync(_server);
        var (bytes, infoHash) = Torrent("solo.bin");
        var solo = MockTorBoxFile.Of(rig.Mock.NextFileId(), "solo/solo.bin", 1234, 79);
        rig.Mock.ScriptTorrentUpload(bytes, infoHash);
        rig.Mock.ScriptSubmission(infoHash, [solo], [MockTorBoxState.Ready]);

        var job = await rig.AddAsync(new RemoteAddRequest
        {
            TorrentFile = bytes,
            ApprovedUncached = true,
            Selection = RemoteSelectionMode.Chosen,
            FilePaths = [new RemoteChosenPath("solo.bin", 1234)],
        });
        await rig.RunAsync();

        Assert.Equal(RemoteJobState.LocalStarted, rig.View(job.Id).State);
        Assert.Equal([solo.Id.ToString()], rig.View(job.Id).SelectedFileIds);
    }

    [Fact]
    public async Task AChoiceFromATorrentFileIsKeptThroughARestartBeforeTheProviderListsTheFiles()
    {
        await using var rig = await RemoteRig.CreateAsync(_server);
        var (bytes, infoHash) = FolderTorrent("Kept", ("one.bin", 4 * Kb), ("two.bin", 8 * Kb));
        var one = MockTorBoxFile.Of(rig.Mock.NextFileId(), "Kept/one.bin", 4 * Kb, 80);
        var two = MockTorBoxFile.Of(rig.Mock.NextFileId(), "Kept/two.bin", 8 * Kb, 81);
        rig.Mock.ScriptTorrentUpload(bytes, infoHash);
        rig.Mock.ScriptSubmission(infoHash, [one, two], [MockTorBoxState.MetaData, MockTorBoxState.MetaData, MockTorBoxState.Downloading(0.5), MockTorBoxState.Ready]);

        var job = await rig.AddAsync(new RemoteAddRequest
        {
            TorrentFile = bytes,
            ApprovedUncached = true,
            Selection = RemoteSelectionMode.Chosen,
            FilePaths = [new RemoteChosenPath("Kept/two.bin", 8 * Kb)],
        });
        await rig.RunAsync();
        Assert.Equal(RemoteJobState.Waiting, rig.View(job.Id).State);
        Assert.Single(rig.Mock.Submissions);

        await rig.RestartAsync();
        await UntilSettledAsync(rig, job.Id);

        var started = rig.View(job.Id);
        Assert.Equal(RemoteJobState.LocalStarted, started.State);
        Assert.Equal([two.Id.ToString()], started.SelectedFileIds);
        await rig.AssertCompletedAsync(two, "Kept", "two.bin");
        Assert.False(File.Exists(Path.Combine(rig.Destination, "Kept", "one.bin")));
        Assert.Single(rig.Mock.Submissions);
    }

    [Fact]
    public async Task ChosenFilesTheProviderDoesntListAreSaidAndTheListIsOfferedAgain()
    {
        await using var rig = await RemoteRig.CreateAsync(_server);
        var (bytes, infoHash) = FolderTorrent("Odd", ("a.bin", 4 * Kb), ("b.bin", 8 * Kb), ("c.bin", 12 * Kb));
        var a = MockTorBoxFile.Of(rig.Mock.NextFileId(), "Odd/a.bin", 4 * Kb, 82);
        var b = MockTorBoxFile.Of(rig.Mock.NextFileId(), "Odd/b.bin", 8 * Kb, 83);
        // Not the size the .torrent file gave, so not the file that was chosen.
        var c = MockTorBoxFile.Of(rig.Mock.NextFileId(), "Odd/c.bin", 10 * Kb, 84);
        rig.Mock.ScriptTorrentUpload(bytes, infoHash);
        rig.Mock.ScriptSubmission(infoHash, [a, b, c], [MockTorBoxState.Ready]);

        var job = await rig.AddAsync(new RemoteAddRequest
        {
            TorrentFile = bytes,
            ApprovedUncached = true,
            Selection = RemoteSelectionMode.Chosen,
            FilePaths = [new RemoteChosenPath("Odd/a.bin", 4 * Kb), new RemoteChosenPath("Odd/c.bin", 12 * Kb)],
        });
        await rig.RunAsync();

        const string Words = "1 of the chosen files isn't in TorBox's list. Choose again.";
        var asking = rig.View(job.Id);
        Assert.Equal(RemoteJobState.AwaitingSelection, asking.State);
        Assert.Equal(Words, asking.Status);
        // What was found stays chosen, so the list can show it ticked.
        Assert.Equal([a.Id.ToString()], asking.SelectedFileIds);
        Assert.Empty(rig.Children());

        // A second look says the same: the missing file isn't quietly left out.
        Assert.True((await rig.Jobs.CheckNowAsync(job.Id)).Ok);
        await rig.RunAsync();
        Assert.Equal(RemoteJobState.AwaitingSelection, rig.View(job.Id).State);
        Assert.Equal(Words, rig.View(job.Id).Status);
        Assert.Empty(rig.Children());

        // A choice from the provider's own list replaces it.
        Assert.True((await rig.Jobs.ChooseFilesAsync(job.Id, all: false, [a.Id.ToString(), c.Id.ToString()])).Ok);
        Assert.Null(rig.View(job.Id).FailureMessage);
        await rig.RunAsync();
        Assert.Equal(RemoteJobState.LocalStarted, rig.View(job.Id).State);
        Assert.Equal(2, rig.Children().Count);
        await rig.AssertCompletedAsync(c, "Odd", "c.bin");
    }

    [Fact]
    public async Task AChoiceByPathIsCheckedWhenItIsAdded()
    {
        await using var rig = await RemoteRig.CreateAsync(_server);
        var (bytes, _) = FolderTorrent("Checked", ("a.bin", 4 * Kb), ("b.bin", 4 * Kb));
        async Task<string?> Refusal(RemoteAddRequest request)
            => (await rig.Jobs.AddAsync(request with { TorrentFile = bytes, Destination = rig.Destination, Selection = RemoteSelectionMode.Chosen })).Error;

        Assert.Equal("Choose files by id or by path, not both.",
            await Refusal(new RemoteAddRequest { FileIds = ["1"], FilePaths = [new RemoteChosenPath("Checked/a.bin", 4 * Kb)] }));
        Assert.Equal("A chosen file has a path that can't be used.",
            await Refusal(new RemoteAddRequest { FilePaths = [new RemoteChosenPath("Checked/../../a.bin", 4 * Kb)] }));
        Assert.Equal("That is too many files to choose before it is added. Choose them when it is ready.",
            await Refusal(new RemoteAddRequest { FilePaths = Enumerable.Range(0, RemoteJobCoordinator.MostFilesToChooseFrom + 1).Select(i => new RemoteChosenPath($"Checked/{i}.bin", 1)).ToList() }));
        Assert.Empty(rig.Jobs.List());
    }

    // ---- Over the commands, as a client sees it ------------------------------------------------------

    [Fact]
    public async Task TheCommandsListATorrentFilesFilesAndTakeTheChoiceByPath()
    {
        await using var rig = await RemoteRig.CreateAsync(_server);
        using var commands = new RemoteCommands(rig.Box.Registry, rig.Jobs, rig.Time);
        var (bytes, infoHash) = FolderTorrent("Wire", ("a.bin", 4 * Kb), ("b.bin", 4 * Kb));
        var path = Path.Combine(rig.Destination, "wire.torrent");
        await File.WriteAllBytesAsync(path, bytes);
        var b = MockTorBoxFile.Of(rig.Mock.NextFileId(), "Wire/b.bin", 4 * Kb, 85);
        rig.Mock.ScriptTorrentUpload(bytes, infoHash);
        rig.Mock.ScriptSubmission(infoHash, [MockTorBoxFile.Of(rig.Mock.NextFileId(), "Wire/a.bin", 4 * Kb, 86), b], [MockTorBoxState.Ready]);

        var plain = await commands.ClassifyAsync(new RemoteClassifyRequest { Text = path });
        Assert.Null(plain.Files);

        var classified = await commands.ClassifyAsync(new RemoteClassifyRequest { Text = path, WithFiles = true });
        Assert.True(classified.Ok);
        Assert.Equal("torrentFile", classified.Kind);
        Assert.Null(classified.Cached);
        Assert.Equal([Path.Combine("Wire", "a.bin"), Path.Combine("Wire", "b.bin")], classified.Files!.Select(file => file.RelativePath));

        // No selection word: paths alone mean chosen.
        var added = await commands.AddAsync(new RemoteAddRequestDto
        {
            Input = path,
            Destination = rig.Destination,
            ApproveUncached = true,
            FilePaths = [new RemoteFilePathDto { Path = classified.Files![1].RelativePath!, Size = 4 * Kb }],
        });
        Assert.True(added.Ok, added.Error);
        Assert.Equal("chosen", added.Job!.SelectionMode);
        await rig.RunAsync();

        Assert.Equal(RemoteJobState.LocalStarted, rig.View(added.Job.Id).State);
        await rig.AssertCompletedAsync(b, "Wire", "b.bin");

        var broken = await commands.AddAsync(new RemoteAddRequestDto { Input = path, Destination = rig.Destination, FilePaths = [null!] });
        Assert.Equal("A chosen file has a path that can't be used.", broken.Error);
    }

    [Fact]
    public async Task TheCommandsSayWhetherAMagnetIsCachedAndListItsFilesByTheirIds()
    {
        await using var rig = await RemoteRig.CreateAsync(_server);
        using var commands = new RemoteCommands(rig.Box.Registry, rig.Jobs, rig.Time);
        var hash = new string('9', 40);
        var file = MockTorBoxFile.Of(rig.Mock.NextFileId(), "Listed/one.bin", 4 * Kb, 87);
        rig.Mock.AddCached(hash, "Listed", 4 * Kb);
        rig.Mock.ScriptSubmission(hash, [file], [MockTorBoxState.CachedReady]);

        var cached = await commands.ClassifyAsync(new RemoteClassifyRequest { Text = "magnet:?xt=urn:btih:" + hash, WithFiles = true });
        Assert.True(cached.Cached);
        Assert.Equal(file.Id.ToString(), Assert.Single(cached.Files!).FileId);

        var uncached = await commands.ClassifyAsync(new RemoteClassifyRequest { Text = "magnet:?xt=urn:btih:" + new string('8', 40), WithFiles = true });
        Assert.False(uncached.Cached);
        Assert.Null(uncached.Files);
    }
}

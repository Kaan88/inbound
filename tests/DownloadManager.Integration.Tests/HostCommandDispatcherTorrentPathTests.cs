using System.Security.Cryptography;
using DownloadManager.Contracts;
using DownloadManager.Runtime;

namespace DownloadManager.Integration.Tests;

/// <summary>T1 review: what a torrent command refuses before it reads anything, and the file choice's checks.</summary>
public sealed partial class HostCommandDispatcherTests
{
    [Theory]
    [InlineData(@"\\attacker.example\share\x.torrent")]
    [InlineData("//attacker.example/share/x.torrent")]
    [InlineData(@"\\?\UNC\attacker.example\share\x.torrent")]
    [InlineData(@"\\.\pipe\x.torrent")]
    [InlineData(@"\??\UNC\attacker.example\share\x.torrent")]
    public async Task ANetworkOrDeviceTorrentPathIsRefusedBeforeAnythingLooksAtIt(string path)
    {
        var service = await TorrentServiceAsync();
        var dispatcher = Dispatcher(service);
        // The first step that looks at a path; the refusal must come before it.
        var looked = 0;
        service.ResolveTorrentUpload = _ => { Interlocked.Increment(ref looked); return null; };

        var inspect = (await AskAsync(dispatcher, Session(), TorrentMessageTypes.Inspect, new TorrentInspectRequest { Input = path }))
            .GetPayload<TorrentInspectResult>();
        var add = (await AskAsync(dispatcher, Session(), TorrentMessageTypes.Add, new TorrentAddRequest { Input = "  " + path + " " }))
            .GetPayload<TorrentAddResult>();

        Assert.False(inspect.Ok);
        Assert.Equal(DownloadManagerService.TorrentNetworkPathText, inspect.Error);
        Assert.False(add.Ok);
        Assert.Equal(DownloadManagerService.TorrentNetworkPathText, add.Error);
        Assert.Equal(0, looked);
        Assert.Empty(service.GetSnapshot().Jobs);
    }

    [Fact]
    public async Task AFileChoiceWithAnIndexOutsideTheTorrentIsRefusedAndAValidOneIsKept()
    {
        // Several files: a single-file torrent never waits for a choice.
        var service = await TorrentServiceAsync(filesPerTorrent: 2);
        var dispatcher = Dispatcher(service);
        var hash = Convert.ToHexStringLower(RandomNumberGenerator.GetBytes(20));
        var added = (await AskAsync(dispatcher, Session(), TorrentMessageTypes.Add, new TorrentAddRequest
        {
            Input = $"magnet:?xt=urn:btih:{hash}&dn=Album", Destination = Path.Combine(_root, "down"), Files = "askWhenReady",
        })).GetPayload<TorrentAddResult>();
        Assert.True(added.Ok, added.Error);
        var waiting = await UntilAsync(() => service.GetSnapshot().Jobs.FirstOrDefault(j => j.Torrent?.AwaitingFileChoice == true && j.State == "paused"));
        var files = (await AskAsync(dispatcher, Session(), TorrentMessageTypes.Files, new TorrentFilesRequest { JobId = waiting.Id }))
            .GetPayload<TorrentFilesResult>().Files;
        Assert.NotEmpty(files);
        var outside = files.Max(file => file.Index) + 1;

        var refused = (await AskAsync(dispatcher, Session(), TorrentMessageTypes.SetFiles,
            new TorrentSetFilesRequest { JobId = waiting.Id, FileIndexes = [files[0].Index, outside] })).GetPayload<TorrentActionResult>();
        Assert.False(refused.Ok);
        Assert.Equal("A chosen file is not in this torrent.", refused.Error);

        var all = (await AskAsync(dispatcher, Session(), TorrentMessageTypes.SetFiles,
            new TorrentSetFilesRequest { JobId = waiting.Id, FileIndexes = files.Select(file => file.Index).ToList() })).GetPayload<TorrentActionResult>();
        Assert.True(all.Ok, all.Error);
        var after = (await AskAsync(dispatcher, Session(), TorrentMessageTypes.Files, new TorrentFilesRequest { JobId = waiting.Id }))
            .GetPayload<TorrentFilesResult>().Files;
        Assert.All(after, file => Assert.True(file.Wanted));
    }
}

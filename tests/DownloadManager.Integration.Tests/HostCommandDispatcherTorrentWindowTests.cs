using System.Collections.Concurrent;
using DownloadManager.Contracts;
using DownloadManager.Runtime;

namespace DownloadManager.Integration.Tests;

/// <summary>
/// T9 W4: the Host opens no Progress Details window when a torrent starts (the torrent dialog already asked where and
/// which files); its completion window follows the usual rule. The desktop window's TorrentRules.ProgressWindowComing
/// follows this (TorrentPromptRulesTests).
/// </summary>
public sealed partial class HostCommandDispatcherTests
{
    [Fact]
    public void ATorrentOpensNoWindowAsItStartsAndOtherDownloadsStillDo()
    {
        Assert.False(DownloadWindows.OpensStartWindow(new JobDto { Source = "ui", Torrent = new TorrentDto { InfoHash = new string('a', 40) } }));
        Assert.True(DownloadWindows.OpensStartWindow(new JobDto { Source = "ui" }));
        Assert.False(DownloadWindows.OpensStartWindow(new JobDto { Source = "cli" }));
        // Its completion window is still the usual one.
        Assert.True(DownloadWindows.OpensWindows(new JobDto { Source = "ui", Torrent = new TorrentDto() }));
    }

    [Fact]
    public async Task AStartedTorrentGetsOnlyItsCompletionWindow()
    {
        var service = await TorrentServiceAsync();
        var windows = new ConcurrentQueue<DownloadWindowRequest>();
        using var decisions = new DownloadWindows(service, () => true, windows.Enqueue);
        var file = Path.Combine(_root, "windows.torrent");
        var (bytes, _) = SingleFileTorrent("windows.mkv", 50_000);
        await File.WriteAllBytesAsync(file, bytes);

        var added = (await AskAsync(Dispatcher(service), Session(), TorrentMessageTypes.Add, new TorrentAddRequest
        {
            Input = file, Destination = Path.Combine(_root, "down-windows"), StartMode = "startNow",
        })).GetPayload<TorrentAddResult>();
        Assert.True(added.Ok, added.Error);
        var id = added.Job!.Id;
        Assert.True(DownloadWindows.OpensWindows(added.Job));

        var worker = await UntilAsync(() => _torrentWorkers.FirstOrDefault(w => w.Torrents.Count == 1));
        await UntilAsync(() => service.GetSnapshot().Jobs.FirstOrDefault(j => j.Id == id && j.State is "connecting" or "downloading"));
        Assert.Empty(windows);

        for (var i = 0; i < 4; i++) worker.Advance();
        await UntilAsync(() => service.GetSnapshot().Jobs.FirstOrDefault(j => j.Id == id && j.State == "completed"));
        await UntilAsync(() => windows.IsEmpty ? null : windows);
        await Task.Delay(100);
        var only = Assert.Single(windows);
        Assert.Equal(new DownloadWindowRequest(id, IsPackage: false), only);
    }
}

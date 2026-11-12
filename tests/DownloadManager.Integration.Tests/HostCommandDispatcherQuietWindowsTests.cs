using System.Collections.Concurrent;
using DownloadManager.Contracts;
using DownloadManager.Runtime;
using DownloadManager.Runtime.Ipc;

namespace DownloadManager.Integration.Tests;

/// <summary>
/// EXT.R12: a download the browser extension sends with QuietWindows opens no app window (the browser keeps the focus
/// and shows the progress on its icon), and "Show in app" opens the main window at one download.
/// </summary>
public sealed partial class HostCommandDispatcherTests
{
    private static HandoffRequest BrowserHandoff(string folder, bool? quiet) => new()
    {
        RequestId = Guid.NewGuid().ToString("N"),
        ExtensionId = BrowserBridge.StoreExtensionId,
        Url = "http://127.0.0.1:9/files/" + Guid.NewGuid().ToString("N") + ".zip",
        DestinationDirectory = folder,
        StartMode = "startNow",
        IsAutoIntercept = true,
        QuietWindows = quiet,
    };

    [Fact]
    public void AQuietDownloadIsKeptQuietAndOthersAreNot()
    {
        Assert.True(DownloadWindows.KeptQuiet(new JobDto { Source = "chromeAuto", QuietWindows = true }));
        Assert.False(DownloadWindows.KeptQuiet(new JobDto { Source = "chromeAuto" }));
        // The other rules are as they were.
        Assert.True(DownloadWindows.OpensWindows(new JobDto { Source = "chromeAuto" }));
    }

    [Fact]
    public async Task AQuietBrowserHandoffOpensNoWindowAndAnOrdinaryOneStillDoes()
    {
        var service = await ServiceAsync();
        var dispatcher = Dispatcher(service);
        var windows = new ConcurrentQueue<DownloadWindowRequest>();
        using var decisions = new DownloadWindows(service, () => true, windows.Enqueue);
        var folder = Path.Combine(_root, "quiet");

        var quiet = (await AskAsync(dispatcher, Session(), MessageTypes.HandoffRequest, BrowserHandoff(folder, quiet: true))).GetPayload<HandoffAck>();
        Assert.True(quiet.Accepted, quiet.Error);
        Assert.True(quiet.Job!.QuietWindows);
        // It runs and fails (nothing answers on port 9): no window as it starts. A quiet download that finishes is in
        // QuietBrowserDownloadTests.
        await UntilAsync(() => service.GetSnapshot().Jobs.FirstOrDefault(j => j.Id == quiet.JobId && j.State is "failed" or "completed"));
        await Task.Delay(100);
        Assert.Empty(windows);

        var loud = (await AskAsync(dispatcher, Session(), MessageTypes.HandoffRequest, BrowserHandoff(folder, quiet: null))).GetPayload<HandoffAck>();
        Assert.True(loud.Accepted, loud.Error);
        Assert.False(loud.Job!.QuietWindows);
        await UntilAsync(() => windows.IsEmpty ? null : windows);
        Assert.Equal(new DownloadWindowRequest(loud.JobId, IsPackage: false), Assert.Single(windows));
    }

    [Fact]
    public async Task AQuietDownloadStaysQuietAfterTheHostStartsAgain()
    {
        var service = await ServiceAsync();
        var ack = (await AskAsync(Dispatcher(service), Session(), MessageTypes.HandoffRequest,
            BrowserHandoff(Path.Combine(_root, "kept"), quiet: true))).GetPayload<HandoffAck>();
        Assert.True(ack.Accepted, ack.Error);
        await UntilAsync(() => service.GetSnapshot().Jobs.FirstOrDefault(j => j.Id == ack.JobId && j.State is "failed" or "completed"));
        await service.ShutdownAsync();
        _engine?.Dispose();
        _service = null;

        var again = await ServiceAsync();
        Assert.True(again.GetSnapshot().Jobs.Single(j => j.Id == ack.JobId).QuietWindows);
    }

    [Fact]
    public async Task ShowInAppOpensTheMainWindowAtADownloadOnTheList()
    {
        var service = await ServiceAsync();
        var platform = new FakePlatform(PlatformCapabilities.Desktop, _timeline);
        var dispatcher = Dispatcher(service, platform);
        var ack = (await AskAsync(dispatcher, Session(), MessageTypes.HandoffRequest, BrowserHandoff(Path.Combine(_root, "show"), quiet: true)))
            .GetPayload<HandoffAck>();
        Assert.True(ack.Accepted, ack.Error);

        var shown = (await AskAsync(dispatcher, Session(), MessageTypes.HostOpenUi, new HostActionRequest { JobId = ack.JobId }))
            .GetPayload<HostActionResult>();
        Assert.True(shown.Ok);
        Assert.Equal([ack.JobId], platform.Shown);
        Assert.Empty(platform.Opened);

        // A download not on the list: nothing opens, and the extension is told why.
        var gone = (await AskAsync(dispatcher, Session(), MessageTypes.HostOpenUi, new HostActionRequest { JobId = Guid.NewGuid() }))
            .GetPayload<HostActionResult>();
        Assert.False(gone.Ok);
        Assert.Contains("no longer", gone.Message);
        Assert.Single(platform.Shown);

        // Without a download (an older extension): the main window, as before.
        Assert.True((await AskAsync(dispatcher, Session(), MessageTypes.HostOpenUi, new HostActionRequest())).GetPayload<HostActionResult>().Ok);
        Assert.Equal([null], platform.Opened);
    }

    [Fact]
    public async Task ShowInAppIsUnavailableWhereNoWindowCanOpen()
    {
        var service = await ServiceAsync();
        var platform = new FakePlatform(new PlatformCapabilities(), _timeline);
        var dispatcher = Dispatcher(service, platform);
        var result = (await AskAsync(dispatcher, Session(), MessageTypes.HostOpenUi, new HostActionRequest { JobId = Guid.NewGuid() }))
            .GetPayload<HostActionResult>();
        Assert.False(result.Ok);
        Assert.Empty(platform.Shown);
    }

    [Fact]
    public void TheExtensionMayAskForShowInAppThroughTheNativeHost()
    {
        var message = IpcMessage.Create(MessageTypes.HostOpenUi, new HostActionRequest { JobId = Guid.NewGuid() });
        Assert.True(NativeBridgePolicy.AllowFromExtension(message));
    }

    [Fact]
    public void AnOlderClientsOpenUiStillReadsAndANewOneSendsTheJob()
    {
        var old = IpcMessage.Create(MessageTypes.HostOpenUi, new { action = "settings" }).GetPayload<HostActionRequest>();
        Assert.Equal("settings", old.Action);
        Assert.Null(old.JobId);
        var id = Guid.NewGuid();
        Assert.Equal(id, IpcMessage.Create(MessageTypes.HostOpenUi, new { action = "", jobId = id }).GetPayload<HostActionRequest>().JobId);
        // A handoff without the flag (an older extension) is not quiet.
        Assert.Null(IpcMessage.Create(MessageTypes.HandoffRequest, new { requestId = "r", url = "http://x.test/a" }).GetPayload<HandoffRequest>().QuietWindows);
    }
}

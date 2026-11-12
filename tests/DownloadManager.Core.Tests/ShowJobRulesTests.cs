using DownloadManager.Contracts;
using DownloadManager.Desktop.WinUI;

namespace DownloadManager.Core.Tests;

/// <summary>
/// EXT.R12: the window's <c>--show-job</c> argument and its hand-over to a running window, and the desktop window's
/// rules for a download the browser asked to keep quiet.
/// </summary>
public sealed class ShowJobRulesTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "inbound-show-" + Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { /* best effort */ }
    }

    [Fact]
    public void TheArgumentNamesOneDownload()
    {
        var id = Guid.NewGuid();
        Assert.Equal(id, UiArguments.ShowJobId(["inbound.exe", "--show-job", id.ToString("D")]));
        Assert.Equal(id, UiArguments.ShowJobId(["--SHOW-JOB", id.ToString("N")]));
        Assert.Null(UiArguments.ShowJobId(["--show-job"]));
        Assert.Null(UiArguments.ShowJobId(["--show-job", "not-a-guid"]));
        Assert.Null(UiArguments.ShowJobId(["--show-job", Guid.Empty.ToString()]));
        Assert.Null(UiArguments.ShowJobId(["--job", id.ToString()]));
    }

    [Fact]
    public void ARunningWindowTakesTheNewestRequestOnce()
    {
        var now = DateTimeOffset.UtcNow;
        Assert.Null(ShowJobRequests.Take(_dir, now));
        var first = Guid.NewGuid();
        var second = Guid.NewGuid();
        ShowJobRequests.Write(_dir, first, now);
        ShowJobRequests.Write(_dir, second, now);
        Assert.Equal(second, ShowJobRequests.Take(_dir, now));
        Assert.Null(ShowJobRequests.Take(_dir, now));
        Assert.Empty(Directory.GetFiles(_dir));
    }

    [Fact]
    public void AStaleOrDamagedRequestIsDropped()
    {
        var now = DateTimeOffset.UtcNow;
        ShowJobRequests.Write(_dir, Guid.NewGuid(), now - ShowJobRequests.MaxAge - TimeSpan.FromSeconds(1));
        Assert.Null(ShowJobRequests.Take(_dir, now));
        Directory.CreateDirectory(_dir);
        File.WriteAllText(Path.Combine(_dir, "show-job.request"), "garbage");
        Assert.Null(ShowJobRequests.Take(_dir, now));
        Assert.Empty(Directory.GetFiles(_dir));
    }

    [Fact]
    public void AWindowShowingAnotherServerSwitchesToThisPcBeforeShowingTheDownload()
    {
        Assert.Equal(ShowJobRequests.When.Now, ShowJobRequests.ShowWhen(showingAnotherServer: false, listIn: true));
        Assert.Equal(ShowJobRequests.When.AfterList, ShowJobRequests.ShowWhen(showingAnotherServer: false, listIn: false));
        Assert.Equal(ShowJobRequests.When.AfterSwitch, ShowJobRequests.ShowWhen(showingAnotherServer: true, listIn: true));
        Assert.Equal(ShowJobRequests.When.AfterSwitch, ShowJobRequests.ShowWhen(showingAnotherServer: true, listIn: false));
        // The other server's list, arriving before the switch is done, does not take the waiting download.
        Assert.False(ShowJobRequests.ListBringsIt(showingAnotherServer: true));
        Assert.True(ShowJobRequests.ListBringsIt(showingAnotherServer: false));
    }

    [Fact]
    public void AQuietDownloadOpensNoCompletionWindowFromTheMainWindow()
    {
        Assert.True(RemoteDataRules.WindowOpensCompletion(remote: false, "cli", null, settingShowsDialog: true));
        Assert.False(RemoteDataRules.WindowOpensCompletion(remote: false, "cli", null, settingShowsDialog: true, quietWindows: true));
        Assert.True(RemoteDataRules.WindowOpensCompletion(remote: true, "chromeAuto", null, settingShowsDialog: true));
        Assert.False(RemoteDataRules.WindowOpensCompletion(remote: true, "chromeAuto", null, settingShowsDialog: true, quietWindows: true));
    }

    [Fact]
    public void AQuietDownloadsFileChoiceWaitsForNoProgressWindow()
    {
        var now = DateTimeOffset.UtcNow;
        JobDto Added(bool quiet) => new() { Source = "chromeAuto", StartMode = "startNow", CreatedAt = now, QuietWindows = quiet };
        Assert.True(TorrentRules.ProgressWindowComing(Added(quiet: false), localHost: true, windowSeen: false, now));
        Assert.False(TorrentRules.ProgressWindowComing(Added(quiet: true), localHost: true, windowSeen: false, now));
    }
}

using DownloadManager.Contracts;
using DownloadManager.Desktop.WinUI;

namespace DownloadManager.Core.Tests;

/// <summary>
/// T11: Progress Details and package windows opened from the main window are built in its process, one per download or
/// package and server, and the process ends with the last of them when the main window was closed with "Closing the
/// window ends it" (PERF.5) on.
/// </summary>
public sealed class ProgressWindowSetTests
{
    private const string ServerId = "0123456789abcdef0123456789abcdef";
    private static readonly ServerTarget Server = ServerTarget.Remote(ServerId, "minipc", new Uri("http://minipc:8080/"), "token", false);

    private sealed class FakeWindow;

    [Fact]
    public void AnOpenWindowIsFoundAgainForTheSameDownloadAndServer()
    {
        var set = new ProgressWindowSet<FakeWindow>();
        var job = Guid.NewGuid();
        var window = new FakeWindow();
        set.Add(ProgressWindowKey.For(ProgressWindowKind.Download, job, ServerTarget.Local), window);

        Assert.Same(window, set.Find(ProgressWindowKey.For(ProgressWindowKind.Download, job, ServerTarget.Local)));
        // The same id as a package, another download, or the same download on a server: other windows.
        Assert.Null(set.Find(ProgressWindowKey.For(ProgressWindowKind.Package, job, ServerTarget.Local)));
        Assert.Null(set.Find(ProgressWindowKey.For(ProgressWindowKind.Download, Guid.NewGuid(), ServerTarget.Local)));
        Assert.Null(set.Find(ProgressWindowKey.For(ProgressWindowKind.Download, job, Server)));
        Assert.Equal(1, set.Count);
    }

    [Fact]
    public void AServersIdIsTheSameWindowWhateverItsCase()
    {
        var upper = ServerTarget.Remote(ServerId.ToUpperInvariant(), "minipc", new Uri("http://minipc:8080/"), "token", false);
        var job = Guid.NewGuid();
        Assert.Equal(ProgressWindowKey.For(ProgressWindowKind.Download, job, Server), ProgressWindowKey.For(ProgressWindowKind.Download, job, upper));
        Assert.Null(ProgressWindowKey.For(ProgressWindowKind.Download, job, ServerTarget.Local).ServerId);
    }

    [Fact]
    public void AClosedWindowIsForgottenSoTheNextOpenBuildsANewOne()
    {
        var set = new ProgressWindowSet<FakeWindow>();
        var key = ProgressWindowKey.For(ProgressWindowKind.Download, Guid.NewGuid(), ServerTarget.Local);
        var window = new FakeWindow();
        set.Add(key, window);

        Assert.False(set.Closed(key, window));
        Assert.Null(set.Find(key));
        Assert.Equal(0, set.Count);
    }

    [Fact]
    public void AWindowThatIsNoLongerTheOneKeptChangesNothingWhenItCloses()
    {
        var set = new ProgressWindowSet<FakeWindow>();
        var key = ProgressWindowKey.For(ProgressWindowKind.Package, Guid.NewGuid(), Server);
        var old = new FakeWindow();
        var kept = new FakeWindow();
        set.Add(key, old);
        set.Add(key, kept);

        Assert.False(set.Closed(key, old));
        Assert.Same(kept, set.Find(key));
    }

    [Fact]
    public void ClosingTheMainWindowWithNoProgressWindowOpenEndsTheProcessAtOnce()
    {
        var set = new ProgressWindowSet<FakeWindow>();
        Assert.True(set.MainWindowClosed(endsProcess: true));
    }

    [Fact]
    public void ClosingTheMainWindowKeepsOpenProgressWindowsAndTheLastOneToCloseEndsTheProcess()
    {
        var set = new ProgressWindowSet<FakeWindow>();
        var first = ProgressWindowKey.For(ProgressWindowKind.Download, Guid.NewGuid(), ServerTarget.Local);
        var second = ProgressWindowKey.For(ProgressWindowKind.Package, Guid.NewGuid(), Server);
        var a = new FakeWindow();
        var b = new FakeWindow();
        set.Add(first, a);
        set.Add(second, b);

        Assert.False(set.MainWindowClosed(endsProcess: true));
        Assert.True(set.EndsWithLastWindow);
        Assert.Equal(2, set.Count);

        Assert.False(set.Closed(first, a));
        Assert.True(set.Closed(second, b));
    }

    [Fact]
    public void ShowingTheMainWindowAgainKeepsTheProcessAfterTheLastProgressWindow()
    {
        var set = new ProgressWindowSet<FakeWindow>();
        var key = ProgressWindowKey.For(ProgressWindowKind.Download, Guid.NewGuid(), ServerTarget.Local);
        var window = new FakeWindow();
        set.Add(key, window);

        Assert.False(set.MainWindowClosed(endsProcess: true));
        set.MainWindowShown();

        Assert.False(set.EndsWithLastWindow);
        Assert.False(set.Closed(key, window));
    }

    [Fact]
    public void CompletionWindowsStayClosedWhileTheProcessWaitsForItsLastProgressWindow()
    {
        var set = new ProgressWindowSet<FakeWindow>();
        Assert.True(set.OpensCompletionWindows);
        var key = ProgressWindowKey.For(ProgressWindowKind.Download, Guid.NewGuid(), ServerTarget.Local);
        set.Add(key, new FakeWindow());

        Assert.False(set.MainWindowClosed(endsProcess: false));
        Assert.True(set.OpensCompletionWindows);

        Assert.False(set.MainWindowClosed(endsProcess: true));
        Assert.False(set.OpensCompletionWindows);

        set.MainWindowShown();
        Assert.True(set.OpensCompletionWindows);
    }

    [Fact]
    public void WithTheSettingOffClosingTheMainWindowOnlyHidesIt()
    {
        var set = new ProgressWindowSet<FakeWindow>();
        Assert.False(set.MainWindowClosed(endsProcess: false));

        var key = ProgressWindowKey.For(ProgressWindowKind.Download, Guid.NewGuid(), ServerTarget.Local);
        var window = new FakeWindow();
        set.Add(key, window);
        Assert.False(set.MainWindowClosed(endsProcess: false));
        Assert.False(set.Closed(key, window));
    }

    [Fact]
    public void AnInProcessWindowTakesTheNameAHostStartedWindowWouldHave()
    {
        var job = Guid.NewGuid();
        var package = Guid.NewGuid();
        const string user = "user";

        Assert.Equal(WindowInstance.EventName(user, false, job, null, null), WindowInstance.WindowName(user, package: false, job, ServerTarget.Local));
        Assert.Equal(WindowInstance.EventName(user, false, null, package, ServerId), WindowInstance.WindowName(user, package: true, package, Server));
        Assert.Equal(WindowInstance.WindowName(user, package: false, job, Server), WindowInstance.JobWindowName(user, job, Server));
        Assert.NotEqual(WindowInstance.WindowName(user, package: false, job, Server), WindowInstance.WindowName(user, package: true, job, Server));
    }

    // ---- The windows themselves (WinUI, not built here): their sources keep to the rules ----------------------

    [Fact]
    public void ProgressWindowsNeverChangeTheShownServer()
    {
        foreach (var file in new[] { "DownloadWindow.xaml.cs", "PackageWindow.cs", "DownloadWindow.TorrentTabs.cs", "DownloadWindow.TorrentControls.cs" })
        {
            var source = Source(file);
            Assert.DoesNotContain("ShownServer.Target =", source);
            Assert.DoesNotContain("ShownServer.Describe(", source);
        }

        // The window's icon follows its own Host.
        Assert.Contains("_iconJob.OnAnotherServer = _connection.IsRemote;", Source("DownloadWindow.xaml.cs"));
        Assert.Contains("FileIconProvider.LoadAsync(fileName, completedPath, key, OnAnotherServer)", Source("JobViewModel.cs"));
        Assert.Contains("ShownServer.IsRemoteFor(remote)", Source("FileIconProvider.cs"));
    }

    [Fact]
    public void TheMainWindowOpensProgressWindowsInItsOwnProcess()
    {
        var main = Source("MainWindow.xaml.cs") + Source("MainWindow.Servers.cs") + Source("MainWindow.Torrents.cs");
        Assert.DoesNotContain("WindowInstance.Arguments(", main);
        var windows = Source("MainWindow.ProgressWindows.cs");
        Assert.Contains("new DownloadWindow(id, manual: true, target, inMainWindowProcess: true)", windows);
        Assert.Contains("new PackageWindow(id, target, jobId => OpenProgressWindow(ProgressWindowKind.Download, jobId, target, package))", windows);
        // UI.B28: a window the user opened stays in front of the one it was opened from once the click is over,
        // whether it opens now or was open already.
        Assert.Contains("OpenProgressWindow(ProgressWindowKind.Download, jobId, _connection.Target, this)", windows);
        Assert.Contains("OpenProgressWindow(ProgressWindowKind.Package, packageId, _connection.Target, this)", Source("MainWindow.xaml.cs"));
        Assert.Contains("if (openedFrom is not null) WindowFront.KeepInFrontAfterInput(open, openedFrom);", windows);
        Assert.Contains("if (openedFrom is not null) WindowFront.KeepInFrontAfterInput(window, openedFrom);", windows);
        // Reopening a window many times leaves no Closed handler behind: each check removes its own.
        var front = Source("WindowFront.cs");
        Assert.Contains("window.Closed += onClosed;", front);
        Assert.Contains("window.Closed -= onClosed;", front);
        // The completion burst is nobody's click: its windows are not pulled forward again.
        Assert.Contains("OpenProgressWindow(next.Kind, next.Id, next.Target);", windows);
        // The completion burst: one window per dispatcher turn, at low priority.
        Assert.Contains("DispatcherQueue.TryEnqueue(DispatcherQueuePriority.Low,", windows);
        Assert.Contains("OpenWindowsInTurns(opens);", Source("MainWindow.Servers.cs"));
        // Uno's X11 head needs Environment.Exit where the process ends.
        Assert.Contains("#if HAS_UNO", windows);
        Assert.Contains("Environment.Exit(0);", windows);
        // The Host's own windows are still processes of their own.
        var app = Source("App.xaml.cs");
        Assert.Contains("new DownloadWindow(id, commandLine.Contains(\"--manual\"), server)", app);
        Assert.Contains("new PackageWindow(package, server)", app);
    }

    [Fact]
    public void AnInProcessWindowClosesItselfWhenTheHostExits()
    {
        Assert.Contains("if (!_inMainWindowProcess) Application.Current.Exit();", Source("DownloadWindow.xaml.cs"));
        Assert.Contains("if (_openFileWindow is null) Application.Current.Exit();", Source("PackageWindow.cs"));
    }

    [Fact]
    public void AnInProcessWindowLeavesTheThemeToTheMainWindowAndCascadesOffItsWindows()
    {
        // AppTheme is the whole process's: the main window follows the setting (and its kept View choice) for all.
        Assert.Contains("if (!_connection.IsRemote && !_inMainWindowProcess) AppTheme.FollowSettings(snapshot.Settings);", Source("DownloadWindow.xaml.cs"));
        Assert.Contains("if (!_connection.IsRemote && _openFileWindow is null) AppTheme.FollowSettings(snapshot.Settings);", Source("PackageWindow.cs"));
        // The cascade and the main window's screen count this process's windows too.
        var cascade = Source("WindowCascade.cs");
        Assert.DoesNotContain("process.Id != current.Id", cascade);
        Assert.Contains("var ids = new HashSet<uint> { (uint)current.Id };", cascade);
    }

    [Fact]
    public void TheTorrentFileChoiceLooksInThisProcessFirst()
    {
        var torrents = Source("MainWindow.Torrents.cs");
        Assert.Contains("InProcessDownloadWindow(jobId) is not null || TryProgressWindow(", torrents);
    }

    private static string Source(string file) => File.ReadAllText(Path.Combine(Root(), "src", "DownloadManager.Desktop.WinUI", file));

    private static string Root()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "DownloadManager.slnx"))) directory = directory.Parent;
        return directory?.FullName ?? throw new InvalidOperationException("The repository root wasn't found.");
    }
}

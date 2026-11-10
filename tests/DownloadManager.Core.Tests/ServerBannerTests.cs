using DownloadManager.Contracts;
using DownloadManager.Desktop.WinUI;

namespace DownloadManager.Core.Tests;

/// <summary>
/// UI.B18, U-WR-1, U-WR-3, U-WR-4, U-WR-5, U-WR-6, U-WR-13: the bar above the list while another server is shown, and the
/// words that name the server in the windows and Options.
/// </summary>
public sealed class ServerBannerTests
{
    private static readonly ServerTarget Server = ServerTarget.Remote("0123456789abcdef0123456789abcdef", "Test server",
        new Uri("http://127.0.0.1:18577/"), "token", plainHttpConfirmed: true);

    [Fact]
    public void ThisPcHasNoBarAndTheUsualEmptyText()
    {
        var banner = ServerBannerRules.For(ServerTarget.Local, connected: false, everConnected: false, pairingEnded: false, stopped: false, error: "Host unavailable");

        Assert.False(banner.Visible);
        Assert.Null(banner.EmptyText);
        Assert.False(banner.Stale);
    }

    [Fact]
    public void AConnectedServerSaysWhichOneItIsWithNoProblemAndNoOtherAction()
    {
        var banner = ServerBannerRules.For(Server, connected: true, everConnected: true, pairingEnded: false, stopped: false, error: null);

        Assert.True(banner.Visible);
        Assert.Equal("Showing the downloads of Test server.", banner.Text);
        Assert.False(banner.IsProblem);
        Assert.Equal(ServerBannerAction.None, banner.Action);
        Assert.Null(banner.ActionText);
        Assert.True(banner.Connected);
        Assert.Null(banner.EmptyText);
    }

    [Fact]
    public void WhileFirstConnectingTheListSaysConnectingNotEmpty()
    {
        var banner = ServerBannerRules.For(Server, connected: false, everConnected: false, pairingEnded: false, stopped: false, error: null);

        Assert.True(banner.Visible);
        Assert.Equal("Connecting to Test server…", banner.Text);
        Assert.Equal("Connecting to Test server…", banner.EmptyText);
        Assert.False(banner.IsProblem);
        Assert.False(banner.Connected);
    }

    [Fact]
    public void AServerNeverReachedIsAProblemWithTryAgainAndNeverAnEmptyServer()
    {
        var banner = ServerBannerRules.For(Server, connected: false, everConnected: false, pairingEnded: false, stopped: false,
            error: "Couldn't reach Test server. Trying again shortly.");

        Assert.True(banner.IsProblem);
        Assert.Equal(ServerBannerAction.Retry, banner.Action);
        Assert.Equal("Try again", banner.ActionText);
        Assert.Equal("Couldn't reach Test server. Trying again shortly.", banner.Text);
        Assert.False(banner.Stale);
        Assert.NotNull(banner.EmptyText);
        Assert.DoesNotContain("No downloads", banner.EmptyText);
        Assert.Contains("can't be reached", banner.EmptyText);
    }

    [Fact]
    public void ALostConnectionMarksTheListOutOfDate()
    {
        var banner = ServerBannerRules.For(Server, connected: false, everConnected: true, pairingEnded: false, stopped: false,
            error: "Disconnected from Test server. Trying again shortly.");

        Assert.True(banner.Stale);
        Assert.Contains("from before the connection was lost", banner.Text);
        Assert.Equal(ServerBannerAction.Retry, banner.Action);
    }

    [Fact]
    public void AReasonThatDoesNotNameTheServerIsGivenItsName()
    {
        var banner = ServerBannerRules.For(Server, connected: false, everConnected: false, pairingEnded: false, stopped: false,
            error: "Couldn't reach the server.");

        Assert.Equal("Test server: Couldn't reach the server.", banner.Text);
    }

    [Fact]
    public void ARefusedPairingOffersPairingAgainNotTryAgain()
    {
        var banner = ServerBannerRules.For(Server, connected: false, everConnected: true, pairingEnded: true, stopped: true,
            error: "This server no longer accepts this PC. Pair it again in File > Servers.");

        Assert.Equal(ServerBannerAction.PairAgain, banner.Action);
        Assert.Equal("Pair again…", banner.ActionText);
        Assert.True(banner.IsProblem);
        Assert.True(banner.Stale);
        Assert.Contains("paired with it again", banner.EmptyText);
    }

    [Fact]
    public void AnHttpAddressNotAllowedYetOffersTheServersSettings()
    {
        var banner = ServerBannerRules.For(Server, connected: false, everConnected: false, pairingEnded: false, stopped: true,
            error: "This server's address starts with http://.");

        Assert.Equal(ServerBannerAction.EditServer, banner.Action);
        Assert.StartsWith("This server's address starts with http://.", banner.Text);
    }

    [Fact]
    public void WindowsNameTheServerAndThisPcKeepsItsTitles()
    {
        Assert.Equal("Download complete — a.zip", ServerNames.ForWindow("Download complete — a.zip", ServerTarget.Local));
        Assert.Equal("Download complete — a.zip — Test server", ServerNames.ForWindow("Download complete — a.zip", Server));
    }

    [Fact]
    public void TheSwitcherSaysWhichHostIsShownToAScreenReader()
    {
        Assert.Equal("Show downloads of: This PC", ServerNames.SwitcherName(ServerTarget.Local));
        Assert.Equal("Show downloads of: Test server", ServerNames.SwitcherName(Server));
    }

    [Fact]
    public void ACompletedDownloadOnAServerDoesNotPromiseActionsItHasNot()
    {
        Assert.DoesNotContain("Choose an action", ServerNames.Completed(Server));
        Assert.Contains("on Test server", ServerNames.Completed(Server));
        Assert.Contains("Open it", ServerNames.Completed(ServerTarget.Local));
    }

    [Fact]
    public void OptionsForAnUnreachableServerSaysSoWithoutYet()
    {
        var text = RemoteOptionsText.ServerUnreachable("127.0.0.1");

        Assert.StartsWith("Can't reach 127.0.0.1", text);
        Assert.DoesNotContain("yet", text);
        Assert.Contains("This PC's own options", text);
    }

    [Fact]
    public void TheServerPageSaysWhereTheRestIsChangedForEachKindOfHost()
    {
        Assert.Contains("in its web UI", RemoteOptionsText.ServerPageHint("NAS", HostKinds.Server));
        var windows = RemoteOptionsText.ServerPageHint("Office PC", HostKinds.Windows);
        Assert.DoesNotContain("web UI", windows);
        Assert.Contains("in the Inbound app on Office PC", windows);
        Assert.Contains("folders", windows);
    }

    [Fact]
    public void TheProviderKeyNoteSaysWhoseAccountAndWhereTheKeyIsKept()
    {
        Assert.Contains(OperatingSystem.IsWindows() ? "your Windows account" : "your user account", RemoteOptionsText.ProviderKeyNote(null, null));
        var server = RemoteOptionsText.ProviderKeyNote("NAS", HostKinds.Server);
        Assert.Contains("account NAS uses", server);
        Assert.Contains("saved on NAS", server);
        Assert.DoesNotContain("your Windows account", server);
        Assert.Contains("Windows account Inbound runs under there", RemoteOptionsText.ProviderKeyNote("Office PC", HostKinds.Windows));
        Assert.DoesNotContain("encrypted", RemoteOptionsText.ProviderKeyNote("Unknown", null));
    }
}

/// <summary>U-WL-4, U-WL-17, U-WR-2, U-WR-15, U-WR-16: completion windows, Add URL's refusals and the Web UI page's address.</summary>
public sealed class CompletionAndAddTests
{
    private static readonly ServerTarget Server = ServerTarget.Remote("0123456789abcdef0123456789abcdef", "Office PC",
        new Uri("http://127.0.0.1:18743/"), "token", plainHttpConfirmed: true);

    [Fact]
    public void APackageGetsOneWindowOnceAllItsFilesAreDoneHoweverManyFinishTogether()
    {
        var package = Guid.NewGuid();
        var files = Enumerable.Range(0, 4).Select(_ => Guid.NewGuid()).ToList();
        var loose = Guid.NewGuid();

        var plan = CompletionBurst.Plan([.. files, loose, files[0]], id => files.Contains(id) ? (package, true) : null);

        Assert.Equal([new CompletionWindow(true, package), new CompletionWindow(false, loose)], plan);
    }

    [Fact]
    public void AFileWhosePackageIsNotDoneOpensNoWindowOfItsOwn()
    {
        var package = Guid.NewGuid();
        var file = Guid.NewGuid();

        Assert.Empty(CompletionBurst.Plan([file], _ => (package, false)));
    }

    [Fact]
    public void TheGateCountsWindowsNotFinishesSoAPackageOfManyFilesStillGetsItsWindow()
    {
        var gate = new RemoteCompletionGate();
        var now = DateTimeOffset.UtcNow;
        var files = Enumerable.Range(0, 5).Select(_ => Guid.NewGuid()).ToList();
        foreach (var file in files) gate.Saw(file, "downloading");

        Assert.All(files, file => Assert.True(gate.Finished(file)));
        Assert.False(gate.Finished(files[0]));
        Assert.False(gate.Finished(Guid.NewGuid()));
        for (var i = 0; i < RemoteCompletionGate.MostWindows; i++) Assert.True(gate.MayOpenWindow(now));
        Assert.False(gate.MayOpenWindow(now));
        Assert.True(gate.MayOpenWindow(now + RemoteCompletionGate.Window));
    }

    [Fact]
    public void AWindowOnAnotherOfTheAppsWindowsMovesDownAndRightByAStep()
    {
        Assert.Equal((78, 78), WindowCascadeRules.Place(78, 78, [], 0, 0, 1920, 1040, 600, 480));
        Assert.Equal((110, 110), WindowCascadeRules.Place(78, 78, [(78, 78)], 0, 0, 1920, 1040, 600, 480));
        Assert.Equal((142, 142), WindowCascadeRules.Place(78, 78, [(78, 78), (110, 110), (500, 20)], 0, 0, 1920, 1040, 600, 480));
    }

    [Fact]
    public void ACascadeThatWouldLeaveTheScreenStartsANewColumnAtTheTop()
    {
        // A 480-high window at y 540 on a 1040-high work area: the next step would end below it.
        var (x, y) = WindowCascadeRules.Place(100, 540, [(100, 540)], 0, 0, 1920, 1040, 600, 480);

        Assert.Equal(0, y);
        Assert.Equal(WindowCascadeRules.Step, x);
    }

    [Fact]
    public void AWindowTitleThatEndsWithTheAppsNameGetsTheServerBeforeIt()
    {
        Assert.Equal("My TorBox — Office PC — Inbound Download Manager", ServerNames.ForWindow("My TorBox — Inbound Download Manager", Server));
        Assert.Equal("My TorBox — Inbound Download Manager", ServerNames.ForWindow("My TorBox — Inbound Download Manager", ServerTarget.Local));
    }

    [Fact]
    public void AnAddressTheHostRefusesTurnsStartOffAndSaysWhereItCantBeAdded()
    {
        var text = AddRules.RefusedText("Downloads added from another device can't use addresses of the computer Inbound runs on or of its local network.", Server);

        Assert.Contains("can't be added to Office PC", text);
        Assert.Contains("are off", text);
        Assert.DoesNotContain("You can still start", text);
    }

    [Fact]
    public void AFailedAddSaysItWasNotAddedAndThatTheInputIsKept()
    {
        Assert.Equal("Not added to Office PC: Disk full. Change what is needed and try again, or Cancel.", AddRules.NotAddedText(" Disk full. ", Server));
        Assert.StartsWith("Not added: the Host rejected the request.", AddRules.NotAddedText(null, ServerTarget.Local));
    }

    [Fact]
    public void WhileOnlyThisPcCanReachTheWebUiItsAddressHereIsShown()
    {
        var localOnly = new WebUiStatusDto { State = WebUiStates.Running, LocalOnly = true, Port = 18743 };

        Assert.Equal("http://127.0.0.1:18743/", WebUiForm.ThisPcAddress(localOnly));
        Assert.Contains("http://127.0.0.1:18743/", WebUiForm.StateText(localOnly).Text);
        Assert.Contains("port 18743", WebUiForm.StateText(localOnly).Text);
        Assert.Null(WebUiForm.ThisPcAddress(new WebUiStatusDto { State = WebUiStates.Off, Port = 18743 }));
    }
}

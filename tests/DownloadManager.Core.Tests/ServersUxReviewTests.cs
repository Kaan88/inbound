using DownloadManager.Contracts;
using DownloadManager.Desktop.WinUI;

namespace DownloadManager.Core.Tests;

/// <summary>The review of the servers-ux and window-local fixes (U-WR-1, U-WR-2, U-WR-8, U-WL-4, U-WR-16).</summary>
public sealed class ServersUxReviewTests
{
    private static readonly ServerTarget Server = ServerTarget.Remote("0123456789abcdef0123456789abcdef", "Office PC",
        new Uri("http://127.0.0.1:18743/"), "token", plainHttpConfirmed: true);

    [Fact]
    public void U_WR_1_a_server_still_connecting_says_so_in_the_status_bar()
    {
        var connecting = ServerBannerRules.For(Server, connected: false, everConnected: false, pairingEnded: false, stopped: false, error: null);
        Assert.Equal("Connecting to Office PC…", connecting.Status);

        var down = ServerBannerRules.For(Server, connected: false, everConnected: false, pairingEnded: false, stopped: false, error: "Couldn't reach the server.");
        Assert.Equal("Not connected to Office PC", down.Status);

        var lost = ServerBannerRules.For(Server, connected: false, everConnected: true, pairingEnded: false, stopped: false, error: "Couldn't reach the server.");
        Assert.Equal("Not connected to Office PC · the list is out of date", lost.Status);

        Assert.Null(ServerBannerRules.For(Server, connected: true, everConnected: true, pairingEnded: false, stopped: false, error: null).Status);
        Assert.Null(ServerBannerRules.For(ServerTarget.Local, connected: false, everConnected: false, pairingEnded: false, stopped: false, error: null).Status);
    }

    [Fact]
    public void U_WR_2_a_dialog_that_cannot_be_shown_again_is_tried_again_rather_than_dropped()
    {
        // The first show refused: nothing was typed yet, so there is nothing to keep.
        Assert.Equal(ReshowStep.Stop, AddRules.AfterShow(shown: false, problem: null, lastProblem: null, failedShows: 0));
        // Added, or cancelled.
        Assert.Equal(ReshowStep.Stop, AddRules.AfterShow(shown: true, problem: null, lastProblem: "Disk full.", failedShows: 0));
        Assert.Equal(ReshowStep.ShowAgain, AddRules.AfterShow(shown: true, problem: "Disk full.", lastProblem: null, failedShows: 0));
        // Shown again after a failed Start, but refused (the old dialog was still closing, or another one is open).
        Assert.Equal(ReshowStep.TryAgain, AddRules.AfterShow(shown: false, problem: null, lastProblem: "Disk full.", failedShows: 0));
        Assert.Equal(ReshowStep.TryAgain, AddRules.AfterShow(shown: false, problem: null, lastProblem: "Disk full.", failedShows: AddRules.MostReshowTries - 1));
        Assert.Equal(ReshowStep.GiveUp, AddRules.AfterShow(shown: false, problem: null, lastProblem: "Disk full.", failedShows: AddRules.MostReshowTries));
    }

    [Fact]
    public void U_WR_2_a_refusal_lasts_only_until_the_host_answers_without_one()
    {
        Assert.Equal("Not here.", AddRules.RefusalAfter(null, answered: true, refused: true, "Not here."));
        Assert.Equal("This address can't be added.", AddRules.RefusalAfter(null, answered: true, refused: true, null));
        Assert.Null(AddRules.RefusalAfter("Not here.", answered: true, refused: false, null));
        // A check that got no answer (timed out, or credentials changed mid-way) says nothing new.
        Assert.Equal("Not here.", AddRules.RefusalAfter("Not here.", answered: false, refused: false, null));
    }

    [Fact]
    public void U_WR_8_no_container_text_tells_the_user_to_add_a_link_themselves()
    {
        var source = File.ReadAllText(Path.Combine(WindowLocalFindingsTests.RepoRoot(), "src", "DownloadManager.Runtime", "Remote", "ContainerImportService.cs"));
        Assert.DoesNotContain("yourself as a normal download", source, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void U_WL_4_a_window_on_another_screen_opens_over_the_main_window_on_its_screen()
    {
        // The main window on a second screen at 1960,80; the saved place is on the first.
        var main = new ScreenRect(1960, 80, 1400, 760);
        var mainScreen = new ScreenRect(1920, 0, 1920, 1080);
        var mainWork = new ScreenRect(1920, 0, 1920, 1040);

        Assert.Equal((2360, 220), WindowCascadeRules.OntoMainScreen(78, 78, 600, 480, main, mainScreen, mainWork));
        // Already on the main window's screen: the saved place stays.
        Assert.Equal((2000, 300), WindowCascadeRules.OntoMainScreen(2000, 300, 600, 480, main, mainScreen, mainWork));
        // Kept inside the work area when the main window hangs off its edge.
        Assert.Equal((3240, 560), WindowCascadeRules.OntoMainScreen(78, 78, 600, 480, new ScreenRect(3500, 900, 1400, 760), mainScreen, mainWork));
    }

    [Fact]
    public void U_WR_16_a_package_with_failed_files_gets_its_window_once_every_file_has_settled()
    {
        Assert.True(CompletionBurst.Settled(["completed", "failed", "completed"]));
        Assert.True(CompletionBurst.Settled(["completed", "cancelled"]));
        Assert.True(CompletionBurst.Settled(["completed", "completed"]));
        Assert.False(CompletionBurst.Settled(["completed", "downloading"]));
        Assert.False(CompletionBurst.Settled(["failed", "failed"]));
        Assert.False(CompletionBurst.Settled([]));

        var package = Guid.NewGuid();
        var failed = Guid.NewGuid();
        Assert.Equal([new CompletionWindow(true, package)], CompletionBurst.Plan([failed], _ => (package, true)));
    }

    [Fact]
    public void U_WR_16_a_package_window_counts_failed_and_cancelled_files()
    {
        Assert.Equal("3 of 5 files complete · 2 failed", CompletionBurst.Summary(["completed", "completed", "failed", "completed", "failed"]));
        Assert.Equal("1 of 3 files complete · 1 failed · 1 cancelled", CompletionBurst.Summary(["completed", "failed", "cancelled"]));
        Assert.Equal("2 of 2 files complete", CompletionBurst.Summary(["completed", "completed"]));
        Assert.Equal("Package complete", CompletionBurst.Title(["completed", "completed"]));
        Assert.Equal("Package finished, 1 of 3 files failed", CompletionBurst.Title(["completed", "failed", "completed"]));
        Assert.Equal("Package finished, 1 of 2 files cancelled", CompletionBurst.Title(["completed", "cancelled"]));
        Assert.Null(CompletionBurst.Title(["completed", "downloading"]));
    }
}

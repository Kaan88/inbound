using DownloadManager.Contracts;
using DownloadManager.Desktop.WinUI;

namespace DownloadManager.Core.Tests;

/// <summary>UI.R40: Check for Updates is one dialog; these walk its steps without a window.</summary>
public sealed class UpdateDialogFlowTests
{
    private static UpdateCheckResult Offer(string version = "0.7.2", long size = 65L * 1024 * 1024) => new()
    {
        Ok = true, Status = "updateAvailable", InstalledVersion = "0.7.1", AvailableVersion = version, Size = size,
        InstallerUrl = "https://example.invalid/inbound.zip", Sha256 = new string('a', 64),
    };

    private static UpdateDialogFlow Offered(bool resumes = true)
    {
        var flow = new UpdateDialogFlow(resumes);
        Assert.Equal(UpdateCommand.Check, flow.Start());
        flow.Checked(Offer());
        return flow;
    }

    private static UpdateDialogFlow Downloaded(int active = 0, bool resumes = true)
    {
        var flow = Offered(resumes);
        Assert.Equal(UpdateCommand.Download, flow.Primary());
        flow.Downloaded(new UpdateDownloadResult { Ok = true, Path = "x", Size = 1 }, active);
        return flow;
    }

    [Fact]
    public void ItOpensCheckingWithARingAndOnlyCancel()
    {
        var flow = new UpdateDialogFlow();
        Assert.Equal(UpdateCommand.Check, flow.Start());
        var view = flow.View();
        Assert.Equal(UpdateStep.Checking, flow.Step);
        Assert.Equal(UpdateBusy.Ring, view.Busy);
        Assert.Null(view.Primary);
        Assert.Equal("Cancel", view.Close);
        Assert.True(view.CanLeave);
        Assert.Equal(UpdateCommand.None, flow.Leave(out var left));
        Assert.True(left);
    }

    [Fact]
    public void UpToDateIsASuccessWithCloseOnly()
    {
        var flow = new UpdateDialogFlow();
        flow.Start();
        flow.Checked(new UpdateCheckResult { Ok = true, Status = "upToDate", InstalledVersion = "0.7.1", AvailableVersion = "0.7.1" });
        var view = flow.View();
        Assert.Equal(UpdateStep.UpToDate, flow.Step);
        Assert.Equal(UpdateTone.Success, view.Tone);
        Assert.Equal("Inbound 0.7.1 is the latest version.", view.Message);
        Assert.Null(view.Primary);
        Assert.Equal("Close", view.Close);
        Assert.Equal(UpdateCommand.None, flow.Leave(out _));
    }

    [Fact]
    public void APortableCopySaysHowToGetUpdates()
    {
        var flow = new UpdateDialogFlow();
        flow.Start();
        flow.Checked(new UpdateCheckResult { Ok = false, Status = "notInstalled", Error = "Portable and development builds do not auto-update." });
        var view = flow.View();
        Assert.Equal(UpdateStep.NotInstalled, flow.Step);
        Assert.Contains("Setup", view.Detail);
        Assert.Null(view.Primary);
    }

    [Fact]
    public void AFailedCheckOffersTryAgainOnlyWhenTryingAgainCanHelp()
    {
        var flow = new UpdateDialogFlow();
        flow.Start();
        flow.Checked(new UpdateCheckResult { Ok = false, Status = "invalid", Error = "Update manifest returned HTTP 503." });
        var view = flow.View();
        Assert.Equal(UpdateStep.CheckFailed, flow.Step);
        Assert.Equal(UpdateTone.Error, view.Tone);
        Assert.Equal("Update manifest returned HTTP 503.", view.Message);
        Assert.Equal("The update server had a problem. Try again later.", view.Detail);
        Assert.Equal("Try again", view.Primary);
        Assert.Equal(UpdateCommand.Check, flow.Primary());
        Assert.Equal(UpdateStep.Checking, flow.Step);

        // No update source set: trying again gives the same answer.
        flow.Checked(new UpdateCheckResult { Ok = false, Status = "notConfigured", Error = "Update manifest URL/public key are not configured." });
        Assert.Null(flow.View().Primary);
        Assert.Equal(UpdateCommand.None, flow.Primary());
        // Plain words with a next step, not the Host's wording.
        Assert.Equal("This copy of Inbound has no place set to get updates from.", flow.View().Message);
        Assert.Equal("To get updates, install Inbound with Setup.", flow.View().Detail);

        // The Host gone or a timeout: try again.
        var thrown = new UpdateDialogFlow();
        thrown.Start();
        thrown.CheckThrew("The Host did not answer.");
        Assert.Equal("Try again", thrown.View().Primary);
        Assert.DoesNotContain("internet", thrown.View().Detail);
    }

    [Theory]
    // The update information couldn't be trusted: the connection is not the problem.
    [InlineData("The update manifest signature is invalid.", "Inbound couldn't verify")]
    [InlineData("Update manifest is too large.", "Inbound couldn't verify")]
    [InlineData("The update manifest redirected to a non-HTTPS host.", "Inbound couldn't verify")]
    [InlineData("The update manifest must use HTTPS (loopback is allowed for testing).", "Inbound couldn't verify")]
    [InlineData("Update manifest returned HTTP 404.", "The update server had a problem")]
    // An exception from the request itself (no connection, a name that didn't resolve, a timeout).
    [InlineData("No such host is known. (updates.example.invalid:443)", "Check your internet connection")]
    [InlineData(null, "Check your internet connection")]
    public void EachCheckFailureGetsANextStepThatFits(string? error, string detail)
    {
        var flow = new UpdateDialogFlow();
        flow.Start();
        flow.Checked(new UpdateCheckResult { Ok = false, Status = "invalid", Error = error });
        Assert.StartsWith(detail, flow.View().Detail);
        Assert.Equal("Try again", flow.View().Primary);
    }

    [Fact]
    public void AnOfferShowsTheVersionSizeAndNotesWithDownloadAndLater()
    {
        var flow = Offered();
        var view = flow.View();
        Assert.Equal(UpdateStep.Available, flow.Step);
        Assert.Equal("Update available", view.Title);
        Assert.Equal("Inbound 0.7.2 is available (65.0 MB). You have 0.7.1.", view.Message);
        Assert.True(view.Notes);
        Assert.Equal("Download", view.Primary);
        Assert.Equal("Later", view.Close);
        Assert.True(view.PrimaryIsDefault);
        // Later puts this version off, as it always did.
        Assert.Equal(UpdateCommand.Defer, flow.Leave(out var left));
        Assert.True(left);
    }

    [Fact]
    public void DownloadingShowsABarThatFollowsTheHostAndCancelDoesNotPutItOff()
    {
        var flow = Offered();
        Assert.Equal(UpdateCommand.Download, flow.Primary());
        var view = flow.View();
        Assert.Equal(UpdateStep.Downloading, flow.Step);
        Assert.Equal(UpdateBusy.Bar, view.Busy);
        Assert.Equal(0, view.Percent);
        Assert.Null(view.Primary);
        Assert.Equal("Cancel", view.Close);
        Assert.True(view.Notes);

        flow.Progress(new UpdateProgressDto { Stage = "downloading", Version = "0.7.2", ReceivedBytes = 32L * 1024 * 1024, TotalBytes = 64L * 1024 * 1024 });
        view = flow.View();
        Assert.Equal(50, view.Percent);
        Assert.Equal("32.0 MB of 64.0 MB", view.ProgressText);

        // Another version's progress (an automatic install) is not this dialog's.
        flow.Progress(new UpdateProgressDto { Stage = "downloading", Version = "0.7.3", ReceivedBytes = 1, TotalBytes = 2 });
        Assert.Equal(50, flow.View().Percent);

        flow.Progress(new UpdateProgressDto { Stage = "verifying", Version = "0.7.2" });
        view = flow.View();
        Assert.Equal(UpdateBusy.Bar, view.Busy);
        Assert.Null(view.Percent);
        Assert.StartsWith("Checking Inbound 0.7.2", view.Message);

        // Cancel lets the download finish in the background; the version stays on offer (the tray's entry and the
        // automatic install keep it), so nothing is sent.
        Assert.Equal(UpdateCommand.None, flow.Leave(out var left));
        Assert.True(left);
    }

    [Fact]
    public void ACancelledDownloadSaysTheUpdateStaysOnOffer()
    {
        var flow = Offered();
        flow.Primary();
        Assert.Contains("stays on offer", flow.View().Detail);
        Assert.Equal(UpdateCommand.None, flow.Leave(out _));
    }

    [Fact]
    public void AFailedDownloadSaysWhyAndOffersTryAgain()
    {
        var flow = Offered();
        flow.Primary();
        flow.Downloaded(new UpdateDownloadResult { Ok = false, Error = "Installer size or SHA-256 did not match the signed manifest." }, 0);
        var view = flow.View();
        Assert.Equal(UpdateStep.DownloadFailed, flow.Step);
        Assert.Equal(UpdateTone.Error, view.Tone);
        Assert.Equal("Installer size or SHA-256 did not match the signed manifest.", view.Message);
        Assert.Contains("Nothing was changed", view.Detail);
        Assert.Equal("Try again", view.Primary);
        Assert.Equal(UpdateCommand.None, flow.Leave(out _));
        Assert.Equal(UpdateCommand.Download, flow.Primary());
        Assert.Equal(UpdateStep.Downloading, flow.Step);

        flow.DownloadThrew("Timed out.");
        Assert.Equal(UpdateStep.DownloadFailed, flow.Step);
    }

    [Fact]
    public void ReadyWithNoDownloadsRunningInstallsAtOnce()
    {
        var flow = Downloaded();
        var view = flow.View();
        Assert.Equal(UpdateStep.Ready, flow.Step);
        Assert.Null(view.Warning);
        Assert.Equal("Install now", view.Primary);
        Assert.Equal("Later", view.Close);
        Assert.True(view.PrimaryIsDefault);
        Assert.Equal(UpdateCommand.Install, flow.Primary());
        Assert.Equal(UpdateStep.Installing, flow.Step);
    }

    [Theory]
    [InlineData(1, true, "1 download is running. Installing stops it, and it goes on when Inbound starts again.")]
    [InlineData(1, false, "1 download is running. Installing stops it, and it stays stopped until you resume it.")]
    [InlineData(3, false, "3 downloads are running. Installing stops them, and they stay stopped until you resume them.")]
    public void ReadyWarnsInlineWhenDownloadsWouldBePaused(int active, bool resumes, string warning)
    {
        var flow = Downloaded(active, resumes);
        var view = flow.View();
        Assert.Equal(warning, view.Warning);
        Assert.Equal("Install anyway", view.Primary);
        Assert.False(view.PrimaryIsDefault);
        Assert.Equal(UpdateCommand.InstallAnyway, flow.Primary());
    }

    [Fact]
    public void TheWarningFollowsTheDownloadsWhileTheDialogWaits()
    {
        var flow = Downloaded(2);
        flow.ActiveDownloads(0);
        Assert.Null(flow.View().Warning);
        Assert.Equal("Install now", flow.View().Primary);
        flow.ActiveDownloads(1);
        Assert.NotNull(flow.View().Warning);
    }

    [Fact]
    public void ABusyWarningEndsWhenTheDownloadsAreCountedAgain()
    {
        // The Host answered busy for a download only starting, which the window didn't count yet.
        var flow = Downloaded();
        flow.Primary();
        flow.Installed(new UpdateInstallResult { Ok = false, Status = "busy", Error = "Downloads became active while the update was downloading." });
        Assert.NotNull(flow.View().Warning);
        Assert.Equal("Install anyway", flow.View().Primary);

        // It finished: the warning goes and Install now installs without pausing anything.
        flow.ActiveDownloads(0);
        Assert.Null(flow.View().Warning);
        Assert.Equal("Install now", flow.View().Primary);
        Assert.Equal(UpdateCommand.Install, flow.Primary());
    }

    [Fact]
    public void StartingTheInstallSaysItCanTakeAMinute()
    {
        var flow = Downloaded();
        flow.Primary();
        Assert.Equal(UpdateStep.Installing, flow.Step);
        Assert.Equal("This can take a minute or two.", flow.View().Detail);
    }

    [Fact]
    public void ABusyAnswerForDownloadsComesBackAsTheWarningAndThenInstallsAnyway()
    {
        var flow = Downloaded();
        Assert.Equal(UpdateCommand.Install, flow.Primary());
        var installing = flow.View();
        Assert.False(installing.CanLeave);
        Assert.Null(installing.Close);
        Assert.Equal(UpdateCommand.None, flow.Leave(out var left));
        Assert.False(left);

        flow.Installed(new UpdateInstallResult { Ok = false, Status = "busy", Error = "Downloads are active. Use Install anyway or wait for them to finish." });
        var view = flow.View();
        Assert.Equal(UpdateStep.Ready, flow.Step);
        Assert.Equal("Downloads are running. Installing stops them, and they go on when Inbound starts again.", view.Warning);
        Assert.Equal(UpdateCommand.InstallAnyway, flow.Primary());
    }

    [Fact]
    public void AStartedInstallSaysInboundRestartsAndCanBeClosed()
    {
        var flow = Downloaded();
        flow.Primary();
        flow.Installed(new UpdateInstallResult { Ok = true, Status = "scheduled", Version = "0.7.2" });
        var view = flow.View();
        Assert.Equal(UpdateStep.Restarting, flow.Step);
        Assert.Equal("Inbound 0.7.2 is being installed.", view.Message);
        Assert.Contains("starts again", view.Detail);
        Assert.Null(view.Primary);
        Assert.Equal("Close", view.Close);
        Assert.Equal(UpdateCommand.None, flow.Leave(out var left));
        Assert.True(left);
    }

    [Fact]
    public void AFailedInstallSaysNothingChangedAndTriesAgainOnlyWhenItCanHelp()
    {
        var flow = Downloaded();
        flow.Primary();
        flow.Installed(new UpdateInstallResult { Ok = false, Status = "failed", Error = "The update package has no apply-update.ps1." });
        var view = flow.View();
        Assert.Equal(UpdateStep.InstallFailed, flow.Step);
        Assert.Equal(UpdateTone.Error, view.Tone);
        Assert.StartsWith("Inbound wasn't changed.", view.Detail);
        Assert.Equal("Try again", view.Primary);
        Assert.Equal(UpdateCommand.Install, flow.Primary());

        flow.Installed(new UpdateInstallResult { Ok = false, Status = "busy", Error = "An update is already being installed." });
        Assert.Equal(UpdateStep.InstallFailed, flow.Step);
        Assert.Equal("An update is already being installed.", flow.View().Message);
        Assert.Null(flow.View().Primary);
        Assert.Equal(UpdateCommand.None, flow.Primary());

        var thrown = Downloaded();
        thrown.Primary();
        thrown.InstallThrew("The Host did not answer.");
        Assert.Equal(UpdateStep.InstallFailed, thrown.Step);
    }

    [Fact]
    public void ADialogOpenedDuringAnEarlierDownloadShowsThatDownload()
    {
        var flow = new UpdateDialogFlow();
        flow.ResumeDownloading(Offer());
        Assert.Equal(UpdateStep.Downloading, flow.Step);
        Assert.Equal("Downloading Inbound 0.7.2…", flow.View().Message);
        flow.Downloaded(new UpdateDownloadResult { Ok = true }, 0);
        Assert.Equal(UpdateStep.Ready, flow.Step);
    }

    [Fact]
    public void AnswersForAnotherStepAreIgnored()
    {
        var flow = Offered();
        flow.Checked(new UpdateCheckResult { Ok = true, Status = "upToDate" });
        flow.Downloaded(new UpdateDownloadResult { Ok = true }, 0);
        flow.Installed(new UpdateInstallResult { Ok = true });
        flow.CheckThrew("late");
        Assert.Equal(UpdateStep.Available, flow.Step);
    }

    [Fact]
    public void EveryStepButStartingTheInstallCanBeLeftAndHasAClosingButton()
    {
        foreach (var step in Enum.GetValues<UpdateStep>())
        {
            var view = Reach(step).View();
            Assert.Equal(step != UpdateStep.Installing, view.CanLeave);
            Assert.Equal(view.CanLeave, view.Close is not null);
        }
    }

    private static UpdateDialogFlow Reach(UpdateStep step)
    {
        var flow = new UpdateDialogFlow();
        flow.Start();
        switch (step)
        {
            case UpdateStep.Checking: return flow;
            case UpdateStep.UpToDate: flow.Checked(new UpdateCheckResult { Ok = true, Status = "upToDate" }); return flow;
            case UpdateStep.NotInstalled: flow.Checked(new UpdateCheckResult { Status = "notInstalled" }); return flow;
            case UpdateStep.CheckFailed: flow.CheckThrew("x"); return flow;
        }

        flow.Checked(Offer());
        if (step == UpdateStep.Available) return flow;
        flow.Primary();
        if (step == UpdateStep.Downloading) return flow;
        if (step == UpdateStep.DownloadFailed) { flow.DownloadThrew("x"); return flow; }
        flow.Downloaded(new UpdateDownloadResult { Ok = true }, 0);
        if (step == UpdateStep.Ready) return flow;
        flow.Primary();
        if (step == UpdateStep.Installing) return flow;
        if (step == UpdateStep.Restarting) flow.Installed(new UpdateInstallResult { Ok = true });
        else flow.InstallThrew("x");
        Assert.Equal(step, flow.Step);
        return flow;
    }
}

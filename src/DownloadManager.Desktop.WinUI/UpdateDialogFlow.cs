using DownloadManager.Contracts;

namespace DownloadManager.Desktop.WinUI;

/// <summary>Where the Check for Updates dialog is (UI.R40).</summary>
internal enum UpdateStep
{
    Checking,
    UpToDate,
    NotInstalled,
    CheckFailed,
    Available,
    Downloading,
    DownloadFailed,
    Ready,
    Installing,
    Restarting,
    InstallFailed,
}

/// <summary>What the window does next for the dialog: a Host update command, or nothing.</summary>
internal enum UpdateCommand
{
    None,
    Check,
    Download,
    Install,
    InstallAnyway,
    Defer,
}

/// <summary>The dialog's busy sign: none, a ring (no amount known) or a bar (a percent, or none for "still working").</summary>
internal enum UpdateBusy
{
    None,
    Ring,
    Bar,
}

/// <summary>How the main line reads: plain text, or a result bar (design guidelines: check results are an InfoBar).</summary>
internal enum UpdateTone
{
    Plain,
    Success,
    Error,
}

/// <summary>What the dialog shows at one step. Null button text hides that button.</summary>
/// <param name="Notes">Show UPD.N1's notes box and the Full release notes link (an update is on offer).</param>
/// <param name="Warning">A line shown in the warning colour (downloads that would be paused), or null.</param>
/// <param name="Percent">The bar's value, 0 to 100; null for a bar that only says it's working.</param>
/// <param name="PrimaryIsDefault">Enter presses the primary button; otherwise the close button.</param>
/// <param name="CanLeave">Esc and the close button work; false only while the install is being started.</param>
internal sealed record UpdateDialogView(
    string Title,
    UpdateTone Tone,
    string Message,
    string? Detail,
    string? Warning,
    bool Notes,
    UpdateBusy Busy,
    double? Percent,
    string? ProgressText,
    string? Primary,
    string? Close,
    bool PrimaryIsDefault,
    bool CanLeave);

/// <summary>
/// UI.R40: Check for Updates in one dialog whose content changes in place: checking, then up to date or an update on
/// offer (with UPD.N1's notes), downloading and checking it, ready to install (saying so when downloads would be
/// paused), then installing, or what went wrong with a plain next step. Pure: the window sends the Host commands this
/// returns and feeds the answers back, so every step is tested without a window (Core.Tests).
/// </summary>
internal sealed class UpdateDialogFlow
{
    private const string NextTimeHint = "You can try again from Help > Check for Updates.";

    private string? _error;
    private string? _checkStatus;
    private string? _installStatus;
    private long _received;
    private long? _total;
    private bool _verifying;
    private int _activeDownloads;
    private bool _activeUnknown;

    /// <param name="resumesAfterRestart">Options > "resume unfinished downloads on start": downloads paused by the
    /// install go on by themselves after it; otherwise they stay paused.</param>
    public UpdateDialogFlow(bool resumesAfterRestart = true)
    {
        ResumesAfterRestart = resumesAfterRestart;
    }

    public UpdateStep Step { get; private set; } = UpdateStep.Checking;
    public string? InstalledVersion { get; private set; }
    public string? AvailableVersion { get; private set; }
    public long? Size { get; private set; }
    public bool ResumesAfterRestart { get; set; }

    /// <summary>The check's answer, kept for the notes box.</summary>
    public UpdateCheckResult? Check { get; private set; }

    /// <summary>The command to send when the dialog opens: the check.</summary>
    public UpdateCommand Start()
    {
        Step = UpdateStep.Checking;
        _error = null;
        return UpdateCommand.Check;
    }

    /// <summary>
    /// The dialog opened while an earlier dialog's download of <paramref name="check"/>'s version is still going (it
    /// was cancelled from the dialog, but the Host finishes it): show that download instead of starting another.
    /// </summary>
    public void ResumeDownloading(UpdateCheckResult check)
    {
        Remember(check);
        Step = UpdateStep.Downloading;
        _received = 0;
        _total = check.Size;
        _verifying = false;
    }

    public void Checked(UpdateCheckResult? check)
    {
        if (Step != UpdateStep.Checking) return;
        if (check is null)
        {
            Fail(UpdateStep.CheckFailed, null, "invalid");
            return;
        }

        Remember(check);
        if (check.Status == "notInstalled")
        {
            Step = UpdateStep.NotInstalled;
            return;
        }

        if (!check.Ok)
        {
            Fail(UpdateStep.CheckFailed, check.Error, check.Status);
            return;
        }

        Step = check.Status == "updateAvailable" && !string.IsNullOrWhiteSpace(check.AvailableVersion)
            ? UpdateStep.Available
            : UpdateStep.UpToDate;
    }

    /// <summary>The check or its connection failed with an exception (the Host went away, a timeout).</summary>
    public void CheckThrew(string message)
    {
        if (Step != UpdateStep.Checking) return;
        Fail(UpdateStep.CheckFailed, message, "invalid");
        _checkFailure = CheckFailure.NoAnswer;
    }

    /// <summary>The primary button. Returns the command to send; the step moves on at once.</summary>
    public UpdateCommand Primary()
    {
        switch (Step)
        {
            case UpdateStep.Available:
            case UpdateStep.DownloadFailed:
                Step = UpdateStep.Downloading;
                _received = 0;
                _total = Size;
                _verifying = false;
                _error = null;
                return UpdateCommand.Download;
            case UpdateStep.CheckFailed when Retryable:
                Step = UpdateStep.Checking;
                _error = null;
                return UpdateCommand.Check;
            case UpdateStep.Ready:
            case UpdateStep.InstallFailed when InstallRetryable:
                // What the dialog showed is what the person agreed to: the install goes ahead over running downloads
                // only when it said so. A download that started unseen is caught by the Host's own check (a busy
                // answer), which brings the warning back.
                var anyway = WarnsOfDownloads;
                Step = UpdateStep.Installing;
                _error = null;
                return anyway ? UpdateCommand.InstallAnyway : UpdateCommand.Install;
            default:
                return UpdateCommand.None;
        }
    }

    /// <summary>
    /// Esc or the close button. Leaving an offered or downloaded update puts it off, as Later always did; leaving
    /// anything else sends nothing. A download cancelled from the dialog is not put off: it finishes in the background,
    /// and the tray's entry and the automatic install still have that version. Returns None, without leaving, while the
    /// install is starting.
    /// </summary>
    public UpdateCommand Leave(out bool left)
    {
        left = View().CanLeave;
        if (!left) return UpdateCommand.None;
        return Step is UpdateStep.Available or UpdateStep.Ready && AvailableVersion is not null
            ? UpdateCommand.Defer
            : UpdateCommand.None;
    }

    /// <summary>The Host's update progress (<c>update.progress</c>) while downloading.</summary>
    public void Progress(UpdateProgressDto? progress)
    {
        if (Step != UpdateStep.Downloading || progress is null) return;
        if (progress.Version is { } version && AvailableVersion is not null &&
            !Equals(Changelog.Normal(version), Changelog.Normal(AvailableVersion))) return;
        switch (progress.Stage?.ToLowerInvariant())
        {
            case "downloading":
                _verifying = false;
                _received = Math.Max(0, progress.ReceivedBytes);
                if (progress.TotalBytes is > 0) _total = progress.TotalBytes;
                break;
            case "verifying":
                _verifying = true;
                break;
        }
    }

    public void Downloaded(UpdateDownloadResult? download, int activeDownloads)
    {
        if (Step != UpdateStep.Downloading) return;
        if (download is not { Ok: true })
        {
            Fail(UpdateStep.DownloadFailed, download?.Error, null);
            return;
        }

        Step = UpdateStep.Ready;
        _activeUnknown = false;
        ActiveDownloads(activeDownloads);
    }

    public void DownloadThrew(string message) => Fail(UpdateStep.DownloadFailed, message, null, onlyFrom: UpdateStep.Downloading);

    /// <summary>
    /// The list changed while the dialog waits to install: the warning follows the downloads running now. A count is
    /// news, so it also ends a warning a busy answer brought while the window counted none (a download only starting);
    /// if that download still runs, the next install is answered busy again and the warning comes back.
    /// </summary>
    public void ActiveDownloads(int count)
    {
        _activeDownloads = Math.Max(0, count);
        _activeUnknown = false;
    }

    public void Installed(UpdateInstallResult? install)
    {
        if (Step != UpdateStep.Installing) return;
        if (install is { Ok: true })
        {
            Step = UpdateStep.Restarting;
            if (!string.IsNullOrWhiteSpace(install.Version)) AvailableVersion = install.Version;
            return;
        }

        // Downloads started (or were running unseen, for a window showing another server): back to the question,
        // now saying so; the next Install sends the install anyway, which pauses them as before.
        if (install is { Status: "busy" } && install.Error?.StartsWith("Downloads", StringComparison.Ordinal) == true)
        {
            Step = UpdateStep.Ready;
            _activeUnknown = _activeDownloads == 0;
            return;
        }

        Fail(UpdateStep.InstallFailed, install?.Error, install?.Status);
    }

    public void InstallThrew(string message) => Fail(UpdateStep.InstallFailed, message, null, onlyFrom: UpdateStep.Installing);

    private bool Retryable => _checkStatus is "invalid";

    /// <summary>Why a check failed, for a next step that fits: the Host's status is "invalid" for all but the first.</summary>
    private enum CheckFailure
    {
        /// <summary>No update source set (a build without one): trying again gives the same answer.</summary>
        NotConfigured,
        /// <summary>The update server couldn't be reached (no connection, a name that didn't resolve, a timeout).</summary>
        Connection,
        /// <summary>The server answered with an error (HTTP 4xx or 5xx).</summary>
        Server,
        /// <summary>The answer couldn't be trusted: a bad signature, too large, not HTTPS.</summary>
        NotVerified,
        /// <summary>Inbound's own background part didn't answer the window.</summary>
        NoAnswer,
    }

    private CheckFailure _checkFailure;

    /// <summary>The Host's wording for each failure (DownloadManagerService.CheckForUpdatesAsync); anything else is an
    /// exception from the request itself, which is the connection.</summary>
    private static CheckFailure Classify(string? status, string? error)
    {
        if (status == "notConfigured") return CheckFailure.NotConfigured;
        var text = error ?? string.Empty;
        if (text.StartsWith("Update manifest returned HTTP", StringComparison.Ordinal)) return CheckFailure.Server;
        if (text.Contains("signature", StringComparison.OrdinalIgnoreCase) ||
            text.Contains("too large", StringComparison.OrdinalIgnoreCase) ||
            text.Contains("HTTPS", StringComparison.Ordinal)) return CheckFailure.NotVerified;
        return CheckFailure.Connection;
    }

    private (string Message, string? Detail) CheckFailureText() => _checkFailure switch
    {
        CheckFailure.NotConfigured => ("This copy of Inbound has no place set to get updates from.",
            "To get updates, install Inbound with Setup."),
        CheckFailure.Server => (_error ?? "The update server answered with an error.",
            "The update server had a problem. Try again later."),
        CheckFailure.NotVerified => (_error ?? "The update information couldn't be verified.",
            "Inbound couldn't verify the update information, so nothing was changed. Try again later; if this keeps happening, install the latest Inbound with Setup."),
        CheckFailure.NoAnswer => (_error ?? "Inbound didn't answer.",
            "Inbound's background part didn't answer. Try again in a moment."),
        _ => (_error ?? "The update check failed.", "Check your internet connection, then try again."),
    };

    /// <summary>Not for a copy that can't update, nor while another install is under way: trying again can't help.</summary>
    private bool InstallRetryable => _installStatus is not ("notInstalled" or "busy");

    private bool WarnsOfDownloads => _activeDownloads > 0 || _activeUnknown;

    private void Remember(UpdateCheckResult check)
    {
        Check = check;
        InstalledVersion = check.InstalledVersion ?? InstalledVersion;
        AvailableVersion = check.AvailableVersion ?? AvailableVersion;
        Size = check.Size is > 0 ? check.Size : Size;
    }

    private void Fail(UpdateStep step, string? error, string? status, UpdateStep? onlyFrom = null)
    {
        if (onlyFrom is { } from && Step != from) return;
        Step = step;
        _error = string.IsNullOrWhiteSpace(error) ? null : error.Trim();
        if (step == UpdateStep.CheckFailed)
        {
            _checkStatus = status;
            _checkFailure = Classify(status, _error);
        }
        if (step == UpdateStep.InstallFailed) _installStatus = status;
    }

    private string Name => AvailableVersion is { } version && Changelog.Normal(version) is not null
        ? $"Inbound {Changelog.Display(version)}"
        : "the update";

    /// <summary><see cref="Name"/> at the start of a sentence.</summary>
    private string NameFirst => char.ToUpperInvariant(Name[0]) + Name[1..];

    private string SizeText => Size is > 0 and var size ? $" ({UiFormat.Bytes(size)})" : string.Empty;

    private string? DownloadsWarning()
    {
        if (!WarnsOfDownloads) return null;
        var running = _activeDownloads switch
        {
            0 => "Downloads are running.",
            1 => "1 download is running.",
            var count => $"{count} downloads are running.",
        };
        var after = (_activeDownloads == 1, ResumesAfterRestart) switch
        {
            (true, true) => "Installing stops it, and it goes on when Inbound starts again.",
            (true, false) => "Installing stops it, and it stays stopped until you resume it.",
            (false, true) => "Installing stops them, and they go on when Inbound starts again.",
            (false, false) => "Installing stops them, and they stay stopped until you resume them.",
        };
        return $"{running} {after}";
    }

    public UpdateDialogView View()
    {
        switch (Step)
        {
            case UpdateStep.Checking:
                return new("Check for updates", UpdateTone.Plain, "Checking for a newer version…", null, null, false, UpdateBusy.Ring, null, null,
                    null, "Cancel", false, true);
            case UpdateStep.UpToDate:
                var installed = InstalledVersion is { } mine && Changelog.Normal(mine) is not null ? Changelog.Display(mine) : null;
                return new("You're up to date", UpdateTone.Success,
                    installed is null ? "You have the latest version of Inbound." : $"Inbound {installed} is the latest version.",
                    null, null, false, UpdateBusy.None, null, null, null, "Close", false, true);
            case UpdateStep.NotInstalled:
                return new("Updates aren't available here", UpdateTone.Plain,
                    "This copy of Inbound wasn't installed with Setup, so it can't update itself.",
                    "To get updates, install Inbound with Setup.", null, false, UpdateBusy.None, null, null, null, "Close", false, true);
            case UpdateStep.CheckFailed:
                var (failed, nextStep) = CheckFailureText();
                return new("Couldn't check for updates", UpdateTone.Error, failed, nextStep, null, false, UpdateBusy.None, null, null,
                    Retryable ? "Try again" : null, "Close", Retryable, true);
            case UpdateStep.Available:
                var have = InstalledVersion is { } current && Changelog.Normal(current) is not null
                    ? $" You have {Changelog.Display(current)}."
                    : string.Empty;
                return new("Update available", UpdateTone.Plain, $"{NameFirst} is available{SizeText}.{have}", null, null, true, UpdateBusy.None, null, null,
                    "Download", "Later", true, true);
            case UpdateStep.Downloading:
                if (_verifying)
                    return new("Downloading the update", UpdateTone.Plain, $"Checking {Name}…", "Making sure the download is complete and signed.", null, true,
                        UpdateBusy.Bar, null, null, null, "Cancel", false, true);
                double? percent = _total is > 0 and var total ? Math.Clamp(_received * 100d / total, 0, 100) : null;
                var progress = _total is > 0 and var all
                    ? $"{UiFormat.Bytes(Math.Min(_received, all))} of {UiFormat.Bytes(all)}"
                    : _received > 0 ? $"{UiFormat.Bytes(_received)} downloaded" : null;
                return new("Downloading the update", UpdateTone.Plain, $"Downloading {Name}…",
                    "If you cancel, the download still finishes in the background, so the next try is quick, and the update stays on offer.", null, true,
                    UpdateBusy.Bar, percent, progress, null, "Cancel", false, true);
            case UpdateStep.DownloadFailed:
                return new("Couldn't download the update", UpdateTone.Error, _error ?? "The download failed.",
                    "Nothing was changed. Try again now, or later from Help > Check for Updates.", null, true, UpdateBusy.None, null, null,
                    "Try again", "Close", true, true);
            case UpdateStep.Ready:
                var warning = DownloadsWarning();
                return new("Ready to install", UpdateTone.Plain, $"{NameFirst} is downloaded and checked.",
                    "Inbound closes for a moment, installs it and starts again.", warning, true, UpdateBusy.None, null, null,
                    warning is null ? "Install now" : "Install anyway", "Later", warning is null, true);
            case UpdateStep.Installing:
                return new("Installing the update", UpdateTone.Plain, $"Starting the install of {Name}…",
                    "This can take a minute or two.", null, false, UpdateBusy.Ring, null, null,
                    null, null, false, false);
            case UpdateStep.Restarting:
                return new("Installing the update", UpdateTone.Plain, $"{NameFirst} is being installed.",
                    "Inbound closes now and starts again by itself when it's done.", null, false, UpdateBusy.Bar, null, null,
                    null, "Close", false, true);
            default:
                return new("Couldn't install the update", UpdateTone.Error, _error ?? "The update could not be installed.",
                    $"Inbound wasn't changed. {NextTimeHint}", null, true, UpdateBusy.None, null, null,
                    InstallRetryable ? "Try again" : null, "Close", false, true);
        }
    }
}

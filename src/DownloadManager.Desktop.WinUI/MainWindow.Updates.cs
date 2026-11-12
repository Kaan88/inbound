using System.Diagnostics;
using DownloadManager.Contracts;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Automation.Peers;
using Microsoft.UI.Xaml.Controls;

namespace DownloadManager.Desktop.WinUI;

/// <summary>
/// UI.R40: Help > Check for Updates, the status bar's update link and the tray's update entries open one dialog whose
/// content changes in place (<see cref="UpdateDialogFlow"/>): checking, up to date or on offer with what's new,
/// downloading and checking, ready to install, installing, or what went wrong. It sends the same Host commands as
/// before (update.check, update.download, update.install, update.defer), so the signature check and rollback are the
/// Host's as they were. Updates are always this PC's: a server shown in the window updates itself. The web UI has no
/// update check (only the version text in its status bar, UI.R37), so there is no web side to this dialog.
/// </summary>
public sealed partial class MainWindow
{
    private bool _pendingUpdatesOpen;
    private bool _updateDialogOpen;
    private bool _updatesWaitForDialog;

    /// <summary>
    /// A download the dialog started and was cancelled from: the Host finishes it anyway, so a dialog opened again
    /// meanwhile shows it rather than asking for a second one of the same file.
    /// </summary>
    private Task<UpdateDownloadResult?>? _updateDownload;
    private UpdateCheckResult? _updateDownloadCheck;

    /// <summary>The tray's Check for Updates… and update entries (<c>--page updates</c>).</summary>
    public void RequestUpdatesDialog()
    {
        ShowFromTray();
        if (_settings is null)
        {
            _pendingUpdatesOpen = true;
            return;
        }

        _ = OpenUpdateDialogAsync(fromTray: true);
    }

    /// <summary>After the first snapshot, for a tray request that came before the window could show a dialog.</summary>
    private void OpenPendingUpdatesDialog()
    {
        if (!_pendingUpdatesOpen) return;
        _pendingUpdatesOpen = false;
        _ = OpenUpdateDialogAsync(fromTray: true);
    }

    /// <summary>
    /// A tray request that came while another dialog was open gets its turn when that dialog closes, unless that dialog
    /// closed to open the next one (Add URL's Continue): the next one goes first (review of 0.7.2, ux-7).
    /// </summary>
    private void OnDialogClosedForUpdates(XamlRoot root)
    {
        if (!ReferenceEquals(root, RootGrid.XamlRoot)) return;
        if (_dialogHandoffs > 0) return;
        DialogGuard.Closed -= OnDialogClosedForUpdates;
        _updatesWaitForDialog = false;
        // Queued: the closed dialog's popup leaves the tree after this returns.
        DispatcherQueue.TryEnqueue(() => _ = OpenUpdateDialogAsync(fromTray: true));
    }

    /// <summary>Dialogs closing to open the next one; a waiting update dialog doesn't take their turn.</summary>
    private int _dialogHandoffs;

    private void BeginDialogHandoff() => _dialogHandoffs++;

    /// <summary>The next dialog has been shown and closed, or couldn't be: a waiting update dialog may open now.</summary>
    private void EndDialogHandoff()
    {
        if (_dialogHandoffs > 0) _dialogHandoffs--;
        if (_dialogHandoffs > 0 || !_updatesWaitForDialog || RootGrid.XamlRoot is not { } root || DialogGuard.IsOpen(root)) return;
        DialogGuard.Closed -= OnDialogClosedForUpdates;
        _updatesWaitForDialog = false;
        DispatcherQueue.TryEnqueue(() => _ = OpenUpdateDialogAsync(fromTray: true));
    }

    private void MenuUpdate_Click(object sender, RoutedEventArgs e) => _ = OpenUpdateDialogAsync();

    /// <summary>Downloads of this PC that an install would pause; 0 while the window shows another server (the Host's
    /// own check then answers busy, and the dialog says so).</summary>
    private int LocalActiveDownloads() => _connection.IsRemote
        ? 0
        : _jobsById.Values.Count(job => job.State is "connecting" or "downloading" or "verifying" or "retryWaiting");

    /// <summary>The downloads an install would pause, as the Host counts them: the window's own list, or for a window
    /// showing another server, this PC's Host asked directly.</summary>
    private async Task<int> ActiveDownloadsForInstallAsync()
    {
        if (!_connection.IsRemote) return LocalActiveDownloads();
        try
        {
            return (await _local.RefreshAsync()).Status?.ActiveDownloads ?? 0;
        }
        catch (Exception ex)
        {
            UiTrace.Write($"Update dialog: this PC's downloads couldn't be counted: {ex.GetType().Name}");
            return 0;
        }
    }

    /// <param name="fromTray">The tray asked: when another dialog is open, wait for it to close instead of doing nothing
    /// (Help and the status bar can't be reached while a dialog is open).</param>
    private async Task OpenUpdateDialogAsync(bool fromTray = false)
    {
        if (_updateDialogOpen) return;
        for (var wait = 0; wait < 40 && (RootGrid.XamlRoot is null || !RootGrid.IsLoaded); wait++) await Task.Delay(250);
        if (RootGrid.XamlRoot is null) return;
        if (DialogGuard.IsOpen(RootGrid.XamlRoot) || (fromTray && _dialogHandoffs > 0))
        {
            if (fromTray && !_updatesWaitForDialog)
            {
                _updatesWaitForDialog = true;
                DialogGuard.Closed += OnDialogClosedForUpdates;
                UiTrace.Write("Update dialog waits for the open dialog to close");
            }

            return;
        }

        var flow = new UpdateDialogFlow(LocalSettings?.AutoResumeUnfinishedOnStartup ?? true);
        var theme = RootGrid.ActualTheme;
        var muted = InboundTheme.Brush("InboundSecondaryTextBrush", theme);
        var message = new TextBlock { TextWrapping = TextWrapping.Wrap, IsTextSelectionEnabled = true };
        var result = new InfoBar { IsOpen = false, IsClosable = false, Visibility = Visibility.Collapsed };
        var ring = new ProgressRing { IsActive = false, Width = 20, Height = 20, HorizontalAlignment = HorizontalAlignment.Left };
        var bar = InboundTheme.Styled(new ProgressBar { Maximum = 100 });
        // The content changes in place, so a screen reader is told each new step (the old flow's separate dialogs were
        // read out as they opened).
        AutomationProperties.SetLiveSetting(message, AutomationLiveSetting.Assertive);
        AutomationProperties.SetLiveSetting(result, AutomationLiveSetting.Assertive);
        AutomationProperties.SetName(bar, "Download progress");
        var barText = new TextBlock { Foreground = muted, FontSize = 12 };
        var barRow = new StackPanel { Spacing = 4, Children = { bar, barText } };
        var detail = new TextBlock { Foreground = muted, TextWrapping = TextWrapping.Wrap };
        var warning = new InfoBar { Severity = InfoBarSeverity.Warning, IsOpen = false, IsClosable = false, Visibility = Visibility.Collapsed };
        AutomationProperties.SetLiveSetting(warning, AutomationLiveSetting.Assertive);
        var notes = new StackPanel { Spacing = 12 };
        var body = new StackPanel { Spacing = 12, MinWidth = 380, Children = { message, result, ring, barRow, detail, warning, notes } };
        var notesFor = (string?)null;

        var dialog = new ContentDialog
        {
            XamlRoot = RootGrid.XamlRoot,
            RequestedTheme = theme,
            Content = body,
        };

        var accentStyle = InboundUi.Style("AccentButtonStyle");
        var plainStyle = InboundUi.Style("DefaultButtonStyle");
        var closed = false;
        UpdateStep? shownStep = null;
        string? shownWarning = null;
        var leaveCommand = UpdateCommand.None;
        // While held, the Host's separate update progress window stays shut: the dialog shows that progress.
        ActivationSignal? holdsProgress = null;

        void ReleaseProgressWindow()
        {
            holdsProgress?.Dispose();
            holdsProgress = null;
        }

        void Render()
        {
            if (closed) return;
            var view = flow.View();
            dialog.Title = view.Title;
            message.Text = view.Message;
            var plain = view.Tone == UpdateTone.Plain;
            message.Visibility = plain ? Visibility.Visible : Visibility.Collapsed;
            result.Message = view.Message;
            AutomationProperties.SetName(result, view.Message);
            result.Severity = view.Tone == UpdateTone.Success ? InfoBarSeverity.Success : InfoBarSeverity.Error;
            result.IsOpen = !plain;
            result.Visibility = plain ? Visibility.Collapsed : Visibility.Visible;

            ring.IsActive = view.Busy == UpdateBusy.Ring;
            AutomationProperties.SetName(ring, view.Title);
            ring.Visibility = view.Busy == UpdateBusy.Ring ? Visibility.Visible : Visibility.Collapsed;
            barRow.Visibility = view.Busy == UpdateBusy.Bar ? Visibility.Visible : Visibility.Collapsed;
            bar.IsIndeterminate = view.Busy == UpdateBusy.Bar && view.Percent is null;
            bar.Value = view.Percent ?? 0;
            barText.Text = view.ProgressText ?? string.Empty;
            barText.Visibility = view.ProgressText is null ? Visibility.Collapsed : Visibility.Visible;

            detail.Text = view.Detail ?? string.Empty;
            detail.Visibility = view.Detail is null ? Visibility.Collapsed : Visibility.Visible;
            warning.Message = view.Warning ?? string.Empty;
            AutomationProperties.SetName(warning, view.Warning ?? string.Empty);
            warning.IsOpen = view.Warning is not null;
            warning.Visibility = view.Warning is null ? Visibility.Collapsed : Visibility.Visible;

            // UPD.N1's box: built once for the version on offer, kept while it is on offer.
            if (view.Notes && flow.Check is { } check && flow.AvailableVersion is { } version)
            {
                if (notesFor != version)
                {
                    notesFor = version;
                    notes.Children.Clear();
                    if (UpdateNotesBox(check) is { } box) notes.Children.Add(box);
                    notes.Children.Add(ReleaseNotesLink(version));
                }

                notes.Visibility = Visibility.Visible;
            }
            else
            {
                notes.Visibility = Visibility.Collapsed;
            }

            dialog.PrimaryButtonText = view.Primary ?? string.Empty;
            dialog.CloseButtonText = view.Close ?? string.Empty;
            var primaryLeads = view.Primary is not null && view.PrimaryIsDefault;
            // The default button is the accent one. Cancel while it works is no step forward, so then there is none;
            // Later leads only where it is the safe answer (downloads would be paused), as Wait did before.
            var closeLeads = !primaryLeads && view.Close is not null && !(view.Primary is null && view.Busy != UpdateBusy.None);
            dialog.DefaultButton = primaryLeads ? ContentDialogButton.Primary : closeLeads ? ContentDialogButton.Close : ContentDialogButton.None;
            // A dialog that is already showing keeps the accent where DefaultButton was when it opened, so the accent
            // follows the step through the buttons' styles.
            if (accentStyle is not null && plainStyle is not null)
            {
                dialog.PrimaryButtonStyle = primaryLeads ? accentStyle : plainStyle;
                dialog.CloseButtonStyle = closeLeads ? accentStyle : plainStyle;
            }

            // A new step puts the keyboard focus on its default button, so Enter answers what is shown now rather than
            // pressing whichever button had the focus before (Later, say, after the check found an update).
            var newStep = flow.Step != shownStep;
            var newWarning = view.Warning is not null && view.Warning != shownWarning;
            shownWarning = view.Warning;
            if (newStep)
            {
                shownStep = flow.Step;
                var name = primaryLeads ? "PrimaryButton" : closeLeads ? "CloseButton" : null;
                if (name is not null)
                    DispatcherQueue.TryEnqueue(() =>
                    {
                        if (!closed) DialogArrowKeys.FooterButton(dialog, name)?.Focus(FocusState.Programmatic);
                    });
            }

            // After the layout pass, so the reader finds the new words.
            if (newStep || newWarning)
                DispatcherQueue.TryEnqueue(() =>
                {
                    if (closed) return;
                    if (newStep) LiveRegion.Announce(plain ? message : result);
                    if (newWarning) LiveRegion.Announce(warning);
                });
        }

        async Task RunAsync(UpdateCommand command)
        {
            switch (command)
            {
                case UpdateCommand.Check:
                    try
                    {
                        flow.Checked((await _local.CheckForUpdatesAsync()).GetPayload<UpdateCheckResult>());
                    }
                    catch (Exception ex)
                    {
                        flow.CheckThrew(ex.Message);
                    }

                    break;
                case UpdateCommand.Download:
                    if (_updateDownload is not { IsCompleted: false })
                    {
                        _updateDownloadCheck = flow.Check;
                        _updateDownload = StartUpdateDownloadAsync();
                    }

                    var download = _updateDownload;
                    Render();
                    try
                    {
                        var downloaded = await download;
                        flow.Downloaded(downloaded, await ActiveDownloadsForInstallAsync());
                    }
                    catch (Exception ex)
                    {
                        flow.DownloadThrew(ex.Message);
                    }

                    break;
                case UpdateCommand.Install:
                case UpdateCommand.InstallAnyway:
                    // The install closes this window (and the dialog with it) while Inbound restarts; the Host's
                    // progress window, which it opens as the install starts, is what stays on screen then.
                    ReleaseProgressWindow();
                    Render();
                    try
                    {
                        flow.Installed((await _local.InstallUpdateAsync(force: command == UpdateCommand.InstallAnyway))
                            .GetPayload<UpdateInstallResult>());
                    }
                    catch (Exception ex)
                    {
                        flow.InstallThrew(ex.Message);
                    }

                    break;
                default:
                    return;
            }

            Render();
        }

        void OnProgress(UpdateProgressDto progress) => DispatcherQueue.TryEnqueue(() =>
        {
            if (closed || flow.Step != UpdateStep.Downloading) return;
            flow.Progress(progress);
            Render();
        });

        var counting = false;
        void OnJob(JobDto job, string kind) => DispatcherQueue.TryEnqueue(async () =>
        {
            if (closed || flow.Step != UpdateStep.Ready) return;
            if (!_connection.IsRemote)
            {
                flow.ActiveDownloads(LocalActiveDownloads());
                Render();
                return;
            }

            // Another server is shown: this PC's Host is asked, one question at a time and not for every progress tick.
            if (counting || kind == "progress") return;
            counting = true;
            try
            {
                var count = await ActiveDownloadsForInstallAsync();
                if (closed || flow.Step != UpdateStep.Ready) return;
                flow.ActiveDownloads(count);
                Render();
            }
            finally
            {
                counting = false;
            }
        });

        dialog.PrimaryButtonClick += (sender, clicked) =>
        {
            // The dialog stays: its content moves to the next step.
            clicked.Cancel = true;
            _ = RunAsync(flow.Primary());
            Render();
        };
        dialog.Closing += (_, args) =>
        {
            if (args.Result == ContentDialogResult.Primary) return;
            leaveCommand = flow.Leave(out var left);
            if (!left) args.Cancel = true;
        };

        try
        {
            holdsProgress = ActivationSignal.Create(App.UpdateDialogName(), out _);
        }
        catch (Exception ex)
        {
            UiTrace.Write($"The update dialog's mark couldn't be set: {ex.GetType().Name}");
        }

        _updateDialogOpen = true;
        _local.UpdateProgressChanged += OnProgress;
        _local.JobChanged += OnJob;
        try
        {
            if (_updateDownload is { IsCompleted: false } running && _updateDownloadCheck is { } earlier)
            {
                flow.ResumeDownloading(earlier);
                Render();
                _ = AwaitEarlierDownloadAsync(running);
            }
            else
            {
                _ = RunAsync(flow.Start());
                Render();
            }

            // Refused because another dialog is open: nothing was asked, so nothing is put off either.
            if (await DialogGuard.ShowAsync(dialog) is null) leaveCommand = UpdateCommand.None;
        }
        finally
        {
            // The dialog is gone, so it may open again at once, even while the answer below is on its way.
            closed = true;
            _updateDialogOpen = false;
            _local.UpdateProgressChanged -= OnProgress;
            _local.JobChanged -= OnJob;
            ReleaseProgressWindow();
        }

        // Cancelled while its download goes on: the Host opens its progress window only once, when the download begins,
        // and the dialog kept it shut, so it is opened now, as it would have been without the dialog.
        if (_updateDownload is { IsCompleted: false }) OpenUpdateProgressWindow();

        if (leaveCommand == UpdateCommand.Defer && flow.AvailableVersion is { } version)
        {
            try { await _local.DeferUpdateAsync(version); }
            catch (Exception ex) { UiTrace.Write($"Update defer failed: {ex.GetType().Name}"); }
        }

        async Task AwaitEarlierDownloadAsync(Task<UpdateDownloadResult?> earlierDownload)
        {
            try
            {
                var downloaded = await earlierDownload;
                flow.Downloaded(downloaded, await ActiveDownloadsForInstallAsync());
            }
            catch (Exception ex)
            {
                flow.DownloadThrew(ex.Message);
            }

            Render();
        }
    }

    /// <summary>The Host's update progress window (<c>--page update-progress</c>), started as the Host starts it. One
    /// already open is shown instead of a second.</summary>
    private static void OpenUpdateProgressWindow()
    {
        try
        {
            if (Environment.ProcessPath is not { } exe) return;
            var start = new ProcessStartInfo(exe) { UseShellExecute = false };
            start.ArgumentList.Add("--page");
            start.ArgumentList.Add("update-progress");
            Process.Start(start)?.Dispose();
        }
        catch (Exception ex)
        {
            UiTrace.Write($"The update progress window couldn't be opened: {ex.GetType().Name}");
        }
    }

    /// <summary>
    /// update.download, on a connection of its own: the Host answers one connection's requests in turn, so on the
    /// window's own connection every other request (a pause, putting the update off) would wait for the whole download.
    /// The progress still comes as the window connection's update.progress events. A dialog only starts one while no
    /// earlier one is still running.
    /// </summary>
    private static async Task<UpdateDownloadResult?> StartUpdateDownloadAsync()
        => (await ServerConnector.SendOnceAsync(ServerTarget.Local, IpcMessage.Create(MessageTypes.UpdateDownload, new { }),
            UpdateDownloadTimeout)).GetPayload<UpdateDownloadResult>();

    /// <summary>The longest wait for the installer (the signed size is at most 1 GB).</summary>
    private static readonly TimeSpan UpdateDownloadTimeout = TimeSpan.FromMinutes(30);
}

using Android.Content;
using Android.Views;
using DownloadManager.Contracts;
using DownloadManager.Mobile;
using DownloadManager.Mobile.Links;
using Google.Android.Material.Dialog;
using Inbound.App.Screens;

namespace Inbound.App;

public sealed partial class MainActivity
{
    private Action? _afterLinkPage;

    internal void ShowRefreshLink(JobRow row)
    {
        if (_model is null || _linkHostStamp is not { } stamp || !SameLinkHost(stamp)) return;
        if (row.Job.IsRemote) { ShowMessage("This download’s provider refreshes its address."); return; }
        RefreshLinkSheet.Show(this, row.Name, row.Job.ReceivedBytes, row.Job.TotalBytes,
            row.Job.Failure?.Kind.Equals("expiredUrl", StringComparison.OrdinalIgnoreCase) == true,
            () =>
            {
                if (_nav?.Page is not { } page) return;
                _afterLinkPage = () =>
                {
                    if (_nav is null || _model is null || !SameLinkHost(stamp)) return;
                    var initial = BrowserScreen.Address(row.Job.PageUrl);
                    // A hidden provider URL cannot be opened; the address box stays empty for the user to choose a page.
                    initial ??= !row.Job.AddressHidden ? BrowserScreen.Address(row.Job.Url) : null;
                    BeginLinkPage();
                    _browser = new BrowserScreen(this, initial, capture => _ = RefreshCapturedAsync(row, capture, stamp), refresh: true);
                    _nav.Open(_browser.Root, null, null, OnPageClosed, "Refresh download link",
                        () => _browser?.Back() == true, () => _browser?.HandlesBack == true);
                };
                _nav.Close(page);
            }, async text =>
            {
                if (!SameLinkHost(stamp) || BrowserScreen.Address(text.Trim()) is not { } url) return false;
                var result = await _model.RefreshUrlAsync(new JobRefreshUrlRequest { JobId = row.Id, Url = url }, _linkLifetime.Token);
                if (!SameLinkHost(stamp)) return false;
                if (!result.Ok) { ShowMessage("The Host could not refresh this link. Stop an active download before trying again."); return false; }
                ShowMessage("Link refreshed. The Host will check whether it can resume."); return true;
            });
    }

    private async Task RefreshCapturedAsync(JobRow row, CapturedDownload capture, string stamp)
    {
        if (_catchBusy || _model is null || !SameLinkHost(stamp)) return;
        if (_model.Hello?.Capabilities.Contains("browserRefresh") != true)
        { ShowMessage("Update the selected Host to refresh a link with browser sign-in. You can still paste a direct address."); return; }
        var generation = _linkPageGeneration;
        if (!SameLinkPage(stamp, generation)) return;
        var cancellationToken = _linkPageLifetime!.Token;
        _catchBusy = true;
        try
        {
            if (!await ConfirmCookiesAsync([capture], stamp, cancellationToken)) return;
            var answer = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            var dialog = new MaterialAlertDialogBuilder(this).SetTitle("Refresh " + row.Name + "?")!
                .SetMessage("Use the caught address for this existing download on " + HostName + "? The Host checks whether the saved part still matches the file before resuming.")!
                .SetPositiveButton("Refresh link", (_, _) => answer.TrySetResult(true))!
                .SetNegativeButton("Cancel", (_, _) => answer.TrySetResult(false))!.Create();
            dialog.CancelEvent += (_, _) => answer.TrySetResult(false);
            using var registration = cancellationToken.Register(() => { answer.TrySetResult(false); RunOnUiThread(() => { if (dialog.Handle != IntPtr.Zero) dialog.Dismiss(); }); });
            dialog.Show();
            var allowed = await answer.Task;
            dialog.Dispose();
            if (!allowed || !SameLinkPage(stamp, generation)) return;
            var result = await _model.RefreshUrlAsync(capture.Refresh(row.Id, Capabilities.IsRemote, true), cancellationToken);
            if (!SameLinkPage(stamp, generation)) return;
            ShowMessage(result.Ok ? "Link refreshed. The Host will check whether it can resume." : "The Host could not refresh this link. Stop an active download before trying again.");
        }
        catch (OperationCanceledException) { }
        catch (Exception) { if (!_linkLifetime.IsCancellationRequested) ShowMessage("Could not refresh this download. Catch the link again to retry."); }
        finally { _catchBusy = false; }
    }
}

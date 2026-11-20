using Android.Content;
using Android.Views;
using DownloadManager.Mobile.Links;
using DownloadManager.Mobile.Remote;
using Google.Android.Material.Dialog;
using Inbound.App.Screens;

namespace Inbound.App;

public sealed partial class MainActivity
{
    private BrowserScreen? _browser;
    private CancellationTokenSource? _linkPageLifetime;
    private long _linkPageGeneration;
    private HostDirectory? _linkHosts;
    private volatile bool _linkTargetChanged;
    private void OnLinkHostsChanged()
    {
        if (_linkHosts?.Current.ConnectionStamp != _linkHostStamp) _linkTargetChanged = true;
    }
    private void BeginLinkPage()
    {
        _linkPageLifetime?.Cancel();
        _linkPageLifetime = CancellationTokenSource.CreateLinkedTokenSource(_linkLifetime.Token);
        _linkPageGeneration++;
    }
    private bool SameLinkPage(string stamp, long generation) => SameLinkHost(stamp)
        && _browser is not null && generation == _linkPageGeneration && _linkPageLifetime?.IsCancellationRequested == false;
    private readonly CancellationTokenSource _linkLifetime = new();
    private readonly HashSet<string> _acceptedCaptures = new(StringComparer.Ordinal);
    private IReadOnlyList<string>? _pendingSharedLinks;
    private bool _snapshotReady, _catchBusy;
    private string? _linkHostStamp;

    internal string LinkDestination => Capabilities.IsRemote ? HostName + " · " + _model?.DefaultDownloadDirectory : "This phone · Download/";

    internal bool HasRemoteFilesSource =>
        FindViewById<Google.Android.Material.BottomNavigation.BottomNavigationView>(Resource.Id.nav)
            ?.Menu?.FindItem(Resource.Id.nav_remote_files)?.IsVisible == true;

    internal void OpenRemoteFilesSource()
    {
        if (HasRemoteFilesSource)
            FindViewById<Google.Android.Material.BottomNavigation.BottomNavigationView>(Resource.Id.nav)!
                .SelectedItemId = Resource.Id.nav_remote_files;
    }

    internal void OpenBrowser(string? url = null)
    {
        if (_nav is null || _model is null || _nav.PageOpen) return;
        BeginLinkPage();
        _browser = new BrowserScreen(this, url, capture => _ = ShowCaughtAsync(capture));
        _nav.Open(_browser.Root, null, null, OnPageClosed, "Browser",
            () => _browser?.Back() == true, () => _browser?.HandlesBack == true);
    }

    private void ReceiveShare(Intent? intent, bool redelivered)
    {
        if (intent?.Action != Intent.ActionSend) return;
        var type = intent.Type;
        // Read text only; no attachments, streams, ClipData URIs or incoming grants.
        var text = !redelivered && type?.StartsWith("text/", StringComparison.OrdinalIgnoreCase) == true
            ? intent.GetCharSequenceExtra(Intent.ExtraText)?.ToString() : null;
        intent.RemoveExtra(Intent.ExtraText); intent.RemoveExtra(Intent.ExtraStream);
        intent.ClipData = null;
        intent.SetAction(Intent.ActionMain);
        if (text is null) return;
        var links = SharedLinks.Extract(text);
        if (links.Count == 0) { ShowMessage("No HTTP or HTTPS link found in the shared text."); return; }
        _pendingSharedLinks = links;
        PresentShare();
    }
    private void PresentShare()
    {
        if (!_snapshotReady || _firstRun is not null || _nav?.PageOpen == true || _pendingSharedLinks is null || IsFinishing || IsDestroyed) return;
        var links = _pendingSharedLinks; _pendingSharedLinks = null;
        if (links.Count > 1) { OpenNewDownload(string.Join('\n', links), transient: true); return; }
        var url = links[0];
        // Sharing a page is not evidence that it is a file. Neither choice starts a job without another confirmation.
        new MaterialAlertDialogBuilder(this).SetTitle("Shared link")!
            .SetMessage(new Uri(url).IdnHost + "\nOpen the page to sign in or catch a download, or add this address as a file.")!
            .SetPositiveButton("Open page", (_, _) => OpenBrowser(url))!
            .SetNeutralButton("Add file", (_, _) => OpenNewDownload(url, transient: true))!
            .SetNegativeButton("Cancel", (IDialogInterfaceOnClickListener?)null)!.Show();
    }

    private bool SameLinkHost(string stamp) => !_linkTargetChanged && !_linkLifetime.IsCancellationRequested && _model is { Disposed: false }
        && !IsFinishing && !IsDestroyed && _linkHostStamp == stamp
        && Hosts.HostChoice.Hosts(this).Current.ConnectionStamp == stamp;

    private async Task<bool> ConfirmCookiesAsync(IReadOnlyList<CapturedDownload> captures, string stamp, CancellationToken cancellationToken)
    {
        if (!SameLinkHost(stamp)) return false;
        if (!Capabilities.IsRemote || !captures.Any(c => c.HasCookies)) return true;
        var host = Hosts.HostChoice.Hosts(this).Current;
        var origin = host.AddressUri?.GetLeftPart(UriPartial.Authority) ?? "Unknown server";
        var message = $"Send sign-in cookies for {captures.Count} selected download(s) to {host.Name} ({origin})?\n\n"
            + "These cookies can grant access to your signed-in account. The server will use them to download and may keep them to resume the file.";
        if (host.AddressUri?.Scheme == "http") message += "\n\nThis connection uses HTTP. Cookies are sent without encryption; HTTPS is recommended.";
        var answer = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var dialog = new MaterialAlertDialogBuilder(this).SetTitle("Send sign-in to this server?")!
            .SetMessage(message)!.SetPositiveButton("Send for these files", (_, _) => answer.TrySetResult(true))!
            .SetNegativeButton("Cancel", (_, _) => answer.TrySetResult(false))!.Create();
        dialog.CancelEvent += (_, _) => answer.TrySetResult(false);
        using var registration = cancellationToken.Register(() => { answer.TrySetResult(false); RunOnUiThread(() => { if (dialog.Handle != IntPtr.Zero) dialog.Dismiss(); }); });
        dialog.Show();
        var allowed = await answer.Task;
        dialog.Dispose();
        return allowed && !cancellationToken.IsCancellationRequested && SameLinkHost(stamp);
    }

    private async Task ShowCaughtAsync(CapturedDownload capture)
    {
        if (_catchBusy || _model is null || _linkHostStamp is not { } stamp || !SameLinkHost(stamp)) return;
        if (SupportFragmentManager.FindFragmentByTag("caught_download") is not null) return;
        var generation = _linkPageGeneration;
        if (!SameLinkPage(stamp, generation)) return;
        var cancellationToken = _linkPageLifetime!.Token;
        _catchBusy = true;
        try
        {
            if (!await ConfirmCookiesAsync([capture], stamp, cancellationToken)) return;
            // Consent is required before metadata, too: the server performs that request.
            var metadata = await _model.ProbeAsync(capture.Probe(Capabilities.IsRemote, true), cancellationToken);
            if (!SameLinkPage(stamp, generation)) return;
            if (metadata.AddRefused) { ShowMessage("This address cannot be added by the selected Host."); return; }
            var name = new CapturedDownload(capture.Url, metadata.Ok ? metadata.FileName : capture.FileName).FileName;
            if (string.IsNullOrEmpty(name)) name = "File";
            var size = metadata.Ok ? metadata.TotalBytes : capture.SizeHint;
            var detail = size is >= 0 ? DownloadManager.Mobile.Fmt.Bytes(size.Value) : "Size unknown";
            if (!metadata.Ok) detail += " · File check unavailable";
            var privacy = capture.HasCookies ? Capabilities.IsRemote
                ? "Sign-in cookies will be sent to " + HostName + " for this download."
                : "Cookies stay on this phone for this file and resume."
                : "No sign-in cookies captured. Form POST and browser-only downloads may need your regular browser.";
            var max = Math.Max(1, _model.Settings.MaxConnectionsPerHost);
            var defaultConnections = Math.Clamp(_model.Settings.DefaultConnections, 1, max);
            if ((!capture.HasCookies || Capabilities.IsRemote) && metadata.AdvertisesByteRanges == true) detail += " · Supports byte ranges";
            CaughtFolderChoice? folder = null;
            if (DownloadManager.Mobile.FolderPicker.Offered(_model))
            {
                var host = _model;
                folder = new(host.DefaultDownloadDirectory, () => SameLinkPage(stamp, generation),
                    (start, chosen) => FolderSheet.Show(this, host, start, chosen, cancellationToken));
            }
            CaughtDownloadSheet.Show(this, _model, new CaughtSheetSpec(name, detail, LinkDestination, privacy, defaultConnections, max,
                LocalSignedIn: capture.HasCookies && !Capabilities.IsRemote, HasCookies: capture.HasCookies,
                Queues: _model.Hello?.Capabilities.Contains("queueIdentity") == true
                    ? _model.Settings.Queues.Where(q => q.Generation.GetValueOrDefault() != Guid.Empty)
                        .OrderByDescending(q => q.Id == _model.Settings.DefaultQueueId).ThenBy(q => q.Order).ToArray() : []), folder,
                async (connections, later, destination, queue) =>
                {
                    if (!SameLinkPage(stamp, generation)) { ShowMessage("The selected Host changed. Catch this download again."); return false; }
                    var request = capture.Add(Capabilities.IsRemote, true, destination, Math.Clamp(connections, 1, Math.Max(1, _model.Settings.MaxConnectionsPerHost)), later);
                    if (queue is not null)
                    {
                        request.StartMode = DownloadManager.Contracts.StartModes.Queue;
                        request.QueueId = queue.Id;
                        request.RequireQueue = true;
                        request.ExpectedQueueGeneration = queue.Generation;
                    }
                    if (request.FileName is null) request.FileName = name == "File" ? null : name;
                    var result = await _model.AddAsync(request, cancellationToken);
                    if (!SameLinkPage(stamp, generation)) return false;
                    if (!result.Accepted) { ShowMessage("The Host did not accept this download. Try again."); return false; }
                    ShowMessage("Download added to " + HostName); return true;
                });
        }
        catch (OperationCanceledException) { }
        catch (Exception) { if (!_linkLifetime.IsCancellationRequested) ShowMessage("Could not prepare this download. Try catching it again."); }
        finally { _catchBusy = false; }
    }

    internal async Task<bool> AddPageLinksAsync(IReadOnlyList<CapturedDownload> captures, CancellationToken selectionCancellation)
    {
        if (_model is null || _linkHostStamp is not { } stamp || !SameLinkHost(stamp)) return false;
        var generation = _linkPageGeneration;
        if (!SameLinkPage(stamp, generation)) return false;
        using var lifetime = CancellationTokenSource.CreateLinkedTokenSource(selectionCancellation, _linkPageLifetime!.Token);
        var cancellationToken = lifetime.Token;
        var pending = captures.Where(c => !_acceptedCaptures.Contains(c.RequestId)).ToArray();
        if (!await ConfirmCookiesAsync(pending, stamp, cancellationToken)) return false;
        var accepted = 0;
        foreach (var capture in pending)
        {
            if (cancellationToken.IsCancellationRequested || !SameLinkPage(stamp, generation)) return false;
            var max = Math.Max(1, _model.Settings.MaxConnectionsPerHost);
            var result = await _model.AddAsync(capture.Add(Capabilities.IsRemote, true, null,
                Math.Clamp(_model.Settings.DefaultConnections, 1, max), false), cancellationToken);
            if (!result.Accepted) { ShowMessage($"{accepted} added. A link was refused; retry to add the remaining links."); return false; }
            _acceptedCaptures.Add(capture.RequestId); accepted++;
        }
        if (cancellationToken.IsCancellationRequested || !SameLinkPage(stamp, generation)) return false;
        ShowMessage("Selected links added to " + HostName); return true;
    }
}

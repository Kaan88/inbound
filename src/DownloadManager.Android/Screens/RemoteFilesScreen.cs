using Android.Graphics;
using Android.Views;
using Android.Widget;
using Android.Text;
using DownloadManager.Contracts;
using DownloadManager.Mobile;
using Google.Android.Material.Button;
using Google.Android.Material.Card;
using Google.Android.Material.Dialog;
using Inbound.App.Host;
using Inbound.App.Hosts;

namespace Inbound.App.Screens;

/// <summary>
/// Remote Files view. Construct and call Start on the UI thread with an already-open,
/// capable selected Host connection. The activity owns the connection, its capability checks,
/// and the screen's lifetime; dispose this screen before changing Hosts or removing its view.
/// </summary>
internal sealed partial class RemoteFilesScreen : IDisposable
{
    private const int PageSize = 100;
    private readonly MainActivity _activity;
    private readonly RemoteFilesClient _client;
    private string _hostName; // not readonly: the test-build fixtures set them
    private readonly string _hostDisplay;
    private bool _secureHostConnection; // not readonly: the test-build fixtures (RemoteFilesScreen.Fixture.cs) set it
    private readonly string _hostStamp;
    private bool _phoneHost;
    private volatile bool _hostChanged;
    private readonly CancellationTokenSource _life = new();
    private readonly Func<bool> _safeDownloads;
    private readonly Func<bool> _deleteFiles;
    private readonly Action<bool> _showBottomNavigation;
    private bool _folderFullScreen;
    // Listing tokens of folders shown, by site and path: the Host shows one again from its copy for a minute (review P5).
    private readonly Dictionary<string, string> _knownListings = new(StringComparer.Ordinal);
    private static string ListingKey(string siteId, string? path) => siteId + "\n" + (path ?? "");
    private readonly Func<bool> _cachedFirstPage;
    /// <summary>The Host offers <see cref="SiteTransferStartRequest.RemoteCopyCapability"/> (r6, location to location).</summary>
    private readonly Func<bool> _remoteCopy;
    private readonly LinearLayout _content;
    private readonly FrameLayout _surface;
    private readonly ScrollView _scroll;
    private readonly Dictionary<string, long> _freeBytes = new(StringComparer.Ordinal);
    private SitesStatusDto? _status;
    private List<SiteDto> _sites = [];
    private string? _error;
    private int _generation;
    private bool _started, _closed, _busy, _listLoaded;
    private bool _adding;
    private string _addKind = SiteKindCatalog.WebDav;
    private bool _showTransfers, _mutation, _transfersLoaded;
    private bool _summaryRequestInFlight;
    private List<SiteTransferDto> _transfers = [];
    private EditText? _password;
    private EditText? _clientSecret;
    private AndroidX.AppCompat.App.AlertDialog? _confirm;

    public View View { get; }

    public RemoteFilesScreen(MainActivity activity, IHostConnection connection, string? connectionStamp, bool secureHostConnection,
        Func<bool> safeDownloads, Func<bool> deleteFiles, Func<bool> cachedFirstPage, Func<bool> remoteCopy, Action<bool> showBottomNavigation)
    {
        _activity = activity;
        _cachedFirstPage = cachedFirstPage;
        _remoteCopy = remoteCopy;
        _client = new RemoteFilesClient(connection);
        _safeDownloads = safeDownloads;
        _deleteFiles = deleteFiles;
        _showBottomNavigation = showBottomNavigation;
        _hostName = activity.HostName;
        _secureHostConnection = secureHostConnection;
        var current = HostChoice.Hosts(activity).Current;
        _hostStamp = connectionStamp ?? "";
        _hostDisplay = current.IsPhone ? _hostName : _hostName + " (" + (current.AddressUri?.GetLeftPart(UriPartial.Authority) ?? "unknown address") + ")";
        _phoneHost = current.IsPhone && (connection is InProcessHostConnection or LazyInProcessConnection);
        if (current.ConnectionStamp != _hostStamp || current.IsPhone != _phoneHost || current.Name != _hostName)
            _hostChanged = true;
        HostChoice.Hosts(activity).Changed += HostChanged;
        _scroll = new ScrollView(activity) { FillViewport = true };
        _content = new LinearLayout(activity) { Orientation = Orientation.Vertical };
        int padding = Motion.Dp(activity, 16);
        _content.SetPadding(padding, padding, padding, padding);
        _scroll.AddView(_content);
        _surface = new FrameLayout(activity);
        ResetChrome();
        View = _surface;
    }

    public void Start()
    {
        if (_closed || _started) return;
        _started = true;
#if INBOUND_TEST_BUILD
        if (FixtureMode is not null) { StartFixture(); return; }
#endif
        _ = LoadLocationsAsync();
        StartObserving();
    }

    // The 3 s look at location transfers, stopped while the app is hidden so it wakes nothing (Fable review, 2026-11-22).
    private CancellationTokenSource? _observing;
    private bool _appVisible = true;

    private void StartObserving()
    {
        if (_closed || !_appVisible || _observing is not null) return;
        _observing = CancellationTokenSource.CreateLinkedTokenSource(_life.Token);
        _ = ObserveLocationTransfersAsync(_observing.Token);
    }

    /// <summary>The app went to the background (false) or came back (true).</summary>
    public void SetVisible(bool visible)
    {
        if (_closed || _appVisible == visible) return;
        _appVisible = visible;
        if (visible) { if (_started) StartObserving(); }
        else if (_observing is { } observing) { _observing = null; observing.Cancel(); observing.Dispose(); }
    }

    /// <summary>Returns true when Back was consumed within the screen.</summary>
    public bool HandleBack()
    {
        if (_closed) return false;
        if (_mutation) return true;
        if (_showTransfers)
        {
            CloseTransfers();
            return true;
        }
        if (_adding)
        {
            CloseForm();
            return true;
        }
        if (_inFolder) return FolderBack();
        return false;
    }

    public void Dispose()
    {
        if (_closed) return;
        _closed = true;
        SetFolderFullScreen(false);
        _generation++;
        HostChoice.Hosts(_activity).Changed -= HostChanged;
        CloseCloudSignInUi();
        ClearPassword();
        foreach (var pane in _panes) { pane.Request?.Cancel(); pane.Counts?.Cancel(); }
        _sheet?.Dismiss();
        _confirm?.Dismiss();
        _confirm = null;
        _life.Cancel();
        _life.Dispose();
    }

    private bool SameHost() => !_closed && !_hostChanged && !_activity.IsFinishing && !_activity.IsDestroyed
        && HostChoice.Hosts(_activity).Current.ConnectionStamp == _hostStamp;

    private void HostChanged()
    {
        if (HostChoice.Hosts(_activity).Current.ConnectionStamp == _hostStamp) return;
        _hostChanged = true;
        _activity.RunOnUiThread(() =>
        {
            if (_closed) return;
            CancelCurrent();
            _life.Cancel();
            CloseCloudSignInUi();
            ClearPassword();
            _confirm?.Dismiss();
            _confirm = null;
            _sheet?.Dismiss();
            _inFolder = false;
            SetFolderFullScreen(false);
            ResetChrome();
            _content.RemoveAllViews();
            Heading("Remote Files");
            AddText("The selected Host changed.", 16, Resource.Attribute.colorOnSurfaceVariant);
        });
    }

    // Invalidate pending answers without disposing the caller's connection. A Host may finish
    // a request after Back; its answer must never replace the newer view.
    private void CancelCurrent()
    {
        _generation++;
        _busy = false;
        _openTransfersPending = false;
    }

    private async Task LoadLocationsAsync()
    {
        if (_closed || _busy) return;
        int generation = ++_generation;
        _busy = true; _error = null; _listLoaded = false;
        Render();
        try
        {
            var status = await _client.GetStatusAsync(_life.Token);
            var list = status.Enabled ? await _client.ListAsync(_life.Token) : new SitesListDto();
            if (!SameHost() || generation != _generation) return;
            _status = status; _sites = list.Sites; _listLoaded = true;
            _ = RefreshLocationTransfersAsync(generation);
            _ = FetchSftpFreeSpaceAsync(generation);
        }
        catch (OperationCanceledException) when (_life.IsCancellationRequested) { return; }
        catch (Exception) { if (!_closed && generation == _generation) _error = LoadErrorText; }
        finally
        {
            if (!_closed && generation == _generation) { _busy = false; Render(); ApplyPendingShow(); if (_openTransfersPending) OpenTransfers(); } // (Fable L2)
        }
    }

    private void Render()
    {
        if (_closed) return;
        bool folder = !_showTransfers && !_adding && _inFolder;
        // Transfers draws its own bar; a folder and the add/edit forms take the whole screen too.
        SetFolderFullScreen(folder || _showTransfers || _adding);
        if (!folder && !_showTransfers) ResetChrome();
        _content.RemoveAllViews();
        if (_showTransfers) RenderTransfers();
        else if (_adding) RenderAddLocation();
        else if (!_inFolder) RenderLocations(); else RenderFolder();
    }

    private void ResetChrome()
    {
        _surface.RemoveAllViews();
        if (_scroll.Parent is ViewGroup parent) parent.RemoveView(_scroll);
        if (_content.Parent != _scroll)
        {
            if (_content.Parent is ViewGroup previous) previous.RemoveView(_content);
            _scroll.AddView(_content);
        }
        _surface.AddView(_scroll, new FrameLayout.LayoutParams(ViewGroup.LayoutParams.MatchParent, ViewGroup.LayoutParams.MatchParent));
        _content.SetPadding(Motion.Dp(_activity, 16), Motion.Dp(_activity, 16), Motion.Dp(_activity, 16), Motion.Dp(_activity, 16));
    }

    private void SetFolderFullScreen(bool fullScreen)
    {
        if (_folderFullScreen == fullScreen) return;
        _folderFullScreen = fullScreen;
        _showBottomNavigation(!fullScreen);
        _surface.SetPadding(0, 0, 0, 0);
        InsetsListener.Pad(_surface, top: true, bottom: fullScreen);
    }

    private async Task RefreshLocationTransfersAsync(int generation)
    {
        if (_status?.Enabled != true || _summaryRequestInFlight || !SameHost()) return;
        _summaryRequestInFlight = true;
        try
        {
            var result = await _client.ListTransfersAsync(_life.Token);
            if (!SameHost() || generation != _generation || _inFolder || _adding || _showTransfers) return;
            _transfers = result.Transfers;
            _transfersLoaded = true;
            UpdateTransferSummary();
        }
        catch (Exception) { /* A missing summary must not block saved locations. */ }
        finally { _summaryRequestInFlight = false; }
    }

    private async Task ObserveLocationTransfersAsync(CancellationToken token)
    {
        try
        {
            using var timer = new PeriodicTimer(TimeSpan.FromSeconds(3));
            while (await timer.WaitForNextTickAsync(token))
            {
                if (_showTransfers)
                {
                    // Transfers screen: look again only while something is running, so an idle screen wakes nothing.
                    if (!_transferLoading && _surface.IsShown && _transfers.Any(RemoteTransferText.IsActive)) await LoadTransfersAsync(true);
                    continue;
                }
                if (_busy || _adding || !_surface.IsShown) continue;
                if (_inFolder) await RefreshTransfersQuietAsync();
                else await RefreshLocationTransfersAsync(_generation);
            }
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested) { }
    }

    private void ClearPassword()
    {
        if (_password is not null) _password.Text = "";
        _password = null;
        if (_clientSecret is not null) _clientSecret.Text = "";
        _clientSecret = null;
        ClearFormSecrets();
        ClearKeyState();
    }

    private static bool SafeName(string name) =>
        !string.IsNullOrEmpty(name) && name != "." && name != ".." && !name.Contains('/') && !name.Contains('\\');

    private void Heading(string text)
    {
        var label = Label(text, 28, Resource.Attribute.colorOnSurface);
        label.SetPadding(0, 0, 0, Motion.Dp(_activity, 12));
        _content.AddView(label);
    }

    private void AddText(string text, float size, int color)
    {
        var label = Label(text, size, color);
        label.SetPadding(0, Motion.Dp(_activity, 8), 0, Motion.Dp(_activity, 8));
        _content.AddView(label);
    }

    private TextView Label(string text, float size, int color)
    {
        var label = new TextView(_activity) { Text = text, TextSize = size };
        label.SetTextColor(new Color(Motion.Attr(_activity, color)));
        return label;
    }

    private void AddButton(string text, Action action)
    {
        var button = new MaterialButton(_activity, null, Resource.Attribute.materialButtonOutlinedStyle) { Text = text };
        button.SetAllCaps(false);
        button.Click += (_, _) => action();
        _content.AddView(button);
    }

    private MaterialCardView Card(string title, string subtitle, bool clickable)
    {
        var card = new MaterialCardView(_activity)
        {
            Radius = Motion.Dp(_activity, 24), CardElevation = 0, StrokeWidth = 0,
            Clickable = clickable, Focusable = clickable
        };
        card.SetCardBackgroundColor(new Color(Motion.Attr(_activity, Resource.Attribute.colorSurfaceContainer)));
        card.LayoutParameters = new LinearLayout.LayoutParams(-1, -2) { BottomMargin = Motion.Dp(_activity, 10) };
        var body = new LinearLayout(_activity) { Orientation = Orientation.Vertical };
        int padding = Motion.Dp(_activity, 16);
        body.SetPadding(padding, padding, padding, padding);
        body.AddView(Label(title, 18, Resource.Attribute.colorOnSurface));
        body.AddView(Label(subtitle, 14, Resource.Attribute.colorOnSurfaceVariant));
        card.AddView(body);
        if (clickable) card.ContentDescription = title + ", " + subtitle + ". Open";
        return card;
    }
}

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
    private readonly string _hostName;
    private readonly string _hostDisplay;
    private readonly bool _secureHostConnection;
    private readonly string _hostStamp;
    private readonly bool _phoneHost;
    private volatile bool _hostChanged;
    private readonly CancellationTokenSource _life = new();
    private readonly Func<bool> _safeDownloads;
    private readonly Func<bool> _deleteFiles;
    private readonly Action<bool> _showBottomNavigation;
    private bool _folderFullScreen;
    private CancellationTokenSource? _browseRequest;
    private CancellationTokenSource? _countsRequest;
    // Listing tokens of folders shown, by site and path: the Host shows one again from its copy for a minute (review P5).
    private readonly Dictionary<string, string> _knownListings = new(StringComparer.Ordinal);
    private static string ListingKey(string siteId, string? path) => siteId + "\n" + (path ?? "");
    private readonly Func<bool> _cachedFirstPage;
    private bool _browseRequiresSignIn;
    private readonly LinearLayout _content;
    private readonly FrameLayout _surface;
    private readonly ScrollView _scroll;
    private readonly List<string> _parents = [];
    private readonly List<SiteItemDto> _items = [];
    private SiteDto? _site;
    private SitesStatusDto? _status;
    private List<SiteDto> _sites = [];
    private string? _path, _listing, _error;
    private int _total, _generation;
    private bool _started, _closed, _busy, _listLoaded;
    private bool _adding;
    private string _addKind = SiteKindCatalog.WebDav;
    private string _addName = "";
    private bool _showTransfers, _mutation, _startUncertain, _transfersLoaded;
    private bool _summaryRequestInFlight;
    private List<SiteTransferDto> _transfers = [];
    private EditText? _password;
    private EditText? _clientSecret;
    private AndroidX.AppCompat.App.AlertDialog? _confirm;

    public View View { get; }

    public RemoteFilesScreen(MainActivity activity, IHostConnection connection, string? connectionStamp, bool secureHostConnection,
        Func<bool> safeDownloads, Func<bool> deleteFiles, Func<bool> cachedFirstPage, Action<bool> showBottomNavigation)
    {
        _activity = activity;
        _cachedFirstPage = cachedFirstPage;
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
            CancelCurrent();
            _showTransfers = false;
            _error = null;
            Render();
            return true;
        }
        if (_adding)
        {
            ClearPassword();
            _adding = false;
            _addName = "";
            CancelCurrent();
            Render();
            return true;
        }
        if (_site is null) return false;
        if (_showPhoneFiles && !string.IsNullOrEmpty(_phonePath))
        {
            int separator = _phonePath.LastIndexOf('/');
            OpenPhoneFolder(separator < 0 ? null : _phonePath[..separator]);
            return true;
        }
        if (_showPhoneFiles) { SetPhoneTab(false); return true; }
        CancelCurrent();
        if (_parents.Count == 0)
        {
            _site = null;
            _path = null;
            _error = null;
            _items.Clear();
            Render();
            _ = RefreshLocationTransfersAsync(_generation);
        }
        else
        {
            var parent = _parents[^1];
            _parents.RemoveAt(_parents.Count - 1);
            _ = BrowseAsync(parent, false, false);
        }
        return true;
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
        _browseRequest?.Cancel();
        _countsRequest?.Cancel();
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
        _browseRequest?.Cancel();
        _countsRequest?.Cancel();
        _browseRequiresSignIn = false;
        _generation++;
        _busy = false;
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
        }
        catch (OperationCanceledException) when (_life.IsCancellationRequested) { return; }
        catch (Exception) { if (!_closed && generation == _generation) _error = "Could not load Remote Files from this Host. Check the connection and try again."; }
        finally
        {
            if (!_closed && generation == _generation) { _busy = false; Render(); }
        }
    }

    private async Task BrowseAsync(string? path, bool append, bool refresh)
    {
        if (!SameHost() || _busy || _site is null) return;
        using var browse = CancellationTokenSource.CreateLinkedTokenSource(_life.Token);
        _browseRequest = browse;
        _countsRequest?.Cancel();
        // A Host that keeps the listing answers first without folder counts, which follow (2 s less per open).
        bool lazyCounts = _cachedFirstPage();
        bool fillCounts = false;
        int generation = ++_generation;
        string siteId = _site.Id;
        int offset = append ? _items.Count : 0;
        string? listing = append ? _listing : null;
        string? known = !append && !refresh && _knownListings.TryGetValue(ListingKey(siteId, path), out var remembered) ? remembered : null;
        _browseRequiresSignIn = false;
        _busy = true; _error = null;
        if (!append)
        {
            _selectedRemoteFiles.Clear();
            _path = path; _items.Clear(); _total = 0; _listing = null;
        }
        Render();
        long started = Environment.TickCount64, replied = -1;
        try
        {
            var result = await _client.BrowseAsync(new SiteBrowseRequest
            {
                Id = siteId, Path = path, Offset = offset, Limit = PageSize,
                Listing = listing ?? known, Refresh = refresh, ChronologicalFiles = true, IncludeFolderCounts = !lazyCounts,
                IncludeFreeSpace = !append && _site.Kind == SiteKindCatalog.Sftp
            }, browse.Token);
            replied = Environment.TickCount64 - started;
            if (_closed || generation != _generation) return;
            if (append && !result.Ok && result.Problem == SiteProblems.ListingChanged)
            {
                _busy = false;
                _ = BrowseAsync(path, false, false);
                return;
            }
            if (!result.Ok)
            {
                if (result.Problem == SiteProblems.SignInNeeded && IsSignInKind(_site?.Kind))
                {
                    _expiredCloudSites.Add(siteId);
                    _browseRequiresSignIn = true;
                    _error = "Sign-in has run out. Sign in again to browse this location.";
                    return;
                }
                if (result.Problem is SiteProblems.HostKeyNotAccepted or SiteProblems.HostKeyChanged)
                    _changedSftpKeys.Add(siteId);
                _error = string.IsNullOrWhiteSpace(result.Message) ? "Could not browse this location." : result.Message;
                return;
            }
            // Never combine pages that don't describe the requested page/listing.
            if (result.Offset != offset || (append && result.Listing != listing))
            {
                _error = "The folder listing changed. Refresh to start again.";
                _listing = null; _total = _items.Count;
                return;
            }
            _expiredCloudSites.Remove(siteId);
            if (!append && _site is not null) _site.FreeBytes = result.FreeBytes;
            _path = result.Path;
            _listing = result.Listing;
            if (!append && !string.IsNullOrEmpty(result.Listing))
            {
                if (_knownListings.Count >= 32) _knownListings.Clear();
                _knownListings[ListingKey(siteId, path)] = result.Listing;
                _knownListings[ListingKey(siteId, result.Path)] = result.Listing;
            }
            _items.AddRange(result.Items);
            _total = result.Items.Count == 0 ? _items.Count : Math.Max(result.Total, _items.Count);
            fillCounts = lazyCounts && !string.IsNullOrEmpty(_listing) && result.Items.Any(item => item.IsFolder);
            if (_total > _items.Count && string.IsNullOrEmpty(_listing))
            {
                _error = "This Host did not provide a listing token. Refresh to try again.";
                _total = _items.Count;
            }
        }
        catch (OperationCanceledException) when (browse.IsCancellationRequested) { return; }
        catch (Exception) { if (!_closed && generation == _generation) _error = "Could not load this folder. Check the connection and try again."; }
        finally
        {
            if (ReferenceEquals(_browseRequest, browse)) _browseRequest = null;
            if (!_closed && generation == _generation)
            {
                _busy = false; Render();
                // Remote Files speed work (2026-11-21): reply and first drawn rows, no names or paths.
                Android.Util.Log.Info(InboundApplication.Tag, $"Remote Files browse: reply {replied} ms, rows drawn {Environment.TickCount64 - started} ms, {_items.Count} rows" + (append ? ", next page" : ""));
                if (fillCounts && _path is not null && _listing is not null)
                    _ = FillFolderCountsAsync(generation, siteId, _path, _listing, offset, _items.Count - offset);
            }
        }
    }

    /// <summary>Folder counts for rows already shown, from the listing the Host still holds (review P1).</summary>
    private async Task FillFolderCountsAsync(int generation, string siteId, string path, string listing, int offset, int count)
    {
        using var counts = CancellationTokenSource.CreateLinkedTokenSource(_life.Token);
        _countsRequest?.Cancel();
        _countsRequest = counts;
        try
        {
            var result = await _client.BrowseAsync(new SiteBrowseRequest
            {
                Id = siteId, Path = path, Offset = offset, Limit = count, Listing = listing,
                ChronologicalFiles = true, IncludeFolderCounts = true,
            }, counts.Token);
            if (_closed || generation != _generation || !result.Ok || result.Listing != listing || result.Offset != offset) return;
            bool changed = false;
            for (int i = 0; i < result.Items.Count && offset + i < _items.Count; i++)
            {
                var shown = _items[offset + i];
                var counted = result.Items[i];
                if (!shown.IsFolder || counted.ChildCount is not { } children || shown.Name != counted.Name || shown.ChildCount == children) continue;
                shown.ChildCount = children;
                changed = true;
            }
            if (changed && !_busy) Render();
        }
        catch (OperationCanceledException) { }
        catch (Exception) { /* Counts are optional; the folder stays as shown. */ }
        finally
        {
            if (ReferenceEquals(_countsRequest, counts)) _countsRequest = null;
        }
    }

    private void Render()
    {
        if (_closed) return;
        bool folder = !_showTransfers && !_adding && _site is not null;
        SetFolderFullScreen(folder);
        if (!folder) ResetChrome();
        _content.RemoveAllViews();
        if (_showTransfers) RenderTransfers();
        else if (_adding) RenderAddLocation();
        else if (_site is null) RenderLocations(); else RenderFolder();
    }

    private string? _phoneDestination;

    private string? DownloadDestination => !_phoneHost ? _site?.LocalFolder
        : _phoneDestination is { Length: > 0 } directory
            ? System.IO.Path.Combine(AndroidHost.SharedDownloadFolder, directory)
            : AndroidHost.SharedDownloadFolder;

    private bool CanSelectRemoteFiles() => SameHost() && !_busy && !_startUncertain && _path is not null
        && _safeDownloads() && !string.IsNullOrWhiteSpace(DownloadDestination);

    private bool CanDeleteRemoteFiles() => SameHost() && !_busy && _path is not null && !string.IsNullOrEmpty(_listing)
        && _secureHostConnection && _deleteFiles();

    private void ConfirmDeleteFiles(IReadOnlyList<string> names)
    {
        if (!CanDeleteRemoteFiles() || _confirm is not null || _site is null || _path is null) return;
        var selected = names.Distinct(StringComparer.Ordinal).ToArray();
        if (selected.Length is 0 or > 100 || selected.Any(name =>
            !SafeName(name) || !_items.Any(item => !item.IsFolder && item.Name == name))) return;
        int generation = _generation;
        var request = new SiteChangeRequest
        {
            Id = _site.Id, Path = _path, Listing = _listing,
            Items = selected.Select(name => new SiteChangeItemDto { Name = name }).ToList()
        };
        _confirm = new MaterialAlertDialogBuilder(_activity)
            .SetTitle(selected.Length == 1 ? $"Delete {selected[0]}?" : $"Delete {selected.Length} files?")!
            .SetMessage($"Permanently delete the selected files from {_site.Name} · {_path}?\nHost: {_hostDisplay}\n\nCopies on this phone are not affected.")!
            .SetNegativeButton("Cancel", (Android.Content.IDialogInterfaceOnClickListener?)null)!
            .SetPositiveButton("Delete", (_, _) =>
            {
                if (generation == _generation && CanDeleteRemoteFiles()) _ = DeleteFilesAsync(request);
            })!.Create();
        _confirm.DismissEvent += (_, _) => _confirm = null;
        _confirm.Show();
    }

    private async Task DeleteFilesAsync(SiteChangeRequest request)
    {
        int generation = ++_generation;
        _busy = _mutation = true;
        _error = null;
        Render();
        string? message;
        try
        {
            var result = await _client.DeleteFilesAsync(request, _life.Token);
            if (!SameHost() || generation != _generation) return;
            message = result.Ok ? "Selected files deleted." : result.Message ?? "The Host did not delete every selected file.";
        }
        catch (OperationCanceledException) when (_life.IsCancellationRequested) { return; }
        catch (Exception)
        {
            if (!SameHost() || generation != _generation) return;
            message = "The Host's answer was lost. Check the refreshed list before trying again.";
        }
        finally
        {
            if (!_closed && generation == _generation) _busy = _mutation = false;
        }
        Toast.MakeText(_activity, message, ToastLength.Long)?.Show();
        await BrowseAsync(_path, false, true);
    }

    private void BrowseBreadcrumb(int index)
    {
        if (!SameHost() || _mutation || _site is null || index < -1 || index >= _parents.Count) return;
        CancelCurrent();
        string? parent = index == -1 ? null : _parents[index];
        _parents.RemoveRange(Math.Max(0, index), _parents.Count - Math.Max(0, index));
        _ = BrowseAsync(parent, false, false);
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
            if (!SameHost() || generation != _generation || _site is not null || _adding || _showTransfers) return;
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
                if (!_busy && _site is null && !_adding && !_showTransfers && _surface.IsShown)
                    await RefreshLocationTransfersAsync(_generation);
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested) { }
    }

    private void ConfirmDownloads(IReadOnlyList<string> names, long knownBytes)
    {
        if (!CanSelectRemoteFiles() || _confirm is not null || _site is null || _path is null || DownloadDestination is not { } destination) return;
        var selected = names.Distinct(StringComparer.Ordinal).ToArray();
        if (selected.Length is 0 or > 100 ||
            selected.Any(name => !SafeName(name) || !_items.Any(item => !item.IsFolder && item.Name == name))) return;
        int generation = _generation;
        string siteId = _site.Id, remoteFolder = _path;
        _confirm = new MaterialAlertDialogBuilder(_activity)
            .SetTitle(selected.Length == 1 ? "Download file?" : $"Download {selected.Length} files?")!
            .SetMessage((selected.Length == 1 ? "File: " + selected[0] : $"{selected.Length} selected files" +
                (knownBytes > 0 ? " · " + Fmt.Bytes(knownBytes) : ""))
                + "\nFrom: " + _site.Name + " · " + remoteFolder
                + "\nHost: " + _hostDisplay + "\nDestination on this Host: " + destination
                + "\n\nExisting files are skipped, not replaced. Each file starts as its own transfer.")!
            .SetNegativeButton("Cancel", (Android.Content.IDialogInterfaceOnClickListener?)null)!
            .SetPositiveButton("Download", (_, _) =>
            {
                if (generation == _generation && CanSelectRemoteFiles())
                    _ = StartDownloadsAsync(siteId, remoteFolder, selected, destination);
            })!.Create();
        _confirm.DismissEvent += (_, _) => _confirm = null;
        _confirm.Show();
    }

    private async Task StartDownloadsAsync(string siteId, string folder, string[] names, string destination)
    {
        if (!SameHost() || !_safeDownloads() || _busy || _startUncertain) return;
        int generation = ++_generation;
        _busy = _mutation = true; _error = null;
        Render();
        int accepted = 0;
        try
        {
            foreach (var name in names)
            {
                if (!SameHost() || !_safeDownloads() || generation != _generation) return;
                var result = await _client.StartTransferAsync(new SiteTransferStartRequest
                {
                    SiteId = siteId, Direction = "download", RemoteFolder = folder, LocalFolder = destination, SafeSingleDownload = true,
                    Conflict = "skip", Names = [new SiteTransferNameDto { Name = name, IsFolder = false }]
                }, _life.Token);
                if (!SameHost() || generation != _generation) return;
                if (!result.Ok || result.Transfer is null)
                {
                    _startUncertain = accepted > 0 || result.Ok;
                    var error = result.Ok ? "The Host did not identify a transfer. Check Transfers before trying again."
                        : result.Message ?? "The Host did not start this transfer.";
                    _error = $"{accepted} of {names.Length} transfers confirmed. {error}";
                    return;
                }
                accepted++;
            }
            _showTransfers = true;
            _transfersLoaded = false;
            _busy = _mutation = false;
            _ = LoadTransfersAsync();
        }
        catch (OperationCanceledException) when (_life.IsCancellationRequested) { }
        catch (Exception)
        {
            if (SameHost() && generation == _generation)
            {
                _startUncertain = true;
                _error = $"{accepted} of {names.Length} transfers confirmed. The last answer was lost; check Transfers before trying again.";
            }
        }
        finally
        {
            if (!_closed && generation == _generation) { _busy = _mutation = false; Render(); }
        }
    }

    private void OpenTransfers()
    {
        if (!SameHost() || _busy) return;
        _showTransfers = true;
        _transfersLoaded = false;
        _ = LoadTransfersAsync();
    }

    private async Task LoadTransfersAsync()
    {
        if (!SameHost() || _busy) return;
        int generation = ++_generation;
        _busy = true; _error = null;
        Render();
        try
        {
            var result = await _client.ListTransfersAsync(_life.Token);
            if (!SameHost() || generation != _generation) return;
            _transfers = result.Transfers;
            _transfersLoaded = true;
        }
        catch (OperationCanceledException) when (_life.IsCancellationRequested) { }
        catch (Exception)
        {
            if (SameHost() && generation == _generation)
            {
                _transfersLoaded = false;
                _error = "Could not refresh transfers. Their current state is unknown.";
            }
        }
        finally { if (!_closed && generation == _generation) { _busy = false; Render(); } }
    }

    private void RenderTransfers()
    {
        AddButton("‹  Back", () => HandleBack());
        Heading("Transfers");
        AddText("Host: " + _hostName + " · State as of last refresh", 14, Resource.Attribute.colorOnSurfaceVariant);
        if (!_busy) AddButton("Refresh transfers", () => _ = LoadTransfersAsync());
        if (_busy) AddText("Loading or sending…", 16, Resource.Attribute.colorOnSurfaceVariant);
        if (_error is not null) AddText(_error, 16, Resource.Attribute.colorOnSurfaceVariant);
        if (!_transfersLoaded) return;
        if (_startUncertain && !_busy)
        {
            AddText("Some downloads may already have started. Review this list before making a new selection.",
                14, Resource.Attribute.colorOnSurfaceVariant);
            AddButton("I reviewed these transfers", () =>
            {
                if (!SameHost() || _busy || !_transfersLoaded) return;
                _startUncertain = false;
                _selectedRemoteFiles.Clear();
                Render();
            });
        }
        if (_transfers.Count == 0) AddText("No transfers on this Host.", 16, Resource.Attribute.colorOnSurfaceVariant);
        foreach (var transfer in _transfers.OrderByDescending(t => t.CreatedAt))
        {
            var detail = transfer.State + " · " + transfer.FilesDone + " copied, " + transfer.FilesSkipped
                + " skipped, " + transfer.FilesFailed + " failed of " + transfer.Files
                 + "\n" + transfer.RemoteFolder + " ↔ " + transfer.LocalFolder
                 + "\nStarted: " + transfer.CreatedAt.ToLocalTime().ToString("g") + " · ID " + transfer.Id;
            if (transfer.Direction == "download" && transfer.Conflict == "skip" && transfer.FilesSkipped > 0)
                detail += transfer.FilesSkipped == 1
                    ? "\nAn item with this name already exists at the destination and was kept."
                    : "\nItems with these names already exist at the destination and were kept.";
            if (!string.IsNullOrWhiteSpace(transfer.Message)) detail += "\n" + transfer.Message;
            if (transfer.State == "paused") detail += "\nUnfinished files restart from the beginning on resume.";
            _content.AddView(Card(transfer.Direction + " · " + transfer.SiteName, detail, false));
            if (_busy) continue;
            bool canResume = transfer.State == "paused" || transfer.State == "failed"
                && (transfer.Files - transfer.FilesDone - transfer.FilesSkipped - transfer.FilesFailed - transfer.FilesRunning > 0
                    || transfer.Problem is not null);
            if (transfer.State == "running") AddButton("Pause transfer", () => _ = TransferActionAsync(transfer.Id, "pause"));
            if (canResume) AddButton("Resume transfer", () => _ = TransferActionAsync(transfer.Id, "resume"));
            if (transfer.State is "running" or "paused" || canResume)
                 AddButton("Cancel transfer", () => ConfirmCancelTransfer(transfer));
        }
    }

    private void ConfirmCancelTransfer(SiteTransferDto transfer)
    {
        if (!SameHost() || _busy || _confirm is not null) return;
        _confirm = new MaterialAlertDialogBuilder(_activity)
            .SetTitle("Cancel this transfer?")!
            .SetMessage(transfer.SiteName + " · ID " + transfer.Id + "\nThis batch cannot be resumed after cancellation.")!
            .SetNegativeButton("Keep transfer", (Android.Content.IDialogInterfaceOnClickListener?)null)!
            .SetPositiveButton("Cancel transfer", (_, _) => _ = TransferActionAsync(transfer.Id, "cancel"))!
            .Show();
        if (_confirm is { } dialog) dialog.DismissEvent += (_, _) => { if (ReferenceEquals(_confirm, dialog)) _confirm = null; };
    }

    private async Task TransferActionAsync(string id, string action)
    {
        if (!SameHost() || _busy) return;
        int generation = ++_generation;
        _busy = _mutation = true; _error = null;
        Render();
        try
        {
            var result = action switch
            {
                "pause" => await _client.PauseTransferAsync(id, _life.Token),
                "resume" => await _client.ResumeTransferAsync(id, _life.Token),
                _ => await _client.CancelTransferAsync(id, _life.Token)
            };
            if (!SameHost() || generation != _generation) return;
            _transfersLoaded = false;
            if (!result.Ok) _error = result.Message ?? "The Host did not accept this action. Refresh transfers.";
            else { _busy = _mutation = false; _ = LoadTransfersAsync(); }
        }
        catch (OperationCanceledException) when (_life.IsCancellationRequested) { }
        catch (Exception)
        {
            if (SameHost() && generation == _generation)
            { _transfersLoaded = false; _error = "Could not confirm the action. Refresh transfers to check its state."; }
        }
        finally { if (!_closed && generation == _generation) { _busy = _mutation = false; Render(); } }
    }

    private void ClearPassword()
    {
        if (_password is not null) _password.Text = "";
        _password = null;
        if (_clientSecret is not null) _clientSecret.Text = "";
        _clientSecret = null;
    }

    internal const string OwnClientIdNote =
        "Optional: your own app registration (client ID and secret) from the provider's developer console, instead of rclone's shared one, which rclone says is being retired.";

    /// <summary>The desktop's rules for an own app registration (SitesRules.SignInRequest); null when it is fine.</summary>
    internal static string? ClientIdProblem(string clientId, string secret) =>
        clientId.Length > 256 || !clientId.All(c => char.IsAsciiLetterOrDigit(c) || c is '.' or '-' or '_')
            ? "A client ID is letters, digits, dots, dashes and underscores."
            : clientId.Length == 0 && secret.Length > 0 ? "A client secret needs its client ID." : null;

    private void RenderAddLocation()
    {
        Heading("Add location");
        string kind = _addKind;
        bool cloud = SiteKindCatalog.SignsIn(kind);
        AddText(cloud
            ? "When you sign in, " + _hostName + " will store the authorization."
            : "Credentials will be sent to " + _hostName + " and stored by that Host.", 14, Resource.Attribute.colorOnSurfaceVariant);
        if (_busy) { AddText("Saving…", 16, Resource.Attribute.colorOnSurfaceVariant); return; }
        if (_error is not null) AddText(_error, 16, Resource.Attribute.colorOnSurfaceVariant);
        bool s3 = kind == SiteKindCatalog.S3;
        bool sftp = kind == SiteKindCatalog.Sftp;
        var kinds = new[] { (SiteKindCatalog.WebDav, "WebDAV"), (SiteKindCatalog.Drive, "Google Drive"),
            (SiteKindCatalog.OneDrive, "OneDrive"), (SiteKindCatalog.Dropbox, "Dropbox"), (SiteKindCatalog.S3, "S3"),
            (SiteKindCatalog.Sftp, "SFTP") };
        AddButton("Location type: " + kinds.First(item => item.Item1 == kind).Item2, () =>
        {
            if (!SameHost() || _busy || _confirm is not null) return;
            _confirm = new MaterialAlertDialogBuilder(_activity).SetTitle("Location type")!
                .SetItems(kinds.Select(item => item.Item2).ToArray(), (_, e) =>
                {
                    if (!SameHost()) return;
                    string selectedKind = kinds[e.Which].Item1;
                    if (_addKind == selectedKind) return;
                    ClearPassword();
                    _addKind = selectedKind;
                    _error = null;
                    Render();
                })!.Create();
            _confirm.DismissEvent += (_, _) => _confirm = null;
            _confirm.Show();
        });

        EditText Field(string hint, InputTypes inputType)
        {
            var field = new EditText(_activity) { Hint = hint, SaveEnabled = false };
            field.SetSingleLine(true);
            field.InputType = inputType;
            field.ContentDescription = hint;
            _content.AddView(field);
            return field;
        }

        var name = Field("Location name", InputTypes.ClassText);
        name.Text = _addName;
        Spinner? providerPicker = null;
        var providers = SiteKindCatalog.Find(SiteKindCatalog.S3)!.Choices[SiteFields.Provider];
        if (s3)
        {
            _content.AddView(Label("S3 provider", 14, Resource.Attribute.colorOnSurfaceVariant));
            providerPicker = new Spinner(_activity) { ContentDescription = "S3 provider", SaveEnabled = false };
            var adapter = new ArrayAdapter<string>(_activity, Android.Resource.Layout.SimpleSpinnerItem,
                providers.Select(provider => provider.Title).ToArray());
            adapter.SetDropDownViewResource(Android.Resource.Layout.SimpleSpinnerDropDownItem);
            providerPicker.Adapter = adapter;
            _content.AddView(providerPicker, new LinearLayout.LayoutParams(-1, Motion.Dp(_activity, 48)));
        }
        var address = cloud || sftp ? null : Field(s3 ? "S3 endpoint (optional for Amazon S3)" : "WebDAV address (https://…)",
            InputTypes.ClassText | InputTypes.TextVariationUri);
        var server = sftp ? Field("SFTP server name or address", InputTypes.ClassText) : null;
        var portField = sftp ? Field("Port", InputTypes.ClassNumber) : null;
        if (portField is not null) portField.Text = "22";
        var remoteFolder = sftp || s3 ? Field("Starting folder (optional)", InputTypes.ClassText) : null;
        var region = s3 ? Field("Region (optional)", InputTypes.ClassText) : null;
        var localFolder = _phoneHost ? null : Field("Host download folder (optional)", InputTypes.ClassText);
        if (localFolder is not null) localFolder.ContentDescription = "Download folder on the selected Host (optional)";
        var user = cloud ? null : Field(sftp ? "SFTP user name" : s3 ? "Access key ID" : "User name (optional)", InputTypes.ClassText);
        var password = _password = cloud ? null : Field(s3 ? "Secret access key" : "Password (optional)",
            InputTypes.ClassText | InputTypes.TextVariationPassword);
        if (password is not null) password.Enabled = _secureHostConnection;
        if (!_secureHostConnection)
            AddText("Locations cannot be added over an unencrypted or unverified Host connection. Connect to this Host with HTTPS first.", 14, Resource.Attribute.colorOnSurfaceVariant);
        if (password is not null && Android.OS.Build.VERSION.SdkInt >= Android.OS.BuildVersionCodes.O)
            password.ImportantForAutofill = ImportantForAutofill.No;
        if (cloud) AddText(OwnClientIdNote, 14, Resource.Attribute.colorOnSurfaceVariant);
        var clientId = cloud ? Field("Your own client ID (optional)", InputTypes.ClassText) : null;
        var clientSecret = _clientSecret = cloud ? Field("Its client secret (optional)",
            InputTypes.ClassText | InputTypes.TextVariationPassword) : null;
        if (clientSecret is not null)
        {
            clientSecret.Enabled = _secureHostConnection;
            if (Android.OS.Build.VERSION.SdkInt >= Android.OS.BuildVersionCodes.O)
                clientSecret.ImportantForAutofill = ImportantForAutofill.No;
        }
        var allowHttp = new CheckBox(_activity)
        {
            Text = "Allow HTTP: not encrypted. Credentials and files can be read or changed in transit.",
            SaveEnabled = false
        };
        if (address is not null) _content.AddView(allowHttp);
        if (cloud) AddText("Save the location, then choose Sign in from Remote Files.", 14, Resource.Attribute.colorOnSurfaceVariant);
        const string nameRequired = "Enter a location name.";
        const string sftpServerRequired = "Enter an SFTP server address.";
        const string sftpPortInvalid = "Enter a port from 1 to 65535.";
        const string sftpUserRequired = "Enter an SFTP user name.";
        const string s3KeysRequired = "Enter the access key ID and secret access key.";
        const string s3EndpointRequired = "Enter the S3 endpoint for this provider.";
        const string invalidEndpoint = "Enter an HTTP or HTTPS address without credentials, a query, or a fragment.";
        const string httpConsentRequired = "HTTP is not encrypted. Tick the warning to allow it.";
        var validation = Label("", 14, Resource.Attribute.colorError);
        validation.AccessibilityLiveRegion = AccessibilityLiveRegion.Polite;
        _content.AddView(validation);
        name.TextChanged += (_, _) =>
        {
            _addName = name.Text ?? "";
            if (!string.IsNullOrWhiteSpace(_addName) && validation.Text == nameRequired) validation.Text = "";
        };
        if (address is not null)
        {
            address.TextChanged += (_, _) =>
            {
                allowHttp.Checked = false;
                if (validation.Text is invalidEndpoint or httpConsentRequired or s3EndpointRequired) validation.Text = "";
            };
            allowHttp.CheckedChange += (_, e) =>
            {
                if (e.IsChecked && validation.Text == httpConsentRequired) validation.Text = "";
            };
        }
        if (providerPicker is not null)
        {
            void ClearS3KeysError()
            {
                if (validation.Text == s3KeysRequired && !string.IsNullOrWhiteSpace(user?.Text)
                    && !string.IsNullOrWhiteSpace(password?.Text))
                    validation.Text = "";
            }

            user!.TextChanged += (_, _) => ClearS3KeysError();
            password!.TextChanged += (_, _) => ClearS3KeysError();
            providerPicker.ItemSelected += (_, e) =>
            {
                if (validation.Text == s3EndpointRequired && e.Position >= 0 && e.Position < providers.Count
                    && providers[e.Position].Value == SiteKindCatalog.AwsProvider)
                    validation.Text = "";
            };
        }
        bool TrySftpPort(out int port) => int.TryParse(portField?.Text, out port) && port is >= 1 and <= 65535;
        if (sftp)
        {
            server!.TextChanged += (_, _) =>
            {
                if (validation.Text == sftpServerRequired && !string.IsNullOrWhiteSpace(server.Text)) validation.Text = "";
            };
            portField!.TextChanged += (_, _) =>
            {
                if (validation.Text == sftpPortInvalid && TrySftpPort(out _)) validation.Text = "";
            };
            user!.TextChanged += (_, _) =>
            {
                if (validation.Text == sftpUserRequired && !string.IsNullOrWhiteSpace(user.Text)) validation.Text = "";
            };
        }
        AddButton("Save location", () =>
        {
            if (!SameHost() || _busy) return;
            if (!_secureHostConnection)
            { validation.Text = "Adding a location requires a secure Host connection."; return; }
            var text = address?.Text?.Trim() ?? "";
            var provider = providerPicker is { SelectedItemPosition: >= 0 } picker
                ? providers[picker.SelectedItemPosition].Value : SiteKindCatalog.AwsProvider;
            if (string.IsNullOrWhiteSpace(name.Text))
            { validation.Text = nameRequired; return; }
            var ownClientId = clientId?.Text?.Trim() ?? "";
            var ownSecret = clientSecret?.Text ?? "";
            if (cloud && ClientIdProblem(ownClientId, ownSecret) is { } clientProblem)
            { validation.Text = clientProblem; return; }
            if (s3 && (string.IsNullOrWhiteSpace(user?.Text) || string.IsNullOrWhiteSpace(password?.Text)))
            { validation.Text = s3KeysRequired; return; }
            if (s3 && provider != SiteKindCatalog.AwsProvider && text.Length == 0)
            { validation.Text = s3EndpointRequired; return; }
            int port = 22;
            if (sftp)
            {
                if (string.IsNullOrWhiteSpace(server?.Text))
                { validation.Text = sftpServerRequired; return; }
                if (!TrySftpPort(out port))
                { validation.Text = sftpPortInvalid; return; }
                if (string.IsNullOrWhiteSpace(user?.Text))
                { validation.Text = sftpUserRequired; return; }
            }
            if (address is not null && (!s3 || text.Length > 0))
            {
                if (!Uri.TryCreate(text, UriKind.Absolute, out var uri) ||
                    (uri.Scheme != Uri.UriSchemeHttps && uri.Scheme != Uri.UriSchemeHttp) ||
                    string.IsNullOrWhiteSpace(uri.Host) || !string.IsNullOrEmpty(uri.UserInfo) ||
                    !string.IsNullOrEmpty(uri.Query) || !string.IsNullOrEmpty(uri.Fragment))
                { validation.Text = invalidEndpoint; return; }
                if (uri.Scheme == Uri.UriSchemeHttp && !allowHttp.Checked)
                { validation.Text = httpConsentRequired; return; }
            }
            var request = new SiteSaveRequest
            {
                Name = name.Text!.Trim(), Kind = kind, Url = !s3 && address is not null ? text : null,
                Vendor = !s3 && address is not null ? "other" : null,
                Provider = s3 ? provider : null, Endpoint = s3 && text.Length > 0 ? text : null,
                Region = string.IsNullOrWhiteSpace(region?.Text) ? null : region.Text.Trim(),
                Host = server?.Text?.Trim() ?? "", Port = port, RemoteFolder = remoteFolder?.Text?.Trim() ?? "",
                LocalFolder = string.IsNullOrWhiteSpace(localFolder?.Text) ? null : localFolder.Text.Trim(),
                User = user?.Text?.Trim() ?? "", Password = string.IsNullOrEmpty(password?.Text) ? null : password.Text,
                ClientId = cloud && ownClientId.Length > 0 ? ownClientId : null,
                ClientSecret = cloud && ownSecret.Length > 0 ? ownSecret : null,
            };
            ClearPassword();
            _ = SaveLocationAsync(request);
        });
        AddButton("Cancel", () => HandleBack());
    }

    private async Task SaveLocationAsync(SiteSaveRequest request)
    {
        if (!SameHost() || _busy || !_secureHostConnection) { request.Password = null; request.ClientSecret = null; return; }
        int generation = ++_generation;
        _busy = true; _error = null;
        Render();
        try
        {
            var result = await _client.SaveAsync(request, _life.Token);
            if (_closed || generation != _generation) return;
            if (!result.Ok)
            {
                _error = "The Host could not save this location. Check the details and try again.";
                return;
            }
            _adding = false;
            _addName = "";
            _busy = false;
            _ = LoadLocationsAsync();
        }
        catch (OperationCanceledException) when (_life.IsCancellationRequested) { }
        catch (Exception)
        {
            if (!_closed && generation == _generation)
                _error = "Could not confirm the save. Refresh the location list before trying again.";
        }
        finally
        {
            request.Password = null;
            request.ClientSecret = null;
            if (!_closed && generation == _generation) { _busy = false; Render(); }
        }
    }

    private void ConfirmDisable()
    {
        if (!SameHost() || _busy || _confirm is not null) return;
        _confirm = new MaterialAlertDialogBuilder(_activity)
            .SetTitle("Disable Remote Files?")!
            .SetMessage("Host: " + _hostDisplay + "\nRunning Remote Files transfers will pause.")!
            .SetNegativeButton("Cancel", (Android.Content.IDialogInterfaceOnClickListener?)null)!
            .SetPositiveButton("Disable", (_, _) => _ = SetEnabledAsync(false))!
            .Show();
        if (_confirm is { } dialog) dialog.DismissEvent += (_, _) => { if (ReferenceEquals(_confirm, dialog)) _confirm = null; };
    }

    private async Task SetEnabledAsync(bool enabled)
    {
        if (!SameHost() || _busy) return;
        int generation = ++_generation;
        _busy = true; _error = null;
        Render();
        try
        {
            var status = await _client.SetEnabledAsync(enabled, _life.Token);
            if (_closed || generation != _generation) return;
            if (status.Enabled != enabled)
            {
                _error = "The Host did not change Remote Files. Refresh to check its state.";
                return;
            }
            _busy = false;
            _ = LoadLocationsAsync();
        }
        catch (OperationCanceledException) when (_life.IsCancellationRequested) { }
        catch (Exception)
        {
            if (!_closed && generation == _generation)
                _error = "Could not change Remote Files. Refresh to check the Host's state.";
        }
        finally
        {
            if (!_closed && generation == _generation) { _busy = false; Render(); }
        }
    }

    private void ConfirmRemove(SiteDto site)
    {
        if (!SameHost() || _busy || _confirm is not null) return;
        int generation = _generation;
        _confirm = new MaterialAlertDialogBuilder(_activity)
            .SetTitle("Remove location?")!
            .SetMessage("Remove " + site.Name + " from " + _hostName + "? Remote files will not be deleted.")!
            .SetNegativeButton("Cancel", (Android.Content.IDialogInterfaceOnClickListener?)null)!
            .SetPositiveButton("Remove", (_, _) => { if (SameHost() && generation == _generation) _ = RemoveLocationAsync(site.Id); })!
            .Create();
        _confirm!.DismissEvent += (_, _) => _confirm = null;
        _confirm.Show();
    }

    private async Task RemoveLocationAsync(string id)
    {
        if (_closed || _busy) return;
        int generation = ++_generation;
        _busy = true; _error = null;
        Render();
        try
        {
            var result = await _client.RemoveAsync(id, _life.Token);
            if (_closed || generation != _generation) return;
            if (!result.Ok) { _error = "The Host could not remove this location."; return; }
            _busy = false;
            _ = LoadLocationsAsync();
        }
        catch (OperationCanceledException) when (_life.IsCancellationRequested) { }
        catch (Exception)
        {
            if (!_closed && generation == _generation)
                _error = "Could not confirm removal. Refresh to check the location list.";
        }
        finally
        {
            if (!_closed && generation == _generation) { _busy = false; Render(); }
        }
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

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
internal sealed class RemoteFilesScreen : IDisposable
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
    private CancellationTokenSource? _browseRequest;
    private readonly LinearLayout _content;
    private readonly List<string> _parents = [];
    private readonly List<SiteItemDto> _items = [];
    private SiteDto? _site;
    private SitesStatusDto? _status;
    private List<SiteDto> _sites = [];
    private string? _path, _listing, _error;
    private int _total, _generation;
    private bool _started, _closed, _busy, _listLoaded;
    private bool _adding;
    private bool _showTransfers, _mutation, _startUncertain, _transfersLoaded;
    private List<SiteTransferDto> _transfers = [];
    private EditText? _password;
    private AndroidX.AppCompat.App.AlertDialog? _confirm;

    public View View { get; }

    public RemoteFilesScreen(MainActivity activity, IHostConnection connection, string? connectionStamp, bool secureHostConnection,
        Func<bool> safeDownloads)
    {
        _activity = activity;
        _client = new RemoteFilesClient(connection);
        _safeDownloads = safeDownloads;
        _hostName = activity.HostName;
        _secureHostConnection = secureHostConnection;
        var current = HostChoice.Hosts(activity).Current;
        _hostStamp = connectionStamp ?? "";
        _hostDisplay = current.IsPhone ? _hostName : _hostName + " (" + (current.AddressUri?.GetLeftPart(UriPartial.Authority) ?? "unknown address") + ")";
        _phoneHost = current.IsPhone && (connection is InProcessHostConnection or LazyInProcessConnection);
        if (current.ConnectionStamp != _hostStamp || current.IsPhone != _phoneHost || current.Name != _hostName)
            _hostChanged = true;
        HostChoice.Hosts(activity).Changed += HostChanged;
        var scroll = new ScrollView(activity) { FillViewport = true };
        _content = new LinearLayout(activity) { Orientation = Orientation.Vertical };
        int padding = Motion.Dp(activity, 16);
        _content.SetPadding(padding, padding, padding, padding);
        scroll.AddView(_content);
        View = scroll;
    }

    public void Start()
    {
        if (_closed || _started) return;
        _started = true;
        _ = LoadLocationsAsync();
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
            CancelCurrent();
            Render();
            return true;
        }
        if (_site is null) return false;
        CancelCurrent();
        if (_parents.Count == 0)
        {
            _site = null;
            _path = null;
            _error = null;
            _items.Clear();
            Render();
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
        _generation++;
        HostChoice.Hosts(_activity).Changed -= HostChanged;
        ClearPassword();
        _browseRequest?.Cancel();
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
            ClearPassword();
            _confirm?.Dismiss();
            _confirm = null;
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
            if (_closed || generation != _generation) return;
            _status = status; _sites = list.Sites; _listLoaded = true;
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
        if (_closed || _busy || _site is null) return;
        using var browse = CancellationTokenSource.CreateLinkedTokenSource(_life.Token);
        _browseRequest = browse;
        int generation = ++_generation;
        string siteId = _site.Id;
        int offset = append ? _items.Count : 0;
        string? listing = append ? _listing : null;
        _busy = true; _error = null;
        if (!append) { _path = path; _items.Clear(); _total = 0; _listing = null; }
        Render();
        try
        {
            var result = await _client.BrowseAsync(new SiteBrowseRequest
            {
                Id = siteId, Path = path, Offset = offset, Limit = PageSize,
                Listing = listing, Refresh = refresh
            }, browse.Token);
            if (_closed || generation != _generation) return;
            if (append && !result.Ok && result.Problem == SiteProblems.ListingChanged)
            {
                _busy = false;
                _ = BrowseAsync(path, false, false);
                return;
            }
            if (!result.Ok)
            {
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
            _path = result.Path;
            _listing = result.Listing;
            _items.AddRange(result.Items);
            _total = result.Items.Count == 0 ? _items.Count : Math.Max(result.Total, _items.Count);
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
            if (!_closed && generation == _generation) { _busy = false; Render(); }
        }
    }

    private void Render()
    {
        if (_closed) return;
        _content.RemoveAllViews();
        if (_showTransfers) RenderTransfers();
        else if (_adding) RenderAddLocation();
        else if (_site is null) RenderLocations(); else RenderFolder();
    }

    private void RenderLocations()
    {
        Heading("Remote Files");
        AddText("Host: " + _hostName, 14, Resource.Attribute.colorOnSurfaceVariant);
        if (!_busy) AddButton("Transfers", () => OpenTransfers());
        if (_busy) { AddText("Loading Remote Files…", 16, Resource.Attribute.colorOnSurfaceVariant); return; }
        if (_error is not null)
        {
            AddText(_error, 16, Resource.Attribute.colorOnSurfaceVariant);
            AddButton("Try again", () => _ = LoadLocationsAsync());
            return;
        }
        if (!_listLoaded) return;
        var state = _status!;
        var summary = state.Enabled ? "Remote Files enabled" : "Remote Files is off on this Host";
        summary += "\nrclone: " + state.Rclone;
        if (!string.IsNullOrWhiteSpace(state.RcloneVersion)) summary += " · " + state.RcloneVersion;
        if (!string.IsNullOrWhiteSpace(state.Error)) summary += "\n" + state.Error;
        AddText(summary, 16, Resource.Attribute.colorOnSurfaceVariant);
        if (!state.Enabled) AddButton("Enable Remote Files", () => _ = SetEnabledAsync(true));
        else AddButton("Disable Remote Files", () => ConfirmDisable());
        if (state.Enabled) AddButton("Add location", () => { _adding = true; _error = null; Render(); });
        AddButton("Refresh", () => _ = LoadLocationsAsync());
        if (_sites.Count == 0) AddText("No saved remote locations on this Host.", 16, Resource.Attribute.colorOnSurfaceVariant);
        foreach (var site in _sites)
        {
            var card = Card(site.Name, site.Kind + (site.Encrypted ? "" : " · Not encrypted"), true);
            card.Click += (_, _) =>
            {
                if (_busy || _closed) return;
                _site = site; _parents.Clear();
                _ = BrowseAsync(null, false, false);
            };
            _content.AddView(card);
            AddButton("Remove " + site.Name, () => ConfirmRemove(site));
        }
    }

    private void RenderFolder()
    {
        AddButton("‹  Back", () => HandleBack());
        Heading(_site!.Name);
        AddText("Remote location · " + (_path ?? "Starting folder"), 14, Resource.Attribute.colorOnSurfaceVariant);
        if (!_busy) AddButton("Transfers", () => OpenTransfers());
        string? destination = _phoneHost ? AndroidHost.SharedDownloadFolder : _site.LocalFolder;
        if (!_safeDownloads())
            AddText("Update this Host to download files without replacing existing files.", 14, Resource.Attribute.colorOnSurfaceVariant);
        else if (string.IsNullOrWhiteSpace(destination))
            AddText("No linked folder is configured on this Host. Configure one for this location before downloading.", 14, Resource.Attribute.colorOnSurfaceVariant);
        else if (_startUncertain)
            AddText("The last start could not be confirmed. Check Transfers before starting another download; reopen Remote Files to try again.", 14, Resource.Attribute.colorOnSurfaceVariant);
        if (!_busy) AddButton("Refresh folder", () => _ = BrowseAsync(_path, false, true));
        if (_busy) AddText("Loading…", 16, Resource.Attribute.colorOnSurfaceVariant);
        if (_error is not null)
        {
            AddText(_error, 16, Resource.Attribute.colorOnSurfaceVariant);
            if (!_busy) AddButton("Try again", () => _ = BrowseAsync(_path, false, true));
        }
        foreach (var item in _items)
        {
            string detail = item.IsFolder ? "Folder" : item.Size >= 0 ? Fmt.Bytes(item.Size) : "Size unknown";
            if (item.Modified is { } modified) detail += " · " + modified.ToLocalTime().ToString("d");
            bool canOpen = item.IsFolder && !_busy && _path is not null && SafeName(item.Name);
            var row = Card(item.Name, detail, canOpen);
            if (canOpen)
            {
                var current = _path!;
                var next = current.TrimEnd('/') + "/" + item.Name;
                row.Click += (_, _) =>
                {
                    if (_closed || _busy) return;
                    _parents.Add(current);
                    _ = BrowseAsync(next, false, false);
                };
            }
            _content.AddView(row);
            if (_safeDownloads() && !item.IsFolder && !_busy && !_startUncertain && _path is not null && SafeName(item.Name)
                && !string.IsNullOrWhiteSpace(destination))
                AddButton("Download " + item.Name, () => ConfirmDownload(item.Name, destination!));
        }
        if (!_busy && _error is null && _items.Count == 0) AddText("This folder is empty.", 16, Resource.Attribute.colorOnSurfaceVariant);
        if (!_busy && _items.Count < _total && !string.IsNullOrEmpty(_listing))
            AddButton("Load more", () => _ = BrowseAsync(_path, true, false));
    }

    private void ConfirmDownload(string name, string destination)
    {
        if (!SameHost() || _busy || _startUncertain || _confirm is not null || _site is null || _path is null) return;
        int generation = _generation;
        string siteId = _site.Id, remoteFolder = _path;
        _confirm = new MaterialAlertDialogBuilder(_activity)
            .SetTitle("Download file?")!
            .SetMessage("File: " + name + "\nFrom: " + _site.Name + " · " + remoteFolder
                + "\nHost: " + _hostDisplay + "\nDestination on this Host: " + destination
                + "\n\nIf the destination file already exists, it will be skipped, not replaced.")!
            .SetNegativeButton("Cancel", (Android.Content.IDialogInterfaceOnClickListener?)null)!
            .SetPositiveButton("Download", (_, _) =>
            {
                if (SameHost() && generation == _generation && !_busy && !_startUncertain)
                    _ = StartDownloadAsync(siteId, remoteFolder, name, destination);
            })!.Create();
        _confirm.DismissEvent += (_, _) => _confirm = null;
        _confirm.Show();
    }

    private async Task StartDownloadAsync(string siteId, string folder, string name, string destination)
    {
        if (!SameHost() || !_safeDownloads() || _busy || _startUncertain) return;
        int generation = ++_generation;
        _busy = _mutation = true; _error = null;
        Render();
        try
        {
            var result = await _client.StartTransferAsync(new SiteTransferStartRequest
            {
                SiteId = siteId, Direction = "download", RemoteFolder = folder, LocalFolder = destination, SafeSingleDownload = true,
                Conflict = "skip", Names = [new SiteTransferNameDto { Name = name, IsFolder = false }]
            }, _life.Token);
            if (!SameHost() || generation != _generation) return;
            if (!result.Ok)
                _error = result.Message ?? "The Host did not start this transfer.";
            else if (result.Transfer is null)
            {
                _startUncertain = true;
                _error = "The Host accepted the request but did not identify a transfer. Check Transfers before trying again.";
            }
            else
            {
                _showTransfers = true;
                _transfersLoaded = false;
                _busy = _mutation = false;
                _ = LoadTransfersAsync();
            }
        }
        catch (OperationCanceledException) when (_life.IsCancellationRequested) { }
        catch (Exception)
        {
            if (SameHost() && generation == _generation)
            {
                _startUncertain = true;
                _error = "The Host's answer was lost. The transfer may have started; check Transfers before trying again.";
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
        if (_transfers.Count == 0) AddText("No transfers on this Host.", 16, Resource.Attribute.colorOnSurfaceVariant);
        foreach (var transfer in _transfers.OrderByDescending(t => t.CreatedAt))
        {
            var detail = transfer.State + " · " + transfer.FilesDone + " copied, " + transfer.FilesSkipped
                + " skipped, " + transfer.FilesFailed + " failed of " + transfer.Files
                 + "\n" + transfer.RemoteFolder + " ↔ " + transfer.LocalFolder
                 + "\nStarted: " + transfer.CreatedAt.ToLocalTime().ToString("g") + " · ID " + transfer.Id;
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
    }

    private void RenderAddLocation()
    {
        Heading("Add WebDAV location");
        AddText("Credentials will be sent to " + _hostName + " and stored by that Host.", 14, Resource.Attribute.colorOnSurfaceVariant);
        if (_busy) { AddText("Saving…", 16, Resource.Attribute.colorOnSurfaceVariant); return; }
        if (_error is not null) AddText(_error, 16, Resource.Attribute.colorOnSurfaceVariant);

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
        var address = Field("WebDAV address (https://…)", InputTypes.ClassText | InputTypes.TextVariationUri);
        var localFolder = _phoneHost ? null : Field("Download folder on the selected Host (optional)", InputTypes.ClassText);
        var user = Field("User name (optional)", InputTypes.ClassText);
        var password = _password = Field("Password (optional)", InputTypes.ClassText | InputTypes.TextVariationPassword);
        password.Enabled = _secureHostConnection;
        if (!_secureHostConnection)
            AddText("Locations cannot be added over an unencrypted or unverified Host connection. Connect to this Host with HTTPS first.", 14, Resource.Attribute.colorOnSurfaceVariant);
        if (Android.OS.Build.VERSION.SdkInt >= Android.OS.BuildVersionCodes.O)
            password.ImportantForAutofill = ImportantForAutofill.No;
        var allowHttp = new CheckBox(_activity)
        {
            Text = "Allow HTTP: not encrypted. Credentials and files can be read or changed in transit.",
            SaveEnabled = false
        };
        _content.AddView(allowHttp);
        var validation = Label("", 14, Resource.Attribute.colorOnSurfaceVariant);
        _content.AddView(validation);
        AddButton("Save location", () =>
        {
            if (!SameHost() || _busy) return;
            if (!_secureHostConnection)
            { validation.Text = "Adding a location requires a secure Host connection."; return; }
            var text = address.Text?.Trim() ?? "";
            if (string.IsNullOrWhiteSpace(name.Text))
            { validation.Text = "Enter a location name."; return; }
            if (!Uri.TryCreate(text, UriKind.Absolute, out var uri) ||
                (uri.Scheme != Uri.UriSchemeHttps && uri.Scheme != Uri.UriSchemeHttp) ||
                string.IsNullOrWhiteSpace(uri.Host) || !string.IsNullOrEmpty(uri.UserInfo) ||
                !string.IsNullOrEmpty(uri.Query) || !string.IsNullOrEmpty(uri.Fragment))
            { validation.Text = "Enter an HTTP or HTTPS address without credentials, a query, or a fragment."; return; }
            if (uri.Scheme == Uri.UriSchemeHttp && !allowHttp.Checked)
            { validation.Text = "HTTP is not encrypted. Tick the warning to allow it."; return; }
            var request = new SiteSaveRequest
            {
                Name = name.Text!.Trim(), Kind = "webdav", Url = text, Vendor = "other",
                LocalFolder = string.IsNullOrWhiteSpace(localFolder?.Text) ? null : localFolder.Text.Trim(),
                User = user.Text?.Trim() ?? "", Password = string.IsNullOrEmpty(password.Text) ? null : password.Text
            };
            ClearPassword();
            _ = SaveLocationAsync(request);
        });
        AddButton("Cancel", () => HandleBack());
    }

    private async Task SaveLocationAsync(SiteSaveRequest request)
    {
        if (!SameHost() || _busy || !_secureHostConnection) { request.Password = null; return; }
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

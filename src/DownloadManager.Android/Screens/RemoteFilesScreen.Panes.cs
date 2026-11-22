using Android.Database;
using Android.Provider;
using Android.Widget;
using DownloadManager.Contracts;
using DownloadManager.Mobile;
using DownloadManager.Runtime;
using Inbound.App.Host;

namespace Inbound.App.Screens;

/// <summary>
/// The two panes of the Remote Files redesign (mockups r1 to r6). The left tab opens the chosen location, the right tab is the
/// phone by default, and either can be switched to any location. Copying goes to the other tab's folder.
/// </summary>
internal sealed partial class RemoteFilesScreen
{
    private const int FirstPage = 100;
    private const int LaterPage = 500;
    private const int RowCap = 5000;
    /// <summary>The most items one transfer or delete request takes.</summary>
    private const int MaxSelected = 100;

    /// <summary>A pane's location, folder and rows. A null <see cref="Site"/> is this phone, whose path is "", "Download" or "Download/…".</summary>
    private sealed class Pane
    {
        public SiteDto? Site;
        public string Path = "";
        /// <summary>The rows as the Host (or the phone) listed them; counts arrive later.</summary>
        public readonly List<SiteItemDto> Items = [];
        /// <summary>The rows in the chosen order; what the list shows.</summary>
        public List<SiteItemDto> Rows = [];
        public readonly Dictionary<string, Android.Net.Uri> PhoneUris = new(StringComparer.Ordinal);
        public readonly HashSet<string> Selected = new(StringComparer.Ordinal);
        public string? Listing, Error;
        public int Total, Generation;
        /// <summary>Changes when the pane shows a different folder or reloads from scratch (not on a quiet refresh); the list scrolls to the top on a change.</summary>
        public int ViewEpoch;
        public bool Loading, NeedsSignIn, KeyProblem;
        public RemoteSort Sort = RemoteSort.Name;
        public CancellationTokenSource? Request;
        /// <summary>One token per folder-count request, so a later page's request does not cancel an earlier one; Cancel() ends all of them.</summary>
        public readonly CountRequests Counts = new();
        public bool IsPhone => Site is null;
    }

    private sealed class CountRequests
    {
        private readonly List<CancellationTokenSource> _open = [];
        public void Add(CancellationTokenSource source) => _open.Add(source);
        public void Remove(CancellationTokenSource source) => _open.Remove(source);
        public void Cancel()
        {
            foreach (var source in _open.ToArray()) source.Cancel();
            _open.Clear();
        }
    }

    private readonly Pane[] _panes = [new Pane(), new Pane()];
    private int _active;
    private bool _inFolder;
    private (int Index, string SiteId)? _pendingChoice;
    private int _lastActiveTransfers;
    // Ticks left in which phone panes are read again after a transfer finished (see RefreshTransfersQuietAsync).
    private int _phoneSettleTicks;

    private Pane Active => _panes[_active];
    private Pane Other => _panes[1 - _active];

    private string PaneName(Pane pane) => pane.Site is null ? "This phone" : RemoteLocationText.DisplayName(pane.Site);

    private string PaneRoot(Pane pane) => pane.Site is null ? "" : RemoteCrumbs.Normalize(pane.Site.RemoteFolder);

    private bool ShowsSite(string siteId) => _panes.Any(pane => pane.Site?.Id == siteId);

    private void ResetPane(Pane pane, SiteDto? site, string? path)
    {
        pane.Request?.Cancel();
        pane.Counts?.Cancel();
        pane.Generation++;
        pane.ViewEpoch++;
        pane.Site = site;
        pane.Path = path ?? (site is null ? "Download" : PaneRootOf(site));
        pane.Items.Clear();
        pane.Rows = [];
        pane.PhoneUris.Clear();
        pane.Selected.Clear();
        pane.Listing = pane.Error = null;
        pane.Total = 0;
        pane.Loading = pane.NeedsSignIn = pane.KeyProblem = false;
    }

    private static string PaneRootOf(SiteDto site) => RemoteCrumbs.Normalize(site.RemoteFolder);

    /// <summary>Opens a saved location in the left pane with this phone on the right.</summary>
    private void OpenFolderView(SiteDto site)
    {
        _inFolder = true;
        _active = 0;
        _lastActiveTransfers = _transfers.Count(t => t.State is "queued" or "running" or "paused");
        ResetPane(_panes[0], site, null);
        ResetPane(_panes[1], null, "Download");
        Render();
        _ = BrowsePaneAsync(_panes[0], null, false);
        _ = BrowsePaneAsync(_panes[1], "Download", false);
    }

    private void CloseFolderView()
    {
        foreach (var pane in _panes)
        {
            pane.Request?.Cancel();
            pane.Counts?.Cancel();
            pane.Generation++;
            pane.Loading = false;
            pane.Selected.Clear();
        }
        _inFolder = false;
        _pendingChoice = null;
        Render();
        _ = RefreshLocationTransfersAsync(_generation);
    }

    /// <summary>Back inside the folder view; true when it did something other than leave.</summary>
    private bool FolderBack()
    {
        var pane = Active;
        if (pane.Selected.Count > 0) { pane.Selected.Clear(); UpdateFolderUi(); return true; }
        if (GoUp(pane)) return true;
        CloseFolderView();
        return true;
    }

    private bool GoUp(Pane pane)
    {
        string? parent;
        if (pane.IsPhone)
        {
            if (pane.Path.Length == 0) return false;
            int slash = pane.Path.LastIndexOf('/');
            parent = slash < 0 ? "" : pane.Path[..slash];
        }
        else parent = RemoteCrumbs.Parent(PaneRoot(pane), pane.Path);
        if (parent is null) return false;
        _ = BrowsePaneAsync(pane, parent, false);
        return true;
    }

    private void OpenFolderIn(Pane pane, SiteItemDto folder)
    {
        if (!SafeName(folder.Name)) return;
        string path = pane.IsPhone
            ? pane.Path.Length == 0 ? folder.Name : pane.Path + "/" + folder.Name
            : (pane.Path == "/" ? "" : pane.Path.TrimEnd('/')) + "/" + folder.Name;
        _ = BrowsePaneAsync(pane, path, false);
    }

    private void Resort(Pane pane) => pane.Rows = RemoteFileList.Sort(pane.Items, pane.Sort);

    // ---------------------------------------------------------------------------------------------------------------
    // Listing

    /// <param name="keepView">A quiet refresh: the rows stay shown (and the scroll position and selection kept) until the new ones replace them.</param>
    private async Task BrowsePaneAsync(Pane pane, string? path, bool refresh, bool keepView = false)
    {
        if (!SameHost()) return;
        pane.Request?.Cancel();
        pane.Counts?.Cancel();
        var request = CancellationTokenSource.CreateLinkedTokenSource(_life.Token);
        pane.Request = request;
        int generation = ++pane.Generation;
        var site = pane.Site;
        pane.Loading = true;
        pane.Error = null;
        pane.NeedsSignIn = pane.KeyProblem = false;
        if (!keepView)
        {
            pane.ViewEpoch++;
            pane.Selected.Clear();
            pane.Items.Clear();
            pane.Rows = [];
            pane.PhoneUris.Clear();
            pane.Total = 0;
            pane.Listing = null;
        }
        pane.Path = path ?? (site is null ? "Download" : PaneRootOf(site));
        UpdateFolderUi();
        long started = Environment.TickCount64, replied = -1;
        string? fillListing = null;
        try
        {
            if (site is null)
            {
                var phone = await Task.Run(() => ReadPhoneFolder(pane.Path));
                if (_closed || generation != pane.Generation) return;
                // A quiet refresh that fails leaves the rows as they were; the next real refresh says why.
                if (keepView && phone.Error is not null) return;
                pane.Error = phone.Error;
                if (keepView) { pane.Items.Clear(); pane.PhoneUris.Clear(); }
                pane.Items.AddRange(phone.Items);
                foreach (var pair in phone.Uris) pane.PhoneUris[pair.Key] = pair.Value;
                pane.Total = pane.Items.Count;
                return;
            }

            // A Host that keeps the listing answers first without folder counts, which follow (2 s less per open).
            bool lazyCounts = _cachedFirstPage();
            string? known = !refresh && _knownListings.TryGetValue(ListingKey(site.Id, path), out var remembered) ? remembered : null;
            var result = await _client.BrowseAsync(new SiteBrowseRequest
            {
                Id = site.Id, Path = path, Offset = 0, Limit = FirstPage, Listing = known, Refresh = refresh,
                IncludeFolderCounts = !lazyCounts, IncludeFreeSpace = site.Kind == SiteKindCatalog.Sftp
            }, request.Token);
            replied = Environment.TickCount64 - started;
            if (_closed || generation != pane.Generation) return;
            if (!result.Ok)
            {
                if (keepView) return;
                if (result.Problem == SiteProblems.SignInNeeded && IsSignInKind(site.Kind))
                {
                    _expiredCloudSites.Add(site.Id);
                    pane.NeedsSignIn = true;
                    pane.Error = "Sign-in has run out. Sign in again to browse this location.";
                    return;
                }
                if (result.Problem is SiteProblems.HostKeyNotAccepted or SiteProblems.HostKeyChanged)
                {
                    _changedSftpKeys.Add(site.Id);
                    pane.KeyProblem = true;
                }
                pane.Error = string.IsNullOrWhiteSpace(result.Message) ? "Could not browse this location." : result.Message;
                return;
            }
            _expiredCloudSites.Remove(site.Id);
            site.FreeBytes = result.FreeBytes ?? site.FreeBytes;
            if (result.FreeBytes is { } free) _freeBytes[site.Id] = free;
            pane.Path = result.Path;
            pane.Listing = result.Listing;
            if (!string.IsNullOrEmpty(result.Listing))
            {
                if (_knownListings.Count >= 32) _knownListings.Clear();
                _knownListings[ListingKey(site.Id, path)] = result.Listing;
                _knownListings[ListingKey(site.Id, result.Path)] = result.Listing;
            }
            if (keepView)
            {
                // A refreshed listing comes without folder counts: carry the known ones over by name, so the rows don't
                // lose them and fill again after every finished transfer.
                var counted = new Dictionary<string, int>(StringComparer.Ordinal);
                foreach (var old in pane.Items)
                    if (old.IsFolder && old.ChildCount is { } children) counted.TryAdd(old.Name, children);
                foreach (var item in result.Items)
                    if (item.IsFolder && item.ChildCount is null && counted.TryGetValue(item.Name, out var children)) item.ChildCount = children;
                pane.Items.Clear();
            }
            pane.Items.AddRange(result.Items);
            pane.Total = result.Items.Count == 0 ? pane.Items.Count : Math.Max(result.Total, pane.Items.Count);
            if (pane.Total > pane.Items.Count && string.IsNullOrEmpty(pane.Listing))
            {
                pane.Error = "This Host did not provide a listing token. Refresh to try again.";
                pane.Total = pane.Items.Count;
            }
            else if (lazyCounts && !string.IsNullOrEmpty(pane.Listing) && result.Items.Any(item => item.IsFolder && item.ChildCount is null))
                fillListing = pane.Listing;
        }
        catch (OperationCanceledException) when (request.IsCancellationRequested) { return; }
        catch (Exception)
        {
            if (!_closed && generation == pane.Generation && !keepView) pane.Error = "Could not load this folder. Check the connection and try again.";
        }
        finally
        {
            if (ReferenceEquals(pane.Request, request)) pane.Request = null;
            request.Dispose();
            if (!_closed && generation == pane.Generation)
            {
                pane.Loading = false;
                Resort(pane);
                if (keepView) pane.Selected.RemoveWhere(name => pane.Items.All(item => item.Name != name));
                UpdateFolderUi();
                // Remote Files speed work: reply and first drawn rows, no names or paths.
                Android.Util.Log.Info(InboundApplication.Tag, $"Remote Files browse: reply {replied} ms, rows drawn {Environment.TickCount64 - started} ms, {pane.Items.Count} rows");
                if (site is not null && pane.Listing is { } listing && pane.Error is null)
                {
                    if (fillListing is not null) _ = FillCountsAsync(pane, generation, site.Id, pane.Path, listing, 0, pane.Items.Count);
                    if (pane.Items.Count < pane.Total) _ = LoadRestAsync(pane, generation, site.Id, pane.Path, listing);
                }
            }
        }
    }

    /// <summary>The rest of a long folder follows the first page, so sorting by date or size sees every row.</summary>
    private async Task LoadRestAsync(Pane pane, int generation, string siteId, string path, string listing)
    {
        using var rest = CancellationTokenSource.CreateLinkedTokenSource(_life.Token);
        try
        {
            while (!_closed && generation == pane.Generation && pane.Items.Count < pane.Total && pane.Items.Count < RowCap)
            {
                int offset = pane.Items.Count;
                var result = await _client.BrowseAsync(new SiteBrowseRequest
                {
                    Id = siteId, Path = path, Offset = offset, Limit = LaterPage, Listing = listing,
                    IncludeFolderCounts = !_cachedFirstPage()
                }, rest.Token);
                if (_closed || generation != pane.Generation) return;
                if (!result.Ok || result.Offset != offset || result.Listing != listing || result.Items.Count == 0)
                {
                    pane.Total = pane.Items.Count;
                    if (!result.Ok && result.Problem != SiteProblems.ListingChanged) pane.Error = result.Message ?? "Could not load the rest of this folder.";
                    break;
                }
                pane.Items.AddRange(result.Items);
                Resort(pane);
                UpdateFolderUi();
                if (_cachedFirstPage() && result.Items.Any(item => item.IsFolder))
                    _ = FillCountsAsync(pane, generation, siteId, path, listing, offset, result.Items.Count);
            }
        }
        catch (OperationCanceledException) { }
        catch (Exception) { if (generation == pane.Generation) pane.Total = pane.Items.Count; }
        if (!_closed && generation == pane.Generation) UpdateFolderUi();
    }

    /// <summary>
    /// Folder counts for rows already shown, from the listing the Host still holds (review P1). The Host caps the counts it
    /// computes per request, so the request repeats for the same rows while folders are still missing a count and the last
    /// reply filled at least one.
    /// </summary>
    private async Task FillCountsAsync(Pane pane, int generation, string siteId, string path, string listing, int offset, int count)
    {
        var counts = CancellationTokenSource.CreateLinkedTokenSource(_life.Token);
        pane.Counts.Add(counts);
        try
        {
            // One empty round is allowed: a slow server can run out of the Host's time budget once and count more next time.
            for (int round = 0, empty = 0; round < 200; round++)
            {
                var result = await _client.BrowseAsync(new SiteBrowseRequest
                {
                    Id = siteId, Path = path, Offset = offset, Limit = count, Listing = listing, IncludeFolderCounts = true
                }, counts.Token);
                if (_closed || generation != pane.Generation || !result.Ok || result.Listing != listing || result.Offset != offset) return;
                int filled = 0;
                for (int i = 0; i < result.Items.Count && offset + i < pane.Items.Count; i++)
                {
                    var shown = pane.Items[offset + i];
                    var counted = result.Items[i];
                    if (!shown.IsFolder || counted.ChildCount is not { } children || shown.Name != counted.Name || shown.ChildCount is not null) continue;
                    shown.ChildCount = children;
                    filled++;
                }
                if (filled > 0) UpdateFolderUi();
                bool missing = false;
                for (int i = offset; i < offset + count && i < pane.Items.Count && !missing; i++)
                    missing = pane.Items[i].IsFolder && pane.Items[i].ChildCount is null;
                if (!missing) break;
                if (filled == 0 && ++empty > 1) break;
                if (filled > 0) empty = 0;
            }
        }
        catch (OperationCanceledException) { }
        catch (Exception) { /* Counts are optional; the folder stays as shown. */ }
        finally
        {
            pane.Counts.Remove(counts);
            counts.Dispose();
        }
    }

    // ---------------------------------------------------------------------------------------------------------------
    // The phone's side: Android's scoped Download folder, with the phone's own sizes and dates.

    private sealed record PhoneFolder(IReadOnlyList<SiteItemDto> Items, IReadOnlyDictionary<string, Android.Net.Uri> Uris, string? Error);

    /// <summary>
    /// "" is the phone's top, which holds only Download (scoped storage shows nothing else). Below it, the Downloads MediaStore
    /// collection gives files and sizes; folders also come from the file system, because MediaStore cannot list empty ones.
    /// </summary>
    private PhoneFolder ReadPhoneFolder(string phonePath)
    {
        var none = new Dictionary<string, Android.Net.Uri>();
        if (phonePath.Length == 0)
            return new PhoneFolder([new SiteItemDto { Name = "Download", IsFolder = true }], none, null);
        if (RemoteTransfers.DownloadRelative(phonePath) is not { } relativePath || !SafeRelativeFolder(relativePath))
            return new PhoneFolder([], none, "This folder is not available.");

        var files = new Dictionary<string, SiteItemDto>(StringComparer.Ordinal);
        var uris = new Dictionary<string, Android.Net.Uri>(StringComparer.Ordinal);
        var folders = new Dictionary<string, HashSet<string>>(StringComparer.Ordinal);
        string parent = relativePath.Trim('/');
        string prefix = parent.Length == 0 ? "" : parent + "/";
        var projection = new[]
        {
            MediaStore.IMediaColumns.DisplayName, MediaStore.IMediaColumns.RelativePath,
            MediaStore.IMediaColumns.Size, MediaStore.IMediaColumns.DateModified, "_id"
        };
        try
        {
            using ICursor? cursor = _activity.ContentResolver?.Query(MediaStore.Downloads.ExternalContentUri, projection, null, null,
                MediaStore.IMediaColumns.RelativePath + ", " + MediaStore.IMediaColumns.DisplayName);
            if (cursor is null) return new PhoneFolder([], none, "Android did not return the Downloads list.");
            int nameColumn = cursor.GetColumnIndex(MediaStore.IMediaColumns.DisplayName);
            int pathColumn = cursor.GetColumnIndex(MediaStore.IMediaColumns.RelativePath);
            int sizeColumn = cursor.GetColumnIndex(MediaStore.IMediaColumns.Size);
            int modifiedColumn = cursor.GetColumnIndex(MediaStore.IMediaColumns.DateModified);
            int idColumn = cursor.GetColumnIndex("_id");
            if (nameColumn < 0 || pathColumn < 0) return new PhoneFolder([], none, "Android did not provide Downloads folder details.");
            while (cursor.MoveToNext())
            {
                var displayName = cursor.GetString(nameColumn) ?? "";
                var mediaPath = cursor.GetString(pathColumn) ?? "";
                if (!SafeSegment(displayName) || !TryDownloadsRelativePath(mediaPath, out var directoryPath)) continue;
                var itemPath = directoryPath.Length == 0 ? displayName : directoryPath + "/" + displayName;
                if (!itemPath.StartsWith(prefix, StringComparison.Ordinal)) continue;
                var remainder = itemPath[prefix.Length..];
                if (remainder.Length == 0) continue;
                int slash = remainder.IndexOf('/');
                if (slash >= 0)
                {
                    var child = remainder[..slash];
                    if (!SafeSegment(child)) continue;
                    if (!folders.TryGetValue(child, out var inside)) folders[child] = inside = new HashSet<string>(StringComparer.Ordinal);
                    var next = remainder[(slash + 1)..];
                    int deeper = next.IndexOf('/');
                    inside.Add(deeper < 0 ? next : next[..deeper]);
                    continue;
                }
                long size = sizeColumn >= 0 && !cursor.IsNull(sizeColumn) ? cursor.GetLong(sizeColumn) : -1;
                long modified = modifiedColumn >= 0 && !cursor.IsNull(modifiedColumn) ? cursor.GetLong(modifiedColumn) : 0;
                files[displayName] = new SiteItemDto
                {
                    Name = displayName, Size = Math.Max(-1, size),
                    Modified = modified > 0 ? DateTimeOffset.FromUnixTimeSeconds(modified) : null
                };
                if (idColumn >= 0 && !cursor.IsNull(idColumn))
                    uris[displayName] = Android.Content.ContentUris.WithAppendedId(MediaStore.Downloads.ExternalContentUri!, cursor.GetLong(idColumn));
            }
        }
        catch (Exception)
        {
            return new PhoneFolder([], none, "Could not read the phone's scoped Downloads list.");
        }

        try
        {
            if (ExistingPhoneDownloadFolder(parent) is { } allowed)
                foreach (var directory in new DirectoryInfo(allowed).EnumerateDirectories())
                    if (SafeSegment(directory.Name) && (directory.Attributes & FileAttributes.ReparsePoint) == 0)
                    {
                        if (!folders.TryGetValue(directory.Name, out var inside)) folders[directory.Name] = inside = new HashSet<string>(StringComparer.Ordinal);
                        try { foreach (var entry in Directory.EnumerateFileSystemEntries(directory.FullName)) inside.Add(System.IO.Path.GetFileName(entry)); }
                        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { }
                    }
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            // Scoped-storage differences may hide directory metadata. Keep MediaStore entries.
        }

        var items = folders.Select(pair => new SiteItemDto { Name = pair.Key, IsFolder = true, ChildCount = pair.Value.Count })
            .Concat(files.Values).ToArray();
        return new PhoneFolder(items, uris, null);
    }

    private static string? ExistingPhoneDownloadFolder(string relativePath)
    {
        if (!SafeRelativeFolder(relativePath)) return null;
        string root = AndroidHost.SharedDownloadFolder;
        var roots = new PathRoots([root]);
        string? resolved = roots.Allowed(System.IO.Path.Combine(root, relativePath));
        return resolved is not null && Directory.Exists(resolved) ? resolved : null;
    }

    private static bool SafeRelativeFolder(string path) =>
        !System.IO.Path.IsPathRooted(path) && !path.Contains('\\')
        && path.Split('/', StringSplitOptions.RemoveEmptyEntries).All(SafeSegment);

    private static bool SafeSegment(string name) =>
        !string.IsNullOrWhiteSpace(name) && name is not "." and not ".."
        && !name.Contains('/') && !name.Contains('\\') && !name.Contains('\0');

    private static bool TryDownloadsRelativePath(string mediaPath, out string relative)
    {
        relative = "";
        var normalized = mediaPath.Replace('\\', '/').Trim('/');
        const string root = "Download";
        if (!normalized.Equals(root, StringComparison.OrdinalIgnoreCase)
            && !normalized.StartsWith(root + "/", StringComparison.OrdinalIgnoreCase))
            return false;
        relative = normalized.Length == root.Length ? "" : normalized[(root.Length + 1)..];
        return relative.Split('/', StringSplitOptions.RemoveEmptyEntries).All(SafeSegment);
    }

    private string PhoneFreeText()
    {
        try
        {
            var stat = new Android.OS.StatFs(Android.OS.Environment.ExternalStorageDirectory?.Path ?? AndroidHost.SharedDownloadFolder);
            return "Internal storage · " + Fmt.Bytes(stat.AvailableBytes) + " free";
        }
        catch (Exception) { return "Internal storage"; }
    }

    // ---------------------------------------------------------------------------------------------------------------
    // Choosing what a pane shows

    private SiteDto? FreshSite(string id) => _sites.FirstOrDefault(site => site.Id == id);

    private void ChoosePaneLocation(int index, SiteDto? site)
    {
        if (!SameHost() || _mutation) return;
        var pane = _panes[index];
        if (site is null)
        {
            ResetPane(pane, null, "Download");
            UpdateFolderUi();
            _ = BrowsePaneAsync(pane, "Download", false);
            return;
        }
        if (site.Kind == SiteKindCatalog.Sftp && (site.HostKeys.Count == 0 || _changedSftpKeys.Contains(site.Id)))
        {
            if (_secureHostConnection)
            {
                if (_confirm is null)
                    _ = ReviewSftpKeyAsync(site, () => { if (FreshSite(site.Id) is { } fresh) ChoosePaneLocation(index, fresh); });
                return;
            }
            // The key review needs a secure connection to the Host: show the pane on this site with the reason instead of doing nothing.
            ResetPane(pane, site, null);
            pane.Error = "This server's key must be reviewed over a secure connection to the Host.";
            UpdateFolderUi();
            return;
        }
        if (NeedsCloudSignIn(site))
        {
            _pendingChoice = (index, site.Id);
            BeginCloudSignIn(site);
            return;
        }
        ResetPane(pane, site, null);
        UpdateFolderUi();
        _ = BrowsePaneAsync(pane, null, false);
    }

    /// <summary>Choosing the location the other pane shows swaps the panes, so both stay visible and the chosen one is in tab <paramref name="index"/>.</summary>
    private void SwapPanesKeepingActive(int index)
    {
        (_panes[0], _panes[1]) = (_panes[1], _panes[0]);
        _active = index;
        UpdateFolderUi();
    }

    private void SwapPanes()
    {
        (_panes[0], _panes[1]) = (_panes[1], _panes[0]);
        _active = 1 - _active;
        UpdateFolderUi();
    }

    /// <summary>After a sign-in in the folder view: reread the locations, refresh the panes that show it, finish a pending choice.</summary>
    private async Task AfterFolderSignInAsync(string siteId)
    {
        _expiredCloudSites.Remove(siteId);
        try
        {
            var list = await _client.ListAsync(_life.Token);
            if (!SameHost()) return;
            _sites = list.Sites;
        }
        catch (Exception) { /* The panes below still refresh. */ }
        if (!SameHost() || !_inFolder) return;
        if (_pendingChoice is { } pending && pending.SiteId == siteId)
        {
            _pendingChoice = null;
            if (FreshSite(siteId) is { } fresh) { ChoosePaneLocation(pending.Index, fresh); return; }
        }
        foreach (var pane in _panes)
            if (pane.Site?.Id == siteId)
            {
                pane.Site = FreshSite(siteId) ?? pane.Site;
                _ = BrowsePaneAsync(pane, pane.Path, true);
            }
    }

    // ---------------------------------------------------------------------------------------------------------------
    // Selection

    private void ToggleSelected(Pane pane, SiteItemDto item)
    {
        if (!SafeName(item.Name)) return;
        if (!pane.Selected.Remove(item.Name))
        {
            if (pane.Selected.Count < MaxSelected) pane.Selected.Add(item.Name);
            else Toast.MakeText(_activity, $"{MaxSelected} selected, the most at once.", ToastLength.Short)?.Show();
        }
        UpdateFolderUi();
    }

    private void SelectAll(Pane pane)
    {
        pane.Selected.Clear();
        var selectable = pane.Rows.Where(item => SafeName(item.Name)).ToList();
        foreach (var item in selectable.Take(MaxSelected)) pane.Selected.Add(item.Name);
        if (selectable.Count > MaxSelected) Toast.MakeText(_activity, $"{MaxSelected} selected, the most at once.", ToastLength.Long)?.Show();
        UpdateFolderUi();
    }

    private List<SiteItemDto> SelectedItems(Pane pane) => pane.Rows.Where(item => pane.Selected.Contains(item.Name)).ToList();

    private void OnRowTapped(SiteItemDto item)
    {
        var pane = Active;
        if (pane.Loading && pane.IsPhone) return;
        if (pane.Selected.Count > 0) { ToggleSelected(pane, item); return; }
        if (item.IsFolder) OpenFolderIn(pane, item);
        else ShowFileSheet(pane, item);
    }
}

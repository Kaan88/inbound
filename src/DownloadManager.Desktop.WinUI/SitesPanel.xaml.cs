using System.Collections.ObjectModel;
using DownloadManager.Contracts;
using Microsoft.UI.Input;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Windows.System;

namespace DownloadManager.Desktop.WinUI;

/// <summary>One line of the folder list (or the ".." line above it). Public because the list's bindings read it by reflection.</summary>
public sealed class SiteItemRow
{
    private readonly bool _showsHere;

    internal SiteItemRow(SiteItemDto item, bool showsHere = false, bool isParent = false)
    {
        Item = item;
        IsParent = isParent;
        _showsHere = showsHere;
        Name = item.Name;
        IsFolder = item.IsFolder;
        SizeText = SitesBrowseRules.SizeText(item.Size, item.IsFolder);
        ModifiedText = SitesBrowseRules.ModifiedText(item.Modified);
        HereText = isParent ? "" : SitesBrowseRules.HereText(item.Here);
        var tone = SitesBrowseRules.HereTone(item.Here);
        HereVisibility = showsHere && !isParent ? Visibility.Visible : Visibility.Collapsed;
        HereSameVisibility = tone == "same" ? Visibility.Visible : Visibility.Collapsed;
        HereOlderVisibility = tone == "older" ? Visibility.Visible : Visibility.Collapsed;
        HereOtherVisibility = tone == "other" ? Visibility.Visible : Visibility.Collapsed;
        FolderVisibility = IsFolder ? Visibility.Visible : Visibility.Collapsed;
        FileVisibility = IsFolder ? Visibility.Collapsed : Visibility.Visible;
    }

    /// <summary>The ".." line: the folder above. It is never counted, selected for a transfer or sorted.</summary>
    internal static SiteItemRow Parent(bool showsHere) => new(new SiteItemDto { Name = "..", IsFolder = true, Size = -1 }, showsHere, isParent: true);

    internal SiteItemDto Item { get; }

    internal bool IsParent { get; }

    internal bool ShowsHere => _showsHere;

    internal SitesBrowseRules.SortItem SortItem => new(Name, IsFolder, Item.Size, Item.Modified, Item.Here);

    internal SitesBrowseRules.PaneLine Line => new(Name, IsFolder, Item.Size, Item.Here, IsParent);

    /// <summary>SITES.5: "On this PC", against the site's linked folder.</summary>
    public string HereText { get; }

    public Visibility HereVisibility { get; }

    public Visibility HereSameVisibility { get; }

    public Visibility HereOlderVisibility { get; }

    public Visibility HereOtherVisibility { get; }

    public Visibility FolderVisibility { get; }

    public Visibility FileVisibility { get; }

    public string Name { get; }

    public bool IsFolder { get; }

    public string SizeText { get; }

    public string ModifiedText { get; }

    public string AccessibleName
    {
        get
        {
            if (IsParent) return "Folder above";
            var parts = new List<string> { IsFolder ? $"Folder {Name}" : Name };
            if (SizeText.Length > 0) parts.Add(SizeText);
            if (ModifiedText.Length > 0) parts.Add($"modified {ModifiedText}");
            if (HereText.Length > 0) parts.Add($"on this PC: {HereText}");
            return string.Join(", ", parts);
        }
    }
}

/// <summary>
/// SITES.3 / SITES.11 R2a: the browse view of one saved site (remote-sites.md, "Clients") as the mockup's card: header with
/// the kind chip, path row (Up, breadcrumbs, Star, Filter), sortable columns, a ".." line, a selection footer and the
/// trouble state with its automatic retry. It asks the Host through <see cref="Browse"/>, <see cref="Star"/> and
/// <see cref="HereLookup"/> and never talks to rclone; the window hosts it and keeps its toolbar in step through
/// <see cref="StateChanged"/>.
/// </summary>
public sealed partial class SitesPanel : UserControl
{
    // Every item of the folder so far, in the order the Host sent them; _view is what the list shows (filtered, sorted, with "..").
    private readonly List<SiteItemRow> _rows = [];
    private readonly ObservableCollection<SiteItemRow> _view = [];
    private SiteDto? _site;
    private string _path = SitesBrowseRules.Root;
    private int _total;
    private string _sortKey = SitesBrowseRules.SortName;
    private bool _sortAscending = true;
    private bool _rebuilding;
    private bool _serverOnly;

    // The Host's name for the listing the rows came from; a later page asks for that listing.
    private string? _listing;
    private int _generation;
    private int _folderEpoch;
    private bool _loading;
    private CancellationTokenSource? _cts;
    // One source for all the "On this PC" lookups of the folder shown (every page, every batch); cancelled and disposed on
    // the next folder and in Stop().
    private CancellationTokenSource? _hereCts;
    private readonly SiteHereCache _hereCache = new();

    // Opening a folder anew or F5: its "On this PC" answers are asked for again rather than reused.
    private bool _askHereAgain;
    private string? _selectAfter;
    private bool _sayIfMissing;

    /// <summary>A name <see cref="Open"/> was to select is not among the rows read (only the first page is read).</summary>
    internal event Action<string>? SelectMissing;
    private bool _failed;

    // The trouble state: the failures in a row, the countdown to the next automatic try, and when this site was last listed.
    private int _autoTries;
    private DateTimeOffset? _troubleSince;
    private DateTimeOffset _retryDue;
    private string _retryWhy = string.Empty;
    private Microsoft.UI.Xaml.DispatcherTimer? _retryTimer;
    private DateTimeOffset? _lastListedAt;
    // The countdown stopped because the window was hidden; it goes on when the window shows again.
    private bool _retryPaused;
    private XamlRoot? _watchedRoot;

    // What the user selected, by name, kept while the Filter hides some of it; changed only by the user's selection
    // (a rebuild of the list does not change it) and emptied when another folder or listing loads.
    private readonly HashSet<string> _selectedNames = new(StringComparer.Ordinal);

    // The first row the last page added that the list shows: Load more's last page gives it the keyboard.
    private SiteItemRow? _firstAdded;

    // SITES.11 R2c: drag and drop, built on Windows only (PaneDrag.cs).
    partial void WireDrag();

    public SitesPanel()
    {
        InitializeComponent();
        ItemsList.ItemsSource = _view;
        KeyDown += OnKeyDown;
        LostFocus += OnLostFocus;
        // The list takes Enter for itself (Uno marks it handled before it reaches the panel), so Enter is read on the way in.
        ItemsList.PreviewKeyDown += ItemsList_PreviewKeyDown;
        ItemsList.ContextRequested += ItemsList_ContextRequested;
        WireDrag();
        // SITES.4: Download follows what is selected.
        ItemsList.SelectionChanged += (_, e) =>
        {
            if (_rebuilding) return;
            foreach (var row in e.RemovedItems.OfType<SiteItemRow>()) _selectedNames.Remove(row.Name);
            foreach (var row in e.AddedItems.OfType<SiteItemRow>())
            {
                if (!row.IsParent) _selectedNames.Add(row.Name);
            }

            SyncFooter();
            StateChanged?.Invoke();
        };
        Titles.SetColumns([
            new PaneColumn(SitesBrowseRules.SortName, "Name", 0),
            new PaneColumn(SitesBrowseRules.SortSize, "Size", 70, RightAligned: true),
            new PaneColumn(SitesBrowseRules.SortModified, "Modified", 100),
            new PaneColumn(SitesBrowseRules.SortHere, "On this PC", 100),
        ]);
        Titles.SortRequested += key =>
        {
            (_sortKey, _sortAscending) = SitesBrowseRules.NextSort(_sortKey, _sortAscending, key);
            Titles.SetSort(_sortKey, _sortAscending);
            RebuildView();
        };
        Titles.SetSort(_sortKey, _sortAscending);
        Bar.CrumbClicked += path => OpenFolder(path);
        Bar.UpClicked += () => GoUp();
        Bar.StarClicked += () => _ = ToggleStarAsync();
        Bar.FilterChanged += _ => RebuildView();
        AutomationProperties.SetName(this, "Site browser");
        // The automatic retry counts down only while the window is shown (idle wake-ups): XamlRoot.Changed says when the
        // window is hidden or shown again.
        Loaded += (_, _) => WatchRoot();
        Unloaded += (_, _) =>
        {
            PauseRetry();
            UnwatchRoot();
        };
        SyncChrome();
    }

    /// <summary>Reads one page of a folder from the Host.</summary>
    /// <remarks>The flag is true for a load the user started (open, a folder, Retry, Refresh, Load more), false for the panel's own.</remarks>
    internal Func<SiteBrowseRequest, bool, CancellationToken, Task<SiteBrowseResultDto>>? Browse { get; set; }

    /// <summary>Stars or unstars a folder at the Host.</summary>
    internal Func<SiteStarRequest, Task<SiteResultDto>>? Star { get; set; }

    /// <summary>SITES.11: compares the folders of a page with this PC (sites.here); null when the Host can't.</summary>
    internal Func<SiteHereRequest, CancellationToken, Task<SiteHereResultDto?>>? HereLookup { get; set; }

    /// <summary>
    /// A host-key problem or a switch that is off is fixed in Options > Sites. The window decides how that opens, so the
    /// dialogs built there (host-key trust, Add a site) can replace it later.
    /// </summary>
    internal Action? OpenSitesOptions { get; set; }

    /// <summary>
    /// SITES.11 R4: the button beside a problem the user fixes (<see cref="SitesBrowseRules.FixButtonText"/>), with the remote
    /// and the problem. Without it the button opens Options > Sites.
    /// </summary>
    internal Action<SiteDto, string?>? FixSite { get; set; }

    // The problem the fix button was shown for.
    private string? _fixProblem;

    /// <summary>"Edit site" in the trouble state: the window opens the site's edit dialog.</summary>
    internal event Action<SiteDto>? EditSiteRequested;

    /// <summary>The path, "can go up" or "is starred" changed, so the toolbar follows.</summary>
    internal event Action? StateChanged;

    /// <summary>A star changed the site; the window keeps its copy (and the sidebar) in step.</summary>
    internal event Action<SiteDto>? SiteUpdated;

    /// <summary>Server only: the Filter box shows and the footer says where downloads go.</summary>
    internal bool ServerOnly
    {
        get => _serverOnly;
        set
        {
            if (_serverOnly == value) return;
            _serverOnly = value;
            Bar.ShowFilter = value;
            SyncFooter();
        }
    }

    internal string? SiteId => _site?.Id;

    /// <summary>The site shown, as last saved.</summary>
    internal SiteDto? Site => _site;

    /// <summary>The names listed so far in the folder shown (for a free "New folder" name).</summary>
    internal IEnumerable<string> ShownNames => _rows.Select(row => row.Name);

    /// <summary>SITES.5: F2 and Delete on the list; the window asks and acts.</summary>
    internal event Action? RenameRequested;
    internal event Action? DeleteRequested;

    private IEnumerable<SiteItemRow> SelectedRows => ItemsList.SelectedItems.OfType<SiteItemRow>().Where(row => !row.IsParent);

    /// <summary>SITES.4: the selected files and folders of the folder shown, for Download. The ".." line is never one.</summary>
    internal List<SiteTransferNameDto> SelectedNames =>
        [.. SitesBrowseRules.SelectedForAction(SelectedRows.Select(row => row.Line)).Select(line => new SiteTransferNameDto { Name = line.Name, IsFolder = line.IsFolder })];

    internal string Path => _path;

    // True from opening at the site's start folder until a folder has loaded: the start folder is the Host's to say
    // (null asks for it), so Try again and opening the site again ask for it again rather than for "/".
    private bool _atStart;

    /// <summary>Where opening this site again should go: the folder shown, or null while the start folder never loaded.</summary>
    internal string? ReopenPath => _atStart ? null : _path;

    // What the shown place depends on; when it changes the place is not kept.
    private string? _openedAs;

    private static string StartOf(SiteDto site) => $"{site.Host}\n{site.Port}\n{site.User}\n{site.RemoteFolder}";

    /// <summary>True when this site is shown and its server, user and start folder are the ones it was opened with.</summary>
    internal bool ShowsSameStart(SiteDto site) => _site?.Id == site.Id && StartOf(site) == _openedAs;

    internal bool CanGoUp => !SitesBrowseRules.IsRoot(_path);

    internal bool IsStarred => SitesBrowseRules.IsStarred(_site?.Stars, _path);

    internal bool IsLoading => _loading;

    /// <summary>
    /// Shows a site, at <paramref name="path"/> or, with null, at the folder the site starts in; <paramref name="select"/> is the
    /// name to select in it once listed.
    /// </summary>
    /// <param name="byUser">False when the panel opens the site again itself (its start changed).</param>
    internal void Open(SiteDto site, string? path = null, string? select = null, bool sayIfMissing = false, bool byUser = true)
    {
        _sayIfMissing = sayIfMissing && select is not null;
        if (_site?.Id != site.Id)
        {
            _lastListedAt = null;
            _autoTries = 0;
            _troubleSince = null;
        }

        _site = site;
        _openedAs = StartOf(site);
        SyncHeader();
        _path = path is null ? SitesBrowseRules.Root : SitesBrowseRules.Normalize(path);
        _atStart = path is null;
        _askHereAgain = true;
        _selectAfter = select;
        _ = LoadAsync(path, refresh: false, append: false, byUser: byUser);
    }

    /// <summary>The site's saved data changed (renamed, stars): the name and the starred list follow, the folder stays.</summary>
    internal void UpdateSite(SiteDto site)
    {
        if (_site?.Id != site.Id) return;
        // Another server, user or start folder: the folder shown may not be there, so the view starts again.
        if (StartOf(site) != _openedAs)
        {
            Open(site, byUser: false);
            StateChanged?.Invoke();
            return;
        }

        var reconnect = SitesBrowseRules.ConnectionKey(_site) != SitesBrowseRules.ConnectionKey(site);
        _site = site;
        SyncHeader();
        SyncChrome();
        StateChanged?.Invoke();
        // In trouble, and the site's connection data changed (Edit site, a key, a sign-in): that may be the fix, so the
        // folder is read again now. Other broadcasts (its own reach, other sites) are not, and a failed Load more page
        // (rows still shown) is never reloaded from here: that would wipe the list.
        if (reconnect && _failed && !_loading && _rows.Count == 0)
        {
            _autoTries = 0;
            _troubleSince = null;
            Reload();
        }
    }

    private void SyncHeader()
    {
        if (_site is not { } site) return;
        SiteNameText.Text = site.Name;
        var chip = SitesBrowseRules.KindChipText(site.Kind);
        KindChipText.Text = chip;
        KindChip.Visibility = chip.Length > 0 ? Visibility.Visible : Visibility.Collapsed;
        PlainChip.Visibility = site.Encrypted ? Visibility.Collapsed : Visibility.Visible;
    }

    internal void GoUp()
    {
        if (SitesBrowseRules.Parent(_path) is { } parent) OpenFolder(parent, selectName: SitesBrowseRules.LeafName(_path));
    }

    internal void Reload() => _ = LoadAsync(ReopenPath, refresh: true, append: false);

    /// <summary>F5 and Refresh: the folder and its "On this PC" answers are read again.</summary>
    internal void Refresh()
    {
        _askHereAgain = true;
        _ = LoadAsync(ReopenPath, refresh: true, append: false, byUser: true);
    }

    /// <summary>Retry now: a new spell of automatic tries starts if this one fails too.</summary>
    private void RetryNow()
    {
        _autoTries = 0;
        _troubleSince = null;
        _ = LoadAsync(ReopenPath, refresh: true, append: false, byUser: true);
    }

    private void CancelHere()
    {
        if (_hereCts is not { } cts) return;
        _hereCts = null;
        cts.Cancel();
        cts.Dispose();
    }

    /// <summary>Lists the folder again and selects <paramref name="selectName"/> in it (a new folder, a renamed item).</summary>
    internal void Reload(string? selectName)
    {
        _selectAfter = selectName;
        Reload();
    }

    /// <summary>Ctrl+F in Server only: the keyboard goes to the Filter box.</summary>
    internal void FocusFilter() => Bar.FocusFilter();

    /// <summary>Stars or unstars <paramref name="folder"/> (a folder row of the list), or the folder shown with null.</summary>
    internal async Task ToggleStarAsync(string? folder = null)
    {
        if (_site is not { } site || Star is null) return;
        var path = folder ?? _path;
        var want = !SitesBrowseRules.IsStarred(site.Stars, path);
        SiteResultDto result;
        try
        {
            result = await Star(new SiteStarRequest { Id = site.Id, Path = path, Starred = want });
        }
        catch (Exception ex) when (ex is InvalidOperationException or IOException or TimeoutException)
        {
            result = new SiteResultDto { Message = ex.Message };
        }

        if (_site?.Id != site.Id) return;
        if (result.Ok && result.Site is { } updated)
        {
            _site = updated;
            SyncChrome();
            SiteUpdated?.Invoke(updated);
            StateChanged?.Invoke();
            Say(want ? $"Starred {SitesBrowseRules.LeafName(path)}" : $"Removed the star from {SitesBrowseRules.LeafName(path)}");
            return;
        }

        Say($"Could not {(want ? "star" : "unstar")} this folder: {(string.IsNullOrWhiteSpace(result.Message) ? "the background program did not say why." : result.Message)}");
    }

    /// <summary>Gives the keyboard to the list.</summary>
    internal void FocusList()
    {
        if (_view.Count > 0)
        {
            if (ItemsList.SelectedIndex < 0) ItemsList.SelectedIndex = _view.Count > 1 && _view[0].IsParent ? 1 : 0;
            ItemsList.Focus(FocusState.Programmatic);
        }
        else if (MessagePanel.Visibility == Visibility.Visible && RetryButton.Visibility == Visibility.Visible)
        {
            RetryButton.Focus(FocusState.Programmatic);
        }
        else
        {
            // Still loading or empty: the panel itself holds the keyboard. Focusing it while it is not a tab stop would
            // give it to its first button (Starred, seen on WinUI); the load hands it on to the list or to Try again.
            IsTabStop = true;
            if (!Focus(FocusState.Programmatic)) IsTabStop = false;
        }
    }

    /// <summary>Stops what is loading and the automatic retry; the window leaves the view.</summary>
    internal void Stop()
    {
        _cts?.Cancel();
        CancelHere();
        _folderEpoch++;
        _generation++;
        _loading = false;
        StopRetry();
        // The view is left: the panel no longer holds the keyboard (a collapsed element's LostFocus is not relied on).
        IsTabStop = false;
    }

    private void OpenFolder(string path, string? selectName = null)
    {
        _askHereAgain = true;
        _atStart = false;
        _path = SitesBrowseRules.Normalize(path);
        _selectAfter = selectName;
        _ = LoadAsync(_path, refresh: false, append: false, byUser: true);
    }

    private async Task LoadAsync(string? path, bool refresh, bool append, bool byUser = false)
    {
        if (_site is not { } site || Browse is null) return;
        _cts?.Cancel();
        StopRetry();
        var cts = _cts = new CancellationTokenSource();
        // Every path below ends the loading state: the list, a message or the trouble state. Only a newer load (which
        // then owns it) or Stop() (which clears it) skips that. The client also stops waiting after 90 s, should the
        // Host (which gives up after 60 s) never answer.
        var wait = CancellationTokenSource.CreateLinkedTokenSource(cts.Token);
        wait.CancelAfter(TimeSpan.FromSeconds(90));
        var generation = ++_generation;
        var offset = append ? _rows.Count : 0;
        _firstAdded = null;
        _loading = true;
        if (append)
        {
            // WinUI moves the keyboard off a button that is disabled (to the next tab stop, the status bar here); the
            // panel holds it while the page loads and Load more gets it back.
            if (XamlRoot is not null && ReferenceEquals(FocusManager.GetFocusedElement(XamlRoot), MoreButton))
            {
                IsTabStop = true;
                if (!Focus(FocusState.Programmatic)) IsTabStop = false;
            }

            MoreButton.IsEnabled = false;
        }
        else
        {
            CancelHere();
            _folderEpoch++;
            HoldKeyboard();
            _rows.Clear();
            _view.Clear();
            _selectedNames.Clear();
            _total = 0;
            ShowMessage("Loading…", busy: true, retry: false, options: false);
        }

        SyncChrome();
        StateChanged?.Invoke();

        SiteBrowseResultDto result;
        try
        {
            result = await Browse(new SiteBrowseRequest
            {
                Id = site.Id,
                Path = path,
                Offset = offset,
                Limit = SitesBrowseRules.PageSize,
                Refresh = refresh,
                Listing = append ? _listing : null,
            }, byUser, wait.Token);
        }
        catch (OperationCanceledException) when (cts.IsCancellationRequested)
        {
            // Only a newer load or Stop() cancels this source; the newer load owns _loading, and Stop() cleared it.
            return;
        }
        catch (OperationCanceledException)
        {
            // The client's own limit, or a cancellation from below: the folder could not be read.
            result = new SiteBrowseResultDto { Message = "The background program did not answer in time." };
        }
        catch (Exception ex)
        {
            // Any other failure of the request ends the loading state too (a view left on "Loading…" for good is worse
            // than a plain message); it is traced for the log.
            UiTrace.Write($"Listing a site folder failed: {ex.GetType().Name}");
            result = new SiteBrowseResultDto { Message = ex is InvalidOperationException or IOException or TimeoutException ? ex.Message : "The folder could not be read." };
        }
        finally
        {
            wait.Dispose();
        }

        // A newer request, another site, or the view left: this answer is no longer wanted.
        if (generation != _generation || _site?.Id != site.Id) return;
        _loading = false;

        // The Host no longer has the listing the shown rows came from: the folder is read again from the top rather than
        // mixing two listings.
        if (append && !result.Ok && result.Problem == SiteProblems.ListingChanged)
        {
            _ = LoadAsync(path, refresh: false, append: false);
            return;
        }

        if (!result.Ok)
        {
            _failed = true;
            MoreButton.IsEnabled = true;
            if (!append)
            {
                _rows.Clear();
                _view.Clear();
                ShowTrouble(site, result);
            }
            else
            {
                var words = SitesBrowseRules.ProblemText(result.Problem, result.Message, result.OfferedFingerprint, result.AcceptedFingerprints);
                _fixProblem = result.Problem;
                OptionsButton.Content = SitesBrowseRules.FixButtonText(result.Problem);
                ShowMessage(words, busy: false, retry: true, options: SitesBrowseRules.PointsToOptions(result.Problem));
            }

            PassHeldKeyboard();
            SyncChrome();
            StateChanged?.Invoke();
            return;
        }

        _failed = false;
        _autoTries = 0;
        _troubleSince = null;
        _lastListedAt = DateTimeOffset.Now;
        _atStart = false;
        _listing = result.Listing;
        _path = SitesBrowseRules.Normalize(string.IsNullOrEmpty(result.Path) ? path : result.Path);
        var showsHere = SitesBrowseRules.ShowsHere(_site);
        var added = new List<SiteItemRow>();
        foreach (var item in result.Items) added.Add(new SiteItemRow(item, showsHere));
        _rows.AddRange(added);
        _total = Math.Max(result.Total, _rows.Count);
        // A page with nothing in it ends paging, so a Host that miscounts cannot keep the button asking forever.
        if (result.Items.Count == 0) _total = _rows.Count;

        if (_rows.Count == 0)
        {
            _view.Clear();
            ShowMessage("This folder is empty.", busy: false, retry: false, options: false);
        }
        else
        {
            MessagePanel.Visibility = Visibility.Collapsed;
            LoadingRing.IsActive = false;
            ItemsList.Visibility = Visibility.Visible;
        }

        Titles.Visibility = Visibility.Visible;
        // A next page goes in at its sorted places, so the list keeps its scroll and the keyboard stays where it is.
        if (append) InsertRows(added);
        else RebuildView();
        SyncChrome();
        StateChanged?.Invoke();
        if (!append) RestoreSelection();
        if (!append && _askHereAgain)
        {
            _askHereAgain = false;
            _hereCache.Forget(site.Id, _path);
        }

        _ = ResolveFoldersAsync(site, _path, added);
    }

    /// <summary>
    /// SITES.11: a folder of a linked site comes with "folderHere" (it exists on this PC); the Host reads it level by level
    /// and says Same, Older here, Not here or Some of it. Asked once per page; until the answer the line shows nothing
    /// extra, and a null answer leaves it as it was.
    /// </summary>
    private async Task ResolveFoldersAsync(SiteDto site, string path, IReadOnlyList<SiteItemRow> page)
    {
        if (HereLookup is null || !SitesBrowseRules.ShowsHere(site)) return;
        var epoch = _folderEpoch;
        var now = DateTimeOffset.Now;
        var known = new List<(string Name, string Here)>();
        var ask = new List<string>();
        foreach (var row in page)
        {
            if (!row.IsFolder || row.Item.Here != SiteHereStates.FolderHere) continue;
            if (_hereCache.TryGet(site.Id, path, row.Name, now, out var here)) known.Add((row.Name, here));
            else ask.Add(row.Name);
        }

        // Answers of the last two minutes (a reload after a change or an automatic retry) are not asked for again.
        if (known.Count > 0) ApplyHere(known);
        if (ask.Count == 0) return;
        var token = (_hereCts ??= new CancellationTokenSource()).Token;
        // The Host takes at most MaxNames a request: the rest go in later requests, one after another.
        foreach (var batch in ask.Chunk(SiteHereRequest.MaxNames))
        {
            SiteHereResultDto? answer;
            try
            {
                answer = await HereLookup(new SiteHereRequest { Id = site.Id, Path = path, Names = [.. batch] }, token);
            }
            catch (OperationCanceledException)
            {
                return;
            }
            catch (Exception ex) when (ex is InvalidOperationException or IOException or TimeoutException)
            {
                return;
            }

            if (epoch != _folderEpoch || _site?.Id != site.Id || answer is not { Ok: true }) return;
            var at = DateTimeOffset.Now;
            var answered = new List<(string Name, string Here)>();
            foreach (var folder in answer.Folders)
            {
                if (folder.Here is null) continue;
                _hereCache.Put(site.Id, path, folder.Name, folder.Here, at);
                answered.Add((folder.Name, folder.Here));
            }

            if (answered.Count > 0) ApplyHere(answered);
        }
    }

    /// <summary>Puts "On this PC" answers on their lines.</summary>
    private void ApplyHere(IReadOnlyList<(string Name, string Here)> answers)
    {
        var pairs = new List<(SiteItemRow Old, SiteItemRow New)>();
        foreach (var (name, here) in answers)
        {
            var at = _rows.FindIndex(row => row.IsFolder && string.Equals(row.Name, name, StringComparison.Ordinal));
            if (at < 0) continue;
            var oldRow = _rows[at];
            var old = oldRow.Item;
            var row = new SiteItemRow(new SiteItemDto { Name = old.Name, IsFolder = true, Size = old.Size, Modified = old.Modified, Here = here },
                oldRow.ShowsHere);
            _rows[at] = row;
            pairs.Add((oldRow, row));
        }

        if (pairs.Count > 0) ReplaceRows(pairs);
    }

    /// <summary>
    /// sites.here answered: each changed line is replaced where it is (the list keeps its scroll, selection and keyboard).
    /// Sorted by "On this PC", the replaced lines then move to their places.
    /// </summary>
    private void ReplaceRows(IReadOnlyList<(SiteItemRow Old, SiteItemRow New)> pairs)
    {
        var focused = XamlRoot is null ? null : FocusManager.GetFocusedElement(XamlRoot) as DependencyObject;
        SiteItemRow? refocus = null;
        _rebuilding = true;
        try
        {
            foreach (var (old, row) in pairs)
            {
                var at = _view.IndexOf(old);
                if (at < 0) continue;
                if (focused is not null && ItemsList.ContainerFromIndex(at) is DependencyObject container && IsInside(focused, container)) refocus = row;
                _view[at] = row;
                if (_selectedNames.Contains(row.Name)) ItemsList.SelectedItems.Add(row);
            }

            if (_sortKey == SitesBrowseRules.SortHere) Resort();
        }
        finally
        {
            _rebuilding = false;
        }

        // The replaced row's container is made again on the next layout pass; the keyboard goes back to it.
        if (refocus is not null)
        {
            var generation = _generation;
            DispatcherQueue.TryEnqueue(Microsoft.UI.Dispatching.DispatcherQueuePriority.Low, () =>
            {
                if (generation != _generation) return;
                ItemsList.UpdateLayout();
                var at = _view.IndexOf(refocus);
                if (at >= 0 && ItemsList.ContainerFromIndex(at) is Control row) row.Focus(FocusState.Programmatic);
            });
        }

        SyncFooter();
        StateChanged?.Invoke();
    }

    /// <summary>Moves lines whose sort place changed, without clearing the list; the selection is put back by name.</summary>
    private void Resort()
    {
        var first = _view.Count > 0 && _view[0].IsParent ? 1 : 0;
        var wanted = _view.Skip(first).ToList();
        wanted.Sort((a, b) => SitesBrowseRules.CompareItems(a.SortItem, b.SortItem, _sortKey, _sortAscending));
        for (var index = 0; index < wanted.Count; index++)
        {
            var at = _view.IndexOf(wanted[index]);
            if (at != index + first) _view.Move(at, index + first);
        }

        foreach (var row in _view)
        {
            if (!row.IsParent && _selectedNames.Contains(row.Name) && !ItemsList.SelectedItems.Contains(row)) ItemsList.SelectedItems.Add(row);
        }
    }

    /// <summary>A next page: each new line goes in at its sorted place (when the filter shows it); nothing is cleared.</summary>
    private void InsertRows(IReadOnlyList<SiteItemRow> added)
    {
        _firstAdded = null;
        _rebuilding = true;
        try
        {
            if (_view.Count == 0 && CanGoUp && _rows.Count > 0) _view.Add(SiteItemRow.Parent(SitesBrowseRules.ShowsHere(_site)));
            var first = _view.Count > 0 && _view[0].IsParent ? 1 : 0;
            foreach (var row in added)
            {
                if (!SitesBrowseRules.FilterMatches(row.Name, Bar.FilterText)) continue;
                int low = first, high = _view.Count;
                while (low < high)
                {
                    var middle = (low + high) / 2;
                    if (SitesBrowseRules.CompareItems(_view[middle].SortItem, row.SortItem, _sortKey, _sortAscending) <= 0) low = middle + 1;
                    else high = middle;
                }

                _view.Insert(low, row);
                _firstAdded ??= row;
                if (_selectedNames.Contains(row.Name)) ItemsList.SelectedItems.Add(row);
            }
        }
        finally
        {
            _rebuilding = false;
        }

        SyncFooter();
        StateChanged?.Invoke();
    }

    /// <summary>
    /// Filters and sorts the rows into what the list shows, with ".." first (a new folder, a sort or a filter). The selection
    /// comes back from what the user selected, so lines the Filter hid are selected again when they show again.
    /// </summary>
    private void RebuildView()
    {
        _rebuilding = true;
        try
        {
            var shown = _rows.Where(row => SitesBrowseRules.FilterMatches(row.Name, Bar.FilterText)).ToList();
            shown.Sort((a, b) => SitesBrowseRules.CompareItems(a.SortItem, b.SortItem, _sortKey, _sortAscending));
            _view.Clear();
            if (CanGoUp && _rows.Count > 0) _view.Add(SiteItemRow.Parent(SitesBrowseRules.ShowsHere(_site)));
            foreach (var row in shown) _view.Add(row);
            if (_selectedNames.Count > 0)
            {
                foreach (var row in _view)
                {
                    if (!row.IsParent && _selectedNames.Contains(row.Name)) ItemsList.SelectedItems.Add(row);
                }
            }
        }
        finally
        {
            _rebuilding = false;
        }

        SyncFooter();
        StateChanged?.Invoke();
    }

    private void RestoreSelection()
    {
        var say = _sayIfMissing;
        _sayIfMissing = false;
        if (_rows.Count == 0)
        {
            if (say && _selectAfter is { } gone) SelectMissing?.Invoke(gone);
            _selectAfter = null;
            return;
        }

        var wanted = _selectAfter;
        _selectAfter = null;
        var found = wanted is null ? null : _rows.FirstOrDefault(item => !item.IsParent && string.Equals(item.Name, wanted, StringComparison.Ordinal));
        if (say && wanted is not null && found is null) SelectMissing?.Invoke(wanted);
        var row = (wanted is null ? null : _view.FirstOrDefault(item => !item.IsParent && string.Equals(item.Name, wanted, StringComparison.Ordinal)))
            ?? _view.FirstOrDefault(item => !item.IsParent) ?? _view.FirstOrDefault();
        if (row is null) return;
        ItemsList.SelectedItem = row;
        ItemsList.ScrollIntoView(row);
        FocusListWhenReady(_generation, tries: 5);
    }

    /// <summary>
    /// Hiding the list while a folder loads would hand the keyboard to the menu bar, where the key that started the load
    /// (Enter, Backspace) lands when it is let go and opens a menu. The panel holds the keyboard meanwhile; it is a tab
    /// stop only while it does.
    /// </summary>
    private void HoldKeyboard()
    {
        // The list, or Try again and the message about to be replaced by "Loading…" (Try again starts a load).
        if (XamlRoot is null || FocusManager.GetFocusedElement(XamlRoot) is not DependencyObject focused
            || !(IsInside(focused, ItemsList) || IsInside(focused, MessagePanel))) return;
        IsTabStop = true;
        if (!Focus(FocusState.Programmatic)) IsTabStop = false;
    }

    private void OnLostFocus(object sender, RoutedEventArgs e)
    {
        // WinUI raises LostFocus later than the move: one from an earlier load may arrive after the panel took the
        // keyboard again, and must not make it stop being a tab stop while it holds it.
        if (ReferenceEquals(e.OriginalSource, this) && !(XamlRoot is not null && ReferenceEquals(FocusManager.GetFocusedElement(XamlRoot), this)))
            IsTabStop = false;
    }

    /// <summary>A load that failed while the panel held the keyboard gives it to Try again.</summary>
    private void PassHeldKeyboard()
    {
        if (XamlRoot is not null && ReferenceEquals(FocusManager.GetFocusedElement(XamlRoot), this) && RetryButton.Visibility == Visibility.Visible)
            RetryButton.Focus(FocusState.Programmatic);
    }

    /// <summary>
    /// The list was hidden while the folder loaded, which took the keyboard away from it (Uno gives it to the menu bar).
    /// The selected row can take it back only once it is laid out again, and a focus moved by the hiding can land after a
    /// first try, so it is tried again on later turns until the keyboard is in the list, a few times at most.
    /// </summary>
    private void FocusListWhenReady(int generation, int tries, DependencyObject? seen = null)
    {
        DispatcherQueue.TryEnqueue(Microsoft.UI.Dispatching.DispatcherQueuePriority.Low, () => FocusRowStep(generation, tries, seen));
    }

    private void FocusRowStep(int generation, int tries, DependencyObject? seen)
    {
        if (generation != _generation || ItemsList.Visibility != Visibility.Visible || XamlRoot is null) return;
        var focused = FocusManager.GetFocusedElement(XamlRoot) as DependencyObject;
        // A dialog or an open menu holds the keyboard: it is the user's.
        if (focused is not null && IsInOpenPopup(focused)) return;
        // The first step takes the keyboard back from wherever hiding the list or Load more sent it (Uno: the menu bar;
        // WinUI: the next tab stop, the sidebar or the status bar). A later step finding it outside the panel somewhere
        // the earlier step did not leave it means the user moved it: it stays there. Leaving the view or opening another
        // place ends the chain through the generation.
        if (seen is not null && focused is not null && !ReferenceEquals(focused, seen) && !IsInside(focused, this)) return;
        // The selected row itself: a row scrolled away is reused for another item and loses the keyboard with it.
        var row = ItemsList.SelectedIndex >= 0 ? ItemsList.ContainerFromIndex(ItemsList.SelectedIndex) as Control : null;
        if (row is null && ItemsList.SelectedIndex >= 0 && tries > 1)
        {
            // Not laid out yet (a list shown for the first time has no rows until its first layout pass, which can come
            // after several turns): try again after the next layout pass, or after a short wait if none comes.
            var done = false;
            var timer = DispatcherQueue.CreateTimer();
            void Next()
            {
                if (done) return;
                done = true;
                ItemsList.LayoutUpdated -= Laid;
                timer.Stop();
                FocusListWhenReady(generation, tries - 1, focused);
            }

            void Laid(object? sender, object args) => Next();
            ItemsList.LayoutUpdated += Laid;
            timer.Interval = TimeSpan.FromMilliseconds(100);
            timer.IsRepeating = false;
            timer.Tick += (_, _) => Next();
            timer.Start();
            return;
        }

        if (row is not null && focused is not null && IsInside(focused, row)) return;
        if (row?.Focus(FocusState.Programmatic) != true) ItemsList.Focus(FocusState.Programmatic);
        if (tries > 1) FocusListWhenReady(generation, tries - 1, XamlRoot is null ? null : FocusManager.GetFocusedElement(XamlRoot) as DependencyObject);
    }

    private static bool IsInside(DependencyObject element, DependencyObject container)
    {
        for (var node = element; node is not null; node = Microsoft.UI.Xaml.Media.VisualTreeHelper.GetParent(node))
        {
            if (ReferenceEquals(node, container)) return true;
        }

        return false;
    }

    // Dialogs, menus and flyouts are shown in popups, each with its own tree.
    private bool IsInOpenPopup(DependencyObject element)
    {
        if (XamlRoot is null) return false;
        foreach (var popup in Microsoft.UI.Xaml.Media.VisualTreeHelper.GetOpenPopupsForXamlRoot(XamlRoot))
        {
            if (popup.Child is { } child && IsInside(element, child)) return true;
        }

        return false;
    }

    /// <summary>Loading, an empty folder, or a failure while more pages are loaded: one plain message.</summary>
    private void ShowMessage(string text, bool busy, bool retry, bool options)
    {
        MessagePanel.Visibility = Visibility.Visible;
        ItemsList.Visibility = busy || _rows.Count == 0 ? Visibility.Collapsed : Visibility.Visible;
        // Loading shows no column titles (nothing to sort yet); an empty folder or a later page's failure keeps them.
        Titles.Visibility = busy ? Visibility.Collapsed : Visibility.Visible;
        LoadingRing.IsActive = busy;
        LoadingRing.Visibility = busy ? Visibility.Visible : Visibility.Collapsed;
        TroubleIcon.Visibility = Visibility.Collapsed;
        TitleText.Visibility = Visibility.Collapsed;
        EditSiteButton.Visibility = Visibility.Collapsed;
        MessageText.Text = text;
        RetryButton.Content = "Retry now";
        RetryButton.Visibility = retry ? Visibility.Visible : Visibility.Collapsed;
        OptionsButton.Visibility = options ? Visibility.Visible : Visibility.Collapsed;
        LiveRegion.Announce(MessageText);
    }

    /// <summary>
    /// The trouble state of the mockup (`trouble`): a warning, "Can't reach X", why, Retry now and Edit site. A server that
    /// did not answer is tried again by itself (10 s, 30 s, 60 s, then every 60 s) with the countdown in the line; a
    /// problem the user fixes keeps its own words and button.
    /// </summary>
    private void ShowTrouble(SiteDto site, SiteBrowseResultDto result)
    {
        MessagePanel.Visibility = Visibility.Visible;
        ItemsList.Visibility = Visibility.Collapsed;
        // As in the mockup: no column titles over the trouble state.
        Titles.Visibility = Visibility.Collapsed;
        LoadingRing.IsActive = false;
        LoadingRing.Visibility = Visibility.Collapsed;
        TroubleIcon.Visibility = Visibility.Visible;
        TitleText.Visibility = Visibility.Visible;
        TitleText.Text = SitesBrowseRules.TroubleTitle(result.Problem, site.Name);
        _retryWhy = SitesBrowseRules.TroubleWhy(result.Problem, result.Message, result.OfferedFingerprint, result.AcceptedFingerprints);
        MessageText.Text = _retryWhy;
        RetryButton.Content = "Retry now";
        RetryButton.Visibility = Visibility.Visible;
        EditSiteButton.Visibility = Visibility.Visible;
        // An address opened once is not edited; its password is asked by the fix button instead.
        EditSiteButton.IsEnabled = SitesHomeRules.CanEdit(site);
        _fixProblem = result.Problem;
        OptionsButton.Content = SitesBrowseRules.FixButtonText(result.Problem);
        OptionsButton.Visibility = SitesBrowseRules.PointsToOptions(result.Problem) ? Visibility.Visible : Visibility.Collapsed;
        LiveRegion.Announce(TitleText);
        if (SitesBrowseRules.IsCantReach(result.Problem)) StartRetry();
    }

    private void StartRetry()
    {
        var now = DateTimeOffset.Now;
        _troubleSince ??= now;
        _retryPaused = false;
        if (!SitesBrowseRules.AutoRetryAllowed(_autoTries, now - _troubleSince.Value))
        {
            MessageText.Text = $"{_retryWhy} {SitesBrowseRules.NotRetryingText}";
            return;
        }

        _retryDue = now.AddSeconds(SitesBrowseRules.RetryDelaySeconds(_autoTries));
        MessageText.Text = $"{_retryWhy} {SitesBrowseRules.RetryAtText(_retryDue.LocalDateTime)}";
        _retryTimer ??= CreateRetryTimer();
        if (RetryCanTick) ArmRetry();
        else _retryPaused = true;
    }

    /// <summary>One one-shot wait until the try is due (no ticking countdown: the window is not woken every second).</summary>
    private void ArmRetry()
    {
        if (_retryTimer is not { } timer) return;
        var left = _retryDue - DateTimeOffset.Now;
        timer.Interval = left > TimeSpan.FromMilliseconds(50) ? left : TimeSpan.FromMilliseconds(50);
        timer.Start();
    }

    private bool RetryCanTick => XamlRoot is { IsHostVisible: true } && Visibility == Visibility.Visible;

    /// <summary>The window was hidden (or the panel unloaded): the timer stops, so a hidden window wakes nothing up.</summary>
    private void PauseRetry()
    {
        if (_retryTimer is not { IsEnabled: true } timer) return;
        timer.Stop();
        _retryPaused = true;
    }

    private void ResumeRetry()
    {
        if (!_retryPaused || !RetryCanTick || _retryTimer is null) return;
        _retryPaused = false;
        ArmRetry();
    }

    private void WatchRoot()
    {
        if (XamlRoot is { } root && !ReferenceEquals(root, _watchedRoot))
        {
            UnwatchRoot();
            _watchedRoot = root;
            root.Changed += Root_Changed;
        }

        ResumeRetry();
    }

    private void UnwatchRoot()
    {
        if (_watchedRoot is { } root) root.Changed -= Root_Changed;
        _watchedRoot = null;
    }

    private void Root_Changed(XamlRoot sender, XamlRootChangedEventArgs args)
    {
        if (RetryCanTick) ResumeRetry();
        else PauseRetry();
    }

    private Microsoft.UI.Xaml.DispatcherTimer CreateRetryTimer()
    {
        var timer = new Microsoft.UI.Xaml.DispatcherTimer();
        timer.Tick += (_, _) =>
        {
            timer.Stop();
            // Hidden meanwhile: the try waits until the window shows again (XamlRoot.Changed).
            if (!RetryCanTick)
            {
                _retryPaused = true;
                return;
            }

            if (_troubleSince is { } since && !SitesBrowseRules.AutoRetryAllowed(_autoTries, DateTimeOffset.Now - since))
            {
                MessageText.Text = $"{_retryWhy} {SitesBrowseRules.NotRetryingText}";
                return;
            }

            _autoTries++;
            Reload();
        };
        return timer;
    }

    private void StopRetry()
    {
        _retryPaused = false;
        _retryTimer?.Stop();
    }

    /// <summary>The breadcrumbs, the starred menu, the footer and the load-more button for what is shown now.</summary>
    private void SyncChrome()
    {
        BuildCrumbs();
        BuildStarredMenu();
        Bar.CanGoUp = CanGoUp;
        Bar.Starred = IsStarred;
        // The Host refuses a star on the root.
        Bar.StarEnabled = !_loading && _site is not null && CanGoUp;
        var showsHere = SitesBrowseRules.ShowsHere(_site);
        Titles.SetColumnVisible(SitesBrowseRules.SortHere, showsHere);
        var more = !_loading && _rows.Count > 0 && SitesBrowseRules.HasMore(0, _rows.Count, _total);
        MoreButton.Visibility = more || (_loading && _rows.Count > 0 && _total > _rows.Count) ? Visibility.Visible : Visibility.Collapsed;
        MoreButton.IsEnabled = more;
        if (_total > _rows.Count)
        {
            var left = _total - _rows.Count;
            MoreButton.Content = $"Load more ({left:N0} left)";
        }

        SyncFooter();
    }

    /// <summary>The footer: the selection summary, or "Last listed 2 min ago" while the folder can't be read.</summary>
    private void SyncFooter()
    {
        if (_loading && _rows.Count == 0)
        {
            CountText.Text = string.Empty;
            return;
        }

        if (_failed && _rows.Count == 0)
        {
            CountText.Text = _lastListedAt is { } listed ? SitesBrowseRules.LastListedText(listed, DateTimeOffset.Now) : string.Empty;
            return;
        }

        var counts = SitesBrowseRules.FooterCounts(_view.Select(row => row.Line), SelectedRows.Select(row => row.Line));
        var filtering = Bar.FilterText.Trim().Length > 0;
        var linked = SitesBrowseRules.ShowsHere(_site);
        Say(SitesBrowseRules.ServerFooter(counts.Shown, filtering ? counts.Shown : _total, counts.Selected, counts.SelectedBytes, counts.Same, linked, _serverOnly,
            _site?.LocalFolder), announce: false);
    }

    private void Say(string text, bool announce = true)
    {
        CountText.Text = text;
        if (announce) LiveRegion.Announce(CountText);
    }

    private void BuildCrumbs()
    {
        // The folder asked for, also while it fails (or has not answered): the start folder stands in for the Host's own answer.
        var crumbs = SitesBrowseRules.Crumbs(SitesCommandRules.AskedPath(_atStart, _site?.RemoteFolder, _path));
        var parts = new List<(string Text, string Key)>();
        for (var index = 0; index < crumbs.Count; index++)
            parts.Add((index == 0 ? (_site?.Name is { Length: > 0 } name ? name : "/") : crumbs[index].Name, crumbs[index].Path));
        Bar.SetCrumbs(parts);
    }

    private void BuildStarredMenu()
    {
        var menu = Bar.StarFlyout;
        menu.Items.Clear();
        var stars = SitesBrowseRules.OrderedStars(_site?.Stars);
        if (stars.Count == 0)
        {
            menu.Items.Add(new MenuFlyoutItem { Text = "No starred folders yet", IsEnabled = false });
            return;
        }

        foreach (var star in stars)
        {
            var item = new MenuFlyoutItem { Text = SitesBrowseRules.StarLabel(star, stars), Tag = star };
            ToolTipService.SetToolTip(item, star);
            AutomationProperties.SetName(item, $"Open starred folder {star}");
            item.Click += StarredItem_Click;
            menu.Items.Add(item);
        }
    }

    private void StarredItem_Click(object sender, RoutedEventArgs e)
    {
        if (sender is FrameworkElement { Tag: string path }) OpenFolder(path);
    }

    private void ItemsList_DoubleTapped(object sender, DoubleTappedRoutedEventArgs e)
    {
        // Only a double click on a row acts: empty space or the scrollbar leaves the selection alone.
        if (RowOf(e.OriginalSource as DependencyObject) is null) return;
        ActivateSelection();
    }

    /// <summary>The window manages another Inbound server: Open and Download to… are left out of the menu.</summary>
    internal bool OtherServer { get; set; }

    /// <summary>Every page of the folder shown is loaded (an upload's "already there" check can trust the names).</summary>
    internal bool ListingComplete => !_loading && !_failed && _rows.Count >= _total;

    private void ItemsList_PreviewKeyDown(object sender, KeyRoutedEventArgs e)
    {
        var count = SelectedNames.Count;
        switch (e.Key)
        {
            case VirtualKey.F2 when count == 1:
                RenameRequested?.Invoke();
                e.Handled = true;
                break;
            case VirtualKey.Delete when count > 0:
                DeleteRequested?.Invoke();
                e.Handled = true;
                break;
            // Ctrl+Enter is Open (the window's); a plain Enter opens a folder or downloads at once, with no dialog.
            case VirtualKey.Enter when ItemsList.SelectedItems.Count > 0 && !InputKeyboardSource.GetKeyStateForCurrentThread(VirtualKey.Control).HasFlag(Windows.UI.Core.CoreVirtualKeyStates.Down):
                ActivateSelection();
                e.Handled = true;
                break;
            // Uno's list does not move on Home and End; WinUI's does the same thing.
            case VirtualKey.Home or VirtualKey.End when _view.Count > 0:
                var index = e.Key == VirtualKey.Home ? 0 : _view.Count - 1;
                ItemsList.SelectedIndex = index;
                ItemsList.ScrollIntoView(_view[index]);
                FocusListWhenReady(_generation, tries: 5);
                e.Handled = true;
                break;
        }
    }

    /// <summary>A command of the context menu (and Enter on a file) the window carries out.</summary>
    internal event Action<SiteCommand>? CommandRequested;

    /// <summary>Goes to <paramref name="path"/> of this site ("Paste address", an open request).</summary>
    internal void GoTo(string path) => OpenFolder(SitesBrowseRules.Normalize(path));

    /// <summary>The folder row that is selected alone, or null: what "Open" opens in the pane.</summary>
    internal string? SelectedFolderPath =>
        SelectedRows.Take(2).ToList() is [{ IsFolder: true } only] ? SitesBrowseRules.Join(_path, only.Name) : null;

    /// <summary>
    /// Enter or a double click (decisions 1 and 2): ".." goes up, a folder opens, files download at once with no dialog.
    /// </summary>
    private void ActivateSelection()
    {
        var lines = ItemsList.SelectedItems.OfType<SiteItemRow>().Select(row => row.Line).ToList();
        switch (SitesCommandRules.Activate(lines))
        {
            case ActivateAction.GoUp:
                GoUp();
                break;
            case ActivateAction.OpenFolder:
                if (SelectedFolderPath is { } folder) OpenFolder(folder);
                break;
            case ActivateAction.Download:
                CommandRequested?.Invoke(SiteCommand.Download);
                break;
        }
    }

    /// <summary>
    /// Right click (the row under the pointer is selected first unless it already is), Shift+F10 or the menu key (the
    /// selection). Items the site cannot do are left out; ".." and empty space show the empty-space menu.
    /// </summary>
    private void ItemsList_ContextRequested(UIElement sender, ContextRequestedEventArgs e)
    {
        if (_site is null || _loading || ItemsList.Visibility != Visibility.Visible) return;
        var byPointer = e.TryGetPosition(ItemsList, out var point);
        if (byPointer)
        {
            var under = RowOf(e.OriginalSource as DependencyObject);
            if (under is null)
            {
                if (ItemsList.SelectedItems.Count > 0) ItemsList.SelectedItems.Clear();
            }
            else if (!ItemsList.SelectedItems.Contains(under))
            {
                ItemsList.SelectedItems.Clear();
                ItemsList.SelectedItems.Add(under);
            }
        }

        var menu = BuildContextMenu();
        e.Handled = true;
        if (byPointer)
        {
            menu.ShowAt(ItemsList, new Microsoft.UI.Xaml.Controls.Primitives.FlyoutShowOptions { Position = point });
            return;
        }

        var selected = ItemsList.SelectedIndex >= 0 ? ItemsList.ContainerFromIndex(ItemsList.SelectedIndex) as FrameworkElement : null;
        menu.ShowAt(selected ?? ItemsList);
    }

    private SiteItemRow? RowOf(DependencyObject? node)
    {
        for (; node is not null; node = Microsoft.UI.Xaml.Media.VisualTreeHelper.GetParent(node))
        {
            if (node is ListViewItem item) return ItemsList.ItemFromContainer(item) as SiteItemRow;
        }

        return null;
    }

    private MenuFlyout BuildContextMenu()
    {
        var menu = new MenuFlyout();
        var groups = SitesCommandRules.ServerMenu(_site?.Capabilities, ItemsList.SelectedItems.OfType<SiteItemRow>().Select(row => row.Line), OtherServer);
        var folderRow = SelectedRows.Take(2).ToList() is [{ IsFolder: true } only] ? SitesBrowseRules.Join(_path, only.Name) : null;
        foreach (var group in groups)
        {
            if (menu.Items.Count > 0) menu.Items.Add(new MenuFlyoutSeparator());
            foreach (var command in group)
            {
                var starred = command == SiteCommand.StarFolder && SitesBrowseRules.IsStarred(_site?.Stars, folderRow);
                var item = new MenuFlyoutItem { Text = starred ? "Unstar this folder" : SitesCommandRules.Label(command) };
                if (SitesCommandRules.KeyText(command) is { Length: > 0 } key) item.KeyboardAcceleratorTextOverride = CommandKey.Text(key);
                var chosen = command;
                item.Click += (_, _) =>
                {
                    if (chosen == SiteCommand.StarFolder) _ = ToggleStarAsync(folderRow);
                    else CommandRequested?.Invoke(chosen);
                };
                menu.Items.Add(item);
            }
        }

        return menu;
    }

    private void RetryButton_Click(object sender, RoutedEventArgs e) => RetryNow();

    private void EditSiteButton_Click(object sender, RoutedEventArgs e)
    {
        if (_site is { } site) EditSiteRequested?.Invoke(site);
    }

    private void OptionsButton_Click(object sender, RoutedEventArgs e)
    {
        if (_site is { } site && FixSite is { } fix) fix(site, _fixProblem);
        else OpenSitesOptions?.Invoke();
    }

    private async void MoreButton_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            var generation = _generation + 1;
            await LoadAsync(_path, refresh: false, append: true, byUser: true);
            // Another folder or site was opened meanwhile: its rows are not the ones this page added.
            if (generation != _generation) return;
            // More is left and the panel held the keyboard for the load: back to Load more, for the next page.
            if (MoreButton.Visibility == Visibility.Visible && MoreButton.IsEnabled && XamlRoot is not null
                && ReferenceEquals(FocusManager.GetFocusedElement(XamlRoot), this))
            {
                MoreButton.Focus(FocusState.Programmatic);
                return;
            }

            // The last page took the button (and the keyboard with it) away: the keyboard goes to the first row it added
            // (by reference: the rows are sorted, so the new ones are not at the end).
            if (MoreButton.Visibility == Visibility.Collapsed && ItemsList.Visibility == Visibility.Visible && _firstAdded is { } first && _view.Contains(first))
            {
                ItemsList.SelectedItem = first;
                ItemsList.ScrollIntoView(first);
                FocusListWhenReady(_generation, tries: 5);
            }
        }
        catch (Exception ex)
        {
            UiTrace.Write($"Loading more of a site folder failed: {ex.GetType().Name}");
        }
    }

    private void OnKeyDown(object sender, KeyRoutedEventArgs e)
    {
        // Typing in the Filter box or the path box is the text box's.
        if (e.OriginalSource is TextBox) return;
        var alt = InputKeyboardSource.GetKeyStateForCurrentThread(VirtualKey.Menu).HasFlag(Windows.UI.Core.CoreVirtualKeyStates.Down);
        var ctrl = InputKeyboardSource.GetKeyStateForCurrentThread(VirtualKey.Control).HasFlag(Windows.UI.Core.CoreVirtualKeyStates.Down);
        switch (e.Key)
        {
            case VirtualKey.Back when CanGoUp:
            case VirtualKey.Up when alt && CanGoUp:
                GoUp();
                e.Handled = true;
                break;
            case VirtualKey.F5:
                Refresh();
                e.Handled = true;
                break;
            case VirtualKey.F when ctrl && _serverOnly:
                Bar.FocusFilter();
                e.Handled = true;
                break;
        }
    }
}

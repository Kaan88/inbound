using System.ComponentModel;
using DownloadManager.Contracts;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Markup;

namespace DownloadManager.Desktop.WinUI;

/// <summary>
/// Torrents in the main list (T1 step 5, docs/plans/torrent-t1-design.md): a torrent is one job and one row. An open
/// torrent row lists its files under it as display rows, which no command acts on. Also the Torrents view in the
/// sidebar, the status-bar chip and Choose Files….
/// </summary>
public sealed partial class MainWindow
{
    /// <summary>What an open torrent row lists: its files as last read, and the rows that show them.</summary>
    private sealed class TorrentFilesShown
    {
        public List<TorrentFileDto>? Files;
        public string? Error;

        /// <summary>The torrent's state, file count, metadata and wanted files when the list was last read (<see cref="TorrentRules.FilesReadKey"/>).</summary>
        public (string State, int FileCount, bool HasMetadata, int Wanted, long WantedBytes)? ReadFor;
        public bool Reading;
        /// <summary><see cref="Environment.TickCount64"/> when the last read began.</summary>
        public long ReadAt;

        /// <summary>By file index, so a file keeps its row (and the list its place) from one read to the next.</summary>
        public readonly Dictionary<int, JobViewModel> Rows = [];
        public JobViewModel? More;
        public JobViewModel? Note;
    }

    /// <summary>By torrent job, for the open torrent rows only: a closed row lets its list go.</summary>
    private readonly Dictionary<Guid, TorrentFilesShown> _torrentFiles = [];

    /// <summary>The torrent view (Torrents, or one under it) shown last, which comes back once the Host says whether torrents are on.</summary>
    private string? _pendingTorrentCategory;

    /// <summary>A snapshot has said which jobs there are and how the Host is set up.</summary>
    private bool _torrentSnapshotSeen;

    private MenuFlyoutItem? _menuChooseFiles;

    /// <summary>Torrents whose file choice this window opened by itself: once per torrent, whatever the answer.</summary>
    private readonly HashSet<Guid> _fileChoiceAsked = [];

    /// <summary>A file choice opened by itself is showing, or about to; the next waits for it.</summary>
    private bool _askingFileChoice;

    /// <summary>Torrents whose Progress Details window has been seen open: one closed since is not waited for again.</summary>
    private readonly HashSet<Guid> _progressWindowSeen = [];

    /// <summary>Looks again while a file choice waits for its progress window (<see cref="TorrentRules.ProgressWindowComing"/>).</summary>
    private DispatcherQueueTimer? _fileChoiceRecheck;

    private const string TorrentsText = "Torrents";

    private void InitializeTorrentUi()
    {
        // Its changes are posted to this thread (RemoteClient), so this follows the shown Host's capability.
        _remote.State.PropertyChanged += OnTorrentStateChanged;
        InitializeTorrentControls();
        InitializeTorrentInfo();
        // A file list that came in while another dialog was open, or while the window was hidden, gets its turn once
        // that dialog closes or the window shows again; no torrent update may follow to bring it.
        DialogGuard.Closed += OnDialogClosedForFileChoice;
        Closed += (_, _) => DialogGuard.Closed -= OnDialogClosedForFileChoice;
        AppWindow.Changed += (sender, args) =>
        {
            if (args.DidVisibilityChange && sender.IsVisible) DispatcherQueue.TryEnqueue(OpenArrivedFileChoice);
        };
    }

    private void OnDialogClosedForFileChoice(XamlRoot root)
    {
        // Queued: the closed dialog's popup leaves the tree after this returns.
        if (ReferenceEquals(root, Content?.XamlRoot)) DispatcherQueue.TryEnqueue(OpenArrivedFileChoice);
    }

    private void OnTorrentStateChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName is nameof(RemoteState.TorrentsOffered) or nameof(RemoteState.IsReady)) SyncTorrentViews();
    }

    private List<JobDto> SelectedJobDtos => SelectedJobs.Select(row => row.Dto).OfType<JobDto>().ToList();

    /// <summary>One unfinished torrent with its file list is selected, and the shown Host downloads torrents.</summary>
    private bool CanChooseTorrentFiles => _remote.State.TorrentsOffered && TorrentRules.CanChooseFiles(SelectedJobDtos);

    // ---- The Torrents view and the status-bar chip ------------------------------------------------

    /// <summary>
    /// Shows or hides the Torrents view, as torrents are offered and turned on or torrent jobs are listed, and the
    /// chip. Runs after each snapshot, on torrent job changes and capability changes; Options calls it after a save.
    /// </summary>
    internal void SyncTorrentViews()
    {
        foreach (var gone in _torrentFiles.Keys.Where(id => !_jobsById.ContainsKey(id)).ToList()) _torrentFiles.Remove(gone);
        var anyTorrent = _jobsById.Values.Any(row => row.IsTorrent);
        var show = TorrentRules.SidebarShows(TorrentRules.Applies(_remote.State.TorrentsOffered, _settings), anyTorrent);
        SyncTorrentSidebar(show);
        SyncSeedGoal();

        if (_pendingTorrentCategory is { } pendingTag)
        {
            if (TorrentViewItem(pendingTag) is { } item)
            {
                _pendingTorrentCategory = null;
                CategoryList.SelectedItem = item;
            }
            else if (_torrentSnapshotSeen && _remote.State.IsReady)
            {
                _pendingTorrentCategory = null;
            }
        }

        // The view that was showing has gone.
        if (!show && TorrentListRules.IsTorrentView(_category))
        {
            _category = "all";
            CategoryList.SelectedIndex = 0;
            RebuildVisible();
        }

        UpdateTorrentChip();
        _fileChoiceAsked.RemoveWhere(id => !_jobsById.ContainsKey(id));
        _progressWindowSeen.RemoveWhere(id => !_jobsById.ContainsKey(id));
        OpenArrivedFileChoice();
    }

    private ListBoxItem? TorrentsItem => TorrentViewItem(TorrentRules.SidebarTag);

    private ListBoxItem? TorrentViewItem(string tag)
        => CategoryList.Items.OfType<ListBoxItem>().FirstOrDefault(item => string.Equals(item.Tag as string, tag, StringComparison.OrdinalIgnoreCase));

    /// <summary>DT-5: Torrents and the views under it (mockup a), each with its glyph, words and theme brush.</summary>
    private static readonly (string Tag, string Title, string Glyph, string Brush, double Indent)[] TorrentSidebarItems =
    [
        (TorrentRules.SidebarTag, TorrentsText, "&#xF22C;", "InboundIconMediaBrush", 14),
        (TorrentListRules.DownloadingTag, "Downloading", "&#xE896;", "InboundIconInfoBrush", 30),
        (TorrentListRules.SeedingTag, "Seeding", "&#xE898;", "InboundIconStartBrush", 30),
        (TorrentListRules.StoppedTag, "Stopped", "&#xE769;", "InboundIconPauseBrush", 30),
        // T7: shown only while they hold a torrent (TorrentListRules.ViewShows).
        (TorrentListRules.ActiveTag, "Active", "&#xE945;", "InboundIconInfoBrush", 30),
        (TorrentListRules.StalledTag, "Stalled", "&#xE916;", "InboundIconPauseBrush", 30),
        (TorrentListRules.CheckingTag, "Checking", "&#xE895;", "InboundIconInfoBrush", 30),
        (TorrentListRules.FilesTag, "Waiting for files", "&#xE8FD;", "InboundIconInfoBrush", 30),
        (TorrentListRules.CompletedTag, "Completed", "&#xE73E;", "InboundIconStartBrush", 30),
        (TorrentListRules.ErroredTag, "Errored", "&#xE783;", "InboundIconDangerBrush", 30),
    ];

    /// <summary>The shown torrent sidebar items and their count text, by tag; empty while they are away.</summary>
    private readonly Dictionary<string, (ListBoxItem Item, TextBlock Count)> _torrentSidebar = new(StringComparer.Ordinal);

    /// <summary>The counts the sidebar shows and the view they were shown for, so an update that changes none touches nothing.</summary>
    private (TorrentCounts Main, TorrentExtraCounts Extra, string Category, string? Pending)? _torrentCountsShown;

    /// <summary>Top-level Torrents after Finished, before Extractions and Queues, with Downloading, Seeding and Stopped under it.</summary>
    private void SyncTorrentSidebar(bool show)
    {
        var shown = CategoryList.Items.OfType<ListBoxItem>().Where(item => TorrentListRules.IsTorrentView(item.Tag as string)).ToList();
        if (show == (shown.Count > 0)) return;
        _refreshingCategories = true;
        try
        {
            if (shown.Count > 0)
            {
                foreach (var item in shown) CategoryList.Items.Remove(item);
                _torrentSidebar.Clear();
                _torrentCountsShown = null;
                return;
            }

            var items = CategoryList.Items.OfType<ListBoxItem>().ToList();
            var at = items.FindIndex(existing => existing.Tag as string == ArchiveSetsTag);
            if (at < 0) at = items.FindIndex(existing => existing.Tag is "queues");
            if (at < 0) at = CategoryList.Items.Count;
            // T7: the extra views come in with their first torrent (UpdateTorrentCounts).
            foreach (var (tag, title, glyph, brush, indent) in TorrentSidebarItems)
            {
                if (TorrentListRules.IsExtraTag(tag)) continue;
                CategoryList.Items.Insert(at++, NewTorrentSidebarItem(tag, title, glyph, brush, indent));
            }
        }
        finally
        {
            _refreshingCategories = false;
        }

        UpdateTorrentCounts();
    }

    private ListBoxItem NewTorrentSidebarItem(string tag, string title, string glyph, string brush, double indent)
    {
        var (content, count) = TorrentSidebarContent(glyph, brush, title);
        var added = new ListBoxItem
        {
            Tag = tag,
            Padding = new Thickness(indent, 2, 8, 2),
            HorizontalContentAlignment = HorizontalAlignment.Stretch,
            Content = content,
        };
        AutomationProperties.SetName(added, title);
        _torrentSidebar[tag] = (added, count);
        return added;
    }

    /// <summary>
    /// T7: adds or takes away an extra view under Torrents as it fills or empties (never the one shown). It goes in
    /// after the torrent view before it in <see cref="TorrentSidebarItems"/>' order.
    /// </summary>
    private void SyncExtraTorrentView(string tag, string title, string glyph, string brush, double indent, bool show)
    {
        var present = _torrentSidebar.TryGetValue(tag, out var shown);
        if (show == present) return;
        _refreshingCategories = true;
        try
        {
            if (!show)
            {
                if (ReferenceEquals(CategoryList.SelectedItem, shown.Item)) return;
                CategoryList.Items.Remove(shown.Item);
                _torrentSidebar.Remove(tag);
                return;
            }

            var at = -1;
            foreach (var (earlier, _, _, _, _) in TorrentSidebarItems)
            {
                if (earlier == tag) break;
                if (_torrentSidebar.TryGetValue(earlier, out var before)) at = CategoryList.Items.IndexOf(before.Item);
            }

            if (at < 0) return; // Torrents itself is away
            CategoryList.Items.Insert(at + 1, NewTorrentSidebarItem(tag, title, glyph, brush, indent));
        }
        finally
        {
            _refreshingCategories = false;
        }
    }

    /// <summary>Built from XAML, as the sidebar's own glyphs are, so its brushes follow the theme. The count sits at the right.</summary>
    private static (Grid Content, TextBlock Count) TorrentSidebarContent(string glyph, string brush, string title)
    {
        var content = (Grid)XamlReader.Load(
            "<Grid xmlns=\"http://schemas.microsoft.com/winfx/2006/xaml/presentation\" ColumnSpacing=\"6\">" +
            "<Grid.ColumnDefinitions><ColumnDefinition Width=\"Auto\" /><ColumnDefinition Width=\"*\" /><ColumnDefinition Width=\"Auto\" /></Grid.ColumnDefinitions>" +
            $"<FontIcon Glyph=\"{glyph}\" FontSize=\"14\" Foreground=\"{{ThemeResource {brush}}}\" />" +
            "<TextBlock Grid.Column=\"2\" FontSize=\"11\" VerticalAlignment=\"Center\" Foreground=\"{ThemeResource InboundSecondaryTextBrush}\" />" +
            "</Grid>");
        var words = new TextBlock { Text = title, TextTrimming = TextTrimming.CharacterEllipsis };
        Grid.SetColumn(words, 1);
        content.Children.Add(words);
        return (content, (TextBlock)content.Children[1]);
    }

    /// <summary>
    /// DT-5: how many torrents each torrent view holds, as the list changes or another view is chosen. Nothing runs for
    /// it otherwise. T7: an extra view comes and goes with its count.
    /// </summary>
    private void UpdateTorrentCounts()
    {
        if (_torrentSidebar.Count == 0) return;
        var torrents = _jobsById.Values.Where(row => row.IsTorrent).Select(row => row.Dto).OfType<JobDto>().ToList();
        var counts = TorrentListRules.Count(torrents);
        var extra = TorrentListRules.CountExtras(torrents);
        var key = (counts, extra, _category, _pendingTorrentCategory);
        if (_torrentCountsShown == key) return;
        _torrentCountsShown = key;
        foreach (var (tag, title, glyph, brush, indent) in TorrentSidebarItems)
        {
            var count = TorrentListRules.IsExtraTag(tag) ? extra.Of(tag) : counts.Of(tag);
            if (TorrentListRules.IsExtraTag(tag))
                SyncExtraTorrentView(tag, title, glyph, brush, indent, TorrentListRules.ViewShows(tag, count, _category, _pendingTorrentCategory));
            if (!_torrentSidebar.TryGetValue(tag, out var shown)) continue;
            shown.Count.Text = TorrentListRules.CountText(count);
            AutomationProperties.SetName(shown.Item, TorrentListRules.SpokenItem(title, count));
        }
    }

    /// <summary>DT-6: Seeds, Peers, Up and Ratio take room in the torrent views only. Runs as the list is rebuilt.</summary>
    private void SyncTorrentColumns()
    {
        var show = TorrentListRules.IsTorrentView(_category);
        if (_jobsHeader is null || JobViewModel.ShowTorrentColumns == show) return;
        JobViewModel.ShowTorrentColumns = show;
        foreach (var key in TorrentListRules.ColumnKeys) _jobsHeader.SetColumnVisible(key, show);
        var fileRows = _torrentFiles.Values.SelectMany(files => files.Rows.Values.Append(files.More).Append(files.Note)).OfType<JobViewModel>();
        foreach (var row in _jobsById.Values.Concat(_packageRows.Values).Concat(_remoteRows.Values).Concat(_archiveRows.Values).Concat(fileRows))
            row.NotifyTorrentColumns();
    }

    /// <summary>The seeding goal follows the shown Host's settings; a seeding row reads its way to it again when it changes.</summary>
    private void SyncSeedGoal()
    {
        var goal = SeedGoal.From(_settings);
        if (JobViewModel.SeedGoal == goal) return;
        JobViewModel.SeedGoal = goal;
        foreach (var row in _jobsById.Values) row.RefreshSeedGoal();
    }

    /// <summary>"Torrents: 2 active · 47 peers" while a torrent downloads (the engine runs then); nothing otherwise.</summary>
    private void UpdateTorrentChip()
    {
        UpdateTorrentCounts();
        SyncUploadGraph();
        // T3: and the DHT nodes while this window watches running torrents.
        SyncTorrentWatch();
        var text = TorrentControlRules.ChipText(TorrentRules.ChipText(_jobsById.Values.Select(row => row.Dto).OfType<JobDto>()), _dhtWords);
        var visibility = text is null ? Visibility.Collapsed : Visibility.Visible;
        if (TorrentChip.Visibility != visibility) TorrentChip.Visibility = visibility;
        if (text is null || TorrentChipText.Text == text) return;
        TorrentChipText.Text = text;
        AutomationProperties.SetName(TorrentChip, text);
    }

    private void TorrentChip_Click(object sender, RoutedEventArgs e) => ShowTorrentsView();

    private void ShowTorrentsView()
    {
        SyncTorrentViews();
        if (TorrentsItem is { } item && !ReferenceEquals(CategoryList.SelectedItem, item)) CategoryList.SelectedItem = item;
    }

    // ---- File rows under an open torrent row -------------------------------------------------------

    /// <summary>
    /// The rows under a torrent row: none while it is closed; else its first <see cref="TorrentRules.MostFileRows"/>
    /// files and a row for the rest, or a line while the list is read. Reads the list when it is due.
    /// </summary>
    private List<JobViewModel> TorrentFileRows(JobViewModel row)
    {
        row.SetTorrentExpanded(_uiState.ExpandedPackages.Contains(row.Id));
        if (!row.IsExpanded) return [];
        if (!_torrentFiles.TryGetValue(row.Id, out var shown)) _torrentFiles[row.Id] = shown = new TorrentFilesShown();
        ReadTorrentFilesIfDue(row, shown);
        return FileRowsFrom(row, shown);
    }

    private static List<JobViewModel> FileRowsFrom(JobViewModel row, TorrentFilesShown shown)
    {
        if (shown.Files is not { Count: > 0 } files)
        {
            var words = shown.Error is { } error ? $"Couldn't get the file list: {error}"
                : shown.Files is null ? "Getting the file list…"
                : "The file list isn't known yet.";
            shown.Note ??= new JobViewModel();
            shown.Note.UpdateTorrentNote(RowIdOf(shown.Note), row.Id, words, more: false, problem: shown.Error is not null);
            return [shown.Note];
        }

        var rows = new List<JobViewModel>(Math.Min(files.Count, TorrentRules.MostFileRows) + 1);
        var kept = new HashSet<int>();
        foreach (var file in files.Take(TorrentRules.MostFileRows))
        {
            if (!shown.Rows.TryGetValue(file.Index, out var fileRow)) shown.Rows[file.Index] = fileRow = new JobViewModel();
            fileRow.UpdateTorrentFile(RowIdOf(fileRow), row.Id, file, row.State, row.StatusBrush);
            rows.Add(fileRow);
            kept.Add(file.Index);
        }

        foreach (var gone in shown.Rows.Keys.Where(index => !kept.Contains(index)).ToList()) shown.Rows.Remove(gone);
        if (files.Count > TorrentRules.MostFileRows)
        {
            shown.More ??= new JobViewModel();
            shown.More.UpdateTorrentNote(RowIdOf(shown.More), row.Id, TorrentRules.MoreFiles(files.Count - TorrentRules.MostFileRows), more: true, problem: false);
            rows.Add(shown.More);
        }

        return rows;
    }

    /// <summary>A file row keeps the id it was given first, which the list's selection follows.</summary>
    private static Guid RowIdOf(JobViewModel row) => row.Id == Guid.Empty ? Guid.NewGuid() : row.Id;

    /// <summary>Whether the rows under a torrent row are the ones it should have. False sends the caller to RebuildVisible.</summary>
    private bool TorrentFileRowsInPlace(JobViewModel row)
    {
        var at = _visibleJobs.IndexOf(row);
        if (at < 0) return !_visibleJobs.Any(other => other.TorrentParentId == row.Id);
        var expected = TorrentFileRows(row);
        for (var i = 0; i < expected.Count; i++)
        {
            var place = at + 1 + i;
            if (place >= _visibleJobs.Count || !ReferenceEquals(_visibleJobs[place], expected[i])) return false;
        }

        var next = at + 1 + expected.Count;
        return next >= _visibleJobs.Count || _visibleJobs[next].TorrentParentId != row.Id;
    }

    /// <summary>
    /// The list is read when the row opens, and again when the torrent's state, file count or metadata changes, and
    /// after Choose Files. A list that couldn't be read is tried again only then. While the torrent downloads, its
    /// progress updates read it again (each file's done bytes) at most every <see cref="TorrentRules.FileProgressReadMs"/>;
    /// no timer runs, so nothing is read while nothing changes.
    /// </summary>
    private void ReadTorrentFilesIfDue(JobViewModel row, TorrentFilesShown shown)
    {
        if (shown.Reading || row.Dto is not { } job) return;
        if (shown.ReadFor == TorrentRules.FilesReadKey(job)
            && !TorrentRules.FileProgressDue(job, shown.Files is not null, Environment.TickCount64 - shown.ReadAt)) return;
        _ = ReadTorrentFilesAsync(row.Id, shown);
    }

    private async Task ReadTorrentFilesAsync(Guid jobId, TorrentFilesShown shown)
    {
        shown.Reading = true;
        shown.ReadAt = Environment.TickCount64;
        // Taken before the read: a change while it is out reads the list again once this one is shown.
        var readFor = _jobsById.TryGetValue(jobId, out var before) && before.Dto is { } job ? TorrentRules.FilesReadKey(job) : default((string, int, bool, int, long)?);
        try
        {
            // Never throws but for a cancel, which nothing asks for here; the answer comes back on this thread.
            var result = await _remote.GetTorrentFilesAsync(jobId);
            if (result.Ok)
            {
                shown.Files = result.Files;
                shown.Error = null;
            }
            else if (shown.Files is null)
            {
                shown.Error = string.IsNullOrWhiteSpace(result.Error) ? "the Host didn't answer." : RemoteText.Failure(result.Error);
            }
        }
        catch (Exception ex)
        {
            UiTrace.Write($"Torrent file list unavailable: {ex.GetType().Name}");
            if (shown.Files is null) shown.Error ??= "the Host didn't answer.";
        }
        finally
        {
            shown.Reading = false;
            shown.ReadFor = readFor;
        }

        ShowTorrentFilesRead(jobId, shown);
    }

    /// <summary>The rows follow a list just read, in place, unless rows have to come or go.</summary>
    private void ShowTorrentFilesRead(Guid jobId, TorrentFilesShown shown)
    {
        // Closed, or gone, meanwhile.
        if (!ReferenceEquals(_torrentFiles.GetValueOrDefault(jobId), shown)) return;
        if (!_jobsById.TryGetValue(jobId, out var row) || !_visibleJobs.Contains(row)) return;
        if (!TorrentFileRowsInPlace(row))
        {
            RebuildVisible();
            UpdateStatusText();
        }
    }

    /// <summary>A closed torrent row lets its list go: a big torrent's is long.</summary>
    private void CloseTorrentFiles(Guid jobId) => _torrentFiles.Remove(jobId);

    /// <summary>A torrent left the list: its file rows go with it, and so does its open state.</summary>
    private void ForgetTorrentRow(JobViewModel row)
    {
        _torrentFiles.Remove(row.Id);
        for (var i = _visibleJobs.Count - 1; i >= 0; i--)
        {
            if (_visibleJobs[i].TorrentParentId == row.Id) _visibleJobs.RemoveAt(i);
        }

        if (_uiState.ExpandedPackages.Remove(row.Id)) UiStateStore.Save(_uiState);
    }

    private JobViewModel? TorrentRowOf(JobViewModel fileRow)
        => fileRow.TorrentParentId is { } id && _jobsById.TryGetValue(id, out var row) ? row : null;

    /// <summary>File rows alone stand for their torrents, which the menus act on.</summary>
    private void SelectTorrentsOfFileRows()
    {
        var rows = JobsList.SelectedItems.OfType<JobViewModel>().ToList();
        if (rows.Count == 0 || !rows.All(row => row.IsTorrentFile)) return;
        var torrents = rows.Select(TorrentRowOf).OfType<JobViewModel>().Distinct().Where(_visibleJobs.Contains).ToList();
        if (torrents.Count == 0) return;
        JobsList.SelectedItems.Clear();
        foreach (var torrent in torrents) JobsList.SelectedItems.Add(torrent);
    }

    // ---- Commands -----------------------------------------------------------------------------------

    /// <summary>Right opens a torrent row's files and Left closes them; Left on a file row goes to its torrent.</summary>
    private bool HandleTorrentRowKey(KeyRoutedEventArgs e)
    {
        if (e.Key is not (Windows.System.VirtualKey.Right or Windows.System.VirtualKey.Left)) return false;
        if (JobsList.SelectedItems.Count != 1 || JobsList.SelectedItem is not JobViewModel row) return false;
        if (row.IsTorrentFile)
        {
            if (e.Key != Windows.System.VirtualKey.Left || TorrentRowOf(row) is not { } torrent || !_visibleJobs.Contains(torrent)) return false;
            e.Handled = true;
            JobsList.SelectedItem = torrent;
            (JobsList.ContainerFromItem(torrent) as ListViewItem)?.Focus(FocusState.Keyboard);
            return true;
        }

        if (!row.ShowsTorrentFiles || (e.Key == Windows.System.VirtualKey.Right) == row.IsExpanded) return false;
        e.Handled = true;
        TogglePackage(row);
        return true;
    }

    /// <summary>
    /// A torrent row's own double-click and Enter: Choose Files while it waits for the choice. A file row opens its
    /// torrent's Progress Details, which lists every file. False leaves the row to the usual action.
    /// </summary>
    private async Task<bool> RunTorrentPrimaryAsync()
    {
        if (FirstLocalRow is null && JobsList.SelectedItems.OfType<JobViewModel>().ToList() is [{ IsTorrentFile: true, TorrentParentId: { } parent }])
        {
            OpenDownloadWindow(parent);
            return true;
        }

        if (SelectedJob is { } job && TorrentRules.PrimaryIsChooseFiles(job.Dto) && _remote.State.TorrentsOffered)
        {
            await ChooseTorrentFilesAsync(job.Id);
            return true;
        }

        return false;
    }

    /// <summary>Runs after the context menu's own rules: a torrent has no address to send again and no one file to move.</summary>
    private void ShowTorrentContextItems(IReadOnlyList<JobViewModel> selection)
    {
        if (TorrentRules.BlocksAddressCommands(selection.Select(row => row.Dto).OfType<JobDto>()))
        {
            foreach (var item in new[] { _menuRedownload, _menuRefreshUrl, _menuMoveRename, _menuGroup, _menuUngroup, _menuRenamePackage, _menuOpenWith })
            {
                if (item is not null) item.Visibility = Visibility.Collapsed;
            }
        }

        if (_menuStartSeeding is not null && _menuStopSeeding is not null) SyncSeedingItems(_menuStartSeeding, _menuStopSeeding);
        if (_menuCopyMagnet is not null)
        {
            // Torrents alone: a mixed selection has no one kind of address to copy.
            var jobs = SelectedJobDtos;
            var copies = jobs.Count > 0 && JobsList.SelectedItems.OfType<JobViewModel>().All(row => row.IsTorrent) && TorrentListRules.MagnetsToCopy(jobs) is not null;
            _menuCopyMagnet.Visibility = copies ? Visibility.Visible : Visibility.Collapsed;
            _menuCopyMagnet.Text = TorrentListRules.CopyItemText(jobs.Count);
        }

        if (_menuOpenAsTorrent is not null)
        {
            // T10: one finished .torrent download.
            var opens = selection.Count == 1 && TorrentRules.OpensAsTorrent(selection[0].Dto);
            _menuOpenAsTorrent.Visibility = opens ? Visibility.Visible : Visibility.Collapsed;
        }

        SyncTorrentControlItems();
        if (_menuChooseFiles is null) return;
        var canChoose = CanChooseTorrentFiles;
        _menuChooseFiles.Visibility = canChoose ? Visibility.Visible : Visibility.Collapsed;

        // The step a waiting torrent is held up for stands out.
        _menuChooseFiles.FontWeight = canChoose && TorrentRules.PrimaryIsChooseFiles(SelectedJob?.Dto)
            ? Microsoft.UI.Text.FontWeights.SemiBold
            : Microsoft.UI.Text.FontWeights.Normal;
    }

    private MenuFlyoutItem? _menuOpenAsTorrent;

    // ---- Seeding (T2) ---------------------------------------------------------------------------------

    private MenuFlyoutItem? _menuStartSeeding;
    private MenuFlyoutItem? _menuStopSeeding;

    /// <summary>A seeding start or stop is on its way: the commands wait for it.</summary>
    private bool _seedingBusy;

    /// <summary>Start Seeding and Stop Seeding show for the selected torrents they can act on, on a Host with torrents.</summary>
    private void SyncSeedingItems(MenuFlyoutItem start, MenuFlyoutItem stop)
    {
        var (canStart, canStop) = TorrentSeedRules.Commands(SelectedJobDtos, _remote.State.TorrentsOffered, _settings);
        var startShown = canStart ? Visibility.Visible : Visibility.Collapsed;
        var stopShown = canStop ? Visibility.Visible : Visibility.Collapsed;
        if (start.Visibility != startShown) start.Visibility = startShown;
        if (stop.Visibility != stopShown) stop.Visibility = stopShown;
        if (start.IsEnabled == _seedingBusy) start.IsEnabled = !_seedingBusy;
        if (stop.IsEnabled == _seedingBusy) stop.IsEnabled = !_seedingBusy;
    }

    private async void MenuStartSeeding_Click(object sender, RoutedEventArgs e) => await SetSeedingAsync(on: true);

    private async void MenuStopSeeding_Click(object sender, RoutedEventArgs e) => await SetSeedingAsync(on: false);

    /// <summary>
    /// torrent.seed for each selected torrent it applies to, one after another (on this PC's Host or the shown server);
    /// the status bar says how it went. The rows follow the Host's own updates.
    /// </summary>
    private async Task SetSeedingAsync(bool on)
    {
        if (_seedingBusy || !_remote.State.TorrentsOffered) return;
        var selection = SelectedJobDtos;
        var targets = TorrentSeedRules.Targets(selection, on);
        if (targets.Count == 0) return;
        _seedingBusy = true;
        UpdateButtons();
        var done = 0;
        var failed = 0;
        string? firstError = null;
        try
        {
            foreach (var jobId in targets)
            {
                var result = await _remote.SetTorrentSeedingAsync(jobId, on);
                if (result.Ok) done++;
                else
                {
                    failed++;
                    firstError ??= string.IsNullOrWhiteSpace(result.Error) ? null : RemoteText.Failure(result.Error);
                }
            }
        }
        catch (Exception ex)
        {
            UiTrace.Write($"Seeding change failed: {ex.GetType().Name}");
            failed += targets.Count - done - failed;
        }
        finally
        {
            _seedingBusy = false;
            UpdateButtons();
        }

        var name = targets.Count == 1 && _jobsById.TryGetValue(targets[0], out var row) ? row.FileName : null;
        ShowPassingStatus(TorrentSeedRules.Said(on, name, done, failed, firstError));
    }

    // ---- Copy Magnet Link (DT-4) ----

    private MenuFlyoutItem? _menuCopyMagnet;

    /// <summary>The selected torrents' magnets on the clipboard, one a line; the status bar says so.</summary>
    private void MenuCopyMagnet_Click(object sender, RoutedEventArgs e)
    {
        var jobs = SelectedJobDtos;
        if (TorrentListRules.MagnetsToCopy(jobs) is not { } magnets) return;
        var data = new Windows.ApplicationModel.DataTransfer.DataPackage();
        data.SetText(magnets);
        try
        {
            Windows.ApplicationModel.DataTransfer.Clipboard.SetContent(data);
            ShowPassingStatus(TorrentListRules.CopiedSaid(jobs.Count == 1 ? jobs[0].FileName : null, jobs.Count));
        }
        catch (Exception ex) when (ex is System.Runtime.InteropServices.COMException or UnauthorizedAccessException)
        {
            ShowPassingStatus("Another program is using the clipboard. Try again.");
        }
    }

    /// <summary>Commands for one file and its address say why they don't act on a torrent.</summary>
    private bool SayIfTorrent(string command)
    {
        if (!TorrentRules.BlocksAddressCommands(SelectedJobDtos)) return false;
        ShowPassingStatus($"{command} isn't available for torrents.");
        return true;
    }

    private async void MenuChooseTorrentFiles_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            if (CanChooseTorrentFiles && SelectedJob is { } job) await ChooseTorrentFilesAsync(job.Id);
        }
        catch (Exception ex)
        {
            UiTrace.Write($"Choose files failed: {ex.GetType().Name}");
        }
    }

    /// <summary>
    /// Which of a torrent's files to download: the file tree with the wanted files ticked, then torrent.setFiles. A
    /// Host error is shown. Also opens by itself when a magnet's file list comes in (<see cref="OpenArrivedFileChoice"/>).
    /// </summary>
    internal async Task ChooseTorrentFilesAsync(Guid jobId)
    {
        try
        {
            List<TorrentFileDto>? files = null;
            var answer = await RemoteFileSelectionDialog.AskAsync(
                Content.XamlRoot,
                RootGrid.ActualTheme,
                "Choose files",
                "This torrent",
                async token =>
                {
                    var result = await _remote.GetTorrentFilesAsync(jobId, token);
                    if (!result.Ok)
                    {
                        return new RemoteFilesToShow([], Error: string.IsNullOrWhiteSpace(result.Error) ? "The Host didn't send the file list." : result.Error);
                    }

                    if (result.Files.Count == 0) return new RemoteFilesToShow([], Error: "The file list isn't known yet. Try again once it is.");
                    files = result.Files;
                    var wanted = TorrentRules.WantedIds(result.Files);
                    return new RemoteFilesToShow(TorrentRules.TreeFiles(result.Files), Chosen: wanted.Count > 0 ? wanted : null);
                },
                loadingText: "Getting the file list…");
            if (answer.CouldNotOpen)
            {
                ShowPassingStatus("Choose Files couldn't open, because another dialog is open.");
                return;
            }

            if (answer.Choice is not { } choice || files is null) return;
            var indexes = TorrentRules.Indexes(choice, files);
            if (indexes is not { Count: > 0 })
            {
                await ShowMessageAsync("Choose files", "That choice couldn't be read. Choose the files again.");
                return;
            }

            var set = await _remote.SetTorrentFilesAsync(jobId, indexes);
            if (!set.Ok)
            {
                await ShowMessageAsync("Choose files", string.IsNullOrWhiteSpace(set.Error) ? "The Host didn't take the choice." : RemoteText.Failure(set.Error));
                return;
            }

            var name = _jobsById.TryGetValue(jobId, out var row) ? row.FileName : "The torrent";
            ShowPassingStatus(TorrentRules.ChoiceSaid(name, indexes.Count, files.Count));

            // Its file rows say what was chosen.
            if (_torrentFiles.TryGetValue(jobId, out var shown) && !shown.Reading) _ = ReadTorrentFilesAsync(jobId, shown);
        }
        catch (Exception ex)
        {
            UiTrace.Write($"Choose files failed: {ex.GetType().Name}");
            ShowPassingStatus("Choose Files didn't work. Try again.");
        }
    }

    /// <summary>
    /// A torrent added to ask which files to download opens Choose Files by itself once its file list comes in: one
    /// torrent at a time, only while this window shows and no other dialog is open, and once per torrent. A torrent
    /// already waiting when the list was loaded (a restart, a server switch) doesn't open it; neither does a cancelled
    /// one again. Its row still says "Choose files to download", and the menu item stands out. A torrent whose Progress
    /// Details window is open is chosen there instead: that window comes to the front on its Files tab, and no dialog
    /// opens here for it, then or later (<see cref="TorrentRules.NextFileChoice"/>).
    /// </summary>
    private void OpenArrivedFileChoice()
    {
        var mayOpenDialog = !_askingFileChoice && AppWindow.IsVisible && !DialogGuard.IsOpen(Content?.XamlRoot);
        var waiting = false;
        bool Open(Guid jobId)
        {
            // T11: one opened here first, then one the Host started as a process of its own.
            var open = InProcessDownloadWindow(jobId) is not null || TryProgressWindow(() => ActivationSignal.IsOwned(ProgressWindowName(jobId)));
            if (open) _progressWindowSeen.Add(jobId);
            return open;
        }

        bool Coming(Guid jobId)
        {
            var coming = _jobsById.TryGetValue(jobId, out var row) && row.Dto is { } job &&
                TorrentRules.ProgressWindowComing(job, _connection.Target.IsLocal, _progressWindowSeen.Contains(jobId), DateTimeOffset.UtcNow);
            waiting |= coming;
            return coming;
        }

        while (TorrentRules.NextFileChoice(
                   _jobsById.Values.Where(row => row.IsTorrent && row.Dto is not null).Select(row => (row.Dto!, row.FileChoiceArrived)),
                   _fileChoiceAsked,
                   _remote.State.TorrentsOffered,
                   Open,
                   mayOpenDialog,
                   Coming) is { } found)
        {
            // Shown there: asked, once. A window that closed in between leaves the choice to the dialog here.
            if (found.Place == FileChoicePlace.ProgressWindow && ShowFileChoiceWindow(found.JobId))
            {
                _fileChoiceAsked.Add(found.JobId);
                continue;
            }

            if (mayOpenDialog) OpenFileChoiceDialog(found.JobId);
            return;
        }

        if (waiting) RecheckFileChoiceSoon();
    }

    /// <summary>One look a second while a file choice waits for its progress window; nothing runs otherwise.</summary>
    private void RecheckFileChoiceSoon()
    {
        if (_fileChoiceRecheck is null)
        {
            _fileChoiceRecheck = DispatcherQueue.CreateTimer();
            _fileChoiceRecheck.IsRepeating = false;
            _fileChoiceRecheck.Interval = TimeSpan.FromSeconds(1);
            _fileChoiceRecheck.Tick += (_, _) => OpenArrivedFileChoice();
        }

        if (!_fileChoiceRecheck.IsRunning) _fileChoiceRecheck.Start();
    }

    /// <summary>Another process's progress window is a convenience to find: a failure to look counts as none.</summary>
    private static bool TryProgressWindow(Func<bool> look)
    {
        try
        {
            return look();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Net.Sockets.SocketException or WaitHandleCannotBeOpenedException)
        {
            return false;
        }
    }

    private string ProgressWindowName(Guid jobId) => ProgressWindowEventName(ProgressWindowKind.Download, jobId, _connection.Target);

    /// <summary>Brings the download's progress window to the front on its Files tab: one opened here, or the Host's.</summary>
    private bool ShowFileChoiceWindow(Guid jobId)
    {
        if (InProcessDownloadWindow(jobId) is { } window)
        {
            ShowProgressWindow(window);
            return true;
        }

        // The progress window is another process: without this Windows leaves it behind this one.
        App.AllowAnyForeground();
        return TryProgressWindow(() => ActivationSignal.TryShowOwner(ProgressWindowName(jobId)));
    }

    private void OpenFileChoiceDialog(Guid jobId)
    {
        _fileChoiceAsked.Add(jobId);
        _askingFileChoice = true;
        // Not inside the job update that found it: the list finishes its own update first.
        if (!DispatcherQueue.TryEnqueue(() => _ = AskArrivedFileChoiceAsync(jobId))) _askingFileChoice = false;
    }

    private async Task AskArrivedFileChoiceAsync(Guid jobId)
    {
        try
        {
            // Says itself what went wrong; never throws.
            await ChooseTorrentFilesAsync(jobId);
        }
        finally
        {
            _askingFileChoice = false;
        }

        // The next one whose list came in meanwhile.
        OpenArrivedFileChoice();
    }
}

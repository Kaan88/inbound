using System.Collections.ObjectModel;
using Microsoft.UI.Input;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Windows.System;

namespace DownloadManager.Desktop.WinUI;

public sealed class LocalRow
{
    internal LocalRow(LocalEntry entry, bool isParent = false)
    {
        Entry = entry;
        IsParent = isParent;
        SizeText = SitesBrowseRules.SizeText(entry.Size, entry.IsFolder);
        ModifiedText = SitesBrowseRules.ModifiedText(entry.Modified);
        FolderVisibility = entry.IsFolder ? Visibility.Visible : Visibility.Collapsed;
        FileVisibility = entry.IsFolder ? Visibility.Collapsed : Visibility.Visible;
    }

    internal static LocalRow Parent() => new(new LocalEntry("..", true, -1, null), isParent: true);

    internal LocalEntry Entry { get; }
    internal bool IsParent { get; }
    internal SitesBrowseRules.PaneLine Line => new(Entry.Name, Entry.IsFolder, Entry.Size, IsParent: IsParent);
    public string Name => Entry.Name;
    public bool IsFolder => Entry.IsFolder;
    public string SizeText { get; }
    public string ModifiedText { get; }
    public Visibility FolderVisibility { get; }
    public Visibility FileVisibility { get; }

    public string AccessibleName
    {
        get
        {
            if (IsParent) return "Folder above";
            var parts = new List<string> { IsFolder ? $"Folder {Name}" : Name };
            if (SizeText.Length > 0) parts.Add(SizeText);
            if (ModifiedText.Length > 0) parts.Add($"modified {ModifiedText}");
            return string.Join(", ", parts);
        }
    }
}

/// <summary>
/// SITES.5 / SITES.11 R2a: the "This PC" pane of the Two panes view, with the same chrome as the server pane: header, path
/// row (Up, breadcrumbs that turn into a text box, Star), sortable columns, a ".." line and a footer. It reads folders of
/// this PC itself (the window runs on the same PC as the Host whose sites it shows); moving files goes through the Host's
/// transfers, never through this pane.
/// </summary>
public sealed partial class LocalPane : UserControl
{
    private readonly List<LocalRow> _rows = [];
    private readonly ObservableCollection<LocalRow> _view = [];
    private string _path = "";
    private string _sortKey = SitesBrowseRules.SortName;
    private bool _sortAscending = true;
    private bool _rebuilding;

    // A typed folder that isn't there: said in the footer until the next folder opens.
    private string? _footerNote;
    private string? _pendingNote;

    // SITES.11 R2c: drag and drop, built on Windows only (PaneDrag.cs).
    partial void WireDrag();

    public LocalPane()
    {
        InitializeComponent();
        ItemsList.ItemsSource = _view;
        Titles.SetColumns([
            new PaneColumn(SitesBrowseRules.SortName, "Name", 0),
            new PaneColumn(SitesBrowseRules.SortSize, "Size", 70, RightAligned: true),
            new PaneColumn(SitesBrowseRules.SortModified, "Modified", 100),
        ]);
        Titles.SetSort(_sortKey, _sortAscending);
        Titles.SortRequested += key =>
        {
            (_sortKey, _sortAscending) = SitesBrowseRules.NextSort(_sortKey, _sortAscending, key);
            Titles.SetSort(_sortKey, _sortAscending);
            RebuildView();
        };
        Bar.Editable = true;
        Bar.CrumbClicked += folder => Open(folder);
        Bar.UpClicked += GoUp;
        Bar.EditRequested += () => Bar.BeginEdit(_path);
        Bar.PathSubmitted += typed => Open(Environment.ExpandEnvironmentVariables(typed.Trim().Trim('"')));
        Bar.StarClicked += ToggleStar;
        // Enter or Escape in the path box: the keyboard goes back to the list, not to the menu bar.
        Bar.EditEnded += FocusList;
        KeyDown += OnKeyDown;
        ItemsList.ContextRequested += ItemsList_ContextRequested;
        WireDrag();
        AutomationProperties.SetName(this, "This PC folder");
    }

    /// <summary>The starred folders of this PC pane (UiStateStore), newest first; set by the window.</summary>
    internal Func<IReadOnlyList<string>>? GetStars { get; set; }

    /// <summary>Saves the starred folders after the Star button changed them.</summary>
    internal Action<List<string>>? SaveStars { get; set; }

    /// <summary>The folder shown; empty before the first one opens.</summary>
    internal string Path => _path;

    internal List<LocalEntry> Selected
    {
        get
        {
            var wanted = SitesBrowseRules.SelectedForAction(SelectedRows.Select(row => row.Line)).Select(line => line.Name).ToHashSet(StringComparer.Ordinal);
            return [.. SelectedRows.Where(row => wanted.Remove(row.Name)).Select(row => row.Entry)];
        }
    }

    private IEnumerable<LocalRow> SelectedRows => ItemsList.SelectedItems.OfType<LocalRow>().Where(row => !row.IsParent);

    /// <summary>Selection or folder changed: the window updates the arrows.</summary>
    internal event Action? StateChanged;

    /// <summary>Delete on the list: the window asks, then sends the items to the Recycle Bin.</summary>
    internal event Action? DeleteRequested;

    /// <summary>A command of the context menu (or F2) the window carries out.</summary>
    internal event Action<LocalCommand>? CommandRequested;

    /// <summary>Whether Upload is offered (a site is shown and its folder has loaded); set by the window.</summary>
    internal Func<bool>? CanUpload { get; set; }

    /// <summary>The names of the folder shown (for a free "New folder" name).</summary>
    internal IEnumerable<string> ShownNames => _rows.Select(row => row.Name);

    /// <summary>
    /// Says <paramref name="text"/> in the footer (an error of Rename, New folder or Delete); with <paramref name="reload"/> the
    /// folder is read again first and the note shows after it, until the next folder opens.
    /// </summary>
    internal void Note(string text, bool reload)
    {
        if (reload && _path.Length > 0)
        {
            _pendingNote = text;
            Reload();
            return;
        }

        _footerNote = text;
        SyncFooter();
        LiveRegion.Announce(CountText);
    }

    /// <summary>Selects <paramref name="name"/> after the folder is read again (a new folder, a renamed item).</summary>
    internal void Reload(string? select) => Open(_path, select);

    private void ItemsList_ContextRequested(UIElement sender, ContextRequestedEventArgs e)
    {
        var byPointer = e.TryGetPosition(ItemsList, out var point);
        if (byPointer)
        {
            LocalRow? under = null;
            for (var node = e.OriginalSource as DependencyObject; node is not null; node = Microsoft.UI.Xaml.Media.VisualTreeHelper.GetParent(node))
            {
                if (node is ListViewItem item)
                {
                    under = ItemsList.ItemFromContainer(item) as LocalRow;
                    break;
                }
            }

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

        var menu = new MenuFlyout();
        var groups = SitesCommandRules.LocalMenu(ItemsList.SelectedItems.OfType<LocalRow>().Select(row => row.Line), CanUpload?.Invoke() ?? false);
        foreach (var group in groups)
        {
            if (menu.Items.Count > 0) menu.Items.Add(new MenuFlyoutSeparator());
            foreach (var command in group)
            {
                var item = new MenuFlyoutItem { Text = SitesCommandRules.Label(command) };
                if (command == LocalCommand.Rename) item.KeyboardAcceleratorTextOverride = "F2";
                else if (command == LocalCommand.Delete) item.KeyboardAcceleratorTextOverride = "Delete";
                else if (command == LocalCommand.Refresh) item.KeyboardAcceleratorTextOverride = "F5";
                var chosen = command;
                item.Click += (_, _) => CommandRequested?.Invoke(chosen);
                menu.Items.Add(item);
            }
        }

        e.Handled = true;
        if (byPointer)
        {
            menu.ShowAt(ItemsList, new Microsoft.UI.Xaml.Controls.Primitives.FlyoutShowOptions { Position = point });
            return;
        }

        var selected = ItemsList.SelectedIndex >= 0 ? ItemsList.ContainerFromIndex(ItemsList.SelectedIndex) as FrameworkElement : null;
        menu.ShowAt(selected ?? ItemsList);
    }

    // Reads of this PC's folders run off the UI thread (a share, a sleeping disk, 20 000 entries); the newest Open wins.
    private int _openGeneration;
    private string? _opening;

    /// <summary>The folder being opened, or the one shown.</summary>
    internal string TargetPath => _opening ?? _path;

    internal void Open(string folder, string? select = null) => _ = OpenAsync(folder, select);

    private async Task OpenAsync(string folder, string? select)
    {
        var generation = ++_openGeneration;
        _opening = folder;
        List<LocalEntry> entries;
        string? problem;
        try
        {
            (entries, problem) = await Task.Run(() => LocalListing.Read(folder));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Security.SecurityException or ArgumentException or NotSupportedException)
        {
            (entries, problem) = ([], ex.Message);
        }

        // A newer Open was asked for meanwhile: its folder is shown, whichever read ends first.
        if (generation != _openGeneration) return;
        _opening = null;
        if (problem is not null && _path.Length > 0 && folder != _path)
        {
            // A typed folder that isn't there leaves the one shown in place and says why in the footer.
            _footerNote = problem;
            SyncFooter();
            LiveRegion.Announce(CountText);
            return;
        }

        // Another folder starts with nothing selected; the same folder read again (sort, F5, after a delete) keeps it.
        var sameFolder = string.Equals(folder, _path, StringComparison.OrdinalIgnoreCase);
        // A note kept for after the read (an error of a rename or a delete) shows now; other folders start without one.
        _footerNote = _pendingNote;
        _pendingNote = null;
        _path = folder;
        _rows.Clear();
        foreach (var entry in entries) _rows.Add(new LocalRow(entry));
        Bar.CanGoUp = LocalListing.Parent(folder) is not null;
        SyncBar();
        if (problem is not null) ShowMessage(problem);
        else if (_rows.Count == 0) ShowMessage("This folder is empty.");
        else MessageText.Visibility = Visibility.Collapsed;
        RebuildView(keepSelection: sameFolder);
        var row = select is null ? null : _view.FirstOrDefault(item => !item.IsParent && string.Equals(item.Name, select, StringComparison.OrdinalIgnoreCase));
        if (row is not null)
        {
            ItemsList.SelectedItem = row;
            ItemsList.ScrollIntoView(row);
        }

        StateChanged?.Invoke();
    }

    internal void FocusList()
    {
        if (ItemsList.SelectedIndex >= 0 && ItemsList.ContainerFromIndex(ItemsList.SelectedIndex) is Control row && row.Focus(FocusState.Programmatic)) return;
        ItemsList.Focus(FocusState.Programmatic);
    }

    internal void Reload()
    {
        if (_path.Length == 0) return;
        Open(_path);
    }

    /// <summary>The starred folders changed elsewhere (or the list loaded): the Star button and its menu follow.</summary>
    internal void SyncBar()
    {
        var stars = GetStars?.Invoke() ?? [];
        Bar.Starred = _path.Length > 0 && LocalListing.IsStarred(stars, _path);
        Bar.StarEnabled = _path.Length > 0;
        Bar.SetCrumbs(_path.Length == 0 ? [] : [.. LocalListing.Crumbs(_path).Select(crumb => (crumb.Name, crumb.Path))]);
        var menu = Bar.StarFlyout;
        menu.Items.Clear();
        if (stars.Count == 0)
        {
            menu.Items.Add(new MenuFlyoutItem { Text = "No starred folders yet", IsEnabled = false });
            return;
        }

        foreach (var star in stars)
        {
            var item = new MenuFlyoutItem { Text = LocalListing.Leaf(star) is { Length: > 0 } leaf ? leaf : star, Tag = star };
            ToolTipService.SetToolTip(item, star);
            AutomationProperties.SetName(item, $"Open starred folder {star}");
            item.Click += (_, _) => Open(star);
            menu.Items.Add(item);
        }
    }

    internal void ToggleStar()
    {
        if (_path.Length == 0 || SaveStars is null) return;
        SaveStars(LocalListing.ToggleStar(GetStars?.Invoke() ?? [], _path));
        SyncBar();
    }

    /// <summary>Sorts the rows into what the list shows, with ".." first; with <paramref name="keepSelection"/> the selection stays by name.</summary>
    private void RebuildView(bool keepSelection = true)
    {
        var keep = keepSelection ? SelectedRows.Select(row => row.Name).ToHashSet(StringComparer.OrdinalIgnoreCase) : [];
        _rebuilding = true;
        try
        {
            var sorted = _rows.ToList();
            sorted.Sort((a, b) => LocalListing.Compare(a.Entry, b.Entry, _sortKey, _sortAscending));
            _view.Clear();
            if (LocalListing.Parent(_path) is not null) _view.Add(LocalRow.Parent());
            foreach (var row in sorted) _view.Add(row);
            foreach (var row in _view)
            {
                if (!row.IsParent && keep.Contains(row.Name)) ItemsList.SelectedItems.Add(row);
            }
        }
        finally
        {
            _rebuilding = false;
        }

        SyncFooter();
    }

    private void SyncFooter()
    {
        if (_footerNote is { } note)
        {
            CountText.Text = note;
            ToolTipService.SetToolTip(CountText, note);
            return;
        }

        var counts = SitesBrowseRules.FooterCounts(_rows.Select(row => row.Line), SelectedRows.Select(row => row.Line));
        ToolTipService.SetToolTip(CountText, null);
        CountText.Text = SitesBrowseRules.LocalFooter(counts.Files, counts.Folders, counts.Bytes, counts.Selected, counts.SelectedBytes);
    }

    private void ShowMessage(string text)
    {
        MessageText.Text = text;
        MessageText.Visibility = Visibility.Visible;
    }

    private void GoUp()
    {
        if (LocalListing.Parent(_path) is { } parent) Open(parent, LocalListing.Leaf(_path));
    }

    private void OpenSelectedFolder()
    {
        if (ItemsList.SelectedItems.Count != 1 || ItemsList.SelectedItem is not LocalRow { IsFolder: true } row) return;
        if (row.IsParent) GoUp();
        else Open(System.IO.Path.Combine(_path, row.Name));
    }

    private void ItemsList_DoubleTapped(object sender, DoubleTappedRoutedEventArgs e)
    {
        // Only a double click on a row acts, not one on empty space or the scrollbar.
        for (var node = e.OriginalSource as DependencyObject; node is not null; node = Microsoft.UI.Xaml.Media.VisualTreeHelper.GetParent(node))
        {
            if (node is ListViewItem)
            {
                OpenSelectedFolder();
                return;
            }
        }
    }

    private void ItemsList_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_rebuilding) return;
        SyncFooter();
        StateChanged?.Invoke();
    }

    private void ItemsList_PreviewKeyDown(object sender, KeyRoutedEventArgs e)
    {
        switch (e.Key)
        {
            case VirtualKey.Enter when ItemsList.SelectedItems.Count == 1 && ItemsList.SelectedItem is LocalRow { IsFolder: true }:
                OpenSelectedFolder();
                e.Handled = true;
                break;
            case VirtualKey.Back:
                GoUp();
                e.Handled = true;
                break;
            case VirtualKey.F5:
                Reload();
                e.Handled = true;
                break;
            case VirtualKey.F2 when SelectedRows.Count() == 1:
                CommandRequested?.Invoke(LocalCommand.Rename);
                e.Handled = true;
                break;
            case VirtualKey.Delete when SelectedRows.Any():
                DeleteRequested?.Invoke();
                e.Handled = true;
                break;
        }
    }

    private void OnKeyDown(object sender, KeyRoutedEventArgs e)
    {
        if (e.OriginalSource is TextBox) return;
        var ctrl = InputKeyboardSource.GetKeyStateForCurrentThread(VirtualKey.Control).HasFlag(Windows.UI.Core.CoreVirtualKeyStates.Down);
        if (e.Key == VirtualKey.L && ctrl)
        {
            Bar.BeginEdit(_path);
            e.Handled = true;
        }
    }
}

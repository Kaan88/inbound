using System.Collections.ObjectModel;
using System.ComponentModel;
using DownloadManager.Contracts;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;

namespace DownloadManager.Desktop.WinUI;

/// <summary>One visible line of a file tree. Public because the list's bindings read it by reflection.</summary>
public sealed class RemoteFileRow : INotifyPropertyChanged
{
    private readonly RemoteFileTreeView _owner;
    private bool _expanded;

    internal RemoteFileRow(RemoteFileNode node, RemoteFileTreeView owner)
    {
        Node = node;
        _owner = owner;
        _expanded = node.IsExpanded;
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    internal RemoteFileNode Node { get; }

    public string Name => Node.Name;

    public string SizeText => RemoteText.Bytes(Node.Size);

    public string? Note => Node.Note;

    public Visibility NoteVisibility => Node.Note is not null && !Node.Infected ? Visibility.Visible : Visibility.Collapsed;

    public Visibility ProblemVisibility => Node.Note is not null && Node.Infected ? Visibility.Visible : Visibility.Collapsed;

    public bool CanCheck => Node.CanChoose;

    public Thickness Indent => new(4 + (Node.Depth * 18), 0, 8, 0);

    public string Glyph => Node.IsFolder ? "" : "";

    public Visibility ChevronVisibility => Node.IsFolder ? Visibility.Visible : Visibility.Collapsed;

    public string ChevronGlyph => _expanded ? "" : "";

    public string ChevronName => _expanded ? $"Close folder {Node.Name}" : $"Open folder {Node.Name}";

    /// <summary>The checkbox showing this row just now. The list reuses them as it scrolls.</summary>
    internal CheckBox? Box { get; set; }

    public string AccessibleName
    {
        get
        {
            var size = SizeText.Length > 0 ? $", {SizeText}" : string.Empty;
            var kind = Node.IsFolder ? "Folder " : string.Empty;
            return Node.Note is { } note ? $"{kind}{Node.Name}{size}. Can't be selected: {note}" : $"{kind}{Node.Name}{size}";
        }
    }

    /// <summary>Says what changed since the last call, and nothing else, so a large list isn't redrawn for one click.</summary>
    internal void Refresh()
    {
        if (Box is { } box && box.IsChecked != Node.Checked) _owner.ShowChecked(box, Node.Checked);

        if (_expanded != Node.IsExpanded)
        {
            _expanded = Node.IsExpanded;
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(ChevronGlyph)));
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(ChevronName)));
        }
    }

    public override string ToString() => AccessibleName;
}

/// <summary>
/// A provider item's files with checkboxes, used by the My provider window and the file selection
/// dialog. The rows are virtualized, because an item can hold thousands of files.
/// </summary>
public sealed partial class RemoteFileTreeView : UserControl
{
    private readonly ObservableCollection<RemoteFileRow> _rows = [];
    private RemoteFileTree _tree = RemoteFileTree.Empty;
    private bool _readOnly;
    private bool _showingChecked;

    public RemoteFileTreeView()
    {
        InitializeComponent();
        RowsList.ItemsSource = _rows;
    }

    /// <summary>What is chosen changed.</summary>
    public event EventHandler? SelectionChanged;

    internal RemoteFileTree Tree => _tree;

    /// <summary>Shows these files, with the given ones chosen.</summary>
    /// <param name="chooseAll">Everything that can be chosen starts chosen.</param>
    /// <param name="keep">File ids chosen before the list was read again.</param>
    internal void SetFiles(IEnumerable<RemoteFileDto> files, bool chooseAll = false, IEnumerable<string>? keep = null)
    {
        _tree = RemoteFileTree.Build(files);
        if (chooseAll) _tree.SelectAll();
        else if (keep is not null) _tree.Restore(keep);
        _rows.Clear();
        foreach (var node in _tree.Visible()) _rows.Add(new RemoteFileRow(node, this));
        EmptyText.Text = string.Empty;
        EmptyText.Visibility = Visibility.Collapsed;
        Changed();
    }

    /// <summary>Shows words where the files would be, such as why there are none.</summary>
    internal void ShowMessage(string words)
    {
        _tree = RemoteFileTree.Empty;
        _rows.Clear();
        EmptyText.Text = words;
        EmptyText.Visibility = words.Length > 0 ? Visibility.Visible : Visibility.Collapsed;
        Changed();
    }

    /// <summary>Stops changes while a choice is on its way, without hiding what was chosen.</summary>
    internal bool IsChoiceLocked
    {
        get => _readOnly;
        set
        {
            _readOnly = value;
            RowsList.IsEnabled = !value;
            UpdateHeader();
        }
    }

    /// <summary>
    /// A box takes its state from here rather than a binding, because a binding doesn't pass on the
    /// null that shows a partly chosen folder. A click only ever goes through that third state on its
    /// way to clear, so anything but true means clear.
    /// </summary>
    private void Check_Changed(object sender, RoutedEventArgs e)
    {
        if (_showingChecked || sender is not CheckBox { Tag: RemoteFileRow row } box) return;
        if (!_readOnly) _tree.SetChecked(row.Node, box.IsChecked == true);
        Changed();
        row.Refresh();
    }

    private void Check_DataContextChanged(FrameworkElement sender, DataContextChangedEventArgs args)
    {
        if (sender is not CheckBox box) return;
        if (box.Tag is RemoteFileRow previous && ReferenceEquals(previous.Box, box)) previous.Box = null;
        box.Tag = args.NewValue as RemoteFileRow;
        if (box.Tag is not RemoteFileRow row) return;
        row.Box = box;
        ShowChecked(box, row.Node.Checked);
    }

    internal void ShowChecked(CheckBox box, bool? value)
    {
        _showingChecked = true;
        box.IsChecked = value;
        _showingChecked = false;
    }

    private void Changed()
    {
        foreach (var row in _rows) row.Refresh();
        UpdateHeader();
        SelectionChanged?.Invoke(this, EventArgs.Empty);
    }

    private void UpdateHeader()
    {
        var any = _tree.ChoosableCount > 0 && !_readOnly;
        SelectAllButton.IsEnabled = any && !_tree.AllChosen;
        SelectNoneButton.IsEnabled = any && _tree.ChosenCount > 0;
        SummaryText.Text = _tree.Summary();
        Microsoft.UI.Xaml.Automation.AutomationProperties.SetName(SummaryText, SummaryText.Text.Length > 0 ? SummaryText.Text : "Files selected");
    }

    private void SelectAll_Click(object sender, RoutedEventArgs e)
    {
        _tree.SelectAll();
        Changed();
    }

    private void SelectNone_Click(object sender, RoutedEventArgs e)
    {
        _tree.SelectNone();
        Changed();
    }

    private void Chevron_Click(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.DataContext is RemoteFileRow row) SetExpanded(row, !row.Node.IsExpanded);
    }

    private void SetExpanded(RemoteFileRow row, bool expanded)
    {
        if (!row.Node.IsFolder || row.Node.IsExpanded == expanded) return;
        var at = _rows.IndexOf(row);
        if (at < 0) return;
        row.Node.IsExpanded = expanded;
        if (expanded)
        {
            foreach (var node in RemoteFileTree.VisibleUnder(row.Node)) _rows.Insert(++at, new RemoteFileRow(node, this));
        }
        else
        {
            while (at + 1 < _rows.Count && _rows[at + 1].Node.Depth > row.Node.Depth) _rows.RemoveAt(at + 1);
        }

        row.Refresh();
    }

    private void RowsList_PreviewKeyDown(object sender, KeyRoutedEventArgs e)
    {
        // A focused row is a container, whose item is its content rather than its data context.
        if (FocusManager.GetFocusedElement(XamlRoot) is not FrameworkElement focused) return;
        if (((focused as ContentControl)?.Content ?? focused.DataContext) is not RemoteFileRow row) return;
        switch (e.Key)
        {
            // A focused checkbox takes Space for itself, which comes to the same thing.
            case Windows.System.VirtualKey.Space when focused is not CheckBox:
                if (row.CanCheck && !_readOnly)
                {
                    _tree.SetChecked(row.Node, row.Node.Checked != true);
                    Changed();
                }

                e.Handled = true;
                break;
            case Windows.System.VirtualKey.Right when row.Node is { IsFolder: true, IsExpanded: false }:
                SetExpanded(row, true);
                e.Handled = true;
                break;
            case Windows.System.VirtualKey.Left when row.Node is { IsFolder: true, IsExpanded: true }:
                SetExpanded(row, false);
                e.Handled = true;
                break;
        }
    }
}

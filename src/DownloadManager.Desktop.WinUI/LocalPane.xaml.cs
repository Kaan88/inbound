using System.Collections.ObjectModel;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Windows.System;

namespace DownloadManager.Desktop.WinUI;

public sealed class LocalRow
{
    internal LocalRow(LocalEntry entry)
    {
        Entry = entry;
        SizeText = SitesBrowseRules.SizeText(entry.Size, entry.IsFolder);
        ModifiedText = SitesBrowseRules.ModifiedText(entry.Modified);
    }

    internal LocalEntry Entry { get; }
    public string Name => Entry.Name;
    public bool IsFolder => Entry.IsFolder;
    public string SizeText { get; }
    public string ModifiedText { get; }
    public string Glyph => IsFolder ? "" : "";

    public string AccessibleName
    {
        get
        {
            var parts = new List<string> { IsFolder ? $"Folder {Name}" : Name };
            if (SizeText.Length > 0) parts.Add(SizeText);
            if (ModifiedText.Length > 0) parts.Add($"modified {ModifiedText}");
            return string.Join(", ", parts);
        }
    }
}

/// <summary>
/// SITES.5: the "This PC" pane of the Two panes view. It reads folders of this PC itself (the window runs on the same PC
/// as the Host whose sites it shows); moving files goes through the Host's transfers, never through this pane.
/// </summary>
public sealed partial class LocalPane : UserControl
{
    private readonly ObservableCollection<LocalRow> _rows = [];
    private string _path = "";

    public LocalPane()
    {
        InitializeComponent();
        ItemsList.ItemsSource = _rows;
    }

    /// <summary>The folder shown; empty before the first one opens.</summary>
    internal string Path => _path;

    internal List<LocalEntry> Selected => [.. ItemsList.SelectedItems.OfType<LocalRow>().Select(row => row.Entry)];

    /// <summary>Selection or folder changed: the window updates the arrows.</summary>
    internal event Action? StateChanged;

    /// <summary>Delete on the list: the window asks, then sends the items to the Recycle Bin.</summary>
    internal event Action? DeleteRequested;

    internal void Open(string folder, string? select = null)
    {
        var (entries, problem) = LocalListing.Read(folder);
        if (problem is not null && _path.Length > 0 && folder != _path)
        {
            // A typed folder that isn't there leaves the one shown in place and says why.
            ShowMessage(problem);
            PathBox.Text = _path;
            return;
        }

        _path = folder;
        PathBox.Text = folder;
        _rows.Clear();
        foreach (var entry in entries) _rows.Add(new LocalRow(entry));
        UpButton.IsEnabled = LocalListing.Parent(folder) is not null;
        if (problem is not null) ShowMessage(problem);
        else if (_rows.Count == 0) ShowMessage("This folder is empty.");
        else MessageText.Visibility = Visibility.Collapsed;
        var index = select is null ? -1 : _rows.ToList().FindIndex(row => string.Equals(row.Name, select, StringComparison.OrdinalIgnoreCase));
        if (index >= 0)
        {
            ItemsList.SelectedIndex = index;
            ItemsList.ScrollIntoView(_rows[index]);
        }

        StateChanged?.Invoke();
    }

    internal void FocusList() => ItemsList.Focus(FocusState.Programmatic);

    internal void Reload()
    {
        if (_path.Length == 0) return;
        var selected = Selected.FirstOrDefault()?.Name;
        Open(_path, selected);
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
        if (ItemsList.SelectedItems.Count == 1 && ItemsList.SelectedItem is LocalRow { IsFolder: true } row)
            Open(System.IO.Path.Combine(_path, row.Name));
    }

    private void UpButton_Click(object sender, RoutedEventArgs e) => GoUp();

    private void RefreshButton_Click(object sender, RoutedEventArgs e) => Reload();

    private void PathBox_KeyDown(object sender, KeyRoutedEventArgs e)
    {
        if (e.Key != VirtualKey.Enter) return;
        e.Handled = true;
        var typed = Environment.ExpandEnvironmentVariables(PathBox.Text.Trim().Trim('"'));
        Open(typed);
    }

    private void ItemsList_DoubleTapped(object sender, DoubleTappedRoutedEventArgs e) => OpenSelectedFolder();

    private void ItemsList_SelectionChanged(object sender, SelectionChangedEventArgs e) => StateChanged?.Invoke();

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
            case VirtualKey.Delete when ItemsList.SelectedItems.Count > 0:
                DeleteRequested?.Invoke();
                e.Handled = true;
                break;
        }
    }
}

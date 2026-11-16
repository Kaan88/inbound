using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;

namespace DownloadManager.Desktop.WinUI;

/// <summary>One column title of a pane: the key it sorts by, its words and width.</summary>
internal sealed record PaneColumn(string Key, string Title, double Width, bool RightAligned = false);

/// <summary>
/// SITES.11 R2a: the clickable column titles of a Sites pane (Name, Size, Modified, On this PC). Same widths, padding and
/// spacing as the rows under it. The main list's own header (<c>ColumnHeaderRow</c>) carries resizing, reordering and a
/// column menu for download rows, which a pane of fixed columns doesn't use, so a pane gets this small one.
/// </summary>
internal sealed class PaneColumnHeader : UserControl
{
    private readonly Grid _grid = new() { ColumnSpacing = 8, Padding = new Thickness(10, 4, 10, 4), MinHeight = 28 };
    private readonly Dictionary<string, (Button Button, FontIcon Arrow, ColumnDefinition Column, double Width)> _cells = [];

    public PaneColumnHeader()
    {
        Content = _grid;
    }

    /// <summary>The columns, first (Name) taking the rest of the width, the others fixed. Called once.</summary>
    internal void SetColumns(IReadOnlyList<PaneColumn> columns)
    {
        _grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(22) });
        InboundUi.Follow(_grid, "InboundDividerBrush", brush => _grid.BorderBrush = brush);
        _grid.BorderThickness = new Thickness(0, 1, 0, 1);
        for (var index = 0; index < columns.Count; index++)
        {
            var column = columns[index];
            var definition = index == 0
                ? new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) }
                : new ColumnDefinition { Width = new GridLength(column.Width) };
            _grid.ColumnDefinitions.Add(definition);
            var arrow = new FontIcon { FontSize = 9, Visibility = Visibility.Collapsed, VerticalAlignment = VerticalAlignment.Center };
            var label = new TextBlock { Text = column.Title, Style = (Style)Application.Current.Resources["InboundHeaderTextStyle"], VerticalAlignment = VerticalAlignment.Center };
            var content = new StackPanel
            {
                Orientation = Orientation.Horizontal, Spacing = 6,
                HorizontalAlignment = column.RightAligned ? HorizontalAlignment.Right : HorizontalAlignment.Left,
            };
            content.Children.Add(label);
            content.Children.Add(arrow);
            var button = new Button
            {
                Content = content, Padding = new Thickness(0), Background = new SolidColorBrush(Microsoft.UI.Colors.Transparent), BorderThickness = new Thickness(0), MinHeight = 0,
                HorizontalAlignment = HorizontalAlignment.Stretch, HorizontalContentAlignment = column.RightAligned ? HorizontalAlignment.Right : HorizontalAlignment.Left,
            };
            AutomationProperties.SetName(button, $"Sort by {column.Title}");
            var key = column.Key;
            button.Click += (_, _) => SortRequested?.Invoke(key);
            Grid.SetColumn(button, index + 1);
            _grid.Children.Add(button);
            _cells[key] = (button, arrow, definition, column.Width);
        }

        Content = _grid;
    }

    /// <summary>A title was clicked: the column's key.</summary>
    internal event Action<string>? SortRequested;

    internal void SetSort(string key, bool ascending)
    {
        foreach (var (cellKey, cell) in _cells)
        {
            var on = cellKey == key;
            cell.Arrow.Visibility = on ? Visibility.Visible : Visibility.Collapsed;
            cell.Arrow.Glyph = ascending ? "" : "";
        }
    }

    /// <summary>The "On this PC" column shows only for a site with a linked folder; hidden, it takes no width.</summary>
    internal void SetColumnVisible(string key, bool visible)
    {
        if (!_cells.TryGetValue(key, out var cell)) return;
        cell.Button.Visibility = visible ? Visibility.Visible : Visibility.Collapsed;
        cell.Column.Width = visible ? new GridLength(cell.Width) : new GridLength(0);
    }
}

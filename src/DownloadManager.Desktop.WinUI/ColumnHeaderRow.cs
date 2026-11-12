using Microsoft.UI.Input;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;

namespace DownloadManager.Desktop.WinUI;

/// <summary>The strip at a header cell's right edge that is dragged to resize its column.</summary>
internal sealed partial class ColumnGrip : Grid
{
    public ColumnGrip()
    {
        ProtectedCursor = InputSystemCursor.Create(InputSystemCursorShape.SizeWestEast);
    }
}

/// <summary>
/// A list's header: a button per column that sorts it, and a grip on each right edge that resizes it.
/// Rows are templates with column definitions of their own, so their widths are set from here on the
/// rows that exist just now and on each row as the list prepares it. A long list only ever has the
/// visible rows and a few more, so a drag stays quick however many items there are.
/// </summary>
internal sealed class ColumnHeaderRow
{
    private const double GripWidth = 7;
    private const double KeyboardStep = 10;

    private readonly ListColumnLayout _layout;
    private readonly Grid _header;
    private readonly FrameworkElement _root;
    private readonly Action<string> _sort;
    private readonly Action _saved;
    private readonly ListView _list;
    private readonly HashSet<Grid> _rows = [];
    private readonly Dictionary<string, Cell> _cells = new(StringComparer.Ordinal);
    private string _sortKey = string.Empty;
    private bool _ascending;

    private sealed record Cell(ListColumn Column, Button Button, FontIcon Arrow, FrameworkElement? Grip);

    /// <param name="root">The window's root, for theme colors and for a drag's coordinates, which must not move as the list scrolls sideways.</param>
    /// <param name="sort">Called with a column's key when its header is clicked.</param>
    /// <param name="saved">Called when a change of widths is finished, to save them.</param>
    public ColumnHeaderRow(ListColumnLayout layout, Grid header, ListView list, FrameworkElement root, Action<string> sort, Action saved)
    {
        _layout = layout;
        _header = header;
        _root = root;
        _sort = sort;
        _saved = saved;
        _list = list;
        Build();
        Apply(_header);

        // One Tab stop for the whole header, and the arrow keys move along it.
        _header.TabFocusNavigation = KeyboardNavigationMode.Once;
        _header.XYFocusKeyboardNavigation = XYFocusKeyboardNavigationMode.Enabled;
        list.ContainerContentChanging += (_, args) =>
        {
#if HAS_UNO
            // Uno raises this before the row's template is built; the row is taken once the container has loaded.
            if (args.ItemContainer.ContentTemplateRoot is null)
            {
                var container = args.ItemContainer;
                void Loaded(object sender, RoutedEventArgs _)
                {
                    container.Loaded -= Loaded;
                    if (container is not ListViewItem item) return;
                    UnoListRows.Trim(item);
                    if (UnoListRows.Root(item) is { } late) Take(late);
                }
                container.Loaded += Loaded;
                return;
            }
#endif
            if (args.ItemContainer.ContentTemplateRoot is not Grid row) return;
            Take(row);
        };
    }

    private void Take(Grid row)
    {
        row.Unloaded -= Row_Unloaded;
        row.Unloaded += Row_Unloaded;
        row.Loaded -= Row_Loaded;
        row.Loaded += Row_Loaded;
        _rows.Add(row);
        Apply(row);
    }

    /// <summary>Tells the list how wide it must be before it starts scrolling sideways.</summary>
    public event Action<double>? MinimumWidthChanged;

    private void Row_Unloaded(object sender, RoutedEventArgs e)
    {
        if (sender is Grid row) _rows.Remove(row);
    }

    /// <summary>
    /// U-WL-5: a recycled row can be unloaded after the list has already shown it again, which took it out of
    /// <see cref="_rows"/> while it was on screen, so a column appearing later left it with the old widths. A row
    /// that loads is on screen, so it is tracked again and gets the widths as they are now.
    /// </summary>
    private void Row_Loaded(object sender, RoutedEventArgs e)
    {
        if (sender is not Grid row) return;
        _rows.Add(row);
        Apply(row);
    }

    private void Build()
    {
        foreach (var column in _layout.Columns)
        {
            var at = _header.ColumnDefinitions.Count;
            _header.ColumnDefinitions.Add(new ColumnDefinition());
            if (!column.HasHeader) continue;
            var cell = BuildCell(column);
            Grid.SetColumn(cell.Button, at);
            _header.Children.Add(cell.Button);
            if (cell.Grip is { } grip)
            {
                Grid.SetColumn(grip, at);
                _header.Children.Add(grip);
            }

            _cells[column.Key] = cell;
        }
    }

    private Cell BuildCell(ListColumn column)
    {
        var text = new TextBlock { Text = column.Title, TextTrimming = TextTrimming.CharacterEllipsis };
        if (Application.Current.Resources.TryGetValue("InboundHeaderTextStyle", out var style) && style is Style headerStyle) text.Style = headerStyle;
        if (column.Centered) text.HorizontalAlignment = HorizontalAlignment.Center;
        if (column.RightAligned)
        {
            // UI.R6: over the right-aligned counters below it, at the same distance from the column's edge.
            text.HorizontalAlignment = HorizontalAlignment.Right;
            text.TextAlignment = TextAlignment.Right;
            text.Margin = new Thickness(0, 0, ListColumn.RightPadding, 0);
        }
        var arrow = new FontIcon { FontSize = 9, Visibility = Visibility.Collapsed, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, GripWidth, 0) };
        var content = new Grid();
        content.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        content.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        content.Children.Add(text);
        Grid.SetColumn(arrow, 1);
        content.Children.Add(arrow);

        var button = new Button
        {
            Content = content,
            Padding = new Thickness(0),
            MinHeight = 0,
            MinWidth = 0,
            CornerRadius = new CornerRadius(0),
            BorderThickness = new Thickness(0),
            Background = new Microsoft.UI.Xaml.Media.SolidColorBrush(Microsoft.UI.Colors.Transparent),
            HorizontalAlignment = HorizontalAlignment.Stretch,
            VerticalAlignment = VerticalAlignment.Stretch,
            HorizontalContentAlignment = HorizontalAlignment.Stretch,
            VerticalContentAlignment = VerticalAlignment.Center,
        };
        AutomationProperties.SetName(button, column.Spoken);
        AutomationProperties.SetHelpText(button, $"Sorts the list by {column.Spoken}. Selecting it again reverses the order. Shift with the Left or Right arrow changes the column's width.");
        if (column.Spoken != column.Title) ToolTipService.SetToolTip(button, column.Spoken);
        button.Click += (_, _) => _sort(column.SortKey());
        button.PreviewKeyDown += (_, args) => OnHeaderKey(column, args);
        var grip = BuildGrip(column);

        // A hidden column's button must not be a stop for the keyboard.
        button.Visibility = grip.Visibility = _layout.IsVisible(column.Key) ? Visibility.Visible : Visibility.Collapsed;
        return new Cell(column, button, arrow, grip);
    }

    private FrameworkElement BuildGrip(ListColumn column)
    {
        var line = new Border { Width = 1, HorizontalAlignment = HorizontalAlignment.Center, Margin = new Thickness(0, 5, 0, 5) };
        var grip = new ColumnGrip
        {
            Width = GripWidth,
            HorizontalAlignment = HorizontalAlignment.Right,
            Background = new Microsoft.UI.Xaml.Media.SolidColorBrush(Microsoft.UI.Colors.Transparent),
        };
        grip.Children.Add(line);

        var dragging = false;
        var startX = 0d;
        var startWidth = 0d;
        void ShowLine(bool on) => line.Background = on ? InboundTheme.Brush("InboundSecondaryTextBrush", _root) : null;

        grip.PointerEntered += (_, _) => ShowLine(true);
        grip.PointerExited += (_, _) => { if (!dragging) ShowLine(false); };
        grip.PointerPressed += (_, args) =>
        {
            if (!args.GetCurrentPoint(grip).Properties.IsLeftButtonPressed || !grip.CapturePointer(args.Pointer)) return;
            FixName();
            dragging = true;
            startX = args.GetCurrentPoint(_root).Position.X;
            startWidth = ActualWidthOf(column);
            args.Handled = true;
        };
        grip.PointerMoved += (_, args) =>
        {
            if (!dragging) return;
            if (_layout.Resize(column.Key, startWidth + args.GetCurrentPoint(_root).Position.X - startX, startWidth)) ApplyToAll();
            args.Handled = true;
        };
        void Finish()
        {
            if (!dragging) return;
            dragging = false;
            ShowLine(false);
            _saved();
        }

        grip.PointerReleased += (_, args) =>
        {
            grip.ReleasePointerCapture(args.Pointer);
            Finish();
            args.Handled = true;
        };
        grip.PointerCaptureLost += (_, _) => Finish();

        // The way back to how a column started.
        grip.DoubleTapped += (_, args) =>
        {
            _layout.Reset(column.Key);
            ApplyToAll();
            _saved();
            args.Handled = true;
        };
        return grip;
    }

    private void OnHeaderKey(ListColumn column, KeyRoutedEventArgs args)
    {
        if (args.Key is not (Windows.System.VirtualKey.Left or Windows.System.VirtualKey.Right)) return;
        if (!InputKeyboardSource.GetKeyStateForCurrentThread(Windows.System.VirtualKey.Shift).HasFlag(Windows.UI.Core.CoreVirtualKeyStates.Down)) return;
        FixName();
        var width = ActualWidthOf(column);
        var change = args.Key == Windows.System.VirtualKey.Right ? KeyboardStep : -KeyboardStep;
        if (_layout.Resize(column.Key, width + change, width))
        {
            ApplyToAll();
            _saved();
        }

        args.Handled = true;
    }

    private void FixName()
    {
        if (_layout.NameIsFixed || _cells.GetValueOrDefault(_layout.NameKey) is null) return;
        _layout.FixName(ActualWidthOf(_cells[_layout.NameKey].Column));
        ApplyToAll();
    }

    private double ActualWidthOf(ListColumn column)
    {
        var at = _layout.Columns.ToList().IndexOf(column);
        return _header.ColumnDefinitions[at].ActualWidth;
    }

    /// <summary>Shows the order the list is in. A key with no column, such as order of addition, shows nothing.</summary>
    public void ShowSort(string key, bool ascending)
    {
        _sortKey = key;
        _ascending = ascending;
        foreach (var cell in _cells.Values)
        {
            var sorted = cell.Column.SortKey() == key;
            cell.Arrow.Visibility = sorted ? Visibility.Visible : Visibility.Collapsed;
            if (sorted) cell.Arrow.Glyph = ascending ? "" : "";
            AutomationProperties.SetName(cell.Button, ListSort.SpokenHeader(cell.Column.Spoken, sorted, ascending));
        }
    }

    public void SetColumnVisible(string key, bool visible)
    {
        if (_layout.IsVisible(key) == visible) return;
        _layout.SetVisible(key, visible);
        if (_cells.TryGetValue(key, out var cell))
        {
            cell.Button.Visibility = visible ? Visibility.Visible : Visibility.Collapsed;
            if (cell.Grip is { } grip) grip.Visibility = visible ? Visibility.Visible : Visibility.Collapsed;
        }

        ApplyToAll();
    }

    /// <summary>Everything as the layout says it, for after saved widths are loaded.</summary>
    public void ApplyToAll()
    {
        Apply(_header);
        TakeRealized();
        foreach (var row in _rows) Apply(row);
        MinimumWidthChanged?.Invoke(_layout.MinimumTotal);
    }

    /// <summary>
    /// Review 5:L1: every row the list holds now, tracked or not. A recycled row can be unloaded without a later Loaded
    /// (U-WL-5), which leaves it out of <see cref="_rows"/> while it is on screen; the Extraction column then appeared
    /// in the header and in new rows but not in that one. Only the realized rows are walked (a screenful and a few more).
    /// </summary>
    private void TakeRealized()
    {
        if (_list.ItemsPanelRoot is not { } panel) return;
        foreach (var child in panel.Children)
        {
            if (child is not ListViewItem item) continue;
#if HAS_UNO
            var row = UnoListRows.Root(item);
#else
            var row = item.ContentTemplateRoot as Grid;
#endif
            if (row is not null && !_rows.Contains(row)) Take(row);
        }
    }

    private void Apply(Grid grid)
    {
        var definitions = grid.ColumnDefinitions;
        if (definitions.Count != _layout.Columns.Count) return;
        for (var i = 0; i < definitions.Count; i++)
        {
            var size = _layout.Size(_layout.Columns[i]);
            var width = size.Fills ? new GridLength(1, GridUnitType.Star) : new GridLength(size.Width);
            var minimum = size.Fills ? size.Width : 0;
            var definition = definitions[i];
            if (definition.Width != width) definition.Width = width;
            if (definition.MinWidth != minimum) definition.MinWidth = minimum;
        }
    }
}

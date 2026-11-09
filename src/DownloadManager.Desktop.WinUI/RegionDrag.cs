using Microsoft.UI.Input;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Windows.Foundation;

namespace DownloadManager.Desktop.WinUI;

/// <summary>
/// Dragging a box over a list's rows with the mouse. The list keeps every press and click until the
/// pointer has really moved, so a click and a double-click stay what they were. What the box means is
/// up to the caller. The arithmetic is in <see cref="RegionSelection"/>.
/// </summary>
internal sealed class RegionDrag
{
    private const double FallbackRowHeight = 27;

    private readonly ListView _list;
    private readonly Canvas _layer;
    private readonly Func<bool> _locked;
    private readonly Action<bool> _begin;
    private readonly Action<RowRange?, RowRange?, bool> _change;
    private readonly Action _end;
    private readonly DispatcherTimer _timer = new() { Interval = TimeSpan.FromMilliseconds(30) };

    private ScrollViewer? _scroller;
    private Border? _box;
    private bool _pressed;
    private bool _active;
    private bool _addFromPress;
    private bool _rowsMoved;
    private Point _start;
    private Point _pointer;
    private double _startContentY;
    private double _rowsTop;
    private double _rowHeight = FallbackRowHeight;
    private RowRange? _rows;

    /// <param name="layer">A canvas that lies exactly over the list and takes no pointer input.</param>
    /// <param name="locked">Whether a box may not start just now.</param>
    /// <param name="begin">A box starts. True means it adds to what is there; false means it replaces it.</param>
    /// <param name="change">The rows it touches went from the first to the second. True means the rows themselves moved, so start again from what was kept.</param>
    /// <param name="end">The box is gone.</param>
    public RegionDrag(ListView list, Canvas layer, Func<bool> locked, Action<bool> begin, Action<RowRange?, RowRange?, bool> change, Action end)
    {
        _list = list;
        _layer = layer;
        _locked = locked;
        _begin = begin;
        _change = change;
        _end = end;

        // The rows mark presses as handled for themselves, and this only watches until a drag is sure.
        list.AddHandler(UIElement.PointerPressedEvent, new PointerEventHandler(OnPressed), true);
        list.AddHandler(UIElement.PointerMovedEvent, new PointerEventHandler(OnMoved), true);
        list.AddHandler(UIElement.PointerReleasedEvent, new PointerEventHandler(OnReleased), true);
        list.AddHandler(UIElement.PointerCaptureLostEvent, new PointerEventHandler(OnCaptureLost), true);
        list.Items.VectorChanged += (_, _) => _rowsMoved = _active;
        _timer.Tick += (_, _) => OnTick();
    }

    private void OnPressed(object sender, PointerRoutedEventArgs e)
    {
        var point = e.GetCurrentPoint(_list);
        _pressed = RegionSelection.CanStart(
            e.Pointer.PointerDeviceType == PointerDeviceType.Mouse,
            point.Properties.IsLeftButtonPressed,
            IsOwnControl(e.OriginalSource as DependencyObject),
            _locked());
        if (!_pressed) return;
        _start = point.Position;
        _addFromPress = e.KeyModifiers.HasFlag(Windows.System.VirtualKeyModifiers.Control);
    }

    /// <summary>Whether a press landed on something in a row that has a click of its own, or on a scrollbar.</summary>
    private bool IsOwnControl(DependencyObject? element)
    {
        for (; element is not null && !ReferenceEquals(element, _list); element = VisualTreeHelper.GetParent(element))
        {
            if (element is ButtonBase or Thumb or ScrollBar or TextBox) return true;
            if (element is FrameworkElement { Tag: "RowAction" }) return true;
        }

        return false;
    }

    private void OnMoved(object sender, PointerRoutedEventArgs e)
    {
        if (!_pressed && !_active) return;
        var point = e.GetCurrentPoint(_list);
        if (!point.Properties.IsLeftButtonPressed)
        {
            Finish();
            return;
        }

        _pointer = point.Position;
        if (!_active)
        {
            if (!RegionSelection.PastThreshold(_start.X, _start.Y, _pointer.X, _pointer.Y)) return;
            if (!Start(e)) return;
        }

        e.Handled = true;
        Update();
    }

    private bool Start(PointerRoutedEventArgs e)
    {
        _pressed = false;
        _scroller ??= FindScroller(_list);
        if (_scroller is null || !_list.CapturePointer(e.Pointer)) return false;
        _active = true;
        _startContentY = _start.Y + _scroller.VerticalOffset;
        MeasureRows();
        _rows = null;
        _rowsMoved = false;
        _box = new Border
        {
            BorderThickness = new Thickness(1),
            Background = InboundTheme.Brush("InboundHighlightBrush", _list),
            BorderBrush = InboundTheme.Brush("InboundProgressFillBrush", _list),
            Opacity = 0.6,
            IsHitTestVisible = false,
        };
        _layer.Children.Add(_box);
        _list.Focus(FocusState.Pointer);
        _begin(_addFromPress || e.KeyModifiers.HasFlag(Windows.System.VirtualKeyModifiers.Control));
        _timer.Start();
        return true;
    }

    /// <summary>Rows are all one height, so one that exists says where they all are.</summary>
    private void MeasureRows()
    {
        _rowsTop = 0;
        _rowHeight = FallbackRowHeight;
#if HAS_UNO
        // Uno has no FirstVisibleIndex: the first row that has a container is as good.
        var first = -1;
        for (var at = 0; at < _list.Items.Count; at++)
        {
            if (_list.ContainerFromIndex(at) is FrameworkElement { ActualHeight: > 0 }) { first = at; break; }
        }
#else
        var first = _list.ItemsPanelRoot is ItemsStackPanel panel ? panel.FirstVisibleIndex : -1;
#endif
        if (first < 0) return;
        if (_list.ContainerFromIndex(first) is not FrameworkElement { ActualHeight: > 0 } row) return;
        _rowHeight = row.ActualHeight;
        _rowsTop = row.TransformToVisual(_list).TransformPoint(default).Y + _scroller!.VerticalOffset - (first * _rowHeight);
    }

    private void OnTick()
    {
        if (!_active || _scroller is null) return;
        var step = RegionSelection.AutoScrollStep(_pointer.Y, _list.ActualHeight);
        if (step != 0) _scroller.ChangeView(null, _scroller.VerticalOffset + step, null, true);
        Update();
    }

    private void Update()
    {
        if (!_active || _scroller is null || _box is null) return;
        var offset = _scroller.VerticalOffset;
        var box = RegionSelection.Box(_start.X, _startContentY, _pointer.X, _pointer.Y, offset, _list.ActualWidth, _list.ActualHeight);
        Canvas.SetLeft(_box, box.Left);
        Canvas.SetTop(_box, box.Top);
        _box.Width = box.Width;
        _box.Height = box.Height;

        var rows = RegionSelection.RowsTouched(_startContentY, _pointer.Y + offset, _rowsTop, _rowHeight, _list.Items.Count);
        if (rows == _rows && !_rowsMoved) return;
        var before = _rows;
        var moved = _rowsMoved;
        _rows = rows;
        _rowsMoved = false;
        _change(before, rows, moved);
    }

    private void OnReleased(object sender, PointerRoutedEventArgs e)
    {
        var active = _active;
        Finish();
        if (active)
        {
            _list.ReleasePointerCapture(e.Pointer);
            e.Handled = true;
        }
    }

    private void OnCaptureLost(object sender, PointerRoutedEventArgs e)
    {
        if (_active) Finish();
    }

    private void Finish()
    {
        _pressed = false;
        if (!_active) return;
        _active = false;
        _timer.Stop();
        if (_box is not null) _layer.Children.Remove(_box);
        _box = null;
        _end();
    }

    private static ScrollViewer? FindScroller(DependencyObject parent)
    {
        for (var i = 0; i < VisualTreeHelper.GetChildrenCount(parent); i++)
        {
            var child = VisualTreeHelper.GetChild(parent, i);
            if (child is ScrollViewer found) return found;
            if (FindScroller(child) is { } nested) return nested;
        }

        return null;
    }
}

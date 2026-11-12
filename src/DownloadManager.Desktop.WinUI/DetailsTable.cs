using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;

namespace DownloadManager.Desktop.WinUI;

/// <summary>
/// T12: a list outside the main window with the main list's header (<see cref="ColumnHeaderRow"/>: a click sorts, a
/// second reverses, a grip resizes, a double-click on it puts the width back), plus what a list that refreshes on its
/// own needs. Its widths and sort are kept in <see cref="ListLayoutStore"/> under its name. Columns wider than the card
/// scroll sideways, header and rows together, as in the main list. While the pointer is over the list, or the keyboard
/// moves in it, a refresh does not move rows about (<see cref="Held"/>): the owner keeps the order it shows, sets
/// <see cref="ResortPending"/>, and is called back through <see cref="Released"/> once the pointer and the keyboard
/// have left. A sort by a column that is hidden now waits until it shows again (<see cref="SortKey"/>).
/// </summary>
internal sealed class DetailsTable
{
    private readonly string _name;
    private readonly ListLayoutStore _store;
    private readonly ListColumnLayout _layout;
    private readonly ColumnHeaderRow _header;
    private readonly ListView _list;
    private bool _pointerOver;

    /// <summary>The column picked (kept and saved even while it is hidden); null for the list's own order.</summary>
    private string? _sortKey;

    /// <param name="name">The list's key in the store, such as "progress.files".</param>
    /// <param name="sideways">The scroller around <paramref name="listGrid"/>, which holds the header and the list.</param>
    /// <param name="hoverArea">What the pointer is over while it is over the list: the list's card, header and all.</param>
    /// <param name="root">The window's root, for theme colors and a drag's coordinates.</param>
    /// <param name="sorted">Called after a header click changed the order.</param>
    public DetailsTable(string name, ListColumnLayout layout, Grid header, ListView list, Grid listGrid, ScrollViewer sideways,
        FrameworkElement hoverArea, FrameworkElement root, Action sorted, ListLayoutStore? store = null)
    {
        _name = name;
        _store = store ?? ListLayoutStore.Default;
        _layout = layout;
        _list = list;
        var saved = _store.Load(name);
        _layout.Import(saved.Widths);
        if (saved.SortKey is { } key && layout.Columns.Any(column => column.HasHeader && column.Key == key))
        {
            _sortKey = key;
            SortAscending = saved.SortAscending;
        }

        _header = new ColumnHeaderRow(layout, header, list, root, clicked =>
        {
            (_sortKey, SortAscending) = DetailsSort.Next(SortKey, SortAscending, clicked);
            _header!.ShowSort(SortKey ?? string.Empty, SortAscending);
            ResortPending = false;
            Save();
            sorted();
        }, Save);
        _header.ShowSort(SortKey ?? string.Empty, SortAscending);
        // As the main list: the grid is as wide as the view, and never narrower than its columns, so the scroller can
        // reach the right-hand ones.
        sideways.SizeChanged += (_, args) => listGrid.Width = args.NewSize.Width;
        _header.MinimumWidthChanged += width => listGrid.MinWidth = width;
        _header.ApplyToAll();

        hoverArea.PointerEntered += (_, _) => _pointerOver = true;
        hoverArea.PointerExited += (_, _) => Leave();
        hoverArea.PointerCanceled += (_, _) => Leave();
        // Focus moves from row to row through a LostFocus; whether it left the list is seen once it has landed.
        list.LostFocus += (_, _) => list.DispatcherQueue.TryEnqueue(Release);
    }

    /// <summary>
    /// The column the list is sorted by; null for the list's own order, also while the column picked is hidden (Priority
    /// and Next announce on a Host without T3's controls), so a list is never ordered by something it doesn't show.
    /// </summary>
    public string? SortKey => DetailsSort.Shown(_sortKey, _layout.IsVisible);

    public bool SortAscending { get; private set; }

    /// <summary>A refresh found the order out of date while the list was held.</summary>
    public bool ResortPending { get; set; }

    /// <summary>The list was held when a refresh came and isn't any more: sort it again now.</summary>
    public event Action? Released;

    /// <summary>
    /// The pointer is over the list, or the keyboard moves in it: rows stay where they are. Focus a click left in a row
    /// or a box doesn't count, or the list would stop following its values until something else took focus.
    /// </summary>
    public bool Held => _pointerOver || KeyboardWithin();

    public void SetColumnVisible(string key, bool visible)
    {
        var was = SortKey;
        _header.SetColumnVisible(key, visible);
        if (SortKey == was) return;
        // The sorted column went (or came back): the arrow and the order follow, keeping the view where it is.
        _header.ShowSort(SortKey ?? string.Empty, SortAscending);
        ResortPending = false;
        Released?.Invoke();
    }

    private void Leave()
    {
        _pointerOver = false;
        Release();
    }

    private void Release()
    {
        if (!ResortPending || Held) return;
        ResortPending = false;
        Released?.Invoke();
    }

    private bool KeyboardWithin()
    {
        if (_list.XamlRoot is not { } root) return false;
        if (FocusManager.GetFocusedElement(root) is not Control { FocusState: FocusState.Keyboard } focused) return false;
        for (DependencyObject? element = focused; element is not null; element = VisualTreeHelper.GetParent(element))
        {
            if (ReferenceEquals(element, _list)) return true;
        }

        return false;
    }

    private void Save() => _store.Save(_name, new ListLayout { Widths = _layout.Export(), SortKey = _sortKey, SortAscending = SortAscending });
}

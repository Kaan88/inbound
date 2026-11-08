namespace DownloadManager.Desktop.WinUI;

/// <summary>One column of a list with a header. An empty title means a plain cell with no header of its own.</summary>
/// <param name="SpokenTitle">What a screen reader says for a title that is only a letter.</param>
/// <param name="RightAligned">UI.R6: a live counter. Its header and cells line up on the right, so units hold still as digits are added.</param>
internal sealed record ListColumn(string Key, string Title, double DefaultWidth, double MinWidth, string? SpokenTitle = null, bool Centered = false, bool RightAligned = false)
{
    /// <summary>The gap a right-aligned cell keeps from the next column, in the header and in the rows alike.</summary>
    public const double RightPadding = 8;

    public bool HasHeader => Title.Length > 0;

    public string Spoken => SpokenTitle ?? Title;

    /// <summary>A column's key is also the sort key its header asks for.</summary>
    public string SortKey() => Key;
}

/// <summary>The columns of the two lists that have headers, left to right, the same as their row templates.</summary>
internal static class ListColumnSets
{
    /// <summary>The icon cell has no header. The extraction column stays away until there is an archive to talk about.</summary>
    public static ListColumnLayout Downloads() => new(
        [
            new ListColumn("icon", string.Empty, 30, 30),
            new ListColumn("name", "File Name", 264, 120),
            new ListColumn("queue", "Q", 30, 24, SpokenTitle: "Queue", Centered: true),
            new ListColumn("size", "Size", 78, 40, RightAligned: true),
            new ListColumn("status", "Status", 140, 60),
            new ListColumn("extraction", "Extraction", 140, 60),
            new ListColumn("eta", "Time left", 78, 40, RightAligned: true),
            new ListColumn("rate", "Transfer rate", 86, 40, RightAligned: true),
            new ListColumn("lastTry", "Last Try Date", 116, 60),
            new ListColumn("description", "Description", 210, 60),
        ],
        "name",
        "description",
        chrome: 8);

    /// <summary>The row template's padding and spacing come to 6 + 6 + 6 * 8.</summary>
    public static ListColumnLayout ProviderItems() => new(
        [
            new ListColumn("name", "Name", 200, 100),
            new ListColumn("type", "Type", 86, 40),
            new ListColumn("status", "Status", 190, 60),
            new ListColumn("size", "Size", 70, 40),
            new ListColumn("files", "Files", 58, 30),
            new ListColumn("added", "Added", 82, 40),
            new ListColumn("expires", "Expires", 82, 40),
        ],
        "name",
        "expires",
        chrome: 60);

    /// <summary>What a saved sort key may be for the download list. Order of addition has a menu item and no column.</summary>
    public static bool IsDownloadSort(string? key) => key == "added" || Downloads().Columns.Any(column => column.HasHeader && column.Key == key);

    public static bool IsProviderSort(string? key) => ProviderItems().Columns.Any(column => column.Key == key);
}

/// <summary>How wide one column is laid out. A column that fills takes what is left, and never less than <see cref="Width"/>.</summary>
internal readonly record struct ColumnSize(double Width, bool Fills);

/// <summary>
/// The widths of a list's columns. Nothing here touches the UI.
/// <para>
/// Until someone resizes a column, the name column takes whatever room is left. Every other column is
/// to its right, so resizing one of those would move its left edge and leave its right edge where it
/// was, away from the pointer. So the first resize fixes the name column at the width it has, and from
/// then on the last column takes the room left over. Each column's right edge then follows the pointer.
/// </para>
/// </summary>
internal sealed class ListColumnLayout
{
    public const double MostWidth = 2000;

    private readonly Dictionary<string, double> _widths = new(StringComparer.Ordinal);
    private readonly HashSet<string> _hidden = new(StringComparer.Ordinal);

    /// <param name="columns">Left to right. They need at least the name column and the last column.</param>
    /// <param name="nameKey">The column that fills until something is resized.</param>
    /// <param name="lastKey">The column that fills after that.</param>
    /// <param name="chrome">The room padding and column spacing take from the list's width.</param>
    public ListColumnLayout(IReadOnlyList<ListColumn> columns, string nameKey, string lastKey, double chrome = 0)
    {
        Columns = columns;
        NameKey = nameKey;
        LastKey = lastKey;
        Chrome = chrome;
    }

    public IReadOnlyList<ListColumn> Columns { get; }

    public string NameKey { get; }

    public string LastKey { get; }

    public double Chrome { get; }

    /// <summary>Whether the name column has a width of its own yet.</summary>
    public bool NameIsFixed => _widths.ContainsKey(NameKey);

    public bool IsVisible(string key) => !_hidden.Contains(key);

    public void SetVisible(string key, bool visible)
    {
        if (visible) _hidden.Remove(key);
        else _hidden.Add(key);
    }

    public ColumnSize Size(ListColumn column)
    {
        if (!IsVisible(column.Key)) return new ColumnSize(0, false);
        var width = _widths.TryGetValue(column.Key, out var chosen) ? chosen : column.DefaultWidth;
        var fills = NameIsFixed ? column.Key == LastKey : column.Key == NameKey;
        return new ColumnSize(width, fills);
    }

    /// <summary>The narrowest the list can be without squeezing a column. Past this it scrolls sideways.</summary>
    public double MinimumTotal => Chrome + Columns.Sum(column => Size(column).Width);

    /// <summary>A resize is starting. Call it with the name column's width on screen.</summary>
    public void FixName(double actualNameWidth)
    {
        if (NameIsFixed || Columns.FirstOrDefault(column => column.Key == NameKey) is not { } name) return;
        _widths[NameKey] = Clamp(name, Math.Max(actualNameWidth, name.DefaultWidth));
    }

    /// <summary>
    /// Sets a column's width from where the pointer has dragged its edge to.
    /// A column that fills is already as wide as the list lets it be, so dragging its edge inward changes
    /// nothing on screen. That must not raise its minimum either, or the list would scroll sideways sooner later on.
    /// </summary>
    /// <param name="actualWidth">The width on screen when the drag started.</param>
    public bool Resize(string key, double desired, double actualWidth)
    {
        if (Columns.FirstOrDefault(column => column.Key == key) is not { } column || double.IsNaN(desired)) return false;
        var current = Size(column);
        var wanted = Clamp(column, desired);
        if (current.Fills && wanted < actualWidth && wanted >= current.Width) return false;
        if (wanted == current.Width) return false;
        _widths[key] = wanted;
        return true;
    }

    /// <summary>Back to how it started. For the name column, that means filling again.</summary>
    public void Reset(string key) => _widths.Remove(key);

    public Dictionary<string, double> Export() => new(_widths, StringComparer.Ordinal);

    /// <summary>Takes saved widths. A file can hold anything, so unknown columns and nonsense are left out.</summary>
    public void Import(IReadOnlyDictionary<string, double>? saved)
    {
        _widths.Clear();
        if (saved is null) return;
        foreach (var column in Columns)
        {
            if (saved.TryGetValue(column.Key, out var width) && double.IsFinite(width)) _widths[column.Key] = Clamp(column, width);
        }
    }

    private static double Clamp(ListColumn column, double width) => Math.Clamp(width, column.MinWidth, MostWidth);
}

/// <summary>What a click on a column header does to the order of a list. Nothing here touches the UI.</summary>
internal static class ListSort
{
    /// <summary>More rows out of place than this are replaced at once. One move at a time, a long list would crawl.</summary>
    public const int MostMovesInPlace = 64;

    /// <summary>
    /// Whether to swap a list's source rather than move rows. A swap sends the list to the top and rebuilds every row,
    /// so it is only for a sort, whose caller says where to go next. Any other change, such as a row appearing above
    /// a long list, puts every position out by one and still needs only one row moved.
    /// </summary>
    public static bool ReplacesWholeList<T>(bool sorting, IReadOnlyList<T> current, IReadOnlyList<T> target) where T : class
        => sorting && target.Where((row, at) => at >= current.Count || !ReferenceEquals(current[at], row)).Count() > MostMovesInPlace;

    /// <summary>A click on another column starts in its usual direction, and a second click reverses it.</summary>
    public static (string Key, bool Ascending) Next(string currentKey, bool currentAscending, string clicked)
        => currentKey == clicked ? (clicked, !currentAscending) : (clicked, StartsAscending(clicked));

    /// <summary>Words and times read from the first down. Sizes, speeds and dates read from the biggest or newest.</summary>
    public static bool StartsAscending(string key) => key is "name" or "queue" or "type" or "status" or "extraction" or "eta" or "description" or "expires";

    public static string SpokenHeader(string title, bool sorted, bool ascending)
        => !sorted ? title : ascending ? $"{title}, sorted ascending" : $"{title}, sorted descending";

    /// <summary>Rows with nothing to compare stay at the end in either direction, and equal rows keep their order.</summary>
    public static IEnumerable<T> Order<T, TKey>(IEnumerable<T> rows, Func<T, TKey> key, IComparer<TKey>? comparer, bool ascending, Func<T, bool>? isBlank = null)
    {
        var filled = rows.OrderBy(row => isBlank?.Invoke(row) == true);
        return ascending ? filled.ThenBy(key, comparer) : filled.ThenByDescending(key, comparer);
    }
}

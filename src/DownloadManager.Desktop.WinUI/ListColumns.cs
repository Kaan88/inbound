namespace DownloadManager.Desktop.WinUI;

/// <summary>One column of a list with a header. An empty title means a plain cell with no header of its own.</summary>
/// <param name="SpokenTitle">What a screen reader says for a title that is only a letter.</param>
/// <param name="RightAligned">UI.R6: a live counter. Its header and cells line up on the right, so units hold still as digits are added.</param>
/// <param name="ShownAtFirst">UI.R42: whether the column shows until someone chooses otherwise in the header's menu.</param>
internal sealed record ListColumn(string Key, string Title, double DefaultWidth, double MinWidth, string? SpokenTitle = null, bool Centered = false, bool RightAligned = false,
    bool ShownAtFirst = true)
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
    /// <summary>
    /// The icon cell has no header. The extraction column stays away until there is an archive to talk about. The torrent
    /// columns start hidden; the torrent views show Seeds, Peers, Up and Ratio until chosen otherwise
    /// (<see cref="TorrentViewColumns"/>), and any of them can be chosen in any view from the header's menu (UI.R42).
    /// </summary>
    public static ListColumnLayout Downloads() => new(
        [
            new ListColumn("icon", string.Empty, 30, 30),
            new ListColumn("name", "File Name", 264, 120),
            new ListColumn("queue", "Q", 30, 24, SpokenTitle: "Queue", Centered: true),
            new ListColumn("size", "Size", 78, 40, RightAligned: true),
            new ListColumn("status", "Status", 140, 60),
            new ListColumn("extraction", "Extraction", 140, 60),
            // DT-6: the torrent columns (TorrentColumns), hidden at first outside the torrent views.
            new ListColumn("seeds", "Seeds", 52, 36, RightAligned: true, ShownAtFirst: false),
            new ListColumn("peers", "Peers", 52, 36, RightAligned: true, ShownAtFirst: false),
            new ListColumn("eta", "Time left", 78, 40, RightAligned: true),
            new ListColumn("rate", "Transfer rate", 86, 40, RightAligned: true),
            new ListColumn("up", "Up", 78, 40, SpokenTitle: "Upload rate", RightAligned: true, ShownAtFirst: false),
            new ListColumn("ratio", "Ratio", 52, 36, RightAligned: true, ShownAtFirst: false),
            // UI.R42: more of what each job already says about its torrent, hidden until chosen.
            new ListColumn("uploaded", "Uploaded", 78, 40, RightAligned: true, ShownAtFirst: false),
            new ListColumn("seedTime", "Seeding time", 90, 50, RightAligned: true, ShownAtFirst: false),
            new ListColumn("seedGoal", "Seeding goal", 120, 60, ShownAtFirst: false),
            new ListColumn("lastTry", "Last Try Date", 116, 60),
            new ListColumn("description", "Description", 210, 60),
        ],
        "name",
        "description",
        chrome: 8);

    /// <summary>The download list's torrent columns: blank on a row that isn't a torrent, and hidden at first outside the torrent views.</summary>
    public static readonly IReadOnlyList<string> TorrentColumns = ["seeds", "peers", "up", "ratio", "uploaded", "seedTime", "seedGoal"];

    /// <summary>DT-6: the torrent columns the torrent views show until chosen otherwise.</summary>
    public static readonly IReadOnlyList<string> TorrentViewColumns = ["seeds", "peers", "up", "ratio"];

    /// <summary>
    /// UI.R42: the download list keeps two sets of choices, one for the torrent views and one for every other view, so
    /// hiding Seeds among the torrents doesn't take it from All Downloads, nor the other way round. This puts the layout in
    /// the set of the view shown: what shows there at first, and the choices made there.
    /// </summary>
    public static void UseDownloadsChoices(ListColumnLayout layout, bool torrentView, IReadOnlyDictionary<string, bool>? choices)
    {
        foreach (var key in TorrentViewColumns) layout.SetShownAtFirst(key, torrentView);
        layout.ImportChoices(choices);
    }

    /// <summary>
    /// The order the download list is put in: the one chosen, but a sort by a column that is hidden now (a torrent column
    /// outside the torrent views, or one hidden from the header's menu) runs in the order of addition, newest first as it
    /// starts, and the chosen sort comes back with the column. Order of addition has no column and always holds.
    /// </summary>
    public static (string Key, bool Ascending) ShownSort(ListColumnLayout layout, string key, bool ascending)
        => layout.Columns.Any(column => column.HasHeader && column.Key == key) && !layout.IsVisible(key) ? ("added", false) : (key, ascending);

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

    /// <summary>
    /// T12: Progress Details' Files tab. The box has no header. Cells keep their gaps inside their columns (the row's
    /// column spacing is 0), so a hidden Priority column takes no room at all.
    /// </summary>
    public static ListColumnLayout TorrentFiles() => new(
        [
            new ListColumn("check", string.Empty, 32, 32),
            new ListColumn("name", "Name", 240, 100),
            new ListColumn("size", "Size", 88, 48, RightAligned: true),
            new ListColumn("progress", "Progress", 76, 48, RightAligned: true),
            new ListColumn("priority", "Priority", 72, 48),
            // UI.R42: the file's bytes on disk so far (TorrentFileDto.Done), hidden until chosen.
            new ListColumn("done", "Downloaded", 88, 48, RightAligned: true, ShownAtFirst: false),
        ],
        "name",
        "done");

    /// <summary>T12: Progress Details' Peers tab.</summary>
    public static ListColumnLayout TorrentPeers() => new(
        [
            new ListColumn("peer", "Peer", 140, 60),
            new ListColumn("client", "Client", 128, 50),
            new ListColumn("flags", "Flags", 72, 40),
            new ListColumn("has", "Has", 56, 40, RightAligned: true),
            new ListColumn("down", "Down", 84, 48, SpokenTitle: "Download rate", RightAligned: true),
            new ListColumn("up", "Up", 84, 48, SpokenTitle: "Upload rate", RightAligned: true),
            // UI.R42: what came from this peer and went to it since it connected (TorrentPeerDto), hidden until chosen.
            new ListColumn("received", "Downloaded", 88, 48, SpokenTitle: "Downloaded from the peer", RightAligned: true, ShownAtFirst: false),
            new ListColumn("sent", "Uploaded", 88, 48, SpokenTitle: "Uploaded to the peer", RightAligned: true, ShownAtFirst: false),
        ],
        "peer",
        "sent");

    /// <summary>T12: Progress Details' Trackers tab. Next announce shows only where the Host says it (T3).</summary>
    public static ListColumnLayout TorrentTrackers() => new(
        [
            new ListColumn("tracker", "Tracker", 200, 80),
            new ListColumn("status", "Status", 108, 60),
            new ListColumn("seeds", "Seeds", 64, 40, RightAligned: true),
            new ListColumn("peers", "Peers", 64, 40, RightAligned: true),
            new ListColumn("next", "Next announce", 112, 60, RightAligned: true),
            // UI.R42: the tracker's tier and what it said last (TorrentTrackerDto), hidden until chosen.
            new ListColumn("tier", "Tier", 48, 36, RightAligned: true, ShownAtFirst: false),
            new ListColumn("message", "Message", 200, 60, ShownAtFirst: false),
        ],
        "tracker",
        "message");

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

    /// <summary>Columns the app keeps away because they have nothing to show (no archive yet, a Host without priorities).</summary>
    private readonly HashSet<string> _unavailable = new(StringComparer.Ordinal);

    /// <summary>UI.R42: what the header's menu chose, by column. A column with no choice shows as <see cref="ShownAtFirst"/> says.</summary>
    private readonly Dictionary<string, bool> _chosen = new(StringComparer.Ordinal);

    /// <summary>What the view shown says about a column at first, where it differs from the column's own default.</summary>
    private readonly Dictionary<string, bool> _shownAtFirst = new(StringComparer.Ordinal);

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

    /// <summary>
    /// Whether a column shows: the app has something for it, and the header's menu chose it, or nobody chose and it shows
    /// at first. The name column always shows while it has something to show.
    /// </summary>
    public bool IsVisible(string key)
        => IsAvailable(key) && (key == NameKey || (_chosen.TryGetValue(key, out var chosen) ? chosen : ShownAtFirst(key)));

    public bool IsAvailable(string key) => !_unavailable.Contains(key);

    /// <summary>The app's side: a column with nothing to show stays away, and comes back as it was chosen.</summary>
    public void SetAvailable(string key, bool available)
    {
        if (available) _unavailable.Remove(key);
        else _unavailable.Add(key);
    }

    public bool ShownAtFirst(string key)
        => _shownAtFirst.TryGetValue(key, out var shown) ? shown : Columns.FirstOrDefault(column => column.Key == key)?.ShownAtFirst ?? true;

    /// <summary>What the view shown says about a column until something is chosen for it.</summary>
    public void SetShownAtFirst(string key, bool shown) => _shownAtFirst[key] = shown;

    /// <summary>A column the header's menu can show or hide: one with a header, other than the name.</summary>
    public bool CanChoose(string key) => key != NameKey && Columns.Any(column => column.HasHeader && column.Key == key);

    /// <summary>The columns the header's menu lists, left to right: those with a header that have something to show.</summary>
    public IEnumerable<ListColumn> MenuColumns => Columns.Where(column => column.HasHeader && IsAvailable(column.Key));

    /// <summary>The header's menu showed or hid a column. False when nothing changed or the column can't be chosen.</summary>
    public bool Choose(string key, bool shown)
    {
        if (!CanChoose(key)) return false;
        var before = IsVisible(key);
        _chosen[key] = shown;
        return before != IsVisible(key);
    }

    /// <summary>
    /// The header cell the keyboard moves to when the one it was on has just been hidden: the nearest shown one to its
    /// right, else to its left, else the name (which always shows). The key itself while it still shows; the name for a key
    /// the list doesn't have.
    /// </summary>
    public string FocusAfterHiding(string key)
    {
        var at = -1;
        for (var i = 0; i < Columns.Count; i++)
        {
            if (Columns[i].Key == key) at = i;
        }
        if (at < 0) return NameKey;
        if (IsVisible(key)) return key;
        bool Shows(int i) => Columns[i].HasHeader && IsVisible(Columns[i].Key);
        for (var i = at + 1; i < Columns.Count; i++)
        {
            if (Shows(i)) return Columns[i].Key;
        }
        for (var i = at - 1; i >= 0; i--)
        {
            if (Shows(i)) return Columns[i].Key;
        }
        return NameKey;
    }

    public Dictionary<string, bool> ExportChoices() => new(_chosen, StringComparer.Ordinal);

    /// <summary>Takes saved choices. A file can hold anything, so unknown columns and the name are left out.</summary>
    public void ImportChoices(IReadOnlyDictionary<string, bool>? saved)
    {
        _chosen.Clear();
        if (saved is null) return;
        foreach (var (key, shown) in saved)
        {
            if (CanChoose(key)) _chosen[key] = shown;
        }
    }

    /// <summary>"Reset columns": every column as it started, shown or hidden, at its first width, the name filling again.</summary>
    public void ResetColumns()
    {
        _chosen.Clear();
        _widths.Clear();
    }

    public ColumnSize Size(ListColumn column)
    {
        if (!IsVisible(column.Key)) return new ColumnSize(0, false);
        var width = _widths.TryGetValue(column.Key, out var chosen) ? chosen : column.DefaultWidth;
        var fills = NameIsFixed ? column.Key == FillingKey : column.Key == NameKey;
        return new ColumnSize(width, fills);
    }

    /// <summary>
    /// The column that takes the room left once the name is fixed: the last column, or while it is hidden (T12: a Host
    /// without file priorities has no Priority column) the last one that shows, so the row still reaches the edge.
    /// </summary>
    private string FillingKey => IsVisible(LastKey) ? LastKey : Columns.LastOrDefault(column => IsVisible(column.Key))?.Key ?? LastKey;

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
    /// <remarks>T12: Progress Details' columns follow the same rule: a peer's number, its client and flags, a tracker's
    /// address and its next announce read from the first; what a peer has, the speeds, counts, sizes, progress and priority
    /// from the most. UI.R42's columns too: a seeding goal, a tracker's tier and its message from the first; bytes uploaded
    /// or downloaded and the seeding time from the most.</remarks>
    public static bool StartsAscending(string key) => key is "name" or "queue" or "type" or "status" or "extraction" or "eta" or "description" or "expires"
        or "peer" or "client" or "flags" or "tracker" or "next" or "seedGoal" or "tier" or "message";

    public static string SpokenHeader(string title, bool sorted, bool ascending)
        => !sorted ? title : ascending ? $"{title}, sorted ascending" : $"{title}, sorted descending";

    /// <summary>Rows with nothing to compare stay at the end in either direction, and equal rows keep their order.</summary>
    public static IEnumerable<T> Order<T, TKey>(IEnumerable<T> rows, Func<T, TKey> key, IComparer<TKey>? comparer, bool ascending, Func<T, bool>? isBlank = null)
    {
        var filled = rows.OrderBy(row => isBlank?.Invoke(row) == true);
        return ascending ? filled.ThenBy(key, comparer) : filled.ThenByDescending(key, comparer);
    }
}

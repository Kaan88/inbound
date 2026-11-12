using DownloadManager.Contracts;

namespace DownloadManager.Desktop.WinUI;

/// <summary>
/// One torrent the add dialog was told to add. <see cref="Files"/> is <c>all</c>, <c>chosen</c> (with
/// <see cref="FileIndexes"/>) or <c>askWhenReady</c> (a magnet whose files come later). <see cref="Route"/> is
/// <see cref="TorrentLineRoute.ThisPc"/> or <see cref="TorrentLineRoute.Provider"/>, never Unset.
/// </summary>
internal sealed record TorrentAddPick(string Input, string Name, TorrentLineRoute Route, string Files, IReadOnlyList<int>? FileIndexes)
{
    /// <summary>T3: the chosen files that aren't Normal (High or Low); null when all are Normal.</summary>
    public IReadOnlyList<TorrentFilePriorityDto>? Priorities { get; init; }

    /// <summary>
    /// T9 W2, a provider line: the folder the Host said this torrent would go to with Save to empty (its category folder),
    /// so the provider's files land where This PC's would. Null when the Host didn't say.
    /// </summary>
    public string? CategoryFolder { get; init; }

    /// <summary>T9 W2, a provider line: a .torrent file's ticked files by path and size, as the provider takes them; null for all or for the Host's rule.</summary>
    public IReadOnlyList<RemoteFilePathDto>? FilePaths { get; init; }

    // The input may be a magnet with trackers: never in a log line.
    public override string ToString() => "[torrent pick]";
}

/// <summary>
/// What the torrent add dialog was told to do. A null <see cref="Destination"/> is each torrent's category folder (the
/// Host decides). <see cref="StartMode"/> is <c>startNow</c>, <c>queue</c> or <c>later</c>, as
/// <see cref="TorrentAddRequest.StartMode"/>.
/// </summary>
internal sealed record TorrentAddChoice(IReadOnlyList<TorrentAddPick> Picks, string? Destination, string StartMode, int? QueueId)
{
    /// <summary>"Show in list" was pressed on a line already in the list: nothing is added, and this job is shown instead.</summary>
    public Guid? ShowJobId { get; init; }

    public static TorrentAddChoice ShowJob(Guid id) => new([], null, "startNow", null) { ShowJobId = id };

    /// <summary>
    /// T10: the lines whose torrent was already in the list when the dialog closed with a choice. A finished .torrent
    /// download among them is a copy, which the main window then removes (the Host's <c>Existing</c> rule).
    /// </summary>
    public IReadOnlyList<string> ListedInputs { get; init; } = [];

    public override string ToString() => "[torrent add choice]";
}

/// <summary>How a line of the torrent add dialog stands.</summary>
internal enum TorrentLineState
{
    /// <summary>Asked about (torrent.inspect) and not answered yet.</summary>
    Reading,
    Ready,
    Failed,
    /// <summary>The same torrent is already in the download list.</summary>
    Listed,
    /// <summary>The same torrent as a line above it.</summary>
    Duplicate,
}

/// <summary>
/// Whether the provider has a line's torrent cached. Only said on a line when an account that takes torrents exists; it
/// never picks the line's route.
/// </summary>
internal enum TorrentCacheState
{
    /// <summary>Not asked: no account, or the line isn't read yet.</summary>
    None,
    Checking,
    Cached,
    NotCached,
    /// <summary>The check failed or couldn't be made.</summary>
    Unknown,
}

/// <summary>How a cache badge looks (T9 W3).</summary>
internal enum CacheBadgeKind
{
    /// <summary>A filled accent badge: the provider has it.</summary>
    Cached,
    /// <summary>A neutral outline badge.</summary>
    NotCached,
    /// <summary>A neutral outline badge while the answer comes.</summary>
    Checking,
    /// <summary>A muted badge, with the reason in its tooltip.</summary>
    Unknown,
}

/// <summary>
/// The provider's cache answer as a badge next to a line's provider choice (T9 W3), and the short line under it. The same
/// badge is used wherever the desktop says an item is cached on the provider.
/// </summary>
internal sealed record CacheBadge(CacheBadgeKind Kind, string Text, string Note, string? ToolTip)
{
    /// <summary>Null when nothing was asked (no account, or the line isn't read yet).</summary>
    public static CacheBadge? For(TorrentCacheState state, string? providerName, string? problem = null)
    {
        if (string.IsNullOrWhiteSpace(providerName)) return null;
        return state switch
        {
            TorrentCacheState.Cached => Cached(providerName),
            TorrentCacheState.NotCached => new(CacheBadgeKind.NotCached, $"Not cached on {providerName}", string.Empty, null),
            TorrentCacheState.Checking => new(CacheBadgeKind.Checking, $"Checking {providerName}…", string.Empty, null),
            TorrentCacheState.Unknown => new(CacheBadgeKind.Unknown, $"Couldn't check {providerName}", string.Empty,
                string.IsNullOrWhiteSpace(problem) ? $"{providerName} didn't answer the check." : problem.Trim()),
            _ => null,
        };
    }

    /// <summary>"Cached on TorBox", with what that means for this add.</summary>
    public static CacheBadge Cached(string providerName)
        => new(CacheBadgeKind.Cached, $"Cached on {providerName}", $"{providerName} has it, so it downloads right away", null);

    /// <summary>
    /// A provider item's status says it is cached ("Cached on TorBox", RemoteRows): shown as the same filled badge where
    /// it stands alone, such as My provider's item header. List cells keep their status text.
    /// </summary>
    public static bool IsCachedWords(string? status) => status?.StartsWith("Cached on ", StringComparison.Ordinal) == true;

    /// <summary>What a screen reader hears: the badge, its line and its reason.</summary>
    public string Spoken => string.Join(". ", new[] { Text, Note, ToolTip ?? string.Empty }.Where(part => part.Length > 0));

    public override string ToString() => Text;
}

/// <summary>One magnet or .torrent file in the add dialog. UI thread only.</summary>
internal sealed class TorrentAddLine
{
    private bool _wanted = true;

    internal TorrentAddLine(string input, bool fromPaste, TorrentLineRoute route)
    {
        Input = input;
        FromPaste = fromPaste;
        Route = route;
    }

    /// <summary>The magnet or the full path, as typed. Never logged, and never shown whole for a magnet.</summary>
    public string Input { get; }

    /// <summary>It came from the paste box, so it goes when the box no longer holds it.</summary>
    public bool FromPaste { get; }

    public bool IsMagnet => LinkExtractor.IsMagnet(Input);

    public TorrentInspectResult? Result { get; internal set; }

    /// <summary>The line above that turned out to be the same torrent.</summary>
    public TorrentAddLine? SameAs { get; internal set; }

    public TorrentLineRoute Route { get; internal set; }

    /// <summary>The chosen files' indexes; null means every file.</summary>
    public IReadOnlyList<int>? ChosenIndexes { get; internal set; }

    /// <summary>
    /// T9 W2: the folder the Host gave when it was asked with Save to empty (the torrent's category folder). Kept apart
    /// from <see cref="TorrentInspectResult.Destination"/>, which follows each Save to the user types; null until known.
    /// </summary>
    public string? CategoryFolder { get; internal set; }

    /// <summary>T3: the chosen files' priorities that aren't Normal, by index; null when there are none.</summary>
    public IReadOnlyDictionary<int, string>? Priorities { get; internal set; }

    public TorrentCacheState Cache { get; internal set; }

    /// <summary>Why the cache couldn't be checked, for the badge's tooltip; null otherwise.</summary>
    public string? CacheProblem { get; internal set; }

    public TorrentLineState State => Result switch
    {
        null => TorrentLineState.Reading,
        { Ok: false } => TorrentLineState.Failed,
        _ when SameAs is not null => TorrentLineState.Duplicate,
        { ExistingJobId: not null } => TorrentLineState.Listed,
        _ => TorrentLineState.Ready,
    };

    /// <summary>The job that already holds this torrent, for "Show in list"; null unless the line is <see cref="TorrentLineState.Listed"/>.</summary>
    public Guid? ListedJobId => State == TorrentLineState.Listed ? Result?.ExistingJobId : null;

    /// <summary>Only a line that was read and is new can be ticked.</summary>
    public bool CanSelect => State == TorrentLineState.Ready;

    /// <summary>Ticked: a line still being read keeps its tick, and one that can't be added shows none.</summary>
    public bool IsSelected
    {
        get => _wanted && State is TorrentLineState.Ready or TorrentLineState.Reading;
        internal set => _wanted = value;
    }

    public string Kind => IsMagnet ? "Magnet" : ".torrent file";

    /// <summary>The torrent's name once it is known, else "Magnet link" or the file's name.</summary>
    public string Name
    {
        get
        {
            if (Result is { Ok: true, Name: { Length: > 0 } name } && !string.IsNullOrWhiteSpace(name)) return name.Trim();
            if (IsMagnet) return "Magnet link";
            var path = Input.Trim();
            var cut = path.LastIndexOfAny(['\\', '/']);
            return cut >= 0 && cut < path.Length - 1 ? path[(cut + 1)..] : path;
        }
    }

    /// <summary>Null while the files are not known (a magnet, or a line not read yet).</summary>
    public int? FileCount => Result is { Ok: true, Files: { } files } ? files.Count : null;

    public long? TotalBytes => Result is { Ok: true } result ? result.TotalBytes : null;

    /// <summary>
    /// Some files are left out. T9: a .torrent file's choice is used on either route, except on the provider past
    /// <see cref="TorrentAddLines.MostProviderFiles"/>, where it can't be sent and every file is taken.
    /// </summary>
    public bool IsPartial
        => ChosenIndexes is { } chosen && FileCount is { } count && chosen.Count < count
           && (Route != TorrentLineRoute.Provider || count <= TorrentAddLines.MostProviderFiles);

    /// <summary>What it will download, when known.</summary>
    public long? WantedBytes
    {
        get
        {
            if (!IsPartial || Result?.Files is not { } files) return TotalBytes;
            var chosen = ChosenIndexes!.ToHashSet();
            return files.Where(file => chosen.Contains(file.Index)).Sum(file => file.Size);
        }
    }

    /// <summary>The words under the name: kind · size · file count, or what keeps the line from being added.</summary>
    public string Detail
    {
        get
        {
            switch (State)
            {
                case TorrentLineState.Reading:
                    return $"{Kind} · {(IsMagnet ? "Checking…" : "Reading…")}";
                case TorrentLineState.Failed:
                    return string.IsNullOrWhiteSpace(Result?.Error) ? "This torrent couldn't be read." : Result.Error.Trim();
                case TorrentLineState.Listed:
                    return $"{Kind} · Already in the list";
                case TorrentLineState.Duplicate:
                    return $"{Kind} · Same torrent as above";
            }

            var parts = new List<string> { Kind };
            if (TotalBytes is not { } total) parts.Add("Size not known yet");
            else parts.Add(IsPartial ? $"{UiFormat.Bytes(WantedBytes ?? 0)} of {UiFormat.Bytes(total)}" : UiFormat.Bytes(total));
            if (FileCount is { } count)
            {
                var files = count == 1 ? "1 file" : $"{count} files";
                parts.Add(IsPartial ? $"{ChosenIndexes!.Count} of {files}" : files);
            }

            return string.Join(" · ", parts);
        }
    }

    /// <summary>The cache words next to the detail ("Cached on TorBox"), or empty when nothing was asked.</summary>
    public string CacheText(string? providerName) => providerName is null ? string.Empty : Cache switch
    {
        TorrentCacheState.Checking => $"Checking {providerName}…",
        TorrentCacheState.Cached => $"Cached on {providerName}",
        TorrentCacheState.NotCached => $"Not cached on {providerName}",
        TorrentCacheState.Unknown => $"Couldn't check {providerName}",
        _ => string.Empty,
    };

    /// <summary>T9 W3: the cache answer as a badge next to the provider choice; null when nothing was asked.</summary>
    public CacheBadge? Badge(string? providerName) => CacheBadge.For(Cache, providerName, CacheProblem);

    /// <summary>The detail is a problem, shown in the error colour.</summary>
    public bool IsProblem => State == TorrentLineState.Failed;

    /// <summary>For the tooltip: a magnet only by its info hash (no trackers), a file by its name.</summary>
    public string ToolTip
        => IsMagnet && Result is { Ok: true, InfoHash: { Length: > 0 } hash }
            ? TorrentRules.ShortMagnet(new TorrentDto { InfoHash = hash })
            : Name;

    public override string ToString() => "[torrent line]";
}

/// <summary>
/// The torrent add dialog's lines, kept free of WinUI so they are tested (tests/DownloadManager.Core.Tests links this
/// file). The dialog asks the Host about each line and hands the answer to <see cref="Inspected"/>. UI thread only.
/// </summary>
internal sealed class TorrentAddLines
{
    /// <summary>More than this many lines are not listed: each one is a row and a question to the Host.</summary>
    public const int MostLines = 200;

    private readonly List<TorrentAddLine> _lines = [];
    private readonly TorrentLineRoute _defaultRoute;
    private bool _cappedStart;
    private bool _cappedPaste;

    /// <param name="providerName">The account that also takes torrents, or null: then every line is This PC.</param>
    public TorrentAddLines(TorrentLineRoute defaultRoute, bool chooseFiles, string? providerName)
    {
        ProviderName = string.IsNullOrWhiteSpace(providerName) ? null : providerName;
        _defaultRoute = ProviderName is null ? TorrentLineRoute.ThisPc : defaultRoute;
        ChooseFiles = chooseFiles;
    }

    public IReadOnlyList<TorrentAddLine> Lines => _lines;

    public string? ProviderName { get; }

    public bool ProviderAvailable => ProviderName is not null;

    /// <summary>The setting "Ask which files to download before starting".</summary>
    public bool ChooseFiles { get; }

    /// <summary>Some inputs were left out because <see cref="MostLines"/> were listed already.</summary>
    public bool Capped => _cappedStart || _cappedPaste;

    /// <summary>Adds a line for each magnet or .torrent path not listed yet; returns the new ones.</summary>
    public IReadOnlyList<TorrentAddLine> Add(IEnumerable<string> inputs, bool fromPaste)
    {
        var added = new List<TorrentAddLine>();
        foreach (var raw in inputs)
        {
            if (!TorrentRules.IsTorrentInput(raw)) continue;
            var input = raw.Trim();
            if (_lines.Any(line => string.Equals(line.Input, input, StringComparison.OrdinalIgnoreCase))) continue;
            if (_lines.Count >= MostLines)
            {
                if (fromPaste) _cappedPaste = true;
                else _cappedStart = true;
                continue;
            }

            var line = new TorrentAddLine(input, fromPaste, _defaultRoute);
            _lines.Add(line);
            added.Add(line);
        }

        Recompute();
        return added;
    }

    /// <summary>
    /// Follows the paste box: a line for each magnet or .torrent path in it, and the lines that came from it and are no
    /// longer in it go. Lines the dialog started with stay.
    /// </summary>
    public (IReadOnlyList<TorrentAddLine> Added, IReadOnlyList<TorrentAddLine> Removed) SyncPaste(string? text)
    {
        var inputs = LinkExtractor.Inputs(text).Where(TorrentRules.IsTorrentInput).Select(input => input.Trim()).ToList();
        var kept = new HashSet<string>(inputs, StringComparer.OrdinalIgnoreCase);
        var removed = _lines.Where(line => line.FromPaste && !kept.Contains(line.Input)).ToList();
        foreach (var line in removed) _lines.Remove(line);
        _cappedPaste = false;
        var added = Add(inputs, fromPaste: true);
        return (added, removed);
    }

    /// <summary>The Host's answer for a line. False when the line is no longer listed.</summary>
    /// <param name="askedWithoutFolder">The Host was asked with Save to empty, so its folder is the category folder.</param>
    public bool Inspected(TorrentAddLine line, TorrentInspectResult? result, bool askedWithoutFolder = true)
    {
        if (!_lines.Contains(line)) return false;
        line.Result = result ?? new TorrentInspectResult { Error = "This torrent couldn't be read." };
        if (askedWithoutFolder && line.Result is { Ok: true, Destination: { Length: > 0 } category }) line.CategoryFolder = category;
        Recompute();
        return true;
    }

    /// <summary>
    /// T3: the Host's answer for a read line asked about another Save to (<paramref name="folder"/>; null is each category
    /// folder). Only where it goes and what is free there change; the category folder only from an answer for null.
    /// </summary>
    public bool SpaceAnswered(TorrentAddLine line, string? folder, TorrentInspectResult answer)
    {
        if (!_lines.Contains(line) || line.Result is not { Ok: true } result) return false;
        result.Destination = answer.Ok ? answer.Destination : folder;
        result.FreeBytes = answer.Ok ? answer.FreeBytes : null;
        result.SpaceProblem = answer.Ok ? answer.SpaceProblem : null;
        if (folder is null && answer is { Ok: true, Destination: { Length: > 0 } category }) line.CategoryFolder = category;
        return true;
    }

    /// <summary>Each magnet and .torrent line is checked against the provider's cache, only when an account exists.</summary>
    public bool ChecksCache => ProviderAvailable;

    /// <summary>
    /// The lines whose cache check can start now, each with what to ask <c>remote.classify</c>: a magnet of the info hash
    /// alone, so a .torrent file is never uploaded and a magnet's trackers are not sent for this. Each is marked
    /// <see cref="TorrentCacheState.Checking"/>, so a line is asked about once. A line with no usable hash is
    /// <see cref="TorrentCacheState.Unknown"/> at once.
    /// </summary>
    public IReadOnlyList<(TorrentAddLine Line, string Query)> StartCacheChecks()
    {
        var start = new List<(TorrentAddLine, string)>();
        if (!ChecksCache) return start;
        foreach (var line in _lines.Where(line => line.Cache == TorrentCacheState.None && line.State == TorrentLineState.Ready))
        {
            if (CacheQuery(line.Result?.InfoHash) is { } query)
            {
                line.Cache = TorrentCacheState.Checking;
                start.Add((line, query));
            }
            else
            {
                line.Cache = TorrentCacheState.Unknown;
            }
        }

        return start;
    }

    /// <summary>A v1 info hash (40 hex) as a bare magnet; null for anything else, which the provider check can't take.</summary>
    internal static string? CacheQuery(string? infoHash)
        => infoHash is { Length: 40 } hash && hash.All(char.IsAsciiHexDigit) ? "magnet:?xt=urn:btih:" + hash.ToLowerInvariant() : null;

    public bool IsListed(TorrentAddLine line) => _lines.Contains(line);

    /// <summary>The provider's answer for a line. Only the cache words change, never the route. False when the line went.</summary>
    public bool CacheChecked(TorrentAddLine line, RemoteClassifyResult? result)
    {
        if (!_lines.Contains(line)) return false;
        line.Cache = result switch
        {
            { Ok: true, Cached: true } => TorrentCacheState.Cached,
            { Ok: true, Cached: false } => TorrentCacheState.NotCached,
            _ => TorrentCacheState.Unknown,
        };
        line.CacheProblem = line.Cache != TorrentCacheState.Unknown || result is null ? null
            : !string.IsNullOrWhiteSpace(result.Error) ? RemoteText.Failure(result.Error, null, ProviderName)
            : result.Ok ? $"{ProviderName} didn't say whether it has it." : null;
        return true;
    }

    public void Select(TorrentAddLine line, bool selected)
    {
        if (line.CanSelect) line.IsSelected = selected;
    }

    /// <summary>Ticks every line that can be added, or clears them all when they all are ticked.</summary>
    public void ToggleAll()
    {
        var all = _lines.Where(line => line.CanSelect).All(line => line.IsSelected);
        foreach (var line in _lines.Where(line => line.CanSelect)) line.IsSelected = !all;
    }

    /// <summary>True, false, or null for some, as Select all shows it.</summary>
    public bool? SelectAllState()
    {
        var selectable = _lines.Where(line => line.CanSelect).ToList();
        if (selectable.Count == 0 || selectable.All(line => !line.IsSelected)) return false;
        return selectable.All(line => line.IsSelected) ? true : null;
    }

    /// <summary>This PC or the provider. Without a provider every line stays on This PC.</summary>
    public void SetRoute(TorrentAddLine line, TorrentLineRoute route)
    {
        if (!ProviderAvailable || route == TorrentLineRoute.Unset) return;
        line.Route = route;
    }

    /// <summary>The most files a provider line takes by path before it is added (the Host's limit for remote.add).</summary>
    public const int MostProviderFiles = 5000;

    /// <summary>
    /// The file choice can be made here: a .torrent with several files, with the setting on. T9: on the provider too, by
    /// path and size, up to <see cref="MostProviderFiles"/>.
    /// </summary>
    public bool CanChooseFilesFor(TorrentAddLine line)
        => ChooseFiles && line.State == TorrentLineState.Ready && !line.IsMagnet && line.FileCount > 1
           && (line.Route != TorrentLineRoute.Provider || line.FileCount <= MostProviderFiles);

    /// <summary>The file tree's choice for a line.</summary>
    /// <param name="priorities">T3: the tree's chosen files that aren't Normal, by file id; null keeps none.</param>
    public void SetFileChoice(TorrentAddLine line, RemoteFileChoice choice, IReadOnlyDictionary<string, string>? priorities = null)
    {
        if (line.Result?.Files is not { } files) return;
        var indexes = TorrentRules.Indexes(choice, files);
        line.ChosenIndexes = choice.All || indexes is null || indexes.Count >= files.Count ? null : indexes;
        var byIndex = new Dictionary<int, string>();
        foreach (var (id, priority) in priorities ?? new Dictionary<string, string>())
        {
            if (int.TryParse(id, System.Globalization.NumberStyles.None, System.Globalization.CultureInfo.InvariantCulture, out var index)
                && TorrentPriorities.IsKnown(priority) && priority is not TorrentPriorities.Normal and not TorrentPriorities.Skip)
                byIndex[index] = priority;
        }

        line.Priorities = byIndex.Count == 0 ? null : byIndex;
    }

    /// <summary>The lines that will be added.</summary>
    public IReadOnlyList<TorrentAddLine> Chosen => _lines.Where(line => line.IsSelected && line.State == TorrentLineState.Ready).ToList();

    private bool StillReading => _lines.Any(line => line.IsSelected && line.State == TorrentLineState.Reading);

    private bool HasNoFiles(TorrentAddLine line) => CanChooseFilesFor(line) && line.ChosenIndexes is { Count: 0 };

    /// <summary>Add can be pressed: something is ticked, nothing ticked is still being read, and each has a route and files.</summary>
    public bool CanAdd
    {
        get
        {
            var chosen = Chosen;
            return chosen.Count > 0 && !StillReading && chosen.All(line => line.Route != TorrentLineRoute.Unset && !HasNoFiles(line));
        }
    }

    /// <summary>"Add" or "Add 3".</summary>
    public string PrimaryText => Chosen.Count >= 2 ? $"Add {Chosen.Count}" : "Add";

    /// <summary>"Selected 2.1 GB in 2 torrents", counting only the sizes that are known.</summary>
    public string Summary()
    {
        var chosen = Chosen;
        if (chosen.Count == 0) return _lines.Count == 0 ? string.Empty : "No torrents selected";
        var count = chosen.Count == 1 ? "1 torrent" : $"{chosen.Count} torrents";
        var sizes = chosen.Select(line => line.WantedBytes).OfType<long>().ToList();
        return sizes.Count > 0 ? $"Selected {UiFormat.Bytes(sizes.Sum())} in {count}" : $"Selected {count}";
    }

    /// <summary>Why Add can't be pressed yet, when the user has something to do; else empty.</summary>
    public string Problem()
    {
        var chosen = Chosen;
        if (ProviderName is not null && chosen.Any(line => line.Route == TorrentLineRoute.Unset))
            return $"Choose This PC or {ProviderName} for each ticked torrent.";
        if (chosen.FirstOrDefault(HasNoFiles) is { } empty) return $"Choose at least one file of {empty.Name}.";
        return string.Empty;
    }

    /// <summary>The words under the list, the summary first.</summary>
    public string Status()
    {
        var parts = new[] { Summary(), Problem(), Capped ? $"Only the first {MostLines} are listed." : string.Empty };
        return string.Join(". ", parts.Where(part => part.Length > 0).Select(part => part.TrimEnd('.'))) is { Length: > 0 } words ? words + "." : string.Empty;
    }

    /// <summary>"A magnet asks which files to download once its file list arrives" applies to a listed line.</summary>
    public bool MagnetNoteApplies
        => ChooseFiles && _lines.Any(line => line.IsMagnet && line.Route != TorrentLineRoute.Provider
                                             && line.State is TorrentLineState.Ready or TorrentLineState.Reading);

    /// <summary>T10: the inputs of the lines whose torrent is already in the list.</summary>
    public IReadOnlyList<string> ListedInputs() => _lines.Where(line => line.State == TorrentLineState.Listed).Select(line => line.Input).ToList();

    /// <summary>The chosen lines as the dialog hands them on. Only meaningful while <see cref="CanAdd"/>.</summary>
    public IReadOnlyList<TorrentAddPick> Picks()
        => Chosen.Where(line => line.Route != TorrentLineRoute.Unset).Select(PickOf).ToList();

    private TorrentAddPick PickOf(TorrentAddLine line)
    {
        if (line.Route == TorrentLineRoute.Provider) return ProviderPickOf(line);
        if (line.Result?.Files is null)
            return new TorrentAddPick(line.Input, line.Name, line.Route, ChooseFiles ? "askWhenReady" : "all", null);
        if (!CanChooseFilesFor(line)) return new TorrentAddPick(line.Input, line.Name, line.Route, "all", null);
        var priorities = TorrentPriorities.ToSend(line.Priorities);
        if (!line.IsPartial) return new TorrentAddPick(line.Input, line.Name, line.Route, "all", null) { Priorities = priorities };
        return new TorrentAddPick(line.Input, line.Name, line.Route, "chosen", line.ChosenIndexes!.ToList()) { Priorities = priorities };
    }

    /// <summary>
    /// T9 W2: a provider line is sent from this dialog. A .torrent file whose files were shown takes the ticked ones by
    /// path and size (<c>chosen</c>), or <c>all</c>; a .torrent file whose files aren't chosen here (the setting is off,
    /// one file, or too many) takes <c>all</c>, as on This PC, so no Choose Files window follows. Only a magnet, whose
    /// files aren't known, is left to the Host's rule (<see cref="TorrentAddPick.Files"/> is then empty). Priorities are
    /// This PC's only.
    /// </summary>
    private TorrentAddPick ProviderPickOf(TorrentAddLine line)
    {
        var folder = line.CategoryFolder;
        if (line.Result?.Files is not { } files)
            return new TorrentAddPick(line.Input, line.Name, line.Route, string.Empty, null) { CategoryFolder = folder };
        if (!CanChooseFilesFor(line))
            return new TorrentAddPick(line.Input, line.Name, line.Route, "all", null) { CategoryFolder = folder };
        if (!line.IsPartial) return new TorrentAddPick(line.Input, line.Name, line.Route, "all", null) { CategoryFolder = folder };
        var chosen = line.ChosenIndexes!.ToHashSet();
        var paths = files.Where(file => chosen.Contains(file.Index) && !string.IsNullOrEmpty(file.Path))
            .Select(file => new RemoteFilePathDto { Path = file.Path, Size = file.Size })
            .ToList();
        return new TorrentAddPick(line.Input, line.Name, line.Route, "chosen", line.ChosenIndexes!.ToList()) { CategoryFolder = folder, FilePaths = paths };
    }

    /// <summary>
    /// T3: the free space line of a read line, for the files it would download (null when the Host didn't measure it, or
    /// the line goes to the provider).
    /// </summary>
    public static (string Words, bool IsProblem)? SpaceOf(TorrentAddLine line)
        => line.State != TorrentLineState.Ready || line.Route == TorrentLineRoute.Provider || line.Result is not { } result
            ? null
            : TorrentControlRules.SpaceWords(result.FreeBytes, line.WantedBytes, result.Destination);

    /// <summary>Marks the later of two lines with the same info hash; the first one read in list order is kept.</summary>
    private void Recompute()
    {
        var first = new Dictionary<string, TorrentAddLine>(StringComparer.OrdinalIgnoreCase);
        foreach (var line in _lines)
        {
            line.SameAs = null;
            if (line.Result is not { Ok: true, InfoHash: { Length: > 0 } hash }) continue;
            if (first.TryGetValue(hash, out var earlier)) line.SameAs = earlier;
            else first[hash] = line;
        }
    }
}

/// <summary>T9 W2: what the Host is asked when a torrent dialog line goes to the provider, with no second dialog.</summary>
internal static class TorrentAddRequests
{
    /// <summary>
    /// The provider's add for a line, with the torrent dialog's Save to, Start and Queue, and the provider's own defaults
    /// for the rest: adding is the go-ahead (<c>approveUncached</c>), as in the provider's add dialog. An empty Save to
    /// is the torrent's category folder as the Host gave it for This PC, else (not said) the default download folder.
    /// </summary>
    public static RemoteAddRequestDto ForProvider(TorrentAddPick pick, string? destination, string startMode, int? queueId, int defaultQueueId) => new()
    {
        Input = pick.Input,
        Destination = string.IsNullOrWhiteSpace(destination) ? pick.CategoryFolder : destination.Trim(),
        QueueId = queueId ?? defaultQueueId,
        StartMode = startMode is "startNow" or "queue" or "later" ? startMode : "startNow",
        ApproveUncached = true,
        Selection = pick.Files is "all" or "chosen" ? pick.Files : null,
        FilePaths = pick.Files == "chosen" ? pick.FilePaths?.ToList() : null,
    };
}

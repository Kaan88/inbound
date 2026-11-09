using DownloadManager.Containers;
using DownloadManager.Contracts;
using DownloadManager.Core.Providers;

namespace DownloadManager.Runtime.Remote;

public sealed record ContainerImportOptions
{
    public ContainerLimits Limits { get; init; } = new()
    {
        MaxLinks = 5000,
        MaxPackages = 1000,
        MaxFieldChars = 4096,
    };

    /// <summary>How long links wait in memory for the user to choose among them.</summary>
    public TimeSpan PreviewLifetime { get; init; } = TimeSpan.FromMinutes(15);

    public int MostPreviews { get; init; } = 8;

    /// <summary>Links and passwords kept in memory, across all previews.</summary>
    public long MostHeldChars { get; init; } = 8 * 1024 * 1024;
}

/// <summary>How adding one entry of a preview went. Holds no link.</summary>
public sealed record ContainerAddOutcome(int EntryId, RemoteAddResult Result);

public sealed record ContainerAddAnswer(IReadOnlyList<ContainerAddOutcome> Outcomes, string? Error = null);

/// <summary>
/// Reads a container file into a preview, and adds what the user picks from it. A decrypted link is a
/// secret, so it stays in this process, against a preview id, for a short time: a client sees entry ids
/// and scrubbed names only, and nothing here writes a link anywhere. What is added goes through
/// <see cref="RemoteJobCoordinator.AddAsync"/> like a pasted link, so a container can't skip the cache
/// check, the approval, the spacing or the dedupe, and nothing from one is ever requested directly.
/// A container's archive password goes to the archive side and nowhere else, and only a value the
/// container states to be one. Holds no lock across the file or the network.
/// </summary>
public sealed class ContainerImportService : IDisposable
{
    private const int LongestName = 200;
    private const int LongestComment = 500;
    private const int LongestFolderName = 100;

    private static readonly string[] ContainerExtensions = [".rsdf", ".ccf", ".dlc"];

    private readonly RemoteProviderRegistry _providers;
    private readonly RemoteJobCoordinator _jobs;
    private readonly ContainerImportOptions _options;
    private readonly TimeProvider _time;
    private readonly Action<string>? _log;
    private readonly Lazy<JDownloaderBridge> _bridge;
    private readonly PathRoots? _roots;
    private readonly PathRoots? _jobRoots;
    private readonly Lock _sync = new();
    private readonly Dictionary<Guid, HeldPreview> _held = [];
    private bool _disposed;

    /// <param name="bridge">Null makes the real one the first time it is asked for, which is only when the user asks.</param>
    public ContainerImportService(
        RemoteProviderRegistry providers,
        RemoteJobCoordinator jobs,
        ContainerImportOptions? options = null,
        TimeProvider? timeProvider = null,
        Action<string>? log = null,
        JDownloaderBridge? bridge = null,
        PathRoots? roots = null,
        PathRoots? jobRoots = null)
    {
        _roots = roots;
        _jobRoots = jobRoots;
        _providers = providers;
        _jobs = jobs;
        _options = options ?? new ContainerImportOptions();
        _time = timeProvider ?? TimeProvider.System;
        _log = log;
        _bridge = new Lazy<JDownloaderBridge>(() => bridge ?? new JDownloaderBridge());
    }

    /// <summary>What is held in memory right now. No links, just how many previews.</summary>
    public int HeldPreviews
    {
        get
        {
            lock (_sync) return _held.Count;
        }
    }

    /// <summary>What was read and how it would be handled. Any problem with the file comes back in words, never as an exception.</summary>
    /// <param name="path">The file's full path, or an upload's reference (<see cref="UploadFolder"/>).</param>
    /// <param name="fileName">For an upload, the name the file had, which names its packages; ignored for a path.</param>
    public async Task<RemoteContainerPreviewResult> PreviewAsync(string? path, CancellationToken cancellationToken = default, string? fileName = null)
    {
        if (_jobs.UploadGone(path) is { } gone) return new RemoteContainerPreviewResult { Error = gone };
        var uploaded = _jobs.ResolveUpload(path) is not null;
        if (Confine(ref path) is { } refused)
        {
            return new RemoteContainerPreviewResult { Error = refused };
        }

        var read = await ContainerReader.ReadAsync(path!, _options.Limits, cancellationToken).ConfigureAwait(false);
        var extension = Path.GetExtension(path!).ToLowerInvariant();
        var result = new RemoteContainerPreviewResult
        {
            Format = read.Format == ContainerFormat.Unknown ? "unknown" : read.Format.ToString().ToLowerInvariant(),
            FormatVersion = read.FormatVersion,
            CanSendToJd = extension is ".dlc" or ".ccf",
            Problems = read.Problems.Select(problem => problem.Message).ToList(),
        };

        if (read.Format == ContainerFormat.Dlc)
        {
            result.NotSupported = true;
            result.Error = read.Problems.FirstOrDefault(problem => problem.Kind == ContainerProblemKind.NotSupported)?.Message ?? DlcSupport.Explanation;
            result.Problems.Remove(result.Error);
            return result;
        }

        if (!read.Succeeded)
        {
            result.Error = read.Problems.FirstOrDefault()?.Message ?? "This container holds no links.";
            result.Problems.Remove(result.Error);
            return result;
        }

        var stem = uploaded
            ? (string.IsNullOrWhiteSpace(fileName) ? "Uploaded container" : Path.GetFileNameWithoutExtension(Path.GetFileName(fileName.Trim())))
            : Path.GetFileNameWithoutExtension(path!);
        await FillAsync(result, read, stem, cancellationToken).ConfigureAwait(false);
        return result;
    }

    private async Task FillAsync(RemoteContainerPreviewResult result, ContainerImportResult read, string fileStem, CancellationToken cancellationToken)
    {
        var classified = await _jobs.ClassifyAllAsync(read.Links.Select(link => link.Url).ToList(), cancellationToken).ConfigureAwait(false);
        var previewId = Guid.NewGuid();

        var taker = _providers.All.FirstOrDefault(entry => entry.Capabilities.HasFlag(RemoteCapabilities.WebDownloads)) ?? _providers.All.FirstOrDefault();
        result.AccountProblem = taker?.AccountProblem();

        var packageIndex = new Dictionary<ContainerPackage, int>(ReferenceEqualityComparer.Instance);
        var groups = new Dictionary<int, HeldGroup>();
        foreach (var package in read.Packages)
        {
            var index = result.Packages.Count;
            packageIndex[package] = index;
            var hide = (IEnumerable<string?>)package.ArchivePasswordCandidates;
            var name = RemoteText.Clean(package.Name, LongestName, _providers.Mask, hide);
            result.Packages.Add(new RemoteContainerPackageDto
            {
                Index = index,
                Name = name,
                Comment = RemoteText.Clean(package.Comment, LongestComment, _providers.Mask, hide),
                HasArchivePassword = ArchivePasswordOf(package) is not null,
            });
            groups[index] = new HeldGroup(
                new RemoteJobPlacement(PackageKey(previewId, index), name ?? RemoteText.Clean(fileStem, LongestName, _providers.Mask), FolderNameOf(name)),
                ArchivePasswordOf(package),
                package.ArchivePasswordCandidates);
        }

        // Links in no package still came together, so the parts of a set among them still meet.
        var loose = new HeldGroup(new RemoteJobPlacement(PackageKey(previewId, null), RemoteText.Clean(fileStem, LongestName, _providers.Mask), null), null, []);
        var heldEntries = new Dictionary<int, HeldEntry>();
        long heldChars = 0;
        var notes = new HashSet<string>(StringComparer.Ordinal);
        for (var i = 0; i < read.Links.Count; i++)
        {
            var link = read.Links[i];
            var kind = classified[i];
            var index = link.Package is { } package && packageIndex.TryGetValue(package, out var found) ? found : (int?)null;
            var group = index is { } known ? groups[known] : loose;
            // Every value the container states as a password, used or not, and they all get the same care as the first.
            var hide = group.ArchivePasswordCandidates.Prepend(link.Url).ToList();
            var (word, reason, canAdd) = Describe(kind, taker);
            var name = RemoteText.Clean(link.FileName ?? kind.Name ?? NameInLink(link.Url), LongestName, _providers.Mask, hide);

            var entryId = i + 1;
            result.Entries.Add(new RemoteContainerEntryDto
            {
                EntryId = entryId,
                PackageIndex = index,
                DisplayHost = RemoteText.Clean(kind.DisplayHost ?? HostOf(link.Url), LongestName, _providers.Mask, hide),
                DisplayName = name,
                Size = link.SizeBytes,
                Kind = word,
                Reason = RemoteText.Clean(reason, 500, _providers.Mask, hide),
                CanAdd = canAdd,
            });
            if (index is { } counted)
            {
                result.Packages[counted].EntryCount++;
            }

            // Such as a list of sites that couldn't be refreshed. A note on any other kind is about adding it directly, which isn't on offer here.
            if (kind.Kind == RemoteInputKind.OrdinaryLink && RemoteText.Clean(kind.Note, 300, _providers.Mask, hide) is { } note)
            {
                notes.Add(note);
            }

            // What can't be added is not kept: a link nobody can pick has no reason to stay in memory.
            heldEntries[entryId] = canAdd ? new HeldEntry(link.Url, group, name) : new HeldEntry(null, group, name) { Refusal = reason };
            heldChars += canAdd ? link.Url.Length + sizeof(int) : 0;
        }

        foreach (var (index, group) in groups)
        {
            heldChars += group.ArchivePasswordCandidates.Sum(candidate => candidate.Length);
            if (group.ArchivePasswordCandidates.Count > 1)
            {
                result.Problems.Add($"Package \"{result.Packages[index].Name ?? "without a name"}\" names {group.ArchivePasswordCandidates.Count} possible archive passwords. Only the first is used. If it is wrong, extraction asks for the password.");
            }
        }

        result.Problems.AddRange(notes);
        result.Ok = true;
        if (!heldEntries.Values.Any(entry => entry.Link is not null))
        {
            return;
        }

        if (heldChars > _options.MostHeldChars)
        {
            result.Ok = false;
            result.Error = "The links in this container take up too much memory to keep while you choose.";
            result.Entries.Clear();
            result.Packages.Clear();
            return;
        }

        result.PreviewId = previewId;
        result.ExpiresAt = Hold(previewId, heldEntries, heldChars);
    }

    /// <summary>The first value the container states is an archive password. Only a single line can be one, as far as the tool is concerned.</summary>
    private static string? ArchivePasswordOf(ContainerPackage package)
        => package.ArchivePasswordCandidates.FirstOrDefault(candidate => RemoteJobRules.DescribeBadArchivePassword(candidate) is null && candidate.Length > 0);

    private static string PackageKey(Guid previewId, int? index) => $"remote:container:{previewId:N}:{(index is { } known ? known.ToString() : "loose")}";

    /// <summary>A folder for the package, or null to use the destination itself. The name is untrusted and ends up in a path.</summary>
    private static string? FolderNameOf(string? name)
    {
        if (string.IsNullOrWhiteSpace(name))
        {
            return null;
        }

        var segment = RemotePaths.CleanSegment(name.Replace('/', '_').Replace('\\', '_'));
        if (segment is null)
        {
            return null;
        }

        segment = segment.Length <= LongestFolderName ? segment : RemotePaths.CleanSegment(segment[..LongestFolderName]);
        return segment;
    }

    private static (string Word, string? Reason, bool CanAdd) Describe(RemoteInputClassification kind, RemoteProviderEntry? taker)
    {
        switch (kind.Kind)
        {
            case RemoteInputKind.FolderLink:
                return ("folderLink", null, true);
            case RemoteInputKind.Magnet:
                return ("magnet", null, true);
            case RemoteInputKind.HosterLink:
                var down = kind.HosterAvailable == false
                    ? $"{kind.ProviderName} reports that {kind.HosterName ?? "this site"} is unavailable right now{(kind.HosterNote is { } note ? $": {note}" : ".")}"
                    : null;
                return ("hosterLink", down, true);
            case RemoteInputKind.Undetermined:
                return ("undetermined", kind.Reason, true);
            case RemoteInputKind.TorrentFile:
                return ("unsupported", "A container can't name a file on this computer.", false);
            case RemoteInputKind.ProviderDirectLink:
                return ("unsupported", "This is a direct download link that holds an account key, so it isn't used from a container. Add it yourself as a normal download if you trust this file.", false);
            case RemoteInputKind.OrdinaryLink:
                return ("unsupported", OrdinaryReason(taker), false);
            default:
                return ("unsupported", kind.Reason ?? "This link can't be used.", false);
        }
    }

    private static string OrdinaryReason(RemoteProviderEntry? taker)
    {
        const string normal = "It can be added as a normal download.";
        if (taker is null)
        {
            return $"This app has no provider that could fetch this. {normal}";
        }

        return taker.AccountProblem() is { } problem
            ? $"{problem} Until then this app can't tell whether {taker.DisplayName} would take this link. {normal}"
            : $"{taker.DisplayName} won't take this, because it isn't from a site {taker.DisplayName} fetches from. {normal}";
    }

    private static string? HostOf(string link)
        => Uri.TryCreate(link, UriKind.Absolute, out var uri) && uri.Scheme is "http" or "https" or "ftp" ? uri.IdnHost : null;

    /// <summary>
    /// The link's last segment, only when it looks like a file's name. Anything else is more likely the
    /// file's id on the site, which is as good as the link.
    /// </summary>
    private static string? NameInLink(string link)
    {
        if (!Uri.TryCreate(link, UriKind.Absolute, out var uri) || uri.Scheme is not ("http" or "https" or "ftp"))
        {
            return null;
        }

        var path = uri.AbsolutePath;
        var last = Uri.UnescapeDataString(path[(path.LastIndexOf('/') + 1)..]);
        var extension = Path.GetExtension(last);
        return last.Length <= LongestName && extension.Length is >= 2 and <= 9 && extension.Skip(1).All(char.IsAsciiLetterOrDigit)
            ? last
            : null;
    }

    /// <summary>
    /// <see cref="RefusePath"/>, and on a server also its roots: a client there is not the machine's user, so it may only
    /// have the server read files in the folders it may use. The path becomes the resolved one, so a link can't lead out.
    /// An upload's reference becomes its file in the upload folder, which it can only name, so the roots don't apply.
    /// </summary>
    private string? Confine(ref string? path)
    {
        if (_jobs.ResolveUpload(path) is { } upload)
        {
            path = upload;
            return RefusePath(path);
        }

        if (RefusePath(path) is { } refused) return refused;
        if (_roots is null) return null;
        if (_roots.Allowed(path) is not { } inside) return _roots.OutsideFileText();
        path = inside;
        return null;
    }

    /// <returns>Why it can't be used. Before anything looks at it: asking whether a file on a network path exists already sends this Windows account's credentials there.</returns>
    private static string? RefusePath(string? path)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            return "Choose a container file.";
        }

        if (RemoteInputClassifier.IsNetworkOrDevicePath(path))
        {
            return "Only files on a drive of this computer can be opened, not ones on a network path.";
        }

        if (OperatingSystem.IsWindows()
                ? path.Length < 3 || !char.IsAsciiLetter(path[0]) || path[1] != ':' || path[2] is not ('\\' or '/') || path.Any(char.IsControl)
                : !Path.IsPathFullyQualified(path) || path.Any(char.IsControl))
        {
            return OperatingSystem.IsWindows()
                ? "The file has to be given by its full path, starting with a drive."
                : "The file has to be given by its full path, starting with /.";
        }

        return ContainerExtensions.Contains(Path.GetExtension(path), StringComparer.OrdinalIgnoreCase)
            ? null
            : "Only .rsdf, .ccf and .dlc files can be imported.";
    }

    // ---- Keeping them for a while -------------------------------------------------------------

    private DateTimeOffset Hold(Guid previewId, Dictionary<int, HeldEntry> entries, long chars)
    {
        var expires = _time.GetUtcNow() + _options.PreviewLifetime;
        var timer = _time.CreateTimer(state => Forget((Guid)state!), previewId, _options.PreviewLifetime, Timeout.InfiniteTimeSpan);
        var held = new HeldPreview(entries, expires, chars, timer);
        List<ITimer> stale = [];
        lock (_sync)
        {
            if (_disposed)
            {
                stale.Add(timer);
            }
            else
            {
                _held[previewId] = held;

                // The oldest goes, which is at worst an import to do again. Memory is what this bounds.
                while (_held.Count > 1 && (_held.Count > _options.MostPreviews || _held.Values.Sum(preview => preview.Chars) > _options.MostHeldChars))
                {
                    var (oldestId, oldest) = _held.Where(pair => pair.Key != previewId).MinBy(pair => pair.Value.ExpiresAt);
                    _held.Remove(oldestId);
                    stale.Add(oldest.Timer);
                }
            }
        }

        foreach (var old in stale) old.Dispose();
        return expires;
    }

    private void Forget(Guid previewId)
    {
        ITimer? timer = null;
        lock (_sync)
        {
            if (_held.Remove(previewId, out var preview))
            {
                timer = preview.Timer;
            }
        }

        timer?.Dispose();
    }

    private HeldPreview? Find(Guid previewId)
    {
        HeldPreview? preview;
        lock (_sync) _held.TryGetValue(previewId, out preview);
        if (preview is not null && preview.ExpiresAt <= _time.GetUtcNow())
        {
            Forget(previewId);
            return null;
        }

        return preview;
    }

    // ---- Adding -------------------------------------------------------------------------------

    /// <summary>Each chosen entry on its own, through the same path as a pasted link. One that can't be added doesn't stop the others.</summary>
    public async Task<ContainerAddAnswer> AddAsync(RemoteContainerAddRequest request, CancellationToken cancellationToken = default)
    {
        if (Find(request.PreviewId) is not { } preview)
        {
            return new ContainerAddAnswer([], "This preview has expired or isn't known. Import the file again.");
        }

        var chosen = (request.EntryIds ?? []).Distinct().ToList();
        if (chosen.Count == 0)
        {
            return new ContainerAddAnswer([], "Choose at least one entry.");
        }

        var outcomes = new List<ContainerAddOutcome>(chosen.Count);
        foreach (var entryId in chosen)
        {
            cancellationToken.ThrowIfCancellationRequested();
            outcomes.Add(new ContainerAddOutcome(entryId, await AddOneAsync(preview, entryId, request, cancellationToken).ConfigureAwait(false)));
        }

        return new ContainerAddAnswer(outcomes);
    }

    private async Task<RemoteAddResult> AddOneAsync(HeldPreview preview, int entryId, RemoteContainerAddRequest request, CancellationToken cancellationToken)
    {
        string? link;
        HeldEntry? entry;
        lock (_sync)
        {
            preview.Entries.TryGetValue(entryId, out entry);
            link = entry?.Link;
        }

        if (entry is null)
        {
            return new RemoteAddResult { Error = "This preview has no such entry." };
        }

        if (entry.AddedJob is { } done && _jobs.Find(done) is { } job)
        {
            return new RemoteAddResult { Job = job, Existing = true };
        }

        if (link is null)
        {
            return new RemoteAddResult { Error = entry.Refusal ?? "This entry can't be added. Import the file again." };
        }

        try
        {
            var result = await _jobs.AddAsync(new RemoteAddRequest
            {
                Input = link,
                Destination = request.Destination,
                QueueId = request.QueueId,
                StartMode = string.IsNullOrEmpty(request.StartMode) ? "startNow" : request.StartMode,
                ApprovedUncached = request.ApproveUncached,
                ArchivePassword = entry.Group.ArchivePassword,
                ArchivePasswordOffered = true,
                Placement = entry.Group.Placement,
                NameHint = entry.Name,
                SourceRoots = _jobRoots,
                DestinationRoots = _jobRoots,
            }, cancellationToken).ConfigureAwait(false);
            if (result.Job is { } added)
            {
                lock (_sync)
                {
                    // On the job now, encrypted. There is no reason to go on holding it here too.
                    entry.AddedJob = added.Id;
                    entry.Link = null;
                }
            }

            return result;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            _log?.Invoke($"A container entry could not be saved: {ex.GetType().Name}.");
            return new RemoteAddResult { Error = "This entry could not be saved. Try it again." };
        }
    }

    // ---- The optional route -------------------------------------------------------------------

    /// <summary>Only when the user asks. It hands the file to a JDownloader running on this computer, and the links stay there.</summary>
    public async Task<JDownloaderBridgeResult> SendToJDownloaderAsync(string? path, CancellationToken cancellationToken = default)
    {
        if (Confine(ref path) is { } refused)
        {
            return new JDownloaderBridgeResult(JDownloaderBridgeStatus.InvalidInput, refused);
        }

        return await _bridge.Value.SendFileAsync(path!, cancellationToken).ConfigureAwait(false);
    }

    public void Dispose()
    {
        List<ITimer> timers;
        lock (_sync)
        {
            _disposed = true;
            timers = _held.Values.Select(preview => preview.Timer).ToList();
            _held.Clear();
        }

        foreach (var timer in timers) timer.Dispose();
        if (_bridge.IsValueCreated)
        {
            _bridge.Value.Dispose();
        }
    }

    // Plain classes, so that nothing here can print a link or a password by accident.
    private sealed class HeldGroup(RemoteJobPlacement placement, string? archivePassword, IReadOnlyList<string> archivePasswordCandidates)
    {
        public RemoteJobPlacement Placement { get; } = placement;
        public string? ArchivePassword { get; } = archivePassword;
        public IReadOnlyList<string> ArchivePasswordCandidates { get; } = archivePasswordCandidates;
    }

    private sealed class HeldEntry(string? link, HeldGroup group, string? name)
    {
        public string? Link { get; set; } = link;
        public HeldGroup Group { get; } = group;
        public string? Name { get; } = name;
        public string? Refusal { get; init; }
        public Guid? AddedJob { get; set; }
    }

    private sealed class HeldPreview(Dictionary<int, HeldEntry> entries, DateTimeOffset expiresAt, long chars, ITimer timer)
    {
        public Dictionary<int, HeldEntry> Entries { get; } = entries;
        public DateTimeOffset ExpiresAt { get; } = expiresAt;
        public long Chars { get; } = chars;
        public ITimer Timer { get; } = timer;
    }
}

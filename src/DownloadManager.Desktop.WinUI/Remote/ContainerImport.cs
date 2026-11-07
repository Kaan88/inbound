using System.Globalization;
using DownloadManager.Contracts;
using DownloadManager.Core.Providers;

namespace DownloadManager.Desktop.WinUI;

/// <summary>One line of the container lists: a package or an entry while choosing, an entry's outcome afterwards.</summary>
/// <param name="Checked">Null for a package with only some of its entries ticked.</param>
internal sealed record ContainerRowView(
    string Name,
    string Word,
    string Detail,
    bool IsProblem,
    bool ShowBox,
    bool CanCheck,
    bool? Checked,
    bool IsHeader,
    int Depth,
    string AccessibleName);

/// <summary>What to say about a file once the Host has read it.</summary>
/// <param name="AccountProblem">Why no account can take anything right now.</param>
/// <param name="ShowList">There are packages or entries to show.</param>
/// <param name="CanSendToJd">The separate hand-over is worth offering: the Host can take this kind of file, and nothing in it can be added here.</param>
/// <param name="CanReadAgain">The file wasn't read because the connection failed, so asking again could help.</param>
internal sealed record ContainerReadView(
    string Status,
    bool IsProblem,
    string? AccountProblem,
    IReadOnlyList<string> Notes,
    bool ShowList,
    bool CanSendToJd,
    bool CanReadAgain);

/// <summary>What to say about a hand-over to JDownloader, and whether another try makes sense.</summary>
internal sealed record ContainerSendView(string Text, bool IsProblem, bool CanSendAgain);

/// <summary>What a file picker offers, and words that don't depend on anything the Host said.</summary>
internal static class ContainerFiles
{
    public const string NoPathWords = "This file has no path on a drive of this computer, so it can't be imported. Copy it to a folder here first.";

    public const string StoppedWords = "Stopped adding. Whatever was added before that is on the download list.";

    public static readonly IReadOnlyList<string> Types = [".rsdf", ".ccf", ".dlc"];
}

/// <summary>
/// What the Host's preview holds and which entries are ticked. A package's box speaks for what can be added
/// in it, and what can't be added stays on show with its reason and can never be ticked. Nothing here sees a
/// link or a password: the preview has neither, only that a package states one. Nothing here touches the UI.
/// </summary>
internal sealed class ContainerPicks
{
    public const string StatesArchivePassword = "This container states an archive password";

    private readonly List<Line> _lines = [];
    private readonly Dictionary<int, string> _names = [];

    public ContainerPicks(RemoteContainerPreviewResult preview, string provider)
    {
        PreviewId = preview.PreviewId;
        ExpiresAt = preview.ExpiresAt;
        var packages = preview.Packages.OrderBy(package => package.Index).ToList();
        var known = packages.Select(package => package.Index).ToHashSet();
        var byPackage = preview.Entries.ToLookup(entry => entry.PackageIndex is { } index && known.Contains(index) ? index : (int?)null);
        foreach (var package in packages)
        {
            var entries = byPackage[package.Index].ToList();
            if (entries.Count > 0) AddGroup(PackageName(package), PackageDetail(package), entries, preview.PreviewId is not null, provider, isPackage: true);
        }

        var loose = byPackage[null].ToList();
        if (loose.Count > 0) AddGroup(_lines.Count > 0 ? "Not in a package" : null, string.Empty, loose, preview.PreviewId is not null, provider, isPackage: false);
    }

    public Guid? PreviewId { get; }

    public DateTimeOffset? ExpiresAt { get; }

    public int AddableCount => _lines.Count(line => line is { EntryId: not null, Selectable: true });

    public int ChosenCount => _lines.Count(line => line is { EntryId: not null, Chosen: true });

    public int RefusedCount => _lines.Count(line => line is { EntryId: not null, Selectable: false });

    public bool CanAdd => PreviewId is not null && ChosenCount > 0;

    /// <summary>In the order the file gives them.</summary>
    public List<int> ChosenEntryIds => _lines.Where(line => line is { EntryId: not null, Chosen: true }).Select(line => line.EntryId!.Value).ToList();

    public bool IsExpired(DateTimeOffset now) => ExpiresAt is { } expires && expires <= now;

    public string NameOf(int entryId) => _names.TryGetValue(entryId, out var name) ? name : $"Link {entryId}";

    public IReadOnlyList<ContainerRowView> Views() => _lines.Select(line => line.View with { Checked = line.Checked }).ToList();

    /// <summary>A package takes everything in it that can be added. Anything else stays as it is.</summary>
    public void Toggle(int index, bool chosen)
    {
        if (index < 0 || index >= _lines.Count) return;
        var line = _lines[index];
        if (line.Members is { } members)
        {
            foreach (var member in members) member.Set(chosen);
        }
        else
        {
            line.Set(chosen);
        }
    }

    public void SelectAll()
    {
        foreach (var line in _lines) line.Set(true);
    }

    public void SelectNone()
    {
        foreach (var line in _lines) line.Set(false);
    }

    public string Summary()
    {
        if (AddableCount == 0) return "Nothing in this file can be added.";
        var chosen = $"{ChosenCount} of {AddableCount} selected.";
        return RefusedCount switch
        {
            0 => chosen,
            1 => $"{chosen} 1 can't be added.",
            _ => $"{chosen} {RefusedCount} can't be added.",
        };
    }

    private void AddGroup(string? header, string headerDetail, List<RemoteContainerEntryDto> entries, bool hasPreview, string provider, bool isPackage)
    {
        var members = new List<Line>();
        if (header is not null)
        {
            var count = entries.Count == 1 ? "1 link" : $"{entries.Count} links";
            var spoken = string.Join(", ", new[] { isPackage ? $"Package {header}" : header, count, headerDetail }.Where(part => part.Length > 0));
            _lines.Add(new Line(new ContainerRowView(header, count, headerDetail, false, true, true, false, true, 0, spoken), null, true) { Members = members });
        }

        foreach (var entry in entries)
        {
            var line = EntryLine(entry, header, hasPreview, provider);
            _names[entry.EntryId] = line.View.Name;
            members.Add(line);
            _lines.Add(line);
        }

        if (header is not null && members.All(member => !member.Selectable))
        {
            var row = _lines.Count - members.Count - 1;
            _lines[row].DisableBox();
        }
    }

    private static Line EntryLine(RemoteContainerEntryDto entry, string? group, bool hasPreview, string provider)
    {
        var known = entry.Kind is "folderLink" or "magnet" or "hosterLink" or "undetermined";

        // A kind this build has never heard of counts as unsupported, whatever else the entry says.
        var selectable = hasPreview && entry.CanAdd && known;
        var word = entry.Kind switch
        {
            "folderLink" => "Folder link",
            "magnet" => "Magnet",
            "hosterLink" => $"Hoster through {provider}",
            "undetermined" => "Can't tell",
            _ => "Unsupported",
        };
        var name = EntryName(entry);
        var details = new List<string>();
        if (!string.IsNullOrWhiteSpace(entry.DisplayHost) && !string.Equals(entry.DisplayHost, name, StringComparison.OrdinalIgnoreCase)) details.Add(UrlRedactor.HideAddresses(entry.DisplayHost));
        if (entry.Size is { } size && RemoteText.Bytes(size) is { Length: > 0 } bytes) details.Add($"The container says {bytes}.");
        var reason = UrlRedactor.HideAddresses(entry.Reason);
        if (!string.IsNullOrWhiteSpace(reason)) details.Add(reason);
        else if (!selectable && word == "Unsupported") details.Add("This app can't use this.");

        var cantTell = entry.Kind == "undetermined" && selectable;
        if (cantTell) details.Add("It starts unticked. Tick it to have it checked again when adding. It is never requested directly.");

        // Goes as Add Links does: what would most likely fail, or that nobody can vouch for, waits for the user to tick it.
        var warned = selectable && entry.Kind == "hosterLink" && !string.IsNullOrWhiteSpace(reason);
        var detail = RemoteText.ContainerDetail(details);
        var where = group is null ? string.Empty : $", in {group}";
        var spoken = detail.Length > 0 ? $"{name}{where}, {word}, {detail}" : $"{name}{where}, {word}";
        var view = new ContainerRowView(name, word, detail, !selectable || cantTell || warned, true, selectable, false, false, group is null ? 0 : 1, spoken);
        return new Line(view, entry.EntryId, selectable) { Chosen = selectable && !cantTell && !warned };
    }

    private static string EntryName(RemoteContainerEntryDto entry)
    {
        if (!string.IsNullOrWhiteSpace(entry.DisplayName)) return UrlRedactor.HideAddresses(entry.DisplayName);
        return string.IsNullOrWhiteSpace(entry.DisplayHost)
            ? $"Link {entry.EntryId}"
            : $"{UrlRedactor.HideAddresses(entry.DisplayHost)} (link {entry.EntryId})";
    }

    private static string PackageName(RemoteContainerPackageDto package)
        => string.IsNullOrWhiteSpace(package.Name) ? "Package without a name" : UrlRedactor.HideAddresses(package.Name);

    private static string PackageDetail(RemoteContainerPackageDto package)
    {
        var parts = new List<string>();
        var comment = UrlRedactor.HideAddresses(package.Comment);
        if (!string.IsNullOrWhiteSpace(comment)) parts.Add(comment);
        if (package.HasArchivePassword) parts.Add($"{StatesArchivePassword}.");
        return RemoteText.ContainerDetail(parts);
    }

    public override string ToString() => "[container picks]";

    private sealed class Line(ContainerRowView view, int? entryId, bool selectable)
    {
        public ContainerRowView View { get; private set; } = view;
        public int? EntryId { get; } = entryId;
        public bool Selectable { get; } = selectable;
        public bool Chosen { get; set; }
        public List<Line>? Members { get; init; }

        public bool? Checked
        {
            get
            {
                if (Members is not { } members) return Chosen;
                var chosen = 0;
                var selectable = 0;
                foreach (var member in members)
                {
                    if (!member.Selectable) continue;
                    selectable++;
                    if (member.Chosen) chosen++;
                }

                return chosen == 0 ? false : chosen == selectable ? true : null;
            }
        }

        public void Set(bool chosen)
        {
            if (EntryId is not null && Selectable) Chosen = chosen;
        }

        public void DisableBox() => View = View with { CanCheck = false };
    }
}

/// <summary>
/// What became of each entry. Adding was the decision, so nothing here asks anything more: the jobs are on
/// the download list, and only what wasn't added, or was there already, needs saying. An entry the Host added
/// is never reported as a failure: with <see cref="RemoteContainerAddResult.JobsLeftOut"/> the reply has its id
/// and no job. Nothing here touches the UI.
/// </summary>
internal sealed class ContainerOutcomes
{
    private readonly List<(RemoteContainerAddOutcomeDto Outcome, string Name)> _lines;

    public ContainerOutcomes(RemoteContainerAddResult result, Func<int, string> nameOf)
    {
        _lines = result.Outcomes.Select(outcome => (outcome, nameOf(outcome.EntryId))).ToList();
    }

    public int Count => _lines.Count;

    /// <summary>Saved by the Host, as new jobs or as ones already on the list.</summary>
    public int AddedCount => _lines.Count(line => line.Outcome.Ok);

    /// <summary>New on the list. Something that was there already is never called added.</summary>
    public int NewCount => _lines.Count(line => line.Outcome is { Ok: true, Existing: false });

    /// <summary>One line for each entry that wasn't added or was there already, all of them, in the file's order.</summary>
    public IReadOnlyList<string> Problems => _lines
        .Where(line => !line.Outcome.Ok || line.Outcome.Existing)
        .Select(line => line.Outcome.Ok
            ? RemoteText.AlreadyListed(line.Name, line.Outcome.Note)
            : RemoteText.Refused(line.Name, line.Outcome.Error))
        .ToList();

    public string Summary(string provider)
    {
        if (_lines.Count == 0) return string.Empty;
        if (NewCount > 0) return RemoteText.AddedWords(0, NewCount, provider);
        if (AddedCount == 0) return _lines.Count == 1 ? "This wasn't added." : "None of these were added.";
        if (AddedCount == _lines.Count) return _lines.Count == 1 ? "It was already on the list." : "These were already on the list.";
        return "Nothing new was added.";
    }

    public override string ToString() => "[container outcomes]";
}

internal static partial class RemoteText
{
    /// <summary>A host name or a package's comment has no full stop of its own, so one running into the next sentence reads as one sentence.</summary>
    public static string ContainerDetail(IReadOnlyList<string> parts)
    {
        var words = new System.Text.StringBuilder();
        for (var i = 0; i < parts.Count; i++)
        {
            if (i > 0) words.Append(parts[i - 1].TrimEnd().EndsWith('.') ? " " : " · ");
            words.Append(parts[i]);
        }

        return words.ToString();
    }

    public const string ContainerReadLost = "The background Host didn't answer, so this file wasn't read. Nothing was added. Try again.";
    public const string ContainerSendLost = "The background Host didn't answer, so it isn't known whether JDownloader got this file. Look in JDownloader before trying again.";

    /// <summary>Reading a file changes nothing, so the words about having possibly gone through don't apply.</summary>
    public static string ContainerReadFailure(string error) => error is HostLost or HostTooSlow ? ContainerReadLost : error;

    public static string ContainerSendFailure(string error) => error is HostLost or HostTooSlow ? ContainerSendLost : error;

    /// <param name="fileName">Only the file's name. Nothing looks at the file on this side.</param>
    public static ContainerReadView ContainerRead(RemoteContainerPreviewResult preview, string fileName, IFormatProvider? culture = null)
    {
        var notes = preview.Problems.Select(UrlRedactor.HideAddresses).Where(note => !string.IsNullOrWhiteSpace(note)).ToList();
        var name = UrlRedactor.HideAddresses(fileName);
        if (!preview.Ok)
        {
            var words = Failure(preview.Error);
            return new ContainerReadView(
                preview.NotSupported ? $"{name} isn't supported here. {words}" : words,
                true,
                null,
                notes,
                false,
                preview.CanSendToJd,
                preview.Error is ContainerReadLost or HostBusy or HostNotRunning);
        }

        var links = preview.Entries.Count == 1 ? "1 link" : $"{preview.Entries.Count} links";
        var format = FormatWords(preview);
        var status = $"Read {name}{(format.Length > 0 ? $" ({format})" : string.Empty)}: {links}.";
        if (preview.PreviewId is null) status += " Nothing in it can be added.";
        else if (preview.ExpiresAt is { } expires)
        {
            status += $" These stay ready until {expires.ToLocalTime().ToString("t", culture ?? CultureInfo.CurrentCulture)}. After that, the file has to be read again.";
        }

        var problem = UrlRedactor.HideAddresses(preview.AccountProblem);
        return new ContainerReadView(
            status,
            false,
            string.IsNullOrWhiteSpace(problem) ? null : problem,
            notes,
            preview.Entries.Count > 0,
            preview.CanSendToJd && preview.PreviewId is null,
            false);
    }

    public static ContainerSendView ContainerSend(RemoteContainerSendResult result)
    {
        var words = UrlRedactor.HideAddresses(result.Message);
        if (string.IsNullOrWhiteSpace(words)) words = result.Ok ? "Sent to JDownloader." : Fallback;
        if (result.Ok) return new ContainerSendView(words, false, false);

        // JDownloader's user said no, which is an answer rather than a fault. A file it can't take won't change by asking again.
        return result.Status switch
        {
            "rejected" => new ContainerSendView(words, false, true),
            "invalidInput" => new ContainerSendView(words, true, false),
            _ => new ContainerSendView(words, true, true),
        };
    }

    private static string FormatWords(RemoteContainerPreviewResult preview)
    {
        var format = preview.Format is "rsdf" or "ccf" or "dlc" ? preview.Format.ToUpperInvariant() : string.Empty;
        var version = UrlRedactor.HideAddresses(preview.FormatVersion);
        if (format.Length == 0 || string.IsNullOrWhiteSpace(version)) return format;
        return $"{format} {version}";
    }
}

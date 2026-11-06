using System.Text.RegularExpressions;
using DownloadManager.Contracts;
using DownloadManager.Core.Providers;

namespace DownloadManager.Desktop.WinUI;

/// <summary>
/// What the Extraction column shows for one archive set or a package of them. It sits beside the
/// download's own status and never replaces it, because a failed extraction says nothing against the
/// download. <see cref="Text"/> is short enough for a list cell; the rest says it in full.
/// </summary>
internal sealed record ArchiveRowView(
    string Text,
    string ToolTip,
    RemoteRowTone Tone,
    bool NeedsPassword,
    string AccessibleText);

/// <summary>The words for archive sets. The Host's own <c>stateText</c> is the full version of each.</summary>
internal static partial class ArchiveText
{
    public const string PackagePasswordHint =
        "For extracting this package's archives. It isn't for the download or the site it came from. You can set it now, before the files have downloaded.";

    private const int MostNamesInText = 5;

    /// <summary>One set, short. Waiting names what it waits for, and the tooltip names everything.</summary>
    public static ArchiveRowView Set(ArchiveSetDto set)
    {
        var (text, tone) = Label(set);
        return new ArchiveRowView(text, ToolTip(set), tone, set.NeedsPassword, $"Extraction: {Full(set)}");
    }

    /// <summary>A package: one set says what that set says, several say what most needs attention.</summary>
    public static ArchiveRowView? Package(IReadOnlyList<ArchiveSetDto> sets)
    {
        if (sets.Count == 0) return null;
        if (sets.Count == 1) return Set(sets[0]);

        var (text, tone) = PackageLabel(sets);
        var lines = sets.Take(MostNamesInText).Select(set => $"{set.Name}: {Full(set)}").ToList();
        if (sets.Count > MostNamesInText) lines.Add($"and {sets.Count - MostNamesInText} more");
        var tip = string.Join(Environment.NewLine, lines);
        return new ArchiveRowView(text, tip, tone, sets.Any(set => set.NeedsPassword), $"Extraction: {text}. {string.Join(". ", lines)}");
    }

    /// <summary>The Host's whole sentence for a set, with any address taken out.</summary>
    public static string Full(ArchiveSetDto set) => UrlRedactor.HideAddresses(set.StateText);

    private static (string Text, RemoteRowTone Tone) PackageLabel(IReadOnlyList<ArchiveSetDto> sets)
    {
        var asking = sets.Count(set => set.NeedsPassword);
        if (asking > 0) return ($"{asking} of {sets.Count} need a password", RemoteRowTone.Waiting);

        var running = sets.FirstOrDefault(set => set.State == "extracting");
        if (running is not null) return (Label(running).Text, RemoteRowTone.Active);

        var failed = sets.Count(set => set.State == "failed" && set.FailureKind != "cancelled");
        if (failed > 0) return (failed == 1 ? "1 extraction problem" : $"{failed} extraction problems", RemoteRowTone.Failed);

        var done = sets.Count(set => set.State == "completed");
        if (done == sets.Count) return ("Extraction completed", RemoteRowTone.Done);
        if (sets.Any(set => set.State == "waiting")) return ("Waiting for extraction", RemoteRowTone.Waiting);
        if (done > 0) return ($"Extracted {done} of {sets.Count}", RemoteRowTone.Neutral);
        if (sets.All(set => set.State == "downloaded")) return ("Downloaded", RemoteRowTone.Neutral);
        return ("Downloading", RemoteRowTone.Neutral);
    }

    private static (string Text, RemoteRowTone Tone) Label(ArchiveSetDto set)
    {
        switch (set.State)
        {
            case "downloading":
                if (set.DownloadingVolumes.Count > 0) return ("Downloading", RemoteRowTone.Neutral);
                return set.FailedVolumes.Count > 0
                    ? ($"{Parts(set.FailedVolumes)} didn't download", RemoteRowTone.Waiting)
                    : ("Not downloaded yet", RemoteRowTone.Neutral);

            case "downloaded":
                var gaps = set.MissingVolumes.Concat(set.FailedVolumes).ToList();
                return gaps.Count > 0
                    ? ($"Downloaded, {Parts(gaps)} missing", RemoteRowTone.Waiting)
                    : ("Downloaded", RemoteRowTone.Neutral);

            case "waiting":
                if (set.DownloadingVolumes.Count > 0) return ($"Waiting: {Parts(set.DownloadingVolumes)} downloading", RemoteRowTone.Waiting);
                if (set.FailedVolumes.Count > 0) return ($"Waiting: {Parts(set.FailedVolumes)} didn't download", RemoteRowTone.Waiting);
                if (set.MissingVolumes.Count > 0) return ($"Waiting: {Parts(set.MissingVolumes)} missing", RemoteRowTone.Waiting);
                return ("Waiting for extraction", RemoteRowTone.Waiting);

            case "extracting":
                return (set.Phase switch
                {
                    "verifying" => "Extracting: checking",
                    "finalizing" => "Extracting: moving files",
                    _ => set.Percent is { } percent ? $"Extracting {percent}%" : "Extracting",
                }, RemoteRowTone.Active);

            case "completed":
                return ("Extraction completed", RemoteRowTone.Done);

            case "failed":
                return set.FailureKind switch
                {
                    "passwordRequired" => ("Password required", RemoteRowTone.Waiting),
                    "wrongPassword" => ("Wrong password", RemoteRowTone.Failed),
                    "missingVolume" => ("Missing volume", RemoteRowTone.Failed),
                    "corrupted" => ("Corrupted archive", RemoteRowTone.Failed),
                    "unsupportedVersion" => ("Unsupported archive version", RemoteRowTone.Failed),
                    "toolUnavailable" => ("UnRAR unavailable", RemoteRowTone.Failed),
                    "diskSpace" => ("Not enough disk space", RemoteRowTone.Failed),
                    "permission" => ("Permission refused", RemoteRowTone.Failed),
                    "unsafePath" => ("Unsafe archive", RemoteRowTone.Failed),
                    "cancelled" => ("Extraction cancelled", RemoteRowTone.Neutral),
                    _ => ("Extraction failed", RemoteRowTone.Failed),
                };

            default:
                // A newer Host's state. Its own words are all anyone has for it.
                return (Full(set), RemoteRowTone.Neutral);
        }
    }

    private static string ToolTip(ArchiveSetDto set)
    {
        var lines = new List<string> { Full(set) };
        if (!string.IsNullOrWhiteSpace(set.Note)) lines.Add(UrlRedactor.HideAddresses(set.Note));
        if (set.NeedsPassword) lines.Add("Enter the archive's password to try again.");
        else if (set.HasPassword && set.State is not "completed") lines.Add("A password is saved for this archive.");
        if (set.State == "failed" && set.FailureKind is not ("cancelled" or "missingVolume")) lines.Add("The downloaded files are fine, and they are kept.");
        return string.Join(Environment.NewLine, lines);
    }

    /// <summary>One or two names cut down to what tells volumes apart, otherwise a count. A list cell has no room for more.</summary>
    private static string Parts(IReadOnlyList<string> names) => names.Count switch
    {
        0 => string.Empty,
        1 => ShortVolume(names[0]),
        2 => $"{ShortVolume(names[0])}, {ShortVolume(names[1])}",
        _ => $"{names.Count} parts",
    };

    internal static string ShortVolume(string name)
    {
        var part = PartNumber().Match(name);
        if (part.Success) return part.Groups[1].Value;
        var old = OldVolume().Match(name);
        return old.Success ? old.Groups[1].Value : name;
    }

    /// <summary>What the password dialog says about the archive it asks for.</summary>
    public static string PasswordHint(ArchivePasswordTarget target)
    {
        const string NotTheDownload = "This is for the archive itself, not for the download or the site it came from.";
        if (target.PackageId is not null)
        {
            return target.Needed
                ? $"An archive in \"{target.Name}\" needs its password before it can be extracted. {NotTheDownload} It is used for every archive in this package."
                : $"The password for the archives in \"{target.Name}\". {NotTheDownload} It is used for every archive in this package, including ones still downloading.";
        }

        if (target.WasWrong) return $"The password tried for \"{target.Name}\" was wrong. Enter the right one and extraction runs again. {NotTheDownload}";
        return target.Needed
            ? $"\"{target.Name}\" needs its password before it can be extracted. {NotTheDownload}"
            : $"The password for \"{target.Name}\". {NotTheDownload} It is saved for when this archive is extracted.";
    }

    public static string RemoveQuestion(IReadOnlyList<ArchiveSetDto> sets) => sets.Count == 1
        ? $"Forget the extraction record for \"{sets[0].Name}\", and any archive password saved for it? No file is deleted, neither the archive nor what was extracted."
        : $"Forget the extraction records for these {sets.Count} archives, and any archive passwords saved for them? No file is deleted, neither the archives nor what was extracted.";

    /// <summary>Says what came of asking for extraction.</summary>
    public static string ExtractSummary(ArchiveExtractResult result)
    {
        var started = result.Sets.Count(set => set.State is "waiting" or "extracting");
        var left = result.Ignored.Select(item => UrlRedactor.HideAddresses(item.Reason)).Distinct().Take(3).ToList();
        var words = started switch
        {
            0 => string.Empty,
            1 => $"Extraction asked for: {result.Sets.First(set => set.State is "waiting" or "extracting").Name}.",
            _ => $"Extraction asked for: {started} archives.",
        };
        return string.Join(" ", new[] { words }.Concat(left).Where(part => part.Length > 0));
    }

    [GeneratedRegex(@"\.(part\d+)\.rar$", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex PartNumber();

    [GeneratedRegex(@"(\.[r-z]\d{2})$", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex OldVolume();
}

/// <summary>A file in the selection, with the set it is known to belong to.</summary>
internal sealed record ArchiveSelectedFile(Guid JobId, string FileName, string FilePath, ArchiveSetDto? Set);

/// <summary>What a typed archive password would be for: exactly one of a set, a package or a file with no set yet.</summary>
internal sealed record ArchivePasswordTarget(
    Guid? SetId,
    Guid? PackageId,
    string? FilePath,
    string Name,
    bool Needed,
    bool WasWrong,
    bool HasPassword);

/// <summary>What the selection allows. A disabled Extract says why in <see cref="ExtractText"/>.</summary>
internal sealed record ArchivePlan(
    string? ExtractReason,
    IReadOnlyList<Guid> ExtractJobIds,
    IReadOnlyList<ArchiveSetDto> ExtractSets,
    ArchivePasswordTarget? Password,
    IReadOnlyList<ArchiveSetDto> Retry,
    IReadOnlyList<ArchiveSetDto> Cancel,
    IReadOnlyList<ArchiveSetDto> Remove,
    IReadOnlyList<string> OpenFolders)
{
    public bool CanExtract => ExtractReason is null;

    public string ExtractText => ExtractReason is null ? "Extract" : $"Extract ({ExtractReason})";
}

/// <summary>Which archive commands the Host would take for a selection. Nothing here touches the UI.</summary>
internal static partial class ArchiveRules
{
    /// <summary>The Host decides for real; this is only for what the menus offer, and it matches the Host's test of a name.</summary>
    public static bool LooksLikeRar(string? fileName)
        => !string.IsNullOrEmpty(fileName) &&
           (fileName.EndsWith(".rar", StringComparison.OrdinalIgnoreCase) || OldStyleVolume().IsMatch(fileName));

    /// <param name="files">Selected files. A selected package row contributes all of its files.</param>
    /// <param name="otherSets">Sets that are selected by other means: a record's own row, or a selected package's.</param>
    /// <param name="packageAlone">The one package row, when nothing else is selected: its password is the package's.</param>
    public static ArchivePlan Plan(
        IReadOnlyList<ArchiveSelectedFile> files,
        IReadOnlyList<ArchiveSetDto> otherSets,
        (Guid Id, string Name)? packageAlone,
        bool connected)
    {
        var sets = files.Select(file => file.Set).OfType<ArchiveSetDto>().Concat(otherSets)
            .GroupBy(set => set.Id).Select(group => group.First()).ToList();
        var rarFiles = files.Where(file => file.Set is not null || LooksLikeRar(file.FileName)).ToList();
        var listedSets = sets.Where(set => files.All(file => file.Set?.Id != set.Id)).ToList();

        var jobIds = rarFiles.Where(file => file.Set is null || CanAskFor(file.Set)).Select(file => file.JobId).ToList();
        var ownSets = listedSets.Where(CanAskFor).ToList();

        var reason = !connected ? "the background Host isn't connected"
            : files.Count == 0 && sets.Count == 0 ? "select a RAR download first"
            : rarFiles.Count == 0 && sets.Count == 0 ? "no RAR archive selected"
            : jobIds.Count > 0 || ownSets.Count > 0 ? null
            : WhyNotAgain(sets);

        return new ArchivePlan(
            reason,
            reason is null ? jobIds : [],
            reason is null ? ownSets : [],
            PasswordFor(rarFiles, sets, packageAlone),
            sets.Where(CanRetry).ToList(),
            sets.Where(set => set.State is "waiting" or "extracting").ToList(),
            sets.Where(set => set.State != "extracting").ToList(),
            sets.Where(set => set.State == "completed" && !string.IsNullOrWhiteSpace(set.Destination))
                .Select(set => set.Destination!).Distinct(StringComparer.OrdinalIgnoreCase).ToList());
    }

    /// <summary>What a double-click or Enter does on a record's own row.</summary>
    public static ArchiveRowAction Primary(ArchiveSetDto set) =>
        set.NeedsPassword ? ArchiveRowAction.EnterPassword
        : set.State == "completed" && !string.IsNullOrWhiteSpace(set.Destination) ? ArchiveRowAction.OpenFolder
        : CanRetry(set) ? ArchiveRowAction.Retry
        : ArchiveRowAction.None;

    public static ArchivePasswordTarget Target(ArchiveSetDto set)
        => new(set.Id, null, null, set.Name, set.NeedsPassword, set.FailureKind == "wrongPassword", set.HasPassword);

    /// <summary>A package's password also serves the archives it hasn't got yet.</summary>
    public static ArchivePasswordTarget PackageTarget(Guid packageId, string name, IReadOnlyList<ArchiveSetDto> sets)
    {
        var asking = sets.Where(set => set.NeedsPassword).ToList();
        return new ArchivePasswordTarget(
            null,
            packageId,
            null,
            name,
            asking.Count > 0,
            asking.Count > 0 && asking.All(set => set.FailureKind == "wrongPassword"),
            sets.Any(set => set.HasPassword));
    }

    private static ArchivePasswordTarget? PasswordFor(
        IReadOnlyList<ArchiveSelectedFile> rarFiles,
        IReadOnlyList<ArchiveSetDto> sets,
        (Guid Id, string Name)? packageAlone)
    {
        if (packageAlone is { } package) return PackageTarget(package.Id, package.Name, sets);

        if (sets is [var one] && rarFiles.All(file => file.Set?.Id == one.Id)) return Target(one);

        // A download with no record yet can still get a password in advance, by its file's path.
        if (sets.Count == 0 && rarFiles is [{ FilePath.Length: > 0 } file] && Path.IsPathFullyQualified(file.FilePath))
        {
            return new ArchivePasswordTarget(null, null, file.FilePath, file.FileName, false, false, false);
        }

        return null;
    }

    /// <summary>A set nobody asked for yet, or one that stopped and could go again without anything new from the user.</summary>
    private static bool CanAskFor(ArchiveSetDto set) => set.State switch
    {
        "downloading" or "downloaded" => true,
        "failed" => !set.NeedsPassword || set.HasPassword,
        _ => false,
    };

    private static bool CanRetry(ArchiveSetDto set) => set.State switch
    {
        "failed" => !set.NeedsPassword || set.HasPassword,

        // Its archives are gone, so another run could only fail.
        "completed" => !set.ArchivesDeleted,
        _ => false,
    };

    private static string WhyNotAgain(IReadOnlyList<ArchiveSetDto> sets)
    {
        if (sets.Any(set => set.State == "extracting")) return "already extracting";
        if (sets.Any(set => set.State == "waiting")) return "already waiting to extract";
        if (sets.Any(set => set.NeedsPassword)) return "enter the archive's password first";
        return "already extracted";
    }

    [GeneratedRegex(@"\.[r-z]\d{2}$", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex OldStyleVolume();
}

internal enum ArchiveRowAction
{
    None,
    EnterPassword,
    OpenFolder,
    Retry,
}

internal enum ArchiveNoticeKind
{
    Password,
    Completed,
}

/// <summary>One line above the list. It waits for the user and never takes the focus.</summary>
internal sealed record ArchiveNotice(ArchiveNoticeKind Kind, Guid SetId, string Text, string ActionText);

/// <summary>
/// Decides what the notice above the list says. A set asking for a password always counts, because it
/// waits for the user. A completion counts only if it happened since this window started, and only when
/// the user wants completion notifications. Dismissing lasts until the set has news.
/// </summary>
internal sealed class ArchiveNoticeBoard
{
    private readonly Dictionary<Guid, string> _seen = [];
    private readonly HashSet<string> _dismissed = [];
    private readonly List<Guid> _completed = [];

    /// <param name="watchingSince">When this window started. What finished before then was already there, and is not news.</param>
    public ArchiveNotice? Update(IReadOnlyList<ArchiveSetDto> sets, DateTimeOffset watchingSince, bool notifyOnCompletion)
    {
        foreach (var set in sets)
        {
            var key = Key(set);
            var isNews = !_seen.TryGetValue(set.Id, out var before) || before != key;
            _seen[set.Id] = key;
            if (isNews && notifyOnCompletion && set is { State: "completed", CompletedAt: { } finished } && finished >= watchingSince && !_completed.Contains(set.Id)) _completed.Add(set.Id);
        }

        foreach (var gone in _seen.Keys.Where(id => sets.All(set => set.Id != id)).ToList()) _seen.Remove(gone);
        _completed.RemoveAll(id => sets.All(set => set.Id != id || set.State != "completed"));

        var asking = sets.Where(set => set.NeedsPassword && !_dismissed.Contains(Key(set))).OrderBy(set => set.UpdatedAt).ToList();
        if (asking is [var first, ..])
        {
            var more = asking.Count - 1;
            var words = first.FailureKind == "wrongPassword"
                ? $"The password for \"{first.Name}\" was wrong, so it wasn't extracted. The download is fine."
                : $"\"{first.Name}\" needs a password to extract. The download is fine.";
            if (more > 0) words += more == 1 ? " 1 more archive needs one too." : $" {more} more archives need one too.";
            return new ArchiveNotice(ArchiveNoticeKind.Password, first.Id, words, "Enter Password…");
        }

        if (_completed.Count == 0) return null;
        var latest = sets.First(set => set.Id == _completed[^1]);
        var others = _completed.Count - 1;
        return new ArchiveNotice(
            ArchiveNoticeKind.Completed,
            latest.Id,
            others switch
            {
                0 => $"Extraction completed: \"{latest.Name}\".",
                1 => $"Extraction completed: \"{latest.Name}\" and 1 more.",
                _ => $"Extraction completed: \"{latest.Name}\" and {others} more.",
            },
            "Open Folder");
    }

    public void Dismiss(ArchiveNotice notice, IReadOnlyList<ArchiveSetDto> sets)
    {
        if (notice.Kind == ArchiveNoticeKind.Completed)
        {
            _completed.Clear();
            return;
        }

        var set = sets.FirstOrDefault(candidate => candidate.Id == notice.SetId);
        if (set is not null) _dismissed.Add(Key(set));
    }

    /// <summary>The same words the Host goes by for its tray notice: each outcome of each attempt once.</summary>
    private static string Key(ArchiveSetDto set) => $"{set.Id:N}:{set.Attempts}:{set.State}:{set.FailureKind}";
}

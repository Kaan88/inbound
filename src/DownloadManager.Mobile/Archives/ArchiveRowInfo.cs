using DownloadManager.Contracts;

namespace DownloadManager.Mobile.Archives;

public enum ArchiveTone { Neutral, Amber, Failed, Done }

public enum ArchiveIcon { Zip, Lock }

/// <summary>The button an archive set puts at the end of a download's row. None leaves the download's own button.</summary>
public enum ArchiveAction { None, Enter, Retry }

/// <summary>How an archive set looks on the row of its download (t3). The password is never part of it.</summary>
public sealed class ArchiveRowInfo
{
    public required string Lead { get; init; }
    public string? Rest { get; init; }
    public ArchiveTone Tone { get; init; }
    public ArchiveIcon Icon { get; init; } = ArchiveIcon.Zip;
    public ArchiveAction Action { get; init; }

    /// <summary>An amber bar under the row: determinate with <see cref="Progress"/>, otherwise running.</summary>
    public bool ShowBar { get; init; }

    /// <summary>0..1; null while the tool has given no percentage.</summary>
    public double? Progress { get; init; }

    /// <summary>The subtitle: the lead (coloured by the screen), then " · " and the rest.</summary>
    public string Subtitle => string.IsNullOrEmpty(Rest) ? Lead : Lead + " · " + Rest;

    /// <summary>The row of this set's first volume, or null to leave the download's own row as it is.</summary>
    public static ArchiveRowInfo? For(ArchiveSetDto set, long totalBytes = 0)
    {
        ArgumentNullException.ThrowIfNull(set);
        if (set.NeedsPassword)
        {
            var rest = new List<string>();
            if (set.Volumes.Count > 1) rest.Add($"{set.Volumes.Count} parts");
            if (totalBytes > 0) rest.Add(Fmt.Bytes(totalBytes));
            return new ArchiveRowInfo
            {
                Lead = "Needs a password", Rest = rest.Count == 0 ? null : string.Join(" · ", rest),
                Tone = ArchiveTone.Amber, Icon = ArchiveIcon.Lock, Action = ArchiveAction.Enter,
            };
        }

        var words = (set.StateText ?? "").Trim();
        switch (set.State)
        {
            case "extracting":
                var parts = new List<string>();
                if (set.FilesTotal > 0) parts.Add($"{Math.Min(set.FilesFinalized, set.FilesTotal)} of {set.FilesTotal} files");
                if (set.Percent is { } percent) parts.Add($"{Math.Clamp(percent, 0, 100)}%");
                return new ArchiveRowInfo
                {
                    Lead = "Extracting", Rest = parts.Count == 0 ? null : string.Join(" · ", parts), Tone = ArchiveTone.Amber, ShowBar = true,
                    Progress = set.Percent is { } p ? Math.Clamp(p, 0, 100) / 100.0 : null,
                };

            case "completed":
                return new ArchiveRowInfo { Lead = words.Length > 0 ? words : "Extracted", Tone = ArchiveTone.Done };

            case "failed":
                var why = string.IsNullOrWhiteSpace(set.FailureMessage) ? words : set.FailureMessage.Trim();
                var waiting = set.FailureKind == "missingVolume" && set.MissingVolumes.Count > 0;
                return new ArchiveRowInfo
                {
                    Lead = why.Length > 0 ? why : "Extraction failed",
                    Tone = waiting ? ArchiveTone.Amber : set.FailureKind == "cancelled" ? ArchiveTone.Neutral : ArchiveTone.Failed,
                    Action = ArchiveAction.Retry,
                };

            case "waiting":
                return words.Length == 0 ? null : new ArchiveRowInfo { Lead = words, Tone = ArchiveTone.Amber };

            // Parts still downloading: the download's own progress is the better thing to show.
            case "downloading":
                return null;

            default:
                return words.Length == 0 ? null : new ArchiveRowInfo { Lead = words };
        }
    }

    /// <summary>"project-files.part1.rar (3 parts)." and, when a saved password already failed, the sentence that says so.</summary>
    public static string PasswordSheetLine(ArchiveSetDto set)
    {
        var name = FileNameOf(set.FirstVolume);
        if (name.Length == 0) name = set.Name;
        var line = set.Volumes.Count > 1 ? $"{name} ({set.Volumes.Count} parts)." : name + ".";
        if (!set.NeedsPassword) return line;
        // t6: "The 3 saved passwords didn't open it." The Host counts the ones from the saved list it tried.
        return set.SavedPasswordsTried switch
        {
            > 1 => $"{line} The {set.SavedPasswordsTried} saved passwords didn't open it.",
            1 => line + " The saved password didn't open it.",
            _ => set.HasPassword ? line + " The password given didn't open it." : line,
        };
    }

    /// <summary>The last part of a path written with either slash (a paired PC sends backslashes).</summary>
    public static string FileNameOf(string? path)
    {
        if (string.IsNullOrEmpty(path)) return "";
        var cut = path.LastIndexOfAny(['/', '\\']);
        return cut < 0 ? path : path[(cut + 1)..];
    }
}

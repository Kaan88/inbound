using DownloadManager.Contracts;

namespace DownloadManager.Desktop.WinUI;

/// <summary>What to do about What's new at a window start: whether it opens, from which version on, and what to record.</summary>
/// <param name="Open">Show the notes of the versions after <paramref name="After"/> up to the installed one.</param>
/// <param name="After">The version last seen; the notes start after it. Null with <paramref name="Open"/> false.</param>
/// <param name="AfterKnown">False when <paramref name="After"/> is a guess (an install from before the marker was kept).</param>
/// <param name="Record">The version to keep as last seen; null keeps what is there.</param>
internal sealed record WhatsNewDecision(bool Open, string? After, bool AfterKnown, string? Record);

/// <summary>The words of the update question's notes box (UPD.N1); null <see cref="Header"/> when there are no notes.</summary>
internal sealed record UpdateNotesWords(string? Header, IReadOnlyList<string> Lines, string? Also);

/// <summary>
/// UPD.N1: when the desktop window opens What's new on its own. Once, at the first window start after the installed
/// version changed; never on a fresh install, never for a development build. 0.7.2 is the first version that keeps the
/// version last seen (ui-state.json, WhatsNewSeen), so an install with state from before it and no marker came
/// from <see cref="LastWithoutMarker"/> or older, and sees the notes after that one.
/// </summary>
internal static class WhatsNewRules
{
    public const string LastWithoutMarker = "0.7.1";

    /// <param name="installed">The installed version (install.json); null for a development or portable build.</param>
    /// <param name="lastSeen">The version whose notes were last shown or skipped; null when none was kept.</param>
    /// <param name="hadStateBefore">ui-state.json was there when this window process started, or the Host has downloads.</param>
    public static WhatsNewDecision Decide(string? installed, string? lastSeen, bool hadStateBefore)
    {
        if (Changelog.Normal(installed) is null) return new WhatsNewDecision(false, null, false, null);
        var current = Changelog.Display(installed!);
        if (Changelog.Normal(lastSeen) is null)
        {
            // A fresh install: nothing to tell; from now on the version is kept.
            if (!hadStateBefore) return new WhatsNewDecision(false, null, false, current);
            return Changelog.Compare(current, LastWithoutMarker) > 0
                ? new WhatsNewDecision(true, LastWithoutMarker, false, current)
                : new WhatsNewDecision(false, null, false, current);
        }

        // An older version started again after this one keeps the newer mark while a build that knows the mark runs.
        // A build from before 0.7.2 (a rollback to 0.7.1) rewrites ui-state.json without it, so going forward again
        // counts as an upgrade from 0.7.1 and shows those notes once more. Rare, and accepted.
        return Changelog.Compare(current, lastSeen) > 0
            ? new WhatsNewDecision(true, Changelog.Display(lastSeen!), true, current)
            : new WhatsNewDecision(false, null, false, null);
    }

    /// <summary>
    /// "Updated from 0.7.1 · 12 Nov 2026"; "Updated from 0.7.1 or earlier · …" when where it came from is a guess (no mark
    /// kept); "Released 12 Nov 2026" with no version before it (Help > What's New…).
    /// </summary>
    public static string Subtitle(string? after, bool afterKnown, string? date)
    {
        var day = Changelog.DisplayDate(date);
        if (after is not null)
        {
            var from = afterKnown ? $"Updated from {after}" : $"Updated from {after} or earlier";
            return day is null ? from : $"{from} · {day}";
        }

        return day is null ? string.Empty : $"Released {day}";
    }

    /// <summary>
    /// The notes box of the Update available question: this app's lines of <paramref name="version"/> and a count of the
    /// others. No header when the manifest had no notes, or none for this app, so the question looks as it did before.
    /// </summary>
    /// <param name="windowsApp">False in the Linux window built from the same code: its notes are not the Windows app's
    /// lines, and the Linux window has no notes of its own yet (out of scope), so its question stays as it was.</param>
    public static UpdateNotesWords UpdateNotes(IReadOnlyList<ChangelogItem>? notes, string? version, ChangelogFeatures features, bool windowsApp = true)
    {
        if (!windowsApp || notes is not { Count: > 0 }) return new UpdateNotesWords(null, [], null);
        var (here, elsewhere) = ChangelogFilter.Lines(notes, ChangelogSurface.Windows, features);
        if (here.Count == 0) return new UpdateNotesWords(null, [], null);
        var also = elsewhere.Count == 0 ? null : $"Also {elsewhere.Count} change{(elsewhere.Count == 1 ? string.Empty : "s")} in {ChangelogFilter.Places(elsewhere)}.";
        var name = Changelog.Normal(version) is null ? "the update" : Changelog.Display(version!);
        return new UpdateNotesWords($"What {name} changes in this app", here, also);
    }
}

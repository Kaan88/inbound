namespace DownloadManager.Mobile;

/// <summary>
/// The downloads a notification's "Resume" restarts: what "Pause all" paused, or what a start left paused after the crash guard tripped.
/// Never every paused download (owner, S24 2026-11-17: Resume restarted about 200 old paused entries too). Kept as a small text file in the
/// state folder (one id per line) so it survives the process dying between the pause and the tap. Pure, so the format is tested.
/// </summary>
public static class ResumeList
{
    /// <summary>At most this many ids are kept (a Pause all of a very long list keeps the newest ones).</summary>
    public const int MaxIds = 2000;

    public static IReadOnlyList<Guid> Parse(string? text)
    {
        var list = new List<Guid>();
        if (string.IsNullOrEmpty(text)) return list;
        foreach (var line in text.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
            if (Guid.TryParse(line, out var id) && !list.Contains(id)) list.Add(id);
        return list;
    }

    public static string Serialize(IEnumerable<Guid> ids)
    {
        var all = ids.Distinct().ToList();
        if (all.Count > MaxIds) all.RemoveRange(0, all.Count - MaxIds);
        return string.Concat(all.Select(id => id.ToString("D") + "\n"));
    }

    /// <summary>A second Pause all (or a guard start after a Pause all) adds to the list: one Resume restarts all of them.</summary>
    public static IReadOnlyList<Guid> Merge(IReadOnlyList<Guid> existing, IEnumerable<Guid> added) => existing.Concat(added).Distinct().ToList();

    /// <summary>
    /// The downloads a Pause all is about to pause (what the Host's <c>CanPause</c> says: queued, connecting, downloading, waiting
    /// to retry). Written before the pause, so a process killed between the pause and the write loses nothing (Fable finding
    /// 15); the ids it did not pause are taken out afterwards, and Resume restarts only those still paused anyway.
    /// </summary>
    public static IReadOnlyList<Guid> PauseCandidates(IEnumerable<DownloadManager.Contracts.JobDto> jobs) =>
        jobs.Where(j => j.QueueRetryAt is not null || j.State is "queued" or "connecting" or "downloading" or "retryWaiting")
            .Select(j => j.Id).ToList();

    /// <summary>The remembered downloads a Resume restarts: the ones still paused, in order.</summary>
    public static IReadOnlyList<Guid> ToResume(IReadOnlyList<Guid> ids, Func<Guid, string?> stateOf) =>
        ids.Where(id => string.Equals(stateOf(id), "paused", StringComparison.OrdinalIgnoreCase)).ToList();

    /// <summary>What stays after <paramref name="done"/> are taken out (ids added meanwhile stay).</summary>
    public static IReadOnlyList<Guid> Without(IReadOnlyList<Guid> existing, IEnumerable<Guid> done)
    {
        var gone = new HashSet<Guid>(done);
        return existing.Where(id => !gone.Contains(id)).ToList();
    }

    /// <summary>How many of the remembered downloads are still paused (the count the paused notification shows).</summary>
    public static int StillPaused(IReadOnlyList<Guid> ids, Func<Guid, string?> stateOf) =>
        ids.Count(id => string.Equals(stateOf(id), "paused", StringComparison.OrdinalIgnoreCase));
}

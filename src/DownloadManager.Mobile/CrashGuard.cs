using System.Globalization;

namespace DownloadManager.Mobile;

/// <summary>
/// What the guard remembers between processes: how many runs in a row ended in a crash or ANR while a session (job or service) was
/// running, and the id of the run whose session has not been accounted for yet (set when a session begins, cleared when it ends).
/// </summary>
public sealed record CrashGuardState(int Count, byte[]? SessionRunId)
{
    public static CrashGuardState Empty { get; } = new(0, null);

    public bool Tripped => Count >= CrashGuard.Threshold;
}

/// <summary>What a process start finds out: the state to save and the words for the log.</summary>
public sealed record CrashGuardDecision(CrashGuardState Next, string Why);

/// <summary>
/// Crash-loop guard (design section 8, "Crash loop"). The rules, all pure: a run whose session was running and that ended in a crash,
/// a native crash or an ANR adds one; a session that ended cleanly, a run that ended another way (or left no record, as a reboot does)
/// resets the count; a run that never had a session says nothing, so the count stays. At <see cref="Threshold"/> the guard is tripped:
/// the Host starts with the "stopped by the user" option (interrupted downloads come back Paused), no session is scheduled and the
/// notification says so, until the user resumes (<see cref="Reset"/>).
/// </summary>
public static class CrashGuard
{
    public const int Threshold = 3;

    // ApplicationExitInfo.REASON_CRASH, REASON_CRASH_NATIVE, REASON_ANR.
    public const int ReasonCrash = 4;
    public const int ReasonCrashNative = 5;
    public const int ReasonAnr = 6;

    public static bool IsCrash(int reason) => reason is ReasonCrash or ReasonCrashNative or ReasonAnr;

    /// <summary>Process start: accounts for the run whose session was left unaccounted for, using the platform's exit records.</summary>
    public static CrashGuardDecision AtProcessStart(CrashGuardState saved, IEnumerable<ExitRecord> records)
    {
        if (saved.SessionRunId is not { Length: > 0 } run)
            return new CrashGuardDecision(saved, $"no session was running at the last exit; count stays {saved.Count}");
        ExitRecord? newest = null;
        foreach (var r in records)
        {
            if (r.StateSummary is null || !r.StateSummary.AsSpan().SequenceEqual(run)) continue;
            if (newest is null || r.TimestampMs > newest.TimestampMs) newest = r;
        }

        if (newest is null)
            return new CrashGuardDecision(new CrashGuardState(0, null), "a session was running but its run left no exit record (reboot); count reset");
        if (!IsCrash(newest.Reason))
            return new CrashGuardDecision(new CrashGuardState(0, null), $"the run with a session ended with reason {newest.Reason} (not a crash); count reset");
        var count = saved.Count + 1;
        return new CrashGuardDecision(new CrashGuardState(count, null),
            $"the run with a session ended with reason {newest.Reason} (crash/ANR); count {count} of {Threshold}");
    }

    /// <summary>A session begins in the run <paramref name="runId"/>.</summary>
    public static CrashGuardState SessionBegan(CrashGuardState s, byte[] runId) => s with { SessionRunId = runId };

    /// <summary>The session of run <paramref name="runId"/> ended in an orderly way: downloads ran, so the count restarts. Another run's marker is left alone.</summary>
    public static CrashGuardState SessionEnded(CrashGuardState s, byte[] runId) =>
        s.SessionRunId is { } own && own.AsSpan().SequenceEqual(runId) ? new CrashGuardState(0, null) : s;

    /// <summary>
    /// A command only the user sends that starts downloads (add, resume, retry, download again, resume all): it resets a tripped
    /// guard. A download the queue starts on its own does not, even with the app visible (Fable finding 9).
    /// </summary>
    public static bool IsUserStart(string messageType) => messageType is DownloadManager.Contracts.MessageTypes.JobAdd
        or DownloadManager.Contracts.MessageTypes.JobResume or DownloadManager.Contracts.MessageTypes.JobRetry
        or DownloadManager.Contracts.MessageTypes.JobRedownload or DownloadManager.Contracts.MessageTypes.JobsResumeAll;

    /// <summary>Only explicit client starts lift the guard; queue policy and stop operations never do.</summary>
    public static bool IsUserStart(DownloadManager.Contracts.IpcMessage request)
    {
        if (IsUserStart(request.Type)) return true;
        if (request.Payload is not { ValueKind: System.Text.Json.JsonValueKind.Object } payload ||
            !payload.TryGetProperty("action", out var action) || action.ValueKind != System.Text.Json.JsonValueKind.String) return false;
        var expected = request.Type switch
        {
            DownloadManager.Contracts.MessageTypes.QueueAction => "start",
            DownloadManager.Contracts.MessageTypes.GroupAction => "resume",
            _ => null,
        };
        return expected is not null && string.Equals(action.GetString()?.Trim(), expected, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>The user resumed: the guard starts over.</summary>
    public static CrashGuardState Reset(CrashGuardState s) => s with { Count = 0 };

    public static byte[] Serialize(CrashGuardState s) =>
        System.Text.Encoding.ASCII.GetBytes($"count={s.Count.ToString(CultureInfo.InvariantCulture)}\nsession={(s.SessionRunId is null ? "" : Convert.ToHexString(s.SessionRunId))}\n");

    /// <summary>A missing or damaged file is an empty state.</summary>
    public static CrashGuardState Parse(byte[]? bytes)
    {
        if (bytes is null || bytes.Length == 0) return CrashGuardState.Empty;
        try
        {
            var count = 0;
            byte[]? session = null;
            foreach (var line in System.Text.Encoding.ASCII.GetString(bytes).Split('\n', StringSplitOptions.RemoveEmptyEntries))
            {
                var eq = line.IndexOf('=');
                if (eq < 0) continue;
                var value = line[(eq + 1)..].Trim();
                switch (line[..eq])
                {
                    case "count": count = Math.Clamp(int.Parse(value, CultureInfo.InvariantCulture), 0, 1000); break;
                    case "session": session = value.Length == 0 ? null : Convert.FromHexString(value); break;
                }
            }

            return new CrashGuardState(count, session);
        }
        catch (Exception ex) when (ex is FormatException or OverflowException) { return CrashGuardState.Empty; }
    }
}

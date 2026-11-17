using DownloadManager.Contracts;

namespace DownloadManager.Mobile;

/// <summary>What the ongoing notification says, as plain values (the Android side only puts them into a Notification).</summary>
/// <param name="Title">First line, e.g. "2 downloads · 16.1 MB/s".</param>
/// <param name="Body">Second line (the first file and its time left); null when there is none.</param>
/// <param name="Max">Bar range (<see cref="NotificationText.BarMax"/>).</param>
/// <param name="Progress">Bar position within <paramref name="Max"/>; meaningless when <paramref name="Indeterminate"/>.</param>
/// <param name="Indeterminate">The bar runs without a position (a size is not known).</param>
/// <param name="Warning">Extra line while downloads that can't resume run on mobile data ("... stopping it starts over"); null otherwise.
/// It also makes the notification offer Stop.</param>
public sealed record OngoingText(string Title, string? Body, int Max, int Progress, bool Indeterminate, string? Warning = null);

/// <summary>The words of the phone's notifications (mockup A7). Plain .NET so they are tested without a phone.</summary>
public static class NotificationText
{
    public const int BarMax = 1000;

    /// <summary>The ongoing notification for a summary: "2 downloads · 16.1 MB/s", the first file and its time left, and a bar.</summary>
    public static OngoingText Ongoing(ActivitySummary s)
    {
        var text = OngoingCore(s);
        return s.NoResumeOnMeteredCount > 0 ? text with { Warning = NoResumeWarning(s.NoResumeOnMeteredCount) } : text;
    }

    private static OngoingText OngoingCore(ActivitySummary s)
    {
        if (s.ActiveCount <= 0)
            return new OngoingText("Starting downloads", null, BarMax, 0, true);

        var title = s.ActiveCount == 1 ? "1 download" : s.ActiveCount + " downloads";
        if (s.BytesPerSecond >= 1) title += " · " + Fmt.Rate(s.BytesPerSecond);

        string? body = null;
        if (s.First is { } f)
        {
            body = f.Name;
            if (f.TimeLeft is { } left && left > TimeSpan.Zero) body += " · " + Fmt.Eta(left.TotalSeconds);
        }

        // One file: its own bytes. Several: the combined bytes, only when every size is known.
        long received, total;
        if (s.ActiveCount == 1 && s.First is { } one) { received = one.ReceivedBytes; total = one.TotalBytes ?? 0; }
        else { received = s.ActiveReceivedBytes; total = s.ActiveTotalBytes ?? 0; }
        if (total <= 0) return new OngoingText(title, body, BarMax, 0, true);
        var pos = (int)Math.Clamp(received * (double)BarMax / total, 0, BarMax);
        return new OngoingText(title, body, BarMax, pos, false);
    }

    /// <summary>Shown after "Pause all" when only paused downloads remain.</summary>
    public static string Paused(int count) => count == 1 ? "1 download paused" : count + " downloads paused";

    /// <summary>The finished notification's second line: "Finished · 640 MB · Tap to open".</summary>
    public static string FinishedBody(FinishedJob job) => "Finished · " + Fmt.Bytes(job.SizeBytes) + " · Tap to open";

    public const string Resuming = "Resuming downloads";

    /// <summary>Title of the notification shown while downloads wait for a network that isn't metered (mockup A7).</summary>
    public const string WaitingTitle = "Waiting for Wi-Fi";

    /// <summary>"3 downloads will resume".</summary>
    public static string WaitingBody(int count) => count == 1 ? "1 download will resume" : count + " downloads will resume";

    /// <summary>The warning line of the ongoing notification: stopping a download that can't resume loses what it has.</summary>
    public static string NoResumeWarning(int count) => count == 1
        ? "1 download can't wait for Wi-Fi: stopping it starts over"
        : count + " downloads can't wait for Wi-Fi: stopping them starts over";
}

/// <summary>Plain decisions of the Wi-Fi-only rule on the phone.</summary>
public static class MeteredPolicy
{
    /// <summary>
    /// NetworkCapabilities says metered unless NOT_METERED is present; a network that is only temporarily not metered (a carrier's free
    /// period) counts as not metered, since it is free to use now.
    /// </summary>
    public static bool IsMetered(bool hasNotMetered, bool hasTemporarilyNotMetered) => !(hasNotMetered || hasTemporarilyNotMetered);

    /// <summary>
    /// The rule has just started to hold and downloads that can't resume are running: stop them at once, or let them go on (with a
    /// warning and a Stop action)? Owner: continue if the phone is unlocked (the user is there and, if the switch was deliberate, wants
    /// it; Android can't say whether it was), otherwise stop at once. "Unlocked" means the device is not locked <b>and</b> the screen is
    /// interactive: a screen that is off with the lock timeout not yet run is nobody looking at the warning.
    /// </summary>
    public static bool StopNonResumableAtOnce(bool deviceLocked, bool screenInteractive) => deviceLocked || !screenInteractive;
}

/// <summary>What the user-initiated data transfer job is told about the bytes it is expected to move.</summary>
public static class SessionBytes
{
    /// <summary>Used when a size is not known: a transfer of unknown size is guessed rather than declared empty.</summary>
    public const long UnknownEstimate = 500_000_000;

    private const long MinEstimate = 1_000_000;
    private const long NotableAbsolute = 50_000_000;

    /// <summary>The estimate: what is left across the active downloads, or a fixed guess when any size is unknown.</summary>
    public static long Estimate(ActivitySummary s) =>
        s.ActiveTotalBytes is { } total ? Math.Max(MinEstimate, total - s.ActiveReceivedBytes) : UnknownEstimate;

    /// <summary>Whether a new estimate differs enough from the last one sent to be worth another call (a quarter, and at least 50 MB).</summary>
    public static bool NotableChange(long last, long next) =>
        Math.Abs(next - last) >= Math.Max(NotableAbsolute, last / 4);
}

/// <summary>
/// Turns "bytes received across the active downloads" (which drops when a download leaves the active set) into a growing total of bytes
/// transferred this session, and says when it is worth reporting (the system is told at most every few seconds and megabytes).
/// </summary>
public sealed class TransferMeter(long minStepBytes = 2_000_000, TimeSpan? minInterval = null)
{
    private readonly TimeSpan _interval = minInterval ?? TimeSpan.FromSeconds(5);
    private long _last;
    private bool _based;
    private long _total;
    private long _reported;
    private DateTimeOffset _reportedAt = DateTimeOffset.MinValue;

    /// <summary>Bytes counted so far.</summary>
    public long Total => _total;

    /// <summary>Starts a new session: nothing counted, the next observation is the basis.</summary>
    public void Reset()
    {
        _last = 0; _total = 0; _reported = 0; _reportedAt = DateTimeOffset.MinValue; _based = false;
    }

    /// <summary>One observation. Returns the total to report, or null when it is too soon or too little.</summary>
    public long? Observe(long receivedAcrossActive, DateTimeOffset now)
    {
        // The first observation is the basis: bytes received before the session began were not moved by it.
        if (!_based) { _based = true; _last = receivedAcrossActive; return null; }
        // A drop means a download left the set (its bytes were counted while it grew): rebase, add nothing.
        if (receivedAcrossActive > _last) _total += receivedAcrossActive - _last;
        _last = receivedAcrossActive;
        if (_total - _reported < minStepBytes || now - _reportedAt < _interval) return null;
        _reported = _total;
        _reportedAt = now;
        return _total;
    }
}

/// <summary>One historical process exit, as the platform reports it: the state summary the process saved, the reason code and the time.</summary>
public sealed record ExitRecord(byte[]? StateSummary, int Reason, long TimestampMs);

/// <summary>The decision and the words for the log line.</summary>
public sealed record UserStopDecision(bool StoppedByUser, string Why);

/// <summary>
/// Whether the previous run of the app was stopped by the user (Task Manager "Stop", Settings "Force stop"): the platform gives no callback for
/// it, but the next start can read the exit records. Every run saves a fresh id as its process state summary, and the next run looks for the
/// newest record that carries the id of the run before it, so a record is used once (the id changes with every start) and an unrelated exit
/// is never taken for it. A reboot leaves no record for that run.
/// </summary>
public static class UserStopDetector
{
    /// <summary>
    /// The runs whose exit is still unexplained: every run since the Host last consumed the decision (see <see cref="RunIdList"/>). A process that
    /// never started the Host (a FileProvider query, a receiver) leaves its id in the list, so a user stop of it is not lost; any of them
    /// stopped by the user leaves the interrupted downloads paused.
    /// </summary>
    public static UserStopDecision DecideAny(IReadOnlyList<byte[]> runIds, IEnumerable<ExitRecord> records)
    {
        if (runIds.Count == 0) return new UserStopDecision(false, "no previous run id saved (first start)");
        var all = records as IReadOnlyCollection<ExitRecord> ?? records.ToList();
        UserStopDecision? last = null;
        foreach (var id in runIds)
        {
            var d = Decide(id, all);
            if (d.StoppedByUser) return d;
            last = d;
        }

        return last!;
    }

    /// <summary>ApplicationExitInfo.REASON_USER_REQUESTED.</summary>
    public const int ReasonUserRequested = 10;

    public static UserStopDecision Decide(byte[]? previousRunId, IEnumerable<ExitRecord> records)
    {
        if (previousRunId is null || previousRunId.Length == 0) return new UserStopDecision(false, "no previous run id saved (first start)");
        ExitRecord? newest = null;
        foreach (var r in records)
        {
            if (r.StateSummary is null || !r.StateSummary.AsSpan().SequenceEqual(previousRunId)) continue;
            if (newest is null || r.TimestampMs > newest.TimestampMs) newest = r;
        }

        if (newest is null) return new UserStopDecision(false, "no exit record for the previous run (reboot, or it is still listed as running)");
        return newest.Reason == ReasonUserRequested
            ? new UserStopDecision(true, "the previous run ended with reason 10 (stopped by the user)")
            : new UserStopDecision(false, "the previous run ended with reason " + newest.Reason + " (not the user)");
    }
}

/// <summary>
/// The saved run ids as a file: 16 bytes each, oldest first. Every process start appends its own id; the Host's start (after
/// InitializeAsync, when the decision has been used) rewrites the file with the current run only. Pure, so the sequence is tested.
/// </summary>
public static class RunIdList
{
    public const int IdLength = 16;
    public const int MaxIds = 16;

    public static IReadOnlyList<byte[]> Parse(byte[]? file)
    {
        var list = new List<byte[]>();
        if (file is null) return list;
        // A file of the old format (one id) is the same format; a torn tail is dropped.
        for (var i = 0; i + IdLength <= file.Length; i += IdLength) list.Add(file.AsSpan(i, IdLength).ToArray());
        return list;
    }

    public static byte[] Serialize(IEnumerable<byte[]> ids)
    {
        var all = ids.Where(i => i.Length == IdLength).ToList();
        if (all.Count > MaxIds) all.RemoveRange(0, all.Count - MaxIds);
        var bytes = new byte[all.Count * IdLength];
        for (var i = 0; i < all.Count; i++) all[i].CopyTo(bytes, i * IdLength);
        return bytes;
    }

    /// <summary>What a process start saves: the ids still unexplained, then its own.</summary>
    public static byte[] OnProcessStart(IReadOnlyList<byte[]> previous, byte[] own) => Serialize(previous.Append(own));

    /// <summary>What the Host start saves once the decision was used: this run only.</summary>
    public static byte[] OnHostConsumed(byte[] own) => Serialize([own]);
}

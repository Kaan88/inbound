using Android.App;
using Android.Content;
using Android.Util;
using DownloadManager.Mobile;

namespace Inbound.App.Background;

internal enum ExecutionLeaseKind { KeepAlive, QueueWork }

/// <summary>
/// The crash-loop guard's file and in-process state (rules in <see cref="CrashGuard"/>). <see cref="Begin"/> runs at every process start,
/// right after <see cref="RunTracker.Begin"/>: it accounts for the run whose session was left open, from the platform's exit records.
/// A session marks itself with <see cref="SessionBegan"/> / <see cref="SessionEnded"/>. The file is written atomically, only when it changes.
/// </summary>
internal static class CrashGuardStore
{
    private const string FileName = "crash-guard.txt";
    private static readonly object Gate = new();
    private static CrashGuardState _state = CrashGuardState.Empty;
    private static readonly HashSet<ExecutionLeaseKind> Leases = new();
    private static string? _path;

    /// <summary>The count (for the test hook's state line).</summary>
    public static int Count { get { lock (Gate) return _state.Count; } }

    /// <summary>Three runs in a row ended in a crash or ANR during a download: start the Host paused, schedule no session, say so.</summary>
    public static bool Tripped { get { lock (Gate) return _state.Tripped; } }

    /// <summary>For the Host start: when tripped, interrupted downloads come back Paused (the "stopped by the user" option). Logged.</summary>
    public static bool HostStartsPaused(Context c)
    {
        var tripped = Tripped;
        if (!tripped) return false;
        Log.Info(InboundApplication.Tag, "crash guard: tripped, the Host starts with interrupted downloads paused");
        // Say why, also when the app itself was opened (seen on the API 33 emulator: paused with no word of why).
        try
        {
            Notifications.Notify(c, Notifications.GuardId, Notifications.PausedAfterErrors(c));
            // A Resume tap that reset the guard while this was posted: the notice must not stay up (review of step 2).
            if (!Tripped) Notifications.Cancel(c, Notifications.GuardId);
        }
        catch (System.Exception ex) { Log.Warn(InboundApplication.Tag, "crash guard notice: " + ex.Message); }
        return true;
    }

    public static void Begin(Context c)
    {
        try
        {
            // The binding's enum and the plain numbers the logic tests use must agree.
            int[] bound = [(int)ApplicationExitInfoReason.Crash, (int)ApplicationExitInfoReason.CrashNative, (int)ApplicationExitInfoReason.Anr];
            if (bound[0] != CrashGuard.ReasonCrash || bound[1] != CrashGuard.ReasonCrashNative || bound[2] != CrashGuard.ReasonAnr)
                Log.Warn(InboundApplication.Tag, "crash guard: exit reason numbers differ from the binding");
            var path = Path.Combine(InboundApplication.StateRoot(c), FileName);
            var saved = CrashGuard.Parse(File.Exists(path) ? File.ReadAllBytes(path) : null);
            var records = new List<ExitRecord>();
            if (saved.SessionRunId is not null)
            {
                var am = (ActivityManager)c.GetSystemService(Context.ActivityService)!;
                foreach (var e in am.GetHistoricalProcessExitReasons(c.PackageName, 0, 0) ?? [])
                    records.Add(new ExitRecord(e.GetProcessStateSummary(), (int)e.Reason, e.Timestamp));
            }

            var d = CrashGuard.AtProcessStart(saved, records);
            lock (Gate) { _path = path; _state = d.Next; }
            if (d.Next.Count != saved.Count || !Same(d.Next.SessionRunId, saved.SessionRunId)) Save(d.Next);
            Log.Info(InboundApplication.Tag, "crash guard: " + d.Why + (d.Next.Tripped ? " TRIPPED" : ""));
        }
        catch (System.Exception ex) { Log.Warn(InboundApplication.Tag, "crash guard: " + ex.Message); }
    }

    /// <summary>A session (job or service) starts in this run.</summary>
    public static void SessionBegan(ExecutionLeaseKind lease)
    {
        var run = RunTracker.OwnId;
        if (run is null) return;
        Update(s => { Leases.Add(lease); return CrashGuard.SessionBegan(s, run); }, "session began");
    }

    /// <summary>The session ended in an orderly way (finished, held, or stopped by the system with the process alive).</summary>
    public static void SessionEnded(ExecutionLeaseKind lease)
    {
        var run = RunTracker.OwnId;
        if (run is null) return;
        Update(s => { Leases.Remove(lease); return Leases.Count == 0 ? CrashGuard.SessionEnded(s, run) : s; }, "session ended");
    }

    /// <summary>The user resumed: the count starts over.</summary>
    public static void Reset(string why)
    {
        var run = RunTracker.OwnId;
        Update(s =>
        {
            var reset = CrashGuard.Reset(s);
            return Leases.Count > 0 && run is not null ? CrashGuard.SessionBegan(reset, run) : reset;
        }, "reset (" + why + ")");
        Notifications.Cancel(Android.App.Application.Context, Notifications.GuardId);
    }

    private static void Update(Func<CrashGuardState, CrashGuardState> change, string what)
    {
        try
        {
            lock (Gate)
            {
                if (_path is null) return;
                var next = change(_state);
                if (next.Count == _state.Count && Same(next.SessionRunId, _state.SessionRunId)) return;
                _state = next;
                Save(next);
                Log.Info(InboundApplication.Tag, $"crash guard: {what}; count={next.Count}");
            }
        }
        catch (System.Exception ex) { Log.Warn(InboundApplication.Tag, "crash guard: " + ex.Message); }
    }

    private static bool Same(byte[]? a, byte[]? b) => a is null || b is null ? a is null && b is null : a.AsSpan().SequenceEqual(b);

    private static void Save(CrashGuardState s)
    {
        var path = _path;
        if (path is null) return;
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var tmp = path + ".tmp";
        using (var fs = new FileStream(tmp, FileMode.Create, FileAccess.Write, FileShare.None))
        {
            fs.Write(CrashGuard.Serialize(s));
            fs.Flush(true);
        }

        File.Move(tmp, path, overwrite: true);
    }
}

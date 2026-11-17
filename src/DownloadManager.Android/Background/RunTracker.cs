using Android.App;
using Android.Content;
using Android.Util;
using DownloadManager.Mobile;

namespace Inbound.App.Background;

/// <summary>
/// Tells the Host at start whether the previous run was stopped by the user. Each process start makes a run id, saves it in a small file in the
/// state folder and hands it to the system as the process state summary; the next start reads the exit records, finds the newest one that
/// carries the saved id, and asks <see cref="UserStopDetector"/>. A reboot leaves no record for that run.
/// </summary>
internal static class RunTracker
{
    private const string FileName = "run-id.bin";
    private static readonly object Gate = new();
    private static byte[]? _previous;
    private static bool? _decision;

    /// <summary>Process start (Application.OnCreate): remember the previous run's id, save this run's.</summary>
    public static void Begin(Context c)
    {
        try
        {
            var dir = InboundApplication.StateRoot(c);
            Directory.CreateDirectory(dir);
            var path = Path.Combine(dir, FileName);
            lock (Gate) _previous = File.Exists(path) ? File.ReadAllBytes(path) : null;
            var id = Guid.NewGuid().ToByteArray();
            var tmp = path + ".tmp";
            using (var fs = new FileStream(tmp, FileMode.Create, FileAccess.Write, FileShare.None))
            {
                fs.Write(id);
                fs.Flush(true);
            }

            File.Move(tmp, path, overwrite: true);
            ((ActivityManager)c.GetSystemService(Context.ActivityService)!).SetProcessStateSummary(id);
        }
        catch (System.Exception ex) { Log.Warn(InboundApplication.Tag, "run id: " + ex.Message); }
    }

    /// <summary>Decided once per process (a Host start that is tried again gets the same answer); logged.</summary>
    public static bool LastRunStoppedByUser(Context c)
    {
        lock (Gate)
        {
            if (_decision is { } known) return known;
            UserStopDecision d;
            try
            {
                var am = (ActivityManager)c.GetSystemService(Context.ActivityService)!;
                var records = new List<ExitRecord>();
                foreach (var e in am.GetHistoricalProcessExitReasons(c.PackageName, 0, 0) ?? [])
                    records.Add(new ExitRecord(e.GetProcessStateSummary(), (int)e.Reason, e.Timestamp));
                d = UserStopDetector.Decide(_previous, records);
            }
            catch (System.Exception ex) { d = new UserStopDecision(false, "exit records unreadable: " + ex.Message); }

            Log.Info(InboundApplication.Tag, "last run stopped by user=" + d.StoppedByUser + " (" + d.Why + ")");
            _decision = d.StoppedByUser;
            return d.StoppedByUser;
        }
    }
}

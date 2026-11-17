using Android.App;
using Android.Content;
using Android.Util;
using DownloadManager.Mobile;

namespace Inbound.App.Background;

/// <summary>
/// Tells the Host at start whether the previous run was stopped by the user. Each process start makes a run id, saves it in a small file in the
/// state folder and hands it to the system as the process state summary; the next start reads the exit records, finds the newest one that
/// carries a saved id, and asks <see cref="UserStopDetector"/>. A reboot leaves no record for that run. The file holds every run id since the Host
/// last used the decision (<see cref="RunIdList"/>): a process that never starts the Host (a FileProvider query, a receiver) adds its id, and
/// only <see cref="HostConsumed"/> (after the Host's InitializeAsync) drops the older ones, so such a process cannot lose a user stop.
/// </summary>
internal static class RunTracker
{
    private const string FileName = "run-id.bin";
    private static readonly object Gate = new();
    private static IReadOnlyList<byte[]> _previous = [];
    private static byte[]? _own;
    private static string? _path;
    private static bool? _decision;

    /// <summary>Process start (Application.OnCreate): remember the previous run's id, save this run's.</summary>
    public static void Begin(Context c)
    {
        try
        {
            var dir = InboundApplication.StateRoot(c);
            Directory.CreateDirectory(dir);
            var path = Path.Combine(dir, FileName);
            var previous = RunIdList.Parse(File.Exists(path) ? File.ReadAllBytes(path) : null);
            var id = Guid.NewGuid().ToByteArray();
            lock (Gate) { _previous = previous; _own = id; _path = path; }
            Save(path, RunIdList.OnProcessStart(previous, id));
            ((ActivityManager)c.GetSystemService(Context.ActivityService)!).SetProcessStateSummary(id);
        }
        catch (System.Exception ex) { Log.Warn(InboundApplication.Tag, "run id: " + ex.Message); }
    }

    private static void Save(string path, byte[] bytes)
    {
        var tmp = path + ".tmp";
        using (var fs = new FileStream(tmp, FileMode.Create, FileAccess.Write, FileShare.None))
        {
            fs.Write(bytes);
            fs.Flush(true);
        }

        File.Move(tmp, path, overwrite: true);
    }

    /// <summary>The Host has started (InitializeAsync done) and used the decision: the older runs are explained, only this one stays.</summary>
    public static void HostConsumed()
    {
        try
        {
            lock (Gate)
            {
                if (_own is null || _path is null) return;
                Save(_path, RunIdList.OnHostConsumed(_own));
            }
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
                d = UserStopDetector.DecideAny(_previous, records);
            }
            catch (System.Exception ex) { d = new UserStopDecision(false, "exit records unreadable: " + ex.Message); }

            Log.Info(InboundApplication.Tag, "last run stopped by user=" + d.StoppedByUser + " (" + d.Why + ")");
            _decision = d.StoppedByUser;
            return d.StoppedByUser;
        }
    }
}

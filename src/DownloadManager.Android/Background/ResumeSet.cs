using Android.App;
using Android.Content;
using Android.OS;
using Android.Util;
using DownloadManager.Mobile;
using Inbound.App.Host;

namespace Inbound.App.Background;

/// <summary>
/// The downloads a notification's "Resume" restarts (<see cref="ResumeList"/>), kept in the state folder with an atomic write: Pause all adds
/// what it paused, a start the crash guard paused adds what that start paused, and Resume takes the list, resumes those and clears it.
/// </summary>
internal static class ResumeSet
{
    private const string FileName = "resume-set.txt";
    private static readonly object Gate = new();

    private static string PathOf(Context c) => System.IO.Path.Combine(InboundApplication.StateRoot(c), FileName);

    private static IReadOnlyList<Guid> ReadLocked(Context c)
    {
        var path = PathOf(c);
        return ResumeList.Parse(File.Exists(path) ? File.ReadAllText(path) : null);
    }

    private static void WriteLocked(Context c, IEnumerable<Guid> ids)
    {
        var path = PathOf(c);
        Directory.CreateDirectory(System.IO.Path.GetDirectoryName(path)!);
        var tmp = path + ".tmp";
        using (var fs = new FileStream(tmp, FileMode.Create, FileAccess.Write, FileShare.None))
        {
            fs.Write(System.Text.Encoding.ASCII.GetBytes(ResumeList.Serialize(ids)));
            fs.Flush(true);
        }

        File.Move(tmp, path, overwrite: true);
    }

    public static void Add(Context c, IEnumerable<Guid> ids, string why)
    {
        var list = ids.ToList();
        if (list.Count == 0) return;
        try
        {
            lock (Gate) WriteLocked(c, ResumeList.Merge(ReadLocked(c), list));
            Log.Info(InboundApplication.Tag, $"resume set: +{list.Count} ({why})");
        }
        catch (System.Exception ex) { Log.Warn(InboundApplication.Tag, "resume set: " + ex.Message); }
    }

    /// <summary>How many remembered downloads are still paused: the paused notification's count.</summary>
    public static int StillPaused(Context c, AndroidHost host)
    {
        try
        {
            IReadOnlyList<Guid> ids;
            lock (Gate) ids = ReadLocked(c);
            return ResumeList.StillPaused(ids, id => host.Service.GetJob(id)?.State);
        }
        catch (System.Exception ex) { Log.Warn(InboundApplication.Tag, "resume set: " + ex.Message); return 0; }
    }

    /// <summary>The list, emptied (Resume takes it once).</summary>
    public static IReadOnlyList<Guid> Take(Context c)
    {
        try
        {
            lock (Gate)
            {
                var ids = ReadLocked(c);
                if (ids.Count > 0) WriteLocked(c, []);
                return ids;
            }
        }
        catch (System.Exception ex) { Log.Warn(InboundApplication.Tag, "resume set: " + ex.Message); return []; }
    }

    /// <summary>
    /// "Resume" of the paused or crash-guard notification: the guard starts over, the notices go, and the remembered downloads resume
    /// (only those). Runs the resume on a background thread.
    /// </summary>
    public static void ResumeFromNotification(Context app, string why)
    {
        // First, before the Host may start: a start after this is not paused by the guard, and no notice is posted again.
        CrashGuardStore.Reset(why);
        InboundBackground.ResumedFromNotification();
        _ = Task.Run(async () =>
        {
            try
            {
                var host = await InboundApplication.GetHostAsync(app).ConfigureAwait(false);
                var ids = Take(app);
                var resumed = 0;
                foreach (var id in ids)
                    if ((await host.Service.ResumeAsync(id).ConfigureAwait(false)).Ok) resumed++;
                Log.Info(InboundApplication.Tag, $"resume ({why}): {resumed} of {ids.Count} remembered download(s)");
                InboundBackground.Evaluate();
            }
            catch (System.Exception ex) { Log.Error(InboundApplication.Tag, "resume: " + ex); }
        });
    }
}

/// <summary>
/// The "Resume" action of the paused and crash-guard notifications. Not exported, so no other app can start it (review of step 2: any app
/// could start MainActivity with the resume extra). It resumes, opens the downloads screen (which keeps the process alive while they run)
/// and finishes at once.
/// </summary>
[Activity(Name = "app.inbound.android.ResumeActivity", Exported = false, NoHistory = true, ExcludeFromRecents = true,
    Theme = "@android:style/Theme.NoDisplay", TaskAffinity = "")]
internal sealed class ResumeActivity : Activity
{
    protected override void OnCreate(Bundle? savedInstanceState)
    {
        base.OnCreate(savedInstanceState);
        // A recreated instance or one brought back from history carries the old intent again: it must not resume twice.
        var redelivered = savedInstanceState is not null || (Intent?.Flags & ActivityFlags.LaunchedFromHistory) != 0;
        if (!redelivered) ResumeSet.ResumeFromNotification(ApplicationContext!, "Resume in the notification");
        var open = new Intent(this, typeof(MainActivity));
        open.AddFlags(ActivityFlags.NewTask | ActivityFlags.SingleTop);
        StartActivity(open);
        Finish();
    }
}

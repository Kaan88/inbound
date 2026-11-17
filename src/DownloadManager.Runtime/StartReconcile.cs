using DownloadManager.Core;

namespace DownloadManager.Runtime;

/// <summary>
/// What start-up does with a job a previous run left running, or paused because the Host exited, as one pure rule.
/// <see cref="DownloadManagerService"/> applies it while reconciling, and a platform can ask it beforehand (the Android boot
/// path counts what would resume without starting the Host). It looks only at the job's state and flags, never at files.
/// </summary>
public static class StartReconcile
{
    /// <summary>The outcome of the rule for one job. <see cref="Changed"/> is false when start-up leaves the record alone.</summary>
    public readonly record struct Outcome(DownloadState State, bool PausedByUser, StartMode StartMode, bool PausedByExit, bool Changed);

    private static bool Interrupted(DownloadState state)
        => state is DownloadState.Connecting or DownloadState.Downloading or DownloadState.RetryWaiting or DownloadState.Verifying;

    /// <param name="job">The job as stored. Not changed.</param>
    /// <param name="settings">The loaded settings (AutoResumeUnfinishedOnStartup).</param>
    /// <param name="userStopped">The last run was stopped by the user: what was running stays paused (as a user pause).</param>
    public static Outcome Decide(DownloadJob job, AppSettings settings, bool userStopped)
    {
        var state = job.State;
        var byUser = job.PausedByUser;
        var mode = job.StartMode;
        var changed = false;
        var auto = job.AutoResumeAfterRestart && settings.AutoResumeUnfinishedOnStartup;

        if (Interrupted(state))
        {
            if (userStopped)
            {
                state = DownloadState.Paused;
                byUser = true;
            }
            else
            {
                state = auto ? DownloadState.Queued : DownloadState.Paused;
            }

            changed = true;
        }
        else if (userStopped && state == DownloadState.Queued && mode != StartMode.Later && !byUser)
        {
            // Owner, 2026-11-17: stopping the app stops everything, so what waited in the queue stays paused too ("Pause
            // them too"). A job scheduled for later keeps its schedule.
            state = DownloadState.Paused;
            byUser = true;
            changed = true;
        }

        // Audit 2 M11: paused only because the Host exited while it ran (tray Exit, an update, logoff), so it goes on as one
        // a kill interrupted does. A pause by the user, a queue stop or a quota is not this mark.
        if (job.PausedByExit)
        {
            if (state == DownloadState.Paused && !byUser)
            {
                if (userStopped)
                {
                    byUser = true;
                }
                else if (auto)
                {
                    state = DownloadState.Queued;
                    mode = StartMode.StartNow;
                }
            }

            changed = true;
        }

        return new Outcome(state, byUser, mode, PausedByExit: false, changed);
    }

    /// <summary>Applies <see cref="Decide"/> to the job; true when anything was changed (the job needs saving).</summary>
    public static bool Apply(DownloadJob job, AppSettings settings, bool userStopped)
    {
        var outcome = Decide(job, settings, userStopped);
        if (!outcome.Changed) return false;
        job.State = outcome.State;
        job.PausedByUser = outcome.PausedByUser;
        job.StartMode = outcome.StartMode;
        job.PausedByExit = false;
        return true;
    }

    /// <summary>
    /// Whether this job, as stored, is Queued after start-up and not set for later, so it goes on by itself once the Host runs
    /// (a job already queued counts). Whether its queue runs, quotas and the network are not known here.
    /// </summary>
    public static bool WouldResumeAtStart(DownloadJob job, AppSettings settings, bool userStopped)
    {
        var outcome = Decide(job, settings, userStopped);
        return outcome.State == DownloadState.Queued && outcome.StartMode != StartMode.Later;
    }

    /// <summary>How many of the jobs <see cref="WouldResumeAtStart"/> says go on by themselves once the Host runs.</summary>
    public static int CountWouldResume(IEnumerable<DownloadJob> jobs, AppSettings settings, bool userStopped)
        => jobs.Count(job => WouldResumeAtStart(job, settings, userStopped));
}

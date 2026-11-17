using DownloadManager.Core;
using DownloadManager.Persistence;
using DownloadManager.Runtime;

namespace DownloadManager.Integration.Tests;

/// <summary>The Android boot notice: counting what would resume from the stored files, without starting a Host or writing anything.</summary>
public sealed class StartReconcileCountTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "idm-count-" + Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); } catch (IOException) { }
    }

    private static DownloadJob Job(DownloadState state, bool byUser = false, StartMode mode = StartMode.StartNow, bool autoResume = true) => new()
    {
        Id = Guid.NewGuid(),
        Url = "http://127.0.0.1:9/" + Guid.NewGuid().ToString("N") + ".bin",
        FileName = "a.bin",
        State = state,
        PausedByUser = byUser,
        StartMode = mode,
        AutoResumeAfterRestart = autoResume,
    };

    private static List<DownloadJob> Mixed() =>
    [
        Job(DownloadState.Downloading),                      // interrupted: resumes
        Job(DownloadState.Connecting),                       // interrupted: resumes
        Job(DownloadState.Downloading, autoResume: false),   // interrupted, no auto resume: paused
        Job(DownloadState.Paused, byUser: true),             // the user's pause
        Job(DownloadState.Completed),
        Job(DownloadState.Queued),                           // already queued: counts
        Job(DownloadState.Queued, mode: StartMode.Later),    // scheduled for later: does not
    ];

    [Fact]
    public void Count_follows_the_rule_for_a_normal_start_and_after_a_user_stop()
    {
        var settings = AppSettings.NewInstall();
        Assert.Equal(3, StartReconcile.CountWouldResume(Mixed(), settings, userStopped: false));
        // A user stop: what was running stays paused, and what waited in the queue too.
        Assert.Equal(0, StartReconcile.CountWouldResume(Mixed(), settings, userStopped: true));
        settings.AutoResumeUnfinishedOnStartup = false;
        Assert.Equal(1, StartReconcile.CountWouldResume(Mixed(), settings, userStopped: false));
        Assert.Equal(0, StartReconcile.CountWouldResume([], settings, false));
    }

    [Fact]
    public async Task Reading_the_state_folder_gives_the_same_count_and_changes_nothing()
    {
        var paths = new StoragePaths(_root);
        paths.EnsureCreated();
        var store = new JsonJobStore(paths);
        foreach (var job in Mixed()) await store.SaveAsync(job);
        await new JsonSettingsStore(paths).SaveAsync(AppSettings.NewInstall());
        File.WriteAllText(Path.Combine(paths.JobsDirectory, "damaged.json"), "{ not json");

        string Snapshot() => string.Join("\n", Directory.EnumerateFileSystemEntries(_root, "*", SearchOption.AllDirectories)
            .OrderBy(p => p, StringComparer.Ordinal)
            .Select(p => p + "|" + (File.Exists(p) ? new FileInfo(p).Length + "|" + File.GetLastWriteTimeUtc(p).Ticks : "dir")));
        var before = Snapshot();

        var jobs = ReadOnlyState.ReadJobs(_root);
        var settings = ReadOnlyState.ReadSettings(_root);

        Assert.Equal(7, jobs.Count);
        Assert.Equal(3, StartReconcile.CountWouldResume(jobs, settings, userStopped: false));
        Assert.Equal(before, Snapshot());
    }

    [Fact]
    public void An_empty_or_missing_state_folder_reads_as_nothing_and_creates_nothing()
    {
        Assert.Empty(ReadOnlyState.ReadJobs(_root));
        Assert.True(ReadOnlyState.ReadSettings(_root).AutoResumeUnfinishedOnStartup);
        Assert.False(Directory.Exists(_root));
    }
}

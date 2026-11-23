using DownloadManager.Contracts;
using DownloadManager.Core;
using DownloadManager.Persistence;
using DownloadManager.Runtime;

namespace DownloadManager.Server.Tests;

/// <summary>UPD.D1: the dismiss rules of the server update notice, and its per-server persistence.</summary>
public sealed class UpdateNoticeTests
{
    private static readonly DateTimeOffset Noon = new(2026, 11, 23, 12, 0, 0, TimeSpan.Zero);

    [Fact]
    public void NothingToShowWithoutAVersionOrANewerOne()
    {
        Assert.False(UpdateNoticeState.ShouldShow(null, "0.7.4", UpdateNoticeState.None, Noon));
        Assert.False(UpdateNoticeState.ShouldShow("0.7.2", null, UpdateNoticeState.None, Noon));
        Assert.False(UpdateNoticeState.ShouldShow("0.7.4", "0.7.4", UpdateNoticeState.None, Noon));
        Assert.False(UpdateNoticeState.ShouldShow("0.7.4", "0.7.2", UpdateNoticeState.None, Noon));
    }

    [Fact]
    public void ANEWerVersionShowsUntilDismissed()
    {
        Assert.True(UpdateNoticeState.ShouldShow("0.7.2", "0.7.4", UpdateNoticeState.None, Noon));
        // "0.7" and "0.7.0" read the same.
        Assert.True(UpdateNoticeState.ShouldShow("0.7", "0.7.0.1", UpdateNoticeState.None, Noon));
    }

    [Fact]
    public void NotNowHidesForADayAndComesBack()
    {
        var hidden = UpdateNoticeState.Dismiss(UpdateDismissActions.NotNow, "0.7.4", UpdateNoticeState.None, Noon)!;
        Assert.Equal(Noon + UpdateNoticeState.HiddenFor, hidden.HiddenUntil);
        Assert.Null(hidden.SkippedVersion);

        Assert.False(UpdateNoticeState.ShouldShow("0.7.2", "0.7.4", hidden, hidden.HiddenUntil!.Value - TimeSpan.FromSeconds(1)));
        Assert.True(UpdateNoticeState.ShouldShow("0.7.2", "0.7.4", hidden, hidden.HiddenUntil.Value));
    }

    [Fact]
    public void SkipThisVersionHidesUntilANewerOne()
    {
        var skipped = UpdateNoticeState.Dismiss(UpdateDismissActions.Skip, "0.7.4", UpdateNoticeState.None, Noon)!;
        Assert.Equal("0.7.4", skipped.SkippedVersion);

        Assert.False(UpdateNoticeState.ShouldShow("0.7.2", "0.7.4", skipped, Noon));
        // A newer version clears the skip.
        Assert.True(UpdateNoticeState.ShouldShow("0.7.2", "0.7.5", skipped, Noon));
    }

    [Fact]
    public void SkipWithoutALatestIsRefusedAndAnUnknownActionDismissesNothing()
    {
        Assert.Null(UpdateNoticeState.Dismiss(UpdateDismissActions.Skip, null, UpdateNoticeState.None, Noon));
        Assert.Null(UpdateNoticeState.Dismiss("something-else", "0.7.4", UpdateNoticeState.None, Noon));
    }

    [Fact]
    public void TheStateSurvivesARestart()
    {
        var dir = Path.Combine(AppContext.BaseDirectory, "test-work", "upd-notice-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        try
        {
            var stored = new UpdateNoticeState(Noon + TimeSpan.FromDays(1), "0.7.4");
            new UpdateNoticeStore(dir).Save(stored);
            Assert.Equal(stored, new UpdateNoticeStore(dir).Load());
        }
        finally
        {
            try { Directory.Delete(dir, recursive: true); } catch (IOException) { }
        }
    }

    [Fact]
    public void TheServiceKeepsAndPersistsANotNowAndASkip()
    {
        var dir = Path.Combine(AppContext.BaseDirectory, "test-work", "upd-service-" + Guid.NewGuid().ToString("N"));
        var paths = new StoragePaths(Path.Combine(dir, "state"));
        var clock = new ManualTime(Noon);
        var engine = new HttpDownloadEngine(new EngineOptions { AllowLocalNetwork = true, MaxAttempts = 1 }, checkpoints: new JsonCheckpointStore(paths));
        var service = new DownloadManagerService(paths, new JsonJobStore(paths), new JsonCheckpointStore(paths), new JsonSettingsStore(paths), engine,
            enableSchedulerTimer: false, timeProvider: clock);
        UpdateNoticeState? raised = null;
        service.UpdateNoticeChanged += (_, state) => raised = state;
        try
        {
            service.NoteServerUpdate("0.7.4", null);
            Assert.Equal("0.7.4", service.LatestVersion);

            Assert.True(service.DismissUpdateNotice(UpdateDismissActions.NotNow).Ok);
            var afterNow = service.UpdateNotice();
            Assert.Equal(Noon + UpdateNoticeState.HiddenFor, afterNow.HiddenUntil);
            Assert.Equal(afterNow, raised);

            Assert.True(service.DismissUpdateNotice(UpdateDismissActions.Skip).Ok);
            Assert.Equal("0.7.4", service.UpdateNotice().SkippedVersion);

            // A fresh store on the same folder reads what was saved.
            var onDisk = new UpdateNoticeStore(paths.Root).Load();
            Assert.Equal(service.UpdateNotice().SkippedVersion, onDisk.SkippedVersion);
            Assert.Equal(service.UpdateNotice().HiddenUntil, onDisk.HiddenUntil);
        }
        finally
        {
            engine.Dispose();
            try { Directory.Delete(dir, recursive: true); } catch (IOException) { }
        }
    }

    private sealed class ManualTime(DateTimeOffset now) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => now;
    }
}

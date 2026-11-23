using DownloadManager.Contracts;
using DownloadManager.Core;
using DownloadManager.Persistence;
using DownloadManager.Runtime;

namespace DownloadManager.Server.Tests;

/// <summary>UPD.D1 addendum: the single "update waiting" flag a paired extension reads, and the desktop Host's Docker latest.</summary>
public sealed class UpdateWaitingTests
{
    private static readonly DateTimeOffset Noon = new(2026, 11, 23, 12, 0, 0, TimeSpan.Zero);

    [Fact]
    public void AServerWithANewerVersionWaitsUntilItIsHiddenOrSkipped()
    {
        using var scope = new Scope();
        Assert.False(scope.Service.UpdateWaiting(), "nothing is known yet");

        scope.Service.NoteServerUpdate("0.0.1", null);
        Assert.False(scope.Service.UpdateWaiting(), "an older latest is no update");

        scope.Service.NoteServerUpdate("999.0.0", null);
        Assert.True(scope.Service.UpdateWaiting());
        Assert.True(scope.Service.GetStatus().UpdateWaiting);

        // Not now hides it for the day, and it is back after the day.
        Assert.True(scope.Service.DismissUpdateNotice(UpdateDismissActions.NotNow).Ok);
        Assert.False(scope.Service.UpdateWaiting());
        scope.Clock.Now += UpdateNoticeState.HiddenFor + TimeSpan.FromSeconds(1);
        Assert.True(scope.Service.UpdateWaiting());

        // Skipping this version hides it until a newer one comes.
        Assert.True(scope.Service.DismissUpdateNotice(UpdateDismissActions.Skip).Ok);
        Assert.False(scope.Service.UpdateWaiting());
        scope.Service.NoteServerUpdate("999.0.1", null);
        Assert.True(scope.Service.UpdateWaiting());
    }

    [Fact]
    public async Task TheDockerLatestIsKeptOnAFailedCheckAndClearedWithUpdatesOff()
    {
        using var scope = new Scope();
        var heard = new List<string?>();
        scope.Service.DockerLatestVersionChanged += (_, version) => heard.Add(version);

        // Updates off (no manifest address): nothing is checked and nothing is known.
        var settings = scope.Service.GetSettings();
        settings.UpdateManifestUrl = string.Empty;
        Assert.True((await scope.Service.UpdateSettingsAsync(settings)).Ok);
        await scope.Service.CheckDockerAsync();
        scope.Service.NoteDockerLatest("0.7.4");
        Assert.Null(scope.Service.DockerLatestVersion);
        Assert.Null(scope.Service.GetStatus().DockerLatestVersion);
        Assert.Empty(heard);

        settings = scope.Service.GetSettings();
        settings.UpdateManifestUrl = "http://127.0.0.1:1/manifest.json";
        settings.UpdatePublicKey = "not a key";
        Assert.True((await scope.Service.UpdateSettingsAsync(settings)).Ok);
        Assert.Equal("0.7.4", scope.Service.DockerLatestVersion);
        Assert.Equal("0.7.4", scope.Service.GetStatus().DockerLatestVersion);
        Assert.Equal(["0.7.4"], heard);

        // A fetch that fails (nothing listens there) leaves the known version as it was and says nothing.
        await scope.Service.CheckDockerAsync();
        Assert.Equal("0.7.4", scope.Service.DockerLatestVersion);
        Assert.Equal(["0.7.4"], heard);

        // The Docker manifest sits beside the Windows one.
        Assert.Equal("https://x.test/rel/manifest-docker.json", DownloadManagerService.DockerManifestUrlFor("https://x.test/rel/manifest.json"));
        Assert.Null(DownloadManagerService.DockerManifestUrlFor(""));

        // Updates turned off: the Docker latest is forgotten and the change announced once.
        settings = scope.Service.GetSettings();
        settings.UpdateManifestUrl = string.Empty;
        Assert.True((await scope.Service.UpdateSettingsAsync(settings)).Ok);
        Assert.Null(scope.Service.DockerLatestVersion);
        Assert.Equal(["0.7.4", null], heard);
    }

    private sealed class Clock(DateTimeOffset now) : TimeProvider
    {
        public DateTimeOffset Now { get; set; } = now;
        public override DateTimeOffset GetUtcNow() => Now;
    }

    private sealed class Scope : IDisposable
    {
        private readonly string _dir = Path.Combine(AppContext.BaseDirectory, "test-work", "upd-waiting-" + Guid.NewGuid().ToString("N"));
        private readonly HttpDownloadEngine _engine;

        public Clock Clock { get; } = new(Noon);
        public DownloadManagerService Service { get; }

        public Scope()
        {
            var paths = new StoragePaths(Path.Combine(_dir, "state"));
            _engine = new HttpDownloadEngine(new EngineOptions { AllowLocalNetwork = true, MaxAttempts = 1 }, checkpoints: new JsonCheckpointStore(paths));
            Service = new DownloadManagerService(paths, new JsonJobStore(paths), new JsonCheckpointStore(paths), new JsonSettingsStore(paths), _engine,
                enableSchedulerTimer: false, timeProvider: Clock);
        }

        public void Dispose()
        {
            _engine.Dispose();
            try { Directory.Delete(_dir, recursive: true); } catch (IOException) { }
        }
    }
}

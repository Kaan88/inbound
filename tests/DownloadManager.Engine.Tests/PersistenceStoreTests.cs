using System.Text.Json;
using System.Text.Json.Nodes;
using DownloadManager.Core;
using DownloadManager.Persistence;

namespace DownloadManager.Engine.Tests;

/// <summary>
/// Audit 1, T-9 (L7-L10): direct tests of the JSON stores. A held job file is read again and left in place,
/// settings with null items load, a sealed proxy password that can't be opened is kept, a checkpoint from a
/// newer build fails instead of reading as none, and stale temporary files are swept at start.
/// </summary>
public sealed class PersistenceStoreTests : IDisposable
{
    private readonly string _root = Path.Combine(AppContext.BaseDirectory, "test-work", "persist-" + Guid.NewGuid().ToString("N"));
    private readonly StoragePaths _paths;

    public PersistenceStoreTests()
    {
        _paths = new StoragePaths(_root);
        _paths.EnsureCreated();
    }

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
    }

    private static DownloadJob NewJob() => new()
    {
        Url = "https://example.test/file.bin",
        FileName = "file.bin",
        DestinationDirectory = Path.GetTempPath(),
        FinalFilePath = Path.Combine(Path.GetTempPath(), "file.bin"),
        PartFilePath = Path.Combine(Path.GetTempPath(), "file.bin.part"),
        State = DownloadState.Paused,
    };

    [Fact]
    public async Task AJobFileHeldByAnotherProgramStaysAndIsNotSetAside()
    {
        var store = new JsonJobStore(_paths);
        var job = NewJob();
        await store.SaveAsync(job);
        var path = Directory.GetFiles(_paths.JobsDirectory, "*.json").Single();

        IReadOnlyList<DownloadJob> loaded;
        await using (new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.None))
        {
            loaded = await store.LoadAllAsync();
        }

        Assert.Empty(loaded);
        Assert.True(File.Exists(path), "the held job must stay where it is");
        Assert.Empty(Directory.GetFiles(_paths.CorruptDirectory));
        Assert.Equal(job.Id, (await store.LoadAllAsync()).Single().Id);
    }

    [Fact]
    public async Task AJobFileHeldForAMomentIsReadOnTheNextTry()
    {
        var store = new JsonJobStore(_paths);
        var job = NewJob();
        await store.SaveAsync(job);
        var path = Directory.GetFiles(_paths.JobsDirectory, "*.json").Single();

        var held = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.None);
        var release = Task.Run(async () =>
        {
            await Task.Delay(150);
            await held.DisposeAsync();
        });
        var loaded = await store.LoadAllAsync();
        await release;

        Assert.Equal(job.Id, loaded.Single().Id);
    }

    [Fact]
    public async Task ABrokenJobFileIsStillSetAside()
    {
        var store = new JsonJobStore(_paths);
        var path = Path.Combine(_paths.JobsDirectory, $"{Guid.NewGuid():N}.json");
        await File.WriteAllTextAsync(path, "{ not json");

        Assert.Empty(await store.LoadAllAsync());
        Assert.False(File.Exists(path));
        Assert.Single(Directory.GetFiles(_paths.CorruptDirectory));
    }

    [Fact]
    public async Task SettingsFromANewerBuildKeepWhatThisBuildKnowsAndTheNewerFile()
    {
        var store = new JsonSettingsStore(_paths);
        await store.SaveAsync(new AppSettings { DefaultDownloadDirectory = _root, MaxConcurrentDownloads = 7 });
        var node = JsonNode.Parse(await File.ReadAllTextAsync(_paths.SettingsFile))!.AsObject();
        node["schemaVersion"] = AppSettings.CurrentSchemaVersion + 1;
        node["aSettingFromTheFuture"] = "kept in the copy";
        var newer = node.ToJsonString();
        await File.WriteAllTextAsync(_paths.SettingsFile, newer);

        var settings = await store.LoadAsync();

        Assert.Equal(_root, settings.DefaultDownloadDirectory);
        Assert.Equal(7, settings.MaxConcurrentDownloads);
        var copy = Assert.Single(Directory.GetFiles(_paths.CorruptDirectory, "settings.newer-*.json"));
        Assert.Equal(newer, await File.ReadAllTextAsync(copy));
        // The file now says what it holds: this build's schema, so the next load is an ordinary one.
        using var saved = JsonDocument.Parse(await File.ReadAllTextAsync(_paths.SettingsFile));
        Assert.Equal(AppSettings.CurrentSchemaVersion, saved.RootElement.GetProperty("schemaVersion").GetInt32());
        Assert.Equal(7, (await store.LoadAsync()).MaxConcurrentDownloads);
        Assert.Single(Directory.GetFiles(_paths.CorruptDirectory));
    }

    [Fact]
    public async Task SettingsWithNullItemsLoadInsteadOfThrowing()
    {
        var store = new JsonSettingsStore(_paths);
        await store.SaveAsync(new AppSettings { DefaultDownloadDirectory = _root });
        var node = JsonNode.Parse(await File.ReadAllTextAsync(_paths.SettingsFile))!.AsObject();
        node["excludedHosts"] = new JsonArray(null, "Example.test");
        node["siteLogins"] = new JsonArray((JsonNode?)null);
        node["queues"] = new JsonArray((JsonNode?)null);
        node["schedules"] = new JsonArray((JsonNode?)null);
        node["connectionProfiles"] = new JsonArray((JsonNode?)null);
        node["customCategories"] = new JsonArray((JsonNode?)null);
        await File.WriteAllTextAsync(_paths.SettingsFile, node.ToJsonString());

        var settings = await store.LoadAsync();

        Assert.Equal(["example.test"], settings.ExcludedHosts);
        Assert.Empty(settings.SiteLogins);
        Assert.Single(settings.Queues);
        Assert.Empty(settings.Schedules);
        Assert.Empty(settings.ConnectionProfiles);
        Assert.Empty(Directory.GetFiles(_paths.CorruptDirectory));
        Assert.Equal(_root, settings.DefaultDownloadDirectory);
    }

    [Fact]
    public async Task AProxyPasswordThatCannotBeOpenedIsKeptOnSave()
    {
        if (!OperatingSystem.IsWindows()) return;
        var store = new JsonSettingsStore(_paths);
        await store.SaveAsync(new AppSettings { DefaultDownloadDirectory = _root });
        // Sealed for another account: DPAPI can't open it here.
        var foreign = DpapiSecretProtector.BlobPrefix + Convert.ToBase64String(new byte[64]);
        var node = JsonNode.Parse(await File.ReadAllTextAsync(_paths.SettingsFile))!.AsObject();
        node["proxy"] = new JsonObject { ["mode"] = "manual", ["address"] = "proxy.test:8080", ["passwordProtected"] = foreign };
        await File.WriteAllTextAsync(_paths.SettingsFile, node.ToJsonString());

        var settings = await store.LoadAsync();
        Assert.True(string.IsNullOrEmpty(settings.Proxy.Password));
        await store.SaveAsync(settings);

        using var saved = JsonDocument.Parse(await File.ReadAllTextAsync(_paths.SettingsFile));
        Assert.Equal(foreign, saved.RootElement.GetProperty("proxy").GetProperty("passwordProtected").GetString());
    }

    [Fact]
    public async Task AReadableProxyPasswordCanStillBeCleared()
    {
        if (!OperatingSystem.IsWindows()) return;
        var store = new JsonSettingsStore(_paths);
        var settings = new AppSettings { DefaultDownloadDirectory = _root };
        settings.Proxy.Mode = "manual";
        settings.Proxy.Address = "proxy.test:8080";
        settings.Proxy.Password = "secret";
        await store.SaveAsync(settings);

        var loaded = await store.LoadAsync();
        Assert.Equal("secret", loaded.Proxy.Password);
        loaded.Proxy.Password = null;
        await store.SaveAsync(loaded);

        Assert.True(string.IsNullOrEmpty((await store.LoadAsync()).Proxy.Password));
    }

    [Fact]
    public async Task ACheckpointFromANewerBuildFailsInsteadOfReadingAsNone()
    {
        var store = new JsonCheckpointStore(_paths);
        var id = Guid.NewGuid();
        var checkpoint = new DownloadCheckpoint { JobId = id, Url = "https://example.test/a", SchemaVersion = DownloadCheckpoint.CurrentSchemaVersion + 1 };
        checkpoint.Segments.Add(new SegmentCheckpoint { Start = 0, End = 99, Completed = 50 });
        await store.SaveAsync(checkpoint);

        var watch = System.Diagnostics.Stopwatch.StartNew();
        var error = await Assert.ThrowsAsync<CheckpointTooNewException>(() => store.LoadAsync(id));
        watch.Stop();
        Assert.IsAssignableFrom<CheckpointUnusableException>(error);
        Assert.IsAssignableFrom<IOException>(error);
        Assert.Contains("newer version of the app", error.Message);
        // The store's own retries (100 + 200 + 300 ms) are for a held file, not for one that can never be read.
        Assert.True(watch.ElapsedMilliseconds < 500, $"took {watch.ElapsedMilliseconds} ms; an unusable checkpoint was retried");
        Assert.True(File.Exists(Path.Combine(_paths.CheckpointsDirectory, $"{id:N}.json")));
    }

    [Theory]
    [InlineData("{ \"jobId\": ")]
    [InlineData("not json at all")]
    [InlineData("null")]
    public async Task ADamagedCheckpointFailsInsteadOfReadingAsNone(string content)
    {
        var store = new JsonCheckpointStore(_paths);
        var id = Guid.NewGuid();
        var path = Path.Combine(_paths.CheckpointsDirectory, $"{id:N}.json");
        await File.WriteAllTextAsync(path, content);

        var watch = System.Diagnostics.Stopwatch.StartNew();
        var error = await Assert.ThrowsAsync<CheckpointDamagedException>(() => store.LoadAsync(id));
        watch.Stop();
        Assert.IsAssignableFrom<CheckpointUnusableException>(error);
        Assert.True(watch.ElapsedMilliseconds < 500, $"took {watch.ElapsedMilliseconds} ms; a damaged checkpoint was retried");
        Assert.Equal(content, await File.ReadAllTextAsync(path));
    }

    [Fact]
    public async Task StaleTemporaryFilesAreSweptAtStart()
    {
        var stale = Path.Combine(_paths.CheckpointsDirectory, $".{Guid.NewGuid():N}.json.{Guid.NewGuid():N}.tmp");
        var fresh = Path.Combine(_paths.JobsDirectory, $".{Guid.NewGuid():N}.json.{Guid.NewGuid():N}.tmp");
        var other = Path.Combine(_paths.JobsDirectory, ".notes.tmp");
        foreach (var file in new[] { stale, fresh, other }) await File.WriteAllTextAsync(file, "{}");
        File.SetLastWriteTimeUtc(stale, DateTime.UtcNow.AddMinutes(-10));
        File.SetLastWriteTimeUtc(other, DateTime.UtcNow.AddMinutes(-10));

        await new JsonJobStore(_paths).InitializeAsync();

        Assert.False(File.Exists(stale));
        Assert.True(File.Exists(fresh), "a write in progress elsewhere is left alone");
        Assert.True(File.Exists(other), "only files of the atomic-write shape are removed");
    }

    /// <summary>Audit 1, N2: the torrent, archive and update folders are swept too.</summary>
    [Fact]
    public async Task StaleTemporaryFilesAreSweptInEveryStoreFolder()
    {
        var folders = new[]
        {
            _paths.TorrentsDirectory, Path.Combine(_root, "archive"), Path.Combine(_root, "archive", "sets"),
            Path.Combine(_root, "archive", "packages"), Path.Combine(_root, "updates"),
        };
        var stale = new List<string>();
        foreach (var folder in folders)
        {
            Directory.CreateDirectory(folder);
            var file = Path.Combine(folder, $".{Guid.NewGuid():N}.resume.{Guid.NewGuid():N}.tmp");
            await File.WriteAllTextAsync(file, "x");
            File.SetLastWriteTimeUtc(file, DateTime.UtcNow.AddMinutes(-10));
            stale.Add(file);
        }

        await new JsonJobStore(_paths).InitializeAsync();

        Assert.All(stale, file => Assert.False(File.Exists(file), file));
    }

    /// <summary>Audit 1, N7: a job from a newer build stays where it is, as its checkpoint and the settings do.</summary>
    [Fact]
    public async Task AJobFileFromANewerBuildStaysAndIsNotSetAside()
    {
        var store = new JsonJobStore(_paths);
        var job = NewJob();
        await store.SaveAsync(job);
        var path = Directory.GetFiles(_paths.JobsDirectory, "*.json").Single();
        var node = JsonNode.Parse(await File.ReadAllTextAsync(path))!.AsObject();
        var key = node.Select(pair => pair.Key).Single(name => name.Equals("schemaVersion", StringComparison.OrdinalIgnoreCase));
        node[key] = DownloadJob.CurrentSchemaVersion + 1;
        await File.WriteAllTextAsync(path, node.ToJsonString());

        Assert.Empty(await store.LoadAllAsync());
        Assert.True(File.Exists(path), "the newer job must stay where it is");
        Assert.Empty(Directory.GetFiles(_paths.CorruptDirectory));
    }

    /// <summary>Audit 1, N1: a stream track's checkpoint that can't be used throws, as a download checkpoint does.</summary>
    [Fact]
    public async Task AMediaCheckpointFromANewerBuildFailsInsteadOfReadingAsNone()
    {
        var store = new JsonMediaCheckpointStore(_paths);
        var id = Guid.NewGuid();
        await store.SaveAsync(new MediaCheckpoint { JobId = id, Track = "dash-video", CompletedSegments = 3, CompletedBytes = 300 });
        var path = Directory.GetFiles(_paths.MediaCheckpointsDirectory, "*.json").Single();
        var node = JsonNode.Parse(await File.ReadAllTextAsync(path))!.AsObject();
        var key = node.Select(pair => pair.Key).Single(name => name.Equals("schemaVersion", StringComparison.OrdinalIgnoreCase));
        node[key] = MediaCheckpoint.CurrentSchemaVersion + 1;
        await File.WriteAllTextAsync(path, node.ToJsonString());

        var error = await Assert.ThrowsAsync<CheckpointTooNewException>(() => store.LoadAsync(id, "dash-video"));
        Assert.IsAssignableFrom<CheckpointUnusableException>(error);
        Assert.True(File.Exists(path));
    }

    [Theory]
    [InlineData("{ \"jobId\": ")]
    [InlineData("not json at all")]
    [InlineData("null")]
    public async Task ADamagedMediaCheckpointFailsInsteadOfReadingAsNone(string content)
    {
        var store = new JsonMediaCheckpointStore(_paths);
        var id = Guid.NewGuid();
        await store.SaveAsync(new MediaCheckpoint { JobId = id, Track = "hls" });
        var path = Directory.GetFiles(_paths.MediaCheckpointsDirectory, "*.json").Single();
        await File.WriteAllTextAsync(path, content);

        var watch = System.Diagnostics.Stopwatch.StartNew();
        await Assert.ThrowsAsync<CheckpointDamagedException>(() => store.LoadAsync(id, "hls"));
        watch.Stop();
        Assert.True(watch.ElapsedMilliseconds < 500, $"took {watch.ElapsedMilliseconds} ms; a damaged checkpoint was retried");
        Assert.Equal(content, await File.ReadAllTextAsync(path));
    }

    [Fact]
    public async Task AHeldMediaCheckpointIsReadAgainAndThrowsWhenStillHeld()
    {
        var store = new JsonMediaCheckpointStore(_paths);
        var id = Guid.NewGuid();
        await store.SaveAsync(new MediaCheckpoint { JobId = id, Track = "hls", CompletedSegments = 2, CompletedBytes = 20 });
        var path = Directory.GetFiles(_paths.MediaCheckpointsDirectory, "*.json").Single();

        // Held throughout: not none, which would truncate the track.
        await using (new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.None))
        {
            var error = await Assert.ThrowsAnyAsync<IOException>(() => store.LoadAsync(id, "hls"));
            Assert.IsNotAssignableFrom<CheckpointUnusableException>(error);
        }

        // Let go during the retries: read.
        var holder = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.None);
        var release = Task.Delay(150).ContinueWith(_ => holder.Dispose());
        var loaded = await store.LoadAsync(id, "hls");
        await release;
        Assert.Equal(2, loaded!.CompletedSegments);
    }
}

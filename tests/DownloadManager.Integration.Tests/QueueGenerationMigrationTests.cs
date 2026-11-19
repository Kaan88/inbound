using DownloadManager.Core;
using DownloadManager.Persistence;

namespace DownloadManager.Integration.Tests;

public sealed class QueueGenerationMigrationTests : IDisposable
{
    private readonly string _root = Path.Combine(AppContext.BaseDirectory, "test-work", "queue-generation-" + Guid.NewGuid().ToString("N"));
    private StoragePaths Paths => new(Path.Combine(_root, "state"));

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); } catch (IOException) { } catch (UnauthorizedAccessException) { }
    }

    [Fact]
    public async Task MissingQueueGenerationIsPersistedOnFirstLoadAndStableAcrossIndependentLoads()
    {
        var paths = Paths;
        paths.EnsureCreated();
        await File.WriteAllTextAsync(paths.SettingsFile, $$"""
            {
              "schemaVersion": {{AppSettings.CurrentSchemaVersion}},
              "closeWindowCompletely": false,
              "useTorrentCategoryFolders": true,
              "defaultQueueId": 0,
              "queues": [
                { "id": 0, "name": "Main", "isRunning": true }
              ]
            }
            """);
        await File.WriteAllTextAsync(paths.SettingsFile + ".perf5", "1");

        var first = await new JsonSettingsStore(paths).LoadAsync();
        var firstGeneration = Assert.Single(first.Queues).Generation;

        Assert.NotEqual(Guid.Empty, firstGeneration);
        var firstSavedJson = await File.ReadAllTextAsync(paths.SettingsFile);
        Assert.Contains("\"generation\"", firstSavedJson, StringComparison.OrdinalIgnoreCase);

        var second = await new JsonSettingsStore(paths).LoadAsync();
        var secondGeneration = Assert.Single(second.Queues).Generation;
        Assert.Equal(firstGeneration, secondGeneration);
    }

    [Fact]
    public async Task EmptyGenerationIsReplacedWhileExistingAndNewQueueIdentitiesArePreserved()
    {
        var paths = Paths;
        paths.EnsureCreated();
        var preservedGeneration = Guid.Parse("6c328f29-3711-42a8-a1bb-aeb568488ee7");
        await File.WriteAllTextAsync(paths.SettingsFile, $$"""
            {
              "schemaVersion": {{AppSettings.CurrentSchemaVersion}},
              "closeWindowCompletely": false,
              "useTorrentCategoryFolders": true,
              "defaultQueueId": 0,
              "queues": [
                { "id": 0, "name": "Main", "isRunning": true, "generation": "00000000-0000-0000-0000-000000000000" },
                { "id": 3, "name": "Existing", "isRunning": false, "generation": "{{preservedGeneration}}" },
                { "id": 7, "name": "New", "isRunning": false }
              ]
            }
            """);
        await File.WriteAllTextAsync(paths.SettingsFile + ".perf5", "1");

        var loaded = await new JsonSettingsStore(paths).LoadAsync();

        var emptyGenerationQueue = loaded.Queues.Single(queue => queue.Id == 0);
        var existingQueue = loaded.Queues.Single(queue => queue.Id == 3);
        var missingGenerationQueue = loaded.Queues.Single(queue => queue.Id == 7);
        Assert.NotEqual(Guid.Empty, emptyGenerationQueue.Generation);
        Assert.Equal(preservedGeneration, existingQueue.Generation);
        Assert.NotEqual(Guid.Empty, missingGenerationQueue.Generation);
        Assert.NotEqual(emptyGenerationQueue.Generation, missingGenerationQueue.Generation);
        var reloaded = await new JsonSettingsStore(paths).LoadAsync();
        Assert.Equal(loaded.Queues.Select(q => q.Generation), reloaded.Queues.Select(q => q.Generation));
        Assert.False(loaded.CloseWindowCompletely); // The old marker prevents an unrelated migration from masking this one.
    }
}

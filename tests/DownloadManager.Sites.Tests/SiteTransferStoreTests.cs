using DownloadManager.Sites;

namespace DownloadManager.Sites.Tests;

/// <summary>SITES.4: batches survive a restart, and a damaged, foreign or too-new file is left alone.</summary>
public sealed class SiteTransferStoreTests : IDisposable
{
    private readonly string _root = Path.Combine(AppContext.BaseDirectory, "test-work", "site-transfer-store-" + Guid.NewGuid().ToString("N"));
    private readonly List<string> _log = [];

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { /* best effort */ }
    }

    private SiteTransferStore Store() => new(_root, _log.Add);

    private static SiteTransferRecord Record(DateTimeOffset at) => new()
    {
        Id = SiteTransferStore.NewId(),
        SiteId = SiteStore.NewId(),
        SiteName = "Office",
        Direction = SiteTransferDirections.Download,
        RemoteFolder = "/srv",
        LocalFolder = "/home/me/in",
        Conflict = SiteTransferConflicts.KeepBoth,
        CreatedAt = at,
        Items =
        [
            new SiteTransferItem { Path = "a.txt", Size = 1, State = SiteTransferItemStates.Done },
            new SiteTransferItem { Path = "d/b.txt", Size = 2, Target = "d/b (2).txt", Tries = 1, Error = "No." },
        ],
    };

    [Fact]
    public async Task ABatchReadsBackAsItWasWrittenOldestFirst()
    {
        var store = Store();
        var later = Record(DateTimeOffset.UtcNow);
        var earlier = Record(later.CreatedAt.AddMinutes(-1));
        await store.WriteAsync(later.Id, SiteTransferStore.Serialize(later));
        await store.WriteAsync(earlier.Id, SiteTransferStore.Serialize(earlier));

        var loaded = Store().LoadAll();
        Assert.Equal([earlier.Id, later.Id], loaded.Select(r => r.Id));
        var item = loaded[1].Items[1];
        Assert.Equal(("d/b.txt", "d/b (2).txt", 1, "No."), (item.Path, item.Target, item.Tries, item.Error));
        Assert.Equal(SiteTransferConflicts.KeepBoth, loaded[1].Conflict);
        Assert.Empty(_log);
    }

    [Fact]
    public async Task DamagedForeignAndNewerFilesAreSkippedAndKept()
    {
        var store = Store();
        Directory.CreateDirectory(store.Folder);
        var good = Record(DateTimeOffset.UtcNow);
        await store.WriteAsync(good.Id, SiteTransferStore.Serialize(good));

        var damaged = Path.Combine(store.Folder, SiteTransferStore.NewId() + ".json");
        await File.WriteAllTextAsync(damaged, "{ not json");
        var other = Record(DateTimeOffset.UtcNow);
        var foreign = Path.Combine(store.Folder, SiteTransferStore.NewId() + ".json"); // names another batch
        await File.WriteAllBytesAsync(foreign, SiteTransferStore.Serialize(other));
        var newer = Record(DateTimeOffset.UtcNow);
        newer.SchemaVersion = SiteTransferRecord.CurrentSchemaVersion + 1;
        await store.WriteAsync(newer.Id, SiteTransferStore.Serialize(newer));
        await File.WriteAllTextAsync(Path.Combine(store.Folder, "notes.json"), "{}");

        var loaded = store.LoadAll();
        Assert.Equal(good.Id, Assert.Single(loaded).Id);
        Assert.Equal(3, _log.Count);
        Assert.True(File.Exists(damaged) && File.Exists(foreign));
    }

    [Fact]
    public async Task AnIdThatIsNotOursIsRefused()
    {
        var store = Store();
        Assert.Throws<SiteStoreException>(() => store.Delete("../sites/abc"));
        await Assert.ThrowsAsync<SiteStoreException>(() => store.WriteAsync("..\\x", [1]));
    }
}

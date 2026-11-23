using DownloadManager.Contracts;
using DownloadManager.Core;
using DownloadManager.Persistence;

namespace DownloadManager.Integration.Tests;

public sealed partial class RuntimeServiceTests
{
    [Fact]
    public async Task QueueCreationSaveFailureDoesNotPublishAnUndurableQueue()
    {
        QueueCreationFailingStore? store = null;
        await using var ctx = await ServiceContext.CreateAsync(_server, hooks: new ContextHooks { WrapSettings = inner => store = new QueueCreationFailingStore(inner) });
        var before = ctx.Service.GetSettings();
        store!.Fail = true;
        await Assert.ThrowsAsync<IOException>(() => ctx.Service.QueueActionAsync(new QueueActionRequest { Action = "create", Name = "Not saved", MaxConcurrentDownloads = 2, RequireCharging = true, SpeedLimitBytesPerSecond = 1024 }));
        Assert.Equal(before.Queues.Select(q => q.Id), ctx.Service.GetSettings().Queues.Select(q => q.Id));
        Assert.DoesNotContain(ctx.Service.GetSettings().Queues, q => q.Name == "Not saved");
        Assert.DoesNotContain((await new JsonSettingsStore(ctx.Paths).LoadAsync()).Queues, q => q.Name == "Not saved");
        store.Fail = false;
        var retry = await ctx.Service.QueueActionAsync(new QueueActionRequest { Action = "create", Name = "Saved once", MaxConcurrentDownloads = 2 });
        Assert.True(retry.Ok, retry.Error); Assert.Single(retry.Settings.Queues, q => q.Name == "Saved once");
    }

    [Fact]
    public async Task QueueCreateAndDeleteWorkWhileTheDefaultFolderCannotBeMade()
    {
        await using var ctx = await ServiceContext.CreateAsync(_server);
        // The default folder becomes impossible to make: a file stands where it was, as with an unplugged drive or a share
        // that is gone. Everything else is sound.
        Directory.Delete(ctx.Destination);
        await File.WriteAllTextAsync(ctx.Destination, "");
        try
        {
            var settings = ctx.Service.GetSettings();
            Assert.Equal(ctx.Destination, settings.DefaultDownloadDirectory);

            var created = await ctx.Service.QueueActionAsync(new QueueActionRequest { Action = "create", Name = "Offline", MaxConcurrentDownloads = 2 });
            Assert.True(created.Ok, created.Error);
            var queue = Assert.Single(created.Settings.Queues, q => q.Name == "Offline");
            Assert.Contains((await new JsonSettingsStore(ctx.Paths).LoadAsync()).Queues, q => q.Name == "Offline");

            var deleted = await ctx.Service.QueueActionAsync(new QueueActionRequest { Action = "delete", QueueId = queue.Id, RemoveSchedules = true });
            Assert.True(deleted.Ok, deleted.Error);
            Assert.DoesNotContain(deleted.Settings.Queues, q => q.Id == queue.Id);

            // A user's own settings save still checks the folder, so a folder that can't be made is still reported.
            var refused = await ctx.Service.UpdateSettingsAsync(ctx.Service.GetSettings());
            Assert.False(refused.Ok);
        }
        finally
        {
            File.Delete(ctx.Destination);
        }
    }

    private sealed class QueueCreationFailingStore(ISettingsStore inner) : ISettingsStore
    {
        public bool Fail { get; set; }
        public Task<AppSettings> LoadAsync(CancellationToken cancellationToken = default) => inner.LoadAsync(cancellationToken);
        public Task SaveAsync(AppSettings settings, CancellationToken cancellationToken = default)
            => Fail ? Task.FromException(new IOException("Controlled settings failure")) : inner.SaveAsync(settings, cancellationToken);
    }
}

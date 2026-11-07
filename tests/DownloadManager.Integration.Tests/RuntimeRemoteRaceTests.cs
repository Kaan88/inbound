using DownloadManager.Core.Providers;
using DownloadManager.Persistence;
using DownloadManager.Runtime.Remote;
using TestServer;

namespace DownloadManager.Integration.Tests;

/// <summary>
/// Regressions from a later review of the remote core: two jobs reaching one item, a delete whose
/// item is already gone, and a retry that lands while a look is waiting on the network. All values are invented.
/// </summary>
public sealed partial class RuntimeServiceTests
{
    [Fact]
    public async Task ACopiedMagnetApprovedLaterDoesntDownloadAnItemAnotherJobBrings()
    {
        await using var rig = await RemoteRig.CreateAsync(_server);
        var file = MockTorBoxFile.Of(rig.Mock.NextFileId(), "Pack/a.bin", 8 * Kb, 31);
        var item = rig.Mock.AddItem(RemoteItemKind.Torrent, "Pack", [file]);

        // Copied first and held, so it has a hash and no item yet.
        var held = await rig.AddAsync(new RemoteAddRequest { Input = $"magnet:?xt=urn:btih:{item.Hash}", HoldForApproval = true, Selection = RemoteSelectionMode.All });
        // Then the same item from the account's list, which has an item and no hash: no twin by either.
        var chosen = await rig.AddAsync(new RemoteAddRequest
        {
            ExistingItem = new RemoteItemRef("torbox", RemoteItemKind.Torrent, item.Id.ToString()),
            Selection = RemoteSelectionMode.All,
        });
        Assert.NotEqual(held.Id, chosen.Id);
        await rig.RunAsync();
        Assert.Equal(RemoteJobState.LocalStarted, rig.View(chosen.Id).State);
        Assert.Single(rig.Children());

        Assert.True((await rig.Jobs.ApproveUncachedAsync(held.Id)).Ok);
        await rig.RunAsync();

        var twin = rig.View(held.Id);
        Assert.Equal(RemoteJobState.Failed, twin.State);
        Assert.Contains("already on the list as another job", twin.FailureMessage);
        Assert.Single(rig.Children());
        Assert.Empty(rig.Mock.Submissions);
        await rig.AssertCompletedAsync(file, "Pack", "a.bin");
    }

    [Fact]
    public async Task RetryingAFailedJobWhoseItemAnotherJobNowBringsIsRefused()
    {
        await using var rig = await RemoteRig.CreateAsync(_server);
        var file = MockTorBoxFile.Of(rig.Mock.NextFileId(), "Pack/a.bin", 8 * Kb, 32);
        var item = rig.Mock.AddItem(RemoteItemKind.Torrent, "Pack", [file], MockTorBoxState.Failed("Invented failure."), MockTorBoxState.CachedReady);
        var reference = new RemoteItemRef("torbox", RemoteItemKind.Torrent, item.Id.ToString());

        var first = await rig.AddAsync(new RemoteAddRequest { ExistingItem = reference, Selection = RemoteSelectionMode.All });
        await rig.RunAsync();
        Assert.Equal(RemoteJobState.Failed, rig.View(first.Id).State);

        // A failed job is no twin, so adding the item again makes a second job, which downloads it.
        var second = await rig.AddAsync(new RemoteAddRequest { ExistingItem = reference, Selection = RemoteSelectionMode.All });
        Assert.NotEqual(first.Id, second.Id);
        await rig.RunAsync();
        Assert.Equal(RemoteJobState.LocalStarted, rig.View(second.Id).State);
        Assert.Single(rig.Children());

        var retried = await rig.Jobs.RetryAsync(first.Id);
        Assert.False(retried.Ok);
        Assert.Contains("already on the list as another job", retried.Error);
        await rig.RunAsync();
        Assert.Equal(RemoteJobState.Failed, rig.View(first.Id).State);
        Assert.Single(rig.Children());
    }

    [Fact]
    public async Task DeletingAnItemAlreadyGoneFromTheProviderTakesTheJobOff()
    {
        await using var rig = await RemoteRig.CreateAsync(_server);
        var item = rig.Mock.AddItem(RemoteItemKind.Torrent, "Mine", [MockTorBoxFile.Of(rig.Mock.NextFileId(), "Mine/a.bin", Kb, 1)]);
        var job = await rig.AddAsync(new RemoteAddRequest { Input = $"https://torbox.app/download?id={item.Id}&type=torrents" });
        await rig.RunAsync();

        // Deleted on the provider's own site meanwhile.
        rig.Mock.RemoveItem(item);
        var result = await rig.Jobs.RemoveRemoteItemAsync(job.Id, confirmed: true);

        Assert.True(result.Ok, result.Error);
        Assert.Null(rig.Jobs.Find(job.Id));
        Assert.Empty(rig.Mock.Deleted);
    }

    [Fact]
    public async Task AClientLeavingOnceTheProviderDeletedStillTakesTheJobOff()
    {
        await using var rig = await RemoteRig.CreateAsync(_server);
        var item = rig.Mock.AddItem(RemoteItemKind.Torrent, "Mine", [MockTorBoxFile.Of(rig.Mock.NextFileId(), "Mine/a.bin", Kb, 1)]);
        var job = await rig.AddAsync(new RemoteAddRequest { Input = $"https://torbox.app/download?id={item.Id}&type=torrents" });
        await rig.RunAsync();
        await rig.Jobs.StopAsync();

        // The same records and provider, with the client's connection closing as the provider answers.
        using var connection = new CancellationTokenSource();
        var real = rig.Box.Registry.Find("torbox")!;
        var registry = new RemoteProviderRegistry();
        registry.Add(new LeavesAfterRemoving(real.Provider, connection), real.Account);
        await using var jobs = new RemoteJobCoordinator(registry, new JsonRemoteJobStore(rig.Ctx.Paths), rig.Host, new RemoteCoordinatorOptions { RunLoops = false });
        await jobs.StartAsync();

        var result = await jobs.RemoveRemoteItemAsync(job.Id, confirmed: true, connection.Token);

        Assert.True(result.Ok, result.Error);
        Assert.Equal(item.Id, Assert.Single(rig.Mock.Deleted).Id);
        Assert.Null(jobs.Find(job.Id));
        Assert.Empty(Directory.GetFiles(rig.Ctx.Paths.RemoteJobsDirectory));
    }

    /// <summary>The real provider's delete, then the caller's token cancelled, as a frontend closing right then would.</summary>
    private sealed class LeavesAfterRemoving(IRemoteProvider inner, CancellationTokenSource connection) : IRemoteProvider, IRemoteItemRemover
    {
        public string Id => inner.Id;
        public string DisplayName => inner.DisplayName;
        public RemoteCapabilities Capabilities => inner.Capabilities;
        public RemoteTransferPolicy TransferPolicy => inner.TransferPolicy;
        public Task<RemoteAccountInfo> GetAccountAsync(CancellationToken cancellationToken = default) => inner.GetAccountAsync(cancellationToken);

        public async Task RemoveAsync(RemoteItemRef item, CancellationToken cancellationToken = default)
        {
            await ((IRemoteItemRemover)inner).RemoveAsync(item, cancellationToken);
            await connection.CancelAsync();
        }
    }

    [Fact]
    public async Task APasswordGivenWhileALookIsWaitingIsTheOneSent()
    {
        await using var rig = await RemoteRig.CreateAsync(_server);
        const string password = "invented-raced-password";
        var (link, _, file) = rig.ScriptHoster("raced", MockTorBoxState.Ready);
        rig.Mock.RequireLinkPassword(link, password);
        rig.Mock.Fail(CreateWeb, MockFault.Error("ACTIVE_LIMIT", "Too many active downloads."), times: 1);
        var job = await rig.AddAsync(new RemoteAddRequest { Input = link, ApprovedUncached = true, Selection = RemoteSelectionMode.All });
        await rig.RunAsync();
        Assert.Equal(RemoteJobState.Blocked, rig.View(job.Id).State);

        // Its wait is over and a look at the list has begun when the user retries with the password.
        rig.Time.Advance(TimeSpan.FromSeconds(65));
        var lists = rig.Mock.Calls(WebList);
        rig.Mock.Fail(WebList, MockFault.Delay(TimeSpan.FromMilliseconds(500)), times: 1);
        var running = rig.Jobs.RunDueAsync();
        await Eventually(() => rig.Mock.Calls(WebList) > lists, "the look to begin");
        Assert.True((await rig.Jobs.RetryAsync(job.Id, password)).Ok);
        await running;

        var view = rig.View(job.Id);
        Assert.NotEqual(RemoteJobState.Failed, view.State);
        Assert.Equal(RemoteJobState.LocalStarted, view.State);
        Assert.Equal(password, rig.Mock.Submissions[^1].Password);
        await rig.AssertCompletedAsync(file, "raced", "raced.bin");
        await rig.AssertNothingLeakedAsync(password, link, "invented-fragment");
    }
}

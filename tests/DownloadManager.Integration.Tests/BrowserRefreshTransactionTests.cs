using DownloadManager.Contracts;
using DownloadManager.Core;
using DownloadManager.Persistence;
using DownloadManager.Runtime;

namespace DownloadManager.Integration.Tests;

public sealed partial class RuntimeServiceTests
{
    private sealed class RefreshStore(IJobStore inner) : IJobStore
    {
        public bool FailNext, BlockNext, FailAfterBlock;
        public TaskCompletionSource Entered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource Release { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public async Task SaveAsync(DownloadJob job, CancellationToken cancellationToken = default)
        {
            if (FailNext) { FailNext = false; throw new IOException("invented store failure"); }
            if (BlockNext) { BlockNext = false; Entered.TrySetResult(); await Release.Task.WaitAsync(TimeSpan.FromSeconds(20)); }
            if (FailAfterBlock) { FailAfterBlock = false; throw new IOException("invented blocked store failure"); }
            await inner.SaveAsync(job, cancellationToken);
        }
        public Task InitializeAsync(CancellationToken ct = default) => inner.InitializeAsync(ct);
        public Task<IReadOnlyList<DownloadJob>> LoadAllAsync(CancellationToken ct = default) => inner.LoadAllAsync(ct);
        public Task<DownloadJob?> LoadAsync(Guid id, CancellationToken ct = default) => inner.LoadAsync(id, ct);
        public Task DeleteAsync(Guid id, CancellationToken ct = default) => inner.DeleteAsync(id, ct);
        public Task<Guid?> FindClaimedRequestAsync(string owner, string request, CancellationToken ct = default) => inner.FindClaimedRequestAsync(owner, request, ct);
        public Task<bool> TryClaimRequestAsync(string owner, string request, Guid id, CancellationToken ct = default) => inner.TryClaimRequestAsync(owner, request, id, ct);
        public Task DeleteClaimAsync(string owner, string request, CancellationToken ct = default) => inner.DeleteClaimAsync(owner, request, ct);
    }
    private sealed class RefreshPolicy : IDirectAddressPolicy
    {
        public TaskCompletionSource Entered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource Release { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public async Task<string?> WhyNotDirectAsync(string address, bool explicitDirect, CancellationToken cancellationToken = default)
        { Entered.TrySetResult(); await Release.Task.WaitAsync(cancellationToken); return null; }
    }

    [Fact]
    public async Task BrowserRefreshStoreFailurePublishesNeitherNewAddressNorCredentials()
    {
        RefreshStore? store = null;
        await using var ctx = await ServiceContext.CreateAsync(_server, hooks: new ContextHooks { WrapJobs = inner => store = new RefreshStore(inner) });
        var added = await ctx.Service.AddJobAsync(new AddJobRequest
        {
            Url = $"{_server.BaseAddress}/file/refresh-store.bin?size=4096&seed=31", FileName = "refresh-store.bin", StartMode = "later",
            Headers = new() { ["Cookie"] = "old=login" },
        });
        Assert.True(added.Accepted, added.Error);
        var original = added.Job!;
        store!.FailNext = true;
        var result = await ctx.Service.RefreshUrlAsync(original.Id, original.Url + "&new=1", browserContext: new BrowserRequestContext
        { PageUrl = $"{_server.BaseAddress}/page", Headers = new() { ["Cookie"] = "fresh=login" } });
        Assert.False(result.Ok);
        Assert.Equal(original.Url, ctx.Service.GetJob(original.Id)!.Url);
        Assert.Equal("later", ctx.Service.GetJob(original.Id)!.StartMode);
        Assert.False(File.Exists(original.FinalFilePath));
        var disk = (await new JsonJobStore(ctx.Paths).LoadAsync(original.Id))!;
        Assert.Equal(original.Url, disk.Url); Assert.Equal("old=login", disk.RequestHeaders["Cookie"]);
    }

    [Fact]
    public async Task BrowserRefreshDoesNotPublishUntilSaveAndFinishesCommitAfterCallerCancellation()
    {
        RefreshStore? store = null;
        await using var ctx = await ServiceContext.CreateAsync(_server, hooks: new ContextHooks { WrapJobs = inner => store = new RefreshStore(inner) });
        var added = await ctx.Service.AddJobAsync(new AddJobRequest { Url = $"{_server.BaseAddress}/file/refresh-commit.bin?size=4096&seed=32", FileName = "refresh-commit.bin" });
        Assert.True(added.Accepted, added.Error);
        await ctx.WaitFor(added.Job!.Id, j => j.State == "completed");
        using var cancellation = new CancellationTokenSource();
        var nextUrl = added.Job.Url + "&new=1";
        store!.BlockNext = true;
        var refreshing = ctx.Service.RefreshUrlAsync(added.Job.Id, nextUrl, cancellationToken: cancellation.Token);
        try
        {
            await store.Entered.Task.WaitAsync(TimeSpan.FromSeconds(10));
            cancellation.Cancel();
            Assert.False(refreshing.IsCompleted);
            Assert.Equal(added.Job.Url, ctx.Service.GetJob(added.Job.Id)!.Url);
            Assert.Equal(added.Job.Url, (await new JsonJobStore(ctx.Paths).LoadAsync(added.Job.Id))!.Url);
        }
        finally { store.Release.TrySetResult(); }
        Assert.True((await refreshing).Ok);
        Assert.Equal(nextUrl, ctx.Service.GetJob(added.Job.Id)!.Url);
        Assert.Equal(nextUrl, (await new JsonJobStore(ctx.Paths).LoadAsync(added.Job.Id))!.Url);
    }

    [Fact]
    public async Task BrowserRefreshRechecksCompletionActionsAfterBlockedAddressValidation()
    {
        await using var ctx = await ServiceContext.CreateAsync(_server);
        var added = await ctx.Service.AddJobAsync(new AddJobRequest { Url = $"{_server.BaseAddress}/file/refresh-race.bin?size=4096&seed=33", FileName = "refresh-race.bin", StartMode = "later" });
        Assert.True(added.Accepted, added.Error);
        var policy = new RefreshPolicy(); ctx.Service.DirectAddresses = policy;
        var caller = new CallerScope(new PathRoots([ctx.Destination]));
        var refreshing = ctx.Service.RefreshUrlAsync(added.Job.Id, "https://203.0.113.10/replacement.bin", caller: caller);
        try
        {
            await policy.Entered.Task.WaitAsync(TimeSpan.FromSeconds(10));
            var edited = await ctx.Service.UpdateJobAsync(new JobUpdateRequest { JobId = added.Job.Id, CompletionOptions = new JobCompletionOptionsDto { OpenFile = true } });
            Assert.True(edited.Ok, edited.Error);
        }
        finally { policy.Release.TrySetResult(); }
        var result = await refreshing;
        Assert.False(result.Ok); Assert.Equal(CallerScope.CompletionActionsKeepAddress, result.Error);
        Assert.Equal(added.Job.Url, ctx.Service.GetJob(added.Job.Id)!.Url);
    }
}

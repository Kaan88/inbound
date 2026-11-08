using DownloadManager.Contracts;
using DownloadManager.Core.Providers;
using TestServer;

namespace DownloadManager.Integration.Tests;

/// <summary>
/// TB.S2: a hoster link's password offered to that link's own archive set, only when the user agreed where they typed
/// it, only as an offer, and never anywhere else. In-process over real HTTP to the mock. All values are invented.
/// </summary>
public sealed partial class RuntimeServiceTests
{
    private const string OfferedLinkPassword = "Invented-Link-Pw_5521";

    [Fact]
    public async Task TbS2_ALinkPasswordTheUserAgreedToShareIsOfferedToThatLinksOwnArchivesOnly()
    {
        await using var rig = await ContainerRig.CreateAsync(_server);
        var (link, _, file) = rig.Remote.ScriptHoster("shared-pw", MockTorBoxState.Ready);
        rig.Mock.RequireLinkPassword(link, OfferedLinkPassword);
        var (other, _) = rig.OnAccount("unrelated.rar");

        var added = await rig.Commands.AddAsync(new RemoteAddRequestDto
        {
            Input = link,
            Destination = rig.Remote.Destination,
            ApproveUncached = true,
            LinkPassword = OfferedLinkPassword,
            LinkPasswordForArchives = true,
        });
        Assert.True(added.Ok, added.Error);
        var unrelated = await rig.Commands.AddAsync(new RemoteAddRequestDto { Input = other, Destination = rig.Remote.Destination });
        Assert.True(unrelated.Ok, unrelated.Error);

        await rig.Remote.RunAsync();
        await rig.Remote.AdvanceAsync(65);
        var view = rig.Remote.View(added.Job!.Id);
        Assert.Equal(RemoteJobState.LocalStarted, view.State);
        Assert.Equal(OfferedLinkPassword, rig.Mock.Submissions[^1].Password);

        // Given to this job's package alone, as an offer, and the other package heard nothing.
        var (package, password) = Assert.Single(rig.Keeper.Given);
        Assert.Equal(view.PackageId, package);
        Assert.Equal(OfferedLinkPassword, password);
        Assert.Equal(1, rig.Keeper.Offers);
        Assert.NotEqual(rig.Remote.View(unrelated.Job!.Id).PackageId, package);
        await rig.Remote.AssertCompletedAsync(file, "shared-pw", "shared-pw.bin");
        await rig.Remote.AssertNothingLeakedAsync(OfferedLinkPassword, link);
    }

    [Fact]
    public async Task TbS2_WithoutTheUsersGoAheadOrWithAnArchivePasswordOfTheirOwnTheLinkPasswordStaysWithTheLink()
    {
        await using var rig = await ContainerRig.CreateAsync(_server);
        var (quiet, _, _) = rig.Remote.ScriptHoster("quiet-pw", MockTorBoxState.Ready);
        rig.Mock.RequireLinkPassword(quiet, OfferedLinkPassword);
        var (typed, _, _) = rig.Remote.ScriptHoster("typed-pw", MockTorBoxState.Ready);
        rig.Mock.RequireLinkPassword(typed, OfferedLinkPassword);

        var silent = await rig.Commands.AddAsync(new RemoteAddRequestDto
        {
            Input = quiet, Destination = rig.Remote.Destination, ApproveUncached = true, LinkPassword = OfferedLinkPassword,
        });
        var own = await rig.Commands.AddAsync(new RemoteAddRequestDto
        {
            Input = typed, Destination = rig.Remote.Destination, ApproveUncached = true, LinkPassword = OfferedLinkPassword,
            LinkPasswordForArchives = true, ArchivePassword = "Invented-Own-Archive-Pw",
        });
        Assert.True(silent.Ok, silent.Error);
        Assert.True(own.Ok, own.Error);
        Assert.False(silent.Job!.HasArchivePassword);

        await rig.Remote.RunAsync();
        await rig.Remote.AdvanceAsync(65);
        await rig.Remote.AdvanceAsync(65);
        Assert.Equal(RemoteJobState.LocalStarted, rig.Remote.View(silent.Job.Id).State);
        Assert.Equal(RemoteJobState.LocalStarted, rig.Remote.View(own.Job!.Id).State);

        // Only the user's own archive password reached the archive side, and not as an offer.
        var (package, password) = Assert.Single(rig.Keeper.Given);
        Assert.Equal(rig.Remote.View(own.Job.Id).PackageId, package);
        Assert.Equal("Invented-Own-Archive-Pw", password);
        Assert.Equal(0, rig.Keeper.Offers);
    }

    [Fact]
    public async Task AnArchivePasswordAloneReachesTheArchivesAndTheSiteHearsNoPassword()
    {
        // Add Links' first box (owner, 2026-11-08): the archive password, typed apart from the site's.
        await using var rig = await ContainerRig.CreateAsync(_server);
        var (link, _, file) = rig.Remote.ScriptHoster("archive-only-pw", MockTorBoxState.Ready);

        var added = await rig.Commands.AddAsync(new RemoteAddRequestDto
        {
            Input = link, Destination = rig.Remote.Destination, ApproveUncached = true, ArchivePassword = "Invented-Archive-Only-Pw",
        });
        Assert.True(added.Ok, added.Error);
        Assert.True(added.Job!.HasArchivePassword);
        Assert.False(added.Job.HasLinkPassword);

        await rig.Remote.RunAsync();
        await rig.Remote.AdvanceAsync(65);
        Assert.Equal(RemoteJobState.LocalStarted, rig.Remote.View(added.Job.Id).State);
        Assert.True(string.IsNullOrEmpty(rig.Mock.Submissions[^1].Password));

        var (package, password) = Assert.Single(rig.Keeper.Given);
        Assert.Equal(rig.Remote.View(added.Job.Id).PackageId, package);
        Assert.Equal("Invented-Archive-Only-Pw", password);
        Assert.Equal(0, rig.Keeper.Offers);
        await rig.Remote.AssertCompletedAsync(file, "archive-only-pw", "archive-only-pw.bin");
        await rig.Remote.AssertNothingLeakedAsync("Invented-Archive-Only-Pw", link);
    }

    [Fact]
    public async Task TbS2_ARetryWithTheLinkPasswordCanOfferItAndNeverReplacesAnArchivePasswordAlreadyGiven()
    {
        await using var rig = await ContainerRig.CreateAsync(_server);
        var (link, _, _) = rig.Remote.ScriptHoster("retry-pw", MockTorBoxState.Ready);
        rig.Mock.RequireLinkPassword(link, OfferedLinkPassword);
        var added = await rig.Commands.AddAsync(new RemoteAddRequestDto { Input = link, Destination = rig.Remote.Destination, ApproveUncached = true });
        await rig.Remote.RunAsync();
        Assert.Equal(RemoteFailureKind.LinkPasswordRequired, rig.Remote.View(added.Job!.Id).FailureKind);

        var retried = await rig.Commands.RetryAsync(new RemoteRetryRequest { JobId = added.Job.Id, LinkPassword = OfferedLinkPassword, LinkPasswordForArchives = true });
        Assert.True(retried.Ok, retried.Error);
        Assert.True(rig.Remote.View(added.Job.Id).HasArchivePassword);
        await rig.Remote.AdvanceAsync(65);
        Assert.Equal(RemoteJobState.LocalStarted, rig.Remote.View(added.Job.Id).State);
        Assert.Equal((rig.Remote.View(added.Job.Id).PackageId!.Value, OfferedLinkPassword), Assert.Single(rig.Keeper.Given));
        Assert.Equal(1, rig.Keeper.Offers);

        // Already has one: a second offer changes nothing.
        var offeredAgain = await rig.Remote.Jobs.OfferArchivePasswordAsync(added.Job.Id, "Invented-Later-Pw");
        Assert.True(offeredAgain.Ok);
        Assert.Single(rig.Keeper.Given);
    }
}

using DownloadManager.Contracts;
using DownloadManager.Core;
using DownloadManager.Core.Providers;
using DownloadManager.Runtime;
using DownloadManager.Runtime.Remote;
using TestServer;

namespace DownloadManager.Integration.Tests;

/// <summary>
/// The ways a hoster link could reach a direct request other than by adding one on purpose, and a
/// direct link's key reaching a file name. The mock stands in for the provider. All values are invented.
/// </summary>
public sealed partial class RuntimeServiceTests
{
    private const string WaitingTitle = "Copied link is waiting";
    private const string NotAddedTitle = "Copied link not added";
    private const string DirectTitle = "Clipboard download added";
    private const string TorrentList = "GET torrents/mylist";
    private const string HosterList = "GET webdl/hosters";

    private static async Task<CopiedLinks> CopiedLinksFor(RemoteRig rig)
    {
        var settings = rig.Ctx.Service.GetSnapshot().Settings;
        settings.DefaultDownloadDirectory = rig.Destination;
        Assert.True((await rig.Ctx.Service.UpdateSettingsAsync(settings)).Ok);
        return new CopiedLinks(rig.Ctx.Service, rig.Jobs);
    }

    /// <summary>A job on a direct address, not running, for the address to be changed.</summary>
    private async Task<JobDto> StoppedJob(RemoteRig rig, string? url = null)
    {
        var added = await rig.Ctx.Service.AddJobAsync(new AddJobRequest
        {
            Url = url ?? $"{_server.BaseAddress}/file/stopped.bin?size=1024&seed=801",
            FileName = "stopped.bin",
            DestinationDirectory = rig.Destination,
            StartMode = "later",
            Source = "cli",
        });
        Assert.True(added.Accepted, added.Error);
        return added.Job!;
    }

    private static void AssertSaysNothingOfTheAddress(CopiedLinkOutcome outcome, string link)
    {
        var host = new Uri(link).Host;
        var words = $"{outcome.Title} {outcome.Words} {outcome.Problem}";
        Assert.DoesNotContain("http", words, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain(host, words, StringComparison.OrdinalIgnoreCase);
    }

    // ---- Clipboard -----------------------------------------------------------------------------

    [Fact]
    public async Task ACopiedHosterLinkWaitsForApprovalAndNothingIsAskedOrSpentUntilThen()
    {
        await using var rig = await RemoteRig.CreateAsync(_server);
        var links = await CopiedLinksFor(rig);
        var (link, _, _) = rig.ScriptHoster("copied", MockTorBoxState.Ready);

        var outcome = await links.AddAsync(link);

        Assert.Equal(WaitingTitle, outcome.Title);
        Assert.Contains("TorBox", outcome.Words);
        AssertSaysNothingOfTheAddress(outcome, link);
        var job = Assert.Single(rig.Jobs.List());
        Assert.Equal(RemoteJobState.Intended, job.State);
        Assert.True(job.ApprovalNeeded);
        Assert.True(job.HeldForApproval);
        Assert.Contains("Approve", job.Status);
        Assert.DoesNotContain("cached", job.Status, StringComparison.OrdinalIgnoreCase);
        Assert.Empty(rig.Ctx.Service.GetSnapshot().Jobs);

        // Time passing, Check Now and a restart are none of them the user's approval.
        await rig.RunAsync();
        await rig.AdvanceAsync(120);
        var checkNow = await rig.Jobs.CheckNowAsync(job.Id);
        Assert.False(checkNow.Ok);
        await rig.Jobs.CheckNowAsync(job.Provider);
        await rig.RunAsync();
        await rig.RestartAsync();
        await rig.AdvanceAsync(120);

        Assert.True(rig.View(job.Id).HeldForApproval);
        Assert.Equal(0, rig.Mock.Calls(CreateWeb));
        Assert.Equal(0, rig.Mock.Calls(WebList));
        Assert.Equal(0, rig.Mock.HosterRequests);

        Assert.True((await rig.Jobs.ApproveUncachedAsync(job.Id)).Ok);
        await rig.RunAsync();
        Assert.Equal(1, rig.Mock.Calls(CreateWeb));
        Assert.False(rig.View(job.Id).HeldForApproval);
        Assert.Equal(0, rig.Mock.HosterRequests);
        Assert.DoesNotContain(rig.Ctx.Service.GetSnapshot().Jobs, local => local.Source == "clipboardMonitor");
        await rig.AssertNothingLeakedAsync("invented-fragment-copied");
    }

    [Fact]
    public async Task ACopiedFolderLinkWaitsForApprovalAndThenAsksForItsFiles()
    {
        await using var rig = await RemoteRig.CreateAsync(_server);
        var links = await CopiedLinksFor(rig);
        var item = rig.Mock.AddItem(RemoteItemKind.Torrent, "Pack", [MockTorBoxFile.Of(rig.Mock.NextFileId(), "Pack/a.bin", 4 * Kb, seed: 802)]);
        var link = $"https://torbox.app/download?id={item.Id}&type=torrents";

        var outcome = await links.AddAsync(link);

        Assert.Equal(WaitingTitle, outcome.Title);
        AssertSaysNothingOfTheAddress(outcome, link);
        var job = Assert.Single(rig.Jobs.List());
        Assert.Equal(RemoteJobState.Intended, job.State);
        Assert.True(job.HeldForApproval);
        Assert.Equal(item.Id.ToString(), job.ItemId);
        await rig.AdvanceAsync(120);
        Assert.Equal(0, rig.Mock.Calls(TorrentList));
        Assert.Empty(rig.Children());

        Assert.True((await rig.Jobs.ApproveUncachedAsync(job.Id)).Ok);
        Assert.Equal(RemoteJobState.Waiting, rig.View(job.Id).State);
        await rig.RunAsync();
        Assert.Equal(RemoteJobState.AwaitingSelection, rig.View(job.Id).State);
        Assert.Empty(rig.Children());
    }

    [Fact]
    public async Task ACopiedOrdinaryLinkIsAddedDirectlyAsItAlwaysWas()
    {
        await using var rig = await RemoteRig.CreateAsync(_server);
        var links = await CopiedLinksFor(rig);

        var outcome = await links.AddAsync($"{_server.BaseAddress}/file/copied-ordinary.bin?size=2048&seed=803");

        Assert.Equal(DirectTitle, outcome.Title);
        Assert.Equal("copied-ordinary.bin", outcome.Words);
        Assert.Empty(rig.Jobs.List());
        var job = Assert.Single(rig.Ctx.Service.GetSnapshot().Jobs);
        Assert.Equal("clipboardMonitor", job.Source);
        Assert.Equal("completed", (await rig.Ctx.WaitFor(job.Id, candidate => candidate.State is "completed" or "failed")).State);
    }

    [Fact]
    public async Task ACopiedDirectLinkIsAddedDirectlyWithItsKeyOutOfEverything()
    {
        await using var rig = await RemoteRig.CreateAsync(_server);
        var links = await CopiedLinksFor(rig);

        var outcome = await links.AddAsync($"{_server.BaseAddress}/file/copied-direct.bin?size=2048&seed=804&sig={MockKey}");

        Assert.Equal(DirectTitle, outcome.Title);
        Assert.DoesNotContain(MockKey, outcome.Words);
        Assert.Empty(rig.Jobs.List());
        var job = Assert.Single(rig.Ctx.Service.GetSnapshot().Jobs);
        Assert.DoesNotContain(MockKey, job.Url);
        Assert.Equal("completed", (await rig.Ctx.WaitFor(job.Id, candidate => candidate.State is "completed" or "failed")).State);
        await rig.AssertNothingLeakedAsync();
    }

    [Fact]
    public async Task ACopiedLinkNobodyCanClassifyIsNotAddedAndTheTraySaysToUseAddLinks()
    {
        await using var rig = await RemoteRig.CreateAsync(_server);
        var links = await CopiedLinksFor(rig);
        rig.Mock.Fail(HosterList, MockFault.Status5xx());
        var link = rig.HosterLink("copied-outage");

        var outcome = await links.AddAsync(link);

        Assert.Equal(NotAddedTitle, outcome.Title);
        Assert.Contains("Add Links", outcome.Words);
        Assert.Contains("TorBox", outcome.Words);
        Assert.NotNull(outcome.Problem);
        AssertSaysNothingOfTheAddress(outcome, link);
        Assert.Empty(rig.Jobs.List());
        Assert.Empty(rig.Ctx.Service.GetSnapshot().Jobs);
        Assert.Equal(0, rig.Mock.HosterRequests);
    }

    [Fact]
    public async Task ACopiedLinkWhoseCheckFailsIsNotAddedAndTheLogSaysNoAddress()
    {
        await using var ctx = await ServiceContext.CreateAsync(_server);
        var registry = new RemoteProviderRegistry();
        registry.Add(new BrokenClassifierProvider());
        await using var jobs = new RemoteJobCoordinator(registry, new DownloadManager.Persistence.JsonRemoteJobStore(ctx.Paths), ctx.Service, new RemoteCoordinatorOptions { RunLoops = false });
        await jobs.StartAsync();
        var link = $"{_server.BaseAddress}/file/copied-broken.bin?size=1024&seed=805";

        var outcome = await new CopiedLinks(ctx.Service, jobs).AddAsync(link);

        Assert.Equal(NotAddedTitle, outcome.Title);
        Assert.Contains("Add Links", outcome.Words);
        Assert.Contains("Pretend", outcome.Words);
        AssertSaysNothingOfTheAddress(outcome, link);
        Assert.Empty(ctx.Service.GetSnapshot().Jobs);
    }

    [Fact]
    public async Task ACopiedLinkOfNoUseIsNotAddedDirectly()
    {
        await using var rig = await RemoteRig.CreateAsync(_server);
        var links = await CopiedLinksFor(rig);
        var link = $"https://cdn1.tb-cdn.test/dl/1?token={UrlRedactor.Mask}";

        var outcome = await links.AddAsync(link);

        Assert.Equal(NotAddedTitle, outcome.Title);
        Assert.Contains("Add Links", outcome.Words);
        Assert.Empty(rig.Ctx.Service.GetSnapshot().Jobs);
    }

    [Fact]
    public async Task WithNoKeyACopiedHosterLinkIsAddedDirectlyAndNothingAsksTheProvider()
    {
        await using var rig = await RemoteRig.CreateAsync(_server, key: null);
        var links = await CopiedLinksFor(rig);
        var link = rig.Mock.HosterUrl("file/copied-no-key.bin");

        var outcome = await links.AddAsync(link);

        Assert.Equal(DirectTitle, outcome.Title);
        Assert.Null(outcome.Problem);
        Assert.Empty(rig.Jobs.List());
        Assert.Equal("clipboardMonitor", Assert.Single(rig.Ctx.Service.GetSnapshot().Jobs).Source);
        Assert.Equal(0, rig.Mock.Calls(HosterList));
        Assert.Equal(0, rig.Mock.Calls("GET user/me"));
        Assert.Equal(UrlRedactor.Redact(link), links.DescribeForLog(link));
    }

    [Fact]
    public async Task WithAKeyTheLogSaysOnlyWhichSiteWasCopied()
    {
        await using var rig = await RemoteRig.CreateAsync(_server);
        var links = await CopiedLinksFor(rig);
        var link = rig.HosterLink("copied-log");

        var described = links.DescribeForLog(link);

        Assert.Equal(new Uri(link).GetLeftPart(UriPartial.Scheme) + new Uri(link).Host, described);
        Assert.DoesNotContain("copied-log", described);
    }

    [Fact]
    public async Task ALinkCopiedTwiceAtOnceBecomesOneJobAndOneNotice()
    {
        await using var rig = await RemoteRig.CreateAsync(_server);
        var links = await CopiedLinksFor(rig);
        var (link, _, _) = rig.ScriptHoster("copied-twice", MockTorBoxState.Ready);

        var outcomes = await Task.WhenAll(links.AddAsync(link), links.AddAsync(link), links.AddAsync(link));

        Assert.Single(outcomes, outcome => outcome.Title == WaitingTitle);
        Assert.Equal(2, outcomes.Count(outcome => outcome.Title is null && outcome.Problem is null));
        var job = Assert.Single(rig.Jobs.List());

        // Copied again once it is under way, it stays as it is.
        Assert.True((await rig.Jobs.ApproveUncachedAsync(job.Id)).Ok);
        await rig.RunAsync();
        var again = await links.AddAsync(link);
        Assert.Null(again.Title);
        Assert.False(Assert.Single(rig.Jobs.List()).HeldForApproval);
    }

    [Fact]
    public async Task AddingAHeldLinkOnPurposeReleasesItWithoutApprovingWhatIsNotCached()
    {
        await using var rig = await RemoteRig.CreateAsync(_server);
        var links = await CopiedLinksFor(rig);
        var (link, _, _) = rig.ScriptHoster("copied-then-added", MockTorBoxState.Ready);
        await links.AddAsync(link);
        var held = Assert.Single(rig.Jobs.List());

        var added = await rig.Jobs.AddAsync(new RemoteAddRequest { Input = link });

        Assert.True(added.Existing);
        Assert.Equal(held.Id, added.Job!.Id);
        Assert.False(added.Job.HeldForApproval);
        Assert.False(added.Job.ApprovedUncached);
        await rig.RunAsync();
        var asked = rig.View(held.Id);
        Assert.True(asked.ApprovalNeeded);
        Assert.False(asked.HeldForApproval);
        Assert.Equal(0, rig.Mock.Calls(CreateWeb));
        Assert.Contains("Not cached", asked.Status);
    }

    // ---- Refreshing an address -----------------------------------------------------------------

    [Fact]
    public async Task RefreshRefusesAHosterLinkEvenWithTheDirectFlag()
    {
        await using var rig = await RemoteRig.CreateAsync(_server);
        var job = await StoppedJob(rig);
        var link = rig.HosterLink("refresh-hoster");

        foreach (var explicitDirect in new[] { false, true })
        {
            var refused = await rig.Ctx.Service.RefreshUrlAsync(job.Id, link, explicitDirect);
            Assert.False(refused.Ok);
            Assert.Contains("Add Links", refused.Error);
            Assert.Contains("TorBox", refused.Error);
            Assert.DoesNotContain("http", refused.Error, StringComparison.OrdinalIgnoreCase);
        }

        Assert.Equal(job.Url, rig.Ctx.Service.GetJob(job.Id)!.Url);
        Assert.Equal(0, rig.Mock.HosterRequests);
    }

    [Fact]
    public async Task RefreshRefusesAFolderLink()
    {
        await using var rig = await RemoteRig.CreateAsync(_server);
        var job = await StoppedJob(rig);

        var refused = await rig.Ctx.Service.RefreshUrlAsync(job.Id, "https://torbox.app/download?id=5&type=torrents", explicitDirect: true);

        Assert.False(refused.Ok);
        Assert.Contains("Add Links", refused.Error);
        Assert.Equal(job.Url, rig.Ctx.Service.GetJob(job.Id)!.Url);
    }

    [Fact]
    public async Task RefreshTakesAnOrdinaryAddress()
    {
        await using var rig = await RemoteRig.CreateAsync(_server);
        var job = await StoppedJob(rig);
        var address = $"{_server.BaseAddress}/file/refresh-ordinary.bin?size=2048&seed=806";

        var refreshed = await rig.Ctx.Service.RefreshUrlAsync(job.Id, address);

        Assert.True(refreshed.Ok, refreshed.Error);
        Assert.Equal(address, refreshed.Job!.Url);
    }

    [Fact]
    public async Task RefreshKeepsTheAddressAJobAlreadyHasWithoutAskingAnything()
    {
        await using var rig = await RemoteRig.CreateAsync(_server);
        // Added directly before there was a key, say: a retry on it must not be stranded.
        var link = rig.HosterLink("refresh-same");
        var job = await StoppedJob(rig, link);
        var asked = rig.Mock.Calls(HosterList);
        rig.Mock.Fail(HosterList, MockFault.Status5xx());

        var refreshed = await rig.Ctx.Service.RefreshUrlAsync(job.Id, job.Url);

        Assert.True(refreshed.Ok, refreshed.Error);
        Assert.Equal(asked, rig.Mock.Calls(HosterList));
    }

    [Fact]
    public async Task RefreshTakesAnFtpAddressWithoutAskingAnything()
    {
        await using var rig = await RemoteRig.CreateAsync(_server);
        var job = await StoppedJob(rig);

        var refreshed = await rig.Ctx.Service.RefreshUrlAsync(job.Id, "ftp://127.0.0.1:9/nothing-here.bin");

        Assert.True(refreshed.Ok, refreshed.Error);
        Assert.Equal(0, rig.Mock.Calls(HosterList));
    }

    [Fact]
    public async Task RefreshTakesAnUndeterminedAddressOnlyWithTheDirectFlag()
    {
        await using var rig = await RemoteRig.CreateAsync(_server);
        var job = await StoppedJob(rig);
        rig.Mock.Fail(HosterList, MockFault.Status5xx());
        var address = $"{_server.BaseAddress}/file/refresh-unknown.bin?size=2048&seed=807";

        var refused = await rig.Ctx.Service.RefreshUrlAsync(job.Id, address);
        Assert.False(refused.Ok);
        Assert.Contains("direct request", refused.Error);
        Assert.Equal(job.Url, rig.Ctx.Service.GetJob(job.Id)!.Url);

        var taken = await rig.Ctx.Service.RefreshUrlAsync(job.Id, address, explicitDirect: true);
        Assert.True(taken.Ok, taken.Error);
        Assert.Equal(address, taken.Job!.Url);
    }

    [Fact]
    public async Task RefreshTreatsACheckThatFailedAsNobodyKnowing()
    {
        await using var ctx = await ServiceContext.CreateAsync(_server);
        var registry = new RemoteProviderRegistry();
        registry.Add(new BrokenClassifierProvider());
        await using var jobs = new RemoteJobCoordinator(registry, new DownloadManager.Persistence.JsonRemoteJobStore(ctx.Paths), ctx.Service, new RemoteCoordinatorOptions { RunLoops = false });
        ctx.Service.DirectAddresses = jobs;
        var added = await ctx.Service.AddJobAsync(new AddJobRequest
        {
            Url = $"{_server.BaseAddress}/file/broken-check.bin?size=1024&seed=808",
            FileName = "broken-check.bin",
            DestinationDirectory = ctx.Destination,
            StartMode = "later",
            Source = "cli",
        });
        var address = $"{_server.BaseAddress}/file/broken-check-2.bin?size=1024&seed=809";

        Assert.False((await ctx.Service.RefreshUrlAsync(added.Job!.Id, address)).Ok);
        Assert.True((await ctx.Service.RefreshUrlAsync(added.Job.Id, address, explicitDirect: true)).Ok);
    }

    [Fact]
    public async Task WithNoKeyRefreshTakesAHosterLinkAndNothingAsksTheProvider()
    {
        await using var rig = await RemoteRig.CreateAsync(_server, key: null);
        var job = await StoppedJob(rig);

        var refreshed = await rig.Ctx.Service.RefreshUrlAsync(job.Id, rig.Mock.HosterUrl("file/refresh-no-key.bin"));

        Assert.True(refreshed.Ok, refreshed.Error);
        Assert.Equal(0, rig.Mock.Calls(HosterList));
    }

    // ---- A file name from the server -----------------------------------------------------------

    [Fact]
    public async Task ANameTakenFromAKeyInTheAddressIsNotGivenOut()
    {
        await using var rig = await RemoteRig.CreateAsync(_server);

        var read = await rig.Ctx.Service.ReadFileMetadataAsync(new FileMetadataRequest { Url = $"{_server.BaseAddress}/file/{MockKey}?size=1024&seed=810" });

        Assert.True(read.Ok, read.Error);
        Assert.Null(read.FileName);
        Assert.Equal(1024, read.TotalBytes);
    }

    [Fact]
    public async Task ANameTakenFromAKeyInTheAddressAfterARedirectIsNotGivenOut()
    {
        await using var rig = await RemoteRig.CreateAsync(_server);
        var target = Uri.EscapeDataString($"/file/{MockKey}?size=1024&seed=811");

        var read = await rig.Ctx.Service.ReadFileMetadataAsync(new FileMetadataRequest { Url = $"{_server.BaseAddress}/redirect?to={target}" });

        Assert.True(read.Ok, read.Error);
        Assert.Null(read.FileName);
        Assert.DoesNotContain(MockKey, read.FinalUrl);
    }

    [Fact]
    public async Task ANameTheServerSendsHoldingTheKeyIsNotGivenOut()
    {
        await using var rig = await RemoteRig.CreateAsync(_server);
        var disposition = Uri.EscapeDataString($"attachment; filename=\"{MockKey}.zip\"");

        var read = await rig.Ctx.Service.ReadFileMetadataAsync(new FileMetadataRequest { Url = $"{_server.BaseAddress}/filename/a?disposition={disposition}" });

        Assert.True(read.Ok, read.Error);
        Assert.Null(read.FileName);
    }

    [Fact]
    public async Task AKeyCleaningWouldDisguiseIsStillNotGivenOut()
    {
        // Cleaning turns the colon into something else, so the name alone no longer reads as this key.
        const string key = "abcd:efgh-ijkl-mnop-qrst";
        await using var ctx = await ServiceContext.CreateAsync(_server, linkSecrets: new OneKey(key));

        var read = await ctx.Service.ReadFileMetadataAsync(new FileMetadataRequest { Url = $"{_server.BaseAddress}/file/{key}?size=1024&seed=812" });

        Assert.True(read.Ok, read.Error);
        Assert.Null(read.FileName);
    }

    [Fact]
    public async Task AProviderLinkRepeatingAnUnknownTokenAsItsNameDoesNotGiveItOut()
    {
        await using var ctx = await ServiceContext.CreateAsync(_server, handler: new CdnRedirectHandler(_server));

        var read = await ctx.Service.ReadFileMetadataAsync(new FileMetadataRequest { Url = $"{PastedHost}/file/{LinkSecret}?size=1024&seed=813&token={LinkSecret}" });

        Assert.True(read.Ok, read.Error);
        Assert.Null(read.FileName);
    }

    [Fact]
    public async Task ARealNameIsStillGiven()
    {
        await using var rig = await RemoteRig.CreateAsync(_server);

        var fromAddress = await rig.Ctx.Service.ReadFileMetadataAsync(new FileMetadataRequest { Url = $"{_server.BaseAddress}/file/movie.bin?size=1024&seed=814" });
        Assert.Equal("movie.bin", fromAddress.FileName);

        // A key in the query says nothing about the name in the path.
        var withKey = await rig.Ctx.Service.ReadFileMetadataAsync(new FileMetadataRequest { Url = $"{_server.BaseAddress}/file/movie.bin?size=1024&seed=814&sig={MockKey}" });
        Assert.Equal("movie.bin", withKey.FileName);

        var disposition = Uri.EscapeDataString("attachment; filename=\"real.zip\"");
        var sent = await rig.Ctx.Service.ReadFileMetadataAsync(new FileMetadataRequest { Url = $"{_server.BaseAddress}/filename/a?disposition={disposition}" });
        Assert.Equal("real.zip", sent.FileName);
    }

    [Fact]
    public async Task AnAddWhoseRedirectEndsAtAKeyDoesNotNameTheFileAfterIt()
    {
        await using var rig = await RemoteRig.CreateAsync(_server);
        var target = Uri.EscapeDataString($"/file/{MockKey}?size=1024&seed=815");

        var added = await rig.Ctx.Service.AddJobAsync(new AddJobRequest
        {
            Url = $"{_server.BaseAddress}/redirect?to={target}",
            DestinationDirectory = rig.Destination,
            StartMode = "later",
            Source = "cli",
        });

        Assert.True(added.Accepted, added.Error);
        Assert.DoesNotContain(MockKey, added.Job!.FileName);
        Assert.DoesNotContain(MockKey, added.Job.FinalFilePath ?? string.Empty);
    }

    [Fact]
    public async Task AnAddNamedFromAKeyCleaningWouldDisguiseIsCalledDownload()
    {
        const string key = "abcd:efgh-ijkl-mnop-qrst";
        await using var ctx = await ServiceContext.CreateAsync(_server, linkSecrets: new OneKey(key));

        var added = await ctx.Service.AddJobAsync(new AddJobRequest
        {
            Url = $"{_server.BaseAddress}/file/{key}?size=1024&seed=816",
            DestinationDirectory = ctx.Destination,
            StartMode = "later",
            Source = "cli",
        });

        Assert.True(added.Accepted, added.Error);
        Assert.Equal("download", added.Job!.FileName);
    }

    private sealed class OneKey(string key) : IRemoteSecretMatcher
    {
        private readonly RemoteKeyPattern _pattern = new(key);

        public bool Holds(string? text) => _pattern.Holds(text);

        public string Mask(string text) => _pattern.Mask(text);
    }

    /// <summary>An account with no key to go wrong, whose every look at a link throws.</summary>
    private sealed class BrokenClassifierProvider : IRemoteProvider, IRemoteFolderLinkParser
    {
        public string Id => "pretend";
        public string DisplayName => "Pretend";
        public RemoteCapabilities Capabilities => RemoteCapabilities.WebDownloads;
        public RemoteTransferPolicy TransferPolicy => new();

        public Task<RemoteAccountInfo> GetAccountAsync(CancellationToken cancellationToken = default) => throw new NotSupportedException();

        public RemoteFolderLinkParse? ParseFolderLink(string? text) => throw new InvalidOperationException("Broken on purpose.");
    }
}

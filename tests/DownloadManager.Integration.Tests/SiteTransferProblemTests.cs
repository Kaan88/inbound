using DownloadManager.Contracts;
using DownloadManager.Runtime.Sites;
using DownloadManager.Sites;

namespace DownloadManager.Integration.Tests;

/// <summary>
/// SITES.10 (6): a batch stopped because the remote no longer accepts the sign-in, or its certificate is not trusted,
/// carries the problem browsing gives the same failure, so a client offers Sign in again or Trust on the batch as it does
/// on a listing. The rclone seam hands the batch the error rclone reports for each, so nothing here needs rclone or a
/// network: a batch that could not sign in stops on the first file either way.
/// </summary>
public sealed class SiteTransferProblemTests : IAsyncLifetime
{
    private readonly string _root = Path.Combine(AppContext.BaseDirectory, "test-work", "site-transfer-problem-" + Guid.NewGuid().ToString("N"));
    private readonly List<SiteTransfers> _open = [];

    private string Inbox => Path.Combine(_root, "inbox");

    private string State => Path.Combine(_root, "state");

    public async Task InitializeAsync()
    {
        Directory.CreateDirectory(Inbox);
        await File.WriteAllTextAsync(Path.Combine(Inbox, "a.txt"), "a");
    }

    public async Task DisposeAsync()
    {
        foreach (var transfers in _open) await transfers.DisposeAsync();
        try { Directory.Delete(_root, recursive: true); }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { /* best effort */ }
    }

    // The words rclone gives for each failure (SiteErrorText reads these), wrapped as RcloneCallException writes them.
    private const string SignInRefused = "couldn't fetch token: oauth2: \"invalid_grant\" \"Token has been expired or revoked.\"";
    private const string CertificateUntrusted = "Post \"https://box.example/upload\": tls: failed to verify certificate: x509: certificate signed by unknown authority";

    [Theory]
    [InlineData(SignInRefused, SiteProblems.SignInNeeded)]
    [InlineData(CertificateUntrusted, SiteProblems.CertificateNotTrusted)]
    public async Task AStoppedBatchCarriesTheProblemBrowsingGivesTheSameFailure(string failure, string expected)
    {
        var transfers = Open(failure);
        var site = Site();
        var started = await transfers.StartAsync(Upload(site.Id), CancellationToken.None);
        Assert.True(started.Ok, started.Message);

        var stopped = await WaitAsync(transfers, started.Transfer!.Id, t => SiteTransferStates.IsFinished(t.State));
        Assert.Equal(SiteTransferStates.Failed, stopped.State);
        // The code a listing carries for the same refusal, and the code the clients' Sign in / Trust is offered for.
        Assert.Equal(expected, stopped.Problem);
        // The file it could not move is still waiting, not failed: Retry goes on with it.
        Assert.Equal(0, stopped.FilesFailed);

        // Retry after the fix clears the code, so the row's button goes with the failure it was for.
        var retried = await transfers.RetryAsync(stopped.Id);
        Assert.True(retried.Ok, retried.Message);
        Assert.Null(retried.Transfer!.Problem);

        // A failure that is only this batch's (a file that is gone) keeps no code: there is nothing to sign in to or trust.
        Assert.Null(SiteTransfers.BatchProblem("rclone operations/copyfile failed (404): object not found"));
    }

    [Fact]
    public async Task AConnectionFailureStopsTheBatchWithoutSignInOrTrust()
    {
        var transfers = Open("dial tcp 10.0.0.9:22: i/o timeout");
        var started = await transfers.StartAsync(Upload(Site().Id), CancellationToken.None);
        Assert.True(started.Ok, started.Message);

        // A lost connection waits for the server rather than stopping, and says nothing of a sign-in or a certificate.
        var waiting = await WaitAsync(transfers, started.Transfer!.Id, t => t.FilesRunning == 0 && t.RetryReason is not null);
        Assert.Equal(SiteTransferStates.Running, waiting.State);
        Assert.Null(waiting.Problem);
    }

    private SiteTransfers Open(string failure)
    {
        var transfers = new SiteTransfers(State,
            getSite: (id, _) => Task.FromResult<SiteRecord?>(id == _site?.Id ? _site : null),
            acquire: _ => Task.FromException<RcloneLease>(new RcloneCallException("operations/copyfile", 500, failure)),
            enabled: () => true,
            limit: () => 2,
            log: _ => { },
            firstRetryDelay: TimeSpan.FromMilliseconds(50));
        _open.Add(transfers);
        return transfers;
    }

    private SiteRecord? _site;

    private SiteRecord Site()
    {
        _site = new SiteRecord
        {
            Id = SiteStore.NewId(),
            Name = "Box",
            Kind = SiteKinds.Ftp,
            Host = "box.example",
            Port = 21,
            User = "me",
            CreatedAt = DateTimeOffset.UtcNow,
            ModifiedAt = DateTimeOffset.UtcNow,
        };
        return _site;
    }

    private SiteTransferStartRequest Upload(string siteId) => new()
    {
        SiteId = siteId,
        Direction = SiteTransferDirections.Upload,
        RemoteFolder = "/pub",
        LocalFolder = Inbox,
        Names = [new SiteTransferNameDto { Name = "a.txt" }],
    };

    private static async Task<SiteTransferDto> WaitAsync(SiteTransfers transfers, string id, Func<SiteTransferDto, bool> condition)
    {
        var until = DateTime.UtcNow + TimeSpan.FromSeconds(20);
        SiteTransferDto? last = null;
        while (DateTime.UtcNow < until)
        {
            last = transfers.List().Transfers.SingleOrDefault(t => t.Id == id);
            if (last is not null && condition(last)) return last;
            await Task.Delay(20);
        }

        throw new TimeoutException($"The batch stayed {last?.State}, problem {last?.Problem ?? "none"}, message {last?.Message}.");
    }
}

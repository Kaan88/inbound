using DownloadManager.Core;
using DownloadManager.Persistence;
using TestServer;

namespace DownloadManager.Engine.Tests;

/// <summary>
/// Audit 1, M7 and T-8: FTP resume starts from the flushed checkpoint, notices a replaced file, gives up on a
/// stalled data connection, survives a refused REST and an existing final file, and calls a short transfer retryable.
/// </summary>
[Collection("http-server")]
public sealed class FtpResumeTests(TestServerFixture fixture) : IDisposable
{
    private readonly FtpServer _ftp = fixture.Server.Ftp;
    private readonly string _root = Path.Combine(AppContext.BaseDirectory, "test-work", "ftp-resume-" + Guid.NewGuid().ToString("N"));
    private readonly List<string> _remoteFiles = [];

    public void Dispose()
    {
        _ftp.RestReply = null;
        _ftp.RetrReply = null;
        foreach (var file in _remoteFiles)
        {
            try { File.Delete(file); }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
        }

        try { Directory.Delete(_root, recursive: true); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
    }

    private (string Name, byte[] Content) Remote(int size, int seed)
    {
        var name = $"resume-{Guid.NewGuid():N}.bin";
        var content = TestContent.Generate(seed, size);
        var path = Path.Combine(_ftp.Root, name);
        File.WriteAllBytes(path, content);
        _remoteFiles.Add(path);
        return (name, content);
    }

    private DownloadJob Job(string remoteName, string? fileName = null)
    {
        var destination = Path.Combine(_root, "downloads");
        Directory.CreateDirectory(destination);
        fileName ??= remoteName;
        return new DownloadJob
        {
            Url = $"ftp://ftpuser:ftppass@127.0.0.1:{_ftp.Port}/{remoteName}",
            FileName = fileName,
            DestinationDirectory = destination,
            FinalFilePath = Path.Combine(destination, fileName),
            PartFilePath = Path.Combine(destination, fileName) + ".part",
            RequestedConnections = 1,
            State = DownloadState.Queued,
        };
    }

    private JsonCheckpointStore Store()
    {
        var paths = new StoragePaths(Path.Combine(_root, "state"));
        paths.EnsureCreated();
        return new JsonCheckpointStore(paths);
    }

    private static string Sha(byte[] bytes)
        => Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(bytes)).ToLowerInvariant();

    [Fact]
    public async Task ACutTransferIsRetryableAndResumesFromItsCheckpoint()
    {
        var (name, content) = Remote(300_000, 801);
        var store = Store();
        var engine = new FtpDownloadEngine(checkpoints: store);
        var job = Job(name);
        _ftp.CutNextTransfer(name, 120_000);

        var first = await engine.ExecuteAsync(job, null, null, CancellationToken.None);

        Assert.False(first.Success);
        Assert.True(first.Failure!.IsRetryable, first.Failure.Message);
        var checkpoint = await store.LoadAsync(job.Id);
        Assert.NotNull(checkpoint);
        Assert.Equal(120_000, checkpoint!.CompletedBytes);
        Assert.Equal(content.Length, checkpoint.TotalBytes);
        // The resume below runs with the file's MDTM time on record, and it matches: the time check lets it continue.
        Assert.False(string.IsNullOrWhiteSpace(checkpoint.LastModified));

        var second = await engine.ExecuteAsync(job, checkpoint, null, CancellationToken.None);

        Assert.True(second.Success, second.Failure?.Message);
        Assert.Equal(Sha(content), await Hashing.Sha256FileAsync(second.FinalFilePath!));
        var transfers = _ftp.Transfers(name);
        Assert.Equal(2, transfers.Count);
        // A resume, not a re-download: REST at the checkpoint, and only the rest of the file sent.
        Assert.Equal(120_000, transfers[1].Offset);
        Assert.Equal(content.Length - 120_000, transfers[1].Sent);
        Assert.Null(await store.LoadAsync(job.Id));
    }

    [Fact]
    public async Task ResumeTrustsTheCheckpointNotThePartLength()
    {
        // After a power cut the part can be longer than what was flushed, and its tail reads as zeros.
        var (name, content) = Remote(200_000, 802);
        var store = Store();
        var engine = new FtpDownloadEngine(checkpoints: store);
        var job = Job(name);
        var part = new byte[150_000];
        Array.Copy(content, part, 100_000);
        await File.WriteAllBytesAsync(job.PartFilePath, part);
        var checkpoint = new DownloadCheckpoint { JobId = job.Id, Url = job.Url, TotalBytes = content.Length };
        checkpoint.Segments.Add(new SegmentCheckpoint { Start = 0, End = content.Length - 1, Completed = 100_000 });

        var result = await engine.ExecuteAsync(job, checkpoint, null, CancellationToken.None);

        Assert.True(result.Success, result.Failure?.Message);
        Assert.Equal(100_000, _ftp.Transfers(name).Single().Offset);
        Assert.Equal(Sha(content), await Hashing.Sha256FileAsync(result.FinalFilePath!));
    }

    [Fact]
    public async Task AReplacedRemoteFileStartsOver()
    {
        var (name, content) = Remote(200_000, 803);
        var engine = new FtpDownloadEngine(checkpoints: Store());
        var job = Job(name);
        await File.WriteAllBytesAsync(job.PartFilePath, content.AsSpan(0, 50_000).ToArray());
        // The part came from a 180,000-byte file; the server now has another one.
        var checkpoint = new DownloadCheckpoint { JobId = job.Id, Url = job.Url, TotalBytes = 180_000 };
        checkpoint.Segments.Add(new SegmentCheckpoint { Start = 0, End = 179_999, Completed = 50_000 });

        var result = await engine.ExecuteAsync(job, checkpoint, null, CancellationToken.None);

        Assert.True(result.Success, result.Failure?.Message);
        Assert.Equal(0, _ftp.Transfers(name).Single().Offset);
        Assert.Equal(Sha(content), await Hashing.Sha256FileAsync(result.FinalFilePath!));
    }

    [Fact]
    public async Task ARemoteFileReplacedByOneOfTheSameSizeStartsOver()
    {
        var (name, original) = Remote(200_000, 807);
        var store = Store();
        var engine = new FtpDownloadEngine(checkpoints: store);
        var job = Job(name);
        _ftp.CutNextTransfer(name, 80_000);
        var first = await engine.ExecuteAsync(job, null, null, CancellationToken.None);
        Assert.False(first.Success);
        var checkpoint = await store.LoadAsync(job.Id);
        Assert.Equal(80_000, checkpoint!.CompletedBytes);
        Assert.False(string.IsNullOrWhiteSpace(checkpoint.LastModified));

        // Same name and size, other bytes and another modification time: only MDTM can tell it apart.
        var replacement = TestContent.Generate(808, original.Length);
        var remotePath = Path.Combine(_ftp.Root, name);
        await File.WriteAllBytesAsync(remotePath, replacement);
        File.SetLastWriteTimeUtc(remotePath, DateTime.UtcNow.AddHours(-3));

        var second = await engine.ExecuteAsync(job, checkpoint, null, CancellationToken.None);

        Assert.True(second.Success, second.Failure?.Message);
        Assert.Equal(0, _ftp.Transfers(name)[^1].Offset);
        Assert.Equal(Sha(replacement), await Hashing.Sha256FileAsync(second.FinalFilePath!));
    }

    [Fact]
    public async Task AStalledDataConnectionTimesOutAndKeepsItsBytes()
    {
        var (name, content) = Remote(300_000, 804);
        var store = Store();
        var engine = new FtpDownloadEngine(checkpoints: store, idleTimeout: TimeSpan.FromSeconds(1));
        var job = Job(name);
        _ftp.StallNextTransfer(name, 100_000);

        var stopwatch = System.Diagnostics.Stopwatch.StartNew();
        var first = await engine.ExecuteAsync(job, null, null, CancellationToken.None);
        stopwatch.Stop();

        Assert.False(first.Success);
        Assert.Equal(FailureKind.Timeout, first.Failure!.Kind);
        Assert.True(first.Failure.IsRetryable);
        Assert.True(stopwatch.Elapsed < TimeSpan.FromSeconds(20), $"took {stopwatch.Elapsed}");
        var checkpoint = await store.LoadAsync(job.Id);
        Assert.Equal(100_000, checkpoint!.CompletedBytes);

        var second = await engine.ExecuteAsync(job, checkpoint, null, CancellationToken.None);
        Assert.True(second.Success, second.Failure?.Message);
        Assert.Equal(100_000, _ftp.Transfers(name)[^1].Offset);
        Assert.Equal(Sha(content), await Hashing.Sha256FileAsync(second.FinalFilePath!));
    }

    [Fact]
    public async Task ARefusedRestStartsOverOnce()
    {
        var (name, content) = Remote(120_000, 805);
        var engine = new FtpDownloadEngine(checkpoints: Store());
        var job = Job(name);
        await File.WriteAllBytesAsync(job.PartFilePath, content.AsSpan(0, 40_000).ToArray());
        _ftp.RestReply = "502 Command not implemented";

        var result = await engine.ExecuteAsync(job, null, null, CancellationToken.None);

        Assert.True(result.Success, result.Failure?.Message);
        Assert.False(job.ResumeSupported);
        var transfer = _ftp.Transfers(name).Single();
        Assert.Equal(0, transfer.Offset);
        Assert.Equal(content.Length, transfer.Sent);
        Assert.Equal(Sha(content), await Hashing.Sha256FileAsync(result.FinalFilePath!));
    }

    [Theory]
    [InlineData("500 Syntax error, command unrecognized")]
    [InlineData("502 Command not implemented")]
    public async Task ARefusedRestWhoseFromZeroTryFailsKeepsThePart(string restReply)
    {
        var (name, content) = Remote(120_000, 809);
        var store = Store();
        var engine = new FtpDownloadEngine(checkpoints: store);
        var job = Job(name);
        await File.WriteAllBytesAsync(job.PartFilePath, content.AsSpan(0, 40_000).ToArray());
        var checkpoint = new DownloadCheckpoint { JobId = job.Id, Url = job.Url, TotalBytes = content.Length };
        checkpoint.Segments.Add(new SegmentCheckpoint { Start = 0, End = content.Length - 1, Completed = 40_000 });
        await store.SaveAsync(checkpoint);
        // A 5xx can't say which command it answers; here the server is simply failing, so the from-zero RETR fails too.
        _ftp.RestReply = restReply;
        _ftp.RetrReply = "451 Requested action aborted: local error in processing";

        var result = await engine.ExecuteAsync(job, checkpoint, null, CancellationToken.None);

        Assert.False(result.Success);
        Assert.True(result.Failure!.IsRetryable, result.Failure.Message);
        Assert.Equal(40_000, result.BytesWritten);
        Assert.Equal(content.AsSpan(0, 40_000).ToArray(), await File.ReadAllBytesAsync(job.PartFilePath));
        Assert.Equal(40_000, (await store.LoadAsync(job.Id))!.CompletedBytes);
        Assert.Empty(_ftp.Transfers(name));

        // Once the server is well again the bytes are used: REST is taken and only the rest is sent.
        _ftp.RestReply = null;
        _ftp.RetrReply = null;
        var retry = await engine.ExecuteAsync(job, await store.LoadAsync(job.Id), null, CancellationToken.None);
        Assert.True(retry.Success, retry.Failure?.Message);
        Assert.Equal(40_000, _ftp.Transfers(name).Single().Offset);
        Assert.Equal(Sha(content), await Hashing.Sha256FileAsync(retry.FinalFilePath!));
    }

    [Theory]
    [InlineData(ConflictPolicy.Rename)]
    [InlineData(ConflictPolicy.Overwrite)]
    [InlineData(ConflictPolicy.Fail)]
    public async Task AnExistingFinalFileFollowsTheClashPolicy(ConflictPolicy policy)
    {
        var (name, content) = Remote(64_000, 806);
        var engine = new FtpDownloadEngine(checkpoints: Store());
        var job = Job(name);
        job.ConflictPolicy = policy;
        await File.WriteAllTextAsync(job.FinalFilePath, "already here");

        var result = await engine.ExecuteAsync(job, null, null, CancellationToken.None);

        switch (policy)
        {
            case ConflictPolicy.Rename:
                Assert.True(result.Success, result.Failure?.Message);
                Assert.NotEqual(job.FinalFilePath, result.FinalFilePath);
                Assert.Equal("already here", await File.ReadAllTextAsync(job.FinalFilePath));
                Assert.Equal(Sha(content), await Hashing.Sha256FileAsync(result.FinalFilePath!));
                break;
            case ConflictPolicy.Overwrite:
                Assert.True(result.Success, result.Failure?.Message);
                Assert.Equal(Sha(content), await Hashing.Sha256FileAsync(job.FinalFilePath));
                break;
            default:
                Assert.False(result.Success);
                Assert.Equal(FailureKind.DestinationUnavailable, result.Failure!.Kind);
                Assert.False(result.Failure.IsRetryable);
                Assert.True(File.Exists(job.PartFilePath), "the downloaded bytes are kept");
                break;
        }
    }
}

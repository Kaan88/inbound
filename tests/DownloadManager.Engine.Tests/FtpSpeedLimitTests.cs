using System.Diagnostics;
using DownloadManager.Core;
using TestServer;

namespace DownloadManager.Engine.Tests;

/// <summary>PERF.6: FTP downloads honour their own speed limit and the cap shared with HTTP downloads.</summary>
[Collection("http-server")]
public sealed class FtpSpeedLimitTests(TestServerFixture fixture) : IDisposable
{
    private const int AuthBytes = 131_072;
    private readonly TestServerHost _server = fixture.Server;
    private readonly string _root = Path.Combine(AppContext.BaseDirectory, "test-work", "ftp-speed-" + Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
    }

    private DownloadJob Job(string url, string fileName)
    {
        Directory.CreateDirectory(_root);
        return new DownloadJob
        {
            Url = url,
            FileName = fileName,
            DestinationDirectory = _root,
            FinalFilePath = Path.Combine(_root, fileName),
            PartFilePath = Path.Combine(_root, fileName) + ".part",
            RequestedConnections = 1,
            State = DownloadState.Queued,
        };
    }

    private string FtpUrl(string name) => $"ftp://ftpuser:ftppass@127.0.0.1:{_server.Ftp.Port}/{name}";

    private string Expected(string name) => Convert.ToHexString(
        System.Security.Cryptography.SHA256.HashData(File.ReadAllBytes(Path.Combine(_server.Ftp.Root, name)))).ToLowerInvariant();

    [Fact]
    public async Task AnFtpDownloadKeepsToItsOwnSpeedLimit()
    {
        var engine = new FtpDownloadEngine();
        var job = Job(FtpUrl("auth.bin"), "ftp-own-limit.bin");
        // 128 KB at 32 KB/s is about 4 s. Uncapped, loopback takes a few milliseconds.
        job.SpeedLimitBytesPerSecond = 32_768;

        var stopwatch = Stopwatch.StartNew();
        var result = await engine.ExecuteAsync(job, null, null, CancellationToken.None);
        stopwatch.Stop();

        Assert.True(result.Success, result.Failure?.Message);
        Assert.InRange(stopwatch.Elapsed.TotalSeconds, 3.0, 12.0);
        Assert.Equal(Expected("auth.bin"), await Hashing.Sha256FileAsync(result.FinalFilePath!));
    }

    [Fact]
    public async Task OneSharedCapCoversAnHttpAndAnFtpDownloadTogether()
    {
        using var http = new HttpDownloadEngine(new EngineOptions { AllowLocalNetwork = true, MaxAttempts = 1 });
        var ftp = new FtpDownloadEngine(http.SpeedBudget);
        // Set through the FTP engine, seen by the HTTP one: the same budget.
        ftp.UpdateSharedSpeedLimit(65_536);
        Assert.Equal(65_536, http.SharedSpeedLimit);

        var httpJob = Job($"{_server.BaseAddress}/file/ftp-shared.bin?size={AuthBytes}&seed=41", "http-shared.bin");
        var ftpJob = Job(FtpUrl("auth.bin"), "ftp-shared.bin");
        // 256 KB in all at 64 KB/s together is about 4 s; a cap for each engine would take about 2 s.
        var stopwatch = Stopwatch.StartNew();
        var results = await Task.WhenAll(
            http.ExecuteAsync(httpJob, null, null, CancellationToken.None),
            ftp.ExecuteAsync(ftpJob, null, null, CancellationToken.None));
        stopwatch.Stop();

        Assert.All(results, result => Assert.True(result.Success, result.Failure?.Message));
        Assert.InRange(stopwatch.Elapsed.TotalSeconds, 3.2, 12.0);
        Assert.Equal(Expected("auth.bin"), await Hashing.Sha256FileAsync(results[1].FinalFilePath!));
    }

    [Fact]
    public async Task LiftingTheSharedCapLetsARunningFtpDownloadFinish()
    {
        var ftp = new FtpDownloadEngine();
        // At 8 KB/s this would take 16 s; lifting the cap after half a second lets it finish at once.
        ftp.UpdateSharedSpeedLimit(8_192);
        var job = Job(FtpUrl("auth.bin"), "ftp-lifted.bin");
        var run = ftp.ExecuteAsync(job, null, null, CancellationToken.None);
        await Task.Delay(500);
        Assert.False(run.IsCompleted);
        ftp.UpdateSharedSpeedLimit(null);

        var result = await run.WaitAsync(TimeSpan.FromSeconds(8));
        Assert.True(result.Success, result.Failure?.Message);
        Assert.Equal(Expected("auth.bin"), await Hashing.Sha256FileAsync(result.FinalFilePath!));
    }
}

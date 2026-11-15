using DownloadManager.Contracts;
using DownloadManager.Runtime;

namespace DownloadManager.Integration.Tests;

/// <summary>L3: a finished download is checked by Windows (the antivirus and the internet mark) before anything uses it.</summary>
public sealed partial class RuntimeServiceTests
{
    [Fact]
    public async Task AFileTheAntivirusRemovedFailsTheDownloadAndIsNotRetried()
    {
        await using var ctx = await ServiceContext.CreateAsync(_server);
        string? checkedPath = null;
        ctx.Service.ScanForTesting = (path, _) =>
        {
            checkedPath = path;
            File.Delete(path);
            return Task.FromResult(ScanOutcome.Removed);
        };
        var response = await ctx.Service.AddJobAsync(new AddJobRequest
        {
            Url = $"{_server.BaseAddress}/file/scan-removed.bin?size=4096&seed=41",
            DestinationDirectory = ctx.Destination, FileName = "scan-removed.bin", Source = "cli",
        });
        Assert.True(response.Accepted, response.Error);

        var failed = await ctx.WaitFor(response.Job!.Id, j => j.State == "failed", TimeSpan.FromSeconds(30));
        Assert.Equal(Path.Combine(ctx.Destination, "scan-removed.bin"), checkedPath);
        Assert.Contains("antivirus removed", failed.Failure?.Message ?? "", StringComparison.OrdinalIgnoreCase);
        Assert.Null(failed.QueueRetryAt);
        Assert.Null(failed.CompletedAt);
        Assert.False(File.Exists(checkedPath));
    }

    [Fact]
    public async Task AFileThatPassedTheCheckCompletes()
    {
        await using var ctx = await ServiceContext.CreateAsync(_server);
        var checks = 0;
        ctx.Service.ScanForTesting = (_, _) => { Interlocked.Increment(ref checks); return Task.FromResult(ScanOutcome.Passed); };
        var response = await ctx.Service.AddJobAsync(new AddJobRequest
        {
            Url = $"{_server.BaseAddress}/file/scan-passed.bin?size=4096&seed=42",
            DestinationDirectory = ctx.Destination, FileName = "scan-passed.bin", Source = "cli",
        });
        await ctx.WaitFor(response.Job!.Id, j => j.State == "completed", TimeSpan.FromSeconds(30));
        Assert.Equal(1, Volatile.Read(ref checks));
        Assert.True(File.Exists(Path.Combine(ctx.Destination, "scan-passed.bin")));
    }

    [Fact]
    public async Task WindowsChecksAHarmlessFileAndMarksItAsFromTheInternet()
    {
        if (!OperatingSystem.IsWindows()) return;
        var folder = Path.Combine(Path.GetTempPath(), "inbound-scan-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(folder);
        try
        {
            var path = Path.Combine(folder, "harmless.txt");
            await File.WriteAllTextAsync(path, "An ordinary text file.");
            var outcome = await DownloadScan.RunAsync(path, "https://downloads.example.com/a/b/harmless.txt?token=secret");
            Console.WriteLine($"L3 check outcome: {outcome}");
            // A machine whose attachment services are switched off reports NotRun; a harmless file is never removed.
            Assert.NotEqual(ScanOutcome.Removed, outcome);
            Assert.True(File.Exists(path));
            if (outcome == ScanOutcome.Passed)
            {
                var mark = await File.ReadAllTextAsync(path + ":Zone.Identifier");
                Assert.Contains("ZoneId=3", mark);
                // Only the site goes in the mark, never the path or a token.
                Assert.DoesNotContain("secret", mark);
                Assert.DoesNotContain("/a/b/", mark);
            }
        }
        finally
        {
            Directory.Delete(folder, recursive: true);
        }
    }

    [Fact]
    public async Task ALocalOrLoopbackDownloadIsNotChecked()
    {
        var path = Path.GetTempFileName();
        try
        {
            Assert.Equal(ScanOutcome.NotRun, await DownloadScan.RunAsync(path, "http://127.0.0.1:5000/x.bin"));
            Assert.Equal(ScanOutcome.NotRun, await DownloadScan.RunAsync(path, null));
        }
        finally
        {
            File.Delete(path);
        }
    }
}

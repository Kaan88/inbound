using DownloadManager.Contracts;
using DownloadManager.Core;
using DownloadManager.Persistence;

namespace DownloadManager.Integration.Tests;

/// <summary>
/// Owner, 2026-11-17 ("Free the name"): only downloads that can still write hold a final name. A failed one does not, so a
/// new add may take its name; when the failed one is retried its name is checked again and its duplicate policy decides.
/// </summary>
public sealed partial class RuntimeServiceTests
{
    private const int FailedNameSize = 256 * 1024;

    /// <summary>A failed download of file.zip saved before the service starts, with an empty part file.</summary>
    private static async Task<(StoragePaths Paths, string Destination, Guid Id, string PartPath)> FailedFileZipAsync(string url, string policy)
    {
        var root = Path.Combine(AppContext.BaseDirectory, "test-work", Guid.NewGuid().ToString("N"));
        var paths = new StoragePaths(Path.Combine(root, "state"));
        var destination = Path.Combine(root, "downloads");
        Directory.CreateDirectory(destination);
        paths.EnsureCreated();
        var id = Guid.NewGuid();
        var final = Path.Combine(destination, "file.zip");
        var part = $"{final}.{id:N}.part";
        await File.WriteAllBytesAsync(part, []);
        await new JsonJobStore(paths).SaveAsync(new DownloadJob
        {
            Id = id,
            Url = url,
            FileName = "file.zip",
            DestinationDirectory = destination,
            FinalFilePath = final,
            PartFilePath = part,
            ConflictPolicy = Enum.Parse<ConflictPolicy>(policy, ignoreCase: true),
            State = DownloadState.Failed,
            Failure = new JobFailure(FailureKind.Network, "The connection was lost.", true, true, DateTimeOffset.UtcNow),
        });
        return (paths, destination, id, part);
    }

    private AddJobRequest SlowFileZip(string destination, int seed) => new()
    {
        Url = $"{_server.BaseAddress}/slow/file.zip?size={FailedNameSize}&seed={seed}&bps={64 * 1024}",
        Connections = 1,
        DestinationDirectory = destination,
        FileName = "file.zip",
        ConflictPolicy = "rename",
        Source = "cli",
    };

    [Fact]
    public async Task AFailedDownloadDoesNotHoldItsNameSoTheSameNameAddedAgainGetsIt()
    {
        var (paths, destination, id, _) = await FailedFileZipAsync($"{_server.BaseAddress}/file/file.zip?size={FailedNameSize}&seed=501", "rename");
        await using var ctx = await ServiceContext.CreateAsync(_server, paths, destination);
        Assert.Equal("failed", ctx.Service.GetJob(id)!.State);

        var again = await ctx.Service.AddJobAsync(new AddJobRequest
        {
            Url = $"{_server.BaseAddress}/file/file.zip?size={FailedNameSize}&seed=502",
            DestinationDirectory = destination,
            FileName = "file.zip",
            ConflictPolicy = "rename",
            Source = "cli",
        });

        Assert.True(again.Accepted, again.Error);
        Assert.Equal(Path.Combine(destination, "file.zip"), again.Job!.FinalFilePath, StringComparer.OrdinalIgnoreCase);
        var done = await ctx.WaitFor(again.Job.Id, j => j.State == "completed", TimeSpan.FromSeconds(30));
        Assert.Equal(Sha256Of(502, FailedNameSize), await Hashing.Sha256FileAsync(done.FinalFilePath));
    }

    [Fact]
    public async Task RetryingAFailedDownloadWhoseNameAnotherNowUsesTakesAFreeNameUnderRename()
    {
        var (paths, destination, id, oldPart) = await FailedFileZipAsync($"{_server.BaseAddress}/file/file.zip?size={FailedNameSize}&seed=511", "rename");
        await using var ctx = await ServiceContext.CreateAsync(_server, paths, destination);
        var other = await ctx.Service.AddJobAsync(SlowFileZip(destination, 512));
        Assert.True(other.Accepted, other.Error);
        Assert.Equal(Path.Combine(destination, "file.zip"), other.Job!.FinalFilePath, StringComparer.OrdinalIgnoreCase);
        await ctx.WaitFor(other.Job.Id, j => j.ReceivedBytes > 0);

        var retry = await ctx.Service.ResumeAsync(id);

        Assert.True(retry.Ok, retry.Error);
        var renamed = Path.Combine(destination, "file (1).zip");
        Assert.Equal(renamed, retry.Job!.FinalFilePath, StringComparer.OrdinalIgnoreCase);
        Assert.Equal("file (1).zip", retry.Job.FileName);
        Assert.False(File.Exists(oldPart)); // its part file went with the name
        var retried = await ctx.WaitFor(id, j => j.State == "completed", TimeSpan.FromSeconds(30));
        var running = await ctx.WaitFor(other.Job.Id, j => j.State == "completed", TimeSpan.FromSeconds(30));
        Assert.Equal(renamed, retried.FinalFilePath, StringComparer.OrdinalIgnoreCase);
        Assert.Equal(Sha256Of(511, FailedNameSize), await Hashing.Sha256FileAsync(retried.FinalFilePath));
        Assert.Equal(Sha256Of(512, FailedNameSize), await Hashing.Sha256FileAsync(running.FinalFilePath));
    }

    [Fact]
    public async Task RetryingAFailedDownloadWhoseNameAnotherNowUsesIsRefusedUnderFail()
    {
        var (paths, destination, id, oldPart) = await FailedFileZipAsync($"{_server.BaseAddress}/file/file.zip?size={FailedNameSize}&seed=521", "fail");
        await using var ctx = await ServiceContext.CreateAsync(_server, paths, destination);
        var other = await ctx.Service.AddJobAsync(SlowFileZip(destination, 522));
        Assert.True(other.Accepted, other.Error);
        await ctx.WaitFor(other.Job!.Id, j => j.ReceivedBytes > 0);

        var retry = await ctx.Service.ResumeAsync(id);

        Assert.False(retry.Ok);
        Assert.Contains("Another download already saves to", retry.Error);
        Assert.Equal("failed", ctx.Service.GetJob(id)!.State);
        Assert.Equal(Path.Combine(destination, "file.zip"), ctx.Service.GetJob(id)!.FinalFilePath, StringComparer.OrdinalIgnoreCase);
        Assert.True(File.Exists(oldPart));
    }

    [Fact]
    public async Task RetryingAFailedDownloadWhoseNameNobodyTookKeepsIt()
    {
        var (paths, destination, id, _) = await FailedFileZipAsync($"{_server.BaseAddress}/file/file.zip?size={FailedNameSize}&seed=531", "rename");
        await using var ctx = await ServiceContext.CreateAsync(_server, paths, destination);

        var retry = await ctx.Service.ResumeAsync(id);

        Assert.True(retry.Ok, retry.Error);
        var done = await ctx.WaitFor(id, j => j.State == "completed", TimeSpan.FromSeconds(30));
        Assert.Equal(Path.Combine(destination, "file.zip"), done.FinalFilePath, StringComparer.OrdinalIgnoreCase);
        Assert.Equal(Sha256Of(531, FailedNameSize), await Hashing.Sha256FileAsync(done.FinalFilePath));
    }
}

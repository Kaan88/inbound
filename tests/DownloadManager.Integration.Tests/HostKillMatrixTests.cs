using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using DownloadManager.Contracts;
using DownloadManager.Core.Providers;
using TestServer;
using Xunit.Abstractions;

namespace DownloadManager.Integration.Tests;

/// <summary>
/// The interruption matrix: the real Host process killed hard at each point a provider job passes,
/// then started again on the same profile. Each cell checks the same things afterwards: nothing sent
/// twice, no second child job, every byte equal to what the provider holds, no extraction counted as
/// done before it was, and the files beside it left alone. The mock stands in for the provider, and
/// every value is invented. Cells already covered elsewhere with a real process are not repeated:
/// a kill while the provider works on an item and a kill during a local provider transfer are in
/// <see cref="HostPipeRemoteTests.ScenarioJ_AKilledHostPicksUpEverythingWithoutASecondSubmission"/>,
/// and a kill during the extraction of a local archive is in
/// <see cref="RuntimeArchiveHostTests.KillingTheHostMidExtractionTakesTheToolWithItAndTheNextHostFinishes"/>.
/// </summary>
[Collection("runtime-archive")]
public sealed class HostKillMatrixTests(ITestOutputHelper output) : IAsyncLifetime
{
    private const string Key = "7c1d3e5f-2a4b-4c6d-8e0f-1a2b3c4d5e6f";
    private const int Kb = 1024;
    private const string CreateTorrent = "POST torrents/createtorrent";
    private const string CreateWeb = "POST webdl/createwebdownload";
    private const string WebList = "GET webdl/mylist";
    private const string TorrentLink = "GET torrents/requestdl";

    private TestServerHost _server = null!;
    private MockTorBox Mock => _server.TorBox;

    public async Task InitializeAsync()
    {
        _server = await TestServerHost.StartAsync();
        Mock.ApiKey = Key;
        Mock.AddHoster("Pretend", ["pretend.invalid", "127.0.0.1", "localhost"], "/hoster/");
    }

    public async Task DisposeAsync() => await _server.DisposeAsync();

    private Task<SandboxHost> StartHostAsync() => SandboxHost.StartAsync(Mock.ApiBase);

    private static async Task<HostConnection> WithKeyAsync(SandboxHost host)
    {
        var connection = await host.ConnectAsync();
        var result = await connection.AskAsync<RemoteAccountResult>(MessageTypes.RemoteAccountSetKey, new RemoteSetKeyRequest { Key = Key });
        Assert.True(result.Ok, result.Error);
        return connection;
    }

    private MockTorBoxFile NewFile(string name, int size, int seed) => MockTorBoxFile.Of(Mock.NextFileId(), name, size, seed);

    private static string Md5(string text) => Convert.ToHexString(MD5.HashData(Encoding.UTF8.GetBytes(text))).ToLowerInvariant();

    private static string Sha(byte[] content) => Convert.ToHexString(SHA256.HashData(content));

    private static void AssertSameFile(SandboxHost host, MockTorBoxFile file, params string[] relative)
    {
        var path = Path.Combine([host.Destination, .. relative]);
        Assert.Equal(file.Content.Length, new FileInfo(path).Length);
        Assert.Equal(Sha(file.Content), Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(path))));
    }

    private static async Task Until(Func<bool> condition, string what, int seconds = 30)
    {
        var deadline = DateTimeOffset.UtcNow + TimeSpan.FromSeconds(seconds);
        while (!condition())
        {
            Assert.True(DateTimeOffset.UtcNow < deadline, $"Waited too long for {what}.");
            await Task.Delay(5);
        }
    }

    private string? _lastSeen;

    /// <summary>Writes each state the job passes through to the test output, as the evidence of the order.</summary>
    private async Task<RemoteJobDto> UntilJobAsync(HostConnection connection, Guid id, Func<RemoteJobDto, bool> done, string what, int seconds = 60, bool checkNow = false)
    {
        var deadline = DateTimeOffset.UtcNow + TimeSpan.FromSeconds(seconds);
        while (true)
        {
            var job = (await connection.AskAsync<RemoteListResult>(MessageTypes.RemoteList, new { })).Jobs.FirstOrDefault(candidate => candidate.Id == id);
            var seen = $"{job?.State} ({job?.Status})";
            if (seen != _lastSeen) output.WriteLine($"{DateTimeOffset.UtcNow:HH:mm:ss.fff} {seen}, creates {Mock.Calls(CreateTorrent) + Mock.Calls(CreateWeb)}");
            _lastSeen = seen;
            if (job is not null && done(job)) return job;
            if (job?.State == "failed") throw new InvalidOperationException($"Failed while waiting for {what}: {job.FailureKind}: {job.FailureMessage}");
            if (DateTimeOffset.UtcNow > deadline) throw new TimeoutException($"Waited too long for {what}. Last: {job?.State}: {job?.Status}");
            if (checkNow) await connection.AskAsync<RemoteActionResultDto>(MessageTypes.RemoteCheckNow, new RemoteCheckNowRequest { JobId = id });
            await Task.Delay(checkNow ? 250 : 50);
        }
    }

    private static async Task<List<JobDto>> ProviderJobsAsync(HostConnection connection) =>
        (await connection.AskAsync<SnapshotDto>(MessageTypes.JobsQuery, new { })).Jobs
            .Where(job => job.Url.StartsWith("remote://", StringComparison.Ordinal)).ToList();

    /// <summary>A restart can leave a child paused or queued; a user would press Resume, so this does.</summary>
    private static async Task<IReadOnlyList<JobDto>> FinishProviderJobsAsync(HostConnection connection, int count, int seconds = 90)
    {
        var deadline = DateTimeOffset.UtcNow + TimeSpan.FromSeconds(seconds);
        var resumed = new HashSet<Guid>();
        while (true)
        {
            var jobs = await ProviderJobsAsync(connection);
            Assert.True(jobs.Count <= count, $"{jobs.Count} child jobs where {count} were expected.");
            Assert.DoesNotContain(jobs, job => job.State == "failed");
            if (jobs.Count == count && jobs.All(job => job.State == "completed")) return jobs;
            foreach (var job in jobs.Where(job => job.State is "paused" && resumed.Add(job.Id)))
            {
                await connection.RequestAsync(MessageTypes.JobResume, new JobActionRequest { JobId = job.Id });
            }

            if (DateTimeOffset.UtcNow > deadline) throw new TimeoutException($"Waited too long for {count} jobs: {string.Join(", ", jobs.Select(job => job.State))}");
            await Task.Delay(50);
        }
    }

    private static void AssertNoStrayFiles(SandboxHost host)
    {
        var all = Directory.EnumerateFiles(host.Destination, "*", SearchOption.AllDirectories).Select(Path.GetFileName).ToList();
        Assert.DoesNotContain(all, name => name!.EndsWith(".part", StringComparison.OrdinalIgnoreCase));
        Assert.DoesNotContain(all, name => name!.Contains(" (1)", StringComparison.Ordinal));
    }

    /// <summary>The state a record has on disk, as the next Host will read it.</summary>
    private static async Task<string> SavedStateAsync(string path)
    {
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        using var document = await System.Text.Json.JsonDocument.ParseAsync(stream);
        return document.RootElement.EnumerateObject().First(property => property.Name.Equals("state", StringComparison.OrdinalIgnoreCase)).Value.ToString();
    }

    private static string RemoteRecord(SandboxHost host, Guid id) => Path.Combine(host.DataDirectory, "remote", "jobs", $"{id:N}.json");

    // ---- Submitting -----------------------------------------------------------------------------

    /// <summary>
    /// Killed with the create request sent and not yet taken in. The provider never got it, so after
    /// the restart the job may not send it again at once: the list decides, and only after the grace
    /// does it go once more. One item on the account, one file, the right bytes.
    /// </summary>
    [Fact]
    public async Task KilledDuringASubmitThatNeverArrived_ItIsSentAgainOnceAfterTheGrace()
    {
        await using var host = await StartHostAsync();
        var connection = await WithKeyAsync(host);
        var hash = new string('d', 40);
        var magnet = $"magnet:?xt=urn:btih:{hash}&dn=Lost%20Post";
        var file = NewFile("Lost Post/lost.bin", 256 * Kb, 51);
        Mock.ScriptSubmission(hash, [file], [MockTorBoxState.Downloading(0.5, eta: 5), MockTorBoxState.Ready]);
        Mock.Fail(CreateTorrent, MockFault.Delay(TimeSpan.FromMinutes(5)), times: 1);

        var job = (await connection.AskAsync<RemoteAddResultDto>(MessageTypes.RemoteAdd, new RemoteAddRequestDto
        {
            Input = magnet,
            Destination = host.Destination,
            Selection = "all",
            ApproveUncached = true,
        })).Job!;
        await Until(() => Mock.Calls(CreateTorrent) == 1, "the create request to arrive");
        Assert.Equal("submitting", await SavedStateAsync(RemoteRecord(host, job.Id)));
        await host.KillAsync();
        Assert.Empty(Mock.Submissions);

        await host.RestartAsync();
        connection = await host.ConnectAsync();
        var unknown = await UntilJobAsync(connection, job.Id, candidate => candidate.State == "submissionUnknown", "the restart to read it as unknown");
        Assert.Null(unknown.ItemId);
        await Task.Delay(TimeSpan.FromSeconds(3));
        Assert.Equal(1, Mock.Calls(CreateTorrent));

        await UntilJobAsync(connection, job.Id, candidate => candidate.State == "waiting", "the one resubmission after the grace", seconds: 120);
        await UntilJobAsync(connection, job.Id, candidate => candidate.State == "localStarted", "ready", checkNow: true);
        await FinishProviderJobsAsync(connection, 1);
        AssertSameFile(host, file, "Lost Post", "lost.bin");
        Assert.Single(Mock.Submissions);
        Assert.Equal(2, Mock.Calls(CreateTorrent));
        Assert.NotNull(Mock.FindItem(RemoteItemKind.Torrent, hash));
        AssertNoStrayFiles(host);
        await connection.DisposeAsync();
        await host.StopAsync();
        host.AssertNowhere(Key, magnet);
    }

    /// <summary>
    /// Killed after the provider took the request in and before its answer reached the Host, so the
    /// Host never saved what came of it. After the restart the provider's list shows the item, and
    /// the job adopts it at once, with no second create.
    /// </summary>
    [Fact]
    public async Task KilledAfterTheProviderTookASubmitAndBeforeTheAnswerWasSaved_ItIsAdoptedNotSentAgain()
    {
        await using var host = await StartHostAsync();
        var connection = await WithKeyAsync(host);
        var link = Mock.HosterUrl("file/answered.bin");
        var file = NewFile("answered.bin/answered.bin", 192 * Kb, 52);
        Mock.ScriptSubmission(Md5(link), [file], [MockTorBoxState.Downloading(0.5, eta: 5), MockTorBoxState.Ready]);
        Mock.Fail(CreateWeb, MockFault.AcceptThenDelay(TimeSpan.FromMinutes(5)), times: 1);

        var job = (await connection.AskAsync<RemoteAddResultDto>(MessageTypes.RemoteAdd, new RemoteAddRequestDto
        {
            Input = link,
            Destination = host.Destination,
            Selection = "all",
            ApproveUncached = true,
        })).Job!;
        await Until(() => Mock.Submissions.Count == 1, "the provider to take the request in");
        Assert.Equal("submitting", await SavedStateAsync(RemoteRecord(host, job.Id)));
        await host.KillAsync();

        await host.RestartAsync();
        connection = await host.ConnectAsync();
        var adopted = await UntilJobAsync(connection, job.Id, candidate => candidate.State is "waiting" or "localStarted", "the item to be found on the list");
        var item = Mock.FindItem(RemoteItemKind.WebDownload, Md5(link))!;
        Assert.Equal(item.Id.ToString(), adopted.ItemId);

        await UntilJobAsync(connection, job.Id, candidate => candidate.State == "localStarted", "ready", checkNow: true);
        await FinishProviderJobsAsync(connection, 1);
        AssertSameFile(host, file, "answered.bin");
        Assert.Single(Mock.Submissions);
        Assert.Equal(1, Mock.Calls(CreateWeb));
        AssertNoStrayFiles(host);
        await connection.DisposeAsync();
        await host.StopAsync();
        host.AssertNowhere(Key, link);
    }

    // ---- Polling and links ----------------------------------------------------------------------

    /// <summary>Killed with a status check on its way. The same item is followed after the restart, and nothing is sent again.</summary>
    [Fact]
    public async Task KilledDuringAStatusCheck_TheSameItemIsFollowedAndNothingIsSentAgain()
    {
        await using var host = await StartHostAsync();
        var connection = await WithKeyAsync(host);
        var link = Mock.HosterUrl("file/polled.bin");
        var file = NewFile("polled.bin/polled.bin", 160 * Kb, 53);
        Mock.ScriptSubmission(Md5(link), [file], [.. Enumerable.Repeat(MockTorBoxState.Downloading(0.4, eta: 5), 4), MockTorBoxState.Ready]);

        var job = (await connection.AskAsync<RemoteAddResultDto>(MessageTypes.RemoteAdd, new RemoteAddRequestDto
        {
            Input = link,
            Destination = host.Destination,
            Selection = "all",
            ApproveUncached = true,
        })).Job!;
        var waiting = await UntilJobAsync(connection, job.Id, candidate => candidate.State == "waiting", "the submission");

        var before = Mock.Calls(WebList);
        Mock.Fail(WebList, MockFault.Delay(TimeSpan.FromMinutes(5)));
        await connection.AskAsync<RemoteActionResultDto>(MessageTypes.RemoteCheckNow, new RemoteCheckNowRequest { JobId = job.Id });
        await Until(() => Mock.Calls(WebList) > before, "a status check to be on its way");
        await host.KillAsync();
        Mock.ClearFaults();

        await host.RestartAsync();
        connection = await host.ConnectAsync();
        var started = await UntilJobAsync(connection, job.Id, candidate => candidate.State == "localStarted", "ready after the restart", checkNow: true);
        Assert.Equal(waiting.ItemId, started.ItemId);
        await FinishProviderJobsAsync(connection, 1);
        AssertSameFile(host, file, "polled.bin");
        Assert.Single(Mock.Submissions);
        Assert.Equal(1, Mock.Calls(CreateWeb));
        AssertNoStrayFiles(host);
        await connection.DisposeAsync();
        await host.StopAsync();
        host.AssertNowhere(Key, link, "/torbox-cdn/");
    }

    /// <summary>Killed while every file's link request hangs. After the restart each file has one child job and its exact bytes.</summary>
    [Fact]
    public async Task KilledWhileLinksAreRequested_EachFileHasOneChildAndItsExactBytes()
    {
        await using var host = await StartHostAsync();
        var connection = await WithKeyAsync(host);
        var first = NewFile("Linked/one.bin", 300 * Kb, 54);
        var second = NewFile("Linked/two.bin", 200 * Kb, 55);
        var item = Mock.AddItem(RemoteItemKind.Torrent, "Linked", [first, second]);
        Mock.Fail(TorrentLink, MockFault.Delay(TimeSpan.FromMinutes(5)));

        var job = (await connection.AskAsync<RemoteAddResultDto>(MessageTypes.RemoteAdd, new RemoteAddRequestDto
        {
            Input = $"https://torbox.app/download?id={item.Id}&type=torrents",
            Destination = host.Destination,
            Selection = "all",
        })).Job!;
        await UntilJobAsync(connection, job.Id, candidate => candidate.State == "localStarted", "the child jobs");
        await Until(() => Mock.Calls(TorrentLink) >= 2, "both link requests to be on their way");
        var children = (await ProviderJobsAsync(connection)).Select(child => child.Id).Order().ToList();
        Assert.Equal(2, children.Count);
        await host.KillAsync();
        Mock.ClearFaults();
        Assert.Empty(Mock.IssuedLinks);

        await host.RestartAsync();
        connection = await host.ConnectAsync();
        var done = await FinishProviderJobsAsync(connection, 2);
        Assert.Equal(children, done.Select(child => child.Id).Order().ToList());
        AssertSameFile(host, first, "Linked", "one.bin");
        AssertSameFile(host, second, "Linked", "two.bin");
        Assert.Empty(Mock.Submissions);
        AssertNoStrayFiles(host);
        await connection.DisposeAsync();
        await host.StopAsync();
        host.AssertNowhere(Key, "/torbox-cdn/");
    }

    // ---- A package half done --------------------------------------------------------------------

    /// <summary>
    /// Killed with one file of a package finished and another part way. The finished one is not
    /// fetched again or touched, the other goes on, and the package keeps its three children.
    /// </summary>
    [Fact]
    public async Task KilledWithAPackageHalfDone_TheFinishedFileIsKeptAndTheRestGoOn()
    {
        await using var host = await StartHostAsync();
        var connection = await WithKeyAsync(host);
        var small = NewFile("Half/small.bin", 24 * Kb, 56);
        var big = NewFile("Half/big.bin", 3 * 1024 * Kb, 57);
        var other = NewFile("Half/other.bin", 2 * 1024 * Kb, 58);
        var item = Mock.AddItem(RemoteItemKind.Torrent, "Half", [small, big, other]);
        Mock.CdnBytesPerSecond = 128 * Kb;

        var job = (await connection.AskAsync<RemoteAddResultDto>(MessageTypes.RemoteAdd, new RemoteAddRequestDto
        {
            Input = $"https://torbox.app/download?id={item.Id}&type=torrents",
            Destination = host.Destination,
            Selection = "all",
        })).Job!;
        await UntilJobAsync(connection, job.Id, candidate => candidate.State == "localStarted", "the child jobs");

        var deadline = DateTimeOffset.UtcNow + TimeSpan.FromSeconds(60);
        List<JobDto> seen;
        while (true)
        {
            seen = await ProviderJobsAsync(connection);
            if (seen.Count(child => child.State == "completed") == 1 && seen.Any(child => child.State != "completed" && child.ReceivedBytes > 64 * Kb)) break;
            Assert.True(DateTimeOffset.UtcNow < deadline, "The package never got half done.");
            await Task.Delay(20);
        }

        var finished = seen.Single(child => child.State == "completed");
        Assert.EndsWith("small.bin", finished.FinalFilePath);
        var smallPath = Path.Combine(host.Destination, "Half", "small.bin");
        var writtenAt = File.GetLastWriteTimeUtc(smallPath);
        await host.KillAsync();
        Mock.CdnBytesPerSecond = null;
        var linksForSmall = Mock.IssuedLinks.Count(link => link.File.Id == small.Id);

        await host.RestartAsync();
        connection = await host.ConnectAsync();
        var done = await FinishProviderJobsAsync(connection, 3);
        Assert.Equal(seen.Select(child => child.Id).Order(), done.Select(child => child.Id).Order());
        AssertSameFile(host, small, "Half", "small.bin");
        AssertSameFile(host, big, "Half", "big.bin");
        AssertSameFile(host, other, "Half", "other.bin");
        Assert.Equal(writtenAt, File.GetLastWriteTimeUtc(smallPath));
        Assert.Equal(linksForSmall, Mock.IssuedLinks.Count(link => link.File.Id == small.Id));
        Assert.Empty(Mock.Submissions);
        AssertNoStrayFiles(host);
        await connection.DisposeAsync();
        await host.StopAsync();
        host.AssertNowhere(Key, "/torbox-cdn/");
    }

    // ---- Extraction and cleanup of a provider package -------------------------------------------

    /// <summary>
    /// A split archive and a file beside it, from the provider, extracted by itself with the archives
    /// set to go afterwards. Killed once while 7-Zip runs and once the moment the first volume is
    /// deleted. The kill during the run leaves no completed record; the next Host runs it again;
    /// the kill during the deletes is finished by the next Host; the file beside it stays; and no
    /// download is fetched again or failed for its file being gone.
    /// </summary>
    [RarFact]
    public async Task KilledDuringExtractionAndDuringArchiveCleanup_OneCopyComesOutAndTheSiblingStays()
    {
        await using var host = await StartHostAsync();
        var connection = await WithKeyAsync(host);
        var settings = await connection.AskAsync<ArchiveSettingsResult>(ArchiveMessageTypes.SettingsSet, new ArchiveSettingsUpdate { AutoExtract = true, DeleteArchives = true });
        Assert.True(settings.Ok, settings.Error);
        Assert.True(settings.Tool.Found, settings.Tool.Message);

        var contents = new List<(string Name, byte[] Data)>();
        for (var index = 0; index < 300; index++) contents.Add(($@"many\file{index:0000}.bin", ArchiveTools.RandomBytes(4096, index)));
        contents.Add(("big.bin", ArchiveTools.RandomBytes(96 * 1024 * 1024, 78)));
        var volumes = await ArchiveTools.BuildRar5Async(host.Root, "huge.rar", contents, "-v8m");
        Assert.True(volumes.Count >= 10, $"{volumes.Count} volumes were made.");
        var files = volumes.Select(pair => new MockTorBoxFile { Id = Mock.NextFileId(), Name = "Arc/" + pair.Key, Content = pair.Value, Size = pair.Value.Length }).ToList();
        var sibling = NewFile("Arc/readme.txt", 3 * Kb, 59);
        files.Add(sibling);
        var item = Mock.AddItem(RemoteItemKind.Torrent, "Arc", files);
        var folder = Path.Combine(host.Destination, "Arc");

        var job = (await connection.AskAsync<RemoteAddResultDto>(MessageTypes.RemoteAdd, new RemoteAddRequestDto
        {
            Input = $"https://torbox.app/download?id={item.Id}&type=torrents",
            Destination = host.Destination,
            Selection = "all",
        })).Job!;

        // The first kill, with 7-Zip busy.
        var before = Process.GetProcessesByName("7z").Select(process => process.Id).ToHashSet();
        await Until(() => connection.Events.Any(message => message.Type == ArchiveMessageTypes.Changed &&
            message.GetPayload<ArchiveChangedDto>().Set is { State: "extracting", Phase: "extracting" or "verifying" or "finalizing" }) &&
            Process.GetProcessesByName("7z").Any(process => !before.Contains(process.Id)), "7-Zip to be running", 120);
        var linksAfterDownload = Mock.IssuedLinks.Count;
        var phase = connection.Events.Where(message => message.Type == ArchiveMessageTypes.Changed).Select(message => message.GetPayload<ArchiveChangedDto>().Set).Last(dto => dto is not null)!;
        long bigSoFar;
        try { bigSoFar = Directory.EnumerateFiles(host.Destination, "big.bin", SearchOption.AllDirectories).Sum(path => new FileInfo(path).Length); }
        catch (IOException) { bigSoFar = -1; }
        await host.KillAsync();
        output.WriteLine($"Killed during extraction with 7-Zip running, at phase {phase.Phase}, {phase.Percent}%, {bigSoFar} bytes of big.bin written so far.");
        await Until(() => !Process.GetProcessesByName("7z").Any(process => !before.Contains(process.Id)), "7-Zip to go with the Host", 10);
        var setFile = Assert.Single(Directory.GetFiles(Path.Combine(host.DataDirectory, "archive", "sets"), "*.json"));
        Assert.NotEqual("completed", await SavedStateAsync(setFile));

        // The second kill, the moment a volume goes.
        using var watcher = new FileSystemWatcher(folder, "*.rar") { NotifyFilter = NotifyFilters.FileName };
        var killedAt = new TaskCompletionSource<int>(TaskCreationOptions.RunContinuationsAsynchronously);
        int? second = null;
        watcher.Deleted += (_, _) =>
        {
            if (killedAt.Task.IsCompleted || second is not { } id) return;
            try
            {
                using var process = Process.GetProcessById(id);
                process.Kill(entireProcessTree: true);
            }
            catch (Exception ex) when (ex is ArgumentException or InvalidOperationException) { }
            killedAt.TrySetResult(Directory.GetFiles(folder, "*.rar").Length);
        };

        await host.RestartAsync();
        second = host.ProcessId;
        watcher.EnableRaisingEvents = true;
        var left = await killedAt.Task.WaitAsync(TimeSpan.FromSeconds(120));
        await host.KillAsync();

        // What the kill left: success on record, cleanup unfinished.
        Assert.InRange(left, 0, volumes.Count - 1);
        var leftNow = Directory.GetFiles(folder, "*.rar").Length;
        output.WriteLine($"Killed during cleanup with {left} of {volumes.Count} volumes still there when the first delete was seen, {leftNow} once the Host was gone.");

        await host.RestartAsync();
        connection = await host.ConnectAsync();
        var deadline = DateTimeOffset.UtcNow + TimeSpan.FromSeconds(60);
        ArchiveSetDto set;
        while (true)
        {
            set = Assert.Single((await connection.AskAsync<ArchiveListResult>(ArchiveMessageTypes.List, new { })).Sets);
            if (set is { State: "completed", ArchivesDeleted: true }) break;
            Assert.True(DateTimeOffset.UtcNow < deadline, $"The cleanup never finished: {set.StateText} {set.Note}");
            await Task.Delay(50);
        }

        Assert.Equal(2, set.Attempts);
        Assert.Empty(Directory.GetFiles(folder, "*.rar"));
        Assert.Empty(Directory.EnumerateDirectories(host.Destination, ".inbound-extract-*", SearchOption.AllDirectories));
        AssertSameFile(host, sibling, "Arc", "readme.txt");
        var written = Directory.EnumerateFiles(folder, "*", SearchOption.AllDirectories).Select(path => Path.GetRelativePath(folder, path)).ToList();
        Assert.Equal(contents.Count + 1, written.Count);
        Assert.DoesNotContain(written, name => name.Contains(" (1)", StringComparison.Ordinal));
        Assert.Equal(Sha(contents[^1].Data), Convert.ToHexString(SHA256.HashData(await File.ReadAllBytesAsync(Path.Combine(folder, "big.bin")))));
        foreach (var (name, data) in contents.Take(5)) Assert.Equal(data, await File.ReadAllBytesAsync(Path.Combine(folder, name)));

        var jobs = await ProviderJobsAsync(connection);
        Assert.Equal(files.Count, jobs.Count);
        Assert.All(jobs, child => Assert.Equal("completed", child.State));
        Assert.Equal(volumes.Count, jobs.Count(child => child.FileRemovedAfterExtraction));
        Assert.Equal(linksAfterDownload, Mock.IssuedLinks.Count);
        Assert.Empty(Mock.Submissions);
        await connection.DisposeAsync();
        await host.StopAsync();
        Assert.True(leftNow <= left);
    }
}

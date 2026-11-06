using System.Security.Cryptography;
using System.Text;
using DownloadManager.Containers.Tests;
using DownloadManager.Contracts;
using DownloadManager.Core.Providers;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging;
using TestServer;

namespace DownloadManager.Integration.Tests;

/// <summary>
/// Scenarios G, H and I the whole way: the real Host process over its real pipe, the real engine against
/// the mock's CDN over real HTTP, the real ArchiveCoordinator and real UnRAR on real RAR files built by
/// Rar.exe, in the same Host. Containers come from the test helpers, because no real sample was
/// available. Every value is invented, and nothing reaches the real provider.
/// </summary>
[Collection("runtime-archive")]
public sealed class HostPipeContainerTests : IAsyncLifetime
{
    private const string Key = "7c1e5d2a-41ab-4e70-9d38-1fa2c6b8e047";
    private const string ArchivePassword = "Invented-Rar-Pw_4471";
    private const int Kb = 1024;

    private static readonly byte[] Inner = Encoding.ASCII.GetBytes("what the archive holds");

    private TestServerHost _server = null!;
    private string _scratch = null!;

    private MockTorBox Mock => _server.TorBox;

    public async Task InitializeAsync()
    {
        _server = await TestServerHost.StartAsync();
        Mock.ApiKey = Key;
        Mock.AddHoster("Pretend", ["pretend.invalid", "127.0.0.1", "localhost"], "/hoster/");
        _scratch = Path.Combine(AppContext.BaseDirectory, "test-work", "pipe-container-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_scratch);
    }

    public async Task DisposeAsync()
    {
        await _server.DisposeAsync();
        try { Directory.Delete(_scratch, recursive: true); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
    }

    private Task<SandboxHost> StartHostAsync() => SandboxHost.StartAsync(Mock.ApiBase);

    /// <summary>The key set and extraction following downloads by itself, as the owner has it.</summary>
    private static async Task<HostConnection> ReadyAsync(SandboxHost host, bool autoExtract = true)
    {
        var connection = await host.ConnectAsync();
        var key = await connection.AskAsync<RemoteAccountResult>(MessageTypes.RemoteAccountSetKey, new RemoteSetKeyRequest { Key = Key });
        Assert.True(key.Ok, key.Error);
        if (autoExtract)
        {
            var settings = await connection.AskAsync<ArchiveSettingsResult>(ArchiveMessageTypes.SettingsSet, new ArchiveSettingsUpdate { AutoExtract = true });
            Assert.True(settings.Ok, settings.Error);
            Assert.True(settings.Tool.Found, settings.Tool.Message);
        }

        return connection;
    }

    private string WriteContainer(string name, byte[] bytes)
    {
        var path = Path.Combine(_scratch, name);
        File.WriteAllBytes(path, bytes);
        return path;
    }

    private static string Md5(string text) => Convert.ToHexString(MD5.HashData(Encoding.UTF8.GetBytes(text))).ToLowerInvariant();

    private static MockTorBoxFile Volume(long id, string path, byte[] bytes) => new() { Id = id, Name = path, Content = bytes, Size = bytes.Length };

    private static Task<RemoteContainerPreviewResult> PreviewAsync(HostConnection connection, string path)
        => connection.AskAsync<RemoteContainerPreviewResult>(MessageTypes.RemoteContainerPreview, new RemoteContainerPreviewRequest { Path = path });

    private static async Task<RemoteJobDto> UntilJobAsync(HostConnection connection, Guid id, Func<RemoteJobDto, bool> done, string what, bool checkNow = false)
    {
        var deadline = DateTimeOffset.UtcNow + TimeSpan.FromSeconds(60);
        while (true)
        {
            var job = (await connection.AskAsync<RemoteListResult>(MessageTypes.RemoteList, new { })).Jobs.FirstOrDefault(candidate => candidate.Id == id);
            if (job is not null && done(job)) return job;
            if (job?.State == "failed") throw new InvalidOperationException($"Failed while waiting for {what}: {job.FailureKind}: {job.FailureMessage}");
            if (DateTimeOffset.UtcNow > deadline) throw new TimeoutException($"Waited too long for {what}. Last: {job?.State}: {job?.Status}");
            if (checkNow) await connection.AskAsync<RemoteActionResultDto>(MessageTypes.RemoteCheckNow, new RemoteCheckNowRequest { JobId = id });
            await Task.Delay(checkNow ? 250 : 50);
        }
    }

    private static async Task<ArchiveSetDto> UntilSetAsync(HostConnection connection, SandboxHost host, Func<ArchiveSetDto, bool> done, string what)
    {
        var deadline = DateTimeOffset.UtcNow + TimeSpan.FromSeconds(90);
        while (true)
        {
            var sets = (await connection.AskAsync<ArchiveListResult>(ArchiveMessageTypes.List, new { })).Sets;
            if (sets.FirstOrDefault(done) is { } set)
            {
                // Success is on record just before staging goes.
                while (set.State == "completed" && Directory.EnumerateDirectories(host.Destination, ".inbound-extract-*", SearchOption.AllDirectories).Any() && DateTimeOffset.UtcNow < deadline)
                {
                    await Task.Delay(10);
                }

                return set;
            }

            if (DateTimeOffset.UtcNow > deadline)
            {
                throw new TimeoutException($"Waited too long for {what}. Sets: {string.Join("; ", sets.Select(candidate => $"{candidate.Name}: {candidate.StateText}"))}. Log: {string.Join(" | ", host.LogLines().TakeLast(10))}");
            }

            await Task.Delay(50);
        }
    }

    private static List<string> FilesUnder(string folder) => Directory.EnumerateFiles(folder, "*", SearchOption.AllDirectories)
        .Select(file => Path.GetRelativePath(folder, file)).Order(StringComparer.OrdinalIgnoreCase).ToList();

    // ---- Scenario I ---------------------------------------------------------------------------

    [RarFact]
    public async Task ScenarioI_AMixedContainerGoesThroughTheProviderAndItsPasswordReachesARealExtraction()
    {
        const string passkey = "invented-container-passkey";
        const string fragment = "invented-hoster-fragment";
        const string signature = "invented-signature-value";
        await using var host = await StartHostAsync();
        await using var listener = await host.ConnectAsync();
        await using var connection = await ReadyAsync(host);

        var built = await ArchiveTools.BuildRar5Async(_scratch, "locked.rar", [("inner/locked.txt", Inner)], "-hp" + ArchivePassword);

        // What the account and the provider's cache hold.
        var cachedLink = Mock.HosterUrl($"file/locked.rar#{fragment}");
        Mock.ScriptSubmission(Md5(cachedLink), [Volume(Mock.NextFileId(), "locked.rar/locked.rar", built["locked.rar"])], [MockTorBoxState.CachedReady]);
        Mock.AddCached(Md5(cachedLink));

        var uncachedLink = Mock.HosterUrl("file/not-cached.bin");
        var skippedLink = Mock.HosterUrl("file/left-out.bin");

        var hash = new string('a', 40);
        var magnet = $"magnet:?xt=urn:btih:{hash}&dn=Magnet%20Name&tr=http%3A%2F%2Ftracker.invalid%2F{passkey}";
        var magnetFile = MockTorBoxFile.Of(Mock.NextFileId(), "Magnet Name/magnet.bin", 8 * Kb, 81);
        Mock.ScriptSubmission(hash, [magnetFile], [MockTorBoxState.Ready]);

        var shared = Mock.AddItem(RemoteItemKind.Torrent, "Shared", [MockTorBoxFile.Of(Mock.NextFileId(), "Shared/shared.bin", 8 * Kb, 82)]);
        var folderLink = $"https://torbox.app/download?id={shared.Id}&type=torrents&name=Elsewhere";

        var ordinary = $"https://example.invalid/some/plain.zip?sig={signature}";
        const string foreign = "ed2k://file|invented.iso|1234|ABCDEF|/";

        var path = WriteContainer("mixed.ccf", ContainerFixtures.Ccf50(ContainerFixtures.CryptLoadXml(
            ContainerFixtures.PackageXml("Movie Pack", ArchivePassword, "from a friend",
                ContainerFixtures.DownloadXml(cachedLink, "locked.rar", built["locked.rar"].Length.ToString()),
                ContainerFixtures.DownloadXml(uncachedLink),
                ContainerFixtures.DownloadXml(skippedLink),
                ContainerFixtures.DownloadXml(magnet),
                ContainerFixtures.DownloadXml(folderLink)),
            ContainerFixtures.PackageXml("The Rest", null, null,
                ContainerFixtures.DownloadXml(ordinary),
                ContainerFixtures.DownloadXml(foreign)))));

        // The preview: links, packages, kinds and reasons, with no link in sight.
        var preview = await PreviewAsync(connection, path);
        Assert.True(preview.Ok, preview.Error);
        Assert.Equal("ccf", preview.Format);
        Assert.Equal(["Movie Pack", "The Rest"], preview.Packages.Select(package => package.Name));
        Assert.True(preview.Packages[0].HasArchivePassword);
        Assert.Equal("from a friend", preview.Packages[0].Comment);
        Assert.Equal(5, preview.Packages[0].EntryCount);
        Assert.Equal(["hosterLink", "hosterLink", "hosterLink", "magnet", "folderLink", "unsupported", "unsupported"], preview.Entries.Select(entry => entry.Kind));
        Assert.Equal("locked.rar", preview.Entries[0].DisplayName);
        Assert.Equal("127.0.0.1", preview.Entries[0].DisplayHost);
        Assert.Contains("TorBox won't take this", preview.Entries[5].Reason);
        Assert.Contains("normal download", preview.Entries[5].Reason);
        Assert.False(preview.Entries[5].CanAdd);
        Assert.False(preview.Entries[6].CanAdd);
        Assert.False(string.IsNullOrEmpty(preview.Entries[6].Reason));
        Assert.Empty(Mock.Submissions);

        // Choosing some. One is cached, one isn't, and one of each kind stays behind.
        var added = await connection.AskAsync<RemoteContainerAddResult>(MessageTypes.RemoteContainerAdd, new RemoteContainerAddRequest
        {
            PreviewId = preview.PreviewId!.Value,
            EntryIds = [1, 2, 4, 5, 6],
            Destination = host.Destination,
        });
        Assert.True(added.Ok, added.Error);
        Assert.Equal(5, added.Outcomes.Count);
        Assert.All(added.Outcomes.Take(4), outcome => Assert.True(outcome.Ok, outcome.Error));
        Assert.False(added.Outcomes[4].Ok);
        Assert.Contains("normal download", added.Outcomes[4].Error);
        Assert.True(added.Outcomes[0].Job!.HasArchivePassword);

        // The cached one is fetched, downloaded by the engine from the CDN, and extracted with the container's password.
        var set = await UntilSetAsync(connection, host, candidate => candidate.State == "completed", "the container's password to open the archive");
        Assert.Equal(Inner, await File.ReadAllBytesAsync(Path.Combine(host.Destination, "Movie Pack", "inner", "locked.txt")));
        Assert.False(set.NeedsPassword);
        Assert.Contains("Extraction completed", set.StateText);
        Assert.True(File.Exists(Path.Combine(host.Destination, "Movie Pack", "locked.rar")), "The archive stays by default.");

        // Only what was chosen and is allowed reached the provider, and none of it with an archive password.
        var sent = Assert.Single(Mock.Submissions);
        Assert.Equal(cachedLink, sent.Link);
        Assert.Null(sent.Password);

        // The uncached one and the magnet are waiting for the go-ahead, and nothing was sent for them.
        var uncached = await UntilJobAsync(connection, added.Outcomes[1].Job!.Id, job => job.ApprovalNeeded, "the question about the uncached link");
        Assert.Contains("Not cached on TorBox", uncached.Status);
        var magnetJob = await UntilJobAsync(connection, added.Outcomes[2].Job!.Id, job => job.ApprovalNeeded, "the question about the magnet");
        Assert.Single(Mock.Submissions);

        // The folder link needs no creation, only the user's choice of files.
        var shown = await UntilJobAsync(connection, added.Outcomes[3].Job!.Id, job => job.State == "awaitingSelection", "the folder link to be ready");
        Assert.Equal("Shared", shown.Name);

        // With the go-ahead the magnet goes the way any magnet does.
        var approved = await connection.AskAsync<RemoteActionResultDto>(MessageTypes.RemoteRetry, new RemoteRetryRequest { JobId = magnetJob.Id, ApproveUncached = true });
        Assert.True(approved.Ok, approved.Error);
        await UntilJobAsync(connection, magnetJob.Id, job => job.State == "awaitingSelection", "the magnet to be ready", checkNow: true);
        Assert.Equal(2, Mock.Submissions.Count);
        Assert.Equal(magnet, Mock.Submissions[1].Link);

        // What wasn't chosen was never sent, and no hoster was ever asked for anything.
        Assert.DoesNotContain(Mock.Submissions, submission => submission.Link == skippedLink || submission.Link == uncachedLink);
        Assert.Equal(0, Mock.HosterRequests);
        Assert.Equal(0, Mock.CdnRequestsWithCredentials);
        Assert.Equal(4, (await connection.AskAsync<RemoteListResult>(MessageTypes.RemoteList, new { })).Jobs.Count);

        // The other client heard about the jobs and the set, and only through those.
        Assert.Contains(listener.Events, message => message.Type == MessageTypes.RemoteChanged);
        Assert.Contains(listener.Events, message => message.Type == ArchiveMessageTypes.Changed);

        await host.StopAsync();
        Assert.Equal(0, host.LastExitCode);

        // The search can find something, or passing would mean nothing.
        Assert.Contains(host.Frames, frame => frame.Contains("locked.rar", StringComparison.Ordinal));
        host.AssertNowhere(
        [
            Key,
            ArchivePassword,
            cachedLink,
            cachedLink[..cachedLink.IndexOf('#', StringComparison.Ordinal)],
            fragment,
            uncachedLink,
            skippedLink,
            magnet,
            passkey,
            "tracker.invalid",
            "magnet:?",
            folderLink,
            "name=Elsewhere",
            ordinary,
            signature,
            foreign,
            "invented.iso",
            "/hoster/",
            "/torbox-cdn/",
            .. Mock.IssuedLinks.Select(link => link.Uuid),
        ]);
    }

    [RarFact]
    public async Task ScenarioI_PartsFromSeparateLinksMeetInOneFolderAndAreClaimedAsOneSet()
    {
        await using var host = await StartHostAsync();
        await using var connection = await ReadyAsync(host);
        var payload = ArchiveTools.RandomBytes(300_000, 91);
        var built = await ArchiveTools.BuildRar5Async(_scratch, "movie.rar", [("movie.bin", payload)], "-v96k", "-hp" + ArchivePassword);
        var names = built.Keys.ToList();
        Assert.True(names.Count >= 3, string.Join(", ", names));

        // Each part is its own item on the account, each with the folder a web download comes in.
        var downloads = new List<string>();
        var links = new List<string>();
        foreach (var name in names)
        {
            var link = Mock.HosterUrl($"file/{name}#invented-part-key-{name}");
            links.Add(link);
            Mock.AddItem(RemoteItemKind.WebDownload, name, [Volume(Mock.NextFileId(), $"{name}/{name}", built[name])]).Hash = Md5(link);
            downloads.Add(ContainerFixtures.DownloadXml(link, name));
        }

        var path = WriteContainer("parts.ccf", ContainerFixtures.Ccf30(ContainerFixtures.CryptLoadXml(ContainerFixtures.PackageXml("Parted", ArchivePassword, null, [.. downloads]))));
        var preview = await PreviewAsync(connection, path);
        Assert.Equal("3.0", preview.FormatVersion);
        var added = await connection.AskAsync<RemoteContainerAddResult>(MessageTypes.RemoteContainerAdd, new RemoteContainerAddRequest
        {
            PreviewId = preview.PreviewId!.Value,
            EntryIds = preview.Entries.Select(entry => entry.EntryId).ToList(),
            Destination = host.Destination,
        });
        Assert.All(added.Outcomes, outcome => Assert.True(outcome.Ok, outcome.Error));

        var folder = Path.Combine(host.Destination, "Parted");
        var set = await UntilSetAsync(connection, host, candidate => candidate.State == "completed", "the parts to come together");
        Assert.Equal(payload, await File.ReadAllBytesAsync(Path.Combine(folder, "movie.bin")));

        // One set, with every part, in one folder, and one output.
        var all = (await connection.AskAsync<ArchiveListResult>(ArchiveMessageTypes.List, new { })).Sets;
        Assert.Equal(set.Id, Assert.Single(all).Id);
        Assert.Contains(names[0], set.Volumes);
        Assert.Subset(names.ToHashSet(StringComparer.OrdinalIgnoreCase), set.Volumes.ToHashSet(StringComparer.OrdinalIgnoreCase));
        Assert.Equal(folder, set.Directory, ignoreCase: true);
        Assert.Equal(names.Append("movie.bin").Select(name => Path.Combine("Parted", name)).Order(StringComparer.OrdinalIgnoreCase), FilesUnder(host.Destination));

        // One local package, whichever job brought which part.
        var jobs = (await connection.AskAsync<RemoteListResult>(MessageTypes.RemoteList, new { })).Jobs;
        Assert.Equal(names.Count, jobs.Count);
        var package = Assert.Single(jobs.Select(job => job.PackageId).Distinct());
        Assert.Equal(package, set.PackageId);

        // All of it was on the account, so there was nothing to submit.
        Assert.Empty(Mock.Submissions);
        await host.StopAsync();
        host.AssertNowhere([Key, ArchivePassword, .. links, "invented-part-key", "/hoster/", "/torbox-cdn/"]);
    }

    [RarFact]
    public async Task ScenarioI_AnRsdfWithNoPackagesStillBringsSeparatePartsTogether()
    {
        await using var host = await StartHostAsync();
        await using var connection = await ReadyAsync(host);
        var payload = ArchiveTools.RandomBytes(200_000, 92);
        var built = await ArchiveTools.BuildRar5Async(_scratch, "plain.rar", [("plain.bin", payload)], "-v96k");
        var links = new List<string>();
        foreach (var name in built.Keys)
        {
            var link = Mock.HosterUrl($"file/{name}");
            links.Add(link);
            Mock.AddItem(RemoteItemKind.WebDownload, name, [Volume(Mock.NextFileId(), $"{name}/{name}", built[name])]).Hash = Md5(link);
        }

        // Through the command line, the way a person would.
        var path = WriteContainer("parts.rsdf", ContainerFixtures.Rsdf([.. links]));
        var shown = await host.RunCliAsync(null, "remote", "container", path);
        Assert.True(shown.ExitCode == 0, shown.Error);
        Assert.Contains("hosterLink", shown.Output);
        Assert.Contains("format:   rsdf", shown.Output);
        foreach (var link in links) Assert.DoesNotContain(link, shown.All);

        var result = await host.RunCliAsync(null, "remote", "container", path, "--all", "--dest", host.Destination);
        Assert.True(result.ExitCode == 0, result.All);
        Assert.Contains("added:", result.Output);
        foreach (var link in links) Assert.DoesNotContain(link, result.All);

        await UntilSetAsync(connection, host, candidate => candidate.State == "completed", "the parts to come together");
        Assert.Equal(payload, await File.ReadAllBytesAsync(Path.Combine(host.Destination, "plain.bin")));
        Assert.Single((await connection.AskAsync<ArchiveListResult>(ArchiveMessageTypes.List, new { })).Sets);
    }

    [Fact]
    public async Task ThePipeAndTheCommandLineAnswerAMalformedFileAndNoKeyAndADlcInWords()
    {
        await using var host = await StartHostAsync();
        await using var connection = await host.ConnectAsync();

        foreach (var (name, bytes) in new (string, byte[])[]
        {
            ("garbage.rsdf", Encoding.ASCII.GetBytes("zzzz not hex")),
            ("garbage.ccf", ArchiveTools.RandomBytes(1024, 7)),
            ("truncated.ccf", Encoding.ASCII.GetBytes("CCF3.0abc")),
            ("garbage.dlc", Encoding.ASCII.GetBytes("<html>saved error page</html>")),
        })
        {
            var refused = await PreviewAsync(connection, WriteContainer(name, bytes));
            Assert.False(refused.Ok);
            Assert.False(string.IsNullOrWhiteSpace(refused.Error));
            Assert.DoesNotContain(_scratch, refused.Error);
        }

        // The connection is still fine after all of those, and so is the Host.
        Assert.True((await connection.AskAsync<RemoteListResult>(MessageTypes.RemoteList, new { })).Ok);

        var noKey = await PreviewAsync(connection, WriteContainer("nokey.rsdf", ContainerFixtures.Rsdf("https://example.invalid/a.rar")));
        Assert.True(noKey.Ok, noKey.Error);
        Assert.Contains("No TorBox API key", noKey.AccountProblem);
        Assert.Contains("No TorBox API key", Assert.Single(noKey.Entries).Reason);
        Assert.Equal(0, Mock.TotalApiCalls);

        var network = await PreviewAsync(connection, @"\\attacker.invalid\share\links.ccf");
        Assert.False(network.Ok);
        Assert.Contains("network path", network.Error);

        var dlcPath = WriteContainer("links.dlc", Encoding.ASCII.GetBytes(ContainerFixtures.Dlc()));
        var dlc = await PreviewAsync(connection, dlcPath);
        Assert.False(dlc.Ok);
        Assert.True(dlc.NotSupported);
        Assert.True(dlc.CanSendToJd);
        Assert.Contains("JDownloader", dlc.Error);

        var cliDlc = await host.RunCliAsync(null, "remote", "container", dlcPath);
        Assert.Equal(1, cliDlc.ExitCode);
        Assert.Contains("can't be opened", cliDlc.Error);
        Assert.Contains("container-send-jd", cliDlc.Error);

        var cliNetwork = await host.RunCliAsync(null, "remote", "container", @"\\attacker.invalid\share\links.ccf");
        Assert.Equal(2, cliNetwork.ExitCode);
        var cliUsage = await host.RunCliAsync(null, "remote", "container");
        Assert.Equal(2, cliUsage.ExitCode);
        var cliBoth = await host.RunCliAsync(null, "remote", "container", dlcPath, "--all", "--add", "1");
        Assert.Equal(2, cliBoth.ExitCode);
        var cliNonsense = await host.RunCliAsync(null, "remote", "container", dlcPath, "--add", "one");
        Assert.Equal(2, cliNonsense.ExitCode);

        // Refused before anything could reach JDownloader, whether or not it is running here.
        var wrongKind = await connection.AskAsync<RemoteContainerSendResult>(MessageTypes.RemoteContainerSendToJd, new RemoteContainerSendRequest { Path = WriteContainer("links.rsdf", ContainerFixtures.Rsdf("https://example.invalid/a.rar")) });
        Assert.False(wrongKind.Ok);
        Assert.Equal("invalidInput", wrongKind.Status);
        var networkSend = await host.RunCliAsync(null, "remote", "container-send-jd", @"\\attacker.invalid\share\links.dlc");
        Assert.Equal(2, networkSend.ExitCode);

        await host.StopAsync();
        Assert.Equal(0, host.LastExitCode);
    }

    /// <summary>
    /// The route on the real port, with a stand-in for JDownloader. If the port is taken, a real JDownloader
    /// may be running on this machine, and it must not be sent anything, so the test does nothing then.
    /// </summary>
    [Fact]
    public async Task TheJDownloaderRouteOverThePipeSaysWhatReallyHappened()
    {
        var builder = WebApplication.CreateSlimBuilder();
        builder.Logging.ClearProviders();
        builder.WebHost.UseUrls("http://127.0.0.1:9666");
        await using var standIn = builder.Build();
        var received = new List<string>();
        var answer = "success";
        standIn.MapGet("/flash/", () => "JDownloader");
        standIn.MapPost("/flash/addcrypted", async (HttpContext context) =>
        {
            var form = await context.Request.ReadFormAsync();
            lock (received) received.Add(form["crypted"].ToString());
            return answer;
        });

        try
        {
            await standIn.StartAsync();
        }
        catch (IOException)
        {
            return;
        }

        await using var host = await StartHostAsync();
        await using var connection = await host.ConnectAsync();
        var text = ContainerFixtures.Dlc();
        var path = WriteContainer("links.dlc", Encoding.ASCII.GetBytes(text));

        var sent = await connection.AskAsync<RemoteContainerSendResult>(MessageTypes.RemoteContainerSendToJd, new RemoteContainerSendRequest { Path = path });
        Assert.True(sent.Ok, sent.Message);
        Assert.Equal("sent", sent.Status);
        Assert.True(sent.JdAnswered);
        Assert.Equal(text, Assert.Single(received));

        answer = "failed";
        var refused = await host.RunCliAsync(null, "remote", "container-send-jd", path);
        Assert.Equal(1, refused.ExitCode);
        Assert.Contains("did not accept", refused.Error);

        await standIn.StopAsync();
        var absent = await connection.AskAsync<RemoteContainerSendResult>(MessageTypes.RemoteContainerSendToJd, new RemoteContainerSendRequest { Path = path });
        Assert.False(absent.Ok);
        Assert.False(absent.JdAnswered);
        Assert.Equal("notRunning", absent.Status);

        // None of it involved the provider, and no job came of it.
        Assert.Empty((await connection.AskAsync<RemoteListResult>(MessageTypes.RemoteList, new { })).Jobs);
        Assert.Equal(0, Mock.TotalApiCalls);
    }

    // ---- Scenario G ---------------------------------------------------------------------------

    [RarFact]
    public async Task ScenarioG_APasswordRarFromTheProviderAsksAndExtractsWhenToldAndNeverAsksWhenToldFirst()
    {
        await using var host = await StartHostAsync();
        await using var connection = await ReadyAsync(host);
        var built = await ArchiveTools.BuildRar5Async(_scratch, "asked.rar", [("asked.txt", Inner)], "-hp" + ArchivePassword);
        var builtToo = await ArchiveTools.BuildRar5Async(_scratch, "told.rar", [("told.txt", Inner)], "-p" + ArchivePassword);
        var builtLater = await ArchiveTools.BuildRar5Async(_scratch, "later.rar", [("later.txt", Inner)], "-hp" + ArchivePassword);

        // Nothing said in advance: downloaded, detected, asked.
        var asked = Mock.AddItem(RemoteItemKind.Torrent, "Asked", [Volume(Mock.NextFileId(), "Asked/asked.rar", built["asked.rar"])]);
        var first = await connection.AskAsync<RemoteAddResultDto>(MessageTypes.RemoteAdd, new RemoteAddRequestDto
        {
            Input = $"https://torbox.app/download?id={asked.Id}&type=torrents",
            Destination = host.Destination,
            Selection = "all",
        });
        Assert.True(first.Ok, first.Error);
        Assert.False(first.Job!.HasArchivePassword);

        var needs = await UntilSetAsync(connection, host, set => set.FailureKind == "passwordRequired", "the set to ask for its password");
        Assert.True(needs.NeedsPassword);
        Assert.Contains("Password required", needs.StateText);
        var folder = Path.Combine(host.Destination, "Asked");
        Assert.True(File.Exists(Path.Combine(folder, "asked.rar")), "A missing password says nothing against the download.");
        Assert.Equal("completed", Assert.Single((await connection.AskAsync<SnapshotDto>(MessageTypes.JobsQuery, new { })).Jobs).State);

        // A wrong one is refused, the archive stays, and a right one finishes it, through the job.
        var wrong = await connection.AskAsync<RemoteActionResultDto>(MessageTypes.RemoteArchivePassword, new RemoteArchivePasswordRequest { JobId = first.Job.Id, Password = "Invented-Wrong-Pw" });
        Assert.True(wrong.Ok, wrong.Error);
        await UntilSetAsync(connection, host, set => set.FailureKind == "wrongPassword", "a wrong password to be refused");
        var right = await connection.AskAsync<RemoteActionResultDto>(MessageTypes.RemoteArchivePassword, new RemoteArchivePasswordRequest { JobId = first.Job.Id, Password = ArchivePassword });
        Assert.True(right.Ok, right.Error);
        var done = await UntilSetAsync(connection, host, set => set.Name == "asked" && set.State == "completed", "the right password to work");
        Assert.Contains("Extraction completed", done.StateText);
        Assert.Equal(Inner, await File.ReadAllBytesAsync(Path.Combine(folder, "asked.txt")));

        // Said at remote.add: no question at all, and it is the provider that never hears it.
        var told = Mock.AddItem(RemoteItemKind.Torrent, "Told", [Volume(Mock.NextFileId(), "Told/told.rar", builtToo["told.rar"])]);
        var second = await connection.AskAsync<RemoteAddResultDto>(MessageTypes.RemoteAdd, new RemoteAddRequestDto
        {
            Input = $"https://torbox.app/download?id={told.Id}&type=torrents",
            Destination = host.Destination,
            Selection = "all",
            ArchivePassword = ArchivePassword,
        });
        Assert.True(second.Ok, second.Error);
        Assert.True(second.Job!.HasArchivePassword);
        await UntilSetAsync(connection, host, set => set.Name == "told" && set.State == "completed", "the set told in advance");
        Assert.Equal(Inner, await File.ReadAllBytesAsync(Path.Combine(host.Destination, "Told", "told.txt")));

        // Said by the separate command while the item is still being fetched, before any download exists.
        var laterLink = Mock.HosterUrl("file/later.rar");
        Mock.ScriptSubmission(Md5(laterLink), [Volume(Mock.NextFileId(), "later.rar/later.rar", builtLater["later.rar"])], [MockTorBoxState.Queued, MockTorBoxState.Downloading(0.5, eta: 5), MockTorBoxState.Ready]);
        var third = await connection.AskAsync<RemoteAddResultDto>(MessageTypes.RemoteAdd, new RemoteAddRequestDto { Input = laterLink, Destination = host.Destination, ApproveUncached = true });
        Assert.True(third.Ok, third.Error);
        await UntilJobAsync(connection, third.Job!.Id, job => job.State == "waiting", "the link to be submitted");
        var early = await connection.AskAsync<RemoteActionResultDto>(MessageTypes.RemoteArchivePassword, new RemoteArchivePasswordRequest { JobId = third.Job.Id, Password = ArchivePassword });
        Assert.True(early.Ok, early.Error);
        Assert.True(early.Job!.HasArchivePassword);
        Assert.Null(early.Job.PackageId);
        await UntilJobAsync(connection, third.Job.Id, job => job.State == "localStarted", "the link to be ready", checkNow: true);
        await UntilSetAsync(connection, host, set => set.Name == "later" && set.State == "completed", "the set told before its download existed");
        Assert.Equal(Inner, await File.ReadAllBytesAsync(Path.Combine(host.Destination, "later.rar", "later.txt")));

        Assert.All(Mock.Submissions, submission => Assert.Null(submission.Password));
        await host.StopAsync();
        Assert.Equal(0, host.LastExitCode);
        host.AssertNowhere(Key, ArchivePassword, "Invented-Wrong-Pw", "/torbox-cdn/");
    }

    [RarFact]
    public async Task ScenarioG_ManualExtractionUsesThePasswordTooWhenAutoExtractIsOff()
    {
        await using var host = await StartHostAsync();
        await using var connection = await ReadyAsync(host, autoExtract: false);
        var built = await ArchiveTools.BuildRar5Async(_scratch, "manual.rar", [("manual.txt", Inner)], "-hp" + ArchivePassword);
        var item = Mock.AddItem(RemoteItemKind.Torrent, "Manual", [Volume(Mock.NextFileId(), "Manual/manual.rar", built["manual.rar"])]);
        var added = await connection.AskAsync<RemoteAddResultDto>(MessageTypes.RemoteAdd, new RemoteAddRequestDto
        {
            Input = $"https://torbox.app/download?id={item.Id}&type=torrents",
            Destination = host.Destination,
            Selection = "all",
            ArchivePassword = ArchivePassword,
        });
        Assert.True(added.Ok, added.Error);

        // Nothing runs until asked: the setting is the user's, and a password doesn't change it.
        var downloaded = await UntilSetAsync(connection, host, set => set.State == "downloaded", "the download to finish");
        Assert.True(downloaded.HasPassword);
        var job = await UntilJobAsync(connection, added.Job!.Id, candidate => candidate.State == "localStarted", "the local job");

        var asked = await connection.AskAsync<ArchiveExtractResult>(ArchiveMessageTypes.Extract, new ArchiveExtractRequest { PackageId = job.PackageId });
        Assert.True(asked.Ok, asked.Error);
        await UntilSetAsync(connection, host, set => set.State == "completed", "the extraction that was asked for");
        Assert.Equal(Inner, await File.ReadAllBytesAsync(Path.Combine(host.Destination, "Manual", "manual.txt")));
    }

    // ---- Scenario H ---------------------------------------------------------------------------

    private async Task<(MockTorBoxItem Item, List<MockTorBoxFile> Files, byte[] Payload)> MultipartItemAsync(string name, int seed)
    {
        var payload = ArchiveTools.RandomBytes(400_000, seed);
        var built = await ArchiveTools.BuildRar5Async(_scratch, $"{name}.rar", [($"{name}.bin", payload)], "-v96k");
        Assert.True(built.Count >= 4, string.Join(", ", built.Keys));
        var files = built.Keys.Select(volume => Volume(Mock.NextFileId(), $"{name}/{volume}", built[volume])).ToList();
        return (Mock.AddItem(RemoteItemKind.Torrent, name, files), files, payload);
    }

    private static async Task SelectAsync(HostConnection connection, Guid job, IEnumerable<MockTorBoxFile> files)
    {
        var chosen = await connection.AskAsync<RemoteActionResultDto>(MessageTypes.RemoteSelect, new RemoteSelectRequest { JobId = job, FileIds = files.Select(file => file.Id.ToString()).ToList() });
        Assert.True(chosen.Ok, chosen.Error);
    }

    [RarFact]
    public async Task ScenarioH_AMultiPartRarFromOneItemBecomesOneSetAndOneOutput()
    {
        await using var host = await StartHostAsync();
        await using var connection = await ReadyAsync(host);
        var (item, files, payload) = await MultipartItemAsync("whole", 101);

        var added = await connection.AskAsync<RemoteAddResultDto>(MessageTypes.RemoteAdd, new RemoteAddRequestDto
        {
            Input = $"https://torbox.app/download?id={item.Id}&type=torrents",
            Destination = host.Destination,
            Selection = "all",
        });
        Assert.True(added.Ok, added.Error);

        var set = await UntilSetAsync(connection, host, candidate => candidate.State == "completed", "the whole set");
        Assert.Equal(payload, await File.ReadAllBytesAsync(Path.Combine(host.Destination, "whole", "whole.bin")));
        Assert.Contains(files[0].ShortName, set.Volumes);
        Assert.Single((await connection.AskAsync<ArchiveListResult>(ArchiveMessageTypes.List, new { })).Sets);

        // The volumes and one output, with nothing extracted twice.
        Assert.Equal(files.Select(file => file.ShortName).Append("whole.bin").Select(name => Path.Combine("whole", name)).Order(StringComparer.OrdinalIgnoreCase), FilesUnder(host.Destination));
        var jobs = (await connection.AskAsync<RemoteListResult>(MessageTypes.RemoteList, new { })).Jobs;
        Assert.Equal(set.PackageId, Assert.Single(jobs).PackageId);
        Assert.Equal(files.Count, (await connection.AskAsync<SnapshotDto>(MessageTypes.JobsQuery, new { })).Jobs.Count);
    }

    [RarFact]
    public async Task ScenarioH_SelectingSomeVolumesWaitsForTheGapByNameAndFinishesWhenItIsChosen()
    {
        await using var host = await StartHostAsync();
        await using var connection = await ReadyAsync(host);
        var (item, files, payload) = await MultipartItemAsync("gappy", 102);

        var added = await connection.AskAsync<RemoteAddResultDto>(MessageTypes.RemoteAdd, new RemoteAddRequestDto
        {
            Input = $"https://torbox.app/download?id={item.Id}&type=torrents",
            Destination = host.Destination,
        });
        Assert.True(added.Ok, added.Error);
        await UntilJobAsync(connection, added.Job!.Id, job => job.State == "awaitingSelection", "the choice of files");

        // Everything but the second volume.
        var missing = files[1];
        await SelectAsync(connection, added.Job.Id, files.Where(file => file != missing));

        // Waiting, by the names it has. If the first volume finishes before the next has a job, the tool was asked
        // first and named the gap itself, which stops the set for the moment and is put right the same way.
        var waiting = await UntilSetAsync(
            connection,
            host,
            set => set is { State: "waiting" or "failed", DownloadingVolumes.Count: 0 } && set.MissingVolumes.Contains(missing.ShortName),
            "the set to say which volume it waits for");
        Assert.Contains(missing.ShortName, waiting.StateText);
        Assert.Contains("missing", waiting.StateText, StringComparison.OrdinalIgnoreCase);
        Assert.False(File.Exists(Path.Combine(host.Destination, "gappy", "gappy.bin")));
        Assert.DoesNotContain(files.Where(file => file != missing), file => waiting.MissingVolumes.Contains(file.ShortName));

        // Choosing it as well is all it takes.
        await SelectAsync(connection, added.Job.Id, [missing]);
        await UntilSetAsync(connection, host, set => set.State == "completed", "the set once it has every volume");
        Assert.Equal(payload, await File.ReadAllBytesAsync(Path.Combine(host.Destination, "gappy", "gappy.bin")));
        Assert.Single((await connection.AskAsync<ArchiveListResult>(ArchiveMessageTypes.List, new { })).Sets);
    }

    [RarFact]
    public async Task ScenarioH_LeavingOutTheLastVolumeIsNamedByTheToolAndComesRightWhenChosen()
    {
        await using var host = await StartHostAsync();
        await using var connection = await ReadyAsync(host);
        var (item, files, payload) = await MultipartItemAsync("short", 103);

        var added = await connection.AskAsync<RemoteAddResultDto>(MessageTypes.RemoteAdd, new RemoteAddRequestDto
        {
            Input = $"https://torbox.app/download?id={item.Id}&type=torrents",
            Destination = host.Destination,
        });
        Assert.True(added.Ok, added.Error);
        await UntilJobAsync(connection, added.Job!.Id, job => job.State == "awaitingSelection", "the choice of files");

        // Nothing in the names says there is another volume after these, so only the tool can tell.
        var last = files[^1];
        await SelectAsync(connection, added.Job.Id, files.Where(file => file != last));
        var stopped = await UntilSetAsync(connection, host, set => set.FailureKind == "missingVolume", "the tool to name the missing volume");
        Assert.Contains(last.ShortName, stopped.MissingVolumes);
        Assert.Contains(last.ShortName, stopped.StateText);
        Assert.False(File.Exists(Path.Combine(host.Destination, "short", "short.bin")));

        await SelectAsync(connection, added.Job.Id, [last]);
        await UntilSetAsync(connection, host, set => set.State == "completed", "the set once it has its last volume");
        Assert.Equal(payload, await File.ReadAllBytesAsync(Path.Combine(host.Destination, "short", "short.bin")));
        Assert.Single((await connection.AskAsync<ArchiveListResult>(ArchiveMessageTypes.List, new { })).Sets);
    }
}

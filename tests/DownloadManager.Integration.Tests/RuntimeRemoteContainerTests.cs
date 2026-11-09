using DownloadManager.Tests.Shared;
using System.Net;
using System.Text;
using System.Text.Json;
using DownloadManager.Containers;
using DownloadManager.Containers.Tests;
using DownloadManager.Contracts;
using DownloadManager.Core.Archives;
using DownloadManager.Core.Providers;
using DownloadManager.Persistence;
using DownloadManager.Runtime.Remote;
using TestServer;

namespace DownloadManager.Integration.Tests;

/// <summary>
/// Container import in-process, over real HTTP to the mock, with the coordinator's clock in the test's
/// hands so expiry and the 65 s spacing take no real waiting. The containers are made by the same helpers
/// the reader's own tests use, so these prove what happens to a container's contents, not that the
/// readers open a file made by other software. All values are invented.
/// </summary>
public sealed partial class RuntimeServiceTests
{
    private const string ContainerPassword = "Invented-Archive-Pw_77";

    private sealed class RecordingKeeper : IPackagePasswordKeeper
    {
        private readonly Lock _sync = new();
        private readonly List<(Guid Package, string? Password)> _given = [];

        public Exception? ThrowThis { get; set; }

        /// <summary>Calls that were only offers, whether or not they changed anything.</summary>
        public int Offers { get; private set; }

        public IReadOnlyList<(Guid Package, string? Password)> Given
        {
            get
            {
                lock (_sync) return _given.ToList();
            }
        }

        public Task<string?> SetPackagePasswordAsync(Guid packageId, string? password, bool keepExisting = false, CancellationToken cancellationToken = default)
        {
            if (ThrowThis is { } problem) throw problem;
            lock (_sync)
            {
                if (keepExisting) Offers++;
                if (!keepExisting || _given.All(given => given.Package != packageId)) _given.Add((packageId, password));
            }

            return Task.FromResult<string?>(null);
        }
    }

    /// <summary>Stands in for the socket to JDownloader, so no test here can reach a real one.</summary>
    private sealed class PretendJd(Func<HttpRequestMessage, Task<HttpResponseMessage>> answer) : HttpMessageHandler
    {
        private readonly List<string> _requests = [];

        public IReadOnlyList<string> Requests
        {
            get
            {
                lock (_requests) return _requests.ToList();
            }
        }

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            lock (_requests) _requests.Add($"{request.Method} {request.RequestUri}");
            return answer(request);
        }

        public static PretendJd Answering(string body) => new(request => Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent(request.Method == HttpMethod.Get ? "JDownloader" : body),
        }));
    }

    private sealed class ContainerRig : IAsyncDisposable
    {
        private readonly string _folder;

        private ContainerRig(RemoteRig remote, RemoteCommands commands, ContainerImportService service, RecordingKeeper keeper, string folder)
        {
            Remote = remote;
            Commands = commands;
            Service = service;
            Keeper = keeper;
            _folder = folder;
        }

        public RemoteRig Remote { get; }
        public RemoteCommands Commands { get; }
        public ContainerImportService Service { get; }
        public RecordingKeeper Keeper { get; }
        public MockTorBox Mock => Remote.Mock;

        public static async Task<ContainerRig> CreateAsync(TestServerHost server, string? key = MockKey, ContainerImportOptions? options = null, JDownloaderBridge? bridge = null)
        {
            var keeper = new RecordingKeeper();
            var remote = await RemoteRig.CreateAsync(server, key, options: new RemoteCoordinatorOptions { PackagePasswords = keeper });
            var service = new ContainerImportService(remote.Box.Registry, remote.Jobs, options, remote.Time, line =>
            {
                lock (remote.Log) remote.Log.Add(line);
            }, bridge ?? new JDownloaderBridge(new PretendJd(_ => throw new InvalidOperationException("Nothing in this test should call JDownloader."))));
            var folder = Path.Combine(AppContext.BaseDirectory, "test-work", "containers-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(folder);
            return new ContainerRig(remote, new RemoteCommands(remote.Box.Registry, remote.Jobs, remote.Time, containers: service), service, keeper, folder);
        }

        public string Write(string name, byte[] bytes)
        {
            var path = Path.Combine(_folder, name);
            File.WriteAllBytes(path, bytes);
            return path;
        }

        public Task<RemoteContainerPreviewResult> PreviewAsync(string path) => Commands.ContainerPreviewAsync(new RemoteContainerPreviewRequest { Path = path });

        public Task<RemoteContainerAddResult> AddAsync(Guid previewId, params int[] entries) => Commands.ContainerAddAsync(new RemoteContainerAddRequest
        {
            PreviewId = previewId,
            EntryIds = [.. entries],
            Destination = Remote.Destination,
        });

        /// <summary>An item already on the account for this link, ready, holding one file. Found by its hash, so no creation and no spacing.</summary>
        public (string Link, MockTorBoxFile File) OnAccount(string fileName, int size = 4 * Kb)
        {
            var link = Remote.HosterLink(fileName);
            var file = MockTorBoxFile.Of(Mock.NextFileId(), $"{fileName}/{fileName}", size, seed: 300 + fileName.Length);
            Mock.AddItem(RemoteItemKind.WebDownload, fileName, [file]).Hash = RemoteRig.Md5Of(link);
            return (link, file);
        }

        public async ValueTask DisposeAsync()
        {
            Commands.Dispose();
            await Remote.DisposeAsync();
            try { Directory.Delete(_folder, recursive: true); }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
        }
    }

    private static byte[] MixedCcf(params string[] packages) => ContainerFixtures.Ccf50(ContainerFixtures.CryptLoadXml(packages));

    private static RemoteContainerEntryDto Entry(RemoteContainerPreviewResult preview, int id) => preview.Entries.Single(entry => entry.EntryId == id);

    // ---- Preview ------------------------------------------------------------------------------

    [Fact]
    public async Task HandingOverAnUploadThatIsGoneSaysSoSoTheClientUploadsAgain()
    {
        await using var rig = await ContainerRig.CreateAsync(_server);
        rig.Remote.Jobs.ResolveReference = _ => null;

        var result = await rig.Service.SendToJDownloaderAsync("upload:" + new string('0', 32));

        Assert.Equal(JDownloaderBridgeStatus.InvalidInput, result.Status);
        Assert.Equal(UploadFolder.GoneText, result.Message);
    }

    [Fact]
    public async Task APreviewSortsEveryLinkKeepsPackagesAndShowsNoLinkOrPassword()
    {
        await using var rig = await ContainerRig.CreateAsync(_server);
        var hoster = rig.Remote.HosterLink("named.rar");
        var hash = new string('b', 40);
        const string passkey = "invented-container-passkey";
        var magnet = $"magnet:?xt=urn:btih:{hash}&dn=Pretty%20Name&tr=http%3A%2F%2Ftracker.invalid%2F{passkey}";
        const string ordinary = "https://example.invalid/some/plain.zip?sig=invented-signature";
        const string folder = "https://torbox.app/download?id=4242&type=torrents";
        const string direct = "https://store-1.tb-cdn.io/dld/invented-uuid?token=invented-token";
        const string ed2k = "ed2k://file|invented.iso|1234|ABCDEF|/";
        const string down = "https://rapidgator.net/file/invented-id/down.rar.html";
        const string idOnly = "https://mega.nz/file/invented-handle#invented-mega-key";

        var path = rig.Write("mixed.ccf", MixedCcf(
            ContainerFixtures.PackageXml("Movie Pack", ContainerPassword, $"pw is {ContainerPassword} ok",
                ContainerFixtures.DownloadXml(hoster, "movie.part1.rar", "12345"),
                ContainerFixtures.DownloadXml(magnet),
                ContainerFixtures.DownloadXml(folder)),
            ContainerFixtures.PackageXml("Others", null, null,
                ContainerFixtures.DownloadXml(ordinary),
                ContainerFixtures.DownloadXml(direct),
                ContainerFixtures.DownloadXml(ed2k),
                ContainerFixtures.DownloadXml(down),
                ContainerFixtures.DownloadXml(idOnly))));

        var preview = await rig.PreviewAsync(path);
        Assert.True(preview.Ok, preview.Error);
        Assert.Equal("ccf", preview.Format);
        Assert.Equal("5.0", preview.FormatVersion);
        Assert.Null(preview.AccountProblem);
        Assert.NotNull(preview.PreviewId);
        Assert.NotNull(preview.ExpiresAt);

        Assert.Equal(2, preview.Packages.Count);
        var pack = preview.Packages[0];
        Assert.Equal("Movie Pack", pack.Name);
        Assert.True(pack.HasArchivePassword);
        Assert.Equal(3, pack.EntryCount);
        Assert.Contains("pw is", pack.Comment);
        Assert.False(preview.Packages[1].HasArchivePassword);
        Assert.Equal(5, preview.Packages[1].EntryCount);

        var one = Entry(preview, 1);
        Assert.Equal("hosterLink", one.Kind);
        Assert.True(one.CanAdd);
        Assert.Null(one.Reason);
        Assert.Equal(0, one.PackageIndex);
        Assert.Equal("127.0.0.1", one.DisplayHost);
        Assert.Equal("movie.part1.rar", one.DisplayName);
        Assert.Equal(12345, one.Size);

        var two = Entry(preview, 2);
        Assert.Equal("magnet", two.Kind);
        Assert.True(two.CanAdd);
        Assert.Equal("Pretty Name", two.DisplayName);
        Assert.Null(two.DisplayHost);

        Assert.Equal("folderLink", Entry(preview, 3).Kind);
        Assert.True(Entry(preview, 3).CanAdd);

        var plain = Entry(preview, 4);
        Assert.Equal("unsupported", plain.Kind);
        Assert.False(plain.CanAdd);
        Assert.Contains("TorBox won't take this", plain.Reason);
        Assert.Contains("normal download", plain.Reason);
        Assert.Equal("example.invalid", plain.DisplayHost);
        Assert.Equal("plain.zip", plain.DisplayName);
        Assert.Equal(1, plain.PackageIndex);

        Assert.Equal("unsupported", Entry(preview, 5).Kind);
        Assert.Contains("account key", Entry(preview, 5).Reason);
        Assert.False(Entry(preview, 5).CanAdd);
        Assert.Empty(preview.Problems);

        Assert.Equal("unsupported", Entry(preview, 6).Kind);
        Assert.NotNull(Entry(preview, 6).Reason);

        // A site the provider says is down stays a hoster link, and says so.
        Assert.Equal("hosterLink", Entry(preview, 7).Kind);
        Assert.Contains("unavailable", Entry(preview, 7).Reason);

        // Its last segment isn't a file's name, it is the file's id on the site: as good as the link.
        Assert.Equal("hosterLink", Entry(preview, 8).Kind);
        Assert.Null(Entry(preview, 8).DisplayName);

        // Only the hoster list was asked for, and nothing about any of these went to the provider.
        Assert.Empty(rig.Mock.Submissions);
        Assert.Equal(0, rig.Mock.Calls("GET torrents/mylist"));

        var everything = JsonSerializer.Serialize(preview, IpcJson.Options);
        foreach (var secret in new[]
        {
            ContainerPassword, hoster, "invented-fragment", magnet, "magnet:?", passkey, "tracker.invalid", ordinary, "invented-signature", folder,
            "id=4242", direct, "invented-uuid", "invented-token", ed2k, "invented.iso", "invented-id", "invented-handle", "invented-mega-key",
        })
        {
            Assert.False(everything.Contains(secret, StringComparison.OrdinalIgnoreCase), $"A secret of length {secret.Length} is in the preview.");
        }
    }

    [Fact]
    public async Task WithNoKeyThePreviewSaysSoAndCallsNothing()
    {
        await using var rig = await ContainerRig.CreateAsync(_server, key: null);
        var path = rig.Write("mixed.rsdf", ContainerFixtures.Rsdf(
            "https://example.invalid/plain.zip",
            rig.Remote.HosterLink("hoster.rar"),
            $"magnet:?xt=urn:btih:{new string('c', 40)}",
            "https://torbox.app/download?id=1&type=torrents"));

        var preview = await rig.PreviewAsync(path);
        Assert.True(preview.Ok, preview.Error);
        Assert.Contains("No TorBox API key", preview.AccountProblem);
        Assert.Equal(0, rig.Mock.TotalApiCalls);
        Assert.Null(preview.PreviewId);
        Assert.All(preview.Entries, entry =>
        {
            Assert.False(entry.CanAdd);
            Assert.Equal("unsupported", entry.Kind);
            Assert.Contains("No TorBox API key", entry.Reason);
        });

        // Set the key, and the same file reads differently.
        await rig.Remote.Box.Account.SetKeyAsync(MockKey);
        var again = await rig.PreviewAsync(path);
        Assert.Null(again.AccountProblem);
        Assert.NotNull(again.PreviewId);
        Assert.Equal(["unsupported", "hosterLink", "magnet", "folderLink"], again.Entries.Select(entry => entry.Kind));
    }

    [Fact]
    public async Task WithNoListOfSitesALinkIsUndeterminedAndNeverCalledOrdinary()
    {
        await using var rig = await ContainerRig.CreateAsync(_server);
        rig.Mock.Fail("GET webdl/hosters", MockFault.Status5xx());
        var path = rig.Write("links.rsdf", ContainerFixtures.Rsdf("https://hoster.invalid/file/x.rar"));

        var preview = await rig.PreviewAsync(path);
        var entry = Assert.Single(preview.Entries);
        Assert.Equal("undetermined", entry.Kind);
        Assert.Contains("could not be reached", entry.Reason);

        // It can be tried, and the coordinator gives the same real answer without creating anything.
        Assert.True(entry.CanAdd);
        var added = await rig.AddAsync(preview.PreviewId!.Value, entry.EntryId);
        Assert.Contains("could not be reached", Assert.Single(added.Outcomes).Error);
        Assert.Empty(rig.Remote.Jobs.List());
    }

    [Fact]
    public async Task AContainerCanNotNameAFileOnThisComputer()
    {
        await using var rig = await ContainerRig.CreateAsync(_server);
        var secretFile = rig.Write("private.torrent", Encoding.ASCII.GetBytes("d4:infod6:lengthi1e4:name3:abc12:piece lengthi16384e6:pieces20:aaaaaaaaaaaaaaaaaaaaee"));
        var path = rig.Write("local.ccf", MixedCcf(ContainerFixtures.PackageXml("P", null, null,
            ContainerFixtures.DownloadXml(new Uri(secretFile).AbsoluteUri),
            ContainerFixtures.DownloadXml(secretFile),
            ContainerFixtures.DownloadXml(@"\\attacker.invalid\share\x.torrent"))));

        var preview = await rig.PreviewAsync(path);
        Assert.DoesNotContain(preview.Entries, entry => entry.CanAdd);
        Assert.Null(preview.PreviewId);
        Assert.Empty(rig.Mock.Submissions);
        Assert.Empty(rig.Remote.Jobs.List());
    }

    [Theory]
    [InlineData("empty.rsdf", "")]
    [InlineData("blank.rsdf", "   \r\n  ")]
    [InlineData("words.rsdf", "this is not hex at all")]
    [InlineData("oddhex.rsdf", "ABC")]
    [InlineData("garbagehex.rsdf", "00112233445566778899AABBCCDDEEFF")]
    [InlineData("page.rsdf", "<html><body>Not found</body></html>")]
    [InlineData("empty.ccf", "")]
    [InlineData("words.ccf", "this is not a ccf file")]
    [InlineData("page.ccf", "<html><body>Not found</body></html>")]
    [InlineData("truncated.ccf", "CCF3.0abc")]
    [InlineData("magiconly.ccf", "CCF3.0")]
    [InlineData("empty.dlc", "")]
    [InlineData("words.dlc", "not base64 at all !!")]
    [InlineData("short.dlc", "QUJD")]
    [InlineData("page.dlc", "<html><body>Not found</body></html>")]
    public async Task AMalformedFileGetsARefusalInWordsAndNothingIsHeld(string name, string text)
    {
        await using var rig = await ContainerRig.CreateAsync(_server);
        var preview = await rig.PreviewAsync(rig.Write(name, Encoding.Latin1.GetBytes(text)));

        Assert.False(preview.Ok);
        Assert.False(string.IsNullOrWhiteSpace(preview.Error));
        Assert.DoesNotContain(rig.Write("x.txt", []), preview.Error);
        Assert.Empty(preview.Entries);
        Assert.Null(preview.PreviewId);
        Assert.Equal(0, rig.Service.HeldPreviews);
    }

    [Fact]
    public async Task RandomBytesInACcfAndATooLargeFileAndAMissingFileAreRefusedInWords()
    {
        var limits = new ContainerImportOptions { Limits = new ContainerLimits { MaxInputBytes = 4096 } };
        await using var rig = await ContainerRig.CreateAsync(_server, options: limits);
        var random = new byte[2048];
        new Random(5).NextBytes(random);

        var noise = await rig.PreviewAsync(rig.Write("noise.ccf", random));
        Assert.False(noise.Ok);
        Assert.False(string.IsNullOrWhiteSpace(noise.Error));

        var large = await rig.PreviewAsync(rig.Write("large.rsdf", new byte[8192]));
        Assert.False(large.Ok);
        Assert.Contains("larger than", large.Error);

        var missing = await rig.PreviewAsync(rig.Write("gone.ccf", []).Replace("gone.ccf", "really-gone.ccf", StringComparison.Ordinal));
        Assert.False(missing.Ok);
        Assert.Contains("does not exist", missing.Error);
    }

    [Theory]
    [InlineData(@"\\attacker.invalid\share\links.ccf", "network path")]
    [InlineData(@"//attacker.invalid/share/links.ccf", "network path")]
    [InlineData(@"\\?\UNC\attacker.invalid\share\links.ccf", "network path")]
    [InlineData(@"\\.\pipe\links.ccf", "network path")]
    [InlineData(@"\??\UNC\attacker.invalid\share\links.ccf", "network path")]
    [InlineData(@"links.ccf", "full path")]
    [InlineData(@"\rooted\links.ccf", "full path")]
    [InlineData(@"C:links.ccf", "full path")]
    [InlineData("", "Choose")]
    [InlineData(@"C:\folder\links.exe", ".rsdf, .ccf and .dlc")]
    [InlineData(@"C:\folder\links.ccf:stream", ".rsdf, .ccf and .dlc")]
    public async Task APathThatIsNotOnADriveHereIsNeverOpened(string path, string expected)
    {
        var jd = PretendJd.Answering("success");
        await using var rig = await ContainerRig.CreateAsync(_server, bridge: new JDownloaderBridge(jd));

        // A drive path stands for a full path here; on Linux that is the same path from /.
        if (path.StartsWith(@"C:\", StringComparison.Ordinal)) path = TestPaths.Native(path);
        var preview = await rig.PreviewAsync(path);
        Assert.False(preview.Ok);
        Assert.Contains(expected, preview.Error);

        var sent = await rig.Commands.ContainerSendToJdAsync(new RemoteContainerSendRequest { Path = path });
        Assert.False(sent.Ok);
        Assert.False(sent.JdAnswered);
        Assert.Equal("invalidInput", sent.Status);
        Assert.Empty(jd.Requests);
    }

    [Fact]
    public void TheClassifierRefusesTheSameFormsForATorrentPath()
    {
        Assert.Equal(RemoteInputKind.Unsupported, RemoteInputClassifier.Classify(@"\??\UNC\attacker.invalid\share\a.torrent", []).Kind);
        Assert.True(RemoteInputClassifier.IsNetworkOrDevicePath(@"\\?\C:\a.torrent"));
        Assert.False(RemoteInputClassifier.IsNetworkOrDevicePath(@"C:\a.torrent"));
    }

    [Fact]
    public async Task ATooLongListIsCutWithAProblemSaidAndMemoryIsBounded()
    {
        var bounded = new ContainerImportOptions { Limits = new ContainerLimits { MaxLinks = 2 } };
        await using (var rig = await ContainerRig.CreateAsync(_server, options: bounded))
        {
            var preview = await rig.PreviewAsync(rig.Write("many.rsdf", ContainerFixtures.Rsdf(
                rig.Remote.HosterLink("a.rar"), rig.Remote.HosterLink("b.rar"), rig.Remote.HosterLink("c.rar"))));
            Assert.True(preview.Ok, preview.Error);
            Assert.Equal(2, preview.Entries.Count);
            Assert.Contains(preview.Problems, problem => problem.Contains("more than 2", StringComparison.Ordinal));
        }

        await using (var tiny = await ContainerRig.CreateAsync(_server, options: new ContainerImportOptions { MostHeldChars = 16 }))
        {
            var preview = await tiny.PreviewAsync(tiny.Write("big.rsdf", ContainerFixtures.Rsdf(tiny.Remote.HosterLink("long-enough-to-pass-the-bound.rar"))));
            Assert.False(preview.Ok);
            Assert.Contains("too much memory", preview.Error);
            Assert.Equal(0, tiny.Service.HeldPreviews);
        }

        await using var few = await ContainerRig.CreateAsync(_server, options: new ContainerImportOptions { MostPreviews = 2 });
        var path = few.Write("one.rsdf", ContainerFixtures.Rsdf(few.Remote.HosterLink("a.rar")));
        var first = await few.PreviewAsync(path);
        few.Remote.Time.Advance(TimeSpan.FromSeconds(1));
        await few.PreviewAsync(path);
        few.Remote.Time.Advance(TimeSpan.FromSeconds(1));
        var third = await few.PreviewAsync(path);
        Assert.Equal(2, few.Service.HeldPreviews);

        // The oldest went, and says so in the way an expired one does.
        Assert.Contains("expired", (await few.AddAsync(first.PreviewId!.Value, 1)).Error);
        Assert.True((await few.AddAsync(third.PreviewId!.Value, 1)).Ok);
    }

    [Fact]
    public async Task APreviewForgetsItsLinksWhenItExpires()
    {
        await using var rig = await ContainerRig.CreateAsync(_server);
        var (link, _) = rig.OnAccount("late.rar");
        var preview = await rig.PreviewAsync(rig.Write("late.rsdf", ContainerFixtures.Rsdf(link)));
        Assert.Equal(1, rig.Service.HeldPreviews);
        Assert.Equal(rig.Remote.Time.GetUtcNow() + TimeSpan.FromMinutes(15), preview.ExpiresAt);

        rig.Remote.Time.Advance(TimeSpan.FromMinutes(14));
        Assert.Equal(1, rig.Service.HeldPreviews);

        rig.Remote.Time.Advance(TimeSpan.FromMinutes(2));
        Assert.Equal(0, rig.Service.HeldPreviews);

        var late = await rig.AddAsync(preview.PreviewId!.Value, 1);
        Assert.False(late.Ok);
        Assert.Contains("expired", late.Error);
        Assert.Empty(late.Outcomes);
        Assert.Empty(rig.Remote.Jobs.List());

        var unknown = await rig.AddAsync(Guid.NewGuid(), 1);
        Assert.Contains("expired or isn't known", unknown.Error);
    }

    // ---- Adding -------------------------------------------------------------------------------

    [Fact]
    public async Task OnlyTheChosenEntriesReachTheProviderThroughTheSameChecksAsAPastedLink()
    {
        await using var rig = await ContainerRig.CreateAsync(_server);
        var (cached, cachedHash, cachedFile) = rig.Remote.ScriptHoster("cached", MockTorBoxState.CachedReady);
        rig.Mock.AddCached(cachedHash);
        var (uncached, _, _) = rig.Remote.ScriptHoster("uncached", MockTorBoxState.Downloading(0.5), MockTorBoxState.Ready);
        var (left, _, _) = rig.Remote.ScriptHoster("left-out", MockTorBoxState.Ready);
        const string ordinary = "https://example.invalid/plain.zip";
        var path = rig.Write("mixed.rsdf", ContainerFixtures.Rsdf(cached, uncached, left, ordinary));

        var preview = await rig.PreviewAsync(path);
        var id = preview.PreviewId!.Value;

        var added = await rig.AddAsync(id, 1, 2, 4, 99);
        Assert.True(added.Ok);
        Assert.Equal(4, added.Outcomes.Count);
        Assert.True(added.Outcomes[0].Ok);
        Assert.Equal("hosterLink", added.Outcomes[0].InputKind);
        Assert.True(added.Outcomes[1].Ok);
        Assert.False(added.Outcomes[2].Ok);
        Assert.Contains("normal download", added.Outcomes[2].Error);
        Assert.False(added.Outcomes[3].Ok);
        Assert.Contains("no such entry", added.Outcomes[3].Error);
        Assert.Equal(2, rig.Remote.Jobs.List().Count);

        // Added only saves the intent. Nothing has gone anywhere yet.
        Assert.Empty(rig.Mock.Submissions);

        await rig.Remote.RunAsync();
        var sent = Assert.Single(rig.Mock.Submissions);
        Assert.Equal(cached, sent.Link);
        Assert.Null(sent.Password);

        // The uncached one asks first, as a pasted link does.
        var asking = rig.Remote.View(added.Outcomes[1].Job!.Id);
        Assert.True(asking.ApprovalNeeded);
        Assert.Contains("Not cached", asking.Status);

        // Approved, it takes its turn: the 65 s spacing is the account's, wherever a link came from.
        Assert.True((await rig.Commands.RetryAsync(new RemoteRetryRequest { JobId = asking.Id, ApproveUncached = true })).Ok);
        await rig.Remote.RunAsync();
        Assert.Single(rig.Mock.Submissions);
        await rig.Remote.AdvanceAsync(66);
        Assert.Equal(2, rig.Mock.Submissions.Count);
        Assert.Equal(uncached, rig.Mock.Submissions[1].Link);

        Assert.DoesNotContain(rig.Mock.Submissions, submission => submission.Link == left || submission.Link == ordinary);
        Assert.Equal(0, rig.Mock.HosterRequests);

        // Nothing was ever requested directly: the only local job is the provider's.
        var child = Assert.Single(rig.Remote.Children());
        Assert.StartsWith("remote://torbox/webdl/", child.Url);
        Assert.Equal("remote", child.Source);
        await rig.Remote.AssertCompletedAsync(cachedFile, "cached.bin");

        // Adding it again is the job it already is, in this preview and in a new one.
        var repeat = await rig.AddAsync(id, 1);
        Assert.True(Assert.Single(repeat.Outcomes).Existing);
        var imported = await rig.PreviewAsync(path);
        var twin = await rig.AddAsync(imported.PreviewId!.Value, 1);
        Assert.True(Assert.Single(twin.Outcomes).Existing);
        Assert.Equal(2, rig.Remote.Jobs.List().Count);
        Assert.Equal(2, rig.Mock.Submissions.Count);

        await rig.Remote.AssertNothingLeakedAsync(cached, uncached, left, "invented-fragment");
    }

    [Fact]
    public async Task ApprovalGivenAtAddMeansNoQuestionAndAnEmptyChoiceIsRefused()
    {
        await using var rig = await ContainerRig.CreateAsync(_server);
        var (link, _, _) = rig.Remote.ScriptHoster("agreed", MockTorBoxState.Downloading(0.5));
        var preview = await rig.PreviewAsync(rig.Write("agreed.rsdf", ContainerFixtures.Rsdf(link)));

        Assert.Contains("at least one", (await rig.AddAsync(preview.PreviewId!.Value)).Error);

        var added = await rig.Commands.ContainerAddAsync(new RemoteContainerAddRequest
        {
            PreviewId = preview.PreviewId!.Value,
            EntryIds = [1],
            Destination = rig.Remote.Destination,
            ApproveUncached = true,
        });
        Assert.True(added.Ok, added.Error);
        await rig.Remote.RunAsync();
        Assert.Equal(link, Assert.Single(rig.Mock.Submissions).Link);
        Assert.False(rig.Mock.Submissions[0].OnlyIfCached);
    }

    [Fact]
    public async Task APackagesLinksShareALocalPackageAndOneFolderWhateverTheProviderCallsThem()
    {
        await using var rig = await ContainerRig.CreateAsync(_server);
        var (one, oneFile) = rig.OnAccount("movie.part1.rar");
        var (two, twoFile) = rig.OnAccount("movie.part2.rar");
        var (other, otherFile) = rig.OnAccount("other.bin");
        var path = rig.Write("pack.ccf", MixedCcf(
            ContainerFixtures.PackageXml("Movie Pack", null, "a comment",
                ContainerFixtures.DownloadXml(one, "movie.part1.rar"),
                ContainerFixtures.DownloadXml(two, "movie.part2.rar")),
            ContainerFixtures.PackageXml("Elsewhere", null, null, ContainerFixtures.DownloadXml(other))));

        var preview = await rig.PreviewAsync(path);
        var added = await rig.AddAsync(preview.PreviewId!.Value, 1, 2, 3);
        Assert.Equal(3, added.Outcomes.Count(outcome => outcome.Ok));

        // A name to show before the provider has said anything.
        Assert.Equal("movie.part1.rar", added.Outcomes[0].Job!.Name);

        await rig.Remote.RunAsync();
        var jobs = rig.Remote.Jobs.List();
        Assert.All(jobs, job => Assert.Equal(RemoteJobState.LocalStarted, job.State));
        Assert.Empty(rig.Mock.Submissions);

        var packs = jobs.Select(job => job.PackageId).Distinct().ToList();
        Assert.Equal(2, packs.Count);
        var movie = jobs.Single(job => job.Name == "movie.part1.rar").PackageId;
        Assert.Equal(movie, jobs.Single(job => job.Name == "movie.part2.rar").PackageId);

        var group = Assert.Single(rig.Remote.Ctx.Service.GetDownloadGroups(), candidate => candidate.Id == movie);
        Assert.Equal("Movie Pack", group.Name);
        Assert.Equal(2, group.JobIds.Count);

        await rig.Remote.AssertCompletedAsync(oneFile, "Movie Pack", "movie.part1.rar");
        await rig.Remote.AssertCompletedAsync(twoFile, "Movie Pack", "movie.part2.rar");
        await rig.Remote.AssertCompletedAsync(otherFile, "Elsewhere", "other.bin");
    }

    [Fact]
    public async Task LinksInNoPackageMeetInTheDestinationAndAnItemWithManyFilesKeepsItsFolders()
    {
        await using var rig = await ContainerRig.CreateAsync(_server);
        var (one, oneFile) = rig.OnAccount("loose.part1.rar");
        var (two, twoFile) = rig.OnAccount("loose.part2.rar");

        var manyLink = rig.Remote.HosterLink("many");
        var a = MockTorBoxFile.Of(rig.Mock.NextFileId(), "many/sub/a.bin", 2 * Kb, 61);
        var b = MockTorBoxFile.Of(rig.Mock.NextFileId(), "many/b.bin", 2 * Kb, 62);
        rig.Mock.AddItem(RemoteItemKind.WebDownload, "many", [a, b]).Hash = RemoteRig.Md5Of(manyLink);

        var preview = await rig.PreviewAsync(rig.Write("My Links.rsdf", ContainerFixtures.Rsdf(one, two, manyLink)));
        Assert.Empty(preview.Packages);
        Assert.All(preview.Entries, entry => Assert.Null(entry.PackageIndex));
        Assert.Equal(3, (await rig.AddAsync(preview.PreviewId!.Value, 1, 2, 3)).Outcomes.Count(outcome => outcome.Ok));

        await rig.Remote.RunAsync();
        await rig.Remote.AssertCompletedAsync(oneFile, "loose.part1.rar");
        await rig.Remote.AssertCompletedAsync(twoFile, "loose.part2.rar");
        await rig.Remote.AssertCompletedAsync(a, "many", "sub", "a.bin");
        await rig.Remote.AssertCompletedAsync(b, "many", "b.bin");

        var group = Assert.Single(rig.Remote.Ctx.Service.GetDownloadGroups());
        Assert.Equal("My Links", group.Name);
    }

    [Theory]
    [InlineData("../../escape")]
    [InlineData(@"..\..\escape")]
    [InlineData("C:/Windows/escape")]
    [InlineData("CON")]
    [InlineData("..")]
    public async Task APackageNameCanNotLeadOutOfTheDestination(string name)
    {
        await using var rig = await ContainerRig.CreateAsync(_server);
        var (link, _) = rig.OnAccount("kept.bin");
        var preview = await rig.PreviewAsync(rig.Write("evil.ccf", MixedCcf(ContainerFixtures.PackageXml(name, null, null, ContainerFixtures.DownloadXml(link)))));
        Assert.True((await rig.AddAsync(preview.PreviewId!.Value, 1)).Ok);

        await rig.Remote.RunAsync();
        var child = Assert.Single(rig.Remote.Children());
        Assert.StartsWith(Path.GetFullPath(rig.Remote.Destination) + Path.DirectorySeparatorChar, Path.GetFullPath(child.FinalFilePath));
        Assert.Equal("kept.bin", Path.GetFileName(child.FinalFilePath));
    }

    // ---- Archive passwords --------------------------------------------------------------------

    [Fact]
    public async Task AContainersPasswordGoesToTheArchiveSideAndNeverToTheProvider()
    {
        await using var rig = await ContainerRig.CreateAsync(_server);
        var (link, hash, file) = rig.Remote.ScriptHoster("locked", MockTorBoxState.CachedReady);
        rig.Mock.AddCached(hash);
        var path = rig.Write("locked.ccf", MixedCcf(ContainerFixtures.PackageXml("Locked", ContainerPassword, null, ContainerFixtures.DownloadXml(link))));

        var preview = await rig.PreviewAsync(path);
        var job = Assert.Single((await rig.AddAsync(preview.PreviewId!.Value, 1)).Outcomes).Job!;
        Assert.True(job.HasArchivePassword);
        Assert.False(job.HasLinkPassword);
        Assert.Empty(rig.Keeper.Given);

        // Encrypted under its own purpose while it waits, and it isn't a link password by any reading.
        var saved = Assert.Single(await new JsonRemoteJobStore(rig.Remote.Ctx.Paths).LoadAllAsync());
        Assert.True(SecretProtector.IsProtected(saved.ProtectedArchivePassword));
        Assert.Equal(ContainerPassword, SecretProtector.UnprotectRemoteArchivePassword(saved.ProtectedArchivePassword));
        Assert.Null(SecretProtector.UnprotectRemoteLinkPassword(saved.ProtectedArchivePassword));
        Assert.Null(SecretProtector.UnprotectArchivePassword(saved.ProtectedArchivePassword));
        Assert.Null(saved.ProtectedLinkPassword);

        await rig.Remote.RunAsync();
        Assert.Null(Assert.Single(rig.Mock.Submissions).Password);
        var view = rig.Remote.View(job.Id);
        Assert.Equal(RemoteJobState.LocalStarted, view.State);
        var (package, password) = Assert.Single(rig.Keeper.Given);
        Assert.Equal(view.PackageId, package);
        Assert.Equal(ContainerPassword, password);

        // One copy is kept, on the archive side. Clients still hear that there is one.
        Assert.True(view.HasArchivePassword);
        var after = Assert.Single(await new JsonRemoteJobStore(rig.Remote.Ctx.Paths).LoadAllAsync());
        Assert.Null(after.ProtectedArchivePassword);
        Assert.True(after.ArchivePasswordHandedOver);

        await rig.Remote.AssertCompletedAsync(file, "Locked", "locked.bin");
        await rig.Remote.AssertNothingLeakedAsync(ContainerPassword, link);
    }

    [Fact]
    public async Task EveryJobOfAPackageOnlyOffersTheContainersPasswordSoWhatTheUserSaidStands()
    {
        await using var rig = await ContainerRig.CreateAsync(_server);
        var (one, _) = rig.OnAccount("offered.part1.rar");
        var (two, _) = rig.OnAccount("offered.part2.rar");
        var path = rig.Write("offered.ccf", MixedCcf(ContainerFixtures.PackageXml("Offered", ContainerPassword, null,
            ContainerFixtures.DownloadXml(one), ContainerFixtures.DownloadXml(two))));
        var preview = await rig.PreviewAsync(path);

        // The first comes through. The user corrects it. Then the second's offer changes nothing.
        var first = Assert.Single((await rig.AddAsync(preview.PreviewId!.Value, 1)).Outcomes).Job!;
        await rig.Remote.RunAsync();
        var package = rig.Remote.View(first.Id).PackageId!.Value;
        Assert.Equal((package, ContainerPassword), Assert.Single(rig.Keeper.Given));

        var corrected = await rig.Commands.SetArchivePasswordAsync(new RemoteArchivePasswordRequest { JobId = first.Id, Password = "Invented-Corrected-Pw" });
        Assert.True(corrected.Ok, corrected.Error);
        Assert.Equal((package, "Invented-Corrected-Pw"), rig.Keeper.Given[^1]);

        Assert.True(Assert.Single((await rig.AddAsync(preview.PreviewId!.Value, 2)).Outcomes).Ok);
        await rig.Remote.RunAsync();
        Assert.Equal(2, rig.Keeper.Offers);
        Assert.Equal(2, rig.Keeper.Given.Count);
        Assert.Equal((package, "Invented-Corrected-Pw"), rig.Keeper.Given[^1]);

        // Added again from the file, the offer doesn't replace what the job has been told either.
        var imported = await rig.PreviewAsync(path);
        Assert.True(Assert.Single((await rig.AddAsync(imported.PreviewId!.Value, 1)).Outcomes).Existing);
        Assert.Equal(2, rig.Keeper.Given.Count);

        // What the user types is never only an offer.
        var again = await rig.Commands.AddAsync(new RemoteAddRequestDto { Input = one, ArchivePassword = "Invented-Typed-Pw" });
        Assert.True(again.Existing);
        Assert.Equal((package, "Invented-Typed-Pw"), rig.Keeper.Given[^1]);
    }

    [Fact]
    public async Task APackageWithSeveralPasswordsUsesTheFirstAndSaysSo()
    {
        await using var rig = await ContainerRig.CreateAsync(_server);
        var (link, _) = rig.OnAccount("two-passwords.rar");
        var xml = ContainerFixtures.CryptLoadXml(ContainerFixtures.PackageXml("Two", ContainerPassword, null, ContainerFixtures.DownloadXml(link))
            .Replace("</Options>", "<Passwort>Invented-Second-Pw</Passwort></Options>", StringComparison.Ordinal));
        var preview = await rig.PreviewAsync(rig.Write("two.ccf", ContainerFixtures.Ccf50(xml)));

        Assert.Contains(preview.Problems, problem => problem.Contains("2 possible archive passwords", StringComparison.Ordinal));
        Assert.DoesNotContain("Invented-Second-Pw", JsonSerializer.Serialize(preview, IpcJson.Options));
        Assert.True((await rig.AddAsync(preview.PreviewId!.Value, 1)).Ok);
        await rig.Remote.RunAsync();
        Assert.Equal(ContainerPassword, Assert.Single(rig.Keeper.Given).Password);
    }

    [Fact]
    public async Task EveryPasswordAPackageStatesIsHiddenInItsEntriesAndNotJustTheFirst()
    {
        await using var rig = await ContainerRig.CreateAsync(_server);
        var (link, _) = rig.OnAccount("masked.rar");
        const string second = "Invented-Second-Pw";
        var xml = ContainerFixtures.CryptLoadXml(ContainerFixtures.PackageXml("Two", ContainerPassword, null, ContainerFixtures.DownloadXml(link, $"file-{second}.rar"))
            .Replace("</Options>", $"<Passwort>{second}</Passwort></Options>", StringComparison.Ordinal));

        var preview = await rig.PreviewAsync(rig.Write("masked.ccf", ContainerFixtures.Ccf50(xml)));
        Assert.DoesNotContain(second, JsonSerializer.Serialize(preview, IpcJson.Options));

        var job = Assert.Single((await rig.AddAsync(preview.PreviewId!.Value, 1)).Outcomes).Job!;
        Assert.DoesNotContain(second, JsonSerializer.Serialize(job, IpcJson.Options));
        var saved = Assert.Single(await new JsonRemoteJobStore(rig.Remote.Ctx.Paths).LoadAllAsync());
        Assert.DoesNotContain(second, JsonSerializer.Serialize(saved));
    }

    [Fact]
    public async Task ClearingAPasswordOnOneJobClearsItForTheWholePackage()
    {
        await using var rig = await ContainerRig.CreateAsync(_server);
        var (one, _) = rig.OnAccount("clear.part1.rar");
        var (two, _) = rig.OnAccount("clear.part2.rar");
        var (three, _) = rig.OnAccount("clear.part3.rar");
        var path = rig.Write("clear.ccf", MixedCcf(ContainerFixtures.PackageXml("Clear", ContainerPassword, null,
            ContainerFixtures.DownloadXml(one), ContainerFixtures.DownloadXml(two), ContainerFixtures.DownloadXml(three))));
        var preview = await rig.PreviewAsync(path);

        // The first has handed its copy over. The others still hold theirs, not having reached a package.
        var first = Assert.Single((await rig.AddAsync(preview.PreviewId!.Value, 1)).Outcomes).Job!;
        await rig.Remote.RunAsync();
        Assert.Equal(ContainerPassword, Assert.Single(rig.Keeper.Given).Password);
        var second = Assert.Single((await rig.AddAsync(preview.PreviewId.Value, 2)).Outcomes).Job!;
        var third = Assert.Single((await rig.AddAsync(preview.PreviewId.Value, 3)).Outcomes).Job!;
        Assert.True(second.HasArchivePassword);
        Assert.True(third.HasArchivePassword);

        var cleared = await rig.Commands.SetArchivePasswordAsync(new RemoteArchivePasswordRequest { JobId = first.Id });
        Assert.True(cleared.Ok, cleared.Error);
        Assert.Null(rig.Keeper.Given[^1].Password);

        Assert.False(rig.Remote.View(first.Id).HasArchivePassword);
        Assert.False(rig.Remote.View(second.Id).HasArchivePassword);
        Assert.False(rig.Remote.View(third.Id).HasArchivePassword);

        // When they reach the package, nothing brings it back.
        var given = rig.Keeper.Given.Count;
        await rig.Remote.RunAsync();
        Assert.Equal(given, rig.Keeper.Given.Count);
        Assert.All(await new JsonRemoteJobStore(rig.Remote.Ctx.Paths).LoadAllAsync(), record =>
        {
            Assert.Null(record.ProtectedArchivePassword);
            Assert.False(record.ArchivePasswordHandedOver);
        });
    }

    [Fact]
    public void AnAddTooLargeForOneAnswerStillSaysHowEachEntryWentAndIsDelivered()
    {
        // The real size of the worst case: a container's full count of entries with long names that escape badly.
        var name = new string('ж', 200);
        var outcomes = Enumerable.Range(1, 5000).Select(index => new RemoteContainerAddOutcomeDto
        {
            EntryId = index,
            Ok = true,
            JobId = Guid.NewGuid(),
            Job = new RemoteJobDto { Id = Guid.NewGuid(), Name = name, Status = name, Provider = "torbox" },
        }).ToList();
        var result = new RemoteContainerAddResult { Ok = true, Outcomes = outcomes };
        Assert.True(IpcMessage.Create(MessageTypes.RemoteContainerAddResult, result).Payload!.Value.GetRawText().Length >= Protocol.MaxFrameBytes);

        var fitted = RemoteCommands.FitInOneAnswer(result);

        Assert.True(fitted.Ok);
        Assert.True(fitted.JobsLeftOut);
        Assert.True(IpcMessage.Create(MessageTypes.RemoteContainerAddResult, fitted).Payload!.Value.GetRawText().Length < Protocol.MaxFrameBytes - 1024);
        Assert.Equal(5000, fitted.Outcomes.Count);
        Assert.All(fitted.Outcomes, outcome =>
        {
            Assert.True(outcome.Ok);
            Assert.NotNull(outcome.JobId);
            Assert.Null(outcome.Job);
        });

        var small = new RemoteContainerAddResult { Ok = true, Outcomes = [new RemoteContainerAddOutcomeDto { EntryId = 1, Ok = true, Job = new RemoteJobDto { Name = name } }] };
        Assert.False(RemoteCommands.FitInOneAnswer(small).JobsLeftOut);
        Assert.NotNull(small.Outcomes[0].Job);
    }

    [Fact]
    public void AWorkingFolderNameFromAContainerOrAProviderBecomesAnOrdinaryFolder()
    {
        Assert.Equal("_.inbound-extract-x", RemotePaths.CleanSegment(".inbound-extract-x"));
        Assert.Equal("_.INBOUND-EXTRACT-X", RemotePaths.CleanSegment(".INBOUND-EXTRACT-X"));
        Assert.Equal(Path.Combine("a", "_.inbound-extract-x", "placed.log"), RemotePaths.ToSafeRelativePath("a/.inbound-extract-x/placed.log"));
    }

    [Fact]
    public async Task AContainersPackageCanNotPlaceFilesInAWorkingFolder()
    {
        await using var rig = await ContainerRig.CreateAsync(_server);
        var (link, file) = rig.OnAccount("placed.log");
        var path = rig.Write("plant.ccf", MixedCcf(ContainerFixtures.PackageXml(".inbound-extract-x", null, null, ContainerFixtures.DownloadXml(link))));

        var preview = await rig.PreviewAsync(path);
        var job = Assert.Single((await rig.AddAsync(preview.PreviewId!.Value, 1)).Outcomes).Job!;
        await rig.Remote.RunAsync();

        Assert.Equal(RemoteJobState.LocalStarted, rig.Remote.View(job.Id).State);
        await rig.Remote.AssertCompletedAsync(file, "_.inbound-extract-x", "placed.log");
        Assert.Empty(Directory.EnumerateDirectories(rig.Remote.Destination, ".inbound-extract-*"));
    }

    [Fact]
    public async Task AnArchivePasswordCanBeSetBeforeOrAfterTheDownloadsExistAndCleared()
    {
        await using var rig = await ContainerRig.CreateAsync(_server);
        // Two files, so it waits for a choice before any download exists.
        var early = rig.Mock.AddItem(RemoteItemKind.Torrent, "Early",
            [MockTorBoxFile.Of(rig.Mock.NextFileId(), "Early/a.rar", 2 * Kb, 71), MockTorBoxFile.Of(rig.Mock.NextFileId(), "Early/b.nfo", Kb, 72)]);

        // Before: at remote.add. Not a link password, and sent nowhere.
        var added = await rig.Commands.AddAsync(new RemoteAddRequestDto
        {
            Input = $"https://torbox.app/download?id={early.Id}&type=torrents",
            Destination = rig.Remote.Destination,
            ArchivePassword = ContainerPassword,
        });
        Assert.True(added.Ok, added.Error);
        Assert.True(added.Job!.HasArchivePassword);
        await rig.Remote.RunAsync();
        Assert.Empty(rig.Keeper.Given);

        // Before, again: on a job waiting for its choice of files. The newest word counts.
        var changed = await rig.Commands.SetArchivePasswordAsync(new RemoteArchivePasswordRequest { JobId = added.Job.Id, Password = "Invented-Newer-Pw" });
        Assert.True(changed.Ok, changed.Error);
        Assert.Empty(rig.Keeper.Given);
        Assert.True((await rig.Commands.SelectAsync(new RemoteSelectRequest { JobId = added.Job.Id, All = true })).Ok);
        await rig.Remote.RunAsync();
        var view = rig.Remote.View(added.Job.Id);
        Assert.Equal(RemoteJobState.LocalStarted, view.State);
        Assert.Equal("Invented-Newer-Pw", Assert.Single(rig.Keeper.Given).Password);

        // After: straight through, to the package it has.
        var later = await rig.Commands.SetArchivePasswordAsync(new RemoteArchivePasswordRequest { JobId = view.Id, Password = "Invented-Later-Pw" });
        Assert.True(later.Ok, later.Error);
        Assert.True(later.Job!.HasArchivePassword);
        Assert.Equal((view.PackageId!.Value, "Invented-Later-Pw"), rig.Keeper.Given[^1]);

        // Empty clears it on both sides.
        var cleared = await rig.Commands.SetArchivePasswordAsync(new RemoteArchivePasswordRequest { JobId = view.Id });
        Assert.True(cleared.Ok, cleared.Error);
        Assert.False(cleared.Job!.HasArchivePassword);
        Assert.Equal((view.PackageId!.Value, (string?)null), rig.Keeper.Given[^1]);

        // Refused in words, without being repeated.
        var bad = await rig.Commands.SetArchivePasswordAsync(new RemoteArchivePasswordRequest { JobId = view.Id, Password = "two\nlines" });
        Assert.False(bad.Ok);
        Assert.DoesNotContain("lines", bad.Error!.Replace("line break", string.Empty, StringComparison.Ordinal));
        var noJob = await rig.Commands.SetArchivePasswordAsync(new RemoteArchivePasswordRequest { JobId = Guid.NewGuid(), Password = "x" });
        Assert.Contains("not on the list", noJob.Error);
        var badAdd = await rig.Commands.AddAsync(new RemoteAddRequestDto { Input = "https://torbox.app/download?id=1&type=torrents", ArchivePassword = "two\nlines" });
        Assert.False(badAdd.Ok);

        await rig.Remote.AssertNothingLeakedAsync(ContainerPassword, "Invented-Newer-Pw", "Invented-Later-Pw");
    }

    [Fact]
    public async Task AddingALinkAlreadyOnTheListWithAPasswordStillUsesThePassword()
    {
        await using var rig = await ContainerRig.CreateAsync(_server);
        var (link, _) = rig.OnAccount("again.rar");
        var first = await rig.Commands.AddAsync(new RemoteAddRequestDto { Input = link, Destination = rig.Remote.Destination });
        await rig.Remote.RunAsync();
        Assert.Equal(RemoteJobState.LocalStarted, rig.Remote.View(first.Job!.Id).State);

        var preview = await rig.PreviewAsync(rig.Write("again.ccf", MixedCcf(ContainerFixtures.PackageXml("Again", ContainerPassword, null, ContainerFixtures.DownloadXml(link)))));
        var outcome = Assert.Single((await rig.AddAsync(preview.PreviewId!.Value, 1)).Outcomes);
        Assert.True(outcome.Existing);
        Assert.Equal(first.Job.Id, outcome.Job!.Id);
        Assert.True(outcome.Job.HasArchivePassword);
        Assert.Equal((rig.Remote.View(first.Job.Id).PackageId!.Value, ContainerPassword), Assert.Single(rig.Keeper.Given));
    }

    [Fact]
    public async Task APasswordThatCanNotBeSavedYetHoldsTheStepAndIsNotLost()
    {
        await using var rig = await ContainerRig.CreateAsync(_server);
        var (link, _) = rig.OnAccount("held.rar");
        var preview = await rig.PreviewAsync(rig.Write("held.ccf", MixedCcf(ContainerFixtures.PackageXml("Held", ContainerPassword, null, ContainerFixtures.DownloadXml(link)))));
        var job = Assert.Single((await rig.AddAsync(preview.PreviewId!.Value, 1)).Outcomes).Job!;

        rig.Keeper.ThrowThis = new IOException("The disk is full, and this text mustn't reach anyone.");
        await rig.Remote.RunAsync();
        var held = rig.Remote.View(job.Id);
        Assert.Equal(RemoteJobState.Waiting, held.State);
        Assert.Contains("could not be saved", held.Status);
        Assert.DoesNotContain("disk is full", held.Status);
        Assert.True(held.HasArchivePassword);
        Assert.Single(rig.Remote.Children());

        // It tries again by itself, and one local job is all that ever comes of it.
        rig.Keeper.ThrowThis = null;
        await rig.Remote.AdvanceAsync(60);
        Assert.Equal(RemoteJobState.LocalStarted, rig.Remote.View(job.Id).State);
        Assert.Equal(ContainerPassword, Assert.Single(rig.Keeper.Given).Password);
        Assert.Single(rig.Remote.Children());
    }

    [Fact]
    public async Task APasswordLeftOnAJobByACrashIsHandedOverAtTheNextStart()
    {
        await using var rig = await ContainerRig.CreateAsync(_server);
        var package = Guid.NewGuid();
        var at = rig.Remote.Time.GetUtcNow();
        await rig.Remote.Store.SaveAsync(new RemoteJobRecord
        {
            Id = Guid.NewGuid(),
            Provider = "torbox",
            Kind = RemoteItemKind.WebDownload,
            State = RemoteJobState.LocalStarted,
            PackageId = package,
            ItemId = "1",
            ProtectedArchivePassword = SecretProtector.ProtectRemoteArchivePassword(ContainerPassword),
            CreatedAt = at,
            UpdatedAt = at,
        });

        await rig.Remote.RestartCoordinatorOnlyAsync();
        Assert.Equal((package, ContainerPassword), Assert.Single(rig.Keeper.Given));
        var saved = Assert.Single(await new JsonRemoteJobStore(rig.Remote.Ctx.Paths).LoadAllAsync());
        Assert.Null(saved.ProtectedArchivePassword);
        Assert.True(saved.ArchivePasswordHandedOver);
    }

    [Fact]
    public async Task RemovingAJobForgetsItsPassword()
    {
        await using var rig = await ContainerRig.CreateAsync(_server);
        var added = await rig.Commands.AddAsync(new RemoteAddRequestDto
        {
            Input = rig.Remote.HosterLink("forgotten"),
            Destination = rig.Remote.Destination,
            ArchivePassword = ContainerPassword,
        });
        Assert.True(added.Ok, added.Error);
        rig.Remote.Store.KillOn = record => record.State == RemoteJobState.Removed;
        rig.Remote.Store.PersistKillingWrite = true;
        await Assert.ThrowsAsync<IOException>(() => rig.Commands.RemoveAsync(new RemoteJobIdRequest { JobId = added.Job!.Id }));

        // The crash came just after this save, and already nothing is left to find.
        var saved = Assert.Single(await new JsonRemoteJobStore(rig.Remote.Ctx.Paths).LoadAllAsync());
        Assert.Null(saved.ProtectedArchivePassword);
    }

    // ---- The optional route -------------------------------------------------------------------

    [Fact]
    public async Task ADlcGetsAClearNoAndTheOtherRouteIsOnlyEverOneThatIsAskedFor()
    {
        var jd = PretendJd.Answering("success");
        await using var rig = await ContainerRig.CreateAsync(_server, bridge: new JDownloaderBridge(jd));
        var path = rig.Write("links.dlc", Encoding.ASCII.GetBytes(ContainerFixtures.Dlc()));

        var preview = await rig.PreviewAsync(path);
        Assert.False(preview.Ok);
        Assert.True(preview.NotSupported);
        Assert.True(preview.CanSendToJd);
        Assert.Equal("dlc", preview.Format);
        Assert.Contains("can't be opened", preview.Error);
        Assert.Contains("JDownloader", preview.Error);
        Assert.Null(preview.PreviewId);
        Assert.Empty(preview.Entries);
        Assert.Empty(jd.Requests);
        Assert.Equal(0, rig.Mock.TotalApiCalls);

        var sent = await rig.Commands.ContainerSendToJdAsync(new RemoteContainerSendRequest { Path = path });
        Assert.True(sent.Ok, sent.Message);
        Assert.Equal("sent", sent.Status);
        Assert.True(sent.JdAnswered);
        Assert.Contains("stay in JDownloader", sent.Message);
        Assert.All(jd.Requests, request => Assert.Contains("127.0.0.1:9666", request));
        Assert.Contains(jd.Requests, request => request.StartsWith("POST ", StringComparison.Ordinal) && request.EndsWith("/flash/addcrypted", StringComparison.Ordinal));

        // It stays out of this app, and out of the provider.
        Assert.Empty(rig.Remote.Jobs.List());
        Assert.Equal(0, rig.Mock.TotalApiCalls);
    }

    [Fact]
    public async Task TheRouteSaysHonestlyWhetherJDownloaderAnswered()
    {
        async Task<RemoteContainerSendResult> SendAsync(JDownloaderBridge bridge)
        {
            await using var rig = await ContainerRig.CreateAsync(_server, bridge: bridge);
            return await rig.Commands.ContainerSendToJdAsync(new RemoteContainerSendRequest { Path = rig.Write("links.dlc", Encoding.ASCII.GetBytes(ContainerFixtures.Dlc())) });
        }

        var refused = await SendAsync(new JDownloaderBridge(PretendJd.Answering("failed")));
        Assert.False(refused.Ok);
        Assert.Equal("rejected", refused.Status);
        Assert.True(refused.JdAnswered);

        var absent = await SendAsync(new JDownloaderBridge(new PretendJd(_ => throw new HttpRequestException("Refused."))));
        Assert.False(absent.Ok);
        Assert.Equal("notRunning", absent.Status);
        Assert.False(absent.JdAnswered);
        Assert.Contains("not running", absent.Message);

        // Something else on that port isn't JDownloader answering.
        var stranger = await SendAsync(new JDownloaderBridge(new PretendJd(_ => Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent("Some other server") }))));
        Assert.Equal("notRunning", stranger.Status);
        Assert.False(stranger.JdAnswered);

        var slow = await SendAsync(new JDownloaderBridge(
            new PretendJd(async request =>
            {
                if (request.Method == HttpMethod.Post) await Task.Delay(TimeSpan.FromSeconds(30));
                return new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent("JDownloader") };
            }),
            answerTimeout: TimeSpan.FromMilliseconds(100)));
        Assert.Equal("timedOut", slow.Status);
        Assert.False(slow.JdAnswered);
        Assert.Contains("may still be open", slow.Message);

        // Not everything goes this way, and it says so before calling anyone.
        var jd = PretendJd.Answering("success");
        await using var rig = await ContainerRig.CreateAsync(_server, bridge: new JDownloaderBridge(jd));
        var rsdf = await rig.Commands.ContainerSendToJdAsync(new RemoteContainerSendRequest { Path = rig.Write("links.rsdf", ContainerFixtures.Rsdf("https://example.invalid/a.rar")) });
        Assert.Equal("invalidInput", rsdf.Status);
        Assert.Empty(jd.Requests);
    }

    [Fact]
    public void ThePublicRecordOfAPreviewHasNoFieldThatCouldHoldALink()
    {
        var names = typeof(RemoteContainerEntryDto).GetProperties().Select(property => property.Name).ToHashSet();
        Assert.Equal(new HashSet<string> { "EntryId", "PackageIndex", "DisplayHost", "DisplayName", "Size", "Kind", "Reason", "CanAdd" }, names);
        Assert.DoesNotContain(typeof(RemoteContainerPackageDto).GetProperties(), property => property.Name == "ArchivePassword" || property.Name == "Password");
        Assert.DoesNotContain(typeof(RemoteJobDto).GetProperties(), property => property.Name == "ArchivePassword");
    }
}

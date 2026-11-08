using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using DownloadManager.Core.Providers;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;

namespace TestServer;

public sealed partial class TestServerHost
{
    /// <summary>A stand-in for the TorBox API and CDN on this server's address. Every value in it is invented.</summary>
    public MockTorBox TorBox { get; }

    private void MapTorBoxEndpoints()
    {
        string[] methods = ["GET", "POST", "PUT", "DELETE"];
        _app.MapMethods("/torbox/v1/api/{**path}", methods, (HttpContext context, string path) => TorBox.HandleApiAsync(context, path));
        _app.MapMethods("/torbox-cdn/{id}", ["GET", "HEAD"], (HttpContext context, string id) => TorBox.HandleCdnAsync(context, id));
        _app.MapMethods("/hoster/{**path}", ["GET", "HEAD", "POST"], (HttpContext context) => TorBox.HandleHosterAsync(context));
    }
}

/// <summary>
/// One state an item shows. Readiness is decided by the client from the flags, as with the real
/// service, so <see cref="DownloadState"/> is only the provider's word.
/// </summary>
public sealed record MockTorBoxState(string DownloadState)
{
    public bool Present { get; init; }
    public bool Finished { get; init; }
    public bool Cached { get; init; }
    public double Progress { get; init; }
    public long? Eta { get; init; }
    public long Speed { get; init; }
    public string? Error { get; init; }
    /// <summary>False while the provider doesn't know yet what the item contains.</summary>
    public bool ListsFiles { get; init; } = true;
    /// <summary>What the item's own size says, when that isn't the files'. The real service answers 0 or -1 before it knows.</summary>
    public long? ReportedSize { get; init; }

    public static MockTorBoxState Ready => new("completed") { Present = true, Finished = true, Progress = 1 };
    public static MockTorBoxState CachedReady => new("cached") { Present = true, Finished = true, Cached = true, Progress = 1 };
    public static MockTorBoxState Queued => new("queued") { ListsFiles = false };
    /// <summary>As seen on real items: 100 days is its word for no estimate, and the files appear only when it has finished.</summary>
    public static MockTorBoxState MetaData => new("metaDL") { ListsFiles = false, Eta = 8_640_000, ReportedSize = 0 };
    public static MockTorBoxState Checking => new("checking") { ListsFiles = false, Eta = 8_640_000, ReportedSize = -1 };
    public static MockTorBoxState Stalled => new("stalled (no seeds)") { Progress = 0.1, ListsFiles = false, Eta = 8_640_000 };
    public static MockTorBoxState Failed(string? error = null) => new("failed") { Error = error };
    /// <summary>As on a real account: finished once, not present any more, its file list still there and its eta meaningless.</summary>
    public static MockTorBoxState Expired => new("expired") { Finished = true, Progress = 1, Eta = 1_576_416 };
    public static MockTorBoxState Downloading(double progress = 0.5, long? eta = 20) =>
        new("downloading") { Progress = progress, Eta = eta, Speed = 1_048_576, ListsFiles = false };
}

public sealed class MockTorBoxFile
{
    private static readonly DateTimeOffset DefaultModified = new(2026, 1, 2, 3, 4, 5, TimeSpan.Zero);

    public required long Id { get; init; }
    /// <summary>Relative path with the leading folder, as the real <c>name</c> field has it.</summary>
    public required string Name { get; set; }
    public byte[] Content { get; set; } = [];
    /// <summary>What the API reports, which a test can make disagree with <see cref="Content"/>.</summary>
    public long Size { get; set; }
    public string? Md5 { get; set; }
    public bool Infected { get; set; }
    public string? MimeType { get; set; } = "application/octet-stream";
    public DateTimeOffset LastModified { get; set; } = DefaultModified;

    public string ShortName => Name[(Name.Replace('\\', '/').LastIndexOf('/') + 1)..];

    public static MockTorBoxFile Of(long id, string name, int size, int seed = 1, bool withMd5 = false, bool infected = false)
    {
        var content = size == 0 ? [] : TestContent.Generate(seed, size);
        return new MockTorBoxFile
        {
            Id = id,
            Name = name,
            Content = content,
            Size = size,
            Md5 = withMd5 ? Md5Of(content) : null,
            Infected = infected,
        };
    }

    /// <summary>Replaces what is served and reported, as if the provider re-fetched different content.</summary>
    public void Replace(int size, int seed, bool withMd5 = false, DateTimeOffset? lastModified = null)
    {
        Content = size == 0 ? [] : TestContent.Generate(seed, size);
        Size = size;
        Md5 = withMd5 ? Md5Of(Content) : null;
        LastModified = lastModified ?? LastModified.AddHours(1);
    }

    public static string Md5Of(byte[] content) => Convert.ToHexString(MD5.HashData(content)).ToLowerInvariant();
}

public sealed class MockTorBoxItem
{
    private int _state;

    public required long Id { get; init; }
    public required RemoteItemKind Kind { get; init; }
    public required string Hash { get; set; }
    public required string Name { get; set; }
    public string? OriginalUrl { get; set; }
    public string? Magnet { get; set; }
    public List<string> AlternativeHashes { get; } = [];
    public List<MockTorBoxFile> Files { get; } = [];
    public List<MockTorBoxState> States { get; } = [];
    /// <summary>Accepted, but waiting for a slot: listed by getqueued and not by mylist.</summary>
    public bool InQueue { get; set; }
    public string CreatedAt { get; set; } = "2026-11-05T19:38:10.123456Z";

    public MockTorBoxState Current => States[Math.Min(_state, States.Count - 1)];

    /// <summary>A fresh read moves the item on, so polling walks the scripted sequence.</summary>
    public void Advance() => _state = Math.Min(_state + 1, States.Count - 1);
}

public sealed record MockTorBoxSubmission(string Route, string? Link, int? FileBytes, string? Password, string? Name, bool OnlyIfCached);

public enum MockFaultKind
{
    /// <summary>Answer with this status and envelope instead of doing anything.</summary>
    Respond,
    /// <summary>Do the work and record it, then drop the connection before answering.</summary>
    AcceptThenAbort,
    /// <summary>Do the work and record it, then wait before answering: the answer is on its way when the caller dies.</summary>
    AcceptThenDelay,
    /// <summary>Drop the connection without doing anything.</summary>
    Abort,
    /// <summary>Wait, then carry on normally.</summary>
    Delay,
    /// <summary>A 200 with a short text body: what a dead link can look like.</summary>
    DeadLink,
}

/// <summary>What a route does instead of its usual work. Build with the named helpers.</summary>
public sealed record MockFault(MockFaultKind Kind)
{
    public int Status { get; init; }
    public string? Code { get; init; }
    public string? Detail { get; init; }
    /// <summary>The body as is. Null with no code and no detail means an empty body.</summary>
    public string? RawBody { get; init; }
    public int? RetryAfterSeconds { get; init; }
    public TimeSpan DelayFor { get; init; }

    public static MockFault Error(string code, string detail = "Scripted failure.", int status = 200) =>
        new(MockFaultKind.Respond) { Status = status, Code = code, Detail = detail };

    /// <summary>Real answers to a web download, captured from the service: nothing is created for any of them.</summary>
    public static MockFault DeadWebLink { get; } = Error("DOWNLOAD_SERVER_ERROR", "There was an error scanning this link. It may not be online or it is inaccessible. Please try again later. Please make sure that this URL is for an item rather than a collection.", 500);
    public static MockFault InvalidHosterLink { get; } = Error("DOWNLOAD_SERVER_ERROR", "This is not a valid link. Please provide a valid Mega link.", 500);
    public static MockFault SiteTurnedOff { get; } = Error("TEMPORARILY_DISABLED", "The site you are trying to download from is temporarily disabled. You may view the status of supported sites at https://torbox.app/hosters.", 503);
    public static MockFault SiteNotSupported { get; } = Error("UNSUPPORTED_SITE", "The site you are trying to download from is not supported.", 400);
    public static MockFault NotALink { get; } = Error("INVALID_OPTION", "The link you provided is invalid. Please provide a valid link.", 400);

    public static MockFault Status5xx(int status = 500, string? code = "DATABASE_ERROR", string detail = "Scripted outage.") =>
        new(MockFaultKind.Respond) { Status = status, Code = code, Detail = detail };

    /// <summary>The kind of answer a proxy or load balancer gives: not the API's JSON at all.</summary>
    public static MockFault NotJson(int status = 502, string body = "<html>Bad gateway</html>") =>
        new(MockFaultKind.Respond) { Status = status, RawBody = body };

    public static MockFault TooManyRequests(int retryAfterSeconds = 1) =>
        new(MockFaultKind.Respond)
        {
            Status = 429,
            Code = "RATE_LIMIT_EXCEEDED",
            Detail = "300 per 1 minute",
            RetryAfterSeconds = retryAfterSeconds,
        };

    public static MockFault Empty(int status) => new(MockFaultKind.Respond) { Status = status };
    public static MockFault AcceptThenAbort { get; } = new(MockFaultKind.AcceptThenAbort);
    public static MockFault Abort { get; } = new(MockFaultKind.Abort);
    public static MockFault DeadLink(string body = "This link has expired.") => new(MockFaultKind.DeadLink) { RawBody = body };
    public static MockFault Delay(TimeSpan delay) => new(MockFaultKind.Delay) { DelayFor = delay };
    public static MockFault AcceptThenDelay(TimeSpan delay) => new(MockFaultKind.AcceptThenDelay) { DelayFor = delay };
}

/// <summary>
/// Models what research found about the real service, and what the live project in
/// tests/DownloadManager.TorBox.LiveTests reads again: the envelope, the 404 with success:true,
/// FastAPI's own shape for a bad parameter or address, requestdl's 500 for anything it can't find,
/// Retry-After on every response, rate limit headers, the CDN's headers and its empty 403 and 404.
/// Still unseen, so still a guess: what a queued item looks like, and what a create answers.
/// Routes are counted as "GET torrents/mylist"; the CDN counts as <see cref="CdnRoute"/>.
/// </summary>
public sealed partial class MockTorBox
{
    public const string DefaultApiKey = "0a1b2c3d-1111-4222-8333-444455556666";
    public const string CdnRoute = "GET cdn";

    private readonly Func<string> _baseAddress;
    private readonly Lock _sync = new();
    private readonly List<MockTorBoxItem> _items = [];
    private readonly Dictionary<string, (string? Name, long? Size)> _cached = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, string> _cachedAlternatives = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, (List<MockTorBoxFile> Files, List<MockTorBoxState> States, bool Queued)> _scripts = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, string> _linkPasswords = new(StringComparer.Ordinal);
    private readonly Dictionary<string, MockFault> _refusedLinks = new(StringComparer.Ordinal);
    private readonly Dictionary<string, string> _torrentUploads = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, FaultSlot> _faults = new(StringComparer.Ordinal);
    private readonly Dictionary<string, int> _calls = new(StringComparer.Ordinal);
    private readonly Dictionary<RemoteItemKind, JsonArray> _listSnapshots = [];
    private readonly Dictionary<string, MockCdnLink> _links = new(StringComparer.Ordinal);
    private readonly List<MockCdnLink> _issuedLinks = [];
    private readonly List<JsonObject> _hosters = [];
    private readonly List<MockTorBoxSubmission> _submissions = [];
    private readonly List<(string Route, long Id)> _deleted = [];
    private readonly List<string> _authSeen = [];
    private long _nextId;
    private long _nextFileId;
    private int _budgetUsed;
    private DateTimeOffset _windowEnds;
    private readonly HashSet<HttpContext> _activeAnswers = [];
    private int _peakCdn;
    private int _cdnWithCredentials;
    private int _hosterRequests;
    private long? _nextLinkDiesAfter;

    public MockTorBox(Func<string> baseAddress)
    {
        _baseAddress = baseAddress;
        Reset();
    }

    public string ApiKey { get; set; } = DefaultApiKey;
    public Uri ApiBase => new($"{_baseAddress()}/torbox/v1/api/");

    public int RateLimit { get; set; }
    public TimeSpan RateWindow { get; set; }
    public TimeSpan LinkLifetime { get; set; }
    public int ExpiredLinkStatus { get; set; }
    /// <summary>Not what the real service does, which answers <c>data:[]</c>. Only for the client's tolerance of that older answer.</summary>
    public bool EmptyListIsNotFound { get; set; }
    public bool DuplicatesAreErrors { get; set; }
    public int? CdnBytesPerSecond { get; set; }
    public int AccountPlan { get; set; }
    public string? PremiumExpiresAt { get; set; }
    /// <summary>The real field holds a date even with nothing wrong, so a date alone stops nothing.</summary>
    public string? CooldownUntil { get; set; }

    /// <summary>Raised the moment a link stops working mid-transfer.</summary>
    public Action? LinkDied { get; set; }

    /// <summary>Back to an empty account, default limits and no faults. The key stays the default one.</summary>
    public void Reset()
    {
        lock (_sync)
        {
            _items.Clear();
            _cached.Clear();
            _cachedAlternatives.Clear();
            _scripts.Clear();
            _linkPasswords.Clear();
            _refusedLinks.Clear();
            _torrentUploads.Clear();
            _hosterRequests = 0;
            _faults.Clear();
            _calls.Clear();
            _listSnapshots.Clear();
            _links.Clear();
            _issuedLinks.Clear();
            _hosters.Clear();
            _submissions.Clear();
            _deleted.Clear();
            _authSeen.Clear();
            _nextId = 133_998_000;
            _nextFileId = 0;
            _budgetUsed = 0;
            _windowEnds = default;
            lock (_activeAnswers)
            {
                _activeAnswers.Clear();
                _peakCdn = 0;
            }

            _cdnWithCredentials = 0;
            _nextLinkDiesAfter = null;
            ReleaseAll();
            ApiKey = DefaultApiKey;
            RateLimit = 300;
            RateWindow = TimeSpan.FromSeconds(60);
            LinkLifetime = TimeSpan.FromHours(3);
            ExpiredLinkStatus = 403;
            EmptyListIsNotFound = false;
            DuplicatesAreErrors = false;
            CdnBytesPerSecond = null;
            AccountPlan = 2;
            PremiumExpiresAt = "2030-01-01T00:00:00Z";
            CooldownUntil = "2026-01-01T00:00:00Z";
            LinkDied = null;
            AddHoster("Mega", ["mega.nz", "mega.io", "mega.co.nz"], @"^https?://(?:[^./]+\.)*(mega\.nz|mega\.io|mega\.co\.nz)(/|$)");
            AddHoster("Rapidgator", ["rapidgator.net", "rg.to"], @"^https?://(?:[^./]+\.)*(rapidgator\.net|rg\.to)(/|$)", available: false, note: "Down for maintenance");
        }
    }

    // ---- Scripting ----------------------------------------------------------------------------

    public MockTorBoxItem AddItem(RemoteItemKind kind, string name, IEnumerable<MockTorBoxFile> files, params MockTorBoxState[] states)
    {
        lock (_sync)
        {
            var item = new MockTorBoxItem
            {
                Id = ++_nextId,
                Kind = kind,
                Name = name,
                Hash = Convert.ToHexString(SHA1.HashData(Encoding.UTF8.GetBytes($"mock-{_nextId}"))).ToLowerInvariant(),
            };
            item.Files.AddRange(files);
            item.States.AddRange(states.Length == 0 ? [MockTorBoxState.CachedReady] : states);
            _items.Add(item);
            return item;
        }
    }

    /// <summary>The item with this hash, wherever it is, or null. For a test to move on an item created by a submission.</summary>
    public MockTorBoxItem? FindItem(RemoteItemKind kind, string hash)
    {
        lock (_sync) return _items.FirstOrDefault(item => item.Kind == kind && item.Hash.Equals(hash, StringComparison.OrdinalIgnoreCase));
    }

    public void RemoveItem(MockTorBoxItem item)
    {
        lock (_sync) _items.Remove(item);
    }

    /// <summary>A file id no other file on the account has.</summary>
    public long NextFileId()
    {
        lock (_sync) return ++_nextFileId;
    }

    /// <summary>checkcached finds this hash, and a create of it is ready at once.</summary>
    public void AddCached(string hash, string? name = "cached item", long? size = 1024)
    {
        lock (_sync) _cached[hash] = (name, size);
    }

    /// <summary>checkcached finds this other hash too, and answers under the item's main hash, as the real one does.</summary>
    public void AddCachedAlternative(string alternative, string hash)
    {
        lock (_sync) _cachedAlternatives[alternative] = hash;
    }

    /// <summary>What an item created from this hash will contain and how it will progress.</summary>
    public void ScriptSubmission(string hash, IEnumerable<MockTorBoxFile> files, IEnumerable<MockTorBoxState> states, bool queued = false)
    {
        lock (_sync) _scripts[hash] = (files.ToList(), states.ToList(), queued);
    }

    /// <summary>
    /// The info hash an uploaded .torrent has. Stated by the test, which works it out on its own,
    /// so this mock doesn't borrow the parser it is testing. Without it, the hash is of the whole file.
    /// </summary>
    public void ScriptTorrentUpload(byte[] file, string infoHash)
    {
        lock (_sync) _torrentUploads[Convert.ToHexString(SHA1.HashData(file))] = infoHash.ToLowerInvariant();
    }

    /// <summary>The source site wants this password for this link.</summary>
    public void RequireLinkPassword(string link, string password)
    {
        lock (_sync) _linkPasswords[link] = password;
    }

    public void AddHoster(string name, string[] domains, string regex, bool available = true, string? note = null, int dailyLinkLimit = 100, int dailyLinkUsed = 3)
    {
        lock (_sync)
        {
            _hosters.Add(new JsonObject
            {
                ["id"] = _hosters.Count + 1,
                ["name"] = name,
                ["domains"] = new JsonArray(domains.Select(domain => (JsonNode?)domain).ToArray()),
                ["url"] = domains.Length > 0 ? $"https://{domains[0]}" : "https://no-domains.invalid",
                ["icon"] = "https://icons.invalid/hoster.png",
                ["status"] = available,
                ["type"] = "hoster",
                ["note"] = note,
                ["nsfw"] = false,
                ["daily_link_limit"] = dailyLinkLimit,
                ["daily_link_used"] = dailyLinkUsed,
                ["daily_bandwidth_limit"] = 0,
                ["daily_bandwidth_used"] = 0,
                ["per_link_size_limit"] = 214_748_364_800,
                ["regex"] = regex,
            });
        }
    }

    /// <summary>Makes a route misbehave for its next <paramref name="times"/> calls. "*" means every API route.</summary>
    /// <summary>The service's own refusal of one link, for a link that is dead or a site that's off. Nothing is created.</summary>
    public void RefuseLink(string link, MockFault refusal)
    {
        lock (_sync) _refusedLinks[link] = refusal;
    }

    public void AcceptLink(string link)
    {
        lock (_sync) _refusedLinks.Remove(link);
    }

    public void Fail(string route, MockFault fault, int times = int.MaxValue)
    {
        lock (_sync) _faults[route] = new FaultSlot(fault, times);
    }

    public void ClearFaults()
    {
        lock (_sync) _faults.Clear();
    }

    /// <summary>All links issued so far stop working. In-flight transfers can be cut off with them.</summary>
    public void ExpireLinks(bool abortInFlight = false)
    {
        lock (_sync)
        {
            foreach (var link in _issuedLinks)
            {
                link.Expired = true;
                link.AbortInFlight = abortInFlight;
            }
        }
    }

    /// <summary>
    /// The CDN stops sending this file, on every link and connection, once this many of its bytes went out in
    /// all, until <see cref="Release"/>. Connections stay open and silent, like a stalled server. A test can then
    /// rely on a file not finishing, instead of on a throttle and a race (TEST.1).
    /// </summary>
    public void HoldFile(MockTorBoxFile file, long afterBytes)
    {
        lock (_holds) _holds[file.Id] = new FileHold(afterBytes);
    }

    /// <summary>Lets a held file go on. Releasing a file that isn't held does nothing.</summary>
    public void Release(MockTorBoxFile file)
    {
        FileHold? hold;
        lock (_holds)
        {
            if (!_holds.Remove(file.Id, out hold)) return;
        }

        hold.Gate.TrySetResult();
    }

    public void ReleaseAll()
    {
        List<FileHold> holds;
        lock (_holds)
        {
            holds = _holds.Values.ToList();
            _holds.Clear();
        }

        foreach (var hold in holds) hold.Gate.TrySetResult();
    }

    /// <summary>Bytes of a held file sent so far, across its connections.</summary>
    public long HeldBytesSent(MockTorBoxFile file)
    {
        lock (_holds) return _holds.TryGetValue(file.Id, out var hold) ? Interlocked.Read(ref hold.Sent) : 0;
    }

    private sealed class FileHold(long afterBytes)
    {
        public long AfterBytes { get; } = afterBytes;
        public long Sent;
        public TaskCompletionSource Gate { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
    }

    private readonly Dictionary<long, FileHold> _holds = [];

    /// <summary>The next link issued dies, cutting off its connections, once this many bytes went through it.</summary>
    public void NextLinkDiesAfter(long bytes)
    {
        lock (_sync) _nextLinkDiesAfter = bytes;
    }

    // ---- Inspection ---------------------------------------------------------------------------

    public int Calls(string route)
    {
        lock (_sync) return _calls.GetValueOrDefault(route);
    }

    public int TotalApiCalls
    {
        get
        {
            lock (_sync) return _calls.Where(pair => pair.Key != CdnRoute).Sum(pair => pair.Value);
        }
    }

    public IReadOnlyList<MockTorBoxSubmission> Submissions
    {
        get
        {
            lock (_sync) return _submissions.ToList();
        }
    }

    public IReadOnlyList<(string Route, long Id)> Deleted
    {
        get
        {
            lock (_sync) return _deleted.ToList();
        }
    }

    /// <summary>The Authorization header of each API call, in order.</summary>
    public IReadOnlyList<string> AuthorizationHeaders
    {
        get
        {
            lock (_sync) return _authSeen.ToList();
        }
    }

    /// <summary>
    /// Requests that reached the pretend file hoster at <c>/hoster/...</c>. Everything for a hoster
    /// goes through the provider, so this stays at zero.
    /// </summary>
    public int HosterRequests => Volatile.Read(ref _hosterRequests);

    /// <summary>An address on this server standing in for a file on a hoster the provider lists.</summary>
    public string HosterUrl(string path) => $"{_baseAddress()}/hoster/{path}";

    /// <summary>Links issued so far, oldest first.</summary>
    public IReadOnlyList<MockCdnLink> IssuedLinks
    {
        get
        {
            lock (_sync) return _issuedLinks.ToList();
        }
    }

    public int PeakCdnConnections
    {
        get
        {
            lock (_activeAnswers) return _peakCdn;
        }
    }

    /// <summary>CDN requests that arrived with a login or cookie. Should stay at zero: only the link itself authorizes.</summary>
    public int CdnRequestsWithCredentials
    {
        get
        {
            lock (_sync) return _cdnWithCredentials;
        }
    }

    public void ResetCdnPeak()
    {
        lock (_activeAnswers) _peakCdn = _activeAnswers.Count;
    }

    /// <summary>A few items for looking at real screens with the sandbox profile.</summary>
    public void SeedSandbox()
    {
        AddItem(RemoteItemKind.Torrent, "Sample Torrent", [
            MockTorBoxFile.Of(0, "Sample Torrent/readme.txt", 4_096, 11),
            MockTorBoxFile.Of(1, "Sample Torrent/sub/video.bin", 8 * 1024 * 1024, 12),
            MockTorBoxFile.Of(2, "Sample Torrent/other/video.bin", 2 * 1024 * 1024, 13),
        ]);
        AddItem(RemoteItemKind.Torrent, "Still Fetching", [MockTorBoxFile.Of(0, "Still Fetching/a.bin", 1024, 14)],
            MockTorBoxState.MetaData, MockTorBoxState.Downloading(0.2, 40), MockTorBoxState.Downloading(0.8, 15), MockTorBoxState.Ready);
        AddItem(RemoteItemKind.WebDownload, "web-file.bin", [MockTorBoxFile.Of(0, "web-file.bin/web-file.bin", 1024 * 1024, 15)]);
        AddItem(RemoteItemKind.WebDownload, "broken", [], MockTorBoxState.Failed("Hoster is down"));
        AddItem(RemoteItemKind.Usenet, "Usenet Sample", [MockTorBoxFile.Of(0, "Usenet Sample/part.bin", 512 * 1024, 16)]);
    }

    // ---- API ----------------------------------------------------------------------------------

    private sealed class FaultSlot(MockFault fault, int remaining)
    {
        public MockFault Fault { get; } = fault;
        public int Remaining { get; set; } = remaining;
    }

    private readonly record struct Answer(int Status, JsonNode? Body);

    internal async Task HandleApiAsync(HttpContext context, string path)
    {
        var route = $"{context.Request.Method} {path.Trim('/')}";
        MockFault? fault;
        bool overBudget;
        lock (_sync)
        {
            _calls[route] = _calls.GetValueOrDefault(route) + 1;
            _authSeen.Add(context.Request.Headers.Authorization.ToString());
            overBudget = !WriteRateHeaders(context);
            fault = TakeFault(route) ?? TakeFault("*");
        }

        if (overBudget)
        {
            await WriteAsync(context, Err(429, "RATE_LIMIT_EXCEEDED", $"{RateLimit} per 1 minute")).ConfigureAwait(false);
            return;
        }

        if (fault?.Kind == MockFaultKind.Abort)
        {
            context.Abort();
            return;
        }

        if (fault?.Kind == MockFaultKind.Delay)
        {
            await Task.Delay(fault.DelayFor, context.RequestAborted).ConfigureAwait(false);
        }

        if (fault?.Kind == MockFaultKind.Respond)
        {
            await WriteFaultAsync(context, fault).ConfigureAwait(false);
            return;
        }

        var answer = Authorize(context, route) ?? await RouteAsync(context, route).ConfigureAwait(false);
        if (fault?.Kind == MockFaultKind.AcceptThenAbort)
        {
            context.Abort();
            return;
        }

        if (fault?.Kind == MockFaultKind.AcceptThenDelay)
        {
            await Task.Delay(fault.DelayFor, context.RequestAborted).ConfigureAwait(false);
        }

        await WriteAsync(context, answer).ConfigureAwait(false);
    }

    private MockFault? TakeFault(string route)
    {
        if (!_faults.TryGetValue(route, out var slot) || slot.Remaining <= 0)
        {
            return null;
        }

        if (slot.Remaining != int.MaxValue)
        {
            slot.Remaining--;
        }

        return slot.Fault;
    }

    /// <summary>Retry-After is on every response, even a 200, because the real service does that. Returns false when over budget.</summary>
    private bool WriteRateHeaders(HttpContext context)
    {
        var now = DateTimeOffset.UtcNow;
        if (now >= _windowEnds)
        {
            _windowEnds = now + RateWindow;
            _budgetUsed = 0;
        }

        _budgetUsed++;
        var remaining = Math.Max(0, RateLimit - _budgetUsed);
        var headers = context.Response.Headers;
        headers["X-RateLimit-Limit"] = RateLimit.ToString(CultureInfo.InvariantCulture);
        headers["X-RateLimit-Remaining"] = remaining.ToString(CultureInfo.InvariantCulture);
        headers["X-RateLimit-Reset"] = (_windowEnds.ToUnixTimeMilliseconds() / 1000d).ToString("0.000", CultureInfo.InvariantCulture);
        headers.RetryAfter = Math.Max(1, (int)Math.Ceiling((_windowEnds - now).TotalSeconds)).ToString(CultureInfo.InvariantCulture);
        return _budgetUsed <= RateLimit;
    }

    private static async Task WriteFaultAsync(HttpContext context, MockFault fault)
    {
        context.Response.StatusCode = fault.Status;
        if (fault.RetryAfterSeconds is { } after)
        {
            context.Response.Headers.RetryAfter = after.ToString(CultureInfo.InvariantCulture);
        }

        if (fault.RawBody is { } raw)
        {
            context.Response.ContentType = "text/html";
            await context.Response.WriteAsync(raw).ConfigureAwait(false);
        }
        else if (fault.Code is not null || fault.Detail is not null)
        {
            context.Response.ContentType = "application/json";
            await context.Response.WriteAsync(Envelope(false, fault.Code, fault.Detail, null).ToJsonString()).ConfigureAwait(false);
        }
    }

    private static async Task WriteAsync(HttpContext context, Answer answer)
    {
        context.Response.StatusCode = answer.Status;
        if (answer.Body is not null)
        {
            context.Response.ContentType = "application/json";
            await context.Response.WriteAsync(answer.Body.ToJsonString()).ConfigureAwait(false);
        }
    }

    private static JsonObject Envelope(bool success, string? error, string? detail, JsonNode? data) => new()
    {
        ["success"] = success,
        ["error"] = error,
        ["detail"] = detail,
        ["data"] = data,
    };

    private static Answer Ok(JsonNode? data, string detail = "ok") => new(200, Envelope(true, null, detail, data));
    private static Answer Err(int status, string code, string detail) => new(status, Envelope(false, code, detail, null));

    /// <summary>The real service really does answer a missing item like this: 404, success true, and data null only for web downloads.</summary>
    private static Answer NotFound(RemoteItemKind kind) => kind switch
    {
        RemoteItemKind.Torrent => new(404, Envelope(true, "ITEM_NOT_FOUND", "No torrents found for this user.", new JsonObject())),
        RemoteItemKind.WebDownload => new(404, Envelope(true, "ITEM_NOT_FOUND", "No web downloads found for this user.", null)),
        _ => new(404, Envelope(true, "ITEM_NOT_FOUND", "No Usenet downloads found for this user.", new JsonObject())),
    };

    /// <summary>A parameter that is missing or of the wrong type. FastAPI answers this, with no envelope and a list for detail.</summary>
    private static Answer Validation(string type, string parameter, string message) => new(422, new JsonObject
    {
        ["detail"] = new JsonArray(new JsonObject
        {
            ["type"] = type,
            ["loc"] = new JsonArray("query", parameter),
            ["msg"] = message,
            ["input"] = null,
        }),
    });

    /// <summary>An address that isn't there. The real one doesn't use its documented ENDPOINT_NOT_FOUND here.</summary>
    private static Answer NoSuchAddress() => new(404, new JsonObject { ["detail"] = "Not Found" });

    /// <summary>What requestdl answers for an item not on the account, a file not in it, or an item that has expired.</summary>
    private static Answer LinkFailure(RemoteItemKind kind) => Err(500, "DATABASE_ERROR", kind == RemoteItemKind.Usenet
        ? "Failed to request usenet download. Please try again later."
        : "There was an error processing your request. Please try again later.");

    private Answer? Authorize(HttpContext context, string route)
    {
        var header = context.Request.Headers.Authorization.ToString();
        var isLinkRequest = route.EndsWith("/requestdl", StringComparison.Ordinal);
        if (string.IsNullOrEmpty(header))
        {
            // requestdl can go by its token parameter alone; everything else needs the bearer header.
            return isLinkRequest
                ? null
                : new Answer(401, new JsonObject { ["detail"] = "Not authenticated" });
        }

        return header.StartsWith("Bearer ", StringComparison.Ordinal) && SameKey(header["Bearer ".Length..])
            ? null
            : BadKey();
    }

    private static Answer BadKey() => Err(403, "AUTH_ERROR", "An error occurred while verifying your token. Please try again.");

    /// <summary>The API takes the key with or without dashes, and in either case, as read from the real one. The CDN doesn't: see <see cref="HandleCdnAsync"/>.</summary>
    private bool SameKey(string? candidate) =>
        candidate is not null &&
        candidate.Replace("-", string.Empty, StringComparison.Ordinal).Equals(
            ApiKey.Replace("-", string.Empty, StringComparison.Ordinal), StringComparison.OrdinalIgnoreCase);

    private async Task<Answer> RouteAsync(HttpContext context, string route)
    {
        var query = context.Request.Query;
        switch (route)
        {
            case "GET user/me": return UserMe();
            case "GET torrents/mylist": return MyList(RemoteItemKind.Torrent, query);
            case "GET webdl/mylist": return MyList(RemoteItemKind.WebDownload, query);
            case "GET usenet/mylist": return MyList(RemoteItemKind.Usenet, query);
            case "GET torrents/checkcached" or "POST torrents/checkcached" or
                 "GET webdl/checkcached" or "POST webdl/checkcached" or
                 "GET usenet/checkcached" or "POST usenet/checkcached":
                return await CheckCachedAsync(context).ConfigureAwait(false);
            case "POST torrents/createtorrent": return await CreateTorrentAsync(context).ConfigureAwait(false);
            case "POST webdl/createwebdownload": return await CreateWebDownloadAsync(context).ConfigureAwait(false);
            case "GET torrents/requestdl": return RequestLink(RemoteItemKind.Torrent, "torrent_id", query);
            case "GET webdl/requestdl": return RequestLink(RemoteItemKind.WebDownload, "web_id", query);
            case "GET usenet/requestdl": return RequestLink(RemoteItemKind.Usenet, "usenet_id", query);
            case "GET webdl/hosters": return Hosters();
            case "GET queued/getqueued": return Queued(query);
            case "GET torrents/getqueued":
                return Err(410, "DEPRECATED", "This endpoint is now deprecated. Please use /v1/api/queued/getqueued instead.");
            case "POST torrents/controltorrent": return await ControlAsync(context, route, RemoteItemKind.Torrent, "torrent_id").ConfigureAwait(false);
            case "POST webdl/controlwebdownload": return await ControlAsync(context, route, RemoteItemKind.WebDownload, "webdl_id").ConfigureAwait(false);
            case "POST usenet/controlusenetdownload": return await ControlAsync(context, route, RemoteItemKind.Usenet, "usenet_id").ConfigureAwait(false);
            default: return NoSuchAddress();
        }
    }

    private Answer UserMe()
    {
        lock (_sync)
        {
            return Ok(new JsonObject
            {
                ["id"] = 4242,
                ["auth_id"] = "mock-auth",
                ["created_at"] = "2025-01-01T10:00:00Z",
                ["updated_at"] = "2025-01-01T10:00:00Z",
                ["plan"] = AccountPlan,
                ["total_downloaded"] = 3,
                ["customer"] = "mock-customer",
                ["is_subscribed"] = true,
                ["premium_expires_at"] = PremiumExpiresAt,
                ["cooldown_until"] = CooldownUntil,
                ["email"] = "someone@example.test",
                ["user_referral"] = "mock-referral",
                ["base_email"] = "someone@example.test",
                ["total_bytes_downloaded"] = 1_000_000,
                ["total_bytes_uploaded"] = 0,
                ["torrents_downloaded"] = 2,
                ["web_downloads_downloaded"] = 1,
                ["usenet_downloads_downloaded"] = 0,
                ["additional_concurrent_slots"] = 0,
                ["long_term_seeding"] = false,
                ["long_term_storage"] = false,
                ["is_vendor"] = false,
                ["vendor_id"] = null,
                ["purchases_referred"] = 0,
            }, "Successfully retrieved your user information.");
        }
    }

    private Answer MyList(RemoteItemKind kind, IQueryCollection query)
    {
        var fresh = string.Equals(query["bypass_cache"], "true", StringComparison.OrdinalIgnoreCase);
        lock (_sync)
        {
            if (query.ContainsKey("id"))
            {
                if (!long.TryParse(query["id"], out var id))
                {
                    return Validation("int_parsing", "id", "Input should be a valid integer, unable to parse string as an integer");
                }

                var item = _items.FirstOrDefault(candidate => candidate.Id == id && candidate.Kind == kind && !candidate.InQueue);
                if (item is null)
                {
                    return NotFound(kind);
                }

                var json = ItemJson(item);
                if (fresh)
                {
                    item.Advance();
                }

                return Ok(json, ListWords(kind));
            }

            if (!fresh && _listSnapshots.TryGetValue(kind, out var snapshot))
            {
                return Page(snapshot, query, kind);
            }

            var built = new JsonArray();
            foreach (var item in Enumerable.Reverse(_items).Where(candidate => candidate.Kind == kind && !candidate.InQueue))
            {
                built.Add(ItemJson(item));
                if (fresh)
                {
                    item.Advance();
                }
            }

            _listSnapshots[kind] = built;
            return Page(built, query, kind);
        }
    }

    private static string ListWords(RemoteItemKind kind) => kind switch
    {
        RemoteItemKind.Torrent => "Torrent list retrieved successfully.",
        RemoteItemKind.WebDownload => "web downloads list retrieved successfully.",
        _ => "Usenet downloads list retrieved successfully.",
    };

    private Answer Page(JsonArray list, IQueryCollection query, RemoteItemKind? kind = null)
    {
        var offset = int.TryParse(query["offset"], out var parsedOffset) ? Math.Max(0, parsedOffset) : 0;
        var limit = int.TryParse(query["limit"], out var parsedLimit) ? Math.Clamp(parsedLimit, 0, 1000) : 1000;
        var page = new JsonArray();
        foreach (var node in list.Skip(offset).Take(limit))
        {
            page.Add(node?.DeepClone());
        }

        return page.Count == 0 && EmptyListIsNotFound
            ? NotFound(kind ?? RemoteItemKind.Torrent)
            : Ok(page, kind is { } family ? ListWords(family) : "Queued downloads list retrieved successfully.");
    }

    private static JsonObject ItemJson(MockTorBoxItem item)
    {
        var state = item.Current;
        var files = new JsonArray();
        if (state.ListsFiles)
        {
            foreach (var file in item.Files)
            {
                files.Add(new JsonObject
                {
                    ["id"] = file.Id,
                    ["md5"] = file.Md5,
                    ["hash"] = item.Hash,
                    ["name"] = file.Name,
                    ["size"] = file.Size,
                    ["zipped"] = false,
                    ["s3_path"] = $"mock/{item.Id}/{file.Id}",
                    ["infected"] = file.Infected,
                    ["mimetype"] = file.MimeType,
                    ["short_name"] = file.ShortName,
                    ["absolute_path"] = $"/mock/{file.Name}",
                    ["opensubtitles_hash"] = null,
                });
            }
        }

        var json = new JsonObject
        {
            ["id"] = item.Id,
            ["auth_id"] = "mock-auth",
            ["server"] = 7,
            ["hash"] = item.Hash,
            ["name"] = item.Name,
            ["size"] = state.ReportedSize ?? item.Files.Sum(file => file.Size),
            ["active"] = !state.Finished,
            ["created_at"] = item.CreatedAt,
            ["updated_at"] = item.CreatedAt,
            ["download_state"] = state.DownloadState,
            ["upload_speed"] = 0,
            ["progress"] = state.Progress,
            ["download_speed"] = state.Speed,
            ["eta"] = state.Eta,
            ["expires_at"] = null,
            ["download_present"] = state.Present,
            ["download_finished"] = state.Finished,
            ["cached"] = state.Cached,
            ["cached_at"] = state.Cached ? item.CreatedAt : null,
            ["files"] = files,
            ["alternative_hashes"] = new JsonArray(item.AlternativeHashes.Select(hash => (JsonNode?)hash).ToArray()),
            ["tags"] = new JsonArray(),
            ["airlocked"] = false,
        };
        if (item.Kind == RemoteItemKind.Torrent)
        {
            json["magnet"] = item.Magnet;
            json["seeds"] = 3;
            json["peers"] = 1;
            json["ratio"] = 0.5;
            json["tracker"] = null;
            json["tracker_message"] = null;
            json["torrent_file"] = false;
            json["download_path"] = item.Hash;
            json["availability"] = 0;
            json["total_uploaded"] = 0;
            json["total_downloaded"] = 0;
            json["owner"] = "mock-auth";
            json["seed_torrent"] = false;
            json["allow_zipped"] = true;
            json["long_term_seeding"] = false;
            json["private"] = false;
            // No real torrent has shown this field yet, on any of 144 items. How a failed one says why is a guess.
            if (state.Error is not null)
            {
                json["error"] = state.Error;
            }
        }
        else
        {
            json["original_url"] = item.OriginalUrl;
            json["download_id"] = null;
            json["error"] = state.Error;
        }

        return json;
    }

    private async Task<Answer> CheckCachedAsync(HttpContext context)
    {
        var hashes = new List<string>();
        foreach (var value in context.Request.Query["hash"])
        {
            if (value is not null)
            {
                hashes.AddRange(value.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries));
            }
        }

        if (HttpMethods.IsPost(context.Request.Method))
        {
            using var document = await JsonDocument.ParseAsync(context.Request.Body, cancellationToken: context.RequestAborted).ConfigureAwait(false);
            if (document.RootElement.TryGetProperty("hashes", out var list))
            {
                hashes.AddRange(list.EnumerateArray().Select(hash => hash.GetString() ?? string.Empty));
            }
        }

        var asObject = !string.Equals(context.Request.Query["format"], "list", StringComparison.OrdinalIgnoreCase);
        var listFiles = IsTrue(context.Request.Query["list_files"]);
        lock (_sync)
        {
            var map = new JsonObject();
            var array = new JsonArray();
            foreach (var hash in hashes.Distinct(StringComparer.OrdinalIgnoreCase))
            {
                var main = _cachedAlternatives.GetValueOrDefault(hash, hash);
                if (!_cached.TryGetValue(main, out var hit))
                {
                    continue;
                }

                var entry = new JsonObject { ["name"] = hit.Name, ["size"] = hit.Size, ["hash"] = main.ToLowerInvariant() };
                if (listFiles)
                {
                    entry["files"] = CachedFilesLocked(main, hit.Name);
                }
                if (asObject)
                {
                    map[main.ToLowerInvariant()] = entry;
                }
                else
                {
                    array.Add(entry);
                }
            }

            return Ok(asObject ? map : array, "Torrent cache status retrieved successfully.");
        }
    }

    /// <summary>
    /// The files a cache check lists, under the ids the item will have: a scripted submission's, or the one file a
    /// create would make without a script. The real answer has these fields and no others.
    /// </summary>
    private JsonArray CachedFilesLocked(string hash, string? name)
    {
        var folder = name ?? "torrent-" + hash[..Math.Min(8, hash.Length)];
        var files = _scripts.TryGetValue(hash, out var script) ? script.Files : [MockTorBoxFile.Of(0, $"{folder}/{folder}.bin", 64 * 1024)];
        return new JsonArray(files.Select(file => (JsonNode)new JsonObject
        {
            ["id"] = file.Id,
            ["name"] = file.Name,
            ["size"] = file.Size,
            ["short_name"] = file.ShortName,
            ["mimetype"] = file.MimeType,
        }).ToArray());
    }

    private async Task<Answer> CreateTorrentAsync(HttpContext context)
    {
        if (!context.Request.HasFormContentType)
        {
            return Err(400, "MISSING_REQUIRED_OPTION", "Send a multipart form.");
        }

        var form = await context.Request.ReadFormAsync(context.RequestAborted).ConfigureAwait(false);
        var magnet = form["magnet"].ToString();
        var upload = form.Files.GetFile("file");
        string? hash;
        int? fileBytes = null;
        if (upload is not null)
        {
            using var bytes = new MemoryStream();
            await upload.CopyToAsync(bytes, context.RequestAborted).ConfigureAwait(false);
            fileBytes = (int)bytes.Length;
            var fileHash = Convert.ToHexString(SHA1.HashData(bytes.ToArray()));
            lock (_sync) hash = _torrentUploads.GetValueOrDefault(fileHash) ?? fileHash.ToLowerInvariant();
        }
        else if (!string.IsNullOrEmpty(magnet))
        {
            hash = InfoHash().Match(magnet) is { Success: true } match ? match.Groups[1].Value.ToLowerInvariant() : null;
            if (hash is null)
            {
                return Err(400, "BOZO_TORRENT", "That magnet has no usable hash.");
            }
        }
        else
        {
            return Err(400, "MISSING_REQUIRED_OPTION", "A magnet or a file is required.");
        }

        var onlyIfCached = IsTrue(form["add_only_if_cached"]);
        var name = form["name"].ToString();
        lock (_sync)
        {
            _submissions.Add(new MockTorBoxSubmission("createtorrent", upload is null ? magnet : null, fileBytes, null, name, onlyIfCached));
            return CreateLocked(RemoteItemKind.Torrent, hash, name.Length > 0 ? name : "torrent-" + hash[..8], "torrent_id", onlyIfCached, item => item.Magnet = upload is null ? magnet : null);
        }
    }

    private async Task<Answer> CreateWebDownloadAsync(HttpContext context)
    {
        if (!context.Request.HasFormContentType)
        {
            return Err(400, "MISSING_REQUIRED_OPTION", "Send a form.");
        }

        var form = await context.Request.ReadFormAsync(context.RequestAborted).ConfigureAwait(false);
        var link = form["link"].ToString();
        if (link.Length == 0)
        {
            return Err(400, "MISSING_REQUIRED_OPTION", "A link is required.");
        }

        var onlyIfCached = IsTrue(form["add_only_if_cached"]);
        var name = form["name"].ToString();
        var password = form["password"].ToString();
        var hash = Convert.ToHexString(MD5.HashData(Encoding.UTF8.GetBytes(link))).ToLowerInvariant();
        lock (_sync)
        {
            _submissions.Add(new MockTorBoxSubmission("createwebdownload", link, null, password.Length > 0 ? password : null, name, onlyIfCached));
            if (_refusedLinks.TryGetValue(link, out var refusal))
            {
                return Err(refusal.Status, refusal.Code!, refusal.Detail!);
            }

            if (_linkPasswords.TryGetValue(link, out var wanted) && wanted != password)
            {
                return Err(400, "UNKNOWN_ERROR", "This link is password protected.");
            }

            return CreateLocked(RemoteItemKind.WebDownload, hash, name.Length > 0 ? name : "web-" + hash[..8], "webdownload_id", onlyIfCached, item => item.OriginalUrl = link);
        }
    }

    private Answer CreateLocked(RemoteItemKind kind, string hash, string name, string idField, bool onlyIfCached, Action<MockTorBoxItem> configure)
    {
        var existing = _items.FirstOrDefault(item => item.Kind == kind &&
            (item.Hash.Equals(hash, StringComparison.OrdinalIgnoreCase) ||
             item.AlternativeHashes.Contains(hash, StringComparer.OrdinalIgnoreCase)));
        if (existing is { InQueue: true })
        {
            return Err(400, "DIFF_ISSUE", "Download already queued.");
        }

        if (existing is not null)
        {
            return DuplicatesAreErrors
                ? Err(400, "DUPLICATE_ITEM", "This item is already on the account.")
                : Created(existing, idField, "Found an existing item.");
        }

        var isCached = _cached.ContainsKey(hash);
        if (onlyIfCached && !isCached)
        {
            return Err(200, "DOWNLOAD_NOT_CACHED", "This item is not cached.");
        }

        List<MockTorBoxFile> files;
        List<MockTorBoxState> states;
        var queued = false;
        if (_scripts.TryGetValue(hash, out var script))
        {
            (files, states, queued) = script;
        }
        else
        {
            files = [MockTorBoxFile.Of(0, $"{name}/{name}.bin", 64 * 1024, (int)(++_nextFileId % 1000) + 1)];
            states = isCached
                ? [MockTorBoxState.CachedReady]
                : [MockTorBoxState.Queued, MockTorBoxState.Downloading(), MockTorBoxState.Ready];
        }

        var created = new MockTorBoxItem { Id = ++_nextId, Kind = kind, Hash = hash, Name = name, InQueue = queued };
        created.Files.AddRange(files);
        created.States.AddRange(states);
        configure(created);
        _items.Add(created);
        return Created(created, idField, "Added.");
    }

    private static Answer Created(MockTorBoxItem item, string idField, string detail)
    {
        // The real answers: no item id and no auth id while it waits for a slot.
        return item.InQueue
            ? Ok(new JsonObject { ["hash"] = item.Hash, ["name"] = item.Name, ["queued_id"] = item.Id }, "Torrent queued successfully.")
            : Ok(new JsonObject { ["hash"] = item.Hash, ["auth_id"] = "mock-auth", [idField] = item.Id }, detail);
    }

    private Answer RequestLink(RemoteItemKind kind, string idName, IQueryCollection query)
    {
        var token = query["token"].ToString();
        if (token.Length == 0)
        {
            return Validation("missing", "token", "Field required");
        }

        if (!long.TryParse(query[idName], out var id))
        {
            return Validation("missing", idName, "Field required");
        }

        lock (_sync)
        {
            if (!SameKey(token))
            {
                return BadKey();
            }

            var item = _items.FirstOrDefault(candidate => candidate.Id == id && candidate.Kind == kind);
            var files = item?.Current.ListsFiles == true ? item.Files : [];
            var file = long.TryParse(query["file_id"], out var fileId)
                ? files.FirstOrDefault(candidate => candidate.Id == fileId)
                : files.FirstOrDefault();

            // All the same on the real service, read for an item not on the account, a file not in it,
            // and an expired item. An item still being fetched is unseen, and this is the guess for it.
            if (item is null || file is null || !item.Current.Present)
            {
                return LinkFailure(kind);
            }

            var link = new MockCdnLink(Guid.NewGuid().ToString("D"), item, file, ApiKey, DateTimeOffset.UtcNow, _nextLinkDiesAfter);
            _nextLinkDiesAfter = null;
            _links[link.Uuid] = link;
            _issuedLinks.Add(link);
            // The real link carries the account key as its token, which is what makes it a credential. It
            // is the key with its dashes however it was asked for, and it has no file name in it.
            var words = kind switch
            {
                RemoteItemKind.Torrent => "Torrent download requested successfully.",
                RemoteItemKind.WebDownload => "Web download requested successfully.",
                _ => "Usenet download requested successfully.",
            };
            return Ok($"{_baseAddress()}/torbox-cdn/{link.Uuid}?token={Uri.EscapeDataString(ApiKey)}", words);
        }
    }

    private Answer Hosters()
    {
        lock (_sync)
        {
            return Ok(new JsonArray(_hosters.Select(hoster => hoster.DeepClone()).ToArray()), "Hosters list and information retrieved successfully.");
        }
    }

    internal Task HandleHosterAsync(HttpContext context)
    {
        Interlocked.Increment(ref _hosterRequests);
        context.Response.StatusCode = 200;
        return context.Response.WriteAsync("A hoster's own page, which nothing should have asked for.");
    }

    private Answer Queued(IQueryCollection query)
    {
        var type = query["type"].ToString();
        lock (_sync)
        {
            var list = new JsonArray();
            foreach (var item in _items.Where(candidate => candidate.InQueue))
            {
                var word = item.Kind switch
                {
                    RemoteItemKind.Torrent => "torrent",
                    RemoteItemKind.WebDownload => "webdl",
                    _ => "usenet",
                };
                // The real filter takes only these exact words. Any other answers an empty list.
                if (type.Length > 0 && !type.Equals(word, StringComparison.Ordinal))
                {
                    continue;
                }

                list.Add(new JsonObject
                {
                    ["id"] = item.Id,
                    ["created_at"] = item.CreatedAt,
                    ["magnet"] = item.Magnet,
                    ["torrent_file"] = null,
                    ["hash"] = item.Hash,
                    ["name"] = item.Name,
                    ["type"] = word,
                    ["name_override"] = null,
                    ["seed_torrent_override"] = null,
                });
            }

            return Page(list, query);
        }
    }

    private async Task<Answer> ControlAsync(HttpContext context, string route, RemoteItemKind kind, string idName)
    {
        using var document = await JsonDocument.ParseAsync(context.Request.Body, cancellationToken: context.RequestAborted).ConfigureAwait(false);
        var root = document.RootElement;
        if (!root.TryGetProperty("operation", out var operation) || operation.GetString() != "delete" ||
            !root.TryGetProperty(idName, out var idElement) || !idElement.TryGetInt64(out var id))
        {
            return Err(400, "INVALID_OPTION", "Unsupported operation.");
        }

        lock (_sync)
        {
            var item = _items.FirstOrDefault(candidate => candidate.Id == id && candidate.Kind == kind);
            if (item is null)
            {
                // As the real service answered on 2026-11-07: never ITEM_NOT_FOUND here. A torrent
                // that isn't there is a database error, and the others are "deleted".
                return kind switch
                {
                    RemoteItemKind.Torrent => Err(500, "DATABASE_ERROR", "There was an error processing your request. Please try again later."),
                    RemoteItemKind.WebDownload => Ok(null, "Web download deleted successfully."),
                    _ => Ok(null, "Usenet download deleted successfully."),
                };
            }

            _items.Remove(item);
            _deleted.Add((route, id));
            return Ok(null, "Removed.");
        }
    }

    private static bool IsTrue(string? value) => string.Equals(value, "true", StringComparison.OrdinalIgnoreCase);

    [GeneratedRegex(@"[?&]xt=urn:btih:([0-9a-fA-F]{40})(?:&|$)", RegexOptions.CultureInvariant)]
    private static partial Regex InfoHash();

    // ---- CDN ----------------------------------------------------------------------------------

    internal async Task HandleCdnAsync(HttpContext context, string uuid)
    {
        MockFault? fault;
        MockCdnLink? link;
        lock (_sync)
        {
            _calls[CdnRoute] = _calls.GetValueOrDefault(CdnRoute) + 1;
            if (context.Request.Headers.ContainsKey("Authorization") || context.Request.Headers.ContainsKey("Cookie")) _cdnWithCredentials++;
            fault = TakeFault(CdnRoute);
            _links.TryGetValue(uuid, out link);
        }

        switch (fault?.Kind)
        {
            case MockFaultKind.Abort:
                context.Abort();
                return;
            case MockFaultKind.Respond:
                await WriteFaultAsync(context, fault).ConfigureAwait(false);
                return;
            case MockFaultKind.DeadLink:
                context.Response.ContentType = "text/plain";
                await context.Response.WriteAsync(fault.RawBody ?? string.Empty).ConfigureAwait(false);
                return;
            case MockFaultKind.Delay:
                await Task.Delay(fault.DelayFor, context.RequestAborted).ConfigureAwait(false);
                break;
        }

        // Empty bodies, as with the real CDN: nothing tells one kind of refusal from another.
        if (link is null)
        {
            context.Response.StatusCode = 404;
            return;
        }

        // Exact, unlike the API: the same link with the key's dashes taken out answers 403.
        if (!string.Equals(context.Request.Query["token"], ApiKey, StringComparison.Ordinal))
        {
            context.Response.StatusCode = 403;
            return;
        }

        if (link.Expired || DateTimeOffset.UtcNow - link.IssuedAt > LinkLifetime)
        {
            context.Response.StatusCode = ExpiredLinkStatus;
            return;
        }

        bool itemExists;
        lock (_sync) itemExists = _items.Contains(link.Item);
        if (!itemExists)
        {
            context.Response.StatusCode = 404;
            return;
        }

        await ServeFileAsync(context, link).ConfigureAwait(false);
    }

    private async Task ServeFileAsync(HttpContext context, MockCdnLink link)
    {
        var file = link.File;
        byte[] content;
        DateTimeOffset modified;
        lock (_sync)
        {
            content = file.Content;
            modified = file.LastModified;
        }

        var lastModified = modified.ToString("R", CultureInfo.InvariantCulture);
        var response = context.Response;
        response.Headers.AcceptRanges = "bytes";
        response.Headers.LastModified = lastModified;
        response.Headers.ContentDisposition = $"attachment; filename=\"{file.ShortName}\"";
        response.ContentType = file.MimeType ?? "application/octet-stream";

        var range = context.Request.Headers.Range.ToString();
        var ifRange = context.Request.Headers.IfRange.ToString();
        long start = 0;
        var end = (long)content.Length - 1;
        var partial = false;
        if (range.Length > 0 && (ifRange.Length == 0 || ifRange == lastModified))
        {
            if (!TestServerHost.TryParseRange(range, content.Length, out start, out end))
            {
                // What the real CDN does past the end, seen live: a 206 with no range and an empty body. Not a 416.
                response.StatusCode = 206;
                link.AddRange(range);
                response.Headers.ContentRange = $"bytes */{content.Length}";
                response.ContentLength = 0;
                return;
            }

            partial = true;
            response.StatusCode = 206;
            response.Headers.ContentRange = $"bytes {start}-{end}/{content.Length}";
        }

        link.AddRange(partial ? range : null);

        // Seen live: an HTML file sent whole comes back rewritten by the CDN's edge, longer than the
        // file and with no Content-Length. As a range, even a range from zero, it is untouched.
        if (!partial && !HttpMethods.IsHead(context.Request.Method) && response.ContentType.StartsWith("text/html", StringComparison.OrdinalIgnoreCase))
        {
            await response.Body.WriteAsync(content, context.RequestAborted).ConfigureAwait(false);
            await response.WriteAsync("<script>added by the edge</script>", context.RequestAborted).ConfigureAwait(false);
            return;
        }

        var length = end - start + 1;
        response.ContentLength = Math.Max(0, length);
        if (HttpMethods.IsHead(context.Request.Method) || length <= 0)
        {
            return;
        }

        // Only clients still there count. An answer whose client has closed it can take a moment to wind down here,
        // and that is no connection the client is holding, which is what the provider's limit is about.
        lock (_activeAnswers)
        {
            _activeAnswers.Add(context);
            var active = _activeAnswers.Count(answer => !answer.RequestAborted.IsCancellationRequested);
            if (active > _peakCdn)
            {
                _peakCdn = active;
            }
        }

        try
        {
            var clock = System.Diagnostics.Stopwatch.StartNew();
            var written = 0L;
            while (written < length)
            {
                if (link.AbortInFlight)
                {
                    context.Abort();
                    return;
                }

                FileHold? hold;
                lock (_holds) _holds.TryGetValue(file.Id, out hold);
                if (hold is not null && Interlocked.Read(ref hold.Sent) >= hold.AfterBytes)
                {
                    await hold.Gate.Task.WaitAsync(context.RequestAborted).ConfigureAwait(false);
                }

                var count = (int)Math.Min(32 * 1024, length - written);
                await response.Body.WriteAsync(content.AsMemory((int)(start + written), count), context.RequestAborted).ConfigureAwait(false);
                if (hold is not null) Interlocked.Add(ref hold.Sent, count);
                await response.Body.FlushAsync(context.RequestAborted).ConfigureAwait(false);
                written += count;
                if (link.CountBytes(count))
                {
                    LinkDied?.Invoke();
                    context.Abort();
                    return;
                }

                if (CdnBytesPerSecond is { } bps && bps > 0)
                {
                    var due = TimeSpan.FromSeconds(written / (double)bps) - clock.Elapsed;
                    if (due > TimeSpan.Zero)
                    {
                        await Task.Delay(due, context.RequestAborted).ConfigureAwait(false);
                    }
                }
            }
        }
        catch (Exception) when (context.RequestAborted.IsCancellationRequested)
        {
            // The client went away.
        }
        finally
        {
            lock (_activeAnswers) _activeAnswers.Remove(context);
        }
    }
}

/// <summary>One direct link the mock has issued.</summary>
public sealed class MockCdnLink(string uuid, MockTorBoxItem item, MockTorBoxFile file, string token, DateTimeOffset issuedAt, long? diesAfterBytes)
{
    private readonly Lock _sync = new();
    private readonly List<string> _ranges = [];
    private long _served;

    public string Uuid { get; } = uuid;
    public MockTorBoxItem Item { get; } = item;
    public MockTorBoxFile File { get; } = file;
    public string Token { get; } = token;
    public DateTimeOffset IssuedAt { get; } = issuedAt;
    public bool Expired { get; set; }
    public bool AbortInFlight { get; set; }
    public long BytesServed => Interlocked.Read(ref _served);

    /// <summary>Range headers served through this link, oldest first.</summary>
    public IReadOnlyList<string> Ranges
    {
        get
        {
            lock (_sync) return _ranges.ToList();
        }
    }

    internal void AddRange(string? range)
    {
        if (range is null)
        {
            return;
        }

        lock (_sync) _ranges.Add(range);
    }

    /// <summary>Returns true when this count is the one that kills the link.</summary>
    internal bool CountBytes(int count)
    {
        var total = Interlocked.Add(ref _served, count);
        if (diesAfterBytes is { } limit && total >= limit && !Expired)
        {
            Expired = true;
            AbortInFlight = true;
            return true;
        }

        return false;
    }
}

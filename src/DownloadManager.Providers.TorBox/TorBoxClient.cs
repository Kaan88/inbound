using System.Globalization;
using System.Net;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using DownloadManager.Core.Providers;

namespace DownloadManager.Providers.TorBox;

/// <summary>
/// The TorBox API, separate from the download engine. Nothing here logs: request addresses can
/// hold the account key, and responses can hold links that work as credentials.
/// </summary>
public sealed class TorBoxClient :
    IRemoteProvider, IRemoteCatalog, IRemoteCacheChecker, IRemoteSubmitter, IRemoteLinkResolver,
    IRemoteHosterDirectory, IRemoteItemRemover, IRemoteQueue, IRemoteFolderLinkParser
{
    public const string ProviderId = "torbox";
    public static readonly Uri DefaultBaseAddress = new("https://api.torbox.app/v1/api/");

    /// <summary>Far more than a page of the largest real items, and small enough that no answer can take the Host down with it.</summary>
    public const long DefaultMostResponseBytes = 64 * 1024 * 1024;

    /// <summary>The window is about a minute. Anything much later isn't a window, so nothing waits for it.</summary>
    private static readonly TimeSpan LongestBudgetWindow = TimeSpan.FromMinutes(5);
    private static readonly TimeSpan LongestBudgetWait = TimeSpan.FromSeconds(90);

    private readonly HttpClient _http;
    private readonly Func<string?> _key;
    private readonly Uri _base;
    private readonly TimeProvider _time;
    private readonly long _mostResponseBytes;
    // Ticks of the time, for an atomic read and write. Zero is the distant past.
    private long _budgetExhaustedUntilTicks;

    /// <param name="http">Must not follow the API's links by itself on behalf of callers; requests are all to the API.</param>
    /// <param name="key">Reads the current key each call, so changing it takes effect at once.</param>
    public TorBoxClient(HttpClient http, Func<string?> key, Uri? baseAddress = null, TimeProvider? time = null, long mostResponseBytes = DefaultMostResponseBytes)
    {
        _http = http;
        _key = key;
        _base = baseAddress ?? DefaultBaseAddress;
        _time = time ?? TimeProvider.System;
        _mostResponseBytes = mostResponseBytes;
    }

    public string Id => ProviderId;
    public string DisplayName => "TorBox";

    public RemoteCapabilities Capabilities =>
        RemoteCapabilities.Account | RemoteCapabilities.Catalog | RemoteCapabilities.Torrents |
        RemoteCapabilities.WebDownloads | RemoteCapabilities.Usenet | RemoteCapabilities.CacheCheck |
        RemoteCapabilities.HosterList;

    public RemoteTransferPolicy TransferPolicy { get; } = new()
    {
        MaxConnectionsPerFile = 4,
        CreationSpacing = TimeSpan.FromSeconds(65),
        ShortestPoll = TimeSpan.FromSeconds(15),
        LongestPoll = TimeSpan.FromSeconds(60),
        DefaultPoll = TimeSpan.FromSeconds(30),
    };

    public string? NormalizeItemId(string itemId) => TorBoxMapping.CanonicalId(itemId);

    public async Task<RemoteAccountInfo> GetAccountAsync(CancellationToken cancellationToken = default)
    {
        using var document = await SendAsync(HttpMethod.Get, "user/me", null, false, cancellationToken).ConfigureAwait(false);
        var data = Data(document);
        var plan = Int(data, "plan");
        var until = Date(data, "premium_expires_at");
        return new RemoteAccountInfo
        {
            DisplayPlan = plan switch { 0 => "Free", 1 => "Essential", 2 => "Pro", 3 => "Standard", null => null, _ => $"Plan {plan}" },
            Premium = until is { } end && end > _time.GetUtcNow(),
            PremiumUntil = until,
            CooldownUntil = Date(data, "cooldown_until"),
        };
    }

    public async Task<IReadOnlyList<RemoteItem>> ListAsync(RemoteItemKind kind, int offset, int limit, bool fresh, CancellationToken cancellationToken = default)
    {
        var path = $"{Family(kind)}/mylist?offset={offset}&limit={limit}{(fresh ? "&bypass_cache=true" : "")}";
        try
        {
            using var document = await SendAsync(HttpMethod.Get, path, null, false, cancellationToken).ConfigureAwait(false);
            return ReadList(document, element => TorBoxMapping.Item(kind, element));
        }
        catch (RemoteProviderException ex) when (ex.Kind == RemoteFailureKind.NotFound)
        {
            // "No torrents found for this user" is an answer, not a failure. Only that gets here: see TorBoxMapping.Failure.
            return [];
        }
    }

    /// <summary>
    /// A complete list, or a failure. Calling a broken answer empty would let a lost submission look
    /// absent, and that sends it again. An empty list really is <c>data:[]</c>, on all three families
    /// and the queue, so null isn't taken for one. The reference client also accepts a single item
    /// not wrapped in a list.
    /// </summary>
    private static List<T> ReadList<T>(JsonDocument document, Func<JsonElement, T> read)
    {
        var data = Data(document);
        return data.ValueKind switch
        {
            JsonValueKind.Array => data.EnumerateArray().Select(read).ToList(),
            JsonValueKind.Object when data.TryGetProperty("id", out _) => [read(data)],
            _ => throw NotAList(),
        };
    }

    private static RemoteProviderException NotAList() =>
        new(RemoteFailureKind.BadResponse, null, "TorBox sent a list that could not be read.");

    public async Task<RemoteItem?> GetAsync(RemoteItemRef item, bool fresh, CancellationToken cancellationToken = default)
    {
        var path = $"{Family(item.Kind)}/mylist?id={Uri.EscapeDataString(item.ItemId)}{(fresh ? "&bypass_cache=true" : "")}";
        try
        {
            using var document = await SendAsync(HttpMethod.Get, path, null, false, cancellationToken).ConfigureAwait(false);
            var data = Data(document);
            var read = data.ValueKind switch
            {
                JsonValueKind.Object when data.TryGetProperty("id", out _) => data,
                JsonValueKind.Array when data.GetArrayLength() == 0 => (JsonElement?)null,
                JsonValueKind.Array => data.EnumerateArray().Cast<JsonElement?>().FirstOrDefault(entry => SameId(entry!.Value, item.ItemId))
                    ?? throw NotAList(),
                // A missing item answers with its own error code. This says nothing either way.
                _ => throw NotAList(),
            };
            return read is { } element ? TorBoxMapping.Item(item.Kind, element) : null;
        }
        catch (RemoteProviderException ex) when (ex.Kind == RemoteFailureKind.NotFound)
        {
            return null;
        }
    }

    private static bool SameId(JsonElement element, string id)
    {
        var read = TorBoxMapping.IdText(element, "id");
        return read == id || (read is not null && TorBoxMapping.CanonicalId(read) is { } canonical && canonical == TorBoxMapping.CanonicalId(id));
    }

    public async Task<IReadOnlyList<RemoteQueuedItem>> ListQueuedAsync(RemoteItemKind kind, int offset, int limit, bool fresh, CancellationToken cancellationToken = default)
    {
        var type = kind switch
        {
            RemoteItemKind.Torrent => "torrent",
            RemoteItemKind.WebDownload => "webdl",
            _ => "usenet",
        };
        var path = $"queued/getqueued?type={type}&offset={offset}&limit={limit}{(fresh ? "&bypass_cache=true" : "")}";
        try
        {
            using var document = await SendAsync(HttpMethod.Get, path, null, false, cancellationToken).ConfigureAwait(false);
            return ReadList(document, entry => new RemoteQueuedItem(TorBoxMapping.IdText(entry, "id"), Text(entry, "hash"), Text(entry, "name")));
        }
        catch (RemoteProviderException ex) when (ex.Kind == RemoteFailureKind.NotFound)
        {
            return [];
        }
    }

    public RemoteFolderLinkParse? ParseFolderLink(string? text) => TorBoxFolderLink.Parse(text);

    public Task<IReadOnlyDictionary<string, RemoteCacheHit>> CheckCachedAsync(RemoteItemKind kind, IReadOnlyCollection<string> hashes, CancellationToken cancellationToken = default)
        => CheckCachedAsync(kind, hashes, listFiles: false, cancellationToken);

    /// <summary>Its files carry the ids the item gets on the account, seen to be the same on a real one.</summary>
    public Task<IReadOnlyDictionary<string, RemoteCacheHit>> CheckCachedWithFilesAsync(RemoteItemKind kind, IReadOnlyCollection<string> hashes, CancellationToken cancellationToken = default)
        => CheckCachedAsync(kind, hashes, listFiles: true, cancellationToken);

    private async Task<IReadOnlyDictionary<string, RemoteCacheHit>> CheckCachedAsync(RemoteItemKind kind, IReadOnlyCollection<string> hashes, bool listFiles, CancellationToken cancellationToken)
    {
        var result = new Dictionary<string, RemoteCacheHit>(StringComparer.OrdinalIgnoreCase);
        // The query form takes a list too, but a body keeps long lists out of the address.
        foreach (var chunk in hashes.Distinct(StringComparer.OrdinalIgnoreCase).Chunk(100))
        {
            using var body = new StringContent(JsonSerializer.Serialize(new { hashes = chunk }), Encoding.UTF8, "application/json");
            using var document = await SendAsync(HttpMethod.Post, $"{Family(kind)}/checkcached?format=object&list_files={(listFiles ? "true" : "false")}", body, false, cancellationToken).ConfigureAwait(false);
            var data = Data(document);
            if (data.ValueKind != JsonValueKind.Object) continue;
            foreach (var entry in data.EnumerateObject())
            {
                if (entry.Value.ValueKind != JsonValueKind.Object) continue;
                var hit = new RemoteCacheHit(entry.Name, Text(entry.Value, "name"), Long(entry.Value, "size"), listFiles ? TorBoxMapping.Files(entry.Value) : null);
                result[entry.Name] = hit;

                // Asked by one of an item's alternative hashes, the answer comes back under its main
                // one. With one hash asked, that is the answer to it. With several it can't be told apart.
                if (chunk.Length == 1) result[chunk[0]] = hit;
            }
        }

        return result;
    }

    public string? PredictHash(RemoteItemKind kind, string link)
    {
        if (kind == RemoteItemKind.WebDownload)
        {
            return Convert.ToHexString(MD5.HashData(Encoding.UTF8.GetBytes(link))).ToLowerInvariant();
        }

        return kind == RemoteItemKind.Torrent ? TorBoxMapping.InfoHashFromMagnet(link) : null;
    }

    public async Task<RemoteSubmissionResult> SubmitAsync(RemoteSubmission submission, CancellationToken cancellationToken = default)
    {
        HttpContent content;
        string path;
        string idField;
        switch (submission.Kind)
        {
            case RemoteItemKind.Torrent:
                var multipart = new MultipartFormDataContent();
                if (submission.TorrentFile is { } torrent)
                {
                    var file = new ByteArrayContent(torrent);
                    file.Headers.ContentType = new MediaTypeHeaderValue("application/x-bittorrent");
                    multipart.Add(file, "file", "upload.torrent");
                }
                else if (!string.IsNullOrWhiteSpace(submission.Link))
                {
                    multipart.Add(new StringContent(submission.Link), "magnet");
                }
                else
                {
                    multipart.Dispose();
                    throw new ArgumentException("A torrent submission needs a magnet or a file.", nameof(submission));
                }

                if (submission.OnlyIfCached) multipart.Add(new StringContent("true"), "add_only_if_cached");
                content = multipart;
                path = "torrents/createtorrent";
                idField = "torrent_id";
                break;

            case RemoteItemKind.WebDownload:
                if (string.IsNullOrWhiteSpace(submission.Link))
                {
                    throw new ArgumentException("A web download needs a link.", nameof(submission));
                }

                var fields = new List<KeyValuePair<string, string>> { new("link", submission.Link) };
                if (!string.IsNullOrEmpty(submission.LinkPassword)) fields.Add(new("password", submission.LinkPassword));
                if (submission.OnlyIfCached) fields.Add(new("add_only_if_cached", "true"));
                content = new FormUrlEncodedContent(fields);
                path = "webdl/createwebdownload";
                idField = "webdownload_id";
                break;

            default:
                throw new NotSupportedException("Usenet items can be browsed and downloaded, not submitted.");
        }

        using (content)
        {
            JsonDocument answer;
            try
            {
                answer = await SendAsync(HttpMethod.Post, path, content, true, cancellationToken).ConfigureAwait(false);
            }
            catch (RemoteProviderException ex) when (submission.Kind == RemoteItemKind.WebDownload)
            {
                throw TorBoxMapping.ForWebSubmission(ex);
            }

            using var document = answer;
            var data = Data(document);
            var id = data.ValueKind == JsonValueKind.Object ? TorBoxMapping.IdText(data, idField) ?? TorBoxMapping.IdText(data, "id") : null;
            var hash = data.ValueKind == JsonValueKind.Object ? Text(data, "hash") : null;
            var queued = data.ValueKind == JsonValueKind.Object && data.TryGetProperty("queued_id", out var queuedId) && queuedId.ValueKind != JsonValueKind.Null;
            var reference = id is null ? null : new RemoteItemRef(ProviderId, submission.Kind, id);
            return new RemoteSubmissionResult(reference, hash, queued);
        }
    }

    public async Task<RemoteDirectLink> ResolveLinkAsync(RemoteItemRef item, string fileId, CancellationToken cancellationToken = default)
    {
        var key = _key();
        if (string.IsNullOrWhiteSpace(key))
        {
            throw new RemoteProviderException(RemoteFailureKind.Authentication, null, "No TorBox API key is set.");
        }

        var idName = item.Kind switch
        {
            RemoteItemKind.Torrent => "torrent_id",
            RemoteItemKind.WebDownload => "web_id",
            _ => "usenet_id",
        };
        var path = $"{Family(item.Kind)}/requestdl?{idName}={Uri.EscapeDataString(item.ItemId)}&file_id={Uri.EscapeDataString(fileId)}&token={Uri.EscapeDataString(key)}";
        using var document = await SendAsync(HttpMethod.Get, path, null, false, cancellationToken).ConfigureAwait(false);
        var data = Data(document);
        if (data.ValueKind == JsonValueKind.String &&
            Uri.TryCreate(data.GetString(), UriKind.Absolute, out var url) &&
            IsAcceptableLink(url))
        {
            return new RemoteDirectLink(url);
        }

        throw new RemoteProviderException(RemoteFailureKind.BadResponse, null, "TorBox did not return a usable download link.");
    }

    public async Task<IReadOnlyList<RemoteHoster>> GetHostersAsync(CancellationToken cancellationToken = default)
    {
        using var document = await SendAsync(HttpMethod.Get, "webdl/hosters", null, false, cancellationToken).ConfigureAwait(false);
        var data = Data(document);
        // "None" here would turn every hoster link into an ordinary one for an hour.
        if (data.ValueKind != JsonValueKind.Array) throw NotAList();
        return data.EnumerateArray().Select(TorBoxMapping.Hoster).ToList();
    }

    public async Task RemoveAsync(RemoteItemRef item, CancellationToken cancellationToken = default)
    {
        var (path, idName) = item.Kind switch
        {
            RemoteItemKind.Torrent => ("torrents/controltorrent", "torrent_id"),
            RemoteItemKind.WebDownload => ("webdl/controlwebdownload", "webdl_id"),
            _ => ("usenet/controlusenetdownload", "usenet_id"),
        };
        if (!long.TryParse(item.ItemId, NumberStyles.None, CultureInfo.InvariantCulture, out var id))
        {
            throw new ArgumentException("Not a TorBox id.", nameof(item));
        }

        var payload = new Dictionary<string, object> { ["operation"] = "delete", [idName] = id };
        using var body = new StringContent(JsonSerializer.Serialize(payload), Encoding.UTF8, "application/json");
        using var document = await SendAsync(HttpMethod.Post, path, body, false, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>A link carries the key, so it must be encrypted, unless the whole API is a local test server.</summary>
    private bool IsAcceptableLink(Uri url) =>
        url.Scheme == Uri.UriSchemeHttps ||
        (url.Scheme == Uri.UriSchemeHttp && url.IsLoopback && _base.IsLoopback);

    private static string Family(RemoteItemKind kind) => kind switch
    {
        RemoteItemKind.Torrent => "torrents",
        RemoteItemKind.WebDownload => "webdl",
        RemoteItemKind.Usenet => "usenet",
        _ => throw new ArgumentOutOfRangeException(nameof(kind)),
    };

    /// <param name="isSubmission">A network failure then means "may have been accepted", which callers reconcile.</param>
    private async Task<JsonDocument> SendAsync(HttpMethod method, string relative, HttpContent? content, bool isSubmission, CancellationToken cancellationToken)
    {
        var key = _key();
        if (string.IsNullOrWhiteSpace(key))
        {
            throw new RemoteProviderException(RemoteFailureKind.Authentication, null, "No TorBox API key is set.");
        }

        await WaitForBudgetAsync(cancellationToken).ConfigureAwait(false);
        using var request = new HttpRequestMessage(method, new Uri(_base, relative)) { Content = content };
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", key);

        HttpResponseMessage response;
        try
        {
            // Headers only: the body is read below, up to a limit, because it can be any size at all once unpacked.
            response = await _http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException && !cancellationToken.IsCancellationRequested)
        {
            // HttpRequestException text can quote the address, which holds the key on requestdl.
            throw Unreachable(isSubmission);
        }

        using (response)
        {
            NoteBudget(response);
            JsonDocument? document = null;
            try
            {
                using var body = await ReadBodyAsync(response, isSubmission, cancellationToken).ConfigureAwait(false);
                document = JsonDocument.Parse(body);
            }
            catch (JsonException)
            {
                document = null;
            }

            var root = document?.RootElement;
            var code = root is { ValueKind: JsonValueKind.Object } read ? Text(read, "error") : null;
            var detail = root is { ValueKind: JsonValueKind.Object } read2 ? Text(read2, "detail") : null;
            var status = response.StatusCode;

            // TorBox can answer 404 with success:true, so the error field decides, not success.
            if (status == HttpStatusCode.TooManyRequests)
            {
                document?.Dispose();
                var wait = response.Headers.RetryAfter?.Delta
                    ?? (response.Headers.RetryAfter?.Date is { } until && until > _time.GetUtcNow() ? until - _time.GetUtcNow() : TorBoxMapping.RateLimitWait(detail));
                throw new RemoteProviderException(RemoteFailureKind.RateLimited, code,
                    detail is null ? "TorBox asked to slow down." : UrlRedactor.HideAddresses(detail), wait);
            }

            if (code is not null || !response.IsSuccessStatusCode)
            {
                document?.Dispose();
                throw TorBoxMapping.Failure(status, code, detail);
            }

            if (document is null)
            {
                throw new RemoteProviderException(RemoteFailureKind.BadResponse, null, "TorBox sent a response that could not be read.");
            }

            // Success with no code to go by: it didn't work, and it doesn't say that nothing happened.
            if (root is { ValueKind: JsonValueKind.Object } envelope && envelope.TryGetProperty("success", out var success) && success.ValueKind == JsonValueKind.False)
            {
                document.Dispose();
                throw new RemoteProviderException(RemoteFailureKind.BadResponse, null,
                    detail is null ? "TorBox answered that this failed, without saying why." : UrlRedactor.HideAddresses(detail));
            }

            return document;
        }
    }

    private static RemoteProviderException Unreachable(bool isSubmission) => new(
        RemoteFailureKind.Network, null,
        isSubmission ? "TorBox could not be reached, and may have received the request." : "TorBox could not be reached.");

    /// <summary>
    /// The unpacked body, up to the limit. Reading with the headers alone means the client's own timeout
    /// no longer covers this, so it is applied here.
    /// </summary>
    private async Task<MemoryStream> ReadBodyAsync(HttpResponseMessage response, bool isSubmission, CancellationToken cancellationToken)
    {
        if (response.Content.Headers.ContentLength > _mostResponseBytes)
        {
            throw TooLarge();
        }

        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        if (_http.Timeout != Timeout.InfiniteTimeSpan)
        {
            timeout.CancelAfter(_http.Timeout);
        }

        var body = new MemoryStream();
        try
        {
            await using var stream = await response.Content.ReadAsStreamAsync(timeout.Token).ConfigureAwait(false);
            var chunk = new byte[64 * 1024];
            int read;
            while ((read = await stream.ReadAsync(chunk, timeout.Token).ConfigureAwait(false)) > 0)
            {
                if (body.Length + read > _mostResponseBytes)
                {
                    throw TooLarge();
                }

                body.Write(chunk, 0, read);
            }

            body.Position = 0;
            return body;
        }
        catch (Exception ex) when (ex is HttpRequestException or IOException or OperationCanceledException && !cancellationToken.IsCancellationRequested)
        {
            await body.DisposeAsync().ConfigureAwait(false);
            throw Unreachable(isSubmission);
        }
        catch
        {
            await body.DisposeAsync().ConfigureAwait(false);
            throw;
        }
    }

    private static RemoteProviderException TooLarge() =>
        new(RemoteFailureKind.BadResponse, null, "TorBox sent a response too large to read.");

    /// <summary>
    /// Waits only when the window is actually used up. Retry-After is not consulted here because
    /// TorBox sends it on every response, including successful ones. Each caller waits for itself:
    /// holding a gate across this would queue every other call behind it, a submission among them.
    /// </summary>
    private async Task WaitForBudgetAsync(CancellationToken cancellationToken)
    {
        var until = new DateTimeOffset(Interlocked.Read(ref _budgetExhaustedUntilTicks), TimeSpan.Zero);
        var wait = until - _time.GetUtcNow();
        if (wait > TimeSpan.Zero)
        {
            await Task.Delay(wait > LongestBudgetWait ? LongestBudgetWait : wait, _time, cancellationToken).ConfigureAwait(false);
        }
    }

    private void NoteBudget(HttpResponseMessage response)
    {
        if (!response.Headers.TryGetValues("X-RateLimit-Remaining", out var remainingValues) ||
            !int.TryParse(remainingValues.FirstOrDefault(), NumberStyles.Integer, CultureInfo.InvariantCulture, out var remaining))
        {
            return;
        }

        if (remaining > 1)
        {
            // Budget is back, whatever an earlier answer said about when it would be.
            Interlocked.Exchange(ref _budgetExhaustedUntilTicks, 0);
            return;
        }

        if (!response.Headers.TryGetValues("X-RateLimit-Reset", out var resetValues) ||
            !double.TryParse(resetValues.FirstOrDefault(), NumberStyles.Float, CultureInfo.InvariantCulture, out var reset) ||
            !double.IsFinite(reset) || reset <= 0 || reset * 1000 >= DateTimeOffset.MaxValue.ToUnixTimeMilliseconds())
        {
            return;
        }

        var until = DateTimeOffset.FromUnixTimeMilliseconds((long)(reset * 1000));
        if (until - _time.GetUtcNow() <= LongestBudgetWindow)
        {
            Interlocked.Exchange(ref _budgetExhaustedUntilTicks, until.UtcTicks);
        }
    }

    private static JsonElement Data(JsonDocument document) =>
        document.RootElement.ValueKind == JsonValueKind.Object && document.RootElement.TryGetProperty("data", out var data)
            ? data
            : default;

    private static string? Text(JsonElement element, string name) => TorBoxMapping.Text(element, name);
    private static long? Long(JsonElement element, string name) => TorBoxMapping.Long(element, name);
    private static int? Int(JsonElement element, string name) => TorBoxMapping.Long(element, name) is { } value ? (int)value : null;
    private static bool Bool(JsonElement element, string name) => TorBoxMapping.Bool(element, name);
    private static DateTimeOffset? Date(JsonElement element, string name) => TorBoxMapping.Date(element, name);
}

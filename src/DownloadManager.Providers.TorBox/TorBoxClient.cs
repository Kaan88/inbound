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
    IRemoteHosterDirectory, IRemoteItemRemover
{
    public const string ProviderId = "torbox";
    public static readonly Uri DefaultBaseAddress = new("https://api.torbox.app/v1/api/");

    private readonly HttpClient _http;
    private readonly Func<string?> _key;
    private readonly Uri _base;
    private readonly TimeProvider _time;
    private readonly SemaphoreSlim _budgetGate = new(1, 1);
    private DateTimeOffset _budgetExhaustedUntil = DateTimeOffset.MinValue;

    /// <param name="http">Must not follow the API's links by itself on behalf of callers; requests are all to the API.</param>
    /// <param name="key">Reads the current key each call, so changing it takes effect at once.</param>
    public TorBoxClient(HttpClient http, Func<string?> key, Uri? baseAddress = null, TimeProvider? time = null)
    {
        _http = http;
        _key = key;
        _base = baseAddress ?? DefaultBaseAddress;
        _time = time ?? TimeProvider.System;
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
        LinkLifetime = TimeSpan.FromHours(3),
    };

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
        using var document = await SendAsync(HttpMethod.Get, path, null, false, cancellationToken).ConfigureAwait(false);
        var data = Data(document);
        if (data.ValueKind != JsonValueKind.Array) return [];
        return data.EnumerateArray().Select(element => TorBoxMapping.Item(kind, element)).ToList();
    }

    public async Task<RemoteItem?> GetAsync(RemoteItemRef item, bool fresh, CancellationToken cancellationToken = default)
    {
        var path = $"{Family(item.Kind)}/mylist?id={Uri.EscapeDataString(item.ItemId)}{(fresh ? "&bypass_cache=true" : "")}";
        try
        {
            using var document = await SendAsync(HttpMethod.Get, path, null, false, cancellationToken).ConfigureAwait(false);
            var data = Data(document);
            return data.ValueKind switch
            {
                JsonValueKind.Object when data.TryGetProperty("id", out _) => TorBoxMapping.Item(item.Kind, data),
                JsonValueKind.Array when data.GetArrayLength() > 0 => TorBoxMapping.Item(item.Kind, data[0]),
                _ => null,
            };
        }
        catch (RemoteProviderException ex) when (ex.Kind == RemoteFailureKind.NotFound)
        {
            return null;
        }
    }

    public async Task<IReadOnlyDictionary<string, RemoteCacheHit>> CheckCachedAsync(RemoteItemKind kind, IReadOnlyCollection<string> hashes, CancellationToken cancellationToken = default)
    {
        var result = new Dictionary<string, RemoteCacheHit>(StringComparer.OrdinalIgnoreCase);
        // The query form takes a list too, but a body keeps long lists out of the address.
        foreach (var chunk in hashes.Distinct(StringComparer.OrdinalIgnoreCase).Chunk(100))
        {
            using var body = new StringContent(JsonSerializer.Serialize(new { hashes = chunk }), Encoding.UTF8, "application/json");
            using var document = await SendAsync(HttpMethod.Post, $"{Family(kind)}/checkcached?format=object&list_files=false", body, false, cancellationToken).ConfigureAwait(false);
            var data = Data(document);
            if (data.ValueKind != JsonValueKind.Object) continue;
            foreach (var entry in data.EnumerateObject())
            {
                if (entry.Value.ValueKind != JsonValueKind.Object) continue;
                result[entry.Name] = new RemoteCacheHit(entry.Name, Text(entry.Value, "name"), Long(entry.Value, "size"));
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

                if (!string.IsNullOrWhiteSpace(submission.DisplayName)) multipart.Add(new StringContent(submission.DisplayName), "name");
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
                if (!string.IsNullOrWhiteSpace(submission.DisplayName)) fields.Add(new("name", submission.DisplayName));
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
            using var document = await SendAsync(HttpMethod.Post, path, content, true, cancellationToken).ConfigureAwait(false);
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
            url.Scheme == Uri.UriSchemeHttps)
        {
            return new RemoteDirectLink(url, _time.GetUtcNow());
        }

        throw new RemoteProviderException(RemoteFailureKind.BadResponse, null, "TorBox did not return a usable download link.");
    }

    public async Task<IReadOnlyList<RemoteHoster>> GetHostersAsync(CancellationToken cancellationToken = default)
    {
        using var document = await SendAsync(HttpMethod.Get, "webdl/hosters", null, false, cancellationToken).ConfigureAwait(false);
        var data = Data(document);
        if (data.ValueKind != JsonValueKind.Array) return [];
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
            response = await _http.SendAsync(request, HttpCompletionOption.ResponseContentRead, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException && !cancellationToken.IsCancellationRequested)
        {
            // HttpRequestException text can quote the address, which holds the key on requestdl.
            throw new RemoteProviderException(
                RemoteFailureKind.Network, null,
                isSubmission ? "TorBox could not be reached, and may have received the request." : "TorBox could not be reached.");
        }

        using (response)
        {
            NoteBudget(response);
            var text = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
            JsonDocument? document = null;
            try
            {
                if (!string.IsNullOrWhiteSpace(text)) document = JsonDocument.Parse(text);
            }
            catch (JsonException)
            {
                document = null;
            }

            var code = document?.RootElement is { ValueKind: JsonValueKind.Object } root ? Text(root, "error") : null;
            var detail = document?.RootElement is { ValueKind: JsonValueKind.Object } root2 ? Text(root2, "detail") : null;
            var status = response.StatusCode;

            // TorBox can answer 404 with success:true, so the error field decides, not success.
            if (status == HttpStatusCode.TooManyRequests)
            {
                document?.Dispose();
                var wait = response.Headers.RetryAfter?.Delta ?? TimeSpan.FromSeconds(60);
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

            return document;
        }
    }

    /// <summary>
    /// Waits only when the window is actually used up. Retry-After is not consulted here because
    /// TorBox sends it on every response, including successful ones.
    /// </summary>
    private async Task WaitForBudgetAsync(CancellationToken cancellationToken)
    {
        await _budgetGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var wait = _budgetExhaustedUntil - _time.GetUtcNow();
            if (wait > TimeSpan.Zero)
            {
                await Task.Delay(wait > TimeSpan.FromSeconds(90) ? TimeSpan.FromSeconds(90) : wait, _time, cancellationToken).ConfigureAwait(false);
            }
        }
        finally
        {
            _budgetGate.Release();
        }
    }

    private void NoteBudget(HttpResponseMessage response)
    {
        if (!response.Headers.TryGetValues("X-RateLimit-Remaining", out var remainingValues) ||
            !int.TryParse(remainingValues.FirstOrDefault(), NumberStyles.Integer, CultureInfo.InvariantCulture, out var remaining) ||
            remaining > 1)
        {
            return;
        }

        if (response.Headers.TryGetValues("X-RateLimit-Reset", out var resetValues) &&
            double.TryParse(resetValues.FirstOrDefault(), NumberStyles.Float, CultureInfo.InvariantCulture, out var reset))
        {
            _budgetExhaustedUntil = DateTimeOffset.FromUnixTimeMilliseconds((long)(reset * 1000));
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

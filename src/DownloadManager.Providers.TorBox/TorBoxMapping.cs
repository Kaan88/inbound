using System.Globalization;
using System.Net;
using System.Text.Json;
using System.Text.RegularExpressions;
using DownloadManager.Core.Providers;

namespace DownloadManager.Providers.TorBox;

/// <summary>Turns TorBox's responses into provider-neutral values. Fields are read tolerantly: any can be absent.</summary>
public static partial class TorBoxMapping
{
    /// <summary>The real service's word for no estimate: exactly 100 days, on every item with nothing to go by yet.</summary>
    private const long NoEstimateSeconds = 8_640_000;

    /// <summary>
    /// The one table of TorBox states. Readiness never comes from here: an item is ready only when
    /// <c>download_present</c> is set together with <c>cached</c> or <c>download_finished</c>.
    /// Seen on a real account: cached, completed, downloading, uploading, "uploading (no peers)", expired,
    /// metaDL, checking, checkingResumeData and "stalled (no seeds)". An expired item keeps its file list,
    /// but its files can't be fetched and it doesn't come back by waiting, so it counts as failed. The other
    /// words here come from the reference client.
    /// </summary>
    public static RemoteStage StageFromState(string? state)
    {
        var word = state?.Trim().ToLowerInvariant();
        if (string.IsNullOrEmpty(word)) return RemoteStage.Unknown;
        if (word.StartsWith("stalled", StringComparison.Ordinal)) return RemoteStage.Stalled;
        if (word.StartsWith("failed", StringComparison.Ordinal) || word.StartsWith("error", StringComparison.Ordinal)) return RemoteStage.Failed;

        // A remark can follow the state in brackets, as in "uploading (no peers)".
        var remark = word.IndexOf('(', StringComparison.Ordinal);
        if (remark > 0) word = word[..remark].TrimEnd();
        return word switch
        {
            "expired" => RemoteStage.Failed,
            "queued" or "pending" or "waiting" or "allocating" => RemoteStage.Queued,
            "metadl" or "metadata" or "checkingresumedata" or "checking" => RemoteStage.MetadataPending,
            "downloading" or "uploading" or "processing" or "paused" or "moving" or "compressing" or "repairing" or "extracting" => RemoteStage.Processing,
            // These words alone do not prove the files can be fetched yet.
            "completed" or "cached" or "seeding" => RemoteStage.Processing,
            _ => RemoteStage.Unknown,
        };
    }

    public static RemoteItem Item(RemoteItemKind kind, JsonElement element)
    {
        var state = Text(element, "download_state");
        var error = Text(element, "error");
        var finished = Bool(element, "download_finished");
        var present = Bool(element, "download_present");

        var files = Files(element);

        // Readiness is checked first, as the reference client does: a file that can be fetched can be
        // fetched, whatever an earlier attempt left in the error field. No file list yet means it can't.
        var stage = present && (finished || Bool(element, "cached")) && files.Count > 0
            ? RemoteStage.Ready
            : !string.IsNullOrWhiteSpace(error) ? RemoteStage.Failed
            : present && (finished || Bool(element, "cached")) ? RemoteStage.MetadataPending
            : StageFromState(state);

        var alternatives = new List<string>();
        if (element.ValueKind == JsonValueKind.Object && element.TryGetProperty("alternative_hashes", out var other) && other.ValueKind == JsonValueKind.Array)
        {
            alternatives.AddRange(other.EnumerateArray().Where(hash => hash.ValueKind == JsonValueKind.String).Select(hash => hash.GetString()!));
        }

        return new RemoteItem
        {
            Ref = new RemoteItemRef(TorBoxClient.ProviderId, kind, IdText(element, "id") ?? ""),
            Name = Text(element, "name") ?? "",
            Hash = Text(element, "hash"),
            AlternativeHashes = alternatives,
            // While the service doesn't know yet, the real answer is 0 or -1.
            Size = Math.Max(0, Long(element, "size") ?? 0),
            Stage = stage,
            RawState = state,
            Progress = Math.Clamp(Double(element, "progress") ?? 0, 0, 1),
            SpeedBytesPerSecond = Long(element, "download_speed") ?? 0,
            // Once the transfer has finished, the real figure counts down about 24 hours of seeding, not what is left to wait.
            EtaSeconds = !finished && Long(element, "eta") is { } eta && eta < NoEstimateSeconds ? eta : null,
            CreatedAt = Date(element, "created_at"),
            UpdatedAt = Date(element, "updated_at"),
            ExpiresAt = Date(element, "expires_at"),
            Cached = Bool(element, "cached"),
            Error = error,
            Files = files,
        };
    }

    /// <summary>
    /// The <c>files</c> of an item, or of a cache check that asked for them, which have the same fields and the same
    /// ids. A file without an id or a name is left out.
    /// </summary>
    public static List<RemoteFile> Files(JsonElement element)
    {
        var files = new List<RemoteFile>();
        if (element.ValueKind != JsonValueKind.Object || !element.TryGetProperty("files", out var list) || list.ValueKind != JsonValueKind.Array)
        {
            return files;
        }

        foreach (var file in list.EnumerateArray())
        {
            var id = IdText(file, "id");
            var path = Text(file, "name") ?? Text(file, "short_name");
            if (id is null || path is null) continue;
            files.Add(new RemoteFile
            {
                FileId = id,
                Path = path,
                Size = Long(file, "size") ?? 0,
                Md5 = Text(file, "md5"),
                MimeType = Text(file, "mimetype"),
                Infected = Bool(file, "infected"),
            });
        }

        return files;
    }

    public static RemoteHoster Hoster(JsonElement element)
    {
        var domains = new List<string>();
        if (element.ValueKind == JsonValueKind.Object && element.TryGetProperty("domains", out var list) && list.ValueKind == JsonValueKind.Array)
        {
            domains.AddRange(list.EnumerateArray().Where(domain => domain.ValueKind == JsonValueKind.String).Select(domain => domain.GetString()!));
        }

        // As the reference client does: with no list, the name is the only domain there is.
        var name = Text(element, "name") ?? "";
        if (domains.Count == 0 && name.Length > 0) domains.Add(name);

        return new RemoteHoster
        {
            Name = name,
            Domains = domains,
            // Only an exact false means down. A missing status says nothing against it.
            Available = !(element.ValueKind == JsonValueKind.Object && element.TryGetProperty("status", out var status) && status.ValueKind == JsonValueKind.False),
            Pattern = Text(element, "regex"),
            Note = Text(element, "note"),
            DailyLinkLimit = (int?)Long(element, "daily_link_limit"),
            DailyLinksUsed = (int?)Long(element, "daily_link_used"),
            DailyBandwidthLimit = Long(element, "daily_bandwidth_limit"),
            DailyBandwidthUsed = Long(element, "daily_bandwidth_used"),
            PerLinkSizeLimit = Long(element, "per_link_size_limit"),
            // Only an https picture: the window loads it, and anything else isn't one.
            IconUrl = Text(element, "icon") is { } icon && Uri.TryCreate(icon, UriKind.Absolute, out var iconUri) && iconUri.Scheme == Uri.UriSchemeHttps ? icon : null,
            Type = Text(element, "type"),
        };
    }

    /// <summary>
    /// Sorts TorBox's error codes. An unlisted code is reported with TorBox's own words, and the
    /// HTTP status decides whether it is worth retrying.
    /// </summary>
    public static RemoteProviderException Failure(HttpStatusCode status, string? code, string? detail)
    {
        var kind = code?.ToUpperInvariant() switch
        {
            "NO_AUTH" or "BAD_TOKEN" or "AUTH_ERROR" or "INVALID_DEVICE" => RemoteFailureKind.Authentication,
            // The whole account can't do these for now, whatever is asked next.
            "MONTHLY_LIMIT" or "TOO_MUCH_DATA" or "VENDOR_DISABLED" or "PAYMENT_REQUIRED" => RemoteFailureKind.AccountLimit,
            "ACTIVE_LIMIT" or "COOLDOWN_LIMIT" or "DOWNLOAD_LIMIT_REACHED" => RemoteFailureKind.AccountBusy,
            "ITEM_NOT_FOUND" => RemoteFailureKind.NotFound,
            // About this one input. The next link may well be fine, as the reference client assumes too.
            "DOWNLOAD_TOO_LARGE" or "PLAN_RESTRICTED_FEATURE" or
            "BOZO_TORRENT" or "BOZO_NZB" or "LINK_OFFLINE" or "UNSUPPORTED_SITE" or "TEMPORARILY_DISABLED" or "INVALID_OPTION" or "MISSING_REQUIRED_OPTION" or "TOO_MANY_OPTIONS" => RemoteFailureKind.Rejected,
            "DOWNLOAD_NOT_CACHED" => RemoteFailureKind.NotCached,
            "DUPLICATE_ITEM" => RemoteFailureKind.AlreadyExists,
            // The address went away, not the item: "not found" here would read as an empty account.
            "ENDPOINT_NOT_FOUND" or "DEPRECATED" or
            "DATABASE_ERROR" or "UNKNOWN_ERROR" or "DOWNLOAD_SERVER_ERROR" or "NO_SERVERS_AVAILABLE_ERROR" or "DIFF_ISSUE" => RemoteFailureKind.ServiceError,
            "SLOW_DOWN" or "RATE_LIMITED" or "RATE_LIMIT_EXCEEDED" => RemoteFailureKind.RateLimited,
            _ => status switch
            {
                HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden => RemoteFailureKind.Authentication,
                HttpStatusCode.PaymentRequired => RemoteFailureKind.AccountLimit,
                // A 404 with a body that doesn't say what isn't there is a gateway's, or a proxy's, and says nothing about the account.
                HttpStatusCode.NotFound when detail is not null && NoItemsPhrase().IsMatch(detail) => RemoteFailureKind.NotFound,
                HttpStatusCode.NotFound or HttpStatusCode.Gone => RemoteFailureKind.ServiceError,
                >= HttpStatusCode.InternalServerError => RemoteFailureKind.ServiceError,
                _ => RemoteFailureKind.Rejected,
            },
        };
        if (detail?.Contains("password protected", StringComparison.OrdinalIgnoreCase) == true) kind = RemoteFailureKind.LinkPasswordRequired;
        if (detail is not null && kind != RemoteFailureKind.RateLimited && RatePhrase().IsMatch(detail)) kind = RemoteFailureKind.RateLimited;
        // The detail can quote the submitted magnet or hoster link, and this text reaches job failures.
        var message = !string.IsNullOrWhiteSpace(detail)
            ? UrlRedactor.HideAddresses(detail)
            : code ?? $"TorBox answered with status {(int)status}.";
        return new RemoteProviderException(kind, code, message, kind == RemoteFailureKind.RateLimited ? RateLimitWait(detail) : null);
    }

    /// <summary>
    /// A web download that TorBox can't take answers 500 DOWNLOAD_SERVER_ERROR, with its reason in words:
    /// a dead link, a malformed one, a site that couldn't be read. Nothing is created, every time it was
    /// tried, so this is a refusal of that link and not an outage. The same code elsewhere, or with no
    /// words, says nothing about the link and stays what it was.
    /// </summary>
    public static RemoteProviderException ForWebSubmission(RemoteProviderException failure)
        => failure is { Kind: RemoteFailureKind.ServiceError, ProviderCode: "DOWNLOAD_SERVER_ERROR" } && failure.Message != failure.ProviderCode
            ? new RemoteProviderException(RemoteFailureKind.Rejected, failure.ProviderCode, failure.Message, inner: failure)
            : failure;

    /// <summary>
    /// How long a rate limit answer with no usable Retry-After is worth. TorBox's words give the
    /// window, such as "60 per 1 hour", and the reference client reads them the same way.
    /// </summary>
    public static TimeSpan RateLimitWait(string? detail)
        => detail?.Contains("hour", StringComparison.OrdinalIgnoreCase) == true ? TimeSpan.FromHours(1) : TimeSpan.FromMinutes(1);

    /// <summary>The one way TorBox writes an item id: a whole number, with no padding. Null if it can't be one.</summary>
    public static string? CanonicalId(string? id)
    {
        var trimmed = id?.Trim();
        return !string.IsNullOrEmpty(trimmed) && trimmed.All(char.IsAsciiDigit) && long.TryParse(trimmed, NumberStyles.None, CultureInfo.InvariantCulture, out var value)
            ? value.ToString(CultureInfo.InvariantCulture)
            : null;
    }

    /// <summary>The info hash from a magnet, lower case hex. Base32 hashes are converted. Null if none.</summary>
    public static string? InfoHashFromMagnet(string? magnet)
    {
        if (string.IsNullOrWhiteSpace(magnet)) return null;
        var match = MagnetHash().Match(magnet);
        if (!match.Success) return null;
        var value = match.Groups[1].Value;
        if (value.Length == 40) return value.ToLowerInvariant();
        return Base32ToHex(value);
    }

    private static string? Base32ToHex(string value)
    {
        const string alphabet = "ABCDEFGHIJKLMNOPQRSTUVWXYZ234567";
        var bytes = new byte[20];
        var buffer = 0;
        var bits = 0;
        var index = 0;
        foreach (var c in value.ToUpperInvariant())
        {
            var digit = alphabet.IndexOf(c);
            if (digit < 0) return null;
            buffer = (buffer << 5) | digit;
            bits += 5;
            if (bits >= 8)
            {
                bits -= 8;
                bytes[index++] = (byte)(buffer >> bits);
                buffer &= (1 << bits) - 1;
            }
        }

        return index == 20 ? Convert.ToHexString(bytes).ToLowerInvariant() : null;
    }

    public static string? Text(JsonElement element, string name) =>
        element.ValueKind == JsonValueKind.Object && element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String
            ? value.GetString()
            : null;

    public static string? IdText(JsonElement element, string name)
    {
        if (element.ValueKind != JsonValueKind.Object || !element.TryGetProperty(name, out var value)) return null;
        return value.ValueKind switch
        {
            JsonValueKind.Number => value.GetRawText(),
            JsonValueKind.String => value.GetString(),
            _ => null,
        };
    }

    public static long? Long(JsonElement element, string name)
    {
        if (element.ValueKind != JsonValueKind.Object || !element.TryGetProperty(name, out var value)) return null;
        if (value.ValueKind == JsonValueKind.Number)
        {
            if (value.TryGetInt64(out var whole)) return whole;
            return value.TryGetDouble(out var real) ? (long)real : null;
        }

        return value.ValueKind == JsonValueKind.String && long.TryParse(value.GetString(), NumberStyles.Integer, CultureInfo.InvariantCulture, out var parsed)
            ? parsed
            : null;
    }

    public static double? Double(JsonElement element, string name) =>
        element.ValueKind == JsonValueKind.Object && element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.Number && value.TryGetDouble(out var result)
            ? result
            : null;

    public static bool Bool(JsonElement element, string name) =>
        element.ValueKind == JsonValueKind.Object && element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.True;

    public static DateTimeOffset? Date(JsonElement element, string name) =>
        Text(element, name) is { } text && DateTimeOffset.TryParse(text, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var date)
            ? date
            : null;

    [GeneratedRegex(@"^\d+ per \d* ?(second|minute|hour|day)", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex RatePhrase();

    // "No torrents found for this user."
    [GeneratedRegex(@"^\s*no\b.{0,80}\bfound\b", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex NoItemsPhrase();

    [GeneratedRegex(@"[?&]xt=urn:btih:([0-9a-fA-F]{40}|[A-Za-z2-7]{32})(?:&|$)", RegexOptions.CultureInvariant)]
    private static partial Regex MagnetHash();
}

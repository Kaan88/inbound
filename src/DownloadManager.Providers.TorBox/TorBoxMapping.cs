using System.Globalization;
using System.Net;
using System.Text.Json;
using System.Text.RegularExpressions;
using DownloadManager.Core.Providers;

namespace DownloadManager.Providers.TorBox;

/// <summary>Turns TorBox's responses into provider-neutral values. Fields are read tolerantly: any can be absent.</summary>
public static partial class TorBoxMapping
{
    /// <summary>
    /// The one table of TorBox states. Readiness never comes from here: an item is ready only when
    /// <c>download_present</c> is set together with <c>cached</c> or <c>download_finished</c>.
    /// </summary>
    public static RemoteStage StageFromState(string? state)
    {
        var word = state?.Trim().ToLowerInvariant();
        if (string.IsNullOrEmpty(word)) return RemoteStage.Unknown;
        if (word.StartsWith("stalled", StringComparison.Ordinal)) return RemoteStage.Stalled;
        if (word.StartsWith("failed", StringComparison.Ordinal) || word.StartsWith("error", StringComparison.Ordinal)) return RemoteStage.Failed;
        return word switch
        {
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
        var stage = present && (finished || Bool(element, "cached")) ? RemoteStage.Ready : StageFromState(state);
        if (!string.IsNullOrWhiteSpace(error)) stage = RemoteStage.Failed;

        var files = new List<RemoteFile>();
        if (element.ValueKind == JsonValueKind.Object && element.TryGetProperty("files", out var list) && list.ValueKind == JsonValueKind.Array)
        {
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
        }

        // No file list yet means the content can't be fetched, whatever the flags say.
        if (stage == RemoteStage.Ready && files.Count == 0) stage = RemoteStage.MetadataPending;

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
            Size = Long(element, "size") ?? 0,
            Stage = stage,
            RawState = state,
            Progress = Math.Clamp(Double(element, "progress") ?? 0, 0, 1),
            SpeedBytesPerSecond = Long(element, "download_speed") ?? 0,
            EtaSeconds = Long(element, "eta"),
            CreatedAt = Date(element, "created_at"),
            UpdatedAt = Date(element, "updated_at"),
            ExpiresAt = Date(element, "expires_at"),
            Cached = Bool(element, "cached"),
            Error = error,
            Files = files,
        };
    }

    public static RemoteHoster Hoster(JsonElement element)
    {
        var domains = new List<string>();
        if (element.ValueKind == JsonValueKind.Object && element.TryGetProperty("domains", out var list) && list.ValueKind == JsonValueKind.Array)
        {
            domains.AddRange(list.EnumerateArray().Where(domain => domain.ValueKind == JsonValueKind.String).Select(domain => domain.GetString()!));
        }

        return new RemoteHoster
        {
            Name = Text(element, "name") ?? "",
            Domains = domains,
            Available = Bool(element, "status"),
            Pattern = Text(element, "regex"),
            Note = Text(element, "note"),
            DailyLinkLimit = (int?)Long(element, "daily_link_limit"),
            DailyLinksUsed = (int?)Long(element, "daily_link_used"),
            DailyBandwidthLimit = Long(element, "daily_bandwidth_limit"),
            DailyBandwidthUsed = Long(element, "daily_bandwidth_used"),
            PerLinkSizeLimit = Long(element, "per_link_size_limit"),
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
            "PLAN_RESTRICTED_FEATURE" or "MONTHLY_LIMIT" or "DOWNLOAD_TOO_LARGE" or "TOO_MUCH_DATA" or "VENDOR_DISABLED" or "PAYMENT_REQUIRED" => RemoteFailureKind.AccountLimit,
            "ACTIVE_LIMIT" or "COOLDOWN_LIMIT" or "DOWNLOAD_LIMIT_REACHED" => RemoteFailureKind.AccountBusy,
            "ITEM_NOT_FOUND" or "ENDPOINT_NOT_FOUND" => RemoteFailureKind.NotFound,
            "BOZO_TORRENT" or "BOZO_NZB" or "LINK_OFFLINE" or "UNSUPPORTED_SITE" or "INVALID_OPTION" or "MISSING_REQUIRED_OPTION" or "TOO_MANY_OPTIONS" or "DOWNLOAD_NOT_CACHED" or "DUPLICATE_ITEM" => RemoteFailureKind.Rejected,
            "DATABASE_ERROR" or "UNKNOWN_ERROR" or "DOWNLOAD_SERVER_ERROR" or "NO_SERVERS_AVAILABLE_ERROR" or "DIFF_ISSUE" => RemoteFailureKind.ServiceError,
            "SLOW_DOWN" or "RATE_LIMITED" or "RATE_LIMIT_EXCEEDED" => RemoteFailureKind.RateLimited,
            _ => status switch
            {
                HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden => RemoteFailureKind.Authentication,
                HttpStatusCode.PaymentRequired => RemoteFailureKind.AccountLimit,
                HttpStatusCode.NotFound or HttpStatusCode.Gone => RemoteFailureKind.NotFound,
                >= HttpStatusCode.InternalServerError => RemoteFailureKind.ServiceError,
                _ => RemoteFailureKind.Rejected,
            },
        };
        if (detail?.Contains("password protected", StringComparison.OrdinalIgnoreCase) == true) kind = RemoteFailureKind.LinkPasswordRequired;
        if (detail is not null && kind != RemoteFailureKind.RateLimited && RatePhrase().IsMatch(detail)) kind = RemoteFailureKind.RateLimited;
        var message = !string.IsNullOrWhiteSpace(detail)
            ? detail
            : code ?? $"TorBox answered with status {(int)status}.";
        return new RemoteProviderException(kind, code, message);
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

    [GeneratedRegex(@"[?&]xt=urn:btih:([0-9a-fA-F]{40}|[A-Za-z2-7]{32})(?:&|$)", RegexOptions.CultureInvariant)]
    private static partial Regex MagnetHash();
}

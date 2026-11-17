using System.Text.RegularExpressions;
using DownloadManager.Contracts;

namespace DownloadManager.Mobile;

/// <summary>What a failed download says about why: a few words for the row, the whole message for Details.</summary>
public static partial class FailureWords
{
    /// <summary>"server refused (403)", "address not found", "no space left"; null when the job gives nothing to say.</summary>
    public static string? Short(JobDto j)
    {
        var f = j.Failure;
        if (f is not null && !string.Equals(f.Kind, "none", StringComparison.OrdinalIgnoreCase))
        {
            var byKind = ByKind(f.Kind, f.HttpStatus);
            if (byKind is not null) return byKind;
        }

        return Clause(Full(j));
    }

    /// <summary>The job's whole message (the failure's, else the status note); null when there is none or it is only a resource key.</summary>
    public static string? Full(JobDto j)
    {
        foreach (var s in new[] { j.Failure?.Message, j.StatusNote })
            if (!string.IsNullOrWhiteSpace(s) && !ResourceKey().IsMatch(s.Trim())) return s.Trim();
        return null;
    }

    private static string? ByKind(string kind, int? status) => kind.ToLowerInvariant() switch
    {
        "network" => "connection lost",
        "timeout" => "timed out",
        "dns" => "address not found",
        "tls" => "secure connection failed",
        "unauthorized" => "sign-in needed" + Code(status),
        "forbidden" => "server refused" + Code(status),
        "expiredurl" => "link expired",
        "notfound" => "not found on the server" + Code(status),
        "rangeunsupported" => "server can't resume",
        "remotechanged" => "file changed on the server",
        "invalidcontent" => "not the file expected",
        "diskfull" => "no space left",
        "permission" => "no permission to save",
        "destinationunavailable" => "folder not available",
        "verificationfailed" => "checksum did not match",
        "unsupported" => "address type not supported",
        "http" => status switch
        {
            401 => "sign-in needed (401)",
            403 => "server refused (403)",
            404 or 410 => "not found on the server (" + status + ")",
            429 => "too many requests (429)",
            >= 500 and <= 599 => "server error (" + status + ")",
            { } s => "server answered " + s,
            null => "server error",
        },
        _ => null,
    };

    private static string Code(int? status) => status is { } s ? " (" + s + ")" : "";

    /// <summary>The first clause of a sentence, at most 40 characters, without a trailing full stop.</summary>
    public static string? Clause(string? text)
    {
        if (string.IsNullOrWhiteSpace(text)) return null;
        var first = text.Trim().Split(['\n', '\r'])[0];
        var cut = first.IndexOfAny([':', ';', '.']);
        if (cut > 8) first = first[..cut];
        first = first.Trim().TrimEnd('.', ',');
        if (first.Length > 40) first = first[..39].TrimEnd() + "…";
        return first.Length == 0 ? null : first;
    }

    [GeneratedRegex("^[a-z0-9]+(_[a-z0-9]+)+$")]
    private static partial Regex ResourceKey();
}

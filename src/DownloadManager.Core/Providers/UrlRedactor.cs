using System.Text;
using System.Text.RegularExpressions;

namespace DownloadManager.Core.Providers;

/// <summary>Removes the secret parts of addresses before they reach a log or a message.</summary>
public static partial class UrlRedactor
{
    public const string Mask = "***";

    /// <summary>
    /// Text past this isn't looked at, or kept. The patterns below take quadratic time on text built
    /// to make them, and much of what comes here is whatever a provider chose to send.
    /// </summary>
    public const int LongestText = 64 * 1024;

    private const string HiddenAddress = "[link]";
    private const string HiddenText = "[text hidden]";
    private const int PatternTimeoutMilliseconds = 1000;

    private static readonly HashSet<string> SecretNames = new(StringComparer.OrdinalIgnoreCase)
    {
        "token", "key", "apikey", "api_key", "auth", "signature", "sig",
        "password", "passwd", "pass", "pwd", "secret", "session", "sessionid",
    };

    /// <summary>Catches the many spellings of the same thing: access_token, X-Amz-Signature, X-Goog-Credential.</summary>
    private static readonly string[] SecretSuffixes = ["token", "signature", "credential", "secret", "password", "_key", "-key"];

    /// <summary>Masks secret query values, the fragment and any user name and password of one address.</summary>
    public static string Redact(string? url)
    {
        if (string.IsNullOrEmpty(url)) return string.Empty;
        if (url.StartsWith("magnet:", StringComparison.OrdinalIgnoreCase)) return "magnet:" + Mask;

        var fragment = url.IndexOf('#');
        var redacted = fragment < 0 ? url : url[..fragment];
        var query = redacted.IndexOf('?');
        var address = query < 0 ? redacted : redacted[..query];
        var scheme = address.IndexOf("://", StringComparison.Ordinal);
        if (scheme >= 0)
        {
            var authorityStart = scheme + 3;
            var path = address.IndexOf('/', authorityStart);
            var at = address.LastIndexOf('@', path < 0 ? address.Length - 1 : path);
            if (at >= authorityStart) address = address[..authorityStart] + Mask + address[at..];
        }

        var result = query < 0 ? address : address + "?" + RedactQuery(redacted[(query + 1)..]);
        return fragment < 0 ? result : result + "#" + Mask;
    }

    /// <summary>Redacts every address inside free text, such as an exception's message or stack trace.</summary>
    public static string RedactText(string? text)
    {
        if (string.IsNullOrEmpty(text)) return string.Empty;
        try
        {
            var redacted = MagnetPattern().Replace(Bounded(text), "magnet:" + Mask);
            redacted = AddressPattern().Replace(redacted, match => RedactJoined(match.Value));
            return MaskBarePairs(redacted);
        }
        catch (RegexMatchTimeoutException)
        {
            return HiddenText;
        }
    }

    /// <summary>
    /// For text from a provider, which can quote what was submitted. The whole address goes,
    /// because a hoster link or a magnet is the user's private input even with no key in it.
    /// </summary>
    public static string HideAddresses(string? text)
    {
        if (string.IsNullOrEmpty(text)) return string.Empty;
        try
        {
            var hidden = MagnetPattern().Replace(Bounded(text), HiddenAddress);
            hidden = AddressPattern().Replace(hidden, HiddenAddress);
            return MaskBarePairs(hidden);
        }
        catch (RegexMatchTimeoutException)
        {
            return HiddenText;
        }
    }

    private static string Bounded(string text) => text.Length <= LongestText ? text : text[..LongestText];

    /// <summary>The address pattern takes addresses joined by a comma as one match, and each needs its own redaction.</summary>
    private static string RedactJoined(string match)
    {
        var result = new StringBuilder(match.Length);
        var start = 0;
        foreach (Match next in SchemePattern().Matches(match))
        {
            if (next.Index == 0) continue;
            result.Append(Redact(match[start..next.Index]));
            start = next.Index;
        }

        return result.Append(Redact(match[start..])).ToString();
    }

    private static string RedactQuery(string query)
    {
        var result = new StringBuilder(query.Length);
        var start = 0;
        for (var i = 0; i <= query.Length; i++)
        {
            if (i < query.Length && query[i] is not ('&' or ';')) continue;
            result.Append(RedactPair(query[start..i]));
            if (i < query.Length) result.Append(query[i]);
            start = i + 1;
        }

        return result.ToString();
    }

    private static string RedactPair(string pair)
    {
        var equals = pair.IndexOf('=');
        if (equals <= 0) return pair;
        return IsSecretName(Uri.UnescapeDataString(pair[..equals])) || HoldsAnAddress(pair[(equals + 1)..])
            ? pair[..(equals + 1)] + Mask
            : pair;
    }

    private static bool IsSecretName(string name)
        => SecretNames.Contains(name) ||
           Array.Exists(SecretSuffixes, suffix => name.EndsWith(suffix, StringComparison.OrdinalIgnoreCase));

    /// <summary>A redirect target in a query value can carry a secret of its own, encoded once or twice.</summary>
    private static bool HoldsAnAddress(string value)
    {
        var text = Uri.UnescapeDataString(Uri.UnescapeDataString(value));
        if (text.Contains("://", StringComparison.Ordinal) || text.StartsWith("magnet:", StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        try
        {
            return BarePairPattern().IsMatch(Bounded(text));
        }
        catch (RegexMatchTimeoutException)
        {
            return true;
        }
    }

    private static string MaskBarePairs(string text)
        => BarePairPattern().Replace(text, match => match.Groups["name"].Value + "=" + Mask);

    // An apostrophe is legal in a URL, and stopping at one would leave the rest of a value behind.
    [GeneratedRegex(@"\b[a-z][a-z0-9+.\-]*://[^\s""<>]+", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant, PatternTimeoutMilliseconds)]
    private static partial Regex AddressPattern();

    [GeneratedRegex(@"\b[a-z][a-z0-9+.\-]*://", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant, PatternTimeoutMilliseconds)]
    private static partial Regex SchemePattern();

    // Any magnet: the classifier takes one with no question mark, and that is still someone's private input.
    [GeneratedRegex(@"\bmagnet:[^\s""<>]*", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant, PatternTimeoutMilliseconds)]
    private static partial Regex MagnetPattern();

    // For a request line or a message that quotes a query without its address.
    [GeneratedRegex(
        @"(?<![\w.\-])(?<name>[\w.\-]*(?:token|signature|credential|secret|password|_key|-key)|key|apikey|auth|sig|passwd|pass|pwd|session|sessionid)=(?<value>[^\s&;""'<>,)]+)",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant, PatternTimeoutMilliseconds)]
    private static partial Regex BarePairPattern();
}

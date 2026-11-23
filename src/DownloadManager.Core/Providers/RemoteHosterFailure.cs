namespace DownloadManager.Core.Providers;

/// <summary>
/// The likely reason a provider refused a web (hoster) link, in plain words. Said only from what the
/// provider itself gave: its code and words for this failure, and its own list of sites, which is
/// the only thing that may call a site down or unlisted. Null when neither can say anything, so the
/// caller keeps the words it already had.
/// </summary>
public static class RemoteHosterFailure
{
    /// <summary>
    /// The one part of a link that may be said back: its host name. Nothing of the path, the query or
    /// the fragment, which can hold someone's key. Null when the text names no host.
    /// </summary>
    public static string? HostOf(string? link)
    {
        if (string.IsNullOrWhiteSpace(link)) return null;
        var text = link.Trim();
        if (text.StartsWith("magnet:", StringComparison.OrdinalIgnoreCase)) return null;
        return Uri.TryCreate(text, UriKind.Absolute, out var uri) && !string.IsNullOrEmpty(uri.IdnHost)
            ? uri.IdnHost.ToLowerInvariant()
            : null;
    }

    /// <summary>
    /// The plain sentence, or null for none. In order: a site the provider's list says is down, a host
    /// no site in its list takes, and then a code of its own that names the reason. A code and words
    /// that say nothing more than "it failed" are left to the caller.
    /// </summary>
    /// <param name="providerName">The provider as the user knows it, such as TorBox.</param>
    /// <param name="host">The link's host name only, from <see cref="HostOf"/>.</param>
    /// <param name="hosterName">The site the link was added as, when that was recorded.</param>
    /// <param name="hoster">The list's entry for this link's site, when the list has one.</param>
    /// <param name="hostIsListed">Whether the list has an entry for the link's site. Null when the list can't say.</param>
    /// <param name="providerCode">The provider's error code, such as <c>UNSUPPORTED_SITE</c>.</param>
    /// <param name="detail">The provider's own words for this failure, already scrubbed of secrets.</param>
    public static string? Text(
        string providerName,
        string? host,
        string? hosterName,
        RemoteHoster? hoster,
        bool? hostIsListed,
        string? providerCode,
        string? detail)
    {
        var name = Clean(providerName, 100) ?? "The provider";
        var site = Clean(hoster?.Name, 100) ?? Clean(hosterName, 100) ?? Clean(host, 200);

        // Only an exact false in the list says down, exactly as it was read.
        if (hoster is { Available: false })
        {
            var note = Clean(hoster.Note, 200);
            return note is null
                ? $"{name} lists {site ?? "this site"} as down right now. Try again later."
                : $"{name} lists {site ?? "this site"} as down right now: {Sentence(note)} Try again later.";
        }

        if (hostIsListed == false && Clean(host, 200) is { } unlisted)
        {
            return $"{name} doesn't list {unlisted} among the hosters it supports.";
        }

        var words = detail is not null && !detail.Equals(providerCode, StringComparison.OrdinalIgnoreCase)
            ? Clean(detail, 500)
            : null;
        return providerCode?.Trim().ToUpperInvariant() switch
        {
            "UNSUPPORTED_SITE" => site is null
                ? $"{name} doesn't support this site{(words is null ? "." : $": {words}")}"
                : $"{name} doesn't support {site}{(words is null ? "." : $": {words}")}",
            "TEMPORARILY_DISABLED" => site is null
                ? $"{name} has this site turned off right now{(words is null ? "." : $": {Sentence(words)}")} Try again later."
                : $"{name} has {site} turned off right now{(words is null ? "." : $": {Sentence(words)}")} Try again later.",
            _ => null,
        };
    }

    /// <summary>The provider's own words, without any address they quote and short enough to read.</summary>
    private static string? Clean(string? text, int longest)
    {
        if (string.IsNullOrWhiteSpace(text)) return null;
        var clean = UrlRedactor.HideAddresses(text).Trim();
        if (clean.Length > longest) clean = clean[..longest].TrimEnd();
        return clean.Length == 0 ? null : clean;
    }

    private static string Sentence(string note)
        => note.Length > 0 && note[^1] is '.' or '!' or '?' ? note : note + ".";
}

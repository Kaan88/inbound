using DownloadManager.Core;

namespace DownloadManager.Desktop.WinUI;

/// <summary>What the text in Add URL's address box is (UI.R38).</summary>
internal enum AddUrlKind
{
    /// <summary>Nothing typed yet.</summary>
    Empty,
    /// <summary>One http or https address: this dialog adds it.</summary>
    OneLink,
    /// <summary>Text holding several links, or a numbered address: Add Links lists them one by one.</summary>
    SeveralLinks,
    /// <summary>A magnet or the full path of a .torrent file: the torrent dialog, or the provider's.</summary>
    Torrent,
    /// <summary>An ftp or ftps address, which isn't supported since 0.7.2.</summary>
    Ftp,
    /// <summary>Anything else.</summary>
    NotALink,
}

/// <param name="Address">The one address, trimmed, for <see cref="AddUrlKind.OneLink"/> and <see cref="AddUrlKind.Torrent"/>.</param>
/// <param name="Links">What Add Links gets, for <see cref="AddUrlKind.SeveralLinks"/>.</param>
internal sealed record AddUrlInput(AddUrlKind Kind, string Address, IReadOnlyList<string> Links);

/// <summary>
/// UI.R38: Add URL is one dialog. The address is at the top, and once it stops changing the dialog works out what it is
/// and fills in the rest by itself. Nothing here looks at the network or at a file.
/// </summary>
internal static class AddUrlRules
{
    /// <summary>How long the address has to stay the same before it is checked, so typing doesn't ask for every letter.</summary>
    public static readonly TimeSpan CheckDelay = TimeSpan.FromMilliseconds(400);

    public const string StartText = "Start download";
    public const string LaterText = "Download later";
    public const string ContinueText = "Continue";

    /// <param name="withProvider">An account could take the input, so a magnet or a hoster link in pasted text counts too.</param>
    public static AddUrlInput Read(string? text, bool withProvider)
    {
        var trimmed = text?.Trim() ?? string.Empty;
        if (trimmed.Length == 0) return new AddUrlInput(AddUrlKind.Empty, string.Empty, []);
        // Several magnets pasted on one line are several links, not one torrent.
        if (TorrentRules.IsTorrentInput(trimmed) && !trimmed.Any(char.IsControl) &&
            !(LinkExtractor.IsMagnet(trimmed) && trimmed.Any(char.IsWhiteSpace)))
            return new AddUrlInput(AddUrlKind.Torrent, trimmed, [trimmed]);
        if (!trimmed.Any(char.IsWhiteSpace))
        {
            if (LinkExtractor.IsDownloadableLink(trimmed))
            {
                var numbered = LinkExtractor.ExpandNumbered(trimmed);
                return numbered.Count > 1
                    ? new AddUrlInput(AddUrlKind.SeveralLinks, trimmed, numbered)
                    : new AddUrlInput(AddUrlKind.OneLink, trimmed, [trimmed]);
            }

            if (FtpAddresses.IsFtp(trimmed)) return new AddUrlInput(AddUrlKind.Ftp, trimmed, []);
        }

        // Pasted text: what it holds. One link in a sentence is that link.
        var found = withProvider ? LinkExtractor.Inputs(trimmed) : LinkExtractor.HttpLinks(trimmed);
        if (found.Count > 1) return new AddUrlInput(AddUrlKind.SeveralLinks, trimmed, found);
        if (found.Count == 1 && found[0] != trimmed) return Read(found[0], withProvider);
        return LinkExtractor.HasFtpLink(trimmed)
            ? new AddUrlInput(AddUrlKind.Ftp, trimmed, [])
            : new AddUrlInput(AddUrlKind.NotALink, trimmed, []);
    }

    /// <summary>
    /// Pasted text for the one-line address box. The box would keep only the first line of a list of links (a browser's
    /// box glues the lines together), so each line break becomes a space, and <see cref="Read"/> still finds every link.
    /// </summary>
    public static string OneLine(string? pasted)
        => System.Text.RegularExpressions.Regex.Replace(pasted ?? string.Empty, @"[ \t]*[\r\n]+[ \t]*", " ");

    /// <summary>The line under the address for an input another dialog takes, which Continue opens.</summary>
    public static string HandoffWords(AddUrlInput input, bool torrentDialog) => input.Kind switch
    {
        AddUrlKind.SeveralLinks => $"{input.Links.Count} links. Continue opens Add links, where you choose which to download.",
        AddUrlKind.Torrent => torrentDialog
            ? "A torrent. Continue opens the torrent dialog."
            : "A torrent. Continue opens the add dialog for it.",
        _ => string.Empty,
    };

    /// <summary>The line under the address for a link the provider handles, which Continue opens the provider's add dialog for.</summary>
    public static string ProviderWords(string? kind, string? hosterName, string provider) => kind switch
    {
        "hosterLink" => $"{(string.IsNullOrWhiteSpace(hosterName) ? "This site" : hosterName.Trim())} goes through {provider}. Continue opens the add dialog for it.",
        "folderLink" => $"A link to an item on your {provider} account. Continue opens the add dialog for it.",
        _ => $"This goes through {provider}. Continue opens the add dialog for it.",
    };

    /// <summary>What to type, for text that is no address this dialog or another can take.</summary>
    public static string NotALinkWords(bool torrents) => torrents
        ? "Enter an http or https address, a magnet, or the full path of a .torrent file."
        : "Enter an absolute http or https URL.";

    public const string NoTorrentsWords = "Torrents aren't available here.";

    public const string EmptyInfoWords = "Enter an address above, and its file is checked by itself.";

    /// <summary>
    /// Review of 0.7.2, security-1: the site (scheme, host and port) an address is on, lower case; null when it isn't an
    /// absolute address. A user name and password typed for one site are never sent to another.
    /// </summary>
    public static string? SiteOf(string? address)
    {
        if (!Uri.TryCreate(address?.Trim(), UriKind.Absolute, out var uri) || string.IsNullOrEmpty(uri.Host)) return null;
        return $"{uri.Scheme}://{uri.IdnHost}:{uri.Port}".ToLowerInvariant();
    }

    /// <summary>Whether an address is on another site than the one the user name and password were typed for.</summary>
    public static bool LeavesSite(string? credentialsSite, string address)
        => credentialsSite is not null && !string.Equals(credentialsSite, SiteOf(address), StringComparison.Ordinal);

    public const string CredentialsClearedWords =
        "This address is on another site, so the user name and password were cleared. Enter them again to use them here.";

    public const string CheckWithCredentialsWords =
        "The file is checked with your user name and password only when you choose Check again.";
}

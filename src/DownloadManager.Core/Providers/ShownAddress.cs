namespace DownloadManager.Core.Providers;

/// <summary>
/// The one rule for showing a download's address, anywhere it can be read or copied: windows, lists,
/// tooltips, the CLI and exports. A direct provider link carries the account key, and its path names
/// a file on the account, so it shows as its site alone, masked or not. The Host can say an address
/// is hidden when the key was found elsewhere in it. Every other address shows as it is stored,
/// because it is the user's own link and copying it has to give something that works.
/// </summary>
public static class ShownAddress
{
    /// <summary>What a hidden address shows when it has no readable site.</summary>
    public const string NoSite = "A direct link";

    /// <summary>True when the address shows as its site alone.</summary>
    public static bool IsHidden(string? url, bool hiddenByHost = false)
        => hiddenByHost || ProviderLinks.IsOnProviderHost(url);

    /// <summary>The address as it may be shown or copied.</summary>
    public static string For(string? url, bool hiddenByHost = false)
    {
        if (string.IsNullOrWhiteSpace(url)) return string.Empty;
        if (!IsHidden(url, hiddenByHost)) return url;
        return Uri.TryCreate(url.Trim(), UriKind.Absolute, out var uri) && uri.IdnHost.Length > 0 ? uri.IdnHost : NoSite;
    }

    /// <summary>The label for the box or line that shows it.</summary>
    public static string Label(string? url, bool hiddenByHost = false)
        => IsHidden(url, hiddenByHost) ? "Site" : "Address";
}

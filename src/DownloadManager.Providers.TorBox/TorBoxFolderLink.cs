using System.Globalization;
using DownloadManager.Core.Providers;

namespace DownloadManager.Providers.TorBox;

/// <summary>
/// TorBox's "Copy JDownloader Folder Link". It is a reference to an item on one account, not a
/// share: <c>id</c> and <c>type</c> say which item, and only that account's own list can say whether
/// it exists. <c>name</c> is display text anyone can edit, so it is not even read, and nothing else
/// in the query ever leaves this method.
/// </summary>
public static class TorBoxFolderLink
{
    private const string Domain = "torbox.app";

    /// <returns>Null when the text isn't on TorBox's <c>/download</c> page at all.</returns>
    public static RemoteFolderLinkParse? Parse(string? text)
    {
        var trimmed = text?.Trim();
        if (string.IsNullOrEmpty(trimmed) ||
            !Uri.TryCreate(trimmed, UriKind.Absolute, out var uri) ||
            uri.Scheme is not ("http" or "https") ||
            !IsTorBoxHost(uri.Host) ||
            !uri.IsDefaultPort ||
            uri.UserInfo.Length > 0 ||
            !uri.AbsolutePath.TrimEnd('/').Equals("/download", StringComparison.OrdinalIgnoreCase))
        {
            return null;
        }

        var ids = new HashSet<string>(StringComparer.Ordinal);
        var types = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var pair in uri.Query.TrimStart('?').Split('&', StringSplitOptions.RemoveEmptyEntries))
        {
            var equals = pair.IndexOf('=');
            var name = Decode(equals < 0 ? pair : pair[..equals]);
            var value = equals < 0 ? string.Empty : Decode(pair[(equals + 1)..]).Trim();
            if (name.Equals("id", StringComparison.OrdinalIgnoreCase))
            {
                ids.Add(CanonicalId(value) ?? value);
            }
            else if (name.Equals("type", StringComparison.OrdinalIgnoreCase))
            {
                types.Add(value);
            }
        }

        if (ids.Count != 1 || CanonicalId(ids.First()) is not { } id)
        {
            return Refuse("This TorBox folder link has no usable item id.");
        }

        if (types.Count != 1 || KindOf(types.First()) is not { } kind)
        {
            return Refuse("This TorBox folder link has no usable type. Copy the link from TorBox again.");
        }

        return new RemoteFolderLinkParse(new RemoteFolderReference(kind, id), null);
    }

    private static RemoteFolderLinkParse Refuse(string problem) => new(null, problem);

    private static bool IsTorBoxHost(string host)
        => host.Equals(Domain, StringComparison.OrdinalIgnoreCase) ||
           host.Equals("www." + Domain, StringComparison.OrdinalIgnoreCase);

    private static string Decode(string value)
    {
        try
        {
            return Uri.UnescapeDataString(value);
        }
        catch (ArgumentException)
        {
            return string.Empty;
        }
    }

    /// <summary>Digits only, ASCII only, above zero, and small enough to be an id. Leading zeros don't make it another item.</summary>
    private static string? CanonicalId(string value)
        => value.Length > 0 && value.All(char.IsAsciiDigit) &&
           long.TryParse(value, NumberStyles.None, CultureInfo.InvariantCulture, out var id) && id > 0
            ? id.ToString(CultureInfo.InvariantCulture)
            : null;

    private static RemoteItemKind? KindOf(string type) => type.ToLowerInvariant() switch
    {
        "torrents" => RemoteItemKind.Torrent,
        "web_downloads" => RemoteItemKind.WebDownload,
        "usenet_downloads" => RemoteItemKind.Usenet,
        _ => null,
    };
}

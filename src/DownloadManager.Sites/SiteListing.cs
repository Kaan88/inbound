using System.Globalization;
using System.Text.Json.Nodes;
using DownloadManager.Contracts;

namespace DownloadManager.Sites;

/// <summary>
/// An rclone <c>operations/list</c> answer as <see cref="SiteItemDto"/>s (SITES.3): folders first, then by name, ignoring
/// case and then not, so the order is the same on every call. Entries without a name are skipped.
/// </summary>
public static class SiteListing
{
    public static IReadOnlyList<SiteItemDto> Read(JsonObject listing)
    {
        if (listing["list"] is not JsonArray entries) return [];
        var items = new List<SiteItemDto>(entries.Count);
        foreach (var node in entries)
        {
            if (node is not JsonObject entry || Text(entry, "Name") is not { Length: > 0 } name) continue;
            var isFolder = entry["IsDir"] is JsonValue dir && dir.TryGetValue<bool>(out var d) && d;
            long size = -1;
            if (!isFolder && entry["Size"] is JsonValue sizeValue && sizeValue.TryGetValue<long>(out var s) && s >= 0) size = s;
            DateTimeOffset? modified = null;
            // rclone writes RFC 3339 times; an SFTP server without times gives the zero time, which is shown as none.
            if (Text(entry, "ModTime") is { } time
                && DateTimeOffset.TryParse(time, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out var t)
                && t.Year > 1970)
                modified = t;
            items.Add(new SiteItemDto { Name = name, IsFolder = isFolder, Size = size, Modified = modified });
        }

        items.Sort(static (a, b) =>
        {
            if (a.IsFolder != b.IsFolder) return a.IsFolder ? -1 : 1;
            var byName = StringComparer.OrdinalIgnoreCase.Compare(a.Name, b.Name);
            return byName != 0 ? byName : StringComparer.Ordinal.Compare(a.Name, b.Name);
        });
        return items;
    }

    private static string? Text(JsonObject entry, string name) =>
        entry[name] is JsonValue value && value.TryGetValue<string>(out var text) ? text : null;
}

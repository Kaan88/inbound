using System.Text.Json;

namespace DownloadManager.Mobile.Links;

/// <summary>DOM evaluation is page-controlled data, even when the evaluated script is fixed.</summary>
public static class PageLinkResults
{
    public static PageLinks Parse(string? result)
    {
        var links = new List<PageLink>();
        if (result is null || result.Length > 1024 * 1024) return new PageLinks(links);
        try
        {
            using var wrapper = JsonDocument.Parse(result);
            if (wrapper.RootElement.ValueKind != JsonValueKind.String || wrapper.RootElement.GetString() is not { Length: <= 300000 } json) return new PageLinks(links);
            using var document = JsonDocument.Parse(json);
            if (document.RootElement.ValueKind != JsonValueKind.Array) return new PageLinks(links);
            var inspected = 0;
            foreach (var item in document.RootElement.EnumerateArray())
            {
                if (++inspected > 1000) break;
                if (item.ValueKind != JsonValueKind.Object || !item.TryGetProperty("url", out var url)
                    || url.ValueKind != JsonValueKind.String || LinkRules.Normalize(url.GetString()) is not { } valid) continue;
                var name = item.TryGetProperty("name", out var label) && label.ValueKind == JsonValueKind.String ? label.GetString() : "";
                links.Add(new PageLink(valid, LinkRules.Name(name)));
            }
        }
        catch (JsonException) { }
        return new PageLinks(links);
    }
}

namespace DownloadManager.Contracts;

/// <summary>Which app shows the notes: the Windows window, or the web UI of a server (or of the Windows app's bridge).</summary>
public enum ChangelogSurface
{
    Windows,
    WebUi,
}

/// <summary>
/// What is turned on where the notes show: lines about torrents show only while they are on, and lines tagged with a
/// provider's id only while <paramref name="Providers"/> (provider ids with an account set, lower case) has it.
/// </summary>
public sealed record ChangelogFeatures(bool Torrents, IReadOnlyCollection<string> Providers)
{
    public bool HasProvider(string id) => Providers.Contains(id, StringComparer.OrdinalIgnoreCase);
}

/// <summary>A line of another place's changes, folded away: "Web UI", "Extension"…, then its text.</summary>
public sealed record ChangelogElsewhere(string Place, string Text);

public sealed record ChangelogShownSection(string Title, IReadOnlyList<string> Lines);

public sealed record ChangelogShownVersion(string Version, string? Date, IReadOnlyList<ChangelogShownSection> Sections);

/// <summary>What one app shows of some versions' notes: its own lines by version and section, the others folded.</summary>
public sealed record ChangelogView(IReadOnlyList<ChangelogShownVersion> Versions, IReadOnlyList<ChangelogElsewhere> Elsewhere)
{
    /// <summary>No line is about this app: nothing opens.</summary>
    public bool IsEmpty => Versions.Count == 0;

    /// <summary>"3 changes in the web UI and the browser extension"; empty when there are none.</summary>
    public string ElsewhereText => ChangelogFilter.ElsewhereText(Elsewhere);
}

/// <summary>
/// UPD.N1: which changelog lines an app shows. The Windows app shows lines tagged <c>windows</c> or <c>all</c>; the web UI
/// <c>webui</c>, <c>server</c>, <c>docker</c> or <c>all</c>. Lines about torrents show only while torrents are on, about
/// a provider only while an account of it is set, and Linux lines never. What is left (the extension, the other app) is folded
/// under one line. The web UI has the same rules in <c>webui/src/lib/whatsNew.ts</c>.
/// </summary>
public static class ChangelogFilter
{
    public static ChangelogView For(IEnumerable<ChangelogVersion> versions, ChangelogSurface surface, ChangelogFeatures features)
    {
        var shown = new List<ChangelogShownVersion>();
        var elsewhere = new List<ChangelogElsewhere>();
        foreach (var version in versions)
        {
            var sections = new List<ChangelogShownSection>();
            foreach (var section in version.Sections)
            {
                var lines = new List<string>();
                foreach (var item in section.Items)
                {
                    switch (Place(item.Tags, surface, features))
                    {
                        case Here:
                            lines.Add(item.Text);
                            break;
                        case { } place when place.Length > 0:
                            elsewhere.Add(new ChangelogElsewhere(place, item.Text));
                            break;
                    }
                }

                if (lines.Count > 0) sections.Add(new ChangelogShownSection(section.Title, lines));
            }

            if (sections.Count > 0) shown.Add(new ChangelogShownVersion(version.Version, version.Date, sections));
        }

        return new ChangelogView(shown, elsewhere);
    }

    /// <summary>The lines of one list (the update manifest's notes): this app's, then the others' folded.</summary>
    public static (IReadOnlyList<string> Here, IReadOnlyList<ChangelogElsewhere> Elsewhere) Lines(
        IEnumerable<ChangelogItem> items, ChangelogSurface surface, ChangelogFeatures features)
    {
        var here = new List<string>();
        var elsewhere = new List<ChangelogElsewhere>();
        foreach (var item in items)
        {
            var place = Place(item.Tags, surface, features);
            if (place == Here) here.Add(item.Text);
            else if (place is { Length: > 0 }) elsewhere.Add(new ChangelogElsewhere(place, item.Text));
        }

        return (here, elsewhere);
    }

    private const string Here = "\0here";

    /// <summary><see cref="Here"/>, the label of the other place it is folded under, or null when it is hidden.</summary>
    private static string? Place(IReadOnlyCollection<string> tags, ChangelogSurface surface, ChangelogFeatures features)
    {
        if (Has(tags, ChangelogTags.Torrents) && !features.Torrents) return null;
        if (tags.Any(tag => ChangelogTags.IsProvider(tag) && !features.HasProvider(tag))) return null;
        var places = tags.Where(tag => ChangelogTags.Places.Contains(tag, StringComparer.Ordinal)).ToList();
        if (places.Count == 0 || places.Contains(ChangelogTags.All)) return Here;
        var mine = surface == ChangelogSurface.Windows
            ? new[] { ChangelogTags.Windows }
            : new[] { ChangelogTags.WebUi, ChangelogTags.Server, ChangelogTags.Docker };
        if (places.Any(place => mine.Contains(place))) return Here;
        // Linux lines are for the Linux app, which shows none of this (yet).
        return places.Where(place => place != ChangelogTags.Linux).Select(Label).FirstOrDefault();
    }

    private static bool Has(IReadOnlyCollection<string> tags, string tag) => tags.Contains(tag, StringComparer.Ordinal);

    private static string Label(string place) => place switch
    {
        ChangelogTags.Windows => "Windows",
        ChangelogTags.WebUi => "Web UI",
        ChangelogTags.Extension => "Extension",
        _ => "Server",
    };

    private static string PlaceName(string label) => label switch
    {
        "Windows" => "the Windows app",
        "Web UI" => "the web UI",
        "Extension" => "the browser extension",
        _ => "the server",
    };

    /// <summary>"3 changes in the web UI and the browser extension"; empty for none.</summary>
    public static string ElsewhereText(IReadOnlyCollection<ChangelogElsewhere> elsewhere)
    {
        if (elsewhere.Count == 0) return string.Empty;
        return $"{elsewhere.Count} change{(elsewhere.Count == 1 ? string.Empty : "s")} in {Places(elsewhere)}";
    }

    /// <summary>"the web UI and the browser extension", in the order the lines name them.</summary>
    public static string Places(IEnumerable<ChangelogElsewhere> elsewhere)
    {
        var names = elsewhere.Select(line => PlaceName(line.Place)).Distinct().ToList();
        return names.Count switch
        {
            0 => string.Empty,
            1 => names[0],
            _ => string.Join(", ", names.Take(names.Count - 1)) + " and " + names[^1],
        };
    }
}

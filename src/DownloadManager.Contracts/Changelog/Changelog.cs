using System.Text;
using System.Text.Json;

namespace DownloadManager.Contracts;

/// <summary>
/// UPD.N1: one line of the changelog: plain text (never markup) and the tags that say where it applies
/// (<see cref="ChangelogTags"/>).
/// </summary>
public sealed class ChangelogItem
{
    public string Text { get; set; } = string.Empty;
    public List<string> Tags { get; set; } = new();
}

/// <summary>A heading of one version's notes: "New", "Fixes", "Upgrade notes".</summary>
public sealed class ChangelogSection
{
    public string Title { get; set; } = string.Empty;
    public List<ChangelogItem> Items { get; set; } = new();
}

public sealed class ChangelogVersion
{
    public string Version { get; set; } = string.Empty;

    /// <summary><c>yyyy-MM-dd</c>; null for a version not released yet.</summary>
    public string? Date { get; set; }

    public List<ChangelogSection> Sections { get; set; } = new();
}

/// <summary>
/// The payload of <see cref="MessageTypes.ChangelogResult"/>, and the shape of <c>changelog.json</c>: every version,
/// newest first.
/// </summary>
public sealed class ChangelogDto
{
    public List<ChangelogVersion> Versions { get; set; } = new();
}

/// <summary>Where a changelog line applies. A line with none of the place tags applies everywhere.</summary>
public static class ChangelogTags
{
    public const string All = "all";
    public const string Windows = "windows";
    public const string WebUi = "webui";
    public const string Server = "server";
    public const string Docker = "docker";
    public const string Linux = "linux";
    public const string Extension = "extension";

    /// <summary>A line about torrents downloaded by the app: shown only while torrents are on.</summary>
    public const string Torrents = "torrents";

    public static readonly IReadOnlyList<string> Places = [All, Windows, WebUi, Server, Docker, Linux, Extension];

    /// <summary>
    /// Any other tag is a provider's id (as <c>RemoteAccountDto.Provider</c> names it): a line about that provider's
    /// account, shown only while an account of it is set. Contracts names no provider; changelog.json does, and its test
    /// lists the providers it may name.
    /// </summary>
    public static bool IsProvider(string? tag)
        => tag is { Length: >= 2 and <= 32 } && tag[0] is >= 'a' and <= 'z' &&
           tag.All(c => c is >= 'a' and <= 'z' or >= '0' and <= '9' or '-') &&
           tag != Torrents && !Places.Contains(tag, StringComparer.Ordinal);

    /// <summary>A place, <see cref="Torrents"/>, or a provider id.</summary>
    public static bool IsKnown(string? tag)
        => tag is not null && (Places.Contains(tag, StringComparer.Ordinal) || tag == Torrents || IsProvider(tag));
}

/// <summary>The changelog that ships inside this build (<c>Changelog\changelog.json</c>), and the rules to read it.</summary>
public static class Changelog
{
    /// <summary>Where a release's full notes are.</summary>
    public static string ReleaseNotesUrl(string version)
        => $"https://github.com/Kaan88/inbound/releases/tag/v{Clean(version)}";

    private static readonly Lazy<IReadOnlyList<ChangelogVersion>> EmbeddedVersions = new(LoadEmbedded);

    /// <summary>This build's changelog, newest first; empty if it can't be read (never throws).</summary>
    public static IReadOnlyList<ChangelogVersion> Embedded => EmbeddedVersions.Value;

    private static IReadOnlyList<ChangelogVersion> LoadEmbedded()
    {
        try
        {
            using var stream = typeof(Changelog).Assembly.GetManifestResourceStream("DownloadManager.Contracts.changelog.json");
            if (stream is null) return [];
            using var reader = new StreamReader(stream, Encoding.UTF8);
            return Parse(reader.ReadToEnd());
        }
        catch (Exception ex) when (ex is IOException or JsonException or InvalidDataException)
        {
            return [];
        }
    }

    /// <summary>Reads <c>changelog.json</c>. Throws <see cref="JsonException"/> or <see cref="InvalidDataException"/> when it is not one.</summary>
    public static IReadOnlyList<ChangelogVersion> Parse(string json)
    {
        var dto = JsonSerializer.Deserialize<ChangelogDto>(json, IpcJson.Options)
                  ?? throw new InvalidDataException("The changelog is empty.");
        foreach (var version in dto.Versions)
        {
            if (Normal(version.Version) is null) throw new InvalidDataException($"'{version.Version}' is not a version.");
            version.Sections ??= new();
            foreach (var section in version.Sections)
            {
                section.Items ??= new();
                foreach (var item in section.Items) item.Tags ??= new();
            }
        }

        return dto.Versions;
    }

    /// <summary>The versions after <paramref name="after"/> (null: all) up to and including <paramref name="upTo"/>, newest first.</summary>
    public static IReadOnlyList<ChangelogVersion> Between(IEnumerable<ChangelogVersion> versions, string? after, string upTo)
        => versions
            .Where(version => Compare(version.Version, upTo) <= 0 && (after is null || Compare(version.Version, after) > 0))
            .OrderByDescending(version => Normal(version.Version))
            .ToList();

    /// <summary>The entry for exactly this version, or null.</summary>
    public static ChangelogVersion? Find(IEnumerable<ChangelogVersion> versions, string version)
        => versions.FirstOrDefault(entry => Compare(entry.Version, version) == 0);

    /// <summary>Negative when <paramref name="a"/> is older than <paramref name="b"/>; null when either isn't a version.</summary>
    public static int? Compare(string? a, string? b)
    {
        var left = Normal(a);
        var right = Normal(b);
        if (left is null || right is null) return null;
        return left.CompareTo(right);
    }

    /// <summary>"0.7" and "0.7.0" are the same release; a leading "v" is allowed.</summary>
    public static Version? Normal(string? value)
    {
        var text = Clean(value);
        if (text.Length == 0 || !System.Version.TryParse(text, out var parsed)) return null;
        return new Version(parsed.Major, parsed.Minor, Math.Max(parsed.Build, 0), Math.Max(parsed.Revision, 0));
    }

    /// <summary>The version as people read it: three parts, or four when the fourth isn't 0.</summary>
    public static string Display(string version)
        => Normal(version) is { } normal ? (normal.Revision > 0 ? normal.ToString(4) : normal.ToString(3)) : Clean(version);

    /// <summary>"12 Nov 2026" from "2026-11-12"; null when there is no date.</summary>
    public static string? DisplayDate(string? date)
        => DateOnly.TryParseExact(date, "yyyy-MM-dd", System.Globalization.CultureInfo.InvariantCulture,
            System.Globalization.DateTimeStyles.None, out var day)
            ? day.ToString("d MMM yyyy", System.Globalization.CultureInfo.InvariantCulture)
            : null;

    private static string Clean(string? value) => value?.Trim().TrimStart('v', 'V') ?? string.Empty;
}

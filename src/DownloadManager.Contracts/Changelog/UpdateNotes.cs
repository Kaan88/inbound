using System.Text;
using System.Text.Json;

namespace DownloadManager.Contracts;

/// <summary>
/// UPD.N1: the unsigned notes of an update manifest (<see cref="UpdateManifest.Notes"/>), read with care: anything that
/// isn't a list of <c>{ text, tags }</c> is dropped, a line over <see cref="MaxTextLength"/> characters is dropped, control
/// characters become spaces, unknown tags are dropped (and a line that had tags but no known one, so a bad tag never
/// widens where a line shows), and no more than <see cref="MaxLines"/> lines are kept. The text is
/// only ever shown as plain text. Null when nothing is left.
/// </summary>
public static class UpdateNotes
{
    public const int MaxLines = 40;
    public const int MaxTextLength = 400;
    private const int MaxTags = 12;

    public static List<ChangelogItem>? Read(JsonElement? notes)
    {
        if (notes is not { ValueKind: JsonValueKind.Array } array) return null;
        var lines = new List<ChangelogItem>();
        foreach (var entry in array.EnumerateArray())
        {
            if (lines.Count == MaxLines) break;
            if (entry.ValueKind != JsonValueKind.Object) continue;
            if (!entry.TryGetProperty("text", out var textValue) || textValue.ValueKind != JsonValueKind.String) continue;
            var text = Plain(textValue.GetString());
            if (text.Length == 0 || text.Length > MaxTextLength) continue;
            var tags = new List<string>();
            var hadTags = false;
            if (entry.TryGetProperty("tags", out var tagValues) && tagValues.ValueKind == JsonValueKind.Array)
            {
                foreach (var tag in tagValues.EnumerateArray())
                {
                    hadTags = true;
                    if (tags.Count == MaxTags) break;
                    if (tag.ValueKind == JsonValueKind.String && tag.GetString() is { } name && ChangelogTags.IsKnown(name) && !tags.Contains(name))
                        tags.Add(name);
                }
            }

            // An untagged line applies everywhere; one whose tags were all unknown was meant for somewhere, not here.
            if (hadTags && tags.Count == 0) continue;
            lines.Add(new ChangelogItem { Text = text, Tags = tags });
        }

        return lines.Count == 0 ? null : lines;
    }

    /// <summary>One version's changelog lines, in order, as a manifest carries them.</summary>
    public static List<ChangelogItem> From(ChangelogVersion version)
        => version.Sections.SelectMany(section => section.Items)
            .Select(item => new ChangelogItem { Text = item.Text, Tags = item.Tags.ToList() })
            .ToList();

    /// <summary>Why these lines can't go into a manifest as they are (too many, too long, unknown tags); null when they can.</summary>
    public static string? Problem(IReadOnlyList<ChangelogItem> lines)
    {
        if (lines.Count > MaxLines) return $"{lines.Count} lines; an update manifest carries at most {MaxLines}.";
        foreach (var line in lines)
        {
            if (string.IsNullOrWhiteSpace(line.Text)) return "A line has no text.";
            if (line.Text.Length > MaxTextLength) return $"A line is {line.Text.Length} characters; the most is {MaxTextLength}: {line.Text[..40]}…";
            if (line.Tags.FirstOrDefault(tag => !ChangelogTags.IsKnown(tag)) is { } unknown) return $"Unknown tag '{unknown}'.";
        }

        return null;
    }

    private static string Plain(string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) return string.Empty;
        var builder = new StringBuilder(value.Length);
        foreach (var c in value)
        {
            // Review of 0.7.2, security-2: format characters (bidi overrides, zero-width ones) could lay text out to mislead.
            if (char.GetUnicodeCategory(c) == System.Globalization.UnicodeCategory.Format) continue;
            builder.Append(char.IsControl(c) || c is (char)0x2028 or (char)0x2029 ? ' ' : c);
        }
        return builder.ToString().Trim();
    }
}

namespace DownloadManager.Mobile.Updates;

/// <summary>
/// The release notes as the update prompt draws them: one item per line, with a single leading bullet or dash taken off
/// (Fable L1: taking every marker off turned "-5 % memory" into "5 % memory", and a "---" separator became an empty bullet).
/// </summary>
public static class ReleaseNotesText
{
    private const string Markers = "•-*";

    /// <summary>The lines that carry text, in order; blank lines and lines of nothing but markers are left out.</summary>
    public static IReadOnlyList<string> Lines(string? value)
    {
        if (string.IsNullOrEmpty(value)) return [];
        var lines = new List<string>();
        foreach (var raw in value.Replace("\r\n", "\n").Split('\n'))
        {
            var line = Item(raw);
            if (line.Length > 0) lines.Add(line);
        }
        return lines;
    }

    /// <summary>
    /// One line without its bullet. A marker counts only when a space or tab follows it, so a minus in front of a number
    /// stays; a line that has no text once trimmed (a "---" separator, or a lone marker) comes back empty.
    /// </summary>
    public static string Item(string? raw)
    {
        if (string.IsNullOrEmpty(raw)) return "";
        var body = raw.TrimStart();
        if (body.Length > 1 && Markers.Contains(body[0]) && body[1] is ' ' or '\t') body = body[1..];
        var line = body.Trim();
        foreach (var c in line) if (!Markers.Contains(c)) return line;
        return "";
    }
}

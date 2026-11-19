namespace DownloadManager.Mobile;

/// <summary>Normalizes legacy Host weekday values and editor drafts to Runtime weekday names.</summary>
public static class ScheduleDays
{
    private static readonly string[] OrderedDays = ["Mon", "Tue", "Wed", "Thu", "Fri", "Sat", "Sun"];
    private static readonly IReadOnlyDictionary<string, string> Names = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
    {
        ["Mon"] = "Mon", ["Monday"] = "Mon",
        ["Tue"] = "Tue", ["Tuesday"] = "Tue",
        ["Wed"] = "Wed", ["Wednesday"] = "Wed",
        ["Thu"] = "Thu", ["Thursday"] = "Thu",
        ["Fri"] = "Fri", ["Friday"] = "Fri",
        ["Sat"] = "Sat", ["Saturday"] = "Sat",
        ["Sun"] = "Sun", ["Sunday"] = "Sun",
    };

    /// <summary>An empty legacy Host value means every day, as it does in Runtime.</summary>
    public static HashSet<string> FromHost(string? value)
        => string.IsNullOrWhiteSpace(value) ? AllDays() : Parse(value);

    /// <summary>An empty editor draft means no selected days, so clearing a schedule remains intentional.</summary>
    public static HashSet<string> FromDraft(string? value)
        => string.IsNullOrWhiteSpace(value) ? new(StringComparer.Ordinal) : Parse(value);

    /// <summary>Writes canonical short names in Monday-to-Sunday order.</summary>
    public static string Serialize(IEnumerable<string> days)
    {
        ArgumentNullException.ThrowIfNull(days);
        var canonical = new HashSet<string>(StringComparer.Ordinal);
        foreach (var day in days)
        {
            if (day is null || !Names.TryGetValue(day.Trim(), out var name))
                throw new FormatException($"Unknown schedule weekday '{day}'.");
            canonical.Add(name);
        }

        return string.Join(",", OrderedDays.Where(canonical.Contains));
    }

    private static HashSet<string> Parse(string value)
    {
        var result = new HashSet<string>(StringComparer.Ordinal);
        foreach (var token in value.Split([',', ';'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            if (!Names.TryGetValue(token, out var name))
                throw new FormatException($"Unknown schedule weekday '{token}'.");
            result.Add(name);
        }
        return result;
    }

    private static HashSet<string> AllDays() => new(OrderedDays, StringComparer.Ordinal);
}

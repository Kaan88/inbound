using System.Globalization;
using DownloadManager.Contracts;

namespace DownloadManager.Mobile;

/// <summary>One line of the Details screen's list: "From", "releases.example.org".</summary>
public sealed record InfoLine(string Label, string Value);

/// <summary>What the Details screen writes about a download, from the job and the Host's settings.</summary>
public static class DetailsInfo
{
    /// <summary>"58%": the figure the card opens with; empty when the size is unknown.</summary>
    public static string Percent(JobRow row) =>
        row.Group == RowGroup.Finished ? "100%" : row.Job.TotalBytes is > 0 ? $"{(int)Math.Floor(row.Progress * 100)}%" : "";

    /// <summary>"10.9 MB/s" while it transfers; otherwise the state in words ("Paused", "Finished").</summary>
    public static string RateLine(JobRow row) => row.Group switch
    {
        RowGroup.Active => row.Job.BytesPerSecond is > 0 ? Fmt.Rate(row.Job.BytesPerSecond.Value) : "Connecting",
        RowGroup.Paused => "Paused",
        RowGroup.Finished => "Finished",
        RowGroup.Failed => row.Job.State.Equals("cancelled", StringComparison.OrdinalIgnoreCase) ? "Cancelled" : "Failed",
        _ => "Queued",
    };

    /// <summary>"3.5 GB of 6.1 GB · 4 min left".</summary>
    public static string ProgressLine(JobRow row, DateTimeOffset now)
    {
        var j = row.Job;
        if (row.Group == RowGroup.Finished) return Fmt.Bytes(j.TotalBytes ?? j.ReceivedBytes) + (j.CompletedAt is { } d ? " · " + Fmt.When(d, now) : "");
        if (row.Group == RowGroup.Failed) return string.IsNullOrEmpty(j.StatusNote) ? Fmt.Bytes(j.ReceivedBytes) + " saved" : j.StatusNote;
        var text = j.TotalBytes is > 0 ? $"{Fmt.Bytes(j.ReceivedBytes)} of {Fmt.Bytes(j.TotalBytes.Value)}" : Fmt.Bytes(j.ReceivedBytes);
        if (row.Group == RowGroup.Active && j.BytesPerSecond is > 0 && j.TotalBytes > j.ReceivedBytes)
            text += " · " + Fmt.Eta((j.TotalBytes.Value - j.ReceivedBytes) / j.BytesPerSecond.Value);
        return text;
    }

    /// <summary>The list under the connections: where it comes from, where it goes, which queue, when it was added, the network rule.</summary>
    public static IReadOnlyList<InfoLine> Lines(JobDto j, SettingsDto settings, DateTimeOffset now)
    {
        var lines = new List<InfoLine>
        {
            new("From", HostOf(j)),
            new("Saved to", FolderName(j.DestinationDirectory)),
        };
        if (settings.Queues.FirstOrDefault(q => q.Id == j.QueueId) is { } queue && !string.IsNullOrWhiteSpace(queue.Name))
            lines.Add(new("Queue", queue.Name));
        lines.Add(new("Added", AddedText(j, now)));
        if (settings.DownloadOnlyOnUnmeteredNetwork) lines.Add(new("Network", "Wi-Fi only"));
        return lines;
    }

    /// <summary>The site of the address (never the whole address: it may hold a secret); the Host's masked address gives its site too.</summary>
    public static string HostOf(JobDto j) =>
        Uri.TryCreate(j.Url, UriKind.Absolute, out var u) && !string.IsNullOrEmpty(u.Host) ? u.Host : j.Url;

    /// <summary>The last folder of a path ("/storage/emulated/0/Download" gives "Download"), so a long path does not fill the line.</summary>
    public static string FolderName(string? path)
    {
        var trimmed = (path ?? "").TrimEnd('/', '\\');
        var i = trimmed.LastIndexOfAny(['/', '\\']);
        return i >= 0 && i < trimmed.Length - 1 ? trimmed[(i + 1)..] : trimmed;
    }

    /// <summary>"Today 14:01" / "Yesterday 14:01" / "12 Nov 14:01", with ", from Chrome" for a browser that sent it.</summary>
    public static string AddedText(JobDto j, DateTimeOffset now)
    {
        var c = j.CreatedAt.ToLocalTime();
        var n = now.ToLocalTime();
        var time = c.ToString("HH:mm", CultureInfo.InvariantCulture);
        var day = c.Date == n.Date ? "Today" : c.Date == n.Date.AddDays(-1) ? "Yesterday" : c.ToString("d MMM", CultureInfo.InvariantCulture);
        var from = (j.Source ?? "").ToLowerInvariant() switch { "chrome" => "Chrome", "edge" => "Edge", "firefox" => "Firefox", _ => null };
        return $"{day} {time}" + (from is null ? "" : ", from " + from);
    }

    /// <summary>"8 connections" (those working now when it runs, else the number asked for).</summary>
    public static string ConnectionsTitle(JobRow row)
    {
        var n = row.Group == RowGroup.Active && row.Job.ActiveConnections > 0 ? row.Job.ActiveConnections : row.Job.RequestedConnections;
        return n <= 0 ? "Connections" : n == 1 ? "1 connection" : $"{n} connections";
    }

    /// <summary>Each connection's share of its own piece, 0..1, in order; a complete piece is 1.</summary>
    public static IReadOnlyList<float> SegmentFractions(IReadOnlyList<SegmentDto> segments) =>
        segments.Select(s => s.IsComplete ? 1f : (float)Math.Clamp(s.Fraction, 0, 1)).ToList();
}

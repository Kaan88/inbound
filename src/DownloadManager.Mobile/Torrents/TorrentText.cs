using System.Globalization;
using DownloadManager.Contracts;

namespace DownloadManager.Mobile.Torrents;

public enum TorrentTone { Neutral, Amber, Done }

/// <summary>How a torrent looks on its row in Downloads (t3). Null from <see cref="For"/> leaves the download's own row as it is.</summary>
public sealed class TorrentRowInfo
{
    public required string Lead { get; init; }
    public string? Rest { get; init; }
    public TorrentTone Tone { get; init; }

    /// <summary>The subtitle: the lead (coloured by the screen when it has a tone), then " · " and the rest.</summary>
    public string Subtitle => string.IsNullOrEmpty(Rest) ? Lead : Lead + " · " + Rest;

    /// <summary>"2.5 of 3.9 GB · ↓ 18.6 MB/s · ↑ 1.2 MB/s · 48 seeds" while it downloads; "Seeding · ↑ 640 KB/s · ratio 0.71 of 1.0" while it seeds.</summary>
    public static TorrentRowInfo? For(JobDto job, SettingsDto? settings)
    {
        ArgumentNullException.ThrowIfNull(job);
        if (job.Torrent is not { } torrent) return null;
        if (torrent.AwaitingFileChoice) return new TorrentRowInfo { Lead = "Choose files to download", Tone = TorrentTone.Amber };
        if (TorrentText.IsSeeding(job))
        {
            var parts = new List<string>();
            if (torrent.UploadRate >= 1) parts.Add("↑ " + Fmt.Rate(torrent.UploadRate));
            parts.Add("ratio " + TorrentText.Ratio(torrent.Ratio) + TorrentText.OfGoal(torrent, settings));
            return new TorrentRowInfo { Lead = "Seeding", Rest = string.Join(" · ", parts), Tone = TorrentTone.Done };
        }

        if (!TorrentText.IsActive(job)) return null;
        if (torrent.Checking || job.State == "verifying") return new TorrentRowInfo { Lead = "Checking files" };
        if (!torrent.HasMetadata)
            return new TorrentRowInfo { Lead = "Getting file list", Rest = TorrentText.Peers(torrent.Peers) };
        var total = job.TotalBytes;
        var lead = total is > 0 ? $"{Fmt.Bytes(job.ReceivedBytes)} of {Fmt.Bytes(total.Value)}" : Fmt.Bytes(job.ReceivedBytes);
        var rest = new List<string>();
        if (job.BytesPerSecond is >= 1) rest.Add("↓ " + Fmt.Rate(job.BytesPerSecond.Value));
        if (torrent.UploadRate >= 1) rest.Add("↑ " + Fmt.Rate(torrent.UploadRate));
        rest.Add(torrent.Seeds == 1 ? "1 seed" : torrent.Seeds + " seeds");
        return new TorrentRowInfo { Lead = lead, Rest = string.Join(" · ", rest) };
    }
}

/// <summary>One "label, value" line of the Overview tab.</summary>
public sealed record TorrentLine(string Label, string Value);

/// <summary>The words the torrent screens show. Nothing here is a number the Host did not send.</summary>
public static class TorrentText
{
    public const string NotOffered = "This Host can't download torrents.";
    public const string TurnedOff = "Torrents are off. Turn on Download torrents here in Settings, or use TorBox.";
    public const string NotInput = "Paste a magnet link (it starts with magnet:?) or choose a .torrent file.";
    public const string FileNotRead = "That file could not be read as a .torrent file.";
    public const string NotRunning = "Not running. Peers and trackers show while the torrent downloads or seeds.";
    public const string NoPeers = "No peers connected yet.";
    public const string NoTrackers = "This torrent lists no trackers.";
    public const string SeedingStopped = "Seeding stopped";
    public const string OlderHost = "This Host can't show that. Update Inbound there to see it.";

    /// <summary>The line under "Download with" while This phone is chosen.</summary>
    public static string PhoneHint(bool phoneHost, string hostName) =>
        $"Peers see {(phoneHost ? "this phone" : hostName)}'s IP address, and it uploads to them while it downloads.";

    public static bool IsActive(JobDto job) => job.State is "connecting" or "downloading" or "verifying";

    /// <summary>The torrent finished and is shared with other peers now.</summary>
    public static bool IsSeeding(JobDto? job) => job is { State: "completed", Torrent.Seeding: true };

    /// <summary>A finished torrent that does not seed (seeding ended or was stopped): Start seeding applies.</summary>
    public static bool CanStartSeeding(JobDto? job) => job is { State: "completed", Torrent.Seeding: false };

    /// <summary>The torrent may be on the engine now, so peers and trackers can be listed.</summary>
    public static bool MayBeRunning(JobDto? job) => job?.Torrent is not null && (IsActive(job) || IsSeeding(job));

    public static string Peers(int peers) => peers == 1 ? "1 peer" : $"{peers.ToString(CultureInfo.InvariantCulture)} peers";

    /// <summary>"0.71".</summary>
    public static string Ratio(double ratio) =>
        (double.IsFinite(ratio) ? Math.Max(0, ratio) : 0).ToString("0.00", CultureInfo.InvariantCulture);

    /// <summary>"1.0" or "2.5": a goal as the rows write it.</summary>
    public static string Goal(double value)
    {
        var text = value.ToString("0.##", CultureInfo.InvariantCulture);
        return text.Contains('.') ? text : text + ".0";
    }

    /// <summary>The ratio goal this torrent seeds to, or null when it has none of its own and the setting has none.</summary>
    public static double? RatioGoal(TorrentDto torrent, SettingsDto? settings) => torrent.SeedGoal switch
    {
        "ratio" => torrent.SeedGoalValue,
        "global" when settings is { TorrentSeedAfterDownload: true, TorrentSeedRatio: > 0 } => settings.TorrentSeedRatio,
        _ => null,
    };

    /// <summary>" of 1.0" after the ratio, when there is a ratio goal.</summary>
    public static string OfGoal(TorrentDto torrent, SettingsDto? settings) => RatioGoal(torrent, settings) is { } goal ? " of " + Goal(goal) : "";

    /// <summary>"seed until 1.0 or 48 h", "seed until 1.0", "seed for 48 h", "seeds until stopped" or "doesn't seed".</summary>
    public static string SeedUntil(string? goal, double? value, SettingsDto? settings)
    {
        double? ratio = null, hours = null;
        switch (goal)
        {
            case "ratio": ratio = value; break;
            case "hours": hours = value; break;
            case "never": return "seeds until stopped";
            default:
                if (settings is null) return "";
                if (!settings.TorrentSeedAfterDownload) return "doesn't seed";
                if (settings.TorrentSeedRatio > 0) ratio = settings.TorrentSeedRatio;
                if (settings.TorrentSeedHours > 0) hours = settings.TorrentSeedHours;
                break;
        }

        var h = hours is > 0 ? Hours(hours.Value) : null;
        if (ratio is > 0 && h is not null) return $"seed until {Goal(ratio.Value)} or {h}";
        if (ratio is > 0) return $"seed until {Goal(ratio.Value)}";
        return h is not null ? $"seed for {h}" : "seeds until stopped";
    }

    /// <summary>"48 h" (whole hours) or "1.5 h".</summary>
    public static string Hours(double hours) => hours.ToString("0.#", CultureInfo.InvariantCulture) + " h";

    /// <summary>The Seeding row of t7: "Until ratio 1.0 or 48 hours" (the network and charging part of the mockup waits for a Host setting).</summary>
    public static string SeedingSubtitle(SettingsDto settings)
    {
        if (!settings.TorrentSeedAfterDownload) return "Stops when the download is done";
        var ratio = settings.TorrentSeedRatio > 0;
        var hours = settings.TorrentSeedHours > 0;
        var hoursText = settings.TorrentSeedHours.ToString("0.#", CultureInfo.InvariantCulture) + (settings.TorrentSeedHours == 1 ? " hour" : " hours");
        if (ratio && hours) return $"Until ratio {Goal(settings.TorrentSeedRatio)} or {hoursText}";
        if (ratio) return $"Until ratio {Goal(settings.TorrentSeedRatio)}";
        if (hours) return "For " + hoursText;
        return "Until you stop it";
    }

    /// <summary>The Connections and limits row of t7: "Port 51413, DHT, encryption, speed".</summary>
    public static string ConnectionsSubtitle(SettingsDto settings)
    {
        var parts = new List<string> { settings.TorrentListenPort > 0 ? "Port " + settings.TorrentListenPort.ToString(CultureInfo.InvariantCulture) : "Port chosen at first start" };
        parts.Add(settings.TorrentDht ? "DHT" : "no DHT");
        parts.Add(settings.TorrentEncryption is "require" ? "encryption required" : settings.TorrentEncryption is "off" ? "no encryption" : "encryption");
        parts.Add(settings.TorrentUploadLimit > 0 ? "up to " + Fmt.Rate(settings.TorrentUploadLimit) : "speed");
        return string.Join(", ", parts);
    }

    public static string EncryptionName(string? encryption) => encryption switch
    {
        "require" => "Required",
        "off" => "Off",
        _ => "Prefer",
    };

    /// <summary>The words under the t7 switch: "Off: magnets go to TorBox", or what the setting means when TorBox is not offered.</summary>
    public static string DownloadHereSubtitle(bool on, bool torBoxOffered) =>
        on ? "On: magnets and .torrent files download here" : torBoxOffered ? "Off: magnets go to TorBox" : "Off: torrents are not downloaded";

    /// <summary>"Torrents on this phone", or "Torrents on Office PC" for a paired Host.</summary>
    public static string GroupTitle(bool phoneHost, string hostName) => phoneHost ? "Torrents on this phone" : "Torrents on " + hostName;

    /// <summary>"1 min 18 s left".</summary>
    public static string Left(TimeSpan? remaining)
    {
        if (remaining is not { } span || span <= TimeSpan.Zero) return "";
        var seconds = (long)Math.Round(span.TotalSeconds);
        if (seconds < 60) return $"{Math.Max(1, seconds)} s left";
        if (seconds < 3600) return $"{seconds / 60} min {seconds % 60} s left";
        return $"{seconds / 3600} h {seconds % 3600 / 60} min left";
    }

    /// <summary>"↓ 18.6 MB/s · ↑ 1.2 MB/s" (the details card, t4: both directions once anything moves); only the upload while seeding.</summary>
    public static string RateLine(JobDto job)
    {
        var torrent = job.Torrent;
        var downRate = job.BytesPerSecond is >= 1 && IsActive(job) ? job.BytesPerSecond.Value : 0;
        var upRate = torrent?.UploadRate >= 1 ? torrent.UploadRate : 0;
        if (IsSeeding(job)) return upRate > 0 ? "↑ " + Fmt.Rate(upRate) : "Seeding";
        if (downRate <= 0 && upRate <= 0) return job.State == "paused" ? "Paused" : IsActive(job) ? "Starting…" : "";
        return "↓ " + Fmt.Rate(downRate) + " · ↑ " + Fmt.Rate(upRate);
    }

    /// <summary>"2.5 of 3.9 GB · 1 min 18 s left".</summary>
    public static string ProgressLine(JobDto job)
    {
        var total = job.TotalBytes;
        var size = total is > 0 ? $"{Fmt.Bytes(Math.Min(job.ReceivedBytes, total.Value))} of {Fmt.Bytes(total.Value)}" : Fmt.Bytes(job.ReceivedBytes);
        if (IsSeeding(job)) return Fmt.Bytes(total ?? job.ReceivedBytes) + " · " + Fmt.Bytes(job.Torrent!.Uploaded) + " uploaded";
        var left = IsActive(job) ? Left(job.EstimatedRemaining) : "";
        return left.Length == 0 ? size : size + " · " + left;
    }

    /// <summary>The Overview tab's lines. A line whose figure the Host did not give is left out.</summary>
    /// <param name="savedTo">The folder as the screen writes it (already shortened for a phone).</param>
    public static IReadOnlyList<TorrentLine> Overview(JobDto job, TorrentDetailsResult? details, TorrentTrackersResult? trackers,
        TorrentPiecesResult? pieces, SettingsDto? settings, string savedTo, DateTimeOffset now)
    {
        var lines = new List<TorrentLine>();
        var torrent = job.Torrent ?? new TorrentDto();
        lines.Add(new TorrentLine("Seeds", OutOf(torrent.Seeds, trackers?.Trackers.Select(t => t.Seeds))));
        lines.Add(new TorrentLine("Peers", OutOf(torrent.Peers, trackers?.Trackers.Select(t => t.Peers))));
        var options = details?.Options;
        var until = SeedUntil(options?.SeedGoal ?? torrent.SeedGoal, options?.SeedGoalValue ?? torrent.SeedGoalValue, settings);
        lines.Add(new TorrentLine("Ratio", Ratio(torrent.Ratio) + (until.Length == 0 ? "" : " · " + until)));
        if (pieces is { Ok: true, Count: > 0 } p)
            lines.Add(new TorrentLine("Pieces", $"{Math.Clamp(p.Have, 0, p.Count).ToString("N0", CultureInfo.InvariantCulture)} of {p.Count.ToString("N0", CultureInfo.InvariantCulture)}"));
        else if (details is { PieceCount: > 0 })
            lines.Add(new TorrentLine("Pieces", details.PieceCount.ToString("N0", CultureInfo.InvariantCulture)));
        if (savedTo.Length > 0) lines.Add(new TorrentLine("Saved to", savedTo));
        // The Wi-Fi only part of this line waits for a Host setting (owner decision pending).
        if (settings is { TorrentListenPort: > 0 }) lines.Add(new TorrentLine("Network", "port " + settings.TorrentListenPort.ToString(CultureInfo.InvariantCulture)));
        lines.Add(new TorrentLine("Added", AddedText(details?.AddedAt is { } at && at != default ? at : job.CreatedAt, torrent.Source, now)));
        return lines;
    }

    /// <summary>"48 of 212": connected, then the most any tracker counts when that is more.</summary>
    public static string OutOf(int connected, IEnumerable<int>? trackerCounts)
    {
        var most = trackerCounts?.DefaultIfEmpty(-1).Max() ?? -1;
        return most > connected ? $"{connected} of {most}" : connected.ToString(CultureInfo.InvariantCulture);
    }

    /// <summary>"Today 14:01, magnet" / "Yesterday 14:01, torrent file" / "12 Nov 14:01, magnet".</summary>
    public static string AddedText(DateTimeOffset added, string? source, DateTimeOffset now)
    {
        var c = added.ToLocalTime();
        var n = now.ToLocalTime();
        var day = c.Date == n.Date ? "Today" : c.Date == n.Date.AddDays(-1) ? "Yesterday" : c.ToString("d MMM", CultureInfo.InvariantCulture);
        return $"{day} {c.ToString("HH:mm", CultureInfo.InvariantCulture)}, {(source == "file" ? "torrent file" : "magnet")}";
    }

    /// <summary>A peer's line: "peer 3 · IPv4" (the address is never put in a row), its place in the list.</summary>
    public static string PeerTitle(int number, TorrentPeerDto peer)
    {
        var how = peer.Flags.Contains("utp") ? "uTP" : "TCP";
        if (peer.Flags.Contains("encrypted")) how += " · encrypted";
        var kind = peer.Address.Contains(':', StringComparison.Ordinal) ? "IPv6" : "IPv4";
        return $"peer {number} · {kind} · {how}";
    }

    /// <summary>"has 41% · ↓ 6.1 MB/s · ↑ 80 KB/s".</summary>
    public static string PeerLine(TorrentPeerDto peer)
    {
        var parts = new List<string> { peer.Flags.Contains("seed") ? "seed" : $"has {Math.Floor(Math.Clamp(double.IsFinite(peer.Progress) ? peer.Progress : 0, 0, 1) * 100).ToString("0", CultureInfo.InvariantCulture)}%" };
        if (peer.DownloadRate >= 1) parts.Add("↓ " + Fmt.Rate(peer.DownloadRate));
        if (peer.UploadRate >= 1) parts.Add("↑ " + Fmt.Rate(peer.UploadRate));
        return string.Join(" · ", parts);
    }

    /// <summary>A tracker's address without what could be a passkey: no query, and nothing after the host unless it is a plain announce.</summary>
    public static string TrackerShown(string url)
    {
        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri) || uri.Host.Length == 0) return "Tracker";
        var origin = $"{uri.Scheme}://{uri.Authority}";
        var path = uri.AbsolutePath.TrimEnd('/');
        if (path.Length == 0) return origin;
        return path is "/announce" or "/announce.php" ? origin + path : origin + "/…";
    }

    public static string TrackerStatus(string? status) => status switch
    {
        "working" => "Working",
        "updating" => "Updating",
        "error" => "Error",
        "notContacted" => "Not contacted",
        _ => "Unknown",
    };

    /// <summary>
    /// "Working · 212 seeds · 31 peers"; counts the tracker did not give are left out. An error carries the tracker's or the network's words, as
    /// on the desktop, with any address in them hidden (an announce address may hold a passkey).
    /// </summary>
    public static string TrackerLine(TorrentTrackerDto tracker)
    {
        var parts = new List<string> { TrackerStatus(tracker.Status) };
        if (tracker.Seeds >= 0) parts.Add(tracker.Seeds == 1 ? "1 seed" : tracker.Seeds + " seeds");
        if (tracker.Peers >= 0) parts.Add(Peers(tracker.Peers));
        if (tracker.Status == "error" && HideAddresses(tracker.Message) is { Length: > 0 } said) parts.Add(said);
        return string.Join(" · ", parts);
    }

    private static string HideAddresses(string? text) => string.IsNullOrWhiteSpace(text) ? "" :
        System.Text.RegularExpressions.Regex.Replace(text.Trim(), @"\b[a-zA-Z][a-zA-Z0-9+.-]*://\S+", "(address)");
}

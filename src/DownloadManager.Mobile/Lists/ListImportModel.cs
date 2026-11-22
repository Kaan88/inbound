using System.Text;
using DownloadManager.Contracts;
using DownloadManager.Mobile.Torrents;

namespace DownloadManager.Mobile.Lists;

/// <summary>One line of "In the list": the icon says a link or a torrent; a list file has no sizes, so the name is all there is.</summary>
public sealed record ListRow(string Name, bool IsTorrent);

/// <summary>An entry that was not added, with the reason in plain words. The address is for the banner, the name for Details.</summary>
public sealed record SkippedEntry(string Address, string Name, string Reason)
{
    public string BannerText => Address + ": " + Reason;

    public string DetailsText => Name + ": " + Reason;
}

/// <summary>What an import did: how many went in, which did not, and the words for the snackbar and Details.</summary>
public sealed class ListImportResult(int added, IReadOnlyList<SkippedEntry> skipped)
{
    public const string DetailsTitle = "Skipped from the list";

    public int Added { get; } = added;

    public IReadOnlyList<SkippedEntry> Skipped { get; } = skipped;

    public string SnackbarText => $"Added {Added} from the list" + (Skipped.Count > 0 ? $" · {Skipped.Count} skipped" : "");

    public string? ActionText => Skipped.Count > 0 ? "Details" : null;

    public string DetailsBody => string.Join("\n\n", Skipped.Select(s => s.DetailsText));
}

/// <summary>
/// Import a list (A4): an exported list of downloads from any Inbound, shown for review and sent to the selected Host. Plain classes: the
/// screen only draws what is here and picks the file and the folder. Nothing here is logged; a list can hold private addresses.
/// </summary>
public sealed class ListImportModel
{
    public const int PreviewCount = 4;

    /// <summary>A list file larger than this is not read; a real list is far smaller.</summary>
    public const int MaxFileBytes = 8 * 1024 * 1024;

    public const string TooLarge = "That file is larger than 8 MB, too big to be an Inbound list.";
    public const string NotWebAddress = "That isn't a web address.";
    public const string FtpNotSupported = "FTP addresses aren't supported.";
    public const string TorrentsNotOffered = "This Host doesn't take torrents.";
    public const string HostRefusedWords = "The Host did not accept this download.";
    public const string NotAListTitle = "That isn't an Inbound list";
    public const string EachOwnFolder = "Each download's own folder";
    public const string PhoneHint = "The list's folders are on another computer, so files go here. Queues, start times and names come from the list.";

    private readonly bool _paired;
    private readonly string _hostName;
    private readonly List<SkippedEntry> _refused = [];

    private ListImportModel(IReadOnlyList<JobListEntry> entries, string fileName, bool paired, string hostName)
    {
        Entries = entries;
        FileName = fileName;
        _paired = paired;
        _hostName = hostName;
        foreach (var entry in entries.Where(e => !JobListFile.IsTorrent(e)))
            if (RefusalOf(entry.Url) is { } why) _refused.Add(new SkippedEntry(entry.Url, NameOf(entry), why));
    }

    /// <summary>The list in <paramref name="bytes"/>, or null when it is not one Inbound can read or has no downloads in it.</summary>
    public static ListImportModel? TryRead(byte[] bytes, string fileName, bool paired, string hostName)
    {
        try
        {
            var entries = JobListFile.Deserialize(new UTF8Encoding(false).GetString(bytes).TrimStart('﻿'));
            return entries.Count == 0 ? null : new ListImportModel(entries, fileName, paired, hostName);
        }
        catch (Exception e) when (e is FormatException or System.Text.Json.JsonException or ArgumentException or InvalidOperationException)
        {
            return null;
        }
    }

    public IReadOnlyList<JobListEntry> Entries { get; }

    public string FileName { get; }

    public bool Paired => _paired;

    public int Downloads => Entries.Count;

    public int Torrents => Entries.Count(JobListFile.IsTorrent);

    public int Links => Downloads - Torrents;

    public IReadOnlyList<ListRow> Preview => Entries.Take(PreviewCount).Select(e => new ListRow(NameOf(e), JobListFile.IsTorrent(e))).ToList();

    public int MoreCount => Math.Max(0, Downloads - PreviewCount);

    public string? MoreText => MoreCount > 0 ? $"and {MoreCount} more" : null;

    /// <summary>Entries that are refused before anything is sent (the desktop refuses the same ones).</summary>
    public IReadOnlyList<SkippedEntry> Refused => _refused;

    public string? RefusedTitle => _refused.Count == 0 ? null : $"{_refused.Count} can't be added";

    /// <summary>The first refusal; with more, a line saying how many others.</summary>
    public string? RefusedBody => _refused.Count == 0 ? null
        : _refused.Count == 1 ? _refused[0].BannerText
        : _refused[0].BannerText + $"\nand {_refused.Count - 1} more";

    /// <summary>What will be sent: every entry that was not refused.</summary>
    public int SendCount => Downloads - _refused.Count;

    /// <summary>The one folder for every download, or null: on the phone the Host's own folders, on a paired Host each entry's folder.</summary>
    public string? ChosenFolder { get; set; }

    public bool Sending { get; private set; }

    public bool CanSend => SendCount > 0 && !Sending;

    public string SaveToLabel => _paired ? "Save to, on " + _hostName : "Save to";

    /// <summary>What the field shows; <paramref name="hostDownloadFolder"/> is the Host's download folder (the phone's own).</summary>
    public string SaveToValue(string hostDownloadFolder) => !string.IsNullOrWhiteSpace(ChosenFolder) ? SavePathWords.ForField(ChosenFolder!)
        : _paired ? EachOwnFolder : SavePathWords.ForField(hostDownloadFolder);

    public string SaveToHint => _paired
        ? $"Folders from the list that {_hostName} doesn't have use its download folder. Torrents go to its torrent list."
        : PhoneHint;

    public string AddLabel => _paired ? $"Add {SendCount} on {_hostName}" : $"Add {SendCount}";

    /// <summary>A name for a row: the entry's own, the torrent's, the last part of the address (decoded), or the address.</summary>
    public static string NameOf(JobListEntry entry)
    {
        if (!string.IsNullOrWhiteSpace(entry.FileName)) return entry.FileName.Trim();
        if (!string.IsNullOrWhiteSpace(entry.Torrent?.Name)) return entry.Torrent!.Name!.Trim();
        var url = entry.Url.Trim();
        if (url.StartsWith("magnet:", StringComparison.OrdinalIgnoreCase)) return MagnetName(url) ?? url;
        if (Uri.TryCreate(url, UriKind.Absolute, out var uri))
        {
            var last = uri.AbsolutePath.TrimEnd('/').Split('/').LastOrDefault();
            if (!string.IsNullOrEmpty(last))
            {
                try { return Uri.UnescapeDataString(last); } catch (UriFormatException) { return last; }
            }
        }

        return url;
    }

    private static string? MagnetName(string magnet)
    {
        var query = magnet.IndexOf('?');
        if (query < 0) return null;
        foreach (var part in magnet[(query + 1)..].Split('&'))
            if (part.StartsWith("dn=", StringComparison.OrdinalIgnoreCase) && part.Length > 3)
            {
                try { return Uri.UnescapeDataString(part[3..].Replace('+', ' ')); } catch (UriFormatException) { return null; }
            }

        return null;
    }

    private static string? RefusalOf(string url)
    {
        if (!Uri.TryCreate(url.Trim(), UriKind.Absolute, out var uri)) return NotWebAddress;
        if (uri.Scheme is "ftp" or "ftps") return FtpNotSupported;
        return uri.Scheme is "http" or "https" ? null : NotWebAddress;
    }

    /// <summary>
    /// Sends the list: the torrents in one torrent.import, then each link as its own add, going on past a failure.
    /// <paramref name="torrents"/> is null when the Host doesn't take torrents.
    /// </summary>
    public async Task<ListImportResult> SendAsync(DownloadsModel downloads, TorrentClient? torrents, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(downloads);
        Sending = true;
        try
        {
            var skipped = new List<SkippedEntry>(_refused);
            var added = 0;
            var torrentEntries = Entries.Where(JobListFile.IsTorrent).ToList();
            if (torrentEntries.Count > 0) added += await SendTorrentsAsync(torrentEntries, torrents, skipped, cancellationToken).ConfigureAwait(false);
            foreach (var entry in Entries.Where(e => !JobListFile.IsTorrent(e) && RefusalOf(e.Url) is null))
            {
                cancellationToken.ThrowIfCancellationRequested();
                var why = await AddLinkAsync(downloads, entry, cancellationToken).ConfigureAwait(false);
                if (why is null) added++;
                else skipped.Add(new SkippedEntry(entry.Url, NameOf(entry), why));
            }

            return new ListImportResult(added, skipped);
        }
        finally { Sending = false; }
    }

    private async Task<int> SendTorrentsAsync(List<JobListEntry> torrentEntries, TorrentClient? client, List<SkippedEntry> skipped, CancellationToken cancellationToken)
    {
        void All(string reason) { foreach (var e in torrentEntries) skipped.Add(new SkippedEntry(e.Url, NameOf(e), reason)); }
        if (client is null) { All(TorrentsNotOffered); return 0; }
        var copies = torrentEntries.Select(e =>
        {
            var copy = Copy(e);
            copy.DestinationDirectory = DestinationFor(e);
            return copy;
        }).ToList();
        TorrentImportResult result;
        try { result = await client.ImportAsync(copies, cancellationToken).ConfigureAwait(false); }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex) { All(ex.Message); return 0; }

        // The reply counts; it does not say which entries. When every entry got the same outcome they share its reason, otherwise a line stands for each.
        var skippedWhy = result.SkipReason ?? "Already in the torrent list.";
        var failedWhy = result.FirstError ?? HostRefusedWords;
        if (result.Added == 0 && result.Failed == 0 && result.Skipped >= torrentEntries.Count) All(skippedWhy);
        else if (result.Added == 0 && result.Skipped == 0 && result.Failed >= torrentEntries.Count) All(failedWhy);
        else
        {
            for (var i = 0; i < result.Skipped; i++) skipped.Add(new SkippedEntry("A torrent in the list", "A torrent in the list", skippedWhy));
            for (var i = 0; i < result.Failed; i++) skipped.Add(new SkippedEntry("A torrent in the list", "A torrent in the list", failedWhy));
        }

        return result.Added;
    }

    /// <summary>Null when the Host takes the link; otherwise why not.</summary>
    private async Task<string?> AddLinkAsync(DownloadsModel downloads, JobListEntry entry, CancellationToken cancellationToken)
    {
        var folder = DestinationFor(entry);
        try
        {
            var reply = await downloads.AddAsync(Request(entry, folder), cancellationToken).ConfigureAwait(false);
            if (reply.Accepted) return null;
            // A folder from the list that the Host doesn't have: its own folder takes the file instead.
            if (_paired && string.IsNullOrWhiteSpace(ChosenFolder) && folder is not null)
            {
                var retry = await downloads.AddAsync(Request(entry, null), cancellationToken).ConfigureAwait(false);
                return retry.Accepted ? null : Words(retry.Error ?? reply.Error);
            }

            return Words(reply.Error);
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex) { return Words(ex.Message); }

        static string Words(string? text) => string.IsNullOrWhiteSpace(text) ? HostRefusedWords : text.Trim();
    }

    private string? DestinationFor(JobListEntry entry)
    {
        if (!string.IsNullOrWhiteSpace(ChosenFolder)) return ChosenFolder;
        return _paired && !string.IsNullOrWhiteSpace(entry.DestinationDirectory) ? entry.DestinationDirectory : null;
    }

    private static AddJobRequest Request(JobListEntry entry, string? folder) => new()
    {
        Url = entry.Url,
        FileName = entry.FileName,
        CategoryId = entry.CategoryId,
        DestinationDirectory = folder,
        RememberDestination = false,
        Description = entry.Description,
        QueueId = entry.QueueId,
        Connections = entry.RequestedConnections > 0 ? entry.RequestedConnections : null,
        StartMode = entry.StartMode,
        ConflictPolicy = entry.ConflictPolicy,
        ExpectedSha256 = entry.ExpectedSha256,
        ContentType = entry.ContentType,
        Source = "import",
    };

    private static JobListEntry Copy(JobListEntry e) => new()
    {
        Url = e.Url, FileName = e.FileName, CategoryId = e.CategoryId, DestinationDirectory = e.DestinationDirectory, Description = e.Description,
        QueueId = e.QueueId, RequestedConnections = e.RequestedConnections, StartMode = e.StartMode, ConflictPolicy = e.ConflictPolicy,
        ExpectedSha256 = e.ExpectedSha256, ContentType = e.ContentType, Torrent = e.Torrent,
    };
}

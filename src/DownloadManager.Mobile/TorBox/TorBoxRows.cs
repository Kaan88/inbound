using DownloadManager.Contracts;

namespace DownloadManager.Mobile.TorBox;

public enum TorBoxGroup { Ready, OnTorBox, Failed }

public enum TorBoxChip { All, Ready, OnTorBox, Failed }

public enum TorBoxTone { Done, Active, Waiting, Failed }

/// <summary>What the card's trailing control does: Get (choose files), the chevron to Downloads, or delete.</summary>
public enum TorBoxRowAction { None, Get, OnPhone, Delete }

/// <summary>Size and file count of an item on the account; a job carries neither.</summary>
public sealed record TorBoxItemInfo(long Size, int FileCount);

/// <summary>One card of the TorBox screen.</summary>
public sealed record TorBoxRow(
    Guid Id, string Name, string Word, TorBoxTone Tone, string Details, double? Progress, TorBoxGroup Group,
    TorBoxRowAction Action, bool IsTorrent, bool HasItem, DateTimeOffset CreatedAt)
{
    /// <summary>"Fetching · 37% · 30 files · 41.7 GB", as TalkBack reads it.</summary>
    public string Line => Details.Length == 0 ? Word : Word + " · " + Details;
}

/// <summary>The jobs of the TorBox provider as cards: their words, groups, counts and the chip filter. Plain logic, no views.</summary>
public static class TorBoxRows
{
    public static bool IsTorBox(RemoteJobDto job) =>
        string.Equals(job.Provider, TorBoxClient.ProviderId, StringComparison.OrdinalIgnoreCase) && job.State != "removed";

    public static string Key(string kind, string? itemId) => kind + "/" + itemId;

    /// <param name="where">Where the files go once chosen: "on this phone" or "on a host's name".</param>
    public static TorBoxRow From(RemoteJobDto job, TorBoxItemInfo? info, string where)
    {
        var torrent = job.Kind == "torrents";
        var name = !string.IsNullOrWhiteSpace(job.Name) ? job.Name! : !string.IsNullOrWhiteSpace(job.DisplayHost) ? job.DisplayHost! : "TorBox item";
        var hasItem = !string.IsNullOrEmpty(job.ItemId);
        var facts = Facts(job, info);

        if (job.State is "failed" or "blocked")
        {
            var why = TorBoxText.Failure(job.FailureMessage, job.FailureKind);
            return new TorBoxRow(job.Id, name, job.State == "failed" ? "Failed on TorBox" : "Blocked", TorBoxTone.Failed,
                string.IsNullOrWhiteSpace(job.FailureMessage) && job.FailureKind is null ? "" : why, null, TorBoxGroup.Failed,
                TorBoxRowAction.Delete, torrent, hasItem, job.CreatedAt);
        }

        if (job.State == "localStarted")
            return new TorBoxRow(job.Id, name, "Ready", TorBoxTone.Done, Join(facts, where), null, TorBoxGroup.Ready, TorBoxRowAction.OnPhone, torrent, hasItem, job.CreatedAt);

        if (job.State == "awaitingSelection")
            return new TorBoxRow(job.Id, name, "Ready", TorBoxTone.Done, Join(facts), null, TorBoxGroup.Ready, TorBoxRowAction.Get, torrent, hasItem, job.CreatedAt);

        if (job.Stage == "ready")
            return new TorBoxRow(job.Id, name, "Ready", TorBoxTone.Done, Join(facts), null, TorBoxGroup.Ready, TorBoxRowAction.None, torrent, hasItem, job.CreatedAt);

        if (job.ApprovalNeeded)
            return new TorBoxRow(job.Id, name, "Not sent", TorBoxTone.Waiting, Join(facts), null, TorBoxGroup.OnTorBox, TorBoxRowAction.None, torrent, hasItem, job.CreatedAt);

        var fetching = job.Stage == "processing" || job.Progress > 0;
        if (fetching)
        {
            var percent = (int)Math.Clamp(Math.Round(job.Progress * 100), 0, 100);
            return new TorBoxRow(job.Id, name, "Fetching", TorBoxTone.Active, Join([$"{percent}%", .. facts]), Math.Clamp(job.Progress, 0, 1),
                TorBoxGroup.OnTorBox, TorBoxRowAction.None, torrent, hasItem, job.CreatedAt);
        }

        return new TorBoxRow(job.Id, name, "Waiting", TorBoxTone.Waiting, Join(facts), null, TorBoxGroup.OnTorBox, TorBoxRowAction.None, torrent, hasItem, job.CreatedAt);
    }

    private static List<string> Facts(RemoteJobDto job, TorBoxItemInfo? info)
    {
        var facts = new List<string>();
        if (job.Kind == "webdl") facts.Add("web download");
        else if (job.Kind == "usenet") facts.Add("usenet");
        else if (info is { FileCount: > 0 }) facts.Add(info.FileCount == 1 ? "1 file" : $"{info.FileCount} files");
        if (info is { Size: > 0 }) facts.Add(Fmt.Bytes(info.Size));
        return facts;
    }

    private static string Join(IEnumerable<string> parts, string? last = null) =>
        string.Join(" · ", last is null ? parts : parts.Append(last));

    /// <summary>Fetching first, then ready, failed last; newest first within each.</summary>
    public static IReadOnlyList<TorBoxRow> Order(IEnumerable<TorBoxRow> rows) => rows
        .OrderBy(r => r.Group switch { TorBoxGroup.OnTorBox => 0, TorBoxGroup.Ready => 1, _ => 2 })
        .ThenByDescending(r => r.CreatedAt).ToList();

    public static int Count(IEnumerable<TorBoxRow> rows, TorBoxChip chip) => rows.Count(r => Matches(r, chip));

    /// <summary>"All 6", "Ready 4", "On TorBox 1", "Failed 1".</summary>
    public static string ChipLabel(IEnumerable<TorBoxRow> rows, TorBoxChip chip) => chip switch
    {
        TorBoxChip.Ready => "Ready " + Count(rows, chip),
        TorBoxChip.OnTorBox => "On TorBox " + Count(rows, chip),
        TorBoxChip.Failed => "Failed " + Count(rows, chip),
        _ => "All " + Count(rows, chip),
    };

    public static bool Matches(TorBoxRow row, TorBoxChip chip) => chip switch
    {
        TorBoxChip.Ready => row.Group == TorBoxGroup.Ready,
        TorBoxChip.OnTorBox => row.Group == TorBoxGroup.OnTorBox,
        TorBoxChip.Failed => row.Group == TorBoxGroup.Failed,
        _ => true,
    };

    public static IReadOnlyList<TorBoxRow> Filter(IEnumerable<TorBoxRow> rows, TorBoxChip chip, string? query)
    {
        var q = query?.Trim();
        return rows.Where(r => Matches(r, chip) && (string.IsNullOrEmpty(q) || r.Name.Contains(q, StringComparison.OrdinalIgnoreCase))).ToList();
    }
}

/// <summary>
/// The TorBox jobs the screen follows: the Host's list, then <c>remote.changed</c> pushes. Each push carries the whole job, so a missed
/// one can't leave a job wrong; a removed job goes.
/// </summary>
public sealed class TorBoxList
{
    private readonly Dictionary<Guid, RemoteJobDto> _jobs = [];
    private readonly Dictionary<string, TorBoxItemInfo> _info = new(StringComparer.Ordinal);

    public IReadOnlyCollection<RemoteJobDto> Jobs => _jobs.Values;

    public RemoteJobDto? Find(Guid id) => _jobs.GetValueOrDefault(id);

    public void Load(IEnumerable<RemoteJobDto> jobs)
    {
        _jobs.Clear();
        foreach (var job in jobs.Where(TorBoxRows.IsTorBox)) _jobs[job.Id] = job;
    }

    public void SetItems(IEnumerable<RemoteItemDto> items)
    {
        foreach (var item in items) _info[TorBoxRows.Key(item.Kind, item.ItemId)] = new TorBoxItemInfo(item.Size, item.FileCount);
    }

    /// <summary>Applies a push; false when it was not about a TorBox job (nothing changed).</summary>
    public bool Apply(RemoteChangedDto change)
    {
        if (change.Change == "job" && change.Job is { } job)
        {
            if (!string.Equals(job.Provider, TorBoxClient.ProviderId, StringComparison.OrdinalIgnoreCase)) return false;
            if (job.State == "removed") return _jobs.Remove(job.Id);
            _jobs[job.Id] = job;
            return true;
        }

        return change.Change == "removed" && change.JobId is { } id && _jobs.Remove(id);
    }

    /// <summary>The kinds present that item sizes can be asked for.</summary>
    public IReadOnlyList<string> KindsToDescribe() =>
        _jobs.Values.Where(j => !string.IsNullOrEmpty(j.ItemId) && j.Kind is "torrents" or "webdl").Select(j => j.Kind).Distinct().ToList();

    public IReadOnlyList<TorBoxRow> Rows(string where) => TorBoxRows.Order(_jobs.Values.Select(job =>
        TorBoxRows.From(job, job.ItemId is null ? null : _info.GetValueOrDefault(TorBoxRows.Key(job.Kind, job.ItemId)), where)));
}

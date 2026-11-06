using System.Globalization;
using DownloadManager.Contracts;
using DownloadManager.Core.Providers;

namespace DownloadManager.Desktop.WinUI;

internal enum RemoteRowTone
{
    Waiting,
    Failed,
    Neutral,
    Active,
    Done,
}

/// <summary>What the main list shows for a remote job. <see cref="Status"/> is a stage word and never a local figure.</summary>
internal sealed record RemoteRowView(
    string Name,
    string Status,
    string Detail,
    string ToolTip,
    RemoteRowTone Tone,
    string AccessibleName);

internal enum RemoteRowAction
{
    None,
    SelectFiles,
    Approve,
    EnterLinkPassword,
    Retry,
    CheckNow,
}

/// <summary>What the Host would take for a job right now, and what Enter or a double-click does.</summary>
internal sealed record RemoteRowActions(bool CheckNow, bool SelectFiles, bool Approve, bool EnterLinkPassword, bool Retry)
{
    public RemoteRowAction Primary =>
        SelectFiles ? RemoteRowAction.SelectFiles
        : Approve ? RemoteRowAction.Approve
        : EnterLinkPassword ? RemoteRowAction.EnterLinkPassword
        : Retry ? RemoteRowAction.Retry
        : CheckNow ? RemoteRowAction.CheckNow
        : RemoteRowAction.None;
}

/// <summary>What the My provider window shows for one item of the account.</summary>
internal sealed record RemoteItemView(
    string Name,
    string Type,
    string Status,
    string Size,
    string Files,
    string Added,
    string Expires,
    string Details,
    string Problem,
    RemoteRowTone Tone,
    bool CanDownload,
    string ToolTip,
    string AccessibleName);

/// <summary>Where a job's downloads should be: the paths are collected on the UI thread, and <see cref="Find"/> runs off it.</summary>
internal sealed class DownloadedPlaces(IEnumerable<string?> files, IEnumerable<string?> folders)
{
    private readonly List<string> _files = files.Where(path => !string.IsNullOrWhiteSpace(path)).Select(path => path!).ToList();
    private readonly List<string> _folders = folders.Where(path => !string.IsNullOrWhiteSpace(path)).Select(path => path!).ToList();

    /// <summary>A downloaded file, or the folder given when there is none yet. Null when neither is on disk. This can block for a long time on a slow or missing network share.</summary>
    public string? Find() => _files.FirstOrDefault(File.Exists) ?? _folders.FirstOrDefault(Directory.Exists);
}

/// <summary>Which jobs get a row, which account gets which entry point, and what each state allows. Nothing here touches the UI.</summary>
internal static class RemoteRules
{
    /// <summary>Once local downloads exist, those are the rows, and they are ordinary ones.</summary>
    public static bool IsRow(RemoteJobDto job) => job.State is not ("localStarted" or "removed");

    public static bool SameProvider(string? left, string? right) => string.Equals(left, right, StringComparison.OrdinalIgnoreCase);

    public static bool HasUsableKey(RemoteAccountDto account) => account is { HasKey: true, KeyUnreadable: false };

    public static bool CanBrowse(RemoteAccountDto account)
        => HasUsableKey(account) && account.ItemKinds.Count > 0 &&
           account.Capabilities.Contains("catalog", StringComparer.OrdinalIgnoreCase);

    public static RemoteRowActions Actions(RemoteJobDto job) => new(
        CheckNow: job.State is "intended" or "submissionUnknown" or "waiting" or "blocked" or "awaitingSelection",
        SelectFiles: job.State == "awaitingSelection",
        Approve: job.ApprovalNeeded && job.State == "intended",
        EnterLinkPassword: job.State == "failed" && job.FailureKind == "linkPasswordRequired",
        Retry: job.State is "failed" or "blocked");

    /// <summary>The job that covers this item, or a failed one if that is all there is. An add creates a new job only past a failed one, so callers check for that.</summary>
    public static RemoteJobDto? JobFor(IEnumerable<RemoteJobDto> jobs, RemoteItemDto item)
        => jobs.Where(job => job.ItemId == item.ItemId && job.Kind == item.Kind && SameProvider(job.Provider, item.Provider))
            .OrderBy(job => job.State == "failed")
            .ThenByDescending(job => job.UpdatedAt)
            .FirstOrDefault();

    /// <summary><c>remote.deleteRemote</c> takes a job, and refuses one whose item isn't known. An item with no job on the list can't be deleted from here.</summary>
    public static bool CanDeleteFromProvider(RemoteJobDto? job) => job is { ItemId.Length: > 0 };
}

internal static partial class RemoteText
{
    private const string Separator = " · ";

    public static string ProviderName(RemoteAccountDto account)
        => string.IsNullOrWhiteSpace(account.ProviderName) ? account.Provider : account.ProviderName;

    public static string ProviderName(RemoteJobDto job)
        => string.IsNullOrWhiteSpace(job.ProviderName) ? job.Provider : job.ProviderName;

    /// <summary>Binary multiples with the same labels as the download list. An unknown size stays blank.</summary>
    public static string Bytes(long bytes)
    {
        if (bytes <= 0) return string.Empty;
        string[] units = ["B", "KB", "MB", "GB", "TB"];
        double value = bytes;
        var index = 0;
        while (value >= 1024 && index < units.Length - 1)
        {
            value /= 1024;
            index++;
        }

        return index == 0
            ? $"{bytes} {units[index]}"
            : string.Create(CultureInfo.CurrentCulture, $"{value:0.0} {units[index]}");
    }

    public static string KindSingular(string? kind) => kind switch
    {
        "torrents" => "Torrent",
        "webdl" => "Web download",
        "usenet" => "Usenet",
        _ => kind ?? string.Empty,
    };

    public static string KindHeading(string? kind) => kind switch
    {
        "torrents" => "Torrents",
        "webdl" => "Web Downloads",
        "usenet" => "Usenet",
        _ => kind ?? string.Empty,
    };

    private static string KindEmptyWords(string? kind) => kind switch
    {
        "torrents" => "torrents",
        "webdl" => "web downloads",
        "usenet" => "Usenet items",
        _ => "items",
    };

    public static string EmptyKind(string? kind, string providerName)
        => $"There are no {KindEmptyWords(kind)} on your {providerName} account.";

    // ---- Remote jobs ----------------------------------------------------------------------------

    public static RemoteRowView JobRow(RemoteJobDto job, DateTimeOffset now, IFormatProvider? culture = null)
    {
        var format = culture ?? CultureInfo.CurrentCulture;
        var provider = ProviderName(job);
        var name = JobName(job);
        var status = ShortStatus(job, provider);
        var parts = new List<string>();
        var cause = FailureCause(job);
        if (cause.Length > 0) parts.Add(cause);
        if (ProviderProgress(job.Progress, job.SpeedBytesPerSecond, job.EtaSeconds, provider, job.State == "waiting") is { Length: > 0 } progress) parts.Add(progress);
        if (job.ApprovalNeeded && job.State == "intended") parts.Add($"Approve it to have {provider} fetch it.");
        if (job.State == "awaitingSelection") parts.Add("Choose the files.");
        if (NextCheck(job, now, format) is { Length: > 0 } next) parts.Add(next);
        var detail = string.Join(Separator, parts);

        var tip = new List<string> { name, $"{provider}: {Clean(job.Status, status)}" };
        tip.AddRange(parts.Where(part => part != job.Status));
        if (!string.IsNullOrWhiteSpace(job.RawState)) tip.Add($"{provider}'s own word for it: {UrlRedactor.HideAddresses(job.RawState)}");
        tip.AddRange(job.Notes.Where(note => !string.IsNullOrWhiteSpace(note)).Take(5).Select(UrlRedactor.HideAddresses));

        return new RemoteRowView(
            name,
            status,
            detail,
            string.Join(Environment.NewLine, tip),
            job.State switch
            {
                "failed" => RemoteRowTone.Failed,
                "waiting" or "blocked" or "awaitingSelection" or "intended" or "submissionUnknown" or "submitting" => RemoteRowTone.Waiting,
                _ => RemoteRowTone.Neutral,
            },
            detail.Length > 0 ? $"{name}, on {provider}, {status}, {detail}" : $"{name}, on {provider}, {status}");
    }

    private static string JobName(RemoteJobDto job)
    {
        if (!string.IsNullOrWhiteSpace(job.Name)) return UrlRedactor.HideAddresses(job.Name);
        if (!string.IsNullOrWhiteSpace(job.DisplayHost)) return UrlRedactor.HideAddresses(job.DisplayHost);
        return $"{KindSingular(job.Kind)} on {ProviderName(job)}";
    }

    /// <summary>The Host's words are the longer form. This one fits the 140px column, with a failure's cause beside it.</summary>
    private static string ShortStatus(RemoteJobDto job, string provider) => job.State switch
    {
        "failed" => job.FailureKind == "linkPasswordRequired" ? "Needs the link's password" : $"Failed on {provider}",
        "blocked" => $"{provider} account busy",
        "awaitingSelection" => "Ready to download",
        "intended" when job.ApprovalNeeded => $"Not cached on {provider}",
        "intended" when job.Cached => $"Cached on {provider}",
        "intended" => $"Checking {provider}…",
        "submitting" => $"Adding to {provider}…",
        "submissionUnknown" => $"Checking {provider}…",
        "waiting" => job.Stage switch
        {
            "queued" => $"Waiting for {provider}",
            "metadataPending" => $"Waiting for {provider}",
            "processing" => $"Processing on {provider}",
            "stalled" => $"Stalled on {provider}",
            "ready" => "Ready to download",
            _ => $"Waiting for {provider}",
        },
        _ => Clean(job.Status, $"Waiting for {provider}"),
    };

    private static string FailureCause(RemoteJobDto job)
    {
        if (!string.IsNullOrWhiteSpace(job.FailureMessage)) return Failure(job.FailureMessage, job.FailureKind, ProviderName(job));
        return job.State is "failed" || job.FailureKind is not null
            ? Failure(null, job.FailureKind, ProviderName(job))
            : job.State == "blocked" ? Clean(job.Status, string.Empty) : string.Empty;
    }

    /// <summary>The provider's own progress, named as such. It says nothing about bytes reaching this computer.</summary>
    public static string ProviderProgress(double progress, long speed, long? etaSeconds, string provider, bool active = true)
    {
        if (!active) return string.Empty;
        var parts = new List<string>();
        if (progress > 0 && progress < 1) parts.Add(string.Create(CultureInfo.CurrentCulture, $"{progress * 100:0}%"));
        if (speed > 0) parts.Add($"{Bytes(speed)}/s");
        if (etaSeconds is > 0 and var eta) parts.Add($"about {Duration(eta)} left");
        return parts.Count == 0 ? string.Empty : $"{provider} reports {string.Join(", ", parts)}";
    }

    private static string Duration(long seconds)
    {
        var span = TimeSpan.FromSeconds(seconds);
        if (span.TotalHours >= 1) return $"{(int)span.TotalHours}h {span.Minutes}m";
        return span.TotalMinutes >= 1 ? $"{(int)span.TotalMinutes}m {span.Seconds}s" : $"{span.Seconds}s";
    }

    private static string NextCheck(RemoteJobDto job, DateTimeOffset now, IFormatProvider culture)
    {
        if (job.NextCheckAt is not { } next || job.State is not ("intended" or "submissionUnknown" or "waiting" or "blocked")) return string.Empty;
        if (job.ApprovalNeeded) return string.Empty;
        if (next <= now) return "Checking soon";
        var local = next.ToLocalTime();
        return local.Date == now.ToLocalTime().Date
            ? $"Next check {local.ToString("HH:mm:ss", culture)}"
            : $"Next check {local.ToString("MMM dd HH:mm", culture)}";
    }

    private static string Clean(string? words, string fallback)
        => string.IsNullOrWhiteSpace(words) ? fallback : UrlRedactor.HideAddresses(words);

    // ---- Items on the account -------------------------------------------------------------------

    /// <param name="job">The job that already covers this item, if any: its words are newer than the list's.</param>
    /// <param name="localStatus">How its local downloads are doing, read just now, if they exist.</param>
    public static RemoteItemView Item(RemoteItemDto item, string provider, RemoteJobDto? job, string? localStatus = null, IFormatProvider? culture = null)
    {
        var format = culture ?? CultureInfo.CurrentCulture;
        var name = string.IsNullOrWhiteSpace(item.Name) ? $"{KindSingular(item.Kind)} {item.ItemId}" : UrlRedactor.HideAddresses(item.Name);
        var type = KindSingular(item.Kind);
        var failed = item.Stage == "failed" || !string.IsNullOrWhiteSpace(item.Error);
        var status = job is null || job.State == "failed" ? ItemStatus(item, provider, failed) : LinkedStatus(job, provider, localStatus);
        var size = Bytes(item.Size);
        var files = item.FileCount > 0 ? item.FileCount.ToString(format) : string.Empty;
        var added = Day(item.CreatedAt, format);
        var expires = Day(item.ExpiresAt, format);

        var problem = failed
            ? Failure(item.Error, null, provider)
            : job is { State: "failed" } ? FailureCause(job) : string.Empty;

        var details = new List<string> { type };
        if (size.Length > 0) details.Add(size);
        if (item.FileCount > 0) details.Add(item.FileCount == 1 ? "1 file" : $"{files} files");
        if (added.Length > 0) details.Add($"Added {added}");
        if (Day(item.UpdatedAt, format) is { Length: > 0 } updated) details.Add($"Updated {updated}");
        if (expires.Length > 0) details.Add($"Expires {expires}");
        if (ProviderProgress(item.Progress, item.SpeedBytesPerSecond, item.EtaSeconds, provider, item.Stage is "queued" or "processing" or "stalled" or "metadataPending") is { Length: > 0 } progress)
        {
            details.Add(progress);
        }

        if (!string.IsNullOrWhiteSpace(item.RawState) && item.Stage == "unknown")
        {
            details.Add($"{provider}'s own word for it: {UrlRedactor.HideAddresses(item.RawState)}");
        }

        var line = string.Join(Separator, details);
        var spoken = string.Join(", ", new[] { name, type, status, size, item.FileCount == 1 ? "1 file" : item.FileCount > 0 ? $"{files} files" : string.Empty, added.Length > 0 ? $"added {added}" : string.Empty, expires.Length > 0 ? $"expires {expires}" : string.Empty }.Where(part => part.Length > 0));
        return new RemoteItemView(
            name,
            type,
            status,
            size,
            files,
            added,
            expires,
            line,
            problem,
            ItemTone(item, job, status, failed),
            CanDownload: !failed,
            ToolTip: problem.Length > 0 ? $"{name}{Environment.NewLine}{problem}" : name,
            AccessibleName: spoken);
    }

    /// <summary>The same colors as the download list: blue while bytes are coming, green when done, amber while waiting.</summary>
    private static RemoteRowTone ItemTone(RemoteItemDto item, RemoteJobDto? job, string status, bool failed)
    {
        if (failed || job is { State: "failed" }) return RemoteRowTone.Failed;
        if (job is { State: "localStarted" })
        {
            return status == "Completed" ? RemoteRowTone.Done
                : status.Contains("failed", StringComparison.Ordinal) ? RemoteRowTone.Failed
                : status.StartsWith("Downloading", StringComparison.Ordinal) ? RemoteRowTone.Active
                : RemoteRowTone.Waiting;
        }

        return job is not null || item.Stage != "ready" ? RemoteRowTone.Waiting : RemoteRowTone.Neutral;
    }

    private static string LinkedStatus(RemoteJobDto job, string provider, string? localStatus)
        => job.State == "localStarted" ? localStatus ?? Clean(job.Status, "Downloading locally") : ShortStatus(job, provider);

    /// <summary>How a package's local downloads are doing, in the Host's words for it. The Host only works this out when a remote job changes.</summary>
    public static string? LocalWords(IReadOnlyCollection<JobDto> files)
    {
        if (files.Count == 0) return null;
        var completed = files.Count(file => file.State == "completed");
        var failed = files.Count(file => file.State == "failed");
        var paused = files.Count(file => file.State == "paused");
        if (completed == files.Count) return "Completed";
        if (failed > 0) return $"{failed} of {files.Count} local downloads failed";
        if (files.Any(file => file.State is "connecting" or "downloading" or "verifying" or "assembling" or "retryWaiting"))
        {
            return files.Count == 1 ? "Downloading locally" : $"Downloading locally, {completed} of {files.Count} done";
        }

        return paused == files.Count - completed ? "Paused locally" : "Waiting to download locally";
    }

    private static string ItemStatus(RemoteItemDto item, string provider, bool failed)
    {
        if (failed) return $"Failed on {provider}";
        return item.Stage switch
        {
            "ready" => item.Cached ? $"Cached on {provider}" : "Ready to download",
            "queued" => $"Waiting for {provider}",
            "metadataPending" => $"Waiting for {provider} to list the files",
            "processing" => $"Processing on {provider}",
            "stalled" => $"Stalled on {provider}",
            _ when !string.IsNullOrWhiteSpace(item.RawState) => $"{provider} reports: {UrlRedactor.HideAddresses(item.RawState)}",
            _ => $"Waiting for {provider}",
        };
    }

    private static string Day(DateTimeOffset? value, IFormatProvider culture)
        => value is { } day && day > DateTimeOffset.MinValue ? day.ToLocalTime().ToString("MMM dd yyyy", culture) : string.Empty;

    // ---- Dialogs --------------------------------------------------------------------------------

    public static string ApproveQuestion(RemoteJobDto job)
    {
        var provider = ProviderName(job);
        return $"'{JobName(job)}' isn't cached on {provider}. Approving starts work on {provider}: it fetches this on your account, which may count against your {provider} limits. Downloading to this computer starts only when {provider} has it.";
    }

    public static string RemoveQuestion(IReadOnlyList<RemoteJobDto> jobs)
    {
        var providers = jobs.Select(ProviderName).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        var provider = providers.Count == 1 ? providers[0] : "the provider";
        var what = jobs.Count == 1 ? $"'{JobName(jobs[0])}'" : $"these {jobs.Count} items";
        return $"Take {what} off this list? This doesn't touch {provider}: anything on your account stays there, and this app just stops watching it.";
    }

    public static string DeleteQuestion(string itemName, string provider)
        => $"Delete '{itemName}' from your {provider} account? This can't be undone. Files already on this computer are kept.";

    public static string LinkPasswordHint(RemoteJobDto job)
        => $"'{JobName(job)}' needs the password its source site set for the link. This isn't an archive password. It is kept encrypted.";
}

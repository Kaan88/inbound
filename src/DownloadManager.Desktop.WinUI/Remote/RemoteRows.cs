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

/// <summary>The order of My provider's list by a column. A provider gives no size or date for some items, and those stay at the end.</summary>
internal static class RemoteItemSort
{
    /// <summary>
    /// A column's order means little over part of a list: with fifty of a hundred and fifty items read, "most files" could only
    /// say so of those fifty. A page that came back empty ends it even if the provider says there is more.
    /// </summary>
    public static bool NeedsTheRest(string? sortKey, bool hasMore, int lastPageCount) => sortKey is not null && hasMore && lastPageCount > 0;

    public static IEnumerable<T> Order<T>(IEnumerable<T> rows, Func<T, RemoteItemDto> item, Func<T, RemoteItemView> view, string key, bool ascending) => key switch
    {
        "type" => ListSort.Order(rows, row => view(row).Type, NaturalStringComparer.Instance, ascending),
        "status" => ListSort.Order(rows, row => view(row).Status, NaturalStringComparer.Instance, ascending),
        "size" => ListSort.Order(rows, row => item(row).Size, null, ascending, row => item(row).Size <= 0),
        "files" => ListSort.Order(rows, row => item(row).FileCount, null, ascending, row => item(row).FileCount <= 0),
        "added" => ListSort.Order(rows, row => item(row).CreatedAt, null, ascending, row => item(row).CreatedAt is null),
        "expires" => ListSort.Order(rows, row => item(row).ExpiresAt, null, ascending, row => item(row).ExpiresAt is null),
        _ => ListSort.Order(rows, row => view(row).Name, NaturalStringComparer.Instance, ascending),
    };
}

/// <summary>Which jobs get a row, which account gets which entry point, and what each state allows. Nothing here touches the UI.</summary>
internal static class RemoteRules
{
    /// <summary>Once local downloads exist, those are the rows, and they are ordinary ones.</summary>
    public static bool IsRow(RemoteJobDto job) => job.State is not ("localStarted" or "removed");

    public static bool SameProvider(string? left, string? right) => string.Equals(left, right, StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// Whether Download in My provider has done its job, so the window can close: the Host took the item as a job
    /// that is on its way. A refusal, a job that failed or was blocked at once, or a note that something asked for
    /// didn't happen keeps the window open with the reason.
    /// </summary>
    public static bool DownloadStarted(RemoteAddResultDto result)
        => result is { Ok: true, Job: { } job } && string.IsNullOrWhiteSpace(result.Note) && job.State is not ("failed" or "blocked" or "removed");

    public static bool HasUsableKey(RemoteAccountDto account) => account is { HasKey: true, KeyUnreadable: false };

    /// <summary>
    /// Whether adding has to ask the Host about each input. Only a Host with no remote support, or an
    /// account read that worked and found no usable key, lets a link skip that.
    /// </summary>
    public static bool ProviderAddApplies(RemoteState state)
        => !state.AccountsKnown || (state.RemoteSupported && state.Accounts.Any(HasUsableKey));

    public static bool CanBrowse(RemoteAccountDto account)
        => HasUsableKey(account) && account.ItemKinds.Count > 0 &&
           account.Capabilities.Contains("catalog", StringComparer.OrdinalIgnoreCase);

    public static RemoteRowActions Actions(RemoteJobDto job) => new(
        // A look at a held copied link could submit it, so the Host refuses, and there is nothing to offer.
        CheckNow: !job.HeldForApproval && job.State is "intended" or "submissionUnknown" or "waiting" or "blocked" or "awaitingSelection",
        SelectFiles: job.State == "awaitingSelection" || CanChooseEarly(job),
        Approve: job.ApprovalNeeded && job.State == "intended",
        EnterLinkPassword: job.State == "failed" && job.FailureKind == "linkPasswordRequired",
        Retry: job.State is "failed" or "blocked");

    /// <summary>
    /// What Enter does with these rows selected: one row's own next step, or, for several, the one thing they all wait
    /// for when that is being added (copied links, sent together) or a file choice (one dialog after another). Several
    /// rows waiting for different things are left alone.
    /// </summary>
    public static RemoteRowAction EnterFor(IReadOnlyList<RemoteJobDto> jobs) => jobs switch
    {
        [] => RemoteRowAction.None,
        [var one] => Actions(one).Primary,
        _ when AllCan(jobs, actions => actions.Approve) => RemoteRowAction.Approve,
        _ when AllCan(jobs, actions => actions.SelectFiles) => RemoteRowAction.SelectFiles,
        _ => RemoteRowAction.None,
    };

    /// <summary>Whether every one of these rows takes this, so one menu item can stand for all of them.</summary>
    public static bool AllCan(IReadOnlyList<RemoteJobDto> jobs, Func<RemoteRowActions, bool> takes) => jobs.Count > 0 && jobs.All(job => takes(Actions(job)));

    /// <summary>The rows "Download All Files" covers: those waiting for a file choice. Each takes all of its files.</summary>
    public static IReadOnlyList<RemoteJobDto> TakingAllFiles(IReadOnlyList<RemoteJobDto> jobs) => jobs.Where(job => job.State == "awaitingSelection").ToList();

    /// <summary>"Download All Files" is for several rows at once. One row's own file dialog does the same for it.</summary>
    public static bool OffersDownloadAll(IReadOnlyList<RemoteJobDto> jobs) => TakingAllFiles(jobs).Count >= 2;

    /// <summary>
    /// Several rows' file choices, one after another in the order given. Each row is read again just before its turn,
    /// so one dealt with meanwhile is passed over. A choice left unanswered, such as Cancel, ends the walk, and the
    /// rows after it go on waiting.
    /// </summary>
    /// <param name="current">The row's job as it is now. Null once it has left the list.</param>
    /// <param name="choose">False when nothing was answered.</param>
    public static async Task WalkFileChoicesAsync(IReadOnlyList<Guid> order, Func<Guid, RemoteJobDto?> current, Func<RemoteJobDto, Task<bool>> choose)
    {
        foreach (var id in order)
        {
            if (current(id) is not { } job || !Actions(job).SelectFiles) continue;
            if (!await choose(job)) return;
        }
    }

    /// <summary>A selection comes in the order of clicking. A walk goes down the list instead, with anything not in it last.</summary>
    public static IReadOnlyList<Guid> InListOrder(IReadOnlyList<RemoteJobDto> jobs, IReadOnlyList<Guid> list)
    {
        var position = new Dictionary<Guid, int>();
        for (var i = 0; i < list.Count; i++) position.TryAdd(list[i], i);
        return jobs.Select(job => job.Id).OrderBy(id => position.TryGetValue(id, out var index) ? index : int.MaxValue).ToList();
    }

    /// <summary>
    /// The files can be chosen before the provider is done: the job would ask for them, and nothing holds it back. A
    /// copied link isn't added yet, and a folder link asks by default, so a held one is left out.
    /// </summary>
    public static bool CanChooseEarly(RemoteJobDto job)
        => job is { HeldForApproval: false, ApprovalNeeded: false, SelectionMode: "askWhenReady" or "askIfCached", State: "intended" or "submitting" or "submissionUnknown" or "waiting" };

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
        if (job.ApprovalNeeded && job.State == "intended") parts.Add(job.HeldForApproval ? $"Enter adds it to {provider}." : $"Enter starts it on {provider}.");
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
        "awaitingSelection" => "Choose files",
        "intended" when job.HeldForApproval => "Copied link",
        // Nothing was sent: from the command line without its go-ahead, or saved before adding was the decision.
        "intended" when job.ApprovalNeeded => "Not started",
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

    /// <summary>The menu item and its message's title for a row nothing has been sent for yet.</summary>
    public static string AddTo(RemoteJobDto job) => AddTo([job]);

    /// <summary>The same for several rows, named for their provider when they share one.</summary>
    public static string AddTo(IReadOnlyList<RemoteJobDto> jobs)
    {
        var providers = jobs.Select(ProviderName).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        return providers.Count == 1 ? $"Add to {providers[0]}" : "Add";
    }

    /// <summary>The file dialog's way past a list the provider doesn't have yet, for a row that would ask.</summary>
    public const string DownloadAllWhenReady = "Download all when ready";

    /// <summary>
    /// Said in passing after a choice made before the provider is done. The row can't show it: an early "all" looks the
    /// same as a hoster link's own.
    /// </summary>
    public static string EarlyChoice(bool all) => all ? "All files will download when ready." : "Your choice will download when ready.";

    /// <summary>The menu item that takes every file of several rows waiting for a choice.</summary>
    public const string DownloadAllFiles = "Download All Files";

    /// <summary>Said in passing once "Download All Files" was taken, for how many rows the Host took it.</summary>
    public static string AllFilesTaken(int rows) => rows == 1 ? "All its files will download." : $"All files of {rows} items will download.";

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

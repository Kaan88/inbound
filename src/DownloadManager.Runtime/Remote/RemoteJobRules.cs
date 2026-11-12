using DownloadManager.Core;
using DownloadManager.Core.Providers;

namespace DownloadManager.Runtime.Remote;

/// <summary>What to do when a submission was refused or got no answer.</summary>
public enum RemoteSubmitOutcome
{
    /// <summary>It may have been accepted. Only the provider's list can say.</summary>
    SubmissionUnknown,
    /// <summary>Definitely not accepted, and worth trying again by itself after a wait.</summary>
    TryAgainLater,
    Blocked,
    NeedsApproval,
    Failed,
    /// <summary>As failed, and nothing new is created on this account until the user retries.</summary>
    FailedAndStopCreating,
}

public enum RemoteItemOutcome
{
    KeepWaiting,
    Ready,
    Failed,
}

/// <summary>The state table and the timing, apart from any I/O so each rule can be tested on its own.</summary>
public static class RemoteJobRules
{
    public static readonly TimeSpan RegistrationGrace = TimeSpan.FromMinutes(1);
    public static readonly TimeSpan LongestBackoff = TimeSpan.FromMinutes(15);
    public static readonly TimeSpan LongestIdlePoll = TimeSpan.FromMinutes(5);
    /// <summary>
    /// The most a rate limit holds an account, whatever it asked for. One bad header, or a date far off,
    /// must not freeze an account across restarts with nothing able to lift it. If the limit is real
    /// the next call simply gets told again.
    /// </summary>
    public static readonly TimeSpan LongestRateLimitHold = TimeSpan.FromHours(1);
    private static readonly TimeSpan DefaultRateLimitHold = TimeSpan.FromMinutes(1);
    private static readonly TimeSpan FirstBackoff = TimeSpan.FromSeconds(30);
    private const int UnchangedPollsPerSlowdown = 10;

    /// <summary>How long a rate limit holds the account, given what it asked for, if anything.</summary>
    public static TimeSpan RateLimitHold(TimeSpan? asked)
    {
        if (asked is not { } wait || wait <= TimeSpan.Zero)
        {
            return DefaultRateLimitHold;
        }

        return wait < LongestRateLimitHold ? wait : LongestRateLimitHold;
    }

    /// <summary>What is wrong with an archive password, without repeating any of it. Null when nothing is.</summary>
    public static string? DescribeBadArchivePassword(string? password)
        => password is not null && password.AsSpan().IndexOfAny('\r', '\n', '\0') >= 0
            ? "An archive password cannot contain a line break."
            : null;

    /// <summary>Whether the provider has said this site's daily limit is reached. Zero means no limit.</summary>
    public static bool DailyLimitUsedUp(RemoteHoster hoster)
        => (hoster.DailyLinkLimit is > 0 and var links && hoster.DailyLinksUsed >= links) ||
           (hoster.DailyBandwidthLimit is > 0 and var bytes && hoster.DailyBandwidthUsed >= bytes);

    /// <summary>The states the account's loop looks at. The others wait for the user, or are finished.</summary>
    public static bool IsPolled(RemoteJobState state)
        => state is RemoteJobState.Intended or RemoteJobState.SubmissionUnknown or RemoteJobState.Waiting or RemoteJobState.Blocked;

    public static bool IsTerminal(RemoteJobState state)
        => state is RemoteJobState.LocalStarted or RemoteJobState.Failed or RemoteJobState.Removed;

    /// <summary>A record found in Submitting may or may not have reached the provider.</summary>
    public static RemoteJobState AfterRestart(RemoteJobState state)
        => state == RemoteJobState.Submitting ? RemoteJobState.SubmissionUnknown : state;

    public static RemoteSubmitOutcome AfterSubmitFailure(RemoteFailureKind kind) => kind switch
    {
        // No clear refusal: the provider could have done the work and lost only the answer.
        RemoteFailureKind.Network or RemoteFailureKind.BadResponse or RemoteFailureKind.ServiceError or RemoteFailureKind.AlreadyExists
            => RemoteSubmitOutcome.SubmissionUnknown,
        RemoteFailureKind.RateLimited => RemoteSubmitOutcome.TryAgainLater,
        RemoteFailureKind.AccountBusy => RemoteSubmitOutcome.Blocked,
        RemoteFailureKind.NotCached => RemoteSubmitOutcome.NeedsApproval,
        RemoteFailureKind.AccountLimit => RemoteSubmitOutcome.FailedAndStopCreating,
        RemoteFailureKind.Rejected or RemoteFailureKind.NotFound or RemoteFailureKind.LinkPasswordRequired or RemoteFailureKind.Authentication
            => RemoteSubmitOutcome.Failed,
        _ => RemoteSubmitOutcome.SubmissionUnknown,
    };

    /// <summary>An unknown stage stays visible as itself and never counts as ready.</summary>
    public static RemoteItemOutcome AfterItemRead(RemoteStage stage) => stage switch
    {
        RemoteStage.Ready => RemoteItemOutcome.Ready,
        RemoteStage.Failed => RemoteItemOutcome.Failed,
        _ => RemoteItemOutcome.KeepWaiting,
    };

    /// <summary>The provider's eta when it gives one, kept within its polling bounds, and slower while nothing at all is changing.</summary>
    public static TimeSpan NextPoll(long? etaSeconds, int unchangedPolls = 0, RemoteTransferPolicy? policy = null)
    {
        var bounds = policy ?? new RemoteTransferPolicy();
        var poll = etaSeconds is { } eta
            ? TimeSpan.FromSeconds(Math.Clamp(eta, (long)bounds.ShortestPoll.TotalSeconds, (long)bounds.LongestPoll.TotalSeconds))
            : bounds.DefaultPoll;
        var slowdowns = Math.Min(unchangedPolls / UnchangedPollsPerSlowdown, 8);
        var slowed = poll * (1L << slowdowns);
        return slowed < LongestIdlePoll || slowdowns == 0 ? slowed : LongestIdlePoll;
    }

    /// <param name="failures">How many in a row, counting this one.</param>
    /// <param name="jitter">0 to 1. Spreads the retries of several jobs by up to a fifth either way.</param>
    public static TimeSpan Backoff(int failures, double jitter)
    {
        var doubled = FirstBackoff * Math.Pow(2, Math.Clamp(failures, 1, 16) - 1);
        var capped = doubled < LongestBackoff ? doubled : LongestBackoff;
        var spread = capped * (0.8 + (0.4 * Math.Clamp(jitter, 0, 1)));
        return spread < LongestBackoff ? spread : LongestBackoff;
    }

    /// <summary>A choice left to the cache, once it is known: asked if it was cached, and all of it otherwise. Any other choice stays.</summary>
    public static RemoteSelectionMode DecideSelection(RemoteSelectionMode mode, bool cached)
        => mode == RemoteSelectionMode.AskIfCached ? (cached ? RemoteSelectionMode.AskWhenReady : RemoteSelectionMode.All) : mode;

    /// <summary>Some of a choice made before the provider listed the files isn't among them, so nothing was started.</summary>
    public static string ChosenFilesMissing(int missing, string providerName)
        => missing == 1
            ? $"1 of the chosen files isn't in {providerName}'s list. Choose again."
            : $"{missing} of the chosen files aren't in {providerName}'s list. Choose again.";

    public static bool SameHash(RemoteItem item, string hash)
        => string.Equals(item.Hash, hash, StringComparison.OrdinalIgnoreCase) ||
           item.AlternativeHashes.Contains(hash, StringComparer.OrdinalIgnoreCase);

    /// <summary>What the local jobs of a package are doing right now. No record holds this: it is read when asked.</summary>
    private static string LocalWords(RemoteLocalSummary? local)
    {
        // Saved only once the downloads exist, so none left means they were taken off the list since.
        if (local is not { Total: > 0 } work)
        {
            return "Its downloads are no longer on the list";
        }

        if (work.Completed == work.Total)
        {
            return "Completed";
        }

        if (work.Failed > 0)
        {
            return $"{work.Failed} of {work.Total} local downloads failed";
        }

        if (work.Active > 0)
        {
            return "Downloading locally";
        }

        return work.Paused == work.Total - work.Completed ? "Stopped locally" : "Waiting to download locally";
    }

    /// <summary>The words for a job. Up to the local jobs these are about the provider's side, not local progress; after that, the package's state.</summary>
    public static string StatusText(RemoteJobRecord record, string providerName, RemoteLocalSummary? local = null) => record.State switch
    {
        RemoteJobState.Intended when record.HeldForApproval =>
            $"Copied from the clipboard. Nothing has been sent to {providerName}.",
        RemoteJobState.Intended when record.ApprovalNeeded =>
            $"Not cached on {providerName}, so it wasn't started.",
        RemoteJobState.Intended when record.FailureMessage is { } problem => problem,
        RemoteJobState.Intended when record.Cached => $"Cached on {providerName}",
        RemoteJobState.Intended => $"Checking {providerName}...",
        RemoteJobState.Submitting => $"Adding to {providerName}...",
        RemoteJobState.SubmissionUnknown => $"Checking whether {providerName} received it...",
        RemoteJobState.Waiting when record.FailureMessage is { } problem => problem,
        RemoteJobState.Waiting => record.Stage switch
        {
            RemoteStage.Queued => $"Added to {providerName} queue",
            RemoteStage.MetadataPending => $"Waiting for {providerName} to list the files",
            RemoteStage.Processing => record.Progress > 0
                ? $"Processing on {providerName} ({record.Progress * 100:0}%)"
                : $"Processing on {providerName}",
            RemoteStage.Stalled => $"Stalled on {providerName}",
            RemoteStage.Ready => "Ready to download",
            RemoteStage.Unknown when !string.IsNullOrEmpty(record.RawState) => $"{providerName} reports: {record.RawState}",
            _ => $"Waiting for {providerName}",
        },
        RemoteJobState.AwaitingSelection when record.FailureMessage is { } problem => problem,
        RemoteJobState.AwaitingSelection => "Ready to download. Choose the files.",
        RemoteJobState.LocalStarted => LocalWords(local),
        RemoteJobState.Blocked => record.FailureMessage ?? $"Your {providerName} account is busy. This will be tried again by itself.",
        RemoteJobState.Failed => record.FailureMessage ?? $"{providerName} could not process this.",
        RemoteJobState.Removed => "Removed",
        _ => record.State.ToString(),
    };
}

/// <summary>Turns a ready item into local job requests, leaving out what shouldn't be downloaded and saying why.</summary>
public static class RemoteChildPlanner
{
    private const int MostNotes = 50;
    private const int LongestPathInNote = 300;

    public sealed record Plan(IReadOnlyList<RemoteJobRequest> Requests, IReadOnlyList<string> Notes);

    /// <summary>Why a file can't become a download. The list a user chooses from and the plan use this one rule.</summary>
    public enum Unusable
    {
        None,
        UnsafePath,
        Infected,
        Empty,
    }

    /// <summary>Whether a file could be downloaded, with the safe relative path it would have.</summary>
    public static Unusable Check(RemoteFile file, out string? relative)
    {
        relative = RemotePaths.ToSafeRelativePath(file.Path);
        return relative is null ? Unusable.UnsafePath
            : file.Infected ? Unusable.Infected
            : file.Size <= 0 ? Unusable.Empty
            : Unusable.None;
    }

    /// <summary>
    /// How many files a plan with no choice would take: the first of each id, when it can be downloaded.
    /// A choice is only worth asking for when this is more than one.
    /// </summary>
    public static int UsableCount(IEnumerable<RemoteFile> files)
    {
        var seen = new HashSet<string>(StringComparer.Ordinal);
        return files.Count(file => seen.Add(file.FileId) && Check(file, out _) == Unusable.None);
    }

    /// <summary>
    /// A choice against the files the provider lists: the ids of everything chosen, by id or found again by path and
    /// size, the paths that weren't found, and how much of the choice is missing either way.
    /// </summary>
    public static (IReadOnlyList<string> Ids, IReadOnlyList<RemoteFileKey> Unfound, int Missing) MatchChoice(
        IReadOnlyList<string> fileIds,
        IReadOnlyList<RemoteFileKey> paths,
        IReadOnlyList<RemoteFile> files)
    {
        var present = files.Select(file => file.FileId).ToHashSet(StringComparer.Ordinal);
        var missing = fileIds.Count(id => !present.Contains(id));
        if (paths.Count == 0)
        {
            return (fileIds, paths, missing);
        }

        var byKey = new Dictionary<RemoteFileKey, string>();
        foreach (var file in files)
        {
            if (RemoteFileKey.Of(file.Path, file.Size) is { } key)
            {
                byKey.TryAdd(key, file.FileId);
            }
        }

        var ids = fileIds.ToList();
        var taken = ids.ToHashSet(StringComparer.Ordinal);
        var unfound = new List<RemoteFileKey>();
        foreach (var path in paths)
        {
            if (!byKey.TryGetValue(path, out var id))
            {
                unfound.Add(path);
            }
            else if (taken.Add(id))
            {
                ids.Add(id);
            }
        }

        // The same lists when nothing was found, so a record that a look didn't change compares equal and isn't saved again.
        return (ids.Count == fileIds.Count ? fileIds : ids, unfound.Count == paths.Count ? paths : unfound, missing + unfound.Count);
    }

    public static Plan Build(RemoteJobRecord record, RemoteItem item, string providerName, int maxConnections, string destination)
    {
        var requests = new List<RemoteJobRequest>();
        var notes = new List<string>();
        var chosen = record.SelectionMode == RemoteSelectionMode.Chosen ? record.SelectedFileIds.ToHashSet(StringComparer.Ordinal) : null;
        var seen = new HashSet<string>(StringComparer.Ordinal);
        var onlyFile = item.Files.Select(file => file.FileId).Distinct(StringComparer.Ordinal).Count() == 1;
        foreach (var file in item.Files)
        {
            if (!seen.Add(file.FileId) || chosen?.Contains(file.FileId) == false)
            {
                continue;
            }

            var unusable = Check(file, out var safe);
            if (unusable == Unusable.UnsafePath)
            {
                notes.Add($"Skipped file {RemoteText.Clean(file.FileId, 40)}: its path isn't safe to use.");
                continue;
            }

            var relative = safe!;
            if (unusable == Unusable.Infected)
            {
                notes.Add($"Skipped {RemoteText.Clean(relative, LongestPathInNote)}: {providerName} marks it as infected.");
                continue;
            }

            if (unusable == Unusable.Empty)
            {
                notes.Add($"Skipped {RemoteText.Clean(relative, LongestPathInNote)}: it is empty.");
                continue;
            }

            var placed = Placed(relative, record.Placement, onlyFile);
            if (placed is null)
            {
                notes.Add($"Skipped {RemoteText.Clean(relative, LongestPathInNote)}: its path would be too long in this package's folder.");
                continue;
            }

            requests.Add(new RemoteJobRequest
            {
                Source = new RemoteSource(
                    item.Ref.Provider,
                    RemoteKinds.ToWord(item.Ref.Kind),
                    item.Ref.ItemId,
                    file.FileId,
                    placed,
                    record.Id.ToString("N")),
                FileName = Path.GetFileName(relative),
                DestinationRoot = destination,
                MaxConnections = maxConnections,
                ExpectedTotalBytes = file.Size,
                ExpectedMd5 = string.IsNullOrWhiteSpace(file.Md5) ? null : file.Md5,
                QueueId = record.QueueId,
                StartMode = record.StartMode,
            });
        }

        if (chosen is not null)
        {
            var missing = chosen.Count(id => !seen.Contains(id));
            if (missing > 0)
            {
                notes.Add($"{missing} chosen file(s) are no longer in this item.");
            }
        }

        // Every client gets these on every change, so an item with thousands of bad files can't grow them for ever.
        if (notes.Count > MostNotes)
        {
            var left = notes.Count - MostNotes;
            notes.RemoveRange(MostNotes, left);
            notes.Add($"{left} more file(s) were skipped as well.");
        }

        return new Plan(requests, notes);
    }

    /// <summary>
    /// Where a file goes when its job is one of a group. An item holding one file has no structure worth
    /// keeping, and the folder a provider puts around it would keep it from the other parts of its set.
    /// </summary>
    private static string? Placed(string relative, RemoteJobPlacement? placement, bool onlyFile)
    {
        if (placement is null)
        {
            return onlyFile ? WithoutFolderNamedAfterItsFile(relative) : relative;
        }

        var within = onlyFile ? Path.GetFileName(relative) : relative;
        return placement.Subfolder is { } folder ? RemotePaths.ToSafeRelativePath(Path.Combine(folder, within)) : within;
    }

    /// <summary>
    /// A provider can put a lone file in a folder of the file's own name, x.rar/x.rar, and a folder with an extension is
    /// no use to anyone. An archive keeps a folder, without its extension, because what is extracted next to it stays
    /// together that way. Any other file needs no folder. A folder with any other name is the provider's real structure.
    /// </summary>
    private static string WithoutFolderNamedAfterItsFile(string relative)
    {
        var file = Path.GetFileName(relative);
        if (!string.Equals(Path.GetDirectoryName(relative), file, StringComparison.OrdinalIgnoreCase))
        {
            return relative;
        }

        if (!file.EndsWith(".rar", StringComparison.OrdinalIgnoreCase))
        {
            return file;
        }

        var folder = MultipartArchiveName.TryGetBaseName(file, out var baseName) ? baseName : file[..^".rar".Length];
        return RemotePaths.ToSafeRelativePath(Path.Combine(folder, file)) ?? file;
    }
}

using System.Collections.Concurrent;
using System.Runtime.CompilerServices;
using System.Text.Json;
using DownloadManager.Contracts;
using DownloadManager.Core;
using DownloadManager.Core.Providers;
using DownloadManager.Runtime.WebAuth;

namespace DownloadManager.Runtime.Ipc;

/// <summary>
/// What a web session is told about, and may do to, downloads outside its folders (<see cref="CallerScope"/>): it sees
/// them, but not their addresses, paths or completion program, and it can't regroup, requeue or re-prioritise them.
/// Also the browser's hand-off folder rule and the window pages a client may ask for.
/// </summary>
public sealed partial class HostCommandDispatcher
{
    private const string HiddenFolderText = "the download's folder";
    private const int HeldCacheLimit = 4096;

    // Whether a download's paths are inside a scope's folders, by its paths: a progress event comes several times a
    // second, and the check reads the disk (links). Emptied when the settings change, which is when the folders do.
    private readonly ConditionalWeakTable<CallerScope, ConcurrentDictionary<string, bool>> _held = new();

    /// <summary>Package actions that only start or stop files (as job.pause/resume/cancel do) work on any download.</summary>
    private GroupActionResult? RefuseGroupActionFor(GroupActionRequest request, CallerScope caller)
    {
        var action = request.Action?.Trim().ToLowerInvariant();
        if (action is "pause" or "resume" or "cancel") return null;
        // "create" names no package yet; every other action reaches all of the package's files.
        var package = action == "create" ? (Guid?)null : request.GroupId;
        return HoldsAll(request.JobIds, package, caller)
            ? null
            : new GroupActionResult { Error = CallerScope.OutsideJobRefused, GroupId = request.GroupId, Groups = _service.GetDownloadGroups() };
    }

    /// <summary>Deleting a queue moves its downloads into another: only when they are all inside the caller's folders.</summary>
    private SettingsResult? RefuseQueueActionFor(QueueActionRequest request, CallerScope caller)
    {
        if (!string.Equals(request.Action, "delete", StringComparison.OrdinalIgnoreCase) || request.MoveContentsToQueueId is null) return null;
        var members = _service.GetSnapshot().Jobs.Where(job => job.QueueId == request.QueueId).Select(job => job.Id);
        return HoldsAll(members, null, caller) ? null : new SettingsResult { Error = CallerScope.OutsideJobRefused, Settings = _service.GetSettings() };
    }

    /// <summary>Whether this download's paths are all inside the caller's folders, from the paths its DTO carries.</summary>
    private bool Holds(CallerScope caller, JobDto job)
    {
        var cache = _held.GetValue(caller, static _ => new ConcurrentDictionary<string, bool>(StringComparer.Ordinal));
        var key = string.Join('\n', job.DestinationDirectory, job.FinalFilePath, job.PartFilePath);
        if (cache.TryGetValue(key, out var held)) return held;
        held = caller.Holds(job.DestinationDirectory, job.FinalFilePath, job.PartFilePath);
        if (cache.Count >= HeldCacheLimit) cache.Clear();
        cache[key] = held;
        return held;
    }

    private void ForgetHeldPaths() => _held.Clear();

    /// <summary>
    /// M4: a download outside the caller's folders as that caller sees it: its site instead of its address (a magnet
    /// only by its hash, since trackers can carry a passkey), no final or page address beyond the site, no paths, no
    /// completion program, and its notes without addresses or its folder. One inside its folders is returned as it is.
    /// The DTO given is never changed, because a broadcast shares it among sessions.
    /// </summary>
    private JobDto ForScope(JobDto job, CallerScope caller)
    {
        if (Holds(caller, job)) return job;
        var copy = JsonSerializer.Deserialize<JobDto>(JsonSerializer.SerializeToUtf8Bytes(job, IpcJson.Options), IpcJson.Options)!;
        var folder = job.DestinationDirectory;
        copy.Url = job.Torrent is { InfoHash: { Length: > 0 } hash } ? $"magnet:?xt=urn:btih:{hash}" : SiteOf(job.Url) ?? string.Empty;
        copy.AddressHidden = job.Torrent is null;
        copy.FinalUrl = null;
        copy.PageUrl = SiteOf(job.PageUrl);
        copy.DestinationDirectory = string.Empty;
        copy.FinalFilePath = string.Empty;
        copy.PartFilePath = string.Empty;
        copy.CompletionOptions.ProgramPath = null;
        copy.ClientRequestId = null;
        copy.OwnerExtensionId = null;
        copy.StatusNote = Hidden(copy.StatusNote, folder);
        if (copy.Failure is { } failure) failure.Message = Hidden(failure.Message, folder) ?? string.Empty;
        return copy;
    }

    private static string? Hidden(string? text, string folder)
    {
        if (string.IsNullOrEmpty(text)) return text;
        var hidden = UrlRedactor.HideAddresses(text);
        return string.IsNullOrWhiteSpace(folder) ? hidden : hidden.Replace(folder.Trim(), HiddenFolderText, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>An address as its site alone (scheme, host and a port other than the default), or null.</summary>
    private static string? SiteOf(string? address)
        => !string.IsNullOrWhiteSpace(address) && Uri.TryCreate(address.Trim(), UriKind.Absolute, out var uri) && uri.Host.Length > 0
            ? uri.GetComponents(UriComponents.SchemeAndServer, UriFormat.UriEscaped) + "/"
            : null;

    private SnapshotDto JobsForScope(SnapshotDto snapshot, CallerScope caller)
    {
        snapshot.Jobs = snapshot.Jobs.Select(job => ForScope(job, caller)).ToList();
        return snapshot;
    }

    /// <summary>A reply's download (an action's, an add's, a hand-off's), as the caller may see it.</summary>
    private object? ReplyForScope(object? payload, CallerScope caller)
    {
        switch (payload)
        {
            case JobActionResponse { Job: { } job } action:
                action.Job = ForScope(job, caller);
                break;
            case AddJobResponse { Job: { } added } add:
                add.Job = ForScope(added, caller);
                break;
            case HandoffAck { Job: { } handed } ack:
                ack.Job = ForScope(handed, caller);
                break;
            case TorrentAddResult { Job: { } torrent } result:
                result.Job = ForScope(torrent, caller);
                break;
        }

        return payload;
    }

    /// <summary>A set of archives the caller may see: its folder and its output inside the caller's folders.</summary>
    private static ArchiveChangedDto ArchiveForScope(ArchiveChangedDto change, CallerScope caller)
        => change.Set is not { } set ||
           (caller.Roots.Allowed(set.Directory) is not null && (string.IsNullOrWhiteSpace(set.Destination) || caller.Roots.Allowed(set.Destination) is not null))
            ? change
            : new ArchiveChangedDto { RemovedId = set.Id };

    // ---- provider jobs (report 2, M4) ------------------------------------------------------------------

    /// <summary>A provider job's folder, when it is outside the caller's folders, is not told (as a download's isn't).</summary>
    private static RemoteJobDto? RemoteJobForScope(RemoteJobDto? job, CallerScope caller)
    {
        if (job is null || string.IsNullOrWhiteSpace(job.Destination) || caller.Roots.Allowed(job.Destination) is not null) return job;
        var copy = JsonSerializer.Deserialize<RemoteJobDto>(JsonSerializer.SerializeToUtf8Bytes(job, IpcJson.Options), IpcJson.Options)!;
        copy.Destination = null;
        return copy;
    }

    /// <summary>A provider reply's jobs (a list, an add, an action), as the caller may see them.</summary>
    private static object RemoteReplyForScope(object reply, CallerScope caller)
    {
        switch (reply)
        {
            case RemoteListResult list:
                list.Jobs = [.. list.Jobs.Select(job => RemoteJobForScope(job, caller)!)];
                break;
            case RemoteAddResultDto add:
                add.Job = RemoteJobForScope(add.Job, caller);
                break;
            case RemoteActionResultDto action:
                action.Job = RemoteJobForScope(action.Job, caller);
                break;
            case RemoteContainerAddResult container:
                foreach (var outcome in container.Outcomes) outcome.Job = RemoteJobForScope(outcome.Job, caller);
                break;
        }

        return reply;
    }

    /// <summary>A provider change as the caller may see it.</summary>
    private static RemoteChangedDto RemoteChangeForScope(RemoteChangedDto change, CallerScope caller)
        => RemoteJobForScope(change.Job, caller) is var job && ReferenceEquals(job, change.Job)
            ? change
            : new RemoteChangedDto { Change = change.Change, Job = job, JobId = change.JobId, Account = change.Account };

    // ---- the browser's hand-off folder (report 3, M1) ----------------------------------------------

    // Folders this Host's own folder dialog returned (host.pickFolder): the user chose them on this PC. Kept in the state
    // folder, so the extension's saved browser folder (EXT.R10, picked once in its Options) still works after the Host
    // restarts. Newest first, at most PickedFolderLimit.
    private const string PickedFoldersFile = "picked-folders.json";
    private const int PickedFolderLimit = 64;
    private readonly object _pickedGate = new();
    private List<string>? _pickedFolders;

    /// <summary>The words a hand-off gets when its folder was not used.</summary>
    public const string HandoffFolderReplaced =
        "The folder the browser asked for is not one of the app's folders, so the download went to the default folder. " +
        "Choose the folder with Choose…, or add it as a favourite in the app.";

    private string PickedFoldersPath => Path.Combine(_service.StateDirectory, PickedFoldersFile);

    /// <summary>The picked folders, read from the state folder once. An unreadable file counts as none.</summary>
    private List<string> PickedFoldersLocked()
    {
        if (_pickedFolders is not null) return _pickedFolders;
        var folders = new List<string>();
        try
        {
            if (File.Exists(PickedFoldersPath) &&
                JsonSerializer.Deserialize<List<string>>(File.ReadAllBytes(PickedFoldersPath)) is { } stored)
            {
                foreach (var folder in stored.Select(Normalized))
                {
                    if (folder is not null && !folders.Contains(folder, PathNames.Comparer)) folders.Add(folder);
                    if (folders.Count == PickedFolderLimit) break;
                }
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
        {
            _log.Warn("The folders picked for browser downloads could not be read; they must be chosen again.");
        }

        return _pickedFolders = folders;
    }

    private void RememberPickedFolder(FolderPickResult picked)
    {
        if (!picked.Ok || picked.Cancelled || Normalized(picked.Path) is not { } folder) return;
        lock (_pickedGate)
        {
            var folders = PickedFoldersLocked();
            folders.RemoveAll(existing => PathNames.Same(existing, folder));
            folders.Insert(0, folder);
            if (folders.Count > PickedFolderLimit) folders.RemoveRange(PickedFolderLimit, folders.Count - PickedFolderLimit);
            try
            {
                Directory.CreateDirectory(_service.StateDirectory);
                AtomicFile.Write(PickedFoldersPath, JsonSerializer.SerializeToUtf8Bytes(folders));
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                // Still used until the Host stops.
                _log.Warn("The folder picked for browser downloads could not be saved; it is used until the app restarts.");
            }
        }
    }

    private string[] PickedFolders()
    {
        lock (_pickedGate) return [.. PickedFoldersLocked()];
    }

    /// <summary>
    /// The browser's native host may save into the app's own folders and the folders under them: the default folder, the
    /// last one used, the category folders, the favourites, the recent folders, and one the Host's folder dialog
    /// returned. Anything else (a compromised extension could name the Startup folder) goes to the default folder.
    /// </summary>
    private bool HandoffFolderAllowed(string? requested)
    {
        if (string.IsNullOrWhiteSpace(requested)) return true;
        if (Normalized(requested) is not { } folder) return false;
        var settings = _service.GetSettings();
        IEnumerable<string?> allowed =
        [
            settings.DefaultDownloadDirectory,
            settings.LastDestinationDirectory,
            .. (settings.CategoryFolders ?? new()).Values,
            .. (settings.CustomCategories ?? []).Select(category => category?.DestinationDirectory),
            .. (settings.FavoriteFolders ?? []).Select(favorite => favorite?.Path),
            .. settings.RecentDestinationDirectories ?? [],
            .. PickedFolders(),
        ];
        return allowed.Select(Normalized).Any(root => root is not null && PathRoots.IsInside(folder, root));
    }

    /// <summary>A full path as text (no colon after the drive, no relative parts), without a trailing separator; else null.</summary>
    private static string? Normalized(string? path)
    {
        if (string.IsNullOrWhiteSpace(path)) return null;
        var trimmed = path.Trim();
        if (!Path.IsPathFullyQualified(trimmed) || (OperatingSystem.IsWindows() && trimmed.IndexOf(':', 2) >= 0)) return null;
        try { return Path.TrimEndingDirectorySeparator(Path.GetFullPath(trimmed)); }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException) { return null; }
    }

    // ---- window pages (report 3, L2) ------------------------------------------------------------------

    /// <summary>The pages a client may ask <c>host.openUi</c> to open; any other text opens the main window.</summary>
    public static string? ClientPage(string? action)
        => string.Equals(action?.Trim(), UiPages.Settings, StringComparison.OrdinalIgnoreCase) ? UiPages.Settings : null;
}

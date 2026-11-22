using System.Text.Json.Nodes;
using DownloadManager.Contracts;
using DownloadManager.Sites;

namespace DownloadManager.Runtime.Sites;

/// <summary>
/// SITES.13: what a remote remembers for every client (<c>sites.remember</c>), a preview of a file (<c>sites.preview</c>)
/// and a one-way sync of a linked folder (<c>sites.sync.plan</c> and <c>sites.sync.run</c>), which never deletes.
/// </summary>
public sealed partial class SitesCommands
{
    private const string NoSuchRemote = "That remote no longer exists.";

    private async Task<SiteResultDto> RememberAsync(SiteRememberRequest request, CancellationToken cancellationToken)
    {
        SiteRecord saved;
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (!SiteStore.IsId(request.Id) || await _sites.GetAsync(request.Id, cancellationToken).ConfigureAwait(false) is not { } site)
                return Fail(NoSuchRemote);
            var lastFolder = site.LastFolder;
            if (request.LastFolder is { } folder)
            {
                if (folder.Length == 0) lastFolder = null;
                else if (SitePaths.Normalize(folder) is { } normalized) lastFolder = normalized;
                else return Fail("That is not a folder path on the server.");
            }

            if (request.Sort is not null && !SiteSortKeys.IsKnown(request.Sort)) return Fail("That sort order is not known.");
            saved = site with
            {
                LastFolder = lastFolder,
                Sort = request.Sort ?? site.Sort,
                SortDescending = request.SortDescending ?? site.SortDescending,
            };
            if (saved == site) return new SiteResultDto { Ok = true, Site = ToDto(site) };
            try
            {
                await _sites.SaveAsync(saved, cancellationToken, keepRevision: true).ConfigureAwait(false);
            }
            catch (SiteStoreException e)
            {
                return Fail(e.Message);
            }
        }
        finally
        {
            _gate.Release();
        }

        await RaiseChangedAsync(cancellationToken).ConfigureAwait(false);
        return new SiteResultDto { Ok = true, Site = ToDto(saved) };
    }

    // ---- Preview --------------------------------------------------------------------------------------------------

    private static readonly HashSet<string> TextExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".txt", ".md", ".log", ".csv", ".tsv", ".json", ".xml", ".yml", ".yaml", ".toml", ".ini", ".conf", ".cfg", ".env",
        ".properties", ".nfo", ".srt", ".vtt", ".sub", ".ass", ".html", ".htm", ".css", ".js", ".ts", ".cs", ".py", ".sh",
        ".ps1", ".bat", ".cmd", ".c", ".h", ".cpp", ".hpp", ".java", ".go", ".rs", ".rb", ".php", ".sql", ".svg", ".diff",
        ".patch", ".sfv", ".md5", ".sha1", ".sha256", ".m3u", ".cue",
    };

    private async Task<SitePreviewDto> PreviewAsync(SitePreviewRequest request, CancellationToken cancellationToken)
    {
        if (!SiteStore.IsId(request.Id) || await _sites.GetAsync(request.Id, cancellationToken).ConfigureAwait(false) is not { } site)
            return new SitePreviewDto { Message = NoSuchRemote };
        if (SitePaths.Normalize(request.Path) is not { } path || path == "/") return new SitePreviewDto { Message = "That file can't be read." };
        var name = path[(path.LastIndexOf('/') + 1)..];
        if (site.NeedsHostKey)
            return new SitePreviewDto { Name = name, Problem = SiteProblems.HostKeyNotAccepted, Message = "The server's key has not been accepted yet." };
        var most = request.MaxBytes is > 0 and <= SitePreviewLimits.MaxBytes ? request.MaxBytes : SitePreviewLimits.MaxBytes;

        RcloneLease lease;
        try
        {
            lease = await AcquireAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (RcloneUnavailableException e)
        {
            return new SitePreviewDto { Name = name, Message = e.Message };
        }

        using (lease)
        {
            try
            {
                var remote = await SiteRemote.NamedAsync(site, lease.Client, cancellationToken).ConfigureAwait(false);
                using var response = await lease.Client.OpenFileAsync(remote, path, cancellationToken).ConfigureAwait(false);
                var size = response.Content.Headers.ContentLength ?? -1;
                await using var body = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
                var buffer = new byte[most];
                var filled = 0;
                int read;
                while (filled < buffer.Length && (read = await body.ReadAsync(buffer.AsMemory(filled), cancellationToken).ConfigureAwait(false)) > 0)
                    filled += read;
                // One byte more says whether the file goes on, without reading the rest.
                var truncated = filled == buffer.Length && (size > filled || (size < 0 && await body.ReadAsync(new byte[1], cancellationToken).ConfigureAwait(false) > 0));
                Touch(site.Id);
                return Preview(name, buffer.AsSpan(0, filled), size, truncated);
            }
            catch (Exception e) when (e is RcloneCallException or SiteStoreException or HttpRequestException or IOException)
            {
                return new SitePreviewDto
                {
                    Name = name,
                    Problem = e is SiteSignInNeededException ? SiteProblems.SignInNeeded
                        : SiteErrorText.IsUntrustedCertificate(e.Message) ? SiteProblems.CertificateNotTrusted : null,
                    Message = e is HttpRequestException or IOException ? "The file could not be read. Try again." : SiteErrorText.Friendly(e.Message),
                };
            }
        }
    }

    /// <summary>What the first bytes are: an image by its signature, text by its name or by having no NUL byte; never sent otherwise.</summary>
    internal static SitePreviewDto Preview(string name, ReadOnlySpan<byte> head, long size, bool truncated)
    {
        var result = new SitePreviewDto { Ok = true, Name = name, Size = size, Truncated = truncated };
        if (ImageType(head) is { } image)
        {
            result.Kind = SitePreviewKinds.Image;
            result.MimeType = image;
            // An image cut short can't be shown: no bytes, and the client says it is too large.
            if (!truncated) result.Data = head.ToArray();
            else result.Message = $"The image is larger than {SitePreviewLimits.MaxBytes / (1024 * 1024)} MB, so it is not previewed.";
            return result;
        }

        var sample = head[..Math.Min(head.Length, 8192)];
        if (TextExtensions.Contains(Path.GetExtension(name)) || (head.Length > 0 && !sample.Contains((byte)0)))
        {
            if (sample.Contains((byte)0))
            {
                result.Message = "This file is not text.";
                return result;
            }

            result.Kind = SitePreviewKinds.Text;
            result.MimeType = "text/plain";
            result.Data = head.ToArray();
            return result;
        }

        result.Message = head.Length == 0 ? "The file is empty." : "There is no preview for this kind of file.";
        return result;
    }

    private static string? ImageType(ReadOnlySpan<byte> head)
    {
        if (head.StartsWith((ReadOnlySpan<byte>)[0x89, (byte)'P', (byte)'N', (byte)'G', 0x0D, 0x0A, 0x1A, 0x0A])) return "image/png";
        if (head.StartsWith((ReadOnlySpan<byte>)[0xFF, 0xD8, 0xFF])) return "image/jpeg";
        if (head.StartsWith("GIF87a"u8) || head.StartsWith("GIF89a"u8)) return "image/gif";
        if (head.Length >= 12 && head.StartsWith("RIFF"u8) && head[8..12].SequenceEqual("WEBP"u8)) return "image/webp";
        if (head.StartsWith("BM"u8) && head.Length >= 26) return "image/bmp";
        return null;
    }

    // ---- One-way sync ---------------------------------------------------------------------------------------------

    private sealed record SyncEntry(long Size, DateTimeOffset? Modified);

    private async Task<(SiteSyncPlanDto Plan, SiteRecord? Site, List<SiteTransferItem>? Items)> PlanSyncAsync(SiteSyncRequest request, CancellationToken cancellationToken)
    {
        var plan = new SiteSyncPlanDto { Direction = request.Direction ?? "" };
        (SiteSyncPlanDto, SiteRecord?, List<SiteTransferItem>?) Refused(string message, string? problem = null)
        {
            plan.Message = message;
            plan.Problem = problem;
            return (plan, null, null);
        }

        if (!SiteStore.IsId(request.SiteId) || await _sites.GetAsync(request.SiteId, cancellationToken).ConfigureAwait(false) is not { } site)
            return Refused(NoSuchRemote);
        if (request.Direction is not (SiteTransferDirections.Download or SiteTransferDirections.Upload)) return Refused("Choose download or upload.");
        if (SitePaths.Normalize(request.RemoteFolder) is not { } remoteFolder) return Refused("That is not a folder path on the server.");
        plan.RemoteFolder = remoteFolder;
        var localFolder = !string.IsNullOrWhiteSpace(request.LocalFolder) ? request.LocalFolder.Trim() : LinkedFolder(site, remoteFolder);
        if (localFolder is null || !Path.IsPathFullyQualified(localFolder))
            return Refused("This folder has no linked folder on this PC; choose one.");
        localFolder = Path.GetFullPath(localFolder);
        plan.LocalFolder = localFolder;
        var download = request.Direction == SiteTransferDirections.Download;
        if (!download && !Directory.Exists(localFolder)) return Refused("The folder on this PC doesn't exist.");
        if (site.NeedsHostKey) return Refused("The server's key has not been accepted yet.", SiteProblems.HostKeyNotAccepted);

        Dictionary<string, SyncEntry> server;
        Dictionary<string, SyncEntry> here;
        try
        {
            server = await ListServerAsync(site, remoteFolder, cancellationToken).ConfigureAwait(false);
            here = ListHere(localFolder);
        }
        catch (TooManyFilesException)
        {
            return Refused($"That folder holds more than {SiteTransfers.MaxItems:N0} files; sync a folder inside it.");
        }
        catch (RcloneUnavailableException e)
        {
            return Refused(e.Message);
        }
        catch (Exception e) when (e is RcloneCallException or SiteStoreException)
        {
            return Refused(SiteErrorText.Friendly(e.Message), e is SiteSignInNeededException ? SiteProblems.SignInNeeded : null);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            return Refused($"The folder on this PC could not be read: {e.Message}");
        }

        var (source, target) = download ? (server, here) : (here, server);
        var items = new List<SiteTransferItem>();
        foreach (var (path, from) in source.OrderBy(p => p.Key, StringComparer.Ordinal))
        {
            string reason;
            if (!target.TryGetValue(path, out var to)) reason = SiteSyncReasons.New;
            else if (from.Modified is { } a && to.Modified is { } b && b > a + SiteHere.Slack)
            {
                // Newer at the target: someone changed it there, so it is never written over.
                plan.NewerThere++;
                continue;
            }
            else if ((from.Modified is { } c && to.Modified is { } d && c > d + SiteHere.Slack) || (from.Size >= 0 && to.Size >= 0 && from.Size != to.Size))
                reason = SiteSyncReasons.Changed;
            else
            {
                plan.Same++;
                continue;
            }

            items.Add(new SiteTransferItem { Path = path, Size = from.Size });
            plan.Count++;
            if (from.Size > 0) plan.Bytes += from.Size;
            if (plan.Files.Count < SiteSyncPlanDto.MaxListed) plan.Files.Add(new SiteSyncFileDto { Path = path, Size = from.Size, Reason = reason });
        }

        plan.Ok = true;
        return (plan, site, items);
    }

    private async Task<SiteTransferResultDto> RunSyncAsync(SiteSyncRequest request, CancellationToken cancellationToken)
    {
        var (plan, site, items) = await PlanSyncAsync(request, cancellationToken).ConfigureAwait(false);
        if (!plan.Ok || site is null || items is null) return new SiteTransferResultDto { Ok = false, Message = plan.Message, Problem = plan.Problem };
        if (items.Count == 0) return new SiteTransferResultDto { Ok = true, Message = "Nothing to copy: the folders match." };
        return await _transfers.StartSyncAsync(site, plan.Direction, plan.RemoteFolder, plan.LocalFolder, items, request.WaitForSchedule).ConfigureAwait(false);
    }

    private async Task<SiteResultDto> ScheduleSyncAsync(SiteSyncScheduleRequest request, CancellationToken cancellationToken)
    {
        SiteRecord saved;
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (!SiteStore.IsId(request.SiteId) || await _sites.GetAsync(request.SiteId, cancellationToken).ConfigureAwait(false) is not { } site)
                return Fail(NoSuchRemote);
            SiteSyncRecord? sync = null;
            if (request.Enabled)
            {
                if (request.Direction is not (SiteTransferDirections.Download or SiteTransferDirections.Upload)) return Fail("Choose download or upload.");
                if (SitePaths.Normalize(request.RemoteFolder) is not { } remoteFolder) return Fail("That is not a folder path on the server.");
                if (string.IsNullOrWhiteSpace(request.LocalFolder) || !Path.IsPathFullyQualified(request.LocalFolder.Trim()))
                    return Fail("Choose the folder on this PC to sync with.");
                var localFolder = Path.GetFullPath(request.LocalFolder.Trim());
                if (SiteTransfers.IsInside(localFolder, _stateDirectory)) return Fail("Inbound's own data folder can't be synced; choose another folder.");
                sync = new SiteSyncRecord { Direction = request.Direction, RemoteFolder = remoteFolder, LocalFolder = localFolder };
            }

            if (site.Unsaved && sync is not null) return Fail("Remember this remote first: a schedule only runs saved remotes.");
            saved = site with { ScheduledSync = sync };
            try
            {
                await _sites.SaveAsync(saved, cancellationToken, keepRevision: true).ConfigureAwait(false);
            }
            catch (SiteStoreException e)
            {
                return Fail(e.Message);
            }
        }
        finally
        {
            _gate.Release();
        }

        await RaiseChangedAsync(cancellationToken).ConfigureAwait(false);
        return new SiteResultDto { Ok = true, Site = ToDto(saved) };
    }

    /// <summary>
    /// A schedule with "Remote files transfers" on: at its start (<paramref name="start"/> true) the batches a schedule
    /// paused or that wait for one go on, and each remote's scheduled sync runs (unless one of it is still unfinished); at
    /// its stop the running batches pause. Nothing while Remote files is off.
    /// </summary>
    public async Task ScheduleAsync(bool start)
    {
        if (!start)
        {
            await _transfers.PauseForScheduleAsync().ConfigureAwait(false);
            return;
        }

        await _transfers.ResumeForScheduleAsync().ConfigureAwait(false);
        if (!_config.Enabled) return;
        foreach (var site in await _sites.ListAsync().ConfigureAwait(false))
        {
            if (site.ScheduledSync is not { } sync) continue;
            if (_transfers.HasUnfinished(site.Id, sync.Direction, sync.RemoteFolder, sync.LocalFolder))
            {
                _log($"Scheduled sync of remote {site.Id}: the last one is not finished, so no new one started.");
                continue;
            }

            using var limit = new CancellationTokenSource(TimeSpan.FromMinutes(5));
            try
            {
                var ran = await RunSyncAsync(new SiteSyncRequest
                {
                    SiteId = site.Id, Direction = sync.Direction, RemoteFolder = sync.RemoteFolder, LocalFolder = sync.LocalFolder,
                }, limit.Token).ConfigureAwait(false);
                _log($"Scheduled sync of remote {site.Id}: {(ran.Ok ? ran.Transfer is null ? "nothing to copy" : $"{ran.Transfer.Files} file(s) started" : ran.Message)}.");
            }
            catch (OperationCanceledException)
            {
                _log($"Scheduled sync of remote {site.Id}: the server did not answer in time.");
            }
        }
    }

    /// <summary>The folder here that matches <paramref name="remoteFolder"/> under the remote's linked folder, or null.</summary>
    private static string? LinkedFolder(SiteRecord site, string remoteFolder)
    {
        if (string.IsNullOrEmpty(site.LocalFolder)) return null;
        var root = SitePaths.Normalize(site.RemoteFolder.Length == 0 ? "/" : site.RemoteFolder) ?? "/";
        if (remoteFolder == root) return site.LocalFolder;
        var cut = remoteFolder.LastIndexOf('/');
        return SiteHere.LocalPath(site.LocalFolder, root, cut == 0 ? "/" : remoteFolder[..cut], remoteFolder[(cut + 1)..]);
    }

    private sealed class TooManyFilesException : Exception;

    /// <summary>Every file under the server folder, by its path relative to it; a missing folder is empty.</summary>
    private async Task<Dictionary<string, SyncEntry>> ListServerAsync(SiteRecord site, string remoteFolder, CancellationToken cancellationToken)
    {
        using var lease = await AcquireAsync(cancellationToken).ConfigureAwait(false);
        var fs = await SiteRemote.BuildAsync(site, lease.Client, cancellationToken).ConfigureAwait(false);
        var inside = SitePaths.ToRemote(remoteFolder);
        JsonObject listing;
        try
        {
            listing = await lease.Client.CallAsync("operations/list", new JsonObject
            {
                ["fs"] = fs,
                ["remote"] = inside,
                ["opt"] = new JsonObject { ["recurse"] = true, ["filesOnly"] = true, ["noMimeType"] = true },
            }, cancellationToken).ConfigureAwait(false);
        }
        catch (RcloneCallException e) when (e.Message.Contains("directory not found", StringComparison.OrdinalIgnoreCase))
        {
            return new Dictionary<string, SyncEntry>(StringComparer.Ordinal);
        }

        var prefix = inside.Length == 0 ? "" : inside + "/";
        var files = new Dictionary<string, SyncEntry>(StringComparer.Ordinal);
        if (listing["list"] is not JsonArray list) return files;
        foreach (var node in list)
        {
            if (node is not JsonObject file || file["Path"]?.GetValue<string>() is not { } path || !path.StartsWith(prefix, StringComparison.Ordinal)) continue;
            if (file["IsDir"] is JsonValue dir && dir.TryGetValue<bool>(out var isDir) && isDir) continue;
            var relative = path[prefix.Length..];
            if (!SiteTransferPaths.IsRelative(relative)) continue;
            var size = file["Size"] is JsonValue s && s.TryGetValue<long>(out var bytes) && bytes >= 0 ? bytes : -1;
            DateTimeOffset? modified = file["ModTime"] is JsonValue m && m.TryGetValue<string>(out var text) && DateTimeOffset.TryParse(text, out var at) ? at : null;
            files[relative] = new SyncEntry(size, modified);
            if (files.Count > SiteTransfers.MaxItems) throw new TooManyFilesException();
        }

        return files;
    }

    /// <summary>
    /// Every file under the folder here, by its path relative to it with <c>/</c>; links are not followed. On Windows a
    /// path differing only in case is the same file.
    /// </summary>
    private static Dictionary<string, SyncEntry> ListHere(string folder)
    {
        var files = new Dictionary<string, SyncEntry>(OperatingSystem.IsWindows() || OperatingSystem.IsMacOS() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal);
        if (!Directory.Exists(folder)) return files;
        var options = new EnumerationOptions
        {
            RecurseSubdirectories = true,
            AttributesToSkip = FileAttributes.ReparsePoint,
            IgnoreInaccessible = true,
            ReturnSpecialDirectories = false,
        };
        foreach (var file in new DirectoryInfo(folder).EnumerateFiles("*", options))
        {
            var relative = Path.GetRelativePath(folder, file.FullName).Replace(Path.DirectorySeparatorChar, '/');
            if (!SiteTransferPaths.IsRelative(relative)) continue;
            // rclone's own partial files of a stopped copy are not part of the folder.
            if (file.Name.EndsWith(".partial", StringComparison.OrdinalIgnoreCase)) continue;
            files[relative] = new SyncEntry(file.Length, new DateTimeOffset(file.LastWriteTimeUtc, TimeSpan.Zero));
            if (files.Count > SiteTransfers.MaxItems) throw new TooManyFilesException();
        }

        return files;
    }
}

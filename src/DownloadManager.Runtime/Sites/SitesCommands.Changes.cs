using System.Text.Json.Nodes;
using DownloadManager.Contracts;
using DownloadManager.Sites;

namespace DownloadManager.Runtime.Sites;

/// <summary>
/// SITES.5: remote changes (remote-sites.md, "Transfers" and "IPC"): a new folder, a rename, the count for the delete
/// dialog, and delete on the server with a result per item. Nothing that a running, paused or stopped transfer still uses
/// is renamed or deleted under it.
/// </summary>
public sealed partial class SitesCommands
{
    /// <summary>Items one <c>sites.delete</c> or <c>sites.count</c> takes at most.</summary>
    public const int MaxChangeItems = 1000;

    private async Task<SiteResultDto> MakeFolderAsync(SiteMakeFolderRequest request, CancellationToken cancellationToken)
    {
        if (await SiteOfAsync(request.Id, cancellationToken).ConfigureAwait(false) is not { } site) return Fail("That remote no longer exists.");
        if (SitePaths.Normalize(request.Path) is not { } folder) return Fail("That is not a folder path on the server.");
        if (SitePaths.NameProblem(request.Name) is { } problem) return Fail(problem);
        var target = SiteTransferPaths.Remote(folder, request.Name);
        var (_, failure) = await OnServerAsync(site, async (client, fs) =>
        {
            if (await StatAsync(client, fs, target, cancellationToken).ConfigureAwait(false) is not null)
                throw new ChangeRefused($"\"{request.Name}\" is already there.");
            await client.CallAsync("operations/mkdir", new JsonObject { ["fs"] = fs.DeepClone(), ["remote"] = SitePaths.ToRemote(target) },
                cancellationToken).ConfigureAwait(false);
            return true;
        }, cancellationToken).ConfigureAwait(false);
        ForgetListings(site.Id);
        return failure ?? new SiteResultDto { Ok = true, Message = $"Made the folder {request.Name}." };
    }

    private async Task<SiteResultDto> RenameAsync(SiteRenameRequest request, CancellationToken cancellationToken)
    {
        if (await SiteOfAsync(request.Id, cancellationToken).ConfigureAwait(false) is not { } site) return Fail("That remote no longer exists.");
        if (SitePaths.Normalize(request.Path) is not { } folder) return Fail("That is not a folder path on the server.");
        if (SitePaths.NameProblem(request.Name) is not null) return Fail("That item can't be renamed.");
        if (SitePaths.NameProblem(request.NewName) is { } problem) return Fail(problem);
        if (request.NewName == request.Name) return new SiteResultDto { Ok = true };
        var from = SiteTransferPaths.Remote(folder, request.Name);
        var to = SiteTransferPaths.Remote(folder, request.NewName);
        if (_transfers.Uses(site.Id, from)) return Fail(BusyMessage);
        var (_, failure) = await OnServerAsync(site, async (client, fs) =>
        {
            if (await StatAsync(client, fs, from, cancellationToken).ConfigureAwait(false) is not { } item)
                throw new ChangeRefused($"\"{request.Name}\" is no longer there.");
            // A change of case only: a server that ignores case finds the item itself under the new name, so the folder's
            // exact names decide; on one that keeps case, another item of that exact name stops the rename.
            var taken = string.Equals(request.Name, request.NewName, StringComparison.OrdinalIgnoreCase)
                ? await HasExactNameAsync(client, fs, folder, request.NewName, cancellationToken).ConfigureAwait(false)
                : await StatAsync(client, fs, to, cancellationToken).ConfigureAwait(false) is not null;
            if (taken) throw new ChangeRefused($"\"{request.NewName}\" is already there.");
            var isFolder = item["IsDir"] is JsonValue dir && dir.TryGetValue<bool>(out var isDir) && isDir;
            if (string.Equals(request.Name, request.NewName, StringComparison.OrdinalIgnoreCase))
            {
                // A server that ignores case treats "a" to "A" as no change at all: go through a name of its own first.
                var between = SiteTransferPaths.Remote(folder, $".inbound-rename-{Guid.NewGuid():N}");
                await MoveOnServerAsync(client, fs, from, between, isFolder, cancellationToken).ConfigureAwait(false);
                try
                {
                    await MoveOnServerAsync(client, fs, between, to, isFolder, cancellationToken).ConfigureAwait(false);
                }
                catch (Exception e) when (e is RcloneCallException or OperationCanceledException)
                {
                    // Put it back under its old name rather than leave the in-between name behind.
                    await MoveOnServerAsync(client, fs, between, from, isFolder, CancellationToken.None).ConfigureAwait(false);
                    throw;
                }
            }
            else
            {
                await MoveOnServerAsync(client, fs, from, to, isFolder, cancellationToken).ConfigureAwait(false);
            }

            return true;
        }, cancellationToken).ConfigureAwait(false);
        ForgetListings(site.Id);
        return failure ?? new SiteResultDto { Ok = true, Message = $"Renamed {request.Name} to {request.NewName}." };
    }

    /// <summary>What a delete would remove: the files inside the chosen folders as well, and their size.</summary>
    private async Task<SiteCountResultDto> CountAsync(SiteChangeRequest request, CancellationToken cancellationToken)
    {
        if (await SiteOfAsync(request.Id, cancellationToken).ConfigureAwait(false) is not { } site) return CountFail("That remote no longer exists.");
        if (SitePaths.Normalize(request.Path) is not { } folder) return CountFail("That is not a folder path on the server.");
        if (ItemsProblem(request.Items) is { } problem) return CountFail(problem);
        var (count, failure) = await OnServerAsync(site, async (client, fs) =>
        {
            var result = new SiteCountResultDto { Ok = true };
            foreach (var entry in request.Items)
            {
                var path = SiteTransferPaths.Remote(folder, entry.Name);
                if (await StatAsync(client, fs, path, cancellationToken).ConfigureAwait(false) is not { } item)
                {
                    result.Missing++;
                    continue;
                }

                if (item["IsDir"] is JsonValue dir && dir.TryGetValue<bool>(out var isDir) && isDir)
                {
                    result.Folders++;
                    var size = await client.CallAsync("operations/size", new JsonObject { ["fs"] = Rooted(fs, path) }, cancellationToken).ConfigureAwait(false);
                    result.Files += size["count"] is JsonValue c && c.TryGetValue<long>(out var files) ? files : 0;
                    var bytes = size["bytes"] is JsonValue b && b.TryGetValue<long>(out var n) ? n : -1;
                    if (bytes >= 0) result.Bytes += bytes;
                    // rclone counts files of unknown size too, but not in bytes.
                    if (size["sizeless"] is JsonValue s && s.TryGetValue<long>(out var sizeless) && sizeless > 0) result.SizeUnknown = true;
                }
                else
                {
                    result.Files++;
                    var bytes = item["Size"] is JsonValue b && b.TryGetValue<long>(out var n) ? n : -1;
                    if (bytes >= 0) result.Bytes += bytes;
                    else result.SizeUnknown = true;
                }
            }

            return result;
        }, cancellationToken).ConfigureAwait(false);
        if (failure is not null) return CountFail(failure.Message ?? "The count failed.", failure.Problem);
        return count!;
    }

    /// <summary>Deletes on the server: a file with <c>deletefile</c>, a folder and all inside it with <c>purge</c>. One result per item.</summary>
    private async Task<SiteChangeResultDto> DeleteAsync(SiteChangeRequest request, CancellationToken cancellationToken)
    {
        if (await SiteOfAsync(request.Id, cancellationToken).ConfigureAwait(false) is not { } site) return ChangeFail("That remote no longer exists.");
        if (SitePaths.Normalize(request.Path) is not { } folder) return ChangeFail("That is not a folder path on the server.");
        if (ItemsProblem(request.Items) is { } problem) return ChangeFail(problem);
        var busy = request.Items.Where(i => _transfers.Uses(site.Id, SiteTransferPaths.Remote(folder, i.Name))).Select(i => i.Name).ToList();
        if (busy.Count > 0) return ChangeFail(BusyMessage);

        var (done, failure) = await OnServerAsync(site, async (client, fs) =>
        {
            var result = new SiteChangeResultDto { Ok = true };
            foreach (var entry in request.Items)
            {
                var path = SiteTransferPaths.Remote(folder, entry.Name);
                var line = new SiteChangeItemResultDto { Name = entry.Name, Ok = true };
                try
                {
                    var item = await StatAsync(client, fs, path, cancellationToken).ConfigureAwait(false);
                    if (item is null)
                    {
                        // Gone already: what the user wanted.
                        line.Message = "It was no longer there.";
                    }
                    else if (item["IsDir"] is JsonValue dir && dir.TryGetValue<bool>(out var isDir) && isDir)
                    {
                        await client.CallAsync("operations/purge", new JsonObject { ["fs"] = fs.DeepClone(), ["remote"] = SitePaths.ToRemote(path) },
                            cancellationToken).ConfigureAwait(false);
                    }
                    else
                    {
                        await client.CallAsync("operations/deletefile", new JsonObject { ["fs"] = fs.DeepClone(), ["remote"] = SitePaths.ToRemote(path) },
                            cancellationToken).ConfigureAwait(false);
                    }
                }
                catch (RcloneCallException e) when (SshHostKeyProblem.Parse(e.Message) is null)
                {
                    // One item that can't go (rights, in use) does not stop the others.
                    line.Ok = false;
                    line.Message = SiteErrorText.Friendly(e.Message);
                    result.Ok = false;
                }

                result.Items.Add(line);
            }

            var failed = result.Items.Count(i => !i.Ok);
            result.Message = failed == 0 ? null : failed == 1 ? "One item could not be deleted." : $"{failed} items could not be deleted.";
            return result;
        }, cancellationToken).ConfigureAwait(false);
        ForgetListings(site.Id);
        if (failure is not null) return ChangeFail(failure.Message ?? "The delete failed.", failure.Problem);
        return done!;
    }

    private const string BusyMessage = "A transfer is still using that. Let it finish, or cancel it in the Transfers bar first.";

    private static string? ItemsProblem(List<SiteChangeItemDto> items)
    {
        if (items.Count == 0) return "Nothing was chosen.";
        if (items.Count > MaxChangeItems) return $"Choose at most {MaxChangeItems:N0} items at once.";
        if (items.Any(i => SitePaths.NameProblem(i.Name) is not null)) return "One of the chosen names can't be used.";
        if (items.Select(i => i.Name).Distinct(StringComparer.Ordinal).Count() != items.Count) return "An item was chosen twice.";
        return null;
    }

    private async Task<SiteRecord?> SiteOfAsync(string id, CancellationToken cancellationToken) =>
        SiteStore.IsId(id) ? await _sites.GetAsync(id, cancellationToken).ConfigureAwait(false) : null;

    /// <summary>The item at that path on the server, or null when nothing is there.</summary>
    private static async Task<JsonObject?> StatAsync(RcloneClient client, JsonObject fs, string path, CancellationToken cancellationToken)
    {
        var stat = await client.CallAsync("operations/stat", new JsonObject { ["fs"] = fs.DeepClone(), ["remote"] = SitePaths.ToRemote(path) },
            cancellationToken).ConfigureAwait(false);
        return stat["item"] as JsonObject;
    }

    /// <summary>
    /// One item to another name on the same server: a file with <c>movefile</c>, a folder as one move of the whole tree
    /// (rclone uses the server's own folder rename where the backend has one).
    /// </summary>
    private static async Task MoveOnServerAsync(RcloneClient client, JsonObject fs, string from, string to, bool isFolder, CancellationToken cancellationToken)
    {
        if (isFolder)
        {
            await client.CallAsync("sync/move", new JsonObject
            {
                ["srcFs"] = Rooted(fs, from),
                ["dstFs"] = Rooted(fs, to),
                ["deleteEmptySrcDirs"] = true,
                ["createEmptySrcDirs"] = true,
            }, cancellationToken).ConfigureAwait(false);
            return;
        }

        await client.CallAsync("operations/movefile", new JsonObject
        {
            ["srcFs"] = fs.DeepClone(),
            ["srcRemote"] = SitePaths.ToRemote(from),
            ["dstFs"] = fs.DeepClone(),
            ["dstRemote"] = SitePaths.ToRemote(to),
        }, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>True when the folder holds an item of exactly that name, case included.</summary>
    private static async Task<bool> HasExactNameAsync(RcloneClient client, JsonObject fs, string folder, string name, CancellationToken cancellationToken)
    {
        var listing = await client.CallAsync("operations/list", new JsonObject
        {
            ["fs"] = fs.DeepClone(),
            ["remote"] = SitePaths.ToRemote(folder),
            ["opt"] = new JsonObject { ["noMimeType"] = true, ["noModTime"] = true },
        }, cancellationToken).ConfigureAwait(false);
        return SiteListing.Read(listing).Any(entry => string.Equals(entry.Name, name, StringComparison.Ordinal));
    }

    /// <summary>The site's remote with its root at that folder, for the calls that take a whole tree.</summary>
    private static JsonObject Rooted(JsonObject fs, string path)
    {
        var rooted = (JsonObject)fs.DeepClone();
        rooted["_root"] = path;
        return rooted;
    }

    /// <summary>
    /// Runs <paramref name="work"/> with an rclone lease and the site's remote; a refusal, a changed host key and rclone's
    /// errors come back as the failure, in the words <c>sites.browse</c> uses.
    /// </summary>
    private async Task<(T? Value, SiteResultDto? Failure)> OnServerAsync<T>(SiteRecord site, Func<RcloneClient, JsonObject, Task<T>> work,
        CancellationToken cancellationToken)
    {
        if (site.NeedsHostKey)
            return (default, new SiteResultDto { Ok = false, Problem = SiteProblems.HostKeyNotAccepted, Message = "The server's key has not been accepted yet." });

        RcloneLease lease;
        try
        {
            lease = await AcquireAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (RcloneUnavailableException e)
        {
            return (default, Fail(e.Message));
        }

        using (lease)
        {
            try
            {
                var fs = await SiteRemote.BuildAsync(site, lease.Client, cancellationToken).ConfigureAwait(false);
                return (await work(lease.Client, fs).ConfigureAwait(false), null);
            }
            catch (ChangeRefused e)
            {
                return (default, Fail(e.Message));
            }
            catch (RcloneCallException e) when (SshHostKeyProblem.Parse(e.Message) is { } problem)
            {
                return (default, new SiteResultDto
                {
                    Ok = false,
                    Problem = SiteProblems.HostKeyChanged,
                    Message = "The server's key has changed. This can mean the connection is being intercepted. Do not continue unless the server's owner confirms the new key.",
                    OfferedFingerprint = problem.OfferedFingerprint,
                    AcceptedFingerprints = [.. problem.PinnedFingerprints],
                });
            }
            catch (Exception e) when (e is RcloneCallException or RcloneUnavailableException or SiteStoreException)
            {
                return (default, Fail(SiteErrorText.Friendly(e.Message)));
            }
        }
    }

    private static SiteCountResultDto CountFail(string message, string? problem = null) => new() { Ok = false, Message = message, Problem = problem };

    private static SiteChangeResultDto ChangeFail(string message, string? problem = null) => new() { Ok = false, Message = message, Problem = problem };

    /// <summary>A change refused before anything was done on the server, in words for the user.</summary>
    private sealed class ChangeRefused(string message) : Exception(message);
}

using System.Text.Json.Nodes;
using DownloadManager.Contracts;
using DownloadManager.Sites;

namespace DownloadManager.Runtime.Sites;

/// <summary>
/// SITES.11 (owner, 2026-11-16: "build"): "Some of it" on a folder. A browse page says only whether a folder is here
/// (<see cref="SiteHereStates.FolderHere"/>); <c>sites.here</c> reads each named folder all the way down on the server
/// and compares its files with the same files here. On demand only, never polled.
/// </summary>
public sealed partial class SitesCommands
{
    /// <summary>How long one <c>sites.here</c> may take in all; folders not done by then keep the browse's own value.</summary>
    internal static TimeSpan HereBudget { get; set; } = TimeSpan.FromSeconds(60);

    private async Task<SiteHereResultDto> HereAsync(SiteHereRequest request, CancellationToken cancellationToken)
    {
        if (!SiteStore.IsId(request.Id) || await _sites.GetAsync(request.Id, cancellationToken).ConfigureAwait(false) is not { } site)
            return new SiteHereResultDto { Ok = false, Message = "That remote no longer exists.", Path = request.Path ?? "" };
        if (SitePaths.Normalize(request.Path) is not { } path)
            return new SiteHereResultDto { Ok = false, Message = "That is not a folder path on the server.", Path = request.Path ?? "" };
        var names = (request.Names ?? []).Where(IsFolderName).Distinct(StringComparer.Ordinal).ToList();
        if (names.Count > SiteHereRequest.MaxNames)
            return new SiteHereResultDto { Ok = false, Message = $"At most {SiteHereRequest.MaxNames} folders can be compared at once.", Path = path };

        var result = new SiteHereResultDto { Ok = true, Path = path };
        // Only folders that are here are read from the server: a missing one is "Not here" from the browse already.
        var wanted = new List<string>();
        foreach (var name in names)
        {
            result.Folders.Add(new SiteFolderHereDto { Name = name });
            if (SiteHere.LocalPath(site.LocalFolder, site.RemoteFolder, path, name) is { } local && Directory.Exists(local)) wanted.Add(name);
        }

        if (wanted.Count == 0) return result;
        if (site.NeedsHostKey)
            return new SiteHereResultDto { Ok = false, Problem = SiteProblems.HostKeyNotAccepted, Message = "The server's key has not been accepted yet.", Path = path };
        if (site is { Unsaved: true, Kind: SiteKinds.Sftp, SealedPassword: null })
            return new SiteHereResultDto { Ok = false, Problem = SiteProblems.PasswordNeeded, Message = "Type the password for this server.", Path = path };

        RcloneLease lease;
        try
        {
            lease = await AcquireAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (RcloneUnavailableException e)
        {
            return new SiteHereResultDto { Ok = false, Message = e.Message, Path = path };
        }

        using (lease)
        {
            JsonObject fs;
            try
            {
                fs = await SiteRemote.BuildAsync(site, lease.Client, cancellationToken).ConfigureAwait(false);
            }
            catch (Exception e) when (e is RcloneCallException or SiteStoreException)
            {
                return new SiteHereResultDto { Ok = false, Message = SiteErrorText.Friendly(e.Message), Path = path };
            }

            using var budget = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            budget.CancelAfter(HereBudget);
            foreach (var name in wanted)
            {
                try
                {
                    result.Folders.First(f => f.Name == name).Here = await FolderHereAsync(lease.Client, fs, site, path, name, budget.Token).ConfigureAwait(false);
                }
                catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
                {
                    // Out of time: this folder and the rest keep the browse's own value.
                    break;
                }
                catch (RcloneUnavailableException e)
                {
                    return new SiteHereResultDto { Ok = false, Message = e.Message, Path = path };
                }
                catch (RcloneCallException e) when (SiteErrorText.CantReach(e.Message) || SiteErrorText.RefusesTheSite(e.Message)
                    || SiteErrorText.IsSignInRefused(e.Message) || SshHostKeyProblem.Parse(e.Message) is not null)
                {
                    // Every other folder would fail the same way.
                    SetReach(site.Id, SiteReach.Trouble);
                    return new SiteHereResultDto { Ok = false, Message = SiteErrorText.Friendly(e.Message), Path = path };
                }
                catch (RcloneCallException)
                {
                    // This folder only (no access to a folder inside it, say): it keeps the browse's own value.
                }
            }
        }

        return result;
    }

    /// <summary>
    /// One folder, read a level at a time (review: a recursive listing of a huge tree costs memory before any cap), until
    /// every file is counted or the answer can only be Some of it. Null over <see cref="SiteHere.MaxFolderFiles"/> files
    /// or <see cref="SiteHere.MaxFolderFolders"/> folders.
    /// </summary>
    private static async Task<string?> FolderHereAsync(RcloneClient client, JsonObject fs, SiteRecord site, string parent, string name,
        CancellationToken cancellationToken)
    {
        var tally = new SiteHere.FolderTally();
        var pending = new Queue<string>();
        pending.Enqueue(parent == "/" ? "/" + name : parent + "/" + name);
        int files = 0, folders = 0;
        while (pending.TryDequeue(out var folder))
        {
            if (++folders > SiteHere.MaxFolderFolders) return null;
            var listing = await client.CallAsync("operations/list", new JsonObject
            {
                ["fs"] = fs.DeepClone(),
                ["remote"] = SitePaths.ToRemote(folder),
                ["opt"] = new JsonObject { ["noMimeType"] = true },
            }, cancellationToken).ConfigureAwait(false);
            if (listing["list"] is JsonArray { Count: > SiteHere.MaxFolderFiles }) return null;
            foreach (var item in SiteListing.Read(listing))
            {
                if (item.IsFolder)
                {
                    if (IsFolderName(item.Name)) pending.Enqueue(folder + "/" + item.Name);
                    continue;
                }

                if (++files > SiteHere.MaxFolderFiles) return null;
                tally.Add(SiteHere.Of(site.LocalFolder, site.RemoteFolder, folder, item));
                if (tally.Settled) return tally.Result;
            }
        }

        return tally.Result;
    }

    private static bool IsFolderName(string? name) =>
        name is { Length: > 0 and <= 255 } && name != "." && name != ".." && !name.Contains('/') && !name.Any(char.IsControl);
}

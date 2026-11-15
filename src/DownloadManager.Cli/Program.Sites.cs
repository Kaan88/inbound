using DownloadManager.Contracts;

namespace DownloadManager.Cli;

/// <summary>
/// SITES.4: <c>dm sites</c> (remote-sites.md, "Clients"). Not <c>dm site</c>, which already means site logins. Lists
/// the saved sites and the transfers, starts downloads and uploads, and acts on transfers. Adding and editing sites stays
/// in Options › Sites, where secrets and host keys are asked for.
/// </summary>
internal static partial class Program
{
    internal const string SitesUsage = """
        usage: sites list
               sites transfers
               sites get <site> <path on the site>... --to <folder on this PC> [--conflict skip|replace|keepBoth]
               sites put <site> <folder on the site> <file or folder on this PC>... [--conflict skip|replace|keepBoth]
               sites pause|resume|cancel|retry <transfer-id>
               sites clear [<transfer-id>]   (without an id: every finished transfer)
        <site> is a saved site's name or the start of its id. The paths of one command must share a folder.
        """;

    private static async Task<int> SitesAsync(CliClient client, string[] args, bool json)
    {
        var positional = CliArgs.Positional(args);
        var sub = positional.ElementAtOrDefault(0)?.ToLowerInvariant() ?? "list";
        try
        {
            switch (sub)
            {
                case "list":
                {
                    var sites = await client.AskAsync<SitesListDto>(SitesMessageTypes.ListRequest, new EmptyPayload()).ConfigureAwait(false);
                    if (json) { PrintJson(sites.Sites); return 0; }
                    Console.WriteLine($"{"ID",-9} {"KIND",-6} {"NAME",-24} ADDRESS");
                    foreach (var site in sites.Sites)
                        Console.WriteLine($"{site.Id[..Math.Min(8, site.Id.Length)],-9} {site.Kind,-6} {site.Name,-24} {site.User}@{site.Host}:{site.Port}");
                    return 0;
                }
                case "transfers":
                {
                    var list = await client.AskAsync<SiteTransfersDto>(SitesMessageTypes.TransferList, new EmptyPayload()).ConfigureAwait(false);
                    if (json) { PrintJson(list.Transfers); return 0; }
                    if (list.Transfers.Count == 0) { Console.WriteLine("No site transfers."); return 0; }
                    foreach (var t in list.Transfers.OrderBy(t => t.CreatedAt)) Console.WriteLine(SiteTransferLine(t));
                    return 0;
                }
                case "get":
                    return await StartSiteTransferAsync(client, args, positional, download: true, json).ConfigureAwait(false);
                case "put":
                    return await StartSiteTransferAsync(client, args, positional, download: false, json).ConfigureAwait(false);
                case "pause" or "resume" or "cancel" or "retry" or "clear":
                {
                    var selector = positional.ElementAtOrDefault(1);
                    if (selector is null && sub != "clear") { Console.Error.WriteLine(SitesUsage); return 2; }
                    var id = "";
                    if (selector is not null)
                    {
                        var list = await client.AskAsync<SiteTransfersDto>(SitesMessageTypes.TransferList, new EmptyPayload()).ConfigureAwait(false);
                        var matches = list.Transfers.Where(t => t.Id.StartsWith(selector, StringComparison.OrdinalIgnoreCase)).ToList();
                        if (matches.Count != 1)
                        {
                            Console.Error.WriteLine($"'{selector}' did not match exactly one transfer; see 'sites transfers'.");
                            return 1;
                        }

                        id = matches[0].Id;
                    }

                    var type = sub switch
                    {
                        "pause" => SitesMessageTypes.TransferPause,
                        "resume" => SitesMessageTypes.TransferResume,
                        "cancel" => SitesMessageTypes.TransferCancel,
                        "retry" => SitesMessageTypes.TransferRetry,
                        _ => SitesMessageTypes.TransferClear,
                    };
                    var reply = await client.AskAsync<SiteTransferResultDto>(type, new SiteTransferIdRequest { Id = id }).ConfigureAwait(false);
                    return PrintTransferResult(reply, json);
                }
                default:
                    Console.Error.WriteLine(SitesUsage);
                    return 2;
            }
        }
        catch (HostRefusedException e)
        {
            Console.Error.WriteLine($"failed: {e.Message}");
            return 1;
        }
    }

    private static async Task<int> StartSiteTransferAsync(CliClient client, string[] args, string[] positional, bool download, bool json)
    {
        // sites get <site> <path>... --to <folder>  |  sites put <site> <remote folder> <local path>...
        if (positional.Length < (download ? 3 : 4)) { Console.Error.WriteLine(SitesUsage); return 2; }
        var conflict = GetOption(args, "--conflict") ?? "skip";
        if (conflict is not ("skip" or "replace" or "keepBoth"))
        {
            Console.Error.WriteLine("--conflict is skip, replace or keepBoth.");
            return 2;
        }

        var sites = await client.AskAsync<SitesListDto>(SitesMessageTypes.ListRequest, new EmptyPayload()).ConfigureAwait(false);
        var wanted = positional[1];
        var found = sites.Sites.Where(s => string.Equals(s.Name, wanted, StringComparison.OrdinalIgnoreCase)).ToList();
        if (found.Count == 0) found = [.. sites.Sites.Where(s => s.Id.StartsWith(wanted, StringComparison.OrdinalIgnoreCase))];
        if (found.Count != 1)
        {
            Console.Error.WriteLine($"'{wanted}' did not match exactly one saved site; see 'sites list'.");
            return 1;
        }

        var request = new SiteTransferStartRequest { SiteId = found[0].Id, Direction = download ? "download" : "upload", Conflict = conflict };
        if (download)
        {
            var to = GetOption(args, "--to");
            if (to is null) { Console.Error.WriteLine("Say where on this PC with --to <folder>."); return 2; }
            var paths = positional.Skip(2).Select(p => "/" + p.Replace('\\', '/').Trim('/')).ToList();
            var folders = paths.Select(p => p[..p.LastIndexOf('/')] is { Length: > 0 } parent ? parent : "/").Distinct(StringComparer.Ordinal).ToList();
            if (folders.Count != 1 || paths.Any(p => p == "/"))
            {
                Console.Error.WriteLine("The paths of one 'sites get' must be in one folder on the site.");
                return 2;
            }

            request.RemoteFolder = folders[0];
            request.LocalFolder = Path.GetFullPath(to);
            request.Names = [.. paths.Select(p => new SiteTransferNameDto { Name = p[(p.LastIndexOf('/') + 1)..] })];
        }
        else
        {
            request.RemoteFolder = "/" + positional[2].Replace('\\', '/').Trim('/');
            var locals = positional.Skip(3).Select(p => Path.GetFullPath(p).TrimEnd('\\', '/')).ToList();
            var parents = locals.Select(Path.GetDirectoryName).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
            if (parents.Count != 1 || parents[0] is null)
            {
                Console.Error.WriteLine("The files and folders of one 'sites put' must be in one folder on this PC.");
                return 2;
            }

            request.LocalFolder = parents[0]!;
            request.Names = [.. locals.Select(p => new SiteTransferNameDto { Name = Path.GetFileName(p), IsFolder = Directory.Exists(p) })];
        }

        var reply = await client.AskAsync<SiteTransferResultDto>(SitesMessageTypes.TransferStart, request).ConfigureAwait(false);
        return PrintTransferResult(reply, json);
    }

    private static int PrintTransferResult(SiteTransferResultDto reply, bool json)
    {
        if (json) { PrintJson(reply); return reply.Ok ? 0 : 1; }
        if (!reply.Ok)
        {
            Console.Error.WriteLine($"failed: {reply.Message}");
            return 1;
        }

        if (reply.Transfer is { } transfer) Console.WriteLine(SiteTransferLine(transfer));
        else Console.WriteLine("ok");
        return 0;
    }

    internal static string SiteTransferLine(SiteTransferDto t)
    {
        var arrow = t.Direction == "download" ? "↓" : "↑";
        var route = t.Direction == "download" ? $"{t.SiteName}:{t.RemoteFolder} -> {t.LocalFolder}" : $"{t.LocalFolder} -> {t.SiteName}:{t.RemoteFolder}";
        var progress = $"{t.FilesDone + t.FilesSkipped}/{t.Files} {(t.Folders > 0 ? "items" : "files")}";
        if (t.FilesFailed > 0) progress += $", {t.FilesFailed} failed";
        var note = t.Message is { Length: > 0 } message ? $"  ({message})" : "";
        return $"{t.Id[..Math.Min(8, t.Id.Length)],-9} {arrow} {t.State,-9} {progress,-22} {route}{note}";
    }
}

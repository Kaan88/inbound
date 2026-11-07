using DownloadManager.Contracts;
using DownloadManager.Core;
using DownloadManager.Core.Providers;

namespace DownloadManager.Cli;

/// <summary>
/// <c>dm remote ...</c>. The key is read from standard input only, because anything on a command
/// line can be read by other programs and ends up in shell history. Nothing printed here holds a
/// link: the Host doesn't send any, and what it sends is scrubbed again on the way to the screen.
/// </summary>
internal static partial class Program
{
    private static readonly string[] RemoteValueOptions =
        ["--dest", "--queue", "--start", "--select", "--provider", "--offset", "--limit", "--item", "--add"];

    private static async Task<int> RemoteAsync(CliClient client, string[] args, bool json)
    {
        try
        {
            return await RemoteCommandAsync(client, args, json).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not HostUnavailableException)
        {
            Console.Error.WriteLine($"error: {Clean(ex.Message)}");
            return 1;
        }
    }

    private static async Task<int> RemoteCommandAsync(CliClient client, string[] args, bool json)
    {
        var words = RemotePositional(args);
        var sub = words.FirstOrDefault()?.ToLowerInvariant();
        switch (sub)
        {
            case "status":
            {
                var result = await AskAsync<RemoteAccountResult>(client, MessageTypes.RemoteAccountGet,
                    new RemoteAccountRequest { Provider = GetOption(args, "--provider"), Refresh = true }).ConfigureAwait(false);
                var jobs = await AskAsync<RemoteListResult>(client, MessageTypes.RemoteList, new { }).ConfigureAwait(false);
                if (json)
                {
                    PrintJson(new { result.Ok, result.Error, result.FailureKind, result.Accounts, RemoteJobs = jobs.Jobs.Count });
                    return result.Ok ? 0 : 1;
                }

                foreach (var account in result.Accounts) PrintAccount(account);
                Console.WriteLine($"remote jobs: {jobs.Jobs.Count}");
                return PrintProblem(result.Ok, result.Error);
            }

            case "key":
                return await RemoteKeyAsync(client, args, words, json).ConfigureAwait(false);

            case "classify":
            {
                if (words.Count != 2)
                {
                    return RemoteUsage("remote classify <link>\nUse - for <link> to read it from standard input, which keeps it off the command line.");
                }

                var result = await AskAsync<RemoteClassifyResult>(client, MessageTypes.RemoteClassify,
                    new RemoteClassifyRequest { Text = ReadLinkArgument(words[1]) ?? string.Empty }).ConfigureAwait(false);
                if (json)
                {
                    PrintJson(result);
                }
                else
                {
                    Console.WriteLine($"kind:     {result.Kind}");
                    if (result.ProviderName is not null) Console.WriteLine($"provider: {result.ProviderName}");
                    if (result.Name is not null) Console.WriteLine($"name:     {Clean(result.Name)}");
                    if (result.DisplayHost is not null) Console.WriteLine($"host:     {Clean(result.DisplayHost)}");
                    if (result.HosterName is not null) Console.WriteLine($"hoster:   {Clean(result.HosterName)}{(result.HosterAvailable == false ? " (down)" : string.Empty)}");
                    if (result.HosterNote is not null) Console.WriteLine($"note:     {Clean(result.HosterNote)}");
                    if (result.HosterDailyLinksLeft is { } linksLeft) Console.WriteLine($"today:    {linksLeft} links left");
                    if (result.HosterDailyBytesLeft is { } bytesLeft) Console.WriteLine($"today:    {FormatUtil.Bytes(bytesLeft)} left");
                    if (result.HosterLargestFile is { } largest) Console.WriteLine($"largest:  {FormatUtil.Bytes(largest)} per file");
                    if (result.Reason is not null) Console.WriteLine($"reason:   {Clean(result.Reason)}");
                    if (result.Note is not null) Console.WriteLine($"note:     {Clean(result.Note)}");
                }

                return result.Ok ? 0 : PrintProblem(false, result.Error);
            }

            case "add":
            {
                if (words.Count != 2)
                {
                    return RemoteUsage("remote add <link> [--approve-uncached] [--dest DIR] [--queue ID] [--start startNow|queue|later] [--select all|ask|ID,ID] [--link-password-stdin] [--archive-password-stdin]\n" +
                                       "Use - for <link> to read it from standard input, which keeps it off the command line.");
                }

                // Options of other commands, which would otherwise be taken here and do nothing.
                if (args.Any(arg => arg.Equals("--item", StringComparison.OrdinalIgnoreCase) || arg.Equals("--provider", StringComparison.OrdinalIgnoreCase) ||
                                    arg.Equals("--offset", StringComparison.OrdinalIgnoreCase) || arg.Equals("--limit", StringComparison.OrdinalIgnoreCase)))
                {
                    return RemoteUsage("remote add takes a link. --item, --provider, --offset and --limit aren't used by it.");
                }

                return await RemoteAddAsync(client, args, ReadLinkArgument(words[1]), json).ConfigureAwait(false);
            }

            case "add-torrent":
            {
                if (words.Count != 2)
                {
                    return RemoteUsage("remote add-torrent <file> [--approve-uncached] [--dest DIR] [--queue ID] [--start startNow|queue|later] [--select all|ask|ID,ID]");
                }

                // Before anything looks at it: even asking whether it exists sends this Windows account's credentials to that host.
                if (NamesAnotherComputer(words[1]))
                {
                    Console.Error.WriteLine("Only .torrent files on a drive of this computer can be added, not ones on a network path.");
                    return 2;
                }

                var path = Path.GetFullPath(words[1]);
                if (!File.Exists(path))
                {
                    Console.Error.WriteLine("That .torrent file doesn't exist.");
                    return 1;
                }

                return await RemoteAddAsync(client, args, path, json).ConfigureAwait(false);
            }

            case "list":
            {
                var result = await AskAsync<RemoteListResult>(client, MessageTypes.RemoteList, new { }).ConfigureAwait(false);
                if (json)
                {
                    PrintJson(result);
                }
                else if (result.Jobs.Count == 0)
                {
                    Console.WriteLine("No remote jobs.");
                }
                else
                {
                    Console.WriteLine($"{"ID",-32}  {"STATE",-18}  STATUS");
                    foreach (var job in result.Jobs)
                    {
                        Console.WriteLine($"{job.Id:N}  {job.State,-18}  {Clean(job.Status)}");
                        Console.WriteLine($"  {job.ProviderName} {job.Kind}: {Clean(job.Name ?? "(no name yet)")}{(job.DisplayHost is null ? string.Empty : $" from {Clean(job.DisplayHost)}")}");
                    }
                }

                return 0;
            }

            case "files":
            {
                if (await RemoteJobIdAsync(client, words, "remote files <id>").ConfigureAwait(false) is not { } id)
                {
                    return 2;
                }

                var result = await AskAsync<RemoteFilesResultDto>(client, MessageTypes.RemoteFiles, new RemoteJobIdRequest { JobId = id }).ConfigureAwait(false);
                if (json)
                {
                    PrintJson(result);
                }
                else if (result.Ok)
                {
                    if (result.Pending) Console.WriteLine("The provider is still working on this, so the list may not be final.");
                    PrintFiles(result.Files);
                }

                return json && result.Ok ? 0 : PrintProblem(result.Ok, result.Error);
            }

            case "select":
            {
                if (words.Count < 3 || await RemoteJobIdAsync(client, words, "remote select <id> all|<file-id>...").ConfigureAwait(false) is not { } id)
                {
                    return RemoteUsage("remote select <id> all|<file-id>...");
                }

                var chosen = words.Skip(2).SelectMany(word => word.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)).ToList();
                var all = chosen.Count == 1 && chosen[0].Equals("all", StringComparison.OrdinalIgnoreCase);
                return PrintRemoteAction(await AskAsync<RemoteActionResultDto>(client, MessageTypes.RemoteSelect,
                    new RemoteSelectRequest { JobId = id, All = all, FileIds = all ? null : chosen }).ConfigureAwait(false), json);
            }

            case "retry":
            {
                if (await RemoteJobIdAsync(client, words, "remote retry <id> [--approve-uncached] [--link-password-stdin]").ConfigureAwait(false) is not { } id)
                {
                    return 2;
                }

                return PrintRemoteAction(await AskAsync<RemoteActionResultDto>(client, MessageTypes.RemoteRetry, new RemoteRetryRequest
                {
                    JobId = id,
                    ApproveUncached = args.Contains("--approve-uncached", StringComparer.OrdinalIgnoreCase) ? true : null,
                    LinkPassword = args.Contains("--link-password-stdin", StringComparer.OrdinalIgnoreCase) ? ReadSecretLine("Link password: ") : null,
                }).ConfigureAwait(false), json);
            }

            case "check":
            {
                var request = new RemoteCheckNowRequest { Provider = GetOption(args, "--provider") };
                if (words.Count > 1)
                {
                    if (await RemoteJobIdAsync(client, words, "remote check [<id>]").ConfigureAwait(false) is not { } id)
                    {
                        return 2;
                    }

                    request.JobId = id;
                }

                return PrintRemoteAction(await AskAsync<RemoteActionResultDto>(client, MessageTypes.RemoteCheckNow, request).ConfigureAwait(false), json);
            }

            case "remove":
            {
                if (await RemoteJobIdAsync(client, words, "remote remove <id> [--delete-from-provider --yes]").ConfigureAwait(false) is not { } id)
                {
                    return 2;
                }

                if (!args.Contains("--delete-from-provider", StringComparer.OrdinalIgnoreCase))
                {
                    var removed = PrintRemoteAction(await AskAsync<RemoteActionResultDto>(client, MessageTypes.RemoteRemove, new RemoteJobIdRequest { JobId = id }).ConfigureAwait(false), json);
                    if (removed == 0 && !json) Console.WriteLine("Taken off this list. The item on the provider and any local downloads are as they were.");
                    return removed;
                }

                if (!args.Contains("--yes", StringComparer.OrdinalIgnoreCase))
                {
                    Console.Error.WriteLine("This deletes the item from the provider for good. Add --yes to confirm.");
                    return 2;
                }

                return PrintRemoteAction(await AskAsync<RemoteActionResultDto>(client, MessageTypes.RemoteDeleteRemote,
                    new RemoteDeleteRequest { JobId = id, Confirmed = true }).ConfigureAwait(false), json);
            }

            case "browse":
                return await RemoteBrowseAsync(client, args, words, json).ConfigureAwait(false);

            case "archive-password":
            {
                if (args.Any(arg => arg.StartsWith("--password", StringComparison.OrdinalIgnoreCase)))
                {
                    Console.Error.WriteLine("The password is read from standard input only.");
                    return 2;
                }

                if (await RemoteJobIdAsync(client, words, "remote archive-password <id>   (the password comes from standard input; empty clears it)").ConfigureAwait(false) is not { } id)
                {
                    return 2;
                }

                return PrintRemoteAction(await AskAsync<RemoteActionResultDto>(client, MessageTypes.RemoteArchivePassword,
                    new RemoteArchivePasswordRequest { JobId = id, Password = ReadArchivePassword() }).ConfigureAwait(false), json);
            }

            case "container":
                return await RemoteContainerAsync(client, args, words, json).ConfigureAwait(false);

            case "container-send-jd":
            {
                if (words.Count != 2)
                {
                    return RemoteUsage("remote container-send-jd <file.dlc|file.ccf>\nHands the file to a JDownloader running on this computer. It asks you to confirm, and the links stay there.");
                }

                if (NamesAnotherComputer(words[1]))
                {
                    Console.Error.WriteLine("Only files on a drive of this computer can be sent, not ones on a network path.");
                    return 2;
                }

                var sent = await AskAsync<RemoteContainerSendResult>(client, MessageTypes.RemoteContainerSendToJd,
                    new RemoteContainerSendRequest { Path = Path.GetFullPath(words[1]) }).ConfigureAwait(false);
                if (json)
                {
                    return PrintJsonResult(sent, sent.Ok);
                }

                if (sent.Ok) Console.WriteLine(Clean(sent.Message));
                return PrintProblem(sent.Ok, sent.Message);
            }

            default:
                return RemoteUsage("remote status|key|classify|add|add-torrent|list|files|select|retry|check|remove|browse|archive-password|container|container-send-jd");
        }
    }

    /// <summary>
    /// <c>dm add</c> means a direct request. With a provider account set up, a hoster link never gets
    /// one, so it is sent where it belongs, and <c>--direct</c> doesn't change that. It only answers the
    /// question when nobody can tell what a link is. A Host that can't answer has nothing to say about
    /// it, and ordinary downloads go on as ever. The same goes for each address of a batch or an import.
    /// </summary>
    private sealed class DirectAddCheck(CliClient client, string[] args)
    {
        private readonly bool _direct = args.Contains("--direct", StringComparer.OrdinalIgnoreCase);
        private bool _saidIncomplete;

        /// <returns>Why this address can't be added directly. Null when it can.</returns>
        public async Task<string?> WhyNotAsync(string url)
        {
            RemoteClassifyResult result;
            try
            {
                result = await AskAsync<RemoteClassifyResult>(client, MessageTypes.RemoteClassify, new RemoteClassifyRequest { Text = url }).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is not HostUnavailableException)
            {
                return null;
            }

            switch (result.Kind)
            {
                case "hosterLink":
                    return $"{Clean(result.HosterName ?? "This site")} goes through {Clean(result.ProviderName ?? "your provider")}, not by a direct request. Use: dm remote add - (and paste the link).";
                case "folderLink":
                    return $"This link is to an item on your {Clean(result.ProviderName ?? "provider")} account, not a direct download. Use: dm remote add - (and paste the link).";
                case "undetermined" when !_direct:
                    return $"{Clean(result.Reason ?? "It couldn't be checked whether this link goes through your provider.")} To request it directly anyway, add --direct.";
            }

            // Once per run: the same list is behind every address, and a script shouldn't stop for it.
            if (result.CheckIncomplete && !_direct && !_saidIncomplete)
            {
                _saidIncomplete = true;
                Console.Error.WriteLine(Clean(result.Note ?? "A listed site couldn't be checked against this link."));
            }

            return null;
        }
    }

    private static async Task<int> RemoteKeyAsync(CliClient client, string[] args, List<string> words, bool json)
    {
        var provider = GetOption(args, "--provider");
        switch (words.ElementAtOrDefault(1)?.ToLowerInvariant())
        {
            case "set":
            {
                if (words.Count > 2)
                {
                    // Not repeated, and not used. It is in the shell's history by now, so say so.
                    Console.Error.WriteLine("The key is only read from standard input, so that one was not used. Pipe it in, or run this and type it. What you just typed may be in your shell history.");
                    return 2;
                }

                var key = ReadSecretLine("API key: ");
                if (string.IsNullOrWhiteSpace(key))
                {
                    Console.Error.WriteLine("No key was given. Pipe it in, or run this and type it.");
                    return 2;
                }

                var saved = await AskAsync<RemoteAccountResult>(client, MessageTypes.RemoteAccountSetKey,
                    new RemoteSetKeyRequest { Provider = provider, Key = key }).ConfigureAwait(false);
                if (!saved.Ok)
                {
                    return json ? PrintJsonResult(saved, saved.Ok) : PrintProblem(false, saved.Error);
                }

                var checkedKey = await AskAsync<RemoteAccountResult>(client, MessageTypes.RemoteAccountValidate,
                    new RemoteAccountRequest { Provider = provider }).ConfigureAwait(false);
                if (json)
                {
                    return PrintJsonResult(checkedKey, checkedKey.Ok);
                }

                Console.WriteLine("Key saved.");
                foreach (var account in checkedKey.Accounts) PrintAccount(account);
                return PrintProblem(checkedKey.Ok, checkedKey.Error);
            }

            case "clear":
            {
                var cleared = await AskAsync<RemoteAccountResult>(client, MessageTypes.RemoteAccountClearKey,
                    new RemoteAccountRequest { Provider = provider }).ConfigureAwait(false);
                if (json)
                {
                    return PrintJsonResult(cleared, cleared.Ok);
                }

                if (cleared.Ok) Console.WriteLine("Key removed from this computer.");
                return PrintProblem(cleared.Ok, cleared.Error);
            }

            default:
                return RemoteUsage("remote key set|clear [--provider ID]\nThe key is read from standard input, never from the command line.");
        }
    }

    private static async Task<int> RemoteAddAsync(CliClient client, string[] args, string? input, bool json)
    {
        if (string.IsNullOrWhiteSpace(input))
        {
            Console.Error.WriteLine("No link was given.");
            return 2;
        }

        var request = new RemoteAddRequestDto
        {
            Input = input,
            Destination = GetOption(args, "--dest") is { } dest ? Path.GetFullPath(dest) : null,
            StartMode = GetOption(args, "--start") ?? "startNow",
            ApproveUncached = args.Contains("--approve-uncached", StringComparer.OrdinalIgnoreCase),
        };
        if (args.Contains("--link-password-stdin", StringComparer.OrdinalIgnoreCase))
        {
            request.LinkPassword = ReadSecretLine("Link password: ");
        }

        if (args.Contains("--archive-password-stdin", StringComparer.OrdinalIgnoreCase))
        {
            request.ArchivePassword = ReadArchivePassword();
        }

        if (int.TryParse(GetOption(args, "--queue"), out var queue))
        {
            request.QueueId = queue;
            if (GetOption(args, "--start") is null)
            {
                request.StartMode = "queue";
            }
        }

        if (GetOption(args, "--select") is { } select)
        {
            if (select.Equals("all", StringComparison.OrdinalIgnoreCase) || select.Equals("ask", StringComparison.OrdinalIgnoreCase))
            {
                request.Selection = select.ToLowerInvariant();
            }
            else
            {
                request.Selection = "chosen";
                request.FileIds = select.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).ToList();
            }
        }

        var result = await AskAsync<RemoteAddResultDto>(client, MessageTypes.RemoteAdd, request).ConfigureAwait(false);
        if (json)
        {
            return PrintJsonResult(result, result.Ok);
        }

        if (result.Job is { } job)
        {
            Console.WriteLine(result.Existing ? "Already on the list:" : "Added:");
            PrintRemoteJob(job);
        }

        if (result.Note is not null) Console.WriteLine($"note:     {Clean(result.Note)}");
        return PrintProblem(result.Ok, result.Error);
    }

    private static async Task<int> RemoteContainerAsync(CliClient client, string[] args, List<string> words, bool json)
    {
        const string usage = "remote container <file> [--add ID,ID | --all] [--dest DIR] [--approve-uncached] [--queue ID] [--start startNow|queue|later]";
        if (words.Count != 2)
        {
            return RemoteUsage(usage);
        }

        var all = args.Contains("--all", StringComparer.OrdinalIgnoreCase);
        var addOption = GetOption(args, "--add");
        if (all && addOption is not null)
        {
            return RemoteUsage(usage);
        }

        List<int>? chosen = null;
        if (addOption is not null)
        {
            chosen = [];
            foreach (var word in addOption.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
            {
                if (!int.TryParse(word, out var id))
                {
                    return RemoteUsage(usage);
                }

                chosen.Add(id);
            }

            if (chosen.Count == 0)
            {
                return RemoteUsage(usage);
            }
        }

        if (NamesAnotherComputer(words[1]))
        {
            Console.Error.WriteLine("Only files on a drive of this computer can be opened, not ones on a network path.");
            return 2;
        }

        var preview = await AskAsync<RemoteContainerPreviewResult>(client, MessageTypes.RemoteContainerPreview,
            new RemoteContainerPreviewRequest { Path = Path.GetFullPath(words[1]) }).ConfigureAwait(false);
        if (!json)
        {
            PrintContainerPreview(preview);
        }

        if (!preview.Ok)
        {
            if (json)
            {
                return PrintJsonResult(preview, false);
            }

            if (preview.NotSupported && preview.CanSendToJd)
            {
                Console.Error.WriteLine("To send it to a JDownloader running on this computer: dm remote container-send-jd <file>");
            }

            return PrintProblem(false, preview.Error);
        }

        if (!all && chosen is null)
        {
            return json ? PrintJsonResult(preview, true) : 0;
        }

        chosen ??= preview.Entries.Where(entry => entry.CanAdd).Select(entry => entry.EntryId).ToList();
        if (preview.PreviewId is not { } previewId || chosen.Count == 0)
        {
            Console.Error.WriteLine("Nothing in this file can be added.");
            return 1;
        }

        var request = new RemoteContainerAddRequest
        {
            PreviewId = previewId,
            EntryIds = chosen,
            Destination = GetOption(args, "--dest") is { } dest ? Path.GetFullPath(dest) : null,
            StartMode = GetOption(args, "--start") ?? "startNow",
            ApproveUncached = args.Contains("--approve-uncached", StringComparer.OrdinalIgnoreCase),
        };
        if (int.TryParse(GetOption(args, "--queue"), out var queue))
        {
            request.QueueId = queue;
            if (GetOption(args, "--start") is null)
            {
                request.StartMode = "queue";
            }
        }

        var added = await AskAsync<RemoteContainerAddResult>(client, MessageTypes.RemoteContainerAdd, request).ConfigureAwait(false);
        if (json)
        {
            PrintJson(new { Preview = preview, Added = added });
            return added.Ok && added.Outcomes.All(outcome => outcome.Ok) ? 0 : 1;
        }

        foreach (var outcome in added.Outcomes)
        {
            if (outcome.Job is { } job)
            {
                Console.WriteLine($"{outcome.EntryId,5}  {(outcome.Existing ? "already on the list" : "added")}: {job.Id:N}  {Clean(job.Status)}");
                if (job.ApprovalNeeded) Console.WriteLine($"       next: dm remote retry {job.Id:N} --approve-uncached");
            }
            else if (outcome.Ok)
            {
                Console.WriteLine($"{outcome.EntryId,5}  {(outcome.Existing ? "already on the list" : "added")}: {outcome.JobId:N}");
            }
            else
            {
                Console.Error.WriteLine($"{outcome.EntryId,5}  not added: {Clean(outcome.Error)}");
            }

            if (outcome.Note is not null) Console.WriteLine($"       note: {Clean(outcome.Note)}");
        }

        return PrintProblem(added.Ok && added.Outcomes.All(outcome => outcome.Ok), added.Error ?? "Not every entry was added.");
    }

    private static void PrintContainerPreview(RemoteContainerPreviewResult preview)
    {
        Console.WriteLine($"format:   {preview.Format}{(preview.FormatVersion is null ? string.Empty : $" {Clean(preview.FormatVersion)}")}");
        if (preview.AccountProblem is not null) Console.WriteLine($"account:  {Clean(preview.AccountProblem)}");
        foreach (var problem in preview.Problems) Console.WriteLine($"note:     {Clean(problem)}");
        foreach (var package in preview.Packages)
        {
            var password = package.HasArchivePassword ? ", with an archive password" : string.Empty;
            Console.WriteLine($"package {package.Index}: {Clean(package.Name ?? "(no name)")} ({package.EntryCount} {(package.EntryCount == 1 ? "link" : "links")}{password})");
            if (package.Comment is not null) Console.WriteLine($"  comment: {Clean(package.Comment)}");
        }

        if (preview.Entries.Count == 0)
        {
            return;
        }

        Console.WriteLine($"{"ID",5}  {"PKG",3}  {"KIND",-12}  {"HOST",-24}  NAME");
        foreach (var entry in preview.Entries)
        {
            Console.WriteLine($"{entry.EntryId,5}  {(entry.PackageIndex is { } index ? index.ToString() : "-"),3}  {entry.Kind,-12}  {Clean(entry.DisplayHost ?? "-"),-24}  {Clean(entry.DisplayName ?? "(no name)")}");
            if (entry.Reason is not null) Console.WriteLine($"       {Clean(entry.Reason)}");
        }
    }

    /// <summary>Before anything looks at the path: even asking whether it exists sends this Windows account's credentials to the host it names.</summary>
    private static bool NamesAnotherComputer(string path)
        => path.Length >= 2 && path[0] is '\\' or '/' && path[1] is '\\' or '/' or '?';

    private static async Task<int> RemoteBrowseAsync(CliClient client, string[] args, List<string> words, bool json)
    {
        if (words.Count != 2)
        {
            return RemoteUsage("remote browse torrents|webdl|usenet [--offset N] [--limit N] [--item ID] [--provider ID]");
        }

        var request = new RemoteItemRequest { Kind = words[1].ToLowerInvariant(), Provider = GetOption(args, "--provider"), ItemId = GetOption(args, "--item"), Fresh = true };
        if (int.TryParse(GetOption(args, "--offset"), out var offset))
        {
            request.Offset = offset;
        }

        if (int.TryParse(GetOption(args, "--limit"), out var limit))
        {
            request.Limit = limit;
        }

        var result = await AskAsync<RemoteItemResult>(client, MessageTypes.RemoteItemGet, request).ConfigureAwait(false);
        if (json)
        {
            return PrintJsonResult(result, result.Ok);
        }

        if (!result.Ok)
        {
            return PrintProblem(false, result.Error);
        }

        if (result.Items.Count == 0)
        {
            Console.WriteLine("Nothing on this page.");
            return 0;
        }

        Console.WriteLine($"{"ID",-14} {"STAGE",-16} {"SIZE",10} {"PROGRESS",9}  NAME");
        foreach (var item in result.Items)
        {
            Console.WriteLine($"{Clean(item.ItemId),-14} {item.Stage,-16} {FormatUtil.Bytes(item.Size),10} {item.Progress * 100,8:0.0}%  {Clean(item.Name)}");
            if (item.Error is not null) Console.WriteLine($"  error: {Clean(item.Error)}");
            if (item.Files is not null) PrintFiles(item.Files);
        }

        if (result.HasMore) Console.WriteLine($"More: --offset {result.Offset + result.Limit}");
        return 0;
    }

    private static async Task<T> AskAsync<T>(CliClient client, string type, object payload)
    {
        var reply = await client.RequestAsync(type, payload).ConfigureAwait(false);
        if (reply.Type == MessageTypes.HostActionResult)
        {
            throw new InvalidOperationException(reply.GetPayload<HostActionResult>().Message ?? "The Host didn't take this command.");
        }

        return reply.GetPayload<T>();
    }

    /// <summary>A job id in full, or enough of its start to tell it from the others.</summary>
    private static async Task<Guid?> RemoteJobIdAsync(CliClient client, List<string> words, string usage)
    {
        var text = words.ElementAtOrDefault(1);
        if (string.IsNullOrWhiteSpace(text))
        {
            RemoteUsage(usage);
            return null;
        }

        if (Guid.TryParse(text, out var id))
        {
            return id;
        }

        var list = await AskAsync<RemoteListResult>(client, MessageTypes.RemoteList, new { }).ConfigureAwait(false);
        var matches = list.Jobs.Where(job => job.Id.ToString("N").StartsWith(text, StringComparison.OrdinalIgnoreCase)).ToList();
        if (matches.Count == 1)
        {
            return matches[0].Id;
        }

        Console.Error.WriteLine(matches.Count == 0 ? "No remote job has that id." : "More than one remote job starts with that.");
        return null;
    }

    private static List<string> RemotePositional(string[] args)
    {
        var words = new List<string>();
        for (var i = 1; i < args.Length; i++)
        {
            if (RemoteValueOptions.Contains(args[i], StringComparer.OrdinalIgnoreCase))
            {
                i++;
            }
            else if (!args[i].StartsWith("--", StringComparison.Ordinal))
            {
                words.Add(args[i]);
            }
        }

        return words;
    }

    /// <summary>A password as typed: spaces can be part of one, so only the line's end comes off.</summary>
    private static string ReadArchivePassword() => SecretInput.ReadLine("Archive password: ") ?? string.Empty;

    /// <summary>One line from standard input. Typed at a console, it isn't shown.</summary>
    private static string? ReadSecretLine(string prompt) => SecretInput.ReadLine(prompt)?.Trim();

    /// <summary>
    /// For what goes to the screen. A provider's words can hold escape sequences, which a terminal would
    /// act on rather than show, so they could rewrite what was printed above them.
    /// </summary>
    private static string Clean(string? text)
    {
        var hidden = UrlRedactor.HideAddresses(text);
        return hidden.Any(FileNameResolver.IsDeceptiveOrControl)
            ? new string(hidden.Select(c => FileNameResolver.IsDeceptiveOrControl(c) ? ' ' : c).ToArray())
            : hidden;
    }

    /// <summary>A link from the arguments, or from standard input for "-". Arguments stay in shell history, which is worth saying about one holding a secret.</summary>
    private static string? ReadLinkArgument(string word)
    {
        if (word == "-")
        {
            return ReadSecretLine("Link: ");
        }

        if (word.StartsWith("magnet:", StringComparison.OrdinalIgnoreCase) || word.Contains('#', StringComparison.Ordinal) ||
            word != UrlRedactor.Redact(word))
        {
            Console.Error.WriteLine("That link may hold a key or a password, and it may now be in your shell history. Use - next time to read it from standard input.");
        }

        return word;
    }

    private static int RemoteUsage(string usage)
    {
        Console.Error.WriteLine($"usage: {usage}");
        return 2;
    }

    private static int PrintProblem(bool ok, string? error)
    {
        if (!ok) Console.Error.WriteLine(Clean(error ?? "That didn't work."));
        return ok ? 0 : 1;
    }

    private static int PrintJsonResult<T>(T result, bool ok)
    {
        PrintJson(result);
        return ok ? 0 : 1;
    }

    private static int PrintRemoteAction(RemoteActionResultDto result, bool json)
    {
        if (json)
        {
            return PrintJsonResult(result, result.Ok);
        }

        if (result.Job is { } job) PrintRemoteJob(job);
        return PrintProblem(result.Ok, result.Error);
    }

    private static void PrintAccount(RemoteAccountDto account)
    {
        var key = account.KeyUnreadable ? "saved, but it can't be read on this Windows account" : account.HasKey ? "set" : "not set";
        Console.WriteLine($"{account.ProviderName} key: {key}");
        if (account.CheckedAt is null)
        {
            return;
        }

        var until = account.PremiumUntil is { } end ? $", until {end:yyyy-MM-dd}" : string.Empty;
        Console.WriteLine($"  plan: {Clean(account.Plan ?? "unknown")}{until}");
        if (account.CooldownUntil is { } cooldown && cooldown > DateTimeOffset.UtcNow) Console.WriteLine($"  cooling down until {cooldown:yyyy-MM-dd HH:mm} UTC");
    }

    private static void PrintRemoteJob(RemoteJobDto job)
    {
        Console.WriteLine($"id:       {job.Id:N}");
        Console.WriteLine($"provider: {job.ProviderName} {job.Kind}");
        if (job.Name is not null) Console.WriteLine($"name:     {Clean(job.Name)}");
        if (job.DisplayHost is not null) Console.WriteLine($"from:     {Clean(job.DisplayHost)}");
        Console.WriteLine($"state:    {job.State}");
        Console.WriteLine($"status:   {Clean(job.Status)}");
        if (job.ApprovalNeeded) Console.WriteLine($"next:     dm remote retry {job.Id:N} --approve-uncached");
        if (job.FailureMessage is not null) Console.WriteLine($"failure:  {Clean(job.FailureMessage)}");
        foreach (var note in job.Notes) Console.WriteLine($"note:     {Clean(note)}");
    }

    private static void PrintFiles(IReadOnlyList<RemoteFileDto> files)
    {
        foreach (var file in files)
        {
            var note = file.Note is null ? string.Empty : $"  ({Clean(file.Note)})";
            Console.WriteLine($"  {Clean(file.FileId),-10} {FormatUtil.Bytes(file.Size),10}  {Clean(file.RelativePath ?? "(unsafe path)")}{note}");
        }
    }
}

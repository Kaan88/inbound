using System.Diagnostics;
using System.Globalization;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using DownloadManager.Contracts;
using DownloadManager.Core;
using DownloadManager.Core.Providers;

namespace DownloadManager.Cli;

internal static partial class Program
{
    private static readonly JsonSerializerOptions PrettyJson = new(IpcJson.Options) { WriteIndented = true };

    /// <summary>
    /// The name this program was started by (DownloadManager.Cli as Setup installs it), for the help and the errors that
    /// say what to run; the help's examples write it "dm".
    /// </summary>
    internal static string ProgramName { get; } =
        Path.GetFileNameWithoutExtension(Environment.ProcessPath) is { Length: > 0 } name && !name.Equals("dotnet", StringComparison.OrdinalIgnoreCase)
            ? name
            : "DownloadManager.Cli";

    private static async Task<int> Main(string[] args)
    {
        Console.OutputEncoding = Encoding.UTF8;
        if (args.Length == 0 || args[0] is "-h" or "--help" or "help")
        {
            PrintHelp();
            return 0;
        }

        if (args[0] is "-v" or "--version" or "version")
        {
            Console.WriteLine($"Inbound Download Manager CLI {Protocol.HostVersion} (protocol {Protocol.Version})");
            return 0;
        }

        try
        {
            return await RunAsync(args).ConfigureAwait(false);
        }
        catch (HostUnavailableException ex)
        {
            Console.Error.WriteLine(ex.Message);
            return 3;
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"error: {ex.Message}");
            return 1;
        }
    }

    private static async Task<int> RunAsync(string[] args)
    {
        var command = args[0].ToLowerInvariant();
        var json = args.Contains("--json", StringComparer.OrdinalIgnoreCase);
        var noStart = args.Contains("--no-start", StringComparer.OrdinalIgnoreCase);
        var valueFlags = CliArgs.ValueFlagsOf(command);
        var readHere = CliArgs.ReadsArgumentsHere(command);
        // torrent, archive and remote read a bare flag's value themselves; "--flag=" with nothing after it is refused for all.
        if (CliArgs.MissingValue(args, valueFlags, nextMustBeValue: readHere) is { } bare)
        {
            var flag = bare.TrimEnd('=');
            Console.Error.WriteLine(readHere
                ? $"{flag} needs a value. A value that starts with \"--\" is given as {flag}=VALUE."
                : $"{flag} needs a value.");
            return 2;
        }

        // A --name=value this command has no such flag for is refused, not dropped without a word (only the name is
        // repeated: the value may be a secret).
        if (CliArgs.UnknownJoined(args, valueFlags) is { } unknown)
        {
            Console.Error.WriteLine($"{unknown} does not take a value with \"=\" in '{command}'. Run \"{ProgramName} --help\" for the flags.");
            return 2;
        }

        if (command == "torrent" && CliArgs.JoinedValueStartingWithDashes(args, valueFlags) is { } dashed)
        {
            Console.Error.WriteLine($"{dashed} in 'torrent' does not take a value that starts with \"--\".");
            return 2;
        }

        args = CliArgs.Expand(args, valueFlags);

        var optionArgs = CliArgs.Positional(args);

        if (command == "doctor")
        {
            return await DoctorAsync(noStart).ConfigureAwait(false);
        }

        await using var client = await CliClient.ConnectAsync(startHost: !noStart).ConfigureAwait(false);

        switch (command)
        {
            case "add-batch":
                return await AddBatchAsync(client, args, optionArgs, json).ConfigureAwait(false);

            case "add":
            {
                if (optionArgs.Length == 0)
                {
                    Console.Error.WriteLine("usage: add <url> [--dest DIR | --to FAVORITE] [--name NAME] [--category ID] [--start now|later] [--priority high|normal|low] [--package KEY] [--package-name NAME] [--direct] [--wait]");
                    return 2;
                }

                if (await new DirectAddCheck(client, args).WhyNotAsync(optionArgs[0]).ConfigureAwait(false) is { } refused)
                {
                    Console.Error.WriteLine(refused);
                    return 2;
                }

                var conflict = GetOption(args, "--conflict")?.ToLowerInvariant();
                if (conflict is not null && conflict is not ("rename" or "overwrite" or "fail"))
                {
                    Console.Error.WriteLine("--conflict expects rename, overwrite, or fail.");
                    return 2;
                }

                var (dest, destError) = await DestinationAsync(client, args).ConfigureAwait(false);
                if (destError is not null)
                {
                    Console.Error.WriteLine(destError);
                    return 2;
                }

                var request = new AddJobRequest
                {
                    Url = optionArgs[0],
                    DestinationDirectory = dest,
                    FileName = GetOption(args, "--name"),
                    CategoryId = GetOption(args, "--category"),
                    StartMode = GetOption(args, "--start") ?? "now",
                    ConflictPolicy = conflict ?? "rename",
                    Description = GetOption(args, "--description"),
                    Referrer = GetOption(args, "--referrer"),
                    UserAgent = GetOption(args, "--user-agent"),
                    ExpectedSha256 = GetOption(args, "--sha256"),
                    // Adds that share a key form one package, as an Add Links batch does.
                    PackageKey = GetOption(args, "--package"),
                    PackageName = GetOption(args, "--package-name"),
                    Source = "cli",
                };
                if (GetOption(args, "--priority") is { } priorityText)
                {
                    if (!JobPriorities.TryParse(priorityText, out var priority))
                    {
                        Console.Error.WriteLine("--priority expects high, normal, or low.");
                        return 2;
                    }

                    request.Priority = priority;
                }

                if (!CliArgs.TryInt(args, "--connections", out var connections, out var numberError) ||
                    !CliArgs.TryInt(args, "--queue", out var queueId, out numberError))
                {
                    Console.Error.WriteLine(numberError);
                    return 2;
                }

                request.Connections = connections;
                // No --queue: the default queue, as add-batch and the app send (-1 names no queue; 0 is one queue's id).
                request.QueueId = queueId ?? -1;

                foreach (var header in GetOptions(args, "--header"))
                {
                    var separator = header.IndexOf(':');
                    if (separator > 0)
                    {
                        request.Headers[header[..separator].Trim()] = header[(separator + 1)..].Trim();
                    }
                }

                var result = await client.AskAsync<AddJobResponse>(MessageTypes.JobAdd, request).ConfigureAwait(false);
                if (json)
                {
                    PrintJson(result);
                }
                else if (!result.Accepted)
                {
                    Console.Error.WriteLine($"add failed: {result.Error}");
                }
                else
                {
                    Console.WriteLine($"{(result.Duplicate ? "Already queued" : "Added")}: {result.Job!.Id:N}  {result.Job.FileName}");
                    Console.WriteLine($"  state: {result.Job.State}  dest: {result.Job.DestinationDirectory}");
                }

                if (!result.Accepted)
                {
                    return 1;
                }

                if (CliArgs.HasFlag(args, "--wait"))
                {
                    return await WaitForAsync(client, result.Job!.Id, json).ConfigureAwait(false);
                }

                return 0;
            }

            case "list":
            {
                var snapshot = await client.SnapshotAsync().ConfigureAwait(false);
                if (json)
                {
                    PrintJson(snapshot);
                }
                else
                {
                    PrintJobs(snapshot.Jobs);
                }

                return 0;
            }

            case "status":
            {
                if (optionArgs.FirstOrDefault() is not { } given)
                {
                    Console.Error.WriteLine("usage: status <job-id>");
                    return 2;
                }

                // Text that isn't an id can't be one in the list either; it is said so, as an id that isn't there is.
                var snapshot = await client.SnapshotAsync().ConfigureAwait(false);
                var job = Guid.TryParse(given, out var id) ? snapshot.Jobs.FirstOrDefault(j => j.Id == id) : null;
                if (job is null)
                {
                    Console.Error.WriteLine($"No download with the ID '{TorrentCommands.Printable(given)}'. \"{ProgramName} list\" shows each one's ID.");
                    return 1;
                }

                if (json)
                {
                    PrintJson(job);
                }
                else
                {
                    PrintJobDetails(job);
                }

                return 0;
            }

            case "profile":
                return await ProfileAsync(client, optionArgs, json).ConfigureAwait(false);

            case "update":
                return await UpdateAsync(client, optionArgs, json).ConfigureAwait(false);

            case "export":
            {
                if (optionArgs.Length < 1)
                {
                    Console.Error.WriteLine("usage: export <file.json>");
                    return 2;
                }

                var snapshot = await client.SnapshotAsync().ConfigureAwait(false);
                // A torrent is saved with its .torrent and file choice, which only the Host has; an older Host's alone.
                IReadOnlyDictionary<Guid, TorrentListEntry>? torrents = null;
                if (snapshot.Jobs.Any(job => job.Torrent is not null))
                {
                    // Page by page, so a long list stays under the message size limit.
                    var all = new Dictionary<Guid, TorrentListEntry>();
                    Guid? after = null;
                    do
                    {
                        var listed = await client.RequestAsync(TorrentMessageTypes.ListEntries, new TorrentListEntriesRequest { After = after }).ConfigureAwait(false);
                        if (listed.Type != TorrentMessageTypes.ListEntriesResult)
                        {
                            // A Host without torrents saves them as magnets; an error is not read as "no torrents"
                            // (audit 4 M12, N5): the list would be written without them and said to be exported.
                            if (TorrentCommands.HostError(listed) is { } error)
                            {
                                Console.Error.WriteLine($"export failed: {error}");
                                return 1;
                            }

                            break;
                        }

                        var page = listed.GetPayload<TorrentListEntriesResult>();
                        foreach (var (id, entry) in page.Entries) all[id] = entry;
                        after = page.Next is { } next && next != after ? next : null;
                    }
                    while (after is not null);

                    if (all.Count > 0) torrents = all;
                }

                var content = JobListFile.Serialize(snapshot.Jobs, torrents);
                await WriteFileAtomicallyAsync(optionArgs[0], content).ConfigureAwait(false);
                Console.WriteLine($"exported {snapshot.Jobs.Count} downloads to {optionArgs[0]}");
                return 0;
            }

            case "import":
            {
                if (optionArgs.Length < 1)
                {
                    Console.Error.WriteLine("usage: import <file.json> [--start now|later] [--direct] [--yes]");
                    return 2;
                }

                IReadOnlyList<JobListEntry> entries;
                try
                {
                    entries = JobListFile.Deserialize(await File.ReadAllTextAsync(optionArgs[0])
                        .ConfigureAwait(false));
                }
                catch (Exception ex) when (ex is IOException or FormatException)
                {
                    Console.Error.WriteLine($"import failed: {ex.Message}");
                    return 2;
                }

                if (entries.Count == 0)
                {
                    Console.Error.WriteLine("import file has no downloads.");
                    return 2;
                }

                if (entries.Count > 25 && !CliArgs.HasFlag(args, "--yes"))
                {
                    Console.Error.WriteLine(
                        $"import contains {entries.Count} downloads; re-run with --yes to add them all.");
                    return 2;
                }

                var startOverride = GetOption(args, "--start");
                var check = new DirectAddCheck(client, args);
                var accepted = 0;
                var duplicates = 0;
                var failed = 0;
                string? firstError = null;
                // Torrents go to the Host's torrent import: one already in the list, or every one while torrents are off, is skipped.
                var torrentEntries = entries.Where(JobListFile.IsTorrent).ToList();
                var skipped = 0;
                string? skipReason = null;
                if (torrentEntries.Count > 0)
                {
                    // In requests that each fit one message.
                    var sent = 0;
                    foreach (var chunk in TorrentListPaging.Chunk(torrentEntries))
                    {
                        var imported = await client.RequestAsync(TorrentMessageTypes.Import,
                            new TorrentImportRequest { Entries = chunk, StartMode = startOverride }).ConfigureAwait(false);
                        if (imported.Type == TorrentMessageTypes.ImportResult)
                        {
                            var result = imported.GetPayload<TorrentImportResult>();
                            accepted += result.Added;
                            failed += result.Failed;
                            firstError ??= result.FirstError;
                            skipped += result.Skipped;
                            skipReason ??= result.SkipReason;
                            sent += chunk.Count;
                        }
                        else if (TorrentCommands.HostError(imported) is { } error)
                        {
                            // The Host failed them with an error of its own (audit 4 M12, N5): they failed, in its words.
                            failed += torrentEntries.Count - sent;
                            firstError ??= error;
                            break;
                        }
                        else
                        {
                            skipped += torrentEntries.Count - sent;
                            skipReason ??= "This Host does not download torrents.";
                            break;
                        }
                    }
                }

                foreach (var entry in entries.Where(entry => !JobListFile.IsTorrent(entry)))
                {
                    // The schemes a download can have, and the ones dm export writes (FTP among them). The address is
                    // shown without a user name or password it may hold.
                    if (!Uri.TryCreate(entry.Url, UriKind.Absolute, out var uri) ||
                        uri.Scheme is not ("http" or "https" or "ftp"))
                    {
                        failed++;
                        firstError ??= $"Invalid URL: {UrlRedactor.Redact(entry.Url)}";
                        continue;
                    }

                    try
                    {
                        if (await check.WhyNotAsync(entry.Url).ConfigureAwait(false) is { } refused)
                        {
                            failed++;
                            firstError ??= refused;
                            continue;
                        }

                        var result = await client.AskAsync<AddJobResponse>(MessageTypes.JobAdd, new AddJobRequest
                        {
                            Url = entry.Url,
                            FileName = entry.FileName,
                            CategoryId = entry.CategoryId,
                            DestinationDirectory = entry.DestinationDirectory,
                            Description = entry.Description,
                            QueueId = entry.QueueId,
                            Connections = entry.RequestedConnections > 0 ? entry.RequestedConnections : null,
                            StartMode = startOverride ?? entry.StartMode,
                            ConflictPolicy = entry.ConflictPolicy,
                            ExpectedSha256 = entry.ExpectedSha256,
                            ContentType = entry.ContentType,
                            // UI.B14: headless, and no completion window per job either (DownloadWindows,
                            // MainWindow), which a long list would flood. Shown as "App", as "import" was.
                            Source = "cliImport",
                        }).ConfigureAwait(false);
                        if (result.Accepted)
                        {
                            accepted++;
                            if (result.Duplicate)
                            {
                                duplicates++;
                            }
                        }
                        else
                        {
                            failed++;
                            firstError ??= result.Error;
                        }
                    }
                    catch (Exception ex)
                    {
                        failed++;
                        firstError ??= ex.Message;
                    }
                }

                Console.WriteLine(
                    $"imported {accepted} of {entries.Count} downloads" +
                    (duplicates > 0 ? $" ({duplicates} already queued)" : string.Empty) +
                    (failed > 0 ? $"; {failed} failed: {firstError}" : ".") +
                    (skipped > 0 ? $" {skipped} torrent{(skipped == 1 ? string.Empty : "s")} skipped: {skipReason}" : string.Empty));
                return failed > 0 ? 1 : 0;
            }

            case "open":
            case "reveal":
            {
                if (optionArgs.Length < 1 || !Guid.TryParse(optionArgs[0], out var shellJobId))
                {
                    Console.Error.WriteLine($"usage: {command} <job-id>");
                    return 2;
                }

                var snapshot = await client.SnapshotAsync().ConfigureAwait(false);
                var job = snapshot.Jobs.FirstOrDefault(candidate => candidate.Id == shellJobId);
                if (job is null)
                {
                    Console.Error.WriteLine("job not found.");
                    return 1;
                }

                var path = string.IsNullOrWhiteSpace(job.FinalFilePath)
                    ? job.DestinationDirectory
                    : job.FinalFilePath;
                if (command == "open" && !File.Exists(path))
                {
                    Console.Error.WriteLine($"file does not exist: {path}");
                    return 1;
                }

                var start = command == "open"
                    ? ShellActions.OpenFile(path)
                    : ShellActions.RevealFile(path);
                ShellActions.Start(start)?.Dispose();
                Console.WriteLine($"{command}: {path}");
                return 0;
            }

            case "describe":
            {
                if (optionArgs.Length < 2 || !Guid.TryParse(optionArgs[0], out var describeId))
                {
                    Console.Error.WriteLine("usage: describe <job-id> <description text>");
                    return 2;
                }

                var reply = await client.RequestAsync(MessageTypes.JobUpdate, new JobUpdateRequest
                {
                    JobId = describeId,
                    Description = string.Join(' ', optionArgs.Skip(1)),
                }).ConfigureAwait(false);
                return PrintAction(reply, json);
            }

            case "priority":
            {
                if (optionArgs.Length < 2 || !Guid.TryParse(optionArgs[0], out var priorityId) ||
                    !JobPriorities.TryParse(optionArgs[1], out var priority))
                {
                    Console.Error.WriteLine("usage: priority <job-id> high|normal|low");
                    return 2;
                }

                var reply = await client.RequestAsync(MessageTypes.JobUpdate,
                    new JobUpdateRequest { JobId = priorityId, Priority = priority }).ConfigureAwait(false);
                return PrintAction(reply, json);
            }

            case "archive":
                return await ArchiveCommands.RunAsync(client, args.Skip(1).ToArray(), json).ConfigureAwait(false);

            case "torrent":
                return await TorrentCommands.RunAsync(client, args.Skip(1).ToArray(), json).ConfigureAwait(false);

            case "package":
                return await PackageAsync(client, optionArgs, json).ConfigureAwait(false);

            case "pause":
                return await SimpleJobActionAsync(client, optionArgs, command, MessageTypes.JobPause, json).ConfigureAwait(false);
            case "resume":
            case "retry":
                return await SimpleJobActionAsync(client, optionArgs, command, MessageTypes.JobResume, json).ConfigureAwait(false);
            case "refresh":
            {
                if (optionArgs.Length < 2 || !Guid.TryParse(optionArgs[0], out var refreshId))
                {
                    Console.Error.WriteLine("usage: refresh <job-id> <new-url> [--direct]");
                    return 2;
                }

                var reply = await client.RequestAsync(MessageTypes.JobRefreshUrl,
                    new JobRefreshUrlRequest
                    {
                        JobId = refreshId,
                        Url = optionArgs[1],
                        ExplicitDirect = CliArgs.HasFlag(args, "--direct"),
                    }).ConfigureAwait(false);
                return PrintAction(reply, json);
            }
            case "cancel":
            {
                if (!Guid.TryParse(optionArgs.FirstOrDefault(), out var id))
                {
                    Console.Error.WriteLine("usage: cancel <job-id> [--keep-file]");
                    return 2;
                }

                var keep = CliArgs.HasFlag(args, "--keep-file");
                var reply = await client.RequestAsync(MessageTypes.JobCancel,
                    new JobActionRequest { JobId = id, DeleteFile = !keep }).ConfigureAwait(false);
                return PrintAction(reply, json);
            }
            case "remove":
            {
                var ids = optionArgs.Select(arg => Guid.TryParse(arg, out var parsed) ? parsed : (Guid?)null).ToList();
                if (ids.Count == 0 || ids.Any(id => id is null))
                {
                    Console.Error.WriteLine("usage: remove <job-id> [<job-id>...] [--keep-file]");
                    return 2;
                }

                var keep = CliArgs.HasFlag(args, "--keep-file");
                // Several ids are removed as one Host operation, as deleting a package does.
                var reply = await client.RequestAsync(MessageTypes.JobRemove, ids.Count == 1
                    ? new JobActionRequest { JobId = ids[0]!.Value, DeleteFile = !keep }
                    : new JobActionRequest { JobIds = ids.Select(id => id!.Value).ToList(), DeleteFile = !keep }).ConfigureAwait(false);
                return PrintAction(reply, json);
            }
            case "pause-all":
            {
                var reply = await client.RequestAsync(MessageTypes.JobsPauseAll, new { }).ConfigureAwait(false);
                return PrintAction(reply, json);
            }
            case "resume-all":
            {
                var reply = await client.RequestAsync(MessageTypes.JobsResumeAll, new { }).ConfigureAwait(false);
                return PrintAction(reply, json);
            }
            case "remote":
                return await RemoteAsync(client, args, json).ConfigureAwait(false);
            case "queue":
                return await QueueAsync(client, args, json).ConfigureAwait(false);
            case "schedule":
                return await ScheduleAsync(client, args, json).ConfigureAwait(false);
            case "site":
                return await SiteAsync(client, args, json).ConfigureAwait(false);
            case "settings":
                return await SettingsAsync(client, args, json).ConfigureAwait(false);
            case "folders":
                return await FoldersAsync(client, json).ConfigureAwait(false);
            case "host":
            {
                var sub = optionArgs.FirstOrDefault()?.ToLowerInvariant();
                if (sub == "status")
                {
                    var status = await client.StatusAsync().ConfigureAwait(false);
                    if (json)
                    {
                        PrintJson(status);
                    }
                    else
                    {
                        Console.WriteLine($"host {status.HostVersion}  protocol {status.ProtocolVersion}");
                        Console.WriteLine($"state: {status.StateDirectory}");
                        Console.WriteLine($"jobs: {status.TotalJobs}  active: {status.ActiveDownloads}  queued: {status.QueuedDownloads}");
                        Console.WriteLine($"tray: {(status.TrayAvailable ? "yes" : "no")}");
                        Console.WriteLine($"browser integration: {IntegrationText(status)}");
                        if (status.OrphanPartFiles.Count > 0)
                        {
                            Console.WriteLine($"orphan parts: {status.OrphanPartFiles.Count}");
                            foreach (var orphan in status.OrphanPartFiles.Take(5))
                            {
                                Console.WriteLine($"  {orphan}");
                            }
                        }
                    }

                    return 0;
                }

                if (sub == "clean-orphans")
                {
                    var delete = CliArgs.HasFlag(args, "--delete");
                    var reply = await client.RequestAsync(MessageTypes.HostCleanOrphans,
                        new CleanOrphansRequest { Delete = delete }).ConfigureAwait(false);
                    var result = CliClient.Checked<CleanOrphansResponse>(reply);
                    if (result is null)
                    {
                        Console.Error.WriteLine("error: host returned no clean-orphans result");
                        return 1;
                    }

                    if (json)
                    {
                        PrintJson(result);
                    }
                    else
                    {
                        Console.WriteLine($"{(delete ? "deleted" : "quarantined")}: {result.Processed.Count}");
                        foreach (var path in result.Processed)
                        {
                            Console.WriteLine($"  {path}");
                        }

                        foreach (var path in result.Failed)
                        {
                            Console.WriteLine($"  failed: {path}");
                        }

                        if (!string.IsNullOrWhiteSpace(result.Error))
                        {
                            Console.Error.WriteLine($"error: {result.Error}");
                        }
                    }

                    return result.Ok ? 0 : 1;
                }

                if (sub == "open")
                {
                    // The main window; "host open settings" opens Options, which plain "open" used to.
                    var page = optionArgs.ElementAtOrDefault(1)?.ToLowerInvariant();
                    var reply = await client.RequestAsync(MessageTypes.HostOpenUi,
                        new HostActionRequest { Action = page == "settings" ? "settings" : string.Empty }).ConfigureAwait(false);
                    return PrintAction(reply, json);
                }

                if (sub == "shutdown")
                {
                    var reply = await client.RequestAsync(MessageTypes.HostShutdown, new { }).ConfigureAwait(false);
                    return PrintAction(reply, json);
                }

                Console.Error.WriteLine("usage: host status|open [settings]|shutdown|clean-orphans [--delete]");
                return 2;
            }
            default:
                // The help is 90 lines; printed here it would scroll the error away.
                Console.Error.WriteLine($"unknown command '{TorrentCommands.Printable(command)}'. Run \"{ProgramName} --help\" for the commands.");
                return 2;
        }
    }

    private static async Task<int> PackageAsync(CliClient client, string[] positional, bool json)
    {
        const string usage = "usage: package list | package pause|resume|cancel|unqueue <package-id> | " +
                             "package queue <package-id> <queue-id> | package priority <package-id> high|normal|low";
        var action = positional.FirstOrDefault()?.ToLowerInvariant();
        if (action == "list")
        {
            var snapshot = await client.SnapshotAsync().ConfigureAwait(false);
            if (json)
            {
                PrintJson(snapshot.Settings.DownloadGroups);
                return 0;
            }

            if (snapshot.Settings.DownloadGroups.Count == 0)
            {
                Console.WriteLine("No packages.");
            }

            foreach (var group in snapshot.Settings.DownloadGroups)
            {
                var children = group.JobIds.Select(id => snapshot.Jobs.FirstOrDefault(job => job.Id == id))
                    .Where(job => job is not null).Select(job => job!).ToList();
                var summary = DownloadGroupSummary.Create(children);
                var queue = PackageRules.QueueText(children);
                Console.WriteLine($"{group.Id:N}  {summary.State,-10} {summary.CompletedCount}/{summary.MemberCount}  queue {(queue.Length == 0 ? "-" : queue)}  {group.Name}");
            }

            return 0;
        }

        if (action is not ("pause" or "resume" or "cancel" or "queue" or "unqueue" or "priority") ||
            !Guid.TryParse(positional.ElementAtOrDefault(1), out var packageId))
        {
            Console.Error.WriteLine(usage);
            return 2;
        }

        var request = new GroupActionRequest { GroupId = packageId, Action = action };
        if (action == "queue")
        {
            if (!int.TryParse(positional.ElementAtOrDefault(2), out var queueId))
            {
                Console.Error.WriteLine(usage);
                return 2;
            }

            request.QueueId = queueId;
        }
        else if (action == "priority")
        {
            if (!JobPriorities.TryParse(positional.ElementAtOrDefault(2), out var priority))
            {
                Console.Error.WriteLine(usage);
                return 2;
            }

            request.Priority = priority;
        }

        var reply = await client.RequestAsync(MessageTypes.GroupAction, request).ConfigureAwait(false);
        var result = CliClient.Checked<GroupActionResult>(reply);
        if (json)
        {
            PrintJson(result);
        }
        else
        {
            Console.WriteLine($"changed {result.Changed} file(s)");
            if (result.LeftCancelled > 0)
            {
                Console.WriteLine($"{result.LeftCancelled} cancelled file(s) stayed cancelled; resume them one by one.");
            }

            if (!result.Ok)
            {
                Console.Error.WriteLine($"failed: {result.Error}");
                foreach (var (id, error) in result.FailedJobs)
                {
                    Console.Error.WriteLine($"  {id:N}: {error}");
                }
            }
        }

        return result.Ok ? 0 : 1;
    }

    private static async Task<int> SimpleJobActionAsync(CliClient client, string[] positional, string command, string type, bool json)
    {
        if (!Guid.TryParse(positional.FirstOrDefault(), out var id))
        {
            Console.Error.WriteLine($"usage: {command} <job-id>");
            return 2;
        }

        var reply = await client.RequestAsync(type, new JobActionRequest { JobId = id }).ConfigureAwait(false);
        return PrintAction(reply, json);
    }

    private static int PrintAction(IpcMessage reply, bool json)
    {
        if (reply.Type == MessageTypes.JobActionResult)
        {
            var result = CliClient.Checked<JobActionResponse>(reply);
            if (json)
            {
                PrintJson(result);
            }
            else if (!result.Ok)
            {
                Console.Error.WriteLine($"failed: {result.Error}");
            }
            else
            {
                Console.WriteLine($"ok: {result.Job?.Id:N} {result.Job?.State}");
            }

            return result.Ok ? 0 : 1;
        }

        var action = reply.GetPayload<HostActionResult>();
        if (json)
        {
            PrintJson(action);
        }
        else if (!action.Ok)
        {
            Console.Error.WriteLine($"failed: {action.Message}");
        }
        else
        {
            Console.WriteLine(action.Message ?? "ok");
        }

        return action.Ok ? 0 : 1;
    }

    /// <summary>
    /// Changes the settings the Host holds now: they are read right before the change and sent straight back, so what
    /// the Host or another window changed meanwhile (a queue stopped, a quota used, a schedule added) is not put back.
    /// <paramref name="change"/> returns an exit code to stop without saving, after saying why. With
    /// <paramref name="savedLine"/>, that line is printed once it is saved instead of every setting.
    /// </summary>
    private static async Task<int> ChangeSettingsAsync(
        CliClient client,
        Func<SettingsDto, int?> change,
        bool json,
        Action<SettingsDto>? saved = null,
        string? savedLine = null)
    {
        var settings = await client.SettingsAsync().ConfigureAwait(false);
        if (change(settings) is { } stop)
        {
            return stop;
        }

        var reply = await client.RequestAsync(MessageTypes.SettingsUpdate,
            new SettingsUpdateRequest { Settings = settings }).ConfigureAwait(false);
        var result = CliClient.Checked<SettingsResult>(reply);
        if (result.Ok && !json)
        {
            saved?.Invoke(result.Settings);
            if (savedLine is not null)
            {
                Console.WriteLine(savedLine);
                return 0;
            }
        }

        return PrintSettingsResult(result, json);
    }

    private static async Task<int> SiteAsync(CliClient client, string[] args, bool json)
    {
        var positional = CliArgs.Positional(args);
        var sub = positional.FirstOrDefault()?.ToLowerInvariant() ?? "list";

        if (sub == "list")
        {
            var settings = (await client.SnapshotAsync().ConfigureAwait(false)).Settings;
            if (json)
            {
                PrintJson(settings.SiteLogins);
                return 0;
            }

            Console.WriteLine($"{"ID",-9} {"PASSWORD",-8} {"USER",-24} SITE");
            foreach (var login in settings.SiteLogins)
            {
                Console.WriteLine(
                    $"{login.Id.ToString("N")[..8],-9} " +
                    $"{(login.HasPassword ? "stored" : "missing"),-8} " +
                    $"{login.Username,-24} {login.Site}");
            }

            return 0;
        }

        if (sub == "add")
        {
            if (positional.Length < 3)
            {
                Console.Error.WriteLine("usage: site add <site> <username> [--password-env VAR]   (otherwise the password is read from standard input)");
                return 2;
            }

            if (GetOption(args, "--password") is not null)
            {
                // Not used, as remote key set: a command line can be read by other programs and stays in shell history.
                Console.Error.WriteLine("A password is only read from standard input or --password-env, so that one was not used. Pipe it in, or run this and type it. What you just typed may be in your shell history.");
                return 2;
            }

            var password = GetOption(args, "--password-env") is { } envName
                ? Environment.GetEnvironmentVariable(envName)
                : SecretInput.ReadLine("Password: ");
            if (string.IsNullOrEmpty(password))
            {
                Console.Error.WriteLine("No password was given. Pipe it in, type it, or use --password-env VAR.");
                return 2;
            }

            var site = positional[1];
            var username = positional[2];
            return await ChangeSettingsAsync(client, settings =>
            {
                settings.SiteLogins.Add(new SiteLoginDto
                {
                    Id = Guid.NewGuid(),
                    Site = site,
                    Username = username,
                    Password = password,
                });
                return null;
            }, json).ConfigureAwait(false);
        }

        if (sub == "remove")
        {
            if (positional.Length < 2)
            {
                Console.Error.WriteLine("usage: site remove <login-id>");
                return 2;
            }

            var selector = positional[1].Replace("-", string.Empty, StringComparison.Ordinal);
            return await ChangeSettingsAsync(client, settings =>
            {
                var matches = settings.SiteLogins
                    .Where(login => login.Id.ToString("N").StartsWith(selector, StringComparison.OrdinalIgnoreCase))
                    .ToList();
                if (matches.Count != 1)
                {
                    Console.Error.WriteLine($"site '{positional[1]}' did not match exactly one entry.");
                    return 1;
                }

                settings.SiteLogins.RemoveAll(login => login.Id == matches[0].Id);
                return null;
            }, json).ConfigureAwait(false);
        }

        Console.Error.WriteLine("usage: site list|add|remove");
        return 2;
    }

    private static async Task<int> ScheduleAsync(CliClient client, string[] args, bool json)
    {
        var positional = CliArgs.Positional(args);
        var sub = positional.FirstOrDefault()?.ToLowerInvariant() ?? "list";

        if (sub == "list")
        {
            var settings = (await client.SnapshotAsync().ConfigureAwait(false)).Settings;
            if (json)
            {
                PrintJson(settings.Schedules);
                return 0;
            }

            Console.WriteLine($"{"ID",-9} {"STATE",-4} {"KIND",-8} {"START",-5} {"STOP",-5} {"Q",3}  NAME / DAYS");
            foreach (var schedule in settings.Schedules)
            {
                Console.WriteLine(
                    $"{schedule.Id.ToString("N")[..8],-9} " +
                    $"{(schedule.Enabled ? "on" : "off"),-4} " +
                    $"{schedule.Kind,-8} {schedule.StartTime,-5} {schedule.StopTime ?? "-",-5} " +
                    $"{schedule.QueueId,3}  {schedule.Name} [{schedule.Days}]" +
                    (schedule.LastStartDate is { } lastStart ? $" last-start={lastStart:yyyy-MM-dd}" : string.Empty));
            }

            return 0;
        }

        if (sub == "add")
        {
            if (positional.Length < 3 ||
                !int.TryParse(positional[1], out var queueId) ||
                !TimeOnly.TryParse(positional[2], CultureInfo.InvariantCulture, DateTimeStyles.None, out var start))
            {
                Console.Error.WriteLine("usage: schedule add <queue-id> <HH:mm> [--days ...] [--stop HH:mm] [--once yyyy-MM-dd] [--name ...] [--disabled]");
                return 2;
            }

            DateOnly? startDate = null;
            var kind = "daily";
            if (GetOption(args, "--once") is { } once)
            {
                if (!DateOnly.TryParse(once, CultureInfo.InvariantCulture, DateTimeStyles.None, out var parsedDate))
                {
                    Console.Error.WriteLine("--once expects yyyy-MM-dd.");
                    return 2;
                }

                startDate = parsedDate;
                kind = "oneTime";
            }

            TimeOnly? stop = null;
            if (GetOption(args, "--stop") is { } stopText)
            {
                if (!TimeOnly.TryParse(stopText, CultureInfo.InvariantCulture, DateTimeStyles.None, out var parsedStop))
                {
                    Console.Error.WriteLine("--stop expects HH:mm.");
                    return 2;
                }

                stop = parsedStop;
            }

            var shutdownMode = GetOption(args, "--shutdown")?.ToLowerInvariant();
            if (shutdownMode is not null && shutdownMode is not ("shutdown" or "restart" or "logoff"))
            {
                Console.Error.WriteLine("--shutdown expects shutdown, restart, or logoff.");
                return 2;
            }

            var schedule = new ScheduleDto
            {
                Id = Guid.NewGuid(),
                QueueId = queueId,
                Enabled = !CliArgs.HasFlag(args, "--disabled"),
                Kind = kind,
                StartDate = startDate,
                Days = GetOption(args, "--days") ?? "Mon,Tue,Wed,Thu,Fri,Sat,Sun",
                StartTime = start.ToString("HH:mm", CultureInfo.InvariantCulture),
                StopTime = stop?.ToString("HH:mm", CultureInfo.InvariantCulture),
                OpenFileWhenDone = CliArgs.HasFlag(args, "--open"),
                ExitWhenDone = CliArgs.HasFlag(args, "--exit"),
                ShutdownWhenDone = shutdownMode is not null,
                ShutdownMode = shutdownMode ?? "shutdown",
                ForceShutdown = CliArgs.HasFlag(args, "--force"),
            };
            return await ChangeSettingsAsync(client, settings =>
            {
                schedule.Name = GetOption(args, "--name") ?? $"Schedule {settings.Schedules.Count + 1}";
                settings.Schedules.Add(schedule);
                return null;
            }, json, _ => Console.WriteLine(
                $"added: {schedule.Id:N} {schedule.Kind} " +
                $"queue {schedule.QueueId}" +
                $" at {schedule.StartTime}")).ConfigureAwait(false);
        }

        if (sub is "remove" or "enable" or "disable")
        {
            if (positional.Length < 2)
            {
                Console.Error.WriteLine($"usage: schedule {sub} <schedule-id>");
                return 2;
            }

            var selector = positional[1];
            var normalized = selector.Replace("-", string.Empty, StringComparison.Ordinal);
            return await ChangeSettingsAsync(client, settings =>
            {
                var matches = settings.Schedules
                    .Where(s => s.Id.ToString("N").StartsWith(normalized, StringComparison.OrdinalIgnoreCase))
                    .ToList();
                if (matches.Count != 1)
                {
                    Console.Error.WriteLine($"schedule '{selector}' did not match exactly one entry.");
                    return 1;
                }

                if (sub == "remove")
                {
                    settings.Schedules.RemoveAll(s => s.Id == matches[0].Id);
                }
                else
                {
                    matches[0].Enabled = sub == "enable";
                }

                return null;
            }, json).ConfigureAwait(false);
        }

        Console.Error.WriteLine("usage: schedule list|add|remove|enable|disable");
        return 2;
    }

    private static async Task<int> UpdateAsync(
        CliClient client,
        string[] positional,
        bool json)
    {
        var sub = positional.FirstOrDefault()?.ToLowerInvariant() ?? "check";
        if (sub is not ("check" or "download" or "install"))
        {
            Console.Error.WriteLine("usage: update check|download|install");
            return 2;
        }

        var type = sub switch
        {
            "download" => MessageTypes.UpdateDownload,
            "install" => MessageTypes.UpdateInstall,
            _ => MessageTypes.UpdateCheck,
        };
        var reply = await client.RequestAsync(type, new { }).ConfigureAwait(false);

        if (sub == "install")
        {
            var install = CliClient.Checked<UpdateInstallResult>(reply);
            if (json)
            {
                PrintJson(install);
            }
            else if (!install.Ok)
            {
                Console.Error.WriteLine($"update install failed: {install.Error}");
            }
            else
            {
                Console.WriteLine(
                    $"update {install.Version} scheduled; the Host is restarting and will apply it.");
                Console.WriteLine($"  result: {install.ResultPath}");
            }

            return install.Ok ? 0 : 1;
        }

        if (sub == "download")
        {
            var result = CliClient.Checked<UpdateDownloadResult>(reply);
            if (json)
            {
                PrintJson(result);
            }
            else if (!result.Ok)
            {
                Console.Error.WriteLine($"update download failed: {result.Error}");
            }
            else
            {
                Console.WriteLine(
                    $"downloaded update: {result.Path} ({result.Size} bytes, {result.Sha256})");
            }

            return result.Ok ? 0 : 1;
        }

        var check = CliClient.Checked<UpdateCheckResult>(reply);
        if (json)
        {
            PrintJson(check);
        }
        else if (!check.Ok && check.Status is not ("upToDate" or "notInstalled"))
        {
            Console.Error.WriteLine($"update check failed: {check.Error}");
        }
        else
        {
            Console.WriteLine($"{check.Status}: installed {check.InstalledVersion ?? "-"} " +
                              $"available {check.AvailableVersion ?? "-"}");
            if (check.Status == "updateAvailable")
            {
                Console.WriteLine($"  installer: {check.InstallerUrl}");
                Console.WriteLine($"  size: {check.Size}  sha256: {check.Sha256}");
                Console.WriteLine("  run 'dm update download' to fetch and verify it.");
            }
            else if (check.Status == "notInstalled")
            {
                Console.WriteLine("  portable/development build: auto-update intentionally disabled.");
            }
            else if (check.Status == "notConfigured")
            {
                Console.WriteLine($"  {check.Error}");
            }
        }

        return check.Status is "updateAvailable" or "upToDate" or "notInstalled" ? 0 : 1;
    }

    private static async Task<int> ProfileAsync(
        CliClient client,
        string[] positional,
        bool json)
    {
        var sub = positional.FirstOrDefault()?.ToLowerInvariant() ?? "list";

        if (sub == "list")
        {
            var settings = (await client.SnapshotAsync().ConfigureAwait(false)).Settings;
            if (json)
            {
                PrintJson(settings.ConnectionProfiles);
                return 0;
            }

            Console.WriteLine($"{"MAX",3}  PATTERN");
            foreach (var profile in settings.ConnectionProfiles)
            {
                Console.WriteLine($"{profile.MaxConnections,3}  {profile.Pattern}");
            }

            return 0;
        }

        if (sub == "add")
        {
            if (positional.Length < 3 ||
                !int.TryParse(positional[2], out var maxConnections) ||
                maxConnections < 1 || maxConnections > 32)
            {
                Console.Error.WriteLine("usage: profile add <host-pattern> <max-connections 1-32>");
                return 2;
            }

            var pattern = positional[1].Trim();
            return await ChangeSettingsAsync(client, settings =>
            {
                settings.ConnectionProfiles.RemoveAll(profile =>
                    string.Equals(profile.Pattern, pattern, StringComparison.OrdinalIgnoreCase));
                settings.ConnectionProfiles.Add(new ConnectionProfileDto
                {
                    Pattern = pattern,
                    MaxConnections = maxConnections,
                });
                return null;
            }, json).ConfigureAwait(false);
        }

        if (sub == "remove")
        {
            if (positional.Length < 2)
            {
                Console.Error.WriteLine("usage: profile remove <host-pattern>");
                return 2;
            }

            var pattern = positional[1].Trim();
            return await ChangeSettingsAsync(client, settings =>
            {
                var removed = settings.ConnectionProfiles.RemoveAll(profile =>
                    string.Equals(profile.Pattern, pattern, StringComparison.OrdinalIgnoreCase));
                if (removed == 0)
                {
                    Console.Error.WriteLine($"profile '{pattern}' not found.");
                    return 1;
                }

                return null;
            }, json).ConfigureAwait(false);
        }

        Console.Error.WriteLine("usage: profile list|add|remove");
        return 2;
    }

    private static void SetCategoryFolder(SettingsDto settings, string category, string value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            settings.CategoryFolders.Remove(category);
        }
        else
        {
            settings.CategoryFolders[category] = value.Trim();
        }
    }

    private static async Task<int> AddBatchAsync(
        CliClient client,
        string[] args,
        string[] positional,
        bool json)
    {
        if (positional.Length == 0)
        {
            Console.Error.WriteLine("usage: add-batch <pattern> [--dest DIR] [--start now|later] [--connections N] [--queue ID] [--priority high|normal|low] [--direct] [--wait] [--yes]");
            return 2;
        }

        IReadOnlyList<string> urls;
        try
        {
            urls = BatchUrlExpander.Expand(positional[0]);
        }
        catch (FormatException ex)
        {
            Console.Error.WriteLine($"batch pattern: {ex.Message}");
            return 2;
        }

        urls = urls.Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        if (urls.Count == 0)
        {
            Console.Error.WriteLine("batch pattern produced no URLs.");
            return 2;
        }

        if (urls.Count > 25 && !CliArgs.HasFlag(args, "--yes"))
        {
            Console.Error.WriteLine(
                $"batch pattern expands to {urls.Count} URLs; re-run with --yes to add them all.");
            return 2;
        }

        var startMode = GetOption(args, "--start") ?? "now";
        if (!CliArgs.TryInt(args, "--connections", out var connections, out var numberError) ||
            !CliArgs.TryInt(args, "--queue", out var queueId, out numberError))
        {
            Console.Error.WriteLine(numberError);
            return 2;
        }

        var (destination, destinationError) = await DestinationAsync(client, args).ConfigureAwait(false);
        if (destinationError is not null)
        {
            Console.Error.WriteLine(destinationError);
            return 2;
        }

        var conflict = GetOption(args, "--conflict")?.ToLowerInvariant();
        if (conflict is not null && conflict is not ("rename" or "overwrite" or "fail"))
        {
            Console.Error.WriteLine("--conflict expects rename, overwrite, or fail.");
            return 2;
        }

        int? priority = null;
        if (GetOption(args, "--priority") is { } priorityText)
        {
            if (!JobPriorities.TryParse(priorityText, out var parsedPriority))
            {
                Console.Error.WriteLine("--priority expects high, normal, or low.");
                return 2;
            }

            priority = parsedPriority;
        }

        var results = new List<(string Url, AddJobResponse Result)>();
        var check = new DirectAddCheck(client, args);
        foreach (var url in urls)
        {
            try
            {
                if (await check.WhyNotAsync(url).ConfigureAwait(false) is { } refused)
                {
                    results.Add((url, new AddJobResponse { Accepted = false, Error = refused }));
                    if (!json)
                    {
                        Console.Error.WriteLine($"Failed: {url}: {refused}");
                    }

                    continue;
                }

                var result = await client.AskAsync<AddJobResponse>(MessageTypes.JobAdd, new AddJobRequest
                {
                    Url = url,
                    DestinationDirectory = destination,
                    StartMode = startMode,
                    ConflictPolicy = conflict ?? "rename",
                    // UI.B14: headless, with no completion window per job (see DownloadWindows): a batch of
                    // 1,000 must not open 1,000 progress or completion windows, each its own window process.
                    Source = "cliBatch",
                    Connections = connections,
                    QueueId = queueId.GetValueOrDefault(-1),
                    Priority = priority,
                }).ConfigureAwait(false);
                results.Add((url, result));
                if (!json)
                {
                    Console.WriteLine(result.Accepted
                        ? $"{(result.Duplicate ? "Already queued" : "Added")}: {result.Job?.Id:N}  {result.Job?.FileName}"
                        : $"Failed: {url}: {result.Error}");
                }
            }
            catch (Exception ex)
            {
                results.Add((url, new AddJobResponse { Accepted = false, Error = ex.Message }));
                if (!json)
                {
                    Console.Error.WriteLine($"Failed: {url}: {ex.Message}");
                }
            }
        }

        if (json)
        {
            PrintJson(results.Select(r => new { r.Url, r.Result.Accepted, r.Result.Duplicate, r.Result.Error, r.Result.Job }));
        }

        var accepted = results.Count(r => r.Result.Accepted);
        var duplicates = results.Count(r => r.Result.Duplicate);
        var failed = results.Count - accepted;
        if (!json)
        {
            Console.WriteLine($"batch: {urls.Count} URLs, {accepted} accepted ({duplicates} duplicate), {failed} failed");
        }

        if (CliArgs.HasFlag(args, "--wait"))
        {
            // A URL the Host refused is a failure here too, as it is without --wait.
            var exit = failed > 0 ? 1 : 0;
            foreach (var entry in results.Where(r => r.Result.Accepted && r.Result.Job is not null))
            {
                exit = Math.Max(exit, await WaitForAsync(client, entry.Result.Job!.Id, json)
                    .ConfigureAwait(false));
            }

            return exit;
        }

        return failed > 0 ? 1 : 0;
    }

    private static async Task<int> QueueAsync(CliClient client, string[] args, bool json)
    {
        var optionArgs = CliArgs.Positional(args);
        var sub = optionArgs.FirstOrDefault()?.ToLowerInvariant() ?? "list";

        if (sub == "list")
        {
            var snapshot = await client.SnapshotAsync().ConfigureAwait(false);
            var settings = snapshot.Settings;
            if (json)
            {
                PrintJson(settings.Queues);
            }
            else
            {
                // WAITING: the downloads only a "queue start" lets go, the window's "N waiting for Start" (U-WL-1). A
                // running queue offers Start while that is above 0, a stopped one always.
                Console.WriteLine($"{"ID",-4} {"STATE",-8} {"MAX",4} {"QUOTA",-18} {"WAITING",7}  NAME");
                foreach (var queue in settings.Queues.OrderBy(q => q.Order))
                {
                    var quota = queue.QuotaEnabled
                        ? $"{queue.QuotaBytesUsed / (1024 * 1024)}/{queue.QuotaBytes / (1024 * 1024)}MB/{queue.QuotaWindowHours}h"
                        : "-";
                    var waiting = QueueCommands.WaitingForStart(queue, snapshot.Jobs);
                    Console.WriteLine($"{queue.Id,-4} {(queue.IsRunning ? "running" : "stopped"),-8} {queue.MaxConcurrentDownloads,4} {quota,-18} {waiting,7}  {queue.Name}");
                }
            }

            return 0;
        }

        if (sub == "add")
        {
            var name = optionArgs.Skip(1).FirstOrDefault();
            if (string.IsNullOrWhiteSpace(name))
            {
                Console.Error.WriteLine("usage: queue add <name> [max 1-16]");
                return 2;
            }

            var max = 3;
            if (optionArgs.Length > 2 && (!int.TryParse(optionArgs[2], out max) || max is < 1 or > 16))
            {
                Console.Error.WriteLine("usage: queue add <name> [max 1-16]");
                return 2;
            }

            return await ChangeSettingsAsync(client, settings =>
            {
                settings.Queues.Add(new QueueDto
                {
                    Id = settings.Queues.Count == 0 ? 0 : settings.Queues.Max(q => q.Id) + 1,
                    Name = name,
                    MaxConcurrentDownloads = max,
                    IsRunning = true,
                    Order = settings.Queues.Count,
                });
                return null;
            }, json).ConfigureAwait(false);
        }

        if (sub == "move")
        {
            if (optionArgs.Length < 3 ||
                !Guid.TryParse(optionArgs[1], out var jobId) ||
                !int.TryParse(optionArgs[2], out var targetQueue))
            {
                Console.Error.WriteLine("usage: queue move <job-id> <queue-id>");
                return 2;
            }

            var reply = await client.RequestAsync(MessageTypes.JobMoveToQueue,
                new JobQueueRequest { JobId = jobId, QueueId = targetQueue }).ConfigureAwait(false);
            return PrintAction(reply, json);
        }

        if (sub == "quota")
        {
            const string quotaUsage = "usage: queue quota <queue-id> <megabytes> <window-hours> | queue quota <queue-id> off";
            if (optionArgs.Length < 3 || !int.TryParse(optionArgs[1], out var quotaQueueId))
            {
                Console.Error.WriteLine(quotaUsage);
                return 2;
            }

            var off = optionArgs[2].Equals("off", StringComparison.OrdinalIgnoreCase);
            long megabytes = 0;
            var hours = 0;
            if (!off && (!long.TryParse(optionArgs[2], out megabytes) || megabytes <= 0 ||
                         optionArgs.Length < 4 || !int.TryParse(optionArgs[3], out hours) || hours <= 0))
            {
                Console.Error.WriteLine(quotaUsage);
                return 2;
            }

            return await ChangeSettingsAsync(client, settings =>
            {
                var queue = settings.Queues.FirstOrDefault(q => q.Id == quotaQueueId);
                if (queue is null)
                {
                    Console.Error.WriteLine("Queue not found.");
                    return 1;
                }

                if (off)
                {
                    queue.QuotaEnabled = false;
                }
                else
                {
                    queue.QuotaEnabled = true;
                    queue.QuotaBytes = megabytes * 1024 * 1024;
                    queue.QuotaWindowHours = hours;
                    // The Host keeps quota use; this asks it for a fresh window (settings.update ignores the two fields).
                    queue.ResetQuota = true;
                }

                return null;
            }, json).ConfigureAwait(false);
        }

        if (sub is "start" or "stop" or "max")
        {
            var usage = sub == "max" ? "usage: queue max <queue-id> <max 1-16>" : $"usage: queue {sub} <queue-id>";
            if (optionArgs.Length < 2 || !int.TryParse(optionArgs[1], out var queueId))
            {
                Console.Error.WriteLine(usage);
                return 2;
            }

            var request = new QueueActionRequest { QueueId = queueId, Action = sub };
            if (sub == "max")
            {
                // The Host's action is "setmax"; a missing or wrong number is said, never sent as some default.
                if (optionArgs.Length < 3 || !int.TryParse(optionArgs[2], out var maxValue) || maxValue is < 1 or > 16)
                {
                    Console.Error.WriteLine(usage);
                    return 2;
                }

                request.Action = "setmax";
                request.MaxConcurrentDownloads = maxValue;
            }

            var reply = await client.RequestAsync(MessageTypes.QueueAction, request).ConfigureAwait(false);
            return PrintSettingsResult(CliClient.Checked<SettingsResult>(reply), json);
        }

        Console.Error.WriteLine("usage: queue list|add|start|stop|max|move|quota");
        return 2;
    }

    private static int PrintSettingsResult(SettingsResult result, bool json)
    {
        if (json)
        {
            PrintJson(result);
        }
        else if (!result.Ok)
        {
            Console.Error.WriteLine(result.Error);
        }
        else
        {
            PrintSettings(result.Settings);
            Console.WriteLine("Queues:");
            foreach (var queue in result.Settings.Queues)
            {
                Console.WriteLine($"  {queue.Id}: {queue.Name} ({(queue.IsRunning ? "running" : "stopped")}, max {queue.MaxConcurrentDownloads})");
            }
        }

        return result.Ok ? 0 : 1;
    }

    /// <summary>
    /// <c>--dest DIR</c>, or <c>--to NAME</c> for a favorite folder by its name. Neither leaves the folder to the Host.
    /// </summary>
    private static async Task<(string? Folder, string? Error)> DestinationAsync(CliClient client, string[] args)
    {
        // A relative folder is the caller's, not the Host's (the Host would resolve it against its own folder).
        var dest = GetOption(args, "--dest") is { } given ? Path.GetFullPath(given) : null;
        var to = GetOption(args, "--to");
        if (to is null) return (dest, null);
        if (dest is not null) return (null, "Use --dest or --to, not both.");
        var favorites = (await client.SnapshotAsync().ConfigureAwait(false)).Settings.FavoriteFolders;
        var match = favorites.FirstOrDefault(favorite => string.Equals(favorite.Name, to.Trim(), StringComparison.OrdinalIgnoreCase));
        return match is not null
            ? (match.Path, null)
            : (null, favorites.Count == 0
                ? $"No favorite folder is named \"{to}\". There are no favorites yet; add them in Options or with the star beside a folder box."
                : $"No favorite folder is named \"{to}\". Favorites: {string.Join(", ", favorites.Select(favorite => favorite.Name))}.");
    }

    /// <summary>Favorite, default and recent folders, each with the free space the Host measures for it.</summary>
    private static async Task<int> FoldersAsync(CliClient client, bool json)
    {
        var settings = (await client.SnapshotAsync().ConfigureAwait(false)).Settings;
        var rows = new List<(string Kind, string Name, string Path)>();
        foreach (var favorite in settings.FavoriteFolders) rows.Add(("favorite", favorite.Name, favorite.Path));
        if (!string.IsNullOrWhiteSpace(settings.DefaultDownloadDirectory)) rows.Add(("default", "", settings.DefaultDownloadDirectory));
        foreach (var recent in settings.RecentDestinationDirectories) rows.Add(("recent", "", recent));
        var reply = await client.RequestAsync(MessageTypes.FolderSpace, new FolderSpaceRequest { Paths = rows.Select(row => row.Path).ToList() })
            .ConfigureAwait(false);
        var measured = reply.Type == MessageTypes.FolderSpaceResult ? reply.GetPayload<FolderSpaceResult>().Folders : new List<FolderSpaceDto>();
        FolderSpaceDto? SpaceOf(string path) => measured.FirstOrDefault(folder => string.Equals(folder.Path, path.Trim(), StringComparison.OrdinalIgnoreCase));
        if (json)
        {
            PrintJson(rows.Select(row => new
            {
                kind = row.Kind,
                name = row.Name.Length == 0 ? null : row.Name,
                path = row.Path,
                freeBytes = SpaceOf(row.Path)?.FreeBytes,
                totalBytes = SpaceOf(row.Path)?.TotalBytes,
                insideContainer = SpaceOf(row.Path)?.InsideContainer ?? false,
            }));
            return 0;
        }

        foreach (var row in rows)
        {
            var space = SpaceOf(row.Path);
            var free = space?.FreeBytes is { } bytes
                ? $"{SizeText(bytes)} free" + (space.TotalBytes is { } total ? $" of {SizeText(total)}" : "")
                : "space unknown";
            if (space?.InsideContainer == true) free += "  (not on a mounted volume)";
            var label = row.Name.Length > 0 ? $"{row.Kind} \"{row.Name}\"" : row.Kind;
            Console.WriteLine($"{label,-24} {row.Path}  {free}");
        }

        return 0;
    }

    private static string SizeText(long bytes)
    {
        const double Kb = 1024, Mb = Kb * 1024, Gb = Mb * 1024, Tb = Gb * 1024;
        return bytes >= Tb ? $"{bytes / Tb:N1} TB" : bytes >= Gb ? $"{bytes / Gb:N1} GB" : bytes >= Mb ? $"{bytes / Mb:N0} MB" : $"{bytes / Kb:N0} KB";
    }

    private static async Task<int> SettingsAsync(CliClient client, string[] args, bool json)
    {
        var optionArgs = args.Skip(1).Where(a => !a.StartsWith("--", StringComparison.Ordinal)).ToArray();
        if (optionArgs.Length == 0 || optionArgs[0].Equals("get", StringComparison.OrdinalIgnoreCase))
        {
            var current = await client.SnapshotAsync().ConfigureAwait(false);
            if (json)
            {
                PrintJson(current.Settings);
            }
            else
            {
                PrintSettings(current.Settings);
            }

            return 0;
        }

        if (optionArgs[0].Equals("export", StringComparison.OrdinalIgnoreCase))
        {
            if (optionArgs.Length < 2)
            {
                Console.Error.WriteLine("usage: settings export <file.json>");
                return 2;
            }

            var current = await client.SnapshotAsync().ConfigureAwait(false);
            // A backup carries no revision: restored, it replaces the queues and schedules (see import).
            current.Settings.Revision = 0;
            current.Settings.Proxy.Password = null;
            foreach (var login in current.Settings.SiteLogins)
            {
                login.Password = null;
            }

            await WriteFileAtomicallyAsync(
                optionArgs[1],
                JsonSerializer.Serialize(current.Settings, PrettyJson)).ConfigureAwait(false);
            Console.WriteLine($"exported settings to {optionArgs[1]}");
            return 0;
        }

        if (optionArgs[0].Equals("import", StringComparison.OrdinalIgnoreCase))
        {
            if (optionArgs.Length < 2)
            {
                Console.Error.WriteLine("usage: settings import <file.json>");
                return 2;
            }

            SettingsDto? imported;
            try
            {
                imported = JsonSerializer.Deserialize<SettingsDto>(
                    await File.ReadAllTextAsync(optionArgs[1]).ConfigureAwait(false),
                    IpcJson.Options);
            }
            catch (Exception ex) when (ex is IOException or JsonException)
            {
                Console.Error.WriteLine($"import failed: {ex.Message}");
                return 2;
            }

            if (imported is null)
            {
                Console.Error.WriteLine("import file is empty or invalid.");
                return 2;
            }

            // A restore replaces the queues and schedules with the file's: one deleted since the export comes back and
            // one added since goes. A revision (a file exported by an older build) would merge them instead.
            imported.Revision = 0;
            imported.Proxy.Password = null;
            foreach (var login in imported.SiteLogins)
            {
                login.Password = null;
            }

            var importedReply = await client.RequestAsync(MessageTypes.SettingsUpdate,
                new SettingsUpdateRequest { Settings = imported }).ConfigureAwait(false);
            return PrintSettingsResult(CliClient.Checked<SettingsResult>(importedReply), json);
        }

        if (!optionArgs[0].Equals("set", StringComparison.OrdinalIgnoreCase))
        {
            Console.Error.WriteLine("usage: settings [get|set key value|export|import]");
            return 2;
        }

        // `settings set proxypass --clear`: "--" arguments are not positional, so the flag is read from the raw arguments.
        var clearProxyPassword = optionArgs.Length == 2 &&
                                 optionArgs[1].Equals("proxypass", StringComparison.OrdinalIgnoreCase) &&
                                 CliArgs.HasFlag(args, "--clear");
        // `settings set proxypass` with no value: the password is read from standard input, never from the command line.
        var proxyPasswordFromInput = !clearProxyPassword && optionArgs.Length == 2 &&
                                     optionArgs[1].Equals("proxypass", StringComparison.OrdinalIgnoreCase);
        if (!clearProxyPassword && !proxyPasswordFromInput && (optionArgs.Length < 3 || optionArgs.Length % 2 == 0))
        {
            Console.Error.WriteLine("usage: settings set <key> <value> [<key> <value>...] (or: settings set proxypass, which reads it from standard input; settings set proxypass --clear)");
            return 2;
        }

        for (var i = 1; i + 1 < optionArgs.Length; i += 2)
        {
            if (optionArgs[i].Equals("proxypass", StringComparison.OrdinalIgnoreCase))
            {
                Console.Error.WriteLine("The proxy password is only read from standard input, so that one was not used. Run: dm settings set proxypass, then pipe it in or type it. What you just typed may be in your shell history.");
                return 2;
            }
        }

        string? proxyPassword = null;
        if (proxyPasswordFromInput)
        {
            proxyPassword = SecretInput.ReadLine("Proxy password: ");
            if (string.IsNullOrEmpty(proxyPassword))
            {
                Console.Error.WriteLine("No password was given. Pipe it in or type it; to remove it use: dm settings set proxypass --clear");
                return 2;
            }
        }

        // Read once, before the settings are: a key file that can't be read stops here.
        string? updateKeyFromFile = null;
        for (var i = 1; i + 1 < optionArgs.Length; i += 2)
        {
            if (optionArgs[i].Equals("updatekeyfile", StringComparison.OrdinalIgnoreCase))
            {
                updateKeyFromFile = (await File.ReadAllTextAsync(optionArgs[i + 1]).ConfigureAwait(false)).Trim();
            }
        }

        return await ChangeSettingsAsync(client, settings =>
        {
            if (clearProxyPassword)
            {
                settings.Proxy.Password = null;
                settings.Proxy.ClearPassword = true;
            }
            else if (proxyPassword is not null)
            {
                settings.Proxy.Password = proxyPassword;
            }

            for (var i = 1; i + 1 < optionArgs.Length; i += 2)
            {
                var key = optionArgs[i].ToLowerInvariant();
                var value = optionArgs[i + 1];
                switch (key)
                {
                    case "defaultdir":
                    case "default-download-directory":
                        settings.DefaultDownloadDirectory = value;
                        break;
                    case "maxconcurrent":
                        settings.MaxConcurrentDownloads = int.Parse(value, CultureInfo.InvariantCulture);
                        break;
                    case "defaultconnections":
                        settings.DefaultConnections = int.Parse(value, CultureInfo.InvariantCulture);
                        break;
                    case "launchatstartup":
                        settings.LaunchAtStartup = ParseBool(value);
                        break;
                    case "clipboard":
                        settings.ClipboardMonitoring = ParseBool(value);
                        break;
                    case "usecategoryfolders":
                        settings.UseCategoryFolders = ParseBool(value);
                        break;
                    case "usetorrentcategoryfolders":
                        settings.UseTorrentCategoryFolders = ParseBool(value);
                        break;
                    case "torrentdir":
                        // Empty, "none" or "-": torrents use the same folders as other downloads.
                        settings.TorrentCategoryFoldersRoot = string.IsNullOrWhiteSpace(value) || value is "none" or "-" ? string.Empty : value.Trim();
                        break;
                    case "closewindowcompletely":
                        settings.CloseWindowCompletely = ParseBool(value);
                        break;
                    case "browserintegration":
                        settings.BrowserIntegrationEnabled = ParseBool(value);
                        break;
                    case "speedlimit":
                        settings.GlobalSpeedLimitBytesPerSecond = long.Parse(value, CultureInfo.InvariantCulture);
                        break;
                    case "proxymode":
                        settings.Proxy.Mode = value.ToLowerInvariant();
                        break;
                    case "proxyaddress":
                        settings.Proxy.Address = value;
                        break;
                    case "proxyuser":
                        settings.Proxy.Username = value;
                        break;
                    case "allowsystemcompletionactions":
                        settings.AllowSystemCompletionActions = ParseBool(value);
                        break;
                    case "preventsleep":
                        settings.PreventSleepWhileDownloading = ParseBool(value);
                        break;
                    case "ffmpegpath":
                        settings.FfmpegPath = string.IsNullOrWhiteSpace(value) ? null : value;
                        break;
                    case "postdownload":
                        settings.PostDownloadCommand =
                            string.IsNullOrWhiteSpace(value) || value is "none" or "-" ? null : value;
                        break;
                    case "updatemanifest":
                        settings.UpdateManifestUrl =
                            string.IsNullOrWhiteSpace(value) || value is "none" or "-" ? null : value;
                        break;
                    case "updatekey":
                        settings.UpdatePublicKey = value;
                        break;
                    case "updatekeyfile":
                        settings.UpdatePublicKey = updateKeyFromFile;
                        break;
                    case "updatechannel":
                        settings.UpdateChannel = value;
                        break;
                    case "autocheckupdates":
                        settings.AutoCheckUpdates = ParseBool(value);
                        break;
                    case "compresseddir":
                        SetCategoryFolder(settings, "compressed", value);
                        break;
                    case "documentsdir":
                        SetCategoryFolder(settings, "documents", value);
                        break;
                    case "musicdir":
                        SetCategoryFolder(settings, "music", value);
                        break;
                    case "programsdir":
                        SetCategoryFolder(settings, "programs", value);
                        break;
                    case "videodir":
                        SetCategoryFolder(settings, "video", value);
                        break;
                    default:
                        Console.Error.WriteLine($"unknown setting '{key}'. Keys: {SettingKeys}");
                        return 2;
                }
            }

            return null;
        }, json, savedLine: "saved: " + string.Join(", ", SetKeys(optionArgs, clearProxyPassword || proxyPasswordFromInput))
            + $" (\"{ProgramName} settings\" shows them all)").ConfigureAwait(false);
    }

    /// <summary>The keys a <c>settings set</c> names, in its order.</summary>
    private static IEnumerable<string> SetKeys(string[] optionArgs, bool proxyPassword)
    {
        if (proxyPassword) yield return "proxypass";
        for (var i = 1; i + 1 < optionArgs.Length; i += 2) yield return optionArgs[i].ToLowerInvariant();
    }

    /// <summary>The keys <c>settings set</c> knows, for its help and its error.</summary>
    private const string SettingKeys =
        "defaultdir, maxconcurrent, defaultconnections, launchatstartup, clipboard, usecategoryfolders, " +
        "usetorrentcategoryfolders, torrentdir, " +
        "closewindowcompletely, browserintegration, speedlimit, proxymode, proxyaddress, proxyuser, proxypass, " +
        "allowsystemcompletionactions, preventsleep, ffmpegpath, postdownload, updatemanifest, updatekey, updatekeyfile, " +
        "updatechannel, autocheckupdates, compresseddir, documentsdir, musicdir, programsdir, videodir";

    /// <summary>
    /// Until the download ends. Paused (as "--start later" leaves it) it would never end by itself, so that ends the
    /// wait too. The list is asked for less often the longer it takes: every half second at first, every 5 seconds later.
    /// </summary>
    private static async Task<int> WaitForAsync(CliClient client, Guid id, bool json)
    {
        var delay = TimeSpan.FromMilliseconds(500);
        while (true)
        {
            var snapshot = await client.SnapshotAsync().ConfigureAwait(false);
            var job = snapshot.Jobs.FirstOrDefault(j => j.Id == id);
            if (job is null)
            {
                Console.Error.WriteLine("job disappeared");
                return 1;
            }

            if (!json)
            {
                // UI.B7: says when it waits for a connection to its host; padded so a shorter line overwrites it.
                var waiting = job.WaitingForHost is { Length: > 0 } host ? $"  Waiting for a connection to {host}" : string.Empty;
                Console.Write($"\r{job.State,-12} {FormatUtil.Bytes(job.ReceivedBytes),-12} {job.BytesPerSecond ?? 0,10:0.0} B/s{waiting,-60}");
            }

            if (job.State is "completed")
            {
                if (!json)
                {
                    Console.WriteLine();
                }

                return 0;
            }

            if (job.State is "failed" or "cancelled")
            {
                if (!json)
                {
                    Console.WriteLine($"\n{job.State}: {job.Failure?.Message}");
                }

                return 1;
            }

            // Paused, or added to start later: neither goes on by itself, so the wait would never end.
            if (job.State is "paused" || job.State is "queued" && job.StartMode == "later")
            {
                if (!json)
                {
                    Console.WriteLine($"\n{(job.State == "paused" ? "paused" : "not started")}: it does not go on by itself; start it with: dm resume {job.Id:N}");
                }

                return 1;
            }

            await Task.Delay(delay).ConfigureAwait(false);
            delay = TimeSpan.FromMilliseconds(Math.Min(delay.TotalMilliseconds * 1.5, 5000));
        }
    }

    private static async Task<int> DoctorAsync(bool noStart)
    {
        try
        {
            await using var client = await CliClient.ConnectAsync(startHost: !noStart).ConfigureAwait(false);
            var status = await client.StatusAsync().ConfigureAwait(false);
            Console.WriteLine("Inbound Download Manager diagnostics");
            Console.WriteLine($"  host:        {status.HostVersion} (protocol {status.ProtocolVersion})");
            Console.WriteLine($"  state dir:   {status.StateDirectory}");
            Console.WriteLine($"  tray:        {(status.TrayAvailable ? "yes" : "no")}");
            Console.WriteLine($"  jobs:        {status.TotalJobs} total, {status.ActiveDownloads} active, {status.QueuedDownloads} queued");
            Console.WriteLine($"  integration: {IntegrationText(status)}");
            return 0;
        }
        catch (HostUnavailableException ex)
        {
            Console.Error.WriteLine(ex.Message);
            return 3;
        }
    }

    /// <summary>
    /// The Host's browser integration text, which already says whether and for which browser the native host is
    /// registered. A separate "native host: registered" beside "Chrome registration is broken" (Edge's works) read as a
    /// contradiction, so the registration is added only where the text doesn't cover it: integration turned off.
    /// </summary>
    private static string IntegrationText(HostStatusDto status)
        => status.BrowserIntegration == "disabled"
            ? $"disabled (native host {(status.NativeHostRegistered ? "registered" : "not registered")})"
            : status.BrowserIntegration;

    private static void PrintJobs(IReadOnlyList<JobDto> jobs)
    {
        if (jobs.Count == 0)
        {
            Console.WriteLine("No downloads.");
            return;
        }

        Console.WriteLine($"{"STATE",-12} {"SIZE",12} {"PROGRESS",9} {"SPEED",12}  FILE");
        foreach (var job in jobs)
        {
            var progress = job.Progress is null ? "-" : $"{job.Progress * 100:0.0}%";
            var size = ListSizeText(job);
            var speed = job.BytesPerSecond is null ? "-" : $"{FormatUtil.Bytes((long)job.BytesPerSecond.Value)}/s";
            Console.WriteLine($"{StateText(job.State),-12} {size,12} {progress,9} {speed,12}  {job.FileName}");
            Console.WriteLine($"  {job.Id:N}  {ShownAddress.For(job.Url, job.AddressHidden)}");
            if (job.StatusNote is not null) Console.WriteLine($"  {job.StatusNote}");
        }
    }

    /// <summary>A state in the window's words ("Retrying", not "retryWaiting"); --json keeps the Host's.</summary>
    internal static string StateText(string state) => state switch
    {
        "queued" => "Queued",
        "connecting" => "Connecting",
        "downloading" => "Downloading",
        "retryWaiting" => "Retrying",
        "verifying" => "Verifying",
        "paused" => "Paused",
        "completed" => "Completed",
        "failed" => "Failed",
        "cancelled" => "Cancelled",
        _ => state,
    };

    /// <summary>The size, or what has come so far while it isn't known; blank before anything has, as the window leaves it.</summary>
    internal static string ListSizeText(JobDto job)
        => job.TotalBytes is { } total ? FormatUtil.Bytes(total)
            : job.ReceivedBytes > 0 ? FormatUtil.Bytes(job.ReceivedBytes)
            : string.Empty;

    private static void PrintJobDetails(JobDto job)
    {
        Console.WriteLine($"id:       {job.Id:N}");
        Console.WriteLine($"file:     {job.FinalFilePath}");
        Console.WriteLine($"{(ShownAddress.IsHidden(job.Url, job.AddressHidden) ? "site:" : "url:"),-10}{ShownAddress.For(job.Url, job.AddressHidden)}");
        Console.WriteLine($"state:    {StateText(job.State)}");
        if (job.StatusNote is not null) Console.WriteLine($"note:     {job.StatusNote}");
        Console.WriteLine($"queue:    {job.QueueId}:{job.QueueName}");
        Console.WriteLine($"part:     {job.PartFilePath}");
        Console.WriteLine($"bytes:    {job.ReceivedBytes} / {job.TotalBytes?.ToString() ?? "unknown"}");
        Console.WriteLine($"speed:    {job.BytesPerSecond?.ToString("0.0") ?? "-"} B/s");
        Console.WriteLine($"source:   {job.Source}");
        Console.WriteLine($"created:  {job.CreatedAt:O}");
        if (job.Failure is not null)
        {
            Console.WriteLine($"failure:  {job.Failure.Kind}: {job.Failure.Message}");
        }
    }

    private static void PrintSettings(SettingsDto settings)
    {
        Console.WriteLine($"default dir:       {settings.DefaultDownloadDirectory}");
        Console.WriteLine($"last folder:       {settings.LastDestinationDirectory ?? "-"}");
        foreach (var recent in settings.RecentUrls.Take(3))
        {
            Console.WriteLine($"  recent           {recent}");
        }
        Console.WriteLine($"max concurrent:    {settings.MaxConcurrentDownloads}");
        Console.WriteLine($"default conns:     {settings.DefaultConnections}");
        foreach (var profile in settings.ConnectionProfiles)
        {
            Console.WriteLine($"  profile        {profile.Pattern} = {profile.MaxConnections}");
        }
        Console.WriteLine($"global speed limit:{settings.GlobalSpeedLimitBytesPerSecond?.ToString() ?? "none"}");
        Console.WriteLine($"proxy:             {settings.Proxy.Mode} {settings.Proxy.Address ?? "-"}" +
                          (string.IsNullOrWhiteSpace(settings.Proxy.Username) ? string.Empty : $" user={settings.Proxy.Username} password=***"));
        Console.WriteLine($"launch at sign-in: {settings.LaunchAtStartup}");
        Console.WriteLine($"clipboard monitor: {settings.ClipboardMonitoring}");
        foreach (var category in FileCategories.Names)
        {
            if (settings.CategoryFolders.TryGetValue(category, out var folder) &&
                !string.IsNullOrWhiteSpace(folder))
            {
                Console.WriteLine($"  {category,-11} {folder}");
            }
        }
        if (settings.UseTorrentCategoryFolders is { } torrentCategories)
        {
            Console.WriteLine($"torrent folder:    {(string.IsNullOrWhiteSpace(settings.TorrentCategoryFoldersRoot) ? "same as other downloads" : settings.TorrentCategoryFoldersRoot)}");
            Console.WriteLine($"torrent categories: {(torrentCategories ? "on" : "off")}");
        }
        Console.WriteLine($"browser integration: {settings.BrowserIntegrationEnabled}");
        Console.WriteLine($"auto-resume:       {settings.AutoResumeUnfinishedOnStartup}");
        Console.WriteLine($"system actions:    {settings.AllowSystemCompletionActions}");
        Console.WriteLine($"prevent sleep:     {settings.PreventSleepWhileDownloading}");
        Console.WriteLine($"ffmpeg path:       {settings.FfmpegPath ?? "auto"}");
        Console.WriteLine($"post-download:     {settings.PostDownloadCommand ?? "none"}");
        Console.WriteLine($"queues:            {string.Join(", ", settings.Queues.Select(q => $"{q.Id}:{q.Name}"))}");
    }

    private static void PrintJson<T>(T value)
        => Console.WriteLine(JsonSerializer.Serialize(value, PrettyJson));

    /// <summary>
    /// Writes beside the file and then puts it in place, so a stop part way (Ctrl+C, a full disk) leaves the old file
    /// as it was rather than a cut-off one.
    /// </summary>
    private static async Task WriteFileAtomicallyAsync(string path, string content)
    {
        var full = Path.GetFullPath(path);
        var temporary = Path.Combine(Path.GetDirectoryName(full) ?? ".", $".{Path.GetFileName(full)}.{Guid.NewGuid():N}.tmp");
        try
        {
            await using (var stream = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            await using (var writer = new StreamWriter(stream, new UTF8Encoding(false)))
            {
                await writer.WriteAsync(content).ConfigureAwait(false);
                await writer.FlushAsync().ConfigureAwait(false);
                stream.Flush(flushToDisk: true);
            }

            File.Move(temporary, full, overwrite: true);
        }
        finally
        {
            if (File.Exists(temporary))
            {
                File.Delete(temporary);
            }
        }
    }

    internal static string? Option(string[] args, string name) => GetOption(args, name);

    /// <summary>The value after <paramref name="name"/>, found only where a flag goes (<see cref="CliArgs.OptionValues"/>).</summary>
    private static string? GetOption(string[] args, string name) => CliArgs.OptionValues(args, name).FirstOrDefault();

    private static IEnumerable<string> GetOptions(string[] args, string name) => CliArgs.OptionValues(args, name);

    private static bool ParseBool(string value)
        => value.Equals("true", StringComparison.OrdinalIgnoreCase) ||
           value.Equals("1", StringComparison.OrdinalIgnoreCase) ||
           value.Equals("yes", StringComparison.OrdinalIgnoreCase) ||
           value.Equals("on", StringComparison.OrdinalIgnoreCase);

    private static void PrintHelp()
    {
        Console.WriteLine($"Inbound Download Manager CLI. In the usage below, \"dm\" stands for {ProgramName}.");
        Console.WriteLine("""

            Usage:
              dm add <url> [--dest DIR | --to FAVORITE] [--name NAME] [--category ID] [--start now|later] [--direct] [--wait]
                          [--connections N] [--queue ID] [--header "Name: value"] [--sha256 HEX]
                          [--conflict rename|overwrite|fail] [--priority high|normal|low]
                          [--description TEXT] [--referrer URL] [--user-agent TEXT]
                          [--package KEY] [--package-name NAME]
              dm folders [--json]   favorite, default and recent folders with free space
              dm add-batch <pattern> [--dest DIR | --to FAVORITE] [--start now|later] [--direct] [--wait] [--yes]
                          [--connections N] [--queue ID] [--conflict rename|overwrite|fail]
                          [--priority high|normal|low]
              dm list [--json]
              dm status <job-id> [--json]
              dm open|reveal <job-id>
              dm describe <job-id> <description text>
              dm profile list|add|remove
              dm update check|download|install
              dm export <file.json>
              dm import <file.json> [--start now|later] [--direct] [--yes]
              dm pause|resume|retry <job-id>
              dm priority <job-id> high|normal|low
              dm package list
              dm package pause|resume|cancel|unqueue <package-id>
              dm package queue <package-id> <queue-id>
              dm package priority <package-id> high|normal|low
              dm archive status|list
              dm archive extract <path> | --job ID | --package ID [--password-stdin]
              dm archive password <set-id> | --package ID | --file PATH  (password from stdin)
              dm archive retry|cancel|remove <set-id>
              dm archive settings [--auto-extract on|off]
                          [--destination next|subfolder|folder] [--folder PATH]
                          [--preserve-folders on|off] [--conflict rename|overwrite|skip]
                          [--archives keep|delete]
              dm torrent inspect <magnet|file.torrent> [--dest DIR]   name, files and free space
              dm torrent add <magnet|file.torrent> [--dest DIR] [--later] [--files 0,2]
                          [--priority 0=high,1=skip] [--down BYTES/S] [--up BYTES/S] [--sequential]
              dm torrent files <job-id>
              dm torrent priority <job-id> INDEX=skip|low|normal|high ...
              dm torrent options <job-id> [--down BYTES/S] [--up BYTES/S]
                          [--seed-goal global|never|ratio:N|hours:N] [--sequential on|off]
              dm torrent recheck|announce|details <job-id>
              dm torrent seed <job-id> on|off
              dm torrent peers|trackers|pieces <job-id>
              dm torrent network   the torrent port, router forwarding and incoming connections
              dm refresh <job-id> <new-url> [--direct]
              dm cancel|remove <job-id> [--keep-file]   (deletes the downloaded file too, unless --keep-file)
              dm pause-all | resume-all
              dm queue list | add <name> [max] | start|stop <queue-id> | max <queue-id> <1-16>
              dm queue move <job-id> <queue-id> | quota <queue-id> <megabytes> <hours> | quota <queue-id> off
              dm schedule list|add|remove|enable|disable
                          [--open] [--exit]
                          [--shutdown shutdown|restart|logoff] [--force]
              dm site list | add <site> <username> [--password-env VAR] | remove <login-id>
                          (the password is read from standard input unless --password-env names it)
              dm settings [get] | settings set <key> <value> [<key> <value>...]
              | settings set proxypass   (reads it from standard input) | settings set proxypass --clear
              | settings export <file.json> | settings import <file.json>
                          keys: defaultdir, maxconcurrent, defaultconnections, launchatstartup, clipboard,
                          usecategoryfolders, closewindowcompletely, browserintegration, speedlimit, proxymode,
                          proxyaddress, proxyuser, allowsystemcompletionactions, preventsleep, ffmpegpath,
                          postdownload, updatemanifest, updatekey, updatekeyfile, updatechannel,
                          autocheckupdates, compresseddir, documentsdir, musicdir, programsdir, videodir,
                          usetorrentcategoryfolders, torrentdir (none: the same folders as other downloads)
              dm remote status
              dm remote key set        (reads the key from standard input, never from the command line)
              dm remote key clear
              dm remote classify <link>
              dm remote add <link>|- [--approve-uncached] [--dest DIR] [--queue ID]
                          [--start startNow|queue|later] [--select all|ask|ID,ID]
                          [--link-password-stdin [--link-password-for-archives]] [--archive-password-stdin]
              dm remote add-torrent <file> [--approve-uncached] [--dest DIR] [--queue ID] [--select ...]
              dm remote list
              dm remote files <id>
              dm remote select <id> all|<file-id>...
              dm remote retry <id> [--approve-uncached] [--link-password-stdin [--link-password-for-archives]]
              dm remote check [<id>]
              dm remote start-queued <id>
              dm remote remove <id> [--delete-from-provider --yes]
              dm remote browse torrents|webdl|usenet [--offset N] [--limit N] [--item ID]
              dm remote archive-password <id>   (from standard input; empty clears it)
              dm remote container <file.rsdf|file.ccf|file.dlc> [--add ID,ID | --all] [--dest DIR]
                          [--approve-uncached] [--queue ID] [--start startNow|queue|later]
              dm remote container-send-jd <file.dlc|file.ccf>
              dm host status | open [settings] | shutdown | clean-orphans [--delete]
              dm doctor [--no-start]

            Global:
              --json      Machine-readable output where supported
              --no-start  Do not launch the background host if it is not running
              --flag=VALUE  The same as --flag VALUE for any flag that takes a value, in every command;
                            in all but torrent, also the way to give a value that starts with "--"
            """);
    }
}

internal sealed class HostUnavailableException(string message) : Exception(message);

/// <summary>The Host answered, but with a refusal or an error instead of the result asked for.</summary>
internal sealed class HostRefusedException(string message) : Exception(message);

internal sealed class CliClient : IAsyncDisposable
{
    private const uint HandleFlagInherit = 1;

    private readonly IpcClient _client;

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern IntPtr GetStdHandle(int which);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetHandleInformation(IntPtr handle, uint mask, uint flags);

    /// <summary>Input, output and error. Nothing to do when one isn't there or can't be changed, as for a console's own.</summary>
    private static void KeepStandardHandlesFromChildren()
    {
        foreach (var which in new[] { -10, -11, -12 })
        {
            SetHandleInformation(GetStdHandle(which), HandleFlagInherit, 0);
        }
    }

    private CliClient(IpcClient client)
    {
        _client = client;
    }

    public static async Task<CliClient> ConnectAsync(bool startHost)
    {
        try
        {
            var connected = await TryConnectAsync().ConfigureAwait(false);
            if (connected is not null)
            {
                return new CliClient(connected);
            }
        }
        catch (UnauthorizedAccessException)
        {
            throw RunningHostPermissionDenied();
        }
        catch (HostUnavailableException)
        {
            throw;
        }
        catch (Exception)
        {
            // fall through to starting the host
        }

        if (!startHost)
        {
            throw new HostUnavailableException("The background host is not running.");
        }

        var host = HostLocator.FindHostExecutable()
                   ?? throw new HostUnavailableException("Could not find DownloadManager.Host.exe. Build or install the host first.");
        try
        {
            // The Host would otherwise inherit this process's standard handles and hold a script's pipe open
            // for as long as it runs, so `dm list | findstr` would never end. Not through the shell: that can
            // put a security warning on the desktop and wait for it.
            KeepStandardHandlesFromChildren();
            Process.Start(new ProcessStartInfo
            {
                FileName = host,
                Arguments = "--background",
                UseShellExecute = false,
                CreateNoWindow = true,
                WindowStyle = ProcessWindowStyle.Hidden,
            });
        }
        catch (Exception ex)
        {
            throw new HostUnavailableException($"Could not start the background host: {ex.Message}");
        }

        var deadline = DateTimeOffset.UtcNow + TimeSpan.FromSeconds(15);
        while (DateTimeOffset.UtcNow < deadline)
        {
            await Task.Delay(250).ConfigureAwait(false);
            try
            {
                var connected = await TryConnectAsync().ConfigureAwait(false);
                if (connected is not null)
                {
                    return new CliClient(connected);
                }
            }
            catch (UnauthorizedAccessException)
            {
                throw RunningHostPermissionDenied();
            }
            catch (HostUnavailableException)
            {
                throw;
            }
            catch (Exception)
            {
                // retry
            }
        }

        throw new HostUnavailableException("The background host did not become ready within 15 seconds.");
    }

    private static HostUnavailableException RunningHostPermissionDenied()
        => new("Windows denied access to the running Host. If it was started as administrator, exit Download Manager and start it normally.");

    private static async Task<IpcClient?> TryConnectAsync()
    {
        IpcClient? client = null;
        try
        {
            // PERF.7: one command per run, and a command that needs the list asks jobs.query for it, so neither the
            // greeting's snapshot nor the events are wanted.
            client = await IpcClient.ConnectAsync(new ClientHelloDto { Snapshot = false, Events = false },
                TimeSpan.FromMilliseconds(700)).ConfigureAwait(false);
            // A Host that takes the connection and then says nothing must not hang every command (Setup runs one).
            var hello = await ReadWithinAsync(client, HelloTimeout).ConfigureAwait(false);
            if (hello is null || hello.Type != MessageTypes.ServerHello)
            {
                await client.DisposeAsync().ConfigureAwait(false);
                return null;
            }

            if (hello.V != Protocol.Version)
            {
                await client.DisposeAsync().ConfigureAwait(false);
                throw new HostUnavailableException(
                    $"Protocol mismatch: client {Protocol.Version}, host {hello.V}.");
            }

            // An older Host sends the snapshot anyway.
            if (client.SnapshotFollowsHello) await ReadWithinAsync(client, HelloTimeout).ConfigureAwait(false);
            return client;
        }
        catch
        {
            if (client is not null)
            {
                await client.DisposeAsync().ConfigureAwait(false);
            }

            throw;
        }
    }

    /// <summary>How long the Host has to greet a new connection.</summary>
    private static readonly TimeSpan HelloTimeout = TimeSpan.FromSeconds(10);

    /// <summary>How long a reply may take. Most come at once; these wait on the network or the disk.</summary>
    internal static TimeSpan ReplyTimeout(string type) => type switch
    {
        MessageTypes.UpdateDownload or MessageTypes.UpdateInstall => TimeSpan.FromMinutes(30),
        // The Host waits up to 5 minutes for the user to answer JDownloader's dialog (JDownloaderBridge); waiting longer
        // here lets its own "no answer" come through instead of a timeout of ours at the same moment (audit 4 N3).
        MessageTypes.RemoteContainerSendToJd => TimeSpan.FromMinutes(6),
        _ when type.StartsWith("remote.", StringComparison.Ordinal) || type.StartsWith("torrent.", StringComparison.Ordinal) ||
               type.StartsWith("archive.", StringComparison.Ordinal) || type == MessageTypes.UpdateCheck ||
               type == MessageTypes.JobRefreshUrl || type == MessageTypes.JobAdd => TimeSpan.FromMinutes(5),
        _ => TimeSpan.FromMinutes(1),
    };

    private static async Task<IpcMessage?> ReadWithinAsync(IpcClient client, TimeSpan timeout)
    {
        using var cancel = new CancellationTokenSource(timeout);
        try
        {
            return await client.ReadAsync(cancel.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancel.IsCancellationRequested)
        {
            throw new HostUnavailableException($"The background host did not answer within {timeout.TotalSeconds:0} seconds.");
        }
    }

    /// <summary>
    /// The reply's payload as <typeparamref name="T"/>. A Host that could not do the command answers with
    /// host.actionResult instead (a refusal, or the error that stopped it); that becomes an error with its words, so it
    /// is never read as an empty result.
    /// </summary>
    public async Task<T> AskAsync<T>(string type, object payload)
        => Checked<T>(await RequestAsync(type, payload).ConfigureAwait(false));

    public static T Checked<T>(IpcMessage reply)
    {
        if (reply.Type == MessageTypes.HostActionResult && typeof(T) != typeof(HostActionResult))
        {
            throw new HostRefusedException(reply.GetPayload<HostActionResult>()?.Message is { Length: > 0 } message
                ? message
                : "The Host didn't take this command.");
        }

        return reply.GetPayload<T>() ?? throw new HostRefusedException("The Host's answer was empty.");
    }

    public async Task<IpcMessage> RequestAsync<T>(string type, T payload)
    {
        var request = IpcMessage.Create(type, payload);
        await _client.SendAsync(request).ConfigureAwait(false);
        var timeout = ReplyTimeout(type);
        using var cancel = new CancellationTokenSource(timeout);
        while (true)
        {
            IpcMessage? read;
            try
            {
                read = await _client.ReadAsync(cancel.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (cancel.IsCancellationRequested)
            {
                throw new HostUnavailableException($"The background host did not answer within {timeout.TotalSeconds:0} seconds.");
            }

            var message = read ?? throw new HostUnavailableException("The background host closed the connection.");
            if (message.ReplyTo == request.Id)
            {
                if (message.Type == MessageTypes.ServerIncompatible)
                {
                    throw new HostUnavailableException(message.GetPayload<IncompatibleDto>().Message);
                }

                return message;
            }

            // job.event and broadcast snapshots are not replies; keep reading.
        }
    }

    public Task<SnapshotDto> SnapshotAsync() => AskAsync<SnapshotDto>(MessageTypes.JobsQuery, new { });

    public Task<HostStatusDto> StatusAsync() => AskAsync<HostStatusDto>(MessageTypes.HostStatus, new { });

    /// <summary>The settings as the Host holds them now: read right before a change is made to them and sent back.</summary>
    public async Task<SettingsDto> SettingsAsync()
    {
        var result = await AskAsync<SettingsResult>(MessageTypes.SettingsGet, new { }).ConfigureAwait(false);
        return result.Ok ? result.Settings : throw new HostRefusedException(result.Error ?? "The Host did not send its settings.");
    }

    public async ValueTask DisposeAsync() => await _client.DisposeAsync().ConfigureAwait(false);
}

internal static class HostLocator
{
    public static string? FindHostExecutable()
    {
        var candidates = new List<string>
        {
            Path.Combine(AppContext.BaseDirectory, "DownloadManager.Host.exe"),
            Path.Combine(AppContext.BaseDirectory, "..", "DownloadManager.Host.exe"),
            Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", "DownloadManager.Host", "bin", "Debug", "net10.0-windows", "DownloadManager.Host.exe"),
            Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", "DownloadManager.Host", "bin", "Release", "net10.0-windows", "DownloadManager.Host.exe"),
        };

        var fromEnv = Environment.GetEnvironmentVariable("DOWNLOADMANAGER_HOST");
        if (!string.IsNullOrWhiteSpace(fromEnv))
        {
            candidates.Insert(0, fromEnv);
        }

        return candidates.Select(Path.GetFullPath).FirstOrDefault(File.Exists);
    }
}

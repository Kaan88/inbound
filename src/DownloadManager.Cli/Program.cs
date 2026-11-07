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
        var optionArgs = args.Skip(1).Where(a => !a.StartsWith("--", StringComparison.Ordinal)).ToArray();

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
                    Console.Error.WriteLine("usage: add <url> [--dest DIR] [--name NAME] [--category ID] [--start now|later] [--priority high|normal|low] [--package KEY] [--package-name NAME] [--direct] [--wait]");
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

                var request = new AddJobRequest
                {
                    Url = optionArgs[0],
                    DestinationDirectory = GetOption(args, "--dest"),
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

                if (int.TryParse(GetOption(args, "--connections"), out var connections))
                {
                    request.Connections = connections;
                }

                if (int.TryParse(GetOption(args, "--queue"), out var queueId))
                {
                    request.QueueId = queueId;
                }

                foreach (var header in GetOptions(args, "--header"))
                {
                    var separator = header.IndexOf(':');
                    if (separator > 0)
                    {
                        request.Headers[header[..separator].Trim()] = header[(separator + 1)..].Trim();
                    }
                }

                var reply = await client.RequestAsync(MessageTypes.JobAdd, request).ConfigureAwait(false);
                var result = reply.GetPayload<AddJobResponse>();
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

                if (args.Contains("--wait", StringComparer.OrdinalIgnoreCase))
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
                if (!Guid.TryParse(optionArgs.FirstOrDefault(), out var id))
                {
                    Console.Error.WriteLine("usage: status <job-id>");
                    return 2;
                }

                var snapshot = await client.SnapshotAsync().ConfigureAwait(false);
                var job = snapshot.Jobs.FirstOrDefault(j => j.Id == id);
                if (job is null)
                {
                    Console.Error.WriteLine("job not found");
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
                var content = JobListFile.Serialize(snapshot.Jobs);
                await File.WriteAllTextAsync(optionArgs[0], content).ConfigureAwait(false);
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

                if (entries.Count > 25 && !args.Contains("--yes", StringComparer.OrdinalIgnoreCase))
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
                foreach (var entry in entries)
                {
                    if (!Uri.TryCreate(entry.Url, UriKind.Absolute, out var uri) ||
                        uri.Scheme is not ("http" or "https"))
                    {
                        failed++;
                        firstError ??= $"Invalid URL: {entry.Url}";
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

                        var reply = await client.RequestAsync(MessageTypes.JobAdd, new AddJobRequest
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
                            Source = "import",
                        }).ConfigureAwait(false);
                        var result = reply.GetPayload<AddJobResponse>();
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
                    (failed > 0 ? $"; {failed} failed: {firstError}" : "."));
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
                Process.Start(start);
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

            case "package":
                return await PackageAsync(client, optionArgs, json).ConfigureAwait(false);

            case "pause":
                return await SimpleJobActionAsync(client, optionArgs, MessageTypes.JobPause, json).ConfigureAwait(false);
            case "resume":
            case "retry":
                return await SimpleJobActionAsync(client, optionArgs, MessageTypes.JobResume, json).ConfigureAwait(false);
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
                        ExplicitDirect = args.Contains("--direct", StringComparer.OrdinalIgnoreCase),
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

                var keep = args.Contains("--keep-file", StringComparer.OrdinalIgnoreCase);
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

                var keep = args.Contains("--keep-file", StringComparer.OrdinalIgnoreCase);
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
                        Console.WriteLine($"tray: {status.TrayAvailable}  native host: {(status.NativeHostRegistered ? "registered" : "not registered")}");
                        Console.WriteLine($"browser integration: {status.BrowserIntegration}");
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
                    var delete = args.Contains("--delete", StringComparer.OrdinalIgnoreCase);
                    var reply = await client.RequestAsync(MessageTypes.HostCleanOrphans,
                        new CleanOrphansRequest { Delete = delete }).ConfigureAwait(false);
                    var result = reply.GetPayload<CleanOrphansResponse>();
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
                    var reply = await client.RequestAsync(MessageTypes.HostOpenUi,
                        new HostActionRequest { Action = "settings" }).ConfigureAwait(false);
                    return PrintAction(reply, json);
                }

                if (sub == "shutdown")
                {
                    var reply = await client.RequestAsync(MessageTypes.HostShutdown, new { }).ConfigureAwait(false);
                    return PrintAction(reply, json);
                }

                Console.Error.WriteLine("usage: host status|open|shutdown");
                return 2;
            }
            default:
                Console.Error.WriteLine($"unknown command '{command}'");
                PrintHelp();
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
        var result = reply.GetPayload<GroupActionResult>();
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

    private static async Task<int> SimpleJobActionAsync(CliClient client, string[] positional, string type, bool json)
    {
        if (!Guid.TryParse(positional.FirstOrDefault(), out var id))
        {
            Console.Error.WriteLine($"usage: {type.Replace("job.", string.Empty)} <job-id>");
            return 2;
        }

        var reply = await client.RequestAsync(type, new JobActionRequest { JobId = id }).ConfigureAwait(false);
        return PrintAction(reply, json);
    }

    private static int PrintAction(IpcMessage reply, bool json)
    {
        if (reply.Type == MessageTypes.JobActionResult)
        {
            var result = reply.GetPayload<JobActionResponse>();
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

    private static async Task<int> SiteAsync(CliClient client, string[] args, bool json)
    {
        var positional = args.Skip(1)
            .Where(a => !a.StartsWith("--", StringComparison.Ordinal))
            .ToArray();
        var sub = positional.FirstOrDefault()?.ToLowerInvariant() ?? "list";
        var snapshot = await client.SnapshotAsync().ConfigureAwait(false);
        var settings = snapshot.Settings;

        if (sub == "list")
        {
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
                Console.Error.WriteLine("usage: site add <site> <username> [--password ...] [--password-env VAR]");
                return 2;
            }

            var password = GetOption(args, "--password");
            if (GetOption(args, "--password-env") is { } envName)
            {
                password = Environment.GetEnvironmentVariable(envName);
            }

            if (string.IsNullOrEmpty(password))
            {
                Console.Error.WriteLine("A password is required (--password or --password-env).");
                return 2;
            }

            settings.SiteLogins.Add(new SiteLoginDto
            {
                Id = Guid.NewGuid(),
                Site = positional[1],
                Username = positional[2],
                Password = password,
            });
            var added = await client.RequestAsync(MessageTypes.SettingsUpdate,
                new SettingsUpdateRequest { Settings = settings }).ConfigureAwait(false);
            return PrintSettingsResult(added, json);
        }

        if (sub == "remove")
        {
            if (positional.Length < 2)
            {
                Console.Error.WriteLine("usage: site remove <login-id>");
                return 2;
            }

            var selector = positional[1].Replace("-", string.Empty, StringComparison.Ordinal);
            var matches = settings.SiteLogins
                .Where(login => login.Id.ToString("N").StartsWith(selector, StringComparison.OrdinalIgnoreCase))
                .ToList();
            if (matches.Count != 1)
            {
                Console.Error.WriteLine($"site '{positional[1]}' did not match exactly one entry.");
                return 1;
            }

            settings.SiteLogins.RemoveAll(login => login.Id == matches[0].Id);
            var removed = await client.RequestAsync(MessageTypes.SettingsUpdate,
                new SettingsUpdateRequest { Settings = settings }).ConfigureAwait(false);
            return PrintSettingsResult(removed, json);
        }

        Console.Error.WriteLine("usage: site list|add|remove");
        return 2;
    }

    private static async Task<int> ScheduleAsync(CliClient client, string[] args, bool json)
    {
        var positional = args.Skip(1)
            .Where(a => !a.StartsWith("--", StringComparison.Ordinal))
            .ToArray();
        var sub = positional.FirstOrDefault()?.ToLowerInvariant() ?? "list";
        var snapshot = await client.SnapshotAsync().ConfigureAwait(false);
        var settings = snapshot.Settings;

        if (sub == "list")
        {
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
                Name = GetOption(args, "--name") ?? $"Schedule {settings.Schedules.Count + 1}",
                QueueId = queueId,
                Enabled = !args.Contains("--disabled", StringComparer.OrdinalIgnoreCase),
                Kind = kind,
                StartDate = startDate,
                Days = GetOption(args, "--days") ?? "Mon,Tue,Wed,Thu,Fri,Sat,Sun",
                StartTime = start.ToString("HH:mm", CultureInfo.InvariantCulture),
                StopTime = stop?.ToString("HH:mm", CultureInfo.InvariantCulture),
                OpenFileWhenDone = args.Contains("--open", StringComparer.OrdinalIgnoreCase),
                ExitWhenDone = args.Contains("--exit", StringComparer.OrdinalIgnoreCase),
                ShutdownWhenDone = shutdownMode is not null,
                ShutdownMode = shutdownMode ?? "shutdown",
                ForceShutdown = args.Contains("--force", StringComparer.OrdinalIgnoreCase),
            };
            settings.Schedules.Add(schedule);
            var added = await client.RequestAsync(MessageTypes.SettingsUpdate,
                new SettingsUpdateRequest { Settings = settings }).ConfigureAwait(false);
            var addedResult = added.GetPayload<SettingsResult>();
            if (addedResult.Ok)
            {
                Console.WriteLine(
                    $"added: {schedule.Id:N} {schedule.Kind} " +
                    $"queue {schedule.QueueId}" +
                    $" at {schedule.StartTime}");
            }

            return PrintSettingsResult(added, json);
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

            var changed = await client.RequestAsync(MessageTypes.SettingsUpdate,
                new SettingsUpdateRequest { Settings = settings }).ConfigureAwait(false);
            return PrintSettingsResult(changed, json);
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
        var type = sub switch
        {
            "download" => MessageTypes.UpdateDownload,
            "install" => MessageTypes.UpdateInstall,
            _ => MessageTypes.UpdateCheck,
        };
        var reply = await client.RequestAsync(type, new { }).ConfigureAwait(false);

        if (sub == "install")
        {
            var install = reply.GetPayload<UpdateInstallResult>();
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
            var result = reply.GetPayload<UpdateDownloadResult>();
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

        var check = reply.GetPayload<UpdateCheckResult>();
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
        var snapshot = await client.SnapshotAsync().ConfigureAwait(false);
        var settings = snapshot.Settings;

        if (sub == "list")
        {
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
                Console.Error.WriteLine("usage: profile add <host-pattern> <max-connections>");
                return 2;
            }

            var pattern = positional[1].Trim();
            settings.ConnectionProfiles.RemoveAll(profile =>
                string.Equals(profile.Pattern, pattern, StringComparison.OrdinalIgnoreCase));
            settings.ConnectionProfiles.Add(new ConnectionProfileDto
            {
                Pattern = pattern,
                MaxConnections = maxConnections,
            });
            var reply = await client.RequestAsync(MessageTypes.SettingsUpdate,
                new SettingsUpdateRequest { Settings = settings }).ConfigureAwait(false);
            return PrintSettingsResult(reply, json);
        }

        if (sub == "remove")
        {
            if (positional.Length < 2)
            {
                Console.Error.WriteLine("usage: profile remove <host-pattern>");
                return 2;
            }

            var pattern = positional[1].Trim();
            var removed = settings.ConnectionProfiles.RemoveAll(profile =>
                string.Equals(profile.Pattern, pattern, StringComparison.OrdinalIgnoreCase));
            if (removed == 0)
            {
                Console.Error.WriteLine($"profile '{pattern}' not found.");
                return 1;
            }

            var reply = await client.RequestAsync(MessageTypes.SettingsUpdate,
                new SettingsUpdateRequest { Settings = settings }).ConfigureAwait(false);
            return PrintSettingsResult(reply, json);
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

        if (urls.Count > 25 && !args.Contains("--yes", StringComparer.OrdinalIgnoreCase))
        {
            Console.Error.WriteLine(
                $"batch pattern expands to {urls.Count} URLs; re-run with --yes to add them all.");
            return 2;
        }

        var startMode = GetOption(args, "--start") ?? "now";
        int? connections = int.TryParse(GetOption(args, "--connections"), out var parsedConnections)
            ? parsedConnections
            : null;
        int? queueId = int.TryParse(GetOption(args, "--queue"), out var parsedQueue)
            ? parsedQueue
            : null;
        var destination = GetOption(args, "--dest");
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

                var reply = await client.RequestAsync(MessageTypes.JobAdd, new AddJobRequest
                {
                    Url = url,
                    DestinationDirectory = destination,
                    StartMode = startMode,
                    ConflictPolicy = conflict ?? "rename",
                    Source = "batch",
                    Connections = connections,
                    QueueId = queueId.GetValueOrDefault(-1),
                    Priority = priority,
                }).ConfigureAwait(false);
                var result = reply.GetPayload<AddJobResponse>();
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

        if (args.Contains("--wait", StringComparer.OrdinalIgnoreCase))
        {
            var exit = 0;
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
        var optionArgs = args.Skip(1).Where(a => !a.StartsWith("--", StringComparison.Ordinal)).ToArray();
        var sub = optionArgs.FirstOrDefault()?.ToLowerInvariant() ?? "list";
        var snapshot = await client.SnapshotAsync().ConfigureAwait(false);
        var settings = snapshot.Settings;

        if (sub == "list")
        {
            if (json)
            {
                PrintJson(settings.Queues);
            }
            else
            {
                Console.WriteLine($"{"ID",-4} {"STATE",-8} {"MAX",4} {"QUOTA",-18}  NAME");
                foreach (var queue in settings.Queues.OrderBy(q => q.Order))
                {
                    var quota = queue.QuotaEnabled
                        ? $"{queue.QuotaBytesUsed / (1024 * 1024)}/{queue.QuotaBytes / (1024 * 1024)}MB/{queue.QuotaWindowHours}h"
                        : "-";
                    Console.WriteLine($"{queue.Id,-4} {(queue.IsRunning ? "running" : "stopped"),-8} {queue.MaxConcurrentDownloads,4} {quota,-18}  {queue.Name}");
                }
            }

            return 0;
        }

        if (sub == "add")
        {
            var name = optionArgs.Skip(1).FirstOrDefault();
            if (string.IsNullOrWhiteSpace(name))
            {
                Console.Error.WriteLine("usage: queue add <name> [max]");
                return 2;
            }

            var max = optionArgs.Length > 2 && int.TryParse(optionArgs[2], out var parsedMax) ? parsedMax : 3;
            var nextId = settings.Queues.Count == 0 ? 0 : settings.Queues.Max(q => q.Id) + 1;
            settings.Queues.Add(new QueueDto
            {
                Id = nextId,
                Name = name,
                MaxConcurrentDownloads = max,
                IsRunning = true,
                Order = settings.Queues.Count,
            });
            var update = await client.RequestAsync(MessageTypes.SettingsUpdate,
                new SettingsUpdateRequest { Settings = settings }).ConfigureAwait(false);
            return PrintSettingsResult(update, json);
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
            if (optionArgs.Length < 3 || !int.TryParse(optionArgs[1], out var quotaQueueId))
            {
                Console.Error.WriteLine("usage: queue quota <queue-id> <megabytes> <window-hours> | queue quota <queue-id> off");
                return 2;
            }

            var queue = settings.Queues.FirstOrDefault(q => q.Id == quotaQueueId);
            if (queue is null)
            {
                Console.Error.WriteLine("Queue not found.");
                return 1;
            }

            if (optionArgs[2].Equals("off", StringComparison.OrdinalIgnoreCase))
            {
                queue.QuotaEnabled = false;
            }
            else
            {
                if (!long.TryParse(optionArgs[2], out var megabytes) || megabytes <= 0 ||
                    optionArgs.Length < 4 || !int.TryParse(optionArgs[3], out var hours) || hours <= 0)
                {
                    Console.Error.WriteLine("usage: queue quota <queue-id> <megabytes> <window-hours>");
                    return 2;
                }

                queue.QuotaEnabled = true;
                queue.QuotaBytes = megabytes * 1024 * 1024;
                queue.QuotaWindowHours = hours;
                queue.QuotaWindowStart = DateTimeOffset.UtcNow;
                queue.QuotaBytesUsed = 0;
            }

            var quotaUpdate = await client.RequestAsync(MessageTypes.SettingsUpdate,
                new SettingsUpdateRequest { Settings = settings }).ConfigureAwait(false);
            return PrintSettingsResult(quotaUpdate, json);
        }

        if (sub is "start" or "stop" or "max")
        {
            if (optionArgs.Length < 2 || !int.TryParse(optionArgs[1], out var queueId))
            {
                Console.Error.WriteLine($"usage: queue {sub} <queue-id> [max]");
                return 2;
            }

            var request = new QueueActionRequest
            {
                QueueId = queueId,
                Action = sub,
                MaxConcurrentDownloads = optionArgs.Length > 2 && int.TryParse(optionArgs[2], out var maxValue)
                    ? maxValue
                    : 3,
            };
            var reply = await client.RequestAsync(MessageTypes.QueueAction, request).ConfigureAwait(false);
            return PrintSettingsResult(reply, json);
        }

        Console.Error.WriteLine("usage: queue list|add|start|stop|max");
        return 2;
    }

    private static int PrintSettingsResult(IpcMessage reply, bool json)
    {
        var result = reply.GetPayload<SettingsResult>();
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
            current.Settings.Proxy.Password = null;
            foreach (var login in current.Settings.SiteLogins)
            {
                login.Password = null;
            }

            await File.WriteAllTextAsync(
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

            imported.Proxy.Password = null;
            foreach (var login in imported.SiteLogins)
            {
                login.Password = null;
            }

            var importedReply = await client.RequestAsync(MessageTypes.SettingsUpdate,
                new SettingsUpdateRequest { Settings = imported }).ConfigureAwait(false);
            return PrintSettingsResult(importedReply, json);
        }

        if (!optionArgs[0].Equals("set", StringComparison.OrdinalIgnoreCase))
        {
            Console.Error.WriteLine("usage: settings [get|set key value|export|import]");
            return 2;
        }

        var snapshot = await client.SnapshotAsync().ConfigureAwait(false);
        var settings = snapshot.Settings;
        // `settings set proxypass --clear`: "--" arguments are filtered from optionArgs above,
        // so the flag is read from the raw arguments.
        var clearProxyPassword = optionArgs.Length == 2 &&
                                 optionArgs[1].Equals("proxypass", StringComparison.OrdinalIgnoreCase) &&
                                 args.Contains("--clear", StringComparer.OrdinalIgnoreCase);
        if (clearProxyPassword)
        {
            settings.Proxy.Password = null;
            settings.Proxy.ClearPassword = true;
        }
        else if (optionArgs.Length < 3)
        {
            Console.Error.WriteLine("usage: settings set <key> <value> (or: settings set proxypass --clear)");
            return 2;
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
                case "proxypass":
                    settings.Proxy.Password = value;
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
                    settings.UpdatePublicKey = File.ReadAllText(value).Trim();
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
                    Console.Error.WriteLine($"unknown setting '{key}'");
                    return 2;
            }
        }

        var reply = await client.RequestAsync(MessageTypes.SettingsUpdate,
            new SettingsUpdateRequest { Settings = settings }).ConfigureAwait(false);
        var result = reply.GetPayload<SettingsResult>();
        if (json)
        {
            PrintJson(result);
        }
        else if (result.Ok)
        {
            PrintSettings(result.Settings);
        }
        else
        {
            Console.Error.WriteLine(result.Error);
        }

        return result.Ok ? 0 : 1;
    }

    private static async Task<int> WaitForAsync(CliClient client, Guid id, bool json)
    {
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
                Console.Write($"\r{job.State,-12} {FormatUtil.Bytes(job.ReceivedBytes),-12} {job.BytesPerSecond ?? 0,10:0.0} B/s   ");
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

            await Task.Delay(500).ConfigureAwait(false);
        }
    }

    private static async Task<int> DoctorAsync(bool noStart)
    {
        try
        {
            await using var client = await CliClient.ConnectAsync(startHost: !noStart).ConfigureAwait(false);
            var snapshot = await client.SnapshotAsync().ConfigureAwait(false);
            var status = await client.StatusAsync().ConfigureAwait(false);
            Console.WriteLine("Inbound Download Manager diagnostics");
            Console.WriteLine($"  host:        {status.HostVersion} (protocol {status.ProtocolVersion})");
            Console.WriteLine($"  state dir:   {status.StateDirectory}");
            Console.WriteLine($"  tray:        {(status.TrayAvailable ? "yes" : "no")}");
            Console.WriteLine($"  native host: {(status.NativeHostRegistered ? "registered" : "not registered")}");
            Console.WriteLine($"  jobs:        {status.TotalJobs} total, {status.ActiveDownloads} active, {status.QueuedDownloads} queued");
            Console.WriteLine($"  integration: {(snapshot.Settings.BrowserIntegrationEnabled ? "enabled" : "disabled")}");
            return 0;
        }
        catch (HostUnavailableException ex)
        {
            Console.Error.WriteLine(ex.Message);
            return 3;
        }
    }

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
            var size = job.TotalBytes is null ? FormatUtil.Bytes(job.ReceivedBytes) : FormatUtil.Bytes(job.TotalBytes.Value);
            var speed = job.BytesPerSecond is null ? "-" : $"{FormatUtil.Bytes((long)job.BytesPerSecond.Value)}/s";
            Console.WriteLine($"{job.State,-12} {size,12} {progress,9} {speed,12}  {job.FileName}");
            Console.WriteLine($"  {job.Id:N}  {ShownAddress.For(job.Url, job.AddressHidden)}");
            if (job.StatusNote is not null) Console.WriteLine($"  {job.StatusNote}");
        }
    }

    private static void PrintJobDetails(JobDto job)
    {
        Console.WriteLine($"id:       {job.Id:N}");
        Console.WriteLine($"file:     {job.FinalFilePath}");
        Console.WriteLine($"{(ShownAddress.IsHidden(job.Url, job.AddressHidden) ? "site:" : "url:"),-10}{ShownAddress.For(job.Url, job.AddressHidden)}");
        Console.WriteLine($"state:    {job.State}");
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

    private static string? GetOption(string[] args, string name)
    {
        for (var i = 0; i < args.Length - 1; i++)
        {
            if (args[i].Equals(name, StringComparison.OrdinalIgnoreCase))
            {
                return args[i + 1];
            }
        }

        return null;
    }

    private static IEnumerable<string> GetOptions(string[] args, string name)
    {
        for (var i = 0; i < args.Length - 1; i++)
        {
            if (args[i].Equals(name, StringComparison.OrdinalIgnoreCase))
            {
                yield return args[i + 1];
            }
        }
    }

    private static bool ParseBool(string value)
        => value.Equals("true", StringComparison.OrdinalIgnoreCase) ||
           value.Equals("1", StringComparison.OrdinalIgnoreCase) ||
           value.Equals("yes", StringComparison.OrdinalIgnoreCase) ||
           value.Equals("on", StringComparison.OrdinalIgnoreCase);

    private static void PrintHelp()
    {
        Console.WriteLine("""
            Download Manager CLI

            Usage:
              dm add <url> [--dest DIR] [--name NAME] [--category ID] [--start now|later] [--direct] [--wait]
                          [--connections N] [--header "Name: value"] [--sha256 HEX]
                          [--conflict rename|overwrite|fail] [--priority high|normal|low]
              dm add-batch <pattern> [--dest DIR] [--start now|later] [--direct] [--wait] [--yes]
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
              dm archive settings [--auto-extract on|off] [--unrar-path PATH | --unrar-path-default]
                          [--destination next|subfolder|folder] [--folder PATH]
                          [--preserve-folders on|off] [--conflict rename|overwrite|skip]
                          [--archives keep|delete]
              dm refresh <job-id> <new-url> [--direct]
              dm cancel|remove <job-id> [--keep-file]
              dm pause-all | resume-all
              dm queue list|add|start|stop|max|move|quota
              dm schedule list|add|remove|enable|disable
                          [--open] [--exit]
                          [--shutdown shutdown|restart|logoff] [--force]
              dm site list|add|remove
              dm settings [get] | settings set <key> <value>
              | settings export <file.json> | settings import <file.json>
              dm remote status
              dm remote key set        (reads the key from standard input, never from the command line)
              dm remote key clear
              dm remote classify <link>
              dm remote add <link>|- [--approve-uncached] [--dest DIR] [--queue ID]
                          [--start startNow|queue|later] [--select all|ask|ID,ID] [--link-password-stdin]
                          [--archive-password-stdin]
              dm remote add-torrent <file> [--approve-uncached] [--dest DIR] [--queue ID] [--select ...]
              dm remote list
              dm remote files <id>
              dm remote select <id> all|<file-id>...
              dm remote retry <id> [--approve-uncached] [--link-password-stdin]
              dm remote check [<id>]
              dm remote remove <id> [--delete-from-provider --yes]
              dm remote browse torrents|webdl|usenet [--offset N] [--limit N] [--item ID]
              dm remote archive-password <id>   (from standard input; empty clears it)
              dm remote container <file.rsdf|file.ccf|file.dlc> [--add ID,ID | --all] [--dest DIR]
                          [--approve-uncached] [--queue ID] [--start startNow|queue|later]
              dm remote container-send-jd <file.dlc|file.ccf>
              dm host status|open|shutdown
              dm doctor [--no-start]

            Global:
              --json      Machine-readable output where supported
              --no-start  Do not launch the background host if it is not running
            """);
    }
}

internal sealed class HostUnavailableException(string message) : Exception(message);

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
            client = await IpcClient.ConnectAsync(TimeSpan.FromMilliseconds(700)).ConfigureAwait(false);
            var hello = await client.ReadAsync().ConfigureAwait(false);
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

            // Consume the initial snapshot.
            await client.ReadAsync().ConfigureAwait(false);
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

    public async Task<IpcMessage> RequestAsync<T>(string type, T payload)
    {
        var request = IpcMessage.Create(type, payload);
        await _client.SendAsync(request).ConfigureAwait(false);
        while (true)
        {
            var message = await _client.ReadAsync().ConfigureAwait(false)
                          ?? throw new HostUnavailableException("The background host closed the connection.");
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

    public async Task<SnapshotDto> SnapshotAsync()
    {
        var reply = await RequestAsync(MessageTypes.JobsQuery, new { }).ConfigureAwait(false);
        return reply.GetPayload<SnapshotDto>();
    }

    public async Task<HostStatusDto> StatusAsync()
    {
        var reply = await RequestAsync(MessageTypes.HostStatus, new { }).ConfigureAwait(false);
        return reply.GetPayload<HostStatusDto>();
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

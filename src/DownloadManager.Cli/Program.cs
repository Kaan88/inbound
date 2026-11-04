using System.Diagnostics;
using System.Globalization;
using System.Text;
using System.Text.Json;
using DownloadManager.Contracts;
using DownloadManager.Core;

namespace DownloadManager.Cli;

internal static class Program
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
            Console.WriteLine($"Download Manager CLI {Protocol.HostVersion} (protocol {Protocol.Version})");
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
                    Console.Error.WriteLine("usage: add <url> [--dest DIR] [--name NAME] [--start now|later] [--wait]");
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
                    StartMode = GetOption(args, "--start") ?? "now",
                    ConflictPolicy = conflict ?? "rename",
                    Description = GetOption(args, "--description"),
                    Referrer = GetOption(args, "--referrer"),
                    UserAgent = GetOption(args, "--user-agent"),
                    ExpectedSha256 = GetOption(args, "--sha256"),
                    Source = "cli",
                };
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

            case "grab":
                return await GrabAsync(client, args, optionArgs, json).ConfigureAwait(false);

            case "grabber":
                return await GrabberAsync(client, args, optionArgs, json).ConfigureAwait(false);

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
                    Console.Error.WriteLine("usage: import <file.json> [--start now|later] [--yes]");
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
                        var reply = await client.RequestAsync(MessageTypes.JobAdd, new AddJobRequest
                        {
                            Url = entry.Url,
                            FileName = entry.FileName,
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

            case "pause":
                return await SimpleJobActionAsync(client, optionArgs, MessageTypes.JobPause, json).ConfigureAwait(false);
            case "resume":
            case "retry":
                return await SimpleJobActionAsync(client, optionArgs, MessageTypes.JobResume, json).ConfigureAwait(false);
            case "refresh":
            {
                if (optionArgs.Length < 2 || !Guid.TryParse(optionArgs[0], out var refreshId))
                {
                    Console.Error.WriteLine("usage: refresh <job-id> <new-url>");
                    return 2;
                }

                var reply = await client.RequestAsync(MessageTypes.JobRefreshUrl,
                    new JobRefreshUrlRequest { JobId = refreshId, Url = optionArgs[1] }).ConfigureAwait(false);
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
                if (!Guid.TryParse(optionArgs.FirstOrDefault(), out var id))
                {
                    Console.Error.WriteLine("usage: remove <job-id> [--keep-file]");
                    return 2;
                }

                var keep = args.Contains("--keep-file", StringComparer.OrdinalIgnoreCase);
                var reply = await client.RequestAsync(MessageTypes.JobRemove,
                    new JobActionRequest { JobId = id, DeleteFile = !keep }).ConfigureAwait(false);
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
                    }

                    return 0;
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
                    (schedule.GrabberProjectId is { } grabber ? $" grabber={grabber:N}" : string.Empty) +
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
                Console.Error.WriteLine("usage: schedule add <queue-id> <HH:mm> [--grabber PROJECT-ID] [--days ...] [--stop HH:mm] [--once yyyy-MM-dd] [--name ...] [--disabled]");
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

            Guid? grabberProjectId = null;
            if (GetOption(args, "--grabber") is { } grabberSelector)
            {
                grabberProjectId = await ResolveGrabberProjectIdAsync(client, grabberSelector)
                    .ConfigureAwait(false);
                if (grabberProjectId is null)
                {
                    return 2;
                }
            }

            var schedule = new ScheduleDto
            {
                Id = Guid.NewGuid(),
                Name = GetOption(args, "--name") ?? $"Schedule {settings.Schedules.Count + 1}",
                QueueId = queueId,
                GrabberProjectId = grabberProjectId,
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
                    (schedule.GrabberProjectId is { } grabber
                        ? $"grabber {grabber:N}"
                        : $"queue {schedule.QueueId}") +
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

    private static async Task<int> GrabAsync(
        CliClient client,
        string[] args,
        string[] positional,
        bool json)
    {
        if (positional.Length == 0)
        {
            Console.Error.WriteLine(
                "usage: grab <start-url> [--dest DIR] [--depth N] [--max-pages N] " +
                "[--extensions zip,mp3] [--include GLOB] [--exclude GLOB] " +
                "[--no-pages] [--no-rewrite] [--start now|later] [--queue ID]");
            return 2;
        }

        var request = ParseGrabberRequest(args, positional[0]);
        var reply = await client.RequestAsync(MessageTypes.GrabberRun, request).ConfigureAwait(false);
        var result = reply.GetPayload<SiteGrabberResult>();
        if (json)
        {
            PrintJson(result);
        }
        else if (!result.Ok)
        {
            Console.Error.WriteLine($"grab failed: {result.Error}");
        }
        else
        {
            Console.WriteLine(
                $"crawled {result.PagesCrawled} pages, queued {result.FilesQueued} files, " +
                $"skipped {result.FilesSkipped}");
            foreach (var error in result.Errors)
            {
                Console.WriteLine($"  warning: {error}");
            }
        }

        return result.Ok ? 0 : 1;
    }

    private static async Task<int> GrabberAsync(
        CliClient client,
        string[] args,
        string[] positional,
        bool json)
    {
        var sub = positional.FirstOrDefault()?.ToLowerInvariant() ?? "list";
        if (sub == "list")
        {
            var reply = await client.RequestAsync(MessageTypes.GrabberProjectsQuery, new { })
                .ConfigureAwait(false);
            var result = reply.GetPayload<GrabberProjectsResult>();
            if (json)
            {
                PrintJson(result.Projects);
                return 0;
            }

            Console.WriteLine($"{"ID",-9} {"STATUS",-12} {"PAGES",5} {"QUEUED",6} {"SKIPPED",7}  NAME");
            foreach (var project in result.Projects)
            {
                Console.WriteLine(
                    $"{project.Id.ToString("N")[..8],-9} {project.Status,-12} " +
                    $"{project.PagesCrawled,5} {project.FilesQueued,6} {project.FilesSkipped,7}  " +
                    $"{project.Name}  {project.Request.StartUrl}");
            }

            return 0;
        }

        if (sub == "add")
        {
            if (positional.Length < 2)
            {
                Console.Error.WriteLine("usage: grabber add <start-url> [--name NAME] [--dest DIR] [grabber options]");
                return 2;
            }

            var request = ParseGrabberRequest(args, positional[1]);
            var defaultName = Uri.TryCreate(request.StartUrl, UriKind.Absolute, out var startUri)
                ? $"Grab {startUri.Host}"
                : "Grabber project";
            var created = await client.RequestAsync(MessageTypes.GrabberProjectCreate,
                new GrabberProjectCreateRequest
                {
                    Name = GetOption(args, "--name") ?? defaultName,
                    Request = request,
                }).ConfigureAwait(false);
            var result = created.GetPayload<GrabberProjectResult>();
            if (json)
            {
                PrintJson(result);
            }
            else if (!result.Ok)
            {
                Console.Error.WriteLine($"grabber add failed: {result.Error}");
            }
            else
            {
                Console.WriteLine($"added: {result.Project!.Id:N}  {result.Project.Name}");
            }

            return result.Ok ? 0 : 1;
        }

        if (sub is "run" or "remove")
        {
            if (positional.Length < 2)
            {
                Console.Error.WriteLine($"usage: grabber {sub} <project-id>");
                return 2;
            }

            var projectId = await ResolveGrabberProjectIdAsync(client, positional[1]).ConfigureAwait(false);
            if (projectId is null)
            {
                return 1;
            }

            var type = sub == "run" ? MessageTypes.GrabberProjectRun : MessageTypes.GrabberProjectRemove;
            var reply = await client.RequestAsync(type,
                new GrabberProjectActionRequest { Id = projectId.Value }).ConfigureAwait(false);
            var result = reply.GetPayload<GrabberProjectResult>();
            if (json)
            {
                PrintJson(result);
            }
            else if (!result.Ok)
            {
                Console.Error.WriteLine($"grabber {sub} failed: {result.Error}");
            }
            else if (sub == "run")
            {
                var project = result.Project!;
                Console.WriteLine(
                    $"{project.Name}: {project.Status}; {project.PagesCrawled} pages, " +
                    $"{project.FilesQueued} queued, {project.FilesSkipped} skipped");
                foreach (var error in result.Run?.Errors ?? new List<string>())
                {
                    Console.WriteLine($"  warning: {error}");
                }
            }
            else
            {
                Console.WriteLine($"removed: {projectId.Value:N}");
            }

            return result.Ok ? 0 : 1;
        }

        Console.Error.WriteLine("usage: grabber list|add|run|remove");
        return 2;
    }

    private static SiteGrabberRequest ParseGrabberRequest(string[] args, string startUrl)
    {
        var request = new SiteGrabberRequest
        {
            StartUrl = startUrl,
            DestinationDirectory = GetOption(args, "--dest"),
            MaxDepth = int.TryParse(GetOption(args, "--depth"), out var depth) ? depth : 2,
            MaxPages = int.TryParse(GetOption(args, "--max-pages"), out var pages) ? pages : 50,
            SameHostOnly = !args.Contains("--any-host", StringComparer.OrdinalIgnoreCase),
            SavePages = !args.Contains("--no-pages", StringComparer.OrdinalIgnoreCase),
            RewriteLinks = !args.Contains("--no-rewrite", StringComparer.OrdinalIgnoreCase),
            StartMode = GetOption(args, "--start") ?? "now",
            IncludePatterns = GetOptions(args, "--include").ToList(),
            ExcludePatterns = GetOptions(args, "--exclude").ToList(),
        };
        if (GetOption(args, "--extensions") is { } extensions)
        {
            request.Extensions = extensions
                .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                .ToList();
        }

        if (int.TryParse(GetOption(args, "--queue"), out var queueId))
        {
            request.QueueId = queueId;
        }

        if (int.TryParse(GetOption(args, "--connections"), out var connections))
        {
            request.Connections = connections;
        }

        return request;
    }

    private static async Task<Guid?> ResolveGrabberProjectIdAsync(CliClient client, string selector)
    {
        var reply = await client.RequestAsync(MessageTypes.GrabberProjectsQuery, new { })
            .ConfigureAwait(false);
        var projects = reply.GetPayload<GrabberProjectsResult>().Projects;
        var normalized = selector.Replace("-", string.Empty, StringComparison.Ordinal);
        var matches = projects
            .Where(project => project.Id.ToString("N").StartsWith(normalized, StringComparison.OrdinalIgnoreCase))
            .ToList();
        if (matches.Count != 1)
        {
            Console.Error.WriteLine($"grabber '{selector}' did not match exactly one project.");
            return null;
        }

        return matches[0].Id;
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
            Console.Error.WriteLine("usage: add-batch <pattern> [--dest DIR] [--start now|later] [--connections N] [--queue ID] [--wait] [--yes]");
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

        var results = new List<(string Url, AddJobResponse Result)>();
        foreach (var url in urls)
        {
            try
            {
                var reply = await client.RequestAsync(MessageTypes.JobAdd, new AddJobRequest
                {
                    Url = url,
                    DestinationDirectory = destination,
                    StartMode = startMode,
                    ConflictPolicy = conflict ?? "rename",
                    Source = "batch",
                    Connections = connections,
                    QueueId = queueId.GetValueOrDefault(-1),
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

        if (!optionArgs[0].Equals("set", StringComparison.OrdinalIgnoreCase))
        {
            Console.Error.WriteLine("usage: settings [get|set key value]");
            return 2;
        }

        var snapshot = await client.SnapshotAsync().ConfigureAwait(false);
        var settings = snapshot.Settings;
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
            Console.WriteLine("Download Manager diagnostics");
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
            Console.WriteLine($"  {job.Id:N}  {job.Url}");
        }
    }

    private static void PrintJobDetails(JobDto job)
    {
        Console.WriteLine($"id:       {job.Id:N}");
        Console.WriteLine($"file:     {job.FinalFilePath}");
        Console.WriteLine($"url:      {job.Url}");
        Console.WriteLine($"state:    {job.State}");
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
        Console.WriteLine($"max concurrent:    {settings.MaxConcurrentDownloads}");
        Console.WriteLine($"default conns:     {settings.DefaultConnections}");
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
        Console.WriteLine($"auto intercept:    {settings.BrowserAutoIntercept}");
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
              dm add <url> [--dest DIR] [--name NAME] [--start now|later] [--wait]
                          [--connections N] [--header "Name: value"] [--sha256 HEX]
                          [--conflict rename|overwrite|fail]
              dm add-batch <pattern> [--dest DIR] [--start now|later] [--wait] [--yes]
                          [--connections N] [--queue ID] [--conflict rename|overwrite|fail]
              dm list [--json]
              dm status <job-id> [--json]
              dm describe <job-id> <description text>
              dm grab <start-url> [--dest DIR] [--depth N] [--max-pages N]
                          [--extensions zip,mp3] [--include GLOB] [--exclude GLOB]
                          [--no-pages] [--no-rewrite] [--start now|later]
              dm grabber list|add|run|remove
              dm grabber add <start-url> [--name NAME] [--dest DIR] [grabber options]
              dm export <file.json>
              dm import <file.json> [--start now|later] [--yes]
              dm pause|resume|retry <job-id>
              dm refresh <job-id> <new-url>
              dm cancel|remove <job-id> [--keep-file]
              dm pause-all | resume-all
              dm queue list|add|start|stop|max|move|quota
              dm schedule list|add|remove|enable|disable
                          [--grabber PROJECT-ID] [--open] [--exit]
                          [--shutdown shutdown|restart|logoff] [--force]
              dm site list|add|remove
              dm settings [get] | settings set <key> <value>
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
    private readonly IpcClient _client;

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
            Process.Start(new ProcessStartInfo
            {
                FileName = host,
                Arguments = "--background",
                UseShellExecute = true,
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
            catch (Exception)
            {
                // retry
            }
        }

        throw new HostUnavailableException("The background host did not become ready within 15 seconds.");
    }

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

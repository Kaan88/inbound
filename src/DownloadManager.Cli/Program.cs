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
            case "add":
            {
                if (optionArgs.Length == 0)
                {
                    Console.Error.WriteLine("usage: add <url> [--dest DIR] [--name NAME] [--start now|later] [--wait]");
                    return 2;
                }

                var request = new AddJobRequest
                {
                    Url = optionArgs[0],
                    DestinationDirectory = GetOption(args, "--dest"),
                    FileName = GetOption(args, "--name"),
                    StartMode = GetOption(args, "--start") ?? "now",
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

            case "pause":
                return await SimpleJobActionAsync(client, optionArgs, MessageTypes.JobPause, json).ConfigureAwait(false);
            case "resume":
            case "retry":
                return await SimpleJobActionAsync(client, optionArgs, MessageTypes.JobResume, json).ConfigureAwait(false);
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
        Console.WriteLine($"max concurrent:    {settings.MaxConcurrentDownloads}");
        Console.WriteLine($"default conns:     {settings.DefaultConnections}");
        Console.WriteLine($"global speed limit:{settings.GlobalSpeedLimitBytesPerSecond?.ToString() ?? "none"}");
        Console.WriteLine($"launch at sign-in: {settings.LaunchAtStartup}");
        Console.WriteLine($"clipboard monitor: {settings.ClipboardMonitoring}");
        Console.WriteLine($"browser integration: {settings.BrowserIntegrationEnabled}");
        Console.WriteLine($"auto intercept:    {settings.BrowserAutoIntercept}");
        Console.WriteLine($"auto-resume:       {settings.AutoResumeUnfinishedOnStartup}");
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
              dm list [--json]
              dm status <job-id> [--json]
              dm pause|resume|retry <job-id>
              dm cancel|remove <job-id> [--keep-file]
              dm pause-all | resume-all
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

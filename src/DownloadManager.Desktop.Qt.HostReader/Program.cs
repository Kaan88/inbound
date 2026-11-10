using System.Text.Json;
using DownloadManager.Contracts;

// Stdout is a private, display stream to the Qt window. Only send fields it displays.
try
{
    if (args.Length != 0)
        return await RunCommand(args);

    using var handshake = new CancellationTokenSource(TimeSpan.FromSeconds(5));
    await using var client = await IpcClient.ConnectAsync(cancellationToken: handshake.Token);
    var hello = await client.ReadAsync(handshake.Token);
    if (hello is not { Type: MessageTypes.ServerHello, V: Protocol.Version })
        return 1;

    var snapshot = await client.ReadAsync(handshake.Token);
    if (snapshot is not { Type: MessageTypes.JobsSnapshot, V: Protocol.Version })
        return 1;

    var initial = snapshot.GetPayload<SnapshotDto>();
    Console.WriteLine(JsonSerializer.Serialize(new
    {
        type = "snapshot",
        torboxAvailable = hello.GetPayload<HostHelloDto>().Capabilities.Contains("remote"),
        jobs = initial.Jobs.Select(Row),
        queues = QueueRows(initial.Settings),
        defaultCompletionDialog = initial.Settings.ShowCompletionDialog
    }));

    while (await client.ReadAsync() is { } message)
    {
        if (message.V != Protocol.Version) return 1;
        if (message.Type != MessageTypes.JobEvent) continue;
        var change = message.GetPayload<JobEventDto>();
        Console.WriteLine(JsonSerializer.Serialize(new
        {
            type = change.EventType == "removed" ? "remove" : "update",
            job = Row(change.Job)
        }));
    }
    return 0;
}
catch (Exception ex) when (ex is IOException or TimeoutException or UnauthorizedAccessException or
    OperationCanceledException or JsonException or System.Net.Sockets.SocketException)
{
    return 1;
}

static object Row(JobDto job) => new
{
    id = job.Id, name = job.FileName, state = job.State, size = job.TotalBytes, rate = job.BytesPerSecond,
    showCompletionDialog = job.ShowCompletionDialog,
    completedFile = job.State == "completed" && !job.FileRemovedAfterExtraction
        ? job.FinalFilePath : null,
    qtOwned = job.Source == "qt",
    completedAt = job.CompletedAt,
    received = job.ReceivedBytes, connections = job.ActiveConnections,
    queue = job.QueueName, description = job.Description, startedAt = job.StartedAt,
    remainingSeconds = job.EstimatedRemaining?.TotalSeconds, progress = job.Progress,
    canPause = job.CanPause, canCancel = job.CanCancel, canChangeSpeedLimit = job.CanChangeSpeedLimit, canResume = job.CanResume, queueId = job.QueueId, speedLimit = job.SpeedLimitBytesPerSecond ?? 0, terminal = job.IsTerminal, priority = JobPriorities.Name(job.Priority).ToLowerInvariant()
};

static IEnumerable<object> QueueRows(SettingsDto settings) =>
    settings.Queues.OrderBy(queue => queue.Order)
        .Select(queue => new { id = queue.Id, name = queue.Name, running = queue.IsRunning, concurrency = queue.MaxConcurrentDownloads,
            isDefault = queue.Id == settings.DefaultQueueId,
            quotaEnabled = queue.QuotaEnabled, quotaBytes = queue.QuotaBytes,
            quotaHours = queue.QuotaWindowHours, quotaUsed = queue.QuotaBytesUsed });

static async Task<int> RunCommand(string[] args)
{
    if (args is ["options-get"] or ["options-save"])
        return await RunOptions(args[0]);
    if (args is [var providerCommand] && providerCommand.StartsWith("torbox-", StringComparison.Ordinal))
        return await RunTorBox(providerCommand);

    if (args is ["move-to-queue"] or ["set-priority"])
        return await UpdateSelectedDownloads(args[0]);

    if (args is ["download-telemetry", var jobId] && Guid.TryParse(jobId, out var telemetryId))
    {
        var payload = new JobActionRequest { JobId = telemetryId };
        var segments = ServerConnector.SendOnceAsync(ServerTarget.Local,
            IpcMessage.Create(MessageTypes.JobSegments, payload), TimeSpan.FromSeconds(5));
        var connections = ServerConnector.SendOnceAsync(ServerTarget.Local,
            IpcMessage.Create(MessageTypes.JobConnections, payload), TimeSpan.FromSeconds(5));
        var speed = ServerConnector.SendOnceAsync(ServerTarget.Local,
            IpcMessage.Create(MessageTypes.SpeedHistoryGet, new SpeedHistoryRequest { JobId = telemetryId }),
            TimeSpan.FromSeconds(5));
        await Task.WhenAll(segments, connections, speed);

        var segmentReply = await segments;
        var connectionReply = await connections;
        var speedReply = await speed;
        var history = speedReply is { Type: MessageTypes.SpeedHistoryResult, V: Protocol.Version }
            ? speedReply.GetPayload<SpeedHistoryDto>() : null;
        if (history?.JobId != telemetryId) history = null;
        var interval = Math.Max(1, history?.IntervalSeconds ?? 1);
        var points = history?.BytesPerSecond.TakeLast(60 / interval + 1).ToArray();
        Console.WriteLine(JsonSerializer.Serialize(new
        {
            history = points is null ? null : new { interval, points },
            segments = segmentReply is { Type: MessageTypes.JobSegmentsResult, V: Protocol.Version }
                ? segmentReply.GetPayload<List<SegmentDto>>() : null,
            connections = connectionReply is { Type: MessageTypes.JobConnectionsResult, V: Protocol.Version }
                ? connectionReply.GetPayload<List<ConnectionTelemetryDto>>()
                    .Select(c => new { c.State, c.RangeStart, c.RangeEnd, c.ReceivedBytes, c.BytesPerSecond })
                : null
        }, IpcJson.Options));
        return 0;
    }

    IpcMessage request;
    if (args is ["add"])
    {
        // URLs may contain tokens. Receive them over stdin, never through process arguments.
        var url = await Console.In.ReadLineAsync();
        if (!Uri.TryCreate(url, UriKind.Absolute, out var parsed) ||
            parsed.Scheme is not ("http" or "https")) return 1;
        request = IpcMessage.Create(MessageTypes.JobAdd, new AddJobRequest { Url = url!, Source = "qt" });
    }
    else if (args is ["edit-description"] or ["edit-speed-limit"] or ["edit-completion-dialog"])
    {
        var line = await Console.In.ReadLineAsync();
        if (line is null) return 1;
        var edit = JsonSerializer.Deserialize<JobUpdateRequest>(line, IpcJson.Options);
        if (edit is null || edit.JobId == Guid.Empty) return 1;
        var change = new JobUpdateRequest { JobId = edit.JobId };
        if (args[0] == "edit-description" && edit.Description is not null)
            change.Description = edit.Description;
        else if (args[0] == "edit-speed-limit" && edit.SpeedLimitBytesPerSecond is >= 0)
            change.SpeedLimitBytesPerSecond = edit.SpeedLimitBytesPerSecond;
        else if (args[0] == "edit-completion-dialog" && edit.ShowCompletionDialog is not null)
            change.ShowCompletionDialog = edit.ShowCompletionDialog;
        else return 1;
        request = IpcMessage.Create(MessageTypes.JobUpdate, change);
    }
    else if (args is ["delete-completed"])
    {
        var line = await Console.In.ReadLineAsync();
        if (line is null) return 1;
        var ids = JsonSerializer.Deserialize<List<Guid>>(line);
        if (ids is not { Count: > 0 } || ids.Contains(Guid.Empty)) return 1;

        var snapshot = await ServerConnector.SendOnceAsync(ServerTarget.Local,
            IpcMessage.Create(MessageTypes.JobsSnapshot), TimeSpan.FromSeconds(8));
        if (snapshot is not { Type: MessageTypes.JobsSnapshot, V: Protocol.Version }) return 1;
        var confirmed = ids.ToHashSet();
        var completed = snapshot.GetPayload<SnapshotDto>().Jobs
            .Where(job => job.State == "completed" && confirmed.Contains(job.Id))
            .Select(job => job.Id).ToList();
        if (completed.Count == 0) return 0;
        request = IpcMessage.Create(MessageTypes.JobRemove,
            new JobActionRequest { JobIds = completed, DeleteFile = false });
    }
    else if (args is ["stop-all"])
        request = IpcMessage.Create(MessageTypes.JobsPauseAll, new { });
    else if (args is ["edit-queue"])
    {
        var line = await Console.In.ReadLineAsync();
        if (line is null) return 1;
        var edit = JsonSerializer.Deserialize<QueueActionRequest>(line, IpcJson.Options);
        if (edit is null || edit.Action is not ("create" or "policy" or "moveup" or "movedown" or "delete"))
            return 1;
        request = IpcMessage.Create(MessageTypes.QueueAction, new QueueActionRequest
        {
            Action = edit.Action, QueueId = edit.QueueId, Name = edit.Name,
            MaxConcurrentDownloads = edit.MaxConcurrentDownloads, QuotaEnabled = edit.QuotaEnabled,
            QuotaBytes = edit.QuotaBytes, QuotaWindowHours = edit.QuotaWindowHours,
            MoveContentsToQueueId = edit.Action == "delete" ? edit.MoveContentsToQueueId : null
        });
    }
    else if (args.Length == 2 && args[0] is ("queue-start" or "queue-stop") &&
             int.TryParse(args[1], out var queueId))
        request = IpcMessage.Create(MessageTypes.QueueAction, new QueueActionRequest
        {
            QueueId = queueId,
            Action = args[0] == "queue-start" ? "start" : "stop"
        });
    else if (args.Length == 2 && Guid.TryParse(args[1], out var id))
    {
        var type = args[0] switch
        {
            "stop" => MessageTypes.JobPause,
            "cancel-download" => MessageTypes.JobCancel,
            "resume" => MessageTypes.JobResume,
            "remove" => MessageTypes.JobRemove,
            _ => null
        };
        if (type is null) return 1;
        var cancel = type == MessageTypes.JobCancel;
        request = IpcMessage.Create(type, new JobActionRequest
        {
            JobId = id, DeleteFile = cancel, OnlyIfUnfinished = cancel
        });
    }
    else return 1;

    var reply = await ServerConnector.SendOnceAsync(ServerTarget.Local, request, TimeSpan.FromSeconds(8));
    var accepted = reply.V == Protocol.Version &&
           reply.Payload is { ValueKind: JsonValueKind.Object } body &&
           body.TryGetProperty(request.Type == MessageTypes.JobAdd ? "accepted" : "ok", out var ok) && ok.ValueKind == JsonValueKind.True;
    if (reply is { Type: MessageTypes.SettingsResult, V: Protocol.Version } &&
        request.Type == MessageTypes.QueueAction)
    {
        var result = reply.GetPayload<SettingsResult>();
        Console.WriteLine(JsonSerializer.Serialize(new { queues = QueueRows(result.Settings), error = result.Error }));
    }
    return accepted ? 0 : 1;
}

static async Task<int> UpdateSelectedDownloads(string command)
{
    var line = await Console.In.ReadLineAsync();
    if (line is null) return 1;
    var batch = JsonSerializer.Deserialize<DownloadBatch>(line, IpcJson.Options);
    if (batch?.JobIds is not { Count: > 0 } || batch.JobIds.Contains(Guid.Empty)) return 1;

    int priority = JobPriorities.Normal;
    if (command == "set-priority" && !JobPriorities.TryParse(batch.Priority, out priority))
        return 1;

    int confirmed = 0;
    var ids = batch.JobIds.Distinct().ToList();
    foreach (var id in ids)
    {
        try
        {
            var request = command == "set-priority"
                ? IpcMessage.Create(MessageTypes.JobUpdate, new JobUpdateRequest { JobId = id, Priority = priority })
                : IpcMessage.Create(MessageTypes.JobMoveToQueue, new JobQueueRequest
                {
                    JobId = id, QueueId = batch.QueueId.GetValueOrDefault(), Detach = batch.QueueId is null
                });
            var reply = await ServerConnector.SendOnceAsync(ServerTarget.Local, request, TimeSpan.FromSeconds(8));
            if (reply.V == Protocol.Version && reply.GetPayload<JobActionResponse>().Ok)
                confirmed++;
        }
        catch (Exception ex) when (ex is IOException or TimeoutException or UnauthorizedAccessException or
            OperationCanceledException or JsonException or System.Net.Sockets.SocketException)
        {
            break;
        }
    }

    Console.WriteLine(JsonSerializer.Serialize(new { confirmed, total = ids.Count }));
    return confirmed == ids.Count ? 0 : 1;
}

static async Task<int> RunOptions(string command)
{
    var current = await ServerConnector.SendOnceAsync(ServerTarget.Local,
        IpcMessage.Create(MessageTypes.SettingsGet), TimeSpan.FromSeconds(8));
    if (current is not { Type: MessageTypes.SettingsResult, V: Protocol.Version }) return 1;
    var result = current.GetPayload<SettingsResult>();
    if (!result.Ok) return 1;

    if (command == "options-save")
    {
        var line = await Console.In.ReadLineAsync();
        if (line is null) return 1;
        var edit = JsonSerializer.Deserialize<QtOptions>(line, IpcJson.Options);
        if (edit is null || edit.MaxConcurrentDownloads is < 1 or > 16 ||
            edit.DefaultConnections is < 1 or > 32 ||
            edit.GlobalSpeedLimitBytesPerSecond is < 0) return 1;

        var settings = result.Settings;
        if (edit.DefaultDownloadDirectory is { } directory)
        {
            if (!Path.IsPathFullyQualified(directory)) return 1;
            settings.DefaultDownloadDirectory = directory;
        }
        if (edit.MaxConcurrentDownloads is { } parallel) settings.MaxConcurrentDownloads = parallel;
        if (edit.DefaultConnections is { } connections) settings.DefaultConnections = connections;
        if (edit.GlobalSpeedLimitBytesPerSecond is { } rate) settings.GlobalSpeedLimitBytesPerSecond = rate == 0 ? null : rate;
        if (edit.NotifyOnCompletion is { } notify) settings.NotifyOnCompletion = notify;
        if (edit.ShowCompletionDialog is { } show) settings.ShowCompletionDialog = show;
        if (edit.AutoResumeUnfinishedOnStartup is { } resume) settings.AutoResumeUnfinishedOnStartup = resume;

        var reply = await ServerConnector.SendOnceAsync(ServerTarget.Local,
            IpcMessage.Create(MessageTypes.SettingsUpdate, new SettingsUpdateRequest { Settings = settings }),
            TimeSpan.FromSeconds(8));
        if (reply is not { Type: MessageTypes.SettingsResult, V: Protocol.Version }) return 1;
        result = reply.GetPayload<SettingsResult>();
    }

    var value = result.Settings;
    Console.WriteLine(JsonSerializer.Serialize(new
    {
        result.Ok, result.Error,
        options = new QtOptions(value.DefaultDownloadDirectory, value.MaxConcurrentDownloads,
            value.DefaultConnections, value.GlobalSpeedLimitBytesPerSecond ?? 0,
            value.NotifyOnCompletion, value.ShowCompletionDialog, value.AutoResumeUnfinishedOnStartup)
    }, IpcJson.Options));
    return result.Ok ? 0 : 1;
}

static async Task<int> RunTorBox(string command)
{
    var line = await Console.In.ReadLineAsync();
    if (line is null) return 1;
    using var document = JsonDocument.Parse(line);
    var input = document.RootElement;
    if (input.ValueKind != JsonValueKind.Object) return 1;
    string? Text(string name) => input.TryGetProperty(name, out var field) &&
        field.ValueKind == JsonValueKind.String ? field.GetString() : null;
    bool Flag(string name) => input.TryGetProperty(name, out var field) && field.ValueKind == JsonValueKind.True;
    int Offset() => input.TryGetProperty("offset", out var field) && field.ValueKind == JsonValueKind.Number && field.TryGetInt32(out var offset)
        ? Math.Max(0, offset) : 0;

    const string provider = "torbox";
    IpcMessage request;
    string expected;
    switch (command)
    {
        case "torbox-account":
        case "torbox-clear-key":
            request = IpcMessage.Create(command == "torbox-account" ? MessageTypes.RemoteAccountGet : MessageTypes.RemoteAccountClearKey,
                new RemoteAccountRequest { Provider = provider, Refresh = Flag("refresh") });
            expected = MessageTypes.RemoteAccountResult;
            break;
        case "torbox-set-key":
            if (Text("key")?.Trim() is not { Length: > 0 } key) return 1;
            request = IpcMessage.Create(MessageTypes.RemoteAccountSetKey,
                new RemoteSetKeyRequest { Provider = provider, Key = key });
            expected = MessageTypes.RemoteAccountResult;
            break;
        case "torbox-catalog":
        case "torbox-item":
            var kind = Text("kind");
            if (kind is not ("torrents" or "webdl" or "usenet")) return 1;
            var lookupId = command == "torbox-item" ? Text("itemId") : null;
            if (command == "torbox-item" && string.IsNullOrWhiteSpace(lookupId)) return 1;
            request = IpcMessage.Create(MessageTypes.RemoteItemGet, new RemoteItemRequest
            {
                Provider = provider, Kind = kind, ItemId = lookupId, Offset = Offset(), Fresh = Flag("fresh"), Limit = 50
            });
            expected = MessageTypes.RemoteItemResult;
            break;
        case "torbox-add":
            var link = Text("input");
            var itemKind = Text("itemKind");
            var itemId = Text("itemId");
            if (string.IsNullOrWhiteSpace(link) &&
                (itemKind is not ("torrents" or "webdl" or "usenet") || string.IsNullOrWhiteSpace(itemId))) return 1;
            List<string>? files = null;
            if (input.TryGetProperty("fileIds", out var selection))
            {
                if (selection.ValueKind != JsonValueKind.Array || string.IsNullOrWhiteSpace(itemId)) return 1;
                files = selection.EnumerateArray().Where(value => value.ValueKind == JsonValueKind.String)
                    .Select(value => value.GetString()!).Where(id => !string.IsNullOrWhiteSpace(id)).Distinct().ToList();
                if (files.Count == 0) return 1;
            }
            request = IpcMessage.Create(MessageTypes.RemoteAdd, new RemoteAddRequestDto
            {
                Provider = provider, Input = link, ItemKind = itemKind, ItemId = itemId,
                Destination = Text("destination"), Selection = files is null ? "all" : "chosen",
                FileIds = files, ApproveUncached = Flag("approveUncached")
            });
            expected = MessageTypes.RemoteAddResult;
            break;
        default:
            return 1;
    }

    var reply = await ServerConnector.SendOnceAsync(ServerTarget.Local, request, TimeSpan.FromSeconds(45));
    if (reply.V != Protocol.Version || reply.Type != expected ||
        reply.Payload is not { ValueKind: JsonValueKind.Object } body) return 1;
    Console.WriteLine(JsonSerializer.Serialize(body));
    return body.TryGetProperty("ok", out var ok) && ok.ValueKind == JsonValueKind.True ? 0 : 1;
}

sealed record QtOptions(string? DefaultDownloadDirectory, int? MaxConcurrentDownloads,
    int? DefaultConnections, long? GlobalSpeedLimitBytesPerSecond, bool? NotifyOnCompletion,
    bool? ShowCompletionDialog, bool? AutoResumeUnfinishedOnStartup);

sealed record DownloadBatch(int? QueueId, List<Guid> JobIds, string? Priority);

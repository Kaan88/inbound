using System.Buffers.Binary;
using System.IO.Pipes;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace DownloadManager.Contracts;

public static class Protocol
{
    public const int Version = 1;
    public const int MaxFrameBytes = 8 * 1024 * 1024;
    public static string HostVersion { get; } =
        typeof(Protocol).Assembly.GetName().Version?.ToString(3) ?? "unknown";
}

public static class MessageTypes
{
    public const string ServerHello = "server.hello";
    public const string ServerIncompatible = "server.incompatible";
    public const string JobsSnapshot = "jobs.snapshot";
    public const string JobEvent = "job.event";
    public const string JobAdd = "job.add";
    public const string JobAddResult = "job.addResult";
    public const string FileMetadata = "file.metadata";
    public const string FileMetadataResult = "file.metadataResult";
    public const string JobPause = "job.pause";
    public const string JobResume = "job.resume";
    public const string JobCancel = "job.cancel";
    public const string JobRetry = "job.retry";
    public const string JobRemove = "job.remove";
    public const string JobMoveToQueue = "job.moveToQueue";
    public const string JobRefreshUrl = "job.refreshUrl";
    public const string JobUpdate = "job.update";
    public const string JobConnections = "job.connections";
    public const string JobConnectionsResult = "job.connectionsResult";
    public const string JobActionResult = "job.actionResult";
    public const string HostStatusResult = "host.statusResult";
    public const string JobsPauseAll = "jobs.pauseAll";
    public const string JobsResumeAll = "jobs.resumeAll";
    public const string SettingsGet = "settings.get";
    public const string SettingsUpdate = "settings.update";
    public const string SettingsResult = "settings.result";
    public const string FolderSpace = "folders.space";
    public const string FolderSpaceResult = "folders.spaceResult";
    public const string QueueAction = "queue.action";
    public const string GroupAction = "group.action";
    public const string GroupActionResult = "group.actionResult";
    /// <summary>Host push: the current package (download group) definitions after any change.</summary>
    public const string GroupsChanged = "groups.changed";
    public const string HostStatus = "host.status";
    public const string HostShutdown = "host.shutdown";
    /// <summary>Host event: the user asked the whole app to exit (tray, menu, CLI, queue "exit when done"). Not sent for update restarts.</summary>
    public const string HostExiting = "host.exiting";
    public const string HostPing = "host.ping";
    public const string HostOpenUi = "host.openUi";
    public const string HostPickFolder = "host.pickFolder";
    public const string HostFolderPicked = "host.folderPicked";
    public const string UpdateProgress = "update.progress";
    public const string HostCleanOrphans = "host.cleanOrphans";
    public const string HostCleanOrphansResult = "host.cleanOrphansResult";
    public const string HostActionResult = "host.actionResult";
    public const string HandoffRequest = "handoff.request";
    public const string HandoffAck = "handoff.ack";
    public const string HandoffError = "handoff.error";
    public const string StatisticsGet = "statistics.get";
    public const string StatisticsResult = "statistics.result";
    public const string JobsQuery = "jobs.query";
    public const string JobSegments = "job.segments";
    public const string JobSegmentsResult = "job.segmentsResult";
    public const string UpdateCheck = "update.check";
    public const string UpdateCheckResult = "update.checkResult";
    public const string UpdateDownload = "update.download";
    public const string UpdateDownloadResult = "update.downloadResult";
    public const string UpdateInstall = "update.install";
    public const string UpdateInstallResult = "update.installResult";
    public const string UpdateDefer = "update.defer";

    // Remote provider accounts and items. Some of these wait on the network, and commands run one
    // at a time per connection, so a frontend sends those on a connection of their own.
    public const string RemoteAccountGet = "remote.account.get";
    public const string RemoteAccountSetKey = "remote.account.setKey";
    public const string RemoteAccountClearKey = "remote.account.clearKey";
    public const string RemoteAccountValidate = "remote.account.validate";
    public const string RemoteAccountResult = "remote.account.result";
    public const string RemoteClassify = "remote.classify";
    public const string RemoteClassifyResult = "remote.classifyResult";
    public const string RemoteAdd = "remote.add";
    public const string RemoteAddResult = "remote.addResult";
    public const string RemoteList = "remote.list";
    public const string RemoteListResult = "remote.listResult";
    public const string RemoteItemGet = "remote.item.get";
    public const string RemoteItemResult = "remote.item.result";
    public const string RemoteFiles = "remote.files";
    public const string RemoteFilesResult = "remote.filesResult";
    public const string RemoteSelect = "remote.select";
    public const string RemoteRetry = "remote.retry";
    public const string RemoteCheckNow = "remote.checkNow";
    public const string RemoteRemove = "remote.remove";
    public const string RemoteDeleteRemote = "remote.deleteRemote";
    public const string RemoteActionResult = "remote.actionResult";
    public const string RemoteArchivePassword = "remote.archivePassword";
    public const string RemoteContainerPreview = "remote.container.preview";
    public const string RemoteContainerPreviewResult = "remote.container.previewResult";
    public const string RemoteContainerAdd = "remote.container.add";
    public const string RemoteContainerAddResult = "remote.container.addResult";
    public const string RemoteContainerSendToJd = "remote.container.sendToJd";
    public const string RemoteContainerSendResult = "remote.container.sendResult";
    /// <summary>Host push: one remote job, or the account, changed.</summary>
    public const string RemoteChanged = "remote.changed";
}

public static class IpcJson
{
    public static readonly JsonSerializerOptions Options = Create();

    private static JsonSerializerOptions Create()
    {
        var options = new JsonSerializerOptions
        {
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
            PropertyNameCaseInsensitive = true,
            DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        };
        options.Converters.Add(new JsonStringEnumConverter(JsonNamingPolicy.CamelCase));
        return options;
    }
}

public sealed record IpcMessage(
    int V,
    string Type,
    string Id,
    string? ReplyTo,
    JsonElement? Payload)
{
    public static IpcMessage Create<T>(string type, T payload, string? replyTo = null)
        => new(Protocol.Version, type, Guid.NewGuid().ToString("N"), replyTo,
            JsonSerializer.SerializeToElement(payload, IpcJson.Options));

    public static IpcMessage Create(string type, string? replyTo = null)
        => new(Protocol.Version, type, Guid.NewGuid().ToString("N"), replyTo, null);

    public T GetPayload<T>()
    {
        if (Payload is null)
        {
            throw new InvalidDataException($"Message '{Type}' has no payload.");
        }

        return Payload.Value.Deserialize<T>(IpcJson.Options)
               ?? throw new InvalidDataException($"Message '{Type}' payload could not be deserialized.");
    }
}

public static class IpcFraming
{
    public static async Task WriteAsync(Stream stream, IpcMessage message, CancellationToken cancellationToken = default)
    {
        var json = JsonSerializer.SerializeToUtf8Bytes(message, IpcJson.Options);
        if (json.Length > Protocol.MaxFrameBytes)
        {
            throw new InvalidDataException($"IPC frame is too large ({json.Length} bytes).");
        }

        var header = new byte[4];
        BinaryPrimitives.WriteInt32LittleEndian(header, json.Length);
        await stream.WriteAsync(header, cancellationToken).ConfigureAwait(false);
        await stream.WriteAsync(json, cancellationToken).ConfigureAwait(false);
        await stream.FlushAsync(cancellationToken).ConfigureAwait(false);
    }

    public static async Task<IpcMessage?> ReadAsync(Stream stream, CancellationToken cancellationToken = default)
    {
        var header = new byte[4];
        var read = await ReadExactlyAsync(stream, header, allowEof: true, cancellationToken).ConfigureAwait(false);
        if (!read)
        {
            return null;
        }

        var length = BinaryPrimitives.ReadInt32LittleEndian(header);
        if (length <= 0 || length > Protocol.MaxFrameBytes)
        {
            throw new InvalidDataException($"Invalid IPC frame length {length}.");
        }

        var payload = new byte[length];
        await ReadExactlyAsync(stream, payload, allowEof: false, cancellationToken).ConfigureAwait(false);
        return JsonSerializer.Deserialize<IpcMessage>(payload, IpcJson.Options)
               ?? throw new InvalidDataException("IPC frame was empty.");
    }

    private static async Task<bool> ReadExactlyAsync(
        Stream stream,
        byte[] buffer,
        bool allowEof,
        CancellationToken cancellationToken)
    {
        var offset = 0;
        while (offset < buffer.Length)
        {
            var read = await stream.ReadAsync(buffer.AsMemory(offset), cancellationToken).ConfigureAwait(false);
            if (read == 0)
            {
                if (allowEof && offset == 0)
                {
                    return false;
                }

                throw new EndOfStreamException("The IPC stream closed in the middle of a frame.");
            }

            offset += read;
        }

        return true;
    }
}

public static class PipeNames
{
    public static string CurrentUserKey()
    {
        var identity = $"{Environment.UserDomainName}\\{Environment.UserName}";
        var instance = Environment.GetEnvironmentVariable("DOWNLOADMANAGER_INSTANCE");
        if (!string.IsNullOrWhiteSpace(instance) && !string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("DOWNLOADMANAGER_DATA")))
            identity += "\nInstance:" + instance.Trim();
        var hash = SHA256.HashData(Encoding.UTF8.GetBytes(identity));
        return Convert.ToHexString(hash.AsSpan(0, 8)).ToLowerInvariant();
    }

    public static string HostPipe() => $"DownloadManager-{CurrentUserKey()}";

    public static string MutexName() => $"Local\\DownloadManager.Host.{CurrentUserKey()}";
}

public sealed class IpcClient : IAsyncDisposable
{
    private readonly NamedPipeClientStream _pipe;

    private IpcClient(NamedPipeClientStream pipe)
    {
        _pipe = pipe;
    }

    public bool IsConnected => _pipe.IsConnected;

    public static async Task<IpcClient> ConnectAsync(
        TimeSpan? timeout = null,
        CancellationToken cancellationToken = default)
    {
        var pipe = new NamedPipeClientStream(
            ".",
            PipeNames.HostPipe(),
            PipeDirection.InOut,
            PipeOptions.Asynchronous | PipeOptions.WriteThrough);
        try
        {
            await pipe.ConnectAsync((int)(timeout ?? TimeSpan.FromSeconds(5)).TotalMilliseconds, cancellationToken)
                .ConfigureAwait(false);
            return new IpcClient(pipe);
        }
        catch
        {
            await pipe.DisposeAsync().ConfigureAwait(false);
            throw;
        }
    }

    public Task SendAsync(IpcMessage message, CancellationToken cancellationToken = default)
        => IpcFraming.WriteAsync(_pipe, message, cancellationToken);

    public Task<IpcMessage?> ReadAsync(CancellationToken cancellationToken = default)
        => IpcFraming.ReadAsync(_pipe, cancellationToken);

    public async ValueTask DisposeAsync()
    {
        await _pipe.DisposeAsync().ConfigureAwait(false);
    }
}

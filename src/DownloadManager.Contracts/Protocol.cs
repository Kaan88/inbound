using System.Buffers.Binary;
using System.IO.Pipes;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Security.Cryptography;
using System.Security.Principal;
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
    /// <summary>
    /// PERF.7: the first frame on the Host's hello pipe (<see cref="PipeNames.HostHelloPipe"/>), sent by the client before the
    /// Host says anything, with a <see cref="ClientHelloDto"/>. Never sent on the main pipe, where the Host speaks first.
    /// </summary>
    public const string ClientHello = "client.hello";
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
    /// <summary>
    /// EXT.R11: shows one download's file in the file manager of the Host's machine (Explorer with the file selected), or
    /// its folder when the file is gone. A <see cref="JobActionRequest"/> with <c>jobId</c>; answered with
    /// <see cref="HostActionResult"/>. Only where the platform can (<c>SupportsReveal</c>), never for a web session.
    /// </summary>
    public const string JobReveal = "job.reveal";
    /// <summary>
    /// EXT.R11: adds one download again from its address, into its folder under a new name when the file is there (the
    /// conflict policy is always rename), as the desktop's Redownload does. A <see cref="JobActionRequest"/> with
    /// <c>jobId</c>; answered with <see cref="JobActionResult"/> holding the new download.
    /// </summary>
    public const string JobRedownload = "job.redownload";
    public const string JobConnections = "job.connections";
    public const string JobConnectionsResult = "job.connectionsResult";
    public const string JobActionResult = "job.actionResult";
    public const string HostStatusResult = "host.statusResult";
    public const string JobsPauseAll = "jobs.pauseAll";
    public const string JobsResumeAll = "jobs.resumeAll";
    public const string SettingsGet = "settings.get";
    public const string SettingsUpdate = "settings.update";
    public const string SettingsResult = "settings.result";
    /// <summary>Only the default download folder and the favorite folders (<see cref="DownloadFoldersResult"/>).</summary>
    public const string SettingsDownloadFolders = "settings.downloadFolders";
    public const string SettingsDownloadFoldersResult = "settings.downloadFoldersResult";
    public const string FolderSpace = "folders.space";
    public const string FolderSpaceResult = "folders.spaceResult";
    /// <summary>Lists folders under the server's allowed roots. Only where the hello offers <c>folders</c>.</summary>
    public const string FoldersBrowse = "folders.browse";
    public const string FoldersBrowseResult = "folders.browseResult";
    /// <summary>Creates a folder under the server's allowed roots; answered with <see cref="FoldersBrowseResult"/> for the new folder.</summary>
    public const string FoldersCreate = "folders.create";
    public const string QueueAction = "queue.action";
    public const string GroupAction = "group.action";
    public const string GroupActionResult = "group.actionResult";
    /// <summary>Host push: the current package (download group) definitions after any change.</summary>
    public const string GroupsChanged = "groups.changed";
    /// <summary>Host push: the whole current settings (<c>SettingsDto</c>) after any change, as a session is shown them.</summary>
    public const string SettingsChanged = "settings.changed";
    public const string HostStatus = "host.status";
    public const string HostShutdown = "host.shutdown";
    /// <summary>Host event: the user asked the whole app to exit (tray, menu, CLI, queue "exit when done"). Not sent for update restarts.</summary>
    public const string HostExiting = "host.exiting";
    public const string HostPing = "host.ping";
    public const string HostOpenUi = "host.openUi";
    public const string HostPickFolder = "host.pickFolder";
    public const string HostFolderPicked = "host.folderPicked";
    public const string UpdateProgress = "update.progress";
    /// <summary>
    /// UI.R37, Host push: the newest version its update check found changed (<see cref="UpdateLatestDto"/>). Sent only
    /// when a check finds a different one; a client that doesn't know it ignores it.
    /// </summary>
    public const string UpdateLatest = "update.latest";
    /// <summary>
    /// UI.R41, Host push to the sessions at this PC: a browser's extension reached this Host for the first time
    /// (<see cref="ExtensionsSeenDto"/>, the whole list), so an open window can drop its extension hint at once. Never
    /// sent to a web session or by a Host that doesn't keep count; a client that doesn't know it ignores it.
    /// </summary>
    public const string ExtensionsSeen = "extensions.seen";
    public const string HostCleanOrphans = "host.cleanOrphans";
    public const string HostCleanOrphansResult = "host.cleanOrphansResult";
    public const string HostActionResult = "host.actionResult";
    public const string HandoffRequest = "handoff.request";
    public const string HandoffAck = "handoff.ack";
    public const string HandoffError = "handoff.error";
    public const string StatisticsGet = "statistics.get";
    public const string StatisticsResult = "statistics.result";
    /// <summary>UI.R24: the last hour of total download speed (<see cref="SpeedHistoryDto"/>). A Host from before it answers with an error.</summary>
    public const string SpeedHistoryGet = "speed.history";
    public const string SpeedHistoryResult = "speed.historyResult";
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

    /// <summary>
    /// UPD.N1: <c>{}</c>; answered with <see cref="ChangelogResult"/>, this Host's changelog (<see cref="ChangelogDto"/>),
    /// for a web UI's "What's new". A Host from before it answers with an error.
    /// </summary>
    public const string ChangelogGet = "changelog.get";
    public const string ChangelogResult = "changelog.result";

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
    /// <summary>Starts a job's entry waiting in the provider's queue (TB.S1). Payload <c>RemoteJobIdRequest</c>.</summary>
    public const string RemoteStartQueued = "remote.startQueued";
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
    public const string RemoteHosters = "remote.hosters";
    public const string RemoteHostersResult = "remote.hostersResult";
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
    public static Task WriteAsync(Stream stream, IpcMessage message, CancellationToken cancellationToken = default)
        => WriteFrameAsync(stream, JsonSerializer.SerializeToUtf8Bytes(message, IpcJson.Options), cancellationToken);

    /// <summary>One frame of a message already serialised with <see cref="IpcJson.Options"/>.</summary>
    public static async Task WriteFrameAsync(Stream stream, byte[] json, CancellationToken cancellationToken = default)
    {
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

    /// <summary>
    /// PERF.7: the Host's second pipe, same ACL, where the client speaks first with <c>client.hello</c> and so can ask for
    /// no snapshot or no events. The main pipe is unchanged, so a client that doesn't know this one never sees it.
    /// </summary>
    public static string HostHelloPipe() => $"{HostPipe()}-hello";

    public static string MutexName() => $"Local\\DownloadManager.Host.{CurrentUserKey()}";
}

/// <summary>
/// The client's half of "only this user's Host": the Host's pipe ACL lets only this user in, and this checks that the pipe
/// a client opened is this user's, so another Windows account (or a program of it) that made a pipe of the same name
/// first can't answer in the Host's place. It is <see cref="PipeOptions.CurrentUserOnly"/>'s check plus one case:
/// that option compares the pipe's owner with the token's default owner only, which in an elevated process of an
/// administrator is the Administrators group, so a client in an administrator terminal (the CLI, say) would be refused
/// by its own user's Host. This user's own SID is accepted too. The Host sets its pipes' owner to the user itself, also
/// when it runs elevated, so a client that isn't elevated reaches an elevated Host; and the Host checks the same owner on
/// the pipe it serves (<see cref="VerifyServer"/>), so it never serves an instance of a pipe another account made first.
/// </summary>
public static class PipeOwner
{
    /// <summary>Whether a pipe with this owner may be this user's Host.</summary>
    /// <param name="user">This user's SID (<see cref="WindowsIdentity.User"/>).</param>
    /// <param name="defaultOwner">The token's default owner (<see cref="WindowsIdentity.Owner"/>): the user, or the
    /// Administrators group in an elevated process.</param>
    public static bool IsTrusted(SecurityIdentifier? owner, SecurityIdentifier? user, SecurityIdentifier? defaultOwner)
        => owner is not null && (owner == user || owner == defaultOwner);

    /// <summary>Throws <see cref="UnauthorizedAccessException"/> unless the connected pipe's owner is <see cref="IsTrusted"/>.</summary>
    [SupportedOSPlatform("windows")]
    public static void Verify(NamedPipeClientStream pipe)
    {
        var owner = pipe.GetAccessControl().GetOwner(typeof(SecurityIdentifier)) as SecurityIdentifier;
        using var current = WindowsIdentity.GetCurrent();
        if (!IsTrusted(owner, current.User, current.Owner))
            throw new UnauthorizedAccessException("The app's pipe belongs to another user.");
    }

    /// <summary>
    /// Whether a pipe the Host is about to serve is its own. A new instance of a pipe that already exists takes that pipe's
    /// security, not the Host's, so an instance of a pipe another account made first (with an owner of its own and access
    /// for itself) would let that account in. The Host makes its pipes with this user as the owner, so only that counts.
    /// </summary>
    public static bool IsHostsOwn(SecurityIdentifier? owner, SecurityIdentifier? user)
        => owner is not null && user is not null && owner == user;

    /// <summary>Throws <see cref="UnauthorizedAccessException"/> unless a pipe instance the Host made is <see cref="IsHostsOwn"/>.</summary>
    [SupportedOSPlatform("windows")]
    public static void VerifyServer(NamedPipeServerStream pipe)
    {
        var owner = pipe.GetAccessControl().GetOwner(typeof(SecurityIdentifier)) as SecurityIdentifier;
        using var current = WindowsIdentity.GetCurrent();
        if (!IsHostsOwn(owner, current.User))
            throw new UnauthorizedAccessException("Another account has made a pipe with the app's name first.");
    }
}

public sealed class IpcClient : IIpcConnection
{
    private readonly Stream _pipe;

    private IpcClient(Stream pipe)
    {
        _pipe = pipe;
    }

    public bool IsConnected => _pipe switch
    {
        PipeStream pipe => pipe.IsConnected,
        System.Net.Sockets.NetworkStream socket => socket.Socket.Connected,
        _ => false,
    };

    /// <summary>
    /// Whether <c>jobs.snapshot</c> follows <c>server.hello</c> on this connection. True on the main pipe, and on the hello
    /// pipe when the client asked for it; false when it asked for none.
    /// </summary>
    public bool SnapshotFollowsHello { get; private init; } = true;

    public static async Task<IpcClient> ConnectAsync(
        TimeSpan? timeout = null,
        CancellationToken cancellationToken = default)
        => new(await OpenAsync(PipeNames.HostPipe(), timeout, cancellationToken).ConfigureAwait(false));

    /// <summary>
    /// PERF.7: connects to the hello pipe and sends <paramref name="hello"/> first, so the Host leaves out what the client
    /// doesn't want. Read <c>server.hello</c> next as on the main pipe, then the snapshot only if
    /// <see cref="SnapshotFollowsHello"/>. A Host from before the hello pipe has only the main pipe; this then connects
    /// there, and the snapshot and events follow as they always did.
    /// </summary>
    public static async Task<IpcClient> ConnectAsync(
        ClientHelloDto hello,
        TimeSpan? timeout = null,
        CancellationToken cancellationToken = default)
    {
        if (OperatingSystem.IsWindows() && !PipeProbe.Exists(PipeNames.HostHelloPipe()) && PipeProbe.Exists(PipeNames.HostPipe()))
        {
            return await ConnectAsync(timeout, cancellationToken).ConfigureAwait(false);
        }

        var pipe = await OpenAsync(PipeNames.HostHelloPipe(), timeout, cancellationToken).ConfigureAwait(false);
        var client = new IpcClient(pipe) { SnapshotFollowsHello = hello.Snapshot };
        try
        {
            await client.SendAsync(IpcMessage.Create(MessageTypes.ClientHello, hello), cancellationToken).ConfigureAwait(false);
            return client;
        }
        catch
        {
            await client.DisposeAsync().ConfigureAwait(false);
            throw;
        }
    }

    /// <summary>A named pipe on Windows; on Linux, the Host's Unix socket of that name (<see cref="UnixSockets"/>).</summary>
    private static async Task<Stream> OpenAsync(string name, TimeSpan? timeout, CancellationToken cancellationToken)
    {
        if (OperatingSystem.IsLinux())
            return await UnixSockets.ConnectAsync(name, timeout ?? TimeSpan.FromSeconds(5), cancellationToken).ConfigureAwait(false);

        var pipe = new NamedPipeClientStream(
            ".",
            name,
            PipeDirection.InOut,
            PipeOptions.Asynchronous | PipeOptions.WriteThrough);
        try
        {
            await pipe.ConnectAsync((int)(timeout ?? TimeSpan.FromSeconds(5)).TotalMilliseconds, cancellationToken)
                .ConfigureAwait(false);
            if (OperatingSystem.IsWindows()) PipeOwner.Verify(pipe);
            return pipe;
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

/// <summary>Whether a pipe exists, without connecting to it or taking an instance. Windows only.</summary>
internal static class PipeProbe
{
    private const int FileNotFound = 2;

    [SupportedOSPlatform("windows")]
    public static bool Exists(string name)
    {
        // A busy pipe (every instance taken) times out here and still exists; only "not found" means it isn't there.
        if (WaitNamedPipe($@"\\.\pipe\{name}", 1)) return true;
        return Marshal.GetLastPInvokeError() != FileNotFound;
    }

    [DllImport("kernel32.dll", EntryPoint = "WaitNamedPipeW", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool WaitNamedPipe(string name, uint timeoutMilliseconds);
}

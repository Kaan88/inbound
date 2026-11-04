using System.Collections.Concurrent;
using System.IO.Pipes;
using System.Security.AccessControl;
using System.Security.Principal;
using System.Threading.Channels;
using DownloadManager.Contracts;
using DownloadManager.Runtime;
using DownloadManager.Tray;

namespace DownloadManager.Host;

public interface IHostController
{
    void OpenUi(string? page = null);
    void RequestShutdown();
}

public sealed class HostPipeServer
{
    private readonly DownloadManagerService _service;
    private readonly IHostController _controller;
    private readonly FileLogger _log;
    private readonly ConcurrentDictionary<Guid, ClientConnection> _clients = new();
    private readonly CancellationTokenSource _cts = new();
    private Task? _acceptLoop;

    public HostPipeServer(DownloadManagerService service, IHostController controller, FileLogger log)
    {
        _service = service;
        _controller = controller;
        _log = log;
    }

    public void Start()
    {
        _service.JobChanged += OnJobChanged;
        _acceptLoop = AcceptLoopAsync(_cts.Token);
        _log.Info($"IPC server listening on pipe '{PipeNames.HostPipe()}'.");
    }

    public async Task StopAsync()
    {
        _service.JobChanged -= OnJobChanged;
        _cts.Cancel();
        foreach (var client in _clients.Values)
        {
            client.Writer.TryComplete();
        }

        if (_acceptLoop is not null)
        {
            try
            {
                await _acceptLoop.ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                // expected
            }
        }
    }

    private async Task AcceptLoopAsync(CancellationToken cancellationToken)
    {
        while (!cancellationToken.IsCancellationRequested)
        {
            NamedPipeServerStream? pipe = null;
            try
            {
                pipe = CreatePipe();
                await pipe.WaitForConnectionAsync(cancellationToken).ConfigureAwait(false);
                var client = new ClientConnection(pipe);
                _ = HandleClientAsync(client, cancellationToken);
            }
            catch (OperationCanceledException)
            {
                pipe?.Dispose();
                break;
            }
            catch (Exception ex)
            {
                pipe?.Dispose();
                _log.Error($"Named-pipe accept failed: {ex.Message}");
                try
                {
                    await Task.Delay(500, cancellationToken).ConfigureAwait(false);
                }
                catch (OperationCanceledException)
                {
                    break;
                }
            }
        }
    }

    private static NamedPipeServerStream CreatePipe()
    {
        var security = PipeSecurityFactory.CreateForCurrentUser();

        return NamedPipeServerStreamAcl.Create(
            PipeNames.HostPipe(),
            PipeDirection.InOut,
            8,
            PipeTransmissionMode.Byte,
            PipeOptions.Asynchronous,
            64 * 1024,
            64 * 1024,
            security);
    }

    private async Task HandleClientAsync(ClientConnection client, CancellationToken serverToken)
    {
        _clients[client.Id] = client;
        var writerTask = client.WriterLoopAsync(serverToken);
        try
        {
            await client.SendDirectAsync(IpcMessage.Create(MessageTypes.ServerHello, new HostHelloDto
            {
                HostVersion = Protocol.HostVersion,
                ProtocolVersion = Protocol.Version,
                StartedAt = _service.StartedAt,
                Capabilities = new List<string> { "jobs", "settings", "handoff", "tray", "status" },
            }), serverToken).ConfigureAwait(false);

            await client.SendDirectAsync(
                IpcMessage.Create(MessageTypes.JobsSnapshot, _service.GetSnapshot()),
                serverToken).ConfigureAwait(false);

            while (!serverToken.IsCancellationRequested)
            {
                var message = await IpcFraming.ReadAsync(client.Stream, serverToken).ConfigureAwait(false);
                if (message is null)
                {
                    break;
                }

                if (message.V != Protocol.Version)
                {
                    await client.SendAsync(IpcMessage.Create(MessageTypes.ServerIncompatible, new IncompatibleDto
                    {
                        MinVersion = Protocol.Version,
                        MaxVersion = Protocol.Version,
                        Message = $"This host speaks protocol {Protocol.Version}.",
                    }, message.Id)).ConfigureAwait(false);
                    break;
                }

                await DispatchAsync(client, message, serverToken).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException)
        {
            // shutting down
        }
        catch (IOException ex)
        {
            _log.Info($"IPC client disconnected: {ex.Message}");
        }
        catch (Exception ex)
        {
            _log.Error($"IPC client failed: {ex}");
        }
        finally
        {
            _clients.TryRemove(client.Id, out _);
            client.Writer.TryComplete();
            try
            {
                await writerTask.ConfigureAwait(false);
            }
            catch (Exception)
            {
                // writer termination is best effort
            }

            await client.DisposeAsync().ConfigureAwait(false);
        }
    }

    private async Task DispatchAsync(ClientConnection client, IpcMessage message, CancellationToken cancellationToken)
    {
        try
        {
            switch (message.Type)
            {
                case MessageTypes.HostPing:
                    await Reply(client, message, MessageTypes.HostActionResult,
                        new HostActionResult { Ok = true, Message = "pong" }).ConfigureAwait(false);
                    break;

                case MessageTypes.HostStatus:
                    await Reply(client, message, MessageTypes.HostStatusResult,
                        _service.GetSnapshot().Status).ConfigureAwait(false);
                    break;

                case MessageTypes.JobsSnapshot:
                case MessageTypes.JobsQuery:
                    await Reply(client, message, MessageTypes.JobsSnapshot, _service.GetSnapshot())
                        .ConfigureAwait(false);
                    break;

                case MessageTypes.JobAdd:
                    var add = message.GetPayload<AddJobRequest>();
                    var addResult = await _service.AddJobAsync(add, cancellationToken).ConfigureAwait(false);
                    await Reply(client, message, MessageTypes.JobAddResult, addResult).ConfigureAwait(false);
                    break;

                case MessageTypes.HandoffRequest:
                    await HandleHandoffAsync(client, message, cancellationToken).ConfigureAwait(false);
                    break;

                case MessageTypes.JobPause:
                    var pause = message.GetPayload<JobActionRequest>();
                    await Reply(client, message, MessageTypes.JobActionResult,
                        await _service.PauseAsync(pause.JobId, cancellationToken).ConfigureAwait(false))
                        .ConfigureAwait(false);
                    break;

                case MessageTypes.JobResume:
                case MessageTypes.JobRetry:
                    var resume = message.GetPayload<JobActionRequest>();
                    await Reply(client, message, MessageTypes.JobActionResult,
                        await _service.ResumeAsync(resume.JobId, cancellationToken).ConfigureAwait(false))
                        .ConfigureAwait(false);
                    break;

                case MessageTypes.JobCancel:
                    var cancel = message.GetPayload<JobActionRequest>();
                    await Reply(client, message, MessageTypes.JobActionResult,
                        await _service.CancelAsync(cancel.JobId, cancel.DeleteFile, cancellationToken).ConfigureAwait(false))
                        .ConfigureAwait(false);
                    break;

                case MessageTypes.JobRemove:
                    var remove = message.GetPayload<JobActionRequest>();
                    await Reply(client, message, MessageTypes.JobActionResult,
                        await _service.RemoveAsync(remove.JobId, remove.DeleteFile, cancellationToken).ConfigureAwait(false))
                        .ConfigureAwait(false);
                    break;

                case MessageTypes.JobMoveToQueue:
                    var moveToQueue = message.GetPayload<JobQueueRequest>();
                    await Reply(client, message, MessageTypes.JobActionResult,
                        await _service.MoveToQueueAsync(moveToQueue.JobId, moveToQueue.QueueId, cancellationToken)
                            .ConfigureAwait(false))
                        .ConfigureAwait(false);
                    break;

                case MessageTypes.JobRefreshUrl:
                    var refreshUrl = message.GetPayload<JobRefreshUrlRequest>();
                    await Reply(client, message, MessageTypes.JobActionResult,
                        await _service.RefreshUrlAsync(refreshUrl.JobId, refreshUrl.Url, cancellationToken)
                            .ConfigureAwait(false))
                        .ConfigureAwait(false);
                    break;

                case MessageTypes.JobUpdate:
                    var jobUpdate = message.GetPayload<JobUpdateRequest>();
                    await Reply(client, message, MessageTypes.JobActionResult,
                        await _service.UpdateJobAsync(jobUpdate, cancellationToken)
                            .ConfigureAwait(false))
                        .ConfigureAwait(false);
                    break;

                case MessageTypes.GrabberRun:
                    var grabberRequest = message.GetPayload<SiteGrabberRequest>();
                    await Reply(client, message, MessageTypes.GrabberResult,
                        await _service.RunSiteGrabberAsync(grabberRequest, cancellationToken)
                            .ConfigureAwait(false))
                        .ConfigureAwait(false);
                    break;

                case MessageTypes.JobSegments:
                    var segmentsRequest = message.GetPayload<JobActionRequest>();
                    await Reply(client, message, MessageTypes.JobSegmentsResult,
                        await _service.GetSegmentsAsync(segmentsRequest.JobId, cancellationToken)
                            .ConfigureAwait(false))
                        .ConfigureAwait(false);
                    break;

                case MessageTypes.UpdateCheck:
                    await Reply(client, message, MessageTypes.UpdateCheckResult,
                        await _service.CheckForUpdatesAsync(cancellationToken).ConfigureAwait(false))
                        .ConfigureAwait(false);
                    break;

                case MessageTypes.UpdateDownload:
                    await Reply(client, message, MessageTypes.UpdateDownloadResult,
                        await _service.DownloadUpdateAsync(cancellationToken).ConfigureAwait(false))
                        .ConfigureAwait(false);
                    break;

                case MessageTypes.UpdateInstall:
                    var installRequest = message.GetPayload<UpdateInstallRequest>() ??
                                         new UpdateInstallRequest();
                    var installResult = await _service.InstallUpdateAsync(
                        installRequest, cancellationToken).ConfigureAwait(false);
                    await Reply(client, message, MessageTypes.UpdateInstallResult, installResult)
                        .ConfigureAwait(false);
                    if (installResult.Ok)
                    {
                        _service.RequestUpdateShutdown();
                    }

                    break;

                case MessageTypes.UpdateDefer:
                    var deferRequest = message.GetPayload<UpdateDeferRequest>();
                    await Reply(client, message, MessageTypes.SettingsResult,
                        await _service.DeferUpdateAsync(deferRequest.Version, cancellationToken)
                            .ConfigureAwait(false))
                        .ConfigureAwait(false);
                    break;

                case MessageTypes.GrabberProjectsQuery:
                    await Reply(client, message, MessageTypes.GrabberProjectsResult,
                        _service.GetGrabberProjects()).ConfigureAwait(false);
                    break;

                case MessageTypes.GrabberProjectCreate:
                    var projectCreate = message.GetPayload<GrabberProjectCreateRequest>();
                    await Reply(client, message, MessageTypes.GrabberProjectResult,
                        await _service.CreateGrabberProjectAsync(projectCreate, cancellationToken)
                            .ConfigureAwait(false))
                        .ConfigureAwait(false);
                    break;

                case MessageTypes.GrabberProjectRun:
                    var projectRun = message.GetPayload<GrabberProjectActionRequest>();
                    await Reply(client, message, MessageTypes.GrabberProjectResult,
                        await _service.RunGrabberProjectAsync(projectRun.Id, cancellationToken)
                            .ConfigureAwait(false))
                        .ConfigureAwait(false);
                    break;

                case MessageTypes.GrabberProjectRemove:
                    var projectRemove = message.GetPayload<GrabberProjectActionRequest>();
                    await Reply(client, message, MessageTypes.GrabberProjectResult,
                        await _service.RemoveGrabberProjectAsync(projectRemove.Id, cancellationToken)
                            .ConfigureAwait(false))
                        .ConfigureAwait(false);
                    break;

                case MessageTypes.JobsPauseAll:
                    await _service.PauseAllAsync(cancellationToken).ConfigureAwait(false);
                    await Reply(client, message, MessageTypes.HostActionResult,
                        new HostActionResult { Ok = true, Message = "All downloads paused." }).ConfigureAwait(false);
                    break;

                case MessageTypes.JobsResumeAll:
                    await _service.ResumeAllAsync(cancellationToken).ConfigureAwait(false);
                    await Reply(client, message, MessageTypes.HostActionResult,
                        new HostActionResult { Ok = true, Message = "All downloads resumed." }).ConfigureAwait(false);
                    break;

                case MessageTypes.SettingsGet:
                    await Reply(client, message, MessageTypes.SettingsResult,
                        new SettingsResult
                        {
                            Ok = true,
                            Settings = _service.GetSnapshot().Settings,
                        }).ConfigureAwait(false);
                    break;

                case MessageTypes.SettingsUpdate:
                    var update = message.GetPayload<SettingsUpdateRequest>();
                    await Reply(client, message, MessageTypes.SettingsResult,
                        await _service.UpdateSettingsAsync(update.Settings, cancellationToken).ConfigureAwait(false))
                        .ConfigureAwait(false);
                    break;

                case MessageTypes.QueueAction:
                    var queueAction = message.GetPayload<QueueActionRequest>();
                    await Reply(client, message, MessageTypes.SettingsResult,
                        await _service.QueueActionAsync(queueAction, cancellationToken).ConfigureAwait(false))
                        .ConfigureAwait(false);
                    break;

                case MessageTypes.HostOpenUi:
                    var open = message.GetPayload<HostActionRequest>();
                    _controller.OpenUi(string.IsNullOrWhiteSpace(open.Action) ? null : open.Action);
                    await Reply(client, message, MessageTypes.HostActionResult,
                        new HostActionResult { Ok = true, Message = "Opening the desktop UI." }).ConfigureAwait(false);
                    break;

                case MessageTypes.HostShutdown:
                    await Reply(client, message, MessageTypes.HostActionResult,
                        new HostActionResult { Ok = true, Message = "Shutting down." }).ConfigureAwait(false);
                    _controller.RequestShutdown();
                    break;

                default:
                    await Reply(client, message, MessageTypes.HostActionResult,
                        new HostActionResult { Ok = false, Message = $"Unknown message type '{message.Type}'." })
                        .ConfigureAwait(false);
                    break;
            }
        }
        catch (Exception ex)
        {
            _log.Error($"IPC command '{message.Type}' failed: {ex.Message}");
            await Reply(client, message, MessageTypes.HostActionResult,
                new HostActionResult { Ok = false, Message = ex.Message }).ConfigureAwait(false);
        }
    }

    private async Task HandleHandoffAsync(ClientConnection client, IpcMessage message, CancellationToken cancellationToken)
    {
        var handoff = message.GetPayload<HandoffRequest>();
        if (string.IsNullOrWhiteSpace(handoff.RequestId) || string.IsNullOrWhiteSpace(handoff.Url))
        {
            await Reply(client, message, MessageTypes.HandoffAck, new HandoffAck
            {
                Accepted = false,
                Error = "requestId and url are required.",
                ErrorCode = "invalid_handoff",
            }).ConfigureAwait(false);
            return;
        }

        if (!string.Equals(handoff.ExtensionId, BrowserBridge.ExtensionId, StringComparison.Ordinal))
        {
            await Reply(client, message, MessageTypes.HandoffAck, new HandoffAck
            {
                Accepted = false,
                Error = "The handoff came from an unknown extension.",
                ErrorCode = "untrusted_extension",
            }).ConfigureAwait(false);
            return;
        }

        var settings = _service.GetSnapshot().Settings;
        if (!settings.BrowserIntegrationEnabled)
        {
            await Reply(client, message, MessageTypes.HandoffAck, new HandoffAck
            {
                Accepted = false,
                Error = "Browser integration is disabled.",
                ErrorCode = "integration_disabled",
            }).ConfigureAwait(false);
            return;
        }

        var request = new AddJobRequest
        {
            Url = handoff.Url,
            FinalUrl = handoff.FinalUrl,
            DestinationDirectory = handoff.DestinationDirectory,
            FileName = handoff.SuggestedFileName,
            StartMode = handoff.StartMode,
            Source = handoff.IsAutoIntercept ? "chromeAuto" : "chromeContextMenu",
            ClientRequestId = handoff.RequestId,
            OwnerExtensionId = handoff.ExtensionId,
            Headers = handoff.Headers ?? new Dictionary<string, string>(),
            Referrer = handoff.Referrer,
            UserAgent = handoff.UserAgent,
            PageUrl = handoff.PageUrl,
            Description = handoff.MimeType,
        };

        var result = await _service.AddJobAsync(request, cancellationToken).ConfigureAwait(false);
        await Reply(client, message, MessageTypes.HandoffAck, new HandoffAck
        {
            Accepted = result.Accepted,
            Duplicate = result.Duplicate,
            JobId = result.Job?.Id ?? Guid.Empty,
            Job = result.Job,
            Error = result.Error,
            ErrorCode = result.ErrorCode,
        }).ConfigureAwait(false);
    }

    private static Task Reply<T>(ClientConnection client, IpcMessage request, string type, T payload)
        => client.SendAsync(IpcMessage.Create(type, payload, request.Id));

    private void OnJobChanged(object? sender, JobChangeEventArgs e)
    {
        var message = IpcMessage.Create(MessageTypes.JobEvent, new JobEventDto
        {
            Job = e.Job,
            EventType = e.ChangeType,
        });

        foreach (var client in _clients.Values)
        {
            client.Writer.TryWrite(message);
        }
    }

    private sealed class ClientConnection : IAsyncDisposable
    {
        private readonly NamedPipeServerStream _pipe;
        private readonly SemaphoreSlim _directWrite = new(1, 1);
        private readonly Channel<IpcMessage> _channel;

        public ClientConnection(NamedPipeServerStream pipe)
        {
            _pipe = pipe;
            _channel = Channel.CreateBounded<IpcMessage>(new BoundedChannelOptions(512)
            {
                FullMode = BoundedChannelFullMode.DropOldest,
                SingleReader = true,
                SingleWriter = false,
            });
        }

        public Guid Id { get; } = Guid.NewGuid();
        public Stream Stream => _pipe;
        public ChannelWriter<IpcMessage> Writer => _channel.Writer;

        public async Task SendDirectAsync(IpcMessage message, CancellationToken cancellationToken)
        {
            await _directWrite.WaitAsync(cancellationToken).ConfigureAwait(false);
            try
            {
                await IpcFraming.WriteAsync(_pipe, message, cancellationToken).ConfigureAwait(false);
            }
            finally
            {
                _directWrite.Release();
            }
        }

        public Task SendAsync(IpcMessage message)
        {
            Writer.TryWrite(message);
            return Task.CompletedTask;
        }

        public async Task WriterLoopAsync(CancellationToken cancellationToken)
        {
            try
            {
                await foreach (var message in _channel.Reader.ReadAllAsync(cancellationToken).ConfigureAwait(false))
                {
                    await SendDirectAsync(message, cancellationToken).ConfigureAwait(false);
                }
            }
            catch (OperationCanceledException)
            {
                // expected
            }
            catch (IOException)
            {
                // client went away
            }
        }

        public async ValueTask DisposeAsync()
        {
            try
            {
                await _pipe.DisposeAsync().ConfigureAwait(false);
            }
            catch (IOException)
            {
                // best effort
            }

            _directWrite.Dispose();
        }
    }
}

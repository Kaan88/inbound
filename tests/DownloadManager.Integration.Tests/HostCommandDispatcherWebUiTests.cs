using DownloadManager.Contracts;
using DownloadManager.Runtime.Ipc;

namespace DownloadManager.Integration.Tests;

/// <summary>D8: <c>webui.*</c> belong to the user at this PC. A web session (a browser through the bridge) is refused them all.</summary>
public sealed partial class HostCommandDispatcherTests
{
    private sealed class RecordingWebUi : IWebUiCommands
    {
        public List<string> Seen { get; } = [];

        public Task<IpcMessage> HandleAsync(IpcMessage request, CancellationToken cancellationToken)
        {
            lock (Seen) Seen.Add(request.Type);
            return Task.FromResult(request.Type == WebUiMessageTypes.OpenTicket
                ? IpcMessage.Create(WebUiMessageTypes.Ticket, new WebUiTicketDto { Url = "http://127.0.0.1:8080/api/auth/ticket?t=x" }, request.Id)
                : IpcMessage.Create(WebUiMessageTypes.Status, new WebUiStatusDto { Enabled = true }, request.Id));
        }
    }

    private static readonly (string Type, object Payload)[] WebUiRequests =
    [
        (WebUiMessageTypes.Get, new { }),
        (WebUiMessageTypes.Set, new WebUiSetRequest { Enabled = true, Port = 9090, AlsoReachableAs = ["evil"] }),
        (WebUiMessageTypes.SetPassword, new WebUiPasswordRequest { Password = "chosen by a browser" }),
        (WebUiMessageTypes.Recheck, new { }),
        (WebUiMessageTypes.OpenTicket, new { }),
    ];

    [Fact]
    public async Task TheWebUiCommandsAreRefusedForAWebSessionAndAnsweredForThePipe()
    {
        var service = await ServiceAsync();
        var profile = new Runtime.Ipc.WebSessionProfile(service, PlatformCapabilities.Desktop);
        _cleanup.Add(new Disposer(profile.Dispose));
        var webUi = new RecordingWebUi();
        var dispatcher = new HostCommandDispatcher(service, new FakePlatform(PlatformCapabilities.Desktop, _timeline), new ListLog())
        {
            WebSessions = profile.Rights,
            WebUi = webUi,
        };
        var web = Session();
        var pipe = Session();
        await dispatcher.OpenSessionAsync(web, new ClientHelloDto { Role = ClientRoles.Web }, CancellationToken.None);
        await dispatcher.OpenSessionAsync(pipe, new ClientHelloDto(), CancellationToken.None);

        foreach (var (type, payload) in WebUiRequests)
        {
            var refused = await AskAsync(dispatcher, web, type, payload);
            Assert.Equal(MessageTypes.HostActionResult, refused.Type);
            var result = refused.GetPayload<HostActionResult>();
            Assert.False(result.Ok);
            Assert.Equal("The web UI can only be set up in the app on this PC.", result.Message);
        }

        Assert.Empty(webUi.Seen);

        foreach (var (type, payload) in WebUiRequests)
        {
            var answered = await AskAsync(dispatcher, pipe, type, payload);
            Assert.Equal(type == WebUiMessageTypes.OpenTicket ? WebUiMessageTypes.Ticket : WebUiMessageTypes.Status, answered.Type);
        }

        Assert.Equal(WebUiRequests.Select(request => request.Type), webUi.Seen);
    }

    [Fact]
    public async Task WebUiChangesReachThePipeSessionsOnly()
    {
        var service = await ServiceAsync();
        var profile = new Runtime.Ipc.WebSessionProfile(service, PlatformCapabilities.Desktop);
        _cleanup.Add(new Disposer(profile.Dispose));
        var dispatcher = new HostCommandDispatcher(service, new FakePlatform(PlatformCapabilities.Desktop, _timeline), new ListLog())
        {
            WebSessions = profile.Rights,
            WebUi = new RecordingWebUi(),
        };
        var web = Session();
        var pipe = Session();
        var quiet = Session();
        await dispatcher.OpenSessionAsync(web, new ClientHelloDto { Role = ClientRoles.Web }, CancellationToken.None);
        await dispatcher.OpenSessionAsync(pipe, new ClientHelloDto(), CancellationToken.None);
        await dispatcher.OpenSessionAsync(quiet, new ClientHelloDto { Events = false }, CancellationToken.None);

        dispatcher.BroadcastWebUiChanged(new WebUiStatusDto { Enabled = true, State = WebUiStates.Running, Port = 8080 });
        await pipe.UntilAsync(frames => frames.Any(frame => frame.Type == WebUiMessageTypes.Changed));
        await Task.Delay(100);
        Assert.DoesNotContain(web.Frames, frame => frame.Type == WebUiMessageTypes.Changed);
        Assert.DoesNotContain(quiet.Frames, frame => frame.Type == WebUiMessageTypes.Changed);
        var change = pipe.Frames.Single(frame => frame.Type == WebUiMessageTypes.Changed).GetPayload<WebUiStatusDto>();
        Assert.Equal(WebUiStates.Running, change.State);
    }

    [Fact]
    public async Task AHostWithoutAWebUiSaysSo()
    {
        var service = await ServiceAsync();
        var dispatcher = Dispatcher(service);
        var session = Session();
        await dispatcher.OpenSessionAsync(session, CancellationToken.None);
        var reply = await AskAsync(dispatcher, session, WebUiMessageTypes.Get, new { });
        Assert.Equal("The web UI is not available here.", reply.GetPayload<HostActionResult>().Message);
    }
}

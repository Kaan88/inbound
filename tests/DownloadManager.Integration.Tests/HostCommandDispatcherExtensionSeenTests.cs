using DownloadManager.Contracts;
using DownloadManager.Runtime;
using DownloadManager.Runtime.Ipc;

namespace DownloadManager.Integration.Tests;

/// <summary>UI.R41: the Host keeps which browsers' extensions reached it, from the native host's hello.</summary>
public sealed partial class HostCommandDispatcherTests
{
    [Fact]
    public async Task A_native_host_hello_with_its_browser_is_kept_and_shown_in_the_status()
    {
        var service = await ServiceAsync();
        service.ExtensionSightings = new ExtensionSightings(Path.Combine(_root, "seen"));
        var dispatcher = new HostCommandDispatcher(service, new FakePlatform(PlatformCapabilities.Desktop, _timeline), new ListLog());

        var window = Session();
        await dispatcher.OpenSessionAsync(window, new ClientHelloDto(), CancellationToken.None);
        var before = (await AskAsync(dispatcher, window, MessageTypes.HostStatus, new { })).GetPayload<HostStatusDto>();
        Assert.NotNull(before.ExtensionsSeen);
        Assert.Empty(before.ExtensionsSeen!);

        await dispatcher.OpenSessionAsync(Session(), new ClientHelloDto { Snapshot = false, Browser = ExtensionBrowsers.Edge }, CancellationToken.None);
        // A name the Host doesn't know is not kept.
        await dispatcher.OpenSessionAsync(Session(), new ClientHelloDto { Snapshot = false, Browser = "netscape" }, CancellationToken.None);

        var after = (await AskAsync(dispatcher, window, MessageTypes.HostStatus, new { })).GetPayload<HostStatusDto>();
        Assert.Equal([ExtensionBrowsers.Edge], after.ExtensionsSeen!.Keys);
        Assert.True(File.Exists(Path.Combine(_root, "seen", ExtensionSightings.FileName)));
    }

    [Fact]
    public async Task A_web_session_cant_say_an_extension_came()
    {
        var web = await WebAsync();
        web.Service.ExtensionSightings = new ExtensionSightings(Path.Combine(_root, "seen"));
        await web.Dispatcher.OpenSessionAsync(Session(), new ClientHelloDto { Role = ClientRoles.Web, Browser = ExtensionBrowsers.Chrome }, CancellationToken.None);

        var status = (await AskAsync(web.Dispatcher, web.Pipe, MessageTypes.HostStatus, new { })).GetPayload<HostStatusDto>();
        Assert.Empty(status.ExtensionsSeen!);
    }

    [Fact]
    public async Task A_browser_seen_the_first_time_is_pushed_to_the_window_but_not_to_web_sessions()
    {
        // Review of UI.R41: an open window drops its hint when an extension connects, not at its next snapshot.
        var web = await WebAsync();
        web.Service.ExtensionSightings = new ExtensionSightings(Path.Combine(_root, "seen"));

        await web.Dispatcher.OpenSessionAsync(Session(), new ClientHelloDto { Snapshot = false, Events = false, Browser = ExtensionBrowsers.Chrome }, CancellationToken.None);
        await web.Pipe.UntilAsync(frames => frames.Any(f => f.Type == MessageTypes.ExtensionsSeen));
        var pushed = Assert.Single(web.Pipe.Frames, f => f.Type == MessageTypes.ExtensionsSeen).GetPayload<ExtensionsSeenDto>();
        Assert.Equal([ExtensionBrowsers.Chrome], pushed.Seen.Keys);

        // The same browser again the same day changes nothing, so nothing more is pushed.
        await web.Dispatcher.OpenSessionAsync(Session(), new ClientHelloDto { Snapshot = false, Events = false, Browser = ExtensionBrowsers.Chrome }, CancellationToken.None);
        var marker = await AskAsync(web.Dispatcher, web.Pipe, MessageTypes.HostStatus, new { });
        Assert.NotNull(marker);
        Assert.Single(web.Pipe.Frames, f => f.Type == MessageTypes.ExtensionsSeen);
        Assert.DoesNotContain(web.Session.Frames, f => f.Type == MessageTypes.ExtensionsSeen);
    }

    [Fact]
    public async Task A_Host_that_cant_know_says_null_not_none()
    {
        // The headless server: its extensions pair over the network, so it has no sightings and says so with null.
        var service = await ServiceAsync();
        var dispatcher = new HostCommandDispatcher(service, new FakePlatform(PlatformCapabilities.Desktop, _timeline), new ListLog());
        var window = Session();
        await dispatcher.OpenSessionAsync(window, new ClientHelloDto { Browser = ExtensionBrowsers.Chrome }, CancellationToken.None);

        var status = (await AskAsync(dispatcher, window, MessageTypes.HostStatus, new { })).GetPayload<HostStatusDto>();
        Assert.Null(status.ExtensionsSeen);
    }

    [Fact]
    public async Task A_client_that_names_no_browser_counts_as_an_extension_only_when_it_sends_what_only_an_extension_sends()
    {
        // UI.B31: a native host older than 0.7.2 names no browser; its host.ping or handoff says it is one.
        var service = await ServiceAsync();
        service.ExtensionSightings = new ExtensionSightings(Path.Combine(_root, "seen"));
        var dispatcher = new HostCommandDispatcher(service, new FakePlatform(PlatformCapabilities.Desktop, _timeline), new ListLog());

        var window = Session();
        await dispatcher.OpenSessionAsync(window, null, CancellationToken.None);
        // The window's own questions are not an extension's.
        await AskAsync(dispatcher, window, MessageTypes.HostStatus, new { });
        await AskAsync(dispatcher, window, MessageTypes.JobsQuery, new { });
        await AskAsync(dispatcher, window, MessageTypes.HostPing, new { });
        // Nor is a ping from a client that says hello as the CLI does.
        var cli = Session();
        await dispatcher.OpenSessionAsync(cli, new ClientHelloDto { Snapshot = false, Events = false }, CancellationToken.None);
        await AskAsync(dispatcher, cli, MessageTypes.HostPing, new { });
        Assert.Empty(service.ExtensionSightings.Snapshot());

        var oldNativeHost = Session();
        await dispatcher.OpenSessionAsync(oldNativeHost, new ClientHelloDto { Snapshot = false, Events = true }, CancellationToken.None);
        await AskAsync(dispatcher, oldNativeHost, MessageTypes.HostPing, new { });
        Assert.Equal([ExtensionBrowsers.Unknown], service.ExtensionSightings.Snapshot().Keys);
        await window.UntilAsync(frames => frames.Any(f => f.Type == MessageTypes.ExtensionsSeen));
    }
}

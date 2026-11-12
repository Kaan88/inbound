using System.Net;
using DownloadManager.Contracts;
using DownloadManager.Runtime.Ipc;
using DownloadManager.Runtime.WebUi;

namespace DownloadManager.Integration.Tests;

/// <summary>
/// D8: the web UI's supervisor with a fake bridge, firewall, listener table and password file. What the real parts do is
/// in <see cref="WebUiHostPartsTests"/> and <see cref="WebUiBridgeEndToEndTests"/>.
/// </summary>
public sealed class WebUiSupervisorTests : IDisposable
{
    private readonly string _data = Path.Combine(AppContext.BaseDirectory, "test-work", "webui-" + Guid.NewGuid().ToString("N"));
    private readonly List<string> _timeline = [];
    private readonly FakeLauncher _launcher;
    private readonly FakeFirewall _firewall = new();
    private readonly FakeListener _listener = new();
    private readonly FakePasswords _passwords;
    private readonly List<WebUiStatusDto> _changes = [];
    private readonly List<string> _notices = [];
    private int _closed;

    public WebUiSupervisorTests()
    {
        Directory.CreateDirectory(_data);
        _launcher = new FakeLauncher(_timeline);
        _passwords = new FakePasswords(_timeline);
    }

    public void Dispose()
    {
        try { Directory.Delete(_data, recursive: true); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { /* best effort */ }
    }

    private WebUiSupervisor Supervisor(TimeSpan? firstRestart = null, TimeSpan? startTimeout = null, TimeProvider? time = null)
    {
        var supervisor = new WebUiSupervisor(new WebUiConfigStore(_data),
            new WebUiPlatform(_launcher, _firewall, _listener, _passwords, new FakeNetwork()), _data, "test-hello-pipe", new TestLog(), time)
        {
            FirstRestartDelay = firstRestart ?? TimeSpan.FromMilliseconds(50),
            StartTimeout = startTimeout ?? TimeSpan.FromSeconds(10),
            StopGrace = TimeSpan.FromSeconds(1),
        };
        supervisor.Changed += (_, status) => { lock (_changes) _changes.Add(status); };
        supervisor.CloseWebSessions = () => { Interlocked.Increment(ref _closed); lock (_timeline) _timeline.Add("close-web"); return 1; };
        supervisor.Notice = (title, message) => { lock (_notices) _notices.Add($"{title}: {message}"); };
        return supervisor;
    }

    private static async Task UntilAsync(Func<bool> done, string what)
    {
        var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(10);
        while (!done())
        {
            if (DateTime.UtcNow > deadline) throw new TimeoutException(what);
            await Task.Delay(10);
        }
    }

    [Fact]
    public async Task OffByDefaultStartsNothingAndReadsTheFirewallOnlyWhenAsked()
    {
        await using var supervisor = Supervisor();
        await supervisor.StartAsync();
        Assert.Empty(_launcher.Starts);
        Assert.Equal(0, _firewall.Checks);

        var status = await supervisor.GetAsync();
        Assert.False(status.Enabled);
        Assert.Equal(WebUiStates.Off, status.State);
        Assert.Equal(WebUiDefaults.Port, status.Port);
        Assert.Equal(WebUiDefaults.RuleName, status.RuleName);
        Assert.Equal(_launcher.BridgePath, status.BridgePath);
        Assert.Equal(WebUiFirewallStates.Missing, status.Firewall);
        Assert.False(status.LocalOnly);
        Assert.Empty(status.Addresses);
        Assert.Empty(_launcher.Starts);
        Assert.False(File.Exists(Path.Combine(_data, WebUiConfigStore.FileName)));
    }

    [Fact]
    public async Task EnablingWithoutAPasswordIsRefusedAndNothingIsSaved()
    {
        await using var supervisor = Supervisor();
        var status = await supervisor.SetAsync(new WebUiSetRequest { Enabled = true });
        Assert.False(status.Enabled);
        Assert.Equal("Set a password first. Other devices sign in with it.", status.Error);
        Assert.Empty(_launcher.Starts);
        Assert.False(File.Exists(Path.Combine(_data, WebUiConfigStore.FileName)));
    }

    [Fact]
    public async Task APortOutsideTheRangeOrANameThatIsNoneIsRefused()
    {
        await using var supervisor = Supervisor();
        Assert.Equal("The port must be between 1024 and 65535.", (await supervisor.SetAsync(new WebUiSetRequest { Port = 80 })).Error);
        Assert.Equal("The port must be between 1024 and 65535.", (await supervisor.SetAsync(new WebUiSetRequest { Port = 70000 })).Error);
        Assert.Contains("isn't a computer name", (await supervisor.SetAsync(new WebUiSetRequest { AlsoReachableAs = ["http://mypc:8080"] })).Error);
        Assert.Contains("isn't a computer name", (await supervisor.SetAsync(new WebUiSetRequest { AlsoReachableAs = ["192.168.1.2"] })).Error);
        var named = await supervisor.SetAsync(new WebUiSetRequest { AlsoReachableAs = [" MyPC ", "mypc", "nas.home."] });
        Assert.Null(named.Error);
        Assert.Equal(["mypc", "nas.home"], named.AlsoReachableAs);
        Assert.Equal(WebUiDefaults.Port, named.Port);
    }

    [Fact]
    public async Task EnablingStartsTheBridgeOnThisPcOnlyWhileTheRuleIsMissingAndSavesTheSettings()
    {
        _passwords.Has = true;
        await using var supervisor = Supervisor();
        var status = await supervisor.SetAsync(new WebUiSetRequest { Enabled = true, Port = 18080 });

        Assert.Null(status.Error);
        Assert.Equal(WebUiStates.Running, status.State);
        Assert.True(status.LocalOnly);
        Assert.Empty(status.Addresses);
        var start = Assert.Single(_launcher.Starts);
        Assert.True(start.LocalOnly);
        Assert.Equal(18080, start.Port);
        Assert.Equal(_data, start.DataDirectory);
        Assert.Equal("test-hello-pipe", start.HelloPipe);
        Assert.Equal(32, Convert.FromBase64String(start.TicketKey).Length);
        Assert.Contains(_changes, change => change.State == WebUiStates.Starting);
        Assert.Contains(_changes, change => change.State == WebUiStates.Running);

        var saved = new WebUiConfigStore(_data).Load(out var problem);
        Assert.Null(problem);
        Assert.True(saved.Enabled);
        Assert.Equal(18080, saved.Port);

        // The Host starts again: the box is still ticked, so the bridge is started at once.
        await supervisor.StopAsync();
        Assert.True(_launcher.Processes[0].Stopped);
        await using var again = Supervisor();
        await again.StartAsync();
        Assert.Equal(2, _launcher.Starts.Count);
        Assert.Equal(WebUiStates.Running, (await again.GetAsync()).State);
    }

    [Fact]
    public async Task OnceTheRuleIsThereRecheckMovesTheBridgeToEveryAddress()
    {
        _passwords.Has = true;
        await using var supervisor = Supervisor();
        await supervisor.SetAsync(new WebUiSetRequest { Enabled = true });
        Assert.True(_launcher.Starts[0].LocalOnly);
        var closedBefore = _closed;

        // Nothing changed: no restart.
        await supervisor.RecheckAsync();
        Assert.Single(_launcher.Starts);

        _firewall.State = WebUiFirewallStates.Ready;
        var status = await supervisor.RecheckAsync();
        Assert.Equal(2, _launcher.Starts.Count);
        Assert.False(_launcher.Starts[1].LocalOnly);
        Assert.True(_launcher.Processes[0].Stopped);
        Assert.False(status.LocalOnly);
        Assert.Equal(WebUiFirewallStates.Ready, status.Firewall);
        Assert.Equal(["http://192.168.1.20:8080/"], status.Addresses);

        // A rule that goes away (or is blocked again) brings it back to this PC only.
        _firewall.State = WebUiFirewallStates.Missing;
        Assert.True((await supervisor.RecheckAsync()).LocalOnly);
        Assert.True(_launcher.Starts[2].LocalOnly);
        // Only the addresses changed: the web sessions end with the old bridge's pipes, the sign-ins stay.
        Assert.Equal(closedBefore, _closed);
        Assert.DoesNotContain("end-sign-ins", _timeline);
    }

    [Fact]
    public async Task ARecheckStartsNothingThatIsntRunning()
    {
        // Failed for a port in use: a recheck can't fix that, so it doesn't try again or raise the notice again.
        _passwords.Has = true;
        _launcher.Behave = process => process.Exit(WebBridgeExit.PortInUse, "Port 8080 is in use by another program. Choose another port.");
        await using var supervisor = Supervisor();
        await supervisor.SetAsync(new WebUiSetRequest { Enabled = true });
        _firewall.State = WebUiFirewallStates.Ready;
        var status = await supervisor.RecheckAsync();
        Assert.Equal(WebUiStates.Failed, status.State);
        Assert.Equal(WebUiFirewallStates.Ready, status.Firewall);
        await supervisor.RecheckAsync();
        Assert.Single(_launcher.Starts);
        Assert.Single(_notices);

        // Off: nothing starts either.
        await supervisor.SetAsync(new WebUiSetRequest { Enabled = false });
        await supervisor.RecheckAsync();
        Assert.Single(_launcher.Starts);
    }

    [Fact]
    public async Task ARecheckWhileWaitingToRestartLeavesTheRuleToTheRestart()
    {
        _passwords.Has = true;
        await using var supervisor = Supervisor(firstRestart: TimeSpan.FromMilliseconds(400));
        await supervisor.SetAsync(new WebUiSetRequest { Enabled = true });
        _launcher.Processes[0].Exit(WebBridgeExit.Crashed, null);
        await UntilAsync(() => supervisor.GetAsync().GetAwaiter().GetResult().State == WebUiStates.Starting, "waiting to restart");
        _firewall.State = WebUiFirewallStates.Ready;
        await supervisor.RecheckAsync();
        Assert.Single(_launcher.Starts);
        await UntilAsync(() => _launcher.Starts.Count == 2, "the restart");
        Assert.False(_launcher.Starts[1].LocalOnly);
    }

    [Fact]
    public async Task APortOrNameChangeRestartsTheBridgeAndTurningItOffStopsItAndEndsTheWebSessions()
    {
        _passwords.Has = true;
        await using var supervisor = Supervisor();
        await supervisor.SetAsync(new WebUiSetRequest { Enabled = true });
        await supervisor.SetAsync(new WebUiSetRequest { Port = 18081 });
        Assert.Equal(2, _launcher.Starts.Count);
        Assert.Equal(18081, _launcher.Starts[1].Port);
        await supervisor.SetAsync(new WebUiSetRequest { AlsoReachableAs = ["mypc"] });
        Assert.Equal(3, _launcher.Starts.Count);
        Assert.Equal(["mypc"], _launcher.Starts[2].Hosts);
        // The same values again change nothing.
        await supervisor.SetAsync(new WebUiSetRequest { Enabled = true, Port = 18081, AlsoReachableAs = ["mypc"] });
        Assert.Equal(3, _launcher.Starts.Count);

        var closedBefore = _closed;
        lock (_timeline) _timeline.Clear();
        var off = await supervisor.SetAsync(new WebUiSetRequest { Enabled = false });
        Assert.Equal(WebUiStates.Off, off.State);
        Assert.All(_launcher.Processes, process => Assert.True(process.Stopped));
        Assert.True(_closed > closedBefore);
        Assert.False(new WebUiConfigStore(_data).Load(out _).Enabled);
        Assert.Equal(3, _launcher.Starts.Count);
        // Every browser signs in again next time, once the bridge is gone (it can't write them back).
        Assert.Equal(["stop", "close-web", "end-sign-ins"], _timeline);

        // Cleared again: nothing more to end.
        lock (_timeline) _timeline.Clear();
        await supervisor.SetAsync(new WebUiSetRequest { Enabled = false });
        Assert.DoesNotContain("end-sign-ins", _timeline);
    }

    [Fact]
    public async Task APortInUseFailsWithItsMessageAndATrayNoticeAndIsNotRetried()
    {
        _passwords.Has = true;
        _launcher.Behave = process => process.Exit(WebBridgeExit.PortInUse, "Port 8080 is in use by another program. Choose another port.");
        await using var supervisor = Supervisor();
        var status = await supervisor.SetAsync(new WebUiSetRequest { Enabled = true });

        Assert.True(status.Enabled);
        Assert.Equal(WebUiStates.Failed, status.State);
        Assert.Equal("Port 8080 is in use by another program. Choose another port.", status.Error);
        Assert.Equal(["Web UI: Port 8080 is in use by another program. Choose another port."], _notices);
        await Task.Delay(300);
        Assert.Single(_launcher.Starts);

        // Ticking the box again (or another port) tries again.
        _launcher.Behave = null;
        Assert.Equal(WebUiStates.Running, (await supervisor.SetAsync(new WebUiSetRequest { Enabled = true })).State);
    }

    [Fact]
    public async Task APortWindowsKeepsFailsWithItsMessageAndATrayNoticeAndIsNotRetried()
    {
        _passwords.Has = true;
        _launcher.Behave = process => process.Exit(WebBridgeExit.PortForbidden, "Windows doesn't allow listening on port 8080. Choose another port.");
        await using var supervisor = Supervisor();
        var status = await supervisor.SetAsync(new WebUiSetRequest { Enabled = true });
        Assert.Equal(WebUiStates.Failed, status.State);
        Assert.Equal("Windows doesn't allow listening on port 8080. Choose another port.", status.Error);
        Assert.Equal(["Web UI: Windows doesn't allow listening on port 8080. Choose another port."], _notices);
        await Task.Delay(300);
        Assert.Single(_launcher.Starts);

        // Without a message from the bridge, the Host says it.
        _launcher.Behave = process => process.Exit(WebBridgeExit.PortForbidden, null);
        Assert.Equal("Windows doesn't allow listening on port 18085. Choose another port.",
            (await supervisor.SetAsync(new WebUiSetRequest { Port = 18085 })).Error);
    }

    [Fact]
    public async Task ARunOfTwoMinutesStartsTheCrashCountAgain()
    {
        _passwords.Has = true;
        var time = new OffsetTime();
        await using var supervisor = Supervisor(time: time);
        await supervisor.SetAsync(new WebUiSetRequest { Enabled = true });

        // Each bridge says ready, then ends by itself: restarted each time, up to the limit.
        for (var i = 0; i < WebUiSupervisor.MaxRestarts; i++)
        {
            _launcher.Processes[i].Exit(WebBridgeExit.Crashed, null);
            var next = i + 2;
            await UntilAsync(() => _launcher.Starts.Count == next, $"restart {next - 1}");
            await UntilAsync(() => supervisor.GetAsync().GetAwaiter().GetResult().State == WebUiStates.Running, "running again");
        }

        // The last one ran for more than two minutes before it ended: that is no run of crashes, so it starts again.
        time.Offset += TimeSpan.FromMinutes(3);
        _launcher.Processes[WebUiSupervisor.MaxRestarts].Exit(WebBridgeExit.Crashed, null);
        await UntilAsync(() => _launcher.Starts.Count == WebUiSupervisor.MaxRestarts + 2, "a restart after a long run");
        await UntilAsync(() => supervisor.GetAsync().GetAwaiter().GetResult().State == WebUiStates.Running, "running after a long run");
        Assert.Empty(_notices);

        // Without the long run, the next end after the limit gives up.
        for (var i = 0; i < WebUiSupervisor.MaxRestarts; i++)
        {
            var count = _launcher.Starts.Count;
            _launcher.Processes[count - 1].Exit(WebBridgeExit.Crashed, null);
            if (i < WebUiSupervisor.MaxRestarts - 1)
            {
                await UntilAsync(() => _launcher.Starts.Count == count + 1, "a restart");
                await UntilAsync(() => supervisor.GetAsync().GetAwaiter().GetResult().State == WebUiStates.Running, "running");
            }
        }

        await UntilAsync(() => supervisor.GetAsync().GetAwaiter().GetResult().State == WebUiStates.Failed, "failed");
        Assert.Single(_notices);
    }

    [Fact]
    public async Task ABridgeThatEndsByItselfIsStartedAgainWithGrowingWaitsUntilItGivesUp()
    {
        _passwords.Has = true;
        await using var supervisor = Supervisor();
        await supervisor.SetAsync(new WebUiSetRequest { Enabled = true });

        _launcher.Processes[0].Exit(WebBridgeExit.Crashed, "boom");
        await UntilAsync(() => _launcher.Starts.Count == 2, "a restart");
        await UntilAsync(() => supervisor.GetAsync().GetAwaiter().GetResult().State == WebUiStates.Running, "running again");
        Assert.Contains(_changes, change => change.State == WebUiStates.Starting && change.Error == "The web UI stopped unexpectedly. Starting it again.");
        Assert.Contains("close-web", _timeline);

        // From now on every start fails at once: five restarts in a row, then it stays failed.
        _launcher.Behave = process => process.Exit(WebBridgeExit.Crashed, null);
        _launcher.Processes[1].Exit(WebBridgeExit.Crashed, null);
        await UntilAsync(() => supervisor.GetAsync().GetAwaiter().GetResult().State == WebUiStates.Failed, "failed");
        var failed = await supervisor.GetAsync();
        Assert.Equal("The web UI stopped unexpectedly several times. Details are in the app's log.", failed.Error);
        Assert.Equal(2 + WebUiSupervisor.MaxRestarts - 1, _launcher.Starts.Count);
        Assert.Single(_notices);
        await Task.Delay(500);
        Assert.Equal(2 + WebUiSupervisor.MaxRestarts - 1, _launcher.Starts.Count);
    }

    [Fact]
    public async Task TurningItOffDuringTheWaitBeforeARestartStartsNothing()
    {
        _passwords.Has = true;
        await using var supervisor = Supervisor(firstRestart: TimeSpan.FromMilliseconds(400));
        await supervisor.SetAsync(new WebUiSetRequest { Enabled = true });
        _launcher.Processes[0].Exit(WebBridgeExit.Crashed, null);
        await UntilAsync(() => supervisor.GetAsync().GetAwaiter().GetResult().State == WebUiStates.Starting, "waiting to restart");
        await supervisor.SetAsync(new WebUiSetRequest { Enabled = false });
        await Task.Delay(800);
        Assert.Single(_launcher.Starts);
        Assert.Equal(WebUiStates.Off, (await supervisor.GetAsync()).State);
    }

    [Fact]
    public async Task ABridgeThatNeverSaysReadyIsStoppedAndFails()
    {
        _passwords.Has = true;
        _launcher.Behave = _ => { };
        await using var supervisor = Supervisor(startTimeout: TimeSpan.FromMilliseconds(300));
        var status = await supervisor.SetAsync(new WebUiSetRequest { Enabled = true });
        Assert.Equal(WebUiStates.Failed, status.State);
        Assert.StartsWith("The web UI did not start within", status.Error);
        Assert.True(_launcher.Processes[0].Stopped);
    }

    [Fact]
    public async Task SettingThePasswordStopsTheBridgeFirstThenSavesItThenStartsItAgain()
    {
        _passwords.Has = true;
        await using var supervisor = Supervisor();
        await supervisor.SetAsync(new WebUiSetRequest { Enabled = true });
        lock (_timeline) _timeline.Clear();

        Assert.Equal("The password needs at least 8 characters.", (await supervisor.SetPasswordAsync("short")).Error);
        Assert.Empty(_timeline);

        var status = await supervisor.SetPasswordAsync("a long enough password");
        Assert.Null(status.Error);
        Assert.Equal(WebUiStates.Running, status.State);
        Assert.Equal(["stop", "close-web", "password", "start"], _timeline);
        Assert.False(_passwords.KeptBrowsers);

        // The password in force set again changes nothing: no restart, nobody signed out or unpaired.
        lock (_timeline) _timeline.Clear();
        var same = await supervisor.SetPasswordAsync("a long enough password");
        Assert.Null(same.Error);
        Assert.Equal(WebUiStates.Running, same.State);
        Assert.Empty(_timeline);

        // Keeping the paired browsers is passed on.
        await supervisor.HandleAsync(IpcMessage.Create(WebUiMessageTypes.SetPassword,
            new WebUiPasswordRequest { Password = "a third long password", KeepPairedBrowsers = true }), CancellationToken.None);
        Assert.True(_passwords.KeptBrowsers);

        // With the box cleared it is only saved.
        await supervisor.SetAsync(new WebUiSetRequest { Enabled = false });
        lock (_timeline) _timeline.Clear();
        Assert.Equal(WebUiStates.Off, (await supervisor.SetPasswordAsync("another long password")).State);
        Assert.Equal(["close-web", "password"], _timeline);
    }

    [Fact]
    public async Task APasswordIsNotChangedWhileABridgeThatDidNotStopStillRunsEvenWhenTriedAgain()
    {
        _passwords.Has = true;
        await using var supervisor = Supervisor();
        await supervisor.SetAsync(new WebUiSetRequest { Enabled = true });
        var stuck = Assert.Single(_launcher.Processes);
        stuck.IgnoresStop = true;
        lock (_timeline) _timeline.Clear();

        const string Refusal = "The web UI did not stop, so the password was not changed. Try again in a moment.";
        Assert.Equal(Refusal, (await supervisor.SetPasswordAsync("a long enough password")).Error);
        // Tried again while that bridge still runs: asked to stop again, and still refused, with no password written.
        Assert.Equal(Refusal, (await supervisor.SetPasswordAsync("a long enough password")).Error);
        Assert.DoesNotContain("password", _timeline);
        Assert.Equal(2, _timeline.Count(step => step == "stop"));

        // Once it has ended the password is changed, and a new bridge starts.
        stuck.Exit(WebBridgeExit.Stopped, null);
        var status = await supervisor.SetPasswordAsync("a long enough password");
        Assert.Null(status.Error);
        Assert.Contains("password", _timeline);
        Assert.Equal(WebUiStates.Running, status.State);
        Assert.Equal(2, _launcher.Processes.Count);
    }

    [Fact]
    public async Task Audit3N9_ANewBridgeIsNotStartedWhileOneThatDidNotStopStillRuns()
    {
        _passwords.Has = true;
        await using var supervisor = Supervisor();
        await supervisor.SetAsync(new WebUiSetRequest { Enabled = true, Port = 18083 });
        var stuck = Assert.Single(_launcher.Processes);
        stuck.IgnoresStop = true;

        // A new port: the old bridge did not end, so no second one is launched beside it.
        var status = await supervisor.SetAsync(new WebUiSetRequest { Enabled = true, Port = 18084 });
        Assert.Equal(WebUiStates.Failed, status.State);
        Assert.Equal("The web UI is still stopping; try again in a moment.", status.Error);
        Assert.Single(_launcher.Processes);

        // Tried again while it still runs: still nothing launched.
        Assert.Equal(WebUiStates.Failed, (await supervisor.SetAsync(new WebUiSetRequest { Enabled = true, Port = 18084 })).State);
        Assert.Single(_launcher.Processes);

        // Once it has ended, the next try starts the bridge.
        stuck.Exit(WebBridgeExit.Stopped, null);
        status = await supervisor.SetAsync(new WebUiSetRequest { Enabled = true, Port = 18084 });
        Assert.Equal(WebUiStates.Running, status.State);
        Assert.Equal(2, _launcher.Processes.Count);
        Assert.Equal(18084, _launcher.Starts[^1].Port);
    }

    [Fact]
    public async Task TheTicketIsGivenOnlyWhileRunningAndOnlyWhenTheBridgeItStartedIsTheOneListening()
    {
        _passwords.Has = true;
        await using var supervisor = Supervisor();
        Assert.Equal("The web UI isn't running.", (await supervisor.OpenTicketAsync()).Error);

        await supervisor.SetAsync(new WebUiSetRequest { Enabled = true, Port = 18082 });
        _listener.Answer = ListenerOwner.Other;
        var refused = await supervisor.OpenTicketAsync();
        Assert.Null(refused.Url);
        Assert.Contains("Another program answers on port", refused.Error);
        Assert.Equal((_launcher.Processes[0].ProcessId, 18082), _listener.LastAsked);

        // Nothing there (the bridge ended between two checks) is said as that, not as another program.
        _listener.Answer = ListenerOwner.None;
        var nobody = await supervisor.OpenTicketAsync();
        Assert.Null(nobody.Url);
        Assert.Equal($"The web UI isn't answering on port {18082} right now. Try again in a moment.", nobody.Error);

        _listener.Answer = ListenerOwner.Ours;
        var ticket = await supervisor.OpenTicketAsync();
        Assert.Null(ticket.Error);
        Assert.StartsWith("http://127.0.0.1:18082/api/auth/ticket?t=", ticket.Url);
        var key = Convert.FromBase64String(_launcher.Starts[0].TicketKey);
        var url = ticket.Url!;
        var value = url[(url.IndexOf("?t=", StringComparison.Ordinal) + 3)..];
        Assert.NotNull(WebUiTicket.Check(key, value, DateTimeOffset.UtcNow));
        Assert.Null(WebUiTicket.Check(key, value, DateTimeOffset.UtcNow + TimeSpan.FromSeconds(61)));
        Assert.Null(WebUiTicket.Check(WebUiTicket.NewKey(), value, DateTimeOffset.UtcNow));
    }

    [Fact]
    public async Task ADamagedSettingsFileCountsAsOff()
    {
        await File.WriteAllTextAsync(Path.Combine(_data, WebUiConfigStore.FileName), "{ not json");
        _passwords.Has = true;
        await using var supervisor = Supervisor();
        await supervisor.StartAsync();
        Assert.Empty(_launcher.Starts);
        Assert.False((await supervisor.GetAsync()).Enabled);
    }

    [Fact]
    public async Task TheSettingsAreWrittenWholeThroughATemporaryFile()
    {
        var store = new WebUiConfigStore(_data);
        await store.SaveAsync(new WebUiConfig { Enabled = true, Port = 9000, AlsoReachableAs = ["mypc"] });
        Assert.False(File.Exists(store.Path + ".tmp"));
        var loaded = store.Load(out var problem);
        Assert.Null(problem);
        Assert.True(loaded.Enabled);
        Assert.Equal(9000, loaded.Port);
        Assert.Equal(["mypc"], loaded.AlsoReachableAs);
        // A port out of range in the file is the default; it is never used as it is.
        await File.WriteAllTextAsync(store.Path, """{"enabled":true,"port":22}""");
        Assert.Equal(WebUiDefaults.Port, store.Load(out _).Port);
    }

    [Fact]
    public async Task TheCommandsAnswerWithTheirReplyTypes()
    {
        _passwords.Has = true;
        await using var supervisor = Supervisor();
        var get = await supervisor.HandleAsync(IpcMessage.Create(WebUiMessageTypes.Get, new { }), CancellationToken.None);
        Assert.Equal(WebUiMessageTypes.Status, get.Type);
        var set = IpcMessage.Create(WebUiMessageTypes.Set, new WebUiSetRequest { Enabled = true });
        var reply = await supervisor.HandleAsync(set, CancellationToken.None);
        Assert.Equal(set.Id, reply.ReplyTo);
        Assert.True(reply.GetPayload<WebUiStatusDto>().Enabled);
        var ticket = await supervisor.HandleAsync(IpcMessage.Create(WebUiMessageTypes.OpenTicket, new { }), CancellationToken.None);
        Assert.Equal(WebUiMessageTypes.Ticket, ticket.Type);
        Assert.NotNull(ticket.GetPayload<WebUiTicketDto>().Url);
    }

    internal sealed class TestLog : IHostLog
    {
        public List<string> Lines { get; } = [];
        public void Info(string message) { lock (Lines) Lines.Add("INFO " + message); }
        public void Warn(string message) { lock (Lines) Lines.Add("WARN " + message); }
        public void Error(string message) { lock (Lines) Lines.Add("ERROR " + message); }
    }

    // ---- fakes ------------------------------------------------------------------------------------------------

    internal sealed class FakeProcess(int id, List<string> timeline) : IWebBridgeProcess
    {
        private readonly TaskCompletionSource _ready = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly TaskCompletionSource<WebBridgeExitInfo> _exited = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public int ProcessId { get; } = id;
        public Task Ready => _ready.Task;
        public Task<WebBridgeExitInfo> Exited => _exited.Task;
        public bool Stopped { get; private set; }

        /// <summary>A bridge that doesn't end when asked to (it still runs after the grace and the kill).</summary>
        public bool IgnoresStop { get; set; }

        public void MarkReady() => _ready.TrySetResult();

        public void Exit(int code, string? message) => _exited.TrySetResult(new WebBridgeExitInfo(code, message));

        public Task StopAsync(TimeSpan grace)
        {
            Stopped = true;
            lock (timeline) timeline.Add("stop");
            if (!IgnoresStop) Exit(WebBridgeExit.Stopped, null);
            return Task.CompletedTask;
        }
    }

    internal sealed class FakeLauncher(List<string> timeline) : IWebBridgeLauncher
    {
        private int _next = 5000;

        public string BridgePath => @"C:\Program Files\Test\DownloadManager.Server.exe";
        public List<WebBridgeStartDto> Starts { get; } = [];
        public List<FakeProcess> Processes { get; } = [];

        /// <summary>What a started bridge does; null says ready at once.</summary>
        public Action<FakeProcess>? Behave { get; set; }

        public IWebBridgeProcess Start(WebBridgeStartDto start)
        {
            var process = new FakeProcess(Interlocked.Increment(ref _next), timeline);
            lock (Starts)
            {
                Starts.Add(start);
                Processes.Add(process);
            }

            lock (timeline) timeline.Add("start");
            if (Behave is { } behave) behave(process);
            else process.MarkReady();
            return process;
        }
    }

    internal sealed class FakeFirewall : IFirewallRules
    {
        public string State { get; set; } = WebUiFirewallStates.Missing;
        public int Checks { get; private set; }

        public string Check(string program, int port, string ruleName)
        {
            Checks++;
            return State;
        }
    }

    internal sealed class FakeListener : IListenerCheck
    {
        public ListenerOwner Answer { get; set; } = ListenerOwner.Ours;
        public (int, int)? LastAsked { get; private set; }

        public ListenerOwner Owner(int processId, int port)
        {
            LastAsked = (processId, port);
            return Answer;
        }
    }

    internal sealed class FakePasswords(List<string> timeline) : IWebUiPasswords
    {
        private string? _current;

        public bool Has { get; set; }
        public bool KeptBrowsers { get; private set; }

        public bool HasPassword() => Has;

        public string? Problem(string? password) => password is null || password.Length < 8 ? "The password needs at least 8 characters." : null;

        public Task<bool> IsCurrentAsync(string password, CancellationToken cancellationToken) => Task.FromResult(Has && password == _current);

        public Task SetAsync(string password, bool keepPairedBrowsers, CancellationToken cancellationToken)
        {
            lock (timeline) timeline.Add("password");
            Has = true;
            _current = password;
            KeptBrowsers = keepPairedBrowsers;
            return Task.CompletedTask;
        }

        public void EndSignIns()
        {
            lock (timeline) timeline.Add("end-sign-ins");
        }
    }

    /// <summary>The system's clock and timers, with the time moved on by <see cref="Offset"/>.</summary>
    internal sealed class OffsetTime : TimeProvider
    {
        public TimeSpan Offset { get; set; }

        public override DateTimeOffset GetUtcNow() => System.GetUtcNow() + Offset;
    }

    internal sealed class FakeNetwork : INetworkAddresses
    {
        public IReadOnlyList<IPAddress> Current() => [IPAddress.Parse("192.168.1.20"), IPAddress.Loopback];
    }
}

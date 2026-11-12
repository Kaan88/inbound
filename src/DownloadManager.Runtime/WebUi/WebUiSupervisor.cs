using System.Globalization;
using System.Net;
using System.Net.Sockets;
using DownloadManager.Contracts;
using DownloadManager.Runtime.Ipc;

namespace DownloadManager.Runtime.WebUi;

/// <summary>
/// The desktop Host's web UI (D8): the <c>webui.*</c> commands and the bridge process's life.
/// <list type="bullet">
/// <item>Started when the box is ticked, and when the Host starts with it ticked; stopped when it is cleared, which also
/// ends every web session and every browser's sign-in (paired apps stay paired); started again when the port, the extra
/// names or the password change.</item>
/// <item>While the firewall rule is missing (or can't be read) the bridge listens on this PC only; <c>webui.recheck</c>
/// reads the rule again and moves it to every address once it is there. That is a restart of the bridge, so open pages
/// reconnect, still signed in. A recheck starts nothing that isn't running (off, failed, or waiting to restart).</item>
/// <item>A bridge that ends by itself is started again after 1, 2, 4 ... seconds (at most a minute), up to
/// <see cref="MaxRestarts"/> times in a row; one that ran two minutes first starts the count again. A port in use, one
/// Windows keeps, or a start the bridge refuses is not retried: it is the state's error and a tray notice.</item>
/// <item>With the box cleared nothing runs: no process, no timer, nothing listening.</item>
/// </list>
/// Every change is one step at a time (one gate), so two windows saving at once can't leave two bridges.
/// </summary>
public sealed class WebUiSupervisor : IWebUiCommands, IAsyncDisposable
{
    public const int MaxRestarts = 5;
    private const string NoticeTitle = "Web UI";
    private static readonly TimeSpan StableRun = TimeSpan.FromMinutes(2);
    private static readonly TimeSpan MaxRestartDelay = TimeSpan.FromMinutes(1);

    private readonly WebUiConfigStore _store;
    private readonly WebUiPlatform _platform;
    private readonly string _dataDirectory;
    private readonly string _helloPipe;
    private readonly IHostLog _log;
    private readonly TimeProvider _time;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private WebUiConfig _config;
    private string _firewall = WebUiFirewallStates.Unknown;
    private int? _firewallPort;
    private string _state = WebUiStates.Off;
    private string? _error;
    private Run? _run;

    // Bridges that were asked to stop and had not ended: asked again by the next stop, which reports one as still
    // running until it has ended (L20), so a retry can't change the password under it.
    private readonly List<Run> _lingering = [];
    private CancellationTokenSource? _restart;
    private int _crashes;
    private bool _stopped;

    public WebUiSupervisor(WebUiConfigStore store, WebUiPlatform platform, string dataDirectory, string helloPipe, IHostLog log,
        TimeProvider? time = null)
    {
        _store = store;
        _platform = platform;
        _dataDirectory = dataDirectory;
        _helloPipe = helloPipe;
        _log = log;
        _time = time ?? TimeProvider.System;
        _config = store.Load(out var problem);
        if (problem is not null) log.Warn(problem);
    }

    /// <summary>How long the bridge has to say it listens.</summary>
    public TimeSpan StartTimeout { get; init; } = TimeSpan.FromSeconds(20);

    /// <summary>How long a bridge asked to stop has before it is ended.</summary>
    public TimeSpan StopGrace { get; init; } = TimeSpan.FromSeconds(10);

    /// <summary>The wait before the first restart after a crash; it doubles each time.</summary>
    public TimeSpan FirstRestartDelay { get; init; } = TimeSpan.FromSeconds(1);

    /// <summary>The status after every change, for <c>webui.changed</c> (to the desktop role's sessions only).</summary>
    public event EventHandler<WebUiStatusDto>? Changed;

    /// <summary>Ends every web session (<see cref="HostCommandDispatcher.CloseSessions"/>); how many.</summary>
    public Func<int>? CloseWebSessions { get; set; }

    /// <summary>A tray notice: title, message. Set once the tray exists.</summary>
    public Action<string, string>? Notice { get; set; }

    /// <summary>On the Host's start: the bridge, if the box is ticked.</summary>
    public async Task StartAsync(CancellationToken cancellationToken = default)
    {
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (_config.Enabled && !_stopped) await StartLockedAsync().ConfigureAwait(false);
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <summary>On the Host's exit: the bridge stops and nothing starts it again.</summary>
    public async Task StopAsync()
    {
        await _gate.WaitAsync().ConfigureAwait(false);
        try
        {
            _stopped = true;
            await StopLockedAsync().ConfigureAwait(false);
        }
        finally
        {
            _gate.Release();
        }
    }

    public ValueTask DisposeAsync() => new(StopAsync());

    public async Task<IpcMessage> HandleAsync(IpcMessage request, CancellationToken cancellationToken)
    {
        switch (request.Type)
        {
            case WebUiMessageTypes.OpenTicket:
                return IpcMessage.Create(WebUiMessageTypes.Ticket, await OpenTicketAsync(cancellationToken).ConfigureAwait(false), request.Id);
            case WebUiMessageTypes.Set:
                return Status(request, await SetAsync(request.GetPayload<WebUiSetRequest>(), cancellationToken).ConfigureAwait(false));
            case WebUiMessageTypes.SetPassword:
                var password = request.GetPayload<WebUiPasswordRequest>();
                return Status(request, await SetPasswordAsync(password.Password, password.KeepPairedBrowsers, cancellationToken).ConfigureAwait(false));
            case WebUiMessageTypes.Recheck:
                return Status(request, await RecheckAsync(cancellationToken).ConfigureAwait(false));
            default:
                return Status(request, await GetAsync(cancellationToken).ConfigureAwait(false));
        }

        static IpcMessage Status(IpcMessage request, WebUiStatusDto status) => IpcMessage.Create(WebUiMessageTypes.Status, status, request.Id);
    }

    public Task<WebUiStatusDto> GetAsync(CancellationToken cancellationToken = default) => Locked(() =>
    {
        // Read once, and again for another port: nothing watches the rules (no timer), so the page asks with recheck.
        if (_firewallPort != _config.Port) CheckFirewall();
        return Task.FromResult(Status());
    }, cancellationToken);

    public Task<WebUiStatusDto> SetAsync(WebUiSetRequest request, CancellationToken cancellationToken = default) => Locked(async () =>
    {
        var next = _config.Copy();
        if (request.Port is { } port)
        {
            if (WebUiConfig.PortProblem(port) is { } problem) return Refused(problem);
            next.Port = port;
        }

        if (request.AlsoReachableAs is { } names)
        {
            if (WebUiConfig.CleanNames(names, out var problem) is not { } clean) return Refused(problem!);
            next.AlsoReachableAs = clean;
        }

        if (request.Enabled is { } enabled) next.Enabled = enabled;
        if (next.Enabled && !_platform.Passwords.HasPassword())
            return Refused("Set a password first. Other devices sign in with it.");

        var changed = next.Enabled != _config.Enabled || next.Port != _config.Port || !next.AlsoReachableAs.SequenceEqual(_config.AlsoReachableAs);
        if (changed)
        {
            try
            {
                await _store.SaveAsync(next, CancellationToken.None).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                _log.Warn($"The web UI settings could not be saved: {ex.GetType().Name}");
                return Refused("The web UI settings could not be saved, so nothing was changed. Check the space on the app's drive.");
            }

            _log.Info($"Web UI settings: {(next.Enabled ? "on" : "off")}, port {next.Port}, {next.AlsoReachableAs.Count} extra name(s).");
            _config = next;
        }

        _crashes = 0;
        if (!_config.Enabled)
        {
            await StopLockedAsync().ConfigureAwait(false);
            // Cleared: a browser signed in before must sign in again when it is ticked again (the bridge is stopped,
            // so it can't write them back).
            if (changed) EndSignIns();
            SetState(WebUiStates.Off, null);
        }
        else if (changed || _state is WebUiStates.Failed or WebUiStates.Off)
        {
            await StopLockedAsync().ConfigureAwait(false);
            await StartLockedAsync().ConfigureAwait(false);
        }

        return Announce();
    }, cancellationToken);

    /// <param name="keepPairedBrowsers">Leave the paired browser extensions paired (<see cref="WebUiPasswordRequest.KeepPairedBrowsers"/>).</param>
    public Task<WebUiStatusDto> SetPasswordAsync(string? password, bool keepPairedBrowsers = false, CancellationToken cancellationToken = default) => Locked(async () =>
    {
        if (_platform.Passwords.Problem(password) is { } problem) return Refused(problem);
        bool same;
        try
        {
            same = await _platform.Passwords.IsCurrentAsync(password!, CancellationToken.None).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            same = false;
        }

        // The password in force, saved again: nothing to end, nobody to unpair, no restart.
        if (same) return Announce();

        // The bridge keeps the sign-in file in memory and would write the old password back: it stops first, and one
        // that is still running leaves the password as it was (L20).
        if (!await StopLockedAsync().ConfigureAwait(false))
            return Refused("The web UI did not stop, so the password was not changed. Try again in a moment.");
        try
        {
            await _platform.Passwords.SetAsync(password!, keepPairedBrowsers, CancellationToken.None).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidDataException)
        {
            _log.Warn($"The web UI password could not be saved: {ex.GetType().Name}");
            if (_config.Enabled) await StartLockedAsync().ConfigureAwait(false);
            return Refused("The password could not be saved, so nothing was changed. Check the space on the app's drive.");
        }

        _log.Info(keepPairedBrowsers
            ? "The web UI password was set in the app; browsers sign in again, paired desktop apps pair again, paired browser extensions were kept."
            : "The web UI password was set in the app; browsers sign in again and paired apps pair again.");
        _crashes = 0;
        if (_config.Enabled) await StartLockedAsync().ConfigureAwait(false);
        else SetState(WebUiStates.Off, null);
        return Announce();
    }, cancellationToken);

    public Task<WebUiStatusDto> RecheckAsync(CancellationToken cancellationToken = default) => Locked(async () =>
    {
        CheckFirewall();
        var wantLocal = _firewall != WebUiFirewallStates.Ready;
        // Only a running bridge listening on the wrong addresses is restarted. One that failed (a port in use, say)
        // isn't tried again by a recheck, which can't fix that; one waiting to restart reads the rule when it starts.
        if (_config.Enabled && _run is { } run && run.LocalOnly != wantLocal)
        {
            _crashes = 0;
            // Only the addresses change: the sign-ins stay, and the pages reconnect to the new bridge.
            await StopLockedAsync(endSessions: false).ConfigureAwait(false);
            await StartLockedAsync().ConfigureAwait(false);
        }

        return Announce();
    }, cancellationToken);

    public Task<WebUiTicketDto> OpenTicketAsync(CancellationToken cancellationToken = default) => Locked(() =>
    {
        if (_run is not { IsReady: true } run || _state != WebUiStates.Running)
            return Task.FromResult(new WebUiTicketDto { Error = "The web UI isn't running." });
        // Exclusive use of the port keeps others off it, but only a check says the bridge started here is the one
        // answering: the ticket signs a browser in, so it goes to no other program.
        ListenerOwner owner;
        try
        {
            owner = _platform.Listener.Owner(run.Process.ProcessId, run.Port);
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            _log.Warn($"Could not see which program listens on the web UI's port: {ex.GetType().Name}");
            return Task.FromResult(new WebUiTicketDto { Error = "The app could not check the web UI's port, so it can't be opened from here. Details are in the app's log." });
        }

        var portText = run.Port.ToString(CultureInfo.CurrentCulture);
        switch (owner)
        {
            case ListenerOwner.None:
                return Task.FromResult(new WebUiTicketDto { Error = $"The web UI isn't answering on port {portText} right now. Try again in a moment." });
            case ListenerOwner.Other:
                return Task.FromResult(new WebUiTicketDto
                {
                    Error = $"Another program answers on port {portText}, so the web UI can't be opened from here. Choose another port.",
                });
        }

        var ticket = WebUiTicket.Create(run.Key, _time.GetUtcNow());
        return Task.FromResult(new WebUiTicketDto
        {
            Url = $"http://127.0.0.1:{run.Port.ToString(CultureInfo.InvariantCulture)}{WebUiDefaults.TicketPath}?t={ticket}",
        });
    }, cancellationToken);

    // ---- Under the gate ----------------------------------------------------------------------------------------

    private async Task<T> Locked<T>(Func<Task<T>> step, CancellationToken cancellationToken)
    {
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            return await step().ConfigureAwait(false);
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <summary>A request that changes nothing: the status as it is, with why in <see cref="WebUiStatusDto.Error"/>.</summary>
    private WebUiStatusDto Refused(string why)
    {
        var status = Status();
        status.Error = why;
        return status;
    }

    /// <summary>Every browser signs in again (the saved sessions go); the bridge is stopped.</summary>
    private void EndSignIns()
    {
        try
        {
            _platform.Passwords.EndSignIns();
            _log.Info("The web UI was turned off; every browser signs in again when it is turned on.");
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            _log.Warn($"The web UI's saved sign-ins could not be removed: {ex.GetType().Name}");
        }
    }

    private void CheckFirewall()
    {
        try
        {
            _firewall = _platform.Firewall.Check(_platform.Launcher.BridgePath, _config.Port, WebUiDefaults.RuleName);
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            _log.Warn($"The firewall rules could not be read: {ex.GetType().Name}");
            _firewall = WebUiFirewallStates.Unknown;
        }

        _firewallPort = _config.Port;
    }

    private async Task StartLockedAsync()
    {
        CancelRestart();
        if (_stopped) return;
        if (!_platform.Passwords.HasPassword())
        {
            SetState(WebUiStates.Failed, "Set a password for the web UI first.");
            return;
        }

        CheckFirewall();
        var localOnly = _firewall != WebUiFirewallStates.Ready;
        var key = WebUiTicket.NewKey();
        var port = _config.Port;
        IWebBridgeProcess process;
        try
        {
            process = _platform.Launcher.Start(new WebBridgeStartDto
            {
                DataDirectory = _dataDirectory,
                HelloPipe = _helloPipe,
                Port = port,
                LocalOnly = localOnly,
                Hosts = [.. _config.AlsoReachableAs],
                TicketKey = Convert.ToBase64String(key),
            });
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            _log.Warn($"The web bridge could not be started: {ex.GetType().Name}: {ex.Message}");
            Fail(ex is FileNotFoundException
                ? "The web UI isn't part of this installation. Repair or reinstall the app."
                : "The web UI could not be started. Details are in the app's log.");
            return;
        }

        var run = new Run(process, key, port, localOnly, _time.GetUtcNow());
        _run = run;
        // A restart after a crash keeps saying why until it runs again.
        SetState(WebUiStates.Starting, _crashes > 0 ? _error : null);
        _log.Info($"Web bridge started (process {process.ProcessId}), port {port}, {(localOnly ? "this PC only until the firewall rule is there" : "every address")}.");

        using var wait = new CancellationTokenSource();
        var timeout = Task.Delay(StartTimeout, _time, wait.Token);
        var first = await Task.WhenAny(process.Ready, process.Exited, timeout).ConfigureAwait(false);
        await wait.CancelAsync().ConfigureAwait(false);
        if (first == process.Ready)
        {
            run.IsReady = true;
            SetState(WebUiStates.Running, null);
            _log.Info("Web bridge ready.");
            _ = WatchAsync(run);
            return;
        }

        _run = null;
        if (first == process.Exited)
        {
            Ended(run, await process.Exited.ConfigureAwait(false));
            return;
        }

        if (!await StopProcessAsync(run).ConfigureAwait(false)) _lingering.Add(run);
        Fail($"The web UI did not start within {StartTimeout.TotalSeconds.ToString("0", CultureInfo.CurrentCulture)} seconds. Details are in the app's log.");
    }

    /// <param name="endSessions">
    /// Also end the web-role pipe sessions still being answered (off, a new port or password). A restart for other
    /// addresses only leaves that to the bridge's own pipes closing with it.
    /// </param>
    /// <returns>Whether no bridge is left running: false when the one there didn't end.</returns>
    private async Task<bool> StopLockedAsync(bool endSessions = true)
    {
        CancelRestart();
        var stopped = true;
        foreach (var earlier in _lingering.ToArray())
        {
            if (earlier.Process.Exited.IsCompleted || await StopProcessAsync(earlier).ConfigureAwait(false))
            {
                _lingering.Remove(earlier);
                continue;
            }

            stopped = false;
            _log.Info("The web bridge asked to stop earlier has still not ended.");
        }

        if (_run is { } run)
        {
            _run = null;
            var ended = await StopProcessAsync(run).ConfigureAwait(false);
            _log.Info(ended ? "Web bridge stopped." : "The web bridge did not end when asked to.");
            if (!ended) _lingering.Add(run);
            stopped &= ended;
        }

        // The bridge's pipes close with it; this also ends any that are still being answered.
        var closed = endSessions ? CloseWebSessions?.Invoke() ?? 0 : 0;
        if (closed > 0) _log.Info($"Ended {closed} web session(s).");
        if (_state != WebUiStates.Failed || !_config.Enabled) SetState(WebUiStates.Off, null);
        return stopped;
    }

    /// <returns>Whether the bridge has ended.</returns>
    private async Task<bool> StopProcessAsync(Run run)
    {
        try
        {
            await run.Process.StopAsync(StopGrace).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            _log.Warn($"The web bridge did not stop cleanly: {ex.GetType().Name}");
        }

        return run.Process.Exited.IsCompleted;
    }

    /// <summary>After the bridge was ready: an end nobody asked for.</summary>
    private async Task WatchAsync(Run run)
    {
        var exit = await run.Process.Exited.ConfigureAwait(false);
        await _gate.WaitAsync().ConfigureAwait(false);
        try
        {
            if (!ReferenceEquals(_run, run)) return; // stopped on purpose
            _run = null;
            CloseWebSessions?.Invoke();
            Ended(run, exit);
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <summary>The bridge ended by itself, starting or running.</summary>
    private void Ended(Run run, WebBridgeExitInfo exit)
    {
        _log.Warn($"Web bridge ended with {exit.ExitCode}: {exit.Message ?? "(no message)"}");
        switch (exit.ExitCode)
        {
            case WebBridgeExit.PortInUse:
                Fail(exit.Message ?? $"Port {run.Port} is in use by another program. Choose another port.");
                return;
            case WebBridgeExit.PortForbidden:
                Fail(exit.Message ?? $"Windows doesn't allow listening on port {run.Port}. Choose another port.");
                return;
            case WebBridgeExit.Refused:
                Fail(exit.Message ?? "The web UI refused to start. Details are in the app's log.");
                return;
        }

        if (_stopped || !_config.Enabled)
        {
            SetState(WebUiStates.Off, null);
            return;
        }

        if (_time.GetUtcNow() - run.StartedAt > StableRun) _crashes = 0;
        _crashes++;
        if (_crashes > MaxRestarts)
        {
            Fail("The web UI stopped unexpectedly several times. Details are in the app's log.");
            return;
        }

        var delay = TimeSpan.FromTicks(Math.Min(MaxRestartDelay.Ticks, FirstRestartDelay.Ticks << Math.Min(_crashes - 1, 20)));
        SetState(WebUiStates.Starting, "The web UI stopped unexpectedly. Starting it again.");
        var restart = new CancellationTokenSource();
        _restart = restart;
        _ = RestartLaterAsync(delay, restart);
    }

    private async Task RestartLaterAsync(TimeSpan delay, CancellationTokenSource restart)
    {
        try
        {
            await Task.Delay(delay, _time, restart.Token).ConfigureAwait(false);
            await _gate.WaitAsync(restart.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            return;
        }

        try
        {
            if (!ReferenceEquals(_restart, restart) || _stopped || !_config.Enabled) return;
            _restart = null;
            restart.Dispose();
            await StartLockedAsync().ConfigureAwait(false);
        }
        finally
        {
            _gate.Release();
        }
    }

    private void CancelRestart()
    {
        if (_restart is not { } restart) return;
        _restart = null;
        restart.Cancel();
        restart.Dispose();
    }

    private void Fail(string message)
    {
        SetState(WebUiStates.Failed, message);
        _log.Warn($"Web UI: {message}");
        try
        {
            Notice?.Invoke(NoticeTitle, message);
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            _log.Warn($"Web UI notice failed: {ex.GetType().Name}");
        }
    }

    private void SetState(string state, string? error)
    {
        var same = _state == state && _error == error;
        _state = state;
        _error = error;
        if (same) return;
        var status = Status();
        try
        {
            Changed?.Invoke(this, status);
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            _log.Warn($"Web UI change announcement failed: {ex.GetType().Name}");
        }
    }

    /// <summary>The status, announced to the desktop sessions (a setting changed, which no state change may show).</summary>
    private WebUiStatusDto Announce()
    {
        var status = Status();
        try
        {
            Changed?.Invoke(this, status);
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            _log.Warn($"Web UI change announcement failed: {ex.GetType().Name}");
        }

        return status;
    }

    private WebUiStatusDto Status()
    {
        var listening = _run is not null && _state is WebUiStates.Starting or WebUiStates.Running;
        var localOnly = listening && _run!.LocalOnly;
        return new WebUiStatusDto
        {
            Enabled = _config.Enabled,
            Port = _config.Port,
            AlsoReachableAs = [.. _config.AlsoReachableAs],
            HasPassword = SafeHasPassword(),
            State = _state,
            Error = _error,
            LocalOnly = localOnly,
            Firewall = _firewall,
            Addresses = listening && !localOnly ? AddressesFor(_config.Port) : [],
            BridgePath = _platform.Launcher.BridgePath,
            RuleName = WebUiDefaults.RuleName,
        };
    }

    private bool SafeHasPassword()
    {
        try
        {
            return _platform.Passwords.HasPassword();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidDataException)
        {
            return false;
        }
    }

    private List<string> AddressesFor(int port)
    {
        IReadOnlyList<IPAddress> addresses;
        try
        {
            addresses = _platform.Network.Current();
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            _log.Warn($"This PC's network addresses could not be read: {ex.GetType().Name}");
            return [];
        }

        var text = port.ToString(CultureInfo.InvariantCulture);
        return addresses
            .Where(address => !IPAddress.IsLoopback(address))
            .OrderBy(address => address.AddressFamily == AddressFamily.InterNetwork ? 0 : 1)
            .Select(address => address.AddressFamily == AddressFamily.InterNetworkV6 ? $"http://[{address}]:{text}/" : $"http://{address}:{text}/")
            .Distinct(StringComparer.Ordinal)
            .ToList();
    }

    private sealed class Run(IWebBridgeProcess process, byte[] key, int port, bool localOnly, DateTimeOffset startedAt)
    {
        public IWebBridgeProcess Process { get; } = process;
        public byte[] Key { get; } = key;
        public int Port { get; } = port;
        public bool LocalOnly { get; } = localOnly;
        public DateTimeOffset StartedAt { get; } = startedAt;
        public bool IsReady { get; set; }
    }
}

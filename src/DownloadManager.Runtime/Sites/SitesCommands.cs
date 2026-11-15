using System.Text.Json.Nodes;
using DownloadManager.Contracts;
using DownloadManager.Persistence;
using DownloadManager.Runtime.Ipc;
using DownloadManager.Sites;

namespace DownloadManager.Runtime.Sites;

/// <summary>
/// Answers <c>sites.*</c> (SITES.2; remote-sites.md "The switch", "Credentials and trust"). Owns the switch, the saved
/// sites and rclone's supervisor. With Sites off nothing runs: no rclone process, no timer, and only <c>sites.get</c> and
/// <c>sites.setEnabled</c> are answered. Turning Sites off stops rclone; the saved sites and their sealed secrets stay.
/// <para>With Sites on, an rclone Inbound downloaded is checked for updates once a day and, with "Update rclone
/// automatically" on, updated while rclone is idle (remote-sites.md, "rclone updates"). A new version is used from the
/// next rclone start; it counts as proven after its first good use, and one that fails before that is rolled back.</para>
/// </summary>
public sealed class SitesCommands : ISitesCommands, IAsyncDisposable
{
    private const string OffMessage = "Sites is off. Turn it on in Options › Sites.";

    private readonly string _stateDirectory;
    private readonly SitesConfigStore _configStore;
    private readonly SiteStore _sites;
    private readonly Action<string> _log;
    private readonly Func<string?, string?> _findRclone;
    private readonly Func<string, string, RcloneSupervisor> _createSupervisor;
    private readonly Func<string, int, CancellationToken, Task<IReadOnlyList<SshHostKey>>> _probe;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly RcloneUpdater? _updater;
    private readonly TimeProvider _time;
    private readonly TimeSpan _firstCheckDelay;
    private readonly TimeSpan _updateTick;
    private readonly Lock _loopSync = new();
    private CancellationTokenSource? _updateLoop;
    private Task? _updateTask;
    private bool _shippedChecked;
    private int _unprovenFailures;

    /// <summary>A freshly installed rclone that fails this many starts before it ever worked is rolled back.</summary>
    public const int UnprovenStartFailures = 2;

    /// <summary>Checks rclone's server about once a day.</summary>
    public static readonly TimeSpan CheckInterval = TimeSpan.FromHours(23);

    /// <summary>A folder's listing is paged from memory for this long, then read again.</summary>
    public static readonly TimeSpan ListingLifetime = TimeSpan.FromMinutes(1);

    public const int DefaultPageSize = 500;
    public const int MaxPageSize = 2000;

    /// <summary>A folder with more items than this is refused rather than held in the Host's memory.</summary>
    public const int MaxListingItems = 100_000;

    private sealed record Listing(string Token, string Path, IReadOnlyList<SiteItemDto> Items, DateTimeOffset At);

    // The last folder listed per site, dropped after a minute unread: enough for paging, and nothing grows with browsing.
    private readonly Dictionary<string, Listing> _listings = new(StringComparer.Ordinal);

    // Bumped when a site changes, so a listing read before the change is not kept after it.
    private readonly Dictionary<string, int> _generations = new(StringComparer.Ordinal);

    private SitesConfig _config;
    private string? _configProblem;
    private RcloneSupervisor? _supervisor;
    private string? _supervisorFor;
    private string? _rcloneVersion;
    private string? _rcloneProblem;
    private bool _rcloneMissing;

    /// <param name="stateDirectory">The Host's data folder; sites live in <c>sites/</c>, rclone's own state in <c>sites/rclone/</c>.</param>
    /// <param name="findRclone">The rclone to run for an own path (tests); <see cref="RcloneSetup.Find(string?, string?)"/> by default.</param>
    /// <param name="createSupervisor">A supervisor for (rclone, state folder) (tests).</param>
    /// <param name="probe">Reads a server's host keys (tests); <see cref="SshHostKeyProbe.ProbeAllAsync"/> by default.</param>
    /// <param name="updater">Downloads and updates rclone; null where Inbound can't (then only an own or shipped rclone is used).</param>
    /// <param name="firstCheckDelay">How long after Sites starts the first update check waits (two minutes by default).</param>
    /// <param name="updateTick">How often the update loop looks (hourly by default; it asks rclone's server about once a day).</param>
    public SitesCommands(string stateDirectory, Action<string> log, Func<string?, string?>? findRclone = null,
        Func<string, string, RcloneSupervisor>? createSupervisor = null,
        Func<string, int, CancellationToken, Task<IReadOnlyList<SshHostKey>>>? probe = null,
        RcloneUpdater? updater = null, TimeProvider? time = null, TimeSpan? firstCheckDelay = null, TimeSpan? updateTick = null)
    {
        _stateDirectory = stateDirectory;
        _configStore = new SitesConfigStore(stateDirectory);
        _sites = new SiteStore(stateDirectory, log);
        _log = log;
        _updater = updater;
        _time = time ?? TimeProvider.System;
        _firstCheckDelay = firstCheckDelay ?? TimeSpan.FromMinutes(2);
        _updateTick = updateTick ?? TimeSpan.FromHours(1);
        _findRclone = findRclone ?? (own => RcloneSetup.Find(own, downloaded: _updater?.CurrentExecutable));
        _createSupervisor = createSupervisor ?? ((rclone, state) => RcloneSupervisor.ForExecutable(rclone, state, null, log));
        _probe = probe ?? ((host, port, ct) => SshHostKeyProbe.ProbeAllAsync(host, port, null, ct));
        _config = _configStore.Load(out _configProblem);
        if (_configProblem is not null) log(_configProblem);
        if (_config.Enabled) StartUpdateLoop();
    }

    /// <summary>
    /// The rclone updater for a Host: downloads go through <paramref name="handler"/> (the Host's proxied handler, so the
    /// proxy in Options applies). Null on a system rclone has no build for.
    /// </summary>
    public static RcloneUpdater? CreateUpdater(string stateDirectory, HttpMessageHandler handler, Action<string> log)
    {
        if (RclonePlatform.Current is not { } platform) return null;
        var http = new HttpClient(handler) { Timeout = TimeSpan.FromMinutes(15) };
        http.DefaultRequestHeaders.UserAgent.ParseAdd("Inbound-rclone-updater");
        return new RcloneUpdater(stateDirectory, new RcloneDownloads(http), platform, log);
    }

    /// <summary>After the switch, "Use my own rclone", rclone's state or a saved site changed; for <c>sites.changed</c>.</summary>
    public event EventHandler<SitesChangedDto>? Changed;

    public async Task<IpcMessage> HandleAsync(IpcMessage request, CancellationToken cancellationToken)
    {
        switch (request.Type)
        {
            case SitesMessageTypes.Get:
                return IpcMessage.Create(SitesMessageTypes.Status, Status(), request.Id);
            case SitesMessageTypes.SetEnabled:
                return IpcMessage.Create(SitesMessageTypes.Status,
                    await SetEnabledAsync(request.GetPayload<SitesSetEnabledRequest>() ?? new SitesSetEnabledRequest(), cancellationToken).ConfigureAwait(false), request.Id);
        }

        if (!_config.Enabled)
        {
            return request.Type switch
            {
                SitesMessageTypes.ListRequest => IpcMessage.Create(SitesMessageTypes.List, new SitesListDto(), request.Id),
                SitesMessageTypes.KindsRequest => IpcMessage.Create(SitesMessageTypes.Kinds, Kinds(), request.Id),
                SitesMessageTypes.Probe => IpcMessage.Create(SitesMessageTypes.ProbeResult, new SiteProbeResultDto { Ok = false, Message = OffMessage }, request.Id),
                SitesMessageTypes.Browse => IpcMessage.Create(SitesMessageTypes.BrowseResult,
                    new SiteBrowseResultDto { Ok = false, Message = OffMessage, Problem = SiteProblems.Off }, request.Id),
                SitesMessageTypes.RcloneInstall or SitesMessageTypes.RcloneCheck => IpcMessage.Create(SitesMessageTypes.Status, WithError(Status(), OffMessage), request.Id),
                _ => IpcMessage.Create(SitesMessageTypes.Result, new SiteResultDto { Ok = false, Message = OffMessage, Problem = SiteProblems.Off }, request.Id),
            };
        }

        switch (request.Type)
        {
            case SitesMessageTypes.ListRequest:
                return IpcMessage.Create(SitesMessageTypes.List, new SitesListDto { Sites = await ListAsync(cancellationToken).ConfigureAwait(false) }, request.Id);
            case SitesMessageTypes.KindsRequest:
                return IpcMessage.Create(SitesMessageTypes.Kinds, Kinds(), request.Id);
            case SitesMessageTypes.Save:
                return Result(request, await SaveAsync(request.GetPayload<SiteSaveRequest>() ?? new SiteSaveRequest(), cancellationToken).ConfigureAwait(false));
            case SitesMessageTypes.Remove:
                return Result(request, await RemoveAsync(request.GetPayload<SiteIdRequest>()?.Id ?? "", cancellationToken).ConfigureAwait(false));
            case SitesMessageTypes.Probe:
                return IpcMessage.Create(SitesMessageTypes.ProbeResult,
                    await ProbeAsync(request.GetPayload<SiteProbeRequest>() ?? new SiteProbeRequest(), cancellationToken).ConfigureAwait(false), request.Id);
            case SitesMessageTypes.Trust:
                return Result(request, await TrustAsync(request.GetPayload<SiteTrustRequest>() ?? new SiteTrustRequest(), cancellationToken).ConfigureAwait(false));
            case SitesMessageTypes.Test:
                return Result(request, await TestAsync(request.GetPayload<SiteIdRequest>()?.Id ?? "", cancellationToken).ConfigureAwait(false));
            case SitesMessageTypes.Browse:
                return IpcMessage.Create(SitesMessageTypes.BrowseResult,
                    await BrowseAsync(request.GetPayload<SiteBrowseRequest>() ?? new SiteBrowseRequest(), cancellationToken).ConfigureAwait(false), request.Id);
            case SitesMessageTypes.Star:
                return Result(request, await StarAsync(request.GetPayload<SiteStarRequest>() ?? new SiteStarRequest(), cancellationToken).ConfigureAwait(false));
            case SitesMessageTypes.RcloneInstall:
                return IpcMessage.Create(SitesMessageTypes.Status,
                    await InstallRcloneAsync(request.GetPayload<SitesRcloneInstallRequest>()?.Version, cancellationToken).ConfigureAwait(false), request.Id);
            case SitesMessageTypes.RcloneCheck:
                return IpcMessage.Create(SitesMessageTypes.Status, await CheckRcloneAsync(cancellationToken).ConfigureAwait(false), request.Id);
            default:
                return Result(request, new SiteResultDto { Ok = false, Message = $"Unknown Sites request {request.Type}." });
        }

        static IpcMessage Result(IpcMessage request, SiteResultDto result) => IpcMessage.Create(SitesMessageTypes.Result, result, request.Id);
    }

    public SitesStatusDto Status()
    {
        var supervisor = Volatile.Read(ref _supervisor);
        var state = supervisor?.Status;
        return new SitesStatusDto
        {
            Enabled = _config.Enabled,
            OwnRclonePath = _config.OwnRclonePath,
            Rclone = _rcloneProblem is not null && supervisor is null
                ? (_rcloneMissing ? SitesRcloneStates.Missing : SitesRcloneStates.Failed)
                : state?.State switch
            {
                RcloneState.Starting => SitesRcloneStates.Starting,
                RcloneState.Running => SitesRcloneStates.Running,
                RcloneState.Stopping => SitesRcloneStates.Stopping,
                RcloneState.Waiting => SitesRcloneStates.Waiting,
                RcloneState.Failed => SitesRcloneStates.Failed,
                _ => SitesRcloneStates.Off,
            },
            RcloneVersion = _rcloneVersion,
            Error = _configProblem ?? _rcloneProblem ?? state?.Error,
            RcloneSource = SourceOf(Volatile.Read(ref _supervisorFor) ?? _findRclone(_config.OwnRclonePath)),
            RcloneInstalled = _updater?.State.Current,
            AutoUpdateRclone = _config.AutoUpdateRclone,
            RcloneInstalling = _updater?.Busy ?? false,
            RcloneUpdate = _updater?.Offer is { } offer
                ? new SitesRcloneUpdateDto
                {
                    Version = offer.Version.ToString(3),
                    State = offer.State switch { RcloneOfferState.Ready => "ready", RcloneOfferState.Waiting => "waiting", _ => "needsAsk" },
                    WaitUntil = offer.WaitUntil,
                    Note = offer.Note,
                }
                : null,
            RcloneLastCheck = _updater?.State.LastCheck,
            RcloneUpdateError = _updater?.State.LastError,
        };
    }

    private string? SourceOf(string? path)
    {
        if (path is null) return null;
        if (path == _config.OwnRclonePath) return SitesRcloneSources.Own;
        if (path == Environment.GetEnvironmentVariable(RcloneSetup.RcloneVariable)) return SitesRcloneSources.Environment;
        if (_updater is not null && path == _updater.CurrentExecutable) return SitesRcloneSources.Downloaded;
        return SitesRcloneSources.Shipped;
    }

    private async Task<SitesStatusDto> SetEnabledAsync(SitesSetEnabledRequest request, CancellationToken cancellationToken)
    {
        var changed = false;
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var own = request.OwnRclonePath is null ? _config.OwnRclonePath
                : request.OwnRclonePath.Length == 0 ? null
                : request.OwnRclonePath;
            if (own is not null && !Path.IsPathFullyQualified(own))
                return WithError(Status(), "\"Use my own rclone\" needs the full path of the rclone program.");

            var next = _config with { Enabled = request.Enabled, OwnRclonePath = own, AutoUpdateRclone = request.AutoUpdateRclone ?? _config.AutoUpdateRclone };
            changed = next != _config;
            if (changed)
            {
                await _configStore.SaveAsync(next, cancellationToken).ConfigureAwait(false);
                var rcloneChanged = next.OwnRclonePath != _config.OwnRclonePath;
                _config = next;
                _configProblem = null;
                // Off, or another rclone: the running one stops. With Sites on it starts again when something needs it.
                if (!next.Enabled || rcloneChanged) await StopRcloneLockedAsync().ConfigureAwait(false);
                if (!next.Enabled) ForgetAllListings();
                if (next.Enabled != _config.Enabled || changed) _log(next.Enabled ? "Sites settings saved; Sites is on." : "Sites turned off; rclone stopped.");
            }
        }
        finally
        {
            _gate.Release();
        }

        if (_config.Enabled) StartUpdateLoop();
        else await StopUpdateLoopAsync().ConfigureAwait(false);
        if (changed) await RaiseChangedAsync(cancellationToken).ConfigureAwait(false);
        return Status();
    }

    private async Task<SitesStatusDto> InstallRcloneAsync(string? versionText, CancellationToken cancellationToken)
    {
        if (_updater is null) return WithError(Status(), "This Inbound can't download rclone; choose your own rclone in Options › Sites.");
        if (_config.OwnRclonePath is not null)
            return WithError(Status(), "You chose your own rclone in Options › Sites; Inbound does not download or update it.");
        Version? version = null;
        if (!string.IsNullOrWhiteSpace(versionText) && (!Version.TryParse(versionText, out version) || version.Build < 0 || version.Revision >= 0))
            return WithError(Status(), "That is not an rclone version (like 1.76.0).");

        var install = _updater.InstallAsync(version, cancellationToken);
        await RaiseChangedAsync(CancellationToken.None).ConfigureAwait(false); // shows "installing"
        string? problem = null;
        try
        {
            await install.ConfigureAwait(false);
        }
        catch (RcloneReleaseException e)
        {
            problem = e.Message;
            _log($"rclone was not installed: {e.Message}");
        }

        await SwapIfIdleAsync(cancellationToken).ConfigureAwait(false);
        await RaiseChangedAsync(CancellationToken.None).ConfigureAwait(false);
        return problem is null ? Status() : WithError(Status(), problem);
    }

    private async Task<SitesStatusDto> CheckRcloneAsync(CancellationToken cancellationToken)
    {
        if (_updater is null) return WithError(Status(), "This Inbound can't download rclone.");
        string? problem = null;
        try
        {
            await _updater.CheckAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (RcloneReleaseException e)
        {
            problem = e.Message;
        }

        await RaiseChangedAsync(cancellationToken).ConfigureAwait(false);
        return problem is null ? Status() : WithError(Status(), problem);
    }

    /// <summary>
    /// One look of the update loop (tests call it directly): an rclone shipped with this build retires downloaded ones at
    /// or below it; rclone's server is asked about once a day; a ready update installs while rclone is idle when
    /// automatic updates are on. Never with Sites off or with the user's own rclone.
    /// </summary>
    public async Task UpdateTickAsync(CancellationToken cancellationToken)
    {
        if (!_config.Enabled || _updater is null || _config.OwnRclonePath is not null) return;
        if (!_shippedChecked)
        {
            _shippedChecked = true;
            if (RcloneSetup.Shipped() is { } shipped
                && (await RcloneSetup.ReadVersionAsync(shipped, cancellationToken).ConfigureAwait(false)).Version is { } shippedVersion)
                await _updater.RemoveAtOrBelowAsync(shippedVersion, cancellationToken).ConfigureAwait(false);
        }

        var changed = false;
        if (_updater.State.LastCheck is not { } last || _time.GetUtcNow() - last >= CheckInterval)
        {
            try
            {
                await _updater.CheckAsync(cancellationToken).ConfigureAwait(false);
            }
            catch (RcloneReleaseException)
            {
                // Logged and kept by the updater; tried again at the next check.
            }

            changed = true;
        }

        // Updates replace an rclone Inbound manages: one it downloaded, or one shipped with this build (the Docker
        // image). Never a first download on the desktop: that is the user's "Download rclone".
        if (_config.AutoUpdateRclone && _updater.Offer is { State: RcloneOfferState.Ready } offer && RcloneIdle()
            && SourceOf(_findRclone(_config.OwnRclonePath)) is SitesRcloneSources.Downloaded or SitesRcloneSources.Shipped)
        {
            try
            {
                await _updater.InstallAsync(offer.Version, cancellationToken).ConfigureAwait(false);
                await SwapIfIdleAsync(cancellationToken).ConfigureAwait(false);
            }
            catch (RcloneReleaseException e)
            {
                _log($"rclone {offer.Version.ToString(3)} was not installed: {e.Message}");
            }

            changed = true;
        }

        if (changed) await RaiseChangedAsync(cancellationToken).ConfigureAwait(false);
    }

    private bool RcloneIdle() => Volatile.Read(ref _supervisor) is not { } supervisor || supervisor.Status.State == RcloneState.Off;

    /// <summary>After an install: an idle rclone of the old version is let go, so the next use starts the new one.</summary>
    private async Task SwapIfIdleAsync(CancellationToken cancellationToken)
    {
        Interlocked.Exchange(ref _unprovenFailures, 0); // a new version gets its own two tries
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (_supervisor is null)
            {
                _rcloneProblem = null;
                _rcloneMissing = false;
            }
            else if (_supervisor.Status.State == RcloneState.Off && _supervisorFor != _findRclone(_config.OwnRclonePath))
            {
                await StopRcloneLockedAsync().ConfigureAwait(false);
            }
        }
        finally
        {
            _gate.Release();
        }
    }

    private void StartUpdateLoop()
    {
        if (_updater is null) return;
        lock (_loopSync)
        {
            if (_updateLoop is not null) return;
            var loop = new CancellationTokenSource();
            _updateLoop = loop;
            _updateTask = Task.Run(() => UpdateLoopAsync(loop.Token));
        }
    }

    private async Task StopUpdateLoopAsync()
    {
        CancellationTokenSource? loop;
        Task? task;
        lock (_loopSync)
        {
            (loop, task) = (_updateLoop, _updateTask);
            (_updateLoop, _updateTask) = (null, null);
        }

        if (loop is null) return;
        await loop.CancelAsync().ConfigureAwait(false);
        try
        {
            if (task is not null) await task.ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
        }

        loop.Dispose();
    }

    private async Task UpdateLoopAsync(CancellationToken cancellationToken)
    {
        await Task.Delay(_firstCheckDelay, _time, cancellationToken).ConfigureAwait(false);
        while (true)
        {
            try
            {
                await UpdateTickAsync(cancellationToken).ConfigureAwait(false);
            }
            catch (Exception e) when (e is not OperationCanceledException)
            {
                _log($"rclone update check failed: {e.GetType().Name}: {e.Message}");
            }

            await Task.Delay(_updateTick, _time, cancellationToken).ConfigureAwait(false);
        }
    }

    private async Task<List<SiteDto>> ListAsync(CancellationToken cancellationToken) =>
        [.. (await _sites.ListAsync(cancellationToken).ConfigureAwait(false)).Select(ToDto)];

    private async Task<SiteResultDto> SaveAsync(SiteSaveRequest request, CancellationToken cancellationToken)
    {
        SiteRecord saved;
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            SiteRecord? existing = null;
            if (!string.IsNullOrEmpty(request.Id))
            {
                if (!SiteStore.IsId(request.Id)) return Fail("That is not a site id.");
                existing = await _sites.GetAsync(request.Id, cancellationToken).ConfigureAwait(false);
                if (existing is null) return Fail("That site no longer exists.");
            }

            if (request.AuthMethod is not (SiteAuthMethods.Password or SiteAuthMethods.Key))
                return Fail("Choose a password or a key file to sign in with.");

            var host = request.Host.Trim();
            var now = DateTimeOffset.UtcNow;
            // Another server is another key: a changed host or port forgets the accepted ones, and the window asks again.
            var sameServer = existing is not null && string.Equals(existing.Host, host, StringComparison.OrdinalIgnoreCase) && existing.Port == request.Port;
            try
            {
                saved = new SiteRecord
                {
                    Id = existing?.Id ?? SiteStore.NewId(),
                    Name = request.Name.Trim(),
                    Kind = request.Kind,
                    Host = host,
                    Port = request.Port,
                    User = request.User.Trim(),
                    AuthMethod = request.AuthMethod,
                    SealedPassword = Seal(request.Password, existing?.SealedPassword),
                    SealedPrivateKey = Seal(request.PrivateKey, existing?.SealedPrivateKey),
                    SealedKeyPassphrase = Seal(request.KeyPassphrase, existing?.SealedKeyPassphrase),
                    HostKeys = sameServer ? existing!.HostKeys : [],
                    RemoteFolder = request.RemoteFolder.Trim(),
                    LocalFolder = string.IsNullOrWhiteSpace(request.LocalFolder) ? null : request.LocalFolder.Trim(),
                    // Stars are folders on the same server; another server forgets them with its keys.
                    Stars = sameServer ? existing!.Stars : [],
                    CreatedAt = existing?.CreatedAt ?? now,
                    ModifiedAt = now,
                };
                if (saved.AuthMethod == SiteAuthMethods.Key && saved.SealedPrivateKey is null)
                    return Fail("Choose the private key to sign in with.");
                await _sites.SaveAsync(saved, cancellationToken).ConfigureAwait(false);
            }
            catch (SiteStoreException e)
            {
                return Fail(e.Message);
            }
            catch (InvalidOperationException e)
            {
                // No secret protector on this system (SecretProtector.Protect): nothing was written.
                return Fail(e.Message);
            }
        }
        finally
        {
            _gate.Release();
        }

        ForgetListings(saved.Id);
        await RaiseChangedAsync(cancellationToken).ConfigureAwait(false);
        return new SiteResultDto { Ok = true, Site = ToDto(saved) };
    }

    private async Task<SiteResultDto> RemoveAsync(string id, CancellationToken cancellationToken)
    {
        bool removed;
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            removed = SiteStore.IsId(id) && await _sites.RemoveAsync(id, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _gate.Release();
        }

        ForgetListings(id);
        if (!removed) return Fail("That site no longer exists.");
        await RaiseChangedAsync(cancellationToken).ConfigureAwait(false);
        return new SiteResultDto { Ok = true };
    }

    private async Task<SiteProbeResultDto> ProbeAsync(SiteProbeRequest request, CancellationToken cancellationToken)
    {
        var host = request.Host.Trim();
        if (host.Length == 0 || request.Port is < 1 or > 65535) return new SiteProbeResultDto { Ok = false, Message = "Enter the server's name or address and port." };
        try
        {
            var keys = await _probe(host, request.Port, cancellationToken).ConfigureAwait(false);
            return new SiteProbeResultDto { Ok = true, Keys = [.. keys.Select(ToDto)] };
        }
        catch (SshProbeException e)
        {
            return new SiteProbeResultDto { Ok = false, Message = e.Message };
        }
    }

    private async Task<SiteResultDto> TrustAsync(SiteTrustRequest request, CancellationToken cancellationToken)
    {
        SiteRecord saved;
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (!SiteStore.IsId(request.Id) || await _sites.GetAsync(request.Id, cancellationToken).ConfigureAwait(false) is not { } site)
                return Fail("That site no longer exists.");
            if (request.Entries.Count is 0 or > 16) return Fail("Accept between 1 and 16 of the server's keys.");
            saved = site with { HostKeys = [.. request.Entries.Distinct(StringComparer.Ordinal)], ModifiedAt = DateTimeOffset.UtcNow };
            try
            {
                await _sites.SaveAsync(saved, cancellationToken).ConfigureAwait(false);
            }
            catch (SiteStoreException e)
            {
                return Fail(e.Message);
            }
        }
        finally
        {
            _gate.Release();
        }

        ForgetListings(saved.Id);
        await RaiseChangedAsync(cancellationToken).ConfigureAwait(false);
        return new SiteResultDto { Ok = true, Site = ToDto(saved) };
    }

    private async Task<SiteResultDto> TestAsync(string id, CancellationToken cancellationToken)
    {
        if (!SiteStore.IsId(id) || await _sites.GetAsync(id, cancellationToken).ConfigureAwait(false) is not { } site)
            return Fail("That site no longer exists.");
        var path = SitePaths.Normalize(site.RemoteFolder);
        if (path is null) return Fail("The site's remote folder is not a usable path; change it in Edit site.");
        var (items, failure) = await ListFolderAsync(site, path, cancellationToken).ConfigureAwait(false);
        if (failure is not null) return failure;
        var count = items!.Count;
        return new SiteResultDto { Ok = true, Message = $"Connected. The folder holds {count} item{(count == 1 ? "" : "s")}." };
    }

    /// <summary>
    /// One folder, sorted folders first then by name, paged (SITES.3). A page after the first comes from the listing the
    /// Host read for that folder in the last <see cref="ListingLifetime"/>, so the pages of one folder agree with each other.
    /// </summary>
    private async Task<SiteBrowseResultDto> BrowseAsync(SiteBrowseRequest request, CancellationToken cancellationToken)
    {
        if (!SiteStore.IsId(request.Id) || await _sites.GetAsync(request.Id, cancellationToken).ConfigureAwait(false) is not { } site)
            return new SiteBrowseResultDto { Ok = false, Message = "That site no longer exists.", Path = request.Path ?? "" };
        var fromSite = string.IsNullOrEmpty(request.Path);
        var path = SitePaths.Normalize(fromSite ? site.RemoteFolder : request.Path);
        if (path is null)
        {
            return new SiteBrowseResultDto
            {
                Ok = false,
                Path = request.Path ?? "",
                Message = fromSite ? "The site's folder on the server is not a usable path; change it in Edit site." : "That is not a folder path on the server.",
            };
        }

        var limit = request.Limit is < 1 ? DefaultPageSize : Math.Min(request.Limit, MaxPageSize);
        var offset = Math.Max(0, request.Offset);

        Listing listing;
        if (offset > 0 && !request.Refresh)
        {
            // A later page comes from the first page's listing or not at all: a fresh listing sliced at the old offset could
            // repeat or skip items.
            if (CachedListing(site.Id, path, request.Listing) is not { } cached)
                return new SiteBrowseResultDto { Ok = false, Problem = SiteProblems.ListingChanged, Path = path, Message = "The folder is read again." };
            listing = cached;
        }
        else
        {
            var generation = GenerationOf(site.Id);
            var (listed, failure) = await ListFolderAsync(site, path, cancellationToken).ConfigureAwait(false);
            if (failure is not null)
            {
                return new SiteBrowseResultDto
                {
                    Ok = false,
                    Message = failure.Message,
                    Problem = failure.Problem,
                    OfferedFingerprint = failure.OfferedFingerprint,
                    AcceptedFingerprints = failure.AcceptedFingerprints,
                    Path = path,
                };
            }

            listing = new Listing(Guid.NewGuid().ToString("N"), path, listed!, _time.GetUtcNow());
            StoreListing(site.Id, generation, listing);
        }

        return new SiteBrowseResultDto
        {
            Ok = true,
            Path = path,
            Total = listing.Items.Count,
            Offset = offset,
            Listing = listing.Token,
            Items = [.. listing.Items.Skip(offset).Take(limit)],
        };
    }

    /// <summary>The site's listing of that folder when it is the one asked for and still fresh; each page read keeps it a minute longer.</summary>
    private Listing? CachedListing(string siteId, string path, string? token)
    {
        lock (_listings)
        {
            DropExpiredListingsLocked();
            if (!_listings.TryGetValue(siteId, out var listing) || listing.Path != path || listing.Token != token) return null;
            _listings[siteId] = listing with { At = _time.GetUtcNow() };
            return listing;
        }
    }

    private int GenerationOf(string siteId)
    {
        lock (_listings) return _generations.GetValueOrDefault(siteId);
    }

    /// <summary>
    /// Keeps a listing unless the site changed (saved, trusted, removed) while it was read: one read from the old server or
    /// with the old login must not be paged after the change.
    /// </summary>
    private void StoreListing(string siteId, int generation, Listing listing)
    {
        lock (_listings)
        {
            DropExpiredListingsLocked();
            if (_generations.GetValueOrDefault(siteId) == generation) _listings[siteId] = listing;
        }
    }

    private void DropExpiredListingsLocked()
    {
        var now = _time.GetUtcNow();
        foreach (var (id, listing) in _listings.ToArray())
            if (now - listing.At >= ListingLifetime) _listings.Remove(id);
    }

    private void ForgetListings(string siteId)
    {
        lock (_listings)
        {
            _listings.Remove(siteId);
            _generations[siteId] = _generations.GetValueOrDefault(siteId) + 1;
        }
    }

    private void ForgetAllListings()
    {
        lock (_listings)
        {
            foreach (var id in _listings.Keys.ToArray()) _generations[id] = _generations.GetValueOrDefault(id) + 1;
            _listings.Clear();
        }
    }

    /// <summary>Lists one folder through rclone: the items sorted, or the failure as <c>sites.test</c> reports it.</summary>
    private async Task<(IReadOnlyList<SiteItemDto>? Items, SiteResultDto? Failure)> ListFolderAsync(SiteRecord site, string path, CancellationToken cancellationToken)
    {
        if (site.HostKeys.Count == 0)
            return (null, new SiteResultDto { Ok = false, Problem = SiteProblems.HostKeyNotAccepted, Message = "The server's key has not been accepted yet." });

        RcloneLease lease;
        try
        {
            lease = await AcquireAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (RcloneUnavailableException e)
        {
            return (null, Fail(e.Message));
        }

        using (lease)
        {
            try
            {
                var fs = await SiteRemote.BuildAsync(site, lease.Client, cancellationToken).ConfigureAwait(false);
                var listing = await lease.Client.CallAsync("operations/list", new JsonObject
                {
                    ["fs"] = fs,
                    ["remote"] = SitePaths.ToRemote(path),
                    // Without MIME types rclone need not look at each file's name; Sites shows none.
                    ["opt"] = new JsonObject { ["noMimeType"] = true },
                }, cancellationToken).ConfigureAwait(false);
                if (listing["list"] is JsonArray { Count: > MaxListingItems } big)
                    return (null, Fail($"The folder holds {big.Count:N0} items, more than Sites can show ({MaxListingItems:N0})."));
                return (SiteListing.Read(listing), null);
            }
            catch (RcloneCallException e) when (SshHostKeyProblem.Parse(e.Message) is { } problem)
            {
                return (null, new SiteResultDto
                {
                    Ok = false,
                    Problem = SiteProblems.HostKeyChanged,
                    Message = problem.Kind == SshHostKeyProblemKind.Changed
                        ? "The server's key has changed. This can mean the connection is being intercepted. Do not continue unless the server's owner confirms the new key."
                        : "The server no longer offers the kind of key that was accepted. Do not continue unless the server's owner confirms the change.",
                    OfferedFingerprint = problem.OfferedFingerprint,
                    AcceptedFingerprints = [.. problem.PinnedFingerprints],
                });
            }
            catch (Exception e) when (e is RcloneCallException or RcloneUnavailableException or SiteStoreException)
            {
                // RcloneClient has already redacted the text.
                return (null, Fail(e.Message));
            }
        }
    }

    private async Task<SiteResultDto> StarAsync(SiteStarRequest request, CancellationToken cancellationToken)
    {
        SiteRecord saved;
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (!SiteStore.IsId(request.Id) || await _sites.GetAsync(request.Id, cancellationToken).ConfigureAwait(false) is not { } site)
                return Fail("That site no longer exists.");
            if (string.IsNullOrEmpty(request.Path) || SitePaths.Normalize(request.Path) is not { } path || !SitePaths.IsStarrable(path))
                return Fail("That folder can't be starred.");
            // Starring a starred folder again keeps its place.
            var stars = site.Stars.Where(s => s != path).ToList();
            if (request.Starred) stars = site.Stars.Contains(path) ? [.. site.Stars] : [.. site.Stars, path];
            if (stars.Count > SitePaths.MaxStars) return Fail($"A site can have at most {SitePaths.MaxStars} starred folders.");
            if (stars.SequenceEqual(site.Stars)) return new SiteResultDto { Ok = true, Site = ToDto(site) };
            saved = site with { Stars = stars, ModifiedAt = DateTimeOffset.UtcNow };
            try
            {
                await _sites.SaveAsync(saved, cancellationToken).ConfigureAwait(false);
            }
            catch (SiteStoreException e)
            {
                return Fail(e.Message);
            }
        }
        finally
        {
            _gate.Release();
        }

        await RaiseChangedAsync(cancellationToken).ConfigureAwait(false);
        return new SiteResultDto { Ok = true, Site = ToDto(saved) };
    }

    /// <summary>
    /// A lease on rclone, starting it if needed. The first time for an rclone the version is checked: Sites has SFTP only
    /// for now, so one older than <see cref="RcloneSetup.MinimumForSftp"/> is refused.
    /// </summary>
    private async Task<RcloneLease> AcquireAsync(CancellationToken cancellationToken)
    {
        RcloneSupervisor supervisor;
        string path;
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            // An update installed while rclone ran: the old one is let go once idle, and the new one starts here.
            if (_supervisor is { Status.State: RcloneState.Off } && _supervisorFor != _findRclone(_config.OwnRclonePath))
                await StopRcloneLockedAsync().ConfigureAwait(false);

            if (_supervisor is null)
            {
                var rclone = _findRclone(_config.OwnRclonePath);
                if (rclone is not null && await RollBackIfBrokenAsync(rclone, cancellationToken).ConfigureAwait(false))
                    rclone = _findRclone(_config.OwnRclonePath);
                if (rclone is null)
                {
                    _rcloneProblem = _config.OwnRclonePath is not null ? "The rclone chosen in Options › Sites was not found."
                        : _updater is not null ? "rclone is not on this PC yet. Download it in Options › Sites, or choose your own rclone."
                        : "rclone is not on this PC yet. Choose your own rclone in Options › Sites.";
                    _rcloneMissing = true;
                    throw new RcloneUnavailableException(_rcloneProblem);
                }

                var (version, problem) = await RcloneSetup.ReadVersionAsync(rclone, cancellationToken).ConfigureAwait(false);
                problem ??= version is null ? "rclone's version could not be read." : RcloneSetup.TooOldForSftp(version);
                if (problem is not null)
                {
                    // Found but unusable (too old, not rclone, no answer): failed with the reason, not missing.
                    _rcloneProblem = problem;
                    _rcloneMissing = false;
                    throw new RcloneUnavailableException(problem);
                }

                _rcloneVersion = version!.ToString(3);
                _rcloneProblem = null;
                _supervisor = _createSupervisor(rclone, Path.Combine(_stateDirectory, "sites", "rclone"));
                _supervisorFor = rclone;
                _supervisor.Changed += OnSupervisorChanged;
            }

            supervisor = _supervisor;
            path = _supervisorFor!;
        }
        finally
        {
            _gate.Release();
        }

        var unproven = _updater is not null && path == _updater.CurrentExecutable && !_updater.State.Proven;
        RcloneLease lease;
        try
        {
            lease = await supervisor.AcquireAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (RcloneUnavailableException e) when (unproven && !cancellationToken.IsCancellationRequested
                                                   && (Interlocked.Increment(ref _unprovenFailures) >= UnprovenStartFailures || supervisor.Status.State == RcloneState.Failed))
        {
            // A freshly installed rclone that fails twice before it ever worked: back to the one before.
            Interlocked.Exchange(ref _unprovenFailures, 0);
            if (await _updater!.RollBackAsync(e.Message, CancellationToken.None).ConfigureAwait(false))
            {
                await _gate.WaitAsync(CancellationToken.None).ConfigureAwait(false);
                try
                {
                    if (ReferenceEquals(_supervisor, supervisor)) await StopRcloneLockedAsync().ConfigureAwait(false);
                }
                finally
                {
                    _gate.Release();
                }

                await RaiseChangedAsync(CancellationToken.None).ConfigureAwait(false);
            }

            throw;
        }

        if (unproven)
        {
            Interlocked.Exchange(ref _unprovenFailures, 0);
            await _updater!.MarkProvenAsync(CancellationToken.None).ConfigureAwait(false);
            await RaiseChangedAsync(CancellationToken.None).ConfigureAwait(false);
        }

        return lease;
    }

    /// <summary>A freshly installed rclone that can't even report its version is rolled back before it is used.</summary>
    private async Task<bool> RollBackIfBrokenAsync(string rclone, CancellationToken cancellationToken)
    {
        if (_updater is null || rclone != _updater.CurrentExecutable || _updater.State.Proven) return false;
        var (version, problem) = await RcloneSetup.ReadVersionAsync(rclone, cancellationToken).ConfigureAwait(false);
        if (version is not null) return false;
        return await _updater.RollBackAsync(problem ?? "its version could not be read", cancellationToken).ConfigureAwait(false);
    }

    private void OnSupervisorChanged(object? sender, RcloneStatus status) => _ = RaiseChangedAsync(CancellationToken.None);

    private async Task StopRcloneLockedAsync()
    {
        var supervisor = _supervisor;
        _supervisor = null;
        _supervisorFor = null;
        _rcloneVersion = null;
        _rcloneProblem = null;
        _rcloneMissing = false;
        if (supervisor is null) return;
        supervisor.Changed -= OnSupervisorChanged;
        await supervisor.DisposeAsync().ConfigureAwait(false);
    }

    /// <summary>On the Host's exit.</summary>
    public async ValueTask DisposeAsync()
    {
        await StopUpdateLoopAsync().ConfigureAwait(false);
        await _gate.WaitAsync().ConfigureAwait(false);
        try
        {
            await StopRcloneLockedAsync().ConfigureAwait(false);
        }
        finally
        {
            _gate.Release();
        }
    }

    private async Task RaiseChangedAsync(CancellationToken cancellationToken)
    {
        if (Changed is null) return;
        try
        {
            var dto = new SitesChangedDto
            {
                Status = Status(),
                Sites = _config.Enabled ? await ListAsync(cancellationToken).ConfigureAwait(false) : [],
            };
            Changed?.Invoke(this, dto);
        }
        catch (Exception e)
        {
            _log($"Sites change notice failed: {e.Message}");
        }
    }

    private static string? Seal(string? given, string? stored) => given switch
    {
        null => stored,
        "" => null,
        _ => SecretProtector.ProtectSiteSecret(given),
    };

    private static SiteKindsDto Kinds() => new()
    {
        Kinds = [new SiteKindDto { Kind = SiteKinds.Sftp, Title = "SFTP", DefaultPort = 22, AuthMethods = [SiteAuthMethods.Password, SiteAuthMethods.Key] }],
    };

    private static SiteDto ToDto(SiteRecord site) => new()
    {
        Id = site.Id,
        Name = site.Name,
        Kind = site.Kind,
        Host = site.Host,
        Port = site.Port,
        User = site.User,
        AuthMethod = site.AuthMethod,
        HasPassword = site.SealedPassword is not null,
        HasPrivateKey = site.SealedPrivateKey is not null,
        HasKeyPassphrase = site.SealedKeyPassphrase is not null,
        HostKeys = [.. site.HostKeys.Select(EntryToDto).OfType<SiteHostKeyDto>()],
        RemoteFolder = site.RemoteFolder,
        LocalFolder = site.LocalFolder,
        Stars = [.. site.Stars],
    };

    private static SiteHostKeyDto ToDto(SshHostKey key) => new() { Type = key.KeyType, Fingerprint = key.Fingerprint, Entry = key.HostKeysEntry };

    private static SiteHostKeyDto? EntryToDto(string entry)
    {
        var parts = entry.Split(' ');
        if (parts.Length != 2) return null;
        try
        {
            return ToDto(new SshHostKey(parts[0], Convert.FromBase64String(parts[1])));
        }
        catch (FormatException)
        {
            return null;
        }
    }

    private static SiteResultDto Fail(string message) => new() { Ok = false, Message = message };

    private static SitesStatusDto WithError(SitesStatusDto status, string error)
    {
        status.Error = error;
        return status;
    }
}

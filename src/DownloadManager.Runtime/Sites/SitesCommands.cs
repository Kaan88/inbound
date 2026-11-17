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
public sealed partial class SitesCommands : ISitesCommands, IAsyncDisposable
{
    private const string OffMessage = "Remote files is off. Turn it on in Options › Remote files.";

    private readonly string _stateDirectory;
    private readonly SitesConfigStore _configStore;
    private readonly SiteStore _sites;
    private readonly Action<string> _log;
    private readonly Func<string?, string?> _findRclone;
    private readonly Func<string, string, RcloneSupervisor> _createSupervisor;
    private readonly Func<string, int, CancellationToken, Task<IReadOnlyList<SshHostKey>>> _probe;
    private readonly Func<SiteRecord, CancellationToken, Task<TlsCertificateInfo>> _certificateProbe = ProbeCertificateAsync;

    /// <summary>SITES.6c: the OneDrive a token opens (Graph <c>/me/drive</c>); tests give their own.</summary>
    internal Func<string, CancellationToken, Task<(string? Id, string? Type, string? Problem)>> _oneDriveDrive = ReadOneDriveDriveAsync;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly RcloneUpdater? _updater;
    private readonly SiteTransfers _transfers;
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
    /// <param name="transferRetryDelay">The wait before a failed file's second try (tests); 2 s by default.</param>
    /// <param name="sleepPrevention">Keeps the PC awake while a transfer runs: a share of the Host's power request.</param>
    /// <param name="preventSleep">Options › "Prevent sleep while downloading".</param>
    public SitesCommands(string stateDirectory, Action<string> log, Func<string?, string?>? findRclone = null,
        Func<string, string, RcloneSupervisor>? createSupervisor = null,
        Func<string, int, CancellationToken, Task<IReadOnlyList<SshHostKey>>>? probe = null,
        RcloneUpdater? updater = null, TimeProvider? time = null, TimeSpan? firstCheckDelay = null, TimeSpan? updateTick = null,
        TimeSpan? transferRetryDelay = null, DownloadManager.Core.ISleepPrevention? sleepPrevention = null, Func<bool>? preventSleep = null)
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
        _transfers = new SiteTransfers(stateDirectory, (id, ct) => _sites.GetAsync(id, ct), AcquireAsync,
            () => _config.Enabled, () => _config.TransfersAtOnce, log, _time, transferRetryDelay, sleepPrevention, preventSleep);
        _transfers.Changed += change => TransfersChanged?.Invoke(this, change);
        _transfers.Reached += (siteId, ok) => SetReach(siteId, ok ? SiteReach.Ok : SiteReach.Trouble);
        if (_config.Enabled)
        {
            StartUpdateLoop();
            // Batches that were running when the Host stopped go on.
            _transfers.Pump();
        }
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
    /// <summary>The transfer runner, for the Host to share the overall speed limit with it.</summary>
    public SiteTransfers Transfers => _transfers;

    public event EventHandler<SitesChangedDto>? Changed;

    /// <summary>A batch changed or moved bytes; for <c>sites.transfer.changed</c>.</summary>
    public event EventHandler<SiteTransfersDto>? TransfersChanged;

    public async Task<IpcMessage> HandleAsync(IpcMessage request, CancellationToken cancellationToken)
    {
        switch (request.Type)
        {
            case SitesMessageTypes.Get:
                return IpcMessage.Create(SitesMessageTypes.Status, Status(), request.Id);
            case SitesMessageTypes.SetEnabled:
                return IpcMessage.Create(SitesMessageTypes.Status,
                    await SetEnabledAsync(request.GetPayload<SitesSetEnabledRequest>() ?? new SitesSetEnabledRequest(), cancellationToken).ConfigureAwait(false), request.Id);
            // The batches stay readable with Sites off: they were paused when it was turned off.
            case SitesMessageTypes.TransferList:
                return IpcMessage.Create(SitesMessageTypes.TransferListResult, _transfers.List(), request.Id);
            case SitesMessageTypes.TransferGet:
                return IpcMessage.Create(SitesMessageTypes.TransferResult, _transfers.Get(TransferId(request)), request.Id);
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
                SitesMessageTypes.Here => IpcMessage.Create(SitesMessageTypes.HereResult,
                    new SiteHereResultDto { Ok = false, Message = OffMessage, Problem = SiteProblems.Off }, request.Id),
                SitesMessageTypes.Count => IpcMessage.Create(SitesMessageTypes.CountResult,
                    new SiteCountResultDto { Ok = false, Message = OffMessage, Problem = SiteProblems.Off }, request.Id),
                SitesMessageTypes.Delete => IpcMessage.Create(SitesMessageTypes.ChangeResult,
                    new SiteChangeResultDto { Ok = false, Message = OffMessage, Problem = SiteProblems.Off }, request.Id),
                SitesMessageTypes.Preview => IpcMessage.Create(SitesMessageTypes.PreviewResult,
                    new SitePreviewDto { Ok = false, Message = OffMessage, Problem = SiteProblems.Off }, request.Id),
                SitesMessageTypes.SyncPlan => IpcMessage.Create(SitesMessageTypes.SyncPlanResult,
                    new SiteSyncPlanDto { Ok = false, Message = OffMessage, Problem = SiteProblems.Off }, request.Id),
                SitesMessageTypes.SyncRun or SitesMessageTypes.TransferSchedule or SitesMessageTypes.TransferStart or SitesMessageTypes.TransferPause or SitesMessageTypes.TransferResume or SitesMessageTypes.TransferCancel
                    or SitesMessageTypes.TransferRetry or SitesMessageTypes.TransferClear or SitesMessageTypes.TransferStopFile or SitesMessageTypes.TransferStartFile => IpcMessage.Create(SitesMessageTypes.TransferResult,
                    new SiteTransferResultDto { Ok = false, Message = OffMessage, Problem = SiteProblems.Off }, request.Id),
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
            case SitesMessageTypes.Certificate:
                return IpcMessage.Create(SitesMessageTypes.CertificateResult,
                    await CertificateAsync(request.GetPayload<SiteIdRequest>()?.Id ?? "", cancellationToken).ConfigureAwait(false), request.Id);
            case SitesMessageTypes.DeviceReadOpen or SitesMessageTypes.DeviceWriteOpen:
                return IpcMessage.Create(SitesMessageTypes.DeviceStream, await DeviceOpenAsync(request.GetPayload<SiteDeviceOpenRequest>() ?? new SiteDeviceOpenRequest(),
                    request.Type == SitesMessageTypes.DeviceWriteOpen, cancellationToken).ConfigureAwait(false), request.Id);
            case SitesMessageTypes.DeviceRead:
                return IpcMessage.Create(SitesMessageTypes.DeviceChunk,
                    await DeviceReadAsync(request.GetPayload<SiteDeviceChunkRequest>() ?? new SiteDeviceChunkRequest(), cancellationToken).ConfigureAwait(false), request.Id);
            case SitesMessageTypes.DeviceWrite:
                return IpcMessage.Create(SitesMessageTypes.DeviceChunk,
                    await DeviceWriteAsync(request.GetPayload<SiteDeviceChunkRequest>() ?? new SiteDeviceChunkRequest(), cancellationToken).ConfigureAwait(false), request.Id);
            case SitesMessageTypes.DeviceClose:
                return Result(request, await DeviceCloseAsync(request.GetPayload<SiteDeviceChunkRequest>()?.StreamId ?? "").ConfigureAwait(false));
            case SitesMessageTypes.SignIn:
                return IpcMessage.Create(SitesMessageTypes.SignInResult,
                    await SignInAsync(request.GetPayload<SiteSignInRequest>() ?? new SiteSignInRequest(), cancellationToken).ConfigureAwait(false), request.Id);
            case SitesMessageTypes.SignInCancel:
                return Result(request, await SignInCancelAsync(request.GetPayload<SiteIdRequest>()?.Id ?? "").ConfigureAwait(false));
            case SitesMessageTypes.Test:
                return Result(request, await TestAsync(request.GetPayload<SiteIdRequest>()?.Id ?? "", cancellationToken).ConfigureAwait(false));
            case SitesMessageTypes.TestDraft:
                return Result(request, await TestDraftAsync(request.GetPayload<SiteTestDraftRequest>() ?? new SiteTestDraftRequest(), cancellationToken).ConfigureAwait(false));
            case SitesMessageTypes.Browse:
                return IpcMessage.Create(SitesMessageTypes.BrowseResult,
                    await BrowseAsync(request.GetPayload<SiteBrowseRequest>() ?? new SiteBrowseRequest(), cancellationToken).ConfigureAwait(false), request.Id);
            case SitesMessageTypes.Star:
                return Result(request, await StarAsync(request.GetPayload<SiteStarRequest>() ?? new SiteStarRequest(), cancellationToken).ConfigureAwait(false));
            case SitesMessageTypes.MakeFolder:
                return Result(request, await MakeFolderAsync(request.GetPayload<SiteMakeFolderRequest>() ?? new SiteMakeFolderRequest(), cancellationToken).ConfigureAwait(false));
            case SitesMessageTypes.Rename:
                return Result(request, await RenameAsync(request.GetPayload<SiteRenameRequest>() ?? new SiteRenameRequest(), cancellationToken).ConfigureAwait(false));
            case SitesMessageTypes.Count:
                return IpcMessage.Create(SitesMessageTypes.CountResult,
                    await CountAsync(request.GetPayload<SiteChangeRequest>() ?? new SiteChangeRequest(), cancellationToken).ConfigureAwait(false), request.Id);
            case SitesMessageTypes.Delete:
                return IpcMessage.Create(SitesMessageTypes.ChangeResult,
                    await DeleteAsync(request.GetPayload<SiteChangeRequest>() ?? new SiteChangeRequest(), cancellationToken).ConfigureAwait(false), request.Id);
            case SitesMessageTypes.RcloneInstall:
                return IpcMessage.Create(SitesMessageTypes.Status,
                    await InstallRcloneAsync(request.GetPayload<SitesRcloneInstallRequest>()?.Version, cancellationToken).ConfigureAwait(false), request.Id);
            case SitesMessageTypes.RcloneCheck:
                return IpcMessage.Create(SitesMessageTypes.Status, await CheckRcloneAsync(cancellationToken).ConfigureAwait(false), request.Id);
            case SitesMessageTypes.TransferStart:
            {
                var start = request.GetPayload<SiteTransferStartRequest>() ?? new SiteTransferStartRequest();
                var started = await _transfers.StartAsync(start, cancellationToken).ConfigureAwait(false);
                if (started.Ok) Touch(start.SiteId);
                return Transfer(request, started);
            }
            case SitesMessageTypes.OpenOnce:
                return Result(request, await OpenOnceAsync(request.GetPayload<SiteOpenOnceRequest>() ?? new SiteOpenOnceRequest(), cancellationToken).ConfigureAwait(false));
            case SitesMessageTypes.Close:
                return Result(request, await CloseAsync(request.GetPayload<SiteIdRequest>()?.Id ?? "", cancellationToken).ConfigureAwait(false));
            case SitesMessageTypes.Keep:
                return Result(request, await KeepAsync(request.GetPayload<SiteKeepRequest>() ?? new SiteKeepRequest(), cancellationToken).ConfigureAwait(false));
            case SitesMessageTypes.Here:
                return IpcMessage.Create(SitesMessageTypes.HereResult,
                    await HereAsync(request.GetPayload<SiteHereRequest>() ?? new SiteHereRequest(), cancellationToken).ConfigureAwait(false), request.Id);
            case SitesMessageTypes.SyncSchedule:
                return Result(request, await ScheduleSyncAsync(request.GetPayload<SiteSyncScheduleRequest>() ?? new SiteSyncScheduleRequest(), cancellationToken).ConfigureAwait(false));
            case SitesMessageTypes.TransferSchedule:
            {
                var wait = request.GetPayload<SiteTransferScheduleRequest>() ?? new SiteTransferScheduleRequest();
                return Transfer(request, await _transfers.WaitForScheduleAsync(wait.Id ?? "", wait.Wait).ConfigureAwait(false));
            }
            case SitesMessageTypes.Remember:
                return Result(request, await RememberAsync(request.GetPayload<SiteRememberRequest>() ?? new SiteRememberRequest(), cancellationToken).ConfigureAwait(false));
            case SitesMessageTypes.Preview:
                return IpcMessage.Create(SitesMessageTypes.PreviewResult,
                    await PreviewAsync(request.GetPayload<SitePreviewRequest>() ?? new SitePreviewRequest(), cancellationToken).ConfigureAwait(false), request.Id);
            case SitesMessageTypes.SyncPlan:
                return IpcMessage.Create(SitesMessageTypes.SyncPlanResult,
                    (await PlanSyncAsync(request.GetPayload<SiteSyncRequest>() ?? new SiteSyncRequest(), cancellationToken).ConfigureAwait(false)).Plan, request.Id);
            case SitesMessageTypes.SyncRun:
            {
                var sync = request.GetPayload<SiteSyncRequest>() ?? new SiteSyncRequest();
                var ran = await RunSyncAsync(sync, cancellationToken).ConfigureAwait(false);
                if (ran.Transfer is not null) Touch(sync.SiteId);
                return Transfer(request, ran);
            }
            case SitesMessageTypes.TransferPause:
                return Transfer(request, await _transfers.PauseAsync(TransferId(request)).ConfigureAwait(false));
            case SitesMessageTypes.TransferResume:
                return Transfer(request, await _transfers.ResumeAsync(TransferId(request)).ConfigureAwait(false));
            case SitesMessageTypes.TransferCancel:
                return Transfer(request, await _transfers.CancelAsync(TransferId(request)).ConfigureAwait(false));
            case SitesMessageTypes.TransferRetry:
                return Transfer(request, await _transfers.RetryAsync(TransferId(request)).ConfigureAwait(false));
            case SitesMessageTypes.TransferClear:
                return Transfer(request, await _transfers.ClearAsync(TransferId(request)).ConfigureAwait(false));
            case SitesMessageTypes.TransferStopFile:
            {
                var file = request.GetPayload<SiteTransferFileRequest>() ?? new SiteTransferFileRequest();
                return Transfer(request, await _transfers.StopFileAsync(file.Id ?? "", file.Path ?? "").ConfigureAwait(false));
            }
            case SitesMessageTypes.TransferStartFile:
            {
                var file = request.GetPayload<SiteTransferFileRequest>() ?? new SiteTransferFileRequest();
                return Transfer(request, await _transfers.StartFileAsync(file.Id ?? "", file.Path ?? "").ConfigureAwait(false));
            }
            default:
                return Result(request, new SiteResultDto { Ok = false, Message = $"Unknown Sites request {request.Type}." });
        }

        static IpcMessage Result(IpcMessage request, SiteResultDto result) => IpcMessage.Create(SitesMessageTypes.Result, result, request.Id);
        static IpcMessage Transfer(IpcMessage request, SiteTransferResultDto result) => IpcMessage.Create(SitesMessageTypes.TransferResult, result, request.Id);
    }

    private static string TransferId(IpcMessage request) => request.GetPayload<SiteTransferIdRequest>()?.Id ?? "";

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
            MaxTransfers = _config.TransfersAtOnce,
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
        if (request.MaxTransfers is < 1 or > SitesConfig.MostTransfers)
            return WithError(Status(), $"\"Transfers at once\" must be between 1 and {SitesConfig.MostTransfers}.");
        if (request.OwnRclonePath is { Length: > 0 } ownPath && !Path.IsPathFullyQualified(ownPath))
            return WithError(Status(), "\"Use my own rclone\" needs the full path of the rclone program.");
        // Before the gate: a file being stopped may still need a lease, and leases are taken under the gate. A start or
        // resume that slips in before the switch is saved only waits: nothing runs while Sites is off.
        if (!request.Enabled && _config.Enabled) await _transfers.PauseAllAsync("Paused when Remote files was turned off.").ConfigureAwait(false);
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var own = request.OwnRclonePath is null ? _config.OwnRclonePath
                : request.OwnRclonePath.Length == 0 ? null
                : request.OwnRclonePath;
            if (own is not null && !Path.IsPathFullyQualified(own))
                return WithError(Status(), "\"Use my own rclone\" needs the full path of the rclone program.");

            var next = _config with
            {
                Enabled = request.Enabled,
                OwnRclonePath = own,
                AutoUpdateRclone = request.AutoUpdateRclone ?? _config.AutoUpdateRclone,
                MaxTransfers = request.MaxTransfers ?? _config.MaxTransfers,
            };
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

        if (_config.Enabled)
        {
            StartUpdateLoop();
            _transfers.Pump(); // a higher "transfers at once" starts more at once
        }
        else
        {
            await StopUpdateLoopAsync().ConfigureAwait(false);
        }

        if (changed) await RaiseChangedAsync(cancellationToken).ConfigureAwait(false);
        return Status();
    }

    private async Task<SitesStatusDto> InstallRcloneAsync(string? versionText, CancellationToken cancellationToken)
    {
        if (_updater is null) return WithError(Status(), "This Inbound can't download rclone; choose your own rclone in Options › Remote files.");
        if (_config.OwnRclonePath is not null)
            return WithError(Status(), "You chose your own rclone in Options › Remote files; Inbound does not download or update it.");
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
        [.. (await _sites.ListAsync(cancellationToken, includeUnsaved: true).ConfigureAwait(false)).Select(ToDto).Select(MarkSigningIn)];

    private async Task<SiteResultDto> SaveAsync(SiteSaveRequest request, CancellationToken cancellationToken)
    {
        SiteRecord saved;
        var droppedCertificates = false;
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            SiteRecord? existing = null;
            if (!string.IsNullOrEmpty(request.Id))
            {
                if (!SiteStore.IsId(request.Id)) return Fail("That is not a remote id.");
                existing = await _sites.GetAsync(request.Id, cancellationToken).ConfigureAwait(false);
                if (existing is null) return Fail("That remote no longer exists.");
            }

            try
            {
                var built = BuildRecord(request, existing, draftKeys: null);
                if (built.Failure is not null) return built.Failure;
                saved = built.Record!;
                droppedCertificates = existing is { TrustedCertificates.Count: > 0 } && saved.TrustedCertificates.Count == 0;
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
        _transfers.SiteLimitsChanged(saved);
        // SITES.6b: another server no longer trusts the old one's certificate.
        if (droppedCertificates) await TrustedCertificatesChangedAsync(cancellationToken).ConfigureAwait(false);
        await RaiseChangedAsync(cancellationToken).ConfigureAwait(false);
        return new SiteResultDto { Ok = true, Site = ToDto(saved) };
    }

    /// <summary>
    /// The record a <c>sites.save</c> would store, checked as the save checks it, or the reason it can't be (SITES.11 R4: shared
    /// with <c>sites.testDraft</c>). With <paramref name="draftKeys"/> (a draft, never stored) a saved secret is carried only
    /// while server, port and user are unchanged, and the given server keys are added for this test. Throws
    /// <see cref="InvalidOperationException"/> when no secret protector exists, as <see cref="Seal"/> does.
    /// </summary>
    private static (SiteRecord? Record, SiteResultDto? Failure) BuildRecord(SiteSaveRequest request, SiteRecord? existing, IReadOnlyList<string>? draftKeys)
    {
        if (SiteKindCatalog.Find(request.Kind) is null) return (null, Fail("Choose what kind of remote it is."));
        if (existing is not null && existing.Kind != request.Kind) return (null, Fail("A remote's kind can't be changed; add a new remote instead."));
        var signsIn = SiteKindCatalog.SignsIn(request.Kind);
        // SITES.6c: a sign-in kind has no password or key of its own; its access comes from sites.signIn.
        if (signsIn)
        {
            if (!string.IsNullOrEmpty(request.Password) || !string.IsNullOrEmpty(request.PrivateKey) || !string.IsNullOrEmpty(request.KeyPassphrase))
                return (null, Fail("This kind of remote signs in through the browser, not with a password or key."));
            request.AuthMethod = SiteAuthMethods.SignIn;
            request.Password = null;
        }
        else if (!string.IsNullOrEmpty(request.ClientId) || !string.IsNullOrEmpty(request.ClientSecret))
            return (null, Fail("Only a remote that signs in through the browser has a client ID."));
        if (!signsIn && request.Kind != SiteKinds.Sftp && (request.AuthMethod != SiteAuthMethods.Password || !string.IsNullOrEmpty(request.PrivateKey)))
            return (null, Fail("Only SFTP remotes sign in with a key."));
        if (!signsIn && request.AuthMethod is not (SiteAuthMethods.Password or SiteAuthMethods.Key))
            return (null, Fail("Choose a password or a key file to sign in with."));
        if (KindAddress(request) is { Problem: { } addressProblem }) return (null, Fail(addressProblem));
        if (request.DownLimit is < 0 or > SiteStore.MaxLimit || request.UpLimit is < 0 or > SiteStore.MaxLimit)
            return (null, Fail($"A speed limit must be between 0 (none) and {SiteStore.MaxLimit:N0} KB/s."));
        // SITES.14: a PuTTY key becomes OpenSSH text with no passphrase of its own (sealed like any key); its passphrase,
        // needed only to open it, is not kept.
        if (PuttyKey.IsPutty(request.PrivateKey))
        {
            var (openSsh, puttyProblem) = PuttyKey.ToOpenSsh(request.PrivateKey!, request.KeyPassphrase);
            if (openSsh is null) return (null, Fail(puttyProblem!));
            request.PrivateKey = openSsh;
            request.KeyPassphrase = "";
        }

        // A key that rclone could not read would be sealed and only fail at the next test; it is refused here instead.
        if (!string.IsNullOrEmpty(request.PrivateKey) && SiteKeyText.Problem(request.PrivateKey) is { } keyProblem)
            return (null, Fail(keyProblem));

        var draft = draftKeys is not null;
        var (host, port, _) = KindAddress(request);
        var now = DateTimeOffset.UtcNow;
        // Another server is another key: a changed host or port forgets the accepted ones, and the window asks again.
        // For an address kind the address is the server: another one forgets the stars too.
        var sameServer = existing is not null && string.Equals(existing.Host, host, StringComparison.OrdinalIgnoreCase) && existing.Port == port
            && string.Equals(existing.Url, Trimmed(request.Url), StringComparison.Ordinal)
            && string.Equals(existing.Endpoint, Trimmed(request.Endpoint), StringComparison.Ordinal);
        // SITES.11 R4 review: a saved password is sent only with the encryption it was saved with, so switching FTP's
        // encryption off can't send it in clear (save and draft alike).
        // An FTP record from before the choice existed has no Tls; the window shows it as explicit, as rclone treats it.
        var sameTls = existing is null || request.Kind != SiteKinds.Ftp
            || (existing.Tls ?? SiteTlsModes.Explicit) == (request.Tls ?? SiteTlsModes.Explicit);
        // SITES.11 R4: a draft (sites.testDraft) takes a saved secret only while server, port and user are all unchanged,
        // so a test of a newly typed server or user never sends it a stored password or key.
        var keepPassword = (draft ? sameServer && string.Equals(existing!.User, request.User.Trim(), StringComparison.Ordinal) : sameServer) && sameTls;
        var keepKey = draft ? keepPassword : sameServer || !request.OwnSignInOnly;
        // Audit 2026-11-15: a saved password is sent only to the server it was saved for. Pointing a site at another
        // server keeps the saved key (signing in with a key gives the server nothing of it) but not the password,
        // which rclone would send there: it has to be typed again.
        if (existing is not null && !keepPassword && existing.SealedPassword is not null && request.Password is null
            && request.AuthMethod == SiteAuthMethods.Password)
            return (null, Fail(!sameServer
                ? "The server changed, so type the password again: a saved password is only sent to the server it was saved for."
                : !sameTls
                    ? "The encryption changed, so type the password again: a saved password is only sent with the encryption it was saved with."
                    : "The user changed, so type the password again to test it: a saved password is only tried for the user it was saved for."));
        var saved = new SiteRecord
        {
            Id = existing?.Id ?? SiteStore.NewId(),
            Name = request.Name.Trim(),
            Kind = request.Kind,
            Host = host,
            Port = port,
            User = request.User.Trim(),
            Tls = request.Kind == SiteKinds.Ftp ? request.Tls : null,
            Url = request.Kind == SiteKinds.WebDav ? Trimmed(request.Url) : null,
            Vendor = request.Kind == SiteKinds.WebDav ? request.Vendor ?? "other" : null,
            Provider = request.Kind == SiteKinds.S3 ? request.Provider : null,
            Endpoint = request.Kind == SiteKinds.S3 ? Trimmed(request.Endpoint) : null,
            Region = request.Kind == SiteKinds.S3 ? Trimmed(request.Region) : null,
            AuthMethod = request.AuthMethod,
            // Audit 2026-11-15: never carried to another server, whatever the sign-in method, so a site moved
            // with key sign-in and switched back to a password can't take the old server's password along.
            SealedPassword = Seal(request.Password, keepPassword ? existing?.SealedPassword : null),
            // SITES.11 review: from a web session the key stays with its server too, or an edit could sign in
            // anywhere with this PC's stored key.
            SealedPrivateKey = Seal(request.PrivateKey, keepKey ? existing?.SealedPrivateKey : null),
            SealedKeyPassphrase = Seal(request.KeyPassphrase, keepKey ? existing?.SealedKeyPassphrase : null),
            // A draft's accepted keys only stand in for keys the remote does not have: a remote with trusted keys keeps
            // them, so a changed key comes back as hostKeyChanged, as sites.test says it.
            HostKeys = sameServer && existing!.HostKeys.Count > 0 ? existing.HostKeys : [.. (draftKeys ?? []).Distinct(StringComparer.Ordinal)],
            // SITES.6b: a trusted certificate belongs to one server and one kind of encryption.
            TrustedCertificates = sameServer && existing!.Tls == (request.Kind == SiteKinds.Ftp ? request.Tls : null)
                && SiteKindCatalog.Encrypted(request.Kind, request.Tls, Trimmed(request.Url), Trimmed(request.Endpoint))
                ? existing.TrustedCertificates : [],
            RemoteFolder = request.RemoteFolder.Trim(),
            LocalFolder = string.IsNullOrWhiteSpace(request.LocalFolder) ? null : request.LocalFolder.Trim(),
            // Stars are folders on the same server; another server forgets them with its keys.
            Stars = sameServer ? existing!.Stars : [],
            CreatedAt = existing?.CreatedAt ?? now,
            LastUsedAt = existing?.LastUsedAt,
            // SITES.11: an edit of an address opened once keeps it unsaved (the store holds it in memory).
            Unsaved = existing?.Unsaved ?? false,
            // SITES.13: kept with the remote; the last folder belongs to its server.
            LastFolder = sameServer ? existing!.LastFolder : null,
            Sort = existing?.Sort,
            SortDescending = existing?.SortDescending ?? false,
            DownLimit = request.DownLimit ?? existing?.DownLimit ?? 0,
            UpLimit = request.UpLimit ?? existing?.UpLimit ?? 0,
            ScheduledSync = sameServer ? existing!.ScheduledSync : null,
            ModifiedAt = now,
        };
        if (signsIn)
        {
            var clientId = Trimmed(request.ClientId);
            saved = saved with
            {
                ClientId = clientId,
                // A secret belongs to its client ID: a new ID without one has none.
                SealedClientSecret = clientId is null ? null
                    : Seal(request.ClientSecret, existing?.ClientId == clientId ? existing.SealedClientSecret : null),
                // Another app registration is another grant: the site signs in again.
                SealedSignIn = existing is not null && existing.ClientId == clientId ? existing.SealedSignIn : null,
            };
        }

        if (saved.AuthMethod == SiteAuthMethods.Key && saved.SealedPrivateKey is null)
            return (null, Fail("Choose the private key to sign in with."));
        if (saved.Kind == SiteKinds.S3 && (saved.User.Length == 0 || saved.SealedPassword is null))
            return (null, Fail("An S3 remote needs its access key ID and secret access key."));
        if (saved.Kind == SiteKinds.B2 && (saved.User.Length == 0 || saved.SealedPassword is null))
            return (null, Fail("A B2 remote needs its application key ID and application key."));
        if (request.OwnSignInOnly && saved is { Kind: SiteKinds.Sftp, AuthMethod: SiteAuthMethods.Password, SealedPassword: null })
            return (null, Fail("Type the password, or choose a key: a remote saved here can't sign in with the server PC's own SSH keys."));
        return (saved, null);
    }

    /// <param name="unsavedOnly">
    /// SITES.11 <c>sites.close</c>: refused, inside the gate, when the site is (by now) a saved one; its batches are
    /// canceled after the gate, once the record is gone.
    /// </param>
    private async Task<SiteResultDto> RemoveAsync(string id, CancellationToken cancellationToken, bool unsavedOnly = false)
    {
        bool removed;
        var hadCertificates = false;
        if (!unsavedOnly)
        {
            if (SiteStore.IsId(id)) await _transfers.CancelSiteAsync(id, "Canceled because the remote was removed.").ConfigureAwait(false);
            CancelSignIn(id);
        }

        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            // Checked and removed under the gate, which "Remember" takes too: a site kept meanwhile stays saved, its
            // transfers untouched; once removed here, "Remember" no longer finds it.
            if (unsavedOnly && !_sites.IsUnsaved(id)) return Fail("That remote was saved meanwhile; remove it in Options › Remote files.");
            hadCertificates = SiteStore.IsId(id) && await _sites.GetAsync(id, cancellationToken).ConfigureAwait(false) is { TrustedCertificates.Count: > 0 };
            removed = SiteStore.IsId(id) && await _sites.RemoveAsync(id, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _gate.Release();
        }

        if (unsavedOnly && removed)
        {
            // After the gate, as for a saved remote: a file being stopped may still need a lease, and leases are taken
            // under the gate.
            await _transfers.CancelSiteAsync(id, "Canceled because the address was closed.").ConfigureAwait(false);
            CancelSignIn(id);
        }

        ForgetListings(id);
        ForgetMemory(id);
        if (!removed) return Fail("That remote no longer exists.");
        if (hadCertificates) await TrustedCertificatesChangedAsync(cancellationToken).ConfigureAwait(false);
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
        // SITES.6b: other kinds trust a certificate, read outside the gate (a handshake can take seconds).
        if (SiteStore.IsId(request.Id) && await _sites.GetAsync(request.Id, cancellationToken).ConfigureAwait(false) is { Kind: not SiteKinds.Sftp } tlsSite)
            return await TrustCertificateAsync(tlsSite, request, cancellationToken).ConfigureAwait(false);

        SiteRecord saved;
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (!SiteStore.IsId(request.Id) || await _sites.GetAsync(request.Id, cancellationToken).ConfigureAwait(false) is not { } site)
                return Fail("That remote no longer exists.");
            if (site.Kind != SiteKinds.Sftp) return Fail("The remote changed while it was trusted. Try again.");
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
            return Fail("That remote no longer exists.");
        var path = SitePaths.Normalize(site.RemoteFolder);
        if (path is null) return Fail("The remote's start folder is not a usable path; change it in Edit remote.");
        var (items, failure) = await ListFolderAsync(site, path, cancellationToken).ConfigureAwait(false);
        if (failure is not null) return failure;
        var count = items!.Count;
        return new SiteResultDto { Ok = true, Message = $"Connected. The folder holds {count} item{(count == 1 ? "" : "s")}." };
    }

    /// <summary>
    /// SITES.11 R4, <c>sites.testDraft</c>: the form as it would be saved (<see cref="BuildRecord"/>, the same checks), built in
    /// memory and its start folder listed, as <see cref="TestAsync"/> does (no gate, the same time limit). Nothing is
    /// written, broadcast or remembered: no store write, no reach, no last-used time. An SFTP server whose key is not
    /// accepted yet is only asked for its keys (no login, nothing of the remote sent); an untrusted certificate is read
    /// and returned to be trusted after the save.
    /// </summary>
    private async Task<SiteResultDto> TestDraftAsync(SiteTestDraftRequest request, CancellationToken cancellationToken)
    {
        var form = request.Site ?? new SiteSaveRequest();
        var keys = request.HostKeys ?? [];
        if (keys.Count > 16) return Fail("Accept between 1 and 16 of the server's keys.");
        // One key per entry: rclone takes the list joined with commas.
        if (keys.Any(k => string.IsNullOrWhiteSpace(k) || k.Contains(',', StringComparison.Ordinal) || k.Contains('\n', StringComparison.Ordinal) || k.Contains('\r', StringComparison.Ordinal)))
            return Fail("That is not a server key.");

        SiteRecord? existing = null;
        if (!string.IsNullOrEmpty(form.Id))
        {
            if (!SiteStore.IsId(form.Id)) return Fail("That is not a remote id.");
            existing = await _sites.GetAsync(form.Id, cancellationToken).ConfigureAwait(false);
            if (existing is null) return Fail("That remote no longer exists.");
        }

        SiteRecord draft;
        try
        {
            var built = BuildRecord(form, existing, keys);
            if (built.Failure is not null) return built.Failure;
            draft = built.Record!;
        }
        catch (Exception e) when (e is InvalidOperationException or SiteStoreException)
        {
            return Fail(e.Message);
        }
        finally
        {
            form.Password = null;
            form.PrivateKey = null;
            form.KeyPassphrase = null;
            form.ClientSecret = null;
        }

        var path = SitePaths.Normalize(draft.RemoteFolder);
        if (path is null) return Fail("The starting folder is not a usable path.");

        if (draft.NeedsHostKey)
        {
            // Read with no login: the server learns nothing of the remote.
            try
            {
                var offered = await _probe(draft.Host, draft.Port, cancellationToken).ConfigureAwait(false);
                return new SiteResultDto
                {
                    Ok = false, Problem = SiteProblems.HostKeyNotAccepted, Message = "The server's key has not been accepted yet.",
                    OfferedKeys = [.. offered.Select(ToDto)],
                };
            }
            catch (SshProbeException e)
            {
                return Fail(e.Message);
            }
        }

        if (SiteKindCatalog.SignsIn(draft.Kind) && draft.SealedSignIn is null)
            return new SiteResultDto { Ok = false, Problem = SiteProblems.SignInNeeded, Message = "Save the remote, and your browser opens to sign in." };

        var (items, failure) = await ListFolderAsync(draft, path, cancellationToken, draft: true).ConfigureAwait(false);
        if (failure is not null)
        {
            if (failure.Problem == SiteProblems.CertificateNotTrusted && CertificateProblem(draft) is null)
            {
                try
                {
                    failure.Certificate = ToDto(await _certificateProbe(draft, cancellationToken).ConfigureAwait(false));
                }
                catch (TlsProbeException)
                {
                    // The failure's own words stand; the certificate is read again after the save.
                }
            }

            return failure;
        }

        var count = items!.Count;
        return new SiteResultDto { Ok = true, ItemCount = count, Message = $"Connected. The folder holds {count} item{(count == 1 ? "" : "s")}." };
    }

    /// <summary>
    /// One folder, sorted folders first then by name, paged (SITES.3). A page after the first comes from the listing the
    /// Host read for that folder in the last <see cref="ListingLifetime"/>, so the pages of one folder agree with each other.
    /// </summary>
    private async Task<SiteBrowseResultDto> BrowseAsync(SiteBrowseRequest request, CancellationToken cancellationToken)
    {
        if (!SiteStore.IsId(request.Id) || await _sites.GetAsync(request.Id, cancellationToken).ConfigureAwait(false) is not { } site)
            return new SiteBrowseResultDto { Ok = false, Message = "That remote no longer exists.", Path = request.Path ?? "" };
        var fromSite = string.IsNullOrEmpty(request.Path);
        var path = SitePaths.Normalize(fromSite ? site.RemoteFolder : request.Path);
        if (path is null)
        {
            return new SiteBrowseResultDto
            {
                Ok = false,
                Path = request.Path ?? "",
                Message = fromSite ? "The remote's folder on the server is not a usable path; change it in Edit remote." : "That is not a folder path on the server.",
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
            // SITES.11: a user's first page; a later page and Refresh are not a new use.
            if (offset == 0 && !request.Refresh) Touch(site.Id);
        }

        return new SiteBrowseResultDto
        {
            Ok = true,
            Path = path,
            Total = listing.Items.Count,
            Offset = offset,
            Listing = listing.Token,
            // "On this PC" is read for the page only, so a long folder costs one look per item shown.
            Items = [.. listing.Items.Skip(offset).Take(limit).Select(item => WithHere(item, site, path))],
        };
    }

    /// <summary>A copy of the cached item with its "On this PC" state as it is now.</summary>
    private static SiteItemDto WithHere(SiteItemDto item, SiteRecord site, string path) => new()
    {
        Name = item.Name,
        IsFolder = item.IsFolder,
        Size = item.Size,
        Modified = item.Modified,
        Here = SiteHere.Of(site.LocalFolder, site.RemoteFolder, path, item),
    };

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
    /// <param name="draft">SITES.11 R4: a <c>sites.testDraft</c> record, which is never stored: its reach is not recorded.</param>
    private async Task<(IReadOnlyList<SiteItemDto>? Items, SiteResultDto? Failure)> ListFolderAsync(SiteRecord site, string path, CancellationToken cancellationToken, bool draft = false)
    {
        if (site.NeedsHostKey)
            return (null, new SiteResultDto { Ok = false, Problem = SiteProblems.HostKeyNotAccepted, Message = "The server's key has not been accepted yet." });
        // SITES.11 review: with no password rclone would offer this PC's ssh-agent keys to whatever server was typed.
        if (site is { Unsaved: true, Kind: SiteKinds.Sftp, SealedPassword: null })
            return (null, new SiteResultDto { Ok = false, Problem = SiteProblems.PasswordNeeded, Message = "Type the password for this server." });

        var result = await ListFolderOnceAsync(site, path, cancellationToken, draft).ConfigureAwait(false);
        if (result.Items is not null && !draft) SetReach(site.Id, SiteReach.Ok);
        return result;
    }

    /// <summary>The longest one folder's listing may take before the browse gives up (tests shorten it).</summary>
    internal static TimeSpan ListTimeout { get; set; } = TimeSpan.FromSeconds(60);

    private async Task<(IReadOnlyList<SiteItemDto>? Items, SiteResultDto? Failure)> ListFolderOnceAsync(SiteRecord site, string path, CancellationToken cancellationToken,
        bool draft = false)
    {
        // A draft's reach is not recorded: it is not a remote yet, or not as saved.
        void Trouble()
        {
            if (!draft) SetReach(site.Id, SiteReach.Trouble);
        }

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
                // SITES.11 R2: a server that stops answering mid-way must not leave a browse waiting for ever.
                using var limit = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
                limit.CancelAfter(ListTimeout);
                var listing = await lease.Client.CallAsync("operations/list", new JsonObject
                {
                    ["fs"] = fs,
                    ["remote"] = SitePaths.ToRemote(path),
                    // Without MIME types rclone need not look at each file's name; Sites shows none.
                    ["opt"] = new JsonObject { ["noMimeType"] = true },
                }, limit.Token).ConfigureAwait(false);
                if (listing["list"] is JsonArray { Count: > MaxListingItems } big)
                    return (null, Fail($"The folder holds {big.Count:N0} items, more than Remote files can show ({MaxListingItems:N0})."));
                return (SiteListing.Read(listing), null);
            }
            catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
            {
                // Not cantReach: it may have connected and signed in before going quiet, so the client does not retry on its own.
                Trouble();
                return (null, Fail($"The server did not answer within {ListTimeout.TotalSeconds:0} seconds."));
            }
            catch (RcloneCallException e) when (SshHostKeyProblem.Parse(e.Message) is { } problem)
            {
                Trouble();
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
            catch (SiteSignInNeededException e)
            {
                Trouble();
                return (null, new SiteResultDto { Ok = false, Problem = SiteProblems.SignInNeeded, Message = e.Message });
            }
            catch (RcloneCallException e) when (SiteErrorText.IsSignInRefused(e.Message))
            {
                Trouble();
                return (null, new SiteResultDto
                {
                    Ok = false, Problem = SiteProblems.SignInNeeded,
                    Message = "The service no longer accepts this remote's sign-in. Sign in again in Options > Remote files.",
                });
            }
            catch (RcloneCallException e) when (SiteErrorText.IsUntrustedCertificate(e.Message))
            {
                Trouble();
                return (null, new SiteResultDto { Ok = false, Problem = SiteProblems.CertificateNotTrusted, Message = SiteErrorText.Friendly(e.Message) });
            }
            catch (RcloneCallException e) when (site.Unsaved && SiteErrorText.RefusesTheSite(e.Message))
            {
                // An address opened once: the server wants a password, or another one.
                Trouble();
                return (null, new SiteResultDto { Ok = false, Problem = SiteProblems.PasswordNeeded, Message = SiteErrorText.Friendly(e.Message) });
            }
            catch (Exception e) when (e is RcloneCallException or RcloneUnavailableException or SiteStoreException)
            {
                // Only a connection-class failure says the site is in trouble; rclone not running here does not.
                var call = e as RcloneCallException;
                if (call is not null && (SiteErrorText.CantReach(call.Message) || SiteErrorText.RefusesTheSite(call.Message)))
                    Trouble();
                // RcloneClient has already redacted the text.
                var failure = Fail(SiteErrorText.Friendly(e.Message));
                // Only a server that did not answer may be retried by a client on its own: a refused sign-in retried would
                // send the wrong password again and again (lockouts, fail2ban).
                if (call is not null && SiteErrorText.DidNotAnswer(call.Message)) failure.Problem = SiteProblems.CantReach;
                return (null, failure);
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
                return Fail("That remote no longer exists.");
            if (string.IsNullOrEmpty(request.Path) || SitePaths.Normalize(request.Path) is not { } path || !SitePaths.IsStarrable(path))
                return Fail("That folder can't be starred.");
            // Starring a starred folder again keeps its place.
            var stars = site.Stars.Where(s => s != path).ToList();
            if (request.Starred) stars = site.Stars.Contains(path) ? [.. site.Stars] : [.. site.Stars, path];
            if (stars.Count > SitePaths.MaxStars) return Fail($"A remote can have at most {SitePaths.MaxStars} starred folders.");
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
                    _rcloneProblem = _config.OwnRclonePath is not null ? "The rclone chosen in Options › Remote files was not found."
                        : _updater is not null ? "rclone is not on this PC yet. Download it in Options › Remote files, or choose your own rclone."
                        : "rclone is not on this PC yet. Choose your own rclone in Options › Remote files.";
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
                // SITES.6b: the trusted-certificates file as the sites have it now, with today's system roots.
                await WriteTrustedCertificatesAsync(cancellationToken).ConfigureAwait(false);
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

        // SITES.6c: a token rclone renewed is sealed into its site (SiteRemote.SignedInAsync notices it).
        lease.Client.SignInRenewed ??= (siteId, previous, settings, ct) => SaveRenewedSignInAsync(siteId, previous, settings, ct);
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

    public string? TransferLocalFolder(string transferId) => _transfers.LocalFolderOf(transferId);

    /// <summary>On the Host's exit.</summary>
    public async ValueTask DisposeAsync()
    {
        _disposing = true;
        await StopUpdateLoopAsync().ConfigureAwait(false);
        CancelSignIn(null);
        await CloseAllStreamsAsync().ConfigureAwait(false);
        // Files in flight stop first (they hold leases), and their batches go on at the next start.
        await _transfers.DisposeAsync().ConfigureAwait(false);
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

    private static SiteKindsDto Kinds() => new() { Kinds = [.. SiteKindCatalog.All] };

    private static string? Trimmed(string? value) => string.IsNullOrWhiteSpace(value) ? null : value.Trim();

    /// <summary>
    /// The host and port a site is shown and compared by: as typed for SFTP and FTP; from the address for WebDAV and S3
    /// (Amazon S3 without an endpoint is <c>s3.amazonaws.com</c>). A problem when the address can't be used.
    /// </summary>
    private static (string Host, int Port, string? Problem) KindAddress(SiteSaveRequest request)
    {
        string? address = request.Kind switch
        {
            SiteKinds.WebDav => Trimmed(request.Url),
            SiteKinds.S3 => Trimmed(request.Endpoint) ?? (request.Provider == SiteKindCatalog.AwsProvider ? "https://s3.amazonaws.com" : null),
            // SITES.6c: the service's own address; nothing is typed for these kinds.
            SiteKinds.Drive => "https://www.googleapis.com",
            SiteKinds.OneDrive => "https://graph.microsoft.com",
            SiteKinds.Dropbox => "https://api.dropboxapi.com",
            SiteKinds.B2 => "https://api.backblazeb2.com",
            _ => null,
        };
        if (request.Kind is not (SiteKinds.WebDav or SiteKinds.S3 or SiteKinds.Drive or SiteKinds.OneDrive or SiteKinds.Dropbox or SiteKinds.B2))
            return (request.Host.Trim(), request.Port, null);
        var what = request.Kind == SiteKinds.WebDav ? "server address" : "service address (endpoint)";
        if (SiteKindCatalog.AddressProblem(address, what) is { } problem) return ("", 0, problem);
        var uri = new Uri(address!);
        return (uri.Host, uri.Port, null);
    }

    private SiteDto ToDto(SiteRecord site)
    {
        var dto = ToDtoOfFile(site);
        (dto.Reach, dto.LastUsedAt) = MemoryOf(site);
        dto.Unsaved = site.Unsaved;
        return dto;
    }

    private static SiteDto ToDtoOfFile(SiteRecord site) => new()
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
        TrustedCertificates = [.. site.TrustedCertificates.Select(c => new SiteTrustedCertificateDto { Fingerprint = c.Fingerprint, Subject = c.Subject, NotAfter = c.NotAfter })],
        RemoteFolder = site.RemoteFolder,
        LocalFolder = site.LocalFolder,
        Stars = [.. site.Stars],
        Tls = site.Tls,
        Url = site.Url,
        Vendor = site.Vendor,
        Provider = site.Provider,
        Endpoint = site.Endpoint,
        Region = site.Region,
        Capabilities = [.. SiteKindCatalog.Find(site.Kind)?.Capabilities ?? []],
        Encrypted = SiteKindCatalog.Encrypted(site.Kind, site.Tls, site.Url, site.Endpoint),
        SignedIn = site.SignedIn,
        ClientId = site.ClientId,
        HasClientSecret = site.SealedClientSecret is not null,
        LastFolder = site.LastFolder,
        ScheduledSync = site.ScheduledSync is { } sync
            ? new SiteSyncRequest { SiteId = site.Id, Direction = sync.Direction, RemoteFolder = sync.RemoteFolder, LocalFolder = sync.LocalFolder }
            : null,
        Sort = site.Sort,
        SortDescending = site.SortDescending,
        DownLimit = site.DownLimit,
        UpLimit = site.UpLimit,
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

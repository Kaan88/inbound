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
    public SitesCommands(string stateDirectory, Action<string> log, Func<string?, string?>? findRclone = null,
        Func<string, string, RcloneSupervisor>? createSupervisor = null,
        Func<string, int, CancellationToken, Task<IReadOnlyList<SshHostKey>>>? probe = null)
    {
        _stateDirectory = stateDirectory;
        _configStore = new SitesConfigStore(stateDirectory);
        _sites = new SiteStore(stateDirectory, log);
        _log = log;
        _findRclone = findRclone ?? (own => RcloneSetup.Find(own));
        _createSupervisor = createSupervisor ?? ((rclone, state) => RcloneSupervisor.ForExecutable(rclone, state, null, log));
        _probe = probe ?? ((host, port, ct) => SshHostKeyProbe.ProbeAllAsync(host, port, null, ct));
        _config = _configStore.Load(out _configProblem);
        if (_configProblem is not null) log(_configProblem);
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
        };
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

            var next = _config with { Enabled = request.Enabled, OwnRclonePath = own };
            changed = next != _config;
            if (changed)
            {
                await _configStore.SaveAsync(next, cancellationToken).ConfigureAwait(false);
                var rcloneChanged = next.OwnRclonePath != _config.OwnRclonePath;
                _config = next;
                _configProblem = null;
                // Off, or another rclone: the running one stops. With Sites on it starts again when something needs it.
                if (!next.Enabled || rcloneChanged) await StopRcloneLockedAsync().ConfigureAwait(false);
                _log(next.Enabled ? "Sites turned on." : "Sites turned off; rclone stopped.");
            }
        }
        finally
        {
            _gate.Release();
        }

        if (changed) await RaiseChangedAsync(cancellationToken).ConfigureAwait(false);
        return Status();
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

        await RaiseChangedAsync(cancellationToken).ConfigureAwait(false);
        return new SiteResultDto { Ok = true, Site = ToDto(saved) };
    }

    private async Task<SiteResultDto> TestAsync(string id, CancellationToken cancellationToken)
    {
        if (!SiteStore.IsId(id) || await _sites.GetAsync(id, cancellationToken).ConfigureAwait(false) is not { } site)
            return Fail("That site no longer exists.");
        if (site.HostKeys.Count == 0)
            return new SiteResultDto { Ok = false, Problem = SiteProblems.HostKeyNotAccepted, Message = "The server's key has not been accepted yet." };

        RcloneLease lease;
        try
        {
            lease = await AcquireAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (RcloneUnavailableException e)
        {
            return Fail(e.Message);
        }

        using (lease)
        {
            try
            {
                var fs = await SiteRemote.BuildAsync(site, lease.Client, cancellationToken).ConfigureAwait(false);
                var listing = await lease.Client.CallAsync("operations/list", new JsonObject
                {
                    ["fs"] = fs,
                    ["remote"] = site.RemoteFolder.TrimStart('/'),
                }, cancellationToken).ConfigureAwait(false);
                var count = listing["list"] is JsonArray items ? items.Count : 0;
                return new SiteResultDto { Ok = true, Message = $"Connected. The folder holds {count} item{(count == 1 ? "" : "s")}." };
            }
            catch (RcloneCallException e) when (SshHostKeyProblem.Parse(e.Message) is { } problem)
            {
                return new SiteResultDto
                {
                    Ok = false,
                    Problem = SiteProblems.HostKeyChanged,
                    Message = problem.Kind == SshHostKeyProblemKind.Changed
                        ? "The server's key has changed. This can mean the connection is being intercepted. Do not continue unless the server's owner confirms the new key."
                        : "The server no longer offers the kind of key that was accepted. Do not continue unless the server's owner confirms the change.",
                    OfferedFingerprint = problem.OfferedFingerprint,
                    AcceptedFingerprints = [.. problem.PinnedFingerprints],
                };
            }
            catch (Exception e) when (e is RcloneCallException or RcloneUnavailableException or SiteStoreException)
            {
                // RcloneClient has already redacted the text.
                return Fail(e.Message);
            }
        }
    }

    /// <summary>
    /// A lease on rclone, starting it if needed. The first time for an rclone the version is checked: Sites has SFTP only
    /// for now, so one older than <see cref="RcloneSetup.MinimumForSftp"/> is refused.
    /// </summary>
    private async Task<RcloneLease> AcquireAsync(CancellationToken cancellationToken)
    {
        RcloneSupervisor supervisor;
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (_supervisor is null)
            {
                var rclone = _findRclone(_config.OwnRclonePath);
                if (rclone is null)
                {
                    _rcloneProblem = _config.OwnRclonePath is null
                        ? "rclone is not on this PC yet. Choose your own rclone in Options › Sites."
                        : "The rclone chosen in Options › Sites was not found.";
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
        }
        finally
        {
            _gate.Release();
        }

        return await supervisor.AcquireAsync(cancellationToken).ConfigureAwait(false);
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

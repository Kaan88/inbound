using DownloadManager.Contracts;
using DownloadManager.Persistence;
using DownloadManager.Sites;

namespace DownloadManager.Runtime.Sites;

/// <summary>
/// SITES.11 R1 (sites-rework.md, "R1 design"): what the Host knows of a site beyond its file. Whether it was reachable
/// (in memory only), when it was last used (memory first, the file at most every ten minutes), and the unsaved sites of
/// "Open an address once" (held by <see cref="SiteStore"/> in memory).
/// </summary>
public sealed partial class SitesCommands
{
    /// <summary>The site file's "last used" is written at most this often; the Host serves the newer value from memory.</summary>
    public static readonly TimeSpan LastUsedWriteEvery = TimeSpan.FromMinutes(10);

    private readonly Lock _memory = new();
    private readonly Dictionary<string, string> _reach = new(StringComparer.Ordinal);
    private readonly Dictionary<string, DateTimeOffset> _lastUsed = new(StringComparer.Ordinal);
    // Sites whose "last used" write is pending: one at a time per site, never waited for by a browse.
    private readonly HashSet<string> _stamping = new(StringComparer.Ordinal);
    // The "last used" each site's file holds, as last read or written, so a browse within ten minutes costs nothing.
    private readonly Dictionary<string, DateTimeOffset> _stamped = new(StringComparer.Ordinal);
    // One pending sites.changed for reach changes at a time, sent in order: a flaky server can't flood clients.
    private readonly SemaphoreSlim _reachNotice = new(1, 1);
    private int _reachPending;

    /// <summary>Reach changes within this long go out as one <c>sites.changed</c>.</summary>
    private static readonly TimeSpan ReachNoticeDelay = TimeSpan.FromMilliseconds(250);

    /// <summary>Records whether the site answered; a change is sent as <c>sites.changed</c> (the sidebar's dot).</summary>
    private void SetReach(string siteId, string reach)
    {
        lock (_memory)
        {
            if (_reach.TryGetValue(siteId, out var current) && current == reach) return;
            _reach[siteId] = reach;
        }

        if (Interlocked.Exchange(ref _reachPending, 1) == 0) _ = Task.Run(RaiseReachAsync);
    }

    private async Task RaiseReachAsync()
    {
        try
        {
            // Serialised, and the flag is cleared before the list is read: a change after this point sends one more
            // notice after this one, which reads the newer state, so clients end with the last dot.
            await _reachNotice.WaitAsync().ConfigureAwait(false);
            try
            {
                await Task.Delay(ReachNoticeDelay, _time).ConfigureAwait(false);
                Interlocked.Exchange(ref _reachPending, 0);
                await RaiseChangedAsync(CancellationToken.None).ConfigureAwait(false);
            }
            finally
            {
                _reachNotice.Release();
            }
        }
        catch (ObjectDisposedException)
        {
            // Sites is shutting down.
        }
    }

    /// <summary>
    /// The user used the site (a first page browsed, a transfer started). Kept in memory at once; the file is written in
    /// the background, under the site gate, from the site as it is then, and only when the stored value is empty or at
    /// least <see cref="LastUsedWriteEvery"/> old. A site that is gone or unsaved is not written.
    /// </summary>
    private void Touch(string siteId)
    {
        var now = _time.GetUtcNow();
        lock (_memory)
        {
            _lastUsed[siteId] = now;
            if (_stamped.TryGetValue(siteId, out var written) && now - written < LastUsedWriteEvery) return;
            if (!_stamping.Add(siteId)) return;
        }

        _ = Task.Run(() => StampAsync(siteId, now));
    }

    private async Task StampAsync(string siteId, DateTimeOffset now)
    {
        try
        {
            await _gate.WaitAsync().ConfigureAwait(false);
            try
            {
                // Read again under the gate: every other writer holds it too, so this never puts back an older copy.
                if (await _sites.GetAsync(siteId).ConfigureAwait(false) is not { Unsaved: false } site) return;
                if (site.LastUsedAt is { } last && now - last < LastUsedWriteEvery)
                {
                    lock (_memory) _stamped[siteId] = last;
                    return;
                }

                await _sites.SaveAsync(site with { LastUsedAt = now }, keepRevision: true).ConfigureAwait(false);
                lock (_memory) _stamped[siteId] = now;
            }
            finally
            {
                _gate.Release();
            }
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or SiteStoreException or ObjectDisposedException)
        {
            _log($"Sites: when site {siteId} was last used could not be saved ({e.GetType().Name}).");
        }
        finally
        {
            lock (_memory) _stamping.Remove(siteId);
        }
    }

    /// <summary>The memory's view of a site for <see cref="SiteDto"/>: its reach and the newer "last used".</summary>
    private (string? Reach, DateTimeOffset? LastUsed) MemoryOf(SiteRecord site)
    {
        lock (_memory)
        {
            _reach.TryGetValue(site.Id, out var reach);
            DateTimeOffset? used = site.LastUsedAt;
            if (_lastUsed.TryGetValue(site.Id, out var inMemory) && (used is null || inMemory > used)) used = inMemory;
            return (reach, used);
        }
    }

    private void ForgetMemory(string siteId)
    {
        lock (_memory)
        {
            _reach.Remove(siteId);
            _lastUsed.Remove(siteId);
            _stamped.Remove(siteId);
        }
    }

    // ---- Open an address once ----------------------------------------------------------------------------------------

    /// <summary>
    /// <c>sites.openOnce</c>: an address becomes an unsaved site, or (with <c>Id</c>) an open unsaved site gets its
    /// password. An open unsaved site of the same kind, server, port and user is used again rather than opened twice. A
    /// saved site is never reached this way.
    /// </summary>
    private async Task<SiteResultDto> OpenOnceAsync(SiteOpenOnceRequest request, CancellationToken cancellationToken)
    {
        var now = _time.GetUtcNow();
        if (!string.IsNullOrEmpty(request.Id))
        {
            if (!SiteStore.IsId(request.Id) || !_sites.IsUnsaved(request.Id)) return Fail("That address is no longer open.");
            if (string.IsNullOrEmpty(request.Password)) return Fail("Type the password.");
            return await SetUnsavedPasswordAsync(request.Id, request.Password, now, cancellationToken).ConfigureAwait(false);
        }

        if (SiteAddress.Parse(request.Address, out var problem) is not { } address) return Fail(problem!);
        if (SitePaths.Normalize(address.Folder) is not { } folder) return Fail("The folder in the address must be a path such as /pub/files, without . or .. in it.");
        // ftps:// on port 990 is the old implicit TLS; on any other port, the usual explicit TLS.
        var tls = address.Tls == SiteTlsModes.Explicit && address.Port == 990 ? SiteTlsModes.Implicit : address.Tls;

        var open = (await _sites.ListAsync(cancellationToken, includeUnsaved: true).ConfigureAwait(false)).FirstOrDefault(s => s.Unsaved
            && s.Kind == address.Kind && string.Equals(s.Host, address.Host, StringComparison.OrdinalIgnoreCase) && s.Port == address.Port
            && s.User == address.User && s.Tls == tls && s.Url == address.Url);
        if (open is not null)
        {
            if (!string.IsNullOrEmpty(request.Password)) return await SetUnsavedPasswordAsync(open.Id, request.Password, now, cancellationToken).ConfigureAwait(false);
            return new SiteResultDto { Ok = true, Site = ToDto(open) };
        }

        string? sealedPassword;
        try
        {
            sealedPassword = string.IsNullOrEmpty(request.Password) ? null : SecretProtector.ProtectSiteSecret(request.Password);
        }
        catch (InvalidOperationException e)
        {
            // No secret protector on this system: nothing is held.
            return Fail(e.Message);
        }

        var site = new SiteRecord
        {
            Id = SiteStore.NewId(),
            Name = address.Host,
            Kind = address.Kind,
            Host = address.Host,
            Port = address.Port,
            User = address.User,
            Tls = tls,
            Url = address.Url,
            Vendor = address.Kind == SiteKinds.WebDav ? "other" : null,
            AuthMethod = SiteAuthMethods.Password,
            SealedPassword = sealedPassword,
            // An SFTP site starts with no accepted key: the usual key question comes first (NeedsHostKey).
            RemoteFolder = address.Kind == SiteKinds.WebDav ? "" : folder,
            CreatedAt = now,
            ModifiedAt = now,
            Unsaved = true,
        };

        try
        {
            await _sites.AddUnsavedAsync(site, cancellationToken).ConfigureAwait(false);
        }
        catch (SiteStoreException e)
        {
            return Fail(e.Message);
        }

        _log($"Sites: an address was opened once as site {site.Id} ({site.Kind}).");
        await RaiseChangedAsync(cancellationToken).ConfigureAwait(false);
        return new SiteResultDto { Ok = true, Site = ToDto(site) };
    }

    private async Task<SiteResultDto> SetUnsavedPasswordAsync(string id, string password, DateTimeOffset now, CancellationToken cancellationToken)
    {
        SiteRecord saved;
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (await _sites.GetAsync(id, cancellationToken).ConfigureAwait(false) is not { Unsaved: true } site) return Fail("That address is no longer open.");
            saved = site with { SealedPassword = SecretProtector.ProtectSiteSecret(password), ModifiedAt = now };
            await _sites.SaveAsync(saved, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception e) when (e is SiteStoreException or InvalidOperationException)
        {
            return Fail(e.Message);
        }
        finally
        {
            _gate.Release();
        }

        ForgetListings(id);
        await RaiseChangedAsync(cancellationToken).ConfigureAwait(false);
        return new SiteResultDto { Ok = true, Site = ToDto(saved) };
    }

    /// <summary><c>sites.close</c>: forgets an unsaved site as <c>sites.remove</c> would. A saved site is refused.</summary>
    private async Task<SiteResultDto> CloseAsync(string id, CancellationToken cancellationToken)
    {
        if (!SiteStore.IsId(id) || !_sites.IsUnsaved(id)) return Fail("Only an address opened once can be closed.");
        var result = await RemoveAsync(id, cancellationToken, unsavedOnly: true).ConfigureAwait(false);
        if (result.Ok) _log($"Sites: site {id}, opened once, was closed.");
        return result;
    }

    /// <summary><c>sites.keep</c> ("Remember"): an unsaved site is written as a saved one. A saved site is refused.</summary>
    private async Task<SiteResultDto> KeepAsync(SiteKeepRequest request, CancellationToken cancellationToken)
    {
        if (!SiteStore.IsId(request.Id) || !_sites.IsUnsaved(request.Id)) return Fail("Only an address opened once can be remembered.");
        if (request.Name is { Length: > 200 }) return Fail("That name is too long.");
        SiteRecord? kept;
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            // SITES.11 review: saved, it would no longer be guarded as unsaved, and rclone would offer this PC's ssh-agent keys.
            if (await _sites.GetAsync(request.Id, cancellationToken).ConfigureAwait(false) is { Kind: SiteKinds.Sftp, AuthMethod: SiteAuthMethods.Password, SealedPassword: null })
                return Fail("Type the password for this server before remembering it.");
            kept = await _sites.KeepAsync(request.Id, request.Name, _time.GetUtcNow(), cancellationToken).ConfigureAwait(false);
        }
        catch (Exception e) when (e is SiteStoreException or IOException or UnauthorizedAccessException)
        {
            return Fail(e is SiteStoreException ? e.Message : "The remote could not be saved.");
        }
        finally
        {
            _gate.Release();
        }

        if (kept is null) return Fail("That address is no longer open.");
        _log($"Sites: site {kept.Id}, opened once, was saved.");
        await RaiseChangedAsync(cancellationToken).ConfigureAwait(false);
        return new SiteResultDto { Ok = true, Site = ToDto(kept) };
    }
}

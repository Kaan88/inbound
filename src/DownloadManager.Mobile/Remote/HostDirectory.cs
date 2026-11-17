using DownloadManager.Contracts;

namespace DownloadManager.Mobile.Remote;

/// <summary>What the pairing screen sends.</summary>
public sealed record PairRequest(string AddressText, string Password, string? Name, bool PlainHttpConfirmed);

/// <summary>What pairing gave: the host added (or paired again), or an error in words.</summary>
public sealed record PairOutcome(HostEntry? Host, string? Error, bool NeedsPlainHttpConfirmation = false)
{
    public bool Ok => Host is not null;
}

/// <summary>
/// The hosts the screens can show (C5): this phone first, then the paired servers, and which one is shown. Pairing uses
/// the server's existing password pairing for apps (<c>POST /api/pair</c>, role <c>remoteClient</c>, as the desktop window
/// does, design D9 section 4); the password is never kept, the token goes to the <see cref="ISecretStore"/> and nowhere
/// else. Thread-safe; <see cref="Changed"/> is raised on the caller's thread.
/// </summary>
public sealed class HostDirectory
{
    public delegate Task<RemotePairResult> PairCall(Uri address, string password, string clientName, bool plainHttpConfirmed, CancellationToken cancellationToken);

    public delegate Task<bool> UnpairCall(ServerTarget target, CancellationToken cancellationToken);

    private const string SecretPrefix = "host-token-";
    private readonly object _gate = new();
    private readonly IHostListStore _store;
    private readonly ISecretStore _secrets;
    private readonly string _clientName;
    private readonly TimeProvider _time;
    private readonly PairCall _pair;
    private readonly UnpairCall _unpair;
    private readonly HostEntry _phone = new();
    private HostListData _data;

    /// <param name="clientName">The name the server lists this phone under, such as "Inbound on Galaxy S24 FE".</param>
    public HostDirectory(IHostListStore store, ISecretStore secrets, string clientName, TimeProvider? time = null,
        PairCall? pair = null, UnpairCall? unpair = null)
    {
        _store = store;
        _secrets = secrets;
        _clientName = clientName;
        _time = time ?? TimeProvider.System;
        _pair = pair ?? ((address, password, name, confirmed, ct) => RemoteServerHttp.PairAsync(address, password, name, confirmed, ct));
        _unpair = unpair ?? RemoteServerHttp.UnpairAsync;
        _data = store.Load();
        _data.Servers.RemoveAll(s => string.IsNullOrEmpty(s.Id) || s.IsPhone || s.AddressUri is null);
        if (Find(_data.CurrentId) is null) _data.CurrentId = HostEntry.PhoneId;
    }

    /// <summary>The list changed, or another host is now shown.</summary>
    public event Action? Changed;

    /// <summary>This phone, then the servers in the order they were paired. Copies: changing them changes nothing.</summary>
    public IReadOnlyList<HostEntry> Hosts
    {
        get
        {
            lock (_gate) return [_phone.Copy(), .. _data.Servers.Select(s => s.Copy())];
        }
    }

    /// <summary>The host the screens show.</summary>
    public HostEntry Current
    {
        get
        {
            lock (_gate) return (Find(_data.CurrentId) ?? _phone).Copy();
        }
    }

    public HostCapabilities Capabilities => Current.IsPhone ? HostCapabilities.Phone : HostCapabilities.Remote;

    private HostEntry? Find(string id) => id == HostEntry.PhoneId ? _phone : _data.Servers.FirstOrDefault(s => s.Id == id);

    /// <summary>Shows another host. False when there is no such host; nothing changes when it is already shown.</summary>
    public bool Select(string id)
    {
        lock (_gate)
        {
            if (Find(id) is null) return false;
            if (_data.CurrentId == id) return true;
            _data.CurrentId = id;
            SaveQuietly();
        }

        Changed?.Invoke();
        return true;
    }

    /// <summary>
    /// The connection target of a server: its address and its token. Null for this phone, or when the token can't be read
    /// (the Keystore key was lost): the user then pairs again.
    /// </summary>
    public ServerTarget? TargetFor(HostEntry host)
    {
        if (host.IsPhone || host.AddressUri is not { } address) return null;
        string? token;
        try { token = _secrets.Read(SecretPrefix + host.Id); }
        catch (Exception e) when (e is not OutOfMemoryException) { token = null; }
        return string.IsNullOrEmpty(token) ? null : ServerTarget.Remote(host.Id, host.Name, address, token, host.PlainHttpConfirmed);
    }

    /// <summary>
    /// Checks an address as typed. <paramref name="needsConfirmation"/>: plain <c>http://</c> to another machine, which the
    /// user must allow for this server before the password is sent (D9 section 4; the owner allows plain http).
    /// </summary>
    public static bool CheckAddress(string text, out Uri address, out string error, out bool needsConfirmation)
    {
        needsConfirmation = false;
        if (!ServerAddress.TryNormalize(text, out address, out error)) return false;
        needsConfirmation = ServerAddress.NeedsPlainHttpConfirmation(address);
        return true;
    }

    /// <summary>
    /// Pairs with a server by its password and saves it (or pairs again with a server already in the list, at the same
    /// address: it keeps its id and gets the new token). The new host is not selected; the caller decides.
    /// </summary>
    public async Task<PairOutcome> PairAsync(PairRequest request, CancellationToken cancellationToken = default)
    {
        if (!CheckAddress(request.AddressText, out var address, out var error, out var needsConfirmation)) return new PairOutcome(null, error);
        if (needsConfirmation && !request.PlainHttpConfirmed)
            return new PairOutcome(null, "This address starts with http://, so the password and the sign-in would cross the network unencrypted. Allow that for this server to go on.",
                NeedsPlainHttpConfirmation: true);
        if (string.IsNullOrEmpty(request.Password)) return new PairOutcome(null, "Type the server's password.");

        var paired = await _pair(address, request.Password, _clientName, request.PlainHttpConfirmed, cancellationToken).ConfigureAwait(false);
        if (!paired.Ok || string.IsNullOrEmpty(paired.Token)) return new PairOutcome(null, paired.Error ?? "The server refused the pairing.");

        HostEntry entry;
        ServerTarget? replaced = null;
        lock (_gate)
        {
            var existing = _data.Servers.FirstOrDefault(s => s.Address == address.AbsoluteUri);
            if (existing is not null) replaced = TargetFor(existing);
            entry = existing?.Copy() ?? new HostEntry { Id = Guid.NewGuid().ToString("N") };
            entry.Name = string.IsNullOrWhiteSpace(request.Name) ? existing?.Name ?? address.Host : request.Name.Trim();
            entry.Address = address.AbsoluteUri;
            entry.PlainHttpConfirmed = request.PlainHttpConfirmed && needsConfirmation;
            entry.HostKind = paired.HostKind ?? existing?.HostKind;
            entry.TokenId = paired.TokenId;
            entry.PairedAt = _time.GetUtcNow();
            entry.LastSeenAt = _time.GetUtcNow();
            try
            {
                _secrets.Write(SecretPrefix + entry.Id, paired.Token);
                var index = existing is null ? -1 : _data.Servers.IndexOf(existing);
                if (index >= 0) _data.Servers[index] = entry;
                else _data.Servers.Add(entry);
                try { _store.Save(_data); }
                catch
                {
                    if (index >= 0) _data.Servers[index] = existing!;
                    else _data.Servers.Remove(entry);
                    throw;
                }
            }
            catch (Exception e) when (e is not OutOfMemoryException)
            {
                // Not kept here, so the server must not keep it either. A re-pairing that failed keeps the old token.
                if (replaced?.Token is { } old)
                {
                    try { _secrets.Write(SecretPrefix + entry.Id, old); }
                    catch (Exception again) when (again is not OutOfMemoryException) { /* the entry then asks to pair again */ }
                }
                else
                {
                    try { _secrets.Delete(SecretPrefix + entry.Id); }
                    catch (Exception again) when (again is not OutOfMemoryException) { /* no entry reads it */ }
                }

                _ = ForgetAsync(new ServerTarget[] { ServerTarget.Remote(entry.Id, entry.Name, address, paired.Token, request.PlainHttpConfirmed) });
                return new PairOutcome(null, "The pairing worked, but this phone couldn't store it safely, so it was undone. Try again.");
            }
        }

        // The old token of a server paired again is of no use any more.
        if (replaced is not null && replaced.Token != paired.Token) _ = ForgetAsync([replaced]);
        Changed?.Invoke();
        return new PairOutcome(entry.Copy(), null);
    }

    /// <summary>
    /// Removes a server from the list and forgets its token here and, best effort, on the server. True when the server
    /// forgot it too (false: it could not be reached; the pairing can be removed in its web UI). The phone is shown when
    /// the removed server was.
    /// </summary>
    public async Task<bool> RemoveAsync(string id, CancellationToken cancellationToken = default)
    {
        ServerTarget? target;
        lock (_gate)
        {
            var entry = _data.Servers.FirstOrDefault(s => s.Id == id);
            if (entry is null) return true;
            target = TargetFor(entry);
            _data.Servers.Remove(entry);
            if (_data.CurrentId == id) _data.CurrentId = HostEntry.PhoneId;
            SaveQuietly();
            try { _secrets.Delete(SecretPrefix + id); }
            catch (Exception e) when (e is not OutOfMemoryException) { /* the entry is gone, so the secret is never read again */ }
        }

        Changed?.Invoke();
        if (target is null) return false;
        try { return await _unpair(target, cancellationToken).ConfigureAwait(false); }
        catch (Exception e) when (e is not OutOfMemoryException) { return false; }
    }

    /// <summary>A connection to a server got its snapshot: notes when, and the kind the server says it is.</summary>
    public void Seen(string id, string? hostKind)
    {
        lock (_gate)
        {
            var entry = _data.Servers.FirstOrDefault(s => s.Id == id);
            if (entry is null) return;
            var now = _time.GetUtcNow();
            // At most one write a minute for a server that reconnects often.
            if (entry.HostKind == (hostKind ?? entry.HostKind) && entry.LastSeenAt is { } last && now - last < TimeSpan.FromMinutes(1)) return;
            entry.HostKind = hostKind ?? entry.HostKind;
            entry.LastSeenAt = now;
            SaveQuietly();
        }

        Changed?.Invoke();
    }

    private void SaveQuietly()
    {
        try { _store.Save(_data); }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            // Shown as chosen for this run; the next start shows what was last saved.
        }
    }

    private async Task ForgetAsync(IEnumerable<ServerTarget> targets)
    {
        foreach (var target in targets)
        {
            try { await _unpair(target, CancellationToken.None).ConfigureAwait(false); }
            catch (Exception e) when (e is not OutOfMemoryException) { }
        }
    }
}

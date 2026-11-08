using System.Collections.Concurrent;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace DownloadManager.Server.Auth;

/// <summary>One signed-in browser. <see cref="Ended"/> fires on sign-out, a password change and expiry, and closes its WebSockets.</summary>
public sealed class Session
{
    private readonly CancellationTokenSource _ended = new();
    private int _sockets;

    internal Session(string key, DateTimeOffset createdAt, DateTimeOffset lastSeenAt)
    {
        Key = key;
        CreatedAt = createdAt;
        LastSeenAt = lastSeenAt;
        SavedLastSeenAt = lastSeenAt;
    }

    internal string Key { get; }
    public DateTimeOffset CreatedAt { get; }
    public DateTimeOffset LastSeenAt { get; internal set; }
    /// <summary>The last-seen time the sessions file holds for this session.</summary>
    internal DateTimeOffset SavedLastSeenAt { get; set; }
    public CancellationToken Ended => _ended.Token;
    public bool HasOpenSocket => Volatile.Read(ref _sockets) > 0;

    internal void SocketOpened() => Interlocked.Increment(ref _sockets);
    internal void SocketClosed() => Interlocked.Decrement(ref _sockets);

    internal void End()
    {
        try
        {
            _ended.Cancel();
        }
        catch (ObjectDisposedException)
        {
            // ended already
        }
    }
}

/// <summary>
/// Signed-in browsers. The cookie holds 32 random bytes; the store keeps only their SHA-256, so a lookup's timing tells
/// nothing about a real id. A session ends after <see cref="IdleTimeout"/> without a request (an open WebSocket counts
/// as one) and after <see cref="AbsoluteTimeout"/> whatever happens.
/// <para>
/// With a file (<see cref="FileName"/> in the data folder) the sessions survive a restart: the file holds each
/// session's hash, when it began and when it was last seen, never the id itself, written atomically (temporary file,
/// flush, replace) and owner-only (mode 600) off Windows. A new session, a sign-out, a password change and an expiry
/// are written at once. Last-seen times are written at most once per <see cref="SaveEvery"/> per session, and on a
/// clean stop (<see cref="Flush"/>), so a busy browser doesn't keep the disk awake; after a kill a session may end up
/// to that much earlier than it would have. The file also notes a stamp of the password it was written under: a
/// password changed while the server was down (by <c>DOWNLOADMANAGER_PASSWORD</c>, or a deleted <c>auth.json</c>)
/// drops every saved session. A damaged file is deleted, and everyone signs in again.
/// </para>
/// </summary>
public sealed class SessionStore
{
    public const string FileName = "sessions.json";
    private const int MaxSessions = 200;

    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web) { WriteIndented = false };

    private readonly ConcurrentDictionary<string, Session> _sessions = new(StringComparer.Ordinal);
    private readonly TimeProvider _time;
    private readonly string? _path;
    private readonly Func<string?> _passwordStamp;
    private readonly Action<string> _warn;
    private readonly object _saveGate = new();

    /// <summary>Sessions in memory only: a restart signs everyone out.</summary>
    public SessionStore(TimeProvider time, TimeSpan idleTimeout, TimeSpan absoluteTimeout)
        : this(time, idleTimeout, absoluteTimeout, path: null, passwordStamp: null, warn: null)
    {
    }

    /// <param name="path">The sessions file, or null to keep them in memory only.</param>
    /// <param name="passwordStamp">A stamp of the password in force (<see cref="StampOf"/>); null while none is set.</param>
    /// <param name="warn">Where a failed read or write is reported.</param>
    public SessionStore(TimeProvider time, TimeSpan idleTimeout, TimeSpan absoluteTimeout, string? path, Func<string?>? passwordStamp,
        Action<string>? warn)
    {
        _time = time;
        IdleTimeout = idleTimeout;
        AbsoluteTimeout = absoluteTimeout;
        _path = path;
        _passwordStamp = passwordStamp ?? (() => null);
        _warn = warn ?? (_ => { });
        // A tenth of the idle time with short test timeouts; ten minutes against the default 24 hours.
        SaveEvery = TimeSpan.FromTicks(Math.Min(TimeSpan.FromMinutes(10).Ticks, Math.Max(1, idleTimeout.Ticks / 10)));
        if (_path is not null) Load();
    }

    public TimeSpan IdleTimeout { get; }
    public TimeSpan AbsoluteTimeout { get; }
    /// <summary>How stale a session's saved last-seen time may get before a request writes it.</summary>
    public TimeSpan SaveEvery { get; }
    public int Count => _sessions.Count;

    /// <summary>The stamp the file keeps of a password: a SHA-256 of its salt and hash, which tells nothing more than they do.</summary>
    public static string? StampOf(PasswordRecord? password) =>
        password is null ? null : Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(password.Salt + ":" + password.Hash)));

    /// <summary>A new session and the id to put in the cookie.</summary>
    public (string Id, Session Session) Create()
    {
        var id = Base64Url(RandomNumberGenerator.GetBytes(32));
        var key = KeyFor(id);
        var now = _time.GetUtcNow();
        var session = new Session(key, now, now);
        _sessions[key] = session;
        PruneWithoutSaving();
        Save();
        return (id, session);
    }

    /// <summary>The live session for this cookie value, touched; null when there is none or it has expired.</summary>
    public Session? Find(string? id)
    {
        if (string.IsNullOrEmpty(id) || id.Length > 128) return null;
        if (!_sessions.TryGetValue(KeyFor(id), out var session)) return null;
        var now = _time.GetUtcNow();
        if (Expired(session, now))
        {
            Remove(session);
            return null;
        }

        Touch(session, now);
        return session;
    }

    /// <summary>When the session ends at the latest, whatever it does.</summary>
    public DateTimeOffset AbsoluteEnd(Session session) => session.CreatedAt + AbsoluteTimeout;

    public void Touch(Session session) => Touch(session, _time.GetUtcNow());

    private void Touch(Session session, DateTimeOffset now)
    {
        session.LastSeenAt = now;
        if (_path is null || now - session.SavedLastSeenAt < SaveEvery || !_sessions.ContainsKey(session.Key)) return;
        // Parallel requests past the mark write once: the first one's save moves the mark for the rest.
        lock (_saveGate)
        {
            if (now - session.SavedLastSeenAt >= SaveEvery) Save();
        }
    }

    public void Remove(Session session)
    {
        if (!RemoveWithoutSaving(session)) return;
        Save();
    }

    /// <summary>Signs out every browser but <paramref name="keep"/> (after a password change).</summary>
    public void RemoveAllExcept(Session? keep)
    {
        var removed = false;
        foreach (var session in _sessions.Values)
        {
            if (!ReferenceEquals(session, keep)) removed |= RemoveWithoutSaving(session);
        }

        // Written even when nothing was removed: the stamp of the new password goes into the file with the kept session.
        if (removed || keep is not null) Save();
    }

    /// <summary>
    /// Writes last-seen times not yet saved (on a clean stop). A session with an open WebSocket counts as seen now:
    /// the socket was its activity, and it is closing.
    /// </summary>
    public void Flush()
    {
        if (_path is null) return;
        var now = _time.GetUtcNow();
        var dirty = false;
        foreach (var session in _sessions.Values)
        {
            if (session.HasOpenSocket) session.LastSeenAt = now;
            dirty |= session.LastSeenAt != session.SavedLastSeenAt;
        }

        if (dirty) Save();
    }

    private bool RemoveWithoutSaving(Session session)
    {
        if (!_sessions.TryRemove(session.Key, out var removed)) return false;
        removed.End();
        return true;
    }

    private bool Expired(Session session, DateTimeOffset now) =>
        now >= session.CreatedAt + AbsoluteTimeout ||
        (!session.HasOpenSocket && now >= session.LastSeenAt + IdleTimeout);

    private void PruneWithoutSaving()
    {
        var now = _time.GetUtcNow();
        foreach (var session in _sessions.Values)
        {
            if (Expired(session, now)) RemoveWithoutSaving(session);
        }

        // Many sign-ins from scripts must not grow the store without end: the least recently seen go first.
        while (_sessions.Count > MaxSessions && _sessions.Values.OrderBy(session => session.LastSeenAt).FirstOrDefault() is { } oldest)
            RemoveWithoutSaving(oldest);
    }

    // ---- The file ----------------------------------------------------------------------------------------------

    private sealed class SessionsFile
    {
        public int Version { get; set; } = 1;
        public string? PasswordStamp { get; set; }
        public List<SavedSession> Sessions { get; set; } = [];
    }

    private sealed class SavedSession
    {
        /// <summary>The SHA-256 of the cookie's id, in hex; never the id.</summary>
        public string Hash { get; set; } = string.Empty;
        public DateTimeOffset CreatedAt { get; set; }
        public DateTimeOffset LastSeenAt { get; set; }
    }

    private void Load()
    {
        SessionsFile? file;
        try
        {
            if (!File.Exists(_path)) return;
            file = JsonSerializer.Deserialize<SessionsFile>(File.ReadAllBytes(_path!), Json);
        }
        catch (Exception ex) when (ex is JsonException or IOException or UnauthorizedAccessException)
        {
            _warn($"The sessions file {_path} could not be read ({ex.GetType().Name}); every browser signs in again.");
            Delete();
            return;
        }

        var stamp = _passwordStamp();
        if (file is null || file.Version != 1 || stamp is null || !string.Equals(file.PasswordStamp, stamp, StringComparison.Ordinal))
        {
            // Another password (or none) than the one these sessions signed in with: they all end.
            if (file is { Sessions.Count: > 0 }) Delete();
            return;
        }

        var now = _time.GetUtcNow();
        var dropped = false;
        foreach (var saved in file.Sessions ?? [])
        {
            var valid = saved.Hash is { Length: 64 } hash && hash.All(Uri.IsHexDigit) && saved.CreatedAt <= now && saved.LastSeenAt >= saved.CreatedAt;
            var session = valid ? new Session(saved.Hash.ToUpperInvariant(), saved.CreatedAt, saved.LastSeenAt) : null;
            if (session is null || Expired(session, now) || !_sessions.TryAdd(session.Key, session)) dropped = true;
        }

        var before = _sessions.Count;
        PruneWithoutSaving();
        if (dropped || _sessions.Count != before) Save();
    }

    /// <summary>
    /// Writes every session. A write that fails must not bring back a session that ended (a sign-out, a password
    /// change), so the file is then deleted: everyone signs in again after a restart, which is the safe side.
    /// </summary>
    private void Save()
    {
        if (_path is null) return;
        lock (_saveGate)
        {
            var sessions = _sessions.Values.ToList();
            var file = new SessionsFile
            {
                PasswordStamp = _passwordStamp(),
                Sessions = sessions.Select(session => new SavedSession
                {
                    Hash = session.Key,
                    CreatedAt = session.CreatedAt,
                    LastSeenAt = session.LastSeenAt,
                }).ToList(),
            };
            try
            {
                AtomicFile.Write(_path, JsonSerializer.SerializeToUtf8Bytes(file, Json));
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                _warn($"The sessions file could not be saved ({ex.GetType().Name}); it is removed, so a restart signs every browser out.");
                Delete();
            }

            // Moved on a failure too, so a full disk isn't retried on every request.
            foreach (var (session, saved) in sessions.Zip(file.Sessions)) session.SavedLastSeenAt = saved.LastSeenAt;
        }
    }

    private void Delete()
    {
        try
        {
            File.Delete(_path!);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            _warn($"The sessions file {_path} could not be removed either ({ex.GetType().Name}).");
        }
    }

    private static string KeyFor(string id) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(id)));

    internal static string Base64Url(byte[] bytes) =>
        Convert.ToBase64String(bytes).TrimEnd('=').Replace('+', '-').Replace('/', '_');
}

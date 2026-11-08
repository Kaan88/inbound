using System.Collections.Concurrent;
using System.Security.Cryptography;
using System.Text;

namespace DownloadManager.Server.Auth;

/// <summary>One signed-in browser. <see cref="Ended"/> fires on sign-out, a password change and expiry, and closes its WebSockets.</summary>
public sealed class Session
{
    private readonly CancellationTokenSource _ended = new();
    private int _sockets;

    internal Session(string key, DateTimeOffset now)
    {
        Key = key;
        CreatedAt = now;
        LastSeenAt = now;
    }

    internal string Key { get; }
    public DateTimeOffset CreatedAt { get; }
    public DateTimeOffset LastSeenAt { get; internal set; }
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
/// Signed-in browsers, in memory only: a restart signs everyone out. The cookie holds 32 random bytes; the store
/// keeps only their SHA-256, so a lookup's timing tells nothing about a real id. A session ends after
/// <see cref="IdleTimeout"/> without a request (an open WebSocket counts as one) and after
/// <see cref="AbsoluteTimeout"/> whatever happens.
/// </summary>
public sealed class SessionStore(TimeProvider time, TimeSpan idleTimeout, TimeSpan absoluteTimeout)
{
    private const int MaxSessions = 200;

    private readonly ConcurrentDictionary<string, Session> _sessions = new(StringComparer.Ordinal);

    public TimeSpan IdleTimeout { get; } = idleTimeout;
    public TimeSpan AbsoluteTimeout { get; } = absoluteTimeout;
    public int Count => _sessions.Count;

    /// <summary>A new session and the id to put in the cookie.</summary>
    public (string Id, Session Session) Create()
    {
        var id = Base64Url(RandomNumberGenerator.GetBytes(32));
        var key = KeyFor(id);
        var session = new Session(key, time.GetUtcNow());
        _sessions[key] = session;
        Prune();
        return (id, session);
    }

    /// <summary>The live session for this cookie value, touched; null when there is none or it has expired.</summary>
    public Session? Find(string? id)
    {
        if (string.IsNullOrEmpty(id) || id.Length > 128) return null;
        if (!_sessions.TryGetValue(KeyFor(id), out var session)) return null;
        var now = time.GetUtcNow();
        if (Expired(session, now))
        {
            Remove(session);
            return null;
        }

        session.LastSeenAt = now;
        return session;
    }

    /// <summary>When the session ends at the latest, whatever it does.</summary>
    public DateTimeOffset AbsoluteEnd(Session session) => session.CreatedAt + AbsoluteTimeout;

    public void Touch(Session session) => session.LastSeenAt = time.GetUtcNow();

    public void Remove(Session session)
    {
        if (_sessions.TryRemove(session.Key, out var removed)) removed.End();
    }

    /// <summary>Signs out every browser but <paramref name="keep"/> (after a password change).</summary>
    public void RemoveAllExcept(Session? keep)
    {
        foreach (var session in _sessions.Values)
        {
            if (!ReferenceEquals(session, keep)) Remove(session);
        }
    }

    private bool Expired(Session session, DateTimeOffset now) =>
        now >= session.CreatedAt + AbsoluteTimeout ||
        (!session.HasOpenSocket && now >= session.LastSeenAt + IdleTimeout);

    private void Prune()
    {
        var now = time.GetUtcNow();
        foreach (var session in _sessions.Values)
        {
            if (Expired(session, now)) Remove(session);
        }

        // Many sign-ins from scripts must not grow the store without end: the least recently seen go first.
        while (_sessions.Count > MaxSessions && _sessions.Values.OrderBy(session => session.LastSeenAt).FirstOrDefault() is { } oldest)
            Remove(oldest);
    }

    private static string KeyFor(string id) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(id)));

    internal static string Base64Url(byte[] bytes) =>
        Convert.ToBase64String(bytes).TrimEnd('=').Replace('+', '-').Replace('/', '_');
}

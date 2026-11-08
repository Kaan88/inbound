using System.Security.Cryptography;
using System.Text;

namespace DownloadManager.Server.Auth;

public sealed class TokenDto
{
    public string Id { get; set; } = string.Empty;
    public string Name { get; set; } = string.Empty;
    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset? LastUsedAt { get; set; }

    /// <summary><see cref="TokenRoles.Extension"/> or <see cref="TokenRoles.RemoteClient"/>.</summary>
    public string Role { get; set; } = TokenRoles.Extension;
}

/// <summary>
/// Pairing an app with this server.
/// <list type="bullet">
/// <item>The browser extension: a signed-in user asks for a code: 8 characters from 32 that can't be mistaken for
/// each other (no I, O, 0 or 1), good for 10 minutes and for one pairing. Asking again replaces it, and 1,000 wrong
/// tries on it end it (from any address: the waits are per address). The extension sends the code to <c>/api/pair</c>
/// and gets a token that only ever opens the extension's role
/// (<see cref="DownloadManager.Contracts.BridgeRole.ServerExtension"/>). A code makes extension tokens only.</item>
/// <item>A desktop app managing this server sends the server's password instead (<see cref="ServerAuth"/> checks it)
/// and gets a <see cref="TokenRoles.RemoteClient"/> token (<see cref="Issue"/>).</item>
/// </list>
/// A token is 32 random bytes; only its SHA-256 is kept. A token can be revoked, and revoking it ends the WebSockets it
/// has open (<see cref="Lifetime"/>).
/// </summary>
public sealed class Pairing(AuthStore store, TimeProvider time)
{
    public const string Alphabet = "ABCDEFGHJKLMNPQRSTUVWXYZ23456789";
    public const int CodeLength = 8;
    public const int MaxTokens = 50;
    public const string TokenPrefix = "ibt_";
    public static readonly TimeSpan CodeLifetime = TimeSpan.FromMinutes(10);
    /// <summary>
    /// Wrong tries, from all addresses together, that end a code. A code is one of 32^8 (about 10^12), so even this
    /// many guesses have less than a one-in-a-billion chance; a lower number would let anyone cancel the owner's code
    /// with a few wrong tries.
    /// </summary>
    public const int MaxWrongTries = 1000;

    private readonly object _gate = new();
    /// <summary>One per token with something open (a WebSocket); cancelled when the token goes.</summary>
    private readonly Dictionary<string, CancellationTokenSource> _lifetimes = new(StringComparer.Ordinal);
    private string? _code;
    private DateTimeOffset _codeExpires;
    private int _wrongTries;

    public (string Code, DateTimeOffset ExpiresAt) NewCode()
    {
        var code = new StringBuilder(CodeLength);
        for (var i = 0; i < CodeLength; i++) code.Append(Alphabet[RandomNumberGenerator.GetInt32(Alphabet.Length)]);
        lock (_gate)
        {
            _code = code.ToString();
            _codeExpires = time.GetUtcNow() + CodeLifetime;
            _wrongTries = 0;
            return (_code, _codeExpires);
        }
    }

    /// <summary>Whether a code is waiting and when it expires (for the page that shows it).</summary>
    public DateTimeOffset? CodeExpiresAt
    {
        get
        {
            lock (_gate) return _code is not null && time.GetUtcNow() < _codeExpires ? _codeExpires : null;
        }
    }

    public void CancelCode()
    {
        lock (_gate) _code = null;
    }

    /// <summary>The new token, or null for a wrong, used or expired code. A right code is used up.</summary>
    public (string Token, TokenDto Record)? Redeem(string? code, string? name)
    {
        var given = Normalize(code);
        lock (_gate)
        {
            if (_code is null) return null;
            if (time.GetUtcNow() >= _codeExpires)
            {
                _code = null;
                return null;
            }

            if (given is null || !CryptographicOperations.FixedTimeEquals(Encoding.ASCII.GetBytes(given), Encoding.ASCII.GetBytes(_code)))
            {
                if (++_wrongTries >= MaxWrongTries) _code = null;
                return null;
            }

            _code = null;
        }

        return Issue(TokenRoles.Extension, name);
    }

    /// <summary>
    /// A new token of this role. Only a right code (<see cref="Redeem"/>, the extension) or a right password
    /// (<see cref="ServerAuth"/>, a desktop app) gets here.
    /// </summary>
    internal (string Token, TokenDto Record) Issue(string role, string? name)
    {
        var token = TokenPrefix + SessionStore.Base64Url(RandomNumberGenerator.GetBytes(32));
        var record = new TokenRecord
        {
            Id = Convert.ToHexString(RandomNumberGenerator.GetBytes(6)).ToLowerInvariant(),
            Name = CleanName(name, role == TokenRoles.RemoteClient ? "Desktop app" : "Browser"),
            Hash = HashHex(token),
            CreatedAt = time.GetUtcNow(),
            // Left out for the extension, as in files from before roles.
            Role = role == TokenRoles.Extension ? null : role,
        };
        store.AddToken(record, MaxTokens);
        // Too many tokens push the oldest unused one out; whatever it had open closes.
        EndRemoved();
        return (token, ToDto(record));
    }

    /// <summary>The paired app this bearer token belongs to, or null (also for a role this version doesn't know).</summary>
    public TokenDto? Authenticate(string? token)
    {
        if (string.IsNullOrEmpty(token) || !token.StartsWith(TokenPrefix, StringComparison.Ordinal) || token.Length > 128) return null;
        var found = store.FindToken(SHA256.HashData(Encoding.UTF8.GetBytes(token)), time.GetUtcNow());
        return found is null || TokenRoles.Of(found) is null ? null : ToDto(found);
    }

    public List<TokenDto> List() => store.Tokens().OrderBy(token => token.CreatedAt).Select(ToDto).ToList();

    public bool Revoke(string id)
    {
        if (string.IsNullOrEmpty(id) || !store.RemoveToken(id)) return false;
        EndRemoved();
        return true;
    }

    /// <summary>
    /// Unpairs every app after a password change; how many there were. <paramref name="keepExtensions"/> keeps the
    /// browser extensions' tokens only: a desktop app's token was made with the password and has a signed-in browser's
    /// rights, so it goes with the old password as the sessions do (and so does a role this version doesn't know).
    /// </summary>
    public int RevokeAll(bool keepExtensions = false)
    {
        var removed = keepExtensions
            ? store.RemoveTokens(token => TokenRoles.Of(token) != TokenRoles.Extension)
            : store.RemoveAllTokens();
        EndRemoved();
        return removed;
    }

    /// <summary>Notes that the token with this id is in use without a request (its WebSocket is open).</summary>
    public void Touch(string id) => store.TouchToken(id, time.GetUtcNow());

    /// <summary>
    /// Cancelled when the token with this id is revoked, by a password change too, or pushed out by newer ones; already
    /// cancelled when it is gone by now. What a token keeps open beyond one request (a WebSocket) ends with it, as a
    /// cookie session's sockets end with <see cref="Session.Ended"/>.
    /// </summary>
    public CancellationToken Lifetime(string id)
    {
        lock (_lifetimes)
        {
            // Looked up under the lock EndRemoved cancels under: a token removed before this call is seen as gone here,
            // and one removed after it finds this source and cancels it.
            if (!store.Tokens().Any(token => token.Id == id)) return new CancellationToken(canceled: true);
            if (!_lifetimes.TryGetValue(id, out var source)) _lifetimes[id] = source = new CancellationTokenSource();
            return source.Token;
        }
    }

    /// <summary>Cancels the lifetimes of tokens that are no longer in <c>auth.json</c>.</summary>
    private void EndRemoved()
    {
        List<CancellationTokenSource> ended = [];
        lock (_lifetimes)
        {
            if (_lifetimes.Count == 0) return;
            var present = store.Tokens().Select(token => token.Id).ToHashSet(StringComparer.Ordinal);
            foreach (var id in _lifetimes.Keys.Where(id => !present.Contains(id)).ToList())
            {
                ended.Add(_lifetimes[id]);
                _lifetimes.Remove(id);
            }
        }

        // Outside the lock: cancelling runs the sockets' callbacks.
        foreach (var source in ended) source.Cancel();
    }

    /// <summary>Upper case, without spaces or dashes; null when it can't be a code at all.</summary>
    internal static string? Normalize(string? code)
    {
        if (code is null) return null;
        var cleaned = new StringBuilder(CodeLength);
        foreach (var c in code)
        {
            if (c is ' ' or '-') continue;
            var upper = char.ToUpperInvariant(c);
            if (Alphabet.IndexOf(upper) < 0 || cleaned.Length >= CodeLength) return null;
            cleaned.Append(upper);
        }

        return cleaned.Length == CodeLength ? cleaned.ToString() : null;
    }

    private static string CleanName(string? name, string fallback)
    {
        var text = new string((name ?? string.Empty).Where(c => !char.IsControl(c)).ToArray()).Trim();
        if (text.Length > 60) text = text[..60].Trim();
        return text.Length == 0 ? fallback : text;
    }

    private static string HashHex(string token) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(token)));

    private static TokenDto ToDto(TokenRecord record) => new()
    {
        Id = record.Id,
        Name = record.Name,
        CreatedAt = record.CreatedAt,
        LastUsedAt = record.LastUsedAt,
        // A role from a newer version is shown as it is (such a token opens nothing here, see Authenticate).
        Role = TokenRoles.Of(record) ?? record.Role!,
    };
}

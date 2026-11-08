using System.Security.Cryptography;
using System.Text;

namespace DownloadManager.Server.Auth;

public sealed class TokenDto
{
    public string Id { get; set; } = string.Empty;
    public string Name { get; set; } = string.Empty;
    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset? LastUsedAt { get; set; }
}

/// <summary>
/// Pairing a browser extension with this server. A signed-in user asks for a code: 8 characters from 32 that can't be
/// mistaken for each other (no I, O, 0 or 1), good for 10 minutes and for one pairing. Asking again replaces it, and
/// 10 wrong tries on it end it. The extension sends the code to <c>/api/pair</c> and gets a token of 32 random bytes;
/// only the token's SHA-256 is kept. A token can be revoked, and it only ever opens the extension's role
/// (<see cref="DownloadManager.Contracts.BridgeRole.ServerExtension"/>).
/// </summary>
public sealed class Pairing(AuthStore store, TimeProvider time)
{
    public const string Alphabet = "ABCDEFGHJKLMNPQRSTUVWXYZ23456789";
    public const int CodeLength = 8;
    public const int MaxTokens = 50;
    public const string TokenPrefix = "ibt_";
    public static readonly TimeSpan CodeLifetime = TimeSpan.FromMinutes(10);
    private const int MaxWrongTries = 10;

    private readonly object _gate = new();
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

        var token = TokenPrefix + SessionStore.Base64Url(RandomNumberGenerator.GetBytes(32));
        var record = new TokenRecord
        {
            Id = Convert.ToHexString(RandomNumberGenerator.GetBytes(6)).ToLowerInvariant(),
            Name = CleanName(name),
            Hash = HashHex(token),
            CreatedAt = time.GetUtcNow(),
        };
        store.AddToken(record, MaxTokens);
        return (token, ToDto(record));
    }

    /// <summary>The paired browser this bearer token belongs to, or null.</summary>
    public TokenDto? Authenticate(string? token)
    {
        if (string.IsNullOrEmpty(token) || !token.StartsWith(TokenPrefix, StringComparison.Ordinal) || token.Length > 128) return null;
        var found = store.FindToken(SHA256.HashData(Encoding.UTF8.GetBytes(token)), time.GetUtcNow());
        return found is null ? null : ToDto(found);
    }

    public List<TokenDto> List() => store.Tokens().OrderBy(token => token.CreatedAt).Select(ToDto).ToList();

    public bool Revoke(string id) => !string.IsNullOrEmpty(id) && store.RemoveToken(id);

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

    private static string CleanName(string? name)
    {
        var text = new string((name ?? string.Empty).Where(c => !char.IsControl(c)).ToArray()).Trim();
        if (text.Length > 60) text = text[..60].Trim();
        return text.Length == 0 ? "Browser" : text;
    }

    private static string HashHex(string token) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(token)));

    private static TokenDto ToDto(TokenRecord record) => new()
    {
        Id = record.Id,
        Name = record.Name,
        CreatedAt = record.CreatedAt,
        LastUsedAt = record.LastUsedAt,
    };
}

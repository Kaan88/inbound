using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace DownloadManager.Server.Auth;

/// <summary>A paired app's token, as saved: its SHA-256 only, never the token.</summary>
public sealed class TokenRecord
{
    public string Id { get; set; } = string.Empty;
    public string Name { get; set; } = string.Empty;
    public string Hash { get; set; } = string.Empty;
    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset? LastUsedAt { get; set; }

    /// <summary>
    /// What the token may do (<see cref="TokenRoles"/>). Left out for the browser extension, so <c>auth.json</c> files
    /// written before roles existed read as they always did, and an older server reading a newer file sees every token
    /// as an extension's (fewer rights, never more).
    /// </summary>
    public string? Role { get; set; }
}

/// <summary>The roles a paired token can have.</summary>
public static class TokenRoles
{
    /// <summary>The browser extension: the extension's commands only (<c>NativeBridgePolicy.ServerExtension</c>), on <c>POST /api/ipc</c>.</summary>
    public const string Extension = "extension";

    /// <summary>A desktop app managing this server: a signed-in browser's rights, on <c>/api/ws</c> and <c>POST /api/ipc</c>.</summary>
    public const string RemoteClient = "remoteClient";

    /// <summary>The role a saved record has: none means the extension; one this version doesn't know means none at all (null).</summary>
    public static string? Of(TokenRecord record) => record.Role switch
    {
        null or "" or Extension => Extension,
        RemoteClient => RemoteClient,
        _ => null,
    };
}

public sealed class AuthFile
{
    public int Version { get; set; } = 1;
    public PasswordRecord? Password { get; set; }
    public List<TokenRecord> Tokens { get; set; } = [];
}

/// <summary>
/// <c>auth.json</c> in the data folder: the password's hash and the paired apps' token hashes and roles. Neither can be
/// turned back into what it was made from. Written to a temporary file next to it, flushed, then moved over it, so a
/// kill at any point leaves the old file or the new one; owner-only (mode 600) off Windows. A change that can't be
/// saved is undone in memory too, so what is in force is always what the file says.
/// </summary>
public sealed class AuthStore
{
    public const string FileName = "auth.json";

    private static readonly JsonSerializerOptions Json = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    private readonly string _path;
    private readonly object _gate = new();
    private AuthFile _file;

    private AuthStore(string path, AuthFile file)
    {
        _path = path;
        _file = file;
    }

    public string Path => _path;

    public static AuthStore Open(string dataFolder)
    {
        var path = System.IO.Path.Combine(dataFolder, FileName);
        if (!File.Exists(path)) return new AuthStore(path, new AuthFile());
        try
        {
            var file = JsonSerializer.Deserialize<AuthFile>(File.ReadAllText(path, Encoding.UTF8), Json) ?? new AuthFile();
            file.Tokens ??= [];
            return new AuthStore(path, file);
        }
        catch (JsonException)
        {
            // A damaged file would otherwise read as "no password yet" and open the setup page to anyone who comes first.
            throw new ServerStartException(
                $"The sign-in file {path} is damaged. Restore it from a backup, or delete it to set a new password (paired browsers then pair again).");
        }
    }

    public PasswordRecord? Password
    {
        get { lock (_gate) return _file.Password; }
    }

    public void SetPassword(PasswordRecord record)
    {
        lock (_gate)
        {
            var before = _file.Password;
            _file.Password = record;
            SaveOrUndo(() => _file.Password = before);
        }
    }

    public List<TokenRecord> Tokens()
    {
        lock (_gate) return _file.Tokens.Select(Copy).ToList();
    }

    public void AddToken(TokenRecord record, int limit)
    {
        lock (_gate)
        {
            var before = _file.Tokens.ToList();
            _file.Tokens.Add(record);
            // The oldest unused ones go first when there are too many.
            while (_file.Tokens.Count > limit)
                _file.Tokens.Remove(_file.Tokens.OrderBy(token => token.LastUsedAt ?? token.CreatedAt).First());
            SaveOrUndo(() => _file.Tokens = before);
        }
    }

    public bool RemoveToken(string id)
    {
        lock (_gate)
        {
            var before = _file.Tokens.ToList();
            var removed = _file.Tokens.RemoveAll(token => token.Id == id) > 0;
            if (removed) SaveOrUndo(() => _file.Tokens = before);
            return removed;
        }
    }

    /// <summary>Removes every paired app's token; how many there were.</summary>
    public int RemoveAllTokens() => RemoveTokens(_ => true);

    /// <summary>Removes the tokens that <paramref name="which"/> picks; how many there were.</summary>
    public int RemoveTokens(Func<TokenRecord, bool> which)
    {
        lock (_gate)
        {
            var before = _file.Tokens;
            var kept = before.Where(token => !which(token)).ToList();
            var removed = before.Count - kept.Count;
            if (removed == 0) return 0;
            _file.Tokens = kept;
            SaveOrUndo(() => _file.Tokens = before);
            return removed;
        }
    }

    /// <summary>
    /// Notes a use of this token that isn't a request (its WebSocket is still open), saved as <see cref="FindToken"/>
    /// saves: at most once a minute. False when the token is gone.
    /// </summary>
    public bool TouchToken(string id, DateTimeOffset now)
    {
        lock (_gate)
        {
            var token = _file.Tokens.FirstOrDefault(token => token.Id == id);
            if (token is null) return false;
            NoteUse(token, now);
            return true;
        }
    }

    private void SaveOrUndo(Action undo)
    {
        try
        {
            Save();
        }
        catch
        {
            undo();
            throw;
        }
    }

    /// <summary>The token whose hash this is, with its last use noted (saved at most once a minute per token).</summary>
    public TokenRecord? FindToken(byte[] hash, DateTimeOffset now)
    {
        lock (_gate)
        {
            foreach (var token in _file.Tokens)
            {
                byte[] stored;
                try
                {
                    stored = Convert.FromHexString(token.Hash);
                }
                catch (FormatException)
                {
                    continue;
                }

                if (!CryptographicOperations.FixedTimeEquals(stored, hash)) continue;
                NoteUse(token, now);
                return Copy(token);
            }

            return null;
        }
    }

    /// <summary>Under the lock: the token's last use is now; saved when the saved one is over a minute old.</summary>
    private void NoteUse(TokenRecord token, DateTimeOffset now)
    {
        var save = token.LastUsedAt is not { } last || now - last > TimeSpan.FromMinutes(1);
        token.LastUsedAt = now;
        if (!save) return;
        try
        {
            Save();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // The last-used time is only shown (and orders which token goes first); a failed save must not refuse the request.
        }
    }

    private static TokenRecord Copy(TokenRecord token) => new()
    {
        Id = token.Id,
        Name = token.Name,
        Hash = token.Hash,
        CreatedAt = token.CreatedAt,
        LastUsedAt = token.LastUsedAt,
        Role = token.Role,
    };

    private void Save() => AtomicFile.Write(_path, JsonSerializer.SerializeToUtf8Bytes(_file, Json));
}

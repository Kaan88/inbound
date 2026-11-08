using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace DownloadManager.Server.Auth;

/// <summary>A paired browser's token, as saved: its SHA-256 only, never the token.</summary>
public sealed class TokenRecord
{
    public string Id { get; set; } = string.Empty;
    public string Name { get; set; } = string.Empty;
    public string Hash { get; set; } = string.Empty;
    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset? LastUsedAt { get; set; }
}

public sealed class AuthFile
{
    public int Version { get; set; } = 1;
    public PasswordRecord? Password { get; set; }
    public List<TokenRecord> Tokens { get; set; } = [];
}

/// <summary>
/// <c>auth.json</c> in the data folder: the password's hash and the paired browsers' token hashes. Neither can be
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

    /// <summary>Removes every paired browser's token; how many there were.</summary>
    public int RemoveAllTokens()
    {
        lock (_gate)
        {
            var before = _file.Tokens;
            if (before.Count == 0) return 0;
            _file.Tokens = [];
            SaveOrUndo(() => _file.Tokens = before);
            return before.Count;
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
                var save = token.LastUsedAt is not { } last || now - last > TimeSpan.FromMinutes(1);
                token.LastUsedAt = now;
                if (save)
                {
                    try
                    {
                        Save();
                    }
                    catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                    {
                        // The last-used time is only shown; a failed save must not refuse the request.
                    }
                }

                return Copy(token);
            }

            return null;
        }
    }

    private static TokenRecord Copy(TokenRecord token) => new()
    {
        Id = token.Id,
        Name = token.Name,
        Hash = token.Hash,
        CreatedAt = token.CreatedAt,
        LastUsedAt = token.LastUsedAt,
    };

    private void Save() => AtomicFile.Write(_path, JsonSerializer.SerializeToUtf8Bytes(_file, Json));
}

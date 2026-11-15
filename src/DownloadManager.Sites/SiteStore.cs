using System.Text.Json;
using System.Text.Json.Serialization;
using DownloadManager.Persistence;

namespace DownloadManager.Sites;

public static class SiteKinds
{
    /// <summary>The first kind (SITES.2 to SITES.5); the others arrive in SITES.6.</summary>
    public const string Sftp = "sftp";
}

public static class SiteAuthMethods
{
    public const string Password = "password";
    public const string Key = "key";
}

/// <summary>
/// A saved site as stored in <c>sites/{id}.json</c> (remote-sites.md, "Credentials and trust"). Secrets are only ever
/// held sealed here (<see cref="SecretProtector.ProtectSiteSecret"/>); they are opened when rclone's remote is built and
/// never sent to a client. No password is part of an address.
/// </summary>
public sealed record SiteRecord
{
    public const int CurrentSchemaVersion = 1;

    public int SchemaVersion { get; init; } = CurrentSchemaVersion;
    public required string Id { get; init; }
    public required string Name { get; init; }
    public required string Kind { get; init; }
    public required string Host { get; init; }
    public int Port { get; init; } = 22;
    public string User { get; init; } = "";
    public string AuthMethod { get; init; } = SiteAuthMethods.Password;

    /// <summary>The password, sealed.</summary>
    public string? SealedPassword { get; init; }

    /// <summary>The private key's text (PEM or OpenSSH format), sealed; the Host keeps no key file of its own.</summary>
    public string? SealedPrivateKey { get; init; }

    /// <summary>The private key's passphrase, sealed.</summary>
    public string? SealedKeyPassphrase { get; init; }

    /// <summary>The host keys the user accepted, as <c>host_keys</c> entries (<c>type base64</c>). Empty until accepted.</summary>
    public List<string> HostKeys { get; init; } = [];

    /// <summary>The folder a browse starts in; <c>/</c> when empty.</summary>
    public string RemoteFolder { get; init; } = "";

    /// <summary>The linked folder on this PC (two panes, "On this PC"), or null.</summary>
    public string? LocalFolder { get; init; }

    /// <summary>Starred folders: absolute paths on the server, normalised by <see cref="SitePaths.Normalize"/>.</summary>
    public List<string> Stars { get; init; } = [];

    public DateTimeOffset CreatedAt { get; init; }
    public DateTimeOffset ModifiedAt { get; init; }
}

/// <summary>
/// PERF.10 and PERF.12 rules for Sites (sites-and-performance-order.md): every stored record in a generated context, files
/// written compact, straight to UTF-8 bytes.
/// </summary>
[JsonSourceGenerationOptions(
    WriteIndented = false,
    PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase,
    PropertyNameCaseInsensitive = true,
    DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull)]
[JsonSerializable(typeof(SiteRecord))]
internal sealed partial class SitesJsonContext : JsonSerializerContext;

public sealed class SiteStoreException(string message, Exception? inner = null) : Exception(message, inner);

/// <summary>
/// The saved sites, one file each under <c>&lt;state&gt;/sites/</c>, written atomically (temp file, flush to disk,
/// replace). One gate for writes, so two windows saving at once can't interleave. A damaged or too-new file is reported
/// and left in place, never overwritten or deleted by a load.
/// </summary>
public sealed class SiteStore
{
    private readonly string _folder;
    private readonly Action<string>? _log;
    private readonly SemaphoreSlim _gate = new(1, 1);

    public SiteStore(string stateDirectory, Action<string>? log = null)
    {
        _folder = Path.Combine(stateDirectory, "sites");
        _log = log;
    }

    public string Folder => _folder;

    public async Task<IReadOnlyList<SiteRecord>> ListAsync(CancellationToken cancellationToken = default)
    {
        if (!Directory.Exists(_folder)) return [];
        var sites = new List<SiteRecord>();
        foreach (var file in Directory.EnumerateFiles(_folder, "*.json"))
        {
            // Only files named by a site id: the folder also holds Sites' own config.json.
            if (!IsId(Path.GetFileNameWithoutExtension(file))) continue;
            var site = await ReadAsync(file, cancellationToken).ConfigureAwait(false);
            if (site is not null) sites.Add(site);
        }

        return [.. sites.OrderBy(s => s.Name, StringComparer.CurrentCultureIgnoreCase).ThenBy(s => s.Id, StringComparer.Ordinal)];
    }

    public async Task<SiteRecord?> GetAsync(string id, CancellationToken cancellationToken = default)
    {
        var path = PathFor(id);
        return File.Exists(path) ? await ReadAsync(path, cancellationToken).ConfigureAwait(false) : null;
    }

    public async Task SaveAsync(SiteRecord site, CancellationToken cancellationToken = default)
    {
        Validate(site);
        var bytes = JsonSerializer.SerializeToUtf8Bytes(site, SitesJsonContext.Default.SiteRecord);
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await AtomicFile.WriteBytesAsync(PathFor(site.Id), bytes, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <summary>True when a file was removed.</summary>
    public async Task<bool> RemoveAsync(string id, CancellationToken cancellationToken = default)
    {
        var path = PathFor(id);
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (!File.Exists(path)) return false;
            File.Delete(path);
            return true;
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <summary>A new id: 32 lowercase hex digits, the only form <see cref="PathFor"/> accepts.</summary>
    public static string NewId() => Guid.NewGuid().ToString("N");

    private string PathFor(string id)
    {
        // The id names a file: only our own form, so no client can reach outside the folder with "../" or a drive.
        if (!IsId(id)) throw new SiteStoreException("That is not a site id.");
        return Path.Combine(_folder, id + ".json");
    }

    public static bool IsId(string id) => id.Length == 32 && id.All(c => c is >= '0' and <= '9' or >= 'a' and <= 'f');

    private static void Validate(SiteRecord site)
    {
        if (site.SchemaVersion != SiteRecord.CurrentSchemaVersion) throw new SiteStoreException("A site of another version can't be saved.");
        if (site.Kind != SiteKinds.Sftp) throw new SiteStoreException($"Sites of kind \"{site.Kind}\" are not supported yet.");
        if (string.IsNullOrWhiteSpace(site.Name)) throw new SiteStoreException("A site needs a name.");
        if (string.IsNullOrWhiteSpace(site.Host) || site.Host.Any(c => char.IsWhiteSpace(c) || c is '/' or '@' or '?' or '#'))
            throw new SiteStoreException("A site needs a server name or address, without a path, user or password in it.");
        if (site.Port is < 1 or > 65535) throw new SiteStoreException("The port must be between 1 and 65535.");
        foreach (var sealedValue in new[] { site.SealedPassword, site.SealedPrivateKey, site.SealedKeyPassphrase })
            if (sealedValue is not null && !SecretProtector.IsProtected(sealedValue))
                throw new SiteStoreException("A site secret must be sealed before it is saved.");
        foreach (var entry in site.HostKeys)
            if (!IsHostKeyEntry(entry)) throw new SiteStoreException("A host key must be \"type base64\", with the key's own type.");
        if (site.Stars.Count > SitePaths.MaxStars) throw new SiteStoreException($"A site can have at most {SitePaths.MaxStars} starred folders.");
        foreach (var star in site.Stars)
            if (!SitePaths.IsStarrable(star)) throw new SiteStoreException("A starred folder must be an absolute path on the server.");
        if (site.RemoteFolder.Length > 0 && SitePaths.Normalize(site.RemoteFolder) is null)
            throw new SiteStoreException("The folder on the server must be a path such as /home/me/files, without . or .. in it.");
    }

    /// <summary>"type base64", where the blob is an SSH public key whose own type (its first string) is that type.</summary>
    public static bool IsHostKeyEntry(string entry)
    {
        if (entry.Split(' ') is not [{ Length: > 0 } type, { Length: > 0 } key]) return false;
        var buffer = new byte[key.Length];
        if (!Convert.TryFromBase64String(key, buffer, out var length) || length < 4) return false;
        var nameLength = System.Buffers.Binary.BinaryPrimitives.ReadUInt32BigEndian(buffer);
        return nameLength > 0 && nameLength <= length - 4
            && System.Text.Encoding.ASCII.GetString(buffer, 4, (int)nameLength) == type
            && length > 4 + nameLength;
    }

    private async Task<SiteRecord?> ReadAsync(string path, CancellationToken cancellationToken)
    {
        try
        {
            var bytes = await File.ReadAllBytesAsync(path, cancellationToken).ConfigureAwait(false);
            var site = JsonSerializer.Deserialize(bytes, SitesJsonContext.Default.SiteRecord);
            if (site is null) return null;
            // A hand-edited file with "stars": null or "hostKeys": null reads as empty lists, not as a site that breaks sites.list.
            if (site.Stars is null || site.HostKeys is null) site = site with { Stars = site.Stars ?? [], HostKeys = site.HostKeys ?? [] };
            if (site.SchemaVersion > SiteRecord.CurrentSchemaVersion)
            {
                _log?.Invoke($"Site file {Path.GetFileName(path)} is from a newer version of Inbound; it is left as it is.");
                return null;
            }

            if (!string.Equals(site.Id + ".json", Path.GetFileName(path), StringComparison.Ordinal))
            {
                _log?.Invoke($"Site file {Path.GetFileName(path)} names another site; it is left as it is.");
                return null;
            }

            return site;
        }
        catch (Exception e) when (e is JsonException or IOException or UnauthorizedAccessException or NotSupportedException)
        {
            // The message of a JsonException can quote the file; nothing of it is logged.
            _log?.Invoke($"Site file {Path.GetFileName(path)} could not be read ({e.GetType().Name}); it is left as it is.");
            return null;
        }
    }
}

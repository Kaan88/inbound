using System.Text;
using System.Text.Json;
using DownloadManager.Contracts;
using DownloadManager.Persistence;

namespace DownloadManager.Desktop.WinUI;

/// <summary>One server the window can show (design D9, section 5). The token is kept sealed for this Windows user only.</summary>
internal sealed class SavedServer
{
    /// <summary>32 hex digits, made here. Also part of the progress and package windows' names (<c>--server</c>).</summary>
    public string Id { get; set; } = string.Empty;

    public string Name { get; set; } = string.Empty;

    /// <summary>The normalised base address (<see cref="ServerAddress.TryNormalize"/>).</summary>
    public string Address { get; set; } = string.Empty;

    /// <summary>The <c>remoteClient</c> token, sealed with DPAPI for the current user (<see cref="DpapiSecretProtector"/>).</summary>
    public string? SealedToken { get; set; }

    /// <summary>The server's id for the token (for the user to recognise it in the server's web UI).</summary>
    public string? TokenId { get; set; }

    /// <summary>The user agreed to send this server's token over plain <c>http://</c>.</summary>
    public bool PlainHttpConfirmed { get; set; }

    /// <summary>What the server said it is when it was paired: <c>windows</c> or <c>server</c>.</summary>
    public string? HostKind { get; set; }

    public DateTimeOffset PairedAt { get; set; }

    public override string ToString() => $"{Name} ({Address})";
}

/// <summary>
/// The window's own list of servers, <c>servers.json</c> next to its other state (never the Host's settings, export or
/// import). Only the token is secret, and it is kept sealed with DPAPI for this Windows user, bound to the server's id, so
/// a copied file opens on no other account and a sealed token moved to another entry doesn't open. The password is
/// never kept. Written to a temporary file, flushed and moved over the old one.
/// </summary>
internal sealed class ServerStore
{
    public const string FileName = "servers.json";
    private const int MaxServers = 50;
    private const int MaxNameLength = 80;

    private static readonly JsonSerializerOptions Options = new(JsonSerializerDefaults.Web) { WriteIndented = true };

    private readonly string _path;
    private readonly ISecretProtector _protector;

    public ServerStore(string path, ISecretProtector? protector = null)
    {
        _path = path;
        _protector = protector ?? new DpapiSecretProtector();
    }

    /// <summary>Beside the window's <c>ui-state.json</c>: the isolated data folder of a test instance, or the app's own.</summary>
    public static ServerStore ForThisUser()
    {
        var isolated = Environment.GetEnvironmentVariable("DOWNLOADMANAGER_DATA");
        var folder = !string.IsNullOrWhiteSpace(isolated) ? isolated : AppFolders.DataRoot();
        return new ServerStore(Path.Combine(folder, FileName));
    }

    /// <summary>The saved servers, in the order added. Entries that don't read are left out.</summary>
    public List<SavedServer> Load() => Read().Servers;

    /// <summary>The server the window showed last; null for this PC.</summary>
    public string? LastShown => Read().LastShown;

    public SavedServer? Find(string id) => Load().FirstOrDefault(server => string.Equals(server.Id, id, StringComparison.OrdinalIgnoreCase));

    /// <summary>
    /// The target for a saved server, with its token opened; null when there is no such server or its token doesn't open
    /// here (another Windows account, or damage): it then has to be paired again.
    /// </summary>
    public ServerTarget? Target(string id)
    {
        if (Find(id) is not { } server || server.SealedToken is null) return null;
        if (!ServerAddress.TryNormalize(server.Address, out var address, out _)) return null;
        var token = OperatingSystem.IsWindows() ? _protector.Unprotect(server.SealedToken, Purpose(server.Id)) : null;
        return string.IsNullOrEmpty(token) ? null : ServerTarget.Remote(server.Id, server.Name, address, token, server.PlainHttpConfirmed);
    }

    public SavedServer Add(string name, Uri address, string token, string? tokenId, bool plainHttpConfirmed, string? hostKind)
    {
        var file = Read();
        if (file.Servers.Count >= MaxServers) throw new InvalidOperationException($"At most {MaxServers} servers can be saved.");
        var server = new SavedServer
        {
            Id = Guid.NewGuid().ToString("N"),
            Name = CleanName(name, address),
            Address = address.ToString(),
            TokenId = tokenId,
            PlainHttpConfirmed = plainHttpConfirmed,
            HostKind = hostKind,
            PairedAt = DateTimeOffset.UtcNow,
        };
        server.SealedToken = Seal(server.Id, token);
        file.Servers.Add(server);
        Write(file);
        return server;
    }

    /// <summary>A new pairing for a saved server (its old token stopped working, or the address changed).</summary>
    public void Repair(string id, Uri address, string token, string? tokenId, bool plainHttpConfirmed, string? hostKind)
    {
        var file = Read();
        var server = file.Servers.FirstOrDefault(entry => entry.Id == id) ?? throw new InvalidOperationException("That server isn't saved.");
        server.Address = address.ToString();
        server.SealedToken = Seal(id, token);
        server.TokenId = tokenId;
        server.PlainHttpConfirmed = plainHttpConfirmed;
        server.HostKind = hostKind ?? server.HostKind;
        server.PairedAt = DateTimeOffset.UtcNow;
        Write(file);
    }

    /// <summary>Changes the name or the plain-HTTP choice. The address stays: another address needs a new pairing.</summary>
    public void Update(string id, string? name = null, bool? plainHttpConfirmed = null)
    {
        var file = Read();
        var server = file.Servers.FirstOrDefault(entry => entry.Id == id) ?? throw new InvalidOperationException("That server isn't saved.");
        if (name is not null && ServerAddress.TryNormalize(server.Address, out var address, out _)) server.Name = CleanName(name, address);
        if (plainHttpConfirmed is { } confirmed) server.PlainHttpConfirmed = confirmed;
        Write(file);
    }

    public void Remove(string id)
    {
        var file = Read();
        file.Servers.RemoveAll(entry => entry.Id == id);
        if (file.LastShown == id) file.LastShown = null;
        Write(file);
    }

    public void SetLastShown(string? id)
    {
        var file = Read();
        file.LastShown = id is not null && file.Servers.Any(entry => entry.Id == id) ? id : null;
        Write(file);
    }

    /// <summary>The name to keep: trimmed, without control characters, at most 80 characters; the host when empty.</summary>
    public static string CleanName(string? name, Uri address)
    {
        var cleaned = new string((name ?? string.Empty).Where(c => !char.IsControl(c)).ToArray()).Trim();
        if (cleaned.Length == 0) cleaned = address.Host;
        return cleaned.Length > MaxNameLength ? cleaned[..MaxNameLength] : cleaned;
    }

    private static byte[] Purpose(string id) => Encoding.UTF8.GetBytes("Inbound remote server token\n" + id.ToLowerInvariant());

    private string Seal(string id, string token)
    {
        if (!OperatingSystem.IsWindows()) throw new PlatformNotSupportedException("Saving a server's token needs Windows.");
        return _protector.Protect(token, Purpose(id));
    }

    private ServerFile Read()
    {
        try
        {
            if (!File.Exists(_path)) return new ServerFile();
            var file = JsonSerializer.Deserialize<ServerFile>(File.ReadAllText(_path), Options) ?? new ServerFile();
            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            file.Servers = (file.Servers ?? [])
                .Where(server => server is not null && server.Id.Length == 32 && server.Id.All(Uri.IsHexDigit) && seen.Add(server.Id)
                    && ServerAddress.TryNormalize(server.Address, out _, out _))
                .Take(MaxServers)
                .ToList();
            if (file.LastShown is not null && !file.Servers.Any(server => server.Id == file.LastShown)) file.LastShown = null;
            return file;
        }
        catch (Exception ex) when (ex is IOException or JsonException or UnauthorizedAccessException)
        {
            return new ServerFile();
        }
    }

    private void Write(ServerFile file)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(_path)!);
        var temp = _path + ".tmp";
        var bytes = JsonSerializer.SerializeToUtf8Bytes(file, Options);
        using (var stream = new FileStream(temp, FileMode.Create, FileAccess.Write, FileShare.None))
        {
            stream.Write(bytes);
            stream.Flush(flushToDisk: true);
        }

        File.Move(temp, _path, overwrite: true);
    }

    private sealed class ServerFile
    {
        public int Version { get; set; } = 1;
        public string? LastShown { get; set; }
        public List<SavedServer> Servers { get; set; } = [];
    }
}

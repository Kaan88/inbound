using System.Text.Json;
using System.Text.Json.Serialization;
using DownloadManager.Contracts;

namespace DownloadManager.Mobile.Remote;

/// <summary>
/// One host the screens can show: this phone, or a paired server. Holds no token: the token is in the
/// <see cref="ISecretStore"/> under <see cref="Id"/>, encrypted by the platform.
/// </summary>
public sealed class HostEntry
{
    /// <summary>The id of this phone's own Host.</summary>
    public const string PhoneId = "phone";

    public string Id { get; set; } = PhoneId;

    public string Name { get; set; } = "This phone";

    /// <summary>The server's base address (<see cref="ServerAddress.TryNormalize"/>); null for this phone.</summary>
    public string? Address { get; set; }

    /// <summary>The user agreed to send this server's sign-in over plain <c>http://</c> to another machine.</summary>
    public bool PlainHttpConfirmed { get; set; }

    /// <summary>The server's <c>hostKind</c> (<see cref="HostKinds"/>), as it last said.</summary>
    public string? HostKind { get; set; }

    /// <summary>The server's id for the token (for the server's own list of paired apps). Not the token.</summary>
    public string? TokenId { get; set; }

    /// <summary>
    /// https: the SHA-256 (<c>AB:CD:…</c>) of the certificate the user trusted at pairing, accepted besides what the phone
    /// trusts (owner, 2026-11-17: "pin at pairing"). Public data, not a secret; null for system trust only.
    /// </summary>
    public string? CertificatePin { get; set; }

    public DateTimeOffset? PairedAt { get; set; }

    /// <summary>When a connection to it last got the snapshot.</summary>
    public DateTimeOffset? LastSeenAt { get; set; }

    [JsonIgnore]
    public bool IsPhone => Id == PhoneId;

    [JsonIgnore]
    public Uri? AddressUri => Address is not null && Uri.TryCreate(Address, UriKind.Absolute, out var uri) ? uri : null;

    internal HostEntry Copy() => (HostEntry)MemberwiseClone();

    public override string ToString() => IsPhone ? Name : $"{Name} ({Address})";
}

/// <summary>What is saved: the paired servers (never this phone, never a token) and which host the screens show.</summary>
public sealed class HostListData
{
    public int Version { get; set; } = 1;
    public string CurrentId { get; set; } = HostEntry.PhoneId;
    public List<HostEntry> Servers { get; set; } = [];
}

/// <summary>Where the host list is kept.</summary>
public interface IHostListStore
{
    /// <summary>The saved list; an empty one when nothing was saved or the file can't be read.</summary>
    HostListData Load();

    void Save(HostListData data);
}

/// <summary>
/// Secrets (the servers' tokens) by name. The Android one encrypts with a key in the Android Keystore; nothing in it is
/// ever written in plain text or logged.
/// </summary>
public interface ISecretStore
{
    /// <summary>The secret, or null when there is none or it can no longer be decrypted (the key was lost).</summary>
    string? Read(string name);

    void Write(string name, string secret);

    void Delete(string name);
}

/// <summary>The host list as a JSON file, written to a temp file in the same folder and moved into place.</summary>
public sealed class HostListFile(string path) : IHostListStore
{
    public string Path { get; } = path;

    public HostListData Load()
    {
        try
        {
            if (!File.Exists(Path)) return new HostListData();
            using var stream = File.OpenRead(Path);
            return JsonSerializer.Deserialize(stream, MobileJson.Default.HostListData) ?? new HostListData();
        }
        catch (Exception e) when (e is IOException or JsonException or UnauthorizedAccessException)
        {
            return new HostListData();
        }
    }

    public void Save(HostListData data)
    {
        var folder = System.IO.Path.GetDirectoryName(System.IO.Path.GetFullPath(Path))!;
        Directory.CreateDirectory(folder);
        var temp = System.IO.Path.Combine(folder, System.IO.Path.GetFileName(Path) + "." + Guid.NewGuid().ToString("N") + ".tmp");
        try
        {
            using (var stream = new FileStream(temp, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            {
                JsonSerializer.Serialize(stream, data, MobileJson.Default.HostListData);
                stream.Flush(flushToDisk: true);
            }

            File.Move(temp, Path, overwrite: true);
        }
        finally
        {
            try { if (File.Exists(temp)) File.Delete(temp); }
            catch (IOException) { }
        }
    }
}

[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase, WriteIndented = true,
    DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull)]
[JsonSerializable(typeof(HostListData))]
internal sealed partial class MobileJson : JsonSerializerContext;

/// <summary>
/// What the screens may offer for the host they show. A remote host's files are on that machine, so the actions that
/// need the file on this phone are hidden there (not shown disabled).
/// </summary>
public sealed record HostCapabilities(bool IsRemote)
{
    public static HostCapabilities Phone { get; } = new(false);

    public static HostCapabilities Remote { get; } = new(true);

    /// <summary>Open a finished file, or share it with another app.</summary>
    public bool CanOpenFiles => !IsRemote;

    /// <summary>Show a finished file's folder on the phone.</summary>
    public bool CanShowInFolder => !IsRemote;

    /// <summary>Pick a destination folder with the phone's own folder picker (a server's folders are its own).</summary>
    public bool CanPickPhoneFolder => !IsRemote;

    /// <summary>The phone's own background rules (battery note, Wi-Fi only, the keep-alive notification).</summary>
    public bool ShowsPhoneBackgroundState => !IsRemote;

    /// <summary>Delete a download and its file: on a server the server deletes its own file, so this stays.</summary>
    public bool CanDeleteWithFile => true;
}

/// <summary>Words for a host in the list (C5).</summary>
public static class HostWords
{
    /// <summary>Windows, Server or Linux, from the server's <c>hostKind</c>; null when it hasn't said.</summary>
    public static string? Kind(string? hostKind) => hostKind switch
    {
        HostKinds.Windows => "Windows",
        HostKinds.Server => "Server",
        HostKinds.Linux => "Linux",
        _ => null,
    };

    /// <summary>"Windows · last seen 5 min ago" and the like, for a server row; the address when nothing else is known.</summary>
    public static string Subtitle(HostEntry host, DateTimeOffset now)
    {
        if (host.IsPhone) return "Downloads run on this phone";
        var parts = new List<string>();
        if (Kind(host.HostKind) is { } kind) parts.Add(kind);
        parts.Add(host.LastSeenAt is { } seen ? "last seen " + Ago(now - seen) : host.AddressUri?.Authority ?? host.Address ?? "");
        return string.Join(" · ", parts);
    }

    public static string Ago(TimeSpan span) => span switch
    {
        { TotalMinutes: < 1 } => "now",
        { TotalHours: < 1 } => $"{(int)span.TotalMinutes} min ago",
        { TotalDays: < 1 } => $"{(int)span.TotalHours} h ago",
        _ => $"{(int)span.TotalDays} d ago",
    };

    /// <summary>The strip on every screen while another host is shown (C6).</summary>
    public static string SavedOn(string name) => $"Files are saved on {name}";

    /// <summary>
    /// A row of C5 with the figures read when the sheet opened: "Windows · 5 active · 32 MB/s", "Server · 12 queued",
    /// "2 active · 14.6 MB/s" for the phone. Without figures (not reached), <see cref="Subtitle"/>.
    /// </summary>
    public static string WithFigures(HostEntry host, HostFigures? figures, DateTimeOffset now)
    {
        if (figures is null) return Subtitle(host, now) + (host.IsPhone ? "" : " · not reached now");
        var parts = new List<string>();
        if (!host.IsPhone && Kind(host.HostKind) is { } kind) parts.Add(kind);
        if (figures.Active > 0)
        {
            parts.Add($"{figures.Active} active");
            if (figures.BytesPerSecond > 0) parts.Add(Fmt.Rate(figures.BytesPerSecond));
        }
        if (figures.Queued > 0) parts.Add($"{figures.Queued} queued");
        if (figures.Active == 0 && figures.Queued == 0) parts.Add("nothing running");
        return string.Join(" · ", parts);
    }
}

/// <summary>A host's figures for the host list: downloads running, waiting, and their speed together.</summary>
public sealed record HostFigures(int Active, int Queued, double BytesPerSecond)
{
    public static HostFigures From(SnapshotDto snapshot)
    {
        int active = 0, queued = 0;
        double speed = 0;
        foreach (var job in snapshot.Jobs)
        {
            switch ((job.State ?? "").ToLowerInvariant())
            {
                case "downloading" or "connecting" or "verifying":
                    active++;
                    speed += Math.Max(0, job.BytesPerSecond ?? 0);
                    break;
                case "queued":
                    queued++;
                    break;
            }
        }

        return new HostFigures(active, queued, speed);
    }
}

/// <summary>What a paired server is called in the list.</summary>
public static class HostNaming
{
    /// <summary>The name typed, else the one kept from an earlier pairing (when that was a real name), else the name the server gave itself, else its address.</summary>
    public static string Choose(string? typed, string? earlier, string? serverName, string addressHost)
    {
        if (!string.IsNullOrWhiteSpace(typed)) return typed.Trim();
        if (!string.IsNullOrWhiteSpace(earlier) && earlier != addressHost) return earlier;
        if (!string.IsNullOrWhiteSpace(serverName)) return serverName.Trim();
        return addressHost;
    }
}

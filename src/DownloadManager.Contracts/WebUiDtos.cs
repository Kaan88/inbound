using System.Buffers.Binary;
using System.Security.Cryptography;

namespace DownloadManager.Contracts;

/// <summary>
/// The web UI on the desktop Host (D8): turning it on, its port, password and firewall state, and the one-time sign-in
/// for "Open in browser". Only the user at this PC may send these: the Host answers them on its pipes for a session with
/// the desktop role and refuses them for a web session (a browser through the bridge, with a cookie or a paired token).
/// A Host without a web UI (the headless server) refuses them too.
/// </summary>
public static class WebUiMessageTypes
{
    /// <summary><c>{}</c>; answered with <see cref="Status"/>.</summary>
    public const string Get = "webui.get";

    /// <summary><see cref="WebUiSetRequest"/>; answered with <see cref="Status"/>.</summary>
    public const string Set = "webui.set";

    /// <summary><see cref="WebUiPasswordRequest"/>; answered with <see cref="Status"/>.</summary>
    public const string SetPassword = "webui.setPassword";

    /// <summary><c>{}</c>: reads the firewall rule again; answered with <see cref="Status"/>.</summary>
    public const string Recheck = "webui.recheck";

    /// <summary><c>{}</c>; answered with <see cref="Ticket"/>.</summary>
    public const string OpenTicket = "webui.openTicket";

    /// <summary>The reply to get, set, setPassword and recheck: a <see cref="WebUiStatusDto"/>.</summary>
    public const string Status = "webui.status";

    /// <summary>The reply to openTicket: a <see cref="WebUiTicketDto"/>.</summary>
    public const string Ticket = "webui.ticket";

    /// <summary>Host push to the desktop role's sessions only: a <see cref="WebUiStatusDto"/> after any change.</summary>
    public const string Changed = "webui.changed";

    /// <summary>The requests (not the replies or the push).</summary>
    public static bool IsRequest(string? type) => type is Get or Set or SetPassword or Recheck or OpenTicket;
}

/// <summary>The values of <see cref="WebUiStatusDto.State"/>.</summary>
public static class WebUiStates
{
    public const string Off = "off";
    public const string Starting = "starting";
    public const string Running = "running";
    public const string Failed = "failed";
}

/// <summary>The values of <see cref="WebUiStatusDto.Firewall"/>.</summary>
public static class WebUiFirewallStates
{
    /// <summary>An enabled inbound allow rule for the bridge, TCP, this port, and no inbound block rule for it.</summary>
    public const string Ready = "ready";

    /// <summary>No such rule, or a block rule for the bridge overrides it.</summary>
    public const string Missing = "missing";

    /// <summary>The rules could not be read. Treated as missing: the bridge then listens on this PC only.</summary>
    public const string Unknown = "unknown";
}

public sealed class WebUiStatusDto
{
    /// <summary>The box "Let other devices on my network control this PC's downloads".</summary>
    public bool Enabled { get; set; }

    public int Port { get; set; } = WebUiDefaults.Port;

    /// <summary>Host names other devices may use for this PC besides its addresses (the server's <c>DOWNLOADMANAGER_HOSTS</c>).</summary>
    public List<string> AlsoReachableAs { get; set; } = [];

    public bool HasPassword { get; set; }

    /// <summary><see cref="WebUiStates"/>.</summary>
    public string State { get; set; } = WebUiStates.Off;

    /// <summary>Why it failed, why a request was refused (nothing changed then), or why it is restarting. Null otherwise.</summary>
    public string? Error { get; set; }

    /// <summary>True while the bridge listens on this PC only because the firewall rule is missing (or unreadable).</summary>
    public bool LocalOnly { get; set; }

    /// <summary><see cref="WebUiFirewallStates"/>.</summary>
    public string Firewall { get; set; } = WebUiFirewallStates.Unknown;

    /// <summary><c>http://&lt;ip&gt;:&lt;port&gt;/</c> for each usable local network address; empty while <see cref="LocalOnly"/>.</summary>
    public List<string> Addresses { get; set; } = [];

    /// <summary>The full path of <c>DownloadManager.Server.exe</c>, the program the firewall rule names.</summary>
    public string BridgePath { get; set; } = string.Empty;

    /// <summary>The firewall rule's name (<see cref="WebUiDefaults.RuleName"/>).</summary>
    public string RuleName { get; set; } = WebUiDefaults.RuleName;
}

public static class WebUiDefaults
{
    public const int Port = 8080;
    public const int MinPort = 1024;
    public const int MaxPort = 65535;
    public const string RuleName = "Inbound Download Manager web UI";

    /// <summary>The bridge's ticket sign-in: <c>GET</c> with <c>?t=</c> and the ticket.</summary>
    public const string TicketPath = "/api/auth/ticket";

    /// <summary>How long a ticket from <c>webui.openTicket</c> can be used.</summary>
    public static readonly TimeSpan TicketLifetime = TimeSpan.FromSeconds(60);
}

/// <summary>What <c>webui.set</c> changes; a field left out stays as it is.</summary>
public sealed class WebUiSetRequest
{
    public bool? Enabled { get; set; }
    public int? Port { get; set; }
    public List<string>? AlsoReachableAs { get; set; }
}

public sealed class WebUiPasswordRequest
{
    public string Password { get; set; } = string.Empty;
}

/// <summary>
/// The answer to <c>webui.openTicket</c>: <see cref="Url"/> signs a browser on this PC in once, within
/// <see cref="WebUiDefaults.TicketLifetime"/>; or <see cref="Error"/> when the web UI isn't running.
/// </summary>
public sealed class WebUiTicketDto
{
    public string? Url { get; set; }
    public string? Error { get; set; }
}

/// <summary>
/// What the Host gives the bridge it starts (<c>DownloadManager.Server.exe --bridge</c>), as one JSON line on the
/// bridge's standard input, never on its command line or in its environment. The bridge keeps reading standard input
/// after it; when it closes (the Host stopped it, or the Host is gone) the bridge stops.
/// </summary>
public sealed class WebBridgeStartDto
{
    /// <summary>The Host's data folder: <c>auth.json</c>, <c>sessions.json</c>, <c>uploads</c> and the bridge's log.</summary>
    public string DataDirectory { get; set; } = string.Empty;

    /// <summary>The Host's hello pipe (<see cref="PipeNames.HostHelloPipe"/>) to open one connection on per browser call.</summary>
    public string HelloPipe { get; set; } = string.Empty;

    public int Port { get; set; } = WebUiDefaults.Port;

    /// <summary>Listen on 127.0.0.1 and [::1] only (the firewall rule is missing); otherwise on every address.</summary>
    public bool LocalOnly { get; set; } = true;

    public List<string> Hosts { get; set; } = [];

    /// <summary>The key, Base64, that the Host signs this run's sign-in tickets with (<see cref="WebUiTicket"/>).</summary>
    public string TicketKey { get; set; } = string.Empty;
}

/// <summary>
/// How the bridge ends, so the Host can say why (<see cref="WebBridgeStartDto"/>). The message is the last line on its
/// standard error. The bridge writes <see cref="ReadyLine"/> to its standard output once it listens.
/// </summary>
public static class WebBridgeExit
{
    public const string ReadyLine = "ready";

    /// <summary>Stopped as asked.</summary>
    public const int Stopped = 0;

    /// <summary>Something unexpected; the Host restarts it after a wait.</summary>
    public const int Crashed = 1;

    /// <summary>A start it refuses: no password, a damaged sign-in file, start options it can't use.</summary>
    public const int Refused = 2;

    /// <summary>Another program uses the port.</summary>
    public const int PortInUse = 3;

    /// <summary>Windows doesn't let it listen there (a port Windows keeps for itself, or no permission).</summary>
    public const int PortForbidden = 4;
}

/// <summary>
/// The one-time sign-in of "Open in browser": the Host makes a ticket with the key it gave the bridge for this run, the
/// bridge checks it, once, from a browser on this PC. 16 random bytes, the expiry, and an HMAC-SHA256 of both; Base64Url.
/// </summary>
public static class WebUiTicket
{
    private const int NonceBytes = 16;
    private const int ExpiryBytes = 8;
    private const int MacBytes = 32;
    private const int Length = NonceBytes + ExpiryBytes + MacBytes;

    public static byte[] NewKey() => RandomNumberGenerator.GetBytes(32);

    public static string Create(byte[] key, DateTimeOffset now)
    {
        var bytes = new byte[Length];
        RandomNumberGenerator.Fill(bytes.AsSpan(0, NonceBytes));
        BinaryPrimitives.WriteInt64LittleEndian(bytes.AsSpan(NonceBytes, ExpiryBytes), (now + WebUiDefaults.TicketLifetime).ToUnixTimeMilliseconds());
        HMACSHA256.HashData(key, bytes.AsSpan(0, NonceBytes + ExpiryBytes), bytes.AsSpan(NonceBytes + ExpiryBytes));
        return Base64Url(bytes);
    }

    /// <summary>The ticket's nonce (to use once) and expiry when it was made with this key and is not expired; null otherwise.</summary>
    public static (string Nonce, DateTimeOffset Expires)? Check(byte[] key, string? ticket, DateTimeOffset now)
    {
        if (string.IsNullOrEmpty(ticket) || ticket.Length > 128 || FromBase64Url(ticket) is not { Length: Length } bytes) return null;
        Span<byte> expected = stackalloc byte[MacBytes];
        HMACSHA256.HashData(key, bytes.AsSpan(0, NonceBytes + ExpiryBytes), expected);
        if (!CryptographicOperations.FixedTimeEquals(expected, bytes.AsSpan(NonceBytes + ExpiryBytes))) return null;
        var expires = DateTimeOffset.FromUnixTimeMilliseconds(BinaryPrimitives.ReadInt64LittleEndian(bytes.AsSpan(NonceBytes, ExpiryBytes)));
        // A ticket from the future is one this key never made (the Host's clock is the bridge's).
        if (now >= expires || expires - now > WebUiDefaults.TicketLifetime + TimeSpan.FromSeconds(5)) return null;
        return (Convert.ToHexString(bytes, 0, NonceBytes), expires);
    }

    private static string Base64Url(byte[] bytes) => Convert.ToBase64String(bytes).TrimEnd('=').Replace('+', '-').Replace('/', '_');

    private static byte[]? FromBase64Url(string text)
    {
        var base64 = text.Replace('-', '+').Replace('_', '/');
        base64 = base64.PadRight(base64.Length + (4 - base64.Length % 4) % 4, '=');
        try
        {
            return Convert.FromBase64String(base64);
        }
        catch (FormatException)
        {
            return null;
        }
    }
}

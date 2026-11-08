using System.Text.RegularExpressions;

namespace DownloadManager.Desktop.WinUI;

// The web UI messages of the Host (webui.*). They are pipe-only: the Host refuses them to a web or token
// session. These types follow the agreed contract field for field. They are named ...Wire on purpose, so they can't
// silently shadow the contract's own types (DownloadManager.Contracts: WebUiStatusDto and so on) when those arrive:
// at the merge, delete the Wire types and use the contract's, and the build then points at every place left.

/// <summary>The message names of the web UI commands.</summary>
internal static class WebUiWireTypes
{
    public const string Get = "webui.get";
    public const string Set = "webui.set";
    public const string SetPassword = "webui.setPassword";
    public const string Recheck = "webui.recheck";
    public const string OpenTicket = "webui.openTicket";
    public const string Changed = "webui.changed";

    /// <summary>The reply to get, set, setPassword and recheck.</summary>
    public const string Status = "webui.status";

    /// <summary>The reply to openTicket.</summary>
    public const string Ticket = "webui.ticket";
}

/// <summary>What the Host says about the web UI: <c>webui.get</c> and <c>webui.changed</c>.</summary>
internal sealed class WebUiStatusWire
{
    public bool Enabled { get; set; }
    public int Port { get; set; } = WebUiForm.DefaultPort;
    public List<string> AlsoReachableAs { get; set; } = [];
    public bool HasPassword { get; set; }

    /// <summary><c>off</c>, <c>starting</c>, <c>running</c> or <c>failed</c>.</summary>
    public string State { get; set; } = WebUiWireStates.Off;
    public string? Error { get; set; }

    /// <summary>The web UI listens on this PC only, because the firewall rule is missing.</summary>
    public bool LocalOnly { get; set; }

    /// <summary><c>ready</c>, <c>missing</c> or <c>unknown</c>.</summary>
    public string Firewall { get; set; } = WebUiWireFirewall.Unknown;

    /// <summary>The addresses to type on another device, empty while <see cref="LocalOnly"/>.</summary>
    public List<string> Addresses { get; set; } = [];

    /// <summary>Full path of the program the firewall rule names.</summary>
    public string BridgePath { get; set; } = string.Empty;
    public string RuleName { get; set; } = WebUiForm.DefaultRuleName;

    /// <summary>
    /// The Host refused the request this reply answers: an <see cref="Error"/> on a status that isn't a failed start
    /// (nothing changed then).
    /// </summary>
    public bool IsRefusal => !string.IsNullOrWhiteSpace(Error) && State != WebUiWireStates.Failed && State != WebUiWireStates.Starting;
}

internal static class WebUiWireStates
{
    public const string Off = "off";
    public const string Starting = "starting";
    public const string Running = "running";
    public const string Failed = "failed";
}

internal static class WebUiWireFirewall
{
    public const string Ready = "ready";
    public const string Missing = "missing";
    public const string Unknown = "unknown";
}

/// <summary><c>webui.set</c>: only the fields that change.</summary>
internal sealed class WebUiSetWire
{
    public bool? Enabled { get; set; }
    public int? Port { get; set; }
    public List<string>? AlsoReachableAs { get; set; }
}

/// <summary><c>webui.setPassword</c>. The password goes only into this message.</summary>
internal sealed class WebUiPasswordWire
{
    public string Password { get; set; } = string.Empty;
}

/// <summary>The reply of <c>webui.openTicket</c>.</summary>
internal sealed class WebUiTicketWire
{
    public string? Url { get; set; }
    public string? Error { get; set; }
}

/// <summary>The kinds of network Windows sorts connections into.</summary>
internal enum NetworkProfile { Private, Public, Domain }

/// <summary>One connected network: its name in Windows and its kind.</summary>
internal sealed record NetworkEntry(string Name, NetworkProfile Kind);

/// <summary>The checks and the words of the Options page that don't need a window.</summary>
internal static partial class WebUiForm
{
    public const int DefaultPort = 8080;
    public const int MinPort = 1024;
    public const int MaxPort = 65535;
    public const int MinPasswordLength = 8;
    public const int MaxPasswordLength = 1024;
    public const int MostNames = 8;
    public const string DefaultRuleName = "Inbound Download Manager web UI";

    public static string? PortProblem(double value)
        => double.IsNaN(value) || value != Math.Floor(value) || value < MinPort || value > MaxPort
            ? $"The port must be a whole number from {MinPort} to {MaxPort}."
            : null;

    /// <summary>The same rules as the web UI's own sign-in, so the password works on both.</summary>
    public static string? PasswordProblem(string? password, string? repeated)
    {
        if (string.IsNullOrEmpty(password) || password.Length < MinPasswordLength)
            return $"The password needs at least {MinPasswordLength} characters.";
        if (password.Length > MaxPasswordLength)
            return $"The password can have at most {MaxPasswordLength} characters.";
        return string.Equals(password, repeated, StringComparison.Ordinal) ? null : "The two passwords don't match.";
    }

    [GeneratedRegex(@"^(?=.{1,253}$)([A-Za-z0-9]([A-Za-z0-9-]{0,61}[A-Za-z0-9])?)(\.[A-Za-z0-9]([A-Za-z0-9-]{0,61}[A-Za-z0-9])?)*$")]
    private static partial Regex HostNamePattern();

    /// <summary>The names typed in the box, one after another. Null, and a problem, when one is not a computer name.</summary>
    public static List<string>? ParseNames(string? text, out string? problem)
    {
        problem = null;
        var names = new List<string>();
        foreach (var raw in (text ?? string.Empty).Split([',', ';', ' ', '\t', '\r', '\n'], StringSplitOptions.RemoveEmptyEntries))
        {
            if (!HostNamePattern().IsMatch(raw) || System.Net.IPAddress.TryParse(raw, out _))
            {
                problem = $"\"{Shorten(raw)}\" isn't a computer name. Type names such as nas or nas.example.com, separated by commas, without http:// or a port.";
                return null;
            }

            if (!names.Contains(raw, StringComparer.OrdinalIgnoreCase)) names.Add(raw);
        }

        if (names.Count > MostNames)
        {
            problem = $"Use at most {MostNames} names.";
            return null;
        }

        return names;
    }

    private static string Shorten(string text) => text.Length <= 40 ? text : text[..40] + "…";

    /// <summary>
    /// What to send for the box, port and names, or null when nothing changed. A problem comes back in
    /// <paramref name="problem"/>; the box can't be ticked on without a password.
    /// </summary>
    public static WebUiSetWire? Changes(WebUiStatusWire saved, bool enabled, double port, string? namesText, out string? problem)
    {
        problem = PortProblem(port);
        if (problem is not null) return null;
        var names = ParseNames(namesText, out problem);
        if (names is null) return null;
        if (enabled && !saved.HasPassword)
        {
            problem = "Set a password first. This can't be turned on without one.";
            return null;
        }

        var request = new WebUiSetWire();
        var changed = false;
        if (enabled != saved.Enabled) { request.Enabled = enabled; changed = true; }
        if ((int)port != saved.Port) { request.Port = (int)port; changed = true; }
        if (!names.SequenceEqual(saved.AlsoReachableAs, StringComparer.OrdinalIgnoreCase))
        {
            request.AlsoReachableAs = names;
            changed = true;
        }

        return changed ? request : null;
    }

    /// <summary>The addresses to type on another device. While the web UI listens on this PC only, the address of this PC.</summary>
    public static List<string> AddressesToType(WebUiStatusWire status)
    {
        if (status.State != WebUiWireStates.Running) return [];
        // While it listens on this PC only, no address works on another device, so none is offered.
        if (status.LocalOnly) return [];
        var all = new List<string>(status.Addresses);
        foreach (var name in status.AlsoReachableAs)
        {
            var address = $"http://{name}:{status.Port}/";
            if (!all.Contains(address, StringComparer.OrdinalIgnoreCase)) all.Add(address);
        }

        return all;
    }

    public static (string Text, bool IsProblem) StateText(WebUiStatusWire status) => status.State switch
    {
        WebUiWireStates.Starting => ("Starting…", false),
        WebUiWireStates.Running when status.LocalOnly => ("On, but only this PC can reach it until Windows Firewall allows it (see Windows Firewall below). Open in browser works on this PC.", false),
        WebUiWireStates.Running => ("On. Other devices on your network can open it.", false),
        WebUiWireStates.Failed => ($"It couldn't start. {FailureWords(status.Error)}", true),
        _ => ("Off. Nothing is running and no port is open.", false),
    };

    private static string FailureWords(string? error)
        => string.IsNullOrWhiteSpace(error) ? "Check that no other program uses this port, then try again." : error.Trim();

    public static string PasswordText(WebUiStatusWire status)
        => status.HasPassword ? "A password is set." : "No password is set yet. Set one before turning this on.";

    public static string FirewallText(WebUiStatusWire status) => status.Firewall switch
    {
        WebUiWireFirewall.Ready => "Windows Firewall allows other devices to reach this.",
        WebUiWireFirewall.Missing => "Windows Firewall isn't set to allow this yet, so other devices can't reach it.",
        _ => "Whether Windows Firewall allows this couldn't be read.",
    };

    public const string PublicNetworkNote =
        "Every network this PC is on is set to Public. Windows treats Public networks as untrusted. Setting your home network to Private in Windows Settings is the safer fix; or tick \"Also allow on Public networks\" below.";

    /// <summary>The note applies only when no connected network is Private or Domain: one Public adapter (a VPN, say) is not a reason.</summary>
    public static bool ShowPublicNote(IReadOnlyList<NetworkEntry>? networks)
        => networks is { Count: > 0 } && networks.All(network => network.Kind == NetworkProfile.Public);

    /// <summary>The wording of the network type line; the networks are null when they could not be read.</summary>
    public static string ProfileText(IReadOnlyList<NetworkEntry>? networks)
    {
        if (networks is null) return "Network type: couldn't be read.";
        if (networks.Count == 0) return "Network type: this PC isn't connected to a network.";
        return "Network type: " + string.Join(", ", networks.Select(network =>
            $"{(string.IsNullOrWhiteSpace(network.Name) ? "network" : network.Name)} is {KindText(network.Kind)}")) + ".";
    }

    private static string KindText(NetworkProfile kind) => kind switch
    {
        NetworkProfile.Private => "Private",
        NetworkProfile.Public => "Public",
        _ => "Domain",
    };

    /// <summary>Opening the browser needs the Host's own sign-in link: this PC, http, the port, no credentials.</summary>
    public static bool IsLocalSignInUrl(string? url, int port)
        => Uri.TryCreate(url, UriKind.Absolute, out var uri) &&
           uri.Scheme == Uri.UriSchemeHttp &&
           uri.Port == port &&
           uri.UserInfo.Length == 0 &&
           (uri.Host == "127.0.0.1" || uri.Host == "localhost" || uri.Host == "[::1]");
}

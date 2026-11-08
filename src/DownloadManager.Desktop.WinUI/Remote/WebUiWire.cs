using System.Text.RegularExpressions;

namespace DownloadManager.Desktop.WinUI;

// The web UI messages of the Host (webui.*). They are pipe-only: the Host refuses them to a web or token
// session. These types follow the agreed contract field for field; when the contract's own types are in
// DownloadManager.Contracts, this file's copies of the types are deleted and nothing else changes.

/// <summary>The message names of the web UI commands.</summary>
internal static class WebUiMessageTypes
{
    public const string Get = "webui.get";
    public const string Set = "webui.set";
    public const string SetPassword = "webui.setPassword";
    public const string Recheck = "webui.recheck";
    public const string OpenTicket = "webui.openTicket";
    public const string Changed = "webui.changed";
}

/// <summary>What the Host says about the web UI: <c>webui.get</c> and <c>webui.changed</c>.</summary>
internal sealed class WebUiStatusDto
{
    public bool Enabled { get; set; }
    public int Port { get; set; } = WebUiForm.DefaultPort;
    public List<string> AlsoReachableAs { get; set; } = [];
    public bool HasPassword { get; set; }

    /// <summary><c>off</c>, <c>starting</c>, <c>running</c> or <c>failed</c>.</summary>
    public string State { get; set; } = WebUiStates.Off;
    public string? Error { get; set; }

    /// <summary>The web UI listens on this PC only, because the firewall rule is missing.</summary>
    public bool LocalOnly { get; set; }

    /// <summary><c>ready</c>, <c>missing</c> or <c>unknown</c>.</summary>
    public string Firewall { get; set; } = WebUiFirewallStates.Unknown;

    /// <summary>The addresses to type on another device, empty while <see cref="LocalOnly"/>.</summary>
    public List<string> Addresses { get; set; } = [];

    /// <summary>Full path of the program the firewall rule names.</summary>
    public string BridgePath { get; set; } = string.Empty;
    public string RuleName { get; set; } = WebUiForm.DefaultRuleName;
}

internal static class WebUiStates
{
    public const string Off = "off";
    public const string Starting = "starting";
    public const string Running = "running";
    public const string Failed = "failed";
}

internal static class WebUiFirewallStates
{
    public const string Ready = "ready";
    public const string Missing = "missing";
    public const string Unknown = "unknown";
}

/// <summary><c>webui.set</c>: only the fields that change.</summary>
internal sealed class WebUiSetRequest
{
    public bool? Enabled { get; set; }
    public int? Port { get; set; }
    public List<string>? AlsoReachableAs { get; set; }
}

/// <summary><c>webui.setPassword</c>. The password goes only into this message.</summary>
internal sealed class WebUiSetPasswordRequest
{
    public string Password { get; set; } = string.Empty;
}

/// <summary>The reply of <c>webui.openTicket</c>.</summary>
internal sealed class WebUiTicketDto
{
    public string Url { get; set; } = string.Empty;
}

/// <summary>The kinds of network Windows sorts connections into.</summary>
internal enum NetworkProfile { Private, Public, Domain }

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
    public static WebUiSetRequest? Changes(WebUiStatusDto saved, bool enabled, double port, string? namesText, out string? problem)
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

        var request = new WebUiSetRequest();
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
    public static List<string> AddressesToType(WebUiStatusDto status)
    {
        if (status.State != WebUiStates.Running) return [];
        if (status.LocalOnly || status.Addresses.Count == 0) return [$"http://localhost:{status.Port}/"];
        var all = new List<string>(status.Addresses);
        foreach (var name in status.AlsoReachableAs)
        {
            var address = $"http://{name}:{status.Port}/";
            if (!all.Contains(address, StringComparer.OrdinalIgnoreCase)) all.Add(address);
        }

        return all;
    }

    public static (string Text, bool IsProblem) StateText(WebUiStatusDto status) => status.State switch
    {
        WebUiStates.Starting => ("Starting…", false),
        WebUiStates.Running when status.LocalOnly => ("On, but only this PC can reach it until Windows Firewall allows it (below).", false),
        WebUiStates.Running => ("On. Other devices on your network can open it.", false),
        WebUiStates.Failed => ($"It couldn't start. {FailureWords(status.Error)}", true),
        _ => ("Off. Nothing is running and no port is open.", false),
    };

    private static string FailureWords(string? error)
        => string.IsNullOrWhiteSpace(error) ? "Check that no other program uses this port, then try again." : error.Trim();

    public static string PasswordText(WebUiStatusDto status)
        => status.HasPassword ? "A password is set." : "No password is set yet. Set one before turning this on.";

    public static string FirewallText(WebUiStatusDto status) => status.Firewall switch
    {
        WebUiFirewallStates.Ready => "Windows Firewall allows other devices to reach this.",
        WebUiFirewallStates.Missing => "Windows Firewall isn't set to allow this yet, so other devices can't reach it.",
        _ => "Whether Windows Firewall allows this couldn't be read.",
    };

    public const string PublicNetworkNote =
        "This PC's network is set to Public. Windows treats Public networks as untrusted. Setting the network to Private in Windows Settings is the safer fix; or tick \"Also allow on Public networks\" below.";

    /// <summary>The wording of the network type line; the profiles are null when they could not be read.</summary>
    public static string ProfileText(IReadOnlyList<NetworkProfile>? profiles)
    {
        if (profiles is null) return "Network type: couldn't be read.";
        if (profiles.Count == 0) return "Network type: this PC isn't connected to a network.";
        return "Network type: " + string.Join(", ", profiles.Distinct().Select(profile => profile switch
        {
            NetworkProfile.Private => "Private",
            NetworkProfile.Public => "Public",
            _ => "Domain",
        })) + ".";
    }

    /// <summary>Opening the browser needs the Host's own sign-in link: this PC, http, the port, no credentials.</summary>
    public static bool IsLocalSignInUrl(string? url, int port)
        => Uri.TryCreate(url, UriKind.Absolute, out var uri) &&
           uri.Scheme == Uri.UriSchemeHttp &&
           uri.Port == port &&
           uri.UserInfo.Length == 0 &&
           (uri.Host == "127.0.0.1" || uri.Host == "localhost" || uri.Host == "[::1]");
}

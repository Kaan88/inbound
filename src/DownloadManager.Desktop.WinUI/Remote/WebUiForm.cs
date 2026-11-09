using System.Text.RegularExpressions;
using DownloadManager.Contracts;

namespace DownloadManager.Desktop.WinUI;

// The words and checks of Options > Web UI. The messages and their types are the contract's (DownloadManager.Contracts:
// WebUiMessageTypes, WebUiStatusDto and so on); the Host answers them on the pipe only, never to a web or token session.

/// <summary>The kinds of network Windows sorts connections into.</summary>
internal enum NetworkProfile { Private, Public, Domain }

/// <summary>One connected network: its name in Windows and its kind.</summary>
internal sealed record NetworkEntry(string Name, NetworkProfile Kind);

/// <summary>The checks and the words of the Options page that don't need a window.</summary>
internal static partial class WebUiForm
{
    public const int DefaultPort = WebUiDefaults.Port;
    public const int MinPort = WebUiDefaults.MinPort;
    public const int MaxPort = WebUiDefaults.MaxPort;
    public const int MinPasswordLength = 8;
    public const int MaxPasswordLength = 1024;
    public const int MostNames = 8;
    public const string DefaultRuleName = WebUiDefaults.RuleName;

    /// <summary>
    /// The Host refused the request a status answers: an <see cref="WebUiStatusDto.Error"/> on a status that isn't a
    /// failed or restarting start (nothing changed then).
    /// </summary>
    public static bool IsRefusal(WebUiStatusDto status)
        => !string.IsNullOrWhiteSpace(status.Error) && status.State != WebUiStates.Failed && status.State != WebUiStates.Starting;

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

    public static (string Text, bool IsProblem) StateText(WebUiStatusDto status) => status.State switch
    {
        WebUiStates.Starting => ("Starting…", false),
        WebUiStates.Running when status.LocalOnly => ("On, but only this PC can reach it until Windows Firewall allows it (see Windows Firewall below). Open in browser works on this PC.", false),
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

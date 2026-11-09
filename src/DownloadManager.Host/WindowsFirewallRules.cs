using System.Runtime.InteropServices;
using DownloadManager.Contracts;
using DownloadManager.Runtime.WebUi;

namespace DownloadManager.Host;

/// <summary>
/// Reads Windows Firewall's rules through its own interface (<c>HNetCfg.FwPolicy2</c>, readable without
/// administrator rights, and the same in every Windows language, unlike <c>netsh</c>'s text). It starts no program and
/// changes nothing: the window adds the rule, with one UAC prompt.
/// <para>
/// Ready means, for at least one of the network profiles in force now (<c>CurrentProfileTypes</c>): an enabled inbound
/// allow rule with this name for the bridge program, TCP, whose local ports include the port, whose remote addresses
/// are the local subnet only, and which covers that profile; and no enabled inbound block rule for the program that
/// covers the port and that profile (Windows makes one when its own prompt is answered "Cancel", and a block rule wins
/// over any allow rule). A rule widened to other remote addresses is not the rule the window adds, so it doesn't count.
/// </para>
/// </summary>
public sealed class WindowsFirewallRules : IFirewallRules
{
    internal const int Inbound = 1;
    internal const int Block = 0;
    internal const int Allow = 1;
    internal const int Tcp = 6;
    internal const int AnyProtocol = 256;
    internal const int DomainProfile = 1;
    internal const int PrivateProfile = 2;
    internal const int PublicProfile = 4;

    private readonly Action<string>? _warn;

    public WindowsFirewallRules(Action<string>? warn = null) => _warn = warn;

    public string Check(string program, int port, string ruleName)
    {
        if (ReadRules(out var current) is not { } rules) return WebUiFirewallStates.Unknown;
        return Evaluate(rules, program, port, ruleName, current);
    }

    /// <summary>The inbound rules for any program, and the profiles in force; null when they can't be read.</summary>
    internal List<FirewallRuleFacts>? ReadRules(out int currentProfiles)
    {
        currentProfiles = 0;
        if (!OperatingSystem.IsWindows()) return null;
        if (Type.GetTypeFromProgID("HNetCfg.FwPolicy2", throwOnError: false) is not { } type) return null;
        object? policy = null;
        try
        {
            policy = Activator.CreateInstance(type);
            if (policy is null) return null;
            currentProfiles = (int)((dynamic)policy).CurrentProfileTypes;
            var rules = (object)((dynamic)policy).Rules;
            // The rule collection only enumerates through _NewEnum (IEnumVARIANT); .NET can't cast it to IEnumerable.
            var enumerator = (System.Runtime.InteropServices.ComTypes.IEnumVARIANT)((dynamic)rules)._NewEnum;
            var facts = new List<FirewallRuleFacts>();
            var one = new object[1];
            try
            {
                while (enumerator.Next(1, one, IntPtr.Zero) == 0)
                {
                    dynamic rule = one[0];
                    one[0] = null!;
                    try
                    {
                        if ((int)rule.Direction != Inbound) continue;
                        var program = (string?)rule.ApplicationName;
                        // Only rules for a program can be the bridge's (a rule for every program has none).
                        if (string.IsNullOrWhiteSpace(program)) continue;
                        var protocol = (int)rule.Protocol;
                        // Ports mean something for TCP (and any protocol) only; other rules can't be the bridge's.
                        var ported = protocol is Tcp or AnyProtocol;
                        facts.Add(new FirewallRuleFacts(
                            (string?)rule.Name,
                            program,
                            (bool)rule.Enabled,
                            (int)rule.Action,
                            protocol,
                            ported ? (string?)rule.LocalPorts : null,
                            ported ? (string?)rule.RemoteAddresses : null,
                            (int)rule.Profiles));
                    }
                    finally
                    {
                        if (Marshal.IsComObject((object)rule)) Marshal.ReleaseComObject((object)rule);
                    }
                }
            }
            finally
            {
                if (Marshal.IsComObject(enumerator)) Marshal.ReleaseComObject(enumerator);
                if (Marshal.IsComObject(rules)) Marshal.ReleaseComObject(rules);
            }

            return facts;
        }
        catch (Exception ex) when (ex is COMException or InvalidCastException or Microsoft.CSharp.RuntimeBinder.RuntimeBinderException or
                                       UnauthorizedAccessException or InvalidComObjectException or NotSupportedException)
        {
            _warn?.Invoke($"Could not read the firewall rules: {ex.GetType().Name}: {ex.Message}");
            return null;
        }
        finally
        {
            if (policy is not null && OperatingSystem.IsWindows() && Marshal.IsComObject(policy)) Marshal.ReleaseComObject(policy);
        }
    }

    /// <summary>The state for these inbound rules (see the class), with <paramref name="currentProfiles"/> the profiles in force.</summary>
    internal static string Evaluate(IEnumerable<FirewallRuleFacts> rules, string program, int port, string ruleName, int currentProfiles)
    {
        var mine = rules.Where(rule => rule.Enabled && SameProgram(rule.Program, program)).ToList();
        // None read (a PC whose network isn't identified yet): the profile the window's rule is made for.
        if ((currentProfiles & (DomainProfile | PrivateProfile | PublicProfile)) == 0) currentProfiles = PrivateProfile;
        foreach (var profile in new[] { DomainProfile, PrivateProfile, PublicProfile })
        {
            if ((currentProfiles & profile) == 0) continue;
            var allowed = mine.Any(rule => rule.Action == Allow && string.Equals(rule.Name, ruleName, StringComparison.Ordinal) &&
                                           rule.Protocol == Tcp && CoversPort(rule.LocalPorts, port) &&
                                           IsLocalSubnetOnly(rule.RemoteAddresses) && (rule.Profiles & profile) != 0);
            var blocked = mine.Any(rule => rule.Action == Block && rule.Protocol is Tcp or AnyProtocol &&
                                           CoversPort(rule.LocalPorts, port) && (rule.Profiles & profile) != 0);
            if (allowed && !blocked) return WebUiFirewallStates.Ready;
        }

        return WebUiFirewallStates.Missing;
    }

    /// <summary>The rule's remote addresses are the local subnet and nothing else (as the window makes it).</summary>
    internal static bool IsLocalSubnetOnly(string? remoteAddresses) =>
        string.Equals(remoteAddresses?.Trim(), "LocalSubnet", StringComparison.OrdinalIgnoreCase);

    internal static bool SameProgram(string? ruleProgram, string program)
    {
        if (string.IsNullOrWhiteSpace(ruleProgram)) return false;
        try
        {
            var expanded = Environment.ExpandEnvironmentVariables(ruleProgram.Trim().Trim('"'));
            return string.Equals(Path.GetFullPath(expanded), Path.GetFullPath(program), StringComparison.OrdinalIgnoreCase);
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
        {
            return false;
        }
    }

    /// <summary>A rule's local ports (<c>*</c>, <c>8080</c>, <c>8000-8100</c>, comma separated) include this one.</summary>
    internal static bool CoversPort(string? ports, int port)
    {
        if (string.IsNullOrWhiteSpace(ports)) return false;
        foreach (var part in ports.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            if (part == "*") return true;
            var dash = part.IndexOf('-');
            if (dash > 0 && int.TryParse(part[..dash], out var low) && int.TryParse(part[(dash + 1)..], out var high))
            {
                if (port >= low && port <= high) return true;
            }
            else if (int.TryParse(part, out var single) && single == port)
            {
                return true;
            }
        }

        return false;
    }
}

/// <summary>What <see cref="WindowsFirewallRules"/> reads of one inbound rule.</summary>
internal sealed record FirewallRuleFacts(
    string? Name,
    string? Program,
    bool Enabled,
    int Action,
    int Protocol,
    string? LocalPorts,
    string? RemoteAddresses,
    int Profiles);

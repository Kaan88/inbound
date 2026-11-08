using System.Runtime.InteropServices;
using DownloadManager.Contracts;
using DownloadManager.Runtime.WebUi;

namespace DownloadManager.Host;

/// <summary>
/// Reads Windows Firewall's rules through its own interface (<c>HNetCfg.FwPolicy2</c>, readable without
/// administrator rights, and the same in every Windows language, unlike <c>netsh</c>'s text). It starts no program and
/// changes nothing: the window adds the rule, with one UAC prompt.
/// <para>
/// Ready means: an enabled inbound allow rule with this name for the bridge program, TCP, whose local ports include the
/// port, and no enabled inbound block rule for the program (Windows makes one when its own prompt is answered "Cancel",
/// and a block rule wins over any allow rule).
/// </para>
/// </summary>
public sealed class WindowsFirewallRules : IFirewallRules
{
    private const int Inbound = 1;
    private const int Block = 0;
    private const int Allow = 1;
    private const int Tcp = 6;
    private const int AnyProtocol = 256;

    private readonly Action<string>? _warn;

    public WindowsFirewallRules(Action<string>? warn = null) => _warn = warn;

    public string Check(string program, int port, string ruleName)
    {
        if (!OperatingSystem.IsWindows()) return WebUiFirewallStates.Unknown;
        if (Type.GetTypeFromProgID("HNetCfg.FwPolicy2", throwOnError: false) is not { } type) return WebUiFirewallStates.Unknown;
        object? policy = null;
        try
        {
            policy = Activator.CreateInstance(type);
            if (policy is null) return WebUiFirewallStates.Unknown;
            var rules = (object)((dynamic)policy).Rules;
            // The rule collection only enumerates through _NewEnum (IEnumVARIANT); .NET can't cast it to IEnumerable.
            var enumerator = (System.Runtime.InteropServices.ComTypes.IEnumVARIANT)((dynamic)rules)._NewEnum;
            var allowed = false;
            var blocked = false;
            var one = new object[1];
            try
            {
                while (enumerator.Next(1, one, IntPtr.Zero) == 0)
                {
                    dynamic rule = one[0];
                    one[0] = null!;
                    try
                    {
                        if (!SameProgram((string?)rule.ApplicationName, program) || (int)rule.Direction != Inbound || !(bool)rule.Enabled) continue;
                        var action = (int)rule.Action;
                        if (action == Block)
                        {
                            // Any block rule for the program that covers TCP and this port.
                            if ((int)rule.Protocol is Tcp or AnyProtocol && CoversPort((string?)rule.LocalPorts, port)) blocked = true;
                        }
                        else if (action == Allow && string.Equals((string?)rule.Name, ruleName, StringComparison.Ordinal) &&
                                 (int)rule.Protocol == Tcp && CoversPort((string?)rule.LocalPorts, port))
                        {
                            allowed = true;
                        }
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

            return allowed && !blocked ? WebUiFirewallStates.Ready : WebUiFirewallStates.Missing;
        }
        catch (Exception ex) when (ex is COMException or InvalidCastException or Microsoft.CSharp.RuntimeBinder.RuntimeBinderException or
                                       UnauthorizedAccessException or InvalidComObjectException or NotSupportedException)
        {
            _warn?.Invoke($"Could not read the firewall rules: {ex.GetType().Name}: {ex.Message}");
            return WebUiFirewallStates.Unknown;
        }
        finally
        {
            if (policy is not null && OperatingSystem.IsWindows() && Marshal.IsComObject(policy)) Marshal.ReleaseComObject(policy);
        }
    }

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

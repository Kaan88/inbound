using System.Runtime.InteropServices;

namespace DownloadManager.Desktop.WinUI;

/// <summary>Whether Windows Firewall lets peers connect in to the torrent engine.</summary>
internal enum TorrentFirewallState
{
    /// <summary>The rules could not be read.</summary>
    Unknown,
    /// <summary>No rule allows it on the networks in use now.</summary>
    Missing,
    /// <summary>Allow rules cover TCP and UDP on a network in use now, and no block rule does.</summary>
    Allowed,
    /// <summary>A block rule for the engine (Windows makes one when its own prompt is cancelled) wins over any allow rule.</summary>
    Blocked,
}

/// <summary>What the Options page shows: the state, and whether this app's own rules exist (so they can be removed).</summary>
internal sealed record TorrentFirewallStatus(TorrentFirewallState State, bool HasOwnRules);

/// <summary>What is read of one inbound rule for the torrent engine.</summary>
internal sealed record TorrentFirewallRule(string? Name, string? Program, bool Enabled, int Action, int Protocol, int Profiles);

/// <summary>
/// Options › Torrents' Windows Firewall rule (T2): one inbound allow rule per protocol (TCP and UDP) for
/// <c>inbound-torrent.exe</c>, on Private and Public networks, any port (the port can change in Options). As with
/// <see cref="WebUiFirewall"/>, only a System32 program runs elevated, the command is built here from fixed text, and the
/// program must be the engine in this app's own folder. Reading needs no elevation (<c>HNetCfg.FwPolicy2</c>, the same in
/// every Windows language). Uninstall removes the rules by their name and program (installer/DownloadManager.iss).
/// </summary>
internal static class TorrentFirewall
{
    /// <summary>The name of both rules. The uninstaller looks for this exact text.</summary>
    internal const string RuleName = "Inbound Download Manager torrents";

    internal const string WorkerFileName = "inbound-torrent.exe";

    internal const int Inbound = 1;
    internal const int Block = 0;
    internal const int Allow = 1;
    internal const int Tcp = 6;
    internal const int Udp = 17;
    internal const int AnyProtocol = 256;
    internal const int DomainProfile = 1;
    internal const int PrivateProfile = 2;
    internal const int PublicProfile = 4;

    /// <summary>The torrent engine beside this app's own program (Setup puts them in one folder).</summary>
    internal static string WorkerPath(string? installDirectory = null)
        => Path.Combine(installDirectory ?? WebUiFirewall.InstallDirectory(), WorkerFileName);

    /// <summary>Why this program can't be named in a rule, or null.</summary>
    internal static string? WorkerPathProblem(string? path, string? installDirectory = null)
    {
        if (string.IsNullOrWhiteSpace(path) || !Path.IsPathFullyQualified(path) || path.StartsWith(@"\\", StringComparison.Ordinal) ||
            path.Any(char.IsControl) || path.Contains('"') || path.Length > 1000)
            return "The torrent engine's place isn't usable, so the firewall can't be set up from here.";
        if (!string.Equals(Path.GetFileName(path), WorkerFileName, StringComparison.OrdinalIgnoreCase))
            return "The firewall can only be set up for the torrent engine.";
        var folder = Path.GetFullPath(installDirectory ?? WebUiFirewall.InstallDirectory());
        if (!folder.EndsWith(Path.DirectorySeparatorChar)) folder += Path.DirectorySeparatorChar;
        if (!Path.GetFullPath(path).StartsWith(folder, StringComparison.OrdinalIgnoreCase))
            return "The torrent engine isn't in this app's own folder, so the firewall can't be set up from here.";
        return null;
    }

    /// <summary>
    /// The elevated command that allows incoming connections: removes the inbound block rules Windows made for the engine
    /// and this app's own rules, then adds one allow rule for TCP and one for UDP. Exits 0 when all of it worked.
    /// </summary>
    /// <exception cref="ArgumentException">The program can't be used.</exception>
    internal static FirewallCommand BuildAllowCommand(string workerPath, string? systemDirectory = null, string? installDirectory = null)
    {
        if (WorkerPathProblem(workerPath, installDirectory) is { } problem) throw new ArgumentException(problem, nameof(workerPath));
        var script = AllowScript(workerPath, systemDirectory ?? Environment.SystemDirectory);
        return new FirewallCommand(WebUiFirewall.PowerShellPath(systemDirectory),
            $"-NoProfile -NonInteractive -EncodedCommand {Convert.ToBase64String(System.Text.Encoding.Unicode.GetBytes(script))}");
    }

    /// <summary>The text <see cref="BuildAllowCommand"/> encodes. The program and name go in quoted and are compared exactly.</summary>
    internal static string AllowScript(string workerPath, string systemDirectory)
    {
        var modules = WebUiFirewall.ModuleDirectory(systemDirectory);
        return string.Join("\n",
            "$ErrorActionPreference = 'Stop'",
            "try {",
            $"  $env:PSModulePath = {WebUiFirewall.Quote(modules)}",
            $"  Import-Module {WebUiFirewall.Quote(Path.Combine(modules, "NetSecurity", "NetSecurity.psd1"))} -ErrorAction Stop",
            $"  $program = {WebUiFirewall.Quote(workerPath)}",
            $"  $name = {WebUiFirewall.Quote(RuleName)}",
            "  $mine = @(Get-NetFirewallApplicationFilter -PolicyStore PersistentStore | Where-Object { $_.Program -eq $program } | Get-NetFirewallRule)",
            "  $mine | Where-Object { $_.Direction -eq 'Inbound' -and ($_.Action -eq 'Block' -or $_.DisplayName -ceq $name) } | Remove-NetFirewallRule",
            "  New-NetFirewallRule -DisplayName $name -Direction Inbound -Action Allow -Program $program -Protocol TCP -Profile Private,Public | Out-Null",
            "  New-NetFirewallRule -DisplayName $name -Direction Inbound -Action Allow -Program $program -Protocol UDP -Profile Private,Public | Out-Null",
            "  exit 0",
            "} catch {",
            "  exit 1",
            "}");
    }

    /// <summary>
    /// The elevated command that removes this app's rules: netsh deletes every inbound rule with this name for this
    /// program and nothing else. Exits 0 when it deleted at least one.
    /// </summary>
    /// <exception cref="ArgumentException">The program can't be used.</exception>
    internal static FirewallCommand BuildRemoveCommand(string workerPath, string? systemDirectory = null, string? installDirectory = null)
    {
        if (WorkerPathProblem(workerPath, installDirectory) is { } problem) throw new ArgumentException(problem, nameof(workerPath));
        return new FirewallCommand(Path.Combine(systemDirectory ?? Environment.SystemDirectory, "netsh.exe"),
            $"advfirewall firewall delete rule name=\"{RuleName}\" dir=in program=\"{workerPath}\"");
    }

    /// <summary>The state for these inbound rules, with <paramref name="currentProfiles"/> the network profiles in force.</summary>
    internal static TorrentFirewallStatus Evaluate(IEnumerable<TorrentFirewallRule> rules, string workerPath, int currentProfiles)
    {
        var mine = rules.Where(rule => WindowsSameProgram(rule.Program, workerPath)).ToList();
        var own = mine.Any(rule => string.Equals(rule.Name, RuleName, StringComparison.Ordinal));
        var enabled = mine.Where(rule => rule.Enabled).ToList();
        // None read (a network not identified yet): Windows treats it as Public.
        if ((currentProfiles & (DomainProfile | PrivateProfile | PublicProfile)) == 0) currentProfiles = PublicProfile;
        var blocked = false;
        foreach (var profile in new[] { DomainProfile, PrivateProfile, PublicProfile })
        {
            if ((currentProfiles & profile) == 0) continue;
            var onProfile = enabled.Where(rule => (rule.Profiles & profile) != 0).ToList();
            if (onProfile.Any(rule => rule.Action == Block)) { blocked = true; continue; }
            bool Allows(int protocol) => onProfile.Any(rule => rule.Action == Allow && (rule.Protocol == protocol || rule.Protocol == AnyProtocol));
            if (Allows(Tcp) && Allows(Udp)) return new TorrentFirewallStatus(TorrentFirewallState.Allowed, own);
        }

        return new TorrentFirewallStatus(blocked ? TorrentFirewallState.Blocked : TorrentFirewallState.Missing, own);
    }

    internal static bool WindowsSameProgram(string? ruleProgram, string program)
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

    /// <summary>The page's text for a state.</summary>
    internal static string Describe(TorrentFirewallState state) => state switch
    {
        TorrentFirewallState.Allowed => "Windows Firewall allows incoming connections to the torrent engine.",
        TorrentFirewallState.Blocked => "Windows Firewall blocks incoming connections to the torrent engine. Allowing them replaces that block.",
        TorrentFirewallState.Missing => "Windows Firewall doesn't allow incoming connections to the torrent engine yet. Downloads still work; fewer peers can reach you.",
        _ => "Windows Firewall's rules couldn't be read.",
    };

    /// <summary>Reads the rules without elevation, off the window's thread; <see cref="TorrentFirewallState.Unknown"/> when they can't be read.</summary>
    internal static Task<TorrentFirewallStatus> ReadAsync(string workerPath)
        => Task.Run(() => ReadRules(out var current) is { } rules
            ? Evaluate(rules, workerPath, current)
            : new TorrentFirewallStatus(TorrentFirewallState.Unknown, false));

    /// <summary>The inbound rules for any program, and the profiles in force; null when they can't be read.</summary>
    private static List<TorrentFirewallRule>? ReadRules(out int currentProfiles)
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
            // The rule collection only enumerates through _NewEnum (IEnumVARIANT).
            var enumerator = (System.Runtime.InteropServices.ComTypes.IEnumVARIANT)((dynamic)rules)._NewEnum;
            var facts = new List<TorrentFirewallRule>();
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
                        if (string.IsNullOrWhiteSpace(program) ||
                            !string.Equals(Path.GetFileName(program.Trim().Trim('"')), WorkerFileName, StringComparison.OrdinalIgnoreCase)) continue;
                        facts.Add(new TorrentFirewallRule((string?)rule.Name, program, (bool)rule.Enabled, (int)rule.Action,
                            (int)rule.Protocol, (int)rule.Profiles));
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
            return null;
        }
        finally
        {
            if (policy is not null && OperatingSystem.IsWindows() && Marshal.IsComObject(policy)) Marshal.ReleaseComObject(policy);
        }
    }
}

/// <summary>Options › Torrents' port hint (T2): one plain line from what the Host reports of the engine's listening.</summary>
/// <summary>The listening-port box of Options › Torrents, which is left as typed (its value is not clamped).</summary>
internal static class TorrentPortInput
{
    /// <summary>Why the typed port can't be saved, or null. Empty (NaN) keeps the saved port.</summary>
    internal static string? Problem(double value)
    {
        if (double.IsNaN(value)) return null;
        var rounded = Math.Round(value);
        return rounded >= DownloadManager.Contracts.TorrentListenPorts.Min && rounded <= DownloadManager.Contracts.TorrentListenPorts.Max
            ? null
            : $"The torrent port must be {DownloadManager.Contracts.TorrentListenPorts.Min} to {DownloadManager.Contracts.TorrentListenPorts.Max}.";
    }
}

internal static class TorrentPortHint
{
    /// <summary>The line, or null when the Host didn't answer.</summary>
    internal static string? Describe(DownloadManager.Contracts.TorrentNetworkResult? network)
    {
        if (network is not { Ok: true }) return null;
        if (!network.Ran) return "The torrent engine hasn't run since Inbound started, so whether peers can reach you isn't known yet.";
        if (network.Port <= 0)
        {
            if (!string.IsNullOrWhiteSpace(network.ListenError))
                return $"The port couldn't be opened: {network.ListenError}. Choose another port.";
            return network.Running ? "The torrent engine is starting." : "The torrent engine didn't say which port it used.";
        }

        var text = (network.Running ? "Listening on port " : "Last run listened on port ") + network.Port + ".";
        if (network.UnusablePort is > 0)
            text = $"Port {network.UnusablePort} couldn't be opened, so the torrent engine used port {network.Port} for this run. Choose another port.";
        if (network.MappedPort is > 0) text += $" Your router forwards port {network.MappedPort} to it.";
        if (network.IncomingSeen) text += " A peer connected in, so others can reach you.";
        else if (network.Running) text += " No peer has connected in yet.";
        else text += " No peer connected in: a firewall or your router may be blocking the port.";
        return text;
    }
}

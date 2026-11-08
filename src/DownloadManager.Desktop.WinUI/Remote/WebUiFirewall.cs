using System.ComponentModel;
using System.Diagnostics;
using System.Text;

namespace DownloadManager.Desktop.WinUI;

/// <summary>A program to start: its full path and its command line.</summary>
internal sealed record FirewallCommand(string FileName, string Arguments);

/// <summary><c>TimedOut</c>: no answer in time. The elevated command may still finish, so it is not a failure.</summary>
internal enum FirewallRunOutcome { Done, Declined, Failed, TimedOut }

/// <summary>
/// The Windows Firewall step of the web UI page (design section 3). One elevated command, and only a
/// System32 program ever runs elevated: the command is built here from fixed text, and never takes a
/// program from the install folder. Nothing the Host sent is trusted: the rule's name must be the fixed one, the
/// program must be inside this app's own folder, and both go into the script as quoted data that is compared
/// exactly, never as a pattern. The script uses the firewall module of System32 only, never one found through
/// the user's module folders. Windows PowerShell rather than netsh because one prompt has to run several steps
/// (netsh can't remove only block rules, and a script file for it would be a user-writable file); the design
/// (section 3) says so.
/// </summary>
internal static class WebUiFirewall
{
    /// <summary>The program the rule names. The Host says where it is; nothing else is allowed through.</summary>
    internal const string BridgeFileName = "DownloadManager.Server.exe";

    private static readonly TimeSpan ElevatedPatience = TimeSpan.FromMinutes(2);
    private static readonly TimeSpan ReadPatience = TimeSpan.FromSeconds(20);

    /// <summary>The Windows PowerShell of this PC, which is in System32 and never found by a search path.</summary>
    internal static string PowerShellPath(string? systemDirectory = null)
        => Path.Combine(systemDirectory ?? Environment.SystemDirectory, "WindowsPowerShell", "v1.0", "powershell.exe");

    /// <summary>Only Windows PowerShell and netsh, in the system folder, may be started by this page.</summary>
    internal static bool IsSystemProgram(string fileName, string? systemDirectory = null)
    {
        var system = Path.GetFullPath(systemDirectory ?? Environment.SystemDirectory);
        if (!Path.IsPathFullyQualified(fileName)) return false;
        var full = Path.GetFullPath(fileName);
        return string.Equals(full, Path.GetFullPath(PowerShellPath(system)), StringComparison.OrdinalIgnoreCase) ||
               string.Equals(full, Path.Combine(system, "netsh.exe"), StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>The folder this app runs from; the web UI program must be inside it.</summary>
    internal static string InstallDirectory() => AppContext.BaseDirectory;

    /// <summary>Why this program path can't be named in a rule, or null.</summary>
    internal static string? BridgePathProblem(string? path, string? installDirectory = null)
    {
        if (string.IsNullOrWhiteSpace(path) || !Path.IsPathFullyQualified(path) || path.StartsWith(@"\\", StringComparison.Ordinal))
            return "The web UI program's place isn't known, so the firewall can't be set up from here.";
        if (path.Any(char.IsControl) || path.Length > 1000)
            return "The web UI program's place isn't usable, so the firewall can't be set up from here.";
        if (!string.Equals(Path.GetFileName(path), BridgeFileName, StringComparison.OrdinalIgnoreCase))
            return "The firewall can only be set up for the web UI program.";
        var folder = Path.GetFullPath(installDirectory ?? InstallDirectory());
        if (!folder.EndsWith(Path.DirectorySeparatorChar)) folder += Path.DirectorySeparatorChar;
        if (!Path.GetFullPath(path).StartsWith(folder, StringComparison.OrdinalIgnoreCase))
            return "The web UI program isn't in this app's own folder, so the firewall can't be set up from here.";
        return null;
    }

    /// <summary>
    /// The one elevated command: removes the inbound block rules Windows made for the web UI program, replaces this
    /// app's own rule for it, and adds one inbound allow rule for the program, TCP, this port, the local subnet,
    /// on Private networks and, if asked, Public ones. Exits 0 when all of it worked.
    /// </summary>
    /// <exception cref="ArgumentException">The path, port or name can't be used.</exception>
    internal static FirewallCommand BuildAllowCommand(string bridgePath, int port, string ruleName, bool alsoPublic, string? systemDirectory = null, string? installDirectory = null)
    {
        if (BridgePathProblem(bridgePath, installDirectory) is { } pathProblem) throw new ArgumentException(pathProblem, nameof(bridgePath));
        if (WebUiForm.PortProblem(port) is { } portProblem) throw new ArgumentException(portProblem, nameof(port));
        if (!string.Equals(ruleName, WebUiForm.DefaultRuleName, StringComparison.Ordinal))
            throw new ArgumentException("The rule's name can't be used.", nameof(ruleName));

        var script = AllowScript(bridgePath, port, ruleName, alsoPublic, systemDirectory ?? Environment.SystemDirectory);
        return Encode(script, systemDirectory);
    }

    private static FirewallCommand Encode(string script, string? systemDirectory)
        => new(PowerShellPath(systemDirectory), $"-NoProfile -NonInteractive -EncodedCommand {Convert.ToBase64String(Encoding.Unicode.GetBytes(script))}");

    /// <summary>The module folder of System32. The scripts use this one and no other.</summary>
    internal static string ModuleDirectory(string systemDirectory) => Path.Combine(systemDirectory, "WindowsPowerShell", "v1.0", "Modules");

    /// <summary>The first lines of both scripts: only the system folder's modules, and the one needed, imported by its full path.</summary>
    private static string[] PinnedModule(string systemDirectory, string module)
    {
        var modules = ModuleDirectory(systemDirectory);
        return
        [
            $"  $env:PSModulePath = {Quote(modules)}",
            $"  Import-Module {Quote(Path.Combine(modules, module, module + ".psd1"))} -ErrorAction Stop",
        ];
    }

    /// <summary>
    /// The text that <see cref="BuildAllowCommand"/> encodes. Data goes in as single-quoted strings, and the rules are
    /// found by comparing the program and the name exactly: the cmdlets' own -Program and -DisplayName would read them as patterns.
    /// </summary>
    internal static string AllowScript(string bridgePath, int port, string ruleName, bool alsoPublic, string systemDirectory)
    {
        var profile = alsoPublic ? "Private,Public" : "Private";
        return string.Join("\n",
            new[] { "$ErrorActionPreference = 'Stop'", "try {" }
            .Concat(PinnedModule(systemDirectory, "NetSecurity"))
            .Concat(new[]
            {
                $"  $program = {Quote(bridgePath)}",
                $"  $name = {Quote(ruleName)}",
                "  $mine = @(Get-NetFirewallApplicationFilter -PolicyStore PersistentStore | Where-Object { $_.Program -eq $program } | Get-NetFirewallRule)",
                "  $mine | Where-Object { $_.Direction -eq 'Inbound' -and ($_.Action -eq 'Block' -or $_.DisplayName -ceq $name) } | Remove-NetFirewallRule",
                $"  New-NetFirewallRule -DisplayName $name -Direction Inbound -Action Allow -Program $program -Protocol TCP -LocalPort {port} -RemoteAddress LocalSubnet -Profile {profile} | Out-Null",
                "  exit 0",
                "} catch {",
                "  exit 1",
                "}",
            }));
    }

    /// <summary>A PowerShell single-quoted string. PowerShell treats the curly single quotes as quotes too, so each is doubled.</summary>
    internal static string Quote(string text)
    {
        var quoted = new StringBuilder("'");
        foreach (var c in text)
        {
            quoted.Append(c);
            if (c is '\'' or '‘' or '’' or '‚' or '‛') quoted.Append(c);
        }

        return quoted.Append('\'').ToString();
    }

    /// <summary>Reads the connected networks and their types without being elevated: one line per network, name, a bar, kind.</summary>
    internal static FirewallCommand NetworkProfileCommand(string? systemDirectory = null)
    {
        var script = string.Join("\n",
            new[] { "$ErrorActionPreference = 'Stop'", "try {" }
            .Concat(PinnedModule(systemDirectory ?? Environment.SystemDirectory, "NetConnection"))
            .Concat(new[]
            {
                "  Get-NetConnectionProfile | ForEach-Object { [string]$_.InterfaceAlias + '|' + [string]$_.NetworkCategory }",
                "  exit 0",
                "} catch {",
                "  exit 1",
                "}",
            }));
        return Encode(script, systemDirectory);
    }

    /// <summary>The networks in the output of <see cref="NetworkProfileCommand"/>, one per line.</summary>
    internal static List<NetworkEntry> ParseProfiles(string? output)
    {
        var networks = new List<NetworkEntry>();
        foreach (var line in (output ?? string.Empty).Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries))
        {
            var bar = line.LastIndexOf('|');
            var name = bar < 0 ? string.Empty : line[..bar].Trim();
            var word = (bar < 0 ? line : line[(bar + 1)..]).Trim();
            if (word.Equals("Private", StringComparison.OrdinalIgnoreCase)) networks.Add(new NetworkEntry(name, NetworkProfile.Private));
            else if (word.Equals("Public", StringComparison.OrdinalIgnoreCase)) networks.Add(new NetworkEntry(name, NetworkProfile.Public));
            else if (word.StartsWith("Domain", StringComparison.OrdinalIgnoreCase)) networks.Add(new NetworkEntry(name, NetworkProfile.Domain));
        }

        return networks;
    }

    /// <summary>The network types of the connected networks, or null when they could not be read.</summary>
    internal static async Task<List<NetworkEntry>?> ReadNetworkProfilesAsync(CancellationToken cancellationToken)
    {
        var command = NetworkProfileCommand();
        if (!IsSystemProgram(command.FileName) || !File.Exists(command.FileName)) return null;
        using var limit = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        limit.CancelAfter(ReadPatience);
        try
        {
            using var process = Process.Start(new ProcessStartInfo(command.FileName, command.Arguments)
            {
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                StandardOutputEncoding = Encoding.UTF8,
            });
            if (process is null) return null;
            try
            {
                var errors = process.StandardError.ReadToEndAsync(limit.Token);
                var output = await process.StandardOutput.ReadToEndAsync(limit.Token).ConfigureAwait(true);
                await process.WaitForExitAsync(limit.Token).ConfigureAwait(true);
                await errors.ConfigureAwait(true);
                return process.ExitCode == 0 ? ParseProfiles(output) : null;
            }
            catch (OperationCanceledException)
            {
                try { process.Kill(entireProcessTree: true); } catch (Exception ex) when (ex is InvalidOperationException or Win32Exception) { }
                if (cancellationToken.IsCancellationRequested) throw;
                return null;
            }
        }
        catch (Exception ex) when (ex is Win32Exception or InvalidOperationException or IOException)
        {
            return null;
        }
    }

    /// <summary>Starts the command elevated and returns its exit code. Process.Start with "runas" waits for the prompt on its thread, so it runs off the window's thread.</summary>
    private static async Task<int> StartElevatedAsync(FirewallCommand command, CancellationToken cancellationToken)
    {
        var process = await Task.Run(() => Process.Start(new ProcessStartInfo(command.FileName, command.Arguments)
        {
            UseShellExecute = true,
            Verb = "runas",
            WindowStyle = ProcessWindowStyle.Hidden,
        }), cancellationToken).ConfigureAwait(true) ?? throw new InvalidOperationException("The command didn't start.");
        using (process)
        {
            await process.WaitForExitAsync(cancellationToken).ConfigureAwait(true);
            return process.ExitCode;
        }
    }

    /// <summary>
    /// Runs the command after one Windows permission prompt. Declining the prompt is an outcome, not a failure; no
    /// answer in time is <see cref="FirewallRunOutcome.TimedOut"/>, because the command may still be running.
    /// <paramref name="runner"/> and <paramref name="patience"/> are for tests.
    /// </summary>
    internal static async Task<FirewallRunOutcome> RunElevatedAsync(FirewallCommand command, CancellationToken cancellationToken,
        Func<FirewallCommand, CancellationToken, Task<int>>? runner = null, TimeSpan? patience = null)
    {
        if (runner is null && (!IsSystemProgram(command.FileName) || !File.Exists(command.FileName))) return FirewallRunOutcome.Failed;
        using var limit = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        limit.CancelAfter(patience ?? ElevatedPatience);
        try
        {
            var exitCode = await (runner ?? StartElevatedAsync)(command, limit.Token).ConfigureAwait(true);
            return exitCode == 0 ? FirewallRunOutcome.Done : FirewallRunOutcome.Failed;
        }
        catch (Win32Exception ex) when (ex.NativeErrorCode == 1223)
        {
            // ERROR_CANCELLED: the person said no at the Windows prompt.
            return FirewallRunOutcome.Declined;
        }
        catch (Exception ex) when (ex is Win32Exception or InvalidOperationException)
        {
            return FirewallRunOutcome.Failed;
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            return FirewallRunOutcome.TimedOut;
        }
    }
}

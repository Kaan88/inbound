using System.ComponentModel;
using System.Diagnostics;
using System.Text;

namespace DownloadManager.Desktop.WinUI;

/// <summary>A program to start: its full path and its command line.</summary>
internal sealed record FirewallCommand(string FileName, string Arguments);

internal enum FirewallRunOutcome { Done, Declined, Failed }

/// <summary>
/// The Windows Firewall step of the web UI page (design section 3). One elevated command, and only a
/// System32 program ever runs elevated: the command is built here from fixed text, and never takes a
/// program from the install folder or from anything the Host sent apart from the bridge's path, the port and the
/// rule's name, which go in as quoted data.
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

    /// <summary>Why this program path can't be named in a rule, or null.</summary>
    internal static string? BridgePathProblem(string? path)
    {
        if (string.IsNullOrWhiteSpace(path) || !Path.IsPathFullyQualified(path) || path.StartsWith(@"\\", StringComparison.Ordinal))
            return "The web UI program's place isn't known, so the firewall can't be set up from here.";
        if (path.Any(char.IsControl) || path.Length > 1000)
            return "The web UI program's place isn't usable, so the firewall can't be set up from here.";
        if (!string.Equals(Path.GetFileName(path), BridgeFileName, StringComparison.OrdinalIgnoreCase))
            return "The firewall can only be set up for the web UI program.";
        return null;
    }

    /// <summary>
    /// The one elevated command: removes the inbound block rules Windows made for the web UI program, replaces a
    /// rule of the same name, and adds one inbound allow rule for the program, TCP, this port, the local subnet,
    /// on Private networks and, if asked, Public ones. Exits 0 when all of it worked.
    /// </summary>
    /// <exception cref="ArgumentException">The path, port or name can't be used.</exception>
    internal static FirewallCommand BuildAllowCommand(string bridgePath, int port, string ruleName, bool alsoPublic, string? systemDirectory = null)
    {
        if (BridgePathProblem(bridgePath) is { } pathProblem) throw new ArgumentException(pathProblem, nameof(bridgePath));
        if (WebUiForm.PortProblem(port) is { } portProblem) throw new ArgumentException(portProblem, nameof(port));
        if (string.IsNullOrWhiteSpace(ruleName) || ruleName.Length > 100 || ruleName.Any(char.IsControl))
            throw new ArgumentException("The rule's name can't be used.", nameof(ruleName));

        var script = AllowScript(bridgePath, port, ruleName, alsoPublic);
        var encoded = Convert.ToBase64String(Encoding.Unicode.GetBytes(script));
        return new FirewallCommand(PowerShellPath(systemDirectory), $"-NoProfile -NonInteractive -EncodedCommand {encoded}");
    }

    /// <summary>The text that <see cref="BuildAllowCommand"/> encodes. Data goes in as single-quoted strings.</summary>
    internal static string AllowScript(string bridgePath, int port, string ruleName, bool alsoPublic)
    {
        var profile = alsoPublic ? "Private,Public" : "Private";
        return string.Join("\n",
            "$ErrorActionPreference = 'Stop'",
            "try {",
            $"  $program = {Quote(bridgePath)}",
            $"  $name = {Quote(ruleName)}",
            "  Get-NetFirewallApplicationFilter -Program $program -ErrorAction SilentlyContinue | Get-NetFirewallRule | Where-Object { $_.Direction -eq 'Inbound' -and $_.Action -eq 'Block' } | Remove-NetFirewallRule",
            "  Get-NetFirewallRule -DisplayName $name -ErrorAction SilentlyContinue | Remove-NetFirewallRule",
            $"  New-NetFirewallRule -DisplayName $name -Direction Inbound -Action Allow -Program $program -Protocol TCP -LocalPort {port} -RemoteAddress LocalSubnet -Profile {profile} | Out-Null",
            "  exit 0",
            "} catch {",
            "  exit 1",
            "}");
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

    /// <summary>Reads the network type without being elevated.</summary>
    internal static FirewallCommand NetworkProfileCommand(string? systemDirectory = null)
        => new(PowerShellPath(systemDirectory),
            "-NoProfile -NonInteractive -Command \"Get-NetConnectionProfile | ForEach-Object { [string]$_.NetworkCategory }\"");

    /// <summary>The kinds in the output of <see cref="NetworkProfileCommand"/>, one per line.</summary>
    internal static List<NetworkProfile> ParseProfiles(string? output)
    {
        var kinds = new List<NetworkProfile>();
        foreach (var line in (output ?? string.Empty).Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries))
        {
            var word = line.Trim();
            if (word.Equals("Private", StringComparison.OrdinalIgnoreCase)) kinds.Add(NetworkProfile.Private);
            else if (word.Equals("Public", StringComparison.OrdinalIgnoreCase)) kinds.Add(NetworkProfile.Public);
            else if (word.StartsWith("Domain", StringComparison.OrdinalIgnoreCase)) kinds.Add(NetworkProfile.Domain);
        }

        return kinds;
    }

    /// <summary>The network types of the connected networks, or null when they could not be read.</summary>
    internal static async Task<List<NetworkProfile>?> ReadNetworkProfilesAsync(CancellationToken cancellationToken)
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

    /// <summary>Runs the command after one Windows permission prompt. Declining the prompt is an outcome, not a failure.</summary>
    internal static async Task<FirewallRunOutcome> RunElevatedAsync(FirewallCommand command, CancellationToken cancellationToken)
    {
        if (!IsSystemProgram(command.FileName) || !File.Exists(command.FileName)) return FirewallRunOutcome.Failed;
        using var limit = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        limit.CancelAfter(ElevatedPatience);
        try
        {
            using var process = Process.Start(new ProcessStartInfo(command.FileName, command.Arguments)
            {
                UseShellExecute = true,
                Verb = "runas",
                WindowStyle = ProcessWindowStyle.Hidden,
            });
            if (process is null) return FirewallRunOutcome.Failed;
            await process.WaitForExitAsync(limit.Token).ConfigureAwait(true);
            return process.ExitCode == 0 ? FirewallRunOutcome.Done : FirewallRunOutcome.Failed;
        }
        catch (Win32Exception ex) when (ex.NativeErrorCode == 1223)
        {
            // ERROR_CANCELLED: the person said no at the Windows prompt.
            return FirewallRunOutcome.Declined;
        }
        catch (Win32Exception)
        {
            return FirewallRunOutcome.Failed;
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            return FirewallRunOutcome.Failed;
        }
    }
}

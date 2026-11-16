using System.Diagnostics;
using DownloadManager.Contracts;

namespace DownloadManager.Runtime;

/// <summary>
/// MAC.1: after Inbound.app was replaced by an update. A small shell, in the background so the Host's exit doesn't take
/// it along, waits for the Host to be gone, then starts the new bundle: through LaunchServices (<c>open</c>) when a
/// window was open, so the window comes back as the app's own, else only its Host, so an update in the night leaves no
/// window on the screen. As <see cref="AppImageRestarter"/> does for an AppImage.
/// </summary>
public static class MacAppRestarter
{
    /// <summary>
    /// The shell: <c>$1</c> the Host's process id, <c>$2</c> the bundle, <c>$3</c> <c>open</c> or empty, <c>$4</c> the
    /// old bundle set aside, removed once the Host is gone (its programs loaded from it until then), or empty.
    /// </summary>
    public const string Script =
        "while kill -0 \"$1\" 2>/dev/null; do sleep 0.2; done; " +
        "if [ -n \"$4\" ]; then rm -rf \"$4\"; fi; " +
        "if [ \"$3\" = open ]; then exec /usr/bin/open -n -a \"$2\"; fi; " +
        "exec \"$2/Contents/Resources/host/inbound-host\" --background";

    public static void Start(string bundle, int hostProcessId, string? oldBundle = null, bool? reopenWindow = null)
    {
        if (!OperatingSystem.IsMacOS()) throw new InvalidOperationException("Inbound.app is a macOS bundle.");
        var open = reopenWindow ?? WindowRunning(bundle);
        DetachedProcess.Start("/bin/sh", Arguments(bundle, hostProcessId, open, oldBundle));
    }

    public static string[] Arguments(string bundle, int hostProcessId, bool reopenWindow, string? oldBundle = null) =>
        ["-c", Script, "inbound-restart", hostProcessId.ToString(System.Globalization.CultureInfo.InvariantCulture),
            bundle, reopenWindow ? "open" : string.Empty, oldBundle ?? string.Empty];

    /// <summary>
    /// Whether this bundle's window runs: a process of this user whose command line is the window program under the
    /// bundle. Matched on the path, as macOS cuts process names to 16 characters and the window's is longer.
    /// </summary>
    [System.Runtime.Versioning.SupportedOSPlatform("macos")]
    public static bool WindowRunning(string bundle)
    {
        try
        {
            var start = new ProcessStartInfo("/usr/bin/pgrep")
            {
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
            };
            start.ArgumentList.Add("-U");
            start.ArgumentList.Add(UnixSockets.EffectiveUserId().ToString(System.Globalization.CultureInfo.InvariantCulture));
            start.ArgumentList.Add("-f");
            start.ArgumentList.Add(Pattern(bundle));
            using var pgrep = Process.Start(start);
            if (pgrep is null) return false;
            pgrep.StandardOutput.ReadToEnd();
            pgrep.StandardError.ReadToEnd();
            if (!pgrep.WaitForExit(5000)) { pgrep.Kill(); return false; }
            return pgrep.ExitCode == 0;
        }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException)
        {
            return false;
        }
    }

    /// <summary>The pgrep pattern (an extended regular expression) for the bundle's window program.</summary>
    public static string Pattern(string bundle)
    {
        var path = bundle.TrimEnd('/') + "/Contents/Resources/window/DownloadManager.Desktop.Uno";
        var pattern = new System.Text.StringBuilder(path.Length + 8);
        foreach (var c in path)
        {
            if (@"\.[]()*+?{}|^$".Contains(c)) pattern.Append('\\');
            pattern.Append(c);
        }

        return pattern.ToString();
    }
}

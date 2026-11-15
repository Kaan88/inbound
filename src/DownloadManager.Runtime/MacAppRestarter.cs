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
    /// <summary>The shell: <c>$1</c> the Host's process id, <c>$2</c> the bundle, <c>$3</c> <c>open</c> or empty.</summary>
    public const string Script =
        "while kill -0 \"$1\" 2>/dev/null; do sleep 0.2; done; " +
        "if [ \"$3\" = open ]; then exec /usr/bin/open -n -a \"$2\"; fi; " +
        "exec \"$2/Contents/Resources/host/inbound-host\" --background";

    public static void Start(string bundle, int hostProcessId, bool? reopenWindow = null)
    {
        if (!OperatingSystem.IsMacOS()) throw new InvalidOperationException("Inbound.app is a macOS bundle.");
        var open = reopenWindow ?? WindowRunning();
        DetachedProcess.Start("/bin/sh", Arguments(bundle, hostProcessId, open));
    }

    public static string[] Arguments(string bundle, int hostProcessId, bool reopenWindow) =>
        ["-c", Script, "inbound-restart", hostProcessId.ToString(System.Globalization.CultureInfo.InvariantCulture),
            bundle, reopenWindow ? "open" : string.Empty];

    /// <summary>Whether a window of this app runs (macOS has no /proc to tell instances apart; one is enough here).</summary>
    public static bool WindowRunning()
    {
        var windows = Process.GetProcessesByName("DownloadManager.Desktop.Uno");
        try
        {
            return windows.Length > 0;
        }
        finally
        {
            foreach (var window in windows) window.Dispose();
        }
    }
}

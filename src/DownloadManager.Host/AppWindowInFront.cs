using System.Diagnostics;
using System.Runtime.InteropServices;

namespace DownloadManager.Host;

/// <summary>
/// Whether the window in front belongs to the app's frontend, where the rows a balloon would count are on show
/// already. Any instance's frontend counts, which only means one balloon fewer. This reads the real screen, so no
/// test here reaches it.
/// </summary>
internal static class AppWindowInFront
{
    private const string FrontendProcess = "DownloadManager.Desktop.WinUI";

    public static bool Now()
    {
        var window = GetForegroundWindow();
        if (window == IntPtr.Zero || GetWindowThreadProcessId(window, out var id) == 0 || id == 0) return false;
        try
        {
            using var process = Process.GetProcessById((int)id);
            return string.Equals(process.ProcessName, FrontendProcess, StringComparison.OrdinalIgnoreCase);
        }
        catch (Exception ex) when (ex is ArgumentException or InvalidOperationException)
        {
            // It closed meanwhile.
            return false;
        }
    }

    [DllImport("user32.dll")]
    private static extern IntPtr GetForegroundWindow();

    [DllImport("user32.dll")]
    private static extern uint GetWindowThreadProcessId(IntPtr window, out uint processId);
}

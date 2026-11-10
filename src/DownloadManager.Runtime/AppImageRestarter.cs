using System.Diagnostics;

namespace DownloadManager.Runtime;

/// <summary>
/// After an AppImage replaced itself: a small shell, in a session of its own so the Host's exit doesn't take it along,
/// waits for the Host to be gone and starts the new AppImage's Host. The window opens again only when one was open, so
/// an update in the night doesn't leave a window on the screen.
/// </summary>
public static class AppImageRestarter
{
    /// <summary>The shell: <c>$1</c> the Host's process id, <c>$2</c> the AppImage, <c>$3</c> empty or <c>--open</c>.</summary>
    public const string Script = "while kill -0 \"$1\" 2>/dev/null; do sleep 0.2; done; exec \"$2\" --host $3";

    public static void Start(string appImage, int hostProcessId, bool? reopenWindow = null)
    {
        var open = reopenWindow ?? WindowRunning();
        var start = new ProcessStartInfo("setsid")
        {
            UseShellExecute = false,
            RedirectStandardInput = false,
        };
        foreach (var argument in Arguments(appImage, hostProcessId, open)) start.ArgumentList.Add(argument);
        // The old mount's variables would point the new AppImage's start at the old files.
        foreach (var name in new[] { "APPDIR", "APPIMAGE", "ARGV0", "OWD", "DOWNLOADMANAGER_UI" }) start.Environment.Remove(name);
        using var process = Process.Start(start) ?? throw new InvalidOperationException("setsid did not start.");
    }

    public static IReadOnlyList<string> Arguments(string appImage, int hostProcessId, bool reopenWindow) =>
        ["-f", "/bin/sh", "-c", Script, "inbound-restart", hostProcessId.ToString(System.Globalization.CultureInfo.InvariantCulture),
            appImage, reopenWindow ? "--open" : string.Empty];

    /// <summary>
    /// Whether this Host's window is running: a process whose program is the Uno window and whose instance and data
    /// folder (<c>DOWNLOADMANAGER_INSTANCE</c>, <c>DOWNLOADMANAGER_DATA</c>) are this Host's. A window of another instance
    /// (a test copy, another data folder) doesn't count.
    /// </summary>
    public static bool WindowRunning(string proc = "/proc")
    {
        var instance = Environment.GetEnvironmentVariable(InstanceVariable);
        var data = Environment.GetEnvironmentVariable(DataVariable);
        try
        {
            foreach (var directory in Directory.EnumerateDirectories(proc))
            {
                if (!int.TryParse(Path.GetFileName(directory), out _)) continue;
                try
                {
                    var target = new FileInfo(Path.Combine(directory, "exe")).LinkTarget;
                    if (target is null || !Path.GetFileName(target).StartsWith("DownloadManager.Desktop", StringComparison.Ordinal)) continue;
                    if (SameInstance(File.ReadAllText(Path.Combine(directory, "environ")), instance, data)) return true;
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
        return false;
    }

    private const string InstanceVariable = "DOWNLOADMANAGER_INSTANCE";
    private const string DataVariable = "DOWNLOADMANAGER_DATA";

    /// <summary>A process's environment (<c>/proc/&lt;pid&gt;/environ</c>, NUL-separated) names the same instance and data folder.</summary>
    public static bool SameInstance(string environ, string? instance, string? data)
    {
        string? theirInstance = null, theirData = null;
        foreach (var entry in environ.Split('\0', StringSplitOptions.RemoveEmptyEntries))
        {
            if (entry.StartsWith(InstanceVariable + "=", StringComparison.Ordinal)) theirInstance = entry[(InstanceVariable.Length + 1)..];
            else if (entry.StartsWith(DataVariable + "=", StringComparison.Ordinal)) theirData = entry[(DataVariable.Length + 1)..];
        }
        static string? Blank(string? value) => string.IsNullOrWhiteSpace(value) ? null : value;
        return Blank(theirInstance) == Blank(instance) && Blank(theirData) == Blank(data);
    }
}

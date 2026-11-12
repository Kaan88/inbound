using System.Diagnostics;
using System.Runtime.InteropServices;

namespace DownloadManager.Contracts;

public static class ShellActions
{
    private const int AnyProcess = -1; // ASFW_ANY

    // Full paths, not bare names: the Host's working folder is the user profile, so a bare name could be looked up
    // there first (audit N6, as L3 did for its own programs).
    private static string Explorer => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Windows), "explorer.exe");
    private static string RunDll32 => Path.Combine(Environment.SystemDirectory, "rundll32.exe");

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool AllowSetForegroundWindow(int processId);

    /// <summary>
    /// UI.B24: starts <paramref name="start"/> (Explorer, a file's program) so that the window it shows may come to the
    /// front. The explorer.exe started here normally passes the request on to the Explorer already running, which opens the
    /// window or tab; that process was not started by the foreground one, so Windows keeps its window behind unless
    /// the foreground process allows it (AllowSetForegroundWindow). Only a process that may set the foreground itself
    /// can allow it, so this helps only when the caller is the foreground process, was started by it, or was allowed
    /// by it in turn; otherwise the call does nothing. Off Windows it just starts the process.
    /// </summary>
    public static Process? Start(ProcessStartInfo start)
    {
        AllowShellForeground();
        return Process.Start(start);
    }

    /// <summary>Lets any process take the foreground until the user's next input (ASFW_ANY); Windows only, a no-op
    /// elsewhere and when this process may not set the foreground itself.</summary>
    public static void AllowShellForeground()
    {
        if (!OperatingSystem.IsWindows()) return;
        try
        {
            AllowSetForegroundWindow(AnyProcess);
        }
        catch (Exception ex) when (ex is DllNotFoundException or EntryPointNotFoundException)
        {
            // Not a desktop Windows: nothing to allow.
        }
    }

    /// <summary>Opens a completed file with its registered handler.</summary>
    public static ProcessStartInfo OpenFile(string path)
        => new(path)
        {
            UseShellExecute = true,
        };

    /// <summary>Shows the Windows "Open with" chooser for an existing file.</summary>
    public static ProcessStartInfo OpenWith(string path)
        => new(RunDll32, $"shell32.dll,OpenAs_RunDLL \"{path}\"")
        {
            UseShellExecute = true,
        };

    /// <summary>Opens a folder in the file manager: Explorer, or the desktop's own (<c>xdg-open</c>) off Windows.</summary>
    public static ProcessStartInfo OpenFolder(string folder)
    {
        if (!OperatingSystem.IsWindows())
        {
            var open = new ProcessStartInfo("xdg-open") { UseShellExecute = false };
            open.ArgumentList.Add(folder);
            return open;
        }
        return new ProcessStartInfo(Explorer, $"\"{folder}\"") { UseShellExecute = true };
    }

    /// <summary>
    /// Opens Explorer with the file selected, or the folder when it is missing. Off Windows: the desktop's file manager
    /// with the file selected (org.freedesktop.FileManager1, which Dolphin and Nautilus answer), or the folder.
    /// </summary>
    public static ProcessStartInfo RevealFile(string path)
    {
        var folder = Path.GetDirectoryName(path);
        if (!OperatingSystem.IsWindows())
        {
            if (!File.Exists(path)) return OpenFolder(folder ?? path);
            var show = new ProcessStartInfo("gdbus") { UseShellExecute = false };
            foreach (var argument in new[] { "call", "--session", "--dest", "org.freedesktop.FileManager1", "--object-path",
                         "/org/freedesktop/FileManager1", "--method", "org.freedesktop.FileManager1.ShowItems",
                         "['" + new Uri(path).AbsoluteUri.Replace("'", "%27", StringComparison.Ordinal) + "']", "" })
                show.ArgumentList.Add(argument);
            return show;
        }
        if (File.Exists(path))
        {
            return new ProcessStartInfo(Explorer, $"/select,\"{path}\"")
            {
                UseShellExecute = true,
            };
        }

        return new ProcessStartInfo(Explorer, $"\"{folder ?? path}\"")
        {
            UseShellExecute = true,
        };
    }
}

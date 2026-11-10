using System.Diagnostics;

namespace DownloadManager.Contracts;

public static class ShellActions
{
    /// <summary>Opens a completed file with its registered handler.</summary>
    public static ProcessStartInfo OpenFile(string path)
        => new(path)
        {
            UseShellExecute = true,
        };

    /// <summary>Shows the Windows "Open with" chooser for an existing file.</summary>
    public static ProcessStartInfo OpenWith(string path)
        => new("rundll32.exe", $"shell32.dll,OpenAs_RunDLL \"{path}\"")
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
        return new ProcessStartInfo("explorer.exe", $"\"{folder}\"") { UseShellExecute = true };
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
            return new ProcessStartInfo("explorer.exe", $"/select,\"{path}\"")
            {
                UseShellExecute = true,
            };
        }

        return new ProcessStartInfo("explorer.exe", $"\"{folder ?? path}\"")
        {
            UseShellExecute = true,
        };
    }
}

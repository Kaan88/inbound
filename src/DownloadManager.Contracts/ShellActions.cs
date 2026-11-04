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

    /// <summary>Opens Explorer with the file selected, or the folder when it is missing.</summary>
    public static ProcessStartInfo RevealFile(string path)
    {
        var folder = Path.GetDirectoryName(path);
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

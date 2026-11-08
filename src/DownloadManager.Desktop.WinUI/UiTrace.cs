using System.Text;

namespace DownloadManager.Desktop.WinUI;

/// <summary>
/// The window's start-up trace, in the app's logs folder (with the Host's logs, which uninstall keeps), never beside
/// the exe: a window closing as uninstall stops the Host wrote one last line there and left the install folder behind.
/// </summary>
internal static class UiTrace
{
    private const long MaxBytes = 512 * 1024;
    private static readonly object Gate = new();

    internal static string FilePath
    {
        get
        {
            var isolated = Environment.GetEnvironmentVariable("DOWNLOADMANAGER_DATA");
            var root = string.IsNullOrWhiteSpace(isolated) ? DownloadManager.Contracts.AppFolders.DataRoot() : isolated;
            return Path.Combine(root, "logs", "ui-trace.log");
        }
    }

    public static void Write(string message)
    {
        try
        {
            lock (Gate)
            {
                var path = FilePath;
                Directory.CreateDirectory(Path.GetDirectoryName(path)!);
                // About a dozen lines each time the window opens: started again once it passes the cap.
                var existing = new FileInfo(path);
                if (existing.Exists && existing.Length > MaxBytes) existing.Delete();
                File.AppendAllText(path, $"{DateTimeOffset.Now:O} {message}{Environment.NewLine}", new UTF8Encoding(false));
            }
        }
        catch (Exception)
        {
            // tracing must never affect startup
        }
    }
}

using System.Text;

namespace DownloadManager.Desktop.WinUI;

internal static class UiTrace
{
    private static readonly object Gate = new();

    public static void Write(string message)
    {
        try
        {
            lock (Gate)
            {
                var path = Path.Combine(AppContext.BaseDirectory, "ui-trace.log");
                File.AppendAllText(path, $"{DateTimeOffset.Now:O} {message}{Environment.NewLine}", new UTF8Encoding(false));
            }
        }
        catch (Exception)
        {
            // tracing must never affect startup
        }
    }
}

using Avalonia;

namespace DownloadManager.AvaloniaUi;

internal static class Program
{
    [STAThread]
    public static void Main(string[] args)
    {
        IpcJsonContext.Install();
        var builder = AppBuilder.Configure<App>().UsePlatformDetect().LogToTrace();
        builder.StartWithClassicDesktopLifetime(args);
    }
}

using Uno.UI.Hosting;

namespace DownloadManager.Desktop.WinUI;

internal static class Program
{
    [STAThread]
    public static void Main(string[] args)
    {
        UiTrace.Write("Uno Program.Main begin");
        if (Environment.GetEnvironmentVariable("DOWNLOADMANAGER_UNO_TRACE") == "1")
            AppDomain.CurrentDomain.FirstChanceException += (_, e) => UiTrace.Write("first chance: " + e.Exception.GetType().Name + ": " + e.Exception.Message + " | " + e.Exception.StackTrace);
        // Uno keeps ApplicationData under %LOCALAPPDATA%\<assembly>; a test instance keeps it in its own data folder.
        var data = Environment.GetEnvironmentVariable("DOWNLOADMANAGER_DATA");
        if (!string.IsNullOrWhiteSpace(data))
            Uno.WinRTFeatureConfiguration.ApplicationData.ApplicationDataPathOverride = Path.Combine(data, "uno-appdata");
        // [Uno 7 trial] DOWNLOADMANAGER_UNO_SOFTWARE (software rendering on X11) is gone for now: Uno 7 removed
        // FeatureConfiguration.Rendering.UseOpenGLOnX11; its X11 host builder takes the backend instead.
        try
        {
            var host = UnoPlatformHostBuilder.Create()
                .App(() => new App())
                .UseX11()
                .UseLinuxFrameBuffer()
                .UseMacOS()
                .UseWin32()
                .Build();
            UiTrace.Write("Uno host built");
            // Uno names windows for X in Latin-1 only; this gives them their UTF-8 titles (em dashes, file names).
            X11Titles.Start();
            if (Environment.GetEnvironmentVariable("DOWNLOADMANAGER_UNO_TRACE") == "1")
            {
                try { UiTrace.Write("MicaController.IsSupported: " + Microsoft.UI.Composition.SystemBackdrops.MicaController.IsSupported()); }
                catch (Exception ex) { UiTrace.Write("MicaController.IsSupported threw " + ex.GetType().Name); }
            }
            host.Run();
        }
        catch (Exception ex)
        {
            UiTrace.Write("Uno host failed: " + ex);
            throw;
        }
    }
}

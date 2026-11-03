using System.Diagnostics;
using System.Drawing;
using DownloadManager.Contracts;
using DownloadManager.Runtime;

namespace DownloadManager.Host;

public sealed class HostController : IHostController
{
    private readonly DownloadManagerService _service;
    private readonly FileLogger _log;

    public HostController(DownloadManagerService service, FileLogger log)
    {
        _service = service;
        _log = log;
    }

    public SynchronizationContext? UiContext { get; set; }
    public bool UiAvailable { get; private set; }

    public void OpenUi(string? page = null)
    {
        var executable = FindUiExecutable();
        if (executable is null)
        {
            MessageBox.Show(
                "The desktop frontend was not found next to the host.\r\n\r\n" +
                "Build/publish DownloadManager.Desktop.WinUI and place it in the host directory.",
                "Download Manager",
                MessageBoxButtons.OK,
                MessageBoxIcon.Warning);
            return;
        }

        try
        {
            var arguments = string.IsNullOrWhiteSpace(page) ? string.Empty : $"--page {page}";
            Process.Start(new ProcessStartInfo
            {
                FileName = executable,
                Arguments = arguments,
                UseShellExecute = true,
            });
            _log.Info($"Opened desktop UI '{executable}'.");
        }
        catch (Exception ex)
        {
            _log.Error($"Could not open the desktop UI: {ex.Message}");
            MessageBox.Show($"Could not open the desktop UI:\r\n{ex.Message}",
                "Download Manager", MessageBoxButtons.OK, MessageBoxIcon.Error);
        }
    }

    public void RequestShutdown()
    {
        _log.Info("Exit requested from the tray or IPC.");
        if (UiContext is not null)
        {
            UiContext.Post(_ => Application.ExitThread(), null);
        }
        else
        {
            Application.ExitThread();
        }
    }

    private static string? FindUiExecutable()
    {
        var candidates = new[]
        {
            Path.Combine(AppContext.BaseDirectory, "DownloadManager.Desktop.WinUI.exe"),
            Path.Combine(AppContext.BaseDirectory, "desktop", "DownloadManager.Desktop.WinUI.exe"),
            Path.Combine(AppContext.BaseDirectory, "..", "DownloadManager.Desktop.WinUI.exe"),
        };

        return candidates.Select(Path.GetFullPath).FirstOrDefault(File.Exists);
    }
}

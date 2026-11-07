using System.Diagnostics;
using System.Drawing;
using DownloadManager.Contracts;
using DownloadManager.Runtime;

namespace DownloadManager.Host;

public sealed class HostController : IHostController
{
    private readonly DownloadManagerService _service;
    private readonly FileLogger _log;
    private int _folderPickerOpen;
    private bool _exitRequested;

    public HostController(DownloadManagerService service, FileLogger log)
    {
        _service = service;
        _log = log;
    }

    public SynchronizationContext? UiContext { get; set; }
    public bool UiAvailable { get; private set; }

    public void OpenUi(string? page = null) => LaunchUi(page, null);

    public void OpenDownload(Guid jobId) => LaunchUi(null, jobId);

    public void OpenPackage(Guid packageId) => LaunchUi(null, null, packageId);

    public Task<FolderPickResult> PickFolderAsync(string? initialPath, CancellationToken cancellationToken)
    {
        var context = UiContext;
        if (context is null)
            return Task.FromResult(new FolderPickResult { Error = "The desktop Host is not ready. Try again." });
        if (Interlocked.CompareExchange(ref _folderPickerOpen, 1, 0) != 0)
            return Task.FromResult(new FolderPickResult { Error = "A folder selection dialog is already open." });
        var completion = new TaskCompletionSource<FolderPickResult>(TaskCreationOptions.RunContinuationsAsynchronously);
        context.Post(_ =>
        {
            try
            {
                if (cancellationToken.IsCancellationRequested)
                {
                    completion.TrySetCanceled(cancellationToken);
                    return;
                }
                using var picker = new FolderBrowserDialog
                {
                    Description = "Choose the destination for this download",
                    UseDescriptionForTitle = true,
                    ShowNewFolderButton = true,
                };
                var startingPath = string.IsNullOrWhiteSpace(initialPath)
                    ? _service.GetSnapshot().Settings.DefaultDownloadDirectory
                    : initialPath;
                if (Directory.Exists(startingPath)) picker.InitialDirectory = startingPath;
                var accepted = picker.ShowDialog() == DialogResult.OK;
                completion.TrySetResult(new FolderPickResult
                {
                    Ok = true, Cancelled = !accepted, Path = accepted ? picker.SelectedPath : null,
                });
            }
            catch (Exception ex)
            {
                completion.TrySetResult(new FolderPickResult { Error = ex.Message });
            }
            finally
            {
                Interlocked.Exchange(ref _folderPickerOpen, 0);
            }
        }, null);
        return completion.Task.WaitAsync(cancellationToken);
    }

    [System.Runtime.InteropServices.DllImport("user32.dll")]
    private static extern bool AllowSetForegroundWindow(int processId);

    private void LaunchUi(string? page, Guid? jobId, Guid? packageId = null)
    {
        var executable = FindUiExecutable();
        if (executable is null)
        {
            MessageBox.Show(
                "The desktop frontend was not found next to the host.\r\n\r\n" +
                "Build/publish DownloadManager.Desktop.WinUI and place it in the host directory.",
                "Inbound Download Manager",
                MessageBoxButtons.OK,
                MessageBoxIcon.Warning);
            return;
        }

        try
        {
            var arguments = packageId is { } package ? $"--package {package:D}" :
                jobId is { } id ? $"--job {id:D}" :
                string.IsNullOrWhiteSpace(page) ? string.Empty : $"--page {page}";
            // The tray click gave the Host the foreground; pass it on so the window comes to the front (UI.B4).
            AllowSetForegroundWindow(-1);
            Process.Start(new ProcessStartInfo
            {
                FileName = executable,
                Arguments = arguments,
                UseShellExecute = false,
                // Not app\: what a window opens inherits this, and it would hold that folder (see Program.LeaveProgramFolder).
                WorkingDirectory = Environment.CurrentDirectory,
            });
            _log.Info($"Opened desktop UI '{executable}'.");
        }
        catch (Exception ex)
        {
            _log.Error($"Could not open the desktop UI: {ex.Message}");
            MessageBox.Show($"Could not open the desktop UI:\r\n{ex.Message}",
                "Inbound Download Manager", MessageBoxButtons.OK, MessageBoxIcon.Error);
        }
    }

    public void RequestShutdown()
    {
        _log.Info("Exit requested from the tray or IPC.");

        // Written before UiContext is read, and Program sets UiContext before reading this, so one of the two always sees the other.
        Volatile.Write(ref _exitRequested, true);
        UiContext?.Post(_ => Application.ExitThread(), null);
    }

    /// <summary>An exit came before there was a message loop for it to end, as a client can ask as soon as the pipe is open.</summary>
    public bool ExitWasRequested => Volatile.Read(ref _exitRequested);

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

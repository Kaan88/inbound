using System.Diagnostics;
using DownloadManager.Contracts;
using DownloadManager.Runtime;
using DownloadManager.Runtime.Ipc;

namespace DownloadManager.Host;

/// <summary>
/// The Linux desktop: a window to open (the Uno build, found next to the Host), a folder dialog, an exit, and the program
/// settings, for the user who could start those programs anyway (the socket admits only them). No updater: updates come
/// with a new AppImage. A tray when the desktop has one (<see cref="TrayAvailable"/>).
/// </summary>
public sealed class LinuxHostPlatform(DownloadManagerService service, FileLogger log) : IHostPlatform
{
    public static PlatformCapabilities LinuxCapabilities { get; } = new()
    {
        SupportsOpenUi = true,
        SupportsFolderPicker = true,
        SupportsTray = false,
        SupportsShutdown = true,
        SupportsUpdater = false,
        SupportsProgramSettings = true,
        SupportsCompletionActions = true,
        HostKind = HostKinds.Linux,
    };

    /// <summary>The window program's names, next to the Host or in its <c>desktop</c> folder.</summary>
    private static readonly string[] UiNames = ["inbound", "DownloadManager.Desktop", "DownloadManager.Desktop.Uno"];

    private readonly CancellationTokenSource _exit = new();
    private int _folderPickerOpen;

    /// <summary>Set once the tray icon has registered; the hello lists <c>tray</c> only then.</summary>
    public bool TrayAvailable { get; set; }

    public PlatformCapabilities Capabilities => TrayAvailable ? WithTray : LinuxCapabilities;

    private static readonly PlatformCapabilities WithTray = LinuxCapabilities with { SupportsTray = true };

    /// <summary>Cancelled when a client or a signal asks the Host to exit.</summary>
    public CancellationToken ExitRequested => _exit.Token;

    public void OpenUi(string? page = null) => LaunchUi(string.IsNullOrWhiteSpace(page) ? [] : ["--page", page]);

    public void OpenDownload(Guid jobId) => LaunchUi(["--job", jobId.ToString("D")]);

    public void OpenPackage(Guid packageId) => LaunchUi(["--package", packageId.ToString("D")]);

    public void RequestShutdown()
    {
        log.Info("Exit requested.");
        try { _exit.Cancel(); }
        catch (ObjectDisposedException) { /* already exiting */ }
    }

    private void LaunchUi(IReadOnlyList<string> arguments)
    {
        var executable = FindUiExecutable();
        if (executable is null)
        {
            log.Warn("The window program was not found next to the Host; nothing was opened.");
            return;
        }

        try
        {
            var start = new ProcessStartInfo(executable)
            {
                UseShellExecute = false,
                WorkingDirectory = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
            };
            foreach (var argument in arguments) start.ArgumentList.Add(argument);
            using var _ = Process.Start(start);
            log.Info($"Opened the window '{executable}'.");
        }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException)
        {
            log.Error($"Could not open the window: {ex.Message}");
        }
    }

    private static string? FindUiExecutable()
    {
        var overridden = Environment.GetEnvironmentVariable("DOWNLOADMANAGER_UI");
        if (!string.IsNullOrWhiteSpace(overridden)) return File.Exists(overridden) ? Path.GetFullPath(overridden) : null;
        var folders = new[]
        {
            AppContext.BaseDirectory,
            Path.Combine(AppContext.BaseDirectory, "desktop"),
            // A build from the source tree (Host and window in their own bin folders).
            Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", "DownloadManager.Desktop.Uno", "bin", "Debug", "net10.0-desktop"),
            Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", "DownloadManager.Desktop.Uno", "bin", "Release", "net10.0-desktop"),
        };
        return folders.SelectMany(folder => UiNames.Select(name => Path.Combine(folder, name)))
            .Select(Path.GetFullPath)
            .FirstOrDefault(File.Exists);
    }

    /// <summary>
    /// KDE's own dialog, else GTK's. Both are separate programs, so the Host needs no toolkit of its own.
    /// [TODO D7] The desktop portal's FileChooser over D-Bus, which works in a Flatpak too.
    /// </summary>
    public async Task<FolderPickResult> PickFolderAsync(string? initialPath, CancellationToken cancellationToken)
    {
        if (Interlocked.CompareExchange(ref _folderPickerOpen, 1, 0) != 0)
            return new FolderPickResult { Error = "A folder selection dialog is already open." };
        try
        {
            var start = string.IsNullOrWhiteSpace(initialPath) ? service.GetSettings().DefaultDownloadDirectory : initialPath;
            if (!Directory.Exists(start)) start = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
            const string title = "Choose the destination for this download";
            foreach (var (program, arguments) in new (string, string[])[]
                     {
                         ("kdialog", ["--title", title, "--getexistingdirectory", start]),
                         ("zenity", ["--file-selection", "--directory", "--title", title, "--filename", start.TrimEnd('/') + "/"]),
                     })
            {
                var result = await RunDialogAsync(program, arguments, cancellationToken).ConfigureAwait(false);
                if (result is not null) return result;
            }

            return new FolderPickResult { Error = "No folder dialog is available here. Install kdialog or zenity, or type the folder." };
        }
        finally
        {
            Interlocked.Exchange(ref _folderPickerOpen, 0);
        }
    }

    /// <summary>Null when the program isn't there.</summary>
    private static async Task<FolderPickResult?> RunDialogAsync(string program, string[] arguments, CancellationToken cancellationToken)
    {
        var start = new ProcessStartInfo(program) { UseShellExecute = false, RedirectStandardOutput = true, RedirectStandardError = true };
        foreach (var argument in arguments) start.ArgumentList.Add(argument);
        Process? process;
        try
        {
            process = Process.Start(start);
        }
        catch (System.ComponentModel.Win32Exception)
        {
            return null;
        }

        if (process is null) return null;
        using (process)
        {
            try
            {
                var output = await process.StandardOutput.ReadToEndAsync(cancellationToken).ConfigureAwait(false);
                await process.WaitForExitAsync(cancellationToken).ConfigureAwait(false);
                var path = output.TrimEnd('\n', '\r');
                // Both answer 1 for Cancel; anything else with no folder is a failure of the dialog itself.
                if (process.ExitCode == 0 && Path.IsPathRooted(path)) return new FolderPickResult { Ok = true, Path = path };
                if (process.ExitCode == 1) return new FolderPickResult { Ok = true, Cancelled = true };
                return new FolderPickResult { Error = $"The folder dialog ({program}) failed." };
            }
            catch (OperationCanceledException)
            {
                try { process.Kill(); }
                catch (InvalidOperationException) { /* already gone */ }
                throw;
            }
        }
    }
}

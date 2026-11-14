using System.Diagnostics;
using DownloadManager.Contracts;
using DownloadManager.Runtime;
using DownloadManager.Runtime.Ipc;

namespace DownloadManager.Host;

/// <summary>
/// The Linux desktop: a window to open (the Uno build, found next to the Host), a folder dialog, an exit, and the program
/// settings, for the user who could start those programs anyway (the socket admits only them). The updater only when
/// running from an AppImage, which replaces itself; a build run any other way is updated by whoever installed it. A tray when the desktop has one (<see cref="TrayAvailable"/>).
/// </summary>
public sealed class LinuxHostPlatform(DownloadManagerService service, FileLogger log) : IHostPlatform
{
    public static PlatformCapabilities LinuxCapabilities { get; } = new()
    {
        SupportsOpenUi = true,
        SupportsFolderPicker = true,
        SupportsTray = false,
        SupportsShutdown = true,
        SupportsUpdater = DownloadManager.Core.InstalledBuild.AppImage() is not null,
        SupportsProgramSettings = true,
        SupportsCompletionActions = true,
        // EXT.R11 review: only with a desktop to show a file manager on. A Host without a display has nowhere to open it.
        SupportsReveal = HasDesktopSession(),
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

    /// <summary>T10: the torrent dialog for finished .torrent downloads, through the window's <c>--open-torrent</c>. Not yet tried on Linux.</summary>
    public void OpenTorrentFiles(IReadOnlyList<string> paths)
    {
        foreach (var launch in TorrentFileOffers.Launches(paths)) LaunchUi(TorrentFileOffers.Arguments(launch));
    }

    /// <summary>EXT.R12: the main window, at this download.</summary>
    public void ShowJob(Guid jobId) => LaunchUi([UiArguments.ShowJob, jobId.ToString("D")]);

    /// <summary>Whether this Host runs in a desktop session (X11 or Wayland), where a file manager can be shown.</summary>
    internal static bool HasDesktopSession() =>
        !string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("WAYLAND_DISPLAY")) ||
        !string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("DISPLAY"));

    /// <summary>
    /// EXT.R11: the file manager with the file selected (FileManager1), or the folder through xdg-open. A program that
    /// can't be started throws, and the client is told so. When no file manager answers FileManager1 (gdbus fails or
    /// is missing), the folder is opened with xdg-open instead. Not yet tried on a Linux desktop.
    /// </summary>
    public void RevealFile(string path)
    {
        var start = ShellActions.RevealFile(path);
        if (start.FileName != "gdbus")
        {
            using var _ = Process.Start(start);
            return;
        }

        var folder = Path.GetDirectoryName(path) ?? path;
        Process? show = null;
        try { show = Process.Start(start); }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException)
        {
            log.Warn($"gdbus could not be started ({ex.Message}); opening the folder instead.");
        }

        if (show is null)
        {
            using var open = Process.Start(ShellActions.OpenFolder(folder));
            return;
        }

        // gdbus answers at once; a failure means no file manager took the call, so the folder is opened instead.
        _ = Task.Run(async () =>
        {
            using (show)
            {
                try
                {
                    using var limit = new CancellationTokenSource(TimeSpan.FromSeconds(10));
                    await show.WaitForExitAsync(limit.Token).ConfigureAwait(false);
                    if (show.ExitCode == 0) return;
                    log.Warn($"No file manager answered FileManager1 (gdbus exit {show.ExitCode}); opening the folder instead.");
                    using var open = Process.Start(ShellActions.OpenFolder(folder));
                }
                catch (Exception ex)
                {
                    log.Warn($"Could not show the file in its folder: {ex.Message}");
                }
            }
        });
    }

    /// <summary>
    /// EXT.R13: the file (or a folder) with the desktop's program for it, through xdg-open. EXT.B8: an AppImage is given
    /// its owner's execute bit and started directly, since xdg-open does not run one (<see cref="LinuxOpenRule"/>).
    /// Not yet tried on a Linux desktop.
    /// </summary>
    public void OpenFile(string path)
    {
        if (Directory.Exists(path))
        {
            using var folder = Process.Start(ShellActions.OpenFolder(path));
            return;
        }

        if (LinuxOpenRule.IsAppImage(path))
        {
            try
            {
                LinuxOpenRule.MakeOwnerExecutable(path);
                var start = new ProcessStartInfo(path)
                {
                    UseShellExecute = false,
                    WorkingDirectory = Path.GetDirectoryName(path) ?? Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
                };
                using var program = Process.Start(start);
                return;
            }
            catch (Exception error) when (error is System.ComponentModel.Win32Exception or IOException or UnauthorizedAccessException)
            {
                throw new InvalidOperationException($"Couldn't start {Path.GetFileName(path)}: {error.Message}", error);
            }
        }

        var open = new ProcessStartInfo("xdg-open") { UseShellExecute = false };
        open.ArgumentList.Add(path);
        using var _ = Process.Start(open);
    }

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

    internal static string? FindUiExecutable()
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

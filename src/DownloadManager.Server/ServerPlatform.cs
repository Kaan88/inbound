using DownloadManager.Contracts;
using DownloadManager.Runtime.Ipc;

namespace DownloadManager.Server;

/// <summary>
/// The headless server's machine: no window to open, no dialog, no tray, no exit on request (its lifetime belongs to the
/// container or service manager), no updater (the image is updated instead), and no program settings (a network client
/// can't be assumed to be the person who could start those programs). Folders under the allowed roots can be browsed.
/// </summary>
public sealed class ServerPlatform : IHostPlatform
{
    public static PlatformCapabilities ServerCapabilities { get; } = new()
    {
        SupportsOpenUi = false,
        SupportsFolderPicker = false,
        SupportsTray = false,
        SupportsShutdown = false,
        SupportsUpdater = false,
        SupportsProgramSettings = false,
        SupportsFolderBrowse = true,
    };

    public PlatformCapabilities Capabilities => ServerCapabilities;

    // The dispatcher checks the capabilities first, so these are never reached; they say so if they ever are.
    public void OpenUi(string? page = null) => throw new NotSupportedException("The server has no window to open.");

    public void RequestShutdown() => throw new NotSupportedException("The server's lifetime belongs to its service manager.");

    public Task<FolderPickResult> PickFolderAsync(string? initialPath, CancellationToken cancellationToken) =>
        Task.FromResult(new FolderPickResult { Error = "Folder selection is unavailable here. Use folders.browse." });
}

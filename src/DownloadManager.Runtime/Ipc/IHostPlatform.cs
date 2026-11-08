using DownloadManager.Contracts;

namespace DownloadManager.Runtime.Ipc;

/// <summary>
/// What the machine the Host runs on can do for a client. The desktop Host can do all of it; a headless server
/// has no window to open, no dialog to show and no installer to run, and its commands answer that instead.
/// </summary>
public sealed record PlatformCapabilities
{
    /// <summary><c>host.openUi</c> opens a window, and an update's download shows its progress window.</summary>
    public bool SupportsOpenUi { get; init; }

    /// <summary><c>host.pickFolder</c> shows a folder dialog to the person at the machine.</summary>
    public bool SupportsFolderPicker { get; init; }

    /// <summary>A tray icon is there. The hello lists <c>tray</c> only then.</summary>
    public bool SupportsTray { get; init; }

    /// <summary><c>host.shutdown</c> ends the process. A server's lifetime belongs to its service manager.</summary>
    public bool SupportsShutdown { get; init; }

    /// <summary><c>update.*</c> check, download and install the signed Windows installer.</summary>
    public bool SupportsUpdater { get; init; }

    /// <summary>
    /// Folders on the Host's own disk can be listed for a client that can't show a dialog there. Nothing reads this
    /// yet: the server's folder listing (D1b) is what it is for.
    /// </summary>
    public bool SupportsFolderBrowse { get; init; }

    /// <summary>The desktop Host: a window, a dialog, a tray, an exit and the updater, and no folder listing.</summary>
    public static PlatformCapabilities Desktop { get; } = new()
    {
        SupportsOpenUi = true,
        SupportsFolderPicker = true,
        SupportsTray = true,
        SupportsShutdown = true,
        SupportsUpdater = true,
    };
}

/// <summary>The machine-side actions behind a few commands. Replaces the Host's own <c>IHostController</c>.</summary>
public interface IHostPlatform
{
    PlatformCapabilities Capabilities { get; }

    /// <summary>Opens the frontend, at <paramref name="page"/> when one is named. Only called when <see cref="PlatformCapabilities.SupportsOpenUi"/>.</summary>
    void OpenUi(string? page = null);

    /// <summary>Ends the process soon after. Only called when <see cref="PlatformCapabilities.SupportsShutdown"/>.</summary>
    void RequestShutdown();

    /// <summary>Only called when <see cref="PlatformCapabilities.SupportsFolderPicker"/>.</summary>
    Task<FolderPickResult> PickFolderAsync(string? initialPath, CancellationToken cancellationToken);
}

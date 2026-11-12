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
    /// A client may change what the Host runs or trusts: <c>settings.update</c>'s <c>postDownloadCommand</c>,
    /// <c>ffmpegPath</c>, <c>updateManifestUrl</c>, <c>updatePublicKey</c> and <c>allowSystemCompletionActions</c>, and a
    /// download's completion program in <c>job.update</c>. The desktop pipe admits only the user who could start those
    /// programs anyway. A network transport's client can't be assumed to be that person, so a server leaves this off
    /// and those changes are refused; the rest of the settings still apply.
    /// </summary>
    public bool SupportsProgramSettings { get; init; }

    /// <summary>
    /// Folders on the Host's own disk can be listed and created for a client that can't show a dialog there:
    /// <c>folders.browse</c> and <c>folders.create</c>, under the server's allowed roots only (<see cref="FolderBrowser"/>).
    /// The hello then offers <c>folders</c>.
    /// </summary>
    public bool SupportsFolderBrowse { get; init; }

    /// <summary>
    /// What a finished download or schedule may do on this machine may be set: open the file, a system action (shut
    /// down, sleep, exit), and a schedule's when-done actions. The server runs none of these, and a browser on another
    /// device may not set them on the desktop (they would run as this machine's user).
    /// </summary>
    public bool SupportsCompletionActions { get; init; }

    /// <summary>
    /// EXT.R11: <c>job.reveal</c> shows a download's file in this machine's file manager. Only where the person at the
    /// machine is the client's user: the desktop Host, never a server or a web session (the window would open on a
    /// screen nobody asking can see).
    /// </summary>
    public bool SupportsReveal { get; init; }

    /// <summary>The hello's <c>hostKind</c> (<see cref="HostKinds"/>). Null leaves it out.</summary>
    public string? HostKind { get; init; }

    /// <summary>
    /// The desktop Host: a window, a dialog, a tray, an exit, the updater, the program settings and the completion
    /// actions, and no folder listing.
    /// </summary>
    public static PlatformCapabilities Desktop { get; } = new()
    {
        SupportsOpenUi = true,
        SupportsFolderPicker = true,
        SupportsTray = true,
        SupportsShutdown = true,
        SupportsUpdater = true,
        SupportsProgramSettings = true,
        SupportsCompletionActions = true,
        SupportsReveal = true,
        HostKind = HostKinds.Windows,
    };
}

/// <summary>The machine-side actions behind a few commands. Replaces the Host's own <c>IHostController</c>.</summary>
public interface IHostPlatform
{
    PlatformCapabilities Capabilities { get; }

    /// <summary>Opens the frontend, at <paramref name="page"/> when one is named. Only called when <see cref="PlatformCapabilities.SupportsOpenUi"/>.</summary>
    void OpenUi(string? page = null);

    /// <summary>
    /// EXT.R12: opens the main window at <paramref name="jobId"/>, selected and briefly highlighted. The dispatcher has
    /// checked the download is on the list. Only called when <see cref="PlatformCapabilities.SupportsOpenUi"/>; a platform
    /// that can't name a row opens the main window.
    /// </summary>
    void ShowJob(Guid jobId) => OpenUi();

    /// <summary>Ends the process soon after. Only called when <see cref="PlatformCapabilities.SupportsShutdown"/>.</summary>
    void RequestShutdown();

    /// <summary>Only called when <see cref="PlatformCapabilities.SupportsFolderPicker"/>.</summary>
    Task<FolderPickResult> PickFolderAsync(string? initialPath, CancellationToken cancellationToken);

    /// <summary>
    /// EXT.R11: shows <paramref name="path"/> selected in the file manager, or its folder when the file is gone. The
    /// dispatcher has checked that one of them exists. Only called when <see cref="PlatformCapabilities.SupportsReveal"/>.
    /// </summary>
    void RevealFile(string path) => throw new NotSupportedException("Showing files is not available here.");

    /// <summary>
    /// T10: opens the window's torrent dialog with these finished .torrent files (full paths), as the Explorer association
    /// does: one launch of the window with <c>--open-torrent</c> before each path, which a running window takes over
    /// (<see cref="TorrentFileOffers"/> batches them). Only called when <see cref="PlatformCapabilities.SupportsOpenUi"/>.
    /// </summary>
    void OpenTorrentFiles(IReadOnlyList<string> paths) => throw new NotSupportedException("Opening the torrent dialog is not available here.");
}

/// <summary>The window pages <see cref="IHostPlatform.OpenUi"/> knows. Any other text opens the main window.</summary>
public static class UiPages
{
    /// <summary>Options.</summary>
    public const string Settings = "settings";

    /// <summary>An update's progress window; the Host opens it itself, never for a client.</summary>
    public const string UpdateProgress = "update-progress";

    /// <summary>The page as the window's command line names it, or null for one it doesn't know.</summary>
    public static string? Known(string? page) => page?.Trim().ToLowerInvariant() switch
    {
        Settings => Settings,
        UpdateProgress => UpdateProgress,
        _ => null,
    };
}

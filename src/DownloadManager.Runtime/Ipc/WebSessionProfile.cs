using DownloadManager.Contracts;
using DownloadManager.Runtime.Archives;
using DownloadManager.Runtime.Remote;

namespace DownloadManager.Runtime.Ipc;

/// <summary>
/// The rights of a web session on the desktop Host (D8): a browser on another device, through the web bridge. It gets
/// what a client of the server gets, held to the app's own folders, and none of what would act on this PC beyond its
/// downloads: no program settings, completion programs or actions, folder dialog, windows, exit or updater. The
/// archive, folder and provider commands it uses are built for it here, held to those folders, as the server builds
/// its own; the service and the provider coordinator are shared, so its folders go with each call
/// (<see cref="CallerScope"/>). The settings it may change are <see cref="WebSettingsRules"/>.
/// </summary>
public sealed class WebSessionProfile : IDisposable
{
    /// <summary>What the folders are called in a refusal.</summary>
    public const string FoldersCalled = "the app's folders";

    private readonly RemoteCommands? _remote;

    /// <param name="host">The desktop Host's own capabilities; a web session keeps only what is safe from another device.</param>
    /// <param name="archives">Null when extraction didn't start: the session then gets no archive commands, as the window doesn't.</param>
    /// <param name="providers">With <paramref name="remoteJobs"/>, the provider commands; null when they didn't start.</param>
    public WebSessionProfile(DownloadManagerService service, PlatformCapabilities host, ArchiveCoordinator? archives = null,
        RemoteProviderRegistry? providers = null, RemoteJobCoordinator? remoteJobs = null, Action<string>? log = null,
        Func<string, string?>? linkTarget = null)
    {
        Roots = PathRoots.Live(() => AppFolders(service.GetSettings(), archives?.GetSettings().Settings.Folder), FoldersCalled, linkTarget);
        if (providers is not null && remoteJobs is not null)
            _remote = new RemoteCommands(providers, remoteJobs, log: log, roots: Roots, jobRoots: Roots);
        Rights = new SessionRights
        {
            Role = ClientRoles.Web,
            Capabilities = WebCapabilities(host),
            Scope = new CallerScope(Roots),
            Archives = archives is null ? null : new ArchiveIpcDispatcher(archives, Roots),
            Folders = new FolderBrowser(Roots),
            Remote = _remote,
        };
    }

    /// <summary>The app's folders, worked out from the current settings each time they are needed.</summary>
    public PathRoots Roots { get; }

    public SessionRights Rights { get; }

    /// <summary>
    /// The host's capabilities without a window, a dialog, an exit, the updater, the program settings and the completion
    /// actions, and with folder browsing (there is no dialog on the other device's side). The tray stays: it says what
    /// the PC has, not what the session may do.
    /// </summary>
    public static PlatformCapabilities WebCapabilities(PlatformCapabilities host) => host with
    {
        SupportsOpenUi = false,
        SupportsFolderPicker = false,
        SupportsShutdown = false,
        SupportsUpdater = false,
        SupportsProgramSettings = false,
        SupportsCompletionActions = false,
        SupportsFolderBrowse = true,
    };

    /// <summary>
    /// Where a web session may read and write: the download folder, every category folder (built-in and custom), the
    /// favorites and the extraction folder. Because it can't change those settings, it can't widen these.
    /// </summary>
    public static IEnumerable<string> AppFolders(SettingsDto settings, string? extractionFolder)
    {
        yield return settings.DefaultDownloadDirectory;
        foreach (var folder in (settings.CategoryFolders ?? new()).Values) yield return folder;
        foreach (var category in settings.CustomCategories ?? []) if (category?.DestinationDirectory is { } folder) yield return folder;
        foreach (var favorite in settings.FavoriteFolders ?? []) if (favorite?.Path is { } path) yield return path;
        if (extractionFolder is not null) yield return extractionFolder;
    }

    public void Dispose() => _remote?.Dispose();
}

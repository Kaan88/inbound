using DownloadManager.Contracts;
using DownloadManager.Core;
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

    /// <summary>Why a folder can't be one of the roots: it is, holds or is inside one Windows or installed programs use.</summary>
    public const string SystemFolderRefused = "It is, holds or is inside a folder that Windows or installed programs use.";

    private readonly RemoteCommands? _remote;
    private readonly DownloadManagerService _service;
    private readonly ArchiveCoordinator? _archives;
    private readonly IReadOnlyList<string> _systemFolders;
    // The folders from the settings, kept until the settings change: every path check asks for them.
    private string[]? _folders;
    private int _settingsVersion;

    /// <param name="host">The desktop Host's own capabilities; a web session keeps only what is safe from another device.</param>
    /// <param name="archives">Null when extraction didn't start: the session then gets no archive commands, as the window doesn't.</param>
    /// <param name="providers">With <paramref name="remoteJobs"/>, the provider commands; null when they didn't start.</param>
    /// <param name="systemFolders">Folders no root may be, hold or be inside; <see cref="SystemFolders"/> by default.</param>
    public WebSessionProfile(DownloadManagerService service, PlatformCapabilities host, ArchiveCoordinator? archives = null,
        RemoteProviderRegistry? providers = null, RemoteJobCoordinator? remoteJobs = null, Action<string>? log = null,
        Func<string, string?>? linkTarget = null, IReadOnlyList<string>? systemFolders = null)
    {
        _service = service;
        _archives = archives;
        _systemFolders = systemFolders ?? SystemFolders();
        _service.SettingsChanged += OnSettingsChanged;
        Roots = PathRoots.Live(CurrentFolders, FoldersCalled, linkTarget, DescribeSystemFolder);
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
            Torrents = service.TorrentsAvailable,
        };
    }

    /// <summary>The app's folders, from the settings as they are now (read again once they change) and the extraction folder.</summary>
    public PathRoots Roots { get; }

    public SessionRights Rights { get; }

    /// <summary>
    /// The host's capabilities without a window, a dialog, a file shown in Explorer, an exit, the updater, the program settings and the completion
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
        SupportsReveal = false,
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

    private IEnumerable<string> CurrentFolders()
    {
        var folders = Volatile.Read(ref _folders);
        if (folders is null)
        {
            var version = Volatile.Read(ref _settingsVersion);
            folders = AppFolders(_service.GetSettings(), null).ToArray();
            // Kept only when no change came meanwhile; otherwise the next check reads them again.
            if (Volatile.Read(ref _settingsVersion) == version) Interlocked.CompareExchange(ref _folders, folders, null);
        }

        return _archives?.ExtractionFolder is { } extraction ? folders.Append(extraction) : folders;
    }

    private void OnSettingsChanged(object? sender, AppSettings settings)
    {
        Interlocked.Increment(ref _settingsVersion);
        Volatile.Write(ref _folders, null);
    }

    /// <summary>
    /// On Windows: the user's AppData (Roaming and Local, where startup programs and per-user programs live), Windows,
    /// Program Files and ProgramData. A favorite such as the drive or the user's own folder holds them, so it is no root.
    /// </summary>
    public static IReadOnlyList<string> SystemFolders()
    {
        if (!OperatingSystem.IsWindows()) return [];
        return new[]
            {
                Environment.SpecialFolder.ApplicationData, Environment.SpecialFolder.LocalApplicationData, Environment.SpecialFolder.Windows,
                Environment.SpecialFolder.ProgramFiles, Environment.SpecialFolder.ProgramFilesX86, Environment.SpecialFolder.CommonApplicationData,
            }
            .Select(Environment.GetFolderPath)
            .Where(path => !string.IsNullOrWhiteSpace(path))
            .Select(path => Path.TrimEndingDirectorySeparator(Path.GetFullPath(path)))
            .Distinct(PathNames.Comparer)
            .ToList();
    }

    private string? DescribeSystemFolder(string root)
        => _systemFolders.Any(system => PathRoots.IsInside(root, system) || PathRoots.IsInside(system, Path.TrimEndingDirectorySeparator(root)))
            ? SystemFolderRefused
            : null;

    public void Dispose()
    {
        _service.SettingsChanged -= OnSettingsChanged;
        _remote?.Dispose();
    }
}

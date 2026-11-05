namespace DownloadManager.Contracts;

/// <summary>
/// Per-user folder layout. Program files live under %LOCALAPPDATA%\Programs\<see cref="Name"/>
/// (replaced on upgrade); user data lives under %LOCALAPPDATA%\<see cref="Name"/> (kept on uninstall).
/// </summary>
public static class AppFolders
{
    public const string Name = "Inbound Download Manager";

    /// <summary>Default user-data folder: settings, jobs, checkpoints, logs, UI state.</summary>
    public static string DataRoot()
        => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), Name);

    /// <summary>Default per-user install folder used by Setup and the install scripts.</summary>
    public static string InstallRoot()
        => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Programs", Name);
}

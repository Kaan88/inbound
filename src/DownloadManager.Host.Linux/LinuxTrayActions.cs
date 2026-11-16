namespace DownloadManager.Host;

/// <summary>What the tray offers; the same as the Windows tray's actions, less the updater.</summary>
internal sealed record LinuxTrayActions(
    Action Open,
    Action OpenSettings,
    Action PauseAll,
    Action ResumeAll,
    Action Exit,
    Func<int> ActiveCount);

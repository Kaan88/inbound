using System.Runtime.CompilerServices;

namespace DownloadManager.Torrents.Tests;

/// <summary>
/// Every torrent worker this test run starts (and every Host it starts, which inherits the variable) listens on
/// 127.0.0.1 only, so Windows never asks to let a worker from a build or worktree folder through the firewall.
/// </summary>
internal static class TorrentLoopbackOnly
{
    [ModuleInitializer]
    internal static void KeepTorrentsOnLoopback() =>
        Environment.SetEnvironmentVariable(DownloadManager.Torrents.TorrentSetup.LoopbackOnlyVariable, "1");
}

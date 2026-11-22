#if INBOUND_TEST_BUILD
using DownloadManager.Contracts;

namespace Inbound.App.Screens;

/// <summary>
/// Test build only: <c>--es x_fixture full|empty|error</c> shows the Transfers screen with canned transfers (mockup x1 states),
/// because a build without rclone offers no Remote Files. It never talks to a Host and is not part of a release build.
/// </summary>
internal sealed partial class RemoteFilesScreen
{
    /// <summary>"full", "empty" or "error"; null when no fixture is wanted.</summary>
    internal static string? FixtureMode;

    private static SiteTransferDto Fx(string id, string state, string direction, string site, string remote, string local, int files, int done,
        long bytes, long bytesDone, long speed, int minutesAgo, string? message = null, int failed = 0, int skipped = 0, string? destSite = null, string? destFolder = null)
        => new()
        {
            Id = id, State = state, Direction = direction, SiteId = site, SiteName = site == "home" ? "Home NAS" : "Google Drive",
            RemoteFolder = remote, LocalFolder = local, Files = files, FilesDone = done, FilesFailed = failed, FilesSkipped = skipped,
            Bytes = bytes, BytesDone = bytesDone, Speed = speed, Message = message,
            CreatedAt = DateTimeOffset.Now.AddMinutes(-minutesAgo), FinishedAt = state is "done" or "failed" or "canceled" ? DateTimeOffset.Now.AddMinutes(-minutesAgo + 2) : null,
            DestinationSiteId = destSite, DestinationSiteName = destSite is null ? null : "Home NAS", DestinationFolder = destFolder,
        };

    private void StartFixture()
    {
        _listLoaded = true;
        _sites = [new SiteDto { Id = "home", Name = "Home NAS", Kind = SiteKindCatalog.Sftp }, new SiteDto { Id = "drive", Name = "Google Drive", Kind = SiteKindCatalog.Sftp }];
        _transfersLoaded = true;
        _transfers = FixtureMode == "empty" ? [] :
        [
            Fx("t1", "running", "download", "home", "/music", "/storage/emulated/0/Download/Inbound/Music", 1, 0, 455_000_000, 212_000_000, 9_800_000, 1),
            Fx("t2", "running", "copy", "drive", "/Wedding", "", 640, 212, 4_100_000_000, 1_300_000_000, 12_000_000, 4, destSite: "home", destFolder: "/photos"),
            Fx("t3", "paused", "upload", "home", "/backup", "/storage/emulated/0/Download/Inbound", 1, 0, 80_000_000, 20_000_000, 0, 20),
            Fx("t4", "done", "download", "home", "/music", "/storage/emulated/0/Download/Inbound/Music", 1, 1, 8_400_000, 8_400_000, 0, 60),
            Fx("t5", "done", "copy", "drive", "/Wedding", "", 14, 12, 120_000_000, 120_000_000, 0, 90, skipped: 2, destSite: "home", destFolder: "/photos"),
            Fx("t6", "failed", "upload", "home", "/docs", "/storage/emulated/0/Download/Inbound", 24, 21, 300_000_000, 270_000_000, 0, 120, "The connection to Home NAS was lost.", failed: 3),
            Fx("t7", "canceled", "download", "home", "/video", "/storage/emulated/0/Download/Inbound", 1, 0, 700_000_000, 10_000_000, 0, 200),
        ];
        _transferPaths["t1"] = ["session-0503.flac"];
        _transferPaths["t2"] = ["Wedding/a.jpg", "Wedding/b.jpg"];
        _transferPaths["t3"] = ["backup.zip"];
        _transferPaths["t4"] = ["track.mp3"];
        _transferPaths["t5"] = ["Wedding/a.jpg", "Wedding/b.jpg"];
        _transferPaths["t6"] = ["docs/a.pdf", "docs/b.pdf"];
        _transferPaths["t7"] = ["movie.mkv"];
        if (FixtureMode == "error")
        {
            _transferErrorTitle = "Could not refresh transfers";
            _transferErrorText = "Their current state is unknown. Shown as of " + DateTimeOffset.Now.ToString("HH:mm") + ".";
            _transferErrorRetry = true;
        }
        _showTransfers = true;
        Render();
    }

    private static List<SiteTransferItemDto> FixtureItems(string id) => id switch
    {
        "t5" or "t2" =>
        [
            new() { Path = "Wedding/IMG_2291.jpg", State = "done", Size = 4_200_000 },
            new() { Path = "Wedding/IMG_2292.jpg", State = "skipped", Size = 4_100_000 },
            new() { Path = "Wedding/IMG_2293.jpg", State = "failed", Size = 4_000_000, Error = "Timed out" },
            new() { Path = "Wedding/IMG_2294.jpg", State = "done", Size = 3_900_000 },
        ],
        _ => [new() { Path = "session-0503.flac", State = "done", Size = 8_400_000 }],
    };
}
#endif

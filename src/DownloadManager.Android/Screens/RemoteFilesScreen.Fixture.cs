#if INBOUND_TEST_BUILD
using DownloadManager.Contracts;
using DownloadManager.Mobile;

namespace Inbound.App.Screens;

/// <summary>
/// Test build only: <c>--es x_fixture full|empty|error|attention</c> shows the Transfers screen with canned transfers (mockup x1 states; attention adds failed transfers that need a sign-in or trust),
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

    private static SiteDto FixtureKeySite() => new()
    {
        Id = "home", Name = "Home NAS", Kind = SiteKindCatalog.Sftp, Host = "nas.local", Port = 22, User = "media", AuthMethod = "key",
        HasPrivateKey = true, HasKeyPassphrase = true, RemoteFolder = "/media",
        HostKeys = [new SiteHostKeyDto { Type = "ssh-ed25519", Fingerprint = "SHA256:fixture", Entry = "ssh-ed25519 AAAA" }],
    };

    /// <summary>
    /// <c>--es x_fixture pane_signin|pane_key|pane_error|pane_empty</c>: the folder view with the pane message of mockups x32, x32b, x32c, x32d
    /// (no Host call is made).
    /// </summary>
    private void StartPaneFixture()
    {
        _listLoaded = true;
        _secureHostConnection = true;
        SiteDto site = FixtureMode switch
        {
            "pane_signin" => new SiteDto { Id = "od", Name = "OneDrive", Kind = SiteKindCatalog.OneDrive, SignedIn = false },
            "pane_key" => FixtureKeySite(),
            _ => new SiteDto { Id = "work", Name = "Work files", Kind = SiteKindCatalog.WebDav, Url = "https://files.example.com/dav" },
        };
        _sites = [site];
        _inFolder = true;
        _active = 0;
        ResetPane(_panes[0], site, FixtureMode is "pane_error" or "pane_empty" ? "shared" : null);
        ResetPane(_panes[1], null, "Download");
        var pane = _panes[0];
        switch (FixtureMode)
        {
            case "pane_signin":
                _expiredCloudSites.Add(site.Id);
                pane.NeedsSignIn = true;
                pane.ErrorTitle = "Sign-in has run out";
                pane.Error = "Sign in again to browse this location.";
                break;
            case "pane_key":
                pane.KeyProblem = true;
                pane.ErrorTitle = "Review the server key";
                pane.Error = "The server's key is not the one you trusted.";
                break;
            case "pane_error":
                pane.ErrorTitle = "Could not load this folder";
                pane.Error = "Check the connection and try again.";
                break;
        }
        Render();
    }

    /// <summary>
    /// <c>--es x_fixture form_key|form_key_empty|form_key_wrongpass|form_key_public|form_key_edit</c>: the SFTP form of mockups y1, y1b, y2, y2b, y3
    /// with canned values (the key text is a placeholder that is never sent).
    /// </summary>
    private void StartKeyFormFixture()
    {
        _listLoaded = true;
        _secureHostConnection = true;
        _sites = [FixtureKeySite()];
        _adding = true;
        _addStep = 1;
        _addKind = SiteKindCatalog.Sftp;
        _editSite = FixtureMode == "form_key_edit" ? FixtureKeySite() : null;
        _fv.Clear();
        ClearKeyState();
        _fv["name"] = "Home NAS"; _fv["host"] = "nas.local"; _fv["port"] = "22"; _fv["user"] = "media"; _fv["auth"] = "key";
        switch (FixtureMode)
        {
            case "form_key":
                _keyText = "fixture"; _keyName = "nas-key.ppk"; _keyKind = "PuTTY key · Ed25519"; _fv["keypass"] = "0123456789";
                break;
            case "form_key_wrongpass":
                _keyText = "fixture"; _keyName = "nas-key.ppk"; _keyKind = "PuTTY key · Ed25519"; _fv["keypass"] = "01234567";
                _passphraseError = SftpKeyFile.WrongPassphrase;
                break;
            case "form_key_public":
                _keyName = "nas-key.pub"; _keyKind = "Public key"; _keyError = SftpKeyFile.PublicKeyMessage;
                break;
            case "form_key_edit":
                _fv["folder"] = "/media";
                break;
        }
        Render();
    }

    private void StartFixture()
    {
        if (FixtureMode is not null && FixtureMode.StartsWith("pane_", StringComparison.Ordinal)) { StartPaneFixture(); return; }
        if (FixtureMode is not null && FixtureMode.StartsWith("form_key", StringComparison.Ordinal)) { StartKeyFormFixture(); return; }
        _listLoaded = true;
        _sites = [new SiteDto { Id = "home", Name = "Home NAS", Kind = SiteKindCatalog.Sftp }, new SiteDto { Id = "drive", Name = "Google Drive", Kind = FixtureMode == "attention" ? SiteKindCatalog.Drive : SiteKindCatalog.Sftp }];
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
        if (FixtureMode == "attention")
        {
            // A failed transfer that needs a sign-in, a server key review or a certificate review shows that action in place of Retry.
            var signIn = Fx("t8", "failed", "download", "drive", "/Wedding", "/storage/emulated/0/Download/Inbound", 1, 0, 50_000_000, 0, 0, 5, "Sign-in has run out.");
            signIn.Problem = SiteProblems.SignInNeeded;
            var key = Fx("t9", "failed", "upload", "home", "/docs", "/storage/emulated/0/Download/Inbound", 1, 0, 50_000_000, 0, 0, 6, "The server key changed.");
            key.Problem = SiteProblems.HostKeyChanged;
            var cert = Fx("t10", "failed", "download", "home", "/music", "/storage/emulated/0/Download/Inbound/Music", 1, 0, 50_000_000, 0, 0, 7, "The certificate is not trusted.");
            cert.Problem = SiteProblems.CertificateNotTrusted;
            _transfers = [signIn, key, cert, .. _transfers];
            _transferPaths["t8"] = ["a.zip"]; _transferPaths["t9"] = ["b.pdf"]; _transferPaths["t10"] = ["c.mp3"];
        }
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

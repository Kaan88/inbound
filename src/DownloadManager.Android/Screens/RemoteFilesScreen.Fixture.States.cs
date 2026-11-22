#if INBOUND_TEST_BUILD
using Android.Views;
using Google.Android.Material.TextField;
using DownloadManager.Contracts;
using DownloadManager.Mobile;

namespace Inbound.App.Screens;

/// <summary>
/// Test build only, for the side-by-side captures of mockups x2 to x33: <c>--es x_fixture x11</c> (a mockup id) reaches that state with canned data.
/// Nothing here talks to a Host; a paired-Host state sets a fake Host name. Not part of a release build.
/// </summary>
internal sealed partial class RemoteFilesScreen
{
    private Dictionary<string, TextInputLayout>? _fxLayouts;

    private void FxPaired() { _phoneHost = false; _hostName = "Office PC"; }

    private static SiteDto FxSite(string id, string name, string kind, Action<SiteDto>? more = null)
    {
        var site = new SiteDto { Id = id, Name = name, Kind = kind };
        more?.Invoke(site);
        return site;
    }

    private void FxBase(bool paired = false, bool secure = true, bool transfers = true)
    {
        if (paired) FxPaired();
        _listLoaded = true;
        _secureHostConnection = secure;
        _status = new SitesStatusDto { Enabled = true };
        _sites =
        [
            FxSite("home", "Home NAS", SiteKindCatalog.Sftp, s => { s.Host = "nas.local"; s.Port = 22; s.FreeBytes = 1_200_000_000_000; s.RemoteFolder = ""; }),
            FxSite("work", "Work files", SiteKindCatalog.WebDav, s => { s.Url = "https://files.example.com/dav"; }),
            FxSite("drive", "Google Drive", SiteKindCatalog.Drive, s => { s.SignedIn = true; }),
            FxSite("onedrive", "OneDrive", SiteKindCatalog.OneDrive, s => { s.SignedIn = true; }),
            FxSite("backups", "Backups", SiteKindCatalog.S3, s => { s.Region = "eu-central"; }),
            FxSite("print", "Print shop", SiteKindCatalog.Ftp, s => { s.Host = "ftp.example.org"; s.Port = 21; s.Encrypted = false; s.Tls = SiteTlsModes.None; }),
        ];
        _expiredCloudSites.Add("onedrive");
        _transfersLoaded = true;
        _transfers = transfers
            ? [Fx("d1", "done", "download", "home", "/music", "/storage/emulated/0/Download/Inbound/Music", 1, 1, 8_400_000, 8_400_000, 0, 60),
               Fx("d2", "done", "upload", "home", "/docs", "/storage/emulated/0/Download/Inbound", 3, 3, 3_000_000, 3_000_000, 0, 90),
               Fx("d3", "done", "download", "drive", "/Wedding", "/storage/emulated/0/Download/Inbound", 1, 1, 2_000_000, 2_000_000, 0, 120)]
            : [];
    }

    private SiteTransferDto FxRunning(string id, string direction = "download") =>
        Fx(id, "running", direction, "home", "/music", "/storage/emulated/0/Download/Inbound/Music", 1, 0, 455_000_000, 212_000_000, 9_800_000, 1);

    private void FxRunBase(string mode)
    {
        var saved = FixtureMode;
        FixtureMode = mode;
        StartFixture();
        FixtureMode = saved;
    }

    private static void FxCollect(View view, string text, bool byText, List<View> found)
    {
        bool match = byText ? view is TextView tv && tv.Text == text : view.ContentDescription == text;
        if (match && view.Visibility == ViewStates.Visible) found.Add(view);
        if (view is ViewGroup group) for (int i = 0; i < group.ChildCount; i++) FxCollect(group.GetChildAt(i)!, text, byText, found);
    }

    private bool FxClick(string description, bool byText = false, View? root = null)
    {
        var found = new List<View>();
        FxCollect(root ?? _surface, description, byText, found);
        Android.Util.Log.Info("InboundTest", $"fixture click '{description}': {found.Count} views");
        if (found.FirstOrDefault() is not { } view) return false;
        // A text view inside a button: click the first clickable parent.
        for (View? v = view; v is not null; v = v.Parent as View)
            if (v.Clickable) { v.PerformClick(); return true; }
        view.PerformClick();
        return true;
    }

    private bool FxClickText(string text) => FxClick(text, byText: true, root: _sheet?.Window?.DecorView);

    private void FxLater(Action action) => _surface.PostDelayed(() => { if (!_closed) action(); }, 400);

    private void FxForm(string kind, Action<Dictionary<string, string>>? values = null, SiteDto? edit = null)
    {
        FxBase();
        _adding = true; _addStep = 1; _addKind = kind; _editSite = edit;
        _fv.Clear(); ClearKeyState();
        _fv["port"] = kind == SiteKindCatalog.Ftp ? "21" : "22";
        _fv["tls"] = SiteTlsModes.Explicit; _fv["provider"] = SiteKindCatalog.AwsProvider;
        values?.Invoke(_fv);
    }

    private static SiteHostKeyDto FxKey(string type, string fingerprint) => new() { Type = type, Fingerprint = fingerprint, Entry = type + " AAAA" };

    private bool StartStateFixture()
    {
        string id = FixtureMode ?? "";
        if (id.Length < 2 || id[0] != 'x' || !char.IsDigit(id[1])) return false;
        switch (id)
        {
            case "x2":
            {
                FxRunBase("full");
                var t = _transfers.First(x => x.Id == "t5");
                _transferPaths.Remove("t5");
                t.Id = "94bbf4e07a3c4d51b8e2f60c19d7c21a"; t.RemoteFolder = "/backup"; t.LocalFolder = "/storage/emulated/0/Download/Photos 2025";
                t.Files = 24; t.FilesDone = 21; t.FilesFailed = 3; t.Bytes = 112_000_000; t.BytesDone = 96_000_000;
                _transferPaths[t.Id] = ["Photos 2025/IMG_2201.jpg", "Photos 2025/IMG_2202.jpg"];
                FxLater(() => ShowTransferDetails(t));
                return true;
            }
            case "x2b":
            {
                FxRunBase("full");
                var t = _transfers.First(x => x.Id == "t4");
                _transferPaths.Remove("t4");
                t.Id = "5d0e81c2b74a4f0f9e33a1d6c8b2e0f7"; t.Direction = "download"; t.RemoteFolder = "/media/rehearsals"; t.LocalFolder = "/storage/emulated/0/Download/Inbound/Video";
                t.Files = 1; t.Bytes = 1_400_000_000; t.BytesDone = 310_000_000;
                _transferPaths[t.Id] = ["rehearsal-take-3.mp4"];
                FxLater(() => ShowTransferDetails(t));
                return true;
            }
            case "x3":
            {
                FxRunBase("full");
                var t = _transfers.First(x => x.Id == "t2");
                FxLater(() => ConfirmCancelTransfer(t));
                return true;
            }
            case "x5": FxRunBase("full"); FxLater(() => FxClick("More options")); return true;
            case "x5b": FxRunBase("full"); FxLater(() => { if (_tfSwipe is not null) { _tfSwipe.Refreshing = true; RefreshGap(); } }); return true;
            case "x6": FxPaired(); FxRunBase("error"); return true;
            case "x7": case "x8": case "x9": FxFolder(id); return true;
            case "x33":
                FxBase();
                _sites = [_sites[0], _sites[1], _sites[2], _sites[4]];
                _transfers = [FxRunning("t1")];
                Render();
                FxLater(() => _activity.ShowMessage("setlist.pdf is on this phone", "Show in folder", () => { }));
                return true;
            case "x11": FxBase(); break;
            case "x11b": FxBase(); Render(); FxLater(() => FxClick("Remote Files options")); return true;
            case "x12": FxBase(); Render(); FxLater(() => ShowLocationSheet(_sites[2])); return true;
            case "x12b": FxBase(); Render(); FxLater(() => ShowLocationSheet(_sites[0])); return true;
            case "x13": FxBase(); Render(); FxLater(() => ConfirmRemove(_sites[2])); return true;
            case "x14": FxBase(paired: true); _status = new SitesStatusDto { Enabled = false }; break;
            case "x14b": FxBase(paired: true); _status = new SitesStatusDto { Enabled = false }; Render(); FxLater(() => FxClick("Remote Files options")); return true;
            case "x15":
                FxBase();
                _sites = [_sites[0], _sites[1]];
                _transfers = [FxRunning("t1", "upload"), FxRunning("t2")];
                Render(); FxLater(ConfirmDisable); return true;
            case "x16": FxBase(paired: true); _listLoaded = false; _error = LoadErrorText; break;
            case "x17": FxBase(); _locationSearchOpen = true; _locationSearch = "dropbx"; break;
            case "x18": FxBase(); _adding = true; _addStep = 0; break;
            case "x19": FxForm(SiteKindCatalog.Sftp, v => { v["name"] = "Home NAS"; v["host"] = "nas.local"; v["user"] = "media"; v["password"] = "hunter2hunter2"; v["folder"] = "/media"; }); _moreOpen = true; break;
            case "x20": FxForm(SiteKindCatalog.Ftp, v => { v["name"] = "Print shop"; v["host"] = "ftp.example.org"; v["user"] = "orders"; v["password"] = "hunter2x"; v["folder"] = "/"; }); break;
            case "x20b":
                FxForm(SiteKindCatalog.Ftp, v => { v["name"] = "Print shop"; v["host"] = "ftp.example.org"; v["user"] = "orders"; v["password"] = "hunter2x"; v["tls"] = SiteTlsModes.None; });
                _allowInsecure = true;
                break;
            case "x20c":
                FxForm(SiteKindCatalog.Ftp, v => { v["name"] = "Print shop"; v["host"] = "ftp.example.org"; });
                Render();
                FxLater(() => ShowCertificateSheet(_sites[5], new SiteCertificateDto
                {
                    Subject = "ftp.example.org", Issuer = "ftp.example.org", NotAfter = new DateTimeOffset(2027, 2, 3, 12, 0, 0, TimeSpan.Zero), SelfSigned = true,
                    Fingerprint = "4F:A2:91:0C:7E:33:B8:5D:E1:20:6A:9F:C4:17:58:2B:D0:63:AE:F9:14:8C:72:05:BB:3E:69:D1:0A:C8:57:E2",
                }, null));
                return true;
            case "x21":
                FxForm(SiteKindCatalog.WebDav, v => { v["url"] = "http://files.example.com/dav"; v["user"] = "anna"; v["password"] = "hunter2x"; });
                _allowInsecure = false;
                Render();
                FxLater(() => SubmitForm(_fxLayouts!));
                return true;
            case "x22":
                FxForm(SiteKindCatalog.S3, v => { v["name"] = "Backups"; v["provider"] = "Wasabi"; v["endpoint"] = "s3.eu-central-2.example.com"; v["region"] = "eu-central-2"; v["user"] = "AKEXAMPLE7Q2"; v["password"] = "secretsecretsecret"; v["folder"] = "backups/phone"; v["local"] = "D:\\Downloads"; });
                FxPaired();
                break;
            case "x23": FxForm(SiteKindCatalog.B2, v => { v["name"] = "Archive"; v["user"] = "0051exampleKeyId"; v["password"] = "secretsecretsecret"; v["folder"] = "bucket/folder"; }); break;
            case "x23b":
                FxForm(SiteKindCatalog.B2, v => { v["name"] = "Archive"; v["user"] = "0051exampleKeyId"; v["password"] = "secretsecretsecret"; v["folder"] = "bucket/folder"; });
                Render();
                FxLater(() => ShowTestFailed(FxSite("archive", "Archive", SiteKindCatalog.B2), "The application key is not valid."));
                return true;
            case "x24": FxForm(SiteKindCatalog.Drive, v => { v["name"] = "Google Drive"; }); break;
            case "x24b": FxForm(SiteKindCatalog.OneDrive, v => { v["name"] = "OneDrive"; v["ownId"] = "0f3a9c2e-example-client"; v["ownSecret"] = "secretsecret"; }); FxPaired(); _ownIdOpen = true; break;
            case "x25": FxForm(SiteKindCatalog.Sftp); FxPaired(); _secureHostConnection = false; break;
            case "x26":
                FxForm(SiteKindCatalog.Sftp, v => { v["name"] = "Home NAS"; v["host"] = "nas.local"; v["user"] = "media"; v["password"] = "hunter2hunter2"; v["folder"] = "/media"; });
                Render();
                FxLater(() => ShowSftpKeyDialog(_sites[0], new SftpKeyReview(SftpKeyCase.First,
                    [FxKey("ssh-ed25519", "SHA256:q3Vx8pL0eN2Tg7Rk5mWbyZcA1fH9sJ4uD6oKiE0Pt"), FxKey("ecdsa-sha2-nistp256", "SHA256:Lm4QeW1tY8nCv2Rb7XkJpH5sG0dA3zF9uT6oNiE2r")], []), _generation, null));
                return true;
            case "x26b":
                FxBase(); Render();
                FxLater(() => ShowSftpKeyDialog(_sites[0], new SftpKeyReview(SftpKeyCase.MoreKeys,
                    [FxKey("rsa-sha2-512", "SHA256:Hb2Kc9Xw4Tq7Lm1Np5RdvF8sJ3gA6zE0uY2oWiQ7c")], [FxKey("ssh-ed25519", "SHA256:q3Vx8pL0eN2Tg7Rk5mWb")]), _generation, null));
                return true;
            case "x27":
                FxBase(); Render();
                FxLater(() => ShowSftpKeyDialog(_sites[0], new SftpKeyReview(SftpKeyCase.Changed,
                    [FxKey("ssh-ed25519", "SHA256:Zt7cN0wQ4rB9yK2mVp8LeS1xG6hJ3fU5dA0oRiT4n")], [FxKey("ssh-ed25519", "SHA256:q3Vx8pL0eN2Tg7Rk5mWbyZcA1fH9sJ4uD6oKiE0Pt")]), _generation, null));
                return true;
            case "x28":
                FxBase(); _sites[3].SigningIn = true; _sites = [_sites[0], _sites[3]]; Render();
                FxLater(() => ShowPhoneSignInWait(_sites[1]));
                return true;
            case "x29":
                FxBase(paired: true); Render();
                FxLater(() => ShowPasteTokenDialog(_sites[3], "rclone authorize \"onedrive\" --auth-no-open-browser"));
                return true;
            case "x29b":
                FxBase(paired: true); _sites[3].ClientId = "own-id"; Render();
                FxLater(() => ShowPasteTokenDialog(_sites[3], "rclone authorize \"onedrive\" \"<your client ID>\" \"<your client secret>\" --auth-no-open-browser"));
                return true;
            case "x30":
                FxBase(); _sites[2].HasClientSecret = true; _sites[2].ClientId = "my id/with spaces"; Render();
                FxLater(() => { ShowClientIdDialog(_sites[2]); FxLater(() => FxClickText("Save")); });
                return true;
            case "x31":
            {
                FxBase();
                var nas = FxSite("home", "Home NAS", SiteKindCatalog.Sftp, s => { s.Host = "nas.local"; s.Port = 22; s.User = "media"; s.HasPassword = true; s.RemoteFolder = "/media"; s.HostKeys = [FxKey("ssh-ed25519", "SHA256:fixture")]; });
                _sites[0] = nas;
                BeginEditLocation(nas);
                _fv["port"] = "2222";
                Render();
                return true;
            }
            default: return false;
        }
        Render();
        return true;
    }

    private void FxFolder(string id)
    {
        FxBase();
        _transfers = id == "x7" ? [FxRunning("t1"), FxRunning("t2", "upload")] : [FxRunning("t1")];
        _inFolder = true;
        _active = id == "x9" ? 1 : 0;
        ResetPane(_panes[0], _sites[0], "/media/recordings/2026");
        ResetPane(_panes[1], null, id == "x9" ? "Download/Inbound/Music" : "Download");
        static DateTimeOffset D(int m, int d) => new(2026, m, d, 12, 0, 0, TimeSpan.Zero);
        _panes[0].Items.AddRange([
            new() { Name = "masters", IsFolder = true, ChildCount = 12 }, new() { Name = "stems", IsFolder = true, ChildCount = 48 },
            new() { Name = "session-0412.flac", Size = 412_000_000, Modified = D(4, 12) }, new() { Name = "session-0419.flac", Size = 388_000_000, Modified = D(4, 19) },
            new() { Name = "session-0503.flac", Size = 455_000_000, Modified = D(5, 3) }, new() { Name = "notes.pdf", Size = 1_100_000, Modified = D(5, 3) },
            new() { Name = "artwork.zip", Size = 64_000_000, Modified = D(5, 9) },
        ]);
        _panes[1].Items.AddRange([
            new() { Name = "demo-0301.m4a", Size = 18_000_000, Modified = D(3, 1) }, new() { Name = "session-0503.flac", Size = 455_000_000, Modified = DateTimeOffset.Now },
            new() { Name = "session-0419.flac", Size = 388_000_000, Modified = D(4, 20) }, new() { Name = "voice-memo-0517.m4a", Size = 24_000_000, Modified = D(5, 17) },
        ]);
        foreach (var pane in _panes) { pane.Total = pane.Items.Count; pane.Listing = "fx"; Resort(pane); }
        if (id == "x9")
        {
            var pane = _panes[1];
            pane.Mark = pane.PillMark = "session-0503.flac"; pane.MarkShown = true; pane.MarkPath = pane.Path;
        }
        Render();
        if (id == "x7") FxLater(() => _activity.ShowMessage("2 files going to Download/Inbound/Music", "View transfers", () => { }));
        if (id == "x8") FxLater(() => _activity.ShowMessage("session-0503.flac is on this phone", "Show in folder", () => { }));
    }
}
#endif

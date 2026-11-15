using DownloadManager.Contracts;
using DownloadManager.Desktop.WinUI;

namespace DownloadManager.Core.Tests;

/// <summary>The Sites page of Options: the rclone words and buttons, the site form's checks, and the key helpers.</summary>
public sealed class SitesOptionsTests
{
    private static readonly DateTimeOffset Now = new(2026, 11, 15, 12, 0, 0, TimeSpan.Zero);

    private static SitesStatusDto Status(Action<SitesStatusDto>? change = null)
    {
        var status = new SitesStatusDto
        {
            Enabled = true,
            Rclone = SitesRcloneStates.Running,
            RcloneVersion = "1.76.0",
            RcloneSource = SitesRcloneSources.Downloaded,
            RcloneInstalled = "1.76.0",
        };
        change?.Invoke(status);
        return status;
    }

    private static void NoRclone(SitesStatusDto s)
    {
        s.Rclone = SitesRcloneStates.Missing;
        s.RcloneSource = null;
        s.RcloneVersion = null;
        s.RcloneInstalled = null;
    }

    private static SiteFormValues Form(Func<SiteFormValues, SiteFormValues>? change = null)
    {
        var values = new SiteFormValues("sftp", "Office", "files.example.com", "22", "kaan", "password", "pw", "", "", false, "/home", "");
        return change is null ? values : change(values);
    }

    [Fact]
    public void WithRcloneInUseItSaysWhichAndFromWhere()
    {
        var view = SitesRules.Rclone(Status(), Now);
        Assert.Equal("rclone 1.76.0 (downloaded by Inbound)", view.Line);
        Assert.False(view.LineIsProblem);
        Assert.False(view.ShowDownload);
        Assert.True(view.EnableCheck);
        Assert.False(view.ShowUpdate);
    }

    [Fact]
    public void MissingRcloneOffersTheDownload()
    {
        var view = SitesRules.Rclone(Status(NoRclone), Now);
        Assert.True(view.LineIsProblem);
        Assert.True(view.ShowDownload);
        Assert.True(view.EnableDownload);
        Assert.False(view.EnableCheck);
    }

    [Fact]
    public void AWrongOwnPathDoesNotOfferTheDownloadButSaysSo()
    {
        var view = SitesRules.Rclone(Status(s => { NoRclone(s); s.OwnRclonePath = "/nope"; }), Now);
        Assert.False(view.ShowDownload);
        Assert.Contains("path you chose", view.Line);
    }

    [Fact]
    public void InstallingShowsProgressAndDisablesTheButtons()
    {
        var view = SitesRules.Rclone(Status(s => { NoRclone(s); s.RcloneInstalling = true; }), Now);
        Assert.Equal("Downloading rclone…", view.Line);
        Assert.False(view.EnableDownload);
        Assert.False(view.EnableCheck);
        Assert.False(view.EnableUpdate);
    }

    [Fact]
    public void WithSitesOffNothingIsEnabled()
    {
        var view = SitesRules.Rclone(Status(s => { s.Enabled = false; s.RcloneUpdate = new SitesRcloneUpdateDto { Version = "1.77.0", State = "ready" }; }), Now);
        Assert.False(view.EnableCheck);
        Assert.False(view.EnableDownload);
        Assert.False(view.EnableUpdate);
    }

    [Fact]
    public void WithSitesOffAMissingRcloneIsNoProblemAndNoErrorIsRepeated()
    {
        var view = SitesRules.Rclone(Status(s => { NoRclone(s); s.Enabled = false; s.Error = "Sites is off."; }), Now);
        Assert.False(view.LineIsProblem);
        Assert.Equal("rclone is looked for when Sites is on.", view.Line);
        Assert.False(view.EnableAutoUpdate);
    }

    [Fact]
    public void BeforeItsVersionIsReadTheLineSaysSo()
    {
        var view = SitesRules.Rclone(Status(s => { s.RcloneSource = SitesRcloneSources.Own; s.OwnRclonePath = "C:\\rclone.exe"; s.RcloneVersion = null; s.RcloneInstalled = null; s.Rclone = SitesRcloneStates.Off; }), Now);
        Assert.Equal("rclone (your own rclone). Its version shows once it has started.", view.Line);
        Assert.False(view.LineIsProblem);
    }

    [Fact]
    public void AutomaticUpdatesCannotBeChangedForAnOwnRclone()
    {
        Assert.True(SitesRules.Rclone(Status(), Now).EnableAutoUpdate);
        Assert.False(SitesRules.Rclone(Status(s => { s.RcloneSource = SitesRcloneSources.Own; s.OwnRclonePath = "/usr/bin/rclone"; }), Now).EnableAutoUpdate);
    }

    [Fact]
    public void OwnRcloneIsNeverOfferedAnUpdate()
    {
        var view = SitesRules.Rclone(Status(s =>
        {
            s.RcloneSource = SitesRcloneSources.Own;
            s.OwnRclonePath = "/usr/bin/rclone";
            s.RcloneUpdate = new SitesRcloneUpdateDto { Version = "1.77.0", State = "ready" };
        }), Now);
        Assert.Null(view.OfferLine);
        Assert.False(view.ShowUpdate);
        Assert.False(view.EnableCheck);
    }

    [Theory]
    [InlineData("ready", true, "rclone 1.77.0 is ready and installs by itself.", true)]
    [InlineData("ready", false, "rclone 1.77.0 is ready.", true)]
    [InlineData("needsAsk", true, "rclone 1.77.0 is a new major version. It isn't installed by itself.", true)]
    [InlineData("waiting", true, null, false)]
    public void TheOfferLineAndItsUpdateButtonFollowTheState(string state, bool automatic, string? expected, bool button)
    {
        var view = SitesRules.Rclone(Status(s =>
        {
            s.AutoUpdateRclone = automatic;
            s.RcloneUpdate = new SitesRcloneUpdateDto { Version = "1.77.0", State = state, WaitUntil = Now.AddDays(3) };
        }), Now);
        if (expected is not null) Assert.Equal(expected, view.OfferLine);
        else Assert.Contains("waits until", view.OfferLine);
        Assert.Equal(button, view.ShowUpdate);
        Assert.Equal(button ? "1.77.0" : null, view.UpdateVersion);
    }

    [Fact]
    public void AnUpdateErrorIsShownInsteadOfTheLastCheck()
    {
        var view = SitesRules.Rclone(Status(s => { s.RcloneUpdateError = "No network."; s.RcloneLastCheck = Now; }), Now);
        Assert.Contains("No network.", view.LastCheckLine);
    }

    [Fact]
    public void ANewSiteNeedsItsSecret()
    {
        Assert.Null(SitesRules.Request(Form(v => v with { Password = "" }), null, out var problem));
        Assert.Equal("Type the password.", problem);
        Assert.Null(SitesRules.Request(Form(v => v with { AuthMethod = "key", PrivateKey = "" }), null, out problem));
        Assert.Contains("private key", problem);
    }

    [Fact]
    public void EditingKeepsStoredSecretsWhenLeftBlank()
    {
        var existing = new SiteDto { Id = "a", Kind = "sftp", HasPassword = true, AuthMethod = "password" };
        var request = SitesRules.Request(Form(v => v with { Password = "" }), existing, out var problem);
        Assert.Null(problem);
        Assert.NotNull(request);
        Assert.Equal("a", request.Id);
        Assert.Null(request.Password);
        Assert.Null(request.PrivateKey);
        Assert.Null(request.KeyPassphrase);
    }

    [Fact]
    public void ATypedSecretIsSentAndRemovingAPassphraseSendsEmpty()
    {
        var typed = SitesRules.Request(Form(v => v with { Password = "new" }), null, out _);
        Assert.Equal("new", typed!.Password);

        const string key = "-----BEGIN OPENSSH PRIVATE KEY-----\r\nabc\r\n-----END OPENSSH PRIVATE KEY-----";
        var existing = new SiteDto { Id = "a", HasPrivateKey = true, HasKeyPassphrase = true };
        var removed = SitesRules.Request(Form(v => v with { AuthMethod = "key", PrivateKey = key, RemovePassphrase = true }), existing, out var problem);
        Assert.Null(problem);
        Assert.Equal(string.Empty, removed!.KeyPassphrase);
        Assert.DoesNotContain('\r', removed.PrivateKey!);
        Assert.EndsWith("\n", removed.PrivateKey);
    }

    [Fact]
    public void ANameIsRequired()
    {
        Assert.Null(SitesRules.Request(Form(v => v with { Name = " " }), null, out var problem));
        Assert.Equal("Give the site a name.", problem);
    }

    [Theory]
    [InlineData("sftp://files.example.com")]
    [InlineData("kaan@files.example.com")]
    [InlineData("files example")]
    [InlineData("")]
    public void TheServerIsANameOrAnAddressOnly(string host)
    {
        Assert.Null(SitesRules.Request(Form(v => v with { Host = host }), null, out var problem));
        Assert.NotNull(problem);
    }

    [Theory]
    [InlineData("0")]
    [InlineData("70000")]
    [InlineData("ssh")]
    [InlineData("-1")]
    public void ThePortIsOneToSixtyFiveThousand(string port)
    {
        Assert.Null(SitesRules.Request(Form(v => v with { Port = port }), null, out var problem));
        Assert.Contains("port", problem);
    }

    [Fact]
    public void ABlankLocalFolderIsNullAndTextIsTrimmed()
    {
        var request = SitesRules.Request(Form(v => v with { LocalFolder = "  ", Name = " Office ", RemoteFolder = " /x " }), null, out _);
        Assert.Null(request!.LocalFolder);
        Assert.Equal("Office", request.Name);
        Assert.Equal("/x", request.RemoteFolder);
    }

    [Fact]
    public void AKeyThatIsNotAKeyIsRefused()
    {
        Assert.Null(SitesRules.Request(Form(v => v with { AuthMethod = "key", PrivateKey = "hello" }), null, out var problem));
        Assert.Contains("private key", problem);
        // The window says what the Host would: a .pub or .ppk text gets its own words (one rule, SiteKeyRules).
        Assert.Null(SitesRules.Request(Form(v => v with { AuthMethod = "key", PrivateKey = "ssh-ed25519 AAAAC3Nza me@pc" }), null, out problem));
        Assert.Contains("public key", problem);
        Assert.Null(SitesRules.Request(Form(v => v with { AuthMethod = "key", PrivateKey = "PuTTY-User-Key-File-3: ssh-ed25519" }), null, out problem));
        Assert.Contains("PuTTY", problem);
    }

    [Fact]
    public void ASiteWithoutKeysNeedsTrustAndAReplacementMustMatchTheOfferedKey()
    {
        Assert.True(SitesRules.NeedsTrust(new SiteDto { Kind = "sftp" }));
        Assert.False(SitesRules.NeedsTrust(new SiteDto { Kind = "sftp", HostKeys = [new SiteHostKeyDto { Fingerprint = "SHA256:a" }] }));
        var keys = new[] { new SiteHostKeyDto { Fingerprint = "SHA256:a" }, new SiteHostKeyDto { Fingerprint = "SHA256:b" } };
        Assert.True(SitesRules.OffersFingerprint(keys, "SHA256:b"));
        Assert.False(SitesRules.OffersFingerprint(keys, "SHA256:c"));
        Assert.False(SitesRules.OffersFingerprint(keys, null));
    }

    [Fact]
    public void AKeyLineShowsTypeAndFingerprintAndASummaryTheServer()
    {
        Assert.Equal("ssh-ed25519  SHA256:abc", SitesRules.KeyLine(new SiteHostKeyDto { Type = "ssh-ed25519", Fingerprint = "SHA256:abc" }));
        Assert.Equal("kaan@h:2222  /srv", SitesRules.Summary(new SiteDto { User = "kaan", Host = "h", Port = 2222, RemoteFolder = "/srv" }));
        Assert.Equal("kaan@h", SitesRules.Summary(new SiteDto { User = "kaan", Host = "h", Port = 22 }));
    }

    [Fact]
    public void AnFtpSiteKeepsItsEncryptionChoiceAndOnlySftpAsksAboutAKey()
    {
        var request = SitesRules.Request(Form(v => v with { Kind = "ftp", Port = "21", Tls = SiteTlsModes.None }), null, out var problem);
        Assert.Null(problem);
        Assert.Equal(SiteTlsModes.None, request!.Tls);
        Assert.Equal(21, request.Port);
        Assert.Null(SitesRules.Request(Form(v => v with { Kind = "ftp", Host = "ftp://files.example.com" }), null, out problem));
        Assert.Contains("ftp://", problem);
        Assert.False(SitesRules.NeedsTrust(new SiteDto { Kind = "ftp" }));
        Assert.Equal("kaan@h", SitesRules.Summary(new SiteDto { Kind = "ftp", User = "kaan", Host = "h", Port = 21 }));
        Assert.Equal("kaan@h:22", SitesRules.Summary(new SiteDto { Kind = "ftp", User = "kaan", Host = "h", Port = 22 }));
    }

    [Fact]
    public void AWebDavSiteNeedsAnAddressAndNotAServerName()
    {
        Assert.Null(SitesRules.Request(Form(v => v with { Kind = "webdav", Url = "" }), null, out var problem));
        Assert.Equal("Type the server address.", problem);
        Assert.Null(SitesRules.Request(Form(v => v with { Kind = "webdav", Url = "files.example.com" }), null, out problem));
        Assert.StartsWith("The server address must start with https://", problem);
        Assert.Null(SitesRules.Request(Form(v => v with { Kind = "webdav", Url = "https://me:pw@files.example.com/dav" }), null, out problem));
        Assert.Contains("Leave the user name and password out", problem);

        var request = SitesRules.Request(Form(v => v with { Kind = "webdav", Host = "", Url = " https://files.example.com/dav ", Vendor = "nextcloud" }), null, out problem);
        Assert.Null(problem);
        Assert.Equal("https://files.example.com/dav", request!.Url);
        Assert.Equal("nextcloud", request.Vendor);
        Assert.Equal("", request.Host);
        Assert.Null(request.PrivateKey);
        Assert.Equal("https://files.example.com/dav  /home", SitesRules.Summary(new SiteDto { Kind = "webdav", Url = "https://files.example.com/dav", RemoteFolder = "/home" }));
    }

    [Fact]
    public void AnS3SiteNeedsBothKeysAndAnEndpointUnlessItIsAmazon()
    {
        Assert.Null(SitesRules.Request(Form(v => v with { Kind = "s3", Provider = "Wasabi" }), null, out var problem));
        Assert.Equal("Type the service address (endpoint).", problem);
        Assert.Null(SitesRules.Request(Form(v => v with { Kind = "s3", Provider = "AWS", User = "" }), null, out problem));
        Assert.Equal("Type the access key ID.", problem);
        Assert.Null(SitesRules.Request(Form(v => v with { Kind = "s3", Provider = "AWS", Password = "" }), null, out problem));
        Assert.Equal("Type the secret access key.", problem);
        Assert.Null(SitesRules.Request(Form(v => v with { Kind = "s3", Provider = "AWS", Region = "eu central" }), null, out problem));
        Assert.StartsWith("A region is", problem);

        var request = SitesRules.Request(Form(v => v with { Kind = "s3", Provider = "AWS", Region = "eu-central-1" }), null, out problem);
        Assert.Null(problem);
        Assert.Equal("AWS", request!.Provider);
        Assert.Null(request.Endpoint);
        Assert.Equal("eu-central-1", request.Region);
        // Editing keeps the saved secret when the box is left empty.
        Assert.NotNull(SitesRules.Request(Form(v => v with { Kind = "s3", Provider = "AWS", Password = "" }), new SiteDto { Id = "x", Kind = "s3", HasPassword = true }, out problem));
        Assert.Equal("Amazon S3, eu-central-1", SitesRules.Summary(new SiteDto { Kind = "s3", Provider = "AWS", Region = "eu-central-1" }));
        Assert.Equal("MinIO, http://nas:9000", SitesRules.Summary(new SiteDto { Kind = "s3", Provider = "Minio", Endpoint = "http://nas:9000" }));
    }

    [Fact]
    public void PlainFtpAndHttpAddressesAreWarnedAbout()
    {
        Assert.Contains("Plain FTP is not encrypted", SitesRules.PlainWarning("ftp", SiteTlsModes.None, null, null));
        Assert.Null(SitesRules.PlainWarning("ftp", SiteTlsModes.Explicit, null, null));
        Assert.Contains("http://", SitesRules.PlainWarning("webdav", null, "http://nas/dav", null));
        Assert.Null(SitesRules.PlainWarning("webdav", null, "https://nas/dav", null));
        Assert.Contains("http://", SitesRules.PlainWarning("s3", null, null, "http://nas:9000"));
        Assert.Null(SitesRules.PlainWarning("s3", null, null, null));
        Assert.Null(SitesRules.PlainWarning("sftp", null, null, null));
    }

    [Fact]
    public void ACertificateQuestionNamesTheServerTheIssuerAndTheFingerprint()
    {
        var lines = SitesRules.CertificateLines(new SiteCertificateDto
        {
            Subject = "CN=nas", Issuer = "CN=nas", Names = ["nas.home", "192.168.1.5"], SelfSigned = true, Fingerprint = "AB:CD",
            NotBefore = new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero), NotAfter = new DateTimeOffset(2027, 1, 1, 0, 0, 0, TimeSpan.Zero),
        });
        Assert.Equal(("Issued to", "nas.home, 192.168.1.5"), lines[0]);
        Assert.Equal("itself (a self-signed certificate)", lines[1].Value);
        Assert.Equal(("SHA-256 fingerprint", "AB:CD"), lines[3]);
        Assert.Null(SitesRules.TrustedCertificateLine(new SiteDto()));
        Assert.StartsWith("Trusts its server's own certificate", SitesRules.TrustedCertificateLine(new SiteDto { TrustedCertificates = [new SiteTrustedCertificateDto()] }));
        Assert.True(SitesBrowseRules.PointsToOptions(SiteProblems.CertificateNotTrusted));
        Assert.Contains("Test the site", SitesBrowseRules.ProblemText(SiteProblems.CertificateNotTrusted, null, null, null));
    }
}

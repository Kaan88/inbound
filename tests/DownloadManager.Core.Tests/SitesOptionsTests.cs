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
    }

    [Fact]
    public void ASiteWithoutKeysNeedsTrustAndAReplacementMustMatchTheOfferedKey()
    {
        Assert.True(SitesRules.NeedsTrust(new SiteDto()));
        Assert.False(SitesRules.NeedsTrust(new SiteDto { HostKeys = [new SiteHostKeyDto { Fingerprint = "SHA256:a" }] }));
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
}

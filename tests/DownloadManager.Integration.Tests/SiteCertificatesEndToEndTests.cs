using System.Net;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using DownloadManager.Contracts;
using DownloadManager.Runtime.Sites;
using DownloadManager.Sites;

namespace DownloadManager.Integration.Tests;

/// <summary>
/// SITES.6b: a server with its own (self-signed) certificate, end to end with a real rclone: the test fails as untrusted,
/// the certificate is read without a login, trusting it needs the fingerprint the user saw, the next test connects, and
/// another server forgets it. Against <c>rclone serve webdav --cert</c> (https) and <c>rclone serve ftp --cert</c>
/// (implicit FTPS). Skipped without rclone.
/// </summary>
public sealed partial class SiteTransfersEndToEndTests
{
    [SiteTransfersFact]
    public async Task AnHttpsSiteWithItsOwnCertificateIsTrustedOnlyAfterTheUserSaysSo()
    {
        var (certificate, certFile, keyFile) = MakeCertificate("127.0.0.1", withName: true);
        var port = StartServer(slow: false, "webdav", ["--user", User, "--pass", Password, "--cert", certFile, "--key", keyFile]);
        var commands = Open(null);
        await Call<SitesStatusDto>(commands, SitesMessageTypes.SetEnabled, new SitesSetEnabledRequest { Enabled = true, OwnRclonePath = Rclone });
        var saved = await Call<SiteResultDto>(commands, SitesMessageTypes.Save, new SiteSaveRequest
        {
            Name = "DAVS", Kind = SiteKinds.WebDav, Url = $"https://127.0.0.1:{port}/", Vendor = "other", User = User, Password = Password,
        });
        Assert.True(saved.Ok, saved.Message);
        var id = saved.Site!.Id;
        Assert.True(saved.Site.Encrypted);

        var untrusted = await Call<SiteResultDto>(commands, SitesMessageTypes.Test, new SiteIdRequest { Id = id });
        Assert.False(untrusted.Ok);
        Assert.Equal(SiteProblems.CertificateNotTrusted, untrusted.Problem);

        var read = await Call<SiteCertificateResultDto>(commands, SitesMessageTypes.Certificate, new SiteIdRequest { Id = id });
        Assert.True(read.Ok, read.Message);
        var seen = read.Certificate!;
        Assert.Equal(TlsCertificateProbe.Fingerprint(certificate), seen.Fingerprint);
        Assert.True(seen.SelfSigned);
        Assert.False(seen.TrustedBySystem);
        Assert.Null(seen.CannotTrust);
        Assert.Contains("127.0.0.1", seen.Names);
        Assert.False(read.TrustedBySite);

        // Only the certificate the user saw is trusted.
        var wrong = await Call<SiteResultDto>(commands, SitesMessageTypes.Trust,
            new SiteTrustRequest { Id = id, CertificateFingerprint = "00:" + seen.Fingerprint[3..] });
        Assert.False(wrong.Ok);
        var trusted = await Call<SiteResultDto>(commands, SitesMessageTypes.Trust, new SiteTrustRequest { Id = id, CertificateFingerprint = seen.Fingerprint });
        Assert.True(trusted.Ok, trusted.Message);
        Assert.Single(trusted.Site!.TrustedCertificates);
        var file = Path.Combine(State, "sites", "rclone", RcloneProcess.TrustedCertificatesFileName);
        Assert.True(File.Exists(file));
        Assert.Contains(certificate.ExportCertificatePem().Trim(), await File.ReadAllTextAsync(file));

        var works = await Call<SiteResultDto>(commands, SitesMessageTypes.Test, new SiteIdRequest { Id = id });
        Assert.True(works.Ok, works.Message);
        var listed = await Call<SiteBrowseResultDto>(commands, SitesMessageTypes.Browse, new SiteBrowseRequest { Id = id, Path = "/pub" });
        Assert.True(listed.Ok, listed.Message);
        Assert.Contains(listed.Items, item => item.Name == "readme.txt");
        Assert.True((await Call<SiteCertificateResultDto>(commands, SitesMessageTypes.Certificate, new SiteIdRequest { Id = id })).TrustedBySite);

        // Forgotten, the site is refused again; trusted again, it works again.
        var forgotten = await Call<SiteResultDto>(commands, SitesMessageTypes.Trust, new SiteTrustRequest { Id = id, ForgetCertificates = true });
        Assert.True(forgotten.Ok, forgotten.Message);
        Assert.Empty(forgotten.Site!.TrustedCertificates);
        Assert.False(File.Exists(file));
        Assert.Equal(SiteProblems.CertificateNotTrusted,
            (await Call<SiteResultDto>(commands, SitesMessageTypes.Test, new SiteIdRequest { Id = id })).Problem);
        Assert.True((await Call<SiteResultDto>(commands, SitesMessageTypes.Trust, new SiteTrustRequest { Id = id, CertificateFingerprint = seen.Fingerprint })).Ok);
        Assert.True((await Call<SiteResultDto>(commands, SitesMessageTypes.Test, new SiteIdRequest { Id = id })).Ok);

        // Another address is another server: its certificate is not carried over, and the file goes when none is left.
        var moved = await Call<SiteResultDto>(commands, SitesMessageTypes.Save, new SiteSaveRequest
        {
            Id = id, Name = "DAVS", Kind = SiteKinds.WebDav, Url = $"https://localhost:{port}/", Vendor = "other", User = User,
        });
        Assert.True(moved.Ok, moved.Message);
        Assert.Empty(moved.Site!.TrustedCertificates);
        Assert.False(File.Exists(file));
        var otherName = await Call<SiteCertificateResultDto>(commands, SitesMessageTypes.Certificate, new SiteIdRequest { Id = id });
        Assert.True(otherName.Ok, otherName.Message);
        // The certificate is for 127.0.0.1, so trusting it under the name localhost would not help.
        Assert.NotNull(otherName.Certificate!.CannotTrust);
        Assert.False((await Call<SiteResultDto>(commands, SitesMessageTypes.Trust,
            new SiteTrustRequest { Id = id, CertificateFingerprint = otherName.Certificate.Fingerprint })).Ok);
    }

    [SiteTransfersFact]
    public async Task AnImplicitFtpsSiteWithItsOwnCertificateWorksOnceTrusted()
    {
        var (_, certFile, keyFile) = MakeCertificate("127.0.0.1", withName: true);
        var port = StartServer(slow: false, "ftp", ["--user", User, "--pass", Password, "--cert", certFile, "--key", keyFile]);
        var commands = Open(null);
        await Call<SitesStatusDto>(commands, SitesMessageTypes.SetEnabled, new SitesSetEnabledRequest { Enabled = true, OwnRclonePath = Rclone });
        var saved = await Call<SiteResultDto>(commands, SitesMessageTypes.Save, new SiteSaveRequest
        {
            Name = "FTPS", Kind = SiteKinds.Ftp, Host = "127.0.0.1", Port = port, Tls = SiteTlsModes.Implicit, User = User, Password = Password,
        });
        Assert.True(saved.Ok, saved.Message);
        var id = saved.Site!.Id;
        var untrusted = await Call<SiteResultDto>(commands, SitesMessageTypes.Test, new SiteIdRequest { Id = id });
        Assert.Equal(SiteProblems.CertificateNotTrusted, untrusted.Problem);
        var read = await Call<SiteCertificateResultDto>(commands, SitesMessageTypes.Certificate, new SiteIdRequest { Id = id });
        Assert.True(read.Ok, read.Message);
        Assert.True((await Call<SiteResultDto>(commands, SitesMessageTypes.Trust,
            new SiteTrustRequest { Id = id, CertificateFingerprint = read.Certificate!.Fingerprint })).Ok);
        // The handshake and the login now pass. rclone's own test server then refuses PBSZ in implicit mode ("550 Action not
        // taken", seen in its log with rclone 1.76.0), a quirk of that server; the point here is that the certificate is no
        // longer the problem.
        var after = await Call<SiteResultDto>(commands, SitesMessageTypes.Test, new SiteIdRequest { Id = id });
        Assert.NotEqual(SiteProblems.CertificateNotTrusted, after.Problem);
        Assert.True(after.Ok || after.Message!.Contains("550", StringComparison.Ordinal), after.Message);

        // Explicit TLS to a server that speaks TLS from the start is not a certificate question.
        var plainProbe = await Call<SiteResultDto>(commands, SitesMessageTypes.Save, new SiteSaveRequest
        {
            Id = id, Name = "FTPS", Kind = SiteKinds.Ftp, Host = "127.0.0.1", Port = port, Tls = SiteTlsModes.Explicit, User = User, Password = Password,
        });
        Assert.True(plainProbe.Ok, plainProbe.Message);
        Assert.Empty(plainProbe.Site!.TrustedCertificates);
        Assert.False((await Call<SiteCertificateResultDto>(commands, SitesMessageTypes.Certificate, new SiteIdRequest { Id = id })).Ok);
    }

    [Fact]
    public void ACertificateWithoutANameOrOutOfDateCanNotBeTrusted()
    {
        var (noName, _, _) = MakeCertificate("127.0.0.1", withName: false);
        var info = TlsCertificateProbe.Describe(noName, "127.0.0.1", System.Net.Security.SslPolicyErrors.RemoteCertificateChainErrors,
            X509ChainStatusFlags.UntrustedRoot, DateTimeOffset.UtcNow);
        Assert.Contains("subject alternative name", info.CannotTrust);
        var (named, _, _) = MakeCertificate("127.0.0.1", withName: true);
        Assert.Contains("expired", TlsCertificateProbe.Describe(named, "127.0.0.1", System.Net.Security.SslPolicyErrors.RemoteCertificateChainErrors,
            X509ChainStatusFlags.UntrustedRoot, DateTimeOffset.UtcNow.AddYears(3)).CannotTrust);
        Assert.Null(TlsCertificateProbe.Describe(named, "127.0.0.1", System.Net.Security.SslPolicyErrors.RemoteCertificateChainErrors,
            X509ChainStatusFlags.UntrustedRoot, DateTimeOffset.UtcNow).CannotTrust);
    }

    /// <summary>A self-signed ECDSA certificate for <paramref name="ip"/>, written as PEM for <c>rclone serve --cert --key</c>.</summary>
    private (X509Certificate2 Certificate, string CertFile, string KeyFile) MakeCertificate(string ip, bool withName)
    {
        using var key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        var request = new CertificateRequest($"CN={ip}", key, HashAlgorithmName.SHA256);
        if (withName)
        {
            var names = new SubjectAlternativeNameBuilder();
            names.AddIpAddress(IPAddress.Parse(ip));
            request.CertificateExtensions.Add(names.Build());
        }

        request.CertificateExtensions.Add(new X509KeyUsageExtension(X509KeyUsageFlags.DigitalSignature, false));
        request.CertificateExtensions.Add(new X509EnhancedKeyUsageExtension([new Oid("1.3.6.1.5.5.7.3.1")], false));
        var certificate = request.CreateSelfSigned(DateTimeOffset.UtcNow.AddDays(-1), DateTimeOffset.UtcNow.AddYears(1));
        Directory.CreateDirectory(_root);
        var stem = Path.Combine(_root, "server-" + Guid.NewGuid().ToString("N")[..6]);
        File.WriteAllText(stem + ".crt", certificate.ExportCertificatePem());
        File.WriteAllText(stem + ".key", key.ExportPkcs8PrivateKeyPem());
        return (X509CertificateLoader.LoadCertificate(certificate.RawData), stem + ".crt", stem + ".key");
    }
}

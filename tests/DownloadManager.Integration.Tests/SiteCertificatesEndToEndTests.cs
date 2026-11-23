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
            // The first byte is changed, never kept: "00:" would match a real fingerprint that starts with 00 (1 in 256).
            new SiteTrustRequest { Id = id, CertificateFingerprint = (seen.Fingerprint.StartsWith("00:", StringComparison.Ordinal) ? "01:" : "00:") + seen.Fingerprint[3..] });
        Assert.False(wrong.Ok);
        var trusted = await Call<SiteResultDto>(commands, SitesMessageTypes.Trust, new SiteTrustRequest { Id = id, CertificateFingerprint = seen.Fingerprint });
        Assert.True(trusted.Ok, trusted.Message);
        Assert.Single(trusted.Site!.TrustedCertificates);
        // SITES.10: the site's own CA file, written at its first use.
        var folder = Path.Combine(State, "sites", "rclone", RcloneProcess.CertificatesFolderName);
        string[] Files() => Directory.Exists(folder) ? Directory.GetFiles(folder, id + "-*.pem") : [];

        var works = await Call<SiteResultDto>(commands, SitesMessageTypes.Test, new SiteIdRequest { Id = id });
        Assert.True(works.Ok, works.Message);
        Assert.Contains(certificate.ExportCertificatePem().Trim(), await File.ReadAllTextAsync(Assert.Single(Files())));
        var listed = await Call<SiteBrowseResultDto>(commands, SitesMessageTypes.Browse, new SiteBrowseRequest { Id = id, Path = "/pub" });
        Assert.True(listed.Ok, listed.Message);
        Assert.Contains(listed.Items, item => item.Name == "readme.txt");
        Assert.True((await Call<SiteCertificateResultDto>(commands, SitesMessageTypes.Certificate, new SiteIdRequest { Id = id })).TrustedBySite);

        // Forgotten, the site is refused again; trusted again, it works again.
        var forgotten = await Call<SiteResultDto>(commands, SitesMessageTypes.Trust, new SiteTrustRequest { Id = id, ForgetCertificates = true });
        Assert.True(forgotten.Ok, forgotten.Message);
        Assert.Empty(forgotten.Site!.TrustedCertificates);
        // The unused file stays until rclone's next start: it is never taken from under a running rclone.
        Assert.Equal(SiteProblems.CertificateNotTrusted,
            (await Call<SiteResultDto>(commands, SitesMessageTypes.Test, new SiteIdRequest { Id = id })).Problem);
        Assert.True((await Call<SiteResultDto>(commands, SitesMessageTypes.Trust, new SiteTrustRequest { Id = id, CertificateFingerprint = seen.Fingerprint })).Ok);
        Assert.True((await Call<SiteResultDto>(commands, SitesMessageTypes.Test, new SiteIdRequest { Id = id })).Ok);

        // Another address is another server: its certificate is not carried over.
        // Its password is typed again (audit 2026-11-15: a saved password is only sent to the server it was saved for).
        var moved = await Call<SiteResultDto>(commands, SitesMessageTypes.Save, new SiteSaveRequest
        {
            Id = id, Name = "DAVS", Kind = SiteKinds.WebDav, Url = $"https://localhost:{port}/", Vendor = "other", User = User,
            Password = Password,
        });
        Assert.True(moved.Ok, moved.Message);
        Assert.Empty(moved.Site!.TrustedCertificates);
        var otherName = await Call<SiteCertificateResultDto>(commands, SitesMessageTypes.Certificate, new SiteIdRequest { Id = id });
        Assert.True(otherName.Ok, otherName.Message);
        // The certificate is for 127.0.0.1, so trusting it under the name localhost would not help.
        Assert.NotNull(otherName.Certificate!.CannotTrust);
        Assert.False((await Call<SiteResultDto>(commands, SitesMessageTypes.Trust,
            new SiteTrustRequest { Id = id, CertificateFingerprint = otherName.Certificate.Fingerprint })).Ok);
    }

    /// <summary>
    /// SITES.10 (audit 2026-11-17): a certificate trusted for one site is that site's only. Here it is a CA certificate,
    /// and another server's certificate signed by it is still refused for another site (one shared CA file let it in).
    /// </summary>
    [SiteTransfersFact]
    public async Task ACertificateTrustedForOneSiteDoesNotVouchForAnotherServer()
    {
        var (authority, authorityFile, authorityKeyFile, authorityKey) = MakeAuthority("127.0.0.1");
        using var _ = authorityKey;
        var (leafFile, leafKeyFile) = MakeLeaf("127.0.0.1", authority, authorityKey);
        var first = StartServer(slow: false, "webdav", ["--user", User, "--pass", Password, "--cert", authorityFile, "--key", authorityKeyFile]);
        var second = StartServer(slow: false, "webdav", ["--user", User, "--pass", Password, "--cert", leafFile, "--key", leafKeyFile]);
        var commands = Open(null);
        await Call<SitesStatusDto>(commands, SitesMessageTypes.SetEnabled, new SitesSetEnabledRequest { Enabled = true, OwnRclonePath = Rclone });
        async Task<string> Add(string name, int port)
        {
            var saved = await Call<SiteResultDto>(commands, SitesMessageTypes.Save, new SiteSaveRequest
            {
                Name = name, Kind = SiteKinds.WebDav, Url = $"https://127.0.0.1:{port}/", Vendor = "other", User = User, Password = Password,
            });
            Assert.True(saved.Ok, saved.Message);
            return saved.Site!.Id;
        }

        var a = await Add("A", first);
        var b = await Add("B", second);
        var read = await Call<SiteCertificateResultDto>(commands, SitesMessageTypes.Certificate, new SiteIdRequest { Id = a });
        Assert.True(read.Ok, read.Message);
        Assert.True((await Call<SiteResultDto>(commands, SitesMessageTypes.Trust,
            new SiteTrustRequest { Id = a, CertificateFingerprint = read.Certificate!.Fingerprint })).Ok);
        var works = await Call<SiteResultDto>(commands, SitesMessageTypes.Test, new SiteIdRequest { Id = a });
        Assert.True(works.Ok, works.Message);

        var other = await Call<SiteResultDto>(commands, SitesMessageTypes.Test, new SiteIdRequest { Id = b });
        Assert.False(other.Ok);
        Assert.Equal(SiteProblems.CertificateNotTrusted, other.Problem);
    }

    [SiteTransfersFact]
    public async Task ACertificateTrustedForAnFtpsSiteDoesNotVouchForAnHttpsRemote()
    {
        var (authority, authorityFile, authorityKeyFile, authorityKey) = MakeAuthority("127.0.0.1");
        using var _ = authorityKey;
        var (leafFile, leafKeyFile) = MakeLeaf("127.0.0.1", authority, authorityKey);
        var ftps = StartServer(slow: false, "ftp", ["--user", User, "--pass", Password, "--cert", authorityFile, "--key", authorityKeyFile]);
        var dav = StartServer(slow: false, "webdav", ["--user", User, "--pass", Password, "--cert", leafFile, "--key", leafKeyFile]);
        var commands = Open(null);
        await Call<SitesStatusDto>(commands, SitesMessageTypes.SetEnabled, new SitesSetEnabledRequest { Enabled = true, OwnRclonePath = Rclone });
        var ftpsSite = await Call<SiteResultDto>(commands, SitesMessageTypes.Save, new SiteSaveRequest
        {
            Name = "FTPS", Kind = SiteKinds.Ftp, Host = "127.0.0.1", Port = ftps, Tls = SiteTlsModes.Implicit, User = User, Password = Password,
        });
        Assert.True(ftpsSite.Ok, ftpsSite.Message);
        var davSite = await Call<SiteResultDto>(commands, SitesMessageTypes.Save, new SiteSaveRequest
        {
            Name = "DAV", Kind = SiteKinds.WebDav, Url = $"https://127.0.0.1:{dav}/", Vendor = "other", User = User, Password = Password,
        });
        Assert.True(davSite.Ok, davSite.Message);

        var read = await Call<SiteCertificateResultDto>(commands, SitesMessageTypes.Certificate, new SiteIdRequest { Id = ftpsSite.Site!.Id });
        Assert.True(read.Ok, read.Message);
        var trusted = await Call<SiteResultDto>(commands, SitesMessageTypes.Trust,
            new SiteTrustRequest { Id = ftpsSite.Site.Id, CertificateFingerprint = read.Certificate!.Fingerprint });
        Assert.True(trusted.Ok, trusted.Message);
        // The FTPS site's certificate is now in the shared --ca-cert file; it can sign others, and signed the WebDAV
        // server's. The WebDAV remote trusts nothing of its own, so it gets the system's roots alone and is refused.
        Assert.NotEqual(SiteProblems.CertificateNotTrusted,
            (await Call<SiteResultDto>(commands, SitesMessageTypes.Test, new SiteIdRequest { Id = ftpsSite.Site.Id })).Problem);
        var other = await Call<SiteResultDto>(commands, SitesMessageTypes.Test, new SiteIdRequest { Id = davSite.Site!.Id });
        Assert.False(other.Ok);
        Assert.Equal(SiteProblems.CertificateNotTrusted, other.Problem);
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

        // Explicit TLS to a server that speaks TLS from the start is not a certificate question. (Another encryption takes
        // the password again: a saved one is sent only with the encryption it was saved with.)
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

    /// <summary>A self-signed CA certificate (it may sign others) that names <paramref name="ip"/>, so a server can show it too.</summary>
    private (X509Certificate2 Certificate, string CertFile, string KeyFile, ECDsa Key) MakeAuthority(string ip)
    {
        var key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        var request = new CertificateRequest($"CN=Test authority {ip}", key, HashAlgorithmName.SHA256);
        var names = new SubjectAlternativeNameBuilder();
        names.AddIpAddress(IPAddress.Parse(ip));
        request.CertificateExtensions.Add(names.Build());
        request.CertificateExtensions.Add(new X509BasicConstraintsExtension(true, false, 0, true));
        request.CertificateExtensions.Add(new X509KeyUsageExtension(X509KeyUsageFlags.DigitalSignature | X509KeyUsageFlags.KeyCertSign, true));
        request.CertificateExtensions.Add(new X509EnhancedKeyUsageExtension([new Oid("1.3.6.1.5.5.7.3.1")], false));
        request.CertificateExtensions.Add(new X509SubjectKeyIdentifierExtension(request.PublicKey, false));
        var certificate = request.CreateSelfSigned(DateTimeOffset.UtcNow.AddDays(-1), DateTimeOffset.UtcNow.AddYears(1));
        Directory.CreateDirectory(_root);
        var stem = Path.Combine(_root, "authority-" + Guid.NewGuid().ToString("N")[..6]);
        File.WriteAllText(stem + ".crt", certificate.ExportCertificatePem());
        File.WriteAllText(stem + ".key", key.ExportPkcs8PrivateKeyPem());
        return (certificate, stem + ".crt", stem + ".key", key);
    }

    /// <summary>A server certificate for <paramref name="ip"/> signed by <paramref name="authority"/>.</summary>
    private (string CertFile, string KeyFile) MakeLeaf(string ip, X509Certificate2 authority, ECDsa authorityKey)
    {
        using var key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        var request = new CertificateRequest($"CN={ip}", key, HashAlgorithmName.SHA256);
        var names = new SubjectAlternativeNameBuilder();
        names.AddIpAddress(IPAddress.Parse(ip));
        request.CertificateExtensions.Add(names.Build());
        request.CertificateExtensions.Add(new X509BasicConstraintsExtension(false, false, 0, true));
        request.CertificateExtensions.Add(new X509KeyUsageExtension(X509KeyUsageFlags.DigitalSignature, false));
        request.CertificateExtensions.Add(new X509EnhancedKeyUsageExtension([new Oid("1.3.6.1.5.5.7.3.1")], false));
        request.CertificateExtensions.Add(X509AuthorityKeyIdentifierExtension.CreateFromCertificate(authority, true, false));
        var serial = new byte[12];
        RandomNumberGenerator.Fill(serial);
        serial[0] &= 0x7f;
        using var certificate = request.Create(authority.SubjectName, X509SignatureGenerator.CreateForECDsa(authorityKey),
            DateTimeOffset.UtcNow.AddDays(-1), DateTimeOffset.UtcNow.AddMonths(6), serial);
        var stem = Path.Combine(_root, "leaf-" + Guid.NewGuid().ToString("N")[..6]);
        File.WriteAllText(stem + ".crt", certificate.ExportCertificatePem());
        File.WriteAllText(stem + ".key", key.ExportPkcs8PrivateKeyPem());
        return (stem + ".crt", stem + ".key");
    }
}

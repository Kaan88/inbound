using DownloadManager.Sites;

namespace DownloadManager.Sites.Tests;

public sealed class SiteErrorTextTests
{
    // Texts from rclone 1.76.0 on Windows (the first of each pair) and the Go errors it wraps on Linux, as RcloneClient
    // reports them ("rclone operations/list failed (500): ...").
    [Theory]
    [InlineData("rclone operations/list failed (500): NewFs: couldn't connect SSH: dial tcp 127.0.0.1:52299: connectex: No connection could be made because the target machine actively refused it.", "refused the connection")]
    [InlineData("rclone operations/list failed (500): NewFs: couldn't connect SSH: dial tcp 127.0.0.1:52299: connect: connection refused", "refused the connection")]
    [InlineData("rclone operations/list failed (500): NewFs: couldn't connect SSH: dial tcp: lookup nohost.invalid: no such host", "name could not be found")]
    [InlineData("rclone operations/list failed (500): NewFs: couldn't connect SSH: ssh: handshake failed: ssh: unable to authenticate, attempted methods [none publickey], no supported methods remain", "did not accept the sign-in")]
    [InlineData("rclone operations/list failed (500): NewFs: couldn't connect SSH: dial tcp 10.255.255.1:22: connectex: A connection attempt failed because the connected party did not properly respond after a period of time", "did not answer")]
    [InlineData("rclone operations/list failed (500): NewFs: couldn't connect SSH: dial tcp 10.255.255.1:22: i/o timeout", "did not answer")]
    [InlineData("rclone operations/list failed (500): error listing: directory not found", "not on the server")]
    [InlineData("rclone operations/list failed (500): failed to parse private key file: x509: decryption password incorrect", "passphrase is wrong")]
    // SITES.6: FTP, WebDAV and S3, from rclone 1.76.0 on Windows against rclone serve ftp|webdav|s3.
    [InlineData("rclone operations/list failed (500): NewFs: failed to make FTP connection to \"127.0.0.1:52121\": 530 \"Incorrect password, not logged in\"", "did not accept the sign-in")]
    [InlineData("rclone operations/list failed (500): NewFs: failed to make FTP connection to \"127.0.0.1:52999\": dial tcp 127.0.0.1:52999: connectex: No connection could be made because the target machine actively refused it.", "refused the connection")]
    [InlineData("rclone operations/list failed (500): NewFs: failed to make FTP connection to \"nosuchhost.invalid:21\": dial tcp: lookup nosuchhost.invalid: no such host", "name could not be found")]
    [InlineData("rclone operations/list failed (500): error listing: couldn't list files: 401 Unauthorized: 401 Unauthorized", "did not accept the sign-in")]
    [InlineData("rclone operations/list failed (500): error listing: couldn't list files: Propfind \"http://127.0.0.1:52999/\": dial tcp 127.0.0.1:52999: connectex: No connection could be made because the target machine actively refused it.", "refused the connection")]
    [InlineData("rclone operations/list failed (500): error listing: operation error S3: ListBuckets, https response error StatusCode: 403, RequestID: 9A06, HostID: OUEw, api error SignatureDoesNotMatch: The request signature we calculated does not match", "access key ID or the secret")]
    [InlineData("rclone operations/list failed (500): NewFs: failed to make FTP connection to \"127.0.0.1:2121\": tls: failed to verify certificate: x509: certificate signed by unknown authority", "certificate is not trusted")]
    public void KnownFailuresBecomePlainWords(string message, string expected) => Assert.Contains(expected, SiteErrorText.Friendly(message));

    [Theory]
    // A folder named like a failure is not a failure, and an unknown cause keeps rclone's text.
    [InlineData("rclone operations/list failed (500): permission denied: /srv/connection refused")]
    [InlineData("rclone operations/list failed (500): NewFs: couldn't connect SSH: something new")]
    [InlineData("rclone operations/list failed (500): directory not found in /srv/x: permission denied")]
    public void OtherTextsStayAsTheyWere(string message) => Assert.Equal(message, SiteErrorText.Friendly(message));

    // Audit 2026-11-15: what stops a transfer at once (every file would be refused the same way) and what makes it wait
    // for the server, for every kind of site, not only SFTP.
    [Theory]
    [InlineData("rclone operations/copyfile failed (500): NewFs: couldn't connect SSH: ssh: handshake failed: ssh: unable to authenticate, attempted methods [none password], no supported methods remain", true, false)]
    [InlineData("rclone operations/copyfile failed (500): NewFs: failed to make FTP connection to \"127.0.0.1:52121\": 530 \"Incorrect password, not logged in\"", true, false)]
    [InlineData("rclone operations/copyfile failed (500): 401 Unauthorized: ", true, false)]
    [InlineData("rclone operations/copyfile failed (500): operation error S3: PutObject, https response error StatusCode: 403, api error SignatureDoesNotMatch: The request signature we calculated does not match", true, false)]
    [InlineData("rclone operations/copyfile failed (500): couldn't fetch token: oauth2: \"invalid_grant\" \"Token has been expired or revoked.\"", true, false)]
    [InlineData("rclone operations/copyfile failed (500): Get \"https://dav.example/\": tls: failed to verify certificate: x509: certificate signed by unknown authority", true, false)]
    [InlineData("rclone operations/copyfile failed (500): NewFs: couldn't connect SSH: dial tcp 127.0.0.1:52299: connect: connection refused", false, true)]
    [InlineData("rclone operations/copyfile failed (500): NewFs: failed to make FTP connection to \"127.0.0.1:52999\": dial tcp 127.0.0.1:52999: connectex: No connection could be made because the target machine actively refused it.", false, true)]
    [InlineData("rclone operations/copyfile failed (500): Put \"https://dav.example/pub/a.bin\": dial tcp: lookup dav.example: no such host", false, true)]
    [InlineData("rclone operations/copyfile failed (500): Get \"https://s3.example/b\": dial tcp 10.0.0.9:443: i/o timeout", false, true)]
    [InlineData("rclone operations/copyfile failed (500): Get \"https://10.0.0.9/b\": dial tcp 10.0.0.9:443: connectex: A socket operation was attempted to an unreachable network.", false, true)]
    [InlineData("rclone operations/copyfile failed (500): Get \"https://dav.example/\": net/http: TLS handshake timeout", false, true)]
    [InlineData("rclone operations/copyfile failed (500): permission denied: /srv/connection refused", false, false)]
    [InlineData("rclone operations/copyfile failed (500): api error AccessDenied: Access Denied", false, false)]
    public void ATransferStopsOrWaitsForTheWholeSiteOnly(string message, bool refuses, bool cantReach)
    {
        Assert.Equal(refuses, SiteErrorText.RefusesTheSite(message));
        Assert.Equal(cantReach, SiteErrorText.CantReach(message));
    }
}

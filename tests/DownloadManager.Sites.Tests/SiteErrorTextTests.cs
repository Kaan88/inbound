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
    public void KnownFailuresBecomePlainWords(string message, string expected) => Assert.Contains(expected, SiteErrorText.Friendly(message));

    [Theory]
    // A folder named like a failure is not a failure, and an unknown cause keeps rclone's text.
    [InlineData("rclone operations/list failed (500): permission denied: /srv/connection refused")]
    [InlineData("rclone operations/list failed (500): NewFs: couldn't connect SSH: something new")]
    [InlineData("rclone operations/list failed (500): directory not found in /srv/x: permission denied")]
    public void OtherTextsStayAsTheyWere(string message) => Assert.Equal(message, SiteErrorText.Friendly(message));
}

using DownloadManager.Sites;

namespace DownloadManager.Sites.Tests;

public sealed class SiteKeyTextTests
{
    [Theory]
    [InlineData("-----BEGIN OPENSSH PRIVATE KEY-----\nb3Bl\n-----END OPENSSH PRIVATE KEY-----\n")]
    [InlineData("  \r\n-----BEGIN RSA PRIVATE KEY-----\r\nProc-Type: 4,ENCRYPTED\r\nMIIE\r\n-----END RSA PRIVATE KEY-----")]
    [InlineData("-----BEGIN PRIVATE KEY-----\nMC4C\n-----END PRIVATE KEY-----")]
    [InlineData("-----BEGIN ENCRYPTED PRIVATE KEY-----\nMIIF\n-----END ENCRYPTED PRIVATE KEY-----")]
    public void PemPrivateKeysPass(string key) => Assert.Null(SiteKeyText.Problem(key));

    [Theory]
    [InlineData("ssh-ed25519 AAAAC3Nza me@pc", "public key")]
    [InlineData("ecdsa-sha2-nistp256 AAAAE2Vj me@pc", "public key")]
    [InlineData("PuTTY-User-Key-File-3: ssh-ed25519\nEncryption: none", "PuTTY")]
    [InlineData("-----BEGIN SSH2 PUBLIC KEY-----\nAAAA\n-----END SSH2 PUBLIC KEY-----", "not a private key")]
    [InlineData("-----BEGIN OPENSSH PRIVATE KEY-----\nb3Bl", "not a private key")]
    [InlineData("b3BlbnNzaC1rZXktdjE", "not a private key")]
    public void OtherTextIsRefusedWithoutQuotingIt(string key, string expected)
    {
        var problem = SiteKeyText.Problem(key);
        Assert.NotNull(problem);
        Assert.Contains(expected, problem);
        Assert.DoesNotContain(key.Split('\n')[^1], problem);
    }

    [Theory]
    [InlineData("failed to parse private key file: ssh: this private key is passphrase protected", "has a passphrase")]
    [InlineData("failed to parse private key file: x509: decryption password incorrect", "passphrase is wrong")]
    [InlineData("failed to parse private key file: ssh: no key found", "could not be read")]
    [InlineData("couldn't connect SSH: connection refused", "couldn't connect SSH: connection refused")]
    public void RcloneKeyErrorsBecomePlainWords(string message, string expected) =>
        Assert.Contains(expected, SiteKeyText.Friendly(message));
}

using DownloadManager.Contracts;
using DownloadManager.Mobile;
using Xunit;

namespace DownloadManager.Mobile.Tests;

public sealed class SftpKeyFileTests
{
    private const string Ppk = "PuTTY-User-Key-File-3: ssh-ed25519\nEncryption: none\nComment: nas\nPublic-Lines: 2\nAAAA\nPrivate-Lines: 1\nBBBB\nPrivate-MAC: 00\n";
    private const string OpenSsh = "-----BEGIN OPENSSH PRIVATE KEY-----\nAAAA\n-----END OPENSSH PRIVATE KEY-----\n";

    [Fact]
    public void PuttyAndOpenSshKeysAreBothAcceptedAndNamed()
    {
        Assert.Null(SftpKeyFile.Problem(Ppk));
        Assert.Null(SftpKeyFile.Problem(OpenSsh));
        Assert.Equal("PuTTY key · Ed25519", SftpKeyFile.Kind(Ppk));
        Assert.Equal("OpenSSH key", SftpKeyFile.Kind(OpenSsh));
        Assert.Equal("OpenSSH key · RSA", SftpKeyFile.Kind("-----BEGIN RSA PRIVATE KEY-----\nAAAA\n-----END RSA PRIVATE KEY-----"));
    }

    [Fact]
    public void APublicKeyGetsTheDrawnWords()
    {
        const string pub = "ssh-ed25519 AAAAC3Nza home";
        Assert.True(SftpKeyFile.IsPublicKey(pub));
        Assert.Equal(SftpKeyFile.PublicKeyMessage, SftpKeyFile.Problem(pub));
        Assert.Equal("Public key", SftpKeyFile.Kind(pub));
        Assert.Equal("That is a public key (the .pub file). Choose the private key instead: the file with the same name and no .pub ending.", SftpKeyFile.PublicKeyMessage);
    }

    [Fact]
    public void ARandomFileIsNotAKeyAndNeverQuotesItsText()
    {
        const string secretLooking = "hunter2-not-a-key";
        var problem = SftpKeyFile.Problem(secretLooking);
        Assert.NotNull(problem);
        Assert.DoesNotContain(secretLooking, problem);
        Assert.Equal("Not a private key", SftpKeyFile.Kind(secretLooking));
    }

    [Fact]
    public void TheHostsPassphraseAnswersBelongUnderThePassphraseField()
    {
        Assert.True(SftpKeyFile.IsPassphraseProblem("The passphrase doesn't open this key. Check it and try again."));
        Assert.True(SftpKeyFile.IsPassphraseProblem("This PuTTY key has a passphrase. Enter it as the key passphrase."));
        Assert.False(SftpKeyFile.IsPassphraseProblem("Choose the private key to sign in with."));
        Assert.False(SftpKeyFile.IsPassphraseProblem(null));
    }

    [Fact]
    public void KeyTextIsSentTrimmedWithOneNewline()
    {
        Assert.Equal("a\nb\n", SftpKeyFile.Normalize("  a\r\nb\r\n\r\n"));
        Assert.Equal(64 * 1024, SftpKeyFile.MostKeyBytes);
    }

    [Fact]
    public void FailedTransfersThatNeedSignInOrTrustShowThatActionInPlaceOfRetry()
    {
        SiteTransferDto Failed(string? problem) => new() { State = "failed", Problem = problem };
        Assert.Equal(TransferAttention.SignIn, RemoteTransferText.AttentionOf(Failed(SiteProblems.SignInNeeded)));
        Assert.Equal(TransferAttention.ReviewKey, RemoteTransferText.AttentionOf(Failed(SiteProblems.HostKeyChanged)));
        Assert.Equal(TransferAttention.ReviewKey, RemoteTransferText.AttentionOf(Failed(SiteProblems.HostKeyNotAccepted)));
        Assert.Equal(TransferAttention.ReviewCertificate, RemoteTransferText.AttentionOf(Failed(SiteProblems.CertificateNotTrusted)));
        Assert.Equal(TransferAttention.None, RemoteTransferText.AttentionOf(Failed(null)));
        Assert.Equal(TransferAttention.None, RemoteTransferText.AttentionOf(new SiteTransferDto { State = "done", Problem = SiteProblems.SignInNeeded }));
        Assert.Equal("Sign in again", RemoteTransferText.AttentionLabel(TransferAttention.SignIn));
        Assert.Equal("Review certificate", RemoteTransferText.AttentionLabel(TransferAttention.ReviewCertificate));
        Assert.Equal("Review server key", RemoteTransferText.AttentionLabel(TransferAttention.ReviewKey));
    }
}

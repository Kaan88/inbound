using System.Text;
using DownloadManager.Sites;

namespace DownloadManager.Sites.Tests;

/// <summary>
/// SITES.11: .ppk to OpenSSH. Vectors: BLAKE2b from RFC 7693, Argon2 from RFC 9106 section 5, Argon2id for PuTTY's
/// settings from Python's cryptography package, and the key files from PuTTY's own test suite (test/cryptsuite.py,
/// testPPKLoadSave; MIT licence), whose Ed25519 seed gives the public key in the file.
/// </summary>
public sealed class PuttyKeyFileTests
{
    private const string Passphrase = "test-passphrase";
    private const string PublicKeyHex = "7242b33387688f57ff218bb639f6d9fd213ba54f3100d5b5cb64ca6e85247d56";
    private const string SeedHex = "6bef22997c8f2aa7f17b9f930eafdc59c41ad5c5dca5b0e7900fdff006101a58";

    private const string V2Clear = """
        PuTTY-User-Key-File-2: ssh-ed25519
        Encryption: none
        Comment: ed25519-key-20200105
        Public-Lines: 2
        AAAAC3NzaC1lZDI1NTE5AAAAIHJCszOHaI9X/yGLtjn22f0hO6VPMQDVtctkym6F
        JH1W
        Private-Lines: 1
        AAAAIGvvIpl8jyqn8Xufkw6v3FnEGtXF3KWw55AP3/AGEBpY
        Private-MAC: 2a629acfcfbe28488a1ba9b6948c36406bc28422
        """;

    private const string V2Encrypted = """
        PuTTY-User-Key-File-2: ssh-ed25519
        Encryption: aes256-cbc
        Comment: ed25519-key-20200105
        Public-Lines: 2
        AAAAC3NzaC1lZDI1NTE5AAAAIHJCszOHaI9X/yGLtjn22f0hO6VPMQDVtctkym6F
        JH1W
        Private-Lines: 1
        4/jKlTgC652oa9HLVGrMjHZw7tj0sKRuZaJPOuLhGTvb25Jzpcqpbi+Uf+y+uo+Z
        Private-MAC: 5b1f6f4cc43eb0060d2c3e181bc0129343adba2b
        """;

    private const string V3Clear = """
        PuTTY-User-Key-File-3: ssh-ed25519
        Encryption: none
        Comment: ed25519-key-20200105
        Public-Lines: 2
        AAAAC3NzaC1lZDI1NTE5AAAAIHJCszOHaI9X/yGLtjn22f0hO6VPMQDVtctkym6F
        JH1W
        Private-Lines: 1
        AAAAIGvvIpl8jyqn8Xufkw6v3FnEGtXF3KWw55AP3/AGEBpY
        Private-MAC: 816c84093fc4877e8411b8e5139c5ce35d8387a2630ff087214911d67417a54d
        """;

    private const string V3Encrypted = """
        PuTTY-User-Key-File-3: ssh-ed25519
        Encryption: aes256-cbc
        Comment: ed25519-key-20200105
        Public-Lines: 2
        AAAAC3NzaC1lZDI1NTE5AAAAIHJCszOHaI9X/yGLtjn22f0hO6VPMQDVtctkym6F
        JH1W
        Key-Derivation: Argon2id
        Argon2-Memory: 8192
        Argon2-Passes: 13
        Argon2-Parallelism: 1
        Argon2-Salt: 37c3911bfefc8c1d11ec579627d2b3d9
        Private-Lines: 1
        amviz4sVUBN64jLO3gt4HGXJosUArghc4Soi7aVVLb2Tir5Baj0OQClorycuaPRd
        Private-MAC: 6f5e588e475e55434106ec2c3569695b03f423228b44993a9e97d52ffe7be5a8
        """;

    [Fact]
    public void Blake2bMatchesRfc7693()
    {
        var h = new Blake2b(64);
        h.Update("abc"u8);
        var output = new byte[64];
        h.Final(output);
        Assert.Equal("ba80a53f981c4d0d6a2797b69f12f6e94c212f14685ac4b74b12bb6fdbffa2d17d87c5392aab792dc252d5de4533cc9518d38aa8dbf1925ab92386edd4009923",
            Convert.ToHexString(output).ToLowerInvariant());
    }

    [Theory]
    [InlineData("D", "512b391b6f1162975371d30919734294f868e3be3984f3c1a13a4db9fabe4acb")]
    [InlineData("I", "c814d9d1dc7f37aa13f0d77f2494bda1c8de6b016dd388d29952a4c4672b6ce8")]
    [InlineData("Id", "0d640df58d78766c08c037a34a8b53c9d01ef0452d75b65eb52520e96b01e659")]
    public void Argon2MatchesRfc9106(string kind, string expected)
    {
        var tag = Argon2.Hash(Enum.Parse<Argon2.Kind>(kind), Enumerable.Repeat((byte)1, 32).ToArray(), Enumerable.Repeat((byte)2, 16).ToArray(), 32, 3, 4, 32,
            Enumerable.Repeat((byte)3, 8).ToArray(), Enumerable.Repeat((byte)4, 12).ToArray());
        Assert.Equal(expected, Convert.ToHexString(tag).ToLowerInvariant());
    }

    [Fact]
    public void Argon2idMatchesAnotherImplementationForPuttysSettings()
    {
        var tag = Argon2.Hash(Argon2.Kind.Id, Encoding.UTF8.GetBytes(Passphrase), Convert.FromHexString("37c3911bfefc8c1d11ec579627d2b3d9"),
            8192, 13, 1, 80);
        Assert.Equal("6fadb5e3c17fec1610c0b460cc4f990ae17ab6f2b067a89313ecdeb16312cba10fc119fbdf3f2d9fbe74acad10e7c6aed63464a0b2de1b5397bb725f185557c0225b1e14abb86db2c1e2ef939eed3aee",
            Convert.ToHexString(tag).ToLowerInvariant());
    }

    [Theory]
    [InlineData(V2Clear, null)]
    [InlineData(V2Encrypted, Passphrase)]
    [InlineData(V3Clear, null)]
    [InlineData(V3Encrypted, Passphrase)]
    public void EveryVersionGivesTheSameOpenSshKey(string ppk, string? passphrase)
    {
        Assert.True(PuttyKeyFile.IsPuttyKey(ppk));
        Assert.Equal("ssh-ed25519", PuttyKeyFile.KeyTypeOf(ppk));
        var result = PuttyKeyFile.ToOpenSsh(ppk, passphrase);
        Assert.True(result.Ok, result.Problem);
        Assert.Equal("Ed25519", result.KeyType);
        var (publicKey, secret, comment) = ReadOpenSshEd25519(result.OpenSshKey!);
        Assert.Equal(PublicKeyHex, Convert.ToHexString(publicKey).ToLowerInvariant());
        Assert.Equal(SeedHex + PublicKeyHex, Convert.ToHexString(secret).ToLowerInvariant());
        Assert.Equal("ed25519-key-20200105", comment);
    }

    [Theory]
    [InlineData(V2Encrypted)]
    [InlineData(V3Encrypted)]
    public void AWrongOrMissingPassphraseIsSaidPlainly(string ppk)
    {
        Assert.Equal(PuttyKeyFile.WrongPassphrase, PuttyKeyFile.ToOpenSsh(ppk, "not-it").Problem);
        Assert.Equal(PuttyKeyFile.NeedsPassphrase, PuttyKeyFile.ToOpenSsh(ppk, null).Problem);
        Assert.Equal(PuttyKeyFile.NeedsPassphrase, PuttyKeyFile.ToOpenSsh(ppk, "").Problem);
    }

    [Fact]
    public void AChangedOrCutFileIsDamaged()
    {
        Assert.Equal(PuttyKeyFile.Damaged, PuttyKeyFile.ToOpenSsh(V3Clear.Replace("Comment: ed25519", "Comment: Ed25519"), null).Problem);
        Assert.Equal(PuttyKeyFile.Damaged, PuttyKeyFile.ToOpenSsh(V3Clear[..V3Clear.IndexOf("Private-Lines", StringComparison.Ordinal)], null).Problem);
        Assert.Equal(PuttyKeyFile.Damaged, PuttyKeyFile.ToOpenSsh(V2Clear.Replace("Public-Lines: 2", "Public-Lines: 99999999999"), null).Problem);
        Assert.NotNull(PuttyKeyFile.ToOpenSsh(V3Clear.Replace("File-3", "File-1"), null).Problem);
    }

    [Fact]
    public void AnExpensiveKeyDerivationIsRefusedBeforeItRuns()
    {
        var result = PuttyKeyFile.ToOpenSsh(V3Encrypted.Replace("Argon2-Memory: 8192", "Argon2-Memory: 4194304"), Passphrase);
        Assert.False(result.Ok);
        Assert.Contains("more work", result.Problem);
    }

    [Fact]
    public void WindowsLineEndingsAreRead() =>
        Assert.True(PuttyKeyFile.ToOpenSsh(V3Encrypted.Replace("\n", "\r\n"), Passphrase).Ok);

    /// <summary>Reads an unencrypted one-key openssh-key-v1 file as written by <see cref="PuttyKeyFile"/>.</summary>
    private static (byte[] PublicKey, byte[] Secret, string Comment) ReadOpenSshEd25519(string pem)
    {
        var lines = pem.Trim().Split('\n');
        Assert.Equal("-----BEGIN OPENSSH PRIVATE KEY-----", lines[0]);
        Assert.Equal("-----END OPENSSH PRIVATE KEY-----", lines[^1]);
        Assert.All(lines[1..^1], line => Assert.True(line.Length <= 70));
        var data = Convert.FromBase64String(string.Concat(lines[1..^1]));
        int at = 0;
        byte[] Str()
        {
            int length = (data[at] << 24) | (data[at + 1] << 16) | (data[at + 2] << 8) | data[at + 3];
            at += 4;
            var value = data.AsSpan(at, length).ToArray();
            at += length;
            return value;
        }
        Assert.Equal("openssh-key-v1\0", Encoding.ASCII.GetString(data, 0, 15));
        at = 15;
        Assert.Equal("none", Encoding.ASCII.GetString(Str()));
        Assert.Equal("none", Encoding.ASCII.GetString(Str()));
        Assert.Empty(Str());
        at += 4;
        Str();
        var section = Str();
        Assert.Equal(0, section.Length % 8);
        data = section;
        at = 0;
        Assert.Equal(data.AsSpan(0, 4).ToArray(), data.AsSpan(4, 4).ToArray());
        at = 8;
        Assert.Equal("ssh-ed25519", Encoding.ASCII.GetString(Str()));
        var publicKey = Str();
        var secret = Str();
        var comment = Encoding.UTF8.GetString(Str());
        for (byte pad = 1; at < data.Length; pad++) Assert.Equal(pad, data[at++]);
        return (publicKey, secret, comment);
    }
}

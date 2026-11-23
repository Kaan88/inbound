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

    // Several lanes and memory not a multiple of 4 x lanes, from OpenSSL 3.5 (`openssl kdf ARGON2D|ARGON2I|ARGON2ID`);
    // the Argon2id values match Python's cryptography package too (Fable review, SITES.11).
    [Theory]
    [InlineData("D", 1031, 2, 3, "af1933259754f41313c8473eea80647e1a823d906cbb8b76974f6500c8cc80ad7b4d1f10507a9aef32990975069fe6221a09f89d5deb47a21f59177c98c324d9727bccb25a3063b37a801fae85b414d3")]
    [InlineData("D", 4096, 3, 16, "5a76e2475cfdedf6705da40faa876492fadf7f2d6d9de8360fabf63918deefaae0ea0740f6d34042ea868a0045a7c09425b7fd81fb85fded81ed934cf954ed9855917a2a48e3469c4a93ee60192a53f5")]
    [InlineData("I", 1031, 2, 3, "8c3419eac762be7e76861df6834740c0a63aaf0b666af47f766f85f606008c18413a72e9a207bfa6dd1f960bf8e39613d6a2ab08dd304129052702d63c7787ea9b0d65f041bdb0d57c7273d268fa3794")]
    [InlineData("I", 4096, 3, 16, "ed39f7b67f314ffa1099ef936a6d82540731492efc18c166ba4b9e0589d6ede5b6d6d7ad49e7b8cedd86d107dab75ecf86e2af3dad3fa8db044f1873b020b0d9996ee827424768d673ce0d80747075b8")]
    [InlineData("Id", 1031, 2, 3, "c94c6b36311f187fa6d496c1e19d3e67166b5e9392608ca530d3f040b26f2e20b1febec3edd89081097158d2800d8b6117bea8e6a1b6a99c374b321c62fbd071e434803683ed8a7d14564ba7a7127bf1")]
    [InlineData("Id", 4096, 3, 16, "0822ae206379609f57d1f12004b7bdc43ffd7f0af702526a2f86dbfdb58917ea16cdd2616ab17b5819a89b0eb6fdde8b7e86c1ea8e0abde2e15d9917bb4de4e3ee8e0a43163ddbba837c93c406341f61")]
    [InlineData("Id", 129, 1, 2, "a3929eb9641b5c1c4887051de01ab9636892483f4cbf7d2c002a03bc736da694291f8861fbf6755e1ca14e78ff5da6717c263950962e4900f250303a7c6c49c467a8b40d80e5e89ba5b913e395511535")]
    [InlineData("Id", 8192, 2, 5, "78d84b7867a812df07e0ebea5d271ec8636c34b279758f4da8c61422970f83fa851a128d41a91324960898f77e6f29d7af1a8390070d7efbef0c7217a042be3dc153ff404ff468a76e7721a068cc6680")]
    public void Argon2MatchesOpenSslWithSeveralLanes(string kind, int memory, int passes, int lanes, string expected)
    {
        var tag = Argon2.Hash(Enum.Parse<Argon2.Kind>(kind), "password"u8, Convert.FromHexString("000102030405060708090a0b0c0d0e0f"), memory, passes, lanes, 80);
        Assert.Equal(expected, Convert.ToHexString(tag).ToLowerInvariant());
    }

    [Fact]
    public void Argon2StopsWhenCancelled()
    {
        using var cancel = new CancellationTokenSource();
        cancel.Cancel();
        Assert.Throws<OperationCanceledException>(() => Argon2.Hash(Argon2.Kind.Id, "p"u8, new byte[16], 8192, 1, 1, 32, cancellationToken: cancel.Token));
    }

    // Keys from a Python writer that reproduces PuTTY's own four vectors byte for byte; the expected public keys are
    // what Python's cryptography package wrote for the same keys (OpenSSH format).
    private const string EcdsaP256V3 = """
        PuTTY-User-Key-File-3: ecdsa-sha2-nistp256
        Encryption: aes256-cbc
        Comment: p256 test key
        Public-Lines: 3
        AAAAE2VjZHNhLXNoYTItbmlzdHAyNTYAAAAIbmlzdHAyNTYAAABBBFyr38HwDJLT
        o4qnow8tfC5922P3BeuvJaLmCEPxOC1JlLCKzSpBvRdQTbMmtOmYJyjjovL3Av38
        4Ik0cmoSIsI=
        Key-Derivation: Argon2id
        Argon2-Memory: 8192
        Argon2-Passes: 3
        Argon2-Parallelism: 1
        Argon2-Salt: df669123906f1ab4765f8738e439ea72
        Private-Lines: 1
        +Io65gELgZj7BxwJAVWyCYfzjM6WCuX0Vgs3Qt/vlqZ0eQa+yFx55m/xWvtx50mC
        Private-MAC: 0e5a17f09e84d29c0b1d5c54ea35c2b40931eb7e3712ab755738f285a57263ce
        """;

    private const string EcdsaP256Public = "AAAAE2VjZHNhLXNoYTItbmlzdHAyNTYAAAAIbmlzdHAyNTYAAABBBFyr38HwDJLTo4qnow8tfC5922P3BeuvJaLmCEPxOC1JlLCKzSpBvRdQTbMmtOmYJyjjovL3Av384Ik0cmoSIsI=";

    private const string Rsa2048V2 = """
        PuTTY-User-Key-File-2: ssh-rsa
        Encryption: none
        Comment: rsa2048 test key
        Public-Lines: 6
        AAAAB3NzaC1yc2EAAAADAQABAAABAQDXBXo3+662vvWLWjFOMVbqHzVbTgUcYMeC
        zZq2vapXshO82COfV9cGDqHpJvQ1gdAKrpN/qzgR8kLIyA/+/X1/0kH6DvMUMWje
        qRw9W6HmvZqge4qZ7zvFgcpxzdtGjPmZA8GoHVITKc5gK5GpjhjAoqjDw9oG/0o/
        bHhXmUy5e/gC+dP6z6MCixvgoEiEY7E+QcUCZN5RxZ9DMc5wVlljWtPdKrAu9ciE
        81H00Xqj2RJwDwE9EWhxg4JV9nDO7BehxwtVunrVdip/RDfGL7fj9OTO7tY3jwLP
        AtSgidGx1HA8R6qG55kLlP2fdY/5qbJz3rYMEyt9NYqnTTPz+8nF
        Private-Lines: 14
        AAABAA2Aqw2j8PNJ55qCJfF+mLIoFo4uxpRjIXQ93BbYrz9FkAa+XfJOLlMTjSha
        iWBoz/szvlB4nJNH7tiz84k0OvLRbdI5qRseBTNiVEMoI3Ede/3S9D+5bbv37BIc
        dsYl6TCqMzmO6c4JRIv2gHWw1i4WqNluTZ0939g+xAngNxi4qj4jh+bVbMTVHSJP
        AP5LhtEvYqc6DRL5nueHo0avbu95b2r/bpaUL/MG3SdTBpEDNm44KWhGuYgpIHjn
        RpeemKDQ8soMZSS2Hcv/JYYdDhtHUb25PyStcMKPvYHIfLvLnrBuoqY+nzd6FUuJ
        JS1HlVI5B3xi6eSRpTNnaTm9GtsAAACBAPGfKcA5oTVA4oqDLKWbHlf6JneTEqVt
        75GEgy4DajmZ2cvRKT3BuISQ/mHwOkXldkIrkIllE/RA7UKf5lfwos85f9y2IBxs
        gNSRooHpCx4Ju88kWfdI8ELmOJJ19ZpVIqgYQJALKWh3NdyaYwmpJshdpwiPgfjp
        ljh1H64/wM4DAAAAgQDj0RaFQQVmHtjfCAmHZgT96BA9gPF5/BVifP4Cx+HqTCvZ
        lbQHZ3/aWTx97KGJ5f4T8xgydYtSW6c45eHZTW7Otf88npilcK/Cl2O5FQ8wbV7v
        OL/MvgCpk/mfWKQrpnI908O7Sa7b4gO0F+yBP+MDWobRscnw8+FlWgslhwzClwAA
        AIBcV/eSjZjdg9RmOchAu2QWEdrvfF16nfkgtz435pcaCeAj/iCRtEytuWGB+YbX
        DgXbM7kG+lFsxhKRKGbzzkkGvvKAzdaDPKIZZeNtPwoULbrvlA1wvsTg15xVzisJ
        4quPEtuUayRLp+k6QX1q+osNmv4cLqUR4gCa8Od1pjdraQ==
        Private-MAC: 7907b8a516ca3ff8bc005570077d9c5ae9e3171f
        """;

    private const string Rsa2048Public = "AAAAB3NzaC1yc2EAAAADAQABAAABAQDXBXo3+662vvWLWjFOMVbqHzVbTgUcYMeCzZq2vapXshO82COfV9cGDqHpJvQ1gdAKrpN/qzgR8kLIyA/+/X1/0kH6DvMUMWjeqRw9W6HmvZqge4qZ7zvFgcpxzdtGjPmZA8GoHVITKc5gK5GpjhjAoqjDw9oG/0o/bHhXmUy5e/gC+dP6z6MCixvgoEiEY7E+QcUCZN5RxZ9DMc5wVlljWtPdKrAu9ciE81H00Xqj2RJwDwE9EWhxg4JV9nDO7BehxwtVunrVdip/RDfGL7fj9OTO7tY3jwLPAtSgidGx1HA8R6qG55kLlP2fdY/5qbJz3rYMEyt9NYqnTTPz+8nF";

    // Ed25519 seed 01..1f 00: PuTTY drops the last (high) zero byte, so the stored string is 31 bytes.
    private const string Ed25519ZeroTail = """
        PuTTY-User-Key-File-3: ssh-ed25519
        Encryption: none
        Comment: zero tail
        Public-Lines: 2
        AAAAC3NzaC1lZDI1NTE5AAAAINctxmemDGBZtcOnmpCZGvJSIV2/IBBhdi0QdxOT
        59ZR
        Private-Lines: 1
        AAAAHwECAwQFBgcICQoLDA0ODxAREhMUFRYXGBkaGxwdHh8=
        Private-MAC: cfb5bebef35632f86eecd66ce4be12ba39edc7d4cdf1fb1368c2611a505d05e1
        """;

    // An encrypted key whose comment ends in a space: the MAC covers the comment as written (passphrase "pp").
    private const string TrailingSpaceComment = "PuTTY-User-Key-File-3: ssh-ed25519\nEncryption: aes256-cbc\nComment: my key \nPublic-Lines: 2\n"
        + "AAAAC3NzaC1lZDI1NTE5AAAAINctxmemDGBZtcOnmpCZGvJSIV2/IBBhdi0QdxOT\n59ZR\nKey-Derivation: Argon2id\nArgon2-Memory: 8192\n"
        + "Argon2-Passes: 13\nArgon2-Parallelism: 1\nArgon2-Salt: f1e57118e2f3633c4d519a9cb6777c73\nPrivate-Lines: 1\n"
        + "+sFGHlpkuz6SOP6cNZbJ7Xaetydw372sBqjcfS/TNNyjGwPn2nfGdh85Sv2HndIr\n"
        + "Private-MAC: 7a785809beda85d566cc952c87762eff4293d33c113bb69443e60dd868504b14\n";

    [Theory]
    [InlineData(EcdsaP256V3, "pass phrase ü", "ECDSA", EcdsaP256Public)]
    [InlineData(Rsa2048V2, null, "RSA", Rsa2048Public)]
    public void RsaAndEcdsaKeysKeepTheirPublicKey(string ppk, string? passphrase, string type, string publicKey)
    {
        var result = PuttyKeyFile.ToOpenSsh(ppk, passphrase);
        Assert.True(result.Ok, result.Problem);
        Assert.Equal(type, result.KeyType);
        Assert.Equal(publicKey, Convert.ToBase64String(OpenSshPublicBlob(result.OpenSshKey!)));
    }

    [Fact]
    public void AnEd25519SeedEndingInZeroIsPaddedBack()
    {
        var result = PuttyKeyFile.ToOpenSsh(Ed25519ZeroTail, null);
        Assert.True(result.Ok, result.Problem);
        var (publicKey, secret, _) = ReadOpenSshEd25519(result.OpenSshKey!);
        Assert.Equal("d72dc667a60c6059b5c3a79a90991af252215dbf201061762d10771393e7d651", Convert.ToHexString(publicKey).ToLowerInvariant());
        Assert.Equal("0102030405060708090a0b0c0d0e0f101112131415161718191a1b1c1d1e1f00" + Convert.ToHexString(publicKey).ToLowerInvariant(),
            Convert.ToHexString(secret).ToLowerInvariant());
    }

    [Fact]
    public void ACommentIsReadAsWritten()
    {
        var result = PuttyKeyFile.ToOpenSsh(TrailingSpaceComment, "pp");
        Assert.True(result.Ok, result.Problem);
        Assert.Equal("my key ", ReadOpenSshEd25519(result.OpenSshKey!).Comment);
    }

    [Fact]
    public void AnEmptyCommentWithoutItsSpaceIsRead()
    {
        // V3Clear with an empty comment: the MAC is HMAC-SHA256 under an empty key.
        var text = V3Clear.Replace("Comment: ed25519-key-20200105", "Comment:");
        var lines = text.Replace("\r", "").Split('\n');
        static byte[] S(byte[] b) => [.. BitConverter.GetBytes((uint)b.Length).Reverse(), .. b];
        var data = S("ssh-ed25519"u8.ToArray()).Concat(S("none"u8.ToArray())).Concat(S([]))
            .Concat(S(Convert.FromBase64String(lines[4] + lines[5]))).Concat(S(Convert.FromBase64String(lines[7]))).ToArray();
        var mac = Convert.ToHexString(System.Security.Cryptography.HMACSHA256.HashData(Array.Empty<byte>(), data)).ToLowerInvariant();
        text = string.Join('\n', lines[..^1]) + "\nPrivate-MAC: " + mac;
        var result = PuttyKeyFile.ToOpenSsh(text, null);
        Assert.True(result.Ok, result.Problem);
        Assert.Equal("", ReadOpenSshEd25519(result.OpenSshKey!).Comment);
    }

    [Theory]
    [InlineData("Argon2-Memory: 8192", "Argon2-Memory: 65536", "Argon2-Passes: 13", "Argon2-Passes: 100")]
    [InlineData("Argon2-Memory: 8192", "Argon2-Memory: 131072", "Argon2-Passes: 13", "Argon2-Passes: 1")]
    [InlineData("Argon2-Parallelism: 1", "Argon2-Parallelism: 17", "Argon2-Passes: 13", "Argon2-Passes: 13")]
    public void TooMuchWorkIsRefused(string memoryFrom, string memoryTo, string passesFrom, string passesTo)
    {
        var result = PuttyKeyFile.ToOpenSsh(V3Encrypted.Replace(memoryFrom, memoryTo).Replace(passesFrom, passesTo), Passphrase);
        Assert.Contains("more work", result.Problem);
    }

    [Fact]
    public void AnOversizedFileOrLineIsDamaged()
    {
        Assert.Equal(PuttyKeyFile.Damaged, PuttyKeyFile.ToOpenSsh(V3Clear + new string(' ', 70_000), null).Problem);
        // ReplaceLineEndings: a CRLF checkout (core.autocrlf) gives the raw string \r\n, and the replace would match nothing.
        var longLine = V3Clear.ReplaceLineEndings("\n").Replace("Private-Lines: 1\nAAAAIGvv", "Private-Lines: 1\n" + new string('A', 200) + "AAAAIGvv");
        Assert.Equal(PuttyKeyFile.Damaged, PuttyKeyFile.ToOpenSsh(longLine, null).Problem);
    }

    [Theory]
    [InlineData("-----BEGIN PRIVATE KEY-----\nMC4C\n-----END PRIVATE KEY-----", true)]
    [InlineData("-----BEGIN ENCRYPTED PRIVATE KEY-----\nMIIF\n-----END ENCRYPTED PRIVATE KEY-----", false)]
    [InlineData("-----BEGIN RSA PRIVATE KEY-----\nMIIE\n-----END RSA PRIVATE KEY-----", true)]
    [InlineData("-----BEGIN RSA PRIVATE KEY-----\nProc-Type: 4,ENCRYPTED\nMIIE\n-----END RSA PRIVATE KEY-----", false)]
    [InlineData("-----BEGIN OPENSSH PRIVATE KEY-----\nb3BlbnNzaC1rZXktdjEAAAAACmFlczI1Ni1jdHI=\n-----END OPENSSH PRIVATE KEY-----", false)]
    public void PemKeysSayWhetherTheyTakeAPassphrase(string key, bool unencrypted) => Assert.Equal(unencrypted, SiteKeyText.IsUnencrypted(key));

    [Fact]
    public void AConvertedKeyTakesNoPassphrase() =>
        Assert.True(SiteKeyText.IsUnencrypted(PuttyKeyFile.ToOpenSsh(V3Encrypted, Passphrase).OpenSshKey!));

    private static byte[] OpenSshPublicBlob(string pem)
    {
        var lines = pem.Trim().Split('\n');
        var data = Convert.FromBase64String(string.Concat(lines[1..^1]));
        int at = 15;
        int Len() { int l = (data[at] << 24) | (data[at + 1] << 16) | (data[at + 2] << 8) | data[at + 3]; at += 4; return l; }
        for (int i = 0; i < 3; i++) { int skip = Len(); at += skip; }
        at += 4;
        int length = Len();
        return data.AsSpan(at, length).ToArray();
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

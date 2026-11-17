using System.Buffers.Binary;
using System.Security.Cryptography;
using System.Text;
using DownloadManager.Sites;
using static DownloadManager.Tests.Shared.PpkWriter;

namespace DownloadManager.Sites.Tests;

/// <summary>
/// SITES.14: PuTTY keys (.ppk) become OpenSSH text. The .ppk files are written here from keys made for the test (format 2
/// with and without a passphrase, format 3 without), following PuTTY's file format; no real key is in the repository. The
/// OpenSSH text is read back and its private part used to sign, checked against the original public key.
/// </summary>
public sealed class PuttyKeyTests
{
    [Theory]
    [InlineData(2, null)]
    [InlineData(2, "correct horse")]
    [InlineData(3, null)]
    public void AnRsaKeyBecomesAnOpenSshKeyThatSignsForTheSamePublicKey(int version, string? passphrase)
    {
        using var rsa = RSA.Create(2048);
        var p = rsa.ExportParameters(true);
        var publicBlob = Blob(W("ssh-rsa"), Mp(p.Exponent!), Mp(p.Modulus!));
        var privateBlob = Blob(Mp(p.D!), Mp(p.P!), Mp(p.Q!), Mp(p.InverseQ!));
        var ppk = Ppk(version, "ssh-rsa", "test rsa", publicBlob, privateBlob, passphrase);

        var (text, problem) = PuttyKey.ToOpenSsh(ppk, passphrase);

        Assert.Null(problem);
        Assert.Null(SiteKeyText.Problem(text!));
        var (type, pub, fields) = ReadOpenSsh(text!);
        Assert.Equal("ssh-rsa", type);
        Assert.Equal(publicBlob, pub);
        // n, e, d, iqmp, p, q, comment
        using var back = RSA.Create();
        back.ImportParameters(new RSAParameters
        {
            Modulus = Unsigned(fields[0]), Exponent = Unsigned(fields[1]), D = Unsigned(fields[2], p.D!.Length),
            InverseQ = Unsigned(fields[3], p.P!.Length), P = Unsigned(fields[4], p.P.Length), Q = Unsigned(fields[5], p.P.Length),
            DP = p.DP, DQ = p.DQ,
        });
        var data = "sign me"u8.ToArray();
        Assert.True(rsa.VerifyData(data, back.SignData(data, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1),
            HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1));
        Assert.Equal("test rsa", Encoding.UTF8.GetString(fields[6]));
    }

    [Fact]
    public void AnEcdsaKeyKeepsItsCurvePointAndPrivateValue()
    {
        using var ec = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        var p = ec.ExportParameters(true);
        var point = (byte[])[4, .. p.Q.X!, .. p.Q.Y!];
        var publicBlob = Blob(W("ecdsa-sha2-nistp256"), W("nistp256"), S(point));
        var ppk = Ppk(2, "ecdsa-sha2-nistp256", "ec", publicBlob, Blob(Mp(p.D!)), "pass");

        var (text, problem) = PuttyKey.ToOpenSsh(ppk, "pass");

        Assert.Null(problem);
        var (type, pub, fields) = ReadOpenSsh(text!);
        Assert.Equal("ecdsa-sha2-nistp256", type);
        Assert.Equal(publicBlob, pub);
        Assert.Equal("nistp256", Encoding.ASCII.GetString(fields[0]));
        Assert.Equal(point, fields[1]);
        using var back = ECDsa.Create(new ECParameters { Curve = ECCurve.NamedCurves.nistP256, Q = p.Q, D = Unsigned(fields[2], 32) });
        var data = "sign me"u8.ToArray();
        Assert.True(ec.VerifyData(data, back.SignData(data, HashAlgorithmName.SHA256), HashAlgorithmName.SHA256));
    }

    [Theory]
    [InlineData(2)]
    [InlineData(3)]
    public void AnEd25519KeyHoldsTheSeedAndThePublicKeyAsOpenSshDoes(int version)
    {
        var publicBlob = Blob(W("ssh-ed25519"), S(EdPublic));
        var ppk = Ppk(version, "ssh-ed25519", "ed", publicBlob, Blob(S(EdSeed)), null);

        var (text, problem) = PuttyKey.ToOpenSsh(ppk, null);

        Assert.Null(problem);
        var (type, pub, fields) = ReadOpenSsh(text!);
        Assert.Equal("ssh-ed25519", type);
        Assert.Equal(publicBlob, pub);
        Assert.Equal(EdPublic, fields[0]);
        Assert.Equal([.. EdSeed, .. EdPublic], fields[1]);
    }

    [Fact]
    public void AWrongOrMissingPassphraseIsRefusedAndAChangedFileFailsItsCheck()
    {
        var publicBlob = Blob(W("ssh-ed25519"), S(EdPublic));
        var ppk = Ppk(2, "ssh-ed25519", "ed", publicBlob, Blob(S(EdSeed)), "right");

        Assert.Contains("passphrase is wrong", PuttyKey.ToOpenSsh(ppk, "wrong").Problem);
        Assert.Contains("has a passphrase", PuttyKey.ToOpenSsh(ppk, null).Problem);
        Assert.True(PuttyKey.IsEncrypted(ppk));

        var plain = Ppk(2, "ssh-ed25519", "ed", publicBlob, Blob(S(EdSeed)), null);
        var changed = plain.Replace("Comment: ed", "Comment: eve", StringComparison.Ordinal);
        Assert.Contains("damaged", PuttyKey.ToOpenSsh(changed, null).Problem);
        Assert.False(PuttyKey.IsEncrypted(plain));
    }

    [Fact]
    public void Format3WithAPassphraseDsaAndFormat1AreRefusedWithThePuttygenSteps()
    {
        var v3 = "PuTTY-User-Key-File-3: ssh-ed25519\nEncryption: aes256-cbc\nComment: x\nPublic-Lines: 1\nAAAA\n" +
                 "Key-Derivation: Argon2id\nArgon2-Memory: 8192\nArgon2-Passes: 13\nArgon2-Parallelism: 1\nArgon2-Salt: 00\n" +
                 "Private-Lines: 1\nAAAA\nPrivate-MAC: 00\n";
        Assert.Contains("Export OpenSSH key", PuttyKey.ToOpenSsh(v3, "x").Problem);
        var dsa = Ppk(2, "ssh-dss", "d", Blob(W("ssh-dss")), Blob(S([1])), null);
        Assert.Contains("DSA", PuttyKey.ToOpenSsh(dsa, null).Problem);
        Assert.Contains("too old", PuttyKey.ToOpenSsh("PuTTY-User-Key-File-1: ssh-rsa\n", null).Problem);
        Assert.Contains("PuTTY", PuttyKey.ToOpenSsh("PuTTY-User-Key-File-2: ssh-rsa", null).Problem);
    }

    /// <summary>The key's type, its public blob, and the private fields after the type (the comment last).</summary>
    private static (string Type, byte[] Public, List<byte[]> Fields) ReadOpenSsh(string text)
    {
        var body = Convert.FromBase64String(string.Concat(text.Split('\n').Where(l => l.Length > 0 && !l.StartsWith("-----", StringComparison.Ordinal))));
        Assert.Equal("openssh-key-v1\0"u8.ToArray(), body[..15]);
        var at = 15;
        Assert.Equal("none", Encoding.ASCII.GetString(Read(body, ref at)));
        Assert.Equal("none", Encoding.ASCII.GetString(Read(body, ref at)));
        Assert.Empty(Read(body, ref at));
        Assert.Equal(1u, BinaryPrimitives.ReadUInt32BigEndian(body.AsSpan(at, 4)));
        at += 4;
        var pub = Read(body, ref at);
        var section = Read(body, ref at);
        Assert.Equal(0, section.Length % 8);
        Assert.Equal(section[..4], section[4..8]);
        var inner = 8;
        var type = Encoding.ASCII.GetString(Read(section, ref inner));
        var count = type == "ssh-rsa" ? 7 : type == "ssh-ed25519" ? 3 : 4;
        var fields = Enumerable.Range(0, count).Select(_ => Read(section, ref inner)).ToList();
        for (byte pad = 1; inner < section.Length; pad++) Assert.Equal(pad, section[inner++]);
        return (type, pub, fields);
    }

    private static byte[] Read(byte[] data, ref int at)
    {
        var length = (int)BinaryPrimitives.ReadUInt32BigEndian(data.AsSpan(at, 4));
        var value = data.AsSpan(at + 4, length).ToArray();
        at += 4 + length;
        return value;
    }

    private static byte[] Unsigned(byte[] mpint, int length = 0)
    {
        var trimmed = mpint.SkipWhile(b => b == 0).ToArray();
        return length > trimmed.Length ? [.. new byte[length - trimmed.Length], .. trimmed] : trimmed;
    }
}

using System.Buffers.Binary;
using System.Security.Cryptography;
using System.Text;
using DownloadManager.Sites;
using static DownloadManager.Tests.Shared.PpkWriter;

namespace DownloadManager.Sites.Tests;

/// <summary>
/// SITES.11/SITES.14: PuTTY keys (.ppk) become OpenSSH text through <see cref="PuttyKeyFile"/> (main's PuttyKey, merged away
/// in favour of it, brought these tests). The .ppk files are written here from keys made for the test (format 2
/// with and without a passphrase, format 3 without), following PuTTY's file format; no real key is in the repository. The
/// OpenSSH text is read back and its private part used to sign, checked against the original public key.
/// </summary>
public sealed class PuttyKeyFileGeneratedKeyTests
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

        var (text, problem) = Open(ppk, passphrase);

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

        var (text, problem) = Open(ppk, "pass");

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

        var (text, problem) = Open(ppk, null);

        Assert.Null(problem);
        var (type, pub, fields) = ReadOpenSsh(text!);
        Assert.Equal("ssh-ed25519", type);
        Assert.Equal(publicBlob, pub);
        Assert.Equal(EdPublic, fields[0]);
        Assert.Equal([.. EdSeed, .. EdPublic], fields[1]);
    }

    [Theory]
    [InlineData("", null)]
    [InlineData("", "pass")]
    [InlineData("trailing ", null)]
    [InlineData(" both ", "pass")]
    public void AnEmptyCommentOrOneWithEdgeSpacesIsReadAsWritten(string comment, string? passphrase)
    {
        var publicBlob = Blob(W("ssh-ed25519"), S(EdPublic));
        var ppk = Ppk(2, "ssh-ed25519", comment, publicBlob, Blob(S(EdSeed)), passphrase);

        var (text, problem) = Open(ppk, passphrase);

        Assert.Null(problem);
        var (_, pub, fields) = ReadOpenSsh(text!);
        Assert.Equal(publicBlob, pub);
        Assert.Equal(comment, Encoding.UTF8.GetString(fields[2]));
    }

    [Fact]
    public void AnEmptyCommentWhoseTrailingSpaceWasStrippedIsStillRead()
    {
        var publicBlob = Blob(W("ssh-ed25519"), S(EdPublic));
        var ppk = Ppk(3, "ssh-ed25519", "", publicBlob, Blob(S(EdSeed)), null).Replace("Comment: \r\n", "Comment:\r\n", StringComparison.Ordinal);

        Assert.Null(Open(ppk, null).Problem);
    }

    [Fact]
    public void AWrongOrMissingPassphraseIsRefusedAndAChangedFileFailsItsCheck()
    {
        var publicBlob = Blob(W("ssh-ed25519"), S(EdPublic));
        var ppk = Ppk(2, "ssh-ed25519", "ed", publicBlob, Blob(S(EdSeed)), "right");

        Assert.Equal(PuttyKeyFile.WrongPassphrase, Open(ppk, "wrong").Problem);
        Assert.Equal(PuttyKeyFile.NeedsPassphrase, Open(ppk, null).Problem);

        var plain = Ppk(2, "ssh-ed25519", "ed", publicBlob, Blob(S(EdSeed)), null);
        var changed = plain.Replace("Comment: ed", "Comment: eve", StringComparison.Ordinal);
        Assert.Equal(PuttyKeyFile.Damaged, Open(changed, null).Problem);
    }

    [Fact]
    public void DsaAndFormat1AreRefused()
    {
        var dsa = Ppk(2, "ssh-dss", "d", Blob(W("ssh-dss")), Blob(S([1])), null);
        Assert.NotNull(Open(dsa, null).Problem);
        Assert.NotNull(Open("PuTTY-User-Key-File-1: ssh-rsa\n", null).Problem);
        Assert.NotNull(Open("PuTTY-User-Key-File-2: ssh-rsa", null).Problem);
    }

    private static (string? Text, string? Problem) Open(string ppk, string? passphrase)
    {
        var result = PuttyKeyFile.ToOpenSsh(ppk, passphrase);
        return (result.OpenSshKey, result.Problem);
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

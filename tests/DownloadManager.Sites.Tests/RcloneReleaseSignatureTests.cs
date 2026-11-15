using System.Buffers.Binary;
using System.Security.Cryptography;
using System.Text;
using DownloadManager.Sites;

namespace DownloadManager.Sites.Tests;

public sealed class RcloneReleaseSignatureTests
{
    private static readonly string Real = File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "Fixtures", "rclone-v1.76.0-SHA256SUMS"));

    [Fact]
    public void Rclones_own_list_for_1_76_0_verifies_and_holds_the_zip_hashes()
    {
        var sums = RcloneReleaseSignature.ParseSums(RcloneReleaseSignature.Verify(Real));
        Assert.Equal("b00f00b3266c3764413afe599d3ec316f6f43b3e0ea8a0bc1d186d2133e46df7", sums["rclone-v1.76.0-linux-amd64.zip"]);
        Assert.Equal("8b4fd2737ade4e70c057ef4a49deb7cffe94b0fb9d7587c189d149823f21a1dc", sums["rclone-v1.76.0-windows-amd64.zip"]);
    }

    [Fact]
    public void Windows_line_ends_and_trailing_spaces_do_not_change_the_signed_text()
    {
        var changed = string.Join("\r\n", Real.Split('\n').Select(line => line.Length > 70 && !line.StartsWith('-') ? line + "  \t" : line));
        RcloneReleaseSignature.Verify(changed);
    }

    [Fact]
    public void A_changed_hash_is_refused()
    {
        var tampered = Real.Replace("b00f00b3266c", "b00f00b3266d", StringComparison.Ordinal);
        Assert.NotEqual(Real, tampered);
        Assert.Throws<RcloneReleaseException>(() => RcloneReleaseSignature.Verify(tampered));
    }

    [Fact]
    public void A_removed_or_added_line_is_refused()
    {
        var lines = Real.Split('\n').ToList();
        var index = lines.FindIndex(line => line.Contains("linux-amd64.zip", StringComparison.Ordinal));
        var without = lines.ToList();
        without.RemoveAt(index);
        Assert.Throws<RcloneReleaseException>(() => RcloneReleaseSignature.Verify(string.Join('\n', without)));
        var with = lines.ToList();
        with.Insert(index, new string('0', 64) + "  rclone-v1.76.0-linux-amd64.zip");
        Assert.Throws<RcloneReleaseException>(() => RcloneReleaseSignature.Verify(string.Join('\n', with)));
    }

    [Fact]
    public void A_changed_signature_is_refused()
    {
        var lines = Real.Split('\n');
        var at = Array.FindIndex(lines, line => line.StartsWith("-----BEGIN PGP SIGNATURE", StringComparison.Ordinal)) + 3;
        var chars = lines[at].ToCharArray();
        chars[10] = chars[10] == 'A' ? 'B' : 'A';
        lines[at] = new string(chars);
        Assert.Throws<RcloneReleaseException>(() => RcloneReleaseSignature.Verify(string.Join('\n', lines)));
    }

    [Fact]
    public void Text_that_is_not_signed_is_refused() =>
        Assert.Throws<RcloneReleaseException>(() => RcloneReleaseSignature.Verify(new string('a', 64) + "  rclone.zip\n"));

    [Fact]
    public void A_damaged_built_in_key_is_refused()
    {
        var body = new byte[] { 4, 0, 0, 0, 0, 17, 0, 8, 1 };
        Assert.Throws<RcloneReleaseException>(() => RcloneReleaseSignature.ParseKey(body, RcloneReleaseSignature.KeyFingerprint));
    }

    // A key of our own, for what rclone's file does not show: dash-escaping, SHA-256, another issuer, critical parts.
    [Fact]
    public void Dash_escaped_lines_are_read_back_without_their_escape()
    {
        using var key = new TestSigningKey();
        var signed = key.Sign("first\n-----not a header\n- dash", SHA256Digest);
        Assert.Contains("- -----not a header", signed);
        Assert.Equal("first\n-----not a header\n- dash", key.Verify(signed));
    }

    [Fact]
    public void Another_keys_signature_is_refused()
    {
        using var key = new TestSigningKey();
        using var other = new TestSigningKey();
        var signed = other.Sign("text", SHA256Digest);
        // The issuer named in the signature is the other key's.
        Assert.Throws<RcloneReleaseException>(() => key.Verify(signed));
    }

    [Fact]
    public void A_signature_that_claims_the_right_key_but_was_made_by_another_is_refused()
    {
        using var key = new TestSigningKey();
        using var other = new TestSigningKey();
        var signed = other.Sign("text", SHA256Digest, issuer: key.Fingerprint);
        Assert.Throws<RcloneReleaseException>(() => key.Verify(signed));
    }

    [Fact]
    public void An_unknown_critical_part_is_refused()
    {
        using var key = new TestSigningKey();
        var signed = key.Sign("text", SHA256Digest, extraHashed: [0x80 | 30, 1]);
        Assert.Throws<RcloneReleaseException>(() => key.Verify(signed));
        // Not critical: ignored.
        Assert.Equal("text", key.Verify(key.Sign("text", SHA256Digest, extraHashed: [30, 1])));
    }

    [Fact]
    public void A_list_line_that_is_not_hash_and_name_is_refused()
    {
        Assert.Throws<RcloneReleaseException>(() => RcloneReleaseSignature.ParseSums("abc  file.zip"));
        Assert.Throws<RcloneReleaseException>(() => RcloneReleaseSignature.ParseSums(new string('a', 64) + "  a.zip\n" + new string('b', 64) + "  a.zip"));
    }

    private const byte SHA256Digest = TestSigningKey.Sha256Digest;
}

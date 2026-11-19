using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using DownloadManager.Mobile.Updates;

namespace DownloadManager.Mobile.Tests;

public sealed class AndroidReleaseNotesTests : IDisposable
{
    private readonly RSA _key = RSA.Create(2048);
    private readonly RSA _otherKey = RSA.Create(2048);
    private string PublicKey => _key.ExportSubjectPublicKeyInfoPem();
    public void Dispose() { _key.Dispose(); _otherKey.Dispose(); }

    private static AndroidUpdateManifest Offer(long code = 4) => new()
    {
        VersionCode = code,
        VersionName = $"0.0.{code}",
        ApkUrl = $"https://github.com/Kaan88/inbound/releases/download/android-v0.0.{code}/inbound.apk",
        Size = 123456,
        Sha256 = new string('a', 64),
    };
    private static string Sign(RSA key, string text) => Convert.ToBase64String(key.SignData(
        Encoding.UTF8.GetBytes(text), HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1));
    private static string LegacyCanonical(AndroidUpdateManifest m) => FormattableString.Invariant(
        $"inbound-android-update-v1\n{m.VersionCode}\n{m.VersionName}\n{m.ApkUrl}\n{m.Size}\n{m.Sha256.ToLowerInvariant()}");
    private static string NotesCanonical(AndroidUpdateManifest m) =>
        "inbound-android-release-notes-v1\n" + LegacyCanonical(m) + "\n" + m.ReleaseNotes;
    private void SignBase(AndroidUpdateManifest m) => m.Signature = Sign(_key, LegacyCanonical(m));
    private void SignNotes(AndroidUpdateManifest m, RSA? key = null) => m.ReleaseNotesSignature = Sign(key ?? _key, NotesCanonical(m));
    private AndroidUpdateManifest Signed(string? notes = null)
    {
        var m = Offer(); SignBase(m);
        if (notes is not null) { m.ReleaseNotes = notes; SignNotes(m); }
        return m;
    }

    [Fact]
    public void Legacy_offers_without_notes_keep_the_original_base_canonical()
    {
        var m = Signed();
        var expected = LegacyCanonical(m);
        Assert.Equal(expected, AndroidUpdateTrust.Canonical(m));
        AndroidUpdateTrust.Validate(m, PublicKey);
        m.ReleaseNotes = "• Queue rules\n• Faster açılış"; SignNotes(m);
        Assert.Equal(expected, AndroidUpdateTrust.Canonical(m));
        Assert.True(_key.VerifyData(Encoding.UTF8.GetBytes(expected), Convert.FromBase64String(m.Signature),
            HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1));
        AndroidUpdateTrust.Validate(m, PublicKey);
    }

    [Fact]
    public void Notes_canonicalization_is_domain_separated_and_preserves_unicode_and_line_endings()
    {
        var m = Signed("• Güncelleme\r\n• 日本語\n\tMore details");
        Assert.Equal(NotesCanonical(m), AndroidUpdateTrust.ReleaseNotesCanonical(m));
        AndroidUpdateTrust.Validate(m, PublicKey);
        m.ReleaseNotes = m.ReleaseNotes!.Replace("\r\n", "\n");
        Assert.Throws<InvalidDataException>(() => AndroidUpdateTrust.Validate(m, PublicKey));
    }

    [Fact]
    public void Altered_text_and_a_notes_signature_from_another_key_are_rejected()
    {
        var m = Signed("• Original");
        m.ReleaseNotes = "• Altered";
        Assert.Throws<InvalidDataException>(() => AndroidUpdateTrust.Validate(m, PublicKey));
        SignNotes(m, _otherKey);
        Assert.Throws<InvalidDataException>(() => AndroidUpdateTrust.Validate(m, PublicKey));
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void Removing_only_one_of_the_notes_fields_is_rejected(bool removeText)
    {
        var m = Signed("• A note");
        if (removeText) m.ReleaseNotes = null; else m.ReleaseNotesSignature = null;
        Assert.Throws<InvalidDataException>(() => AndroidUpdateTrust.Validate(m, PublicKey));
    }

    [Theory]
    [InlineData("versionCode")]
    [InlineData("versionName")]
    [InlineData("apkUrl")]
    [InlineData("size")]
    [InlineData("sha256")]
    public void Notes_cannot_be_moved_to_a_different_valid_signed_offer(string field)
    {
        var m = Signed("• Same text");
        switch (field)
        {
            case "versionCode": m.VersionCode++; break;
            case "versionName": m.VersionName = "0.0.5"; break;
            case "apkUrl": m.ApkUrl = "https://github.com/Kaan88/inbound/releases/download/android-v0.0.5/new.apk"; break;
            case "size": m.Size++; break;
            case "sha256": m.Sha256 = new string('b', 64); break;
        }
        SignBase(m); // The new offer has a valid base signature; only the old notes signature is stale.
        Assert.True(_key.VerifyData(Encoding.UTF8.GetBytes(LegacyCanonical(m)), Convert.FromBase64String(m.Signature),
            HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1));
        Assert.Throws<InvalidDataException>(() => AndroidUpdateTrust.Validate(m, PublicKey));
    }

    [Fact]
    public void Notes_length_limit_accepts_exact_boundary_and_refuses_the_next_character()
    {
        Assert.Equal(8192, AndroidUpdateTrust.MaximumReleaseNotesCharacters);
        var m = Signed(new string('a', AndroidUpdateTrust.MaximumReleaseNotesCharacters));
        AndroidUpdateTrust.Validate(m, PublicKey);
        m.ReleaseNotes += "b"; SignNotes(m);
        Assert.Throws<InvalidDataException>(() => AndroidUpdateTrust.Validate(m, PublicKey));
    }

    [Theory]
    [InlineData("")]
    [InlineData(" \t\r\n")]
    [InlineData("ok\u0000no")]
    [InlineData("ok\u0001no")]
    [InlineData("ok\u007fno")]
    [InlineData("ok\u0085no")]
    public void Blank_or_illegal_control_characters_are_refused_even_when_signed(string notes)
    {
        var m = Signed(notes);
        Assert.Throws<InvalidDataException>(() => AndroidUpdateTrust.Validate(m, PublicKey));
    }

    [Theory]
    [InlineData("")]
    [InlineData(" ")]
    [InlineData("not-base64!")]
    public void Blank_or_malformed_notes_signature_is_refused(string signature)
    {
        var m = Signed("• Note"); m.ReleaseNotesSignature = signature;
        Assert.Throws<InvalidDataException>(() => AndroidUpdateTrust.Validate(m, PublicKey));
    }

    [Fact]
    public void Json_round_trip_preserves_both_signed_notes_fields_exactly()
    {
        var m = Signed("• Güvenlik\r\n• 日本語\n\tDone");
        var bytes = JsonSerializer.SerializeToUtf8Bytes(m, new JsonSerializerOptions(JsonSerializerDefaults.Web));
        var parsed = AndroidUpdateTrust.Parse(bytes);
        Assert.Equal(m.ReleaseNotes, parsed.ReleaseNotes);
        Assert.Equal(m.ReleaseNotesSignature, parsed.ReleaseNotesSignature);
        Assert.Equal(m.Signature, parsed.Signature);
        AndroidUpdateTrust.Validate(parsed, PublicKey);
    }
}

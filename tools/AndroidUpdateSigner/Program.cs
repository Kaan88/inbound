using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using DownloadManager.Core;
using DownloadManager.Mobile.Updates;

if (args.Length is not (6 or 7) || !long.TryParse(args[1], NumberStyles.None, CultureInfo.InvariantCulture, out var code))
{
    Console.Error.WriteLine("usage: AndroidUpdateSigner <apk> <version-code> <version-name> <github-apk-url> <update-private-key-file> <new-manifest-path> [release-notes-file]");
    return 2;
}
try
{
    var apk = new FileInfo(args[0]);
    await using var input = apk.OpenRead();
    var manifest = new AndroidUpdateManifest
    {
        VersionCode = code, VersionName = args[2], ApkUrl = args[3], Size = apk.Length,
        Sha256 = Convert.ToHexString(await SHA256.HashDataAsync(input)).ToLowerInvariant(),
    };
    using var key = RSA.Create();
    key.ImportFromPem(await File.ReadAllTextAsync(args[4]));
    using var expected = RSA.Create();
    expected.ImportFromPem(ReleaseUpdates.PublicKey);
    if (!key.ExportSubjectPublicKeyInfo().AsSpan().SequenceEqual(expected.ExportSubjectPublicKeyInfo()))
        throw new InvalidDataException("Wrong update signing key.");
    manifest.Signature = Convert.ToBase64String(key.SignData(Encoding.UTF8.GetBytes(AndroidUpdateTrust.Canonical(manifest)),
        HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1));
    if (args.Length == 7)
    {
        var notesFile = new FileInfo(args[6]);
        if (notesFile.Length > AndroidUpdateTrust.MaximumReleaseNotesCharacters * 4L)
            throw new InvalidDataException("Release notes are too large.");
        manifest.ReleaseNotes = (await File.ReadAllTextAsync(notesFile.FullName)).Replace("\r\n", "\n").Trim();
        manifest.ReleaseNotesSignature = Convert.ToBase64String(key.SignData(Encoding.UTF8.GetBytes(AndroidUpdateTrust.ReleaseNotesCanonical(manifest)),
            HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1));
    }
    AndroidUpdateTrust.Validate(manifest, ReleaseUpdates.PublicKey);
    var bytes = JsonSerializer.SerializeToUtf8Bytes(manifest, ReleaseJson.Default.AndroidUpdateManifest);
    var final = Path.GetFullPath(args[5]);
    var temporary = final + "." + Guid.NewGuid().ToString("N") + ".tmp";
    try
    {
        await using (var output = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None))
        { await output.WriteAsync(bytes); output.Flush(true); }
        File.Move(temporary, final, overwrite: false);
    }
    finally { if (File.Exists(temporary)) File.Delete(temporary); }
    Console.WriteLine($"Signed Android manifest for {manifest.VersionName}, versionCode {manifest.VersionCode}.");
    Console.WriteLine($"APK SHA-256: {manifest.Sha256}");
    return 0;
}
catch (Exception e) when (e is IOException or InvalidDataException or UnauthorizedAccessException or CryptographicException or ArgumentException)
{
    Console.Error.WriteLine("Android update manifest was not produced: check the APK, version, output path and signing key.");
    return 1;
}

[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase, WriteIndented = true)]
[JsonSerializable(typeof(AndroidUpdateManifest))]
internal partial class ReleaseJson : JsonSerializerContext;

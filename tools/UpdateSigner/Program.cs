using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

if (args.Length == 2 && args[0] == "--generate-key")
{
    using var key = RSA.Create(3072);
    // Exclusive creation prevents accidentally replacing the release trust anchor.
    await using var output = new FileStream(args[1], FileMode.CreateNew, FileAccess.Write, FileShare.None);
    await using var writer = new StreamWriter(output);
    await writer.WriteAsync(key.ExportPkcs8PrivateKeyPem());
    Console.WriteLine("Created signing key. Keep it private and reuse it for subsequent releases.");
    return 0;
}

// UPD.N1: --notes-from <changelog.json> puts that version's lines in the manifest as its unsigned "notes".
string? changelogPath = null;
var notesAt = Array.IndexOf(args, "--notes-from");
if (notesAt >= 0)
{
    if (notesAt + 1 >= args.Length)
    {
        Console.Error.WriteLine("--notes-from needs the path of changelog.json.");
        return 2;
    }

    changelogPath = args[notesAt + 1];
    args = args.Where((_, index) => index != notesAt && index != notesAt + 1).ToArray();
}

if (args.Length is < 5 or > 7)
{
    Console.Error.WriteLine("usage: UpdateSigner <output-dir> <version> <installer-url> <package-file> <private-key-file> [channel] [manifest-name] [--notes-from <changelog.json>]");
    Console.Error.WriteLine("       UpdateSigner --generate-key <new-private-key-file>");
    return 2;
}

var outputDir = args[0];
var version = args[1];
var installerUrl = args[2];
var packagePath = args[3];
var privateKeyPath = args[4];
var channel = args.Length > 5 ? args[5] : "stable";
// The AppImage's manifest is manifest-linux-x64.json, beside the Windows manifest.json in the same release.
var manifestName = args.Length > 6 ? args[6] : "manifest.json";
if (manifestName != Path.GetFileName(manifestName) || !manifestName.EndsWith(".json", StringComparison.Ordinal))
{
    Console.Error.WriteLine("manifest-name must be a plain .json file name.");
    return 2;
}
List<DownloadManager.Contracts.ChangelogItem>? notes = null;
if (changelogPath is not null)
{
    IReadOnlyList<DownloadManager.Contracts.ChangelogVersion> changelog;
    try
    {
        changelog = DownloadManager.Contracts.Changelog.Parse(await File.ReadAllTextAsync(changelogPath));
    }
    catch (Exception ex) when (ex is IOException or JsonException or InvalidDataException or UnauthorizedAccessException)
    {
        Console.Error.WriteLine($"{changelogPath} could not be read: {ex.Message}");
        return 2;
    }

    if (DownloadManager.Contracts.Changelog.Find(changelog, version) is not { } entry)
    {
        Console.Error.WriteLine($"{changelogPath} has no entry for {version}. Write its lines first (docs\\release-builds.md).");
        return 2;
    }

    notes = DownloadManager.Contracts.UpdateNotes.From(entry);
    if (DownloadManager.Contracts.UpdateNotes.Problem(notes) is { } problem)
    {
        Console.Error.WriteLine($"The changelog lines of {version} can't go into the manifest: {problem}");
        return 2;
    }
}

var bytes = await File.ReadAllBytesAsync(packagePath);
var sha256 = Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();
Directory.CreateDirectory(outputDir);

using var rsa = RSA.Create();
rsa.ImportFromPem(await File.ReadAllTextAsync(privateKeyPath));
// Validate that the supplied PEM includes private material before writing output.
_ = rsa.ExportParameters(includePrivateParameters: true);
await File.WriteAllTextAsync(Path.Combine(outputDir, "public.pem"), rsa.ExportSubjectPublicKeyInfoPem());
var canonical = $"{version}\n{channel}\n{installerUrl}\n{bytes.Length}\n{sha256}";
var signature = rsa.SignData(
    Encoding.UTF8.GetBytes(canonical), HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
var camelCase = new JsonSerializerOptions { PropertyNamingPolicy = JsonNamingPolicy.CamelCase };
string? notesSignature = null;
if (notes is not null)
{
    // Not in the signed text above, so older clients verify without it. It has its own signature over that text and the
    // notes (review of 0.7.2, security-2), and clients show notes only when it verifies, as plain text.
    var notesText = DownloadManager.Contracts.UpdateManifestVerifier.NotesCanonical(canonical, JsonSerializer.SerializeToElement(notes, camelCase))!;
    notesSignature = Convert.ToBase64String(rsa.SignData(
        Encoding.UTF8.GetBytes(notesText), HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1));
}

var manifest = new
{
    version,
    channel,
    installerUrl,
    size = bytes.Length,
    sha256,
    signature = Convert.ToBase64String(signature),
    notes,
    notesSignature,
};
await File.WriteAllTextAsync(
    Path.Combine(outputDir, manifestName),
    JsonSerializer.Serialize(manifest, new JsonSerializerOptions
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = true,
        DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull,
    }));
Console.WriteLine($"signed {version} ({bytes.Length} bytes, {sha256})" + (notes is null ? string.Empty : $", {notes.Count} note lines"));
return 0;

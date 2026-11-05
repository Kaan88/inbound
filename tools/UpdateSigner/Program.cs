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

if (args.Length is < 5 or > 6)
{
    Console.Error.WriteLine("usage: UpdateSigner <output-dir> <version> <installer-url> <package-file> <private-key-file> [channel]");
    Console.Error.WriteLine("       UpdateSigner --generate-key <new-private-key-file>");
    return 2;
}

var outputDir = args[0];
var version = args[1];
var installerUrl = args[2];
var packagePath = args[3];
var privateKeyPath = args[4];
var channel = args.Length > 5 ? args[5] : "stable";
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
var manifest = new
{
    version,
    channel,
    installerUrl,
    size = bytes.Length,
    sha256,
    signature = Convert.ToBase64String(signature),
};
await File.WriteAllTextAsync(
    Path.Combine(outputDir, "manifest.json"),
    JsonSerializer.Serialize(manifest, new JsonSerializerOptions
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = true,
    }));
Console.WriteLine($"signed {version} ({bytes.Length} bytes, {sha256})");
return 0;

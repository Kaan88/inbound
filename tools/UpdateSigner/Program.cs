using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

if (args.Length < 4)
{
    Console.Error.WriteLine("usage: UpdateSigner <output-dir> <version> <installer-url> <package-file> [channel]");
    return 2;
}

var outputDir = args[0];
var version = args[1];
var installerUrl = args[2];
var packagePath = args[3];
var channel = args.Length > 4 ? args[4] : "stable";
var bytes = await File.ReadAllBytesAsync(packagePath);
var sha256 = Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();
Directory.CreateDirectory(outputDir);

using var rsa = RSA.Create(2048);
await File.WriteAllTextAsync(Path.Combine(outputDir, "private.pem"), rsa.ExportPkcs8PrivateKeyPem());
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

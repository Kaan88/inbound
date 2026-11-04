using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace DownloadManager.Contracts;

public sealed class UpdateManifest
{
    public string Version { get; set; } = string.Empty;
    public string Channel { get; set; } = "stable";
    public string InstallerUrl { get; set; } = string.Empty;
    public long Size { get; set; }
    public string Sha256 { get; set; } = string.Empty;
    public string Signature { get; set; } = string.Empty;
}

public sealed class UpdateCheckResult
{
    public bool Ok { get; set; }
    public string Status { get; set; } = "invalid";
    public string? Error { get; set; }
    public string? InstalledVersion { get; set; }
    public string? AvailableVersion { get; set; }
    public string? InstallerUrl { get; set; }
    public long? Size { get; set; }
    public string? Sha256 { get; set; }
}

public sealed class UpdateDownloadResult
{
    public bool Ok { get; set; }
    public string? Error { get; set; }
    public string? Path { get; set; }
    public long? Size { get; set; }
    public string? Sha256 { get; set; }
}

public sealed class UpdateInstallRequest
{
    public bool Force { get; set; }
}

public sealed class UpdateInstallResult
{
    public bool Ok { get; set; }
    public string? Error { get; set; }
    public string Status { get; set; } = "invalid";
    public string? Version { get; set; }
    public string? ResultPath { get; set; }
}

public sealed class UpdateDeferRequest
{
    public string Version { get; set; } = string.Empty;
}

public static class UpdateManifestVerifier
{
    public static string Canonical(UpdateManifest manifest)
        => $"{manifest.Version}\n{manifest.Channel}\n{manifest.InstallerUrl}\n{manifest.Size}\n" +
           manifest.Sha256.ToLowerInvariant();

    public static bool Verify(UpdateManifest manifest, string publicKeyPem)
    {
        try
        {
            if (string.IsNullOrWhiteSpace(publicKeyPem) ||
                string.IsNullOrWhiteSpace(manifest.Signature))
            {
                return false;
            }

            var signature = Convert.FromBase64String(manifest.Signature);
            using var rsa = RSA.Create();
            rsa.ImportFromPem(publicKeyPem);
            return rsa.VerifyData(
                Encoding.UTF8.GetBytes(Canonical(manifest)),
                signature,
                HashAlgorithmName.SHA256,
                RSASignaturePadding.Pkcs1);
        }
        catch (Exception ex) when (ex is CryptographicException or FormatException or ArgumentException)
        {
            return false;
        }
    }

    public static UpdateManifest? Parse(string json)
    {
        try
        {
            return JsonSerializer.Deserialize<UpdateManifest>(json, IpcJson.Options);
        }
        catch (JsonException)
        {
            return null;
        }
    }
}

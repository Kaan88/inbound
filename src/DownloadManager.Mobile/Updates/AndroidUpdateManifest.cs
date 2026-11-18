using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;

namespace DownloadManager.Mobile.Updates;

public sealed class AndroidUpdateManifest
{
    public long VersionCode { get; set; }
    public string VersionName { get; set; } = "";
    public string ApkUrl { get; set; } = "";
    public long Size { get; set; }
    public string Sha256 { get; set; } = "";
    public string Signature { get; set; } = "";
}

public static partial class AndroidUpdateTrust
{
    public const string PackageName = "app.inbound.android";
    public const string CertificateSha256 = "3746bf5a8975e623e8acd8dee6ad2665c3a26ffbf6f34ac6f33380f65d8f0457";
    public const string FeedUrl = "https://github.com/Kaan88/inbound/releases/download/android-latest/manifest-android-arm64.json";
    public const long MaximumApkBytes = 256L * 1024 * 1024;
    public static AndroidUpdateManifest Parse(ReadOnlySpan<byte> json) =>
        JsonSerializer.Deserialize(json, AndroidUpdateJson.Default.AndroidUpdateManifest)
            ?? throw new InvalidDataException("The Android update information is empty.");

    public static string Canonical(AndroidUpdateManifest m) => FormattableString.Invariant(
        $"inbound-android-update-v1\n{m.VersionCode}\n{m.VersionName}\n{m.ApkUrl}\n{m.Size}\n{m.Sha256.ToLowerInvariant()}");

    public static void Validate(AndroidUpdateManifest m, string publicKey)
    {
        if (m.VersionCode <= 0 || m.VersionCode > int.MaxValue || string.IsNullOrEmpty(m.VersionName) || !VersionPattern().IsMatch(m.VersionName) ||
            m.Size <= 0 || m.Size > MaximumApkBytes || string.IsNullOrEmpty(m.Sha256) || !HashPattern().IsMatch(m.Sha256) || !IsReleaseUrl(m.ApkUrl))
            throw new InvalidDataException("The Android update information is invalid.");
        try
        {
            using var rsa = RSA.Create();
            rsa.ImportFromPem(publicKey);
            if (!rsa.VerifyData(Encoding.UTF8.GetBytes(Canonical(m)), Convert.FromBase64String(m.Signature),
                    HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1))
                throw new InvalidDataException("The Android update signature is invalid.");
        }
        catch (Exception e) when (e is CryptographicException or FormatException or ArgumentException)
        {
            throw new InvalidDataException("The Android update signature is invalid.", e);
        }
    }

    public static bool IsReleaseUrl(string value) => Uri.TryCreate(value, UriKind.Absolute, out var u) &&
        u.Scheme == "https" && u.Host.Equals("github.com", StringComparison.OrdinalIgnoreCase) && u.IsDefaultPort &&
        u.UserInfo.Length == 0 && u.Query.Length == 0 && u.Fragment.Length == 0 &&
        u.AbsolutePath.StartsWith("/Kaan88/inbound/releases/download/", StringComparison.Ordinal) &&
        u.AbsolutePath.EndsWith(".apk", StringComparison.OrdinalIgnoreCase);

    [GeneratedRegex(@"^[0-9]+(?:\.[0-9]+){1,3}\z", RegexOptions.CultureInvariant)]
    private static partial Regex VersionPattern();
    [GeneratedRegex(@"^[a-fA-F0-9]{64}\z", RegexOptions.CultureInvariant)]
    private static partial Regex HashPattern();
}

[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase)]
[JsonSerializable(typeof(AndroidUpdateManifest))]
internal partial class AndroidUpdateJson : JsonSerializerContext;

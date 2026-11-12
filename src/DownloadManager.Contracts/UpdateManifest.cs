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

    /// <summary>
    /// UPD.N1: what this version changes, as changelog lines (<see cref="ChangelogItem"/>). Optional, and left out of
    /// <see cref="UpdateManifestVerifier.Canonical"/>, so clients from before it still verify the manifest. It has its own
    /// signature, <see cref="NotesSignature"/>, and is shown only when that verifies (review of 0.7.2, security-2). Kept as
    /// raw JSON so a bad value can't stop the manifest from being read; <see cref="UpdateNotes.Read"/> checks it, and it is
    /// only ever shown as plain text.
    /// </summary>
    public JsonElement? Notes { get; set; }

    /// <summary>
    /// The signature of <see cref="UpdateManifestVerifier.NotesCanonical"/>: the signed text with the notes after it, so
    /// notes can't be moved to another manifest. Older clients ignore it.
    /// </summary>
    public string? NotesSignature { get; set; }
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

    /// <summary>UPD.N1: the available version's notes from its manifest, checked (<see cref="UpdateNotes.Read"/>); null when it has none.</summary>
    public List<ChangelogItem>? Notes { get; set; }
}

public sealed class UpdateProgressDto
{
    public string Stage { get; set; } = "checking";
    public string? Version { get; set; }
    public long ReceivedBytes { get; set; }
    public long? TotalBytes { get; set; }
    public string? Error { get; set; }
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
        => VerifyText(Canonical(manifest), manifest.Signature, publicKeyPem);

    /// <summary>
    /// Review of 0.7.2, security-2: the text <see cref="UpdateManifest.NotesSignature"/> signs: <see cref="Canonical"/>, then
    /// each entry of the notes as <see cref="UpdateNotes.Read"/> sees it (its text and its string tags, JSON-escaped), one
    /// per line. Everything that can reach the screen is in it. Null when the notes aren't a list.
    /// </summary>
    public static string? NotesCanonical(string canonical, JsonElement? notes)
    {
        if (notes is not { ValueKind: JsonValueKind.Array } array) return null;
        var builder = new StringBuilder(canonical).Append("\nnotes");
        foreach (var entry in array.EnumerateArray())
        {
            builder.Append('\n');
            if (entry.ValueKind != JsonValueKind.Object)
            {
                builder.Append('-');
                continue;
            }

            var text = entry.TryGetProperty("text", out var textValue) && textValue.ValueKind == JsonValueKind.String ? textValue.GetString() : null;
            var tags = new List<string>();
            if (entry.TryGetProperty("tags", out var tagValues) && tagValues.ValueKind == JsonValueKind.Array)
            {
                foreach (var tag in tagValues.EnumerateArray())
                    if (tag.ValueKind == JsonValueKind.String) tags.Add(tag.GetString()!);
            }

            builder.Append(JsonSerializer.Serialize(text)).Append(' ').Append(JsonSerializer.Serialize(tags));
        }

        return builder.ToString();
    }

    /// <summary>The manifest's notes when their own signature verifies with this key; null otherwise or when it has none.</summary>
    public static List<ChangelogItem>? VerifiedNotes(UpdateManifest manifest, string publicKeyPem)
        => NotesCanonical(Canonical(manifest), manifest.Notes) is { } text && VerifyText(text, manifest.NotesSignature, publicKeyPem)
            ? UpdateNotes.Read(manifest.Notes)
            : null;

    private static bool VerifyText(string text, string? signatureBase64, string publicKeyPem)
    {
        try
        {
            if (string.IsNullOrWhiteSpace(publicKeyPem) || string.IsNullOrWhiteSpace(signatureBase64)) return false;
            var signature = Convert.FromBase64String(signatureBase64);
            using var rsa = RSA.Create();
            rsa.ImportFromPem(publicKeyPem);
            return rsa.VerifyData(Encoding.UTF8.GetBytes(text), signature, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
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

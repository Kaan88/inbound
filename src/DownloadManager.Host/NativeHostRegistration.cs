using System.Text.Json;
using DownloadManager.Contracts;
using Microsoft.Win32;

namespace DownloadManager.Host;

public static class NativeHostRegistration
{
    private const string RegistryPath =
        @"Software\Google\Chrome\NativeMessagingHosts\" + BrowserBridge.NativeHostName;

    public static bool IsRegistered()
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(RegistryPath);
            var manifestPath = key?.GetValue(null) as string;
            if (string.IsNullOrWhiteSpace(manifestPath) || !File.Exists(manifestPath))
            {
                return false;
            }

            using var document = JsonDocument.Parse(File.ReadAllText(manifestPath));
            var root = document.RootElement;
            if (!root.TryGetProperty("allowed_origins", out var origins) || origins.ValueKind != JsonValueKind.Array)
            {
                return false;
            }

            return origins.EnumerateArray().Any(o =>
                string.Equals(o.GetString(), BrowserBridge.ExtensionOrigin, StringComparison.OrdinalIgnoreCase));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
        {
            return false;
        }
    }

    public static string ManifestPath()
        => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "DownloadManager", "native-host", "com.kaank.downloadmanager.json");
}

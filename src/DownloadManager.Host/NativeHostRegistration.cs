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

            // A manifest written before the store ID existed would block the store extension,
            // so it does not count as registered.
            return origins.EnumerateArray().Any(o =>
                string.Equals(o.GetString(), BrowserBridge.StoreExtensionOrigin, StringComparison.OrdinalIgnoreCase));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
        {
            return false;
        }
    }

}

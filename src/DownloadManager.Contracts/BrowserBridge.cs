namespace DownloadManager.Contracts;

public static class BrowserBridge
{
    public const string NativeHostName = "com.kaank.downloadmanager";
    public const string ExtensionName = "Inbound Download Manager Bridge";

    // The ID the Chrome Web Store assigned to the listing.
    public const string StoreExtensionId = "olhbcdbjdnfoiinllnlpandggadfabfm";

    // Fixed by the manifest "key" of the unpacked build that Setup and the update package carry.
    public const string UnpackedExtensionId = "pknddmneejadnnngffojnoecojhdfalc";

    public const string StoreExtensionOrigin = "chrome-extension://" + StoreExtensionId + "/";
    public const string UnpackedExtensionOrigin = "chrome-extension://" + UnpackedExtensionId + "/";

    // Extension IDs are case-sensitive; Chrome always reports them in lower case.
    public static bool IsTrustedExtensionId(string? extensionId) =>
        string.Equals(extensionId, StoreExtensionId, StringComparison.Ordinal) ||
        string.Equals(extensionId, UnpackedExtensionId, StringComparison.Ordinal);

    public static bool IsTrustedOrigin(string? origin)
    {
        var trimmed = origin?.TrimEnd('/');
        return string.Equals(trimmed, StoreExtensionOrigin.TrimEnd('/'), StringComparison.OrdinalIgnoreCase) ||
            string.Equals(trimmed, UnpackedExtensionOrigin.TrimEnd('/'), StringComparison.OrdinalIgnoreCase);
    }
}

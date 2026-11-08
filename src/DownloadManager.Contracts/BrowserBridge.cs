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

    // Chrome and Edge each look the native-messaging host up under their own HKCU key. Both keys hold
    // the path of the same manifest file, so the Chrome Web Store build works in either browser.
    public const string ChromeNativeHostKeyPath = @"Software\Google\Chrome\NativeMessagingHosts\" + NativeHostName;
    public const string EdgeNativeHostKeyPath = @"Software\Microsoft\Edge\NativeMessagingHosts\" + NativeHostName;

    public static IReadOnlyList<string> NativeHostKeyPaths { get; } = [ChromeNativeHostKeyPath, EdgeNativeHostKeyPath];

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

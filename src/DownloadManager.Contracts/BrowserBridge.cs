namespace DownloadManager.Contracts;

public static class BrowserBridge
{
    public const string NativeHostName = "com.kaank.downloadmanager";
    public const string ExtensionName = "Inbound Download Manager Bridge";

    // The ID the Chrome Web Store assigned to the listing.
    public const string StoreExtensionId = "olhbcdbjdnfoiinllnlpandggadfabfm";

    // Fixed by the manifest "key" of the unpacked build that Setup and the update package carry.
    public const string UnpackedExtensionId = "pknddmneejadnnngffojnoecojhdfalc";

    // Firefox's add-on ID, set by browser_specific_settings.gecko.id in the Firefox build (build.mjs).
    public const string FirefoxExtensionId = "inbound-bridge@kaan88";

    public const string StoreExtensionOrigin = "chrome-extension://" + StoreExtensionId + "/";
    public const string UnpackedExtensionOrigin = "chrome-extension://" + UnpackedExtensionId + "/";

    // Chrome and Edge each look the native-messaging host up under their own HKCU key. Both keys hold
    // the path of the same manifest file, so the Chrome Web Store build works in either browser.
    public const string ChromeNativeHostKeyPath = @"Software\Google\Chrome\NativeMessagingHosts\" + NativeHostName;
    public const string EdgeNativeHostKeyPath = @"Software\Microsoft\Edge\NativeMessagingHosts\" + NativeHostName;

    public static IReadOnlyList<string> NativeHostKeyPaths { get; } = [ChromeNativeHostKeyPath, EdgeNativeHostKeyPath];

    // Firefox reads its own key, which points at a manifest of its own: Firefox lists allowed_extensions
    // (add-on IDs) where Chrome lists allowed_origins, so the two cannot share one file.
    public const string FirefoxNativeHostKeyPath = @"Software\Mozilla\NativeMessagingHosts\" + NativeHostName;
    public const string FirefoxManifestFileName = NativeHostName + ".firefox.json";

    // Extension IDs are case-sensitive; Chrome always reports them in lower case.
    public static bool IsTrustedExtensionId(string? extensionId) =>
        string.Equals(extensionId, StoreExtensionId, StringComparison.Ordinal) ||
        string.Equals(extensionId, UnpackedExtensionId, StringComparison.Ordinal) ||
        string.Equals(extensionId, FirefoxExtensionId, StringComparison.Ordinal);

    public static bool IsTrustedOrigin(string? origin)
    {
        var trimmed = origin?.TrimEnd('/');
        return string.Equals(trimmed, StoreExtensionOrigin.TrimEnd('/'), StringComparison.OrdinalIgnoreCase) ||
            string.Equals(trimmed, UnpackedExtensionOrigin.TrimEnd('/'), StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// Who started the native host, from its command line. Chrome and Edge pass the caller's origin
    /// (chrome-extension://id/) first; Firefox passes the path of its manifest and then the add-on ID.
    /// Only a trusted Chrome origin or the trusted Firefox ID is accepted, and anything else is refused,
    /// including no caller at all. Before, a command line without a chrome-extension:// origin skipped
    /// the check, so a browser that named some other extension as the caller was let through.
    /// This checks what the browser says, not who started the process: any program the user runs can
    /// write this command line, and such a program can reach the Host's pipe directly anyway (its ACL
    /// admits the whole user account). What it closes is the browser path: another extension, in any
    /// browser this native host is registered for, cannot use the bridge.
    /// </summary>
    /// <returns>A short name of the trusted caller for the log, or null to refuse.</returns>
    public static string? TrustedCaller(IReadOnlyList<string> arguments)
    {
        string? caller = null;
        foreach (var argument in arguments)
        {
            if (argument.StartsWith("chrome-extension://", StringComparison.OrdinalIgnoreCase))
            {
                // Two origins would be a forged command line; Chrome passes exactly one.
                if (caller is not null || !IsTrustedOrigin(argument)) return null;
                caller = "chromium";
            }
            else if (argument.StartsWith("moz-extension://", StringComparison.OrdinalIgnoreCase))
            {
                // Firefox names add-ons by ID, never by their internal moz-extension:// address.
                return null;
            }
        }

        if (caller is not null) return caller;

        // Firefox: <manifest path> <add-on ID>. The ID is the last argument, compared exactly.
        return arguments.Count >= 2 && string.Equals(arguments[^1], FirefoxExtensionId, StringComparison.Ordinal)
            ? "firefox"
            : null;
    }
}

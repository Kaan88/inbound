namespace DownloadManager.Contracts;

public static class BrowserBridge
{
    public const string NativeHostName = "com.kaank.downloadmanager";
    public const string ExtensionName = "Inbound Download Manager Bridge";

    // The ID the Chrome Web Store assigned to the listing.
    public const string StoreExtensionId = "olhbcdbjdnfoiinllnlpandggadfabfm";

    // The ID Microsoft Edge Add-ons assigned to the Edge listing (its CRX ID, derived from the public key
    // Partner Center shows). It differs from the Chrome Web Store ID.
    public const string EdgeStoreExtensionId = "ifoagddcenmjpbklkhghbdmgplldcdci";

    // Fixed by the manifest "key" of the unpacked build that Setup and the update package carry.
    public const string UnpackedExtensionId = "pknddmneejadnnngffojnoecojhdfalc";

    // Firefox's add-on ID, set by browser_specific_settings.gecko.id in the Firefox build (build.mjs).
    public const string FirefoxExtensionId = "inbound-bridge@kaan88";

    public const string StoreExtensionOrigin = "chrome-extension://" + StoreExtensionId + "/";
    public const string EdgeStoreExtensionOrigin = "chrome-extension://" + EdgeStoreExtensionId + "/";
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
        string.Equals(extensionId, EdgeStoreExtensionId, StringComparison.Ordinal) ||
        string.Equals(extensionId, UnpackedExtensionId, StringComparison.Ordinal) ||
        string.Equals(extensionId, FirefoxExtensionId, StringComparison.Ordinal);

    public static bool IsTrustedOrigin(string? origin)
    {
        var trimmed = origin?.TrimEnd('/');
        return string.Equals(trimmed, StoreExtensionOrigin.TrimEnd('/'), StringComparison.OrdinalIgnoreCase) ||
            string.Equals(trimmed, EdgeStoreExtensionOrigin.TrimEnd('/'), StringComparison.OrdinalIgnoreCase) ||
            string.Equals(trimmed, UnpackedExtensionOrigin.TrimEnd('/'), StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>UI.R41: where each browser gets the extension. Chrome's and Edge's are the store listings Setup links.</summary>
    public const string ChromeStoreUrl = "https://chromewebstore.google.com/detail/" + StoreExtensionId;
    public const string EdgeStoreUrl = "https://microsoftedge.microsoft.com/addons/detail/" + EdgeStoreExtensionId;

    /// <summary>
    /// UI.R41: Firefox's add-on is signed by Mozilla but not listed on Firefox Add-ons (EXT.S4); it is a file on each
    /// GitHub release (<c>inbound-bridge-&lt;version&gt;-firefox.xpi</c>), so the link is the latest release page.
    /// </summary>
    public const string FirefoxReleaseUrl = "https://github.com/Kaan88/inbound/releases/latest";

    /// <summary>
    /// UI.R41: which browser's extension started the native host, from its command line and the names of the programs
    /// above it, nearest first (<c>cmd.exe</c>, <c>chrome.exe</c>, …): <see cref="ExtensionBrowsers.Chrome"/>,
    /// <see cref="ExtensionBrowsers.Edge"/> or <see cref="ExtensionBrowsers.Firefox"/>, or null for a caller that
    /// <see cref="TrustedCaller"/> refuses. The Chrome Web Store copy also runs in Edge, so the browser program above
    /// decides when one is found; otherwise the listing's ID does (an unpacked build counts as Chrome).
    /// </summary>
    public static string? CallerBrowser(IReadOnlyList<string> arguments, IEnumerable<string> ancestors)
    {
        var caller = TrustedCaller(arguments);
        if (caller is null) return null;
        if (caller == "firefox") return ExtensionBrowsers.Firefox;
        foreach (var ancestor in ancestors)
        {
            var name = Path.GetFileNameWithoutExtension(ancestor ?? string.Empty);
            if (name.Equals("msedge", StringComparison.OrdinalIgnoreCase)) return ExtensionBrowsers.Edge;
            if (name.Equals("chrome", StringComparison.OrdinalIgnoreCase)) return ExtensionBrowsers.Chrome;
        }

        var edgeListing = arguments.Any(a => string.Equals(a.TrimEnd('/'), EdgeStoreExtensionOrigin.TrimEnd('/'), StringComparison.OrdinalIgnoreCase));
        return edgeListing ? ExtensionBrowsers.Edge : ExtensionBrowsers.Chrome;
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

/// <summary>
/// UI.R41: the browsers whose extension can reach the app, as <see cref="ClientHelloDto.Browser"/> and
/// <see cref="HostStatusDto.ExtensionsSeen"/> name them.
/// </summary>
public static class ExtensionBrowsers
{
    public const string Chrome = "chrome";
    public const string Edge = "edge";
    public const string Firefox = "firefox";

    public static IReadOnlyList<string> All { get; } = [Chrome, Edge, Firefox];

    public static bool IsKnown(string? browser) => browser is Chrome or Edge or Firefox;

    /// <summary>
    /// UI.B31: an extension in Chrome or Edge that used the app before the native host named its browser (0.7.2), as the
    /// Host found it in its native-host logs ("Caller=chromium"). It can't tell the two apart, so it is kept apart from
    /// them: a window shows it on both rows as used before, and only until that browser has a sighting of its own.
    /// </summary>
    public const string Chromium = "chromium";

    /// <summary>
    /// UI.B31: an extension whose native host didn't name its browser (one older than 0.7.2). It only tells a window that
    /// an extension is in use; no row shows it.
    /// </summary>
    public const string Unknown = "unknown";

    /// <summary>A name <see cref="HostStatusDto.ExtensionsSeen"/> may hold: one of the browsers, or <see cref="Chromium"/> or <see cref="Unknown"/>.</summary>
    public static bool IsSighting(string? name) => IsKnown(name) || name is Chromium or Unknown;
}

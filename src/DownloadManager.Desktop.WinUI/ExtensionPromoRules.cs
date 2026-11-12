using System.Globalization;
using DownloadManager.Contracts;

namespace DownloadManager.Desktop.WinUI;

/// <summary>What the main window does about its extension hint: show it, and whether to remember it is done with.</summary>
/// <param name="Show">Show the slim bar over the list.</param>
/// <param name="MarkDone">Keep in ui-state.json that the hint is never needed again (an extension has connected).</param>
internal sealed record ExtensionHintDecision(bool Show, bool MarkDone);

/// <summary>One browser's line in Options and in Help > Get the Browser Extension.</summary>
/// <param name="Name">Chrome, Edge or Firefox.</param>
/// <param name="From">Where the link goes, said under the name.</param>
/// <param name="Url">The store page, or the release page for Firefox.</param>
/// <param name="Status">Whether this browser's extension has reached Inbound; null where it can't be known.</param>
/// <param name="Connected">The status says it has.</param>
internal sealed record ExtensionLink(string Browser, string Name, string From, string Url, string? Status, bool Connected);

/// <summary>
/// UI.R41 (review 5:F1): leading the user to the browser extension from inside the app. UI-free, so it can be tested.
/// </summary>
internal static class ExtensionPromoRules
{
    public const string HintTitle = "Send downloads from your browser to Inbound";
    public const string HintMessage = "Get the Inbound extension for Chrome, Edge or Firefox, and downloads you start there come here.";
    public const string SectionHint = "The extension sends downloads you start in your browser to Inbound. Get it for each browser you use.";
    /// <summary>UI.B29: below this width the hint bar's buttons go under its words, together, instead of beside them.</summary>
    public const double HintOneLineMinWidth = 640;

    /// <summary>UI.B31: Chrome's and Edge's rows when the Host knows one of them was used but not which.</summary>
    public const string UsedBefore = "Used before";

    public const string EdgeAlso = "The Chrome Web Store copy works in Edge too; Edge then shows here as connected.";

    /// <summary>
    /// The hint shows once, until "Not now" or until any extension has connected, and never again after either. It needs
    /// to know: on a window showing another server, or with a Host that can't say (an older one: <paramref name="seen"/>
    /// null), it stays hidden. Capturing turned off in Options is a choice against it. A download sent by the browser in
    /// the list counts as connected, which covers an extension that last connected before this Host kept count.
    /// </summary>
    /// <param name="done">ui-state.json says Not now was clicked, or an extension connected before.</param>
    /// <param name="isLocal">The window shows this PC's Host.</param>
    /// <param name="integrationStatus">The Host's browser integration status (<see cref="HostStatusDto.BrowserIntegration"/>).</param>
    /// <param name="seen">The Host's <see cref="HostStatusDto.ExtensionsSeen"/>.</param>
    /// <param name="anyBrowserDownload">The list holds a download the browser extension sent.</param>
    public static ExtensionHintDecision Decide(bool done, bool isLocal, string? integrationStatus,
        IReadOnlyDictionary<string, DateTimeOffset>? seen, bool anyBrowserDownload)
    {
        if (done || !isLocal || seen is null) return new ExtensionHintDecision(false, false);
        // UI.B31: an extension seen before its browser was named (chromium, unknown) counts too.
        if (seen.Keys.Any(ExtensionBrowsers.IsSighting) || anyBrowserDownload) return new ExtensionHintDecision(false, true);
        if (integrationStatus == "disabled") return new ExtensionHintDecision(false, false);
        return new ExtensionHintDecision(true, false);
    }

    /// <summary>Whether a download's <see cref="JobDto.Source"/> says the browser extension sent it.</summary>
    public static bool FromBrowser(string? source)
        => source is "chromeAuto" or "chromeContextMenu";

    /// <summary>
    /// The three browsers, each with its link and, where the Host can say, whether its extension has connected.
    /// <paramref name="seen"/> null (another server, an older Host) gives no status at all. What blocks a browser now
    /// (capturing off, a broken registration) is said before its history, so a browser that connected yesterday but can't
    /// today doesn't show as connected.
    /// </summary>
    /// <param name="canRepair">
    /// The status bar's Repair exists here (Windows). Elsewhere the registration words are left out: Linux's Host
    /// registers the native host itself and reports no registration state, so they would always say "can't reach".
    /// </param>
    public static IReadOnlyList<ExtensionLink> Links(IReadOnlyDictionary<string, DateTimeOffset>? seen, string? integrationStatus,
        bool canRepair, CultureInfo? culture = null)
    {
        return
        [
            Link(ExtensionBrowsers.Chrome, "Chrome", "Chrome Web Store", BrowserBridge.ChromeStoreUrl),
            Link(ExtensionBrowsers.Edge, "Edge", "Microsoft Edge Add-ons", BrowserBridge.EdgeStoreUrl),
            Link(ExtensionBrowsers.Firefox, "Firefox", "The latest release on GitHub: drag the .xpi file onto Firefox", BrowserBridge.FirefoxReleaseUrl),
        ];

        ExtensionLink Link(string browser, string name, string from, string url)
        {
            if (seen is null) return new ExtensionLink(browser, name, from, url, null, false);
            if (Unreached(browser, integrationStatus, canRepair) is { } blocked)
                return new ExtensionLink(browser, name, from, url, blocked, false);
            if (seen.TryGetValue(browser, out var when))
                return new ExtensionLink(browser, name, from, url, $"Connected · last {Day(when, culture)}", true);
            // UI.B31: Chrome or Edge, from before the native host named its browser; which one can't be told.
            if (browser != ExtensionBrowsers.Firefox && seen.TryGetValue(ExtensionBrowsers.Chromium, out var before))
                return new ExtensionLink(browser, name, from, url, $"{UsedBefore} · last {Day(before, culture)}", false);
            return new ExtensionLink(browser, name, from, url, "Not connected yet", false);
        }
    }

    /// <summary>What stops a browser's extension reaching Inbound, from the integration status, or null when nothing does.</summary>
    private static string? Unreached(string browser, string? status, bool canRepair)
    {
        if (status == "disabled") return "Not connected · browser downloads are off";
        // The registration covers Chrome's and Edge's keys; Firefox's is not part of that status.
        if (browser == ExtensionBrowsers.Firefox || !canRepair) return null;
        if (status == "native host not registered") return "Can't reach Inbound · use Repair in the status bar";
        var name = browser == ExtensionBrowsers.Chrome ? "Chrome" : "Edge";
        return status is { } text && text.Contains($"can't reach Inbound in {name}", StringComparison.Ordinal)
            ? "Can't reach Inbound · use Repair in the status bar"
            : null;
    }

    private static string Day(DateTimeOffset when, CultureInfo? culture)
        => when.ToLocalTime().ToString("d MMM yyyy", culture ?? CultureInfo.CurrentCulture);
}

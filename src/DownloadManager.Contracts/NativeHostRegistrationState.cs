using System.Text.Json;

namespace DownloadManager.Contracts;

/// <summary>
/// What the per-browser native-messaging registrations add up to. Pure logic over manifest text and
/// registry values, so it can be tested without touching the registry.
/// </summary>
public readonly record struct NativeHostRegistrationState(bool ChromeUsable, bool EdgeUsable)
{
    /// <summary>
    /// True when at least one browser can reach the host. A working Chrome is not reported as broken
    /// because Edge's key is absent (Edge is optional), and the other way round.
    /// </summary>
    public bool IsRegistered => ChromeUsable || EdgeUsable;

    /// <param name="chromeManifestText">The manifest the Chrome key points to; null when the key or file is missing.</param>
    /// <param name="edgeManifestText">The manifest the Edge key points to; null when the key or file is missing.</param>
    public static NativeHostRegistrationState Evaluate(string? chromeManifestText, string? edgeManifestText) =>
        new(IsManifestUsable(chromeManifestText), IsManifestUsable(edgeManifestText));

    /// <summary>
    /// A manifest is usable when it names the store extension as an allowed origin. One written before
    /// the store ID existed would block the store extension, so it does not count.
    /// </summary>
    public static bool IsManifestUsable(string? manifestText)
    {
        if (string.IsNullOrWhiteSpace(manifestText))
        {
            return false;
        }

        try
        {
            using var document = JsonDocument.Parse(manifestText);
            if (document.RootElement.ValueKind != JsonValueKind.Object ||
                !document.RootElement.TryGetProperty("allowed_origins", out var origins) ||
                origins.ValueKind != JsonValueKind.Array)
            {
                return false;
            }

            return origins.EnumerateArray().Any(o =>
                o.ValueKind == JsonValueKind.String &&
                string.Equals(o.GetString(), BrowserBridge.StoreExtensionOrigin, StringComparison.OrdinalIgnoreCase));
        }
        catch (JsonException)
        {
            return false;
        }
    }

    /// <summary>
    /// A 0.4.0 install has only the Chrome key. Returns the manifest path to write under the Edge key,
    /// or null when nothing should be written: Chrome is not set up, or Edge already has a registration
    /// whose manifest exists (ours or someone else's, and not ours to replace).
    /// </summary>
    public static string? PlanEdgeRepair(string? chromeKeyValue, bool chromeUsable, string? edgeKeyValue, bool edgeManifestExists)
    {
        if (!chromeUsable || string.IsNullOrWhiteSpace(chromeKeyValue))
        {
            return null;
        }

        if (!string.IsNullOrWhiteSpace(edgeKeyValue) && edgeManifestExists)
        {
            return null;
        }

        return chromeKeyValue;
    }
}

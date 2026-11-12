using System.Text.Json;

namespace DownloadManager.Contracts;

/// <summary>What one browser's native-messaging key adds up to.</summary>
public enum NativeHostKeyState
{
    /// <summary>The key is missing or has no value.</summary>
    Absent = 0,

    /// <summary>The key points at a manifest that lists the store origin and names a program that exists.</summary>
    Usable,

    /// <summary>The key has a value, but the browser would not reach the host through it.</summary>
    Broken,
}

/// <summary>
/// What the per-browser native-messaging registrations add up to. Pure logic over manifest text and
/// registry values, so it can be tested without touching the registry.
/// </summary>
public readonly record struct NativeHostRegistrationState(NativeHostKeyState Chrome, NativeHostKeyState Edge)
{
    public bool ChromeUsable => Chrome == NativeHostKeyState.Usable;

    public bool EdgeUsable => Edge == NativeHostKeyState.Usable;

    /// <summary>True when at least one browser can reach the host.</summary>
    public bool IsRegistered => ChromeUsable || EdgeUsable;

    /// <summary>
    /// The browser integration status every surface shows (tray, status bar, CLI), naming a browser that
    /// cannot reach the host while the other can. An absent Edge key is not named: installs from 0.4.0
    /// have none until the Host adds it, and Edge may not be installed. Every version has written
    /// Chrome's key, so its absence means something removed it. A missing and a broken key read the same:
    /// either way the extension can't reach the app, and Repair puts both right (review 5:F2).
    /// </summary>
    public string IntegrationText(bool integrationEnabled)
    {
        if (!integrationEnabled)
        {
            return "disabled";
        }

        if (!IsRegistered)
        {
            return "native host not registered";
        }

        var problems = new List<string>(2);
        if (Chrome == NativeHostKeyState.Broken)
        {
            problems.Add("the extension can't reach Inbound in Chrome");
        }
        else if (Chrome == NativeHostKeyState.Absent)
        {
            problems.Add("the extension can't reach Inbound in Chrome");
        }

        if (Edge == NativeHostKeyState.Broken)
        {
            problems.Add("the extension can't reach Inbound in Edge");
        }

        // U-WL-9: "on", not "connected": a registration says a browser can reach the app, not that one is running.
        return problems.Count == 0 ? "on" : "on; " + string.Join("; ", problems);
    }

    /// <param name="keyValue">The value of the browser's key; null when the key is missing.</param>
    /// <param name="manifestText">The file that value points to; null when it is missing or unreadable.</param>
    /// <param name="executableExists">Whether the program the manifest names exists.</param>
    public static NativeHostKeyState EvaluateKey(string? keyValue, string? manifestText, bool executableExists)
    {
        if (string.IsNullOrWhiteSpace(keyValue))
        {
            return NativeHostKeyState.Absent;
        }

        return executableExists && IsManifestUsable(manifestText)
            ? NativeHostKeyState.Usable
            : NativeHostKeyState.Broken;
    }

    /// <summary>
    /// A manifest is usable when it names both store extensions (Chrome's and Edge's) as allowed origins.
    /// One written before either ID existed would block that store's extension, so it does not count;
    /// Repair rewrites it.
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

            return HasOrigin(origins, BrowserBridge.StoreExtensionOrigin) &&
                HasOrigin(origins, BrowserBridge.EdgeStoreExtensionOrigin);
        }
        catch (JsonException)
        {
            return false;
        }
    }

    private static bool HasOrigin(JsonElement origins, string origin) =>
        origins.EnumerateArray().Any(o =>
            o.ValueKind == JsonValueKind.String &&
            string.Equals(o.GetString(), origin, StringComparison.OrdinalIgnoreCase));

    /// <summary>The program a manifest names, resolved against the manifest's folder; null when it names none.</summary>
    public static string? ManifestExecutable(string? manifestText, string manifestPath)
    {
        if (string.IsNullOrWhiteSpace(manifestText))
        {
            return null;
        }

        try
        {
            using var document = JsonDocument.Parse(manifestText);
            if (document.RootElement.ValueKind != JsonValueKind.Object ||
                !document.RootElement.TryGetProperty("path", out var path) ||
                path.ValueKind != JsonValueKind.String ||
                string.IsNullOrWhiteSpace(path.GetString()))
            {
                return null;
            }

            return Path.Combine(Path.GetDirectoryName(manifestPath) ?? string.Empty, path.GetString()!);
        }
        catch (JsonException)
        {
            return null;
        }
    }

    /// <summary>
    /// Returns the manifest path to write under one browser's key, copied from the other browser's
    /// working key, or null when nothing should be written: the source is not usable, or the target
    /// already has a registration whose manifest exists (ours or someone else's, and not ours to replace).
    /// </summary>
    public static string? PlanKeyRepair(string? sourceKeyValue, bool sourceUsable, string? targetKeyValue, bool targetManifestExists)
    {
        if (!sourceUsable || string.IsNullOrWhiteSpace(sourceKeyValue))
        {
            return null;
        }

        if (!string.IsNullOrWhiteSpace(targetKeyValue) && targetManifestExists)
        {
            return null;
        }

        return sourceKeyValue;
    }
}

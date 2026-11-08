using System.Security;

namespace DownloadManager.Contracts;

/// <summary>The registry and files behind native-host registration, so the logic can run without them.</summary>
public interface INativeHostRegistrationStore
{
    /// <summary>The default value of an HKCU key; null when the key is missing or has no value.</summary>
    string? ReadKeyValue(string keyPath);

    /// <summary>The text of a file; null when it is missing or unreadable.</summary>
    string? ReadTextFile(string path);

    bool FileExists(string path);

    void WriteKeyValue(string keyPath, string value);
}

/// <summary>Reads the state of each browser's key and repairs a missing one at Host start.</summary>
public static class NativeHostRegistrar
{
    public static NativeHostRegistrationState Read(INativeHostRegistrationStore store) => new(
        ReadKey(store, BrowserBridge.ChromeNativeHostKeyPath),
        ReadKey(store, BrowserBridge.EdgeNativeHostKeyPath));

    /// <summary>
    /// Setup of 0.4.0 and earlier wrote only the Chrome key, and a cleaner can remove either one. When one
    /// browser's key works and the other's is missing or points at a file that is gone, writes the other
    /// one beside it, pointing at the same manifest. Best effort: a failure is returned for the log and
    /// never changes the reported state.
    /// </summary>
    /// <param name="isolatedDataDirectory">
    /// The DOWNLOADMANAGER_DATA override. Isolated test or portable state must not write the installed
    /// user's browser registration, so a set value means nothing is read or written.
    /// </param>
    public static string? TryRepair(INativeHostRegistrationStore store, string? isolatedDataDirectory)
    {
        if (!string.IsNullOrWhiteSpace(isolatedDataDirectory))
        {
            return null;
        }

        try
        {
            var chromeValue = store.ReadKeyValue(BrowserBridge.ChromeNativeHostKeyPath);
            var edgeValue = store.ReadKeyValue(BrowserBridge.EdgeNativeHostKeyPath);
            var chromeUsable = Evaluate(store, chromeValue) == NativeHostKeyState.Usable;
            var edgeUsable = Evaluate(store, edgeValue) == NativeHostKeyState.Usable;

            var edgePlan = NativeHostRegistrationState.PlanKeyRepair(chromeValue, chromeUsable, edgeValue, ManifestExists(store, edgeValue));
            if (edgePlan is not null)
            {
                store.WriteKeyValue(BrowserBridge.EdgeNativeHostKeyPath, edgePlan);
            }

            var chromePlan = NativeHostRegistrationState.PlanKeyRepair(edgeValue, edgeUsable, chromeValue, ManifestExists(store, chromeValue));
            if (chromePlan is not null)
            {
                store.WriteKeyValue(BrowserBridge.ChromeNativeHostKeyPath, chromePlan);
            }

            return null;
        }
        catch (Exception ex) when (IsAccessFailure(ex))
        {
            return ex.Message;
        }
    }

    private static NativeHostKeyState ReadKey(INativeHostRegistrationStore store, string keyPath)
    {
        try
        {
            return Evaluate(store, store.ReadKeyValue(keyPath));
        }
        catch (Exception ex) when (IsAccessFailure(ex))
        {
            return NativeHostKeyState.Broken;
        }
    }

    private static NativeHostKeyState Evaluate(INativeHostRegistrationStore store, string? keyValue)
    {
        if (string.IsNullOrWhiteSpace(keyValue))
        {
            return NativeHostKeyState.Absent;
        }

        var manifest = store.ReadTextFile(keyValue);
        var executable = NativeHostRegistrationState.ManifestExecutable(manifest, keyValue);
        return NativeHostRegistrationState.EvaluateKey(keyValue, manifest, executable is not null && store.FileExists(executable));
    }

    private static bool ManifestExists(INativeHostRegistrationStore store, string? keyValue) =>
        !string.IsNullOrWhiteSpace(keyValue) && store.FileExists(keyValue);

    private static bool IsAccessFailure(Exception ex) =>
        ex is IOException or UnauthorizedAccessException or SecurityException;
}

using DownloadManager.Contracts;
using Microsoft.Win32;

namespace DownloadManager.Host;

public static class NativeHostRegistration
{
    public static NativeHostRegistrationState Read() => NativeHostRegistrationState.Evaluate(
        ReadManifest(BrowserBridge.ChromeNativeHostKeyPath),
        ReadManifest(BrowserBridge.EdgeNativeHostKeyPath));

    public static bool IsRegistered() => Read().IsRegistered;

    /// <summary>
    /// Setup of 0.4.0 and earlier wrote only the Chrome key. Writes the Edge key beside it, pointing at
    /// the same manifest, once per start. Best effort: a failure is returned for the log and never
    /// changes the reported state.
    /// </summary>
    public static string? TryRepairEdgeKey()
    {
        try
        {
            var chromeValue = ReadKeyValue(BrowserBridge.ChromeNativeHostKeyPath);
            var edgeValue = ReadKeyValue(BrowserBridge.EdgeNativeHostKeyPath);
            var plan = NativeHostRegistrationState.PlanEdgeRepair(
                chromeValue,
                NativeHostRegistrationState.IsManifestUsable(ReadFile(chromeValue)),
                edgeValue,
                !string.IsNullOrWhiteSpace(edgeValue) && File.Exists(edgeValue));
            if (plan is null)
            {
                return null;
            }

            using var key = Registry.CurrentUser.CreateSubKey(BrowserBridge.EdgeNativeHostKeyPath, writable: true);
            key?.SetValue(null, plan, RegistryValueKind.String);
            return null;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Security.SecurityException)
        {
            return ex.Message;
        }
    }

    private static string? ReadKeyValue(string path)
    {
        using var key = Registry.CurrentUser.OpenSubKey(path);
        return key?.GetValue(null) as string;
    }

    private static string? ReadManifest(string path)
    {
        try
        {
            return ReadFile(ReadKeyValue(path));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Security.SecurityException)
        {
            return null;
        }
    }

    private static string? ReadFile(string? manifestPath)
    {
        if (string.IsNullOrWhiteSpace(manifestPath) || !File.Exists(manifestPath))
        {
            return null;
        }

        try
        {
            return File.ReadAllText(manifestPath);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }
}

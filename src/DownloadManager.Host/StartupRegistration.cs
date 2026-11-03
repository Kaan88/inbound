using Microsoft.Win32;

namespace DownloadManager.Host;

public static class StartupRegistration
{
    private const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";
    private const string ValueName = "DownloadManagerHost";

    public static bool IsEnabled()
    {
        using var key = Registry.CurrentUser.OpenSubKey(RunKey);
        return key?.GetValue(ValueName) is string value && !string.IsNullOrWhiteSpace(value);
    }

    public static void Apply(bool enabled, string hostExecutable)
    {
        using var key = Registry.CurrentUser.CreateSubKey(RunKey, writable: true);
        if (key is null)
        {
            return;
        }

        if (enabled)
        {
            key.SetValue(ValueName, $"\"{hostExecutable}\" --background", RegistryValueKind.String);
        }
        else
        {
            key.DeleteValue(ValueName, throwOnMissingValue: false);
        }
    }

    public static string HostExecutablePath()
    {
        var local = Path.Combine(AppContext.BaseDirectory, "DownloadManager.Host.exe");
        if (File.Exists(local))
        {
            return local;
        }

        return Environment.ProcessPath ?? local;
    }
}

using DownloadManager.Contracts;
using Microsoft.Win32;

namespace DownloadManager.Host;

/// <summary>Connects <see cref="NativeHostRegistrar"/> to the real registry and files.</summary>
public static class NativeHostRegistration
{
    private static readonly RegistryStore Store = new();

    public static NativeHostRegistrationState Read() => NativeHostRegistrar.Read(Store);

    public static string? TryRepair() =>
        NativeHostRegistrar.TryRepair(Store, Environment.GetEnvironmentVariable("DOWNLOADMANAGER_DATA"));

    private sealed class RegistryStore : INativeHostRegistrationStore
    {
        public string? ReadKeyValue(string keyPath)
        {
            using var key = Registry.CurrentUser.OpenSubKey(keyPath);
            return key?.GetValue(null) as string;
        }

        public string? ReadTextFile(string path)
        {
            try
            {
                return File.Exists(path) ? File.ReadAllText(path) : null;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                return null;
            }
        }

        public bool FileExists(string path) => File.Exists(path);

        public void WriteKeyValue(string keyPath, string value)
        {
            using var key = Registry.CurrentUser.CreateSubKey(keyPath, writable: true);
            key.SetValue(null, value, RegistryValueKind.String);
        }
    }
}

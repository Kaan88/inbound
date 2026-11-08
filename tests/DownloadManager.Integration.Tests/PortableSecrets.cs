using System.Runtime.CompilerServices;
using DownloadManager.Persistence;

namespace DownloadManager.Integration.Tests;

/// <summary>
/// Windows seals secrets with DPAPI on its own. Elsewhere nothing seals them until a server picks its key file, and
/// saving a login would throw, so this test run gets a key file of its own, as the server does at start.
/// </summary>
internal static class PortableSecrets
{
    [ModuleInitializer]
    internal static void UseAKeyFileOffWindows()
    {
        if (OperatingSystem.IsWindows()) return;
        var keyFile = Path.Combine(Path.GetTempPath(), "inbound-tests-" + Environment.ProcessId, AesGcmSecretProtector.DefaultFileName);
        SecretProtector.Use(AesGcmSecretProtector.OpenOrCreate(keyFile));
    }
}

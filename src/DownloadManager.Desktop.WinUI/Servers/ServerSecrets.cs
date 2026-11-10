using DownloadManager.Persistence;

namespace DownloadManager.Desktop.WinUI;

/// <summary>
/// What seals a saved server's token. Windows: DPAPI for this user. Linux: the Host's own key, from its key file in the
/// data folder or, without one, the desktop keyring, opened when first needed and never made here (a blob sealed with
/// another key in the data folder would stop the Host's next start). Without the Host's key there yet, saving a server
/// says so instead of failing silently.
/// </summary>
internal sealed class ServerSecrets(string dataFolder) : ISecretProtector
{
    private ISecretProtector? _opened;

    internal static ISecretProtector For(string dataFolder) =>
        OperatingSystem.IsWindows() ? new DpapiSecretProtector() : new ServerSecrets(dataFolder);

    public string Prefix => AesGcmPrefix;

    private const string AesGcmPrefix = "aesgcm:v1:";

    public string Protect(string plaintext, ReadOnlySpan<byte> purpose) => Open().Protect(plaintext, purpose);

    public string? Unprotect(string protectedValue, ReadOnlySpan<byte> purpose) =>
        string.IsNullOrEmpty(protectedValue) ? null : Open().Unprotect(protectedValue, purpose);

    private ISecretProtector Open()
    {
        if (_opened is not null) return _opened;
        var keyFile = Path.Combine(dataFolder, AesGcmSecretProtector.DefaultFileName);
        if (File.Exists(keyFile)) return _opened = AesGcmSecretProtector.OpenOrCreate(keyFile);
#if HAS_UNO
        using var keyring = DownloadManager.Host.SecretServiceKeyStore.TryOpen(dataFolder, UiTrace.Write);
        if (keyring is not null && AesGcmSecretProtector.OpenExisting(keyring, UiTrace.Write) is { } fromKeyring)
            return _opened = fromKeyring;
#endif
        throw new IOException("The app's secret key isn't there yet. Start Inbound once (its background service makes the key), then add the server again.");
    }
}

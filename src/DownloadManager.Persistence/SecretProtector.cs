using System.Text;

namespace DownloadManager.Persistence;

/// <summary>
/// Seals every secret the stores keep: site-login and proxy passwords, request headers, protected addresses, provider
/// keys, remote inputs and archive passwords. Each kind has its own purpose bytes, so a blob of one kind never opens as
/// another. Plaintext never enters the JSON files, and a value that doesn't open reads as nothing, never as itself.
/// The sealing itself is an <see cref="ISecretProtector"/>, one per process: Windows DPAPI by default on Windows
/// (<see cref="DpapiSecretProtector"/>); a server picks the AES-GCM key file in its data folder
/// (<see cref="AesGcmSecretProtector"/>) with <see cref="Use"/> before any store opens. Off Windows nothing is chosen by
/// default, and sealing then throws rather than write a secret in the clear.
/// </summary>
public static class SecretProtector
{
    private static ISecretProtector _current = OperatingSystem.IsWindows()
        ? new DpapiSecretProtector()
        : new UnavailableSecretProtector();

    /// <summary>What seals and opens secrets in this process.</summary>
    public static ISecretProtector Current => Volatile.Read(ref _current);

    /// <summary>
    /// Chooses the protector for this process. Call before any store opens: a value sealed by one protector doesn't open
    /// with another. Tests that need one (Linux, a server) call it once per process.
    /// </summary>
    public static void Use(ISecretProtector protector)
    {
        ArgumentNullException.ThrowIfNull(protector);
        Volatile.Write(ref _current, protector);
    }

    private static readonly byte[] Entropy =
        Encoding.UTF8.GetBytes("DownloadManager.SiteLogin.v1");

    private static readonly byte[] ProxyEntropy =
        Encoding.UTF8.GetBytes("DownloadManager.Proxy.v1");
    private static readonly byte[] HeaderEntropy =
        Encoding.UTF8.GetBytes("DownloadManager.RequestHeader.v1");
    private static readonly byte[] UrlEntropy =
        Encoding.UTF8.GetBytes("DownloadManager.ProtectedUrl.v1");
    private static readonly byte[] ProviderKeyEntropy =
        Encoding.UTF8.GetBytes("DownloadManager.ProviderKey.v1");
    private static readonly byte[] RemoteInputEntropy =
        Encoding.UTF8.GetBytes("DownloadManager.RemoteInput.v1");
    private static readonly byte[] RemoteTorrentEntropy =
        Encoding.UTF8.GetBytes("DownloadManager.RemoteTorrent.v1");
    private static readonly byte[] RemoteLinkPasswordEntropy =
        Encoding.UTF8.GetBytes("DownloadManager.RemoteLinkPassword.v1");
    private static readonly byte[] RemoteArchivePasswordEntropy =
        Encoding.UTF8.GetBytes("DownloadManager.RemoteArchivePassword.v1");

    public static string ProtectHeader(string plaintext) => Protect(plaintext, HeaderEntropy);
    public static string? UnprotectHeader(string? protectedValue) => Unprotect(protectedValue, HeaderEntropy);

    public static string ProtectUrl(string plaintext) => Protect(plaintext, UrlEntropy);
    public static string? UnprotectUrl(string? protectedValue) => Unprotect(protectedValue, UrlEntropy);

    /// <summary>A provider account's API key. Its own entropy, so no other stored blob can stand in for it.</summary>
    public static string ProtectProviderKey(string plaintext) => Protect(plaintext, ProviderKeyEntropy);
    public static string? UnprotectProviderKey(string? protectedValue) => Unprotect(protectedValue, ProviderKeyEntropy);

    /// <summary>What a user gave a remote job: a magnet or a hoster link. It can hold a decryption key.</summary>
    public static string ProtectRemoteInput(string plaintext) => Protect(plaintext, RemoteInputEntropy);
    public static string? UnprotectRemoteInput(string? protectedValue) => Unprotect(protectedValue, RemoteInputEntropy);

    /// <summary>A .torrent file a remote job submits. It names the content and any private tracker's passkey.</summary>
    public static string ProtectRemoteTorrent(byte[] plaintext) => Protect(Convert.ToBase64String(plaintext), RemoteTorrentEntropy);

    public static byte[]? UnprotectRemoteTorrent(string? protectedValue)
    {
        var text = Unprotect(protectedValue, RemoteTorrentEntropy);
        if (text is null)
        {
            return null;
        }

        try
        {
            return Convert.FromBase64String(text);
        }
        catch (FormatException)
        {
            return null;
        }
    }

    /// <summary>The password a source site wants for a link. Never an archive password.</summary>
    public static string ProtectRemoteLinkPassword(string plaintext) => Protect(plaintext, RemoteLinkPasswordEntropy);
    public static string? UnprotectRemoteLinkPassword(string? protectedValue) => Unprotect(protectedValue, RemoteLinkPasswordEntropy);

    /// <summary>An archive password a remote job holds until it has a package. Its own purpose: it is not a link password.</summary>
    public static string ProtectRemoteArchivePassword(string plaintext) => Protect(plaintext, RemoteArchivePasswordEntropy);
    public static string? UnprotectRemoteArchivePassword(string? protectedValue) => Unprotect(protectedValue, RemoteArchivePasswordEntropy);

    public static string Protect(string plaintext) => Protect(plaintext, Entropy);

    public static string? Unprotect(string? protectedValue) => Unprotect(protectedValue, Entropy);

    /// <summary>Proxy passwords use separate entropy so blobs are not interchangeable with site logins.</summary>
    public static string ProtectProxy(string plaintext) => Protect(plaintext, ProxyEntropy);

    public static string? UnprotectProxy(string? protectedValue) => Unprotect(protectedValue, ProxyEntropy);

    private static readonly byte[] ArchivePasswordEntropy =
        Encoding.UTF8.GetBytes("DownloadManager.ArchivePassword.v1");

    /// <summary>Extraction passwords, in their own purpose so a blob cannot be read as any other kind of secret.</summary>
    public static string ProtectArchivePassword(string plaintext) => Protect(plaintext, ArchivePasswordEntropy);

    public static string? UnprotectArchivePassword(string? protectedValue) => Unprotect(protectedValue, ArchivePasswordEntropy);

    /// <summary>Whether a value is a sealed blob of any protector this app has used, so it is never sealed twice.</summary>
    public static bool IsProtected(string? value) =>
        value is not null &&
        (value.StartsWith(DpapiSecretProtector.BlobPrefix, StringComparison.Ordinal) ||
         value.StartsWith(AesGcmSecretProtector.BlobPrefix, StringComparison.Ordinal));

    private static string Protect(string plaintext, byte[] entropy) => Current.Protect(plaintext, entropy);

    private static string? Unprotect(string? protectedValue, byte[] entropy) =>
        string.IsNullOrWhiteSpace(protectedValue) ? null : Current.Unprotect(protectedValue, entropy);
}

/// <summary>Off Windows until a server chooses its key file: nothing is sealed, so nothing is written in the clear.</summary>
internal sealed class UnavailableSecretProtector : ISecretProtector
{
    public string Prefix => "none:";

    public string Protect(string plaintext, ReadOnlySpan<byte> purpose) =>
        throw new InvalidOperationException(
            "No secret protector is set up on this system, so a password or key can't be saved. The server sets one from its key file at start.");

    public string? Unprotect(string protectedValue, ReadOnlySpan<byte> purpose) => null;
}

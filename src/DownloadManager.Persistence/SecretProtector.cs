using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;

namespace DownloadManager.Persistence;

/// <summary>
/// Per-user Windows DPAPI protection for stored site-login passwords. The
/// ciphertext is bound to the current Windows user and to an application
/// entropy value; plaintext never enters settings JSON.
/// </summary>
public static class SecretProtector
{
    private const string Prefix = "dpapi:v1:";
    private const int CryptProtectUiForbidden = 0x1;

    private static readonly byte[] Entropy =
        Encoding.UTF8.GetBytes("DownloadManager.SiteLogin.v1");

    private static readonly byte[] ProxyEntropy =
        Encoding.UTF8.GetBytes("DownloadManager.Proxy.v1");
    private static readonly byte[] HeaderEntropy =
        Encoding.UTF8.GetBytes("DownloadManager.RequestHeader.v1");
    private static readonly byte[] UrlEntropy =
        Encoding.UTF8.GetBytes("DownloadManager.ProtectedUrl.v1");

    public static string ProtectHeader(string plaintext) => Protect(plaintext, HeaderEntropy);
    public static string? UnprotectHeader(string? protectedValue) => Unprotect(protectedValue, HeaderEntropy);

    public static string ProtectUrl(string plaintext) => Protect(plaintext, UrlEntropy);
    public static string? UnprotectUrl(string? protectedValue) => Unprotect(protectedValue, UrlEntropy);

    public static bool IsProtected(string? value) => value?.StartsWith(Prefix, StringComparison.Ordinal) == true;

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

    private static string Protect(string plaintext, byte[] entropy)
    {
        var cipher = Transform(Encoding.UTF8.GetBytes(plaintext), protect: true, entropy);
        return Prefix + Convert.ToBase64String(cipher);
    }

    private static string? Unprotect(string? protectedValue, byte[] entropy)
    {
        if (string.IsNullOrWhiteSpace(protectedValue) ||
            !protectedValue.StartsWith(Prefix, StringComparison.Ordinal))
        {
            return null;
        }

        try
        {
            var cipher = Convert.FromBase64String(protectedValue[Prefix.Length..]);
            return Encoding.UTF8.GetString(Transform(cipher, protect: false, entropy));
        }
        catch (Exception ex) when (ex is CryptographicException or FormatException or ArgumentException)
        {
            return null;
        }
    }

    private static byte[] Transform(byte[] input, bool protect, byte[] entropy)
    {
        var inputBlob = CreateBlob(input);
        var entropyBlob = CreateBlob(entropy);
        try
        {
            DataBlob output;
            var ok = protect
                ? CryptProtectData(ref inputBlob, "Download Manager site login", ref entropyBlob,
                    IntPtr.Zero, IntPtr.Zero, CryptProtectUiForbidden, out output)
                : CryptUnprotectData(ref inputBlob, IntPtr.Zero, ref entropyBlob,
                    IntPtr.Zero, IntPtr.Zero, CryptProtectUiForbidden, out output);
            if (!ok)
            {
                throw new CryptographicException(Marshal.GetLastWin32Error());
            }

            try
            {
                var result = new byte[output.Length];
                Marshal.Copy(output.Data, result, 0, output.Length);
                return result;
            }
            finally
            {
                LocalFree(output.Data);
            }
        }
        finally
        {
            FreeBlob(inputBlob);
            FreeBlob(entropyBlob);
        }
    }

    private static DataBlob CreateBlob(byte[] bytes)
    {
        var data = Marshal.AllocHGlobal(bytes.Length);
        Marshal.Copy(bytes, 0, data, bytes.Length);
        return new DataBlob { Length = bytes.Length, Data = data };
    }

    private static void FreeBlob(DataBlob blob)
    {
        if (blob.Data != IntPtr.Zero)
        {
            Marshal.FreeHGlobal(blob.Data);
        }
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct DataBlob
    {
        public int Length;
        public IntPtr Data;
    }

    [DllImport("crypt32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern bool CryptProtectData(
        ref DataBlob dataIn,
        string? description,
        ref DataBlob entropy,
        IntPtr reserved,
        IntPtr prompt,
        int flags,
        out DataBlob dataOut);

    [DllImport("crypt32.dll", SetLastError = true)]
    private static extern bool CryptUnprotectData(
        ref DataBlob dataIn,
        IntPtr description,
        ref DataBlob entropy,
        IntPtr reserved,
        IntPtr prompt,
        int flags,
        out DataBlob dataOut);

    [DllImport("kernel32.dll")]
    private static extern IntPtr LocalFree(IntPtr memory);
}

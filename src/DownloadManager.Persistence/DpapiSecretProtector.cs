using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Security.Cryptography;
using System.Text;

namespace DownloadManager.Persistence;

/// <summary>Seals and opens one secret. One per process: see <see cref="SecretProtector.Use"/>.</summary>
public interface ISecretProtector
{
    /// <summary>The start of every blob this protector writes, so a blob says which protector it needs.</summary>
    string Prefix { get; }

    /// <summary>Seals <paramref name="plaintext"/> for one purpose. Throws when it can't, and never returns the plaintext.</summary>
    string Protect(string plaintext, ReadOnlySpan<byte> purpose);

    /// <summary>
    /// The plaintext, or null when the value doesn't open here: another protector's blob, another purpose, another key,
    /// or damage. Never the value itself.
    /// </summary>
    string? Unprotect(string protectedValue, ReadOnlySpan<byte> purpose);
}

/// <summary>
/// Per-user Windows DPAPI. The ciphertext is bound to the current Windows user and to the purpose bytes as entropy.
/// The desktop Host's protector, and the default on Windows.
/// </summary>
public sealed class DpapiSecretProtector : ISecretProtector
{
    public const string BlobPrefix = "dpapi:v1:";
    private const int CryptProtectUiForbidden = 0x1;

    public string Prefix => BlobPrefix;

    [SupportedOSPlatform("windows")]
    public string Protect(string plaintext, ReadOnlySpan<byte> purpose)
    {
        var cipher = Transform(Encoding.UTF8.GetBytes(plaintext), protect: true, purpose.ToArray());
        return BlobPrefix + Convert.ToBase64String(cipher);
    }

    [SupportedOSPlatform("windows")]
    public string? Unprotect(string protectedValue, ReadOnlySpan<byte> purpose)
    {
        if (!protectedValue.StartsWith(BlobPrefix, StringComparison.Ordinal))
        {
            return null;
        }

        try
        {
            var cipher = Convert.FromBase64String(protectedValue[BlobPrefix.Length..]);
            return Encoding.UTF8.GetString(Transform(cipher, protect: false, purpose.ToArray()));
        }
        catch (Exception ex) when (ex is CryptographicException or FormatException or ArgumentException)
        {
            return null;
        }
    }

    [SupportedOSPlatform("windows")]
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

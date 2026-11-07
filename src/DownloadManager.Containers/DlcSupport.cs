using System.Text;

namespace DownloadManager.Containers;

/// <summary>What can be said about a DLC file without opening it.</summary>
/// <param name="IsRecognised">The bytes have the shape of a DLC file. Nothing has been decrypted.</param>
/// <param name="PayloadBytes">Size of the encrypted payload, when recognised.</param>
public sealed record DlcDescription(bool IsRecognised, int? PayloadBytes, string Explanation)
{
    public bool CanOpen => false;
}

/// <summary>
/// DLC cannot be opened here, by decision. Its content key can only be unlocked by JDownloader's online service,
/// and that service encrypts its reply for a client named in the request. So any standalone reader would have to
/// present itself as JDownloader or as another client, using that client's key. No network call is made here.
/// </summary>
public static class DlcSupport
{
    public const string Explanation =
        "DLC files can't be opened by this app. A DLC needs JDownloader's online service to unlock it, and that service " +
        "only answers JDownloader or a client it has given a key to. Impersonating JDownloader or taking a key out of it " +
        "isn't acceptable, so this app does neither. If JDownloader is running, you can send the file there instead: it " +
        "will ask you to confirm, and the links stay in JDownloader.";

    private const int KeyTailChars = 88;
    private const int KeyTailBytes = 64;
    private const int PayloadBlockBytes = 16;

    public static DlcDescription Describe(byte[]? bytes = null) =>
        bytes is not null && bytes.Length <= ContainerLimits.Default.MaxInputBytes && TryRecognise(bytes, out var payloadBytes)
            ? new DlcDescription(true, payloadBytes, Explanation)
            : new DlcDescription(false, null, Explanation);

    internal static ContainerImportResult ReadForImport(byte[] data, ContainerLimits limits)
    {
        var builder = new ContainerResultBuilder(ContainerFormat.Dlc, limits);
        if (!TryRecognise(data, out _))
        {
            builder.AddProblem(ContainerProblemKind.Malformed, "This file is named DLC but does not have the shape of a DLC file.");
        }

        builder.AddProblem(ContainerProblemKind.NotSupported, Explanation);
        return builder.Build();
    }

    /// <summary>A DLC is base64 text whose last 88 characters are a 64-byte key tail, after a payload of whole 16-byte blocks.</summary>
    internal static bool TryRecognise(ReadOnlySpan<byte> data, out int payloadBytes)
    {
        payloadBytes = 0;
        var text = new StringBuilder(data.Length);
        foreach (var value in ContainerText.SkipBom(data))
        {
            if (ContainerText.IsWhitespace(value))
            {
                continue;
            }

            if (!char.IsAsciiLetterOrDigit((char)value) && value is not ((byte)'+' or (byte)'/' or (byte)'='))
            {
                return false;
            }

            text.Append((char)value);
        }

        if (text.Length <= KeyTailChars)
        {
            return false;
        }

        var all = text.ToString();
        var payload = all[..^KeyTailChars];
        var tail = all[^KeyTailChars..];

        Span<byte> tailBuffer = stackalloc byte[KeyTailBytes];
        if (!Convert.TryFromBase64String(tail, tailBuffer, out var tailWritten) || tailWritten != KeyTailBytes)
        {
            return false;
        }

        var payloadBuffer = new byte[(payload.Length / 4 * 3) + 3];
        if (!Convert.TryFromBase64String(payload, payloadBuffer, out var written) || written == 0 || written % PayloadBlockBytes != 0)
        {
            return false;
        }

        payloadBytes = written;
        return true;
    }
}

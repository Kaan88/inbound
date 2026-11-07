using System.Numerics;
using System.Security.Cryptography;
using System.Text;

namespace DownloadManager.Containers.Tests;

/// <summary>
/// Builds container files for the tests, from the same constants and format description the readers use.
/// These fixtures prove self-consistency only: no real RSDF or CCF sample file was available, so nothing here
/// shows that the readers open a file produced by other software. The encoders are written differently from the
/// decoders on purpose (RSDF uses the platform's CFB-8, CCF 3.0 uses naive loops), which catches slips in the
/// optimised decoders but not a misunderstanding of the format itself.
/// </summary>
internal static class ContainerFixtures
{
    public const string UrlA = "https://files.example.test/alpha/one.rar";
    public const string UrlB = "https://files.example.test/alpha/two.rar";
    public const string UrlC = "http://mirror.example.test/beta/three.zip?token=abc&x=1";
    public const string Password = "Fixture-Pw_123";

    public static string CryptLoadXml(params string[] packages) =>
        $"""<?xml version="1.0" encoding="utf-8"?><CryptLoad>{string.Concat(packages)}</CryptLoad>""";

    public static string PackageXml(string? name, string? password, string? comment, params string[] downloads)
    {
        var options = password is null && comment is null
            ? string.Empty
            : $"<Options>{(comment is null ? string.Empty : $"<Kommentar>{comment}</Kommentar>")}{(password is null ? string.Empty : $"<Passwort>{password}</Passwort>")}</Options>";
        var nameAttribute = name is null ? string.Empty : $" name=\"{name}\"";
        return $"<Package service=\"\" url=\"Directlinks\"{nameAttribute}>{options}{string.Concat(downloads)}</Package>";
    }

    public static string DownloadXml(string url, string? fileName = null, string? size = null) =>
        $"<Download Url=\"{Escape(url)}\"><Url>{Escape(url)}</Url>"
        + (fileName is null ? string.Empty : $"<FileName>{fileName}</FileName>")
        + (size is null ? string.Empty : $"<FileSize>{size}</FileSize>")
        + "</Download>";

    public static string Escape(string text) => text.Replace("&", "&amp;");

    public static byte[] Rsdf(params string[] links) => RsdfWith("\n", resetPerLine: false, links.Select(l => $"CCF: {l}").ToArray());

    /// <summary>Encrypts with one register across all lines, unless resetPerLine shows what the wrong implementation would make.</summary>
    public static byte[] RsdfWith(string separator, bool resetPerLine, params string[] plainLines)
    {
        var lines = RsdfCipherLines(resetPerLine, plainLines);
        return ToHexFile(string.Join(separator, lines));
    }

    public static string[] RsdfCipherLines(bool resetPerLine, params string[] plainLines)
    {
        using var aes = Aes.Create();
        aes.Key = RsdfReader.Key;
        var iv = RsdfReader.InitialRegister();

        var plains = plainLines.Select(Encoding.UTF8.GetBytes).ToArray();
        if (resetPerLine)
        {
            return plains.Select(p => Convert.ToBase64String(aes.EncryptCfb(p, iv, PaddingMode.None, 8))).ToArray();
        }

        var joined = aes.EncryptCfb(plains.SelectMany(p => p).ToArray(), iv, PaddingMode.None, 8);
        var lines = new string[plains.Length];
        var offset = 0;
        for (var i = 0; i < plains.Length; i++)
        {
            lines[i] = Convert.ToBase64String(joined, offset, plains[i].Length);
            offset += plains[i].Length;
        }

        return lines;
    }

    public static byte[] ToHexFile(string decoded) => ToHexFile(Encoding.Latin1.GetBytes(decoded));

    public static byte[] ToHexFile(byte[] decoded) => Encoding.ASCII.GetBytes(Convert.ToHexString(decoded));

    public static byte[] CcfLegacy(string xml, int pairIndex = 0)
    {
        var pair = CcfFormatConstants.Legacy[pairIndex];
        return EncryptCbc(PadToBlock(xml), pair);
    }

    public static byte[] Ccf50(string xml)
    {
        var data = EncryptCbc(PadToBlock(xml), CcfFormatConstants.Ccf50Outer);
        data = EncryptCbc(data, CcfFormatConstants.Ccf50Inner);
        return EncryptCbc(data, CcfFormatConstants.Ccf50Outer);
    }

    public static byte[] Ccf30(string xml, uint key = 0x89ABCDEF) => Ccf30Bytes(Encoding.UTF8.GetBytes(xml), key);

    public static byte[] Ccf30Bytes(byte[] plain, uint key)
    {
        var blockCount = Math.Max(1, (plain.Length + 63) / 64);
        var lastLength = plain.Length == 0 ? 0 : plain.Length - ((blockCount - 1) * 64);
        var output = new byte[11 + (blockCount * 64)];
        "CCF3.0"u8.CopyTo(output);
        BitConverter.TryWriteBytes(output.AsSpan(6, 4), key);
        output[10] = (byte)lastLength;

        var state = key;
        for (var blockIndex = 0; blockIndex < blockCount; blockIndex++)
        {
            var block = new byte[64];
            var take = Math.Min(64, plain.Length - (blockIndex * 64));
            plain.AsSpan(blockIndex * 64, take).CopyTo(block);

            // The key schedule ignores the data, so it can be run forward first and the steps undone in reverse.
            var byteKeys = new byte[64];
            for (var i = 0; i < 64; i++)
            {
                state = (state & 1) != 0 ? RotR(state, (state & 0xFF) % 12) : RotL(state, (state & 0xFF) % 9);
                byteKeys[i] = (byte)state;
            }

            var bitKeys = new byte[64];
            for (var i = 0; i < 64; i++)
            {
                bitKeys[i] = (byte)state;
                state = (state & 1) != 0 ? RotR(state, state & 12) : RotL(state, state & 9);
            }

            for (var row = 0; row < 8; row++)
            {
                var bits = block.AsSpan(row * 8, 8).ToArray();
                for (var m = 7; m >= 0; m--)
                {
                    bits = BitMatrixFlip(bits);
                    bits[m] ^= bitKeys[(row * 8) + m];
                }

                bits.CopyTo(block, row * 8);
            }

            for (var i = 63; i >= 0; i--)
            {
                block = ByteMatrixFlip(block);
                block[i] ^= byteKeys[i];
            }

            block.CopyTo(output, 11 + (blockIndex * 64));
        }

        return output;
    }

    public static string Dlc(int payloadBlocks = 4)
    {
        var random = new Random(1234);
        var payload = new byte[payloadBlocks * 16];
        var tail = new byte[64];
        random.NextBytes(payload);
        random.NextBytes(tail);
        return Convert.ToBase64String(payload) + Convert.ToBase64String(tail);
    }

    private static byte[] PadToBlock(string xml)
    {
        var plain = Encoding.UTF8.GetBytes(xml);
        var padded = new byte[(plain.Length + 15) / 16 * 16];
        plain.CopyTo(padded, 0);
        return padded;
    }

    private static byte[] EncryptCbc(byte[] plain, CcfKeyPair pair)
    {
        using var aes = Aes.Create();
        aes.Key = pair.Key;
        return aes.EncryptCbc(plain, pair.Iv, PaddingMode.None);
    }

    private static uint RotL(uint value, uint steps) => BitOperations.RotateLeft(value, (int)steps);

    private static uint RotR(uint value, uint steps) => BitOperations.RotateRight(value, (int)steps);

    private static byte[] ByteMatrixFlip(byte[] block)
    {
        var flipped = new byte[64];
        for (var i = 0; i < 8; i++)
        {
            for (var j = 0; j < 8; j++)
            {
                flipped[(i * 8) + j] = block[(j * 8) + i];
            }
        }

        return flipped;
    }

    private static byte[] BitMatrixFlip(byte[] row)
    {
        var flipped = new byte[8];
        for (var i = 0; i < 8; i++)
        {
            for (var j = 0; j < 8; j++)
            {
                if ((row[i] & (1 << j)) != 0)
                {
                    flipped[j] |= (byte)(1 << i);
                }
            }
        }

        return flipped;
    }
}

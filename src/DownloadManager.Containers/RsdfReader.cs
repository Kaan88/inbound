using System.Buffers;
using System.Buffers.Text;
using System.Security.Cryptography;
using System.Text;

namespace DownloadManager.Containers;

/// <summary>
/// RSDF: hex text wrapping base64 lines, each AES-192 in CFB-8. It carries bare links, with no packages,
/// names or passwords.
/// </summary>
internal static class RsdfReader
{
    // Key and start value: https://raw.githubusercontent.com/pyload/pyload/stable/module/plugins/container/RSDF.py
    // The working register is the start value encrypted once under the key, which gives the value published at
    // https://raw.githubusercontent.com/C-Sparrow/Tartana/master/src/Tartana/Component/Decrypter/Rsdf.php
    private const string KeyHex = "8C35192D964DC3182C6F84F3252239EB4A320D2500000000";
    private const string RegisterSeedHex = "FFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFF";

    private const byte AlternateSeparator = 0xDA;
    private const string LinkPrefix = "CCF:";
    private const int BlockBytes = 16;

    private static readonly UTF8Encoding StrictUtf8 = new(encoderShouldEmitUTF8Identifier: false, throwOnInvalidBytes: true);

    internal static byte[] Key => Convert.FromHexString(KeyHex);

    internal static byte[] InitialRegister()
    {
        using var aes = Aes.Create();
        aes.Key = Key;
        return aes.EncryptEcb(Convert.FromHexString(RegisterSeedHex), PaddingMode.None);
    }

    public static ContainerImportResult Read(byte[] data, ContainerLimits limits)
    {
        var builder = new ContainerResultBuilder(ContainerFormat.Rsdf, limits);
        var decoded = DecodeHex(data, builder);
        if (decoded.Length == 0)
        {
            return builder.Build();
        }

        var separator = decoded.AsSpan().Contains(AlternateSeparator) ? AlternateSeparator : (byte)'\n';
        using var register = new Cfb8Register(Key, InitialRegister());

        ReadOnlySpan<byte> remaining = decoded;
        var entry = 0;
        while (remaining.Length > 0)
        {
            var end = remaining.IndexOf(separator);
            var line = end < 0 ? remaining : remaining[..end];
            remaining = end < 0 ? default : remaining[(end + 1)..];

            line = Trim(line);
            if (line.Length > 0)
            {
                if (builder.LinkLimitReached)
                {
                    builder.ReportLinkLimitExceeded();
                    break;
                }

                entry++;
                ReadEntry(line, entry, register, builder, limits);
            }
        }

        if (entry == 0)
        {
            builder.AddProblem(ContainerProblemKind.Malformed, "This RSDF file holds no links.");
        }

        return builder.Build();
    }

    private static void ReadEntry(ReadOnlySpan<byte> line, int entry, Cfb8Register register, ContainerResultBuilder builder, ContainerLimits limits)
    {
        var cipher = new byte[Base64.GetMaxDecodedFromUtf8Length(line.Length)];
        if (Base64.DecodeFromUtf8(line, cipher, out var consumed, out var written) != OperationStatus.Done || consumed != line.Length)
        {
            // The register cannot be advanced over bytes that do not decode, so the next entry
            // decrypts wrongly for up to one block and reports its own problem.
            builder.AddProblem(ContainerProblemKind.Malformed, $"Entry {entry} of this RSDF file is not valid base64.");
            return;
        }

        var ciphertext = cipher.AsSpan(0, written);
        if (written > limits.MaxFieldChars)
        {
            register.Skip(ciphertext);
            builder.AddProblem(ContainerProblemKind.InvalidEntry, $"Entry {entry} of this RSDF file is too long to be a link.");
            return;
        }

        var plain = new byte[written];
        register.Decrypt(ciphertext, plain);

        var url = ToUrl(plain);
        if (url is null)
        {
            builder.AddProblem(ContainerProblemKind.InvalidEntry, $"Entry {entry} of this RSDF file did not decrypt to a link.");
            return;
        }

        builder.TryAddLink(url, package: null, fileName: null, sizeBytes: null);
    }

    private static string? ToUrl(byte[] plain)
    {
        string text;
        try
        {
            text = StrictUtf8.GetString(plain);
        }
        catch (DecoderFallbackException)
        {
            return null;
        }

        text = text.Trim().Trim('\0').Trim();
        if (text.StartsWith(LinkPrefix, StringComparison.OrdinalIgnoreCase))
        {
            text = text[LinkPrefix.Length..].TrimStart();
        }

        return ContainerText.IsUrl(text) ? text : null;
    }

    private static byte[] DecodeHex(byte[] data, ContainerResultBuilder builder)
    {
        var body = ContainerText.SkipBom(data);
        var digits = new byte[body.Length];
        var count = 0;
        var strayData = false;
        foreach (var value in body)
        {
            if (ContainerText.IsWhitespace(value))
            {
                continue;
            }

            if (!ContainerText.IsHexDigit(value))
            {
                strayData = true;
                break;
            }

            digits[count++] = value;
        }

        if (count == 0)
        {
            builder.AddProblem(ContainerProblemKind.Malformed, "This file is named RSDF but does not hold the hex text an RSDF file is made of.");
            return [];
        }

        if (strayData)
        {
            builder.AddProblem(ContainerProblemKind.Malformed, "This RSDF file has data after its hex text. That data was ignored.");
        }

        if (count % 2 != 0)
        {
            builder.AddProblem(ContainerProblemKind.Truncated, "This RSDF file ends in the middle of a byte, so it looks cut short.");
            count--;
        }

        var decoded = new byte[count / 2];
        for (var i = 0; i < decoded.Length; i++)
        {
            decoded[i] = (byte)((HexValue(digits[2 * i]) << 4) | HexValue(digits[(2 * i) + 1]));
        }

        return decoded;
    }

    private static int HexValue(byte digit) => digit switch
    {
        <= (byte)'9' => digit - '0',
        <= (byte)'F' => digit - 'A' + 10,
        _ => digit - 'a' + 10,
    };

    private static ReadOnlySpan<byte> Trim(ReadOnlySpan<byte> line)
    {
        var start = 0;
        var end = line.Length;
        while (start < end && ContainerText.IsWhitespace(line[start]))
        {
            start++;
        }

        while (end > start && ContainerText.IsWhitespace(line[end - 1]))
        {
            end--;
        }

        return line[start..end];
    }

    /// <summary>
    /// One CFB-8 register that keeps running across every line of the file. Restarting it per line garbles only
    /// the first block of each later line, which is easy to mistake for a working decoder.
    /// </summary>
    private sealed class Cfb8Register : IDisposable
    {
        private readonly Aes _aes;
        private readonly ICryptoTransform _encryptor;
        private readonly byte[] _register = new byte[BlockBytes];
        private readonly byte[] _keystream = new byte[BlockBytes];

        public Cfb8Register(byte[] key, byte[] initial)
        {
            _aes = Aes.Create();
            _aes.Mode = CipherMode.ECB;
            _aes.Padding = PaddingMode.None;
            _encryptor = _aes.CreateEncryptor(key, null);
            initial.CopyTo(_register, 0);
        }

        public void Decrypt(ReadOnlySpan<byte> cipher, Span<byte> plain)
        {
            for (var i = 0; i < cipher.Length; i++)
            {
                _encryptor.TransformBlock(_register, 0, BlockBytes, _keystream, 0);
                plain[i] = (byte)(cipher[i] ^ _keystream[0]);
                Buffer.BlockCopy(_register, 1, _register, 0, BlockBytes - 1);
                _register[BlockBytes - 1] = cipher[i];
            }
        }

        /// <summary>Moves past ciphertext without decrypting it. The register only ever holds recent ciphertext, so this is exact.</summary>
        public void Skip(ReadOnlySpan<byte> cipher)
        {
            if (cipher.Length >= BlockBytes)
            {
                cipher[^BlockBytes..].CopyTo(_register);
                return;
            }

            Buffer.BlockCopy(_register, cipher.Length, _register, 0, BlockBytes - cipher.Length);
            cipher.CopyTo(_register.AsSpan(BlockBytes - cipher.Length));
        }

        public void Dispose()
        {
            _encryptor.Dispose();
            _aes.Dispose();
        }
    }
}

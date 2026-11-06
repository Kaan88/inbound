using System.Buffers.Binary;
using System.Numerics;

namespace DownloadManager.Containers;

/// <summary>
/// CCF 3.0: a custom cipher with no stored constant. An 11-byte header holds the magic, a 32-bit little-endian key
/// and the number of bytes used in the last block. Data follows in 64-byte blocks. The key state carries from one
/// block to the next, and each block is treated as an 8x8 matrix of bytes.
/// </summary>
internal static class Ccf30Cipher
{
    public const int KeyOffset = 6;
    public const int LastBlockLengthOffset = 10;
    public const int HeaderBytes = 11;
    public const int BlockBytes = 64;
    private const int RowBytes = 8;

    public static byte[]? Decrypt(byte[] data, out ContainerProblem? problem)
    {
        problem = null;
        if (data.Length < HeaderBytes + BlockBytes || (data.Length - HeaderBytes) % BlockBytes != 0)
        {
            problem = new ContainerProblem(ContainerProblemKind.Truncated, "This CCF 3.0 file looks cut short: it does not hold a whole number of blocks.");
            return null;
        }

        var lastBlockLength = data[LastBlockLengthOffset];
        if (lastBlockLength > BlockBytes)
        {
            problem = new ContainerProblem(ContainerProblemKind.Malformed, "This CCF 3.0 file has an impossible length in its header.");
            return null;
        }

        var key = BinaryPrimitives.ReadUInt32LittleEndian(data.AsSpan(KeyOffset));
        var plain = data[HeaderBytes..];
        for (var offset = 0; offset < plain.Length; offset += BlockBytes)
        {
            key = DecryptBlock(plain.AsSpan(offset, BlockBytes), key);
        }

        return plain[..(plain.Length - BlockBytes + lastBlockLength)];
    }

    private static uint DecryptBlock(Span<byte> block, uint key)
    {
        for (var index = 0; index < BlockBytes; index++)
        {
            key = (key & 1) != 0
                ? BitOperations.RotateRight(key, (int)((key & 0xFF) % 12))
                : BitOperations.RotateLeft(key, (int)((key & 0xFF) % 9));

            block[index] ^= (byte)key;
            TransposeBytes(block);
        }

        for (var row = 0; row < RowBytes; row++)
        {
            var bits = block.Slice(row * RowBytes, RowBytes);
            for (var index = 0; index < RowBytes; index++)
            {
                bits[index] ^= (byte)key;
                TransposeBits(bits);

                key = (key & 1) != 0
                    ? BitOperations.RotateRight(key, (int)(key & 12))
                    : BitOperations.RotateLeft(key, (int)(key & 9));
            }
        }

        return key;
    }

    private static void TransposeBytes(Span<byte> block)
    {
        for (var row = 1; row < RowBytes; row++)
        {
            for (var column = 0; column < row; column++)
            {
                var upper = (column * RowBytes) + row;
                var lower = (row * RowBytes) + column;
                (block[upper], block[lower]) = (block[lower], block[upper]);
            }
        }
    }

    // Swapping across the diagonal in three masked steps avoids 64 bit tests on a path that runs once per byte.
    private static void TransposeBits(Span<byte> row)
    {
        var x = BinaryPrimitives.ReadUInt64LittleEndian(row);

        var t = (x ^ (x >> 7)) & 0x00AA00AA00AA00AAUL;
        x ^= t ^ (t << 7);

        t = (x ^ (x >> 14)) & 0x0000CCCC0000CCCCUL;
        x ^= t ^ (t << 14);

        t = (x ^ (x >> 28)) & 0x00000000F0F0F0F0UL;
        x ^= t ^ (t << 28);

        BinaryPrimitives.WriteUInt64LittleEndian(row, x);
    }
}

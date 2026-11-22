using System.Buffers.Binary;
using System.Numerics;

namespace DownloadManager.Sites;

/// <summary>
/// Argon2 version 1.3 (RFC 9106), with BLAKE2b (RFC 7693), for opening passphrase-protected PuTTY version 3 key files
/// (<see cref="PuttyKeyFile"/>, SITES.11). .NET has no Argon2. Tested against the RFC 9106 vectors and PuTTY's own key
/// file vectors. Single-threaded: lanes are filled one after another, which gives the same result.
/// </summary>
internal static class Argon2
{
    public enum Kind { D = 0, I = 1, Id = 2 }

    private const int BlockWords = 128;
    private const int SyncPoints = 4;
    private const uint Version = 0x13;

    public static byte[] Hash(Kind kind, ReadOnlySpan<byte> password, ReadOnlySpan<byte> salt, int memoryKiB, int passes, int lanes,
        int tagLength, ReadOnlySpan<byte> secret = default, ReadOnlySpan<byte> associated = default)
    {
        if (lanes < 1 || passes < 1 || tagLength < 4 || memoryKiB < 8 * lanes) throw new ArgumentOutOfRangeException(nameof(memoryKiB));

        var h0 = new Blake2b(64);
        Span<byte> word = stackalloc byte[4];
        void Put(Blake2b h, uint value, Span<byte> w) { BinaryPrimitives.WriteUInt32LittleEndian(w, value); h.Update(w); }
        Put(h0, (uint)lanes, word);
        Put(h0, (uint)tagLength, word);
        Put(h0, (uint)memoryKiB, word);
        Put(h0, (uint)passes, word);
        Put(h0, Version, word);
        Put(h0, (uint)kind, word);
        Put(h0, (uint)password.Length, word); h0.Update(password);
        Put(h0, (uint)salt.Length, word); h0.Update(salt);
        Put(h0, (uint)secret.Length, word); h0.Update(secret);
        Put(h0, (uint)associated.Length, word); h0.Update(associated);
        var seed = new byte[72];
        h0.Final(seed.AsSpan(0, 64));

        int segment = memoryKiB / (SyncPoints * lanes);
        int laneLength = segment * SyncPoints;
        var memory = new ulong[(long)laneLength * lanes * BlockWords];
        var block = new byte[1024];
        for (int lane = 0; lane < lanes; lane++)
            for (int i = 0; i < 2; i++)
            {
                BinaryPrimitives.WriteUInt32LittleEndian(seed.AsSpan(64), (uint)i);
                BinaryPrimitives.WriteUInt32LittleEndian(seed.AsSpan(68), (uint)lane);
                LongHash(seed, block);
                var dest = memory.AsSpan((lane * laneLength + i) * BlockWords, BlockWords);
                for (int w = 0; w < BlockWords; w++) dest[w] = BinaryPrimitives.ReadUInt64LittleEndian(block.AsSpan(w * 8));
            }

        var zero = new ulong[BlockWords];
        var input = new ulong[BlockWords];
        var address = new ulong[BlockWords];
        var r = new ulong[BlockWords];
        var tmp = new ulong[BlockWords];
        for (int pass = 0; pass < passes; pass++)
            for (int slice = 0; slice < SyncPoints; slice++)
                for (int lane = 0; lane < lanes; lane++)
                {
                    bool independent = kind == Kind.I || (kind == Kind.Id && pass == 0 && slice < SyncPoints / 2);
                    if (independent)
                    {
                        Array.Clear(input);
                        input[0] = (ulong)pass; input[1] = (ulong)lane; input[2] = (ulong)slice;
                        input[3] = (ulong)(laneLength * lanes); input[4] = (ulong)passes; input[5] = (ulong)kind;
                    }

                    int start = 0;
                    if (pass == 0 && slice == 0)
                    {
                        start = 2;
                        if (independent) NextAddresses(address, input, zero, r, tmp);
                    }

                    long current = (long)lane * laneLength + slice * segment + start;
                    long previous = current % laneLength == 0 ? current + laneLength - 1 : current - 1;
                    for (int index = start; index < segment; index++, current++, previous++)
                    {
                        if (current % laneLength == 1) previous = current - 1;
                        ulong random;
                        if (independent)
                        {
                            if (index % BlockWords == 0) NextAddresses(address, input, zero, r, tmp);
                            random = address[index % BlockWords];
                        }
                        else random = memory[previous * BlockWords];

                        long refLane = (long)((random >> 32) % (ulong)lanes);
                        if (pass == 0 && slice == 0) refLane = lane;
                        long refIndex = ReferenceIndex(pass, slice, index, segment, laneLength, (uint)random, refLane == lane);
                        FillBlock(memory, previous * BlockWords, (refLane * laneLength + refIndex) * BlockWords, current * BlockWords,
                            pass != 0, r, tmp);
                    }
                }

        var final = new ulong[BlockWords];
        for (int lane = 0; lane < lanes; lane++)
        {
            var last = memory.AsSpan(checked((lane * laneLength + laneLength - 1) * BlockWords), BlockWords);
            for (int w = 0; w < BlockWords; w++) final[w] ^= last[w];
        }
        for (int w = 0; w < BlockWords; w++) BinaryPrimitives.WriteUInt64LittleEndian(block.AsSpan(w * 8), final[w]);
        var tag = new byte[tagLength];
        LongHash(block, tag);
        Array.Clear(memory);
        Array.Clear(seed);
        return tag;
    }

    private static long ReferenceIndex(int pass, int slice, int index, int segment, int laneLength, uint random, bool sameLane)
    {
        ulong area;
        if (pass == 0)
        {
            if (slice == 0) area = (ulong)(index - 1);
            else if (sameLane) area = (ulong)(slice * segment + index - 1);
            else area = (ulong)(slice * segment + (index == 0 ? -1 : 0));
        }
        else
        {
            area = sameLane ? (ulong)(laneLength - segment + index - 1) : (ulong)(laneLength - segment + (index == 0 ? -1 : 0));
        }

        ulong relative = random;
        relative = relative * relative >> 32;
        relative = area - 1 - (area * relative >> 32);
        ulong startPosition = pass != 0 && slice != SyncPoints - 1 ? (ulong)((slice + 1) * segment) : 0;
        return (long)((startPosition + relative) % (ulong)laneLength);
    }

    private static void NextAddresses(ulong[] address, ulong[] input, ulong[] zero, ulong[] r, ulong[] tmp)
    {
        input[6]++;
        Compress(zero, input, address, false, r, tmp);
        Compress(zero, address, address, false, r, tmp);
    }

    private static void Compress(ulong[] x, ulong[] y, ulong[] next, bool xor, ulong[] r, ulong[] tmp)
    {
        for (int i = 0; i < BlockWords; i++) r[i] = x[i] ^ y[i];
        Array.Copy(r, tmp, BlockWords);
        if (xor) for (int i = 0; i < BlockWords; i++) tmp[i] ^= next[i];
        Permute(r);
        for (int i = 0; i < BlockWords; i++) next[i] = tmp[i] ^ r[i];
    }

    private static void FillBlock(ulong[] memory, long prev, long reference, long next, bool xor, ulong[] r, ulong[] tmp)
    {
        for (int i = 0; i < BlockWords; i++) r[i] = memory[prev + i] ^ memory[reference + i];
        Array.Copy(r, tmp, BlockWords);
        if (xor) for (int i = 0; i < BlockWords; i++) tmp[i] ^= memory[next + i];
        Permute(r);
        for (int i = 0; i < BlockWords; i++) memory[next + i] = tmp[i] ^ r[i];
    }

    private static void Permute(ulong[] r)
    {
        for (int i = 0; i < 8; i++)
        {
            int b = 16 * i;
            Round(r, b, b + 1, b + 2, b + 3, b + 4, b + 5, b + 6, b + 7, b + 8, b + 9, b + 10, b + 11, b + 12, b + 13, b + 14, b + 15);
        }
        for (int i = 0; i < 8; i++)
        {
            int b = 2 * i;
            Round(r, b, b + 1, b + 16, b + 17, b + 32, b + 33, b + 48, b + 49, b + 64, b + 65, b + 80, b + 81, b + 96, b + 97, b + 112, b + 113);
        }
    }

    private static void Round(ulong[] v, int v0, int v1, int v2, int v3, int v4, int v5, int v6, int v7, int v8, int v9, int v10,
        int v11, int v12, int v13, int v14, int v15)
    {
        G(v, v0, v4, v8, v12); G(v, v1, v5, v9, v13); G(v, v2, v6, v10, v14); G(v, v3, v7, v11, v15);
        G(v, v0, v5, v10, v15); G(v, v1, v6, v11, v12); G(v, v2, v7, v8, v13); G(v, v3, v4, v9, v14);
    }

    private static void G(ulong[] v, int a, int b, int c, int d)
    {
        v[a] = v[a] + v[b] + 2 * (v[a] & 0xFFFFFFFF) * (v[b] & 0xFFFFFFFF);
        v[d] = BitOperations.RotateRight(v[d] ^ v[a], 32);
        v[c] = v[c] + v[d] + 2 * (v[c] & 0xFFFFFFFF) * (v[d] & 0xFFFFFFFF);
        v[b] = BitOperations.RotateRight(v[b] ^ v[c], 24);
        v[a] = v[a] + v[b] + 2 * (v[a] & 0xFFFFFFFF) * (v[b] & 0xFFFFFFFF);
        v[d] = BitOperations.RotateRight(v[d] ^ v[a], 16);
        v[c] = v[c] + v[d] + 2 * (v[c] & 0xFFFFFFFF) * (v[d] & 0xFFFFFFFF);
        v[b] = BitOperations.RotateRight(v[b] ^ v[c], 63);
    }

    /// <summary>Argon2's variable-length hash H' (RFC 9106 section 3.3).</summary>
    internal static void LongHash(ReadOnlySpan<byte> input, Span<byte> output)
    {
        Span<byte> length = stackalloc byte[4];
        BinaryPrimitives.WriteUInt32LittleEndian(length, (uint)output.Length);
        if (output.Length <= 64)
        {
            var h = new Blake2b(output.Length);
            h.Update(length); h.Update(input); h.Final(output);
            return;
        }

        int rounds = (output.Length + 31) / 32 - 2;
        Span<byte> v = stackalloc byte[64];
        var first = new Blake2b(64);
        first.Update(length); first.Update(input); first.Final(v);
        v[..32].CopyTo(output);
        int at = 32;
        for (int i = 2; i <= rounds; i++)
        {
            var next = new Blake2b(64);
            next.Update(v); next.Final(v);
            v[..32].CopyTo(output[at..]);
            at += 32;
        }
        var last = new Blake2b(output.Length - at);
        last.Update(v); last.Final(output[at..]);
    }
}

/// <summary>BLAKE2b without a key (RFC 7693), for <see cref="Argon2"/>.</summary>
internal sealed class Blake2b
{
    private static readonly ulong[] Iv =
    [
        0x6a09e667f3bcc908, 0xbb67ae8584caa73b, 0x3c6ef372fe94f82b, 0xa54ff53a5f1d36f1,
        0x510e527fade682d1, 0x9b05688c2b3e6c1f, 0x1f83d9abfb41bd6b, 0x5be0cd19137e2179,
    ];

    private static readonly byte[][] Sigma =
    [
        [0, 1, 2, 3, 4, 5, 6, 7, 8, 9, 10, 11, 12, 13, 14, 15],
        [14, 10, 4, 8, 9, 15, 13, 6, 1, 12, 0, 2, 11, 7, 5, 3],
        [11, 8, 12, 0, 5, 2, 15, 13, 10, 14, 3, 6, 7, 1, 9, 4],
        [7, 9, 3, 1, 13, 12, 11, 14, 2, 6, 5, 10, 4, 0, 15, 8],
        [9, 0, 5, 7, 2, 4, 10, 15, 14, 1, 11, 12, 6, 8, 3, 13],
        [2, 12, 6, 10, 0, 11, 8, 3, 4, 13, 7, 5, 15, 14, 1, 9],
        [12, 5, 1, 15, 14, 13, 4, 10, 0, 7, 6, 3, 9, 2, 8, 11],
        [13, 11, 7, 14, 12, 1, 3, 9, 5, 0, 15, 4, 8, 6, 2, 10],
        [6, 15, 14, 9, 11, 3, 0, 8, 12, 2, 13, 7, 1, 4, 10, 5],
        [10, 2, 8, 4, 7, 6, 1, 5, 15, 11, 9, 14, 3, 12, 13, 0],
    ];

    private readonly ulong[] _h = new ulong[8];
    private readonly byte[] _buffer = new byte[128];
    private readonly ulong[] _m = new ulong[16];
    private readonly ulong[] _v = new ulong[16];
    private readonly int _outLength;
    private int _filled;
    private ulong _t0, _t1;

    public Blake2b(int outLength)
    {
        if (outLength is < 1 or > 64) throw new ArgumentOutOfRangeException(nameof(outLength));
        _outLength = outLength;
        Array.Copy(Iv, _h, 8);
        _h[0] ^= 0x01010000UL ^ (ulong)outLength;
    }

    public void Update(ReadOnlySpan<byte> data)
    {
        while (data.Length > 0)
        {
            if (_filled == 128)
            {
                AddCounter(128);
                Compress(false);
                _filled = 0;
            }
            int take = Math.Min(128 - _filled, data.Length);
            data[..take].CopyTo(_buffer.AsSpan(_filled));
            _filled += take;
            data = data[take..];
        }
    }

    public void Final(Span<byte> output)
    {
        AddCounter((ulong)_filled);
        Array.Clear(_buffer, _filled, 128 - _filled);
        Compress(true);
        Span<byte> all = stackalloc byte[64];
        for (int i = 0; i < 8; i++) BinaryPrimitives.WriteUInt64LittleEndian(all[(i * 8)..], _h[i]);
        all[.._outLength].CopyTo(output);
    }

    private void AddCounter(ulong count)
    {
        _t0 += count;
        if (_t0 < count) _t1++;
    }

    private void Compress(bool last)
    {
        for (int i = 0; i < 16; i++) _m[i] = BinaryPrimitives.ReadUInt64LittleEndian(_buffer.AsSpan(i * 8));
        Array.Copy(_h, _v, 8);
        Array.Copy(Iv, 0, _v, 8, 8);
        _v[12] ^= _t0;
        _v[13] ^= _t1;
        if (last) _v[14] = ~_v[14];
        for (int round = 0; round < 12; round++)
        {
            var s = Sigma[round % 10];
            Mix(0, 4, 8, 12, _m[s[0]], _m[s[1]]);
            Mix(1, 5, 9, 13, _m[s[2]], _m[s[3]]);
            Mix(2, 6, 10, 14, _m[s[4]], _m[s[5]]);
            Mix(3, 7, 11, 15, _m[s[6]], _m[s[7]]);
            Mix(0, 5, 10, 15, _m[s[8]], _m[s[9]]);
            Mix(1, 6, 11, 12, _m[s[10]], _m[s[11]]);
            Mix(2, 7, 8, 13, _m[s[12]], _m[s[13]]);
            Mix(3, 4, 9, 14, _m[s[14]], _m[s[15]]);
        }
        for (int i = 0; i < 8; i++) _h[i] ^= _v[i] ^ _v[i + 8];
    }

    private void Mix(int a, int b, int c, int d, ulong x, ulong y)
    {
        var v = _v;
        v[a] = v[a] + v[b] + x;
        v[d] = BitOperations.RotateRight(v[d] ^ v[a], 32);
        v[c] = v[c] + v[d];
        v[b] = BitOperations.RotateRight(v[b] ^ v[c], 24);
        v[a] = v[a] + v[b] + y;
        v[d] = BitOperations.RotateRight(v[d] ^ v[a], 16);
        v[c] = v[c] + v[d];
        v[b] = BitOperations.RotateRight(v[b] ^ v[c], 63);
    }
}

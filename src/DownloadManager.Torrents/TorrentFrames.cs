using System.Buffers.Binary;
using System.Text.Json.Nodes;

namespace DownloadManager.Torrents;

/// <summary>
/// Frames on the worker's stdin and stdout: a 4-byte little-endian length, then that many bytes of UTF-8 JSON.
/// Both sides refuse a frame above <see cref="MaxFrameBytes"/>, matching <c>native/torrent-worker/src/framing.hpp</c>.
/// </summary>
public static class TorrentFrames
{
    public const int MaxFrameBytes = 32 * 1024 * 1024;

    public static async Task WriteAsync(Stream stream, JsonObject message, CancellationToken cancellationToken)
    {
        var payload = System.Text.Encoding.UTF8.GetBytes(message.ToJsonString());
        if (payload.Length > MaxFrameBytes) throw new InvalidDataException("The message is larger than a frame may be.");
        // Header and payload in one write, so a frame is never left half sent between two calls.
        var frame = new byte[4 + payload.Length];
        BinaryPrimitives.WriteInt32LittleEndian(frame, payload.Length);
        payload.CopyTo(frame, 4);
        await stream.WriteAsync(frame, cancellationToken).ConfigureAwait(false);
        await stream.FlushAsync(cancellationToken).ConfigureAwait(false);
    }

    /// <summary>Reads one frame; null when the stream ended cleanly before a frame began.</summary>
    public static async Task<JsonObject?> ReadAsync(Stream stream, CancellationToken cancellationToken)
    {
        var header = new byte[4];
        var got = await ReadFullyAsync(stream, header, cancellationToken).ConfigureAwait(false);
        if (got == 0) return null;
        if (got != 4) throw new EndOfStreamException("The worker's output ended inside a frame header.");
        var length = BinaryPrimitives.ReadUInt32LittleEndian(header);
        if (length > MaxFrameBytes) throw new InvalidDataException("The worker sent a frame above the size limit.");
        var payload = new byte[length];
        if (await ReadFullyAsync(stream, payload, cancellationToken).ConfigureAwait(false) != payload.Length)
            throw new EndOfStreamException("The worker's output ended inside a frame.");
        return JsonNode.Parse(payload) as JsonObject
            ?? throw new InvalidDataException("The worker sent a frame that is not a JSON object.");
    }

    private static async Task<int> ReadFullyAsync(Stream stream, byte[] buffer, CancellationToken cancellationToken)
    {
        var total = 0;
        while (total < buffer.Length)
        {
            var n = await stream.ReadAsync(buffer.AsMemory(total), cancellationToken).ConfigureAwait(false);
            if (n == 0) break;
            total += n;
        }
        return total;
    }
}

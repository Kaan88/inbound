using System.Security.Cryptography;
using System.Text;

namespace DownloadManager.Torrents.Tests;

/// <summary>Builds a single-file BitTorrent v1 metainfo file for a payload on disk (BEP 3), for loopback tests.</summary>
internal static class TestTorrent
{
    /// <summary>The v1 info-hash (hex) of the torrent made last on this thread, for magnet links.</summary>
    [ThreadStatic] public static string? LastInfoHash;

    public static byte[] Create(string name, byte[] payload, int pieceLength = 64 * 1024)
    {
        using var pieces = new MemoryStream();
        for (var offset = 0; offset < payload.Length; offset += pieceLength)
        {
            var length = Math.Min(pieceLength, payload.Length - offset);
            pieces.Write(SHA1.HashData(payload.AsSpan(offset, length)));
        }
        using var info = new MemoryStream();
        // Keys in sorted order, as bencoding requires.
        Bytes(info, "d");
        Str(info, "length"); Int(info, payload.Length);
        Str(info, "name"); Str(info, name);
        Str(info, "piece length"); Int(info, pieceLength);
        Str(info, "pieces"); Raw(info, pieces.ToArray());
        Bytes(info, "e");
        LastInfoHash = Convert.ToHexStringLower(SHA1.HashData(info.ToArray()));
        using var torrent = new MemoryStream();
        Bytes(torrent, "d");
        Str(torrent, "info");
        torrent.Write(info.ToArray());
        Bytes(torrent, "e");
        return torrent.ToArray();
    }

    private static void Bytes(Stream s, string text) => s.Write(Encoding.ASCII.GetBytes(text));
    private static void Int(Stream s, long n) => Bytes(s, $"i{n}e");
    private static void Str(Stream s, string text) => Raw(s, Encoding.UTF8.GetBytes(text));
    private static void Raw(Stream s, byte[] data)
    {
        Bytes(s, $"{data.Length}:");
        s.Write(data);
    }
}

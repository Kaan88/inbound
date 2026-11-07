using System.Globalization;
using System.Security.Cryptography;
using System.Text;

namespace DownloadManager.Runtime.Remote;

/// <summary>One file a .torrent lists, with its path as <see cref="TorrentMetadata.Files"/> describes it. Untrusted, like the rest of the file.</summary>
public sealed record TorrentFileEntry(string Path, long Length);

/// <summary>
/// What a .torrent file says about itself, read just far enough to name it and find it again. The
/// reader is strict and bounded because the file is untrusted, and because the hash has to be taken
/// over the info dictionary's bytes exactly as written, so nothing may be repaired along the way.
/// Refusals never quote the file: it can hold a private tracker's passkey.
/// </summary>
public sealed record TorrentMetadata(string InfoHash, string Name, long TotalBytes, int FileCount)
{
    /// <summary>
    /// What it holds, in its own order, without the padding files some torrents have. A torrent of several files puts
    /// them in a folder of its name, so that comes first in each path, '/' between the parts. Empty when only a
    /// version 2 file tree describes them.
    /// </summary>
    public IReadOnlyList<TorrentFileEntry> Files { get; init; } = [];

    public const int MaxFileBytes = 16 * 1024 * 1024;
    private const int MaxDepth = 64;
    private const string NotATorrent = "This file is not a valid .torrent file";
    private static readonly UTF8Encoding StrictUtf8 = new(encoderShouldEmitUTF8Identifier: false, throwOnInvalidBytes: true);

    public static bool TryParse(byte[] file, out TorrentMetadata? metadata, out string? problem)
    {
        metadata = null;
        problem = null;
        if (file.Length > MaxFileBytes)
        {
            problem = "This .torrent file is too large to be a real one.";
            return false;
        }

        try
        {
            metadata = Parse(file);
            return true;
        }
        catch (TorrentFormatException ex)
        {
            problem = ex.Message;
            return false;
        }
    }

    private static TorrentMetadata Parse(ReadOnlySpan<byte> file)
    {
        var reader = new BencodeReader(file);
        if (reader.Peek() != 'd')
        {
            throw new TorrentFormatException($"{NotATorrent}.");
        }

        var info = default(Range);
        var found = false;
        foreach (var (key, value) in reader.ReadDictionary(depth: 0))
        {
            if (file[key].SequenceEqual("info"u8))
            {
                info = value;
                found = true;
            }
        }

        if (reader.Position != file.Length)
        {
            throw new TorrentFormatException($"{NotATorrent}: there is data after its end.");
        }

        if (!found)
        {
            throw new TorrentFormatException($"{NotATorrent}: it has no info section.");
        }

        var infoBytes = file[info];
        if (infoBytes[0] != 'd')
        {
            throw new TorrentFormatException($"{NotATorrent}: its info section has the wrong form.");
        }

        var (name, totalBytes, fileCount, files) = ReadInfo(infoBytes);
        return new TorrentMetadata(Convert.ToHexString(SHA1.HashData(infoBytes)).ToLowerInvariant(), name, totalBytes, fileCount) { Files = files };
    }

    private static (string Name, long TotalBytes, int FileCount, List<TorrentFileEntry> Files) ReadInfo(ReadOnlySpan<byte> info)
    {
        var reader = new BencodeReader(info);
        string? name = null;
        long? pieceLength = null;
        var hasPieces = false;
        long? singleLength = null;
        Range? files = null;
        var isVersion2Only = false;
        var hasFileTree = false;
        foreach (var (key, value) in reader.ReadDictionary(depth: 0))
        {
            var bytes = info[value];
            switch (Encoding.ASCII.GetString(info[key]))
            {
                case "name":
                    name = TextOf(RequireString(bytes, "name"));
                    break;
                case "piece length":
                    pieceLength = RequireWholeNumber(bytes, "piece length");
                    break;
                case "pieces":
                    var pieces = RequireString(bytes, "pieces");
                    if (pieces.Length == 0 || pieces.Length % 20 != 0)
                    {
                        throw new TorrentFormatException($"{NotATorrent}: its piece list has the wrong size.");
                    }

                    hasPieces = true;
                    break;
                case "length":
                    singleLength = RequireWholeNumber(bytes, "length");
                    break;
                case "files":
                    files = value;
                    break;
                case "file tree":
                    hasFileTree = bytes[0] == 'd';
                    break;
                case "meta version":
                    isVersion2Only = RequireWholeNumber(bytes, "meta version") == 2;
                    break;
            }
        }

        if (string.IsNullOrEmpty(name))
        {
            throw new TorrentFormatException($"{NotATorrent}: it has no name.");
        }

        if (isVersion2Only && hasFileTree && !hasPieces)
        {
            throw new TorrentFormatException("This is a version 2 torrent. Only torrents with a version 1 hash can be added, because that is how the item is found again.");
        }

        if (pieceLength is not > 0)
        {
            throw new TorrentFormatException($"{NotATorrent}: it has no piece length.");
        }

        if (!hasPieces)
        {
            throw new TorrentFormatException($"{NotATorrent}: it has no piece list.");
        }

        if (files is { } list)
        {
            var (total, count, entries) = ReadFiles(info[list], name);
            return (name, total, count, entries);
        }

        if (singleLength is { } length)
        {
            return (name, length, 1, [new TorrentFileEntry(name, length)]);
        }

        // A hybrid torrent may describe its files only in the version 2 tree.
        return hasFileTree
            ? (name, 0, 0, [])
            : throw new TorrentFormatException($"{NotATorrent}: it lists no files.");
    }

    private static (long Total, int Count, List<TorrentFileEntry> Files) ReadFiles(ReadOnlySpan<byte> list, string folder)
    {
        var reader = new BencodeReader(list);
        if (reader.Peek() != 'l')
        {
            throw new TorrentFormatException($"{NotATorrent}: its file list has the wrong form.");
        }

        var total = 0L;
        var count = 0;
        var files = new List<TorrentFileEntry>();
        foreach (var entry in reader.ReadList(depth: 0))
        {
            var (length, path, padding) = ReadFile(list[entry]);
            try
            {
                total = checked(total + length);
            }
            catch (OverflowException)
            {
                throw new TorrentFormatException($"{NotATorrent}: its file sizes don't add up to a real size.");
            }

            count++;
            if (!padding)
            {
                files.Add(new TorrentFileEntry($"{folder}/{path}", length));
            }
        }

        return count > 0 ? (total, count, files) : throw new TorrentFormatException($"{NotATorrent}: it lists no files.");
    }

    /// <summary>A file's size, its path joined with '/', and whether it only pads the pieces out, which nobody downloads.</summary>
    private static (long Length, string Path, bool Padding) ReadFile(ReadOnlySpan<byte> entry)
    {
        if (entry[0] != 'd')
        {
            throw new TorrentFormatException($"{NotATorrent}: an entry in its file list has the wrong form.");
        }

        var reader = new BencodeReader(entry);
        long? length = null;
        string? path = null;
        string? utf8Path = null;
        var padding = false;
        foreach (var (key, value) in reader.ReadDictionary(depth: 0))
        {
            var bytes = entry[value];
            var name = entry[key];
            if (name.SequenceEqual("length"u8))
            {
                length = RequireWholeNumber(bytes, "length");
            }
            else if (name.SequenceEqual("path"u8))
            {
                path = PathOf(bytes);
            }
            else if (name.SequenceEqual("path.utf-8"u8))
            {
                utf8Path = PathOf(bytes);
            }
            else if (name.SequenceEqual("attr"u8) && char.IsAsciiDigit((char)bytes[0]))
            {
                padding = RequireString(bytes, "attr").Contains((byte)'p');
            }
        }

        // The UTF-8 spelling, where there is one, is what a client shows.
        return length is { } size && (utf8Path ?? path) is { } joined
            ? (size, joined, padding)
            : throw new TorrentFormatException($"{NotATorrent}: an entry in its file list is incomplete.");
    }

    /// <summary>A list of path parts, joined with '/'. Null when it isn't a list of strings with at least one part.</summary>
    private static string? PathOf(ReadOnlySpan<byte> value)
    {
        if (value[0] != 'l' || value.Length <= 2)
        {
            return null;
        }

        var reader = new BencodeReader(value);
        var parts = new List<string>();
        foreach (var part in reader.ReadList(depth: 0))
        {
            var bytes = value[part];
            if (!char.IsAsciiDigit((char)bytes[0]))
            {
                return null;
            }

            parts.Add(TextOf(RequireString(bytes, "path")));
        }

        return parts.Count > 0 ? string.Join('/', parts) : null;
    }

    private static ReadOnlySpan<byte> RequireString(ReadOnlySpan<byte> value, string field)
    {
        if (!char.IsAsciiDigit((char)value[0]))
        {
            throw new TorrentFormatException($"{NotATorrent}: {field} has the wrong form.");
        }

        var colon = value.IndexOf((byte)':');
        return value[(colon + 1)..];
    }

    private static long RequireWholeNumber(ReadOnlySpan<byte> value, string field)
    {
        if (value[0] != 'i' ||
            !long.TryParse(value[1..^1], NumberStyles.None, CultureInfo.InvariantCulture, out var number))
        {
            throw new TorrentFormatException($"{NotATorrent}: {field} is not a usable number.");
        }

        return number;
    }

    private static string TextOf(ReadOnlySpan<byte> bytes)
    {
        try
        {
            return StrictUtf8.GetString(bytes);
        }
        catch (ArgumentException)
        {
            // Old torrents use other encodings. The name is only for display, and this keeps it readable.
            return Encoding.Latin1.GetString(bytes);
        }
    }

    private sealed class TorrentFormatException(string message) : Exception(message);

    /// <summary>
    /// Walks bencode and hands back where each value lies, allocating nothing for the values
    /// themselves. A value's type is checked by whoever cares about it, and everything is checked
    /// for being well formed here, including what nobody reads.
    /// </summary>
    private ref struct BencodeReader(ReadOnlySpan<byte> data)
    {
        private readonly ReadOnlySpan<byte> _data = data;
        private int _position = 0;

        public readonly int Position => _position;

        public readonly int Peek() => _position < _data.Length ? _data[_position] : -1;

        public DictionaryWalk ReadDictionary(int depth)
        {
            var start = _position;
            SkipValue(depth);
            if (_data[start] != 'd')
            {
                throw new TorrentFormatException($"{NotATorrent}.");
            }

            return new DictionaryWalk(_data, start);
        }

        public ListWalk ReadList(int depth)
        {
            var start = _position;
            SkipValue(depth);
            if (_data[start] != 'l')
            {
                throw new TorrentFormatException($"{NotATorrent}.");
            }

            return new ListWalk(_data, start);
        }

        public void SkipValue(int depth)
        {
            if (depth >= MaxDepth)
            {
                throw new TorrentFormatException($"{NotATorrent}: it is nested too deeply.");
            }

            switch (Peek())
            {
                case 'i':
                    SkipInteger();
                    break;
                case 'l':
                    _position++;
                    while (Peek() != 'e')
                    {
                        SkipValue(depth + 1);
                    }

                    _position++;
                    break;
                case 'd':
                    SkipDictionary(depth);
                    break;
                case >= '0' and <= '9':
                    SkipString();
                    break;
                default:
                    throw new TorrentFormatException($"{NotATorrent}: it is damaged or cut short.");
            }
        }

        private void SkipDictionary(int depth)
        {
            _position++;
            var previousStart = -1;
            var previousLength = 0;
            while (Peek() != 'e')
            {
                if (Peek() is < '0' or > '9')
                {
                    throw new TorrentFormatException($"{NotATorrent}: it is damaged or cut short.");
                }

                var (keyStart, keyLength) = SkipString();
                if (previousStart >= 0)
                {
                    var order = _data.Slice(keyStart, keyLength).SequenceCompareTo(_data.Slice(previousStart, previousLength));
                    if (order == 0)
                    {
                        throw new TorrentFormatException($"{NotATorrent}: it has the same field twice.");
                    }

                    if (order < 0)
                    {
                        throw new TorrentFormatException($"{NotATorrent}: its fields are out of order.");
                    }
                }

                previousStart = keyStart;
                previousLength = keyLength;
                SkipValue(depth + 1);
            }

            _position++;
        }

        private void SkipInteger()
        {
            var start = ++_position;
            var end = _data[start..].IndexOf((byte)'e');
            if (end < 0)
            {
                throw new TorrentFormatException($"{NotATorrent}: it is damaged or cut short.");
            }

            var digits = _data.Slice(start, end);
            var negative = digits.Length > 0 && digits[0] == '-';
            var body = negative ? digits[1..] : digits;
            var isValid = body.Length > 0 &&
                          !body.ContainsAnyExceptInRange((byte)'0', (byte)'9') &&
                          (body[0] != '0' || (body.Length == 1 && !negative));
            if (!isValid)
            {
                throw new TorrentFormatException($"{NotATorrent}: it holds a badly written number.");
            }

            _position = start + end + 1;
        }

        private (int Start, int Length) SkipString()
        {
            var colon = _data[_position..].IndexOf((byte)':');
            if (colon <= 0)
            {
                throw new TorrentFormatException($"{NotATorrent}: it is damaged or cut short.");
            }

            var digits = _data.Slice(_position, colon);
            if (digits.ContainsAnyExceptInRange((byte)'0', (byte)'9') || (digits[0] == '0' && digits.Length > 1) ||
                !int.TryParse(digits, NumberStyles.None, CultureInfo.InvariantCulture, out var length))
            {
                throw new TorrentFormatException($"{NotATorrent}: it holds a badly written length.");
            }

            var start = _position + colon + 1;
            if (length > _data.Length - start)
            {
                throw new TorrentFormatException($"{NotATorrent}: it is damaged or cut short.");
            }

            _position = start + length;
            return (start, length);
        }

        /// <summary>A dictionary already known to be well formed.</summary>
        public ref struct DictionaryWalk(ReadOnlySpan<byte> data, int start)
        {
            private BencodeReader _reader = new(data) { _position = start + 1 };

            public (Range Key, Range Value) Current { get; private set; }

            public readonly DictionaryWalk GetEnumerator() => this;

            public bool MoveNext()
            {
                if (_reader.Peek() == 'e')
                {
                    return false;
                }

                var (keyStart, keyLength) = _reader.SkipString();
                var valueStart = _reader._position;
                _reader.SkipValue(0);
                Current = (keyStart..(keyStart + keyLength), valueStart.._reader._position);
                return true;
            }
        }

        /// <summary>A list already known to be well formed.</summary>
        public ref struct ListWalk(ReadOnlySpan<byte> data, int start)
        {
            private BencodeReader _reader = new(data) { _position = start + 1 };

            public Range Current { get; private set; }

            public readonly ListWalk GetEnumerator() => this;

            public bool MoveNext()
            {
                if (_reader.Peek() == 'e')
                {
                    return false;
                }

                var valueStart = _reader._position;
                _reader.SkipValue(0);
                Current = valueStart.._reader._position;
                return true;
            }
        }
    }
}

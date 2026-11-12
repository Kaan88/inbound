namespace DownloadManager.Runtime.Remote;

public sealed partial record TorrentMetadata
{
    private const int MaxAddressDepth = 3;

    /// <summary>
    /// The first address outside the info section that <paramref name="refused"/> refuses: a tracker (<c>announce</c>,
    /// <c>announce-list</c>), a web seed (<c>url-list</c>, <c>httpseeds</c>) or a DHT node (<c>nodes</c>, given as
    /// <c>udp://host:port</c>). Null when none is, or when the file is not one <see cref="TryParse"/> accepts. Nothing
    /// is collected, so a file full of short entries costs no more than reading it.
    /// </summary>
    public static string? FirstAddress(byte[] file, Func<string, bool> refused)
    {
        if (file.Length is 0 or > MaxFileBytes) return null;
        try
        {
            ReadOnlySpan<byte> data = file;
            var reader = new BencodeReader(data);
            if (reader.Peek() != 'd') return null;
            foreach (var (key, value) in reader.ReadDictionary(depth: 0))
            {
                var name = data[key];
                var nodes = name.SequenceEqual("nodes"u8);
                if (!nodes && !name.SequenceEqual("announce"u8) && !name.SequenceEqual("announce-list"u8) &&
                    !name.SequenceEqual("url-list"u8) && !name.SequenceEqual("httpseeds"u8))
                    continue;
                if (FirstAddressIn(data[value], nodes, refused, depth: 1) is { } found) return found;
            }
        }
        catch (TorrentFormatException)
        {
            return null;
        }

        return null;
    }

    private static string? FirstAddressIn(ReadOnlySpan<byte> value, bool nodes, Func<string, bool> refused, int depth)
    {
        if (value.Length == 0 || depth > MaxAddressDepth) return null;
        if (char.IsAsciiDigit((char)value[0]))
        {
            var text = TextOf(RequireString(value, "address"));
            return refused(text) ? text : null;
        }

        if (value[0] != 'l') return null;
        // A DHT node is a list of its host and its port.
        if (nodes && depth == 2 && NodeAddress(value) is { } node) return refused(node) ? node : null;
        var reader = new BencodeReader(value);
        foreach (var item in reader.ReadList(depth))
        {
            if (FirstAddressIn(value[item], nodes, refused, depth + 1) is { } found) return found;
        }

        return null;
    }

    private static string? NodeAddress(ReadOnlySpan<byte> pair)
    {
        string? host = null;
        long? port = null;
        var reader = new BencodeReader(pair);
        foreach (var item in reader.ReadList(0))
        {
            var part = pair[item];
            if (host is null && char.IsAsciiDigit((char)part[0])) host = TextOf(RequireString(part, "node"));
            else if (port is null && part[0] == 'i' &&
                     long.TryParse(part[1..^1], System.Globalization.NumberStyles.AllowLeadingSign, System.Globalization.CultureInfo.InvariantCulture, out var number))
                port = number;
        }

        if (string.IsNullOrWhiteSpace(host)) return null;
        var shown = host.Contains(':') && !host.StartsWith('[') ? $"[{host}]" : host;
        // A port Uri can't take still leaves the host to be checked.
        return port is >= 0 and <= 65535 ? $"udp://{shown}:{port}" : $"udp://{shown}";
    }
}

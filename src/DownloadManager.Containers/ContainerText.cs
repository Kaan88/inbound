namespace DownloadManager.Containers;

internal static class ContainerText
{
    private const int TextSniffBytes = 4096;

    public static ReadOnlySpan<byte> SkipBom(ReadOnlySpan<byte> data) =>
        data.StartsWith<byte>([0xEF, 0xBB, 0xBF]) ? data[3..] : data;

    public static bool IsWhitespace(byte value) => value is (byte)' ' or (byte)'\t' or (byte)'\r' or (byte)'\n';

    public static bool IsHexDigit(byte value) =>
        value is >= (byte)'0' and <= (byte)'9' or >= (byte)'a' and <= (byte)'f' or >= (byte)'A' and <= (byte)'F';

    public static bool LooksLikeText(ReadOnlySpan<byte> data)
    {
        var sample = data.Length > TextSniffBytes ? data[..TextSniffBytes] : data;
        foreach (var value in sample)
        {
            if (value < 0x20 && !IsWhitespace(value))
            {
                return false;
            }
        }

        return true;
    }

    public static bool StartsWithMarkup(ReadOnlySpan<byte> data)
    {
        var body = SkipBom(data);
        foreach (var value in body)
        {
            if (!IsWhitespace(value))
            {
                return value == (byte)'<';
            }
        }

        return false;
    }

    public static bool IsBlank(ReadOnlySpan<byte> data)
    {
        foreach (var value in SkipBom(data))
        {
            if (!IsWhitespace(value))
            {
                return false;
            }
        }

        return true;
    }

    public static bool IsHexText(ReadOnlySpan<byte> data)
    {
        var digits = 0;
        foreach (var value in SkipBom(data))
        {
            if (IsWhitespace(value))
            {
                continue;
            }

            if (!IsHexDigit(value))
            {
                return false;
            }

            digits++;
        }

        return digits > 0;
    }

    public static bool HasControlCharacters(string value)
    {
        foreach (var c in value)
        {
            // U+FFFD means a byte could not be decoded, which is as bad as a control character here.
            if (char.IsControl(c) || c == '�')
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>
    /// True for anything shaped like scheme:rest. Deciding which links are usable is the classification stage's
    /// job, so that an unsupported link can be shown with its reason instead of vanishing here. System.Uri is not
    /// used as the test because it refuses real links such as ed2k://file|name|.
    /// </summary>
    public static bool IsUrl(string value)
    {
        var colon = value.IndexOf(':');

        // One letter before the colon is a drive path, not a scheme.
        if (colon < 2 || colon == value.Length - 1 || !char.IsAsciiLetter(value[0]) || char.IsWhiteSpace(value[colon + 1]))
        {
            return false;
        }

        foreach (var c in value.AsSpan(0, colon))
        {
            if (!char.IsAsciiLetterOrDigit(c) && c is not ('+' or '-' or '.'))
            {
                return false;
            }
        }

        return !HasControlCharacters(value);
    }

    /// <summary>Reduces a container-supplied name to one safe segment, because a later stage may build a path from it.</summary>
    public static string? CleanFileName(string? value)
    {
        if (string.IsNullOrWhiteSpace(value) || HasControlCharacters(value))
        {
            return null;
        }

        var name = value[(value.LastIndexOfAny(['/', '\\']) + 1)..].Trim();
        return name is "" or "." or ".." ? null : name;
    }
}

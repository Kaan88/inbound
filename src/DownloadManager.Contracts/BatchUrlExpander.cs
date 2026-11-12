namespace DownloadManager.Contracts;

public static class BatchUrlExpander
{
    public const int DefaultLimit = 1000;

    /// <summary>
    /// Expands `[start-end]` numeric/letter ranges or `[a,b,c]` lists in a URL.
    /// Multiple bracket groups expand as a cartesian product.
    /// </summary>
    public static IReadOnlyList<string> Expand(string? pattern, int limit = DefaultLimit)
    {
        if (string.IsNullOrWhiteSpace(pattern))
        {
            return Array.Empty<string>();
        }

        if (limit <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(limit));
        }

        var results = new List<string>();
        ExpandInto(pattern.Trim(), results, limit);
        return results;
    }

    private static void ExpandInto(string value, List<string> results, int limit)
    {
        var open = value.IndexOf('[');
        if (open < 0)
        {
            results.Add(value);
            return;
        }

        var close = value.IndexOf(']', open + 1);
        if (close < 0)
        {
            throw new FormatException("The batch pattern has an unclosed '['.");
        }

        var token = value[(open + 1)..close];
        // Lazily: "[1-2000000000]" must stop at the limit, not build two billion strings first.
        var any = false;
        foreach (var replacement in ExpandToken(token))
        {
            any = true;
            if (results.Count >= limit)
            {
                throw new FormatException($"The batch pattern expands beyond {limit} URLs.");
            }

            ExpandInto(value[..open] + replacement + value[(close + 1)..], results, limit);
        }

        if (!any)
        {
            throw new FormatException($"The batch range '[{token}]' is empty.");
        }
    }

    private static IEnumerable<string> ExpandToken(string token)
    {
        if (token.Contains(','))
        {
            foreach (var part in token.Split(','))
            {
                var item = part.Trim();
                if (item.Length == 0)
                {
                    throw new FormatException($"The batch list '[{token}]' has an empty item.");
                }

                yield return item;
            }

            yield break;
        }

        var separator = token.IndexOf('-');
        if (separator <= 0 || separator >= token.Length - 1 || token.IndexOf('-', separator + 1) >= 0)
        {
            throw new FormatException(
                $"The batch range '[{token}]' must be a number/letter range or a comma list.");
        }

        var startText = token[..separator].Trim();
        var endText = token[(separator + 1)..].Trim();
        if (int.TryParse(startText, out var startNumber) &&
            int.TryParse(endText, out var endNumber))
        {
            var pad = Math.Max(startText.Length, endText.Length);
            if (startText.Length > 1 && startText[0] == '0' ||
                endText.Length > 1 && endText[0] == '0')
            {
                pad = Math.Max(startText.Length, endText.Length);
            }
            else
            {
                pad = 0;
            }

            var step = startNumber <= endNumber ? 1 : -1;
            for (var i = startNumber; ; i += step)
            {
                yield return pad > 0
                    ? i.ToString($"D{pad}", System.Globalization.CultureInfo.InvariantCulture)
                    : i.ToString(System.Globalization.CultureInfo.InvariantCulture);
                if (i == endNumber)
                {
                    break;
                }
            }

            yield break;
        }

        if (startText.Length == 1 && endText.Length == 1 &&
            char.IsLetter(startText[0]) && char.IsLetter(endText[0]) &&
            char.IsUpper(startText[0]) == char.IsUpper(endText[0]))
        {
            var startChar = startText[0];
            var endChar = endText[0];
            var step = startChar <= endChar ? 1 : -1;
            for (var c = startChar; ; c = (char)(c + step))
            {
                yield return c.ToString();
                if (c == endChar)
                {
                    break;
                }
            }

            yield break;
        }

        throw new FormatException(
            $"The batch range '[{token}]' must use numbers or single letters of the same case.");
    }
}

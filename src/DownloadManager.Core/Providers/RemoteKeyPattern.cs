namespace DownloadManager.Core.Providers;

/// <summary>
/// Finds one account key in text, in the forms it can take: as entered, without dashes, or with
/// them. A key must stand alone, so an unrelated value that merely contains it is left alone, and
/// a key too short to be a real one matches nothing at all rather than half of every address.
/// </summary>
public sealed class RemoteKeyPattern
{
    private const int ShortestKey = 16;
    private const int DashlessGuidLength = 32;

    private readonly string[] _forms;

    public RemoteKeyPattern(string? key)
    {
        var entered = key?.Trim() ?? string.Empty;
        if (entered.Length < ShortestKey)
        {
            _forms = [];
            return;
        }

        var dashless = entered.Replace("-", string.Empty, StringComparison.Ordinal);
        var forms = new List<string> { entered };
        if (dashless.Length >= ShortestKey) forms.Add(dashless);
        if (dashless.Length == DashlessGuidLength && dashless.All(char.IsAsciiHexDigit))
        {
            forms.Add($"{dashless[..8]}-{dashless[8..12]}-{dashless[12..16]}-{dashless[16..20]}-{dashless[20..]}");
        }

        // Longest first, so a dashed key isn't masked piecemeal by a shorter form.
        _forms = forms.Distinct(StringComparer.OrdinalIgnoreCase).OrderByDescending(form => form.Length).ToArray();
    }

    public bool Holds(string? text)
    {
        if (string.IsNullOrEmpty(text) || _forms.Length == 0)
        {
            return false;
        }

        return HoldsRaw(text) || HoldsRaw(Decoded(text));
    }

    /// <summary>
    /// Replaces each form. A key that is only there percent-encoded can't be cut out of the text
    /// exactly, so then everything after the host goes.
    /// </summary>
    public string Mask(string text)
    {
        if (_forms.Length == 0)
        {
            return text;
        }

        var masked = text;
        foreach (var form in _forms)
        {
            masked = MaskForm(masked, form);
        }

        if (!HoldsRaw(Decoded(masked)))
        {
            return masked;
        }

        return Uri.TryCreate(masked, UriKind.Absolute, out var uri) && !string.IsNullOrEmpty(uri.Host)
            ? $"{uri.Scheme}://{uri.Authority}/{UrlRedactor.Mask}"
            : UrlRedactor.Mask;
    }

    private bool HoldsRaw(string text) => _forms.Any(form => IndexOfStandalone(text, form, 0) >= 0);

    private static string MaskForm(string text, string form)
    {
        var start = IndexOfStandalone(text, form, 0);
        if (start < 0)
        {
            return text;
        }

        var result = new System.Text.StringBuilder(text.Length);
        var copied = 0;
        while (start >= 0)
        {
            result.Append(text, copied, start - copied).Append(UrlRedactor.Mask);
            copied = start + form.Length;
            start = IndexOfStandalone(text, form, copied);
        }

        return result.Append(text, copied, text.Length - copied).ToString();
    }

    private static int IndexOfStandalone(string text, string form, int from)
    {
        var index = from;
        while (index <= text.Length - form.Length)
        {
            index = text.IndexOf(form, index, StringComparison.OrdinalIgnoreCase);
            if (index < 0)
            {
                return -1;
            }

            var end = index + form.Length;
            var clearBefore = index == 0 || !char.IsAsciiLetterOrDigit(text[index - 1]);
            var clearAfter = end == text.Length || !char.IsAsciiLetterOrDigit(text[end]);
            if (clearBefore && clearAfter)
            {
                return index;
            }

            index++;
        }

        return -1;
    }

    /// <summary>Twice, because a key in a redirect target's query is encoded twice.</summary>
    private static string Decoded(string text) => Uri.UnescapeDataString(Uri.UnescapeDataString(text));
}

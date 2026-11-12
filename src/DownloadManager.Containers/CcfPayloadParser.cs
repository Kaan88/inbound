using System.Globalization;
using System.Net;
using System.Text;
using System.Text.RegularExpressions;
using System.Xml;

namespace DownloadManager.Containers;

/// <summary>
/// Reads the decrypted CCF text. JDownloader only pulls out url elements, so that is the one layout with evidence
/// behind it and it always works, including over damaged XML. Package, password, comment, file name and size assume
/// a CryptLoad layout (Package, Options/Kommentar, Options/Passwort, Download, FileName, FileSize) that neither the
/// research notes nor a real file have confirmed, so those fields are best effort and matched loosely.
/// </summary>
internal static class CcfPayloadParser
{
    /// <summary>Far more than any real package names; each is tried on the archive in turn.</summary>
    internal const int MaxPasswordsPerPackage = 64;

    private static readonly UTF8Encoding StrictUtf8 = new(encoderShouldEmitUTF8Identifier: false, throwOnInvalidBytes: true);

    // Non-backtracking keeps this linear on hostile text.
    private static readonly Regex UrlElement = new(
        "<url>.*?</url>",
        RegexOptions.IgnoreCase | RegexOptions.Singleline | RegexOptions.CultureInvariant | RegexOptions.NonBacktracking);

    public static void Parse(byte[] plain, ContainerResultBuilder builder, ContainerLimits limits)
    {
        var text = ExtractDocument(plain);
        if (text is null)
        {
            builder.AddProblem(ContainerProblemKind.Malformed, "This CCF file decrypted, but it holds no readable content.");
            return;
        }

        if (text.Contains("<!DOCTYPE", StringComparison.OrdinalIgnoreCase) || text.Contains("<!ENTITY", StringComparison.OrdinalIgnoreCase))
        {
            builder.AddProblem(ContainerProblemKind.UnsafeXml, "This CCF file declares a DTD, which no genuine container needs. It was refused without being processed.");
            return;
        }

        var walker = new Walker(builder, limits);
        try
        {
            walker.Walk(text);
        }
        catch (XmlException)
        {
            // XmlException text quotes the input, so it must not reach the problem list.
            walker.FlushOpenPackage();
            builder.AddProblem(ContainerProblemKind.Malformed, "The contents of this CCF file are damaged. Links were recovered by searching the text, so package details may be missing.");
            RecoverLinks(text, builder);
        }

        if (builder.LinkCount == 0)
        {
            builder.AddProblem(ContainerProblemKind.Malformed, "This CCF file holds no links.");
        }
    }

    private static void RecoverLinks(string text, ContainerResultBuilder builder)
    {
        foreach (var match in UrlElement.EnumerateMatches(text))
        {
            var inner = text.AsSpan(match.Index, match.Length)["<url>".Length..^"</url>".Length];
            var url = WebUtility.HtmlDecode(inner.Trim().ToString());
            if (builder.HasSeenUrl(url))
            {
                continue;
            }

            if (!ContainerText.IsUrl(url))
            {
                builder.AddProblem(ContainerProblemKind.InvalidEntry, "An entry in this CCF file is not a valid link.");
                continue;
            }

            if (!builder.TryAddLink(url, package: null, fileName: null, sizeBytes: null))
            {
                return;
            }
        }
    }

    /// <summary>
    /// CCF stores no padding length, so the plaintext can end in filler bytes. Everything after the last closing
    /// bracket goes, because those bytes are not XML and can stop the parser.
    /// </summary>
    private static string? ExtractDocument(ReadOnlySpan<byte> plain)
    {
        var start = plain.IndexOf((byte)'<');
        var end = plain.LastIndexOf((byte)'>');
        if (start < 0 || end < start)
        {
            return null;
        }

        var document = plain[start..(end + 1)];
        try
        {
            return StrictUtf8.GetString(document);
        }
        catch (DecoderFallbackException)
        {
            // Old files predate UTF-8 defaults. Lossy decoding would corrupt a password, so read them as Latin-1.
            return Encoding.Latin1.GetString(document);
        }
    }

    private sealed class Walker(ContainerResultBuilder builder, ContainerLimits limits)
    {
        private const int MaxEntityChars = 1024;

        private readonly StringBuilder _fieldText = new();
        private PackageDraft? _package;
        private LinkDraft? _download;
        private string? _field;
        private bool _fieldTooLong;
        private bool _limitReached;

        public void Walk(string text)
        {
            var settings = new XmlReaderSettings
            {
                DtdProcessing = DtdProcessing.Prohibit,
                XmlResolver = null,
                MaxCharactersFromEntities = MaxEntityChars,
                MaxCharactersInDocument = text.Length + 1L,
                ConformanceLevel = ConformanceLevel.Fragment,
                IgnoreComments = true,
                IgnoreProcessingInstructions = true,
            };

            using var stringReader = new StringReader(text);
            using var reader = XmlReader.Create(stringReader, settings);
            while (reader.Read())
            {
                if (reader.Depth > limits.MaxXmlDepth)
                {
                    FlushOpenPackage();
                    builder.AddProblem(ContainerProblemKind.Malformed, "The contents of this CCF file are nested too deeply. Reading stopped there.");
                    return;
                }

                switch (reader.NodeType)
                {
                    case XmlNodeType.Element:
                        OnElement(reader);
                        break;
                    case XmlNodeType.Text:
                    case XmlNodeType.CDATA:
                    case XmlNodeType.SignificantWhitespace:
                        OnText(reader.Value);
                        break;
                    case XmlNodeType.EndElement:
                        OnEndElement(reader.LocalName);
                        break;
                }

                if (_limitReached)
                {
                    return;
                }
            }

            FlushOpenPackage();
        }

        public void FlushOpenPackage()
        {
            FlushDownload();
            var package = _package;
            _package = null;
            if (package is null || package.IsEmpty)
            {
                return;
            }

            var finished = new ContainerPackage(package.Name, package.Passwords.ToArray(), package.Comment);
            if (!builder.TryAddPackage(finished))
            {
                _limitReached = true;
                return;
            }

            foreach (var link in package.Links)
            {
                if (!builder.TryAddLink(link.Url!, finished, link.FileName, link.SizeBytes))
                {
                    _limitReached = true;
                    return;
                }
            }
        }

        private void OnElement(XmlReader reader)
        {
            var name = reader.LocalName;
            var isEmpty = reader.IsEmptyElement;
            if (Is(name, "package"))
            {
                FlushOpenPackage();
                _package = new PackageDraft { Name = ReadShortAttribute(reader, "name") };
                if (isEmpty)
                {
                    FlushOpenPackage();
                }
            }
            else if (Is(name, "download"))
            {
                FlushDownload();
                _download = new LinkDraft { Url = ReadShortAttribute(reader, "url") };
                if (isEmpty)
                {
                    FlushDownload();
                }
            }
            else if (!isEmpty && IsField(name))
            {
                _field = name;
                _fieldText.Clear();
                _fieldTooLong = false;
            }
        }

        private void OnText(string value)
        {
            if (_field is null)
            {
                return;
            }

            if (_fieldText.Length + value.Length > limits.MaxFieldChars)
            {
                _fieldTooLong = true;
                return;
            }

            _fieldText.Append(value);
        }

        private void OnEndElement(string name)
        {
            if (Is(name, "package"))
            {
                FlushOpenPackage();
            }
            else if (Is(name, "download"))
            {
                FlushDownload();
            }
            else if (_field is not null && Is(name, _field))
            {
                var field = _field;
                _field = null;
                if (_fieldTooLong)
                {
                    builder.AddProblem(ContainerProblemKind.InvalidEntry, "A value in this CCF file is too long and was ignored.");
                }
                else
                {
                    ApplyField(field, _fieldText.ToString());
                }
            }
        }

        private void ApplyField(string field, string raw)
        {
            if (Is(field, "url"))
            {
                var url = raw.Trim();
                if (_download is not null)
                {
                    // A blank child must not erase a usable Url attribute on the same element.
                    _download.Url = url.Length > 0 ? url : _download.Url;
                }
                else
                {
                    AddLink(new LinkDraft { Url = url });
                }
            }
            else if (Is(field, "filename"))
            {
                if (_download is not null)
                {
                    _download.FileName = ContainerText.CleanFileName(raw);
                }
            }
            else if (Is(field, "filesize"))
            {
                if (_download is not null && long.TryParse(raw.Trim(), NumberStyles.None, CultureInfo.InvariantCulture, out var size))
                {
                    _download.SizeBytes = size;
                }
            }
            else if (Is(field, "passwort") || Is(field, "password"))
            {
                // Surrounding spaces can be part of a password, so only an all-blank value counts as absent.
                if (!string.IsNullOrWhiteSpace(raw))
                {
                    _package ??= new PackageDraft();
                    if (_package.Passwords.Count >= MaxPasswordsPerPackage)
                    {
                        if (!_package.PasswordLimitReported)
                        {
                            // A hostile file can repeat this field without end; each one costs a lookup.
                            _package.PasswordLimitReported = true;
                            builder.AddProblem(ContainerProblemKind.TooManyEntries, $"A package in this CCF file lists more than {MaxPasswordsPerPackage} passwords. Only the first {MaxPasswordsPerPackage} are kept.");
                        }
                    }
                    else if (_package.SeenPasswords.Add(raw))
                    {
                        _package.Passwords.Add(raw);
                    }
                }
            }
            else if (!string.IsNullOrWhiteSpace(raw))
            {
                _package ??= new PackageDraft();
                _package.Comment = raw.Trim();
            }
        }

        private void FlushDownload()
        {
            var download = _download;
            _download = null;
            if (download is not null)
            {
                AddLink(download);
            }
        }

        private void AddLink(LinkDraft link)
        {
            if (string.IsNullOrWhiteSpace(link.Url))
            {
                return;
            }

            if (!ContainerText.IsUrl(link.Url))
            {
                builder.AddProblem(ContainerProblemKind.InvalidEntry, "An entry in this CCF file is not a valid link.");
                return;
            }

            if (_package is not null)
            {
                _package.Links.Add(link);
            }
            else
            {
                _limitReached |= !builder.TryAddLink(link.Url, null, link.FileName, link.SizeBytes);
            }
        }

        private string? ReadShortAttribute(XmlReader reader, string attribute)
        {
            if (!reader.HasAttributes)
            {
                return null;
            }

            for (var moved = reader.MoveToFirstAttribute(); moved; moved = reader.MoveToNextAttribute())
            {
                if (!Is(reader.LocalName, attribute))
                {
                    continue;
                }

                var value = reader.Value.Trim();
                reader.MoveToElement();
                if (value.Length == 0 || value.Length > limits.MaxFieldChars)
                {
                    return null;
                }

                return value;
            }

            reader.MoveToElement();
            return null;
        }

        private static bool IsField(string name) =>
            Is(name, "url") || Is(name, "filename") || Is(name, "filesize")
            || Is(name, "passwort") || Is(name, "password")
            || Is(name, "kommentar") || Is(name, "comment");

        private static bool Is(string name, string expected) => name.Equals(expected, StringComparison.OrdinalIgnoreCase);
    }

    private sealed class PackageDraft
    {
        public string? Name { get; init; }
        public string? Comment { get; set; }
        public List<string> Passwords { get; } = [];
        public HashSet<string> SeenPasswords { get; } = new(StringComparer.Ordinal);
        public bool PasswordLimitReported { get; set; }
        public List<LinkDraft> Links { get; } = [];

        public bool IsEmpty => Name is null && Comment is null && Passwords.Count == 0 && Links.Count == 0;
    }

    private sealed class LinkDraft
    {
        public string? Url { get; set; }
        public string? FileName { get; set; }
        public long? SizeBytes { get; set; }
    }
}

using System.Globalization;
using DownloadManager.Core.Archives;

namespace DownloadManager.Archives;

/// <summary>
/// Parses <c>7z l -slt</c> output: an archive section of <c>Key = Value</c> lines, a line of ten dashes, then one
/// block per entry with a blank line between. Strict on purpose: a name with a line break spills onto the next line,
/// so a line in an entry block that isn't <c>Key = Value</c> makes the listing untrusted.
/// </summary>
internal sealed class SevenZipListing
{
    private const string EntriesStart = "----------";

    private readonly Dictionary<string, ArchiveEntry> _entries = new(StringComparer.Ordinal);
    private readonly List<string> _problems = [];
    private readonly List<string> _missingVolumes = [];
    private readonly List<ArchiveNameFix> _nameFixes = [];
    private readonly bool _backslashIsAName;

    /// <summary>
    /// The hosts 7-Zip names for a zip entry made where a backslash separates folders and can't be part of a name.
    /// Anything else, Unix above all, may hold a backslash in a name, so there it stays one (and is refused).
    /// </summary>
    private static readonly HashSet<string> WindowsHosts = new(StringComparer.Ordinal) { "FAT", "NTFS", "VFAT", "HPFS" };

    private SevenZipListing(bool backslashIsAName)
    {
        _backslashIsAName = backslashIsAName;
    }

    /// <summary>The innermost format, such as <c>Rar5</c>, <c>7z</c> or <c>zip</c>. A split set shows <c>Split</c> first, then what it holds.</summary>
    public string Type { get; private set; } = string.Empty;

    /// <summary>The outermost format, which for <c>name.7z.001</c> is <c>Split</c>.</summary>
    public string OuterType { get; private set; } = string.Empty;

    public bool HasEntrySection { get; private set; }

    public bool IsVolume { get; private set; }

    /// <summary>From 1, as the format records it.</summary>
    public int? VolumeNumber { get; private set; }

    /// <summary>How many volumes 7-Zip opened, which stops short at the first missing one.</summary>
    public int? Volumes { get; private set; }

    public bool HasUnknownLines { get; private set; }

    public bool HasRepeatedNames { get; private set; }

    public bool HoldsDataStreams { get; private set; }

    /// <summary>Problems 7-Zip wrote into the archive section, such as <c>Headers Error</c> or a missing volume.</summary>
    public IReadOnlyList<string> Problems => _problems;

    public IReadOnlyList<string> MissingVolumes => _missingVolumes;

    public IReadOnlyList<ArchiveEntry> Entries => _entries.Values.ToList();

    /// <summary>See <see cref="ArchiveProbe.NameFixes"/>.</summary>
    public IReadOnlyList<ArchiveNameFix> NameFixes => _nameFixes;

    public static SevenZipListing Parse(string output) => Parse(output, !OperatingSystem.IsWindows());

    /// <param name="output">What <c>7z l -slt</c> printed.</param>
    /// <param name="backslashIsAName">
    /// True where 7-Zip keeps a zip's backslashes in names, which is every system but Windows (checked with 7-Zip 26.04
    /// on Linux). Tests pass it to check either way on any system.
    /// </param>
    public static SevenZipListing Parse(string output, bool backslashIsAName)
    {
        var listing = new SevenZipListing(backslashIsAName);
        var lines = output.TrimStart('﻿').Split('\n').Select(line => line.TrimEnd('\r')).ToList();
        var start = lines.IndexOf(EntriesStart);
        listing.ReadArchiveSection(start < 0 ? lines : lines.Take(start));
        if (start < 0) return listing;

        listing.HasEntrySection = true;
        var block = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var line in lines.Skip(start + 1))
        {
            if (line.Length == 0)
            {
                listing.Commit(block);
                continue;
            }

            var split = line.IndexOf(" = ", StringComparison.Ordinal);
            var key = split < 0 ? null : line[..split];
            var value = split < 0 ? null : line[(split + 3)..];
            if (key is null && line.EndsWith(" =", StringComparison.Ordinal))
            {
                key = line[..^2];
                value = string.Empty;
            }

            // Every block starts with Path, and a key never comes twice in one. Anything else is a name spilling over.
            if (key is null || (block.Count == 0 && key != "Path") || !block.TryAdd(key, value!))
            {
                listing.HasUnknownLines = true;
                continue;
            }
        }

        listing.Commit(block);
        return listing;
    }

    private void ReadArchiveSection(IEnumerable<string> lines)
    {
        // A bare line counts only under ERRORS: or WARNINGS:, up to the next key or blank line. Elsewhere it is the
        // archive's comment, which is free text: it is ignored here, and if it imitates the entry section the
        // real section's own dashes then show up among the entries and make the listing untrusted.
        //
        // 7-Zip prints the comment verbatim between "Comment = {" and "}", and the comment can hold "}" itself, so where
        // it ends can't be told. Everything from the first Comment key on is therefore ignored: otherwise a comment line
        // "Type = gzip" would make the archive a compressed stream whose entries nobody checks, and "WARNINGS:" with
        // "Missing volume : x" would send the set back to waiting again and again. The type, the errors and the
        // warnings come before it (checked on 7-Zip 26.04); a zip lists its volume counts after it, and those are lost.
        var underHeading = false;
        foreach (var line in lines)
        {
            if (line.StartsWith("Comment = ", StringComparison.Ordinal) || line == "Comment =") break;
            var split = line.IndexOf(" = ", StringComparison.Ordinal);
            if (split < 0)
            {
                var text = line.Trim();
                if (text is "ERRORS:" or "WARNINGS:") underHeading = true;
                else if (text.Length == 0) underHeading = false;
                else if (underHeading) _problems.Add(text);
                continue;
            }

            underHeading = false;
            var key = line[..split];
            var value = line[(split + 3)..].Trim();
            switch (key)
            {
                case "Type":
                    if (OuterType.Length == 0) OuterType = value;
                    Type = value;
                    break;
                case "ERROR" or "WARNING":
                    _problems.Add(value);
                    break;
                case "Multivolume":
                    IsVolume |= value == "+";
                    break;
                case "Volume Index" when int.TryParse(value, NumberStyles.None, CultureInfo.InvariantCulture, out var index):
                    VolumeNumber ??= index + 1;
                    break;
                case "Volumes" when int.TryParse(value, NumberStyles.None, CultureInfo.InvariantCulture, out var count):
                    Volumes ??= count;
                    if (count > 1) IsVolume = true;
                    break;
            }
        }

        foreach (var problem in _problems)
        {
            if (problem.StartsWith("Missing volume : ", StringComparison.Ordinal)) AddMissing(problem["Missing volume : ".Length..]);
        }
    }

    private void AddMissing(string name)
    {
        var file = Path.GetFileName(name.Trim());
        if (file.Length > 0 && !_missingVolumes.Contains(file, StringComparer.OrdinalIgnoreCase)) _missingVolumes.Add(file);
    }

    private void Commit(Dictionary<string, string> block)
    {
        if (block.Count == 0) return;
        var path = block["Path"];
        // RAR and zip listings say Folder = +. A 7z listing has no such line, only the D attribute.
        var isFolder = Flag(block, "Folder") || (block.TryGetValue("Attributes", out var letters) && letters.Split(' ')[0].Contains('D'));
        var kind = isFolder ? ArchiveEntryKind.Directory : ArchiveEntryKind.File;
        string? target = null;
        if (Text(block, "Symbolic Link") is { } symbolic) (kind, target) = (ArchiveEntryKind.SymbolicLink, symbolic);
        else if (Text(block, "Hard Link") is { } hard) (kind, target) = (ArchiveEntryKind.HardLink, hard);
        else if (Text(block, "Copy Link") is { } copy) (kind, target) = (ArchiveEntryKind.HardLink, copy);
        else if (Text(block, "Link") is { } link) (kind, target) = (ArchiveEntryKind.SymbolicLink, link);
        else if (block.TryGetValue("Attributes", out var attributes) && IsReparsePoint(attributes)) kind = ArchiveEntryKind.Other;

        if (Flag(block, "Alternate Stream")) HoldsDataStreams = true;

        long size = 0;
        if (block.TryGetValue("Size", out var sizeText) && sizeText.Length > 0 &&
            !long.TryParse(sizeText, NumberStyles.None, CultureInfo.InvariantCulture, out size))
        {
            HasUnknownLines = true;
        }

        // A zip made on Windows (PowerShell 5.1's Compress-Archive, for one) can put a backslash between folders. Where
        // 7-Zip keeps it in the name, the entry is listed by the folders it means, so every check below and in
        // ExtractionSafety sees those, and the extractor moves what 7-Zip wrote to match (ArchiveProbe.NameFixes).
        if (_backslashIsAName && path.Contains('\\') && Type.Equals("zip", StringComparison.OrdinalIgnoreCase) &&
            block.TryGetValue("Host OS", out var host) && WindowsHosts.Contains(host))
        {
            var written = path;
            path = path.Replace('\\', '/').TrimEnd('/');
            _nameFixes.Add(new ArchiveNameFix(written, path, kind == ArchiveEntryKind.Directory));
        }

        var entry = new ArchiveEntry(path, size, kind, Flag(block, "Encrypted"), target);
        if (!_entries.TryAdd(path, entry))
        {
            // A folder can be stored twice. Two files of one name are two files.
            if (kind != ArchiveEntryKind.Directory || _entries[path].Kind != ArchiveEntryKind.Directory) HasRepeatedNames = true;
        }

        block.Clear();
    }

    private static bool Flag(Dictionary<string, string> block, string key) => block.TryGetValue(key, out var value) && value == "+";

    private static string? Text(Dictionary<string, string> block, string key) =>
        block.TryGetValue(key, out var value) && !string.IsNullOrWhiteSpace(value) ? value : null;

    /// <summary>
    /// 7-Zip shows Windows attribute letters, then a Unix mode when the archive has one, whose first letter <c>l</c>
    /// is a symbolic link. A Windows reparse point is caught on disk by the staging walk.
    /// </summary>
    private static bool IsReparsePoint(string attributes)
    {
        var space = attributes.IndexOf(' ');
        return space >= 0 && attributes[(space + 1)..].StartsWith('l');
    }
}

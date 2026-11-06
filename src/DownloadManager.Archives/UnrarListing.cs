using System.Globalization;
using System.Text.RegularExpressions;
using DownloadManager.Core.Archives;

namespace DownloadManager.Archives;

/// <summary>
/// Parses <c>UnRAR lt</c> output. Strict on purpose: a name with a line break is cut short in this
/// output and spills onto the next line, so a line it does not recognise makes the listing untrusted.
/// </summary>
internal sealed partial class UnrarListing
{
    private static readonly HashSet<string> IgnoredLabels = new(StringComparer.Ordinal)
    {
        "Packed size", "Ratio", "mtime", "ctime", "atime", "Modified", "Created", "Accessed", "Attributes",
        "CRC32", "CRC32 MAC", "Pack-CRC32", "BLAKE2", "BLAKE2 MAC", "Pack-BLAKE2",
        "Host OS", "Compression", "File version",
    };

    private readonly Dictionary<string, ArchiveEntry> _entries = new(StringComparer.Ordinal);
    private readonly List<string> _archives = [];
    private readonly HashSet<string> _inThisVolume = new(StringComparer.Ordinal);

    private UnrarListing()
    {
    }

    public bool HasDetails { get; private set; }

    public string Version { get; private set; } = string.Empty;

    public bool HeadersEncrypted { get; private set; }

    public bool IsVolume { get; private set; }

    public int? VolumeNumber { get; private set; }

    public bool HasUnknownLines { get; private set; }

    /// <summary>A name came up a second time other than as a file carried over from the previous volume.</summary>
    public bool HasRepeatedNames { get; private set; }

    /// <summary>One per path: a file split across volumes is listed once per volume.</summary>
    public IReadOnlyList<ArchiveEntry> Entries => _entries.Values.ToList();

    /// <summary>Each volume the tool went through, as its <c>Archive:</c> lines name them.</summary>
    public IReadOnlyList<string> Archives => _archives;

    public bool IsRar5OrLater =>
        int.TryParse(Version.Split('.')[0], NumberStyles.None, CultureInfo.InvariantCulture, out var major) && major >= 5;

    public static UnrarListing Parse(string output)
    {
        var listing = new UnrarListing();
        Pending? entry = null;

        foreach (var rawLine in output.TrimStart('﻿').Split('\n'))
        {
            var line = rawLine.TrimEnd('\r');
            if (line.Length == 0) continue;

            var colon = line.IndexOf(": ", StringComparison.Ordinal);
            var label = colon < 0 ? line.TrimEnd(':').TrimStart() : line[..colon].TrimStart();
            var value = colon < 0 ? string.Empty : line[(colon + 2)..];
            if (colon < 0 && !line.EndsWith(':'))
            {
                listing.HasUnknownLines = true;
                continue;
            }

            switch (label)
            {
                case "Archive":
                    listing.Commit(ref entry);
                    listing._inThisVolume.Clear();
                    if (value.Length > 0) listing._archives.Add(value.Trim());
                    break;
                case "Details":
                    listing.Commit(ref entry);
                    listing.ReadDetails(value);
                    break;
                case "Name":
                    listing.Commit(ref entry);
                    entry = new Pending(value);
                    break;
                case "Type" when entry is not null:
                    entry.Kind = KindFrom(value);
                    entry.HasType = true;
                    break;
                case "Target" when entry is not null:
                    entry.Target = value;
                    break;
                case "Size" when entry is not null:
                    if (long.TryParse(value.Trim(), NumberStyles.None, CultureInfo.InvariantCulture, out var size) && size >= 0) entry.Size = size;
                    else listing.HasUnknownLines = true;
                    break;
                case "Flags" when entry is not null:
                    if (value.Contains("encrypted", StringComparison.Ordinal)) entry.Encrypted = true;
                    break;
                case "Ratio" when entry is not null:
                    // <-- and <-> mark a file that started in an earlier volume.
                    if (value.Contains("<-", StringComparison.Ordinal)) entry.ContinuesEarlierVolume = true;
                    break;
                default:
                    if (entry is null || !IgnoredLabels.Contains(label)) listing.HasUnknownLines = true;
                    break;
            }
        }

        listing.Commit(ref entry);
        return listing;
    }

    private static ArchiveEntryKind KindFrom(string type) => type.Trim() switch
    {
        "File" => ArchiveEntryKind.File,
        "Directory" => ArchiveEntryKind.Directory,
        "Unix symbolic link" or "Windows symbolic link" => ArchiveEntryKind.SymbolicLink,
        "NTFS junction point" => ArchiveEntryKind.Junction,
        "Hard link" or "File reference" => ArchiveEntryKind.HardLink,
        _ => ArchiveEntryKind.Other,
    };

    private void ReadDetails(string details)
    {
        // Only the first volume's line describes the archive that was asked about.
        var isFirst = !HasDetails;
        HasDetails = true;
        var tokens = details.Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries);
        if (isFirst && tokens.Length > 0 && tokens[0].StartsWith("RAR", StringComparison.Ordinal))
        {
            Version = tokens[0][3..].Trim();
        }

        foreach (var token in tokens)
        {
            if (token == "encrypted headers") HeadersEncrypted = true;
            if (isFirst && VolumeToken().Match(token) is { Success: true } volume)
            {
                IsVolume = true;
                if (int.TryParse(volume.Groups["number"].Value, NumberStyles.None, CultureInfo.InvariantCulture, out var number))
                {
                    VolumeNumber = number;
                }
            }
        }
    }

    private void Commit(ref Pending? pending)
    {
        if (pending is null) return;
        var kind = pending.HasType ? pending.Kind : ArchiveEntryKind.Other;
        var entry = new ArchiveEntry(pending.Name, pending.Size, kind, pending.Encrypted, pending.Target);
        if (_entries.TryGetValue(pending.Name, out var earlier))
        {
            // A folder can be stored again in each volume. Two files of one name are two files, however the merge below would read them.
            if (kind != ArchiveEntryKind.Directory && (!_inThisVolume.Add(pending.Name) || !pending.ContinuesEarlierVolume)) HasRepeatedNames = true;
            entry = entry with { Size = Math.Max(earlier.Size, entry.Size), IsEncrypted = earlier.IsEncrypted || entry.IsEncrypted };
        }
        else
        {
            _inThisVolume.Add(pending.Name);
        }

        _entries[pending.Name] = entry;
        pending = null;
    }

    [GeneratedRegex(@"^volume(?:\s+(?<number>\d+))?$", RegexOptions.CultureInvariant)]
    private static partial Regex VolumeToken();

    private sealed class Pending(string name)
    {
        public string Name { get; } = name;

        public ArchiveEntryKind Kind { get; set; }

        public bool HasType { get; set; }

        public string? Target { get; set; }

        public long Size { get; set; }

        public bool Encrypted { get; set; }

        public bool ContinuesEarlierVolume { get; set; }
    }
}

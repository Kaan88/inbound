namespace DownloadManager.Core.Archives;

public interface IArchiveExtractor
{
    /// <summary>
    /// Reads what an archive holds without writing anything. A header-encrypted archive with no
    /// password gives a probe with <see cref="ArchiveProbe.HeadersEncrypted"/> set and no entries;
    /// a password that fails on such an archive throws <see cref="ExtractionException"/>.
    /// </summary>
    Task<ArchiveProbe> ProbeAsync(string firstVolume, string? password, CancellationToken cancellationToken);

    /// <summary>Extracts through staging, or throws <see cref="ExtractionException"/> leaving the destination as it was.</summary>
    Task<ExtractionResult> ExtractAsync(
        ExtractionRequest request,
        IProgress<ExtractionProgress>? progress,
        CancellationToken cancellationToken);
}

public enum ExtractionFailureKind
{
    PasswordRequired,
    WrongPassword,
    MissingVolume,
    Corrupted,
    UnsupportedVersion,
    ToolUnavailable,
    DiskSpace,
    Permission,
    UnsafePath,
    Cancelled,
    Unknown,
}

public enum ExtractionConflictPolicy
{
    Overwrite,
    Rename,
    Skip,
}

public enum ArchiveEntryKind
{
    File,
    Directory,
    SymbolicLink,
    Junction,
    HardLink,
    Other,
}

public sealed record ArchiveEntry(
    string Path,
    long Size,
    ArchiveEntryKind Kind,
    bool IsEncrypted = false,
    string? LinkTarget = null)
{
    public bool IsLink => Kind is ArchiveEntryKind.SymbolicLink or ArchiveEntryKind.Junction or ArchiveEntryKind.HardLink;
}

public sealed record ArchiveProbe
{
    public required string FirstVolume { get; init; }

    /// <summary>True when any entry's data or the headers need a password.</summary>
    public required bool IsEncrypted { get; init; }

    public required bool HeadersEncrypted { get; init; }

    /// <summary>Empty when <see cref="HeadersEncrypted"/> is set and no password was given.</summary>
    public required IReadOnlyList<ArchiveEntry> Entries { get; init; }

    /// <summary>Format version as the tool states it, such as <c>5</c>. Crafted RAR4 files can state <c>1.5</c>.</summary>
    public required string Version { get; init; }

    public required bool IsVolume { get; init; }

    /// <summary>The volume number in the header, when the format records one. RAR4 does not.</summary>
    public int? VolumeNumber { get; init; }

    /// <summary>Null when headers are encrypted, no password was given and no gap shows in the names.</summary>
    public required bool? AllVolumesPresent { get; init; }

    public IReadOnlyList<string> MissingVolumes { get; init; } = [];

    /// <summary>The listing held a line the parser could not account for, which is how a name with a line break shows up.</summary>
    public bool ListingHasUnknownLines { get; init; }

    public long TotalListedSize => Entries.Where(entry => entry.Kind == ArchiveEntryKind.File).Sum(entry => entry.Size);
}

/// <remarks>
/// A class rather than a record: a record's generated <c>ToString</c> would print the password.
/// </remarks>
public sealed class ExtractionRequest
{
    public required string FirstVolume { get; init; }

    public required string Destination { get; init; }

    /// <summary>Parent of the staging folder. Must be on the destination's volume.</summary>
    public required string StagingRoot { get; init; }

    public string? Password { get; init; }

    public ExtractionConflictPolicy ConflictPolicy { get; init; } = ExtractionConflictPolicy.Rename;

    public bool PreserveFolders { get; init; } = true;
}

public enum ExtractionPhase
{
    Probing,
    Extracting,
    Verifying,
    Finalizing,
}

/// <param name="Percent">Overall across the whole set, from the tool's output. Null outside <see cref="ExtractionPhase.Extracting"/>.</param>
public sealed record ExtractionProgress(
    ExtractionPhase Phase,
    int? Percent = null,
    int FilesFinalized = 0,
    int FilesTotal = 0);

public enum ExtractedFileOutcome
{
    Written,
    Overwrote,
    Renamed,
    Skipped,
}

/// <param name="FinalPath">Where the file is now. For a skipped file, the existing file that was kept.</param>
public sealed record ExtractedFile(string EntryPath, string FinalPath, ExtractedFileOutcome Outcome);

public sealed record ExtractionResult(string Destination, IReadOnlyList<ExtractedFile> Files);

public sealed class ExtractionException : Exception
{
    public ExtractionException(ExtractionFailureKind kind, string message, IReadOnlyList<string>? missingVolumes = null)
        : base(message)
    {
        Kind = kind;
        MissingVolumes = missingVolumes ?? [];
    }

    public ExtractionFailureKind Kind { get; }

    public IReadOnlyList<string> MissingVolumes { get; }
}

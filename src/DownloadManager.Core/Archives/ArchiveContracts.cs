namespace DownloadManager.Core.Archives;

/// <summary>
/// The name staging folders start with. Nothing from outside may use it: not an entry in an archive, not a
/// package from a container, not a folder named after a download. Whatever wears this name inside a
/// destination has to be either ours or refused, or a planted one could pass for a crashed extraction.
/// </summary>
public static class ArchiveWorkingFolders
{
    public const string Prefix = ".inbound-extract-";

    public static bool IsReserved(string segment) => segment.StartsWith(Prefix, StringComparison.OrdinalIgnoreCase);
}

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

/// <summary>
/// Whoever looks after archive passwords, for whoever learned one before the downloads existed. It
/// knows a package by id and nothing of where the password came from.
/// </summary>
public interface IPackagePasswordKeeper
{
    /// <summary>Keeps the password for the package's archives, and an empty one clears it. Returns why it couldn't, in words for the user, and never quotes the password.</summary>
    /// <param name="keepExisting">It is only an offer: a password this package already has stays, because the user may have given it since.</param>
    Task<string?> SetPackagePasswordAsync(Guid packageId, string? password, bool keepExisting = false, CancellationToken cancellationToken = default);
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

    /// <summary>The format as 7-Zip names it, such as <c>Rar5</c>, <c>Rar</c> for RAR4, <c>7z</c> or <c>zip</c>.</summary>
    public required string Version { get; init; }

    public required bool IsVolume { get; init; }

    /// <summary>The volume number in the header, when the format records one. RAR4 does not.</summary>
    public int? VolumeNumber { get; init; }

    /// <summary>Null when headers are encrypted, no password was given and no gap shows in the names.</summary>
    public required bool? AllVolumesPresent { get; init; }

    public IReadOnlyList<string> MissingVolumes { get; init; } = [];

    /// <summary>The listing held a line the parser could not account for, which is how a name with a line break shows up.</summary>
    public bool ListingHasUnknownLines { get; init; }

    /// <summary>The same name came up twice other than as one file continuing into the next volume, so one would overwrite the other.</summary>
    public bool ListingHasRepeatedNames { get; init; }

    /// <summary>The archive stores NTFS data streams, which an ordinary listing doesn't show and the tool restores regardless.</summary>
    public bool HoldsDataStreams { get; init; }

    /// <summary>The volumes the tool went through, by full path, in order. Empty when it could not list them, as for encrypted headers without a password.</summary>
    public IReadOnlyList<string> VolumesRead { get; init; } = [];

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

    /// <summary>
    /// Files an overwrite must not replace, normally the archive's own volumes. An entry that would
    /// land on one is renamed instead.
    /// </summary>
    public IReadOnlyCollection<string>? KeepFiles { get; init; }

    /// <summary>
    /// The result has to survive a power cut before <see cref="Committed"/> runs, because the caller
    /// means to delete the archives right after. Costs a flush per file, so only then.
    /// </summary>
    public bool MakeDurable { get; init; }

    /// <summary>
    /// Runs once every file is in place and before the staging folder goes, and is the caller's
    /// chance to record success durably. Staging still knows what it placed until this returns, so
    /// a crash before then, or a throw from here, undoes the attempt and a retry cannot duplicate
    /// its output. A caller that has recorded success must discard that attempt's leftover staging
    /// rather than recover it, because recovery undoes the attempt.
    /// </summary>
    public Func<ExtractionResult, Task>? Committed { get; init; }
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

public sealed record ExtractionResult(string Destination, IReadOnlyList<ExtractedFile> Files)
{
    /// <summary>
    /// The volumes the tool really read, by full path. Only these can be said to be extracted, whatever their
    /// names suggest. Null when the extractor can't say, and then none of them may be treated as used up.
    /// </summary>
    public IReadOnlyList<string>? VolumesRead { get; init; }

    /// <summary>Every file and folder was flushed to the disk, as <see cref="ExtractionRequest.MakeDurable"/> asks. Without this the archives are the only safe copy.</summary>
    public bool FlushedToDisk { get; init; }
}

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

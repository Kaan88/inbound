namespace DownloadManager.Core.Archives;

public enum ArchiveSetState
{
    /// <summary>A volume is still downloading and nobody has asked for extraction yet.</summary>
    Downloading,

    /// <summary>The files are here and nobody has asked for extraction.</summary>
    Downloaded,

    /// <summary>Extraction was asked for and is waiting for volumes or for its turn.</summary>
    Waiting,

    Extracting,
    Completed,

    /// <summary>The archives are kept. <see cref="ArchiveSetRecord.FailureKind"/> says why.</summary>
    Failed,
}

public enum ArchiveDestinationMode
{
    NextToArchive,
    NamedSubfolder,
    ChosenFolder,
}

/// <summary>
/// Extraction settings, kept apart from the application's settings: a save from the Options window
/// rebuilds those field by field and would drop anything it does not know about.
/// </summary>
public sealed class ArchiveSettings
{
    public const int CurrentSchemaVersion = 1;

    public int SchemaVersion { get; set; } = CurrentSchemaVersion;

    public bool AutoExtract { get; set; }

    /// <summary>Downloads that finished before this were not asked to extract themselves.</summary>
    public DateTimeOffset? AutoExtractSince { get; set; }

    /// <summary>When this feature first ran. Older downloads are history and appear only when asked for by name.</summary>
    public DateTimeOffset TrackSince { get; set; }

    public ArchiveDestinationMode DestinationMode { get; set; } = ArchiveDestinationMode.NextToArchive;

    public string? ChosenFolder { get; set; }

    public bool PreserveFolders { get; set; } = true;

    public ExtractionConflictPolicy ConflictPolicy { get; set; } = ExtractionConflictPolicy.Rename;

    public bool DeleteArchives { get; set; }

    public ArchiveSettings Clone() => (ArchiveSettings)MemberwiseClone();
}

/// <summary>
/// One archive set: its own identity, separate from the jobs that downloaded it and any package
/// they belong to. <see cref="ProtectedPassword"/> is a DPAPI blob and never leaves this record.
/// </summary>
public sealed class ArchiveSetRecord
{
    public const int CurrentSchemaVersion = 1;

    public int SchemaVersion { get; set; } = CurrentSchemaVersion;

    public Guid Id { get; set; }

    /// <summary>Folder and the first volume's name as it is really spelled. Two triggers for one set arrive at one key.</summary>
    public string Key { get; set; } = string.Empty;

    public string Directory { get; set; } = string.Empty;

    public string FirstVolumeName { get; set; } = string.Empty;

    public string BaseName { get; set; } = string.Empty;

    public ArchiveSetState State { get; set; }

    /// <summary>Someone asked for this set to be extracted, either the auto-extract setting or a user.</summary>
    public bool Wanted { get; set; }

    /// <summary>Removed from the list. Kept so the next start does not list it again; a new download of it brings it back.</summary>
    public bool Removed { get; set; }

    public DateTimeOffset? RemovedAt { get; set; }

    public ExtractionFailureKind? FailureKind { get; set; }

    public string? FailureMessage { get; set; }

    /// <summary>Volumes present on disk.</summary>
    public List<string> Volumes { get; set; } = [];

    public List<string> MissingVolumes { get; set; } = [];

    /// <summary>Volumes with a download that has not finished yet, whether it is running, paused or queued.</summary>
    public List<string> DownloadingVolumes { get; set; } = [];

    /// <summary>The part of <see cref="DownloadingVolumes"/> whose downloads are all paused.</summary>
    public List<string> PausedVolumes { get; set; } = [];

    /// <summary>The part of <see cref="DownloadingVolumes"/> whose downloads wait in a queue, none running.</summary>
    public List<string> QueuedVolumes { get; set; } = [];

    /// <summary>Volumes whose download failed or was cancelled, leaving no file.</summary>
    public List<string> FailedVolumes { get; set; } = [];

    /// <summary>Volumes the tool itself named as absent, which names alone cannot show for a last volume.</summary>
    public List<string> ToolMissingVolumes { get; set; } = [];

    /// <summary>Which completions of which jobs have been counted, so a repeat of one does nothing.</summary>
    public Dictionary<Guid, DateTimeOffset> SeenJobs { get; set; } = [];

    public List<Guid> JobIds { get; set; } = [];

    public Guid? PackageId { get; set; }

    public string? ProtectedPassword { get; set; }

    /// <summary>The package password this set refused, by its stamp. A newer one is tried; that one is not tried again.</summary>
    public DateTimeOffset? RejectedPackagePasswordStamp { get; set; }

    public string? Destination { get; set; }

    /// <summary>Where this set's staging folder is, written before the run so a restart can find it.</summary>
    public string? StagingRoot { get; set; }

    public int Attempts { get; set; }

    public int FilesWritten { get; set; }

    public int FilesOverwritten { get; set; }

    public int FilesRenamed { get; set; }

    public int FilesSkipped { get; set; }

    /// <summary>Decided at success and saved before the first delete, so a restart finishes the job.</summary>
    public List<string> VolumesToDelete { get; set; } = [];

    public bool ArchivesDeleted { get; set; }

    public string? Note { get; set; }

    public DateTimeOffset CreatedAt { get; set; }

    public DateTimeOffset UpdatedAt { get; set; }

    public DateTimeOffset? CompletedAt { get; set; }

    public ArchiveSetRecord Clone()
    {
        var copy = (ArchiveSetRecord)MemberwiseClone();
        copy.Volumes = [.. Volumes];
        copy.MissingVolumes = [.. MissingVolumes];
        copy.DownloadingVolumes = [.. DownloadingVolumes];
        copy.PausedVolumes = [.. PausedVolumes];
        copy.QueuedVolumes = [.. QueuedVolumes];
        copy.FailedVolumes = [.. FailedVolumes];
        copy.ToolMissingVolumes = [.. ToolMissingVolumes];
        copy.SeenJobs = new Dictionary<Guid, DateTimeOffset>(SeenJobs);
        copy.JobIds = [.. JobIds];
        copy.VolumesToDelete = [.. VolumesToDelete];
        return copy;
    }
}

/// <summary>A password that applies to every set of one package. <see cref="ProtectedPassword"/> is a DPAPI blob.</summary>
public sealed class ArchivePackagePassword
{
    public Guid PackageId { get; set; }

    public string ProtectedPassword { get; set; } = string.Empty;

    /// <summary>Tells one password from its replacement without keeping anything about its value.</summary>
    public DateTimeOffset Stamp { get; set; }
}

public interface IArchiveStore
{
    /// <summary>
    /// Sets whose documents are there but could not be read by the last <see cref="LoadSetsAsync"/>, which says
    /// nothing about what they hold. They stay on disk, and nothing may start the same set over until a later start can read them.
    /// </summary>
    IReadOnlyCollection<Guid> UnreadableSets => [];

    /// <summary>
    /// Packages whose password files are there but could not be read by the last <see cref="LoadPackagePasswordsAsync"/>.
    /// Such a package may have a password the user gave, so an offered one must not replace it.
    /// </summary>
    IReadOnlyCollection<Guid> UnreadablePackagePasswords => [];

    /// <summary>Null when none was ever saved.</summary>
    Task<ArchiveSettings?> LoadSettingsAsync(CancellationToken cancellationToken = default);

    Task SaveSettingsAsync(ArchiveSettings settings, CancellationToken cancellationToken = default);

    Task<IReadOnlyList<ArchiveSetRecord>> LoadSetsAsync(CancellationToken cancellationToken = default);

    Task SaveSetAsync(ArchiveSetRecord set, CancellationToken cancellationToken = default);

    Task DeleteSetAsync(Guid id, CancellationToken cancellationToken = default);

    Task<IReadOnlyList<ArchivePackagePassword>> LoadPackagePasswordsAsync(CancellationToken cancellationToken = default);

    Task SavePackagePasswordAsync(ArchivePackagePassword password, CancellationToken cancellationToken = default);

    Task DeletePackagePasswordAsync(Guid packageId, CancellationToken cancellationToken = default);
}

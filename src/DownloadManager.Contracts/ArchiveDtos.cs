namespace DownloadManager.Contracts;

/// <summary>Extraction messages. Additive within protocol version 1; the browser bridge never carries them.</summary>
public static class ArchiveMessageTypes
{
    public const string Prefix = "archive.";
    public const string SettingsGet = "archive.settings.get";
    public const string SettingsSet = "archive.settings.set";
    public const string SettingsResult = "archive.settings.result";
    public const string List = "archive.list";
    public const string ListResult = "archive.listResult";
    public const string Extract = "archive.extract";
    public const string ExtractResult = "archive.extractResult";
    public const string SetPassword = "archive.setPassword";
    public const string Retry = "archive.retry";
    public const string Cancel = "archive.cancel";
    public const string Remove = "archive.remove";
    public const string ActionResult = "archive.actionResult";

    /// <summary>Host push: the whole current state of one set, or which set left the list.</summary>
    public const string Changed = "archive.changed";
}

/// <summary>One archive set as clients see it. Carries no password, only whether one is held.</summary>
public sealed class ArchiveSetDto
{
    public Guid Id { get; set; }
    public string Name { get; set; } = string.Empty;
    public string Directory { get; set; } = string.Empty;
    public string FirstVolume { get; set; } = string.Empty;

    /// <summary><c>downloading</c>, <c>downloaded</c>, <c>waiting</c>, <c>extracting</c>, <c>completed</c> or <c>failed</c>.</summary>
    public string State { get; set; } = string.Empty;

    /// <summary>The state in plain words, ready to show.</summary>
    public string StateText { get; set; } = string.Empty;

    /// <summary>
    /// When failed: <c>passwordRequired</c>, <c>wrongPassword</c>, <c>missingVolume</c>, <c>corrupted</c>,
    /// <c>unsupportedVersion</c>, <c>toolUnavailable</c>, <c>diskSpace</c>, <c>permission</c>, <c>unsafePath</c>,
    /// <c>cancelled</c> or <c>unknown</c>.
    /// </summary>
    public string? FailureKind { get; set; }

    public string? FailureMessage { get; set; }

    /// <summary>The set is stopped until a password arrives.</summary>
    public bool NeedsPassword { get; set; }

    /// <summary>A password is on record for this set or its package.</summary>
    public bool HasPassword { get; set; }

    public List<string> Volumes { get; set; } = [];
    public List<string> MissingVolumes { get; set; } = [];
    public List<string> DownloadingVolumes { get; set; } = [];

    /// <summary>The part of <see cref="DownloadingVolumes"/> whose downloads are all paused.</summary>
    public List<string> PausedVolumes { get; set; } = [];

    /// <summary>The part of <see cref="DownloadingVolumes"/> whose downloads wait in a queue, none running.</summary>
    public List<string> QueuedVolumes { get; set; } = [];

    public List<string> FailedVolumes { get; set; } = [];

    /// <summary><c>probing</c>, <c>extracting</c>, <c>verifying</c> or <c>finalizing</c> while extracting.</summary>
    public string? Phase { get; set; }

    /// <summary>Overall across the set, from the tool's own output. Null when it has given none.</summary>
    public int? Percent { get; set; }

    public int FilesFinalized { get; set; }
    public int FilesTotal { get; set; }
    public string? Destination { get; set; }
    public int Attempts { get; set; }
    public int FilesWritten { get; set; }
    public int FilesOverwritten { get; set; }
    public int FilesRenamed { get; set; }
    public int FilesSkipped { get; set; }
    public bool ArchivesDeleted { get; set; }
    public string? Note { get; set; }
    public Guid? PackageId { get; set; }
    public List<Guid> JobIds { get; set; } = [];
    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset UpdatedAt { get; set; }
    public DateTimeOffset? CompletedAt { get; set; }
}

public static class ArchiveSetProgress
{
    /// <summary>
    /// Whether an outcome can still follow without the user doing anything: it is extracting, next in line, or
    /// has volumes on their way. A set short of a volume that failed or is missing waits for the user.
    /// </summary>
    public static bool IsStillWorking(this ArchiveSetDto set) => set.State switch
    {
        "extracting" => true,
        "waiting" => set.DownloadingVolumes.Count > 0 || (set.FailedVolumes.Count == 0 && set.MissingVolumes.Count == 0),
        "downloading" => set.DownloadingVolumes.Count > 0,
        _ => false,
    };
}

public sealed class ArchiveChangedDto
{
    /// <summary>The set as it is now. Null when it left the list.</summary>
    public ArchiveSetDto? Set { get; set; }

    public Guid? RemovedId { get; set; }
}

public sealed class ArchiveListResult
{
    public List<ArchiveSetDto> Sets { get; set; } = [];
}

public sealed class ArchiveToolStatusDto
{
    public bool Found { get; set; }

    /// <summary>The executable an extraction would run right now.</summary>
    public string? Path { get; set; }

    public string Message { get; set; } = string.Empty;
}

public sealed class ArchiveSettingsDto
{
    public bool AutoExtract { get; set; }

    /// <summary><c>nextToArchive</c>, <c>subfolder</c> or <c>folder</c>.</summary>
    public string Destination { get; set; } = "nextToArchive";

    public string? Folder { get; set; }
    public bool PreserveFolders { get; set; } = true;

    /// <summary><c>rename</c>, <c>overwrite</c> or <c>skip</c>.</summary>
    public string ConflictPolicy { get; set; } = "rename";

    public bool DeleteArchives { get; set; }
}

/// <summary>Only what is set changes. Null leaves a setting as it is.</summary>
public sealed class ArchiveSettingsUpdate
{
    public bool? AutoExtract { get; set; }
    public string? Destination { get; set; }
    public string? Folder { get; set; }
    public bool? PreserveFolders { get; set; }
    public string? ConflictPolicy { get; set; }
    public bool? DeleteArchives { get; set; }
}

public sealed class ArchiveSettingsResult
{
    public bool Ok { get; set; }
    public string? Error { get; set; }
    public ArchiveSettingsDto Settings { get; set; } = new();
    public ArchiveToolStatusDto Tool { get; set; } = new();
}

/// <remarks>A class rather than a record: a record's generated <c>ToString</c> would print the password.</remarks>
public sealed class ArchiveExtractRequest
{
    public List<Guid>? JobIds { get; set; }
    public Guid? PackageId { get; set; }

    /// <summary>Absolute path of a volume of the set, normally the first.</summary>
    public string? FilePath { get; set; }

    /// <summary>Optional. Kept for the set, or for the package when given with <see cref="PackageId"/>.</summary>
    public string? Password { get; set; }
}

public sealed class ArchiveExtractIgnored
{
    public string Item { get; set; } = string.Empty;
    public string Reason { get; set; } = string.Empty;
}

public sealed class ArchiveExtractResult
{
    public bool Ok { get; set; }
    public string? Error { get; set; }
    public List<ArchiveSetDto> Sets { get; set; } = [];

    /// <summary>What was asked for and left alone, such as a file that is not a RAR archive.</summary>
    public List<ArchiveExtractIgnored> Ignored { get; set; } = [];
}

/// <remarks>A class rather than a record: a record's generated <c>ToString</c> would print the password.</remarks>
public sealed class ArchiveSetPasswordRequest
{
    public Guid? SetId { get; set; }

    /// <summary>Applies to every set of the package, including ones not downloaded yet.</summary>
    public Guid? PackageId { get; set; }

    /// <summary>Absolute path of a volume. The file need not exist yet, which is how a set gets a password in advance.</summary>
    public string? FilePath { get; set; }

    /// <summary>Empty clears what is on record.</summary>
    public string? Password { get; set; }
}

public sealed class ArchiveActionRequest
{
    public Guid SetId { get; set; }
}

public sealed class ArchiveActionResult
{
    public bool Ok { get; set; }
    public string? Error { get; set; }
    public ArchiveSetDto? Set { get; set; }
}

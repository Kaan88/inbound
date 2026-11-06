namespace DownloadManager.Core.Providers;

/// <summary>
/// Where a remote job stands. This is about the provider's side only: bytes reaching this computer
/// are the local jobs' business, and start at <see cref="LocalStarted"/>.
/// </summary>
public enum RemoteJobState
{
    /// <summary>Saved, and nothing has been sent. It may be waiting for approval or for its turn.</summary>
    Intended,
    /// <summary>About to send, or sending. A restart can't tell which, so it becomes <see cref="SubmissionUnknown"/>.</summary>
    Submitting,
    /// <summary>The provider may or may not have received it. Its list decides, never a second guess.</summary>
    SubmissionUnknown,
    /// <summary>On the provider, which is working on it.</summary>
    Waiting,
    /// <summary>Ready, and the user chooses which files to download.</summary>
    AwaitingSelection,
    /// <summary>Local jobs exist. Nothing more happens here.</summary>
    LocalStarted,
    /// <summary>Stopped for a reason retrying soon wouldn't fix. Waits for the user.</summary>
    Failed,
    /// <summary>The account is busy for now. Retries by itself.</summary>
    Blocked,
    /// <summary>Taken off this app's list. Never means the provider's item was touched.</summary>
    Removed,
}

public enum RemoteSelectionMode
{
    AskWhenReady,
    All,
    Chosen,
}

/// <summary>
/// One remote job on disk. Provider state lives here and not in settings, because saving Options
/// drops fields it doesn't know. The protected fields only ever hold ciphertext, so a record is safe
/// to write, copy and log; nothing else in it is a link or a password.
/// </summary>
public sealed record RemoteJobRecord
{
    public const int CurrentSchemaVersion = 1;

    public int SchemaVersion { get; init; } = CurrentSchemaVersion;
    public required Guid Id { get; init; }
    public required string Provider { get; init; }
    public required RemoteItemKind Kind { get; init; }

    public string? ProtectedInput { get; init; }
    public string? ProtectedTorrent { get; init; }
    public string? ProtectedLinkPassword { get; init; }
    /// <summary>
    /// For the archives this job brings, which is not the source site's password for the link. Held
    /// here only until there is a package to give it to, and <see cref="ArchivePasswordHandedOver"/> says that happened.
    /// </summary>
    public string? ProtectedArchivePassword { get; init; }
    public bool ArchivePasswordHandedOver { get; init; }
    /// <summary>It came with the input, such as from a container, and nobody typed it. It never replaces one the package already has.</summary>
    public bool ArchivePasswordOffered { get; init; }
    /// <summary>Where local jobs go when this job is one of several that belong together. Null gives it a package of its own.</summary>
    public RemoteJobPlacement? Placement { get; init; }

    /// <summary>What the provider knows the input by, worked out here, so it can be found after a lost answer.</summary>
    public string? PredictedHash { get; init; }
    public string? ItemId { get; init; }
    public string? Name { get; init; }
    /// <summary>Host name only, without path or key.</summary>
    public string? DisplayHost { get; init; }
    public string? HosterName { get; init; }

    public RemoteJobState State { get; init; }
    public RemoteStage? Stage { get; init; }
    public string? RawState { get; init; }
    public double Progress { get; init; }
    public long SpeedBytesPerSecond { get; init; }
    public long? EtaSeconds { get; init; }
    public bool Cached { get; init; }

    public RemoteFailureKind? FailureKind { get; init; }
    public string? FailureMessage { get; init; }
    public DateTimeOffset? NextCheckAt { get; init; }
    public int ConsecutiveFailures { get; init; }
    public int UnchangedPolls { get; init; }

    /// <summary>Null means the default download folder when the local jobs are created.</summary>
    public string? Destination { get; init; }
    public int QueueId { get; init; }
    public string StartMode { get; init; } = "startNow";
    public RemoteSelectionMode SelectionMode { get; init; }
    public IReadOnlyList<string> SelectedFileIds { get; init; } = [];

    public bool ApprovedUncached { get; init; }
    /// <summary>It isn't cached and nobody has agreed to start work on the provider yet, so nothing was sent.</summary>
    public bool ApprovalNeeded { get; init; }
    /// <summary>
    /// A copied link nobody has asked for yet. <see cref="ApprovalNeeded"/> is set too, but nothing is known about
    /// whether it is cached, because nothing is sent or even asked until the user agrees.
    /// </summary>
    public bool HeldForApproval { get; init; }

    public Guid? PackageId { get; init; }
    /// <summary>What was left out and why, in words for the user.</summary>
    public IReadOnlyList<string> Notes { get; init; } = [];

    public DateTimeOffset CreatedAt { get; init; }
    public DateTimeOffset UpdatedAt { get; init; }
    /// <summary>When the submission was last sent, or about to be. The grace period counts from here.</summary>
    public DateTimeOffset? SubmittedAt { get; init; }
    /// <summary>When a submitted item was first missing from everything the provider lists.</summary>
    public DateTimeOffset? MissingSince { get; init; }
    public int Resubmits { get; init; }
}

/// <summary>
/// Several remote jobs that came as one group, such as the links of a container's package. They share a
/// local package, and an item holding one file puts it straight in the folder, without the item's own
/// folder around it, so the parts of a split archive that came through separate links meet in one place.
/// </summary>
/// <param name="PackageKey">The local package's key, which starts with <c>remote:</c>.</param>
/// <param name="PackageName">What the local package is called. Null lets the file names decide.</param>
/// <param name="Subfolder">One safe folder name under the destination. Null means the destination itself.</param>
public sealed record RemoteJobPlacement(string PackageKey, string? PackageName, string? Subfolder);

/// <summary>An account's limits that have to outlast a restart.</summary>
public sealed record RemoteAccountPacing
{
    /// <summary>The next web download may be created at this time. Reserved before the request is sent.</summary>
    public DateTimeOffset? NextWebCreateAt { get; init; }
    /// <summary>The provider said to stop calling until then, with a 429.</summary>
    public DateTimeOffset? RateLimitedUntil { get; init; }
    /// <summary>Set by a quota error, and nothing new is created until the user retries.</summary>
    public string? CreationStoppedReason { get; init; }
}

public interface IRemoteJobStore
{
    /// <summary>Everything that can be read. A damaged file is set aside rather than stopping the rest.</summary>
    Task<IReadOnlyList<RemoteJobRecord>> LoadAllAsync(CancellationToken cancellationToken = default);
    Task SaveAsync(RemoteJobRecord record, CancellationToken cancellationToken = default);
    Task DeleteAsync(Guid id, CancellationToken cancellationToken = default);
    Task<RemoteAccountPacing> LoadPacingAsync(string providerId, CancellationToken cancellationToken = default);
    Task SavePacingAsync(string providerId, RemoteAccountPacing pacing, CancellationToken cancellationToken = default);
}

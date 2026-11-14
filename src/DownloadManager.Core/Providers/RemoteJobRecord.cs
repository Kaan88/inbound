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
    /// <summary>
    /// What a magnet or a .torrent file gets when nobody said: asks when ready if the provider had it cached, and
    /// otherwise takes all, because then the files come only once the provider has fetched every one of them.
    /// Becomes one or the other once the cache is checked, before anything is sent.
    /// </summary>
    AskIfCached,
}

/// <summary>
/// A file chosen from the input's own list before the provider listed the item, by what finds it again there:
/// its path without the first folder, and its size. The provider puts a folder of its own around what a torrent
/// holds, and makes one up for a torrent of one file, so that folder can't be matched on. Compared exactly.
/// </summary>
public sealed record RemoteFileKey(string Path, long Size)
{
    /// <summary>Null for a path that isn't safe to use, which no file could be found again by.</summary>
    public static RemoteFileKey? Of(string? path, long size)
    {
        var safe = RemotePaths.ToSafeRelativePath(path);
        if (safe is null)
        {
            return null;
        }

        var parts = safe.Split(System.IO.Path.DirectorySeparatorChar);
        return new RemoteFileKey(parts.Length == 1 ? parts[0] : string.Join('/', parts[1..]), size);
    }
}

/// <summary>
/// One remote job on disk. Provider state lives here and not in settings, because saving Options
/// drops fields it doesn't know. The protected fields only ever hold ciphertext, so a record is safe
/// to write, copy and log; nothing else in it is a link or a password.
/// </summary>
// PERF.10: `set`, not `init`, on every member: generated JSON metadata builds a type with init-only members without
// running its property initializers, so a member missing from an old file would read as 0 or null instead of its default.
public sealed record RemoteJobRecord
{
    public const int CurrentSchemaVersion = 1;

    public int SchemaVersion { get; set; } = CurrentSchemaVersion;
    public required Guid Id { get; set; }
    public required string Provider { get; set; }
    public required RemoteItemKind Kind { get; set; }

    public string? ProtectedInput { get; set; }
    public string? ProtectedTorrent { get; set; }
    public string? ProtectedLinkPassword { get; set; }
    /// <summary>
    /// For the archives this job brings, which is not the source site's password for the link. Held
    /// here only until there is a package to give it to, and <see cref="ArchivePasswordHandedOver"/> says that happened.
    /// </summary>
    public string? ProtectedArchivePassword { get; set; }
    public bool ArchivePasswordHandedOver { get; set; }
    /// <summary>It came with the input, such as from a container, and nobody typed it. It never replaces one the package already has.</summary>
    public bool ArchivePasswordOffered { get; set; }
    /// <summary>Where local jobs go when this job is one of several that belong together. Null gives it a package of its own.</summary>
    public RemoteJobPlacement? Placement { get; set; }

    /// <summary>What the provider knows the input by, worked out here, so it can be found after a lost answer.</summary>
    public string? PredictedHash { get; set; }
    public string? ItemId { get; set; }
    public string? Name { get; set; }
    /// <summary>Host name only, without path or key.</summary>
    public string? DisplayHost { get; set; }
    public string? HosterName { get; set; }

    public RemoteJobState State { get; set; }
    public RemoteStage? Stage { get; set; }
    public string? RawState { get; set; }
    public double Progress { get; set; }
    public long SpeedBytesPerSecond { get; set; }
    public long? EtaSeconds { get; set; }
    public bool Cached { get; set; }

    public RemoteFailureKind? FailureKind { get; set; }
    public string? FailureMessage { get; set; }
    public DateTimeOffset? NextCheckAt { get; set; }
    public int ConsecutiveFailures { get; set; }
    public int UnchangedPolls { get; set; }

    /// <summary>Null means the default download folder when the local jobs are created.</summary>
    public string? Destination { get; set; }
    public int QueueId { get; set; }
    public string StartMode { get; set; } = "startNow";
    public RemoteSelectionMode SelectionMode { get; set; }
    public IReadOnlyList<string> SelectedFileIds { get; set; } = [];
    /// <summary>
    /// With <see cref="RemoteSelectionMode.Chosen"/>: files chosen from the input's own list, such as a .torrent file's,
    /// before the provider had one. Turned into <see cref="SelectedFileIds"/> when the item is ready. Any still here
    /// then weren't found, and the job asks again rather than leave them out.
    /// </summary>
    public IReadOnlyList<RemoteFileKey> SelectedPaths { get; set; } = [];

    public bool ApprovedUncached { get; set; }
    /// <summary>It isn't cached and nobody has agreed to start work on the provider yet, so nothing was sent.</summary>
    public bool ApprovalNeeded { get; set; }
    /// <summary>
    /// A copied link nobody has asked for yet. <see cref="ApprovalNeeded"/> is set too, but nothing is known about
    /// whether it is cached, because nothing is sent or even asked until the user agrees.
    /// </summary>
    public bool HeldForApproval { get; set; }

    public Guid? PackageId { get; set; }
    /// <summary>What was left out and why, in words for the user.</summary>
    public IReadOnlyList<string> Notes { get; set; } = [];

    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset UpdatedAt { get; set; }
    /// <summary>When the submission was last sent, or about to be. The grace period counts from here.</summary>
    public DateTimeOffset? SubmittedAt { get; set; }
    /// <summary>When a submitted item was first missing from everything the provider lists.</summary>
    public DateTimeOffset? MissingSince { get; set; }
    public int Resubmits { get; set; }
    /// <summary>What the provider itself said when it last failed to take this, in clean words. Null if it said nothing.</summary>
    public string? LastSubmitAnswer { get; set; }
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
    public DateTimeOffset? NextWebCreateAt { get; set; }
    /// <summary>The provider said to stop calling until then, with a 429.</summary>
    public DateTimeOffset? RateLimitedUntil { get; set; }
    /// <summary>Set by a quota error, and nothing new is created until the user retries.</summary>
    public string? CreationStoppedReason { get; set; }
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

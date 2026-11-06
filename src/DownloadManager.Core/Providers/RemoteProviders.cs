namespace DownloadManager.Core.Providers;

/// <summary>What a remote item is on the provider's side.</summary>
public enum RemoteItemKind
{
    Torrent,
    WebDownload,
    Usenet,
}

/// <summary>
/// How far the provider has got. This is about the provider's own work, never about bytes
/// reaching this computer.
/// </summary>
public enum RemoteStage
{
    /// <summary>The provider reported something this app does not recognise. Never treated as ready.</summary>
    Unknown,
    /// <summary>Waiting in the provider's queue.</summary>
    Queued,
    /// <summary>The provider is still finding out what the item contains.</summary>
    MetadataPending,
    /// <summary>The provider is fetching the content.</summary>
    Processing,
    /// <summary>The provider is fetching but has stopped making progress.</summary>
    Stalled,
    /// <summary>Files can be requested.</summary>
    Ready,
    /// <summary>The provider gave up.</summary>
    Failed,
}

[Flags]
public enum RemoteCapabilities
{
    None = 0,
    Account = 1,
    Catalog = 1 << 1,
    Torrents = 1 << 2,
    WebDownloads = 1 << 3,
    Usenet = 1 << 4,
    CacheCheck = 1 << 5,
    HosterList = 1 << 6,
}

/// <summary>Why a provider call failed. Decides what the app does next, not what the user reads.</summary>
public enum RemoteFailureKind
{
    /// <summary>The key is missing, wrong or revoked.</summary>
    Authentication,
    /// <summary>The account's plan, quota or limit does not allow it. Retrying soon won't help.</summary>
    AccountLimit,
    /// <summary>A limit that clears by itself, such as too many active downloads. Wait, then retry.</summary>
    AccountBusy,
    /// <summary>The source site wants a password for this link. This is not an archive password.</summary>
    LinkPasswordRequired,
    /// <summary>Too many requests. Retry after a wait.</summary>
    RateLimited,
    /// <summary>The provider has no such item on this account.</summary>
    NotFound,
    /// <summary>The provider refused the input: bad link, offline file, unsupported site.</summary>
    Rejected,
    /// <summary>A problem on the provider's side. Retry later.</summary>
    ServiceError,
    /// <summary>The provider could not be reached. For a submission, it may still have been accepted.</summary>
    Network,
    /// <summary>The response could not be understood.</summary>
    BadResponse,
}

public sealed class RemoteProviderException : Exception
{
    public RemoteProviderException(RemoteFailureKind kind, string? providerCode, string message, TimeSpan? retryAfter = null, Exception? inner = null)
        : base(message, inner)
    {
        Kind = kind;
        ProviderCode = providerCode;
        RetryAfter = retryAfter;
    }

    public RemoteFailureKind Kind { get; }
    public string? ProviderCode { get; }
    public TimeSpan? RetryAfter { get; }
}

/// <summary>Points at one item on one provider. Stable: stays valid while the item exists.</summary>
public sealed record RemoteItemRef(string Provider, RemoteItemKind Kind, string ItemId);

public sealed record RemoteFile
{
    public required string FileId { get; init; }
    /// <summary>Path as the provider reports it. Untrusted: pass it through <see cref="RemotePaths"/> before use.</summary>
    public required string Path { get; init; }
    public long Size { get; init; }
    public string? Md5 { get; init; }
    public string? MimeType { get; init; }
    public bool Infected { get; init; }
}

public sealed record RemoteItem
{
    public required RemoteItemRef Ref { get; init; }
    public string Name { get; init; } = "";
    /// <summary>Torrent info hash, or the provider's hash for a web item. Used to find an item again.</summary>
    public string? Hash { get; init; }
    public IReadOnlyList<string> AlternativeHashes { get; init; } = [];
    public long Size { get; init; }
    public RemoteStage Stage { get; init; }
    /// <summary>The provider's own word for the state, for display.</summary>
    public string? RawState { get; init; }
    /// <summary>0 to 1, the provider's own progress.</summary>
    public double Progress { get; init; }
    public long SpeedBytesPerSecond { get; init; }
    public long? EtaSeconds { get; init; }
    public DateTimeOffset? CreatedAt { get; init; }
    public DateTimeOffset? UpdatedAt { get; init; }
    public DateTimeOffset? ExpiresAt { get; init; }
    public bool Cached { get; init; }
    public string? Error { get; init; }
    public IReadOnlyList<RemoteFile> Files { get; init; } = [];
}

public sealed record RemoteAccountInfo
{
    public string? DisplayPlan { get; init; }
    public bool Premium { get; init; }
    public DateTimeOffset? PremiumUntil { get; init; }
    public DateTimeOffset? CooldownUntil { get; init; }
}

public sealed record RemoteHoster
{
    public required string Name { get; init; }
    public IReadOnlyList<string> Domains { get; init; } = [];
    /// <summary>False when the provider says the site is currently down.</summary>
    public bool Available { get; init; }
    public string? Pattern { get; init; }
    public string? Note { get; init; }
    public int? DailyLinkLimit { get; init; }
    public int? DailyLinksUsed { get; init; }
    public long? DailyBandwidthLimit { get; init; }
    public long? DailyBandwidthUsed { get; init; }
    public long? PerLinkSizeLimit { get; init; }
}

public sealed record RemoteCacheHit(string Hash, string? Name, long? Size);

public sealed record RemoteSubmission
{
    public required RemoteItemKind Kind { get; init; }
    /// <summary>Magnet or web address. Secret: a link can hold a decryption key.</summary>
    public string? Link { get; init; }
    /// <summary>A .torrent file, instead of <see cref="Link"/>.</summary>
    public byte[]? TorrentFile { get; init; }
    /// <summary>The password the source site wants for the link. Not an archive password.</summary>
    public string? LinkPassword { get; init; }
    public string? DisplayName { get; init; }
    /// <summary>Have the provider refuse rather than start work when the content isn't cached.</summary>
    public bool OnlyIfCached { get; init; }
}

public sealed record RemoteSubmissionResult(RemoteItemRef? Ref, string? Hash, bool Queued);

/// <summary>A direct address that works for a short time. Secret: never persist or log it.</summary>
public sealed class RemoteDirectLink
{
    public RemoteDirectLink(Uri url, DateTimeOffset issuedAt)
    {
        Url = url;
        IssuedAt = issuedAt;
    }

    public Uri Url { get; }
    public DateTimeOffset IssuedAt { get; }
    public override string ToString() => "[direct link]";
}

/// <summary>Limits a provider sets on how its direct links are used.</summary>
public sealed record RemoteTransferPolicy
{
    public int MaxConnectionsPerFile { get; init; } = 4;
    public TimeSpan LinkLifetime { get; init; } = TimeSpan.FromHours(3);
}

/// <summary>Everything a provider has. Optional abilities are separate interfaces it may also implement.</summary>
public interface IRemoteProvider
{
    string Id { get; }
    string DisplayName { get; }
    RemoteCapabilities Capabilities { get; }
    RemoteTransferPolicy TransferPolicy { get; }
    Task<RemoteAccountInfo> GetAccountAsync(CancellationToken cancellationToken = default);
}

public interface IRemoteCatalog
{
    Task<IReadOnlyList<RemoteItem>> ListAsync(RemoteItemKind kind, int offset, int limit, bool fresh, CancellationToken cancellationToken = default);
    /// <returns>Null when the provider has no such item on this account.</returns>
    Task<RemoteItem?> GetAsync(RemoteItemRef item, bool fresh, CancellationToken cancellationToken = default);
}

public interface IRemoteCacheChecker
{
    /// <summary>What the provider already holds, by hash. A missing hash means not cached.</summary>
    Task<IReadOnlyDictionary<string, RemoteCacheHit>> CheckCachedAsync(RemoteItemKind kind, IReadOnlyCollection<string> hashes, CancellationToken cancellationToken = default);
}

public interface IRemoteSubmitter
{
    /// <summary>
    /// The hash the provider will know this input by, computed locally, so an item can be found
    /// again when the answer to a submission is lost. Null if it can't be known beforehand.
    /// </summary>
    string? PredictHash(RemoteItemKind kind, string link);

    Task<RemoteSubmissionResult> SubmitAsync(RemoteSubmission submission, CancellationToken cancellationToken = default);
}

public interface IRemoteLinkResolver
{
    Task<RemoteDirectLink> ResolveLinkAsync(RemoteItemRef item, string fileId, CancellationToken cancellationToken = default);
}

public interface IRemoteHosterDirectory
{
    Task<IReadOnlyList<RemoteHoster>> GetHostersAsync(CancellationToken cancellationToken = default);
}

/// <summary>Removes an item from the provider. Only ever called from an explicit, confirmed user action.</summary>
public interface IRemoteItemRemover
{
    Task RemoveAsync(RemoteItemRef item, CancellationToken cancellationToken = default);
}

namespace DownloadManager.Contracts;

// Remote provider messages. Replies and events carry no API key, direct link, magnet, hoster link
// or link password, in any form: a client only ever sees names, stages and plain words. A few
// requests do carry a secret on its way in. Those print as nothing, and it never comes back.

public sealed class RemoteAccountRequest
{
    /// <summary>A provider id. Null means every provider for a read, and the only one for a change.</summary>
    public string? Provider { get; set; }
    /// <summary>For <c>remote.account.get</c>: ask the provider now instead of showing the last known answer.</summary>
    public bool Refresh { get; set; }
}

public sealed class RemoteSetKeyRequest
{
    public string? Provider { get; set; }
    public string Key { get; set; } = string.Empty;

    public override string ToString() => "[set key]";
}

public sealed class RemoteAccountDto
{
    public string Provider { get; set; } = string.Empty;
    public string ProviderName { get; set; } = string.Empty;
    /// <summary>What the provider can do: account, catalog, torrents, webDownloads, usenet, cacheCheck, hosterList.</summary>
    public List<string> Capabilities { get; set; } = [];
    /// <summary>The kinds <c>remote.item.get</c> can read: torrents, webdl, usenet.</summary>
    public List<string> ItemKinds { get; set; } = [];
    /// <summary>The only thing a client can ever learn about the key itself.</summary>
    public bool HasKey { get; set; }
    /// <summary>A key is saved and can't be read on this Windows account. Different from no key: it has to be entered again.</summary>
    public bool KeyUnreadable { get; set; }
    public string? Plan { get; set; }
    public bool Premium { get; set; }
    public DateTimeOffset? PremiumUntil { get; set; }
    public DateTimeOffset? CooldownUntil { get; set; }
    /// <summary>When the provider last answered. Null when it hasn't since the key changed or the Host started.</summary>
    public DateTimeOffset? CheckedAt { get; set; }
}

public sealed class RemoteAccountResult
{
    public bool Ok { get; set; }
    public string? Error { get; set; }
    /// <summary>Set when the provider refused or couldn't be reached: authentication, network and so on.</summary>
    public string? FailureKind { get; set; }
    public List<RemoteAccountDto> Accounts { get; set; } = [];
}

public sealed class RemoteClassifyRequest
{
    /// <summary>One pasted string or the full path of a local .torrent file. Not repeated in the reply.</summary>
    public string Text { get; set; } = string.Empty;

    public override string ToString() => "[classify]";
}

public sealed class RemoteClassifyResult
{
    public bool Ok { get; set; }
    public string? Error { get; set; }
    /// <summary>folderLink, magnet, torrentFile, hosterLink, providerDirectLink, ordinaryLink, undetermined (no list of supported sites to tell, so don't treat it as ordinary) or unsupported.</summary>
    public string Kind { get; set; } = string.Empty;
    public string? Provider { get; set; }
    public string? ProviderName { get; set; }
    /// <summary>torrents, webdl or usenet.</summary>
    public string? ItemKind { get; set; }
    public string? ItemId { get; set; }
    public string? Name { get; set; }
    public string? DisplayHost { get; set; }
    public string? HosterName { get; set; }
    public bool? HosterAvailable { get; set; }
    public string? HosterNote { get; set; }
    /// <summary>Links left today on this site, as the provider last said. Null when it sets no limit.</summary>
    public int? HosterDailyLinksLeft { get; set; }
    /// <summary>Bytes left today on this site, as the provider last said. Null when it sets no limit.</summary>
    public long? HosterDailyBytesLeft { get; set; }
    /// <summary>The largest single file the provider takes from this site. Null when it sets no limit.</summary>
    public long? HosterLargestFile { get; set; }
    /// <summary>Why it can't be used, for unsupported or undetermined.</summary>
    public string? Reason { get; set; }
    /// <summary>Worth showing with an input that can be used.</summary>
    public string? Note { get; set; }
}

public sealed class RemoteAddRequestDto
{
    /// <summary>A folder link, magnet, hoster link or the full path of a local .torrent file.</summary>
    public string? Input { get; set; }
    /// <summary>With <see cref="ItemKind"/> and <see cref="ItemId"/> instead of <see cref="Input"/>: an item from <c>remote.item.get</c>.</summary>
    public string? Provider { get; set; }
    public string? ItemKind { get; set; }
    public string? ItemId { get; set; }
    /// <summary>A full path. Null uses the default download folder.</summary>
    public string? Destination { get; set; }
    public int QueueId { get; set; }
    /// <summary>startNow, queue or later.</summary>
    public string StartMode { get; set; } = "startNow";
    /// <summary>ask, all or chosen. Null means chosen with <see cref="FileIds"/>; otherwise a hoster link takes all, and anything else asks.</summary>
    public string? Selection { get; set; }
    public List<string>? FileIds { get; set; }
    /// <summary>Go ahead and have the provider fetch it if it isn't cached.</summary>
    public bool ApproveUncached { get; set; }
    public string? LinkPassword { get; set; }
    /// <summary>For the archives this brings, not for the link. Kept encrypted, and only <c>hasArchivePassword</c> ever comes back.</summary>
    public string? ArchivePassword { get; set; }

    public override string ToString() => "[remote add]";
}

public sealed class RemoteJobDto
{
    public Guid Id { get; set; }
    public string Provider { get; set; } = string.Empty;
    public string ProviderName { get; set; } = string.Empty;
    /// <summary>torrents, webdl or usenet.</summary>
    public string Kind { get; set; } = string.Empty;
    /// <summary>intended, submitting, submissionUnknown, waiting, awaitingSelection, localStarted, failed, blocked or removed.</summary>
    public string State { get; set; } = string.Empty;
    /// <summary>What to show. Remote stages are words, not local progress.</summary>
    public string Status { get; set; } = string.Empty;
    /// <summary>unknown, queued, metadataPending, processing, stalled, ready or failed.</summary>
    public string? Stage { get; set; }
    /// <summary>The provider's own word for the state, shown as it is.</summary>
    public string? RawState { get; set; }
    /// <summary>The provider's own progress, 0 to 1. Not about bytes reaching this computer.</summary>
    public double Progress { get; set; }
    public long SpeedBytesPerSecond { get; set; }
    public long? EtaSeconds { get; set; }
    public string? ItemId { get; set; }
    public string? Name { get; set; }
    public string? DisplayHost { get; set; }
    public bool Cached { get; set; }
    /// <summary>It isn't cached and nothing was sent. <c>remote.retry</c> with approveUncached lets it go ahead.</summary>
    public bool ApprovalNeeded { get; set; }
    public bool ApprovedUncached { get; set; }
    public bool HasLinkPassword { get; set; }
    /// <summary>An archive password is on record for what this job brings. Nothing else about it can be read.</summary>
    public bool HasArchivePassword { get; set; }
    public string? FailureKind { get; set; }
    public string? FailureMessage { get; set; }
    public DateTimeOffset? NextCheckAt { get; set; }
    public string? Destination { get; set; }
    public int QueueId { get; set; }
    public string StartMode { get; set; } = "startNow";
    /// <summary>askWhenReady, all or chosen.</summary>
    public string SelectionMode { get; set; } = string.Empty;
    public List<string> SelectedFileIds { get; set; } = [];
    public Guid? PackageId { get; set; }
    public List<string> Notes { get; set; } = [];
    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset UpdatedAt { get; set; }
}

public sealed class RemoteAddResultDto
{
    public bool Ok { get; set; }
    public string? Error { get; set; }
    /// <summary>Something asked for that didn't happen, because an active job already covers this input. Worth showing.</summary>
    public string? Note { get; set; }
    /// <summary>An active job already covers this input, and it is the one returned.</summary>
    public bool Existing { get; set; }
    public string? InputKind { get; set; }
    public RemoteJobDto? Job { get; set; }
}

public sealed class RemoteListResult
{
    public bool Ok { get; set; }
    public string? Error { get; set; }
    public List<RemoteJobDto> Jobs { get; set; } = [];
}

public sealed class RemoteItemRequest
{
    public string? Provider { get; set; }
    /// <summary>torrents, webdl or usenet.</summary>
    public string Kind { get; set; } = string.Empty;
    /// <summary>One item, with its files, instead of a page.</summary>
    public string? ItemId { get; set; }
    public int Offset { get; set; }
    /// <summary>1 to 250. Anything else uses 50.</summary>
    public int Limit { get; set; } = 50;
    /// <summary>Include each item's files in a page. A single item always has them.</summary>
    public bool IncludeFiles { get; set; }
    /// <summary>Ask the provider past its own cache.</summary>
    public bool Fresh { get; set; }
}

public sealed class RemoteFileDto
{
    public string FileId { get; set; } = string.Empty;
    /// <summary>The relative path it would have. Null when its path isn't safe, so it can't be chosen.</summary>
    public string? RelativePath { get; set; }
    public long Size { get; set; }
    public bool Infected { get; set; }
    public bool Selectable { get; set; }
    public string? Note { get; set; }
}

public sealed class RemoteItemDto
{
    public string Provider { get; set; } = string.Empty;
    public string Kind { get; set; } = string.Empty;
    public string ItemId { get; set; } = string.Empty;
    public string? Name { get; set; }
    public long Size { get; set; }
    public string? Stage { get; set; }
    public string? RawState { get; set; }
    public double Progress { get; set; }
    public long SpeedBytesPerSecond { get; set; }
    public long? EtaSeconds { get; set; }
    public bool Cached { get; set; }
    public string? Error { get; set; }
    public DateTimeOffset? CreatedAt { get; set; }
    public DateTimeOffset? UpdatedAt { get; set; }
    public DateTimeOffset? ExpiresAt { get; set; }
    public int FileCount { get; set; }
    /// <summary>Null when the request didn't ask for them.</summary>
    public List<RemoteFileDto>? Files { get; set; }
}

public sealed class RemoteItemResult
{
    public bool Ok { get; set; }
    public string? Error { get; set; }
    public string? FailureKind { get; set; }
    public List<RemoteItemDto> Items { get; set; } = [];
    public int Offset { get; set; }
    public int Limit { get; set; }
    /// <summary>The page was full, so there may be more.</summary>
    public bool HasMore { get; set; }
}

public sealed class RemoteJobIdRequest
{
    public Guid JobId { get; set; }
}

public sealed class RemoteFilesResultDto
{
    public bool Ok { get; set; }
    public string? Error { get; set; }
    /// <summary>The provider doesn't know the files yet. Nothing is invented meanwhile.</summary>
    public bool Pending { get; set; }
    public List<RemoteFileDto> Files { get; set; } = [];
}

public sealed class RemoteSelectRequest
{
    public Guid JobId { get; set; }
    public bool All { get; set; }
    public List<string>? FileIds { get; set; }
}

public sealed class RemoteRetryRequest
{
    public Guid JobId { get; set; }
    /// <summary>For a failure that asked for one. It is the source site's password for the link, not an archive password.</summary>
    public string? LinkPassword { get; set; }
    /// <summary>Also the way to approve a job waiting with approvalNeeded.</summary>
    public bool? ApproveUncached { get; set; }

    public override string ToString() => $"[retry {JobId:N}]";
}

public sealed class RemoteCheckNowRequest
{
    /// <summary>Null checks everything, on one provider if <see cref="Provider"/> is set.</summary>
    public Guid? JobId { get; set; }
    public string? Provider { get; set; }
}

public sealed class RemoteDeleteRequest
{
    public Guid JobId { get; set; }
    /// <summary>Has to be true. The item on the provider's side is gone for good.</summary>
    public bool Confirmed { get; set; }
}

public sealed class RemoteActionResultDto
{
    public bool Ok { get; set; }
    public string? Error { get; set; }
    public RemoteJobDto? Job { get; set; }
}

public sealed class RemoteArchivePasswordRequest
{
    public Guid JobId { get; set; }
    /// <summary>For this job's archives, whether or not its downloads exist yet. Empty clears it.</summary>
    public string? Password { get; set; }

    public override string ToString() => $"[archive password {JobId:N}]";
}

public sealed class RemoteContainerPreviewRequest
{
    /// <summary>The full path of a local .rsdf, .ccf or .dlc file on a drive of this computer.</summary>
    public string Path { get; set; } = string.Empty;

    public override string ToString() => "[container preview]";
}

public sealed class RemoteContainerPackageDto
{
    /// <summary>What <see cref="RemoteContainerEntryDto.PackageIndex"/> refers to.</summary>
    public int Index { get; set; }
    public string? Name { get; set; }
    public string? Comment { get; set; }
    /// <summary>The container states an archive password for this package. Only this can be learned about it.</summary>
    public bool HasArchivePassword { get; set; }
    public int EntryCount { get; set; }
}

/// <summary>One link of a container, without the link: the Host keeps that, and a client chooses by <see cref="EntryId"/>.</summary>
public sealed class RemoteContainerEntryDto
{
    public int EntryId { get; set; }
    /// <summary>Null when the container puts it in no package.</summary>
    public int? PackageIndex { get; set; }
    public string? DisplayHost { get; set; }
    public string? DisplayName { get; set; }
    /// <summary>As the container states it, so only a hint.</summary>
    public long? Size { get; set; }
    /// <summary>folderLink, magnet, hosterLink, undetermined or unsupported. Treat one you don't know as unsupported.</summary>
    public string Kind { get; set; } = string.Empty;
    /// <summary>Why it can't be used, or something to know about one that can.</summary>
    public string? Reason { get; set; }
    public bool CanAdd { get; set; }
}

public sealed class RemoteContainerPreviewResult
{
    public bool Ok { get; set; }
    public string? Error { get; set; }
    /// <summary>rsdf, ccf, dlc or unknown.</summary>
    public string Format { get; set; } = "unknown";
    public string? FormatVersion { get; set; }
    /// <summary>This format can't be opened here. <see cref="Error"/> says why and what can be done instead.</summary>
    public bool NotSupported { get; set; }
    /// <summary><c>remote.container.sendToJd</c> can take this kind of file. It is a separate choice, and never happens by itself.</summary>
    public bool CanSendToJd { get; set; }
    /// <summary>What <c>remote.container.add</c> needs. Null when nothing in the file can be added.</summary>
    public Guid? PreviewId { get; set; }
    /// <summary>The links are forgotten at this time, and then the file has to be read again.</summary>
    public DateTimeOffset? ExpiresAt { get; set; }
    /// <summary>Why no account can take anything right now, such as no API key being set.</summary>
    public string? AccountProblem { get; set; }
    public List<RemoteContainerPackageDto> Packages { get; set; } = [];
    public List<RemoteContainerEntryDto> Entries { get; set; } = [];
    /// <summary>What was wrong with parts of the file, and anything else worth saying about it as a whole.</summary>
    public List<string> Problems { get; set; } = [];
}

public sealed class RemoteContainerAddRequest
{
    public Guid PreviewId { get; set; }
    public List<int> EntryIds { get; set; } = [];
    /// <summary>A full path. Null uses the default download folder.</summary>
    public string? Destination { get; set; }
    public int QueueId { get; set; }
    /// <summary>startNow, queue or later.</summary>
    public string StartMode { get; set; } = "startNow";
    /// <summary>Go ahead and have the provider fetch these if they aren't cached.</summary>
    public bool ApproveUncached { get; set; }
}

public sealed class RemoteContainerAddOutcomeDto
{
    public int EntryId { get; set; }
    public bool Ok { get; set; }
    public string? Error { get; set; }
    /// <summary>Something asked for that didn't happen, or something to know about what did.</summary>
    public string? Note { get; set; }
    /// <summary>An active job already covers this entry, and it is the one returned.</summary>
    public bool Existing { get; set; }
    public string? InputKind { get; set; }
    /// <summary>The job this entry became, whether or not <see cref="Job"/> is filled in.</summary>
    public Guid? JobId { get; set; }
    /// <summary>Null for an entry that wasn't added, and for every entry when <see cref="RemoteContainerAddResult.JobsLeftOut"/> is set.</summary>
    public RemoteJobDto? Job { get; set; }
}

public sealed class RemoteContainerAddResult
{
    /// <summary>At least one entry was added. <see cref="Outcomes"/> says how each one went.</summary>
    public bool Ok { get; set; }
    public string? Error { get; set; }
    public List<RemoteContainerAddOutcomeDto> Outcomes { get; set; } = [];
    /// <summary>So many entries were added that their full jobs wouldn't fit one answer. <see cref="Ok"/> and each outcome still hold, and <c>remote.list</c> has the jobs.</summary>
    public bool JobsLeftOut { get; set; }
}

public sealed class RemoteContainerSendRequest
{
    /// <summary>The full path of a local .dlc or .ccf file on a drive of this computer.</summary>
    public string Path { get; set; } = string.Empty;

    public override string ToString() => "[container send]";
}

public sealed class RemoteContainerSendResult
{
    /// <summary>JDownloader said it accepted the file.</summary>
    public bool Ok { get; set; }
    /// <summary>sent, notRunning, rejected, timedOut, invalidInput or failed.</summary>
    public string Status { get; set; } = string.Empty;
    /// <summary>JDownloader itself gave an answer, whichever way. False means nothing is known to have reached it.</summary>
    public bool JdAnswered { get; set; }
    /// <summary>What happened, in words for the user.</summary>
    public string Message { get; set; } = string.Empty;
}

/// <summary>Whole state, so a client that missed one can't drift. On connect, read <c>remote.list</c>.</summary>
public sealed class RemoteChangedDto
{
    /// <summary>job, removed or account.</summary>
    public string Change { get; set; } = string.Empty;
    public RemoteJobDto? Job { get; set; }
    public Guid? JobId { get; set; }
    public RemoteAccountDto? Account { get; set; }
}

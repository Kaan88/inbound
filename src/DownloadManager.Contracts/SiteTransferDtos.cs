namespace DownloadManager.Contracts;

/// <summary>
/// A download or upload batch (SITES.4; remote-sites.md, "Transfers"). <see cref="Names"/> are files and folders in the
/// source folder: the server folder for a download, the PC folder for an upload, <see cref="SiteId"/>'s folder for a copy.
/// A folder brings everything in it.
/// </summary>
public sealed class SiteTransferStartRequest
{
    public const string SafeSingleDownloadCapability = "sitesSafeSingleDownload";

    /// <summary>
    /// ANDROID.8 (r6): the Host takes <see cref="Direction"/> <c>copy</c>, from one site's folder to another site's folder
    /// (or another folder of the same site). rclone on the Host streams each file from one to the other; nothing is staged
    /// on the Host's disk. An older Host lacks it and refuses <c>copy</c> with "Choose download or upload."
    /// </summary>
    public const string RemoteCopyCapability = "sitesRemoteCopy";
    /// <summary>Requires one file (never a directory) and no replacement even at final publication.</summary>
    public bool SafeSingleDownload { get; set; }

    public string SiteId { get; set; } = "";

    /// <summary><c>download</c>, <c>upload</c> or <c>copy</c> (<see cref="RemoteCopyCapability"/>).</summary>
    public string Direction { get; set; } = "download";

    /// <summary>The folder on the server, absolute (<c>/srv/pub</c>); for a copy, the source folder on <see cref="SiteId"/>.</summary>
    public string RemoteFolder { get; set; } = "/";

    /// <summary>The folder on this PC, a full path. A download creates it when missing. Not used by a copy.</summary>
    public string LocalFolder { get; set; } = "";

    /// <summary>A copy's target site; it may be <see cref="SiteId"/> itself. Null for a download or an upload.</summary>
    public string? DestinationSiteId { get; set; }

    /// <summary>A copy's target folder on <see cref="DestinationSiteId"/>, absolute. Null for a download or an upload.</summary>
    public string? DestinationFolder { get; set; }

    public List<SiteTransferNameDto> Names { get; set; } = [];

    /// <summary>When a target is already there: <c>skip</c> (the default), <c>replace</c> or <c>keepBoth</c>.</summary>
    public string Conflict { get; set; } = "skip";
}

public sealed class SiteTransferNameDto
{
    public string Name { get; set; } = "";
    public bool IsFolder { get; set; }
}

public sealed class SiteTransferIdRequest
{
    /// <summary>The batch; for <c>sites.transfer.clear</c> an empty id clears every finished batch.</summary>
    public string Id { get; set; } = "";
}

/// <summary>One batch as clients see it. Byte counts include the files in flight.</summary>
public sealed class SiteTransferDto
{
    public string Id { get; set; } = "";
    public string SiteId { get; set; } = "";
    public string SiteName { get; set; } = "";
    public string Direction { get; set; } = "";
    public string RemoteFolder { get; set; } = "";
    public string LocalFolder { get; set; } = "";

    /// <summary>A copy's target site, its name and folder; null for a download or an upload.</summary>
    public string? DestinationSiteId { get; set; }
    public string? DestinationSiteName { get; set; }
    public string? DestinationFolder { get; set; }

    public string Conflict { get; set; } = "";

    /// <summary><c>running</c>, <c>paused</c>, <c>done</c>, <c>failed</c> or <c>canceled</c>.</summary>
    public string State { get; set; } = "";

    /// <summary>A <see cref="SiteProblems"/> value when a problem stopped the batch (a changed host key).</summary>
    public string? Problem { get; set; }

    /// <summary>Why the batch stopped or was paused, when not by the user.</summary>
    public string? Message { get; set; }

    public int Files { get; set; }

    /// <summary>How many of <see cref="Files"/> are empty folders made at the target.</summary>
    public int Folders { get; set; }
    public int FilesDone { get; set; }
    public int FilesSkipped { get; set; }
    public int FilesFailed { get; set; }

    /// <summary>Files moving now.</summary>
    public int FilesRunning { get; set; }

    /// <summary>Total bytes as listed; files of unknown size count as 0.</summary>
    public long Bytes { get; set; }

    public long BytesDone { get; set; }

    /// <summary>Bytes a second over the last few seconds; 0 when nothing moves.</summary>
    public long Speed { get; set; }

    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset? FinishedAt { get; set; }
}

public sealed class SiteTransferItemDto
{
    /// <summary>Relative to the source folder, <c>/</c>-separated.</summary>
    public string Path { get; set; } = "";

    /// <summary>The relative target when it differs (Keep both).</summary>
    public string? Target { get; set; }

    public long Size { get; set; }

    /// <summary><c>waiting</c>, <c>running</c>, <c>done</c>, <c>skipped</c> or <c>failed</c>.</summary>
    public string State { get; set; } = "";

    public int Tries { get; set; }
    public string? Error { get; set; }

    /// <summary>An empty folder that is made at the target rather than a file that is copied.</summary>
    public bool IsFolder { get; set; }
}

public sealed class SiteTransferResultDto
{
    public bool Ok { get; set; }
    public string? Message { get; set; }

    /// <summary>A <see cref="SiteProblems"/> value when the failure needs its own screen.</summary>
    public string? Problem { get; set; }

    public SiteTransferDto? Transfer { get; set; }

    /// <summary>The batch's files, for <c>sites.transfer.get</c> only.</summary>
    public List<SiteTransferItemDto>? Items { get; set; }
}

public sealed class SiteTransfersDto
{
    public List<SiteTransferDto> Transfers { get; set; } = [];
}

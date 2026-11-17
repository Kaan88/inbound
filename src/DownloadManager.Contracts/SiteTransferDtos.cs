namespace DownloadManager.Contracts;

/// <summary>
/// A download or upload batch (SITES.4; remote-sites.md, "Transfers"). <see cref="Names"/> are files and folders in the
/// source folder: the server folder for a download, the PC folder for an upload. A folder brings everything in it.
/// </summary>
public sealed class SiteTransferStartRequest
{
    public string SiteId { get; set; } = "";

    /// <summary><c>download</c>, <c>upload</c> or (SITES.13) <c>between</c>: from this remote to <see cref="TargetSiteId"/>.</summary>
    public string Direction { get; set; } = "download";

    /// <summary>The folder on the server, absolute (<c>/srv/pub</c>); for <c>between</c>, the source folder.</summary>
    public string RemoteFolder { get; set; } = "/";

    /// <summary>The folder on this PC, a full path. A download creates it when missing. Not used for <c>between</c>.</summary>
    public string LocalFolder { get; set; } = "";

    /// <summary>SITES.13, <c>between</c> only: the remote the files go to (may be the same remote, another folder).</summary>
    public string? TargetSiteId { get; set; }

    /// <summary>SITES.13, <c>between</c> only: the folder on the target remote, absolute.</summary>
    public string? TargetFolder { get; set; }

    /// <summary>
    /// SITES.13: made paused, waiting for a schedule with "Remote files transfers" on to start it (its start time).
    /// </summary>
    public bool WaitForSchedule { get; set; }

    public List<SiteTransferNameDto> Names { get; set; } = [];

    /// <summary>When a target is already there: <c>skip</c> (the default), <c>replace</c> or <c>keepBoth</c>.</summary>
    public string Conflict { get; set; } = "skip";
}

public sealed class SiteTransferNameDto
{
    public string Name { get; set; } = "";
    public bool IsFolder { get; set; }
}

/// <summary>SITES.11: one file of a batch, for <c>sites.transfer.stopFile</c>.</summary>
public sealed class SiteTransferFileRequest
{
    public string Id { get; set; } = "";

    /// <summary>As <see cref="SiteTransferItemDto.Path"/>.</summary>
    public string Path { get; set; } = "";
}

/// <summary>SITES.13, <c>sites.transfer.schedule</c>.</summary>
public sealed class SiteTransferScheduleRequest
{
    public string Id { get; set; } = "";

    /// <summary>True: the batch is paused (if running) and waits for a schedule's start. False: it stays paused, waiting for the user.</summary>
    public bool Wait { get; set; } = true;
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
    public string Conflict { get; set; } = "";

    /// <summary>SITES.13, <c>between</c>: the remote the files go to, its name, and the folder there. Null otherwise.</summary>
    public string? TargetSiteId { get; set; }

    public string? TargetSiteName { get; set; }
    public string? TargetFolder { get; set; }

    /// <summary>SITES.13: paused until a schedule with "Remote files transfers" on starts it.</summary>
    public bool WaitsForSchedule { get; set; }

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

    /// <summary>
    /// SITES.11: files the user stopped one by one (<c>sites.transfer.stopFile</c>); they are not moved and are not
    /// failures. Retry leaves them; <c>sites.transfer.startFile</c> starts one again. A batch whose files were all stopped
    /// ends <c>done</c> with none copied. A Host from before SITES.11 never sends it.
    /// </summary>
    public int FilesStopped { get; set; }

    /// <summary>
    /// SITES.11: the names the user picked (files and folders of the source folder), the first few in the order picked,
    /// for a row's title ("isos and 2 more"); <see cref="PickedCount"/> is how many there were. Empty from an older Host.
    /// </summary>
    public List<string> Picked { get; set; } = [];

    public int PickedCount { get; set; }

    /// <summary>
    /// SITES.11: the first failed file (as <see cref="SiteTransferItemDto.Path"/>) and why, so a row can say it without
    /// opening; null when none failed.
    /// </summary>
    public string? FirstFailedPath { get; set; }

    public string? FirstFailedError { get; set; }

    /// <summary>
    /// SITES.11: when a running batch that lost its connection tries again (it waits, doubling up to a minute, before its
    /// next file); null when it is not waiting so. <see cref="Message"/> is not set while it waits; the last file's error is.
    /// </summary>
    public DateTimeOffset? RetryAt { get; set; }

    /// <summary>SITES.11: why it waits for <see cref="RetryAt"/> (the connection error), null otherwise.</summary>
    public string? RetryReason { get; set; }

    /// <summary>Total bytes as listed; files of unknown size count as 0.</summary>
    public long Bytes { get; set; }

    public long BytesDone { get; set; }

    /// <summary>
    /// SITES.11: the bytes of failed and stopped files, which are not going to move: the bar's whole is
    /// <see cref="Bytes"/> less this, and <see cref="SecondsLeft"/> leaves them out. 0 from an older Host.
    /// </summary>
    public long BytesSetAside { get; set; }

    /// <summary>Bytes a second over the last few seconds; 0 when nothing moves.</summary>
    public long Speed { get; set; }

    /// <summary>
    /// SITES.11: whole seconds until the batch is done at <see cref="Speed"/> (<see cref="SiteTransferTime.SecondsLeft"/>);
    /// null unless it is running and moving, or when a file still to move has no known size.
    /// </summary>
    public long? SecondsLeft { get; set; }

    /// <summary>SITES.11: the files moving now (at most "transfers at once"); empty when nothing moves.</summary>
    public List<SiteTransferMovingDto> Moving { get; set; } = [];

    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset? FinishedAt { get; set; }
}

/// <summary>SITES.11: one file of a batch while it moves, as rclone last reported it (about once a second).</summary>
public sealed class SiteTransferMovingDto
{
    /// <summary>Relative to the source folder, <c>/</c>-separated, as <see cref="SiteTransferItemDto.Path"/>.</summary>
    public string Path { get; set; } = "";

    /// <summary>The file's size; -1 when unknown.</summary>
    public long Size { get; set; }

    public long BytesDone { get; set; }
    public long Speed { get; set; }
    public long? SecondsLeft { get; set; }
}

/// <summary>SITES.11: time left, one rule for every client.</summary>
public static class SiteTransferTime
{
    /// <summary>
    /// Whole seconds, rounded up, to move what is left at <paramref name="speed"/> bytes a second: null when nothing
    /// moves or the size is unknown (negative), 0 when nothing is left.
    /// </summary>
    public static long? SecondsLeft(long size, long bytesDone, long speed)
    {
        if (size < 0 || speed <= 0) return null;
        var left = size - Math.Max(0, bytesDone);
        if (left <= 0) return 0;
        return left / speed + (left % speed == 0 ? 0 : 1);
    }
}

public sealed class SiteTransferItemDto
{
    /// <summary>Relative to the source folder, <c>/</c>-separated.</summary>
    public string Path { get; set; } = "";

    /// <summary>The relative target when it differs (Keep both).</summary>
    public string? Target { get; set; }

    public long Size { get; set; }

    /// <summary><c>waiting</c>, <c>running</c>, <c>done</c>, <c>skipped</c>, <c>failed</c> or (SITES.11) <c>stopped</c>.</summary>
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

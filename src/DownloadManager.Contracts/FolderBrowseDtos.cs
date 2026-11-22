namespace DownloadManager.Contracts;

/// <summary>
/// <c>folders.browse</c>: the folders inside one folder on the Host's own disk, for a client that can't show a dialog
/// there (the server's web UI). Only folders under the server's allowed roots can be listed. An empty path lists the
/// roots themselves.
/// </summary>
public sealed class FolderBrowseRequest
{
    public string? Path { get; set; }

    /// <summary>
    /// <see cref="FolderBrowseFiles.Archives"/> also lists the archive files in the folder (an extraction's chooser);
    /// <see cref="FolderBrowseFiles.All"/> lists every file (SITES.14: the web's "Files on the server" pane). Null lists
    /// folders only, as before. Another value is refused (a Host from before SITES.14 refuses <c>all</c> too).
    /// </summary>
    public string? Files { get; set; }
}

/// <summary>What <see cref="FolderBrowseRequest.Files"/> may ask for.</summary>
public static class FolderBrowseFiles
{
    /// <summary>Files whose names extraction can start from (a RAR volume, 7z, zip, tar, compressed tar, .001).</summary>
    public const string Archives = "archives";

    /// <summary>SITES.14: every file in the folder that a file dialog would show (hidden ones left out).</summary>
    public const string All = "all";
}

public sealed class FolderFileDto
{
    public string Name { get; set; } = string.Empty;

    /// <summary>The file's full path, as <c>archive.extract</c> takes it.</summary>
    public string Path { get; set; } = string.Empty;

    public long Size { get; set; }

    /// <summary>SITES.14: when the file was last written; null when it can't be read (and from a Host before SITES.14).</summary>
    public DateTimeOffset? Modified { get; set; }
}

public sealed class FolderEntryDto
{
    public string Name { get; set; } = string.Empty;

    /// <summary>The folder's full path, as it can be sent back to <c>folders.browse</c> or used as a destination.</summary>
    public string Path { get; set; } = string.Empty;
}

/// <summary>Answers both <c>folders.browse</c> and <c>folders.create</c>.</summary>
public sealed class FolderBrowseResult
{
    public bool Ok { get; set; }
    public string? Error { get; set; }

    /// <summary>The folder listed, with links resolved. Null when the roots are listed.</summary>
    public string? Path { get; set; }

    /// <summary>The folder one up, or null at a root (going up from a root lists the roots).</summary>
    public string? Parent { get; set; }

    /// <summary>The allowed roots, always, so a client can offer them.</summary>
    public List<string> Roots { get; set; } = new();

    /// <summary>The folders inside <see cref="Path"/> (or the roots), sorted by name.</summary>
    public List<FolderEntryDto> Folders { get; set; } = new();

    /// <summary>
    /// The files asked for by <see cref="FolderBrowseRequest.Files"/>, sorted by name; empty otherwise, and for the roots.
    /// </summary>
    public List<FolderFileDto> Files { get; set; } = new();

    /// <summary>More folders (or files) were there than a listing carries; the first ones by name are given.</summary>
    public bool Truncated { get; set; }

    /// <summary>Free and total bytes of the mount that holds <see cref="Path"/>; null when unknown or for the roots.</summary>
    public long? FreeBytes { get; set; }
    public long? TotalBytes { get; set; }
}

/// <summary><c>folders.create</c>: a new folder <see cref="Name"/> inside <see cref="Parent"/>, under the allowed roots only.</summary>
public sealed class FolderCreateRequest
{
    public string Parent { get; set; } = string.Empty;
    public string Name { get; set; } = string.Empty;
}

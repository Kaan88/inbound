namespace DownloadManager.Contracts;

/// <summary>
/// <c>folders.browse</c>: the folders inside one folder on the Host's own disk, for a client that can't show a dialog
/// there (the server's web UI). Only folders under the server's allowed roots can be listed. An empty path lists the
/// roots themselves.
/// </summary>
public sealed class FolderBrowseRequest
{
    public string? Path { get; set; }
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

    /// <summary>The folders inside <see cref="Path"/> (or the roots), sorted by name. Files are never listed.</summary>
    public List<FolderEntryDto> Folders { get; set; } = new();

    /// <summary>More folders were there than a listing carries; the first ones by name are given.</summary>
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

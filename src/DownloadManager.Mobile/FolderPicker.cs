using DownloadManager.Contracts;

namespace DownloadManager.Mobile;

/// <summary>
/// The "Save to" chooser for a server: walks the server's own folders through <c>folders.browse</c> (only the folders it allows),
/// one folder at a time. The phone's own Host has no chooser: its folder is the phone's.
/// </summary>
public sealed class FolderPicker(DownloadsModel host)
{
    /// <summary>The folder shown, or null while the server's roots are listed (nothing can be chosen there).</summary>
    public string? Path { get; private set; }

    /// <summary>The folder one up; null at a root (going up from a root lists the roots).</summary>
    public string? Parent { get; private set; }

    public IReadOnlyList<FolderEntryDto> Folders { get; private set; } = Array.Empty<FolderEntryDto>();

    public IReadOnlyList<string> Roots { get; private set; } = Array.Empty<string>();

    /// <summary>More folders were there than a listing carries.</summary>
    public bool Truncated { get; private set; }

    /// <summary>Why the last request failed, in words; null when it worked.</summary>
    public string? Error { get; private set; }

    /// <summary>Free space of the disk that holds the folder, when the server says.</summary>
    public long? FreeBytes { get; private set; }

    /// <summary>A folder is shown (not the list of roots), so "Save here" has a folder to take.</summary>
    public bool CanChoose => Path is not null;

    /// <summary>Whether the connected Host offers folder browsing (its hello lists <c>folders</c>).</summary>
    public static bool Offered(DownloadsModel host) => host.Hello?.Capabilities.Contains("folders") == true;

    /// <summary>
    /// Lists <paramref name="path"/>. When the server refuses it (a folder outside its roots, or gone), the roots are listed instead, so the
    /// person is never left on a dead end; <see cref="Error"/> then keeps the reason only if even that failed.
    /// </summary>
    public async Task OpenAsync(string? path, CancellationToken cancellationToken = default)
    {
        var r = await host.BrowseFoldersAsync(path, cancellationToken).ConfigureAwait(false);
        cancellationToken.ThrowIfCancellationRequested();
        if (!r.Ok && !string.IsNullOrEmpty(path)) r = await host.BrowseFoldersAsync(null, cancellationToken).ConfigureAwait(false);
        cancellationToken.ThrowIfCancellationRequested();
        Apply(r);
    }

    /// <summary>One folder up, or the roots from a root.</summary>
    public Task UpAsync(CancellationToken cancellationToken = default) => OpenAsync(Parent, cancellationToken);

    private void Apply(FolderBrowseResult r)
    {
        if (!r.Ok)
        {
            Error = string.IsNullOrWhiteSpace(r.Error) ? "The folders could not be read." : r.Error;
            return;
        }

        Error = null;
        Path = r.Path;
        Parent = r.Parent;
        Folders = r.Folders;
        Roots = r.Roots;
        Truncated = r.Truncated;
        FreeBytes = r.FreeBytes;
    }
}

/// <summary>Words for the "Save to" field.</summary>
public static class SavePathWords
{
    /// <summary>What TalkBack reads: the whole path (the field shows it cut in the middle) and where it is.</summary>
    public static string Label(string? hostName, string path) =>
        string.IsNullOrEmpty(hostName) ? "Save to " + path : "Save to " + path + " on " + hostName;

    /// <summary>The path as the field first shows it on the phone: the shared storage's own prefix is left out.</summary>
    public static string ForField(string path)
    {
        const string root = "/storage/emulated/0/";
        return path.StartsWith(root, StringComparison.Ordinal) ? path[root.Length..] : path;
    }
}

/// <summary>Cutting a long path in the middle, so its start (the drive or root) and its end (the folder) both stay in view.</summary>
public static class MiddleEllipsis
{
    /// <summary>
    /// <paramref name="text"/> when <paramref name="fits"/> says it fits; otherwise the most of it that fits with "…" in the middle
    /// (the screen measures; this only searches).
    /// </summary>
    public static string Cut(string text, Func<string, bool> fits)
    {
        if (text.Length == 0 || fits(text)) return text;
        int lo = 0, hi = text.Length - 1; // characters kept, besides the mark
        while (lo < hi)
        {
            var mid = (lo + hi + 1) / 2;
            if (fits(Join(text, mid))) lo = mid; else hi = mid - 1;
        }

        return Join(text, lo);
    }

    private static string Join(string text, int keep)
    {
        var head = (keep + 1) / 2;
        var tail = keep / 2;
        return text[..head] + "…" + text[^tail..];
    }
}

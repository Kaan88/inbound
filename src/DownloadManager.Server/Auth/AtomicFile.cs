namespace DownloadManager.Server.Auth;

/// <summary>
/// A whole-file write that a kill at any point leaves as the old file or the new one: a temporary file next to it,
/// flushed to disk, then moved over it. Owner-only (mode 600) off Windows, from the moment the temporary file exists.
/// </summary>
internal static class AtomicFile
{
    public static void Write(string path, ReadOnlySpan<byte> bytes)
    {
        var temp = path + ".tmp";
        var options = new FileStreamOptions { Mode = FileMode.Create, Access = FileAccess.Write, Share = FileShare.None };
        if (!OperatingSystem.IsWindows()) options.UnixCreateMode = UnixFileMode.UserRead | UnixFileMode.UserWrite;
        using (var stream = new FileStream(temp, options))
        {
            stream.Write(bytes);
            stream.Flush(flushToDisk: true);
        }

        // UnixCreateMode applies only when the file is new; a temporary file left by a kill keeps its old mode.
        if (!OperatingSystem.IsWindows()) File.SetUnixFileMode(temp, UnixFileMode.UserRead | UnixFileMode.UserWrite);
        File.Move(temp, path, overwrite: true);
    }
}

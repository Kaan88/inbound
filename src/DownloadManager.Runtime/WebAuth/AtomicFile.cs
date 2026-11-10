namespace DownloadManager.Runtime.WebAuth;

/// <summary>
/// A whole-file write that a kill at any point leaves as the old file or the new one: a temporary file next to it,
/// flushed to disk, then moved over it. Owner-only (mode 600) off Windows, from the moment the temporary file exists.
/// </summary>
public static class AtomicFile
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
        Replace(temp, path);
        if (!OperatingSystem.IsWindows()) SyncDirectory(System.IO.Path.GetDirectoryName(System.IO.Path.GetFullPath(path))!);
    }

    /// <summary>
    /// On Windows a scanner or indexer can hold the old file for a moment (access denied), and that shouldn't cost a save,
    /// as the job store's own save allows for.
    /// </summary>
    private static void Replace(string temp, string path)
    {
        const int MostAttempts = 5;
        for (var attempt = 1; ; attempt++)
        {
            try
            {
                File.Move(temp, path, overwrite: true);
                return;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException && attempt < MostAttempts)
            {
                Thread.Sleep(20 << attempt);
            }
        }
    }

    /// <summary>
    /// Off Windows a rename is durable only once its directory is flushed too; without this a power loss soon after can
    /// bring the old file back (a session that signed out, say). .NET can't open a directory, so libc does it. Best
    /// effort: a system without these calls, or a folder that refuses, keeps the rename as it is.
    /// Not run on this project's Windows test machine; the Linux image is where it applies.
    /// </summary>
    private static void SyncDirectory(string directory)
    {
        try
        {
            var fd = open(directory, 0 /* O_RDONLY */);
            if (fd < 0) return;
            try
            {
                _ = fsync(fd);
            }
            finally
            {
                _ = close(fd);
            }
        }
        catch (Exception ex) when (ex is DllNotFoundException or EntryPointNotFoundException)
        {
            // Not a libc system.
        }
    }

    [System.Runtime.InteropServices.DllImport("libc", SetLastError = true)]
    private static extern int open([System.Runtime.InteropServices.MarshalAs(System.Runtime.InteropServices.UnmanagedType.LPUTF8Str)] string path, int flags);

    [System.Runtime.InteropServices.DllImport("libc", SetLastError = true)]
    private static extern int fsync(int fd);

    [System.Runtime.InteropServices.DllImport("libc", SetLastError = true)]
    private static extern int close(int fd);
}

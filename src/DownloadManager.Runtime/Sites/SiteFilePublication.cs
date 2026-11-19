using System.Runtime.InteropServices;

namespace DownloadManager.Runtime.Sites;

/// <summary>Publishes a staged file without replacing an existing directory entry.</summary>
internal static class SiteFilePublication
{
    // null means Android needs its exclusive-create fallback; false means the destination exists.
    public static bool? TryRename(string source, string destination)
    {
        if (OperatingSystem.IsWindows())
        {
            try { File.Move(source, destination, overwrite: false); return true; }
            catch (IOException e) when ((e.HResult & 0xffff) is 80 or 183) { return false; }
        }

        // File.Move(false) on Unix can check with lstat and then call ordinary, replacing rename.
        // Require the filesystem to implement exclusive rename, or fail without writing the final path.
        int result;
        try
        {
            result = OperatingSystem.IsAndroid() || OperatingSystem.IsLinux()
                ? LinuxRename(-100, source, -100, destination, 1)
                : OperatingSystem.IsMacOS()
                    ? MacRename(source, destination, 4)
                    : throw new IOException("This Host cannot publish files without replacing an existing file.");
        }
        catch (Exception e) when (e is EntryPointNotFoundException or DllNotFoundException)
        {
            if (OperatingSystem.IsAndroid()) return null;
            throw new IOException("This Host cannot publish files without replacing an existing file.", e);
        }
        if (result == 0) return true;
        var error = Marshal.GetLastPInvokeError();
        if (error == 17) return false; // EEXIST
        // Android shared-storage FUSE can reject RENAME_NOREPLACE. CreateNew still gives an
        // exclusive destination; readers may see the file during this local copy.
        if (OperatingSystem.IsAndroid() && error is 22 or 38 or 95)
            return null;
        throw new IOException($"The destination did not accept an exclusive file move (error {error}).");
    }

    public static FileStream? OpenExclusive(string destination)
    {
        try { return new FileStream(destination, FileMode.CreateNew, FileAccess.Write, FileShare.None, 65536, useAsync: true); }
        catch (IOException) when (File.Exists(destination) || Directory.Exists(destination)) { return null; }
    }

    [DllImport("libc", EntryPoint = "renameat2", SetLastError = true)]
    private static extern int LinuxRename(int fromDir, [MarshalAs(UnmanagedType.LPUTF8Str)] string from,
        int toDir, [MarshalAs(UnmanagedType.LPUTF8Str)] string to, uint flags);

    [DllImport("libSystem.dylib", EntryPoint = "renamex_np", SetLastError = true)]
    private static extern int MacRename([MarshalAs(UnmanagedType.LPUTF8Str)] string from,
        [MarshalAs(UnmanagedType.LPUTF8Str)] string to, uint flags);
}

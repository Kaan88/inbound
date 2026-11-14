namespace DownloadManager.Host;

/// <summary>
/// EXT.B8: what "open this finished download" does on Linux. An AppImage has no execute bit when it is downloaded and
/// xdg-open does not run one, so it is made executable for its owner and started directly (owner, 2026-11-13: "Open
/// programs too"). Everything else goes through xdg-open. Pure and unstarted here so it can be tested; the Host does the
/// starting.
/// </summary>
public static class LinuxOpenRule
{
    private static readonly byte[] ElfMagic = [0x7F, 0x45, 0x4C, 0x46];

    /// <summary>
    /// True when the regular file starts with the ELF magic and has the AppImage magic ('A', 'I', then type 1 or 2) at
    /// offset 8. The name does not count: a text file called x.AppImage is not one.
    /// </summary>
    public static bool IsAppImage(string path)
    {
        try
        {
            if (!File.Exists(path)) return false;
            using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
            return IsAppImage(stream);
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        {
            return false;
        }
    }

    public static bool IsAppImage(Stream stream)
    {
        var head = new byte[11];
        var read = 0;
        while (read < head.Length)
        {
            var n = stream.Read(head, read, head.Length - read);
            if (n <= 0) return false;
            read += n;
        }

        return head.AsSpan(0, 4).SequenceEqual(ElfMagic) && head[8] == 0x41 && head[9] == 0x49 && (head[10] == 0x01 || head[10] == 0x02);
    }

    /// <summary>The mode with the owner's execute bit added; group and other bits are kept as they were.</summary>
    public static UnixFileMode WithOwnerExecute(UnixFileMode mode) => mode | UnixFileMode.UserExecute;

    /// <summary>Adds the owner's execute bit to the file, leaving every other bit alone.</summary>
    public static void MakeOwnerExecutable(string path)
    {
        var mode = File.GetUnixFileMode(path);
        var wanted = WithOwnerExecute(mode);
        if (wanted != mode) File.SetUnixFileMode(path, wanted);
    }
}

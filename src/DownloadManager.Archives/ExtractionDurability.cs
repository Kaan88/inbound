using System.ComponentModel;
using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;

namespace DownloadManager.Archives;

/// <summary>
/// Pushes output to the disk before anything is deleted on the strength of it. UnRAR doesn't flush and a
/// rename isn't write-through, and a file whose tail never reached the disk reads as zeros at full size,
/// so without this a power cut just after a delete could leave neither the output nor the archive.
/// </summary>
internal static class ExtractionDurability
{
    private const uint GenericWrite = 0x40000000;
    private const uint ShareAll = 0x00000007;
    private const uint OpenExisting = 3;
    private const uint BackupSemantics = 0x02000000;

    /// <summary>UnRAR restores the read-only attribute, and a write handle needs it off for a moment.</summary>
    public static void FlushFile(string path)
    {
        var attributes = File.GetAttributes(path);
        var readOnly = (attributes & FileAttributes.ReadOnly) != 0;
        if (readOnly) File.SetAttributes(path, attributes & ~FileAttributes.ReadOnly);
        try
        {
            using var handle = File.OpenHandle(path, FileMode.Open, FileAccess.Write, FileShare.ReadWrite | FileShare.Delete);
            RandomAccess.FlushToDisk(handle);
        }
        finally
        {
            if (readOnly) File.SetAttributes(path, attributes);
        }
    }

    /// <summary>Makes the renames into this folder durable, on file systems that allow flushing a folder.</summary>
    public static void FlushDirectory(string path)
    {
        if (!OperatingSystem.IsWindows()) return;
        using var handle = CreateFileW(path, GenericWrite, ShareAll, IntPtr.Zero, OpenExisting, BackupSemantics, IntPtr.Zero);
        if (handle.IsInvalid) throw new Win32Exception(Marshal.GetLastWin32Error());
        if (!FlushFileBuffers(handle)) throw new Win32Exception(Marshal.GetLastWin32Error());
    }

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern SafeFileHandle CreateFileW(string fileName, uint access, uint share, IntPtr security, uint disposition, uint flags, IntPtr template);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool FlushFileBuffers(SafeFileHandle file);
}

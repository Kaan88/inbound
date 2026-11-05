using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;

namespace DownloadManager.Core;

/// <summary>
/// Marks a part file sparse before it is sized. Segments write at their final offsets; on NTFS a
/// write past the data written so far otherwise makes Windows zero-fill everything before it.
/// For a large file that means tens of gigabytes of zeros, a stalled start and a write that
/// cancellation cannot interrupt. In a sparse file unwritten ranges cost nothing, and a completed
/// download has every range written, so it is fully allocated.
/// </summary>
internal static class SparseFile
{
    private const uint FsctlSetSparse = 0x000900C4;

    /// <summary>Best effort: a volume without sparse support (FAT, exFAT, network shares) keeps the old behavior.</summary>
    public static bool TryMarkSparse(SafeFileHandle handle)
    {
        if (!OperatingSystem.IsWindows() || handle.IsInvalid) return false;
        return DeviceIoControl(handle, FsctlSetSparse, IntPtr.Zero, 0, IntPtr.Zero, 0, out _, IntPtr.Zero);
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool DeviceIoControl(SafeFileHandle device, uint controlCode, IntPtr inBuffer, uint inBufferSize,
        IntPtr outBuffer, uint outBufferSize, out uint bytesReturned, IntPtr overlapped);
}

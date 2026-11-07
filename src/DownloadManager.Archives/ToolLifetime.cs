using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;

namespace DownloadManager.Archives;

/// <summary>
/// Puts a child process in a Windows job that kills it when the last handle closes, which happens
/// when this process exits for any reason, a crash included. Best effort: without a job the tool
/// still runs and is still killed on cancel, and only the crash case loses this protection.
/// </summary>
internal sealed class ToolLifetime : IDisposable
{
    private const int ExtendedLimitInformation = 9;
    private const uint KillOnJobClose = 0x2000;

    private readonly SafeFileHandle? _job;

    private ToolLifetime(SafeFileHandle? job)
    {
        _job = job;
    }

    public static ToolLifetime Tie(Process process)
    {
        if (!OperatingSystem.IsWindows()) return new ToolLifetime(null);

        var job = CreateJobObjectW(IntPtr.Zero, null);
        if (job.IsInvalid)
        {
            job.Dispose();
            return new ToolLifetime(null);
        }

        var limits = new ExtendedLimits { BasicLimitInformation = new BasicLimits { LimitFlags = KillOnJobClose } };
        var size = Marshal.SizeOf<ExtendedLimits>();
        var attached = false;
        try
        {
            attached = SetInformationJobObject(job, ExtendedLimitInformation, ref limits, (uint)size)
                && AssignProcessToJobObject(job, process.SafeHandle);
        }
        catch (Exception exception) when (exception is InvalidOperationException or Win32Exception)
        {
            // The process has already gone, which its exit code will explain.
        }

        if (attached) return new ToolLifetime(job);
        job.Dispose();
        return new ToolLifetime(null);
    }

    public void Dispose() => _job?.Dispose();

    [StructLayout(LayoutKind.Sequential)]
    private struct BasicLimits
    {
        public long PerProcessUserTimeLimit;
        public long PerJobUserTimeLimit;
        public uint LimitFlags;
        public UIntPtr MinimumWorkingSetSize;
        public UIntPtr MaximumWorkingSetSize;
        public uint ActiveProcessLimit;
        public UIntPtr Affinity;
        public uint PriorityClass;
        public uint SchedulingClass;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct IoCounters
    {
        public ulong ReadOperationCount;
        public ulong WriteOperationCount;
        public ulong OtherOperationCount;
        public ulong ReadTransferCount;
        public ulong WriteTransferCount;
        public ulong OtherTransferCount;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct ExtendedLimits
    {
        public BasicLimits BasicLimitInformation;
        public IoCounters IoInfo;
        public UIntPtr ProcessMemoryLimit;
        public UIntPtr JobMemoryLimit;
        public UIntPtr PeakProcessMemoryUsed;
        public UIntPtr PeakJobMemoryUsed;
    }

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern SafeFileHandle CreateJobObjectW(IntPtr attributes, string? name);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetInformationJobObject(SafeFileHandle job, int infoClass, ref ExtendedLimits info, uint size);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool AssignProcessToJobObject(SafeFileHandle job, SafeProcessHandle process);
}

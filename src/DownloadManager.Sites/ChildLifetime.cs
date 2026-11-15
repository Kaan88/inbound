using System.Collections.Concurrent;
using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;

namespace DownloadManager.Sites;

/// <summary>
/// Ties rclone to the Host (remote-sites.md, "Never outlives the Host"). rclone itself keeps running, and keeps
/// transferring, when its parent dies (SITES.0), so:
/// <list type="bullet">
/// <item>Windows: a job object that kills it when the job's last handle closes, which happens when the Host exits for any
/// reason, a crash included. The same as <c>ToolLifetime</c> in DownloadManager.Archives (not shared: that one is internal
/// and its project is not a dependency here). Not yet run on Windows for rclone.</item>
/// <item>Linux: rclone is started through <c>setpriv --pdeathsig KILL</c>, so the kernel kills it when the Host thread that
/// started it ends. That thread is <see cref="Spawner"/>'s, which lives as long as the Host. Without <c>setpriv</c> the
/// Host kills it on a normal exit only, and the pid file sweep (<see cref="RcloneProcess"/>) ends one left by a crash.</item>
/// </list>
/// Every tie also kills rclone on a normal Host exit (<see cref="AppDomain.ProcessExit"/>).
/// </summary>
internal sealed class ChildLifetime : IDisposable
{
    private const int ExtendedLimitInformation = 9;
    private const uint KillOnJobClose = 0x2000;

    private static readonly ConcurrentDictionary<Process, byte> Running = new();
    private static int _exitHooked;

    private readonly SafeFileHandle? _job;
    private readonly Process _process;

    private ChildLifetime(Process process, SafeFileHandle? job)
    {
        _process = process;
        _job = job;
    }

    /// <summary>True when the Windows job took the process (always false elsewhere).</summary>
    public bool InJob => _job is not null;

    public static ChildLifetime Tie(Process process)
    {
        if (Interlocked.Exchange(ref _exitHooked, 1) == 0) AppDomain.CurrentDomain.ProcessExit += (_, _) => KillRunning();
        Running[process] = 0;
        return new ChildLifetime(process, OperatingSystem.IsWindows() ? CreateJob(process) : null);
    }

    public void Dispose()
    {
        _job?.Dispose();
        Running.TryRemove(_process, out _);
    }

    internal static void KillRunning()
    {
        foreach (var process in Running.Keys)
        {
            try
            {
                if (!process.HasExited) process.Kill(entireProcessTree: true);
            }
            catch (Exception e) when (e is InvalidOperationException or Win32Exception or NotSupportedException)
            {
            }
        }
    }

    private static SafeFileHandle? CreateJob(Process process)
    {
        var job = CreateJobObjectW(IntPtr.Zero, null);
        if (job.IsInvalid)
        {
            job.Dispose();
            return null;
        }

        var limits = new ExtendedLimits { BasicLimitInformation = new BasicLimits { LimitFlags = KillOnJobClose } };
        var attached = false;
        try
        {
            attached = SetInformationJobObject(job, ExtendedLimitInformation, ref limits, (uint)Marshal.SizeOf<ExtendedLimits>())
                && AssignProcessToJobObject(job, process.SafeHandle);
        }
        catch (Exception e) when (e is InvalidOperationException or Win32Exception)
        {
        }

        if (attached) return job;
        job.Dispose();
        return null;
    }

    /// <summary>
    /// The Linux start: one thread for the Host's whole life that runs every <see cref="Process.Start()"/>, because the parent
    /// death signal follows the thread that forked, and a thread-pool thread can end while rclone still runs.
    /// </summary>
    internal static class Spawner
    {
        private static readonly BlockingCollection<(Func<Process> Start, TaskCompletionSource<Process> Done)> Queue = new();
        private static readonly Lazy<Thread> Worker = new(() =>
        {
            var thread = new Thread(() =>
            {
                foreach (var (start, done) in Queue.GetConsumingEnumerable())
                {
                    try { done.TrySetResult(start()); }
                    catch (Exception e) { done.TrySetException(e); }
                }
            })
            {
                IsBackground = true,
                Name = "Inbound rclone spawner",
            };
            thread.Start();
            return thread;
        });

        public static Task<Process> StartAsync(Func<Process> start)
        {
            _ = Worker.Value;
            var done = new TaskCompletionSource<Process>(TaskCreationOptions.RunContinuationsAsynchronously);
            Queue.Add((start, done));
            return done.Task;
        }
    }

    /// <summary>The <c>setpriv</c> that can set the parent death signal, or null (not Linux, or util-linux's setpriv missing).</summary>
    internal static string? FindSetpriv(Func<string, bool>? exists = null)
    {
        if (!OperatingSystem.IsLinux()) return null;
        exists ??= File.Exists;
        foreach (var candidate in new[] { "/usr/bin/setpriv", "/bin/setpriv", "/usr/sbin/setpriv", "/sbin/setpriv" })
            if (exists(candidate)) return candidate;
        return null;
    }

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

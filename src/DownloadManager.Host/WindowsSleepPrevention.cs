using System.ComponentModel;
using System.Runtime.InteropServices;
using DownloadManager.Core;

namespace DownloadManager.Host;

/// <summary>
/// Holds a Windows system-required power request while downloads are active.
/// PowerRequest is process-wide (unlike SetThreadExecutionState), so it can be
/// acquired and released from different thread-pool threads safely.
/// </summary>
internal sealed class WindowsSleepPrevention : ISleepPrevention
{
    private const uint PowerRequestContextSimpleString = 0x1;
    private const int PowerRequestSystemRequired = 1;

    private readonly Action<string> _log;
    private readonly object _sync = new();
    private IntPtr _request;
    private IntPtr _reasonString;
    private bool _active;

    public WindowsSleepPrevention(Action<string> log)
    {
        _log = log;
    }

    public void SetActive(bool active)
    {
        lock (_sync)
        {
            if (active == _active)
            {
                return;
            }

            try
            {
                if (active)
                {
                    CreateAndSet();
                }
                else
                {
                    ClearAndClose();
                }

                _active = active;
            }
            catch (Exception ex) when (ex is Win32Exception or InvalidOperationException)
            {
                _log($"Sleep prevention update failed: {ex.Message}");
            }
        }
    }

    private void CreateAndSet()
    {
        if (_request != IntPtr.Zero)
        {
            return;
        }

        _reasonString = Marshal.StringToHGlobalUni("Inbound Download Manager is downloading files");
        var context = new ReasonContext
        {
            Version = 0,
            Flags = PowerRequestContextSimpleString,
            Reason = new ReasonUnion { SimpleReasonString = _reasonString },
        };
        _request = PowerCreateRequest(ref context);
        if (_request == IntPtr.Zero)
        {
            var error = new Win32Exception(Marshal.GetLastWin32Error());
            Marshal.FreeHGlobal(_reasonString);
            _reasonString = IntPtr.Zero;
            throw error;
        }

        if (!PowerSetRequest(_request, PowerRequestSystemRequired))
        {
            var error = new Win32Exception(Marshal.GetLastWin32Error());
            CloseHandle(_request);
            _request = IntPtr.Zero;
            Marshal.FreeHGlobal(_reasonString);
            _reasonString = IntPtr.Zero;
            throw error;
        }

        _log("Sleep prevention active.");
    }

    private void ClearAndClose()
    {
        if (_request != IntPtr.Zero)
        {
            PowerClearRequest(_request, PowerRequestSystemRequired);
            CloseHandle(_request);
            _request = IntPtr.Zero;
            _log("Sleep prevention released.");
        }

        if (_reasonString != IntPtr.Zero)
        {
            Marshal.FreeHGlobal(_reasonString);
            _reasonString = IntPtr.Zero;
        }
    }

    public void Dispose()
    {
        lock (_sync)
        {
            ClearAndClose();
            _active = false;
        }
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct ReasonContext
    {
        public uint Version;
        public uint Flags;
        public ReasonUnion Reason;
    }

    [StructLayout(LayoutKind.Explicit)]
    private struct ReasonUnion
    {
        [FieldOffset(0)] public IntPtr SimpleReasonString;
        [FieldOffset(0)] public DetailedReason Detailed;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct DetailedReason
    {
        public IntPtr Module;
        public uint ReasonId;
        public uint ReasonStringCount;
        public IntPtr ReasonStrings;
    }

    [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern IntPtr PowerCreateRequest(ref ReasonContext context);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool PowerSetRequest(IntPtr request, int requestType);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool PowerClearRequest(IntPtr request, int requestType);

    [DllImport("kernel32.dll")]
    private static extern bool CloseHandle(IntPtr handle);
}

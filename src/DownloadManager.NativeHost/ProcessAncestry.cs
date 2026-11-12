using System.Runtime.InteropServices;

namespace DownloadManager.NativeHost;

/// <summary>
/// UI.R41: the names of the programs above this one, nearest first, so the native host can tell Chrome from Edge when
/// both run the Chrome Web Store copy of the extension. Chrome and Edge start it through <c>cmd.exe</c>, so the browser
/// is the second or third name. Windows only (one Toolhelp snapshot, read once at start); elsewhere, and when anything
/// fails, the list is empty and the extension's ID decides.
/// </summary>
internal static class ProcessAncestry
{
    private const int MostLevels = 4;

    public static IReadOnlyList<string> Names()
    {
        if (!OperatingSystem.IsWindows()) return [];
        try
        {
            var parents = new Dictionary<int, (int Parent, string Name)>();
            var snapshot = CreateToolhelp32Snapshot(SnapProcess, 0);
            if (snapshot == IntPtr.Zero || snapshot == new IntPtr(-1)) return [];
            try
            {
                var entry = new ProcessEntry32 { Size = (uint)Marshal.SizeOf<ProcessEntry32>() };
                for (var more = Process32First(snapshot, ref entry); more; more = Process32Next(snapshot, ref entry))
                    parents[(int)entry.ProcessId] = ((int)entry.ParentProcessId, entry.ExeFile ?? string.Empty);
            }
            finally
            {
                CloseHandle(snapshot);
            }

            var names = new List<string>(MostLevels);
            var current = Environment.ProcessId;
            for (var level = 0; level < MostLevels && parents.TryGetValue(current, out var self) && self.Parent != 0; level++)
            {
                if (!parents.TryGetValue(self.Parent, out var parent)) break;
                names.Add(parent.Name);
                current = self.Parent;
            }

            return names;
        }
        catch (Exception ex) when (ex is DllNotFoundException or EntryPointNotFoundException or ExternalException)
        {
            return [];
        }
    }

    private const uint SnapProcess = 0x00000002;

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct ProcessEntry32
    {
        public uint Size;
        public uint Usage;
        public uint ProcessId;
        public IntPtr DefaultHeapId;
        public uint ModuleId;
        public uint Threads;
        public uint ParentProcessId;
        public int PriorityClassBase;
        public uint Flags;

        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 260)]
        public string? ExeFile;
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern IntPtr CreateToolhelp32Snapshot(uint flags, uint processId);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, EntryPoint = "Process32FirstW", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool Process32First(IntPtr snapshot, ref ProcessEntry32 entry);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, EntryPoint = "Process32NextW", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool Process32Next(IntPtr snapshot, ref ProcessEntry32 entry);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CloseHandle(IntPtr handle);
}

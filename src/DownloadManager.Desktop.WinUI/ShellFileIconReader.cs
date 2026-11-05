using System.Runtime.InteropServices;

namespace DownloadManager.Desktop.WinUI;

// Native work only. Call off the UI thread; returned bytes are premultiplied BGRA.
internal static class ShellFileIconReader
{
    internal const int Size = 32;

    internal static byte[]? Read(string fileName, string? completedPath)
    {
        var initialized = CoInitializeEx(0, 0); // COM-initialized background worker (normally MTA).
        nint icon = 0;
        try
        {
            if (initialized < 0 && initialized != unchecked((int)0x80010106)) return null;
            var actualFile = !string.IsNullOrWhiteSpace(completedPath) && File.Exists(completedPath);
            var path = actualFile ? completedPath! : Path.GetExtension(fileName);
            if (string.IsNullOrEmpty(path)) path = ".download-manager-unknown";
            // Never inspect a partial executable; use its registered file type until completed.
            var flags = 0x100u | (actualFile ? 0u : 0x10u); // ICON, USEFILEATTRIBUTES
            if (SHGetFileInfo(path, 0x80, out var info, (uint)Marshal.SizeOf<FileInfo>(), flags) == 0)
                return null;
            icon = info.Icon;
            if (icon == 0) return null;
            var black = Render(icon, false);
            var white = Render(icon, true);
            if (black is null || white is null) return null;
            // Two backgrounds preserve transparency for both alpha and legacy masked icons.
            for (var i = 0; i < black.Length; i += 4)
            {
                var transparency = Math.Max(white[i] - black[i],
                    Math.Max(white[i + 1] - black[i + 1], white[i + 2] - black[i + 2]));
                black[i + 3] = (byte)Math.Clamp(255 - transparency, 0, 255);
            }
            return black;
        }
        finally
        {
            if (icon != 0) DestroyIcon(icon);
            if (initialized >= 0) CoUninitialize();
        }
    }

    private static byte[]? Render(nint icon, bool white)
    {
        var dc = CreateCompatibleDC(0);
        if (dc == 0) return null;
        nint bitmap = 0, previous = 0;
        try
        {
            var header = new BitmapHeader { Size = 40, Width = Size, Height = -Size,
                Planes = 1, BitCount = 32, ImageSize = Size * Size * 4 };
            bitmap = CreateDIBSection(dc, ref header, 0, out var bits, 0, 0);
            if (bitmap == 0 || bits == 0) return null;
            previous = SelectObject(dc, bitmap);
            if (previous == 0 || previous == -1) return null;
            var pixels = new byte[Size * Size * 4];
            if (white) Array.Fill(pixels, (byte)255);
            Marshal.Copy(pixels, 0, bits, pixels.Length);
            if (!DrawIconEx(dc, 0, 0, icon, Size, Size, 0, 0, 3)) return null;
            GdiFlush();
            Marshal.Copy(bits, pixels, 0, pixels.Length);
            return pixels;
        }
        finally
        {
            if (previous != 0 && previous != -1) SelectObject(dc, previous);
            if (bitmap != 0) DeleteObject(bitmap);
            DeleteDC(dc);
        }
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct FileInfo
    {
        public nint Icon;
        public int IconIndex;
        public uint Attributes;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 260)] public string DisplayName;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 80)] public string TypeName;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct BitmapHeader
    {
        public uint Size;
        public int Width, Height;
        public ushort Planes, BitCount;
        public uint Compression, ImageSize;
        public int XPelsPerMeter, YPelsPerMeter;
        public uint ColorsUsed, ColorsImportant;
    }

    [DllImport("shell32.dll", EntryPoint = "SHGetFileInfoW", CharSet = CharSet.Unicode)]
    private static extern nint SHGetFileInfo(string path, uint attributes, out FileInfo info, uint size, uint flags);
    [DllImport("ole32.dll")] private static extern int CoInitializeEx(nint reserved, uint flags);
    [DllImport("ole32.dll")] private static extern void CoUninitialize();
    [DllImport("user32.dll")] [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool DestroyIcon(nint icon);
    [DllImport("user32.dll")] [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool DrawIconEx(nint dc, int x, int y, nint icon, int width, int height, uint step, nint brush, uint flags);
    [DllImport("gdi32.dll")] private static extern nint CreateCompatibleDC(nint dc);
    [DllImport("gdi32.dll")] private static extern nint CreateDIBSection(nint dc, ref BitmapHeader header, uint usage, out nint bits, nint section, uint offset);
    [DllImport("gdi32.dll")] private static extern nint SelectObject(nint dc, nint value);
    [DllImport("gdi32.dll")] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool DeleteObject(nint value);
    [DllImport("gdi32.dll")] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool DeleteDC(nint dc);
    [DllImport("gdi32.dll")] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool GdiFlush();
}

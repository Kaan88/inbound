using Microsoft.UI.Xaml.Media.Imaging;
using System.Runtime.InteropServices.WindowsRuntime;

namespace DownloadManager.Desktop.WinUI;

internal static class FileIconProvider
{
    private static readonly SemaphoreSlim Workers = new(2);
    private sealed record Entry(Task<byte[]?> Task, DateTimeOffset Created);
    private static readonly Dictionary<string, Entry> Cache = new(StringComparer.OrdinalIgnoreCase);
    private const int Capacity = 256;

    // Invoked on the UI thread; no filesystem access or Shell calls before yielding.
    /// <param name="remote">The download is on another server: the asking window's own Host (T11). Null: the server
    /// the main window shows.</param>
    internal static async Task<WriteableBitmap?> LoadAsync(string fileName, string? completedPath, string revision, bool? remote = null)
    {
        // A download on another server: its file is on that machine, so only its type's icon (design D9, section 5).
        completedPath = RemoteDataRules.IconPath(ShownServer.IsRemoteFor(remote), completedPath);
        var key = completedPath is null ? "type:" + Path.GetExtension(fileName) : "file:" + completedPath + ":" + revision;
        Task<byte[]?> task;
        lock (Cache)
        {
            if (Cache.TryGetValue(key, out var entry) &&
                (!entry.Task.IsCompleted || DateTimeOffset.UtcNow - entry.Created < TimeSpan.FromMinutes(1)))
                task = entry.Task;
            else
            {
                Cache.Remove(key);
                if (Cache.Count >= Capacity)
                {
                    var expired = Cache.FirstOrDefault(item => item.Value.Task.IsCompleted);
                    if (expired.Key is not null) Cache.Remove(expired.Key);
                }
                task = ReadAsync(fileName, completedPath);
                // A full in-flight cache must not permanently drop icons in a large snapshot.
                // Overflow requests still use the same two-worker gate, without being cached.
                if (Cache.Count < Capacity) Cache.Add(key, new Entry(task, DateTimeOffset.UtcNow));
            }
        }
        var pixels = await task;
        if (pixels is null) return null;
        var bitmap = new WriteableBitmap(ShellFileIconReader.Size, ShellFileIconReader.Size);
        using var stream = bitmap.PixelBuffer.AsStream();
        stream.Write(pixels);
        bitmap.Invalidate();
        return bitmap;
    }

    private static async Task<byte[]?> ReadAsync(string fileName, string? completedPath)
    {
        // The shell's file icons are Windows-only; elsewhere the list shows its generic icon. [TODO D7] the icon theme's MIME icons.
        if (!OperatingSystem.IsWindows()) return null;
        await Workers.WaitAsync().ConfigureAwait(false);
        try
        {
            return await Task.Run(() => ShellFileIconReader.Read(fileName, completedPath)).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or System.Runtime.InteropServices.COMException)
        {
            return null;
        }
        finally { Workers.Release(); }
    }
}

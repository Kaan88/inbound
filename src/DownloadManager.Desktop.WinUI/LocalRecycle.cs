using FileAttributes = System.IO.FileAttributes;
using System.Runtime.InteropServices;
using DownloadManager.Contracts;

namespace DownloadManager.Desktop.WinUI;

/// <summary>
/// SITES.11 R5: Delete and Delete permanently in the This PC pane (Windows only; the window asks first).
/// <para>
/// Recycle: one shell call for all the items, on a thread of its own (STA, since the shell may show its own question), with
/// FOF_WANTNUKEWARNING so an item the Recycle Bin can't take (a network share, too big for the bin) is asked about by Windows
/// and never deleted outright unasked, and FOF_NO_CONNECTED_ELEMENTS so "page.htm" never takes "page_files" with it. The
/// Visual Basic helper used before passed FOF_NOCONFIRMATION alone, which deleted such items for good without a word.
/// </para>
/// <para>
/// For good: plain File and Directory deletes walked by hand, not the shell. Only the named items and what is inside them go
/// (no connected files, no wildcards, no shell UI); a link (symbolic or junction) is removed itself and never followed; and
/// an item that can't go (in use, read-only) is left and named while the rest goes on.
/// </para>
/// Every path must be a full path (<see cref="SiteChangeRules.LocalDeletePathProblem"/>). Each method returns what was left,
/// one line each.
/// </summary>
internal static class LocalRecycle
{
    private const uint FoDelete = 3;
    private const ushort FofNoConfirmation = 0x0010;
    private const ushort FofAllowUndo = 0x0040;
    private const ushort FofNoConnectedElements = 0x2000;
    private const ushort FofWantNukeWarning = 0x4000;

    // A delete for good with this many items left stops naming more (the note shows three).
    private const int MaxLeft = 200;

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct ShFileOp
    {
        public nint Window;
        public uint Function;
        [MarshalAs(UnmanagedType.LPWStr)] public string From;
        [MarshalAs(UnmanagedType.LPWStr)] public string? To;
        public ushort Flags;
        [MarshalAs(UnmanagedType.Bool)] public bool Aborted;
        public nint Mappings;
        [MarshalAs(UnmanagedType.LPWStr)] public string? Title;
    }

    [DllImport("shell32.dll", CharSet = CharSet.Unicode, EntryPoint = "SHFileOperationW")]
    private static extern int ShFileOperation(ref ShFileOp operation);

    /// <summary>Sends <paramref name="paths"/> to the Recycle Bin in one shell call on an STA thread; what was left, one line each.</summary>
    public static Task<List<string>> RecycleAsync(IReadOnlyList<string> paths, nint owner)
    {
        var done = new TaskCompletionSource<List<string>>(TaskCreationOptions.RunContinuationsAsynchronously);
        var thread = new Thread(() =>
        {
            try
            {
                done.SetResult(Recycle(paths, owner));
            }
            catch (Exception ex)
            {
                done.SetException(ex);
            }
        })
        {
            IsBackground = true,
            Name = "Inbound Recycle Bin",
        };
        if (OperatingSystem.IsWindows()) thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        return done.Task;
    }

    private static List<string> Recycle(IReadOnlyList<string> paths, nint owner)
    {
        var left = new List<string>();
        var send = new List<string>();
        foreach (var path in paths)
        {
            if (SiteChangeRules.LocalDeletePathProblem(path) is { } problem) left.Add($"{Name(path)}: {problem}");
            else if (Exists(path)) send.Add(path);
        }

        if (send.Count == 0) return left;
        if (!OperatingSystem.IsWindows())
        {
            left.AddRange(send.Select(path => $"{Name(path)}: the Recycle Bin is Windows only."));
            return left;
        }

        int code;
        bool aborted;
        try
        {
            // The list ends with two nulls (written here; the marshaller's own one is a third, harmless).
            var operation = new ShFileOp
            {
                Window = owner,
                Function = FoDelete,
                From = string.Join('\0', send) + "\0\0",
                Flags = FofAllowUndo | FofNoConfirmation | FofWantNukeWarning | FofNoConnectedElements,
            };
            code = ShFileOperation(ref operation);
            aborted = operation.Aborted;
        }
        catch (Exception ex) when (ex is DllNotFoundException or EntryPointNotFoundException or MarshalDirectiveException)
        {
            left.AddRange(send.Select(path => $"{Name(path)}: {ex.Message}"));
            return left;
        }

        // One answer for the whole call: an item still there is the one that did not go (a No to Windows' question, in use…).
        var why = aborted ? "not deleted" : code != 0 ? SiteChangeRules.ShellDeleteMessage(code) : "not deleted";
        left.AddRange(send.Where(Exists).Select(path => $"{Name(path)}: {why}"));
        return left;
    }

    /// <summary>
    /// Deletes <paramref name="paths"/> for good (see the class); what was left, one line each, named from
    /// <paramref name="folder"/>. CutShort: it stopped after <see cref="MaxLeft"/> were left, so more may be there.
    /// </summary>
    public static (List<string> Left, bool CutShort) DeleteForGood(string folder, IReadOnlyList<string> paths)
    {
        var left = new List<string>();
        foreach (var path in paths) Remove(folder, path, left);
        return (left, left.Count >= MaxLeft);
    }

    private static void Remove(string folder, string path, List<string> left)
    {
        if (left.Count >= MaxLeft) return;
        // Checked for every item, the chosen ones and each found inside them: a name Windows can't address ("foo.", "a ")
        // would make the delete hit another item, so it is left and named (and nothing under it is walked).
        if (SiteChangeRules.LocalDeletePathProblem(path) is { } problem)
        {
            left.Add($"{Relative(folder, path)}: {problem}");
            return;
        }

        FileAttributes attributes;
        try
        {
            // The item's own attributes: a link is not followed.
            attributes = File.GetAttributes(path);
        }
        catch (Exception ex) when (ex is FileNotFoundException or DirectoryNotFoundException)
        {
            return;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Security.SecurityException or ArgumentException or NotSupportedException)
        {
            left.Add(SiteChangeRules.LocalLeftText(Relative(folder, path), ex));
            return;
        }

        try
        {
            if (!attributes.HasFlag(FileAttributes.Directory))
            {
                // A file, or a link to one (the link goes, not what it points at).
                File.Delete(path);
                return;
            }

            if (!IsLink(path, attributes))
            {
                var before = left.Count;
                var children = Directory.EnumerateFileSystemEntries(path, "*", new EnumerationOptions
                {
                    AttributesToSkip = 0, IgnoreInaccessible = false, RecurseSubdirectories = false, ReturnSpecialDirectories = false,
                }).ToList();
                foreach (var child in children) Remove(folder, child, left);
                // Something inside was left: the folder stays too (it is named through what is in it).
                if (left.Count > before) return;

                // A read-only folder (common with a desktop.ini) can't be removed; files keep their read-only mark, so a
                // read-only file is left and named instead.
                if (attributes.HasFlag(FileAttributes.ReadOnly)) File.SetAttributes(path, attributes & ~FileAttributes.ReadOnly);
            }

            // An emptied folder, or a link to a folder (a junction or a symbolic link): only the entry itself goes.
            Directory.Delete(path, recursive: false);
        }
        catch (Exception ex) when (ex is FileNotFoundException or DirectoryNotFoundException)
        {
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Security.SecurityException or ArgumentException or NotSupportedException)
        {
            left.Add(SiteChangeRules.LocalLeftText(Relative(folder, path), ex));
        }
    }

    /// <summary>
    /// What Delete permanently will remove, for the dialog's count: files and bytes, counting inside the chosen folders and
    /// never through a link. A folder that can't be read makes it "at least".
    /// </summary>
    public static SiteCountResultDto Count(IReadOnlyList<string> paths, CancellationToken token)
    {
        var result = new SiteCountResultDto { Ok = true };
        foreach (var path in paths)
        {
            token.ThrowIfCancellationRequested();
            if (!Exists(path))
            {
                result.Missing++;
                continue;
            }

            try
            {
                var attributes = File.GetAttributes(path);
                if (!attributes.HasFlag(FileAttributes.Directory))
                {
                    result.Files++;
                    result.Bytes += new FileInfo(path).Length;
                    continue;
                }

                result.Folders++;
                if (IsLink(path, attributes)) continue;
                CountInside(path, result, token);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Security.SecurityException)
            {
                result.SizeUnknown = true;
            }
        }

        return result;
    }

    private static void CountInside(string folder, SiteCountResultDto result, CancellationToken token)
    {
        var pending = new Stack<string>();
        pending.Push(folder);
        var options = new EnumerationOptions { AttributesToSkip = 0, IgnoreInaccessible = false, RecurseSubdirectories = false };
        while (pending.Count > 0)
        {
            token.ThrowIfCancellationRequested();
            var current = pending.Pop();
            try
            {
                foreach (var entry in new DirectoryInfo(current).EnumerateFileSystemInfos("*", options))
                {
                    if (entry.Attributes.HasFlag(FileAttributes.Directory))
                    {
                        if (!IsLink(entry.FullName, entry.Attributes)) pending.Push(entry.FullName);
                        continue;
                    }

                    result.Files++;
                    if (entry is FileInfo file) result.Bytes += file.Length;
                }
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Security.SecurityException)
            {
                // Not readable: the count is a floor.
                result.SizeUnknown = true;
            }
        }
    }

    /// <summary>
    /// A symbolic link or a junction: removed itself, never walked into. Other reparse points (a OneDrive folder, say) are
    /// ordinary folders here.
    /// </summary>
    private static bool IsLink(string path, FileAttributes attributes)
    {
        if (!attributes.HasFlag(FileAttributes.ReparsePoint)) return false;
        try
        {
            return new DirectoryInfo(path).LinkTarget is not null;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Not readable as a link: treated as one, so nothing is walked through it.
            return true;
        }
    }

    /// <summary>Whether the entry itself is there (a link whose target is gone still is).</summary>
    private static bool Exists(string path)
    {
        try
        {
            _ = File.GetAttributes(path);
            return true;
        }
        catch (Exception ex) when (ex is FileNotFoundException or DirectoryNotFoundException)
        {
            return false;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
        {
            return true;
        }
    }

    private static string Name(string path)
    {
        var name = Path.GetFileName(path.TrimEnd('\\', '/'));
        return name.Length > 0 ? name : path;
    }

    private static string Relative(string folder, string path)
    {
        try
        {
            return Path.GetRelativePath(folder, path);
        }
        catch (ArgumentException)
        {
            return Name(path);
        }
    }
}

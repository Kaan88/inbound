using System.Runtime.InteropServices;
using System.Text;
using DownloadManager.Core;
using Microsoft.Win32.SafeHandles;

namespace DownloadManager.Runtime;

/// <summary>
/// UI.B19: the folders the app made for one download, and taking them away again when the download is deleted.
/// Only a provider file is put below the folder it was given: its path in the provider's item
/// (<see cref="RemoteSource.RelativePath"/>) becomes folders under that one. Which of them the app actually created is
/// recorded on the job when it creates them (<see cref="DownloadJob.MadeFolders"/>), and only those are ever removed.
/// Every other download goes straight into the folder the user, a category or the default gave it, and that folder is
/// never ours to remove, even when the app created it.
/// </summary>
public sealed partial class DownloadManagerService
{
    /// <summary>
    /// Creates <paramref name="directory"/> and returns the folders this call created strictly below
    /// <paramref name="baseFolder"/>, deepest first. Nothing is recorded without a base, or for a folder that is not
    /// below it as text, so the folder a download was given, and anything outside it, is never listed.
    /// </summary>
    internal static List<string> CreateFolderRecordingMade(string directory, string? baseFolder)
    {
        var full = Normalized(directory) ?? directory;
        var missing = new List<string>();
        if (Normalized(baseFolder) is { } root)
        {
            var current = full;
            while (IsStrictlyBelow(current, root) && !Directory.Exists(current) && !File.Exists(current))
            {
                missing.Add(current);
                if (Path.GetDirectoryName(current) is not { } parent) break;
                current = parent;
            }
        }

        Directory.CreateDirectory(full);
        return missing.Where(Directory.Exists).ToList();
    }

    private static bool IsStrictlyBelow(string path, string root)
    {
        var prefix = Path.EndsInDirectorySeparator(root) ? root : root + Path.DirectorySeparatorChar;
        return path.Length > prefix.Length && path.StartsWith(prefix, PathNames.Comparison);
    }

    private static bool IsAtOrBelow(string path, string folder)
        => PathNames.Same(path, folder) || IsStrictlyBelow(path, folder);

    private bool IsRunning(JobRuntime runtime)
    {
        lock (_sync) return runtime.Starting || runtime.Runner is { IsCompleted: false };
    }

    /// <summary>Adds folders to the job's record, once each. Call under <see cref="_sync"/>.</summary>
    private static bool RecordMadeFoldersLocked(DownloadJob job, IEnumerable<string> folders)
    {
        var changed = false;
        foreach (var folder in folders)
        {
            if (job.MadeFolders.Contains(folder, PathNames.Comparer)) continue;
            job.MadeFolders.Add(folder);
            changed = true;
        }

        return changed;
    }

    /// <summary>
    /// Before a transfer starts: a provider file's folders that are gone (taken away by a cancel, say) are made again
    /// here, and recorded, rather than by the engine, which records nothing. Returns whether the record changed.
    /// </summary>
    private bool RecreateMadeFolders(DownloadJob job)
    {
        string? baseFolder, directory;
        lock (_sync) (baseFolder, directory) = (job.MadeFoldersBase, job.DestinationDirectory);
        if (baseFolder is null || string.IsNullOrWhiteSpace(directory)) return false;
        try
        {
            var made = CreateFolderRecordingMade(directory, baseFolder);
            if (made.Count == 0) return false;
            lock (_sync) return RecordMadeFoldersLocked(job, made);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
        {
            // The engine reports a folder it can't make.
            return false;
        }
    }

    /// <summary>
    /// Removes the folders the app made for this download that are now empty, deepest first. Never one that wasn't
    /// recorded as made, never a drive or share root, never a download, category, favorite, last-used, recent,
    /// extraction or server folder or one above any of those, never one outside a limited caller's folders, never one
    /// that is or holds a link or junction, and never one another download still uses: that download takes it over, so
    /// it goes when the last one does. Paths are compared as this machine compares them, and on Windows also by what
    /// they lead to, so a mapped drive and its share, a short (8.3) name or a <c>\\?\</c> form are the same folder.
    /// When that can't be told the folder stays.
    /// </summary>
    /// <param name="stillListed">The job stays on the list (a cancel): the folders removed leave its record, and a
    /// later start makes and records them again.</param>
    private async Task RemoveEmptyFoldersMadeForAsync(DownloadJob job, CallerScope? caller, bool stillListed)
    {
        List<string> made;
        List<string> kept;
        List<(JobRuntime Runtime, string Folder)> others;
        lock (_sync)
        {
            made = job.MadeFolders.Select(Normalized).Where(path => path is not null).Select(path => path!)
                .Distinct(PathNames.Comparer)
                // Deepest first: an inner folder has to go before the one holding it can be empty.
                .OrderByDescending(path => path.Length).ToList();
            if (made.Count == 0) return;
            kept = KeptFoldersLocked();
            others = [];
            foreach (var runtime in _jobs.Values)
                if (runtime.Job.Id != job.Id && Normalized(runtime.Job.DestinationDirectory) is { } used) others.Add((runtime, used));
        }

        // Each kept or used folder by its text and, where it exists, by what it leads to.
        var keptForms = kept.SelectMany(FormsOf).ToList();
        var otherForms = others.SelectMany(other => FormsOf(other.Folder).Select(form => (other.Runtime, Form: form))).ToList();

        var removed = new List<string>();
        var handedOver = new Dictionary<JobRuntime, List<string>>();
        foreach (var folder in made)
        {
            try
            {
                if (!Directory.Exists(folder))
                {
                    // Gone already, by the user or a run that ended. Nothing to keep a record of.
                    removed.Add(folder);
                    continue;
                }

                if (KeepReason(folder, caller) is { } reason)
                {
                    _log?.Invoke($"Kept a folder made for job {job.Id}: {reason}.");
                    continue;
                }

                // Its text and what it leads to. Without the latter the folder stays: it can't be told apart.
                var identity = Canonical(folder);
                if (identity is null && OperatingSystem.IsWindows())
                {
                    _log?.Invoke($"Kept a folder made for job {job.Id}: it could not be identified.");
                    continue;
                }

                string[] forms = identity is null ? [folder] : [folder, identity];
                if (keptForms.Any(keptFolder => forms.Any(form => IsAtOrBelow(keptFolder, form))))
                {
                    _log?.Invoke($"Kept a folder made for job {job.Id}: it is or holds a folder named in the settings.");
                    continue;
                }

                var user = otherForms.FirstOrDefault(other => forms.Any(form => IsAtOrBelow(other.Form, form))).Runtime;
                if (user is not null)
                {
                    // Still in use. The download using it takes it over, so it goes once the last one using it does.
                    if (!handedOver.TryGetValue(user, out var list)) handedOver[user] = list = [];
                    list.Add(folder);
                    removed.Add(folder);
                    continue;
                }

                // The app's own quarantine folder, when empty, doesn't make the folder holding it the user's.
                RemoveEmptyQuarantine(folder);
                if (Directory.EnumerateFileSystemEntries(folder).Any()) continue;
                // Not recursive: if something arrived meanwhile, this fails and the folder stays.
                Directory.Delete(folder, recursive: false);
                removed.Add(folder);
                _log?.Invoke($"Removed the empty folder made for job {job.Id}.");
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
            {
                _log?.Invoke($"Kept a folder made for job {job.Id}: {ex.Message}");
            }
        }

        foreach (var (user, folders) in handedOver)
        {
            bool changed;
            lock (_sync) changed = _jobs.ContainsKey(user.Job.Id) && RecordMadeFoldersLocked(user.Job, folders);
            if (changed) await PersistAsync(user, CancellationToken.None).ConfigureAwait(false);
        }

        if (stillListed && removed.Count > 0)
        {
            lock (_sync) job.MadeFolders.RemoveAll(path => removed.Contains(Normalized(path) ?? path, PathNames.Comparer));
        }
    }

    /// <summary>Why this folder stays whatever it holds, or null. Touches the file system.</summary>
    private static string? KeepReason(string folder, CallerScope? caller)
    {
        if (Path.GetPathRoot(folder) is { } root && PathNames.Same(Path.TrimEndingDirectorySeparator(root), Path.TrimEndingDirectorySeparator(folder)))
            return "it is a drive or share root";
        // What is behind a link isn't this download's, and a link on the way up means the folder is somewhere else.
        for (var current = folder; current is not null; current = Path.GetDirectoryName(current))
        {
            if (Directory.Exists(current) && new DirectoryInfo(current).Attributes.HasFlag(FileAttributes.ReparsePoint))
                return "it is or lies under a link or junction";
        }

        if (caller is not null && caller.Roots.Allowed(folder) is null)
            return "it is outside the folders of the caller that deleted the download";
        return null;
    }

    /// <summary>A folder's text and, where it exists, what it leads to.</summary>
    private static IEnumerable<string> FormsOf(string folder)
    {
        yield return folder;
        if (Canonical(folder) is { } identity && !PathNames.Same(identity, folder)) yield return identity;
    }

    /// <summary>Folders a user named somewhere in the settings or the server's roots. Call under <see cref="_sync"/>.</summary>
    private List<string> KeptFoldersLocked()
    {
        IEnumerable<string?> named =
        [
            _settings.DefaultDownloadDirectory,
            _settings.LastDestinationDirectory,
            _settings.CategoryFoldersRoot,
            .. _settings.CategoryFolders.Values,
            .. _settings.CustomCategories.Select(category => category.DestinationDirectory),
            .. _settings.FavoriteFolders.Select(favorite => favorite.Path),
            .. _settings.RecentDestinationDirectories,
            ExtractionFolder?.Invoke(),
            .. Roots?.Roots ?? [],
        ];
        return named.Select(Normalized).Where(path => path is not null).Select(path => path!)
            .Distinct(PathNames.Comparer).ToList();
    }

    /// <summary>The extraction folder the user chose (set by the archive coordinator), kept like the download folders.</summary>
    public Func<string?>? ExtractionFolder { get; set; }

    private static string? Normalized(string? path)
    {
        if (string.IsNullOrWhiteSpace(path)) return null;
        try { return Path.TrimEndingDirectorySeparator(Path.GetFullPath(path.Trim())); }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException) { return null; }
    }

    /// <summary>
    /// What an existing folder's path leads to, as Windows names it: long names instead of short (8.3) ones, the share
    /// instead of a mapped drive, without <c>\\?\</c>. Null when it doesn't exist or can't be opened, and on other
    /// systems, where the text is used.
    /// </summary>
    internal static string? Canonical(string folder)
    {
        if (!OperatingSystem.IsWindows()) return null;
        try
        {
            using var handle = CreateFileW(folder, 0, FileShare.ReadWrite | FileShare.Delete, IntPtr.Zero, FileMode.Open,
                BackupSemantics, IntPtr.Zero);
            if (handle.IsInvalid) return null;
            var buffer = new StringBuilder(1024);
            var length = GetFinalPathNameByHandleW(handle, buffer, (uint)buffer.Capacity, 0);
            if (length == 0) return null;
            if (length >= buffer.Capacity)
            {
                buffer = new StringBuilder((int)length + 1);
                length = GetFinalPathNameByHandleW(handle, buffer, (uint)buffer.Capacity, 0);
                if (length == 0 || length >= buffer.Capacity) return null;
            }

            var path = buffer.ToString();
            if (path.StartsWith(@"\\?\UNC\", StringComparison.OrdinalIgnoreCase)) path = @"\\" + path[8..];
            else if (path.StartsWith(@"\\?\", StringComparison.Ordinal)) path = path[4..];
            return Path.TrimEndingDirectorySeparator(path);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or DllNotFoundException or EntryPointNotFoundException)
        {
            return null;
        }
    }

    private const uint BackupSemantics = 0x02000000;

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern SafeFileHandle CreateFileW(string name, uint access, FileShare share, IntPtr security,
        FileMode mode, uint flags, IntPtr template);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern uint GetFinalPathNameByHandleW(SafeFileHandle handle, StringBuilder path, uint length, uint flags);
}

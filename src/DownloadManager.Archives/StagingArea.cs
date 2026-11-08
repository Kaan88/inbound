using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using DownloadManager.Core.Archives;
using DownloadManager.Core;

namespace DownloadManager.Archives;

/// <param name="Removed">The folder is gone. False means something could not be undone yet, so it stays for the next attempt.</param>
/// <param name="LeftInPlace">Files left where they were because they have changed since the attempt placed them, or because an original had to go in beside one.</param>
public sealed record StagingRecovery(bool Removed, int LeftInPlace);

/// <summary>
/// The one folder an extraction writes to before finalizing. Its name comes from the archive and
/// destination, so a retry finds and wipes the same folder. Holds no record of success: a killed
/// UnRAR leaves files at full size with zero tails, so only exit 0, the walk and the move count.
/// <para>
/// It sits next to the user's files, so nothing in it is taken at its word. What a folder is for comes
/// from the caller, who has it saved in the app's own state, and the folder's name has to agree. A log
/// entry can only remove a file still exactly as that entry says it was placed, and only under that
/// destination. A folder nobody here asked for is not ours and is left alone.
/// </para>
/// </summary>
public sealed class StagingArea
{
    private const string FolderPrefix = ArchiveWorkingFolders.Prefix;
    private const string PayloadName = "payload";
    private const string DisplacedName = "displaced";
    private const string PlacementLogName = "placed.log";

    private readonly string _destination;

    private StagingArea(string folder, string destination)
    {
        Folder = folder;
        _destination = destination;
    }

    public string Folder { get; }

    public string PayloadFolder => Path.Combine(Folder, PayloadName);

    internal string DisplacedFolder => Path.Combine(Folder, DisplacedName);

    /// <summary>Set when a rollback could not finish, so the record of it and any original must survive for the next attempt.</summary>
    internal bool KeepForRecovery { get; set; }

    /// <summary>Where the staging folder for this archive and destination is, whether it exists or not.</summary>
    public static string FolderFor(string stagingRoot, string firstVolume, string destination) =>
        Path.Combine(Path.GetFullPath(stagingRoot), FolderPrefix + FolderId(firstVolume, NormalizeDestination(destination)));

    /// <summary>Wipes any earlier attempt and creates an empty staging folder.</summary>
    public static async Task<StagingArea> PrepareAsync(string stagingRoot, string firstVolume, string destination, CancellationToken cancellationToken)
    {
        var fullRoot = Path.GetFullPath(stagingRoot);
        var fullDestination = NormalizeDestination(destination);
        // On Linux the volume is the mount that holds the folder, not "/": in a container /downloads is its own mount.
        if (!Volumes.Same(fullRoot, fullDestination))
        {
            throw new ExtractionException(ExtractionFailureKind.Unknown, "The staging folder must be on the same volume as the destination.");
        }

        var area = new StagingArea(FolderFor(fullRoot, firstVolume, fullDestination), fullDestination);
        try
        {
            if (Directory.Exists(area.Folder) && !(await RecoverAsync(area.Folder, fullDestination)).Removed)
            {
                throw new ExtractionException(ExtractionFailureKind.Permission, "An earlier staging folder could not be removed. A file in it may still be open.");
            }

            cancellationToken.ThrowIfCancellationRequested();
            Directory.CreateDirectory(area.PayloadFolder);
            return area;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            throw ExtractionSafety.MapIoFailure(exception, "preparing the staging folder");
        }
    }

    /// <summary>Folders under a root that wear the staging name. This says nothing about whose they are: a caller matches them to what it has saved.</summary>
    public static IReadOnlyList<string> FindLeftovers(string stagingRoot) =>
        Directory.Exists(stagingRoot) ? Directory.EnumerateDirectories(stagingRoot, FolderPrefix + "*").ToList() : [];

    /// <summary>
    /// Undoes an attempt a crash interrupted: removes the files it had already moved into the
    /// destination and puts back any original it had displaced, then deletes the staging folder.
    /// Without this the next attempt would meet its own earlier output as a conflict, and under
    /// Rename would write everything twice.
    /// </summary>
    public static async Task<StagingRecovery> RecoverAndRemoveAsync(string stagingRoot, string firstVolume, string destination)
    {
        var fullDestination = NormalizeDestination(destination);
        var folder = FolderFor(stagingRoot, firstVolume, fullDestination);
        return Directory.Exists(folder) ? await RecoverAsync(folder, fullDestination) : new StagingRecovery(true, 0);
    }

    /// <summary>
    /// Deletes a staging folder without undoing anything, for an attempt the caller has recorded
    /// as finished: its files are the result now. Returns false when it could not all be removed.
    /// </summary>
    public static Task<bool> DiscardFinishedAsync(string stagingRoot, string firstVolume, string destination) =>
        DeleteTreeAsync(FolderFor(stagingRoot, firstVolume, destination));

    /// <summary>Takes this attempt back out of the destination, when its success could not be recorded.</summary>
    internal Task<StagingRecovery> UndoAsync() => RecoverAsync(Folder, _destination);

    /// <summary>Removes this attempt's staging. A failure leaves a folder the next attempt will find.</summary>
    internal async Task RemoveAsync()
    {
        if (!KeepForRecovery) await DeleteTreeAsync(Folder);
    }

    internal PlacementLog OpenPlacementLog() => new(Path.Combine(Folder, PlacementLogName));

    private static string NormalizeDestination(string destination) => Path.TrimEndingDirectorySeparator(Path.GetFullPath(destination));

    private static async Task<StagingRecovery> RecoverAsync(string folder, string destination)
    {
        try
        {
            // Ours is never a link, and following one would act on whatever it points at.
            if ((File.GetAttributes(folder) & FileAttributes.ReparsePoint) != 0)
            {
                return new StagingRecovery(await DeleteTreeAsync(folder), 0);
            }
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            return new StagingRecovery(!Directory.Exists(folder), 0);
        }

        var (undone, leftInPlace) = UndoPlacements(folder, destination);
        if (!undone) return new StagingRecovery(false, leftInPlace);

        var (restored, placedBeside) = RestoreDisplaced(folder, destination);
        leftInPlace += placedBeside;
        if (!restored) return new StagingRecovery(false, leftInPlace);

        return new StagingRecovery(await DeleteTreeAsync(folder), leftInPlace);
    }

    /// <summary>
    /// A move is a rename on one volume, so a source no longer in the payload proves this attempt
    /// moved it, which makes the log exact even for the entry being written when a crash came. Time
    /// can pass before this runs, so a file that isn't as it was placed has become someone's, and stays.
    /// </summary>
    private static (bool Undone, int LeftInPlace) UndoPlacements(string folder, string destination)
    {
        var logFile = Path.Combine(folder, PlacementLogName);
        if (!File.Exists(logFile)) return (true, 0);

        var root = ExtractionSafety.WithSeparator(destination);
        var undone = true;
        var leftInPlace = 0;
        IReadOnlyList<PlacementEntry> entries;
        try
        {
            entries = PlacementLog.Read(logFile);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            return (false, 0);
        }

        foreach (var entry in entries.Reverse())
        {
            try
            {
                if (entry.Source is null || entry.Target is null || ExtractionSafety.DescribeUnsafePath(entry.Source) is not null) continue;
                var target = Path.GetFullPath(entry.Target);
                if (!target.StartsWith(root, PathNames.Comparison)) continue;
                if (File.Exists(Path.Combine(folder, PayloadName, entry.Source)) || !File.Exists(target)) continue;

                // An overwrite whose original is no longer displaced was already rolled back, so
                // what stands at the target now is that original.
                if (entry.DisplacedAs is { } displacedAs &&
                    (ExtractionSafety.DescribeUnsafePath(displacedAs) is not null || !File.Exists(Path.Combine(folder, DisplacedName, displacedAs))))
                {
                    continue;
                }

                if (!entry.StillDescribes(new FileInfo(target)))
                {
                    leftInPlace++;
                    continue;
                }

                File.SetAttributes(target, FileAttributes.Normal);
                File.Delete(target);
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
            {
                undone = false;
            }
            catch (Exception exception) when (exception is ArgumentException or NotSupportedException)
            {
                // A line no attempt of ours wrote, so nothing was ever placed by it.
            }
        }

        return (undone, leftInPlace);
    }

    /// <summary>An original is someone's only copy. If the place it came from is taken now, it goes in beside that file under a free name.</summary>
    private static (bool Restored, int PlacedBeside) RestoreDisplaced(string folder, string destination)
    {
        var displaced = Path.Combine(folder, DisplacedName);
        if (!Directory.Exists(displaced)) return (true, 0);

        var restored = true;
        var placedBeside = 0;
        var options = new EnumerationOptions { RecurseSubdirectories = true, AttributesToSkip = FileAttributes.ReparsePoint, IgnoreInaccessible = true };
        List<string> files;
        try
        {
            files = Directory.EnumerateFiles(displaced, "*", options).ToList();
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            return (false, 0);
        }

        foreach (var file in files)
        {
            try
            {
                var target = Path.GetFullPath(Path.Combine(destination, Path.GetRelativePath(displaced, file)));
                if (!target.StartsWith(ExtractionSafety.WithSeparator(destination), PathNames.Comparison) || PassesThroughLink(destination, target))
                {
                    restored = false;
                    continue;
                }

                Directory.CreateDirectory(Path.GetDirectoryName(target)!);
                if (File.Exists(target) || Directory.Exists(target))
                {
                    target = ExtractionSafety.FreeName(target);
                    placedBeside++;
                }

                File.Move(file, target);
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
            {
                restored = false;
            }
        }

        return (restored, placedBeside);
    }

    private static bool PassesThroughLink(string destination, string target)
    {
        for (var check = Path.GetDirectoryName(target); check is not null && check.Length > destination.Length; check = Path.GetDirectoryName(check))
        {
            if (Directory.Exists(check) && (File.GetAttributes(check) & FileAttributes.ReparsePoint) != 0) return true;
        }

        return false;
    }

    private static async Task<bool> DeleteTreeAsync(string folder)
    {
        // Retries because a killed process and antivirus can hold a handle for a moment.
        for (var attempt = 0; attempt < 6; attempt++)
        {
            try
            {
                if (!Directory.Exists(folder)) return true;
                if ((File.GetAttributes(folder) & FileAttributes.ReparsePoint) == 0) ClearReadOnly(folder);
                Directory.Delete(folder, recursive: true);
                return true;
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
            {
                await Task.Delay(100 * (attempt + 1), CancellationToken.None);
            }
        }

        return !Directory.Exists(folder);
    }

    /// <summary>UnRAR restores the read-only attribute, and a delete refuses such a file.</summary>
    private static void ClearReadOnly(string folder)
    {
        var options = new EnumerationOptions { RecurseSubdirectories = true, AttributesToSkip = FileAttributes.ReparsePoint, IgnoreInaccessible = true };
        foreach (var file in new DirectoryInfo(folder).EnumerateFiles("*", options))
        {
            if (file.IsReadOnly) file.IsReadOnly = false;
        }
    }

    private static string FolderId(string firstVolume, string destination)
    {
        // Windows folds case, so the same folder written two ways must give one id. Linux keeps them apart: two ids.
        var key = Path.GetFullPath(firstVolume) + "\n" + destination;
        if (OperatingSystem.IsWindows()) key = key.ToUpperInvariant();
        return Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(key)))[..16];
    }
}

/// <param name="Source">Relative to the payload folder.</param>
/// <param name="Target">The full path the file is moved to.</param>
/// <param name="DisplacedAs">Relative to the displaced folder, when this replaced an existing file.</param>
/// <param name="Length">The file's size when it was placed. A rename changes neither this nor the time.</param>
/// <param name="LastWriteTicks">Its last-write time when it was placed, in UTC ticks.</param>
internal sealed record PlacementEntry(string Source, string Target, string? DisplacedAs, long? Length = null, long? LastWriteTicks = null)
{
    public static PlacementEntry For(string relativeSource, string target, string? displacedAs, FileInfo source) =>
        new(relativeSource, target, displacedAs, source.Length, source.LastWriteTimeUtc.Ticks);

    public bool StillDescribes(FileInfo file) =>
        Length == file.Length && LastWriteTicks == file.LastWriteTimeUtc.Ticks;
}

/// <summary>
/// Written ahead of each move into the destination and flushed to the disk, so a crash at any
/// point leaves a record of everything the attempt may have placed. One flush per file is a small
/// price beside the cost of extracting it.
/// </summary>
internal sealed class PlacementLog : IDisposable
{
    private readonly string _path;
    private FileStream? _stream;

    public PlacementLog(string path)
    {
        _path = path;
    }

    public void Record(PlacementEntry entry)
    {
        _stream ??= new FileStream(_path, FileMode.Append, FileAccess.Write, FileShare.Read, 4096, FileOptions.WriteThrough);
        _stream.Write(Encoding.UTF8.GetBytes(JsonSerializer.Serialize(entry) + "\n"));
        _stream.Flush(flushToDisk: true);
    }

    public void Dispose() => _stream?.Dispose();

    /// <summary>A line a crash cut short never reached its move, so it is skipped.</summary>
    public static IReadOnlyList<PlacementEntry> Read(string path)
    {
        var entries = new List<PlacementEntry>();
        foreach (var line in File.ReadAllLines(path, Encoding.UTF8))
        {
            try
            {
                if (JsonSerializer.Deserialize<PlacementEntry>(line) is { } entry) entries.Add(entry);
            }
            catch (JsonException)
            {
                // The torn last line.
            }
        }

        return entries;
    }
}

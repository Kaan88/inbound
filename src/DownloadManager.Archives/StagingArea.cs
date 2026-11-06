using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using DownloadManager.Core.Archives;

namespace DownloadManager.Archives;

/// <summary>A folder left behind by an extraction that never finished, such as after a crash.</summary>
public sealed record StagingLeftover(string Folder, string? FirstVolume, string? Destination);

/// <summary>
/// The one folder an extraction writes to before finalizing. Its name comes from the archive and
/// destination, so a retry finds and wipes the same folder. Holds no record of success: a killed
/// UnRAR leaves files at full size with zero tails, so only exit 0, the walk and the move count.
/// </summary>
public sealed class StagingArea
{
    private const string FolderPrefix = ".inbound-extract-";
    private const string MarkerName = "attempt.json";
    private const string PayloadName = "payload";
    private const string DisplacedName = "displaced";
    private const string PlacementLogName = "placed.log";

    private StagingArea(string folder)
    {
        Folder = folder;
    }

    public string Folder { get; }

    public string PayloadFolder => Path.Combine(Folder, PayloadName);

    internal string DisplacedFolder => Path.Combine(Folder, DisplacedName);

    /// <summary>Set when a rollback could not put an original back, so the only copy must survive for the next attempt.</summary>
    internal bool KeepForRecovery { get; set; }

    /// <summary>Wipes any earlier attempt and creates an empty staging folder.</summary>
    public static async Task<StagingArea> PrepareAsync(string stagingRoot, string firstVolume, string destination, CancellationToken cancellationToken)
    {
        var fullRoot = Path.GetFullPath(stagingRoot);
        var fullDestination = Path.GetFullPath(destination);
        if (!string.Equals(Path.GetPathRoot(fullRoot), Path.GetPathRoot(fullDestination), StringComparison.OrdinalIgnoreCase))
        {
            throw new ExtractionException(ExtractionFailureKind.Unknown, "The staging folder must be on the same volume as the destination.");
        }

        var area = new StagingArea(Path.Combine(fullRoot, FolderPrefix + FolderId(firstVolume, fullDestination)));
        try
        {
            if (Directory.Exists(area.Folder) && !await RecoverAndRemoveAsync(area.Folder))
            {
                throw new ExtractionException(ExtractionFailureKind.Permission, "An earlier staging folder could not be removed. A file in it may still be open.");
            }

            cancellationToken.ThrowIfCancellationRequested();
            Directory.CreateDirectory(area.PayloadFolder);
            var marker = JsonSerializer.Serialize(new Marker(Path.GetFullPath(firstVolume), fullDestination));
            await File.WriteAllTextAsync(Path.Combine(area.Folder, MarkerName), marker, Encoding.UTF8, cancellationToken);
            return area;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            throw ExtractionSafety.MapIoFailure(exception, "preparing the staging folder");
        }
    }

    /// <summary>Finds staging folders under a root. Anything found was not finished.</summary>
    public static IReadOnlyList<StagingLeftover> FindLeftovers(string stagingRoot)
    {
        if (!Directory.Exists(stagingRoot)) return [];
        return Directory.EnumerateDirectories(stagingRoot, FolderPrefix + "*")
            .Select(folder =>
            {
                var marker = ReadMarker(folder);
                return new StagingLeftover(folder, marker?.FirstVolume, marker?.Destination);
            })
            .ToList();
    }

    /// <summary>
    /// Undoes an attempt a crash interrupted: removes the files it had already moved into the
    /// destination and puts back any original it had displaced, then deletes the staging folder.
    /// Without this the next attempt would meet its own earlier output as a conflict, and under
    /// Rename would write everything twice. Returns false when it could not all be undone.
    /// </summary>
    public static async Task<bool> RecoverAndRemoveAsync(string folder)
    {
        var displaced = Path.Combine(folder, DisplacedName);
        var marker = ReadMarker(folder);
        if (marker?.Destination is { } placedInto && !UndoPlacements(folder, placedInto)) return false;

        if (Directory.Exists(displaced) && marker?.Destination is { } destination)
        {
            var unrestored = false;
            foreach (var file in Directory.EnumerateFiles(displaced, "*", SearchOption.AllDirectories))
            {
                var target = Path.Combine(destination, Path.GetRelativePath(displaced, file));
                if (File.Exists(target)) continue;
                try
                {
                    Directory.CreateDirectory(Path.GetDirectoryName(target)!);
                    File.Move(file, target);
                }
                catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
                {
                    unrestored = true;
                }
            }

            if (unrestored) return false;
        }

        return await DeleteTreeAsync(folder);
    }

    /// <summary>
    /// Deletes a staging folder without undoing anything, for an attempt the caller has recorded
    /// as finished: its files are the result now. Returns false when it could not all be removed.
    /// </summary>
    public static Task<bool> DiscardFinishedAsync(string folder) => DeleteTreeAsync(folder);

    /// <summary>Removes this attempt's staging. A failure leaves a folder the next attempt will find.</summary>
    internal async Task RemoveAsync()
    {
        if (!KeepForRecovery) await DeleteTreeAsync(Folder);
    }

    internal PlacementLog OpenPlacementLog() => new(Path.Combine(Folder, PlacementLogName));

    /// <summary>
    /// A move is a rename on one volume, so a source no longer in the payload proves this attempt
    /// moved it, which makes the log exact even for the entry being written when a crash came.
    /// </summary>
    private static bool UndoPlacements(string folder, string destination)
    {
        var logFile = Path.Combine(folder, PlacementLogName);
        if (!File.Exists(logFile)) return true;

        var root = Path.TrimEndingDirectorySeparator(destination) + Path.DirectorySeparatorChar;
        var undone = true;
        try
        {
            foreach (var entry in PlacementLog.Read(logFile).Reverse())
            {
                var target = Path.GetFullPath(entry.Target);
                if (!target.StartsWith(root, StringComparison.OrdinalIgnoreCase)) continue;
                if (File.Exists(Path.Combine(folder, PayloadName, entry.Source)) || !File.Exists(target)) continue;

                // An overwrite whose original is no longer displaced was already rolled back, so
                // what stands at the target now is that original.
                if (entry.DisplacedAs is { } displacedAs && !File.Exists(Path.Combine(folder, DisplacedName, displacedAs))) continue;

                try
                {
                    File.SetAttributes(target, FileAttributes.Normal);
                    File.Delete(target);
                }
                catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
                {
                    undone = false;
                }
            }
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            return false;
        }

        return undone;
    }

    private static async Task<bool> DeleteTreeAsync(string folder)
    {
        // Retries because a killed process and antivirus can hold a handle for a moment.
        for (var attempt = 0; attempt < 6; attempt++)
        {
            try
            {
                if (!Directory.Exists(folder)) return true;
                ClearReadOnly(folder);
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

    private static Marker? ReadMarker(string folder)
    {
        try
        {
            return JsonSerializer.Deserialize<Marker>(File.ReadAllText(Path.Combine(folder, MarkerName), Encoding.UTF8));
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or JsonException)
        {
            return null;
        }
    }

    private static string FolderId(string firstVolume, string destination)
    {
        var key = (Path.GetFullPath(firstVolume) + "\n" + destination).ToUpperInvariant();
        return Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(key)))[..16];
    }

    private sealed record Marker(string FirstVolume, string Destination);
}

/// <param name="Source">Relative to the payload folder.</param>
/// <param name="Target">The full path the file is moved to.</param>
/// <param name="DisplacedAs">Relative to the displaced folder, when this replaced an existing file.</param>
internal sealed record PlacementEntry(string Source, string Target, string? DisplacedAs);

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

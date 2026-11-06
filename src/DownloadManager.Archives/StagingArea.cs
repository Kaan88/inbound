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
    /// Puts back any original file that a crash left in the displaced folder, then deletes the
    /// staging folder. Returns false when it could not all be removed.
    /// </summary>
    public static async Task<bool> RecoverAndRemoveAsync(string folder)
    {
        var displaced = Path.Combine(folder, DisplacedName);
        var marker = ReadMarker(folder);
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

    /// <summary>Removes this attempt's staging. A failure leaves a folder the next attempt will find.</summary>
    internal async Task RemoveAsync()
    {
        if (!KeepForRecovery) await DeleteTreeAsync(Folder);
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

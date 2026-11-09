using DownloadManager.Contracts;

namespace DownloadManager.Desktop.WinUI;

/// <summary>What the Extraction page holds, as plain values.</summary>
internal sealed record ArchiveFormValues(
    bool AutoExtract,
    int DestinationIndex,
    string Folder,
    bool PreserveFolders,
    int ConflictIndex,
    bool DeleteArchives);

/// <summary>Between the Extraction page's choices and the Host's words for them.</summary>
internal static class ArchiveForm
{
    public const int DestinationFolderIndex = 2;

    private static readonly string[] Destinations = ["nextToArchive", "subfolder", "folder"];
    private static readonly string[] Conflicts = ["rename", "overwrite", "skip"];

    /// <summary>Same order as the choices on the page.</summary>
    public static readonly string[] DestinationLabels =
    [
        "The download folder, next to the archive",
        "A subfolder named after the archive",
        "This folder:",
    ];

    /// <summary>Same order as the choices on the page.</summary>
    public static readonly string[] ConflictLabels =
    [
        "Keep both: give the new file another name",
        "Overwrite the existing file",
        "Keep the existing file and skip the new one",
    ];

    public static ArchiveFormValues FromSettings(ArchiveSettingsDto settings) => new(
        settings.AutoExtract,
        Index(Destinations, settings.Destination),
        settings.Folder ?? string.Empty,
        settings.PreserveFolders,
        Index(Conflicts, settings.ConflictPolicy),
        settings.DeleteArchives);

    /// <summary>
    /// What changed, or null when nothing did. Only changes are sent: a setting nobody touched stays
    /// as it is on the Host, and turning auto-extract on starts counting from when it really went on.
    /// </summary>
    public static ArchiveSettingsUpdate? Changes(ArchiveSettingsDto saved, ArchiveFormValues form, out string? error, PathStyle pathStyle = PathStyle.ThisPc)
    {
        error = null;
        var folder = form.Folder.Trim();
        var destination = Destinations[Math.Clamp(form.DestinationIndex, 0, Destinations.Length - 1)];
        var conflict = Conflicts[Math.Clamp(form.ConflictIndex, 0, Conflicts.Length - 1)];
        var savedFolder = saved.Folder?.Trim() ?? string.Empty;

        if (form.DestinationIndex == DestinationFolderIndex)
        {
            if (folder.Length == 0)
            {
                error = "Choose the folder to extract to.";
                return null;
            }

            // The folder is on the Host whose settings these are: a Linux server's is /downloads/extracted.
            if (!ServerPaths.IsFull(folder, pathStyle))
            {
                error = $"The extraction folder must be {ServerPaths.FullFolderWords(pathStyle)}.";
                return null;
            }
        }

        var update = new ArchiveSettingsUpdate();
        var changed = false;
        if (form.AutoExtract != saved.AutoExtract)
        {
            update.AutoExtract = form.AutoExtract;
            changed = true;
        }

        if (!string.Equals(destination, saved.Destination, StringComparison.Ordinal))
        {
            update.Destination = destination;
            changed = true;
        }

        // While another destination is chosen this box is disabled, and it keeps what it had.
        if (form.DestinationIndex == DestinationFolderIndex && !string.Equals(folder, savedFolder, StringComparison.OrdinalIgnoreCase))
        {
            update.Folder = folder;
            changed = true;
        }

        if (form.PreserveFolders != saved.PreserveFolders)
        {
            update.PreserveFolders = form.PreserveFolders;
            changed = true;
        }

        if (!string.Equals(conflict, saved.ConflictPolicy, StringComparison.Ordinal))
        {
            update.ConflictPolicy = conflict;
            changed = true;
        }

        if (form.DeleteArchives != saved.DeleteArchives)
        {
            update.DeleteArchives = form.DeleteArchives;
            changed = true;
        }

        return changed ? update : null;
    }

    private static int Index(string[] words, string? word)
    {
        var index = Array.FindIndex(words, candidate => string.Equals(candidate, word, StringComparison.OrdinalIgnoreCase));
        return index < 0 ? 0 : index;
    }
}

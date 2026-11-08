using DownloadManager.Archives;
using DownloadManager.Contracts;
using DownloadManager.Core;

namespace DownloadManager.Runtime.Ipc;

/// <summary>
/// <c>folders.browse</c> and <c>folders.create</c> for a Host whose clients can't show a folder dialog on its disk. Only
/// folders under the allowed roots are reachable. Every path is resolved first, links and <c>..</c> included, the way
/// the file system itself would (as <c>realpath</c> does), and the root check and the listing use that resolved path,
/// so a link inside a root that points outside it leads nowhere. Files are never listed.
/// </summary>
public sealed class FolderBrowser
{
    /// <summary>A listing carries at most this many folders, so one huge folder can't make a frame too large.</summary>
    public const int MaxFolders = 2000;

    private const int MaxNameBytes = 255;

    private const string Unreadable = "That folder can't be opened.";

    private readonly PathRoots _roots;

    public FolderBrowser(IEnumerable<string> roots)
        : this(new PathRoots(roots))
    {
    }

    public FolderBrowser(PathRoots roots) => _roots = roots;

    /// <summary>The allowed roots, resolved. A root that doesn't exist yet stays, and listing it says so.</summary>
    public IReadOnlyList<string> Roots => _roots.Roots;

    /// <inheritdoc cref="PathRoots.Parse"/>
    public static List<string> ParseRoots(string? value, string fallback) => PathRoots.Parse(value, fallback);

    public FolderBrowseResult Browse(string? path)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            return new FolderBrowseResult
            {
                Ok = true,
                Roots = Roots.ToList(),
                Folders = Roots.Select(root => new FolderEntryDto { Name = root, Path = root }).ToList(),
            };
        }

        if (Allowed(path) is not { } real) return Refused(path);
        if (!Directory.Exists(real)) return Failed("That folder doesn't exist.");

        // Kept sorted while listing and cut at MaxFolders, so a truncated listing really holds the first ones by name
        // (as the contract says), not whichever the file system happened to return first.
        var folders = new SortedSet<FolderEntryDto>(ByName);
        var truncated = false;
        try
        {
            // Hidden folders and ones that can't be read are left out, as a file dialog would. Hidden is the attribute on
            // Windows and a leading dot on Linux; .NET reports a dot name as Hidden there too, but the name check below
            // doesn't rely on that.
            var options = new EnumerationOptions
            {
                IgnoreInaccessible = true,
                RecurseSubdirectories = false,
                AttributesToSkip = FileAttributes.Hidden | FileAttributes.System,
            };
            foreach (var directory in new DirectoryInfo(real).EnumerateDirectories("*", options))
            {
                if (!OperatingSystem.IsWindows() && directory.Name.StartsWith('.')) continue;
                // A link is listed only when it stays inside the roots: anything else could not be opened anyway.
                if (directory.LinkTarget is not null && Allowed(directory.FullName) is null) continue;
                folders.Add(new FolderEntryDto { Name = directory.Name, Path = directory.FullName });
                if (folders.Count > MaxFolders)
                {
                    truncated = true;
                    folders.Remove(folders.Max!);
                }
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Security.SecurityException)
        {
            return Failed(Unreadable);
        }

        var space = FolderSpaceMeter.Measure(real);
        return new FolderBrowseResult
        {
            Ok = true,
            Path = real,
            Parent = _roots.IsRoot(real) ? null : Path.GetDirectoryName(real),
            Roots = Roots.ToList(),
            Folders = folders.ToList(),
            Truncated = truncated,
            FreeBytes = space.FreeBytes,
            TotalBytes = space.TotalBytes,
        };
    }

    public FolderBrowseResult Create(string? parent, string? name)
    {
        if (DescribeBadName(name) is { } problem) return Failed(problem);
        if (string.IsNullOrWhiteSpace(parent) || Allowed(parent) is not { } real) return Refused(parent);
        if (!Directory.Exists(real)) return Failed("That folder doesn't exist.");

        var target = Path.Combine(real, name!);
        if (Directory.Exists(target) || File.Exists(target)) return Failed("Something with that name is already there.");
        try
        {
            Directory.CreateDirectory(target);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return Failed($"The folder could not be created: {ex.GetType().Name}.");
        }

        return Browse(target);
    }

    /// <summary>The resolved path when it is inside a root, otherwise null.</summary>
    public string? Allowed(string path) => _roots.Allowed(path);

    /// <inheritdoc cref="PathRoots.Resolve"/>
    public static string? Resolve(string path) => PathRoots.Resolve(path);

    /// <summary>Case-insensitive first, so "audio" sits next to "Audio"; then exact, so two such names keep one order.</summary>
    private static readonly Comparer<FolderEntryDto> ByName = Comparer<FolderEntryDto>.Create((left, right) =>
        StringComparer.OrdinalIgnoreCase.Compare(left.Name, right.Name) is var order and not 0
            ? order
            : StringComparer.Ordinal.Compare(left.Name, right.Name));

    private static string? DescribeBadName(string? name)
    {
        if (string.IsNullOrWhiteSpace(name)) return "Give the folder a name.";
        if (name.Contains('/') || name.Contains('\\')) return "A folder name can't contain / or \\.";
        if (System.Text.Encoding.UTF8.GetByteCount(name) > MaxNameBytes) return "That name is too long.";
        if (name != name.Trim()) return "A folder name can't start or end with a space.";
        // The same rules an extracted name meets: no .., no control characters, nothing Windows can't keep (a share).
        return ExtractionSafety.DescribeUnsafePath(name) is { } reason ? $"That name can't be used ({reason})." : null;
    }

    private FolderBrowseResult Refused(string? path) =>
        Failed(string.IsNullOrWhiteSpace(path) || !Path.IsPathFullyQualified(path) ? "Give the folder's full path." : PathRoots.OutsideMessage);

    private FolderBrowseResult Failed(string error) => new() { Error = error, Roots = Roots.ToList() };
}

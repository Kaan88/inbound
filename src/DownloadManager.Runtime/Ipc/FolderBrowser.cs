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

    private const int MaxLinkHops = 40;
    private const int MaxNameBytes = 255;

    private const string Outside = "That folder is outside the folders this server may use.";
    private const string Unreadable = "That folder can't be opened.";

    public FolderBrowser(IEnumerable<string> roots)
    {
        Roots = roots
            .Where(root => !string.IsNullOrWhiteSpace(root))
            .Select(root => Resolve(root.Trim()))
            .OfType<string>()
            .Distinct(PathNames.Comparer)
            .ToList();
    }

    /// <summary>The allowed roots, resolved. A root that doesn't exist yet stays, and listing it says so.</summary>
    public IReadOnlyList<string> Roots { get; }

    /// <summary>
    /// <c>DOWNLOADMANAGER_ROOTS</c>: folders separated by <c>;</c>, or also by <c>:</c> off Windows, where a drive letter
    /// can't be confused with one. Nothing given means <paramref name="fallback"/>.
    /// </summary>
    public static List<string> ParseRoots(string? value, string fallback)
    {
        var separators = OperatingSystem.IsWindows() ? new[] { ';' } : new[] { ';', ':' };
        var roots = (value ?? string.Empty).Split(separators, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).ToList();
        return roots.Count > 0 ? roots : [fallback];
    }

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

        var folders = new List<FolderEntryDto>();
        var truncated = false;
        try
        {
            // Hidden folders (a leading dot on Linux) and ones that can't be read are left out, as a file dialog would.
            var options = new EnumerationOptions { IgnoreInaccessible = true, RecurseSubdirectories = false };
            foreach (var directory in new DirectoryInfo(real).EnumerateDirectories("*", options))
            {
                // A link is listed only when it stays inside the roots: anything else could not be opened anyway.
                if (directory.LinkTarget is not null && Allowed(directory.FullName) is null) continue;
                if (folders.Count == MaxFolders)
                {
                    truncated = true;
                    break;
                }

                folders.Add(new FolderEntryDto { Name = directory.Name, Path = directory.FullName });
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Security.SecurityException)
        {
            return Failed(Unreadable);
        }

        folders.Sort((left, right) => StringComparer.OrdinalIgnoreCase.Compare(left.Name, right.Name) is var order and not 0
            ? order
            : StringComparer.Ordinal.Compare(left.Name, right.Name));
        var space = FolderSpaceMeter.Measure(real);
        return new FolderBrowseResult
        {
            Ok = true,
            Path = real,
            Parent = Roots.Any(root => PathNames.Same(root, real)) ? null : Path.GetDirectoryName(real),
            Roots = Roots.ToList(),
            Folders = folders,
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
    public string? Allowed(string path)
    {
        var real = Resolve(path);
        return real is not null && Roots.Any(root => Inside(real, root)) ? real : null;
    }

    private static bool Inside(string real, string root) =>
        PathNames.Same(real, root) || real.StartsWith(Path.EndsInDirectorySeparator(root) ? root : root + Path.DirectorySeparatorChar, PathNames.Comparison);

    /// <summary>
    /// The path as the file system reaches it: each part in turn, a link replaced by its target and walked again, and
    /// <c>..</c> taken from where the walk really is, not from the text. Parts that don't exist are kept as written.
    /// Null for a relative path or a loop of links.
    /// </summary>
    public static string? Resolve(string path)
    {
        try
        {
            if (!Path.IsPathFullyQualified(path)) return null;
            var current = Path.GetPathRoot(path)!;
            var pending = new Stack<string>();
            Push(pending, path[current.Length..]);
            var hops = 0;
            while (pending.Count > 0)
            {
                var part = pending.Pop();
                if (part is "" or ".") continue;
                if (part == "..")
                {
                    current = Path.GetDirectoryName(current) ?? current;
                    continue;
                }

                var next = Path.Combine(current, part);
                var target = new FileInfo(next).LinkTarget;
                if (target is null)
                {
                    current = next;
                    continue;
                }

                if (++hops > MaxLinkHops) return null;
                if (Path.IsPathFullyQualified(target))
                {
                    current = Path.GetPathRoot(target)!;
                    target = target[current.Length..];
                }

                Push(pending, target);
            }

            return Path.TrimEndingDirectorySeparator(current);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
        {
            return null;
        }
    }

    private static void Push(Stack<string> pending, string relative)
    {
        var parts = relative.Split(OperatingSystem.IsWindows() ? ['\\', '/'] : ['/']);
        for (var index = parts.Length - 1; index >= 0; index--) pending.Push(parts[index]);
    }

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
        Failed(string.IsNullOrWhiteSpace(path) || !Path.IsPathFullyQualified(path) ? "Give the folder's full path." : Outside);

    private FolderBrowseResult Failed(string error) => new() { Error = error, Roots = Roots.ToList() };
}

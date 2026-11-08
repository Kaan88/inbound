using DownloadManager.Core;

namespace DownloadManager.Runtime;

/// <summary>
/// The folders a Host without a desktop (the server) may read from and write to on behalf of its clients
/// (<c>DOWNLOADMANAGER_ROOTS</c>). Every path is resolved first, links and <c>..</c> included, the way the file system
/// itself would (as <c>realpath</c> does), and the root check uses that resolved path, so a link inside a root that
/// points outside it leads nowhere. The desktop Host has none: its client is the signed-in user, who may use any folder.
/// </summary>
public sealed class PathRoots
{
    private const int MaxLinkHops = 40;

    public const string OutsideMessage = "That folder is outside the folders this server may use.";

    public PathRoots(IEnumerable<string> roots)
    {
        Roots = roots
            .Where(root => !string.IsNullOrWhiteSpace(root))
            .Select(root => Resolve(root.Trim()))
            .OfType<string>()
            .Distinct(PathNames.Comparer)
            .ToList();
    }

    /// <summary>The allowed roots, resolved. A root that doesn't exist yet stays.</summary>
    public IReadOnlyList<string> Roots { get; }

    /// <summary>
    /// <c>DOWNLOADMANAGER_ROOTS</c>: folders separated by <c>;</c>, or also by <c>:</c> off Windows, where a drive letter
    /// can't be confused with one. Nothing given means <paramref name="fallback"/>.
    /// </summary>
    public static List<string> Parse(string? value, string fallback)
    {
        var separators = OperatingSystem.IsWindows() ? new[] { ';' } : new[] { ';', ':' };
        var roots = (value ?? string.Empty).Split(separators, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).ToList();
        return roots.Count > 0 ? roots : [fallback];
    }

    /// <summary>The resolved path when it is inside a root (or is one), otherwise null. Null for a relative path too.</summary>
    public string? Allowed(string? path)
    {
        if (string.IsNullOrWhiteSpace(path)) return null;
        var real = Resolve(path.Trim());
        return real is not null && Roots.Any(root => Inside(real, root)) ? real : null;
    }

    public bool IsRoot(string real) => Roots.Any(root => PathNames.Same(root, real));

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
}

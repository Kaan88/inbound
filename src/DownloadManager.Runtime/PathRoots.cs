using DownloadManager.Core;

namespace DownloadManager.Runtime;

/// <summary>
/// The folders a client that is not this machine's user may have the Host read from and write to: the server's
/// <c>DOWNLOADMANAGER_ROOTS</c>, or on the desktop the app's own folders for a browser on another device (D8). Every
/// path is resolved first, links and <c>..</c> included, the way the file system itself would (as <c>realpath</c>
/// does), and the root check uses that resolved path, so a link inside a root that points outside it leads nowhere.
/// On Windows a path is checked as text before anything looks at it (<see cref="DescribeUnsafeText"/>): asking about a
/// network path would already send this Windows account's credentials there.
/// </summary>
public sealed class PathRoots
{
    private const int MaxLinkHops = 40;

    private const string ServerFolders = "the folders this server may use";

    public const string OutsideMessage = "That folder is outside " + ServerFolders + ".";

    private static readonly string[] DeviceNames =
    [
        "CON", "PRN", "AUX", "NUL", "CONIN$", "CONOUT$", "CLOCK$",
        "COM0", "COM1", "COM2", "COM3", "COM4", "COM5", "COM6", "COM7", "COM8", "COM9", "COM\u00b9", "COM\u00b2", "COM\u00b3",
        "LPT0", "LPT1", "LPT2", "LPT3", "LPT4", "LPT5", "LPT6", "LPT7", "LPT8", "LPT9", "LPT\u00b9", "LPT\u00b2", "LPT\u00b3",
    ];

    private readonly Func<string, string?> _linkTarget;
    private readonly Func<string, string?>? _refuseRoot;
    private readonly Func<IEnumerable<string>>? _source;
    private readonly Resolution _fixed = new([], []);
    private readonly Lock _cacheGate = new();
    private (string[] Given, Resolution Resolved)? _cache;

    /// <summary>The roots in use, and those given that can't be, each with the reason.</summary>
    private sealed record Resolution(IReadOnlyList<string> Roots, IReadOnlyList<(string Root, string Reason)> Dropped);

    /// <param name="linkTarget">
    /// Reads where a link points, or null when the path is no link. The file system's own by default; a test passes one
    /// that records what was asked.
    /// </param>
    /// <param name="refuseRoot">Why a resolved root can't be one after all, or null when it can.</param>
    public PathRoots(IEnumerable<string> roots, Func<string, string?>? linkTarget = null, Func<string, string?>? refuseRoot = null)
    {
        _linkTarget = linkTarget ?? ReadLinkTarget;
        _refuseRoot = refuseRoot;
        _fixed = ResolveRoots(Given(roots));
    }

    private PathRoots(Func<IEnumerable<string>> source, Func<string, string?>? linkTarget, string called, Func<string, string?>? refuseRoot)
    {
        _linkTarget = linkTarget ?? ReadLinkTarget;
        _refuseRoot = refuseRoot;
        _source = source;
        Called = called;
    }

    /// <summary>
    /// Roots worked out again from <paramref name="source"/> whenever they are asked for, such as the app's folders from
    /// the current settings. Resolved again only when the folders it gives change.
    /// </summary>
    /// <param name="called">What these folders are called in a refusal (<see cref="Called"/>).</param>
    /// <param name="refuseRoot">Why a resolved root can't be one after all, or null when it can.</param>
    public static PathRoots Live(Func<IEnumerable<string>> source, string called, Func<string, string?>? linkTarget = null,
        Func<string, string?>? refuseRoot = null)
        => new(source, linkTarget, called, refuseRoot);

    /// <summary>What these folders are called in a reply that refuses a path: "the folders this server may use" unless set.</summary>
    public string Called { get; init; } = ServerFolders;

    /// <summary>The words for a folder outside these roots.</summary>
    public string OutsideText => $"That folder is outside {Called}.";

    /// <summary>The words for a file outside these roots; <paramref name="kind"/> names it, such as ".torrent file".</summary>
    public string OutsideFileText(string kind = "file") => $"That {kind} is outside {Called}.";

    /// <summary>
    /// The allowed roots, resolved. A root that doesn't exist yet stays; one that fails the text check, or that the
    /// <c>refuseRoot</c> given refuses, doesn't (<see cref="OutsideTextFor"/> says why).
    /// </summary>
    public IReadOnlyList<string> Roots => Current().Roots;

    private Resolution Current()
    {
        if (_source is null) return _fixed;
        var given = Given(_source()).ToArray();
        lock (_cacheGate)
        {
            if (_cache is { } cached && cached.Given.SequenceEqual(given, StringComparer.Ordinal)) return cached.Resolved;
        }

        var resolved = ResolveRoots(given);
        lock (_cacheGate) _cache = (given, resolved);
        return resolved;
    }

    private static IEnumerable<string> Given(IEnumerable<string> roots)
        => roots.Where(root => !string.IsNullOrWhiteSpace(root)).Select(root => root.Trim()).Distinct(StringComparer.Ordinal);

    private Resolution ResolveRoots(IEnumerable<string> roots)
    {
        var resolved = new List<string>();
        var dropped = new List<(string Root, string Reason)>();
        foreach (var root in roots)
        {
            var real = Real(root);
            var reason = real is null ? DescribeUnsafeText(root) ?? string.Empty : _refuseRoot?.Invoke(real);
            if (reason is null)
            {
                if (!resolved.Contains(real!, PathNames.Comparer)) resolved.Add(real!);
            }
            else dropped.Add((TextOnlyFullPath(root) ?? root, reason));
        }

        return new Resolution(resolved, dropped);
    }

    /// <summary>
    /// The words for a path that isn't inside these roots. When it is in one of the folders given that can't be used
    /// (a network or removable drive, or one <c>refuseRoot</c> refused), they say so, since that folder is still one
    /// of the user's own. Text only.
    /// </summary>
    public string OutsideTextFor(string? path)
    {
        var dropped = Current().Dropped;
        if (dropped.Count == 0 || TextOnlyFullPath(path) is not { } full) return OutsideText;
        foreach (var (root, reason) in dropped)
        {
            if (IsInside(full, Path.TrimEndingDirectorySeparator(root)))
                return string.IsNullOrEmpty(reason) ? DroppedText : $"{DroppedText} {reason}";
        }

        return OutsideText;
    }

    private const string DroppedText = "That folder can't be used from another device.";

    /// <summary><see cref="Path.GetFullPath(string)"/>, which works on the text alone, or null for a relative path or one it refuses.</summary>
    private static string? TextOnlyFullPath(string? path)
    {
        if (string.IsNullOrWhiteSpace(path) || !Path.IsPathFullyQualified(path.Trim())) return null;
        try
        {
            return Path.GetFullPath(path.Trim());
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
        {
            return null;
        }
    }

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

    /// <summary>
    /// The resolved path when it is inside a root (or is one), otherwise null. Null for a relative path too, and on
    /// Windows for any path <see cref="DescribeUnsafeText"/> refuses, before the file system is asked anything.
    /// </summary>
    public string? Allowed(string? path)
    {
        if (string.IsNullOrWhiteSpace(path)) return null;
        var real = Real(path.Trim());
        if (real is null) return null;
        var roots = Roots;
        return roots.Any(root => IsInside(real, root)) ? real : null;
    }

    public bool IsRoot(string real) => Roots.Any(root => PathNames.Same(root, real));

    /// <summary>Whether <paramref name="real"/> is <paramref name="root"/> or inside it, by text.</summary>
    public static bool IsInside(string real, string root) =>
        PathNames.Same(real, root) || real.StartsWith(Path.EndsInDirectorySeparator(root) ? root : root + Path.DirectorySeparatorChar, PathNames.Comparison);

    /// <summary>The text check, then (on Windows) <see cref="Path.GetFullPath(string)"/>, then the walk.</summary>
    private string? Real(string path)
    {
        if (OperatingSystem.IsWindows())
        {
            if (DescribeUnsafeText(path) is not null) return null;
            try
            {
                path = Path.GetFullPath(path);
            }
            catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
            {
                return null;
            }

            if (DescribeUnsafeText(path) is not null) return null;
        }

        return Resolve(path, _linkTarget);
    }

    /// <summary>
    /// Why this Windows path can't be used by a client that isn't this machine's user, or null when it can. Text only,
    /// apart from asking Windows what kind of drive the letter is, which goes nowhere: a path to another computer
    /// (<c>\\server\share</c>), the <c>\\?\</c>, <c>\\.\</c> and <c>\??\</c> forms, a relative path, a device name
    /// (<c>CON</c>, <c>NUL</c>, <c>COM1</c>...), an alternate data stream (a <c>:</c> after the drive), wildcards, and a
    /// part that ends in a dot or a space (Windows would quietly drop those, so the folder used would not be the one
    /// checked). Only a drive letter on a fixed drive of this PC passes, not a mapped network drive or a removable one.
    /// Always null off Windows.
    /// </summary>
    public static string? DescribeUnsafeText(string? path)
    {
        if (!OperatingSystem.IsWindows()) return null;
        if (string.IsNullOrEmpty(path)) return "Give the full path.";
        if (path.Length < 3 || !char.IsAsciiLetter(path[0]) || path[1] != ':' || path[2] is not ('\\' or '/'))
            return "Give the full path, starting with a drive letter such as C:\\.";
        if (path.Any(char.IsControl) || path.IndexOfAny(['<', '>', '"', '|', '?', '*']) >= 0)
            return "The path has a character Windows doesn't allow there.";
        if (path.IndexOf(':', 2) >= 0) return "The path has a colon after the drive.";
        foreach (var part in path[3..].Split('\\', '/'))
        {
            if (part is "" or "." or "..") continue;
            if (part.EndsWith('.') || part.EndsWith(' ')) return "A folder or file name in the path ends in a dot or a space.";
            var stem = part.Split('.')[0].TrimEnd(' ');
            if (DeviceNames.Contains(stem, StringComparer.OrdinalIgnoreCase)) return $"{stem} is a device name Windows reserves.";
        }

        try
        {
            if (new DriveInfo(path[..1]).DriveType != DriveType.Fixed) return "Only folders on this PC's own fixed drives can be used.";
        }
        catch (Exception ex) when (ex is ArgumentException or IOException or UnauthorizedAccessException)
        {
            return "Only folders on this PC's own fixed drives can be used.";
        }

        return null;
    }

    /// <inheritdoc cref="Resolve(string, Func{string, string?})"/>
    public static string? Resolve(string path) => Resolve(path, ReadLinkTarget);

    /// <summary>
    /// The path as the file system reaches it: each part in turn, a link replaced by its target and walked again, and
    /// <c>..</c> taken from where the walk really is, not from the text. Parts that don't exist are kept as written.
    /// Null for a relative path or a loop of links.
    /// </summary>
    public static string? Resolve(string path, Func<string, string?> linkTarget)
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
                var target = linkTarget(next);
                if (target is null)
                {
                    current = next;
                    continue;
                }

                if (++hops > MaxLinkHops) return null;
                if (OperatingSystem.IsWindows())
                {
                    // A junction can report its target as \??\C:\... A link to another computer is not followed: the
                    // next step would ask that computer about it.
                    if (target.Length > 6 && (target.StartsWith(@"\??\", StringComparison.Ordinal) || target.StartsWith(@"\\?\", StringComparison.Ordinal)) &&
                        char.IsAsciiLetter(target[4]) && target[5] == ':')
                        target = target[4..];
                    if (target.Length >= 2 && target[0] is '\\' or '/' && target[1] is '\\' or '/' or '?') return null;
                    // Nor one to a mapped network drive or a removable one: the next step would look there.
                    if (Path.IsPathFullyQualified(target) && DescribeUnsafeText(target) is not null) return null;
                    // L17: rooted but not fully qualified. "C:foo" is relative to that drive's current folder, which
                    // this process can't know the file system's view of, so it leads nowhere. "\foo" starts at the
                    // root of the drive the link is on, not in the link's folder.
                    if (!Path.IsPathFullyQualified(target) && Path.IsPathRooted(target))
                    {
                        if (target.Length >= 2 && target[1] == ':') return null;
                        current = Path.GetPathRoot(current)!;
                        target = target.TrimStart('\\', '/');
                    }
                }

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

    private static string? ReadLinkTarget(string path) => new FileInfo(path).LinkTarget;

    private static void Push(Stack<string> pending, string relative)
    {
        var parts = relative.Split(OperatingSystem.IsWindows() ? ['\\', '/'] : ['/']);
        for (var index = parts.Length - 1; index >= 0; index--) pending.Push(parts[index]);
    }
}

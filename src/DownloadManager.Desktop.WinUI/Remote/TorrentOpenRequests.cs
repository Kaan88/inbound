namespace DownloadManager.Desktop.WinUI;

/// <summary>
/// T1 step 6b: Windows starts the window exe with <c>--open-torrent "&lt;magnet or .torrent path&gt;"</c> when the
/// user opens a magnet link or a .torrent file and Inbound is the handler. The launch writes the input into a small
/// queue folder in the data folder, then the running window (signalled through its activation event) or this same
/// launch's window takes it from there and opens the torrent dialog. One file per request, written to a temporary
/// name and renamed, so a reader never sees half of one.
/// </summary>
internal static class TorrentOpenRequests
{
    public const string Argument = "--open-torrent";

    /// <summary>A request older than this is from a launch whose window never took it; it is dropped unopened.</summary>
    public static readonly TimeSpan MaxAge = TimeSpan.FromMinutes(2);

    private const string Extension = ".request";

    /// <summary>The magnet or the .torrent file's full path after <see cref="Argument"/>, or null when there is none.</summary>
    public static string? Parse(IReadOnlyList<string> args)
    {
        for (var i = 0; i < args.Count - 1; i++)
        {
            if (!string.Equals(args[i], Argument, StringComparison.OrdinalIgnoreCase)) continue;
            var value = FromFileUri(args[i + 1].Trim().Trim('"').Trim());
            return IsAccepted(value) ? value : null;
        }

        return null;
    }

    /// <summary>
    /// A Linux desktop may hand a local file to <c>%u</c> as a <c>file://</c> address (T4); that becomes its path.
    /// Anything else is returned as it is.
    /// </summary>
    public static string FromFileUri(string value)
    {
        if (!value.StartsWith("file://", StringComparison.OrdinalIgnoreCase)) return value;
        return Uri.TryCreate(value, UriKind.Absolute, out var uri) && uri.IsFile && string.IsNullOrEmpty(uri.Host)
            ? uri.LocalPath
            : value;
    }

    /// <summary>A magnet, or the full path of a .torrent file.</summary>
    public static bool IsAccepted(string? value)
    {
        if (string.IsNullOrWhiteSpace(value) || value.Length > 64 * 1024) return false;
        if (value.StartsWith("magnet:?", StringComparison.OrdinalIgnoreCase)) return true;
        return value.EndsWith(".torrent", StringComparison.OrdinalIgnoreCase) &&
               !value.Contains("://", StringComparison.Ordinal) &&
               value.IndexOfAny(Path.GetInvalidPathChars()) < 0 &&
               Path.IsPathFullyQualified(value);
    }

    public static string QueueDirectory(string dataRoot) => Path.Combine(dataRoot, "open-requests");

    public static void Enqueue(string directory, string input, DateTimeOffset now)
    {
        Directory.CreateDirectory(directory);
        var name = $"{now.UtcTicks:D20}-{Guid.NewGuid():N}";
        var temp = Path.Combine(directory, name + ".tmp");
        File.WriteAllText(temp, input);
        File.Move(temp, Path.Combine(directory, name + Extension));
    }

    /// <summary>
    /// Takes every waiting request, oldest first, and deletes it. Requests older than <see cref="MaxAge"/>, ones that
    /// are not a magnet or a .torrent path, and ones another reader took first are left out.
    /// </summary>
    public static List<string> Drain(string directory, DateTimeOffset now)
    {
        var inputs = new List<string>();
        string[] files;
        try
        {
            if (!Directory.Exists(directory)) return inputs;
            files = Directory.GetFiles(directory, "*" + Extension);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return inputs;
        }

        Array.Sort(files, StringComparer.Ordinal);
        foreach (var file in files)
        {
            string text;
            try
            {
                text = File.ReadAllText(file);
                File.Delete(file);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                // Another window took it, or it can't be read; either way not this one's.
                continue;
            }

            if (!IsFresh(Path.GetFileNameWithoutExtension(file), now)) continue;
            var value = text.Trim();
            if (IsAccepted(value) && !inputs.Contains(value, StringComparer.Ordinal)) inputs.Add(value);
        }

        return inputs;
    }

    /// <summary>Adds <paramref name="inputs"/> to the window's held list in order, each once.</summary>
    public static void Hold(List<string> held, IEnumerable<string> inputs)
    {
        foreach (var input in inputs)
            if (!held.Contains(input, StringComparer.Ordinal)) held.Add(input);
    }

    private static bool IsFresh(string name, DateTimeOffset now)
    {
        var dash = name.IndexOf('-', StringComparison.Ordinal);
        if (dash <= 0 || !long.TryParse(name.AsSpan(0, dash), out var ticks)) return false;
        if (ticks < DateTimeOffset.MinValue.UtcTicks || ticks > DateTimeOffset.MaxValue.UtcTicks) return false;
        var age = now - new DateTimeOffset(ticks, TimeSpan.Zero);
        return age <= MaxAge && age >= -MaxAge;
    }

    /// <summary>The dialog's starting focus for these inputs.</summary>
    public static string Focus(IReadOnlyList<string> inputs)
    {
        var magnets = inputs.Count(input => input.StartsWith("magnet:", StringComparison.OrdinalIgnoreCase));
        return magnets == inputs.Count ? "magnet" : magnets == 0 ? "torrentFiles" : "mixed";
    }
}

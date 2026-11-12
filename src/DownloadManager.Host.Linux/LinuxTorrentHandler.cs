using System.Text;

namespace DownloadManager.Host;

/// <summary>
/// T4: Options › Torrents › "Open magnet links and .torrent files with Inbound" on a Linux desktop, as
/// <c>TorrentHandlerRegistration</c> does it on Windows. Ticked: a desktop entry <c>inbound-torrent.desktop</c> in
/// the user's applications folder (<c>~/.local/share/applications</c>) declaring both types and opening the window with
/// <c>--open-torrent %u</c>, then <c>xdg-mime default</c> for both. Cleared: the entry goes, and our id is taken out of
/// the user's <c>mimeapps.list</c> files where it is the default; another app's entry there is left alone.
/// Every folder and the <c>xdg-mime</c> call are given, so tests never touch the real ones.
/// </summary>
public sealed class LinuxTorrentHandler(
    string applicationsDirectory,
    IReadOnlyList<string> mimeAppsLists,
    string windowCommand,
    Func<IReadOnlyList<string>, bool> runXdgMime,
    string iconName = "folder-download")
{
    public const string DesktopId = "inbound-torrent.desktop";
    public const string MagnetType = "x-scheme-handler/magnet";
    public const string TorrentType = "application/x-bittorrent";
    public static readonly string[] Types = [MagnetType, TorrentType];

    public string DesktopFilePath => Path.Combine(applicationsDirectory, DesktopId);

    public string DesktopFileText() => new StringBuilder()
        .Append("[Desktop Entry]\n")
        .Append("Type=Application\n")
        .Append("Name=Inbound Download Manager\n")
        .Append("Comment=Opens magnet links and .torrent files in Inbound\n")
        .Append("Exec=").Append(DesktopEntryText.QuoteExec(windowCommand)).Append(" --open-torrent %u\n")
        .Append("Icon=").Append(iconName).Append('\n')
        .Append("Terminal=false\n")
        .Append("NoDisplay=true\n")
        .Append("MimeType=").Append(MagnetType).Append(';').Append(TorrentType).Append(";\n")
        .ToString();

    /// <summary>
    /// Writes the entry. The defaults are set only when the entry wasn't there yet (the box was just ticked) or an
    /// earlier <c>xdg-mime default</c> didn't succeed: a later start, or a moved AppImage, rewrites the entry but leaves a
    /// default the user has since given another app. True when anything changed. Throws <see cref="IOException"/> when
    /// <c>xdg-mime</c> fails; the entry then keeps <see cref="PendingKey"/>, so the next settings change or start tries again.
    /// </summary>
    public bool Register()
    {
        var path = DesktopFilePath;
        var current = File.Exists(path) ? File.ReadAllText(path) : null;
        var pending = current is null || current.Contains(PendingKey, StringComparison.Ordinal);
        var text = DesktopFileText();
        if (!pending && string.Equals(current, text, StringComparison.Ordinal)) return false;
        Directory.CreateDirectory(applicationsDirectory);
        if (pending)
        {
            ReplaceFile(path, text + PendingKey + "=true\n");
            if (!runXdgMime(["default", DesktopId, MagnetType, TorrentType]))
                throw new IOException("xdg-mime default did not succeed.");
        }

        ReplaceFile(path, text);
        return true;
    }

    /// <summary>Marks an entry whose defaults are still to be set (an extension key, which desktops ignore).</summary>
    public const string PendingKey = "X-Inbound-Defaults-Pending";

    /// <summary>Removes the entry and our defaults. True when anything changed.</summary>
    public bool Remove()
    {
        var changed = false;
        var path = DesktopFilePath;
        if (File.Exists(path) && File.ReadAllText(path).Contains("--open-torrent", StringComparison.Ordinal))
        {
            File.Delete(path);
            changed = true;
        }

        foreach (var list in mimeAppsLists)
            changed |= RemoveFromMimeApps(list);
        return changed;
    }

    /// <summary>Takes <see cref="DesktopId"/> out of the two types' lines in one <c>mimeapps.list</c>.</summary>
    internal static bool RemoveFromMimeApps(string path)
    {
        if (!File.Exists(path)) return false;
        var lines = File.ReadAllLines(path);
        var output = new List<string>(lines.Length);
        var changed = false;
        foreach (var line in lines)
        {
            var equals = line.IndexOf('=', StringComparison.Ordinal);
            var key = equals > 0 ? line[..equals].Trim() : null;
            if (key is null || !Types.Contains(key, StringComparer.Ordinal) || line.TrimStart().StartsWith('#'))
            {
                output.Add(line);
                continue;
            }

            var ids = line[(equals + 1)..].Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
            var kept = ids.Where(id => !string.Equals(id, DesktopId, StringComparison.Ordinal)).ToArray();
            if (kept.Length == ids.Length)
            {
                output.Add(line);
                continue;
            }

            changed = true;
            if (kept.Length > 0) output.Add(key + "=" + string.Join(';', kept) + ";");
        }

        if (!changed) return false;
        ReplaceFile(path, string.Join('\n', output) + "\n");
        return true;
    }

    /// <summary>
    /// Writes a temp file next to the real file and renames it over it. A symlinked file (a dotfiles repo) is followed, so
    /// the link stays and its target is updated; the target's Unix mode is kept.
    /// </summary>
    internal static void ReplaceFile(string path, string text)
    {
        var target = File.Exists(path) ? new FileInfo(path).ResolveLinkTarget(returnFinalTarget: true)?.FullName ?? path : path;
        var temporary = target + ".tmp";
        File.WriteAllText(temporary, text);
        try
        {
            if (!OperatingSystem.IsWindows() && File.Exists(target))
                File.SetUnixFileMode(temporary, File.GetUnixFileMode(target));
            File.Move(temporary, target, overwrite: true);
        }
        catch
        {
            try { File.Delete(temporary); }
            catch (Exception cleanup) when (cleanup is IOException or UnauthorizedAccessException) { }
            throw;
        }
    }

    /// <summary>The user's applications folder: <c>$XDG_DATA_HOME/applications</c>, else <c>~/.local/share/applications</c>.</summary>
    public static string ApplicationsDirectory(string home, string? dataHome) =>
        Path.Combine(string.IsNullOrWhiteSpace(dataHome) || !Path.IsPathRooted(dataHome) ? Path.Combine(home, ".local", "share") : dataHome,
            "applications");

    /// <summary>
    /// Where <c>xdg-mime default</c> may have written: <c>$XDG_CONFIG_HOME/mimeapps.list</c> (else <c>~/.config</c>), and
    /// the older <c>mimeapps.list</c> in the applications folder.
    /// </summary>
    public static IReadOnlyList<string> MimeAppsLists(string home, string? configHome, string applicationsDirectory) =>
    [
        Path.Combine(string.IsNullOrWhiteSpace(configHome) || !Path.IsPathRooted(configHome) ? Path.Combine(home, ".config") : configHome,
            "mimeapps.list"),
        Path.Combine(applicationsDirectory, "mimeapps.list"),
    ];
}

/// <summary>Desktop entry spec text.</summary>
public static class DesktopEntryText
{
    /// <summary>The spec's quoting for one Exec argument.</summary>
    public static string QuoteExec(string argument)
    {
        var escaped = new StringBuilder();
        foreach (var c in argument)
        {
            if (c is '"' or '`' or '$' or '\\') escaped.Append('\\');
            escaped.Append(c);
        }

        // In a desktop file the backslash itself is escaped once more.
        return "\"" + escaped.ToString().Replace("\\", "\\\\", StringComparison.Ordinal) + "\"";
    }
}

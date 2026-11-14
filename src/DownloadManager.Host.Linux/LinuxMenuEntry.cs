using System.Text;

namespace DownloadManager.Host;

/// <summary>
/// X.7: an AppImage puts itself in the desktop's application menu, silently, at Host start. The entry is
/// <c>inbound.desktop</c> in the user's applications folder, pointing at the AppImage file; it is rewritten only when its
/// text would change (the file was moved or replaced by an update). A file with that name that lacks
/// <see cref="ManagedKey"/> belongs to the user or another tool (AppImageLauncher) and is never touched.
/// The icon is a copy of the app's own png kept beside our data (<c>$XDG_DATA_HOME/inbound/inbound.png</c>) and named by
/// absolute path: the png is 1024x1024, which the hicolor theme has no size folder for. Without the png the themed
/// <see cref="Fallback"/> name is used. Every folder and the database refresh are given, so tests never touch real ones.
/// </summary>
public sealed class LinuxMenuEntry(
    string applicationsDirectory,
    string iconDirectory,
    string? appImagePath,
    string? iconSource,
    Func<IReadOnlyList<string>, bool>? refreshDatabase = null,
    Action<string>? log = null)
{
    public const string FileName = "inbound.desktop";
    public const string IconFileName = "inbound.png";
    public const string ManagedKey = "X-Inbound-Managed";
    public const string Fallback = "folder-download";

    public string DesktopFilePath => Path.Combine(applicationsDirectory, FileName);
    public string InstalledIconPath => Path.Combine(iconDirectory, IconFileName);

    public string DesktopFileText(string icon) => new StringBuilder()
        .Append("[Desktop Entry]\n")
        .Append("Type=Application\n")
        .Append("Name=Inbound Download Manager\n")
        .Append("Comment=Download manager\n")
        .Append("Exec=").Append(DesktopEntryText.QuoteExec(appImagePath ?? "")).Append(" %U\n")
        .Append("Icon=").Append(icon).Append('\n')
        .Append("Terminal=false\n")
        .Append("Categories=Network;FileTransfer;\n")
        .Append("StartupWMClass=DownloadManager.Desktop.Uno\n")
        .Append(ManagedKey).Append("=true\n")
        .ToString();

    /// <summary>
    /// Installs or refreshes the entry. False (nothing to do) without an existing AppImage file, when an unmarked
    /// <c>inbound.desktop</c> is there, or when nothing changed. Throws <see cref="IOException"/> or
    /// <see cref="UnauthorizedAccessException"/> when a write fails; the caller logs it.
    /// </summary>
    public bool Install()
    {
        if (string.IsNullOrWhiteSpace(appImagePath) || !File.Exists(appImagePath)) return false;
        var path = DesktopFilePath;
        var current = File.Exists(path) ? File.ReadAllText(path) : null;
        if (current is not null && !IsManaged(current))
        {
            log?.Invoke("An inbound.desktop that Inbound didn't write is already in the applications folder; left alone.");
            return false;
        }

        var icon = InstallIcon() ? InstalledIconPath : Fallback;
        var text = DesktopFileText(icon);
        if (string.Equals(current, text, StringComparison.Ordinal)) return false;
        Directory.CreateDirectory(applicationsDirectory);
        LinuxTorrentHandler.ReplaceFile(path, text);
        Refresh();
        return true;
    }

    /// <summary>Removes the entry and the icon copy, but only an entry carrying <see cref="ManagedKey"/>. True when anything went.</summary>
    public bool Remove()
    {
        var changed = false;
        var path = DesktopFilePath;
        if (File.Exists(path) && IsManaged(File.ReadAllText(path)))
        {
            File.Delete(path);
            changed = true;
            Refresh();
        }

        // The icon copy is ours alone (a folder only we write to), whatever happened to the entry.
        if (File.Exists(InstalledIconPath))
        {
            File.Delete(InstalledIconPath);
            changed = true;
        }

        return changed;
    }

    private void Refresh()
    {
        try { refreshDatabase?.Invoke([applicationsDirectory]); }
        catch (Exception ex) when (ex is IOException or InvalidOperationException or System.ComponentModel.Win32Exception) { }
    }

    private static bool IsManaged(string text) =>
        text.Split('\n').Any(line => string.Equals(line.Trim(), ManagedKey + "=true", StringComparison.Ordinal));

    /// <summary>Copies the png when it isn't there or differs. False when there is no source or the copy failed.</summary>
    private bool InstallIcon()
    {
        if (string.IsNullOrWhiteSpace(iconSource) || !File.Exists(iconSource)) return false;
        var target = InstalledIconPath;
        try
        {
            if (File.Exists(target) && File.ReadAllBytes(target).AsSpan().SequenceEqual(File.ReadAllBytes(iconSource))) return true;
            Directory.CreateDirectory(iconDirectory);
            var temporary = target + ".tmp";
            File.Copy(iconSource, temporary, overwrite: true);
            File.Move(temporary, target, overwrite: true);
            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            log?.Invoke($"The menu icon wasn't copied: {ex.GetType().Name}");
            return false;
        }
    }

    /// <summary>Where the icon copy lives: <c>$XDG_DATA_HOME/inbound</c>, else <c>~/.local/share/inbound</c>.</summary>
    public static string IconDirectory(string home, string? dataHome) =>
        Path.Combine(string.IsNullOrWhiteSpace(dataHome) || !Path.IsPathRooted(dataHome) ? Path.Combine(home, ".local", "share") : dataHome,
            "inbound");
}

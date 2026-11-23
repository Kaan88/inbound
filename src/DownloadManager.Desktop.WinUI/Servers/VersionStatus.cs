using DownloadManager.Contracts;

namespace DownloadManager.Desktop.WinUI;

/// <summary>
/// UI.R37: what the status bar says about a Host's version. The latest version comes from the Host's own last update
/// check (<see cref="HostStatusDto.LatestVersion"/>); nothing here checks the network. With no latest known it says the
/// version alone, never "latest".
/// </summary>
internal static class VersionStatus
{
    /// <summary>This PC's version line: the version, then "(latest)" or a link to its update, or the version alone.</summary>
    public static VersionWords ThisPc(string? version, string? latest)
    {
        if (string.IsNullOrWhiteSpace(version)) return new VersionWords(string.Empty, null);
        return Compare(version, latest) switch
        {
            < 0 => new VersionWords(version, $"update available ({Clean(latest!)})"),
            >= 0 => new VersionWords($"{version} (latest)", null),
            null => new VersionWords(version, null),
        };
    }

    /// <summary>
    /// Another server or app's version as the window shows it: "0.6.3 (not latest)" when older than the latest known,
    /// "0.7.0 (latest)" when not, the version alone when the latest isn't known.
    /// </summary>
    public static string Server(string? version, string? latest)
    {
        if (string.IsNullOrWhiteSpace(version)) return string.Empty;
        return Compare(version, latest) switch
        {
            < 0 => $"{version} (not latest)",
            >= 0 => $"{version} (latest)",
            null => version,
        };
    }

    /// <summary>The note after a server's name in the switcher menus; only when it is known to be older. Null otherwise.</summary>
    public static string? ServerNote(string? version, string? latest)
        => Compare(version, latest) < 0 ? $"{version} (not latest)" : null;

    /// <summary>
    /// UI.B33: the latest version to hold another server to, which is only one found for its own install kind. Its own
    /// update check is the one for its build (a Linux server reads the Linux manifest). Without one, this PC's latest
    /// stands for it only when this PC is the same kind (Windows with Windows, Linux with Linux): releases don't ship
    /// every kind from each tag (0.7.3 had no Docker image). A Docker server (<see cref="HostKinds.Server"/>) has no
    /// check yet, and a Host of a kind this window doesn't know isn't held to anything; both show the version alone.
    /// </summary>
    public static string? LatestFor(string? serverLatest, string? serverKind, string? thisPcLatest, string? thisPcKind, string? dockerLatest = null)
    {
        if (!string.IsNullOrWhiteSpace(serverLatest)) return serverLatest;
        // UPD.D1 addendum: a Docker server is held to the Docker latest this PC's Host read, and only to that.
        if (serverKind == HostKinds.Server) return string.IsNullOrWhiteSpace(dockerLatest) ? null : dockerLatest;
        return SameInstallKind(serverKind, thisPcKind) ? thisPcLatest : null;
    }

    /// <summary>
    /// UPD.D1 addendum: the note after a saved server's name in the switcher menus while it is not the shown one. Its
    /// version is the one it last reported, and only a Docker server is compared (with the Docker latest this PC's Host
    /// read, never another install kind's latest, UI.B33). Null unless it is known to be behind.
    /// </summary>
    public static string? SavedServerNote(string? serverKind, string? lastVersion, string? dockerLatest)
        => serverKind == HostKinds.Server && Compare(lastVersion, dockerLatest) < 0 ? $"{lastVersion} (update available)" : null;

    /// <summary>
    /// Whether two Hosts are the same install kind. A Host from before <c>hostKind</c> is a Windows one. The Docker
    /// server kind is never this PC's: a desktop window runs on Windows or Linux.
    /// </summary>
    public static bool SameInstallKind(string? serverKind, string? thisPcKind)
    {
        var server = serverKind ?? HostKinds.Windows;
        var pc = thisPcKind ?? HostKinds.Windows;
        return server == pc && server is HostKinds.Windows or HostKinds.Linux;
    }

    /// <summary>Negative when <paramref name="version"/> is older than <paramref name="latest"/>; null when either can't be read.</summary>
    public static int? Compare(string? version, string? latest)
    {
        if (string.IsNullOrWhiteSpace(version) || string.IsNullOrWhiteSpace(latest)) return null;
        if (!System.Version.TryParse(Clean(version), out var current) || !System.Version.TryParse(Clean(latest), out var newest)) return null;
        return Normal(current).CompareTo(Normal(newest));
    }

    private static string Clean(string value) => value.Trim().TrimStart('v', 'V');

    // "0.7" and "0.7.0" are the same release.
    private static System.Version Normal(System.Version value)
        => new(value.Major, value.Minor, Math.Max(value.Build, 0), Math.Max(value.Revision, 0));
}

/// <summary>A version line: <see cref="Text"/>, then a link to the update (<see cref="UpdateLink"/>) when there is one.</summary>
internal sealed record VersionWords(string Text, string? UpdateLink);

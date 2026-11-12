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
    /// The latest version to hold another server to. Its own update check is the one for its build (a Linux server
    /// reads the Linux manifest). Without one, this PC's: every Inbound build (Windows Setup, the Linux AppImage, the
    /// Docker server) is made from the same version tag with one version number (Directory.Build.props, the
    /// <c>v*</c> tag workflows), so this PC's latest is theirs too. A Host of a kind this window doesn't know isn't
    /// held to it.
    /// </summary>
    public static string? LatestFor(string? serverLatest, string? serverKind, string? thisPcLatest)
    {
        if (!string.IsNullOrWhiteSpace(serverLatest)) return serverLatest;
        return SameReleaseLine(serverKind) ? thisPcLatest : null;
    }

    /// <summary>A Host from before <c>hostKind</c> is a desktop Host of the same releases.</summary>
    public static bool SameReleaseLine(string? kind)
        => kind is null or HostKinds.Windows or HostKinds.Linux or HostKinds.Server;

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

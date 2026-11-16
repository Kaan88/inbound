using System.Text.Json;

namespace DownloadManager.Core;

/// <param name="AppImagePath">Set when this build runs from an AppImage: the file an update replaces.</param>
/// <param name="MacAppPath">Set when this build runs from Inbound.app on macOS: the bundle an update replaces.</param>
public sealed record InstallMetadata(string Version, string Channel, string InstallRoot, string? AppImagePath = null, string? MacAppPath = null);

public static class InstalledBuild
{
    /// <summary>Loads install.json for installer builds; portable/dev builds return null.</summary>
    public static InstallMetadata? TryLoad(string? startDirectory = null)
    {
        var overrideRoot = Environment.GetEnvironmentVariable("DOWNLOADMANAGER_INSTALL_ROOT");
        if (!string.IsNullOrWhiteSpace(overrideRoot))
        {
            return Read(Path.Combine(overrideRoot, "install.json"), overrideRoot);
        }

        if (startDirectory is null && AppImage() is { } appImage) return appImage;
        if (startDirectory is null && MacApp() is { } macApp) return macApp;

        var directory = new DirectoryInfo(startDirectory ?? AppContext.BaseDirectory);
        for (var depth = 0; depth < 5 && directory is not null; depth++)
        {
            var metadata = Read(Path.Combine(directory.FullName, "install.json"), directory.FullName);
            if (metadata is not null)
            {
                return metadata;
            }

            directory = directory.Parent;
        }

        return null;
    }

    /// <summary>
    /// A build running from an AppImage: the AppImage runtime sets <c>APPIMAGE</c> (the file) and <c>APPDIR</c> (where it
    /// is mounted), and this program must be inside that mount, so a copy started some other way with the variables
    /// inherited doesn't count. Its version is this build's own.
    /// </summary>
    public static InstallMetadata? AppImage(string? appImage = null, string? appDir = null, string? baseDirectory = null, string? version = null)
    {
        if (!OperatingSystem.IsLinux() && appImage is null) return null;
        appImage ??= Environment.GetEnvironmentVariable("APPIMAGE");
        appDir ??= Environment.GetEnvironmentVariable("APPDIR");
        baseDirectory ??= AppContext.BaseDirectory;
        if (string.IsNullOrWhiteSpace(appImage) || string.IsNullOrWhiteSpace(appDir) || !Path.IsPathFullyQualified(appImage)) return null;
        var mount = Path.TrimEndingDirectorySeparator(Path.GetFullPath(appDir)) + Path.DirectorySeparatorChar;
        if (!Path.GetFullPath(baseDirectory).StartsWith(mount, StringComparison.Ordinal)) return null;
        version ??= typeof(InstalledBuild).Assembly.GetName().Version?.ToString(3);
        if (string.IsNullOrWhiteSpace(version)) return null;
        return new InstallMetadata(version, "stable", Path.GetDirectoryName(appImage) ?? "/", appImage);
    }

    /// <summary>
    /// MAC.1: a build running from an app bundle on macOS: this program is in <c>X.app/Contents/Resources/host</c> or
    /// <c>.../window</c> (scripts/package-macos.sh), and the bundle has its Info.plist. Its version is this build's own,
    /// and an update replaces the whole bundle. A copy run from a build folder is not one.
    /// </summary>
    public static InstallMetadata? MacApp(string? baseDirectory = null, string? version = null, Func<string, bool>? fileExists = null)
    {
        if (!OperatingSystem.IsMacOS() && baseDirectory is null) return null;
        fileExists ??= File.Exists;
        var directory = Path.TrimEndingDirectorySeparator(Path.GetFullPath(baseDirectory ?? AppContext.BaseDirectory));
        // .../Inbound.app/Contents/Resources/<host|window>
        var resources = Path.GetDirectoryName(directory);
        var contents = resources is null ? null : Path.GetDirectoryName(resources);
        var bundle = contents is null ? null : Path.GetDirectoryName(contents);
        if (bundle is null || Path.GetFileName(resources) != "Resources" || Path.GetFileName(contents) != "Contents" ||
            !bundle.EndsWith(".app", StringComparison.OrdinalIgnoreCase) || !fileExists(Path.Combine(contents, "Info.plist")))
            return null;
        version ??= typeof(InstalledBuild).Assembly.GetName().Version?.ToString(3);
        if (string.IsNullOrWhiteSpace(version)) return null;
        return new InstallMetadata(version, "stable", Path.GetDirectoryName(bundle) ?? "/", MacAppPath: bundle);
    }

    private static InstallMetadata? Read(string path, string root)
    {
        try
        {
            if (!File.Exists(path))
            {
                return null;
            }

            using var document = JsonDocument.Parse(File.ReadAllText(path));
            var element = document.RootElement;
            if (!element.TryGetProperty("version", out var version) ||
                !element.TryGetProperty("channel", out var channel))
            {
                return null;
            }

            var versionValue = version.GetString();
            if (string.IsNullOrWhiteSpace(versionValue))
            {
                return null;
            }

            var effectiveVersion = versionValue.Trim();
            var hostAssembly = Path.Combine(root, "app", "DownloadManager.Host.dll");
            if (File.Exists(hostAssembly) && Version.TryParse(effectiveVersion, out var recorded))
            {
                var actual = System.Reflection.AssemblyName.GetAssemblyName(hostAssembly).Version;
                if (actual is not null && recorded > actual)
                    effectiveVersion = actual.ToString(3);
            }

            return new InstallMetadata(
                effectiveVersion,
                string.IsNullOrWhiteSpace(channel.GetString()) ? "stable" : channel.GetString()!.Trim(),
                root);
        }
        catch (Exception ex) when (ex is IOException or JsonException or UnauthorizedAccessException or BadImageFormatException)
        {
            return null;
        }
    }
}

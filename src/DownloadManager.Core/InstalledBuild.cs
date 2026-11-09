using System.Text.Json;

namespace DownloadManager.Core;

/// <param name="AppImagePath">Set when this build runs from an AppImage: the file an update replaces.</param>
public sealed record InstallMetadata(string Version, string Channel, string InstallRoot, string? AppImagePath = null);

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

using System.Text.Json;

namespace DownloadManager.Core;

public sealed record InstallMetadata(string Version, string Channel, string InstallRoot);

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

            return new InstallMetadata(
                versionValue.Trim(),
                string.IsNullOrWhiteSpace(channel.GetString()) ? "stable" : channel.GetString()!.Trim(),
                root);
        }
        catch (Exception ex) when (ex is IOException or JsonException or UnauthorizedAccessException)
        {
            return null;
        }
    }
}

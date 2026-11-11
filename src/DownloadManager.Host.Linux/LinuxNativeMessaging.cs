using System.Text.Json;
using System.Text.Json.Nodes;
using DownloadManager.Contracts;

namespace DownloadManager.Host;

/// <summary>
/// The browser extension's native host on Linux. Each browser reads a manifest from a folder of its own under the home
/// folder (there is no registry); the Host writes them at start, as the Windows Host repairs its keys. A manifest points
/// at a small launcher in the data folder, because an AppImage's mount point changes with every run, and the launcher
/// starts the native host of the copy that runs now. Only browsers whose own folder exists get one, and a manifest of
/// another program with the same name is left alone.
/// </summary>
internal static class LinuxNativeMessaging
{
    /// <summary>Chromium-family browsers: their config folder under <c>~/.config</c>.</summary>
    private static readonly string[] ChromiumFamily =
        ["google-chrome", "google-chrome-beta", "google-chrome-unstable", "chromium", "microsoft-edge", "microsoft-edge-beta", "BraveSoftware/Brave-Browser", "vivaldi"];

    public const string LauncherName = "inbound-native-host";

    /// <summary>
    /// Writes the launcher and the manifests. Skipped for a sandbox data folder (tests, a second profile), which must not
    /// change the real browsers' registration. Returns what was done, for the log.
    /// </summary>
    public static string Apply(string dataRoot, string home, string? isolatedDataDirectory)
    {
        if (!string.IsNullOrWhiteSpace(isolatedDataDirectory)) return "Browser registration skipped (separate data folder).";
        var target = NativeHostCommand();
        if (target is null) return "Browser registration skipped: the native host program was not found next to the Host.";

        var launcher = WriteLauncher(dataRoot, target);
        var written = new List<string>();
        var kept = new List<string>();
        var config = Environment.GetEnvironmentVariable("XDG_CONFIG_HOME");
        if (string.IsNullOrWhiteSpace(config) || !Path.IsPathRooted(config)) config = Path.Combine(home, ".config");

        foreach (var browser in ChromiumFamily)
        {
            var folder = Path.Combine(config, browser);
            if (!Directory.Exists(folder)) continue;
            Place(Path.Combine(folder, "NativeMessagingHosts"), browser, ChromiumManifest(launcher), launcher, written, kept);
        }

        if (Directory.Exists(Path.Combine(home, ".mozilla")))
            Place(Path.Combine(home, ".mozilla", "native-messaging-hosts"), "firefox", FirefoxManifest(launcher), launcher, written, kept);

        var text = written.Count == 0 ? "Browser registration: no browser folder found." : $"Browser registration written for {string.Join(", ", written)}.";
        return kept.Count == 0 ? text : text + $" Left another program's registration in place for {string.Join(", ", kept)}.";
    }

    private static void Place(string folder, string browser, JsonObject manifest, string launcher, List<string> written, List<string> kept)
    {
        var path = Path.Combine(folder, BrowserBridge.NativeHostName + ".json");
        if (BelongsToAnother(path, launcher))
        {
            kept.Add(browser);
            return;
        }

        Directory.CreateDirectory(folder);
        WriteAtomically(path, manifest.ToJsonString(new JsonSerializerOptions { WriteIndented = true }), UnixFileMode.UserRead | UnixFileMode.UserWrite);
        written.Add(browser);
    }

    /// <summary>A manifest of this name whose program is some other existing file: another install, kept as on Windows.</summary>
    private static bool BelongsToAnother(string path, string launcher)
    {
        try
        {
            if (!File.Exists(path)) return false;
            var existing = JsonNode.Parse(File.ReadAllText(path))?["path"]?.GetValue<string>();
            return !string.IsNullOrWhiteSpace(existing) && existing != launcher && File.Exists(existing);
        }
        catch (Exception ex) when (ex is JsonException or IOException or UnauthorizedAccessException or InvalidOperationException)
        {
            return false;
        }
    }

    private static JsonObject ChromiumManifest(string launcher) => new()
    {
        ["name"] = BrowserBridge.NativeHostName,
        ["description"] = "Inbound Download Manager browser bridge",
        ["path"] = launcher,
        ["type"] = "stdio",
        ["allowed_origins"] = new JsonArray(BrowserBridge.StoreExtensionOrigin, BrowserBridge.EdgeStoreExtensionOrigin, BrowserBridge.UnpackedExtensionOrigin),
    };

    private static JsonObject FirefoxManifest(string launcher) => new()
    {
        ["name"] = BrowserBridge.NativeHostName,
        ["description"] = "Inbound Download Manager browser bridge",
        ["path"] = launcher,
        ["type"] = "stdio",
        ["allowed_extensions"] = new JsonArray(BrowserBridge.FirefoxExtensionId),
    };

    /// <summary>
    /// The command the launcher runs: the AppImage with <c>--native-host</c> when the Host runs from one, else the native
    /// host program beside the Host. Null when neither is there.
    /// </summary>
    private static string[]? NativeHostCommand()
    {
        var appImage = Environment.GetEnvironmentVariable("APPIMAGE");
        if (!string.IsNullOrWhiteSpace(appImage) && File.Exists(appImage)) return [appImage, "--native-host"];
        var candidates = new[]
        {
            Path.Combine(AppContext.BaseDirectory, "DownloadManager.NativeHost"),
            Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", "DownloadManager.NativeHost", "bin", "Debug", "net10.0", "DownloadManager.NativeHost"),
            Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", "DownloadManager.NativeHost", "bin", "Release", "net10.0", "DownloadManager.NativeHost"),
        };
        var found = candidates.Select(Path.GetFullPath).FirstOrDefault(File.Exists);
        return found is null ? null : [found];
    }

    private static string WriteLauncher(string dataRoot, string[] command)
    {
        var path = Path.Combine(dataRoot, "bin", LauncherName);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var script = "#!/bin/sh\n# Written by Inbound's Host at each start; the browsers' native-messaging manifests point here.\nexec " +
                     string.Join(' ', command.Select(QuoteShell)) + " \"$@\"\n";
        WriteAtomically(path, script, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        return path;
    }

    internal static string QuoteShell(string argument) => "'" + argument.Replace("'", "'\\''", StringComparison.Ordinal) + "'";

    private static void WriteAtomically(string path, string text, UnixFileMode mode)
    {
        var temporary = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            using (var stream = new FileStream(temporary, new FileStreamOptions
                   {
                       Mode = FileMode.CreateNew, Access = FileAccess.Write, Share = FileShare.None, UnixCreateMode = mode,
                   }))
            {
                stream.Write(System.Text.Encoding.UTF8.GetBytes(text));
                stream.Flush(flushToDisk: true);
            }

            File.Move(temporary, path, overwrite: true);
        }
        finally
        {
            if (File.Exists(temporary)) File.Delete(temporary);
        }
    }
}

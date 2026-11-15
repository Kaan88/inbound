using System.Security;
using DownloadManager.Core;

namespace DownloadManager.Host;

/// <summary>
/// MAC.1: Options › Start Inbound when I sign in, on macOS. A LaunchAgent (<c>~/Library/LaunchAgents</c>) that starts
/// Inbound.app's launcher with <c>--background</c>, so only the Host starts, as the autostart entry does on Linux. macOS
/// reads it at the next sign-in; removing it is turning the option off.
/// </summary>
internal static class MacLoginItem
{
    public const string Label = "local.inbound.host";

    public static string PathOf() =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "Library", "LaunchAgents", Label + ".plist");

    public static void Apply(bool enabled)
    {
        var path = PathOf();
        if (!enabled)
        {
            if (File.Exists(path)) File.Delete(path);
            return;
        }

        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var temporary = path + ".tmp";
        File.WriteAllText(temporary, Plist(LaunchProgram()));
        File.Move(temporary, path, overwrite: true);
    }

    /// <summary>The bundle's launcher when the Host runs from Inbound.app (it keeps working after an update swaps the bundle), else this Host.</summary>
    internal static string LaunchProgram()
    {
        if (InstalledBuild.MacApp() is { MacAppPath: { } bundle })
        {
            var launcher = System.IO.Path.Combine(bundle, "Contents", "MacOS", "InboundLauncher");
            if (File.Exists(launcher)) return launcher;
        }

        return Environment.ProcessPath ?? System.IO.Path.Combine(AppContext.BaseDirectory, "inbound-host");
    }

    internal static string Plist(string program) => $"""
        <?xml version="1.0" encoding="UTF-8"?>
        <!DOCTYPE plist PUBLIC "-//Apple//DTD PLIST 1.0//EN" "http://www.apple.com/DTDs/PropertyList-1.0.dtd">
        <plist version="1.0">
        <dict>
          <key>Label</key><string>{Label}</string>
          <key>ProgramArguments</key>
          <array>
            <string>{SecurityElement.Escape(program)}</string>
            <string>--background</string>
          </array>
          <key>RunAtLoad</key><true/>
          <key>LimitLoadToSessionType</key><string>Aqua</string>
        </dict>
        </plist>

        """;
}

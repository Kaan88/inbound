using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using Microsoft.Win32;

namespace DownloadManager.Host;

/// <summary>
/// T1 step 6b: Options › Torrents › "Open magnet links and .torrent files with Inbound". Per user, under
/// HKCU\Software\Classes, with no admin rights:
/// <list type="bullet">
/// <item>ProgIDs <c>Inbound.Torrent</c> (for .torrent files) and <c>Inbound.Magnet</c> (a URL protocol class), each
/// opening the window exe with <c>--open-torrent "%1"</c>;</item>
/// <item><c>.torrent\OpenWithProgids</c> gets <c>Inbound.Torrent</c>; the extension's default is set only when nothing
/// holds it yet;</item>
/// <item>the <c>magnet</c> protocol key is written only when it is missing or already ours: another app's magnet
/// handler is never replaced;</item>
/// <item>Capabilities and RegisteredApplications, so Windows offers Inbound in Default apps for both.</item>
/// </list>
/// Removal deletes only what points at this exe (or what this class alone writes). Every path is relative to
/// <paramref name="userRoot"/>, which is HKCU in the Host and a throwaway key in tests.
/// <paramref name="machineClasses"/> (HKLM\Software\Classes in the Host, read only) is checked too: Windows merges it
/// under the user's keys, so a user key written over an all-users torrent app's magnet or .torrent default would take
/// over from it without asking.
/// </summary>
[SupportedOSPlatform("windows")]
public sealed class TorrentHandlerRegistration(RegistryKey userRoot, string windowExecutable, RegistryKey? machineClasses = null)
{
    /// <summary>The window exe's file name; a magnet command naming it with our arguments is an Inbound one.</summary>
    public const string WindowExecutableName = "DownloadManager.Desktop.WinUI.exe";

    public const string TorrentProgId = "Inbound.Torrent";
    public const string MagnetProgId = "Inbound.Magnet";
    public const string ApplicationName = "Inbound Download Manager";
    public const string CapabilitiesPath = @"Software\Inbound Download Manager\Capabilities";
    private const string Classes = @"Software\Classes";
    private const string RegisteredApplications = @"Software\RegisteredApplications";

    private string Command => $"\"{windowExecutable}\" --open-torrent \"%1\"";

    /// <summary>Registers the handler. True when anything was written.</summary>
    public bool Register()
    {
        var changed = false;
        changed |= WriteProgId($@"{Classes}\{TorrentProgId}", "Torrent file", urlProtocol: false);
        changed |= WriteProgId($@"{Classes}\{MagnetProgId}", "URL:Magnet link", urlProtocol: true);

        using (var extension = userRoot.CreateSubKey($@"{Classes}\.torrent", writable: true))
        {
            using var openWith = extension.CreateSubKey("OpenWithProgids", writable: true);
            if (openWith.GetValue(TorrentProgId) is null)
            {
                openWith.SetValue(TorrentProgId, Array.Empty<byte>(), RegistryValueKind.None);
                changed = true;
            }

            if (string.IsNullOrEmpty(extension.GetValue(null) as string) && MachineTorrentDefaultFree())
            {
                extension.SetValue(null, TorrentProgId);
                changed = true;
            }
        }

        // The protocol key itself: only when no other app has it, here or for all users. One an earlier Inbound copy
        // wrote (another folder) is moved to this exe.
        var magnet = $@"{Classes}\magnet";
        var existing = ReadCommand(magnet);
        var free = existing is null
            ? ReadCommand(machineClasses, "magnet") is not { } machine || IsInboundCommand(machine)
            : IsInboundCommand(existing);
        if (free) changed |= WriteProgId(magnet, "URL:Magnet link", urlProtocol: true);

        using (var capabilities = userRoot.CreateSubKey(CapabilitiesPath, writable: true))
        {
            changed |= Set(capabilities, "ApplicationName", ApplicationName);
            changed |= Set(capabilities, "ApplicationDescription", "Downloads files, magnet links and torrents.");
            using (var files = capabilities.CreateSubKey("FileAssociations", writable: true))
                changed |= Set(files, ".torrent", TorrentProgId);
            using (var urls = capabilities.CreateSubKey("URLAssociations", writable: true))
                changed |= Set(urls, "magnet", MagnetProgId);
        }

        using (var registered = userRoot.CreateSubKey(RegisteredApplications, writable: true))
            changed |= Set(registered, ApplicationName, CapabilitiesPath);
        return changed;
    }

    /// <summary>Removes what this exe registered. Keys another exe's command is in stay. True when anything was removed.</summary>
    public bool Remove()
    {
        var changed = false;
        var torrentOurs = DeleteIfOurs($@"{Classes}\{TorrentProgId}");
        changed |= torrentOurs;
        changed |= DeleteIfOurs($@"{Classes}\{MagnetProgId}");
        // The protocol key may name an earlier Inbound copy's exe that is gone (Setup into another folder); it goes too.
        // Another copy that is still there keeps its key.
        if (ReadCommand($@"{Classes}\magnet") is { } magnetCommand &&
            (PointsAtUs(magnetCommand) || (CommandIsInbound(magnetCommand) && !File.Exists(ProgramOf(magnetCommand.Trim())))))
        {
            userRoot.DeleteSubKeyTree($@"{Classes}\magnet", throwOnMissingSubKey: false);
            changed = true;
        }

        // The ProgID is gone (or never was): the extension's pointers at it go too, unless another exe's copy is there.
        if (ReadCommand($@"{Classes}\{TorrentProgId}") is null)
        {
            using var extension = userRoot.OpenSubKey($@"{Classes}\.torrent", writable: true);
            if (extension is not null)
            {
                using (var openWith = extension.OpenSubKey("OpenWithProgids", writable: true))
                {
                    if (openWith?.GetValue(TorrentProgId) is not null)
                    {
                        openWith.DeleteValue(TorrentProgId, throwOnMissingValue: false);
                        changed = true;
                    }
                }

                if (string.Equals(extension.GetValue(null) as string, TorrentProgId, StringComparison.OrdinalIgnoreCase))
                {
                    extension.DeleteValue(string.Empty, throwOnMissingValue: false);
                    changed = true;
                }
            }

            DeleteIfEmpty($@"{Classes}\.torrent\OpenWithProgids");
            DeleteIfEmpty($@"{Classes}\.torrent");
        }

        // Default apps entries: only while no ProgID of ours is left for them to name.
        if (ReadCommand($@"{Classes}\{TorrentProgId}") is null && ReadCommand($@"{Classes}\{MagnetProgId}") is null)
        {
            using (var registered = userRoot.OpenSubKey(RegisteredApplications, writable: true))
            {
                if (registered is not null && string.Equals(registered.GetValue(ApplicationName) as string, CapabilitiesPath, StringComparison.OrdinalIgnoreCase))
                {
                    registered.DeleteValue(ApplicationName, throwOnMissingValue: false);
                    changed = true;
                }
            }

            using (var capabilities = userRoot.OpenSubKey(CapabilitiesPath))
            {
                if (capabilities is not null)
                {
                    capabilities.Dispose();
                    userRoot.DeleteSubKeyTree(CapabilitiesPath, throwOnMissingSubKey: false);
                    changed = true;
                }
            }

            DeleteIfEmpty(Path.GetDirectoryName(CapabilitiesPath)!);
        }

        return changed;
    }

    /// <summary>The command's program is this exe. The command is <c>"path" args</c> or <c>path args</c>.</summary>
    public bool PointsAtUs(string command) => CommandPointsAt(command, windowExecutable);

    public static bool CommandPointsAt(string command, string executable)
    {
        if (ProgramOf(command.Trim()) is not { } program) return false;
        try
        {
            return string.Equals(Path.GetFullPath(program), Path.GetFullPath(executable), StringComparison.OrdinalIgnoreCase);
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
        {
            return false;
        }
    }

    private static string? ProgramOf(string text)
    {
        if (text.StartsWith('"'))
        {
            var end = text.IndexOf('"', 1);
            return end < 0 ? null : text[1..end];
        }

        var space = text.IndexOf(' ');
        return space < 0 ? text : text[..space];
    }

    private bool WriteProgId(string path, string description, bool urlProtocol)
    {
        var changed = false;
        using var key = userRoot.CreateSubKey(path, writable: true);
        changed |= Set(key, null, description);
        if (urlProtocol) changed |= Set(key, "URL Protocol", string.Empty);
        using (var icon = key.CreateSubKey("DefaultIcon", writable: true))
            changed |= Set(icon, null, $"\"{windowExecutable}\",0");
        using (var command = key.CreateSubKey(@"shell\open\command", writable: true))
            changed |= Set(command, null, Command);
        return changed;
    }

    /// <summary>This exe, or any Inbound window exe given our <c>--open-torrent "%1"</c> arguments.</summary>
    public bool IsInboundCommand(string command) => PointsAtUs(command) || CommandIsInbound(command);

    public static bool CommandIsInbound(string command)
    {
        var text = command.Trim();
        if (!text.EndsWith("--open-torrent \"%1\"", StringComparison.OrdinalIgnoreCase)) return false;
        var program = ProgramOf(text);
        return program is not null &&
               string.Equals(Path.GetFileName(program), WindowExecutableName, StringComparison.OrdinalIgnoreCase);
    }

    // The all-users .torrent default is empty or one of ours.
    private bool MachineTorrentDefaultFree()
    {
        if (machineClasses is null) return true;
        using var extension = machineClasses.OpenSubKey(".torrent");
        var current = extension?.GetValue(null) as string;
        return string.IsNullOrEmpty(current) || string.Equals(current, TorrentProgId, StringComparison.OrdinalIgnoreCase);
    }

    private string? ReadCommand(string path) => ReadCommand(userRoot, path);

    private static string? ReadCommand(RegistryKey? root, string path)
    {
        using var key = root?.OpenSubKey($@"{path}\shell\open\command");
        return key?.GetValue(null) as string is { Length: > 0 } command ? command : null;
    }

    private bool DeleteIfOurs(string path)
    {
        if (ReadCommand(path) is not { } command || !PointsAtUs(command)) return false;
        userRoot.DeleteSubKeyTree(path, throwOnMissingSubKey: false);
        return true;
    }

    private void DeleteIfEmpty(string path)
    {
        using (var key = userRoot.OpenSubKey(path))
        {
            if (key is null || key.SubKeyCount > 0 || key.ValueCount > 0) return;
        }

        userRoot.DeleteSubKey(path, throwOnMissingSubKey: false);
    }

    private static bool Set(RegistryKey key, string? name, string value)
    {
        if (key.GetValue(name) is string current && string.Equals(current, value, StringComparison.Ordinal)) return false;
        key.SetValue(name, value, RegistryValueKind.String);
        return true;
    }

    private const int ShcneAssocChanged = 0x08000000;
    private const int ShcnfIdList = 0;

    [DllImport("shell32.dll")]
    private static extern void SHChangeNotify(int eventId, int flags, IntPtr item1, IntPtr item2);

    /// <summary>Tells Explorer the associations changed, so icons and Open with follow at once.</summary>
    public static void NotifyShell() => SHChangeNotify(ShcneAssocChanged, ShcnfIdList, IntPtr.Zero, IntPtr.Zero);
}

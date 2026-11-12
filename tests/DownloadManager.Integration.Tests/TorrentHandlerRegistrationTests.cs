using System.Runtime.Versioning;
using DownloadManager.Host;
using Microsoft.Win32;
using Xunit;

namespace DownloadManager.Integration.Tests;

/// <summary>
/// T1 step 6b: the magnet and .torrent handler's registry writes and removal. Each test uses a throwaway key under
/// HKCU\Software\AppDataLow\InboundDownloadManagerTests\ as the user root, so the real Classes keys are never touched.
/// </summary>
[Trait("Platform", "Windows")]
[SupportedOSPlatform("windows")]
public sealed class TorrentHandlerRegistrationTests : IDisposable
{
    private const string TestParent = @"Software\AppDataLow\InboundDownloadManagerTests";
    private const string Exe = @"C:\Program Files\Inbound\app\DownloadManager.Desktop.WinUI.exe";
    private const string OtherExe = @"C:\Other\torrent.exe";
    private readonly string _testKey = $@"{TestParent}\{Guid.NewGuid():N}";
    private readonly RegistryKey _root;

    public TorrentHandlerRegistrationTests()
    {
        _root = Registry.CurrentUser.CreateSubKey(_testKey, writable: true);
    }

    public void Dispose()
    {
        _root.Dispose();
        Registry.CurrentUser.DeleteSubKeyTree(_testKey, throwOnMissingSubKey: false);
        using (var parent = Registry.CurrentUser.OpenSubKey(TestParent))
        {
            if (parent is not null && parent.SubKeyCount == 0 && parent.ValueCount == 0)
                Registry.CurrentUser.DeleteSubKey(TestParent, throwOnMissingSubKey: false);
        }
    }

    private string? Value(string path, string? name = null)
    {
        using var key = _root.OpenSubKey(path);
        return key?.GetValue(name) as string;
    }

    private bool Exists(string path)
    {
        using var key = _root.OpenSubKey(path);
        return key is not null;
    }

    private void WriteOtherMagnetHandler()
    {
        using var magnet = _root.CreateSubKey(@"Software\Classes\magnet", writable: true);
        magnet.SetValue("URL Protocol", string.Empty);
        using var command = magnet.CreateSubKey(@"shell\open\command", writable: true);
        command.SetValue(null, $"\"{OtherExe}\" \"%1\"");
    }

    [Fact]
    public void Register_writes_the_classes_and_default_apps_entries()
    {
        var registration = new TorrentHandlerRegistration(_root, Exe);

        Assert.True(registration.Register());

        var command = $"\"{Exe}\" --open-torrent \"%1\"";
        Assert.Equal(command, Value(@"Software\Classes\magnet\shell\open\command"));
        Assert.Equal(string.Empty, Value(@"Software\Classes\magnet", "URL Protocol"));
        Assert.Equal(command, Value(@"Software\Classes\Inbound.Magnet\shell\open\command"));
        Assert.Equal(command, Value(@"Software\Classes\Inbound.Torrent\shell\open\command"));
        Assert.Equal("Inbound.Torrent", Value(@"Software\Classes\.torrent"));
        using (var openWith = _root.OpenSubKey(@"Software\Classes\.torrent\OpenWithProgids"))
            Assert.Contains("Inbound.Torrent", openWith!.GetValueNames());
        Assert.Equal("Inbound.Torrent", Value(TorrentHandlerRegistration.CapabilitiesPath + @"\FileAssociations", ".torrent"));
        Assert.Equal("Inbound.Magnet", Value(TorrentHandlerRegistration.CapabilitiesPath + @"\URLAssociations", "magnet"));
        Assert.Equal(TorrentHandlerRegistration.CapabilitiesPath,
            Value(@"Software\RegisteredApplications", TorrentHandlerRegistration.ApplicationName));

        // A second run changes nothing, so Explorer is not told again.
        Assert.False(registration.Register());
    }

    [Fact]
    public void Register_keeps_another_apps_magnet_handler_and_torrent_default()
    {
        WriteOtherMagnetHandler();
        using (var extension = _root.CreateSubKey(@"Software\Classes\.torrent", writable: true))
            extension.SetValue(null, "Other.Torrent");

        new TorrentHandlerRegistration(_root, Exe).Register();

        Assert.Equal($"\"{OtherExe}\" \"%1\"", Value(@"Software\Classes\magnet\shell\open\command"));
        Assert.Equal("Other.Torrent", Value(@"Software\Classes\.torrent"));
        // Still offered: Open with and Default apps.
        using (var openWith = _root.OpenSubKey(@"Software\Classes\.torrent\OpenWithProgids"))
            Assert.Contains("Inbound.Torrent", openWith!.GetValueNames());
        Assert.True(Exists(@"Software\Classes\Inbound.Magnet"));
    }

    [Fact]
    public void Remove_deletes_what_it_wrote_and_leaves_no_empty_keys()
    {
        var registration = new TorrentHandlerRegistration(_root, Exe);
        registration.Register();

        Assert.True(registration.Remove());

        Assert.False(Exists(@"Software\Classes\magnet"));
        Assert.False(Exists(@"Software\Classes\Inbound.Magnet"));
        Assert.False(Exists(@"Software\Classes\Inbound.Torrent"));
        Assert.False(Exists(@"Software\Classes\.torrent"));
        Assert.False(Exists(@"Software\Inbound Download Manager"));
        Assert.Null(Value(@"Software\RegisteredApplications", TorrentHandlerRegistration.ApplicationName));
        Assert.False(registration.Remove());
    }

    [Fact]
    public void Remove_leaves_another_apps_keys()
    {
        WriteOtherMagnetHandler();
        using (var extension = _root.CreateSubKey(@"Software\Classes\.torrent", writable: true))
        {
            extension.SetValue(null, "Other.Torrent");
            using var openWith = extension.CreateSubKey("OpenWithProgids", writable: true);
            openWith.SetValue("Other.Torrent", Array.Empty<byte>(), RegistryValueKind.None);
        }

        var registration = new TorrentHandlerRegistration(_root, Exe);
        registration.Register();
        registration.Remove();

        Assert.Equal($"\"{OtherExe}\" \"%1\"", Value(@"Software\Classes\magnet\shell\open\command"));
        Assert.Equal("Other.Torrent", Value(@"Software\Classes\.torrent"));
        using var kept = _root.OpenSubKey(@"Software\Classes\.torrent\OpenWithProgids");
        Assert.Equal(["Other.Torrent"], kept!.GetValueNames());
    }

    [Fact]
    public void Remove_from_another_copy_leaves_this_copys_registration()
    {
        // This copy's exe is there, so another copy's removal must not take it for a gone one.
        var folder = Path.Combine(Path.GetTempPath(), "InboundHandlerTest-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(folder);
        var exe = Path.Combine(folder, TorrentHandlerRegistration.WindowExecutableName);
        File.WriteAllBytes(exe, []);
        try
        {
            new TorrentHandlerRegistration(_root, exe).Register();
            Assert.False(new TorrentHandlerRegistration(_root, OtherExe).Remove());
        }
        finally
        {
            Directory.Delete(folder, recursive: true);
        }

        Assert.True(Exists(@"Software\Classes\magnet"));
        Assert.True(Exists(@"Software\Classes\Inbound.Torrent"));
        Assert.Equal("Inbound.Torrent", Value(@"Software\Classes\.torrent"));
        Assert.NotNull(Value(@"Software\RegisteredApplications", TorrentHandlerRegistration.ApplicationName));
    }

    [Fact]
    public void Register_keeps_an_all_users_apps_magnet_handler_and_torrent_default()
    {
        // A fake HKLM\Software\Classes under the throwaway key.
        using var machine = _root.CreateSubKey(@"Machine\Classes", writable: true);
        using (var magnet = machine.CreateSubKey(@"magnet\shell\open\command", writable: true))
            magnet.SetValue(null, $"\"{OtherExe}\" \"%1\"");
        using (var extension = machine.CreateSubKey(".torrent", writable: true))
            extension.SetValue(null, "Other.Torrent");

        new TorrentHandlerRegistration(_root, Exe, machine).Register();

        Assert.False(Exists(@"Software\Classes\magnet"));
        Assert.Null(Value(@"Software\Classes\.torrent"));
        // Still offered: Open with and Default apps.
        using (var openWith = _root.OpenSubKey(@"Software\Classes\.torrent\OpenWithProgids"))
            Assert.Contains("Inbound.Torrent", openWith!.GetValueNames());
        Assert.True(Exists(@"Software\Classes\Inbound.Magnet"));
    }

    [Fact]
    public void Register_takes_the_magnet_key_when_the_all_users_one_is_ours()
    {
        using var machine = _root.CreateSubKey(@"Machine\Classes", writable: true);
        using (var magnet = machine.CreateSubKey(@"magnet\shell\open\command", writable: true))
            magnet.SetValue(null, $"\"{Exe}\" --open-torrent \"%1\"");

        new TorrentHandlerRegistration(_root, Exe, machine).Register();

        Assert.Equal($"\"{Exe}\" --open-torrent \"%1\"", Value(@"Software\Classes\magnet\shell\open\command"));
        Assert.Equal("Inbound.Torrent", Value(@"Software\Classes\.torrent"));
    }

    [Fact]
    public void An_earlier_copys_magnet_key_moves_to_this_exe_and_is_removed_with_it()
    {
        const string oldExe = @"D:\Old\Inbound\app\DownloadManager.Desktop.WinUI.exe";
        new TorrentHandlerRegistration(_root, oldExe).Register();

        var registration = new TorrentHandlerRegistration(_root, Exe);
        Assert.True(registration.Register());
        Assert.Equal($"\"{Exe}\" --open-torrent \"%1\"", Value(@"Software\Classes\magnet\shell\open\command"));

        // Written by the old copy again (its exe is gone), then removed by this one.
        new TorrentHandlerRegistration(_root, oldExe).Register();
        registration.Remove();
        Assert.False(Exists(@"Software\Classes\magnet"));
    }

    [Theory]
    [InlineData("\"D:\\Old\\app\\DownloadManager.Desktop.WinUI.exe\" --open-torrent \"%1\"", true)]
    [InlineData("\"D:\\Old\\app\\DownloadManager.Desktop.WinUI.exe\" \"%1\"", false)]
    [InlineData("\"C:\\Other\\torrent.exe\" --open-torrent \"%1\"", false)]
    public void An_inbound_command_is_the_window_exe_with_our_arguments(string command, bool expected)
    {
        Assert.Equal(expected, TorrentHandlerRegistration.CommandIsInbound(command));
    }

    [Theory]
    [InlineData("\"C:\\Program Files\\Inbound\\app\\DownloadManager.Desktop.WinUI.exe\" --open-torrent \"%1\"", true)]
    [InlineData("\"c:\\program files\\inbound\\app\\downloadmanager.desktop.winui.exe\" \"%1\"", true)]
    [InlineData("\"C:\\Other\\torrent.exe\" \"%1\"", false)]
    [InlineData("C:\\Other\\torrent.exe %1", false)]
    [InlineData("\"unterminated", false)]
    public void A_command_points_at_this_exe_only_by_its_program(string command, bool expected)
    {
        Assert.Equal(expected, TorrentHandlerRegistration.CommandPointsAt(command, Exe));
    }
}

using System.Text.RegularExpressions;
using DownloadManager.Contracts;

namespace DownloadManager.Core.Tests;

/// <summary>The desktop window copies settings by hand before each save; a torrent setting it leaves out is reset to its
/// default by every save from the window (T2 review finding).</summary>
public sealed class TorrentSettingsCloneTests
{
    [Fact]
    public void The_window_copies_every_torrent_setting_when_it_saves()
    {
        var source = File.ReadAllText(WindowLocalFindingsTests.WinUiFile("MainWindow.xaml.cs"));
        var start = source.IndexOf("private static SettingsDto CloneSettings(", StringComparison.Ordinal);
        Assert.True(start >= 0, "CloneSettings was not found.");
        var end = source.IndexOf("};", start, StringComparison.Ordinal);
        var body = source[start..end];

        var torrentSettings = typeof(SettingsDto).GetProperties().Select(property => property.Name)
            .Where(name => name.StartsWith("Torrent", StringComparison.Ordinal)).ToList();
        Assert.Contains(nameof(SettingsDto.TorrentSeedRatio), torrentSettings);
        foreach (var name in torrentSettings)
            Assert.Matches(new Regex($@"\b{name} = source\.{name}\b"), body);
    }
}

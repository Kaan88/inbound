using DownloadManager.Desktop.WinUI;

namespace DownloadManager.Core.Tests;

/// <summary>MAC.6: a Ctrl shortcut's words as a Mac menu writes them.</summary>
public sealed class CommandKeyTests
{
    [Theory]
    [InlineData("Ctrl+N", "⌘N")]
    [InlineData("Ctrl+Shift+O", "⇧⌘O")]
    [InlineData("Ctrl+0", "⌘0")]
    [InlineData("F5", "F5")]
    public void MacMenusSayCommand(string windows, string mac) => Assert.Equal(mac, CommandKey.MacText(windows));
}

using DownloadManager.Cli;
using DownloadManager.Contracts;

namespace DownloadManager.Integration.Tests;

/// <summary>
/// What <c>dm torrent</c> prints for text it did not write (audit 4 L15) and for a reply that is not the result it asked
/// for (audit 4 M12). Pure functions of the CLI, called directly.
/// </summary>
public sealed class CliTorrentTextTests
{
    [Theory]
    [InlineData("https://user:pw@tracker.example/announce?passkey=abc123", "https://tracker.example/announce")]
    [InlineData("https://tracker.example/abc123def456/announce", "https://tracker.example/…")]
    [InlineData("udp://tracker.example:6969/announce.php?uk=secret", "udp://tracker.example:6969/announce.php")]
    [InlineData("http://tracker.example/", "http://tracker.example")]
    [InlineData("not an address", "Tracker")]
    public void ATrackerIsShownWithoutWhatCouldBeAPasskey(string url, string shown)
    {
        Assert.Equal(shown, TorrentCommands.TrackerShown(url));
    }

    [Fact]
    public void TextAPeerSendsLosesWhatATerminalWouldActOn()
    {
        var shown = TorrentCommands.Printable("qBit\u001b[2J‮torrent");
        Assert.DoesNotContain('\u001b', shown);
        Assert.DoesNotContain('‮', shown);
        Assert.StartsWith("qBit", shown, StringComparison.Ordinal);
        Assert.EndsWith("torrent", shown, StringComparison.Ordinal);
        Assert.Equal(string.Empty, TorrentCommands.Printable(null));
    }

    [Fact]
    public void AHostRefusalIsShownInTheHostsWords()
    {
        var reply = IpcMessage.Create(MessageTypes.HostActionResult,
            new HostActionResult { Ok = false, Message = "The torrent engine could not start: port in use." });
        Assert.Equal("failed: The torrent engine could not start: port in use.", TorrentCommands.NotDone(reply, "generic"));
    }

    [Fact]
    public void AHostThatDoesNotKnowTheCommandGetsTheGenericText()
    {
        var unknown = IpcMessage.Create(MessageTypes.HostActionResult,
            new HostActionResult { Ok = false, Message = "Unknown message type 'torrent.network'." });
        Assert.Equal("generic", TorrentCommands.NotDone(unknown, "generic"));

        var empty = IpcMessage.Create(MessageTypes.HostActionResult, new HostActionResult { Ok = false });
        Assert.Equal("generic", TorrentCommands.NotDone(empty, "generic"));

        var other = IpcMessage.Create(MessageTypes.SettingsResult, new SettingsResult { Ok = true });
        Assert.Equal("generic", TorrentCommands.NotDone(other, "generic"));
    }
}

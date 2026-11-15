using System.Xml.Linq;
using DownloadManager.Host;

namespace DownloadManager.Integration.Tests;

/// <summary>MAC.1: Start Inbound when I sign in is a LaunchAgent on macOS.</summary>
public sealed class MacLoginItemTests
{
    [Fact]
    public void TheLaunchAgentStartsTheLauncherInTheBackgroundAtSignIn()
    {
        var text = MacLoginItem.Plist("/Applications/My <Apps> & Co/Inbound.app/Contents/MacOS/InboundLauncher");
        Assert.StartsWith("<?xml", text);
        // Parsed without the DOCTYPE line, which names Apple's DTD by address.
        var plist = XDocument.Parse(string.Join('\n', text.Split('\n').Where(line => !line.StartsWith("<!DOCTYPE", StringComparison.Ordinal))));
        var values = plist.Root!.Element("dict")!.Elements().ToList();
        string After(string key) => values[values.FindIndex(element => element.Name == "key" && element.Value == key) + 1].Value;

        Assert.Equal(MacLoginItem.Label, After("Label"));
        var arguments = values[values.FindIndex(element => element.Value == "ProgramArguments") + 1].Elements("string").Select(e => e.Value);
        // A path with XML's own characters survives as it is.
        Assert.Equal(["/Applications/My <Apps> & Co/Inbound.app/Contents/MacOS/InboundLauncher", "--background"], arguments);
        Assert.Equal("true", values[values.FindIndex(element => element.Value == "RunAtLoad") + 1].Name.LocalName);
        Assert.Equal("Aqua", After("LimitLoadToSessionType"));
    }

    [Fact]
    public void ItLivesInTheUsersLaunchAgents() =>
        Assert.EndsWith(Path.Combine("Library", "LaunchAgents", "local.inbound.host.plist"), MacLoginItem.PathOf());
}

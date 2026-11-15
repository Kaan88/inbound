using DownloadManager.Host;

namespace DownloadManager.Integration.Tests;

/// <summary>MAC.5: the Mac's own tools for what the Linux Host does over D-Bus.</summary>
public sealed class MacDesktopTests
{
    [Fact]
    public void ANotificationsTextIsAnArgumentNotPartOfTheScript()
    {
        var start = MacDesktop.NotifyStart("Done \" & quit", "file\" with title \"x");
        Assert.Equal("/usr/bin/osascript", start.FileName);
        Assert.Equal(["-e", "on run argv", "-e", "display notification (item 2 of argv) with title (item 1 of argv)", "-e", "end run",
            "Done \" & quit", "file\" with title \"x"], start.ArgumentList);
    }

    [Theory]
    [InlineData("shutdown", "aevtrsdn", "shut down")]
    [InlineData("restart", "aevtrrst", "restart")]
    [InlineData("logoff", "aevtrlgo", "sign out")]
    [InlineData(null, "aevtrsdn", "shut down")]
    public void CompletionPowerActionsAreLoginwindowsAppleEvents(string? mode, string code, string what)
    {
        var (program, arguments, said) = MacDesktop.Shutdown(mode);
        Assert.Equal("/usr/bin/osascript", program);
        Assert.Equal(["-e", $"tell application \"loginwindow\" to «event {code}»"], arguments);
        Assert.Equal(what, said);
    }

    [Fact]
    public void SleepIsHeldByCaffeinateTiedToTheHost() =>
        Assert.Equal(["-i", "-w", "4242"], MacSleepPrevention.Start(4242).ArgumentList);

    [Fact]
    public void TheKeychainCommandQuotesTheFolderAndKey()
    {
        Assert.Equal("\"a \\\"b\\\" \\\\ c\"", MacKeychainKeyStore.Quote("a \"b\" \\ c"));
        Assert.Equal($"add-generic-password -U -s \"{MacKeychainKeyStore.Service}\" -a \"/Users/me/Library/Application Support/Inbound\" -w \"AAEC\"",
            MacKeychainKeyStore.AddCommand("/Users/me/Library/Application Support/Inbound", "AAEC"));
    }
}

public sealed class MacSoundsAndClipboardTests
{
    [Fact]
    public void EventSoundsFollowTheWindowsRules()
    {
        var none = new Dictionary<string, string>();
        Assert.Equal("/System/Library/Sounds/Glass.aiff", MacSounds.Resolve("completed", none, _ => true));
        Assert.Null(MacSounds.Resolve("failed", none, _ => true));
        Assert.Equal("/System/Library/Sounds/Basso.aiff", MacSounds.Resolve("failed", new Dictionary<string, string> { ["failed"] = "@system" }, _ => true));
        Assert.Null(MacSounds.Resolve("completed", new Dictionary<string, string> { ["Completed"] = "" }, _ => true));
        Assert.Equal("/Users/me/ding.wav", MacSounds.Resolve("queueFinished", new Dictionary<string, string> { ["queueFinished"] = "/Users/me/ding.wav" }, _ => true));
        // A file that's gone: the system sound.
        Assert.Equal("/System/Library/Sounds/Hero.aiff", MacSounds.Resolve("queueFinished", new Dictionary<string, string> { ["queueFinished"] = "/gone.wav" }, _ => false));
        Assert.Null(MacSounds.Resolve("other", none, _ => true));
    }

    [Fact]
    public void CopiedTextGivesItsFirstAddressOnceAMinute()
    {
        var filter = new CopiedUrlFilter();
        var now = DateTimeOffset.UnixEpoch;
        Assert.Equal("https://example.com/a.zip", filter.Accept("see https://example.com/a.zip here", now));
        Assert.Null(filter.Accept("see https://example.com/a.zip here", now.AddMinutes(5)));
        Assert.Null(filter.Accept("https://example.com/a.zip", now.AddSeconds(10)));
        Assert.Equal("https://example.com/a.zip", filter.Accept("again https://example.com/a.zip", now.AddMinutes(2)));
        Assert.Null(filter.Accept("no address", now));
        Assert.Null(filter.Accept("  ", now));
    }
}

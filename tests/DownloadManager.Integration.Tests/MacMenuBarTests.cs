using DownloadManager.Contracts;
using DownloadManager.Host;
using DownloadManager.Runtime;

namespace DownloadManager.Integration.Tests;

/// <summary>MAC.6: the Host's half of the menu-bar helper's protocol, and the pause on a costly network or in Low Power Mode.</summary>
public sealed class MacMenuBarTests
{
    // Speeds follow the Mac's number format ("1,5 GB/s" in many languages); the tests pin one.
    public MacMenuBarTests() => System.Globalization.CultureInfo.CurrentCulture = System.Globalization.CultureInfo.InvariantCulture;

    [Fact]
    public void TheStatusListsRunningDownloadsFastestFirst()
    {
        var slow = new JobDto { Id = Guid.NewGuid(), FileName = "slow.bin", State = "downloading", BytesPerSecond = 1024, Progress = 0.255 };
        var fast = new JobDto { Id = Guid.NewGuid(), FileName = "fast.bin", State = "downloading", BytesPerSecond = 3 * 1024 * 1024, Progress = null };
        var done = new JobDto { Id = Guid.NewGuid(), FileName = "done.bin", State = "completed", BytesPerSecond = 0, Progress = 1 };

        var json = MacMenuBar.StatusJson([slow, done, fast]);

        Assert.Equal(
            $$"""{"active":2,"speed":"3.0 MB/s","items":[{"id":"{{fast.Id:D}}","name":"fast.bin","percent":null,"state":"downloading"},{"id":"{{slow.Id:D}}","name":"slow.bin","percent":25,"state":"downloading"}],"note":null}""",
            json);
        Assert.Equal("""{"active":0,"speed":null,"items":[],"note":null}""", MacMenuBar.StatusJson([done]));
        Assert.EndsWith(""","note":"Downloads wait: Low Power Mode"}""", MacMenuBar.StatusJson([done], "Low Power Mode"));
        var paused = new JobDto { Id = Guid.NewGuid(), FileName = "paused.bin", State = "paused", Progress = 0.5 };
        Assert.Contains($$"""{"id":"{{paused.Id:D}}","name":"paused.bin","percent":50,"state":"paused"}""", MacMenuBar.StatusJson([fast, paused]));
    }

    [Theory]
    [InlineData(500, "500 B/s")]
    [InlineData(2048, "2 KB/s")]
    [InlineData(1.5 * 1024 * 1024 * 1024, "1.5 GB/s")]
    public void SpeedsAreShort(double rate, string expected) => Assert.Equal(expected, MacMenuBar.Speed(rate));

    [Fact]
    public void OnlyWebLinksAreTakenFromDroppedText()
    {
        var links = MacMenuBar.WebLinks("see <https://a.example/x.zip>, and ftp://b.example/y -rf http://c.example/ https://a.example/x.zip file:///etc/passwd");
        Assert.Equal(["https://a.example/x.zip", "http://c.example/"], links);
    }

    [Fact]
    public void AMenuBarAddIsNotCalledAClipboardOne()
    {
        Assert.Equal("Download added", MacMenuBar.AddedTitle("Clipboard download added"));
        Assert.Equal("Something else", MacMenuBar.AddedTitle("Something else"));
    }

    [Fact]
    public void TheHelperIsFoundOnlyInItsOwnBundle()
    {
        var root = Directory.CreateTempSubdirectory("inbound-menu-");
        try
        {
            var host = Path.Combine(root.FullName, "Inbound.app", "Contents", "Resources", "host");
            var bundled = Path.Combine(root.FullName, "Inbound.app", "Contents", "Helpers", MacMenuBar.HelperApp, "Contents", "MacOS", MacMenuBar.HelperName);
            Directory.CreateDirectory(host);
            Assert.Null(MacMenuBar.FindHelper(host));
            File.WriteAllText(Path.Combine(host, MacMenuBar.HelperName), string.Empty);
            Assert.Null(MacMenuBar.FindHelper(host)); // outside a bundle it couldn't notify, so it isn't used
            Directory.CreateDirectory(Path.GetDirectoryName(bundled)!);
            File.WriteAllText(bundled, string.Empty);
            Assert.Equal(Path.GetFullPath(bundled), MacMenuBar.FindHelper(host));
        }
        finally
        {
            root.Delete(recursive: true);
        }
    }

    [Fact]
    public async Task SavingHoldsWhileEnabledAndCostlyOrLowPower()
    {
        var holds = new List<bool>();
        var saving = new SavingPauses(hold => { holds.Add(hold); return Task.CompletedTask; });

        await saving.NetworkAsync("expensive");
        Assert.Null(saving.Reason); // off: no hold
        await saving.EnabledAsync(true);
        Assert.Equal("a costly network", saving.Reason);
        await saving.PowerAsync("low");
        Assert.Equal("Low Power Mode", saving.Reason);
        await saving.NetworkAsync("normal");
        Assert.True(holds[^1]); // still in Low Power Mode
        await saving.PowerAsync("normal");
        Assert.False(holds[^1]);
        Assert.Equal([false, true, true, true, false], holds);
    }

    [Fact]
    public async Task ALostHelperOrTheSettingTurnedOffEndsTheHold()
    {
        var hold = false;
        var saving = new SavingPauses(next => { hold = next; return Task.CompletedTask; });
        await saving.EnabledAsync(true);
        await saving.NetworkAsync("constrained");
        Assert.True(hold);
        await saving.HelperEndedAsync();
        Assert.False(hold);
        await saving.PowerAsync("low");
        Assert.True(hold);
        await saving.EnabledAsync(false);
        Assert.False(hold);
    }
}

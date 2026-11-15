using DownloadManager.Contracts;
using DownloadManager.Host;
using DownloadManager.Runtime;

namespace DownloadManager.Integration.Tests;

/// <summary>MAC.6: the Host's half of the menu-bar helper's protocol, and the pause on a costly network or in Low Power Mode.</summary>
public sealed class MacMenuBarTests
{
    [Fact]
    public void TheStatusListsRunningDownloadsFastestFirst()
    {
        var slow = new JobDto { Id = Guid.NewGuid(), FileName = "slow.bin", State = "downloading", BytesPerSecond = 1024, Progress = 0.255 };
        var fast = new JobDto { Id = Guid.NewGuid(), FileName = "fast.bin", State = "downloading", BytesPerSecond = 3 * 1024 * 1024, Progress = null };
        var done = new JobDto { Id = Guid.NewGuid(), FileName = "done.bin", State = "completed", BytesPerSecond = 0, Progress = 1 };

        var json = MacMenuBar.StatusJson([slow, done, fast]);

        Assert.Equal(
            $$"""{"active":2,"speed":"3.0 MB/s","items":[{"id":"{{fast.Id:D}}","name":"fast.bin","percent":null,"state":"downloading"},{"id":"{{slow.Id:D}}","name":"slow.bin","percent":25,"state":"downloading"}]}""",
            json);
        Assert.Equal("""{"active":0,"speed":null,"items":[]}""", MacMenuBar.StatusJson([done]));
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
    public void TheHelperIsFoundInItsOwnBundleFirst()
    {
        var root = Directory.CreateTempSubdirectory("inbound-menu-");
        try
        {
            var host = Path.Combine(root.FullName, "Inbound.app", "Contents", "Resources", "host");
            var bundled = Path.Combine(root.FullName, "Inbound.app", "Contents", "Helpers", MacMenuBar.HelperApp, "Contents", "MacOS", MacMenuBar.HelperName);
            Directory.CreateDirectory(host);
            Assert.Null(MacMenuBar.FindHelper(host));
            File.WriteAllText(Path.Combine(host, MacMenuBar.HelperName), string.Empty);
            Assert.Equal(Path.Combine(host, MacMenuBar.HelperName), MacMenuBar.FindHelper(host));
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
    public async Task SavingPausesWhatRunsAndResumesOnlyThat()
    {
        var running = Guid.NewGuid();
        var userPaused = Guid.NewGuid();
        var resumedByUser = Guid.NewGuid();
        var states = new Dictionary<Guid, string> { [running] = "downloading", [userPaused] = "paused", [resumedByUser] = "connecting" };
        var resumed = new List<Guid>();
        var saving = new SavingPauses(
            () => states.Select(pair => (pair.Key, pair.Value)).ToList(),
            id => { states[id] = "paused"; return Task.CompletedTask; },
            id => { resumed.Add(id); states[id] = "downloading"; return Task.CompletedTask; },
            _ => { });

        await saving.NetworkAsync("expensive");
        Assert.Equal("downloading", states[running]); // off: nothing happens

        await saving.EnabledAsync(true);
        saving.Observe(running, "downloading"); // a progress event from before the pause took effect
        saving.Observe(running, "paused");
        saving.Observe(resumedByUser, "paused");
        Assert.Equal("paused", states[running]);
        Assert.Equal("paused", states[resumedByUser]);
        Assert.Equal(2, saving.Paused.Count);

        states[resumedByUser] = "downloading"; // the user resumed it on the hotspot, then paused it again
        saving.Observe(resumedByUser, "downloading");
        states[resumedByUser] = "paused";
        await saving.PowerAsync("low");
        await saving.NetworkAsync("normal"); // still in Low Power Mode
        Assert.Empty(resumed);

        await saving.PowerAsync("normal");
        Assert.Equal([running], resumed);
        Assert.Equal("paused", states[userPaused]);
        Assert.Empty(saving.Paused);
    }

    [Fact]
    public async Task TurningTheSettingOffResumesWhatItPaused()
    {
        var id = Guid.NewGuid();
        var states = new Dictionary<Guid, string> { [id] = "downloading" };
        var saving = new SavingPauses(
            () => states.Select(pair => (pair.Key, pair.Value)).ToList(),
            job => { states[job] = "paused"; return Task.CompletedTask; },
            job => { states[job] = "downloading"; return Task.CompletedTask; },
            _ => { });
        await saving.EnabledAsync(true);
        await saving.NetworkAsync("constrained");
        Assert.Equal("paused", states[id]);
        await saving.EnabledAsync(false);
        Assert.Equal("downloading", states[id]);
    }
}

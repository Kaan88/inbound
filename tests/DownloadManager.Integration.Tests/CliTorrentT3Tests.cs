using System.Text.Json;
using DownloadManager.Contracts;
using DownloadManager.Torrents;

namespace DownloadManager.Integration.Tests;

/// <summary>
/// T3: <c>dm torrent inspect|add|files|priority|options|recheck|announce|details</c> against the real Host. The torrent is
/// added to start later, so no worker starts (a stand-in would show if one did).
/// </summary>
public sealed class CliTorrentT3Tests
{
    [Trait("Platform", "Windows")]
    [Fact]
    public async Task TheCliAddsATorrentAndShowsAndChangesItsFilesAndOptions()
    {
        await using var host = await SandboxHost.StartAsync("https://example.invalid/v1/api/", sandbox =>
        {
            var worker = Path.Combine(sandbox.Root, TorrentSetup.WorkerFileName);
            File.Copy(Path.Combine(Environment.SystemDirectory, "cmd.exe"), worker);
            sandbox.ExtraEnvironment[TorrentSetup.WorkerVariable] = worker;
        });
        await using (var setup = await host.ConnectAsync())
        {
            var settings = (await setup.AskAsync<SettingsResult>(MessageTypes.SettingsGet, new { })).Settings!;
            settings.TorrentsEnabled = true;
            Assert.True((await setup.AskAsync<SettingsResult>(MessageTypes.SettingsUpdate, new SettingsUpdateRequest { Settings = settings })).Ok);
        }

        var file = Path.Combine(host.Root, "film.torrent");
        var (bytes, hash) = HostCommandDispatcherTests.SingleFileTorrent("film.mkv", 100_000);
        await File.WriteAllBytesAsync(file, bytes);

        var inspect = await host.RunCliAsync(null, "torrent", "inspect", file, "--dest", host.Destination);
        Assert.True(inspect.ExitCode == 0, inspect.All);
        Assert.Contains(hash, inspect.Output);
        Assert.Contains("film.mkv", inspect.Output);
        Assert.Contains("free)", inspect.Output);

        var add = await host.RunCliAsync(null, "torrent", "add", file, "--dest", host.Destination, "--later", "--priority", "0=high", "--down", "5000", "--sequential", "--json");
        Assert.True(add.ExitCode == 0, add.All);
        var job = JsonSerializer.Deserialize<TorrentAddResult>(add.Output, IpcJson.Options)!.Job!;
        Assert.Equal((5000L, true), (job.Torrent!.DownloadLimit, job.Torrent.Sequential));
        var id = job.Id.ToString();

        var files = await host.RunCliAsync(null, "torrent", "files", id);
        Assert.True(files.ExitCode == 0, files.All);
        Assert.Contains("high", files.Output);

        var priority = await host.RunCliAsync(null, "torrent", "priority", id, "0=low");
        Assert.True(priority.ExitCode == 0, priority.All);
        Assert.Contains("low", (await host.RunCliAsync(null, "torrent", "files", id)).Output);
        var wrong = await host.RunCliAsync(null, "torrent", "priority", id, "0=urgent");
        Assert.Equal(2, wrong.ExitCode);

        var options = await host.RunCliAsync(null, "torrent", "options", id, "--up", "7000", "--seed-goal", "ratio:2", "--sequential", "off");
        Assert.True(options.ExitCode == 0, options.All);
        Assert.Contains("seeding goal: ratio 2", options.Output);
        Assert.Contains("in order: off", options.Output);
        var shown = await host.RunCliAsync(null, "torrent", "options", id);
        Assert.Contains("download limit: 4.9 KB/s", shown.Output);
        Assert.Contains("upload limit: 6.8 KB/s", shown.Output);
        var badGoal = await host.RunCliAsync(null, "torrent", "options", id, "--seed-goal", "forever");
        Assert.Equal(1, badGoal.ExitCode);

        // Nothing is on disk yet, and it isn't running.
        var recheck = await host.RunCliAsync(null, "torrent", "recheck", id);
        Assert.Equal(1, recheck.ExitCode);
        Assert.Contains("Nothing of this torrent is on disk", recheck.Error);
        var announce = await host.RunCliAsync(null, "torrent", "announce", id);
        Assert.Equal(1, announce.ExitCode);
        Assert.Contains("isn't running", announce.Error);
        var details = await host.RunCliAsync(null, "torrent", "details", id);
        Assert.True(details.ExitCode == 0, details.All);
        Assert.Contains(hash, details.Output);
        Assert.Contains("from: file", details.Output);

        Assert.DoesNotContain(host.LogLines(), line => line.Contains("torrent worker:", StringComparison.Ordinal));
    }
}

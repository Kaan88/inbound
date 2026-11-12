using System.Diagnostics;
using DownloadManager.Contracts;
using DownloadManager.Core;
using DownloadManager.Torrents;

namespace DownloadManager.Integration.Tests;

/// <summary>
/// T1 step 7: the real Host with a worker it could start. With <c>DOWNLOADMANAGER_TORRENTS=off</c> it offers no torrents
/// and refuses the commands; with torrents off in Options it offers them but refuses to add. Either way no worker starts,
/// so no torrent socket is opened.
/// </summary>
public sealed class TorrentKillSwitchHostTests
{
    private const string Magnet = "magnet:?xt=urn:btih:0123456789abcdef0123456789abcdef01234567&dn=kill-switch.bin";

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task NoWorkerStartsWithTheKillSwitchOrTheSettingOff(bool killSwitch)
    {
        string? worker = null;
        await using var host = await SandboxHost.StartAsync("https://example.invalid/v1/api/", sandbox =>
        {
            // A stand-in the Host would start as its worker: cmd.exe waits on its input, so a started copy stays visible.
            worker = Path.Combine(sandbox.Root, TorrentSetup.WorkerFileName);
            File.Copy(Path.Combine(Environment.SystemDirectory, "cmd.exe"), worker);
            sandbox.ExtraEnvironment[TorrentSetup.WorkerVariable] = worker;
            if (killSwitch) sandbox.ExtraEnvironment[TorrentAvailability.KillSwitchVariable] = "off";
        });

        await using var connection = await host.ConnectAsync();
        if (killSwitch) Assert.DoesNotContain("torrents", connection.Hello.Capabilities);
        else Assert.Contains("torrents", connection.Hello.Capabilities);

        var settings = (await connection.AskAsync<SettingsResult>(MessageTypes.SettingsGet, new { })).Settings!;
        Assert.False(settings.TorrentsEnabled);
        if (killSwitch)
        {
            // Turned on in Options, the kill switch still wins.
            settings.TorrentsEnabled = true;
            Assert.True((await connection.AskAsync<SettingsResult>(MessageTypes.SettingsUpdate, new SettingsUpdateRequest { Settings = settings })).Ok);
        }

        var request = new TorrentAddRequest { Input = Magnet, Destination = host.Destination };
        var reply = await connection.RequestAsync(TorrentMessageTypes.Add, request);
        if (killSwitch)
        {
            Assert.NotEqual(TorrentMessageTypes.AddResult, reply.Type);
        }
        else
        {
            var result = reply.GetPayload<TorrentAddResult>();
            Assert.False(result.Ok);
            Assert.Contains("turned off", result.Error);
        }

        var inspect = await connection.RequestAsync(TorrentMessageTypes.Inspect, new TorrentInspectRequest { Input = Magnet });
        if (!killSwitch) Assert.False(inspect.GetPayload<TorrentInspectResult>().Ok);
        else Assert.NotEqual(TorrentMessageTypes.InspectResult, inspect.Type);

        await Task.Delay(1500);
        Assert.DoesNotContain(connection.Events, message => message.Type == MessageTypes.JobEvent);
        Assert.Empty(Started(worker!));
        Assert.DoesNotContain(host.LogLines(), line => line.Contains("torrent worker:", StringComparison.Ordinal));
        if (killSwitch) Assert.Contains(host.LogLines(), line => line.Contains("Torrents are unavailable: DOWNLOADMANAGER_TORRENTS=off.", StringComparison.Ordinal));
    }

    private static List<int> Started(string worker)
    {
        var found = new List<int>();
        foreach (var process in Process.GetProcessesByName(Path.GetFileNameWithoutExtension(worker)))
        {
            using (process)
            {
                try
                {
                    if (string.Equals(process.MainModule?.FileName, worker, StringComparison.OrdinalIgnoreCase)) found.Add(process.Id);
                }
                catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException)
                {
                    // Gone, or another user's process.
                }
            }
        }

        return found;
    }
}

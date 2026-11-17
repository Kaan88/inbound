using DownloadManager.Contracts;
using DownloadManager.Mobile;
using DownloadManager.Mobile.Remote;
using TestServer;

namespace DownloadManager.Server.Tests;

/// <summary>
/// ANDROID.7: the phone app's remote connection (<see cref="RemoteHostConnection"/>, paired through
/// <see cref="HostDirectory"/>) against a real server on loopback, driving the Downloads screen's model: add, pause,
/// resume, delete and the event stream; and a pairing removed on the server stopping the phone.
/// </summary>
public sealed class MobileRemoteHostTests
{
    private sealed class Secrets : ISecretStore
    {
        private readonly Dictionary<string, string> _values = new();
        public string? Read(string name) => _values.GetValueOrDefault(name);
        public void Write(string name, string secret) => _values[name] = secret;
        public void Delete(string name) => _values.Remove(name);
    }

    /// <summary>The state folder is under the repo's git-ignored artifacts, never a user folder.</summary>
    private static string Work()
    {
        var root = AppContext.BaseDirectory;
        while (root is not null && !File.Exists(Path.Combine(root, "DownloadManager.slnx"))) root = Path.GetDirectoryName(root);
        var work = Path.Combine(root ?? AppContext.BaseDirectory, "artifacts", "test-work", "mobile-remote-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Path.Combine(work, "downloads"));
        return work;
    }

    private static async Task Until(Func<bool> condition, string what, int seconds = 30)
    {
        var until = DateTime.UtcNow.AddSeconds(seconds);
        while (!condition())
        {
            if (DateTime.UtcNow > until) throw new TimeoutException("Never: " + what);
            await Task.Delay(20);
        }
    }

    private static async Task<(ServerHarness Server, HostDirectory Hosts, HostEntry Host)> PairedAsync(string work)
    {
        var server = await AuthServer.StartAsync(work: work);
        using (await AuthServer.SetUpAsync(server)) { }
        var hosts = new HostDirectory(new HostListFile(Path.Combine(work, "phone", "hosts.json")), new Secrets(), "Inbound on test phone");
        var paired = await hosts.PairAsync(new PairRequest(server.Base.ToString(), AuthServer.Password, "Test server", PlainHttpConfirmed: false));
        Assert.True(paired.Ok, paired.Error);
        Assert.True(hosts.Select(paired.Host!.Id));
        return (server, hosts, paired.Host);
    }

    [Fact]
    public async Task TheDownloadsModelAddsPausesResumesAndDeletesOnTheServerAndFollowsItsEvents()
    {
        var work = Work();
        try
        {
            await using var files = await TestServerHost.StartAsync();
            var (server, hosts, host) = await PairedAsync(work);
            await using var _ = server;
            await using var connection = HostConnections.ForCurrent(hosts, () => throw new InvalidOperationException("not the phone"));
            var remote = Assert.IsType<RemoteHostConnection>(connection);
            var gate = new object();
            using var model = new DownloadsModel(connection, act => { lock (gate) act(); }, TimeSpan.Zero);
            var shown = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            model.FirstSnapshotShown += () => shown.TrySetResult();

            await model.StartAsync(CancellationToken.None);
            await shown.Task.WaitAsync(TimeSpan.FromSeconds(10));
            Assert.Equal(RemoteConnectionStatus.Connected, remote.State.Status);
            Assert.Equal(HostKinds.Server, remote.Hello!.HostKind);
            Assert.Equal(Path.GetFullPath(server.Root), model.DefaultDownloadDirectory);
            Assert.Empty(model.Rows);
            Assert.Equal(HostKinds.Server, hosts.Hosts.Single(h => h.Id == host.Id).HostKind);

            // Slow enough to pause: 2 MB at 256 KB/s.
            var added = await model.AddAsync($"{files.BaseAddress}/file/phone-remote.bin?size=2097152&seed=3&bps=262144");
            Assert.True(added.Accepted, added.Error);
            var id = added.Job!.Id;
            JobRow Row() { lock (gate) return model.Rows.Single(r => r.Id == id); }
            await Until(() => { lock (gate) return model.Rows.Any(r => r.Id == id); }, "the row from the added event");
            // C5's one-shot figures: one jobs.query with the paired token, on its own connection.
            var figures = await hosts.FiguresAsync(hosts.Current);
            Assert.NotNull(figures);
            Assert.Equal(1, figures.Active + figures.Queued);

            var paused = await connection.RequestAsync(IpcMessage.Create(MessageTypes.JobPause, new JobActionRequest { JobId = id }), CancellationToken.None);
            Assert.Equal(MessageTypes.JobActionResult, paused.Type);
            await Until(() => Row().Subtitle.Contains("Paused", StringComparison.OrdinalIgnoreCase), "the paused row");

            var resumed = await connection.RequestAsync(IpcMessage.Create(MessageTypes.JobResume, new JobActionRequest { JobId = id }), CancellationToken.None);
            Assert.Equal(MessageTypes.JobActionResult, resumed.Type);
            await Until(() => Row().Kind == StatusKind.Done, "the completed row", seconds: 60);
            var final = Path.Combine(server.Root, "phone-remote.bin");
            Assert.Equal(files.ContentFor("phone-remote.bin", 2097152, 3), await File.ReadAllBytesAsync(final));

            var removed = await connection.RequestAsync(IpcMessage.Create(MessageTypes.JobRemove, new JobActionRequest { JobId = id, DeleteFile = true }), CancellationToken.None);
            Assert.Equal(MessageTypes.JobActionResult, removed.Type);
            await Until(() => { lock (gate) return model.Rows.Count == 0; }, "the row removed by the event");
            await Until(() => !File.Exists(final), "the file deleted on the server");
        }
        finally
        {
            try { Directory.Delete(work, recursive: true); }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException) { }
        }
    }

    [Fact]
    public async Task APairingRemovedOnTheServerStopsThePhoneAndRemovingItOnThePhoneUnpairs()
    {
        var work = Work();
        try
        {
            var (server, hosts, host) = await PairedAsync(work);
            await using var _ = server;
            var target = hosts.TargetFor(host)!;
            await using (var connection = (RemoteHostConnection)HostConnections.ForCurrent(hosts, () => throw new InvalidOperationException()))
            {
                await connection.OpenAsync(_ => { }, CancellationToken.None);
                Assert.Equal(RemoteConnectionStatus.Connected, connection.State.Status);

                // The server forgets the token (as its web UI's Remove does): its socket closes, the next try is refused.
                Assert.True(await RemoteServerHttp.UnpairAsync(target));
                await Until(() => connection.State.Status == RemoteConnectionStatus.Stopped, "stopped after the pairing ended");
                Assert.Contains("connect to it again", connection.State.Message);
            }

            // Paired again, then removed on the phone: the server forgets that token too.
            var again = await hosts.PairAsync(new PairRequest(server.Base.ToString(), AuthServer.Password, null, false));
            Assert.True(again.Ok, again.Error);
            var second = hosts.TargetFor(again.Host!)!;
            Assert.True(await hosts.RemoveAsync(again.Host!.Id));
            Assert.True(hosts.Current.IsPhone);
            await Assert.ThrowsAsync<RemotePairingEndedException>(() => RemoteServerHttp.SendIpcAsync(second, IpcMessage.Create(MessageTypes.HostPing, new { })));
        }
        finally
        {
            try { Directory.Delete(work, recursive: true); }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException) { }
        }
    }
}

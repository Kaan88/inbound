namespace DownloadManager.Torrents.Tests;

/// <summary>T3 review: the progress interval the worker keeps is the last one asked for, however the calls cross.</summary>
public sealed partial class EngineTests
{
    [Fact]
    public async Task TheLastProgressIntervalAskedForIsTheOneTheWorkerKeeps()
    {
        FakeTorrentEngine? fake = null;
        await using var supervisor = new TorrentWorkerSupervisor(() => fake = new FakeTorrentEngine(),
            new TorrentSupervisorOptions { DrainDelay = TimeSpan.FromSeconds(30) });
        var engine = new TorrentDownloadEngine(supervisor, new MemoryStore(), Log);
        using var cts = new CancellationTokenSource();
        var run = engine.ExecuteAsync(Job("rate"), null, null, cts.Token);
        await WaitForAsync(() => fake?.Torrents.Count == 1 ? fake : null, "the torrent added");

        // Watches that end while others start, from many threads at once; the last word is "watched".
        await Task.WhenAll(Enumerable.Range(0, 8).Select(t => Task.Run(() =>
        {
            for (var i = 0; i < 100; i++) engine.SetProgressWatched((i + t) % 2 == 0);
        })));
        engine.SetProgressWatched(true);

        int? LastSent() => fake!.Sent.LastOrDefault(s => s.Command == "setProgressInterval").Arguments?["ms"]?.GetValue<int>();
        await WaitForAsync(() => LastSent() == TorrentDownloadEngine.WatchedProgressMs ? "" : null, "the watched interval sent");
        await Task.Delay(300); // sends still queued may not change it
        Assert.Equal(TorrentDownloadEngine.WatchedProgressMs, LastSent());
        Assert.Equal(TorrentDownloadEngine.WatchedProgressMs, supervisor.ProgressIntervalMs);

        cts.Cancel();
        await run.WaitAsync(Long);
    }
}

using System.Security.Cryptography;
using DownloadManager.Contracts;
using DownloadManager.Core;
using DownloadManager.Persistence;

namespace DownloadManager.Integration.Tests;

/// <summary>Review 2 M3 and L1: what a torrent job is after the Host starts again.</summary>
public sealed partial class HostCommandDispatcherTests
{
    /// <summary>Review 2 M3 (audit 2 H2 for torrents): a finished torrent whose files are gone stays completed with a
    /// note, and neither Resume All nor a queue start downloads it again.</summary>
    [Fact]
    public async Task AFinishedTorrentWhoseFilesAreGoneStaysCompletedWithANote()
    {
        var (service, _, job) = await CompletedTorrentAsync();
        await UntilAsync(() => service.GetSnapshot().Jobs.FirstOrDefault(j => j.Id == job.Id && j.Torrent!.Seeding));
        await service.ShutdownAsync();
        _service = null;
        // The in-memory engine writes no files: the finished one is absent, its folder is there.
        Directory.CreateDirectory(Path.GetDirectoryName(job.FinalFilePath)!);
        Assert.False(File.Exists(job.FinalFilePath) || Directory.Exists(job.FinalFilePath));

        var next = await TorrentServiceAsync();
        var restored = next.GetJob(job.Id)!;
        Assert.Equal("completed", restored.State);
        Assert.Equal("File not found in its folder", restored.StatusNote);
        Assert.False(restored.Torrent!.Seeding);
        await next.ResumeAllAsync();
        Assert.True((await next.QueueActionAsync(new QueueActionRequest { QueueId = 0, Action = "start" })).Ok);
        Assert.Equal("completed", next.GetJob(job.Id)!.State);

        // The files back: the note goes at the next start.
        await next.ShutdownAsync();
        _service = null;
        await File.WriteAllBytesAsync(job.FinalFilePath, new byte[16]);
        var third = await TorrentServiceAsync();
        Assert.Equal("completed", third.GetJob(job.Id)!.State);
        Assert.Null(third.GetJob(job.Id)!.StatusNote);
    }

    /// <summary>Review 2 M3: a folder that can't be reached (a drive not there yet) says nothing, and the torrent seeds
    /// as it did.</summary>
    [Fact]
    public async Task AFinishedTorrentWhoseFolderIsUnreachableStaysCompletedWithoutANote()
    {
        var (service, _, job) = await CompletedTorrentAsync();
        await UntilAsync(() => service.GetSnapshot().Jobs.FirstOrDefault(j => j.Id == job.Id && j.Torrent!.Seeding));
        await service.ShutdownAsync();
        _service = null;
        var folder = Path.GetDirectoryName(job.FinalFilePath)!;
        if (Directory.Exists(folder)) Directory.Delete(folder, recursive: true);

        var next = await TorrentServiceAsync();
        var restored = next.GetJob(job.Id)!;
        Assert.Equal("completed", restored.State);
        Assert.Null(restored.StatusNote);
    }

    /// <summary>Review 2 M3: a torrent an older build failed for its missing files is completed again.</summary>
    [Fact]
    public async Task AFinishedTorrentAnOlderBuildMarkedFailedForMissingFilesIsCompletedAgain()
    {
        var (service, _, job) = await CompletedTorrentAsync(seedAfterDownload: false);
        await service.ShutdownAsync();
        _service = null;
        var store = new JsonJobStore(_paths);
        await store.InitializeAsync();
        var stored = (await store.LoadAllAsync()).Single(j => j.Id == job.Id);
        stored.State = DownloadState.Failed;
        stored.Failure = new JobFailure(FailureKind.DestinationUnavailable,
            "The completed files are missing from their destination.", false, false, DateTimeOffset.UtcNow);
        await store.SaveAsync(stored);
        Directory.CreateDirectory(Path.GetDirectoryName(job.FinalFilePath)!);

        var next = await TorrentServiceAsync();
        var restored = next.GetJob(job.Id)!;
        Assert.Equal("completed", restored.State);
        Assert.Equal("File not found in its folder", restored.StatusNote);
    }

    /// <summary>Review 2 L7: deleting a torrent whose finalize was stopped part-way takes the files it had already
    /// renamed (under the top-level name it had picked) as well as the part files. A file at a final name is left when
    /// its part file was still there (never renamed: the Host stopped first, or the name was taken) or when its size
    /// isn't the torrent file's.</summary>
    [Theory]
    [InlineData("renamed")]
    [InlineData("part kept")]
    [InlineData("other size")]
    public async Task DeletingATorrentWhoseFinalizeWasInterruptedTakesOnlyTheFilesItRenamed(string layout)
    {
        var (service, _, job) = await CompletedTorrentAsync(seedAfterDownload: false);
        await service.ShutdownAsync();
        _service = null;
        var store = new JsonJobStore(_paths);
        await store.InitializeAsync();
        var stored = (await store.LoadAllAsync()).Single(j => j.Id == job.Id);
        var files = await new TorrentStore(_paths).LoadFilesAsync(job.Id, CancellationToken.None);
        Assert.NotNull(files);
        var destination = stored.DestinationDirectory;
        Directory.CreateDirectory(destination);
        var renamedTop = "renamed-" + Guid.NewGuid().ToString("N")[..8] + ".bin";
        var renamed = TorrentLayout.WithTopName(files!, renamedTop)
            .Select(f => (Path: TorrentLayout.DiskPathOf(destination, f), f.Size)).ToList();
        var parts = files!.Select(f => TorrentLayout.DiskPathOf(destination, f, stored.Torrent!.PartSuffix)).ToList();
        foreach (var (path, size) in renamed)
        {
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            await File.WriteAllBytesAsync(path, new byte[layout == "other size" ? size + 1 : size]);
        }
        if (layout == "part kept")
        {
            foreach (var path in parts)
            {
                Directory.CreateDirectory(Path.GetDirectoryName(path)!);
                await File.WriteAllBytesAsync(path, new byte[8]);
            }
        }
        var unrelated = Path.Combine(destination, "unrelated.bin");
        await File.WriteAllBytesAsync(unrelated, new byte[8]);
        stored.State = DownloadState.Paused;
        stored.CompletedAt = null;
        stored.Torrent!.Finalized = false;
        stored.Torrent.Finalizing = true;
        stored.Torrent.FinalizingRootName = renamedTop;
        await store.SaveAsync(stored);

        var next = await TorrentServiceAsync();
        var removed = await next.RemoveAsync(job.Id, deleteFile: true);
        Assert.True(removed.Ok, removed.Error);
        Assert.All(renamed, file => Assert.Equal(layout != "renamed", File.Exists(file.Path)));
        Assert.All(parts, path => Assert.False(File.Exists(path), path));
        Assert.True(File.Exists(unrelated));
    }

    /// <summary>Review 2 L6, the runtime half: a finalize mark that can't be saved throws, and the job in memory has no
    /// mark either, so the worker is never told to rename.</summary>
    [Fact]
    public async Task AFinalizeMarkThatCannotBeSavedThrowsAndLeavesNoMark()
    {
        if (!OperatingSystem.IsWindows()) return; // a read-only record file refuses the replace on Windows only
        var service = await TorrentServiceAsync();
        var hash = Convert.ToHexStringLower(RandomNumberGenerator.GetBytes(20));
        var job = (await AskAsync(Dispatcher(service), Session(), TorrentMessageTypes.Add, new TorrentAddRequest
        {
            Input = $"magnet:?xt=urn:btih:{hash}&dn=Unsaved", Destination = Path.Combine(_root, "down"),
        })).GetPayload<TorrentAddResult>().Job!;
        var record = new FileInfo(Path.Combine(_paths.JobsDirectory, $"{job.Id:N}.json"));
        Assert.True(record.Exists);
        record.IsReadOnly = true;
        try
        {
            ITorrentJobSink sink = service;
            await Assert.ThrowsAnyAsync<Exception>(() => sink.FinalizingAsync(job.Id, "picked.bin", true, CancellationToken.None));
            var torrent = service.JobRecordForTesting(job.Id)!.Torrent!;
            Assert.False(torrent.Finalizing);
            Assert.Null(torrent.FinalizingRootName);
        }
        finally
        {
            record.IsReadOnly = false;
        }
    }

    /// <summary>Review 2 L1 (audit 2 M11 for torrents): a torrent the Host's exit paused goes on at the next start.</summary>
    [Fact]
    public async Task ATorrentTheExitPausedGoesOnAtTheNextStart()
    {
        var service = await TorrentServiceAsync();
        var hash = Convert.ToHexStringLower(RandomNumberGenerator.GetBytes(20));
        var job = (await AskAsync(Dispatcher(service), Session(), TorrentMessageTypes.Add, new TorrentAddRequest
        {
            Input = $"magnet:?xt=urn:btih:{hash}&dn=Exit", Destination = Path.Combine(_root, "down"),
        })).GetPayload<TorrentAddResult>().Job!;
        var worker = await UntilAsync(() => _torrentWorkers.FirstOrDefault(w => w.Torrents.Count == 1));
        worker.Advance();
        await UntilAsync(() => service.GetSnapshot().Jobs.FirstOrDefault(j => j.Id == job.Id && j.State == "downloading"));
        await service.ShutdownAsync();
        _service = null;
        Assert.Equal("paused", service.GetJob(job.Id)!.State);

        var next = await TorrentServiceAsync();
        await UntilAsync(() => next.GetSnapshot().Jobs.FirstOrDefault(j => j.Id == job.Id && j.State is "queued" or "connecting" or "downloading"));
    }
}

using System.Security.Cryptography;
using System.Text;
using DownloadManager.Archives.Tests;
using DownloadManager.Contracts;
using DownloadManager.Core.Archives;
using DownloadManager.Persistence;
using DownloadManager.Runtime;
using DownloadManager.Runtime.Archives;
using DownloadManager.Torrents;

namespace DownloadManager.Integration.Tests;

/// <summary>T2: extraction waits until a torrent's download has finished, on the in-memory torrent engine.</summary>
public sealed partial class HostCommandDispatcherTests
{
    /// <summary>Notes the job's state each time it is asked to extract, and extracts nothing.</summary>
    private sealed class StateNotingExtractor(DownloadManagerService service, Func<Guid> jobId) : IArchiveExtractor
    {
        public List<string> StatesSeen { get; } = [];

        public Task<ArchiveProbe> ProbeAsync(string firstVolume, string? password, CancellationToken cancellationToken) => throw new NotSupportedException();

        public Task<ExtractionResult> ExtractAsync(ExtractionRequest request, IProgress<ExtractionProgress>? progress, CancellationToken cancellationToken)
        {
            lock (StatesSeen) StatesSeen.Add(service.GetJob(jobId())?.State ?? "gone");
            return Task.FromResult(new ExtractionResult(request.Destination, []) { VolumesRead = [request.FirstVolume], FlushedToDisk = true });
        }
    }

    [Fact]
    public async Task ATorrentsArchiveIsExtractedOnlyOnceItsDownloadHasFinished()
    {
        var service = await TorrentServiceAsync(singleFileName: "film.rar");
        var settings = service.GetSettings();
        settings.TorrentSeedAfterDownload = false;
        Assert.True((await service.UpdateSettingsAsync(settings)).Ok);
        var dispatcher = Dispatcher(service);
        var jobId = Guid.Empty;
        var extractor = new StateNotingExtractor(service, () => jobId);
        var archives = new ArchiveCoordinator(service, new JsonArchiveStore(_paths), null, new ArchiveCoordinatorOptions { ExtractorFactory = _ => extractor });
        try
        {
            await archives.InitializeAsync();
            await archives.UpdateSettingsAsync(new ArchiveSettingsUpdate { AutoExtract = true });

            var hash = Convert.ToHexStringLower(RandomNumberGenerator.GetBytes(20));
            var job = (await AskAsync(dispatcher, Session(), TorrentMessageTypes.Add, new TorrentAddRequest
            {
                Input = $"magnet:?xt=urn:btih:{hash}&dn=film", Destination = Path.Combine(_root, "down"),
            })).GetPayload<TorrentAddResult>().Job!;
            jobId = job.Id;
            var worker = await UntilAsync(() => _torrentWorkers.FirstOrDefault(w => w.Torrents.Count == 1));
            var named = await UntilAsync(() => service.GetJob(job.Id) is { FileName: "film.rar" } found ? found : null);
            worker.Advance();
            await UntilAsync(() => service.GetJob(job.Id) is { State: "downloading" } found ? found : null);

            // While it downloads, its file is on disk under its part name, as the worker writes it, and the job changes:
            // nothing is extracted.
            var final = named.FinalFilePath;
            var part = final + TorrentJobInfoPartSuffix(job.Id);
            await File.WriteAllBytesAsync(part, RarBuilder.Rar5(("inside.txt", Encoding.ASCII.GetBytes("from the torrent"))));
            Assert.True((await service.UpdateJobAsync(new JobUpdateRequest { JobId = job.Id, Description = "still coming" })).Ok);
            await Task.Delay(500);
            lock (extractor.StatesSeen) Assert.Empty(extractor.StatesSeen);
            Assert.Empty(archives.List());

            // The worker gives the file its own name as the torrent finishes (the in-memory one writes nothing, so it is
            // renamed here when the job hears of the names), before the job says completed.
            var renamedWhile = new List<string>();
            void Rename(object? sender, JobChangeEventArgs e)
            {
                if (e.Job.Id != job.Id || e.ChangeType != "updated" || !File.Exists(part)) return;
                File.Move(part, final);
                renamedWhile.Add(e.Job.State);
            }

            service.JobChanged += Rename;
            for (var i = 0; i < 3; i++) worker.Advance();
            var done = await WaitForArchiveSetAsync(archives, set => set.State == "completed");
            service.JobChanged -= Rename;
            Assert.Equal(["downloading"], renamedWhile);
            Assert.Equal("film.rar", Path.GetFileName(done.FirstVolume));
            // Extracted once, as a finished download, never while it downloaded or while its file was renamed.
            lock (extractor.StatesSeen) Assert.Equal(["completed"], extractor.StatesSeen);
        }
        finally
        {
            await archives.ShutdownAsync();
        }
    }

    /// <summary>Extraction set to delete its archives: a seeding torrent's file can't be shared once it goes, so its
    /// seeding stops before the delete.</summary>
    [Fact]
    public async Task ATorrentWhoseArchiveIsDeletedAfterExtractionStopsSeedingFirst()
    {
        var (service, _, job) = await CompletedTorrentAsync();
        await UntilAsync(() => service.GetSnapshot().Jobs.FirstOrDefault(j => j.Id == job.Id && j.Torrent!.Seeding));
        await UntilAsync(() => _torrentWorkers.FirstOrDefault(w => w.Torrents.Count == 1));

        await service.MarkFilesRemovedAfterExtractionAsync([job.FinalFilePath], removed: true);
        Assert.False(service.GetSnapshot().Jobs.Single(j => j.Id == job.Id).Torrent!.Seeding);
        await UntilAsync(() => _torrentWorkers.All(w => w.Torrents.Count == 0) ? "" : null);

        // With the file gone it can't be seeded again.
        var again = await service.SetTorrentSeedingAsync(new TorrentSeedRequest { JobId = job.Id, On = true });
        Assert.False(again.Ok);
        Assert.Equal("The file was removed after extraction.", again.Error);
        Assert.False(service.GetSnapshot().Jobs.Single(j => j.Id == job.Id).Torrent!.Seeding);
        Assert.All(_torrentWorkers, w => Assert.Empty(w.Torrents));
    }

    private static string TorrentJobInfoPartSuffix(Guid jobId) => DownloadManager.Core.TorrentJobInfo.PartSuffixFor(jobId);

    private static async Task<ArchiveSetDto> WaitForArchiveSetAsync(ArchiveCoordinator archives, Func<ArchiveSetDto, bool> predicate)
    {
        var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(30);
        while (DateTime.UtcNow < deadline)
        {
            if (archives.List().FirstOrDefault(predicate) is { } set) return set;
            await Task.Delay(20);
        }

        throw new TimeoutException("No archive set reached the expected state: " + string.Join("; ", archives.List().Select(set => $"{set.Name}: {set.StateText}")));
    }
}

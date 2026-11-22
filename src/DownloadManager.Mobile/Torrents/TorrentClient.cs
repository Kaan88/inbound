using DownloadManager.Contracts;

namespace DownloadManager.Mobile.Torrents;

/// <summary>
/// <c>torrent.*</c> requests on the selected, already-open Host connection (the phone's own Host or a paired PC or server). The Host's
/// libtorrent engine does the work; this only sends the messages and reads the replies. A failed reply (<c>Ok</c> false) is returned for
/// the screen to show its <c>Error</c>; a reply of another kind, or a refusal of the whole request, throws.
/// </summary>
public sealed class TorrentClient
{
    /// <summary>What the hello lists when the Host answers <c>torrent.*</c> (whether or not torrents are turned on in its settings).</summary>
    public const string Capability = "torrents";

    public const string HostRefused = "The Host did not accept the torrent request.";

    /// <summary>How long a watch lasts before it must be renewed; the Host accepts 10 to 300.</summary>
    public const int WatchLeaseSeconds = 60;

    private readonly IHostConnection _connection;

    public TorrentClient(IHostConnection connection) =>
        _connection = connection ?? throw new ArgumentNullException(nameof(connection));

    public static bool Offered(HostHelloDto? hello) => hello?.Capabilities.Contains(Capability) == true;

    /// <summary>A magnet or a .torrent file's path on the Host. <paramref name="destination"/> null is the Host's torrent folder.</summary>
    public Task<TorrentInspectResult> InspectAsync(string input, string? destination = null, CancellationToken cancellationToken = default) =>
        RequestAsync<TorrentInspectResult>(IpcMessage.Create(TorrentMessageTypes.Inspect, new TorrentInspectRequest { Input = input, Destination = destination }),
            TorrentMessageTypes.InspectResult, cancellationToken);

    /// <summary>A .torrent file from this phone, by its bytes (the Host may be another machine).</summary>
    public Task<TorrentInspectResult> InspectFileAsync(byte[] bytes, CancellationToken cancellationToken = default) =>
        RequestAsync<TorrentInspectResult>(IpcMessage.Create(TorrentMessageTypes.Inspect, new TorrentInspectRequest { Data = Convert.ToBase64String(bytes) }),
            TorrentMessageTypes.InspectResult, cancellationToken);

    public Task<TorrentAddResult> AddAsync(TorrentAddRequest request, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        return RequestAsync<TorrentAddResult>(IpcMessage.Create(TorrentMessageTypes.Add, request), TorrentMessageTypes.AddResult, cancellationToken);
    }

    /// <summary>A magnet that asks for its file choice once the file list is in: nothing downloads until <see cref="SetFilesAsync"/>.</summary>
    public static TorrentAddRequest MagnetRequest(string magnet, string? destination) =>
        new() { Input = magnet, Destination = destination, StartMode = "startNow", Files = "askWhenReady" };

    /// <summary>A .torrent file, sent as its bytes so the Host needs no path to it (another PC or server never could read one).</summary>
    public static TorrentAddRequest FileRequest(byte[] bytes, string? destination, IReadOnlyCollection<int>? fileIndexes)
    {
        ArgumentNullException.ThrowIfNull(bytes);
        var request = new TorrentAddRequest { Data = Convert.ToBase64String(bytes), Destination = destination, StartMode = "startNow", Files = "all" };
        if (fileIndexes is { Count: > 0 })
        {
            request.Files = "chosen";
            request.FileIndexes = fileIndexes.OrderBy(i => i).ToList();
        }

        return request;
    }

    public Task<TorrentFilesResult> FilesAsync(Guid jobId, CancellationToken cancellationToken = default) =>
        RequestAsync<TorrentFilesResult>(IpcMessage.Create(TorrentMessageTypes.Files, new TorrentFilesRequest { JobId = jobId }),
            TorrentMessageTypes.FilesResult, cancellationToken);

    /// <summary>The wanted files by index; every other file is left out. At least one must stay.</summary>
    public Task<TorrentActionResult> SetFilesAsync(Guid jobId, IReadOnlyCollection<int> wanted, CancellationToken cancellationToken = default) =>
        RequestAsync<TorrentActionResult>(IpcMessage.Create(TorrentMessageTypes.SetFiles,
            new TorrentSetFilesRequest { JobId = jobId, FileIndexes = wanted.OrderBy(i => i).ToList() }), TorrentMessageTypes.ActionResult, cancellationToken);

    public Task<TorrentDetailsResult> DetailsAsync(Guid jobId, CancellationToken cancellationToken = default) =>
        RequestAsync<TorrentDetailsResult>(IpcMessage.Create(TorrentMessageTypes.Details, new TorrentJobRequest { JobId = jobId }),
            TorrentMessageTypes.DetailsResult, cancellationToken);

    public Task<TorrentPeersResult> PeersAsync(Guid jobId, CancellationToken cancellationToken = default) =>
        RequestAsync<TorrentPeersResult>(IpcMessage.Create(TorrentMessageTypes.Peers, new TorrentJobRequest { JobId = jobId }),
            TorrentMessageTypes.PeersResult, cancellationToken);

    public Task<TorrentTrackersResult> TrackersAsync(Guid jobId, CancellationToken cancellationToken = default) =>
        RequestAsync<TorrentTrackersResult>(IpcMessage.Create(TorrentMessageTypes.Trackers, new TorrentJobRequest { JobId = jobId }),
            TorrentMessageTypes.TrackersResult, cancellationToken);

    public Task<TorrentPiecesResult> PiecesAsync(Guid jobId, CancellationToken cancellationToken = default) =>
        RequestAsync<TorrentPiecesResult>(IpcMessage.Create(TorrentMessageTypes.Pieces, new TorrentJobRequest { JobId = jobId }),
            TorrentMessageTypes.PiecesResult, cancellationToken);

    /// <summary>Starts (<paramref name="on"/>) or stops seeding a finished torrent.</summary>
    public Task<TorrentActionResult> SeedAsync(Guid jobId, bool on, CancellationToken cancellationToken = default) =>
        RequestAsync<TorrentActionResult>(IpcMessage.Create(TorrentMessageTypes.Seed, new TorrentSeedRequest { JobId = jobId, On = on }),
            TorrentMessageTypes.ActionResult, cancellationToken);

    public Task<TorrentOptionsResult> OptionsAsync(Guid jobId, CancellationToken cancellationToken = default) =>
        RequestAsync<TorrentOptionsResult>(IpcMessage.Create(TorrentMessageTypes.Options, new TorrentJobRequest { JobId = jobId }),
            TorrentMessageTypes.OptionsResult, cancellationToken);

    /// <summary>Tells the Host a torrent's details are on screen, so progress comes every second instead of every five. Renew before it lapses.</summary>
    public Task<TorrentActionResult> WatchAsync(bool on, CancellationToken cancellationToken = default) =>
        RequestAsync<TorrentActionResult>(IpcMessage.Create(TorrentMessageTypes.Watch, new TorrentWatchRequest { On = on, LeaseSeconds = WatchLeaseSeconds }),
            TorrentMessageTypes.ActionResult, cancellationToken);

    /// <summary>The torrents of an imported list, in as many requests as the frame size needs. A Host that doesn't know the request skips them all with its words.</summary>
    public async Task<TorrentImportResult> ImportAsync(IReadOnlyList<JobListEntry> entries, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(entries);
        var total = new TorrentImportResult { Ok = true };
        var sent = 0;
        foreach (var chunk in TorrentListPaging.Chunk(entries))
        {
            TorrentImportResult part;
            try
            {
                part = await RequestAsync<TorrentImportResult>(IpcMessage.Create(TorrentMessageTypes.Import, new TorrentImportRequest { Entries = chunk }),
                    TorrentMessageTypes.ImportResult, cancellationToken).ConfigureAwait(false);
            }
            catch (InvalidOperationException ex) when (sent == 0)
            {
                return new TorrentImportResult { Ok = true, Skipped = entries.Count, SkipReason = ex.Message };
            }
            catch (Exception ex) when (ex is InvalidOperationException or InvalidDataException)
            {
                total.Failed += entries.Count - sent;
                total.FirstError ??= ex.Message;
                break;
            }

            TorrentListPaging.AddTo(total, part);
            sent += chunk.Count;
        }

        return total;
    }

    private async Task<T> RequestAsync<T>(IpcMessage request, string expectedType, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var reply = await _connection.RequestAsync(request, cancellationToken).ConfigureAwait(false);
        if (reply.Type == MessageTypes.HostActionResult)
            throw new InvalidOperationException(reply.GetPayload<HostActionResult>().Message ?? HostRefused);
        if (reply.Type != expectedType)
            throw new InvalidDataException($"The Host answered with '{reply.Type}' instead of '{expectedType}'.");
        return reply.GetPayload<T>();
    }
}

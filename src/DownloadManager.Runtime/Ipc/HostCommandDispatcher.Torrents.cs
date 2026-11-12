using DownloadManager.Contracts;

namespace DownloadManager.Runtime.Ipc;

public sealed partial class HostCommandDispatcher
{
    /// <summary><c>torrent.*</c> (T1). A web session's are held to its folders by the service.</summary>
    private async Task AnswerTorrentAsync(IClientSession client, IpcMessage message, CallerScope? caller, CancellationToken cancellationToken)
    {
        switch (message.Type)
        {
            case TorrentMessageTypes.Inspect:
                await Reply(client, message, TorrentMessageTypes.InspectResult,
                    await _service.InspectTorrentAsync(message.GetPayload<TorrentInspectRequest>(), cancellationToken, caller).ConfigureAwait(false)).ConfigureAwait(false);
                break;
            case TorrentMessageTypes.Add:
                await Reply(client, message, TorrentMessageTypes.AddResult,
                    await _service.AddTorrentAsync(message.GetPayload<TorrentAddRequest>(), cancellationToken, caller).ConfigureAwait(false)).ConfigureAwait(false);
                break;
            case TorrentMessageTypes.SetFiles:
                await Reply(client, message, TorrentMessageTypes.ActionResult,
                    await _service.SetTorrentFilesAsync(message.GetPayload<TorrentSetFilesRequest>(), cancellationToken, caller).ConfigureAwait(false)).ConfigureAwait(false);
                break;
            case TorrentMessageTypes.Files:
                await Reply(client, message, TorrentMessageTypes.FilesResult,
                    await _service.GetTorrentFilesAsync(message.GetPayload<TorrentFilesRequest>(), cancellationToken, caller).ConfigureAwait(false)).ConfigureAwait(false);
                break;
            case TorrentMessageTypes.Peers:
                await Reply(client, message, TorrentMessageTypes.PeersResult,
                    await _service.GetTorrentPeersAsync(message.GetPayload<TorrentJobRequest>(), cancellationToken, caller).ConfigureAwait(false)).ConfigureAwait(false);
                break;
            case TorrentMessageTypes.Trackers:
                await Reply(client, message, TorrentMessageTypes.TrackersResult,
                    await _service.GetTorrentTrackersAsync(message.GetPayload<TorrentJobRequest>(), cancellationToken, caller).ConfigureAwait(false)).ConfigureAwait(false);
                break;
            case TorrentMessageTypes.Pieces:
                await Reply(client, message, TorrentMessageTypes.PiecesResult,
                    await _service.GetTorrentPiecesAsync(message.GetPayload<TorrentJobRequest>(), cancellationToken, caller).ConfigureAwait(false)).ConfigureAwait(false);
                break;
            case TorrentMessageTypes.Seed:
                await Reply(client, message, TorrentMessageTypes.ActionResult,
                    await _service.SetTorrentSeedingAsync(message.GetPayload<TorrentSeedRequest>(), cancellationToken, caller).ConfigureAwait(false)).ConfigureAwait(false);
                break;
            case TorrentMessageTypes.Network:
                await Reply(client, message, TorrentMessageTypes.NetworkResult,
                    await _service.GetTorrentNetworkAsync(cancellationToken).ConfigureAwait(false)).ConfigureAwait(false);
                break;
            case TorrentMessageTypes.Options:
                await Reply(client, message, TorrentMessageTypes.OptionsResult,
                    await _service.GetTorrentOptionsAsync(message.GetPayload<TorrentJobRequest>(), cancellationToken, caller).ConfigureAwait(false)).ConfigureAwait(false);
                break;
            case TorrentMessageTypes.SetOptions:
                await Reply(client, message, TorrentMessageTypes.OptionsResult,
                    await _service.SetTorrentOptionsAsync(message.GetPayload<TorrentOptionsDto>(), cancellationToken, caller).ConfigureAwait(false)).ConfigureAwait(false);
                break;
            case TorrentMessageTypes.Recheck:
                await Reply(client, message, TorrentMessageTypes.ActionResult,
                    await _service.RecheckTorrentAsync(message.GetPayload<TorrentJobRequest>(), cancellationToken, caller).ConfigureAwait(false)).ConfigureAwait(false);
                break;
            case TorrentMessageTypes.Announce:
                await Reply(client, message, TorrentMessageTypes.ActionResult,
                    await _service.AnnounceTorrentAsync(message.GetPayload<TorrentJobRequest>(), cancellationToken, caller).ConfigureAwait(false)).ConfigureAwait(false);
                break;
            case TorrentMessageTypes.Details:
                await Reply(client, message, TorrentMessageTypes.DetailsResult,
                    await _service.GetTorrentDetailsAsync(message.GetPayload<TorrentJobRequest>(), cancellationToken, caller).ConfigureAwait(false)).ConfigureAwait(false);
                break;
            case TorrentMessageTypes.Watch:
                await Reply(client, message, TorrentMessageTypes.ActionResult,
                    _service.WatchTorrents(client.Id, message.GetPayload<TorrentWatchRequest>() ?? new TorrentWatchRequest())).ConfigureAwait(false);
                break;
            case TorrentMessageTypes.ListEntries:
                await Reply(client, message, TorrentMessageTypes.ListEntriesResult,
                    await _service.GetTorrentListEntriesAsync(message.GetPayload<TorrentListEntriesRequest>(), cancellationToken, caller).ConfigureAwait(false)).ConfigureAwait(false);
                break;
            case TorrentMessageTypes.Import:
                await Reply(client, message, TorrentMessageTypes.ImportResult,
                    await _service.ImportTorrentsAsync(message.GetPayload<TorrentImportRequest>(), cancellationToken, caller).ConfigureAwait(false)).ConfigureAwait(false);
                break;
        }
    }
}

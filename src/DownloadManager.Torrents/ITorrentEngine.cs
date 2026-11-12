using System.Text.Json.Nodes;
using System.Threading.Channels;

namespace DownloadManager.Torrents;

/// <summary>
/// The seam between the Host and a torrent engine (roadmap section 2). Commands go out as JSON objects with a
/// <c>cmd</c> name and get one reply each; events (<c>hello</c>, <c>metadata</c>, <c>progress</c>, <c>state</c>,
/// <c>completed</c>, <c>resumeBlob</c>, <c>error</c>, <c>stats</c>, <c>bye</c>) arrive on <see cref="Events"/>.
/// The protocol is described in <c>native/torrent-worker/PROTOCOL.md</c>.
/// </summary>
public interface ITorrentEngine : IAsyncDisposable
{
    ChannelReader<JsonObject> Events { get; }

    /// <summary>Completes when the engine has stopped, for any reason.</summary>
    Task Exited { get; }

    /// <summary>Sends a command and returns the reply. Throws <see cref="TorrentCommandException"/> when the engine
    /// refuses it and <see cref="TorrentEngineExitedException"/> when the engine is gone.</summary>
    Task<JsonObject> SendAsync(string command, JsonObject? arguments = null, CancellationToken cancellationToken = default);
}

public sealed class TorrentCommandException(string command, string message)
    : Exception($"The torrent engine refused {command}: {message}")
{
    public string Command { get; } = command;
    public string Reason { get; } = message;
}

public sealed class TorrentEngineExitedException(string message) : Exception(message);

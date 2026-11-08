using DownloadManager.Contracts;

namespace DownloadManager.Runtime.Ipc;

/// <summary>
/// One connected client, as the dispatcher sees it, whatever carries its frames (a named pipe, a WebSocket).
/// <see cref="QueuedClientSession"/> has the queue and its writer, so a transport only writes one frame.
/// </summary>
public interface IClientSession
{
    Guid Id { get; }

    /// <summary>
    /// Queues a frame for the session's writer, in order. The queue holds <see cref="QueuedClientSession.QueueCapacity"/>
    /// frames and drops the oldest when full, so a client that stops reading can't make the Host grow without bound.
    /// Returns false once the session is closed, and the frame is then dropped.
    /// </summary>
    bool Enqueue(IpcMessage message);

    /// <summary>
    /// Writes a frame now, ahead of anything queued. For the hello and the snapshot, which come before any event, and
    /// for the last frames before an exit, which a stopping writer would lose. Throws <see cref="InvalidDataException"/>
    /// when the frame is larger than <see cref="Protocol.MaxFrameBytes"/>, and then nothing of it was sent.
    /// </summary>
    Task SendDirectAsync(IpcMessage message, CancellationToken cancellationToken);

    /// <summary>
    /// Ends the connection from the Host's side (<see cref="HostCommandDispatcher.CloseSessions"/>): no more frames are
    /// read and what is queued is the last that is sent. A session that lives for one reply has nothing to end.
    /// </summary>
    void Close()
    {
    }
}

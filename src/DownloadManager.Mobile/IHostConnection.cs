using DownloadManager.Contracts;

namespace DownloadManager.Mobile;

/// <summary>
/// A client session with a Host, as the phone screens see it: frames come in (hello, snapshot, events), commands go out and their
/// replies come back. The in-process Host on the phone implements it over the dispatcher; a remote Host will implement it over its API.
/// </summary>
public interface IHostConnection : IAsyncDisposable
{
    /// <summary>
    /// Opens the session. Every frame that is not the reply to a request (the hello, the jobs snapshot, then job events) is passed to
    /// <paramref name="onFrame"/>, on whatever thread the transport uses. Frames may arrive before this returns.
    /// </summary>
    Task OpenAsync(Action<IpcMessage> onFrame, CancellationToken cancellationToken);

    /// <summary>Sends one command and waits for the Host's reply to it.</summary>
    Task<IpcMessage> RequestAsync(IpcMessage request, CancellationToken cancellationToken);
}

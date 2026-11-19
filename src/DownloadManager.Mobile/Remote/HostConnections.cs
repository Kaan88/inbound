using DownloadManager.Contracts;

namespace DownloadManager.Mobile.Remote;

/// <summary>The state a remote host's strip shows (C6). The phone's own connection does not have one.</summary>
public interface IRemoteStatus
{
    string HostName { get; }

    RemoteConnectionState State { get; }

    /// <summary>Raised on any thread.</summary>
    event Action<RemoteConnectionState>? StateChanged;

    /// <summary>False while the screens are not shown: no socket, no tries.</summary>
    void SetActive(bool active);

    /// <summary>The phone's network changed.</summary>
    void RetryNow(bool dropCurrent);
}

public static class HostConnections
{
    /// <summary>
    /// The connection for the host the screens show: <paramref name="phone"/> for this phone, otherwise a
    /// <see cref="RemoteHostConnection"/> to the server, which notes in <paramref name="hosts"/> when it was last reached. A
    /// server whose token can't be read any more gets a connection that only says to pair again.
    /// </summary>
    public static IHostConnection ForCurrent(HostDirectory hosts, Func<IHostConnection> phone, Action<Exception>? onError = null,
        RemoteHostConnection.Connector? connect = null) => ForEntry(hosts, hosts.Current, phone, onError, connect);

    public static IHostConnection ForEntry(HostDirectory hosts, HostEntry current, Func<IHostConnection> phone,
        Action<Exception>? onError = null, RemoteHostConnection.Connector? connect = null)
    {
        if (current.IsPhone) return phone();
        if (hosts.TargetFor(current) is not { } target) return new UnpairedHostConnection(current.Name);
        var remote = new RemoteHostConnection(target, connect, onError: onError);
        remote.Connected += hello => hosts.Seen(current.Id, hello.HostKind);
        return remote;
    }
}

/// <summary>
/// A saved server whose token can't be read (the phone's Keystore key was reset): nothing is sent anywhere; the state says
/// to pair again, and every command fails with those words.
/// </summary>
public sealed class UnpairedHostConnection(string hostName) : IHostConnection, IRemoteStatus
{
    public string HostName => hostName;

    public RemoteConnectionState State { get; } = new(RemoteConnectionStatus.Stopped,
        $"This phone can't read its pairing with {hostName} any more. Remove it from the host list and connect to it again.");

    public event Action<RemoteConnectionState>? StateChanged { add { } remove { } }

    public void SetActive(bool active) { }

    public void RetryNow(bool dropCurrent) { }

    public Task OpenAsync(Action<IpcMessage> onFrame, CancellationToken cancellationToken) => Task.CompletedTask;

    public Task<IpcMessage> RequestAsync(IpcMessage request, CancellationToken cancellationToken) =>
        Task.FromException<IpcMessage>(new RemoteServerException(State.Message!));

    public ValueTask DisposeAsync() => ValueTask.CompletedTask;
}

using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;
using DownloadManager.Contracts;

namespace DownloadManager.Desktop.WinUI;

/// <summary>
/// One window per name: the first launch owns the name, and a later one asks it to show itself (or its Options) and
/// exits. On Windows these are named events (show, Options, and open a queued torrent). Linux has no named events, so there the first
/// launch listens on a Unix socket in the user's own socket folder (<see cref="UnixSockets"/>) and a later one connects
/// and sends one byte; only this user's processes are heard.
/// </summary>
internal sealed class ActivationSignal
{
    private readonly EventWaitHandle _show;
    private readonly EventWaitHandle _settings;
    private readonly EventWaitHandle _open;
    private readonly string? _socketPath;

    private ActivationSignal(EventWaitHandle show, EventWaitHandle settings, EventWaitHandle open, string? socketPath)
    {
        _show = show;
        _settings = settings;
        _open = open;
        _socketPath = socketPath;
    }

    private static EventWaitHandle Local() => new(false, EventResetMode.AutoReset);

    /// <summary>Set when another launch asks this window to show itself.</summary>
    public WaitHandle Show => _show;

    /// <summary>Set when another launch asks for Options.</summary>
    public WaitHandle Settings => _settings;

    /// <summary>Set when another launch queued a magnet or .torrent file to open (<see cref="TorrentOpenRequests"/>).</summary>
    public WaitHandle Open => _open;

    public static ActivationSignal Create(string name, out bool createdNew)
    {
        if (!OperatingSystem.IsLinux())
        {
            var show = new EventWaitHandle(false, EventResetMode.AutoReset, name, out createdNew);
            var settings = new EventWaitHandle(false, EventResetMode.AutoReset, name + ".Settings");
            var open = new EventWaitHandle(false, EventResetMode.AutoReset, name + ".Open");
            return new ActivationSignal(show, settings, open, null);
        }

        UnixSockets.EnsureDirectory();
        var signal = new ActivationSignal(Local(), Local(), Local(), SocketPath(name));
        createdNew = !signal.Reachable() && signal.TryListen();
        return signal;
    }

    /// <summary>
    /// A window owns <paramref name="name"/> now (another process's progress window, say), without taking the name or
    /// asking that window for anything.
    /// </summary>
    public static bool IsOwned(string name)
    {
        if (!OperatingSystem.IsLinux())
        {
            if (!EventWaitHandle.TryOpenExisting(name, out var existing)) return false;
            existing.Dispose();
            return true;
        }

        return new ActivationSignal(Local(), Local(), Local(), SocketPath(name)).Reachable();
    }

    /// <summary>
    /// Asks the window that owns <paramref name="name"/> to show itself, as a later launch would, without starting one.
    /// False when no window owns it (it closed meanwhile).
    /// </summary>
    public static bool TryShowOwner(string name)
    {
        if (!OperatingSystem.IsLinux())
        {
            if (!EventWaitHandle.TryOpenExisting(name, out var existing)) return false;
            using (existing) existing.Set();
            return true;
        }

        try
        {
            using var socket = new Socket(AddressFamily.Unix, SocketType.Stream, ProtocolType.Unspecified);
            socket.Connect(new UnixDomainSocketEndPoint(SocketPath(name)));
            UnixSockets.VerifyPeer(socket);
            socket.Send([(byte)'s']);
            return true;
        }
        catch (Exception ex) when (ex is SocketException or UnauthorizedAccessException)
        {
            return false;
        }
    }

    private static string SocketPath(string name)
    {
        // The name holds a server's id and more; a short hash keeps the socket path within the 108-byte limit.
        var hash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(name)).AsSpan(0, 8)).ToLowerInvariant();
        return Path.Combine(UnixSockets.Directory(), $"window-{hash}.sock");
    }

    /// <summary>From a later launch: asks the running window to show itself, or its Options.</summary>
    public void Signal(bool settings) => Send(settings ? _settings : _show, settings ? (byte)'o' : (byte)'s');

    /// <summary>From a later launch: asks the running window to open the queued torrent requests.</summary>
    public void SignalOpen() => Send(_open, (byte)'t');

    private void Send(EventWaitHandle local, byte message)
    {
        if (_socketPath is null)
        {
            local.Set();
            return;
        }

        try
        {
            using var socket = new Socket(AddressFamily.Unix, SocketType.Stream, ProtocolType.Unspecified);
            socket.Connect(new UnixDomainSocketEndPoint(_socketPath));
            UnixSockets.VerifyPeer(socket);
            socket.Send([message]);
        }
        catch (Exception ex) when (ex is SocketException or UnauthorizedAccessException)
        {
            // The window went away meanwhile; nothing to show.
        }
    }

    private bool Reachable()
    {
        try
        {
            using var socket = new Socket(AddressFamily.Unix, SocketType.Stream, ProtocolType.Unspecified);
            socket.Connect(new UnixDomainSocketEndPoint(_socketPath!));
            UnixSockets.VerifyPeer(socket);
            return true;
        }
        catch (Exception ex) when (ex is SocketException or UnauthorizedAccessException)
        {
            return false;
        }
    }

    /// <summary>Takes the name. False when another launch took it between the check and here.</summary>
    private bool TryListen()
    {
        var path = _socketPath!;
        // Nothing answered there: a file left by a window that has gone.
        if (File.Exists(path)) File.Delete(path);
        var listener = new Socket(AddressFamily.Unix, SocketType.Stream, ProtocolType.Unspecified);
        try
        {
            listener.Bind(new UnixDomainSocketEndPoint(path));
            File.SetUnixFileMode(path, UnixFileMode.UserRead | UnixFileMode.UserWrite);
            listener.Listen(8);
        }
        catch (SocketException)
        {
            listener.Dispose();
            return false;
        }

        var thread = new Thread(() => Listen(listener)) { IsBackground = true, Name = "Window activation" };
        thread.Start();
        AppDomain.CurrentDomain.ProcessExit += (_, _) =>
        {
            listener.Dispose();
            try { File.Delete(path); }
            catch (IOException) { /* the next window replaces it */ }
        };
        return true;
    }

    private void Listen(Socket listener)
    {
        var buffer = new byte[1];
        while (true)
        {
            Socket client;
            try
            {
                client = listener.Accept();
            }
            catch (Exception ex) when (ex is SocketException or ObjectDisposedException)
            {
                return;
            }

            using (client)
            {
                try
                {
                    if (UnixSockets.PeerUserId(client) != UnixSockets.EffectiveUserId()) continue;
                    client.ReceiveTimeout = 2000;
                    if (client.Receive(buffer) != 1) continue;
                    if (buffer[0] == (byte)'o') _settings.Set();
                    else if (buffer[0] == (byte)'s') _show.Set();
                    else if (buffer[0] == (byte)'t') _open.Set();
                }
                catch (SocketException)
                {
                    // A probe that only connected (Reachable), or a launch that went away.
                }
            }
        }
    }
}

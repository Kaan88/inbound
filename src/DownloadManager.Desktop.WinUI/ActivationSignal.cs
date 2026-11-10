using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;
using DownloadManager.Contracts;

namespace DownloadManager.Desktop.WinUI;

/// <summary>
/// One window per name: the first launch owns the name, and a later one asks it to show itself (or its Options) and
/// exits. On Windows these are the two named events the app always used. Linux has no named events, so there the first
/// launch listens on a Unix socket in the user's own socket folder (<see cref="UnixSockets"/>) and a later one connects
/// and sends one byte; only this user's processes are heard.
/// </summary>
internal sealed class ActivationSignal
{
    private readonly EventWaitHandle _show;
    private readonly EventWaitHandle _settings;
    private readonly string? _socketPath;

    private ActivationSignal(EventWaitHandle show, EventWaitHandle settings, string? socketPath)
    {
        _show = show;
        _settings = settings;
        _socketPath = socketPath;
    }

    /// <summary>Set when another launch asks this window to show itself.</summary>
    public WaitHandle Show => _show;

    /// <summary>Set when another launch asks for Options.</summary>
    public WaitHandle Settings => _settings;

    public static ActivationSignal Create(string name, out bool createdNew)
    {
        if (!OperatingSystem.IsLinux())
        {
            var show = new EventWaitHandle(false, EventResetMode.AutoReset, name, out createdNew);
            var settings = new EventWaitHandle(false, EventResetMode.AutoReset, name + ".Settings");
            return new ActivationSignal(show, settings, null);
        }

        UnixSockets.EnsureDirectory();
        // The name holds a server's id and more; a short hash keeps the socket path within the 108-byte limit.
        var hash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(name)).AsSpan(0, 8)).ToLowerInvariant();
        var path = Path.Combine(UnixSockets.Directory(), $"window-{hash}.sock");
        var signal = new ActivationSignal(new EventWaitHandle(false, EventResetMode.AutoReset), new EventWaitHandle(false, EventResetMode.AutoReset), path);
        createdNew = !signal.Reachable() && signal.TryListen();
        return signal;
    }

    /// <summary>From a later launch: asks the running window to show itself, or its Options.</summary>
    public void Signal(bool settings)
    {
        if (_socketPath is null)
        {
            (settings ? _settings : _show).Set();
            return;
        }

        try
        {
            using var socket = new Socket(AddressFamily.Unix, SocketType.Stream, ProtocolType.Unspecified);
            socket.Connect(new UnixDomainSocketEndPoint(_socketPath));
            UnixSockets.VerifyPeer(socket);
            socket.Send([settings ? (byte)'o' : (byte)'s']);
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
                }
                catch (SocketException)
                {
                    // A probe that only connected (Reachable), or a launch that went away.
                }
            }
        }
    }
}

using System.Net.Sockets;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;

namespace DownloadManager.Contracts;

/// <summary>
/// The Linux Host's local transport: a Unix socket per pipe name in a folder only this user can open, with the peer's
/// user ID checked on both ends. It does what the Windows pipe ACL and <see cref="PipeOwner"/> do there.
/// </summary>
[SupportedOSPlatform("linux")]
public static class UnixSockets
{
    /// <summary>
    /// <c>$XDG_RUNTIME_DIR/inbound</c>: the session's own folder (mode 0700, on tmpfs, removed at log-out). Without one,
    /// a folder of this user's under the temp folder.
    /// </summary>
    public static string Directory()
    {
        var runtime = Environment.GetEnvironmentVariable("XDG_RUNTIME_DIR");
        return !string.IsNullOrWhiteSpace(runtime) && Path.IsPathRooted(runtime)
            ? Path.Combine(runtime, "inbound")
            : Path.Combine(Path.GetTempPath(), $"inbound-{EffectiveUserId()}");
    }

    public static string PathFor(string pipeName) => Path.Combine(Directory(), pipeName + ".sock");

    /// <summary>
    /// Makes the folder for the Host's sockets, or checks one that is there: it must be this user's and closed to
    /// everyone else, or another account could put its own socket in the Host's place.
    /// </summary>
    public static void EnsureDirectory()
    {
        var dir = Directory();
        if (!System.IO.Directory.Exists(dir))
        {
            System.IO.Directory.CreateDirectory(dir, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        }

        VerifyDirectory(dir);
    }

    private static void VerifyDirectory(string dir)
    {
        var info = new DirectoryInfo(dir);
        if (info.LinkTarget is not null)
            throw new UnauthorizedAccessException($"The app's socket folder {dir} is a link.");
        if (Stat(dir, out var owner) != 0 || owner != EffectiveUserId())
            throw new UnauthorizedAccessException($"The app's socket folder {dir} belongs to another user.");
        if ((info.UnixFileMode & (UnixFileMode.GroupRead | UnixFileMode.GroupWrite | UnixFileMode.GroupExecute |
                                  UnixFileMode.OtherRead | UnixFileMode.OtherWrite | UnixFileMode.OtherExecute)) != 0)
            throw new UnauthorizedAccessException($"The app's socket folder {dir} is open to other users.");
    }

    /// <summary>
    /// Connects to the Host's socket, waiting for it to appear until <paramref name="timeout"/> as a pipe client would,
    /// and refuses a peer that isn't this user.
    /// </summary>
    public static async Task<NetworkStream> ConnectAsync(string pipeName, TimeSpan timeout, CancellationToken cancellationToken)
    {
        var path = PathFor(pipeName);
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        deadline.CancelAfter(timeout);
        while (true)
        {
            var socket = new Socket(AddressFamily.Unix, SocketType.Stream, ProtocolType.Unspecified);
            try
            {
                VerifyDirectory(Path.GetDirectoryName(path)!);
                await socket.ConnectAsync(new UnixDomainSocketEndPoint(path), deadline.Token).ConfigureAwait(false);
                VerifyPeer(socket);
                return new NetworkStream(socket, ownsSocket: true);
            }
            catch (Exception ex) when (ex is SocketException or DirectoryNotFoundException)
            {
                socket.Dispose();
                // Not there yet, or a socket file left by a Host that has gone: wait as a pipe client does.
                try
                {
                    await Task.Delay(100, deadline.Token).ConfigureAwait(false);
                }
                catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
                {
                    throw new TimeoutException($"The app isn't running (no answer on {path}).");
                }
            }
            catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
            {
                socket.Dispose();
                throw new TimeoutException($"The app isn't running (no answer on {path}).");
            }
            catch
            {
                socket.Dispose();
                throw;
            }
        }
    }

    /// <summary>Throws <see cref="UnauthorizedAccessException"/> unless the process at the other end runs as this user.</summary>
    public static void VerifyPeer(Socket socket)
    {
        if (PeerUserId(socket) != EffectiveUserId())
            throw new UnauthorizedAccessException("The other end of the app's socket is another user.");
    }

    /// <summary>The peer's user ID from <c>SO_PEERCRED</c>; -1 when it can't be read.</summary>
    public static long PeerUserId(Socket socket)
    {
        // struct ucred { pid_t pid; uid_t uid; gid_t gid; }
        Span<byte> cred = stackalloc byte[12];
        var length = cred.Length;
        if (GetSockOpt((int)socket.Handle, SolSocket, SoPeerCred, ref MemoryMarshal.GetReference(cred), ref length) != 0 || length < 8)
            return -1;
        return BitConverter.ToUInt32(cred[4..8]);
    }

    public static uint EffectiveUserId() => GetEUid();

    private const int SolSocket = 1;
    private const int SoPeerCred = 17;

    [DllImport("libc", EntryPoint = "geteuid")]
    private static extern uint GetEUid();

    [DllImport("libc", EntryPoint = "getsockopt", SetLastError = true)]
    private static extern int GetSockOpt(int socket, int level, int name, ref byte value, ref int length);

    /// <summary>The owner's user ID of <paramref name="path"/>, not following a final link. 0 on success.</summary>
    private static int Stat(string path, out uint owner)
    {
        // struct statx is the same on every architecture: stx_uid is the u32 at offset 20.
        Span<byte> buffer = stackalloc byte[256];
        owner = uint.MaxValue;
        if (StatX(AtFdCwd, path, AtSymlinkNoFollow, StatxUid, ref MemoryMarshal.GetReference(buffer)) != 0) return -1;
        owner = BitConverter.ToUInt32(buffer[20..24]);
        return 0;
    }

    private const int AtFdCwd = -100;
    private const int AtSymlinkNoFollow = 0x100;
    private const uint StatxUid = 0x8;

    [DllImport("libc", EntryPoint = "statx", SetLastError = true)]
    private static extern int StatX(int dirFd, [MarshalAs(UnmanagedType.LPUTF8Str)] string path, int flags, uint mask, ref byte buffer);
}

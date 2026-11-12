using System.Collections.Concurrent;
using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Text;

namespace TestServer;

/// <summary>
/// Minimal FTP server for integration tests: USER/PASS, SIZE, PASV/EPSV,
/// REST, RETR, binary mode. It intentionally supports only a flat root.
/// </summary>
public sealed class FtpServer : IAsyncDisposable
{
    private const string UserName = "ftpuser";
    private const string Password = "ftppass";

    private readonly TcpListener _listener;
    private readonly CancellationTokenSource _cts = new();
    private readonly Task _acceptTask;
    private readonly ConcurrentDictionary<string, long> _bytesServed = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, int> _retrCounts = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, long> _cutNext = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, long> _stallNext = new(StringComparer.Ordinal);
    private readonly ConcurrentQueue<FtpTransfer> _transfers = new();
    private int _restCount;

    private FtpServer(TcpListener listener, string root)
    {
        _listener = listener;
        Root = root;
        _acceptTask = AcceptLoopAsync(_cts.Token);
    }

    public int Port { get; private set; }
    public string Root { get; }

    public static Task<FtpServer> StartAsync(CancellationToken cancellationToken = default)
    {
        var root = Path.Combine(
            AppContext.BaseDirectory, "ftp-root", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        File.WriteAllBytes(Path.Combine(root, "plain.bin"), TestContent.Generate(302, 65_536));
        File.WriteAllBytes(Path.Combine(root, "auth.bin"), TestContent.Generate(301, 131_072));
        File.WriteAllBytes(Path.Combine(root, "slow.bin"), TestContent.Generate(300, 524_288));

        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var server = new FtpServer(listener, root)
        {
            Port = ((IPEndPoint)listener.LocalEndpoint).Port,
        };
        cancellationToken.ThrowIfCancellationRequested();
        return Task.FromResult(server);
    }

    /// <summary>Bytes written to data connections for this file, counted as they are sent, cut transfers included.</summary>
    public long BytesServed(string fileName) => _bytesServed.GetValueOrDefault(fileName);

    /// <summary>Every RETR of this file so far, in order: the REST offset it started at and what it sent.</summary>
    public IReadOnlyList<FtpTransfer> Transfers(string fileName)
        => _transfers.Where(transfer => transfer.FileName == fileName).ToList();

    /// <summary>The next RETR of this file closes its data connection after this many bytes (426 on the control one).</summary>
    public void CutNextTransfer(string fileName, long afterBytes) => _cutNext[fileName] = afterBytes;

    /// <summary>The next RETR of this file sends this many bytes and then nothing, keeping the connection open.</summary>
    public void StallNextTransfer(string fileName, long afterBytes) => _stallNext[fileName] = afterBytes;

    /// <summary>When set, REST is answered with this reply (for example "502 Command not implemented").</summary>
    public string? RestReply { get; set; }

    /// <summary>When set, RETR of an existing file is answered with this reply and sends nothing (for example "451 Local error").</summary>
    public string? RetrReply { get; set; }
    public int RetrCount(string fileName) => _retrCounts.GetValueOrDefault(fileName);
    public int RestCount => Volatile.Read(ref _restCount);

    private async Task AcceptLoopAsync(CancellationToken cancellationToken)
    {
        while (!cancellationToken.IsCancellationRequested)
        {
            TcpClient client;
            try
            {
                client = await _listener.AcceptTcpClientAsync(cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                break;
            }
            catch (ObjectDisposedException)
            {
                break;
            }

            _ = HandleClientAsync(client, cancellationToken);
        }
    }

    private async Task HandleClientAsync(TcpClient client, CancellationToken cancellationToken)
    {
        using (client)
        {
            client.NoDelay = true;
            await using var stream = client.GetStream();
            using var reader = new StreamReader(stream, Encoding.ASCII, false, 1024, leaveOpen: true);
            using var writer = new StreamWriter(stream, Encoding.ASCII, 1024, leaveOpen: true)
            {
                NewLine = "\r\n",
                AutoFlush = true,
            };

            TcpListener? passive = null;
            string? user = null;
            var authenticated = false;
            long restartOffset = 0;
            await WriteAsync(writer, "220 Test FTP ready").ConfigureAwait(false);

            while (!cancellationToken.IsCancellationRequested)
            {
                string? line;
                try
                {
                    line = await reader.ReadLineAsync(cancellationToken).ConfigureAwait(false);
                }
                catch (OperationCanceledException)
                {
                    break;
                }
                catch (IOException)
                {
                    break;
                }

                if (line is null)
                {
                    break;
                }

                var separator = line.IndexOf(' ');
                var command = (separator < 0 ? line : line[..separator]).Trim().ToUpperInvariant();
                var argument = separator < 0 ? string.Empty : line[(separator + 1)..].Trim();

                if (command == "USER")
                {
                    user = argument;
                    await WriteAsync(writer, "331 Password required").ConfigureAwait(false);
                    continue;
                }

                if (command == "PASS")
                {
                    if (string.Equals(user, UserName, StringComparison.Ordinal) &&
                        string.Equals(argument, Password, StringComparison.Ordinal))
                    {
                        authenticated = true;
                        await WriteAsync(writer, "230 Logged in").ConfigureAwait(false);
                    }
                    else
                    {
                        await WriteAsync(writer, "530 Authentication failed").ConfigureAwait(false);
                    }

                    continue;
                }

                if (command == "QUIT")
                {
                    await WriteAsync(writer, "221 Bye").ConfigureAwait(false);
                    break;
                }

                if (command == "FEAT")
                {
                    await writer.WriteLineAsync("211-Features:").ConfigureAwait(false);
                    await writer.WriteLineAsync(" SIZE").ConfigureAwait(false);
                    await writer.WriteLineAsync(" REST STREAM").ConfigureAwait(false);
                    await writer.WriteLineAsync(" UTF8").ConfigureAwait(false);
                    await WriteAsync(writer, "211 End").ConfigureAwait(false);
                    continue;
                }

                if (!authenticated)
                {
                    await WriteAsync(writer, "530 Please login with USER and PASS").ConfigureAwait(false);
                    continue;
                }

                switch (command)
                {
                    case "SYST":
                        await WriteAsync(writer, "215 UNIX Type: L8").ConfigureAwait(false);
                        break;
                    case "PWD":
                        await WriteAsync(writer, "257 \"/\" is current directory").ConfigureAwait(false);
                        break;
                    case "CWD":
                        await WriteAsync(writer, "250 Directory changed").ConfigureAwait(false);
                        break;
                    case "TYPE":
                    case "MODE":
                    case "STRU":
                    case "OPTS":
                    case "CLNT":
                    case "NOOP":
                        await WriteAsync(writer, "200 OK").ConfigureAwait(false);
                        break;
                    case "PBSZ":
                    case "PROT":
                        await WriteAsync(writer, "200 OK").ConfigureAwait(false);
                        break;
                    case "AUTH":
                        await WriteAsync(writer, "502 TLS not enabled").ConfigureAwait(false);
                        break;
                    case "SIZE":
                    {
                        var path = Resolve(argument);
                        if (path is not null && File.Exists(path))
                        {
                            await WriteAsync(writer, $"213 {new FileInfo(path).Length}").ConfigureAwait(false);
                        }
                        else
                        {
                            await WriteAsync(writer, "550 File not found").ConfigureAwait(false);
                        }

                        break;
                    }
                    case "MDTM":
                    {
                        var path = Resolve(argument);
                        if (path is not null && File.Exists(path))
                        {
                            await WriteAsync(writer,
                                $"213 {File.GetLastWriteTimeUtc(path):yyyyMMddHHmmss}").ConfigureAwait(false);
                        }
                        else
                        {
                            await WriteAsync(writer, "550 File not found").ConfigureAwait(false);
                        }

                        break;
                    }
                    case "REST":
                        if (RestReply is { } restReply)
                        {
                            await WriteAsync(writer, restReply).ConfigureAwait(false);
                        }
                        else if (long.TryParse(argument, out var offset) && offset >= 0)
                        {
                            restartOffset = offset;
                            Interlocked.Increment(ref _restCount);
                            await WriteAsync(writer, "350 Restart position accepted").ConfigureAwait(false);
                        }
                        else
                        {
                            await WriteAsync(writer, "501 Invalid restart position").ConfigureAwait(false);
                        }

                        break;
                    case "PASV":
                    {
                        passive?.Stop();
                        passive = new TcpListener(IPAddress.Loopback, 0);
                        passive.Start();
                        var port = ((IPEndPoint)passive.LocalEndpoint).Port;
                        await WriteAsync(writer,
                            $"227 Entering Passive Mode (127,0,0,1,{port / 256},{port % 256})").ConfigureAwait(false);
                        break;
                    }
                    case "EPSV":
                    {
                        passive?.Stop();
                        passive = new TcpListener(IPAddress.Loopback, 0);
                        passive.Start();
                        var port = ((IPEndPoint)passive.LocalEndpoint).Port;
                        await WriteAsync(writer, $"229 Entering Extended Passive Mode (|||{port}|)").ConfigureAwait(false);
                        break;
                    }
                    case "ABOR":
                        restartOffset = 0;
                        await WriteAsync(writer, "226 Abort successful").ConfigureAwait(false);
                        break;
                    case "RETR":
                    {
                        var path = Resolve(argument);
                        if (path is null || !File.Exists(path))
                        {
                            await WriteAsync(writer, "550 File not found").ConfigureAwait(false);
                            break;
                        }

                        if (passive is null)
                        {
                            await WriteAsync(writer, "425 Use PASV or EPSV first").ConfigureAwait(false);
                            break;
                        }

                        if (RetrReply is { } retrReply)
                        {
                            restartOffset = 0;
                            await WriteAsync(writer, retrReply).ConfigureAwait(false);
                            break;
                        }

                        var name = Path.GetFileName(path);
                        _retrCounts.AddOrUpdate(name, 1, (_, count) => count + 1);
                        var cutAfter = _cutNext.TryRemove(name, out var cut) ? cut : (long?)null;
                        var stallAfter = _stallNext.TryRemove(name, out var stall) ? stall : (long?)null;
                        var transfer = new FtpTransfer(name, restartOffset);
                        await WriteAsync(writer, "150 Opening binary data connection").ConfigureAwait(false);
                        try
                        {
                            using var dataClient = await passive.AcceptTcpClientAsync(cancellationToken)
                                .AsTask().WaitAsync(TimeSpan.FromSeconds(10), cancellationToken)
                                .ConfigureAwait(false);
                            passive.Stop();
                            passive = null;
                            using var dataStream = dataClient.GetStream();
                            _transfers.Enqueue(transfer);
                            var complete = await SendFileAsync(
                                path, dataStream, restartOffset, cutAfter, stallAfter, transfer,
                                sent => _bytesServed.AddOrUpdate(name, sent, (_, total) => total + sent),
                                cancellationToken).ConfigureAwait(false);
                            restartOffset = 0;
                            if (complete)
                            {
                                transfer.Completed = true;
                                await WriteAsync(writer, "226 Transfer complete").ConfigureAwait(false);
                            }
                            else
                            {
                                dataClient.Client.Close();
                                await WriteAsync(writer, "426 Connection closed; transfer aborted").ConfigureAwait(false);
                            }
                        }
                        catch (Exception) when (!cancellationToken.IsCancellationRequested)
                        {
                            restartOffset = 0;
                            try
                            {
                                await WriteAsync(writer, "426 Connection closed; transfer aborted").ConfigureAwait(false);
                            }
                            catch (IOException)
                            {
                                // The client has gone.
                            }
                        }

                        break;
                    }
                    default:
                        await WriteAsync(writer, "502 Command not implemented").ConfigureAwait(false);
                        break;
                }
            }

            passive?.Stop();
        }
    }

    /// <returns>False when the transfer was cut short or stalled on purpose.</returns>
    private static async Task<bool> SendFileAsync(
        string path,
        Stream dataStream,
        long offset,
        long? cutAfter,
        long? stallAfter,
        FtpTransfer transfer,
        Action<long> sent,
        CancellationToken cancellationToken)
    {
        var throttle = string.Equals(Path.GetFileName(path), "slow.bin", StringComparison.OrdinalIgnoreCase)
            ? 131_072
            : 0;
        await using var file = new FileStream(
            path, FileMode.Open, FileAccess.Read, FileShare.Read, 81_920, FileOptions.Asynchronous | FileOptions.SequentialScan);
        if (offset > 0)
        {
            file.Position = Math.Min(offset, file.Length);
        }

        var buffer = new byte[16 * 1024];
        var served = 0L;
        var stopwatch = Stopwatch.StartNew();
        while (true)
        {
            var limit = cutAfter ?? stallAfter;
            var want = limit is { } stop ? (int)Math.Min(buffer.Length, Math.Max(0, stop - served)) : buffer.Length;
            if (want == 0)
            {
                await dataStream.FlushAsync(cancellationToken).ConfigureAwait(false);
                if (cutAfter is not null) return false;
                // A stalled connection: nothing more is sent, and the socket stays open until the client gives up.
                await Task.Delay(TimeSpan.FromSeconds(30), cancellationToken).ConfigureAwait(false);
                return false;
            }

            var read = await file.ReadAsync(buffer.AsMemory(0, want), cancellationToken).ConfigureAwait(false);
            if (read == 0)
            {
                break;
            }

            await dataStream.WriteAsync(buffer.AsMemory(0, read), cancellationToken).ConfigureAwait(false);
            served += read;
            transfer.Sent = served;
            sent(read);
            if (throttle > 0)
            {
                var expected = served / (double)throttle;
                var delay = TimeSpan.FromSeconds(expected) - stopwatch.Elapsed;
                if (delay > TimeSpan.Zero)
                {
                    await Task.Delay(delay, cancellationToken).ConfigureAwait(false);
                }
            }
        }

        await dataStream.FlushAsync(cancellationToken).ConfigureAwait(false);
        return true;
    }

    private string? Resolve(string argument)
    {
        if (string.IsNullOrWhiteSpace(argument))
        {
            return null;
        }

        var name = Path.GetFileName(argument.Replace('/', Path.DirectorySeparatorChar).Trim());
        if (name.Length == 0 || name is "." or "..")
        {
            return null;
        }

        return Path.Combine(Root, name);
    }

    /// <summary>One RETR: the file, the REST offset it began at, the bytes it sent and whether it finished.</summary>
    public sealed class FtpTransfer(string fileName, long offset)
    {
        public string FileName { get; } = fileName;
        public long Offset { get; } = offset;
        public long Sent { get; set; }
        public bool Completed { get; set; }
    }

    private static Task WriteAsync(StreamWriter writer, string line) => writer.WriteLineAsync(line);

    public async ValueTask DisposeAsync()
    {
        _cts.Cancel();
        _listener.Stop();
        try
        {
            await _acceptTask.ConfigureAwait(false);
        }
        catch
        {
            // ignore shutdown errors
        }

        _cts.Dispose();
        try
        {
            Directory.Delete(Root, recursive: true);
        }
        catch
        {
            // best effort
        }
    }
}

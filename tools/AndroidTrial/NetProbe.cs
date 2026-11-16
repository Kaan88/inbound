using System.Diagnostics;
using System.Globalization;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.Json.Nodes;
using Android.Content;

namespace AndroidTrial;

/// <summary>
/// ANDROID.2 phone trial: where the CPU of a download over Wi-Fi goes. dest=net reads (and discards) one URL with `segments`
/// parallel range requests through the read pattern named by the extra <c>impl</c>; dest=write writes 1 GiB to storage and
/// reads nothing; dest=sysdm hands the URL to Android's DownloadManager. Every run reports MB/s, process CPU split in user and
/// kernel, reads and bytes per read (histogram), and the CPU of each thread (/proc/self/task). The engine is not used.
/// Extras (all optional): impl http|sockblock|sockvt|sockasync|android, buf (bytes per read call, default 262144),
/// rcvbuf (SO_RCVBUF), lowat (SO_RCVLOWAT), delayms (sleep after a short read), env "K=V;K=V" (applied before any socket use).
/// </summary>
internal static class NetProbe
{
    internal sealed class ConnStats
    {
        public long Bytes;
        public long Reads;
        public readonly long[] Hist = new long[6];
        public double ThreadUserS;
        public double ThreadKernelS;

        public void Add(int n)
        {
            Bytes += n;
            Reads++;
            Hist[n <= 1500 ? 0 : n <= 4096 ? 1 : n <= 16384 ? 2 : n <= 65536 ? 3 : n <= 262144 ? 4 : 5]++;
        }
    }

    private static readonly string[] HistNames = ["<=1500", "<=4K", "<=16K", "<=64K", "<=256K", ">256K"];

    private sealed record Opts(string Impl, int Buf, int RcvBuf, int Lowat, int DelayMs);

    public static string Extra(TrialRequest r, string key, string fallback) =>
        r.Extras is not null && r.Extras.TryGetValue(key, out var v) ? v : fallback;

    public static async Task RunAsync(Context context, TrialRequest request, JsonObject json, Action<string> say, CancellationToken ct)
    {
        var opts = new Opts(Extra(request, "impl", "http"), int.Parse(Extra(request, "buf", "262144"), CultureInfo.InvariantCulture),
            int.Parse(Extra(request, "rcvbuf", "0"), CultureInfo.InvariantCulture), int.Parse(Extra(request, "lowat", "0"), CultureInfo.InvariantCulture),
            int.Parse(Extra(request, "delayms", "0"), CultureInfo.InvariantCulture));
        json["impl"] = opts.Impl;
        json["buf"] = opts.Buf;
        json["rcvbuf"] = opts.RcvBuf;
        json["lowat"] = opts.Lowat;
        json["delayMs"] = opts.DelayMs;
        foreach (var k in new[] { "DOTNET_SYSTEM_NET_SOCKETS_INLINE_COMPLETIONS", "DOTNET_SYSTEM_NET_SOCKETS_THREAD_COUNT" })
            json["env_" + k] = Environment.GetEnvironmentVariable(k);

        if (request.Dest == "write") { await RunWriteAsync(context, request, json, say, ct).ConfigureAwait(false); return; }
        if (request.Dest == "sysdm") { await RunSystemDownloadAsync(context, request, json, say, ct).ConfigureAwait(false); return; }

        var uri = new Uri(request.Url);
        var size = long.Parse(System.Web.HttpUtility.ParseQueryString(uri.Query)["size"] ?? throw new ArgumentException("url needs ?size="), CultureInfo.InvariantCulture);
        var c = request.Segments;
        var stats = Enumerable.Range(0, c).Select(_ => new ConnStats()).ToArray();
        var chunk = size / c;
        (long From, long To) RangeOf(int i) => (i * chunk, i == c - 1 ? size - 1 : i * chunk + chunk - 1);

        GC.Collect();
        ThreadSampler.Start();
        var before = CpuSnapshot.Take();
        var sw = Stopwatch.StartNew();
        switch (opts.Impl)
        {
            case "http":
            case "android":
                await RunHttpAsync(request, opts, c, RangeOf, stats, ct).ConfigureAwait(false);
                break;
            case "sockblock":
                await Task.WhenAll(Enumerable.Range(0, c).Select(i =>
                {
                    var tcs = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
                    var t = new Thread(() =>
                    {
                        try { BlockingWorker(uri, RangeOf(i), opts, stats[i]); tcs.SetResult(); }
                        catch (Exception ex) { tcs.SetException(ex); }
                    }) { IsBackground = true, Name = "blocking-recv-" + i };
                    t.Start();
                    return tcs.Task;
                })).ConfigureAwait(false);
                break;
            case "sockvt":
                await Task.WhenAll(Enumerable.Range(0, c).Select(i => Task.Run(() => ValueTaskWorkerAsync(uri, RangeOf(i), opts, stats[i], ct), ct))).ConfigureAwait(false);
                break;
            case "sockasync":
                await Task.WhenAll(Enumerable.Range(0, c).Select(i => Task.Run(() => CallbackWorkerAsync(uri, RangeOf(i), opts, stats[i]), ct))).ConfigureAwait(false);
                break;
            default:
                throw new ArgumentException("unknown impl " + opts.Impl);
        }

        sw.Stop();
        var after = CpuSnapshot.Take();
        json["threadGroups"] = ThreadSampler.Stop();
        Report(json, before, after, sw.Elapsed.TotalSeconds, stats, size);
        say($"net {opts.Impl} c={c}: {json["mbPerSecond"]} MB/s");
    }

    // ---------- HttpClient path (SocketsHttpHandler or AndroidMessageHandler), as the engine reads but without the engine ----------

    private static async Task RunHttpAsync(TrialRequest request, Opts opts, int c, Func<int, (long From, long To)> rangeOf, ConnStats[] stats, CancellationToken ct)
    {
        HttpMessageHandler handler;
        if (opts.Impl == "android") handler = new Xamarin.Android.Net.AndroidMessageHandler();
        else
        {
            var sh = new SocketsHttpHandler { MaxConnectionsPerServer = c, AutomaticDecompression = DecompressionMethods.None };
            if (opts.RcvBuf > 0 || opts.Lowat > 0)
                sh.ConnectCallback = async (ctx, token) =>
                {
                    var s = new Socket(SocketType.Stream, ProtocolType.Tcp) { NoDelay = true };
                    if (opts.RcvBuf > 0) s.ReceiveBufferSize = opts.RcvBuf;
                    if (opts.Lowat > 0) s.SetSocketOption(SocketOptionLevel.Socket, SocketOptionName.ReceiveLowWater, opts.Lowat);
                    await s.ConnectAsync(ctx.DnsEndPoint, token).ConfigureAwait(false);
                    return new NetworkStream(s, ownsSocket: true);
                };
            handler = sh;
        }

        using var _ = handler;
        using var client = new HttpClient(handler, disposeHandler: false) { Timeout = Timeout.InfiniteTimeSpan };
        // sink=app|internal (with reader=deadline): write what is read at its offset as the engine does (FileOptions.Asynchronous | RandomAccess).
        FileStream? sink = null;
        string? sinkPath = null;
        if (Extra(request, "sink", "none") is var target and not "none")
        {
            var dir = target == "internal" ? Path.Combine(Android.App.Application.Context.FilesDir!.AbsolutePath, "payload") : Android.App.Application.Context.GetExternalFilesDir(null)!.AbsolutePath;
            Directory.CreateDirectory(dir);
            sinkPath = Path.Combine(dir, $"inbound-trial-sink-{request.RunId}.bin");
            sink = new FileStream(sinkPath, FileMode.Create, FileAccess.ReadWrite, FileShare.ReadWrite, 128 * 1024, FileOptions.Asynchronous | FileOptions.RandomAccess);
            sink.SetLength(long.Parse(System.Web.HttpUtility.ParseQueryString(new Uri(request.Url).Query)["size"]!, CultureInfo.InvariantCulture));
        }

        await Task.WhenAll(Enumerable.Range(0, c).Select(async i =>
        {
            var (from, to) = rangeOf(i);
            using var message = new HttpRequestMessage(HttpMethod.Get, request.Url);
            if (c > 1) message.Headers.Range = new System.Net.Http.Headers.RangeHeaderValue(from, to);
            // Connection: close so the server's FIN ends a read that SO_RCVLOWAT would otherwise hold back.
            message.Headers.ConnectionClose = true;
            using var response = await client.SendAsync(message, HttpCompletionOption.ResponseHeadersRead, ct).ConfigureAwait(false);
            response.EnsureSuccessStatusCode();
            await using var stream = await response.Content.ReadAsStreamAsync(ct).ConfigureAwait(false);
            var st = stats[i];
            if (Extra(request, "reader", "plain") == "deadline")
            {
                // The engine's read pattern (TransferReader replica): one linked CTS re-armed per socket read, FillAsync(fill), then an optional write at the offset.
                using var reader = new DeadlineReader(new CountingStream(stream, st), opts.Buf, TimeSpan.FromSeconds(90), ct);
                var fill = int.Parse(Extra(request, "fill", "65536"), CultureInfo.InvariantCulture);
                var offset = c > 1 ? from : 0;
                int got;
                while ((got = await reader.FillAsync(fill).ConfigureAwait(false)) > 0)
                {
                    if (sink is not null) await RandomAccess.WriteAsync(sink.SafeFileHandle, reader.Buffer[..got], offset, ct).ConfigureAwait(false);
                    offset += got;
                }

                return;
            }

            var buffer = new byte[opts.Buf];
            int n;
            while ((n = await stream.ReadAsync(buffer, ct).ConfigureAwait(false)) > 0)
            {
                st.Add(n);
                if (opts.DelayMs > 0 && n < opts.Buf / 2) await Task.Delay(opts.DelayMs, ct).ConfigureAwait(false);
            }
        })).ConfigureAwait(false);
        sink?.Dispose();
        if (sinkPath is not null) File.Delete(sinkPath);
    }

    /// <summary>Counts reads and bytes per read of the stream under a reader that asks for more than one packet at a time.</summary>
    private sealed class CountingStream(Stream inner, ConnStats st) : Stream
    {
        public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        {
            var n = await inner.ReadAsync(buffer, cancellationToken).ConfigureAwait(false);
            if (n > 0) st.Add(n);
            return n;
        }

        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
        public override void Flush() { }
        public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }

    /// <summary>A copy of src/DownloadManager.Engine/TransferReader.cs (internal there), so the engine's per-read cost can be measured without the engine.</summary>
    private sealed class DeadlineReader : IDisposable
    {
        private readonly Stream _stream;
        private readonly TimeSpan _timeout;
        private readonly CancellationToken _outer;
        private readonly byte[] _array;
        private CancellationTokenSource _deadline;

        public DeadlineReader(Stream stream, int bufferSize, TimeSpan timeout, CancellationToken outer)
        {
            _stream = stream;
            _timeout = timeout;
            _outer = outer;
            _array = System.Buffers.ArrayPool<byte>.Shared.Rent(bufferSize);
            Buffer = _array.AsMemory(0, bufferSize);
            _deadline = CancellationTokenSource.CreateLinkedTokenSource(outer);
        }

        public Memory<byte> Buffer { get; }

        public async ValueTask<int> FillAsync(int atLeast)
        {
            atLeast = Math.Clamp(atLeast, 1, Buffer.Length);
            var filled = 0;
            while (filled < atLeast)
            {
                var read = await ReadOnceAsync(Buffer[filled..]).ConfigureAwait(false);
                if (read == 0) break;
                filled += read;
            }

            return filled;
        }

        private async ValueTask<int> ReadOnceAsync(Memory<byte> into)
        {
            if (!_deadline.TryReset())
            {
                _deadline.Dispose();
                _deadline = CancellationTokenSource.CreateLinkedTokenSource(_outer);
            }

            _deadline.CancelAfter(_timeout);
            try
            {
                var pending = _stream.ReadAsync(into, _deadline.Token);
                if (pending.IsCompletedSuccessfully) return pending.Result;
                return await pending.AsTask().WaitAsync(_deadline.Token).ConfigureAwait(false);
            }
            finally
            {
                _deadline.CancelAfter(Timeout.InfiniteTimeSpan);
            }
        }

        public void Dispose()
        {
            _deadline.Dispose();
            System.Buffers.ArrayPool<byte>.Shared.Return(_array);
        }
    }

    // ---------- plain Socket: a hand-written HTTP/1.1 GET with a Range header, header read synchronously ----------

    private static (Socket Socket, long Remaining) Open(Uri uri, (long From, long To) range, Opts opts, ConnStats st)
    {
        var s = new Socket(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp) { NoDelay = true };
        if (opts.RcvBuf > 0) s.ReceiveBufferSize = opts.RcvBuf;
        s.Connect(uri.Host, uri.Port);
        var req = $"GET {uri.PathAndQuery} HTTP/1.1\r\nHost: {uri.Host}:{uri.Port}\r\nRange: bytes={range.From}-{range.To}\r\nConnection: close\r\n\r\n";
        s.Send(Encoding.ASCII.GetBytes(req));
        var head = new byte[16384];
        var filled = 0;
        int end;
        while (true)
        {
            var n = s.Receive(head, filled, head.Length - filled, SocketFlags.None);
            if (n == 0) throw new IOException("closed in headers");
            filled += n;
            end = IndexOfHeaderEnd(head, filled);
            if (end >= 0) break;
        }

        var status = Encoding.ASCII.GetString(head, 0, 12);
        if (!status.StartsWith("HTTP/1.1 20", StringComparison.Ordinal)) throw new IOException("status " + status);
        var body = filled - end;
        st.Bytes += body;
        if (opts.Lowat > 0) s.SetSocketOption(SocketOptionLevel.Socket, SocketOptionName.ReceiveLowWater, opts.Lowat);
        return (s, range.To - range.From + 1 - body);
    }

    private static int IndexOfHeaderEnd(byte[] b, int len)
    {
        for (var i = 3; i < len; i++)
            if (b[i] == '\n' && b[i - 1] == '\r' && b[i - 2] == '\n' && b[i - 3] == '\r') return i + 1;
        return -1;
    }

    private static void BlockingWorker(Uri uri, (long From, long To) range, Opts opts, ConnStats st)
    {
        var (u0, k0) = CpuSnapshot.ThreadNow();
        var (s, remaining) = Open(uri, range, opts, st);
        using (s)
        {
            var buf = new byte[opts.Buf];
            while (remaining > 0)
            {
                var n = s.Receive(buf, 0, (int)Math.Min(buf.Length, remaining), SocketFlags.None);
                if (n == 0) throw new IOException("closed early");
                st.Add(n);
                remaining -= n;
                if (opts.DelayMs > 0 && n < opts.Buf / 2) Thread.Sleep(opts.DelayMs);
            }
        }

        var (u1, k1) = CpuSnapshot.ThreadNow();
        st.ThreadUserS = u1 - u0;
        st.ThreadKernelS = k1 - k0;
    }

    private static async Task ValueTaskWorkerAsync(Uri uri, (long From, long To) range, Opts opts, ConnStats st, CancellationToken ct)
    {
        var (s, remaining) = Open(uri, range, opts, st);
        using (s)
        {
            var buf = new byte[opts.Buf];
            while (remaining > 0)
            {
                var n = await s.ReceiveAsync(buf.AsMemory(0, (int)Math.Min(buf.Length, remaining)), SocketFlags.None, ct).ConfigureAwait(false);
                if (n == 0) throw new IOException("closed early");
                st.Add(n);
                remaining -= n;
                if (opts.DelayMs > 0 && n < opts.Buf / 2) await Task.Delay(opts.DelayMs, ct).ConfigureAwait(false);
            }
        }
    }

    /// <summary>SocketAsyncEventArgs with a Completed callback and no await at all: the cheapest managed async pattern.</summary>
    private static Task CallbackWorkerAsync(Uri uri, (long From, long To) range, Opts opts, ConnStats st)
    {
        var (s, remaining) = Open(uri, range, opts, st);
        var done = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var args = new SocketAsyncEventArgs();
        args.SetBuffer(new byte[opts.Buf], 0, opts.Buf);
        void Finish(Exception? ex)
        {
            args.Dispose();
            s.Dispose();
            if (ex is null) done.TrySetResult(); else done.TrySetException(ex);
        }

        // Returns true to issue another receive.
        bool Handle(SocketAsyncEventArgs e)
        {
            if (e.SocketError != SocketError.Success) { Finish(new SocketException((int)e.SocketError)); return false; }
            var n = e.BytesTransferred;
            if (n == 0) { Finish(new IOException("closed early")); return false; }
            st.Add(n);
            remaining -= n;
            if (remaining <= 0) { Finish(null); return false; }
            if (opts.DelayMs > 0 && n < opts.Buf / 2) Thread.Sleep(opts.DelayMs);
            e.SetBuffer(0, (int)Math.Min(opts.Buf, remaining));
            return true;
        }

        void Pump(SocketAsyncEventArgs e)
        {
            try
            {
                args.SetBuffer(0, (int)Math.Min(opts.Buf, remaining));
                while (true)
                {
                    if (s.ReceiveAsync(e)) return; // pending: Completed will call back
                    if (!Handle(e)) return; // completed synchronously
                }
            }
            catch (Exception ex) { Finish(ex); }
        }

        args.Completed += (_, e) => { if (Handle(e)) Pump(e); };
        Pump(args);
        return done.Task;
    }

    // ---------- write-only ----------

    private static async Task RunWriteAsync(Context context, TrialRequest request, JsonObject json, Action<string> say, CancellationToken ct)
    {
        var dir = request.Url switch // for dest=write the "url" is the target: app | internal | downloads
        {
            "app" => context.GetExternalFilesDir(null)!.AbsolutePath,
            "internal" => Path.Combine(context.FilesDir!.AbsolutePath, "payload"),
            "downloads" => Android.OS.Environment.GetExternalStoragePublicDirectory(Android.OS.Environment.DirectoryDownloads)!.AbsolutePath,
            _ => throw new ArgumentException("write target app|internal|downloads"),
        };
        Directory.CreateDirectory(dir);
        var size = long.Parse(Extra(request, "size", "1073741824"), CultureInfo.InvariantCulture);
        var chunk = int.Parse(Extra(request, "chunk", "131072"), CultureInfo.InvariantCulture);
        var style = Extra(request, "wstyle", "async"); // async = RandomAccess.WriteAsync on a FileOptions.Asynchronous handle (the engine); sync = RandomAccess.Write
        var path = Path.Combine(dir, $"inbound-trial-write-{request.RunId}.bin");
        json["dir"] = dir;
        json["chunk"] = chunk;
        json["wstyle"] = style;
        var data = new byte[chunk];
        new Random(1).NextBytes(data);
        var opt = style == "async" ? FileOptions.Asynchronous | FileOptions.RandomAccess : FileOptions.RandomAccess;
        var before = CpuSnapshot.Take();
        var sw = Stopwatch.StartNew();
        long written = 0;
        using (var file = new FileStream(path, FileMode.Create, FileAccess.ReadWrite, FileShare.ReadWrite, 128 * 1024, opt))
        {
            file.SetLength(size);
            for (long off = 0; off < size; off += chunk)
            {
                var n = (int)Math.Min(chunk, size - off);
                if (style == "async") await RandomAccess.WriteAsync(file.SafeFileHandle, data.AsMemory(0, n), off, ct).ConfigureAwait(false);
                else RandomAccess.Write(file.SafeFileHandle, data.AsSpan(0, n), off);
                written += n;
            }

            RandomAccess.FlushToDisk(file.SafeFileHandle);
        }

        sw.Stop();
        var after = CpuSnapshot.Take();
        var st = new ConnStats { Bytes = written, Reads = (written + chunk - 1) / chunk };
        Report(json, before, after, sw.Elapsed.TotalSeconds, [st], size);
        File.Delete(path);
        say($"write {dir} chunk={chunk} {style}: {json["mbPerSecond"]} MB/s");
    }

    // ---------- Android's own DownloadManager ----------

    private static async Task RunSystemDownloadAsync(Context context, TrialRequest request, JsonObject json, Action<string> say, CancellationToken ct)
    {
        var dm = (Android.App.DownloadManager)context.GetSystemService(Context.DownloadService)!;
        var name = $"inbound-trial-sysdm-{request.RunId}.bin";
        var req = new Android.App.DownloadManager.Request(Android.Net.Uri.Parse(request.Url)!);
        req.SetDestinationInExternalFilesDir(context, null, name);
        req.SetNotificationVisibility(Android.App.DownloadVisibility.Hidden);
        var before = CpuSnapshot.Take();
        var sw = Stopwatch.StartNew();
        var id = dm.Enqueue(req);
        json["downloadId"] = id;
        long lastBytes = 0;
        string status = "?";
        while (true)
        {
            using var cursor = dm.InvokeQuery(new Android.App.DownloadManager.Query().SetFilterById(id));
            if (cursor is not null && cursor.MoveToFirst())
            {
                var st = cursor.GetInt(cursor.GetColumnIndex(Android.App.DownloadManager.ColumnStatus));
                lastBytes = cursor.GetLong(cursor.GetColumnIndex(Android.App.DownloadManager.ColumnBytesDownloadedSoFar));
                status = st.ToString(CultureInfo.InvariantCulture);
                json["reason"] = cursor.GetInt(cursor.GetColumnIndex(Android.App.DownloadManager.ColumnReason));
                if (st == 8 || st == 16) break; // successful / failed
            }
            else { status = "gone"; break; }

            if (sw.Elapsed > TimeSpan.FromMinutes(5)) { status = "timeout"; break; }
            await Task.Delay(250, ct).ConfigureAwait(false);
        }

        sw.Stop();
        var after = CpuSnapshot.Take();
        json["statusCode"] = status;
        json["bytes"] = lastBytes;
        json["wallSeconds"] = sw.Elapsed.TotalSeconds;
        json["mbPerSecond"] = lastBytes / 1e6 / sw.Elapsed.TotalSeconds;
        json["ownProcessCpuSeconds"] = after.TotalSeconds - before.TotalSeconds; // the polling only: the download runs in another process
        json["success"] = status == "8";
        dm.Remove(id);
        say("sysdm " + status);
    }

    // ---------- result ----------

    private static void Report(JsonObject json, CpuSnapshot before, CpuSnapshot after, double wall, ConnStats[] stats, long size)
    {
        var bytes = stats.Sum(s => s.Bytes);
        var reads = stats.Sum(s => s.Reads);
        var user = after.UserS - before.UserS;
        var kernel = after.KernelS - before.KernelS;
        var gb = bytes / 1e9;
        json["success"] = bytes == size;
        json["bytes"] = bytes;
        json["wallSeconds"] = wall;
        json["mbPerSecond"] = Math.Round(bytes / 1e6 / wall, 2);
        json["cpuUserSeconds"] = Math.Round(user, 2);
        json["cpuKernelSeconds"] = Math.Round(kernel, 2);
        json["cpuSeconds"] = Math.Round(user + kernel, 2);
        json["cpuUserPerGB"] = Math.Round(user / gb, 2);
        json["cpuKernelPerGB"] = Math.Round(kernel / gb, 2);
        json["cpuSecondsPerGB"] = Math.Round((user + kernel) / gb, 2);
        json["reads"] = reads;
        json["bytesPerRead"] = reads > 0 ? Math.Round((double)bytes / reads) : 0;
        var hist = new JsonObject();
        for (var i = 0; i < HistNames.Length; i++) hist[HistNames[i]] = stats.Sum(s => s.Hist[i]);
        json["readSizeHistogram"] = hist;
        json["voluntaryCtxSwitches"] = after.Vol - before.Vol;
        json["involuntaryCtxSwitches"] = after.Invol - before.Invol;
        json["threadCountAtEnd"] = after.Threads.Count;
        var threads = new JsonArray();
        foreach (var t in after.Threads.OrderByDescending(t => t.Value.User + t.Value.Kernel).Take(8))
        {
            before.Threads.TryGetValue(t.Key, out var b);
            var du = t.Value.User - b.User;
            var dk = t.Value.Kernel - b.Kernel;
            if (du + dk < 0.05) continue;
            threads.Add(new JsonObject { ["tid"] = t.Key, ["name"] = t.Value.Name, ["user"] = Math.Round(du, 2), ["kernel"] = Math.Round(dk, 2) });
        }

        json["topThreads"] = threads;
        if (stats.Sum(s => s.ThreadUserS + s.ThreadKernelS) > 0)
            json["workerThreadsExitedCpu"] = new JsonObject { ["user"] = Math.Round(stats.Sum(s => s.ThreadUserS), 2), ["kernel"] = Math.Round(stats.Sum(s => s.ThreadKernelS), 2) };
    }
}

/// <summary>CPU of this process from /proc/self/stat (100 ticks/s on Android) and of each of its threads from /proc/self/task.</summary>
internal sealed class CpuSnapshot
{
    private const double Tick = 0.01;

    public double UserS;
    public double KernelS;
    public long Vol;
    public long Invol;
    public Dictionary<int, (string Name, double User, double Kernel)> Threads = [];

    public double TotalSeconds => UserS + KernelS;

    public static CpuSnapshot Take()
    {
        var s = new CpuSnapshot();
        (s.UserS, s.KernelS) = Parse(File.ReadAllText("/proc/self/stat"), out _);
        try
        {
            foreach (var dir in Directory.EnumerateDirectories("/proc/self/task"))
            {
                try
                {
                    var tid = int.Parse(Path.GetFileName(dir), CultureInfo.InvariantCulture);
                    var (u, k) = Parse(File.ReadAllText(dir + "/stat"), out var name);
                    s.Threads[tid] = (name, u, k);
                    foreach (var line in File.ReadLines(dir + "/status"))
                    {
                        if (line.StartsWith("voluntary_ctxt_switches:", StringComparison.Ordinal)) s.Vol += long.Parse(line[24..].Trim(), CultureInfo.InvariantCulture);
                        else if (line.StartsWith("nonvoluntary_ctxt_switches:", StringComparison.Ordinal)) s.Invol += long.Parse(line[27..].Trim(), CultureInfo.InvariantCulture);
                    }
                }
                catch (IOException) { /* thread ended */ }
            }
        }
        catch (IOException) { }

        return s;
    }

    public static (double User, double Kernel) ThreadNow() => Parse(File.ReadAllText("/proc/thread-self/stat"), out _);

    private static (double, double) Parse(string stat, out string name)
    {
        var open = stat.IndexOf('(');
        var close = stat.LastIndexOf(')');
        name = stat[(open + 1)..close];
        var f = stat[(close + 2)..].Split(' ');
        return (long.Parse(f[11], CultureInfo.InvariantCulture) * Tick, long.Parse(f[12], CultureInfo.InvariantCulture) * Tick);
    }
}

/// <summary>Phone trial: samples /proc/self/task every 250 ms between Start and Stop, so the CPU of threads that exit inside the window is kept; grouped by thread name.</summary>
internal static class ThreadSampler
{
    private static readonly Dictionary<int, (string Name, double U, double K)> Base = [];
    private static readonly Dictionary<int, (string Name, double U, double K)> Last = [];
    private static CancellationTokenSource? _cts;
    private static Task? _task;
    private static readonly object Gate = new();

    private static void Sample(bool baseline)
    {
        var s = CpuSnapshot.Take();
        lock (Gate)
        {
            foreach (var (tid, t) in s.Threads)
            {
                Last[tid] = t;
                if (baseline) Base[tid] = t;
            }
        }
    }

    public static void Start()
    {
        Base.Clear(); Last.Clear();
        Sample(true);
        _cts = new CancellationTokenSource();
        var token = _cts.Token;
        _task = Task.Run(async () =>
        {
            while (!token.IsCancellationRequested)
            {
                try { await Task.Delay(250, token).ConfigureAwait(false); } catch (OperationCanceledException) { break; }
                Sample(false);
            }
        });
    }

    public static System.Text.Json.Nodes.JsonArray Stop()
    {
        _cts?.Cancel();
        try { _task?.Wait(); } catch { }
        Sample(false);
        var groups = new Dictionary<string, (double U, double K, int N)>();
        lock (Gate)
            foreach (var (tid, t) in Last)
            {
                Base.TryGetValue(tid, out var b);
                var u = t.U - b.U; var k = t.K - b.K;
                groups.TryGetValue(t.Name, out var g);
                groups[t.Name] = (g.U + u, g.K + k, g.N + 1);
            }

        var arr = new System.Text.Json.Nodes.JsonArray();
        foreach (var (name, g) in groups.OrderByDescending(x => x.Value.U + x.Value.K))
            arr.Add(new System.Text.Json.Nodes.JsonObject { ["name"] = name, ["threads"] = g.N, ["user"] = Math.Round(g.U, 2), ["kernel"] = Math.Round(g.K, 2), ["total"] = Math.Round(g.U + g.K, 2) });
        return arr;
    }
}

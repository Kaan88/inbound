using System.Buffers.Binary;
using System.Diagnostics;
using System.Net.Sockets;
using System.Text.Json;
using DownloadManager.Contracts;

namespace Bench;

/// <summary>
/// Start times of the shipped programs, always against an isolated instance: DOWNLOADMANAGER_INSTANCE is unique, DOWNLOADMANAGER_DATA
/// is a temp folder, and on Linux the runtime folder (sockets) is a temp folder and the session bus is removed from the child's
/// environment, so the Host cannot reach the desktop keyring, tray or notifications. The user's own instance is never contacted.
/// </summary>
internal static class StartTimes
{
    public static readonly string[] Columns = ["target", "scenario", "kind", "run", "first_output_ms", "ready_ms", "exit_ms"];

    private sealed class Isolation : IDisposable
    {
        private readonly Dictionary<string, string?> _saved = new();

        public static Isolation Create(string dataBase, string realPipe)
        {
            var iso = new Isolation(dataBase);
            if (PipeNames.HostPipe() == realPipe)
            {
                iso.Dispose();
                throw new InvalidOperationException("The isolated endpoint name equals the real one; refusing to go on.");
            }

            return iso;
        }

        private Isolation(string dataBase)
        {
            var id = Guid.NewGuid().ToString("N")[..8];
            Instance = "bench-" + id;
            DataDir = Path.Combine(dataBase, "start-" + id);
            Directory.CreateDirectory(DataDir);
            Vars["DOWNLOADMANAGER_INSTANCE"] = Instance;
            Vars["DOWNLOADMANAGER_DATA"] = DataDir;
            if (OperatingSystem.IsLinux())
            {
                // Short path: a unix socket path is limited to about 108 characters.
                RuntimeDir = Path.Combine(Path.GetTempPath(), "ib-" + id);
                Directory.CreateDirectory(RuntimeDir, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
                Vars["XDG_RUNTIME_DIR"] = RuntimeDir;
                Vars["XDG_CONFIG_HOME"] = Path.Combine(DataDir, "xdg-config");
                Vars["XDG_DATA_HOME"] = Path.Combine(DataDir, "xdg-data");
                Vars["DBUS_SESSION_BUS_ADDRESS"] = null;
            }

            // This process computes the endpoint name from the same variables.
            foreach (var (k, v) in Vars)
            {
                _saved[k] = Environment.GetEnvironmentVariable(k);
                Environment.SetEnvironmentVariable(k, v);
            }
        }

        public string Instance { get; }
        public string DataDir { get; }
        public string? RuntimeDir { get; }
        public Dictionary<string, string?> Vars { get; } = new();

        public void Apply(ProcessStartInfo info)
        {
            foreach (var (k, v) in Vars)
            {
                if (v is null) info.Environment.Remove(k);
                else info.Environment[k] = v;
            }
        }

        public void Dispose()
        {
            foreach (var (k, v) in _saved) Environment.SetEnvironmentVariable(k, v);
            Throughput.TryDelete(DataDir);
            if (RuntimeDir is not null) Throughput.TryDelete(RuntimeDir);
        }
    }

    public static async Task RunAsync(Csv csv, string cli, string nativeHost, string host, string dataBase, int runs)
    {
        Directory.CreateDirectory(dataBase);
        var realPipe = PipeNames.HostPipe();

        // 1. The CLI with no Host running (nothing listens in the isolated instance).
        using (var iso = Isolation.Create(dataBase, realPipe))
        {
            await Measure(csv, runs, "cli", "--version", () => RunProgramAsync(iso, cli, ["--version"])).ConfigureAwait(false);
            await Measure(csv, runs, "cli", "--help", () => RunProgramAsync(iso, cli, ["--help"])).ConfigureAwait(false);
            await Measure(csv, runs, "cli", "doctor --no-start (no Host)", () => RunProgramAsync(iso, cli, ["doctor", "--no-start"])).ConfigureAwait(false);
        }

        // 2. The Host: process start to the IPC endpoint accepting a connection, each run with a fresh data folder.
        await Measure(csv, runs, "host", "endpoint accepts connection (fresh data folder)", async () =>
        {
            using var iso = Isolation.Create(dataBase, realPipe);
            return await HostRunAsync(iso, host, cli).ConfigureAwait(false);
        }).ConfigureAwait(false);

        // 3. One Host that stays up while the native host and the CLI are timed against it.
        using (var iso = Isolation.Create(dataBase, realPipe))
        {
            using var running = await StartHostAsync(iso, host).ConfigureAwait(false);
            if (running is null) { Console.WriteLine("[start] the Host did not become ready; native host and CLI-with-Host rows skipped."); return; }
            await Measure(csv, runs, "native-host", "first reply to host.ping (Host running)", () => NativeHostRunAsync(iso, nativeHost)).ConfigureAwait(false);
            await Measure(csv, runs, "cli", "status --no-start (Host running)", () => RunProgramAsync(iso, cli, ["status", "--no-start"])).ConfigureAwait(false);
            await StopHostAsync(iso, cli, running).ConfigureAwait(false);
        }
    }

    private sealed record Sample(double FirstOutputMs, double ReadyMs, double ExitMs);

    private static async Task Measure(Csv csv, int runs, string target, string scenario, Func<Task<Sample>> once)
    {
        var ready = new List<double>();
        var first = new List<double>();
        var exit = new List<double>();
        // Run 0 is a warm-up (disk cache, first-use costs): written to the CSV as kind=warmup, left out of the statistics.
        for (var run = 0; run <= runs; run++)
        {
            var s = await once().ConfigureAwait(false);
            csv.Row(target, scenario, run == 0 ? "warmup" : "measured", run, s.FirstOutputMs, s.ReadyMs, s.ExitMs);
            if (run == 0) continue;
            if (!double.IsNaN(s.ReadyMs)) ready.Add(s.ReadyMs);
            if (!double.IsNaN(s.FirstOutputMs)) first.Add(s.FirstOutputMs);
            if (!double.IsNaN(s.ExitMs)) exit.Add(s.ExitMs);
        }

        string Fmt(string name, List<double> v) => v.Count == 0 ? "" : $" {name} median {Stats.Median(v):0.0} (min {v.Min():0.0}, max {v.Max():0.0}) ms;";
        Console.WriteLine($"[start] {target,-11} {scenario,-52} runs={runs}:{Fmt("first-output", first)}{Fmt("ready", ready)}{Fmt("exit", exit)}");
    }

    private static ProcessStartInfo Info(Isolation iso, string exe, IEnumerable<string> args, bool stdin = false)
    {
        var info = new ProcessStartInfo(exe)
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            RedirectStandardInput = stdin,
        };
        foreach (var a in args) info.ArgumentList.Add(a);
        iso.Apply(info);
        return info;
    }

    /// <summary>Runs a command to completion; first_output is the first byte on stdout or stderr.</summary>
    private static async Task<Sample> RunProgramAsync(Isolation iso, string exe, string[] args)
    {
        var sw = Stopwatch.StartNew();
        using var p = Process.Start(Info(iso, exe, args)) ?? throw new InvalidOperationException($"Could not start {exe}.");
        var firstOut = new TaskCompletionSource<double>(TaskCreationOptions.RunContinuationsAsynchronously);

        async Task Pump(Stream s)
        {
            var buffer = new byte[4096];
            var n = await s.ReadAsync(buffer).ConfigureAwait(false);
            if (n > 0) firstOut.TrySetResult(sw.Elapsed.TotalMilliseconds);
            while (n > 0) n = await s.ReadAsync(buffer).ConfigureAwait(false);
        }

        var pumps = Task.WhenAll(Pump(p.StandardOutput.BaseStream), Pump(p.StandardError.BaseStream));
        await p.WaitForExitAsync().ConfigureAwait(false);
        var exit = sw.Elapsed.TotalMilliseconds;
        await pumps.ConfigureAwait(false);
        return new Sample(firstOut.Task.IsCompletedSuccessfully ? firstOut.Task.Result : double.NaN, double.NaN, exit);
    }

    // ---- Host -------------------------------------------------------------------------------------------------------------

    private sealed class RunningHost(Process process) : IDisposable
    {
        public Process Process { get; } = process;
        public double ReadyMs { get; set; }

        public void Dispose()
        {
            try { if (!Process.HasExited) { Process.Kill(true); Process.WaitForExit(5000); } } catch (Exception) { }
            Process.Dispose();
        }
    }

    private static async Task<RunningHost?> StartHostAsync(Isolation iso, string host)
    {
        var info = Info(iso, host, ["--background"]);
        var sw = Stopwatch.StartNew();
        var process = Process.Start(info) ?? throw new InvalidOperationException("Could not start the Host.");
        process.OutputDataReceived += (_, _) => { };
        process.ErrorDataReceived += (_, _) => { };
        process.BeginOutputReadLine();
        process.BeginErrorReadLine();
        var running = new RunningHost(process);
        var deadline = TimeSpan.FromSeconds(60);
        while (sw.Elapsed < deadline && !process.HasExited)
        {
            if (TryConnect())
            {
                running.ReadyMs = sw.Elapsed.TotalMilliseconds;
                return running;
            }

            if (OperatingSystem.IsLinux()) await Task.Delay(1).ConfigureAwait(false);
        }

        running.Dispose();
        return null;
    }

    /// <summary>Whether the isolated Host's IPC endpoint accepts a connection (this process has the isolated variables set).</summary>
    private static bool TryConnect()
    {
        try
        {
            if (OperatingSystem.IsLinux())
            {
                using var socket = new Socket(AddressFamily.Unix, SocketType.Stream, ProtocolType.Unspecified);
                socket.Connect(new UnixDomainSocketEndPoint(UnixSockets.PathFor(PipeNames.HostPipe())));
                return true;
            }

            using var pipe = new System.IO.Pipes.NamedPipeClientStream(".", PipeNames.HostPipe(), System.IO.Pipes.PipeDirection.InOut);
            pipe.Connect(1);
            return true;
        }
        catch (Exception ex) when (ex is SocketException or IOException or TimeoutException or UnauthorizedAccessException)
        {
            return false;
        }
    }

    private static async Task<double> StopHostAsync(Isolation iso, string cli, RunningHost running)
    {
        var sw = Stopwatch.StartNew();
        await RunProgramAsync(iso, cli, ["host", "shutdown", "--no-start"]).ConfigureAwait(false);
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        try { await running.Process.WaitForExitAsync(cts.Token).ConfigureAwait(false); }
        catch (OperationCanceledException) { Console.WriteLine("[start] the isolated Host did not exit after shutdown; killing it."); }
        return sw.Elapsed.TotalMilliseconds;
    }

    private static async Task<Sample> HostRunAsync(Isolation iso, string host, string cli)
    {
        using var running = await StartHostAsync(iso, host).ConfigureAwait(false) ?? throw new InvalidOperationException("The isolated Host did not become ready within 60 s.");
        var ready = running.ReadyMs;
        var exit = await StopHostAsync(iso, cli, running).ConfigureAwait(false);
        return new Sample(double.NaN, ready, exit);
    }

    // ---- Native host ------------------------------------------------------------------------------------------------------

    /// <summary>
    /// Starts the native host as a browser would (the trusted extension origin as its argument), sends one host.ping and times the
    /// first byte on stdout and the ping's reply (the host connects to the Host first, so this is "ready").
    /// </summary>
    private static async Task<Sample> NativeHostRunAsync(Isolation iso, string exe)
    {
        var sw = Stopwatch.StartNew();
        using var p = Process.Start(Info(iso, exe, [BrowserBridge.StoreExtensionOrigin], stdin: true)) ?? throw new InvalidOperationException("Could not start the native host.");
        p.ErrorDataReceived += (_, _) => { };
        p.BeginErrorReadLine();
        var ping = IpcMessage.Create(MessageTypes.HostPing);
        await IpcFraming.WriteAsync(p.StandardInput.BaseStream, ping).ConfigureAwait(false);
        var stdout = p.StandardOutput.BaseStream;
        double first = double.NaN, ready = double.NaN;
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        var header = new byte[4];
        try
        {
            while (double.IsNaN(ready))
            {
                await stdout.ReadExactlyAsync(header, timeout.Token).ConfigureAwait(false);
                if (double.IsNaN(first)) first = sw.Elapsed.TotalMilliseconds;
                var body = new byte[BinaryPrimitives.ReadInt32LittleEndian(header)];
                await stdout.ReadExactlyAsync(body, timeout.Token).ConfigureAwait(false);
                var message = JsonSerializer.Deserialize<IpcMessage>(body, IpcJson.Options);
                if (message?.ReplyTo == ping.Id) ready = sw.Elapsed.TotalMilliseconds;
            }
        }
        catch (Exception ex) when (ex is OperationCanceledException or EndOfStreamException) { }

        p.StandardInput.Close();
        using var exitTimeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        try { await p.WaitForExitAsync(exitTimeout.Token).ConfigureAwait(false); }
        catch (OperationCanceledException) { try { p.Kill(true); } catch (Exception) { } }
        return new Sample(first, ready, sw.Elapsed.TotalMilliseconds);
    }
}

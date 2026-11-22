using System.ComponentModel;
using System.Diagnostics;
using System.Globalization;
using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text.Json.Nodes;

namespace DownloadManager.Sites;

/// <summary>A running rclone as the supervisor sees it; <see cref="RcloneProcess"/> in the Host, a stand-in in tests.</summary>
public interface IRcloneInstance : IAsyncDisposable
{
    RcloneClient Client { get; }
    int ProcessId { get; }

    /// <summary>Completes when the process has ended, for any reason.</summary>
    Task Exited { get; }

    /// <summary>Asks rclone to quit (<c>core/quit</c>), waits up to <paramref name="grace"/>, then kills it. True when it quit by itself.</summary>
    Task<bool> StopAsync(TimeSpan grace);
}

/// <summary>
/// One <c>rclone rcd</c> (remote-sites.md, "Start"; SITES.0 points 2 and 10, "Also checked"):
/// <list type="bullet">
/// <item>Loopback only, on a free port, with a random user and password made for this start and passed in
/// <c>RCLONE_RC_USER</c>/<c>RCLONE_RC_PASS</c>, never as flags (flags show in every process list). Every other inherited
/// <c>RCLONE_*</c> variable is removed.</item>
/// <item><c>--config ""</c>: no config file is read or written; <c>--cache-dir</c> and <c>--temp-dir</c> in the Host's
/// state folder. Never above the default log level: <c>-vv</c> logs the password. Its log lines are redacted anyway.</item>
/// <item>Finished rc jobs are kept 10 minutes instead of rclone's 1, so a busy Host still reads every result.</item>
/// <item>Tied to the Host (<see cref="ChildLifetime"/>), and a pid file lets the next start end an rclone that a crashed
/// Host left behind.</item>
/// </list>
/// </summary>
public sealed class RcloneProcess : IRcloneInstance
{
    public const string PidFileName = "rclone.pid";

    /// <summary>SITES.6b: the trusted-certificates file in the state folder, passed as <c>--ca-cert</c> when it exists.</summary>
    public const string TrustedCertificatesFileName = "trusted-certificates.pem";
    private const int StartAttempts = 3;

    private readonly Process _process;
    private readonly ChildLifetime _lifetime;
    private readonly string _pidFile;
    private readonly TaskCompletionSource _exited = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly Queue<string> _recentLines = new();
    private readonly Action<string>? _log;

    private RcloneProcess(Process process, ChildLifetime lifetime, RcloneClient client, string pidFile, Action<string>? log)
    {
        _log = log;
        _process = process;
        _lifetime = lifetime;
        Client = client;
        _pidFile = pidFile;
    }

    public RcloneClient Client { get; }

    public int ProcessId => _process.Id;

    public Task Exited => _exited.Task;

    public int? ExitCode => _process.HasExited ? _process.ExitCode : null;

    /// <summary>True when rclone runs under <c>setpriv --pdeathsig</c> (Linux) or in a kill-on-close job (Windows).</summary>
    public bool TiedByKernel { get; private init; }

    /// <summary>
    /// Starts rclone and waits until it answers an authenticated <c>rc/noop</c>. <paramref name="stateDirectory"/> is the
    /// Host's own folder for rclone (pid file, cache, temp); it is created.
    /// </summary>
    public static async Task<RcloneProcess> StartAsync(string executable, string stateDirectory, Action<string>? log = null,
        TimeSpan? startTimeout = null, CancellationToken cancellationToken = default)
    {
        Directory.CreateDirectory(stateDirectory);
        var pidFile = Path.Combine(stateDirectory, PidFileName);
        EndLeftover(pidFile, log);

        Exception? last = null;
        for (var attempt = 1; attempt <= StartAttempts; attempt++)
        {
            try
            {
                return await StartOnceAsync(executable, stateDirectory, pidFile, log, startTimeout ?? TimeSpan.FromSeconds(20),
                    cancellationToken).ConfigureAwait(false);
            }
            catch (RclonePortTakenException e)
            {
                // The free port was taken between choosing it and rclone binding it; choose another.
                last = e;
            }
        }

        throw new RcloneUnavailableException("rclone could not open its control port.", last);
    }

    private static async Task<RcloneProcess> StartOnceAsync(string executable, string stateDirectory, string pidFile,
        Action<string>? log, TimeSpan startTimeout, CancellationToken cancellationToken)
    {
        var user = "inbound-" + RandomToken(6);
        var password = RandomToken(32);
        var port = FreeLoopbackPort();
        var cache = Path.Combine(stateDirectory, "cache");
        var temp = Path.Combine(stateDirectory, "temp");
        Directory.CreateDirectory(cache);
        Directory.CreateDirectory(temp);

        var setpriv = ChildLifetime.FindSetpriv();
        var info = new ProcessStartInfo(setpriv ?? executable)
        {
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
            WorkingDirectory = stateDirectory,
        };
        if (setpriv is not null)
        {
            info.ArgumentList.Add("--pdeathsig");
            info.ArgumentList.Add("KILL");
            info.ArgumentList.Add("--");
            info.ArgumentList.Add(executable);
        }

        foreach (var argument in new[]
        {
            "rcd",
            "--rc-addr", "127.0.0.1:" + port.ToString(CultureInfo.InvariantCulture),
            "--config", "",
            "--cache-dir", cache,
            "--temp-dir", temp,
            "--rc-job-expire-duration", "10m",
            // rclone's default looks for expired jobs every 10 s; once a minute wakes an idle rclone far less.
            "--rc-job-expire-interval", "1m",
            // SITES.7: a file of a named remote is read over HTTP (GET /[name:]path), for "to this device". Only the
            // Host has the credentials, and only named remotes the Host made exist (--config "").
            "--rc-serve",
            "--log-level", "NOTICE",
            "--use-json-log=false",
        }) info.ArgumentList.Add(argument);

        // SITES.6b: certificates the user trusted for FTPS and https sites, with the system's roots, which this file
        // replaces for the process. rclone reads it only here, at start.
        var trusted = Path.Combine(stateDirectory, TrustedCertificatesFileName);
        if (File.Exists(trusted))
        {
            info.ArgumentList.Add("--ca-cert");
            info.ArgumentList.Add(trusted);
        }

        RcloneEnvironment.Clean(info.Environment);
        info.Environment["RCLONE_RC_USER"] = user;
        info.Environment["RCLONE_RC_PASS"] = password;

        Process process;
        try
        {
            process = setpriv is null
                ? Process.Start(info) ?? throw new InvalidOperationException("no process")
                : await ChildLifetime.Spawner.StartAsync(() => Process.Start(info) ?? throw new InvalidOperationException("no process"))
                    .ConfigureAwait(false);
        }
        catch (Exception e) when (e is Win32Exception or InvalidOperationException)
        {
            throw new RcloneUnavailableException($"rclone could not be started: {e.Message}", e);
        }

        var lifetime = ChildLifetime.Tie(process);
        var exitedFlag = 0;
        // Android's native handler can throw "Socket closed" when an optional request is
        // canceled. Use the managed handler for rclone's loopback control requests.
        var handler = OperatingSystem.IsAndroid() ? new SocketsHttpHandler { UseProxy = false } : null;
        var client = new RcloneClient(new Uri($"http://127.0.0.1:{port.ToString(CultureInfo.InvariantCulture)}/"), user, password,
            () => Volatile.Read(ref exitedFlag) == 1, handler);
        client.AddSecret(user);
        var rclone = new RcloneProcess(process, lifetime, client, pidFile, log)
        {
            TiedByKernel = setpriv is not null || lifetime.InJob,
        };
        _ = rclone.PumpAsync(process.StandardError, log);
        _ = rclone.PumpAsync(process.StandardOutput, log);
        _ = Task.Run(async () =>
        {
            try { await process.WaitForExitAsync().ConfigureAwait(false); }
            catch (Exception) { }
            Volatile.Write(ref exitedFlag, 1);
            rclone._exited.TrySetResult();
        }, CancellationToken.None);

        try
        {
            // Inside the try: a pid file that can't be written must not leave a started rclone behind unwatched.
            rclone.WritePidFile();
            await rclone.WaitReadyAsync(startTimeout, cancellationToken).ConfigureAwait(false);
            log?.Invoke($"rclone started (pid {process.Id}, port {port}).");
            if (!rclone.TiedByKernel)
                log?.Invoke(OperatingSystem.IsLinux()
                    ? "Warning: setpriv (util-linux) was not found, so rclone is not ended by the system if the Host crashes; the next start ends it."
                    : OperatingSystem.IsMacOS()
                        // macOS has no parent-death signal or job object: rclone outlives a crashed Host until the next start.
                        ? "rclone is not ended by macOS if the Host crashes; the next start ends it."
                        : "Warning: rclone could not be put in a job object, so it is not ended by the system if the Host crashes; the next start ends it.");
            return rclone;
        }
        catch
        {
            await rclone.StopAsync(TimeSpan.Zero).ConfigureAwait(false);
            await rclone.DisposeAsync().ConfigureAwait(false);
            throw;
        }
    }

    private async Task WaitReadyAsync(TimeSpan timeout, CancellationToken cancellationToken)
    {
        using var limit = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        limit.CancelAfter(timeout);
        var delay = TimeSpan.FromMilliseconds(10);
        while (true)
        {
            if (_exited.Task.IsCompleted)
            {
                var lines = RecentLines();
                if (lines.Contains("address already in use", StringComparison.OrdinalIgnoreCase)
                    || lines.Contains("Only one usage of each socket address", StringComparison.OrdinalIgnoreCase))
                    throw new RclonePortTakenException();
                throw new RcloneUnavailableException(
                    $"rclone exited at start (code {ExitCode?.ToString(CultureInfo.InvariantCulture) ?? "?"})" +
                    (lines.Length == 0 ? "." : ": " + lines));
            }

            try
            {
                using var attempt = CancellationTokenSource.CreateLinkedTokenSource(limit.Token);
                attempt.CancelAfter(TimeSpan.FromSeconds(2));
                await Client.CallAsync("rc/noop", null, attempt.Token).ConfigureAwait(false);
                return;
            }
            catch (RcloneUnavailableException)
            {
                // Not listening yet.
            }
            catch (OperationCanceledException) when (!limit.IsCancellationRequested)
            {
                // That one attempt hung; try again.
            }
            catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
            {
                throw new RcloneUnavailableException($"rclone did not answer within {timeout.TotalSeconds:0} seconds.");
            }

            try
            {
                await Task.WhenAny(_exited.Task, Task.Delay(delay, limit.Token)).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
            {
                throw new RcloneUnavailableException($"rclone did not answer within {timeout.TotalSeconds:0} seconds.");
            }

            if (delay < TimeSpan.FromMilliseconds(200)) delay *= 2;
        }
    }

    public async Task<bool> StopAsync(TimeSpan grace)
    {
        var quit = _exited.Task.IsCompleted;
        try
        {
            if (!quit && grace > TimeSpan.Zero)
            {
                try
                {
                    using var ask = new CancellationTokenSource(grace);
                    await Client.CallAsync("core/quit", null, ask.Token).ConfigureAwait(false);
                }
                catch (Exception)
                {
                    // Anything: it may close the connection as it quits. The wait below decides, and the kill follows.
                }

                quit = await Task.WhenAny(_exited.Task, Task.Delay(grace)).ConfigureAwait(false) == _exited.Task;
            }
        }
        finally
        {
            if (!quit)
            {
                try { _process.Kill(entireProcessTree: true); }
                catch (Exception e) when (e is InvalidOperationException or Win32Exception) { }
                if (await Task.WhenAny(_exited.Task, Task.Delay(TimeSpan.FromSeconds(10))).ConfigureAwait(false) != _exited.Task)
                    _log?.Invoke($"rclone (pid {_process.Id}) did not end 10 s after it was killed.");
            }

            DeletePidFile();
        }

        return quit;
    }

    /// <summary>Ends rclone at once, as a crash would; for tests of what the supervisor does then.</summary>
    internal void Kill()
    {
        try { _process.Kill(entireProcessTree: true); }
        catch (Exception e) when (e is InvalidOperationException or Win32Exception) { }
    }

    public async ValueTask DisposeAsync()
    {
        try
        {
            if (!_exited.Task.IsCompleted) await StopAsync(TimeSpan.FromSeconds(5)).ConfigureAwait(false);
        }
        finally
        {
            _lifetime.Dispose();
            Client.Dispose();
            _process.Dispose();
        }
    }

    private async Task PumpAsync(StreamReader reader, Action<string>? log)
    {
        try
        {
            string? line;
            while ((line = await reader.ReadLineAsync().ConfigureAwait(false)) is not null)
            {
                var safe = Client.Redact(line);
                lock (_recentLines)
                {
                    _recentLines.Enqueue(safe);
                    while (_recentLines.Count > 5) _recentLines.Dequeue();
                }

                // A logger that throws must not stop the reading: a full pipe would block rclone.
                try { log?.Invoke("rclone: " + safe); }
                catch (Exception) { }
            }
        }
        catch (Exception e) when (e is IOException or ObjectDisposedException)
        {
        }
    }

    private string RecentLines()
    {
        lock (_recentLines) return string.Join(" | ", _recentLines);
    }

    // ------------------------------------------------------------------ pid file

    private void WritePidFile()
    {
        // Without a start time a later sweep could not tell this rclone from a process that reused its pid, so it would
        // never end it; say so instead of writing a record that can't match.
        if (StartTicks(_process) is not { } ticks)
        {
            _log?.Invoke("rclone's start time could not be read; a copy left by a crash will not be ended at the next start.");
            return;
        }

        var text = string.Join('\n', _process.Id.ToString(CultureInfo.InvariantCulture), ticks.ToString(CultureInfo.InvariantCulture));
        var temp = _pidFile + ".tmp";
        File.WriteAllText(temp, text);
        File.Move(temp, _pidFile, overwrite: true);
    }

    private void DeletePidFile()
    {
        try
        {
            // Only our own: a second Host on the same folder never shares it, but a stale write could.
            if (File.Exists(_pidFile) && ReadPidFile(_pidFile)?.Pid == _process.Id) File.Delete(_pidFile);
        }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
    }

    /// <summary>
    /// Ends the rclone a crashed Host left (its pid file is still there). Only a process with that pid, that start time and
    /// "rclone" in its name: a reused pid is someone else's process and is left alone.
    /// </summary>
    internal static void EndLeftover(string pidFile, Action<string>? log)
    {
        var record = ReadPidFile(pidFile);
        if (record is null) return;
        try
        {
            using var process = Process.GetProcessById(record.Value.Pid);
            if (!process.HasExited && process.ProcessName.Contains("rclone", StringComparison.OrdinalIgnoreCase)
                && StartTicks(process) == record.Value.StartTicks)
            {
                process.Kill(entireProcessTree: true);
                process.WaitForExit(5000);
                log?.Invoke($"Ended an rclone left by an earlier run (pid {record.Value.Pid}).");
            }
        }
        catch (ArgumentException)
        {
            // Not running any more.
        }
        catch (Exception e) when (e is InvalidOperationException or Win32Exception or NotSupportedException)
        {
        }

        try { File.Delete(pidFile); }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
    }

    internal static (int Pid, long StartTicks)? ReadPidFile(string pidFile)
    {
        try
        {
            var lines = File.ReadAllLines(pidFile);
            if (lines.Length >= 2
                && int.TryParse(lines[0], NumberStyles.None, CultureInfo.InvariantCulture, out var pid)
                && long.TryParse(lines[1], NumberStyles.None, CultureInfo.InvariantCulture, out var ticks))
                return (pid, ticks);
        }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
        return null;
    }

    private static long? StartTicks(Process process)
    {
        try { return process.StartTime.ToUniversalTime().Ticks; }
        catch (Exception e) when (e is InvalidOperationException or Win32Exception or NotSupportedException) { return null; }
    }

    // ------------------------------------------------------------------ helpers

    private static string RandomToken(int bytes) =>
        Convert.ToBase64String(RandomNumberGenerator.GetBytes(bytes)).TrimEnd('=').Replace('+', '-').Replace('/', '_');

    private static int FreeLoopbackPort()
    {
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        return ((IPEndPoint)listener.LocalEndpoint).Port;
    }

    private sealed class RclonePortTakenException() : Exception("The control port was taken.");
}

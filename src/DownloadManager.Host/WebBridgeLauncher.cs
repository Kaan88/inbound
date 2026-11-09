using System.Diagnostics;
using System.Text.Json;
using DownloadManager.Contracts;
using DownloadManager.Runtime.WebUi;

namespace DownloadManager.Host;

/// <summary>
/// Starts <c>DownloadManager.Server.exe --bridge</c> (D8) with no window. Its options, the ticket key among them, go on
/// its standard input as one JSON line (<see cref="WebBridgeStartDto"/>), never on its command line or in its
/// environment; standard input then stays open, and closing it stops the bridge, so a bridge never outlives the Host
/// (a Host that is killed closes it too). Its standard output says when it listens; its standard error says why not.
/// </summary>
public sealed class WebBridgeLauncher : IWebBridgeLauncher
{
    public static readonly string ExecutableName = OperatingSystem.IsWindows() ? "DownloadManager.Server.exe" : "DownloadManager.Server";

    /// <summary>
    /// A test or portable instance only (one with its own <c>DOWNLOADMANAGER_DATA</c>): the bridge program to start
    /// instead of the one next to the Host, such as a build in another folder.
    /// </summary>
    public const string TestBridgeVariable = "DOWNLOADMANAGER_BRIDGE";

    private static readonly System.Text.UTF8Encoding Utf8 = new(encoderShouldEmitUTF8Identifier: false);

    private readonly Action<string> _warn;

    public WebBridgeLauncher(Action<string> warn, string? bridgePath = null)
    {
        _warn = warn;
        BridgePath = bridgePath ?? FindBridge();
    }

    public string BridgePath { get; }

    /// <summary>Next to the Host, or in its <c>web</c> folder; the first when neither is there (shown, and the start says it is missing).</summary>
    public static string FindBridge()
    {
        if (!string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("DOWNLOADMANAGER_DATA")) &&
            Environment.GetEnvironmentVariable(TestBridgeVariable) is { } test && Path.IsPathFullyQualified(test))
            return Path.GetFullPath(test);
        var candidates = new[]
        {
            Path.Combine(AppContext.BaseDirectory, ExecutableName),
            Path.Combine(AppContext.BaseDirectory, "web", ExecutableName),
        };
        return Path.GetFullPath(candidates.FirstOrDefault(File.Exists) ?? candidates[0]);
    }

    public IWebBridgeProcess Start(WebBridgeStartDto start)
    {
        if (!File.Exists(BridgePath)) throw new FileNotFoundException("The web bridge program is missing.", BridgePath);
        var info = new ProcessStartInfo(BridgePath)
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            // UTF-8 both ways, as the bridge reads and writes them: the data folder's path holds the user's name, which
            // the console's code page (the OEM one for a hidden console) can't always hold.
            StandardInputEncoding = Utf8,
            StandardOutputEncoding = Utf8,
            StandardErrorEncoding = Utf8,
            // Not the program's folder, which it would hold open against an update (Program.LeaveProgramFolder).
            WorkingDirectory = Environment.CurrentDirectory,
        };
        info.ArgumentList.Add("--bridge");
        // Where it listens comes from its options only: ASP.NET's own variables would add addresses.
        foreach (var name in info.Environment.Keys.Where(name => name.StartsWith("ASPNETCORE_", StringComparison.OrdinalIgnoreCase)).ToList())
            info.Environment.Remove(name);
        var process = Process.Start(info) ?? throw new InvalidOperationException("The web bridge did not start.");
        var bridge = new BridgeProcess(process, _warn);
        try
        {
            process.StandardInput.WriteLine(JsonSerializer.Serialize(start, IpcJson.Options));
            process.StandardInput.Flush();
        }
        catch (IOException)
        {
            // It ended at once; Exited says how.
        }

        return bridge;
    }

    private sealed class BridgeProcess : IWebBridgeProcess
    {
        private readonly Process _process;
        private readonly TaskCompletionSource _ready = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly TaskCompletionSource<WebBridgeExitInfo> _exited = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly Task _output;
        private readonly Task<string?> _error;

        public BridgeProcess(Process process, Action<string> warn)
        {
            _process = process;
            ProcessId = process.Id;
            _output = ReadOutputAsync();
            _error = ReadErrorAsync();
            process.EnableRaisingEvents = true;
            process.Exited += (_, _) => _ = CompleteAsync(warn);
            if (process.HasExited) _ = CompleteAsync(warn);
        }

        public int ProcessId { get; }
        public Task Ready => _ready.Task;
        public Task<WebBridgeExitInfo> Exited => _exited.Task;

        private async Task ReadOutputAsync()
        {
            try
            {
                while (await _process.StandardOutput.ReadLineAsync().ConfigureAwait(false) is { } line)
                {
                    if (line.Trim() == WebBridgeExit.ReadyLine) _ready.TrySetResult();
                }
            }
            catch (Exception ex) when (ex is IOException or ObjectDisposedException or InvalidOperationException)
            {
                // It ended.
            }
        }

        /// <summary>The last line it wrote on standard error, at most 500 characters.</summary>
        private async Task<string?> ReadErrorAsync()
        {
            string? last = null;
            try
            {
                while (await _process.StandardError.ReadLineAsync().ConfigureAwait(false) is { } line)
                {
                    if (!string.IsNullOrWhiteSpace(line)) last = line.Trim();
                }
            }
            catch (Exception ex) when (ex is IOException or ObjectDisposedException or InvalidOperationException)
            {
                // It ended.
            }

            return last is { Length: > 500 } ? last[..500] : last;
        }

        private int _completing;

        private async Task CompleteAsync(Action<string> warn)
        {
            if (Interlocked.Exchange(ref _completing, 1) != 0) return;
            string? message = null;
            try
            {
                // The streams end with the process; a grandchild holding them must not hold this up.
                await Task.WhenAll(_output, _error).WaitAsync(TimeSpan.FromSeconds(2)).ConfigureAwait(false);
                message = await _error.ConfigureAwait(false);
            }
            catch (TimeoutException)
            {
                warn("The web bridge's output did not end with it.");
            }

            int code;
            try
            {
                code = _process.ExitCode;
            }
            catch (InvalidOperationException)
            {
                code = WebBridgeExit.Crashed;
            }

            _exited.TrySetResult(new WebBridgeExitInfo(code, message));
            _process.Dispose();
        }

        public async Task StopAsync(TimeSpan grace)
        {
            try
            {
                _process.StandardInput.Close();
            }
            catch (Exception ex) when (ex is IOException or InvalidOperationException or ObjectDisposedException)
            {
                // Gone already.
            }

            try
            {
                await _exited.Task.WaitAsync(grace).ConfigureAwait(false);
                return;
            }
            catch (TimeoutException)
            {
                // Ended below.
            }

            try
            {
                _process.Kill(entireProcessTree: true);
            }
            catch (Exception ex) when (ex is InvalidOperationException or System.ComponentModel.Win32Exception or NotSupportedException)
            {
                // Gone in between.
            }

            await _exited.Task.WaitAsync(TimeSpan.FromSeconds(10)).ConfigureAwait(false);
        }
    }
}

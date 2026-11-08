using System.Collections.Concurrent;
using System.Diagnostics;
using System.IO.Pipes;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using DownloadManager.Contracts;

namespace DownloadManager.Integration.Tests;

/// <summary>
/// The real DownloadManager.Host.exe in a profile of its own: its own data folder, pipe and mutex.
/// The installed app and the real state are out of reach, and only the mock is called. Everything
/// the Host sends back is kept, so a test can search it afterwards.
/// </summary>
internal sealed partial class SandboxHost : IAsyncDisposable
{
    public const string ApiVariable = "DOWNLOADMANAGER_TORBOX_API";

    private readonly string _apiAddress;
    private readonly List<string> _frames = [];
    private readonly List<HostConnection> _connections = [];
    private Process? _process;

    private SandboxHost(string apiAddress)
    {
        _apiAddress = apiAddress;
        Root = Path.Combine(AppContext.BaseDirectory, "test-work", "host-" + Guid.NewGuid().ToString("N"));
        DataDirectory = Path.Combine(Root, "data");
        Destination = Path.Combine(Root, "downloads");
        Instance = "t" + Guid.NewGuid().ToString("N");
        Directory.CreateDirectory(DataDirectory);
        Directory.CreateDirectory(Destination);
    }

    public string Root { get; }
    public string DataDirectory { get; }
    public string Destination { get; }
    public string Instance { get; }
    public string PipeName { get; private set; } = string.Empty;
    public int? LastExitCode { get; private set; }

    /// <summary>The running Host's process id, for a test that has to kill it from another thread at an exact moment.</summary>
    public int? ProcessId => _process?.Id;

    /// <summary>Every frame the Host has sent, on any connection, as text with JSON escapes undone.</summary>
    public IReadOnlyList<string> Frames
    {
        get
        {
            lock (_frames) return _frames.ToList();
        }
    }

    public static async Task<SandboxHost> StartAsync(Uri api, Action<SandboxHost>? beforeStart = null) => await StartAsync(api.AbsoluteUri, beforeStart);

    public static async Task<SandboxHost> StartAsync(string apiAddress, Action<SandboxHost>? beforeStart = null)
    {
        var host = new SandboxHost(apiAddress);
        beforeStart?.Invoke(host);
        await host.LaunchAsync();
        await using var setup = await host.ConnectAsync();
        var settings = (await setup.AskAsync<SettingsResult>(MessageTypes.SettingsGet, new { })).Settings!;
        // No toasts or sounds on the desktop of whoever runs the tests, and nothing near their real downloads.
        settings.NotifyOnCompletion = false;
        settings.PlayCompletionSound = false;
        settings.DefaultDownloadDirectory = host.Destination;
        settings.AutoResumeUnfinishedOnStartup = true;
        var updated = await setup.AskAsync<SettingsResult>(MessageTypes.SettingsUpdate, new SettingsUpdateRequest { Settings = settings });
        Assert.True(updated.Ok, updated.Error);
        return host;
    }

    private static string Executable(string project, string framework)
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        var configuration = directory.Parent!.Name;
        while (!File.Exists(Path.Combine(directory.FullName, "DownloadManager.slnx")))
        {
            directory = directory.Parent ?? throw new InvalidOperationException("Could not find the repository root.");
        }

        var path = Path.Combine(directory.FullName, "src", project, "bin", configuration, framework, project + ".exe");
        return File.Exists(path) ? path : throw new FileNotFoundException($"{project} has not been built: {path}");
    }

    public static string HostExecutable => Executable("DownloadManager.Host", "net10.0-windows");

    private ProcessStartInfo Prepare(string executable)
    {
        var info = new ProcessStartInfo(executable)
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            WorkingDirectory = Root,
        };
        foreach (var name in info.Environment.Keys.Where(name => name.StartsWith("DOWNLOADMANAGER_", StringComparison.OrdinalIgnoreCase)).ToList())
        {
            info.Environment.Remove(name);
        }

        info.Environment["DOWNLOADMANAGER_INSTANCE"] = Instance;
        info.Environment["DOWNLOADMANAGER_DATA"] = DataDirectory;
        info.Environment["DOWNLOADMANAGER_HOST"] = HostExecutable;
        info.Environment[ApiVariable] = _apiAddress;
        return info;
    }

    private static void Abandon(Process process)
    {
        try
        {
            if (!process.HasExited) process.Kill(entireProcessTree: true);
        }
        catch (Exception ex) when (ex is InvalidOperationException or System.ComponentModel.Win32Exception or NotSupportedException)
        {
            // Already gone.
        }
    }

    private async Task LaunchAsync()
    {
        var info = Prepare(HostExecutable);
        info.ArgumentList.Add("--background");
        var alreadyReady = LogLines().Count(line => line.Contains("Host ready.", StringComparison.Ordinal));
        var process = Process.Start(info) ?? throw new InvalidOperationException("The Host did not start.");
        _process = process;

        // Even if the test run itself is stopped, nothing stays running in the background.
        AppDomain.CurrentDomain.ProcessExit += (_, _) => Abandon(process);

        var deadline = DateTimeOffset.UtcNow + TimeSpan.FromSeconds(30);
        while (true)
        {
            var lines = LogLines();
            if (lines.Count(line => line.Contains("Host ready.", StringComparison.Ordinal)) > alreadyReady)
            {
                // It logged into this profile's folder, so it isn't using any other state.
                PipeName = PipeLine().Match(string.Join('\n', lines)).Groups["name"].Value;
                Assert.NotEmpty(PipeName);
                return;
            }

            if (process.HasExited)
            {
                throw new InvalidOperationException($"The Host exited with {process.ExitCode}: {string.Join(" | ", lines.TakeLast(5))}");
            }

            if (DateTimeOffset.UtcNow > deadline)
            {
                process.Kill(entireProcessTree: true);
                throw new TimeoutException("The Host did not get ready in its sandbox profile.");
            }

            await Task.Delay(50);
        }
    }

    [GeneratedRegex("listening on pipe '(?<name>[^']+)'")]
    private static partial Regex PipeLine();

    public IReadOnlyList<string> LogLines()
    {
        var lines = new List<string>();
        var logs = Path.Combine(DataDirectory, "logs");
        if (!Directory.Exists(logs))
        {
            return lines;
        }

        foreach (var file in Directory.GetFiles(logs))
        {
            lines.AddRange(ReadShared(file).Split('\n', StringSplitOptions.RemoveEmptyEntries));
        }

        return lines;
    }

    private static string ReadShared(string path)
    {
        for (var attempt = 1; ; attempt++)
        {
            try
            {
                using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
                using var reader = new StreamReader(stream, Encoding.Latin1);
                return reader.ReadToEnd();
            }
            catch (FileNotFoundException)
            {
                return string.Empty;
            }
            catch (IOException) when (attempt < 20)
            {
                Thread.Sleep(50);
            }
        }
    }

    public async Task<HostConnection> ConnectAsync()
    {
        var connection = await HostConnection.OpenAsync(PipeName, frame =>
        {
            lock (_frames) _frames.Add(frame);
        });
        lock (_connections) _connections.Add(connection);
        return connection;
    }

    /// <summary>The process dies at once, as in a crash or a power cut. Nothing gets to tidy up.</summary>
    public async Task KillAsync()
    {
        var process = _process!;
        process.Kill(entireProcessTree: true);
        await WaitUntilGoneAsync(process);
        process.Dispose();
        _process = null;
        await CloseConnectionsAsync();
    }

    /// <summary>
    /// Until its handles are closed too. WaitForExit can return as soon as the exit code is set, which comes earlier: a
    /// next start in that moment still finds the old Host's mutex, takes itself for a second copy and exits with 0.
    /// </summary>
    public static async Task WaitUntilGoneAsync(Process process)
    {
        await process.WaitForExitAsync();
        using var gone = new ManualResetEvent(false) { SafeWaitHandle = new Microsoft.Win32.SafeHandles.SafeWaitHandle(process.Handle, ownsHandle: false) };
        await Task.Run(() => gone.WaitOne(TimeSpan.FromSeconds(30)));
    }

    /// <summary>
    /// taskkill /F, as a user or a script does it, and back at once: the process may still be exiting, with its
    /// mutex still open, when the next start comes. Returns the killed process so a test can wait for it.
    /// </summary>
    public async Task<Process> TaskkillWithoutWaitingAsync()
    {
        var process = _process!;
        using (var taskkill = Process.Start(new ProcessStartInfo("taskkill.exe")
        {
            ArgumentList = { "/F", "/PID", process.Id.ToString(System.Globalization.CultureInfo.InvariantCulture) },
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        }) ?? throw new InvalidOperationException("taskkill did not start."))
        {
            await taskkill.WaitForExitAsync();
            Assert.Equal(0, taskkill.ExitCode);
        }

        _process = null;
        await CloseConnectionsAsync();
        return process;
    }

    /// <summary>
    /// A user's exit (host.shutdown), and back at once: the next start comes while this one is still shutting down,
    /// with its pipe and mutex still open. Returns the exiting process so a test can wait for it.
    /// </summary>
    public async Task<Process> ExitWithoutWaitingAsync()
    {
        var process = _process!;
        await using (var connection = await ConnectAsync())
        {
            await connection.AskAsync<HostActionResult>(MessageTypes.HostShutdown, new { });
        }

        _process = null;
        await CloseConnectionsAsync();
        return process;
    }

    public Task RestartAsync() => LaunchAsync();

    /// <summary>The Host's single-instance mutex in this profile, named as <see cref="PipeNames.MutexName"/> names it there.</summary>
    public string MutexName
    {
        get
        {
            var identity = $"{Environment.UserDomainName}\\{Environment.UserName}\nInstance:{Instance}";
            var hash = System.Security.Cryptography.SHA256.HashData(Encoding.UTF8.GetBytes(identity));
            return $"Local\\DownloadManager.Host.{Convert.ToHexString(hash.AsSpan(0, 8)).ToLowerInvariant()}";
        }
    }

    /// <summary>A start that is expected to end by itself: its exit code, or a timeout.</summary>
    public async Task<int> StartExpectingExitAsync(TimeSpan wait)
    {
        var info = Prepare(HostExecutable);
        info.ArgumentList.Add("--background");
        using var process = Process.Start(info) ?? throw new InvalidOperationException("The Host did not start.");
        using var timeout = new CancellationTokenSource(wait);
        try
        {
            await process.WaitForExitAsync(timeout.Token);
            return process.ExitCode;
        }
        catch (OperationCanceledException)
        {
            Abandon(process);
            throw new TimeoutException("The Host kept running.");
        }
    }

    /// <summary>A clean exit, through the same message a user's exit takes.</summary>
    public async Task StopAsync()
    {
        if (_process is not { } process)
        {
            return;
        }

        await using (var connection = await ConnectAsync())
        {
            await connection.AskAsync<HostActionResult>(MessageTypes.HostShutdown, new { });
        }

        await CloseConnectionsAsync();
        using var wait = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        try
        {
            await process.WaitForExitAsync(wait.Token);
            LastExitCode = process.ExitCode;
        }
        catch (OperationCanceledException)
        {
            process.Kill(entireProcessTree: true);
            throw new TimeoutException("The Host did not exit when asked.");
        }
        finally
        {
            process.Dispose();
            _process = null;
        }
    }

    private async Task CloseConnectionsAsync()
    {
        List<HostConnection> open;
        lock (_connections)
        {
            open = _connections.ToList();
            _connections.Clear();
        }

        foreach (var connection in open)
        {
            await connection.DisposeAsync();
        }
    }

    public async Task<CliResult> RunCliAsync(string? input, params string[] arguments)
    {
        var info = Prepare(Executable("DownloadManager.Cli", "net10.0"));
        info.RedirectStandardInput = true;
        info.RedirectStandardOutput = true;
        info.RedirectStandardError = true;
        info.StandardOutputEncoding = Encoding.UTF8;
        info.StandardErrorEncoding = Encoding.UTF8;
        info.StandardInputEncoding = new UTF8Encoding(false);
        foreach (var argument in arguments) info.ArgumentList.Add(argument);
        info.ArgumentList.Add("--no-start");

        using var process = Process.Start(info) ?? throw new InvalidOperationException("The CLI did not start.");
        var output = process.StandardOutput.ReadToEndAsync();
        var error = process.StandardError.ReadToEndAsync();
        if (input is not null) await process.StandardInput.WriteLineAsync(input);
        process.StandardInput.Close();
        using var wait = new CancellationTokenSource(TimeSpan.FromSeconds(60));
        await process.WaitForExitAsync(wait.Token);
        return new CliResult(process.ExitCode, await output, await error);
    }

    /// <summary>The CLI with its output on a pipe, as a script has it, allowed to start the Host itself.</summary>
    /// <returns>Whether the pipe ended within the wait, with the Host left running.</returns>
    public async Task<bool> CliStartingTheHostEndsItsOutputAsync(TimeSpan wait, params string[] arguments)
    {
        var info = Prepare(Executable("DownloadManager.Cli", "net10.0"));
        info.RedirectStandardInput = true;
        info.RedirectStandardOutput = true;
        info.RedirectStandardError = true;
        foreach (var argument in arguments) info.ArgumentList.Add(argument);

        using var process = Process.Start(info) ?? throw new InvalidOperationException("The CLI did not start.");
        process.StandardInput.Close();
        var output = process.StandardOutput.ReadToEndAsync();
        var error = process.StandardError.ReadToEndAsync();
        var both = Task.WhenAll(output, error);
        return await Task.WhenAny(both, Task.Delay(wait)) == both;
    }

    /// <summary>The native host as a browser starts it; by default as Chrome does for the unpacked extension.</summary>
    public Process StartNativeHost(params string[] arguments)
    {
        var info = Prepare(Executable("DownloadManager.NativeHost", "net10.0"));
        info.RedirectStandardInput = true;
        info.RedirectStandardOutput = true;
        foreach (var argument in arguments.Length > 0 ? arguments : [BrowserBridge.UnpackedExtensionOrigin, "--parent-window=0"])
        {
            info.ArgumentList.Add(argument);
        }
        return Process.Start(info) ?? throw new InvalidOperationException("The native host did not start.");
    }

    /// <summary>Every form a secret could take, searched for in every file under the data folder and in every log line and frame.</summary>
    public static IEnumerable<string> FormsOf(string secret)
    {
        yield return secret;
        yield return secret.ToUpperInvariant();
        yield return secret.ToLowerInvariant();
        yield return secret.Replace("-", string.Empty, StringComparison.Ordinal);
        yield return Uri.EscapeDataString(secret);
        yield return Convert.ToBase64String(Encoding.UTF8.GetBytes(secret));
        yield return string.Concat(secret.Select(c => c + "\0"));
    }

    /// <summary>What a search has to look through, with JSON's escapes undone so an escaped address still matches.</summary>
    public static string Unescaped(string text) => JsonEscape().Replace(text, match => ((char)Convert.ToInt32(match.Groups[1].Value, 16)).ToString());

    [GeneratedRegex(@"\\u([0-9a-fA-F]{4})")]
    private static partial Regex JsonEscape();

    /// <summary>Searches every file under the data and download folders, every log line and every frame the Host sent.</summary>
    public void AssertNowhere(params string[] secrets)
    {
        var haystacks = new List<(string Where, string Text)>();
        foreach (var file in Directory.GetFiles(DataDirectory, "*", SearchOption.AllDirectories))
        {
            haystacks.Add((Path.GetRelativePath(DataDirectory, file), Unescaped(ReadShared(file))));
        }

        foreach (var file in Directory.GetFiles(Destination, "*", SearchOption.AllDirectories))
        {
            haystacks.Add(("download name " + Path.GetFileName(file), Path.GetRelativePath(Destination, file)));
        }

        var frames = Frames;
        Assert.NotEmpty(frames);
        for (var i = 0; i < frames.Count; i++)
        {
            haystacks.Add(($"frame {i}", frames[i]));
        }

        foreach (var secret in secrets)
        {
            foreach (var form in FormsOf(secret).Distinct())
            {
                foreach (var (where, text) in haystacks)
                {
                    // Not Assert.DoesNotContain: its failure message would print what it found.
                    Assert.False(text.Contains(form, StringComparison.Ordinal), $"A secret of length {secret.Length} turned up in {where}.");
                }
            }
        }
    }

    public async ValueTask DisposeAsync()
    {
        // The process first: whatever else goes wrong here, a test leaves no Host behind.
        if (_process is { } process)
        {
            Abandon(process);
            await process.WaitForExitAsync();
            process.Dispose();
            _process = null;
        }

        await CloseConnectionsAsync();

        for (var attempt = 0; attempt < 10; attempt++)
        {
            try
            {
                if (Directory.Exists(Root)) Directory.Delete(Root, recursive: true);
                return;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                await Task.Delay(100);
            }
        }
    }
}

internal sealed record CliResult(int ExitCode, string Output, string Error)
{
    public string All => Output + "\n" + Error;
}

/// <summary>One client of the pipe, as a frontend would have. Replies go to whoever asked; everything else is an event.</summary>
internal sealed class HostConnection : IAsyncDisposable
{
    private readonly NamedPipeClientStream _pipe;
    private readonly Action<string> _capture;
    private readonly ConcurrentDictionary<string, TaskCompletionSource<IpcMessage>> _pending = new();
    private readonly List<IpcMessage> _events = [];
    private readonly CancellationTokenSource _closing = new();
    private readonly SemaphoreSlim _write = new(1, 1);
    private Task? _reader;
    private int _disposed;

    private HostConnection(NamedPipeClientStream pipe, Action<string> capture)
    {
        _pipe = pipe;
        _capture = capture;
    }

    public HostHelloDto Hello { get; private set; } = null!;

    public IReadOnlyList<IpcMessage> Events
    {
        get
        {
            lock (_events) return _events.ToList();
        }
    }

    public static async Task<HostConnection> OpenAsync(string pipeName, Action<string> capture)
    {
        var pipe = new NamedPipeClientStream(".", pipeName, PipeDirection.InOut, PipeOptions.Asynchronous);
        await pipe.ConnectAsync(10_000);
        var connection = new HostConnection(pipe, capture);
        var hello = await connection.ReadFrameAsync(CancellationToken.None);
        Assert.Equal(MessageTypes.ServerHello, hello!.Type);
        Assert.Equal(1, hello.V);
        connection.Hello = hello.GetPayload<HostHelloDto>();
        var snapshot = await connection.ReadFrameAsync(CancellationToken.None);
        Assert.Equal(MessageTypes.JobsSnapshot, snapshot!.Type);
        connection._reader = connection.ReadLoopAsync();
        return connection;
    }

    private async Task<IpcMessage?> ReadFrameAsync(CancellationToken cancellationToken)
    {
        var message = await IpcFraming.ReadAsync(_pipe, cancellationToken);
        if (message is not null) _capture(SandboxHost.Unescaped(JsonSerializer.Serialize(message, IpcJson.Options)));
        return message;
    }

    private async Task ReadLoopAsync()
    {
        try
        {
            while (await ReadFrameAsync(_closing.Token) is { } message)
            {
                if (message.ReplyTo is { } id && _pending.TryRemove(id, out var waiting))
                {
                    waiting.TrySetResult(message);
                }
                else
                {
                    lock (_events) _events.Add(message);
                }
            }
        }
        catch (Exception ex) when (ex is OperationCanceledException or IOException or ObjectDisposedException)
        {
            // Closed.
        }

        foreach (var waiting in _pending.Values)
        {
            waiting.TrySetException(new IOException("The Host closed the connection."));
        }
    }

    public async Task<IpcMessage> RequestAsync<T>(string type, T payload, TimeSpan? timeout = null)
    {
        var request = IpcMessage.Create(type, payload);
        var waiting = new TaskCompletionSource<IpcMessage>(TaskCreationOptions.RunContinuationsAsynchronously);
        _pending[request.Id] = waiting;
        await _write.WaitAsync();
        try { await IpcFraming.WriteAsync(_pipe, request); }
        finally { _write.Release(); }
        return await waiting.Task.WaitAsync(timeout ?? TimeSpan.FromSeconds(45));
    }

    public async Task<TReply> AskAsync<TReply>(string type, object payload, TimeSpan? timeout = null)
        => (await RequestAsync(type, payload, timeout)).GetPayload<TReply>();

    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
        {
            return;
        }

        await _closing.CancelAsync();
        await _pipe.DisposeAsync();
        if (_reader is not null)
        {
            await _reader;
        }

        _closing.Dispose();
    }
}

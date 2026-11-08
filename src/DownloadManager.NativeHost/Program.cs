using System.Diagnostics;
using System.Text;
using System.Text.Json;
using DownloadManager.Contracts;
using DownloadManager.Core.Providers;
using DownloadManager.Persistence;

namespace DownloadManager.NativeHost;

internal static class Program
{
    private static Stream _stdin = Stream.Null;
    private static Stream _stdout = Stream.Null;
    private static readonly SemaphoreSlim StdoutGate = new(1, 1);

    private static async Task<int> Main(string[] args)
    {
        _stdin = Console.OpenStandardInput();
        _stdout = Console.OpenStandardOutput();
        var log = NativeLog.Create();
        var caller = BrowserBridge.TrustedCaller(args);
        log($"Native host started. Caller={caller ?? "(refused)"} PID={Environment.ProcessId}");

        // Refused before the Host is started or a single frame is read: only our own extension, in
        // Chrome, Edge or Firefox, may use this bridge.
        if (caller is null)
        {
            log($"Refused a caller that is not a trusted extension: {string.Join(' ', args.Select(a => a.Length > 120 ? a[..120] : a))}");
            return 2;
        }

        IpcClient? host = null;
        try
        {
            host = await EnsureHostAsync(log).ConfigureAwait(false);
            if (host is null)
            {
                await TrySendErrorAsync("host_unavailable",
                    "The Download Manager background host is not running and could not be started.", log)
                    .ConfigureAwait(false);
                return 3;
            }

            var first = await IpcFraming.ReadAsync(_stdin).ConfigureAwait(false);
            if (first is null)
            {
                log("Extension closed the port before sending a request.");
                return 0;
            }

            if (first.V != Protocol.Version)
            {
                await TrySendErrorAsync("protocol_mismatch",
                    $"The extension speaks protocol {first.V}; this host speaks {Protocol.Version}.", log)
                    .ConfigureAwait(false);
                return 4;
            }

            await ForwardToHostAsync(host, first, log).ConfigureAwait(false);

            var toHost = PumpStdinToHostAsync(host, log);
            var toExtension = PumpHostToExtensionAsync(host, log);
            await Task.WhenAny(toHost, toExtension).ConfigureAwait(false);
            log("One side of the bridge closed; exiting.");
            return 0;
        }
        catch (Exception ex)
        {
            log($"Bridge failed: {ex}");
            await TrySendErrorAsync("bridge_failure", UrlRedactor.RedactText(ex.Message), log).ConfigureAwait(false);
            return 1;
        }
        finally
        {
            if (host is not null)
            {
                await host.DisposeAsync().ConfigureAwait(false);
            }
        }
    }

    private static async Task PumpStdinToHostAsync(IpcClient host, Action<string> log)
    {
        try
        {
            while (true)
            {
                var message = await IpcFraming.ReadAsync(_stdin).ConfigureAwait(false);
                if (message is null)
                {
                    break;
                }

                await ForwardToHostAsync(host, message, log).ConfigureAwait(false);
            }
        }
        catch (Exception ex) when (ex is IOException or EndOfStreamException)
        {
            log($"Extension stdin closed: {ex.Message}");
        }
    }

    private static async Task ForwardToHostAsync(IpcClient host, IpcMessage message, Action<string> log)
    {
        if (NativeBridgePolicy.AllowFromExtension(message.Type))
        {
            await host.SendAsync(message).ConfigureAwait(false);
            log($"Forwarded '{message.Type}' {message.Id}.");
            return;
        }

        log($"Refused message type from the extension: {message.Id}.");
        await WriteToExtensionAsync(IpcMessage.Create(MessageTypes.HostActionResult,
            new HostActionResult { Ok = false, Message = "The browser extension cannot send this message." }, message.Id))
            .ConfigureAwait(false);
    }

    /// <summary>Both pumps can answer the extension, and two frames must not interleave on stdout.</summary>
    private static async Task WriteToExtensionAsync(IpcMessage message)
    {
        await StdoutGate.WaitAsync().ConfigureAwait(false);
        try { await IpcFraming.WriteAsync(_stdout, message).ConfigureAwait(false); }
        finally { StdoutGate.Release(); }
    }

    private static async Task PumpHostToExtensionAsync(IpcClient host, Action<string> log)
    {
        try
        {
            while (true)
            {
                var message = await host.ReadAsync().ConfigureAwait(false);
                if (message is null)
                {
                    break;
                }

                if (NativeBridgePolicy.ToExtension(message) is { } allowed)
                {
                    await WriteToExtensionAsync(allowed).ConfigureAwait(false);
                }
            }
        }
        catch (Exception ex) when (ex is IOException or EndOfStreamException)
        {
            log($"Host pipe closed: {ex.Message}");
        }
    }

    private static async Task<IpcClient?> EnsureHostAsync(Action<string> log)
    {
        try
        {
            var client = await IpcClient.ConnectAsync(TimeSpan.FromMilliseconds(800)).ConfigureAwait(false);
            await ConsumeGreetingAsync(client).ConfigureAwait(false);
            log("Connected to an existing host.");
            return client;
        }
        catch (Exception)
        {
            // Start the host below.
        }

        var hostExe = HostLocator.FindHostExecutable();
        if (hostExe is null)
        {
            log("Could not locate DownloadManager.Host.exe.");
            return null;
        }

        try
        {
            Process.Start(new ProcessStartInfo
            {
                FileName = hostExe,
                Arguments = "--background",
                UseShellExecute = false,
                CreateNoWindow = true,
                WindowStyle = ProcessWindowStyle.Hidden,
            });
            log($"Started host '{hostExe}'.");
        }
        catch (Exception ex)
        {
            log($"Could not start host: {ex.Message}");
            return null;
        }

        var deadline = DateTimeOffset.UtcNow + TimeSpan.FromSeconds(15);
        while (DateTimeOffset.UtcNow < deadline)
        {
            await Task.Delay(250).ConfigureAwait(false);
            try
            {
                var client = await IpcClient.ConnectAsync(TimeSpan.FromMilliseconds(800)).ConfigureAwait(false);
                await ConsumeGreetingAsync(client).ConfigureAwait(false);
                log("Connected to the newly started host.");
                return client;
            }
            catch (Exception)
            {
                // retry until the deadline
            }
        }

        log("Host did not become ready within 15 seconds.");
        return null;
    }

    private static async Task ConsumeGreetingAsync(IpcClient client)
    {
        var hello = await client.ReadAsync().ConfigureAwait(false);
        if (hello is null || hello.Type != MessageTypes.ServerHello)
        {
            throw new InvalidDataException("The host did not send a server.hello frame.");
        }

        if (hello.V != Protocol.Version)
        {
            throw new InvalidDataException($"The host speaks protocol {hello.V}.");
        }

        await client.ReadAsync().ConfigureAwait(false); // initial snapshot
    }

    private static async Task TrySendErrorAsync(string code, string message, Action<string> log)
    {
        try
        {
            await WriteToExtensionAsync(IpcMessage.Create(MessageTypes.HandoffError,
                new { errorCode = code, error = message })).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            log($"Could not send error to the extension: {ex.Message}");
        }
    }
}

internal static class HostLocator
{
    public static string? FindHostExecutable()
    {
        var candidates = new List<string>
        {
            Path.Combine(AppContext.BaseDirectory, "DownloadManager.Host.exe"),
            Path.Combine(AppContext.BaseDirectory, "..", "DownloadManager.Host.exe"),
            Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", "DownloadManager.Host", "bin", "Debug", "net10.0-windows", "DownloadManager.Host.exe"),
            Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", "DownloadManager.Host", "bin", "Release", "net10.0-windows", "DownloadManager.Host.exe"),
        };

        var fromEnv = Environment.GetEnvironmentVariable("DOWNLOADMANAGER_HOST");
        if (!string.IsNullOrWhiteSpace(fromEnv))
        {
            candidates.Insert(0, fromEnv);
        }

        return candidates.Select(Path.GetFullPath).FirstOrDefault(File.Exists);
    }
}

internal static class NativeLog
{
    public static Action<string> Create()
    {
        var paths = StoragePaths.CreateDefault();
        var directory = paths.LogsDirectory;
        try
        {
            Directory.CreateDirectory(directory);
        }
        catch (Exception)
        {
            directory = Path.Combine(AppContext.BaseDirectory, "logs");
            try
            {
                Directory.CreateDirectory(directory);
            }
            catch (Exception)
            {
                return _ => { };
            }
        }

        var path = Path.Combine(directory, $"native-host-{DateTimeOffset.Now:yyyyMMdd}.log");
        return message =>
        {
            try
            {
                File.AppendAllText(path, $"{DateTimeOffset.Now:O} {UrlRedactor.RedactText(message)}{Environment.NewLine}", new UTF8Encoding(false));
            }
            catch (Exception)
            {
                // never write logs to stdout
            }
        };
    }
}

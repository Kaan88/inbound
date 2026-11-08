using System.Net.Sockets;
using System.Text.Json;
using DownloadManager.Contracts;
using DownloadManager.Server.Auth;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Connections;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;

namespace DownloadManager.Server.Bridge;

/// <summary>
/// <c>DownloadManager.Server.exe --bridge</c> (D8): the server's web front without its engine, started and stopped by
/// the desktop Host when "Let other devices on my network control this PC's downloads" is ticked. It runs no downloads,
/// takes no lock on the data folder, and leaves secrets to Windows' own protection (it never replaces the Host's
/// <c>SecretProtector</c>). It keeps the sign-in (<c>auth.json</c>, <c>sessions.json</c>) and the uploads in the Host's
/// data folder, serves the web UI from its <c>wwwroot</c>, and carries every browser call to the Host's hello pipe in the
/// web role (<see cref="PipeBackend"/>). The Host gives it its options on standard input (<see cref="WebBridgeStartDto"/>).
/// </summary>
public sealed class BridgeApp : IAsyncDisposable
{
    private readonly WebApplication _web;
    private readonly ServerLog _log;
    private int _stopped;

    private BridgeApp(WebApplication web, ServerLog log, ServerAuth auth)
    {
        _web = web;
        _log = log;
        Auth = auth;
    }

    public ServerAuth Auth { get; }

    /// <summary>Where Kestrel really listens.</summary>
    public IReadOnlyList<Uri> Addresses =>
        _web.Services.GetService(typeof(IServer)) is IServer server && server.Features.Get<IServerAddressesFeature>() is { } feature
            ? feature.Addresses.Select(address => new Uri(address)).ToList()
            : [];

    /// <summary>The options as the bridge uses them, or a <see cref="ServerStartException"/> saying what is wrong.</summary>
    /// <param name="anyPort">Tests: port 0 (any free one) is allowed too.</param>
    internal static ServerOptions OptionsFor(WebBridgeStartDto start, bool anyPort = false)
    {
        if (string.IsNullOrWhiteSpace(start.DataDirectory) || !Path.IsPathFullyQualified(start.DataDirectory) || !Directory.Exists(start.DataDirectory))
            throw new ServerStartException("The web bridge was given no data folder it can use.");
        if (string.IsNullOrWhiteSpace(start.HelloPipe) || start.HelloPipe.Length > 200 || start.HelloPipe.Any(c => c is '\\' or '/' || char.IsControl(c)))
            throw new ServerStartException("The web bridge was given no pipe it can use.");
        if (!(anyPort && start.Port == 0) && start.Port is < WebUiDefaults.MinPort or > WebUiDefaults.MaxPort)
            throw new ServerStartException($"The port must be between {WebUiDefaults.MinPort} and {WebUiDefaults.MaxPort}.");
        byte[] key;
        try
        {
            key = Convert.FromBase64String(start.TicketKey);
        }
        catch (FormatException)
        {
            key = [];
        }

        if (key.Length < 32) throw new ServerStartException("The web bridge was given no ticket key it can use.");
        var hosts = (start.Hosts ?? []).Where(host => !string.IsNullOrWhiteSpace(host)).Select(host => host.Trim()).ToList();
        var port = start.Port.ToString(System.Globalization.CultureInfo.InvariantCulture);
        List<string> urls = start.LocalOnly ? [$"http://127.0.0.1:{port}"] : [$"http://0.0.0.0:{port}"];
        // IPv6 too where the PC has it; with port 0 each family would get a different port, so tests keep to one.
        if (Socket.OSSupportsIPv6 && start.Port != 0) urls.Add(start.LocalOnly ? $"http://[::1]:{port}" : $"http://[::]:{port}");
        return new ServerOptions
        {
            DataDirectory = Path.GetFullPath(start.DataDirectory),
            Roots = [],
            Urls = urls,
            SignInRequired = true,
            AllowedHostNames = hosts,
            HostKind = HostKinds.Windows,
            Bridge = true,
            TicketKey = key,
        };
    }

    public static async Task<BridgeApp> StartAsync(WebBridgeStartDto start, bool logToConsole = false, CancellationToken cancellationToken = default,
        bool anyPort = false, TimeProvider? time = null)
    {
        var options = OptionsFor(start, anyPort);
        if (time is not null) options = options with { Time = time };
        var log = new ServerLog(Path.Combine(options.DataDirectory, "logs"), logToConsole, "web-bridge");
        log.Info($"Web bridge starting; version {Protocol.HostVersion}, port {start.Port}, {(start.LocalOnly ? "this PC only" : "every address")}.");
        ServerAuth auth;
        try
        {
            auth = await ServerAuth.CreateAsync(options, options.DataDirectory, log, cancellationToken).ConfigureAwait(false);
        }
        catch (ServerStartException ex)
        {
            log.Error(ex.Message);
            throw;
        }

        // The Host reads the same references from the same folder (upload:<id>).
        var uploads = TorrentUploads.Prepare(options.DataDirectory);
        var web = WebFront.Build(options, new PipeBackend(start.HelloPipe, log), log, auth, uploads);
        var app = new BridgeApp(web, log, auth);
        try
        {
            await web.StartAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            await app.DisposeAsync().ConfigureAwait(false);
            if (Describe(ex, start.Port) is { } bind)
            {
                log.Error(bind.Message);
                throw bind;
            }

            throw;
        }

        log.Info($"Web bridge ready on {string.Join(", ", app.Addresses)}.");
        return app;
    }

    /// <summary>A bind failure, said plainly for Options and the tray, with the exit code the Host reads.</summary>
    internal static BridgeBindException? Describe(Exception ex, int port)
    {
        for (var inner = ex; inner is not null; inner = inner.InnerException)
        {
            if (inner is AddressInUseException || inner is SocketException { SocketErrorCode: SocketError.AddressAlreadyInUse })
                return new BridgeBindException(WebBridgeExit.PortInUse, $"Port {port} is in use by another program. Choose another port.");
            if (inner is SocketException { SocketErrorCode: SocketError.AccessDenied })
                return new BridgeBindException(WebBridgeExit.PortForbidden, $"Windows doesn't allow listening on port {port}. Choose another port.");
        }

        return null;
    }

    public Task WaitForShutdownAsync(CancellationToken cancellationToken = default) => _web.WaitForShutdownAsync(cancellationToken);

    public async Task StopAsync()
    {
        if (Interlocked.Exchange(ref _stopped, 1) != 0) return;
        _log.Info("Web bridge stopping.");
        try
        {
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
            await _web.StopAsync(timeout.Token).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            _log.Warn($"Web shutdown: {ex.GetType().Name}");
        }

        try
        {
            Auth.Sessions.Flush();
        }
        catch (Exception ex)
        {
            _log.Warn($"Sessions flush: {ex.GetType().Name}");
        }

        _log.Info("Web bridge stopped.");
    }

    public async ValueTask DisposeAsync()
    {
        await StopAsync().ConfigureAwait(false);
        await _web.DisposeAsync().ConfigureAwait(false);
    }

    /// <summary>
    /// The process: options from the first line of standard input, <see cref="WebBridgeExit.ReadyLine"/> on standard
    /// output once it listens, the reason on standard error when it can't. It stops when standard input closes.
    /// </summary>
    public static async Task<int> RunAsync(TextReader input, TextWriter output, TextWriter error)
    {
        WebBridgeStartDto? start;
        try
        {
            var line = await input.ReadLineAsync().ConfigureAwait(false);
            start = line is null ? null : JsonSerializer.Deserialize<WebBridgeStartDto>(line, IpcJson.Options);
        }
        catch (JsonException)
        {
            start = null;
        }

        if (start is null)
        {
            await error.WriteLineAsync("The web bridge is started by the app; it reads its options on standard input.").ConfigureAwait(false);
            return WebBridgeExit.Refused;
        }

        BridgeApp app;
        try
        {
            app = await StartAsync(start).ConfigureAwait(false);
        }
        catch (BridgeBindException ex)
        {
            await error.WriteLineAsync(ex.Message).ConfigureAwait(false);
            return ex.ExitCode;
        }
        catch (ServerStartException ex)
        {
            await error.WriteLineAsync(ex.Message).ConfigureAwait(false);
            return WebBridgeExit.Refused;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            await error.WriteLineAsync($"The web UI could not start: {ex.Message}").ConfigureAwait(false);
            return WebBridgeExit.Crashed;
        }

        await using (app)
        {
            await output.WriteLineAsync(WebBridgeExit.ReadyLine).ConfigureAwait(false);
            await output.FlushAsync().ConfigureAwait(false);
            // Standard input closing is the Host saying stop, or the Host gone: a bridge never outlives it.
            var stdinClosed = Task.Run(async () =>
            {
                while (await input.ReadLineAsync().ConfigureAwait(false) is not null)
                {
                    // Nothing else is sent; a line is ignored.
                }
            });
            await Task.WhenAny(stdinClosed, app.WaitForShutdownAsync()).ConfigureAwait(false);
        }

        return WebBridgeExit.Stopped;
    }
}

/// <summary>The bridge could not listen on its port; <see cref="ExitCode"/> says why (<see cref="WebBridgeExit"/>).</summary>
public sealed class BridgeBindException(int exitCode, string message) : Exception(message)
{
    public int ExitCode { get; } = exitCode;
}

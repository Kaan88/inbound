using System.Net.WebSockets;
using System.Text.Json;
using DownloadManager.Contracts;
using DownloadManager.Core;
using DownloadManager.Host;
using DownloadManager.Persistence;
using DownloadManager.Runtime;
using DownloadManager.Runtime.Archives;
using DownloadManager.Runtime.Ipc;
using DownloadManager.Runtime.Remote;
using DownloadManager.Server.Auth;
using Microsoft.AspNetCore.HttpOverrides;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.FileProviders;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace DownloadManager.Server;

/// <summary>
/// The headless server: what the desktop Host runs (Runtime, Engine, Persistence, Archives, Containers and the TorBox
/// provider) behind HTTP instead of a named pipe, without the Windows parts (tray, sounds, clipboard, sleep prevention,
/// shutdown actions, start at sign-in, native-host registration, updater).
/// <list type="bullet">
/// <item><c>GET /api/ws</c>: WebSocket, one envelope per text message; hello and snapshot, then events. With
/// <c>?hello=1</c> the client's first message is <c>client.hello</c>, as on the desktop's hello pipe.</item>
/// <item><c>POST /api/ipc</c>: one request envelope, one reply.</item>
/// <item><c>GET /health</c>: liveness, no sign-in.</item>
/// <item>Everything else: the static web UI from <c>wwwroot</c>.</item>
/// </list>
/// Sign-in (<see cref="ServerAuth"/>, D2) guards everything but <c>/health</c> and the sign-in page; with sign-in off,
/// <see cref="ServerOptions.DescribeUnsafeBinding"/> keeps it on loopback unless that is explicitly allowed. The
/// <see cref="OriginCheck"/> keeps other web pages from driving it through a browser either way.
/// </summary>
public sealed class ServerApp : IAsyncDisposable
{
    private readonly WebApplication _web;
    private readonly ServerLog _log;
    private readonly DownloadManagerService _service;
    private readonly HostCommandDispatcher _dispatcher;
    private readonly ArchiveCoordinator _archives;
    private readonly RemoteJobCoordinator _remoteJobs;
    private readonly RemoteCommands _remoteCommands;
    private readonly RemoteProviderSetup _remote;
    private readonly HttpDownloadEngine _engine;
    private readonly DataFolderLock _dataLock;
    private int _stopped;

    private ServerApp(WebApplication web, ServerLog log, DownloadManagerService service, HostCommandDispatcher dispatcher,
        ArchiveCoordinator archives, RemoteJobCoordinator remoteJobs, RemoteCommands remoteCommands, RemoteProviderSetup remote,
        HttpDownloadEngine engine, StoragePaths paths, DataFolderLock dataLock)
    {
        _dataLock = dataLock;
        _web = web;
        _log = log;
        _service = service;
        _dispatcher = dispatcher;
        _archives = archives;
        _remoteJobs = remoteJobs;
        _remoteCommands = remoteCommands;
        _remote = remote;
        _engine = engine;
        Paths = paths;
    }

    public StoragePaths Paths { get; }

    public DownloadManagerService Service => _service;

    /// <summary>Sign-in, sessions and paired browsers (D2).</summary>
    public ServerAuth Auth { get; private init; } = null!;

    /// <summary>Where Kestrel really listens, with the port it chose for <c>:0</c>.</summary>
    public IReadOnlyList<Uri> Addresses =>
        _web.Services.GetService(typeof(IServer)) is IServer server && server.Features.Get<IServerAddressesFeature>() is { } feature
            ? feature.Addresses.Select(address => new Uri(address)).ToList()
            : [];

    /// <summary>Composes everything and starts listening. Throws <see cref="ServerStartException"/> for a configuration it refuses.</summary>
    public static async Task<ServerApp> StartAsync(ServerOptions options, bool logToConsole = true, CancellationToken cancellationToken = default)
    {
        if (options.DescribeUnsafeBinding() is { } refusal) throw new ServerStartException(refusal);
        if (options.TrustProxyProblem is { } proxyProblem) throw new ServerStartException(proxyProblem);

        var paths = new StoragePaths(Path.GetFullPath(options.DataDirectory));
        paths.EnsureCreated();
        // Before anything reads or writes the folder: one writer, as the desktop Host's mutex gives it.
        var dataLock = DataFolderLock.Acquire(paths.Root);
        try
        {
            return await StartLockedAsync(options, paths, dataLock, logToConsole, cancellationToken).ConfigureAwait(false);
        }
        catch
        {
            dataLock.Dispose();
            throw;
        }
    }

    private static async Task<ServerApp> StartLockedAsync(ServerOptions options, StoragePaths paths, DataFolderLock dataLock, bool logToConsole,
        CancellationToken cancellationToken)
    {
        var log = new ServerLog(paths.LogsDirectory, logToConsole);
        log.Info($"Server starting; version {Protocol.HostVersion}, state '{paths.Root}', roots {string.Join(", ", options.Roots)}.");
        if (!options.SignInRequired && options.AllowUnauthenticated)
            log.Warn("No sign-in: anyone who can reach this server controls it. Keep it off networks you don't trust.");

        // Before any store opens: everything secret the stores read or write is sealed with this key. A key that can't
        // open what the folder already holds stops the start, because the stores would drop those secrets on their next save.
        var keyFile = Path.Combine(paths.Root, AesGcmSecretProtector.DefaultFileName);
        try
        {
            SecretProtector.Use(AesGcmSecretProtector.OpenForDataFolder(keyFile, paths.Root, options.DiscardUnreadableSecrets, log.Warn));
        }
        catch (SecretKeyMismatchException ex)
        {
            log.Error(ex.Message);
            throw new ServerStartException(ex.Message);
        }

        await SeedFirstStartAsync(paths, options, cancellationToken).ConfigureAwait(false);
        // Clients here are not the machine's user: what they may have the server read or write stays in these folders.
        var roots = new PathRoots(options.Roots);

        var jobStore = new JsonJobStore(paths, log.Info);
        var checkpoints = new JsonCheckpointStore(paths);
        var mediaCheckpoints = new JsonMediaCheckpointStore(paths);
        var settingsStore = new JsonSettingsStore(paths);
        var engine = new HttpDownloadEngine(
            new EngineOptions
            {
                MaxAttempts = 5,
                InitialRetryDelay = TimeSpan.FromSeconds(2),
                MaxRetryDelay = TimeSpan.FromSeconds(60),
                ConnectTimeout = TimeSpan.FromSeconds(20),
                ReadTimeout = TimeSpan.FromSeconds(90),
                AllowLocalNetwork = true,
            },
            checkpoints: checkpoints,
            mediaCheckpoints: mediaCheckpoints);
        var remote = RemoteProviderSetup.Create(paths, engine, log.Info);
        log.Secrets = remote.Registry;

        // No completion actions, sleep prevention or post-download command: those start programs or act on the machine,
        // which a server's clients may not ask for (PlatformCapabilities.SupportsProgramSettings is off).
        var service = new DownloadManagerService(
            paths, jobStore, checkpoints, settingsStore, engine, log.Info,
            mediaCheckpoints: mediaCheckpoints,
            ftpEngine: new DownloadManager.Engine.FtpDownloadEngine(engine.SpeedBudget),
            linkBroker: remote.Broker,
            linkSecrets: remote.Registry)
        {
            Roots = roots,
        };
        foreach (var provider in remote.Registry.All)
        {
            // Addresses saved earlier may turn out to hold a key that was only just entered.
            if (provider.Account is { } account)
                account.Changed += (_, _) => _ = Observe(service.ProtectStoredSecretsAsync(), log, "Protecting saved addresses");
        }

        var archives = new ArchiveCoordinator(service, new JsonArchiveStore(paths, log.Warn), log.Info);
        var remoteJobs = remote.CreateCoordinator(paths, service, archives, log.Info);
        // A .torrent file is read from the roots, or from the folder the web UI's uploads go to (and nowhere else).
        var uploads = TorrentUploads.Prepare(paths.Root);
        remoteJobs.Roots = new PathRoots([.. options.Roots, uploads.Directory]);
        remoteJobs.DestinationRoots = roots;
        remoteJobs.ResolveReference = uploads.Resolve;
        var remoteCommands = new RemoteCommands(remote.Registry, remoteJobs, log: log.Info, roots: roots);
        service.DirectAddresses = remoteJobs;
        await service.InitializeAsync(cancellationToken).ConfigureAwait(false);

        var archivesStarted = false;
        try
        {
            await archives.InitializeAsync(cancellationToken).ConfigureAwait(false);
            archivesStarted = true;
        }
        catch (Exception ex)
        {
            log.Error($"Archive extraction could not start: {ex.GetType().Name}");
            await archives.ShutdownAsync(CancellationToken.None).ConfigureAwait(false);
        }

        var remoteStarted = false;
        try
        {
            await remoteJobs.StartAsync().ConfigureAwait(false);
            remoteStarted = true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            log.Warn($"Remote jobs could not start: {ex.GetType().Name}");
        }

        service.TrayAvailable = false;

        var dispatcher = new HostCommandDispatcher(service, new ServerPlatform(), log, remoteStarted ? remoteCommands : null)
        {
            Archives = archivesStarted ? new ArchiveIpcDispatcher(archives, roots) : null,
            Folders = new FolderBrowser(roots),
        };
        archives.Changed += (_, change) => dispatcher.BroadcastArchiveChanged(change);
        service.SettingsChanged += (_, _) => log.Info("Settings updated.");
        dispatcher.Start();

        var auth = await ServerAuth.CreateAsync(options, paths.Root, log, cancellationToken).ConfigureAwait(false);
        var web = BuildWeb(options, dispatcher, log, auth, uploads);
        var app = new ServerApp(web, log, service, dispatcher, archives, remoteJobs, remoteCommands, remote, engine, paths, dataLock) { Auth = auth };
        try
        {
            await web.StartAsync(cancellationToken).ConfigureAwait(false);
            // Kestrel's endpoint configuration is turned off in BuildWeb; this is the check that doesn't depend on that,
            // made on what it really listens on. A refusal stops it at once.
            if (options.DescribeUnsafeBinding(app.Addresses.Select(address => address.OriginalString)) is { } refusal)
            {
                log.Error(refusal);
                throw new ServerStartException(refusal);
            }
        }
        catch
        {
            await app.DisposeAsync().ConfigureAwait(false);
            throw;
        }

        log.Info($"Server ready on {string.Join(", ", app.Addresses)}.");
        return app;
    }

    /// <summary>Waits until the process is asked to stop (Ctrl+C, SIGTERM from <c>docker stop</c>).</summary>
    public Task WaitForShutdownAsync(CancellationToken cancellationToken = default) => _web.WaitForShutdownAsync(cancellationToken);

    /// <summary>A fresh data folder starts with downloads in the first root and nothing that wants a desktop.</summary>
    private static async Task SeedFirstStartAsync(StoragePaths paths, ServerOptions options, CancellationToken cancellationToken)
    {
        if (File.Exists(paths.SettingsFile)) return;
        var store = new JsonSettingsStore(paths);
        var settings = await store.LoadAsync(cancellationToken).ConfigureAwait(false);
        settings.DefaultDownloadDirectory = Path.GetFullPath(options.DefaultDownloadDirectory);
        settings.NotifyOnCompletion = false;
        settings.PlayCompletionSound = false;
        settings.ClipboardMonitoring = false;
        settings.LaunchAtStartup = false;
        await store.SaveAsync(settings, cancellationToken).ConfigureAwait(false);
    }

    private static WebApplication BuildWeb(ServerOptions options, HostCommandDispatcher dispatcher, ServerLog log, ServerAuth auth, TorrentUploads uploads)
    {
        var builder = WebApplication.CreateSlimBuilder(new WebApplicationOptions
        {
            ContentRootPath = AppContext.BaseDirectory,
            WebRootPath = Path.Combine(AppContext.BaseDirectory, "wwwroot"),
        });
        builder.WebHost.UseUrls([.. options.Urls]);
        builder.WebHost.ConfigureKestrel(kestrel =>
        {
            kestrel.Limits.MaxRequestBodySize = Protocol.MaxFrameBytes;
            // The builder reads a "Kestrel" section from every configuration source, unprefixed environment variables
            // included, and Kestrel:Endpoints would replace the addresses above: Kestrel__Endpoints__X__Url=http://0.0.0.0:80
            // (a common compose line) would open the port without the loopback rule seeing it. Only the server's own
            // variables say where it listens.
            kestrel.Configure(new ConfigurationBuilder().Build(), reloadOnChange: false);
        });
        // The server's own log carries what matters; ASP.NET's request logging would repeat addresses it can't redact.
        builder.Logging.ClearProviders();
        builder.Logging.AddSimpleConsole().SetMinimumLevel(LogLevel.Warning);

        var web = builder.Build();
        // The address the connection really came from, before a proxy's header replaces it: the setup code's
        // "this machine" rule goes by it (ServerAuth.NeedsSetupCode).
        web.Use((context, next) =>
        {
            context.Items[ServerAuth.PeerItem] = context.Connection.RemoteIpAddress;
            return next();
        });
        if (options.TrustProxy)
        {
            // Only from the listed proxies: the client's address (for the sign-in waits) and whether it came over HTTPS
            // (for the cookie's Secure flag and the origin check). The nearest hop only. A client that reaches the port
            // directly is not listed, so what it puts in these headers is ignored.
            var forwarded = new ForwardedHeadersOptions
            {
                ForwardedHeaders = ForwardedHeaders.XForwardedFor | ForwardedHeaders.XForwardedProto,
                ForwardLimit = 1,
            };
            forwarded.KnownIPNetworks.Clear();
            forwarded.KnownProxies.Clear();
            foreach (var network in options.TrustedProxies) forwarded.KnownIPNetworks.Add(network);
            web.UseForwardedHeaders(forwarded);
        }

        web.Use(SecurityHeaders.Middleware);
        web.UseWebSockets(new WebSocketOptions { KeepAliveInterval = TimeSpan.FromSeconds(30) });
        web.Use(ExtensionCors.Middleware);
        web.Use(OriginCheck.Middleware(options.AllowedHostNames, auth.HasValidToken));
        web.Use(auth.Middleware);

        var stopping = web.Lifetime.ApplicationStopping;
        web.MapGet("/health", () => Results.Json(new { status = "ok", version = Protocol.HostVersion }));
        auth.Map(web);
        uploads.Map(web);

        web.Map(ServerAuth.WebSocketPath, async context =>
        {
            if (!context.WebSockets.IsWebSocketRequest)
            {
                context.Response.StatusCode = StatusCodes.Status400BadRequest;
                await context.Response.WriteAsync("A WebSocket upgrade is expected here.").ConfigureAwait(false);
                return;
            }

            // Signing out, a changed password or the session's absolute end closes the socket too. A desktop app's
            // token (the gate let no other role through) closes it when the token is revoked, by a password change too.
            var session = ServerAuth.SessionOf(context);
            var paired = ServerAuth.TokenOf(context);
            var tokenEnded = paired is not null ? auth.Pairing.Lifetime(paired.Id) : CancellationToken.None;
            if (tokenEnded.IsCancellationRequested)
            {
                context.Response.StatusCode = StatusCodes.Status401Unauthorized;
                await context.Response.WriteAsJsonAsync(new { ok = false, error = "This app's pairing was removed. Pair it again." }).ConfigureAwait(false);
                return;
            }

            // PERF.7 on the WebSocket: a client that asks with ?hello=1 says first what it wants (no snapshot, no events).
            var clientSpeaksFirst = context.Request.Query["hello"] == "1";
            using var socket = await context.WebSockets.AcceptWebSocketAsync().ConfigureAwait(false);
            using var lifetime = session is null
                ? CancellationTokenSource.CreateLinkedTokenSource(context.RequestAborted, stopping, tokenEnded)
                : CancellationTokenSource.CreateLinkedTokenSource(context.RequestAborted, stopping, session.Ended);
            if (session is not null)
            {
                var left = auth.Sessions.AbsoluteEnd(session) - options.Time.GetUtcNow();
                lifetime.CancelAfter(left > TimeSpan.Zero ? left : TimeSpan.Zero);
                session.SocketOpened();
            }

            // An open socket is the session's activity even when the page sends nothing (it only listens to progress), so
            // its last-seen time is refreshed on the sessions file's own beat; a kill then loses at most that much. A
            // desktop app's token is refreshed on the same beat, so one that stays connected for weeks neither shows a
            // stale last use nor looks like the oldest unused token when too many push one out.
            var seen = session is not null ? KeepSeenAsync(auth.Sessions.SaveEvery, () => auth.Sessions.Touch(session), options.Time, lifetime.Token)
                : paired is not null ? KeepSeenAsync(auth.Sessions.SaveEvery, () => auth.Pairing.Touch(paired.Id), options.Time, lifetime.Token)
                : Task.CompletedTask;
            try
            {
                await WebSocketTransport.RunAsync(socket, dispatcher, log, lifetime.Token,
                    session is null ? null : () => auth.Sessions.Touch(session), clientSpeaksFirst).ConfigureAwait(false);
            }
            finally
            {
                lifetime.Cancel();
                await seen.ConfigureAwait(false);
                if (session is not null)
                {
                    session.SocketClosed();
                    auth.Sessions.Touch(session);
                }
            }
        });

        web.MapPost("/api/ipc", async context =>
        {
            IpcMessage? request;
            try
            {
                request = await JsonSerializer.DeserializeAsync<IpcMessage>(context.Request.Body, IpcJson.Options, context.RequestAborted).ConfigureAwait(false);
            }
            catch (JsonException)
            {
                request = null;
            }

            if (request is null || string.IsNullOrWhiteSpace(request.Type) || string.IsNullOrWhiteSpace(request.Id))
            {
                context.Response.StatusCode = StatusCodes.Status400BadRequest;
                await context.Response.WriteAsJsonAsync(new { error = "One IPC envelope with v, type and id is expected." }).ConfigureAwait(false);
                return;
            }

            // A paired extension's token opens its role only, both ways (NativeBridgePolicy.ServerExtension). A desktop
            // app's token has a signed-in browser's rights, as on /api/ws.
            var paired = ServerAuth.TokenOf(context) is { } token && token.Role == TokenRoles.Extension;
            if (paired && !NativeBridgePolicy.AllowFrom(BridgeRole.ServerExtension, request))
            {
                context.Response.StatusCode = StatusCodes.Status403Forbidden;
                await WriteEnvelopeAsync(context, IpcMessage.Create(MessageTypes.HostActionResult,
                    new HostActionResult { Ok = false, Message = $"A paired browser can't send '{request.Type}'." }, request.Id), stopping).ConfigureAwait(false);
                return;
            }

            // A token's request ends with the token, as its WebSockets do: a command still running when the owner removes
            // the pairing (or changes the password) is cancelled, not finished for an app that no longer has access.
            var tokenEnded = ServerAuth.TokenOf(context) is { } holder ? auth.Pairing.Lifetime(holder.Id) : CancellationToken.None;
            var session = new OneShotSession(request.Id);
            using var lifetime = CancellationTokenSource.CreateLinkedTokenSource(context.RequestAborted, stopping, tokenEnded);
            IpcMessage? reply = null;
            try
            {
                if (!tokenEnded.IsCancellationRequested && !dispatcher.RejectIfIncompatible(session, request))
                    await dispatcher.DispatchAsync(session, request, lifetime.Token).ConfigureAwait(false);
                if (!tokenEnded.IsCancellationRequested)
                    reply = await session.Reply.WaitAsync(TimeSpan.FromMinutes(5), lifetime.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (tokenEnded.IsCancellationRequested)
            {
                // The pairing was removed meanwhile (answered below).
            }

            if (reply is null || tokenEnded.IsCancellationRequested)
            {
                // Also when the cancelled command still answered (with its failure): the answer is the removed pairing.
                context.Response.StatusCode = StatusCodes.Status401Unauthorized;
                await context.Response.WriteAsJsonAsync(new { ok = false, error = "This app's pairing was removed. Pair it again." }, stopping).ConfigureAwait(false);
                return;
            }

            if (paired)
            {
                reply = NativeBridgePolicy.To(BridgeRole.ServerExtension, reply) ??
                        IpcMessage.Create(MessageTypes.HostActionResult,
                            new HostActionResult { Ok = false, Message = "The answer isn't one a paired browser may see." }, request.Id);
            }

            await WriteEnvelopeAsync(context, reply, lifetime.Token).ConfigureAwait(false);
        });

        if (Directory.Exists(builder.Environment.WebRootPath))
        {
            web.UseDefaultFiles();
            web.UseStaticFiles(new StaticFileOptions { FileProvider = new PhysicalFileProvider(builder.Environment.WebRootPath) });
        }

        return web;
    }

    /// <summary>
    /// Touches the session or the token every <paramref name="every"/> (<see cref="SessionStore.SaveEvery"/>) while its
    /// socket is open (one wake-up per beat).
    /// </summary>
    private static async Task KeepSeenAsync(TimeSpan every, Action touch, TimeProvider time, CancellationToken cancellationToken)
    {
        using var timer = new PeriodicTimer(every, time);
        try
        {
            while (await timer.WaitForNextTickAsync(cancellationToken).ConfigureAwait(false)) touch();
        }
        catch (OperationCanceledException)
        {
            // The socket closed.
        }
    }

    private static async Task WriteEnvelopeAsync(HttpContext context, IpcMessage message, CancellationToken cancellationToken)
    {
        context.Response.ContentType = "application/json";
        await JsonSerializer.SerializeAsync(context.Response.Body, message, IpcJson.Options, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>Stops listening first, so no client sees a half-stopped service, then the rest in the Host's order.</summary>
    public async Task StopAsync()
    {
        if (Interlocked.Exchange(ref _stopped, 1) != 0) return;
        _log.Info("Server stopping.");
        _dispatcher.Stop();
        try
        {
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
            await _web.StopAsync(timeout.Token).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            _log.Warn($"Web shutdown: {ex.GetType().Name}");
        }

        await Step(() => _archives.ShutdownAsync(CancellationToken.None), "Archive shutdown").ConfigureAwait(false);
        await Step(() => _remoteJobs.StopAsync(), "Remote jobs shutdown").ConfigureAwait(false);
        await Step(() => _service.ShutdownAsync(CancellationToken.None), "Service shutdown").ConfigureAwait(false);
        // Last-seen times not yet written (they are saved at most every few minutes while the server runs).
        try
        {
            Auth?.Sessions.Flush();
        }
        catch (Exception ex)
        {
            _log.Warn($"Sessions flush: {ex.GetType().Name}");
        }

        _remoteCommands.Dispose();
        _remote.Dispose();
        _engine.Dispose();
        _log.Info("Server stopped.");
    }

    public async ValueTask DisposeAsync()
    {
        await StopAsync().ConfigureAwait(false);
        await _web.DisposeAsync().ConfigureAwait(false);
        // Last: until everything above has stopped writing, the folder is still this process's.
        _dataLock.Dispose();
    }

    private async Task Step(Func<Task> step, string name)
    {
        try
        {
            await step().ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            _log.Error($"{name}: {ex.GetType().Name}");
        }
    }

    private static async Task Observe(Task task, ServerLog log, string what)
    {
        try
        {
            await task.ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            log.Error($"{what} failed: {ex.GetType().Name}");
        }
    }
}

/// <summary>A configuration the server refuses to start with. The message says what to change.</summary>
public sealed class ServerStartException(string message) : Exception(message);

/// <summary>
/// Refuses a request to <c>/api/</c> that a web page on another site made through the user's browser: a WebSocket
/// upgrade or a POST whose <c>Origin</c> isn't this server's own. A request without an <c>Origin</c> (curl, a test)
/// passes that part, because a browser always sends one for these. With sign-in off this is what keeps a page the
/// user happens to open from driving the server on their network; with it on, it is a second wall behind the
/// <c>SameSite=Strict</c> cookie.
/// A page can also make its own name point at this server (DNS rebinding), and then its origin and the Host header
/// agree; its same-origin GETs carry no <c>Origin</c> at all. So every request to <c>/api/</c>, with an
/// <c>Origin</c> or without, must address the server by an IP address, by <c>localhost</c>, or by a name listed in
/// <c>DOWNLOADMANAGER_HOSTS</c> (separated by <c>;</c> or <c>,</c>): a rebinding page can only use a name of its own.
/// The browser extension is the one other origin let through, and only where it has its own credential
/// (<see cref="ExtensionCors.Admits"/>): <c>POST /api/ipc</c> and <c>DELETE /api/pair/self</c> with a bearer token, and
/// <c>/api/pair</c> with a code.
/// <para>
/// An app that isn't a browser (the desktop app managing a server, design D9) may use any name the user typed,
/// such as <c>http://homeserver:8080</c>, when it sends no <c>Origin</c> and either a valid token or a pairing request
/// (<see cref="CarriesOwnCredential"/>). A rebinding page gains nothing from that: the token paths are a POST, a
/// DELETE and a WebSocket, and a browser always sends <c>Origin</c> for those, its own page's included; a page has no
/// valid token to send anyway; and <c>POST /api/pair</c> without a code or password opens nothing (it is also a POST,
/// so a page sends <c>Origin</c> there too). Any request with an <c>Origin</c> still meets both rules above.
/// </para>
/// </summary>
public static class OriginCheck
{
    public const string HostsVariable = "DOWNLOADMANAGER_HOSTS";

    /// <param name="hasValidToken">Whether the request carries a paired app's valid token on a token path (<see cref="ServerAuth.HasValidToken"/>).</param>
    public static Func<HttpContext, Func<Task>, Task> Middleware(IReadOnlyCollection<string> allowedNames, Func<HttpContext, bool> hasValidToken) => async (context, next) =>
    {
        if (context.Request.Path.StartsWithSegments("/api") && !ExtensionCors.Admits(context.Request) &&
            !CarriesOwnCredential(context, hasValidToken) &&
            Refusal(context.Request, allowedNames) is { } refusal)
        {
            context.Response.StatusCode = StatusCodes.Status403Forbidden;
            await context.Response.WriteAsync(refusal).ConfigureAwait(false);
            return;
        }

        await next().ConfigureAwait(false);
    };

    /// <summary>
    /// No <c>Origin</c> (so not a browser's page, see above) and a credential of its own: a valid token, or
    /// <c>POST /api/pair</c>, where the code or password is the credential and wrong ones make the address wait.
    /// </summary>
    internal static bool CarriesOwnCredential(HttpContext context, Func<HttpContext, bool> hasValidToken)
    {
        var request = context.Request;
        if (!string.IsNullOrEmpty(request.Headers.Origin.ToString())) return false;
        if (HttpMethods.IsPost(request.Method) && request.Path.Equals("/api/pair", StringComparison.OrdinalIgnoreCase)) return true;
        return hasValidToken(context);
    }

    public static List<string> ParseHosts(string? value) =>
        (value ?? string.Empty).Split([';', ','], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).ToList();

    /// <summary>Why the request is refused, or null.</summary>
    public static string? Refusal(HttpRequest request, IReadOnlyCollection<string> allowedNames)
    {
        var origin = request.Headers.Origin.ToString();
        var hasOrigin = !string.IsNullOrEmpty(origin);
        // Without a Host (HTTP/1.0) it isn't a browser, and there is no name to check.
        if (!hasOrigin && !request.Host.HasValue) return null;
        var own = $"{request.Scheme}://{request.Host.Value}";
        if (!Uri.TryCreate(own, UriKind.Absolute, out var self)) return "Requests from another site are refused.";
        if (hasOrigin &&
            (!Uri.TryCreate(origin, UriKind.Absolute, out var from) ||
             !string.Equals(from.Scheme, self.Scheme, StringComparison.OrdinalIgnoreCase) ||
             !string.Equals(from.Host, self.Host, StringComparison.OrdinalIgnoreCase) ||
             from.Port != self.Port))
            return "Requests from another site are refused.";

        var name = self.IdnHost;
        if (self.HostNameType is UriHostNameType.IPv4 or UriHostNameType.IPv6 ||
            string.Equals(name, "localhost", StringComparison.OrdinalIgnoreCase) ||
            allowedNames.Contains(name, StringComparer.OrdinalIgnoreCase))
            return null;
        return $"This server isn't known by the name {name}. Use its IP address, or add the name to {HostsVariable}.";
    }
}

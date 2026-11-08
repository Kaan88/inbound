using System.Net;
using System.Net.Sockets;
using System.Net.WebSockets;
using System.Text.Json;
using DownloadManager.Contracts;
using DownloadManager.Server.Auth;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.HttpOverrides;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.FileProviders;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace DownloadManager.Server;

/// <summary>
/// Where the envelopes of <c>/api/ws</c> and <c>/api/ipc</c> go: the server's own dispatcher
/// (<see cref="DispatcherBackend"/>), or the desktop Host's hello pipe for the web bridge (<see cref="Bridge.PipeBackend"/>).
/// Sign-in, origin checks, uploads and the static web UI are the same either way (<see cref="WebFront"/>).
/// </summary>
internal interface IEnvelopeBackend
{
    /// <summary>Runs one WebSocket until it closes, the backend ends it, or <paramref name="cancellationToken"/> ends.</summary>
    /// <param name="onMessage">Called for each message the client sends (the session's activity).</param>
    /// <param name="clientSpeaksFirst">The client sends <c>client.hello</c> before anything is sent to it (<c>?hello=1</c>).</param>
    Task RunSocketAsync(WebSocket socket, Action? onMessage, bool clientSpeaksFirst, CancellationToken cancellationToken);

    /// <summary>One request envelope and its one reply.</summary>
    /// <exception cref="BackendUnavailableException">There is nothing to answer it (the Host isn't there).</exception>
    Task<IpcMessage?> SendOneAsync(IpcMessage request, CancellationToken cancellationToken);
}

/// <summary>The backend can't answer at all now; <c>/api/ipc</c> answers 503 with the message.</summary>
internal sealed class BackendUnavailableException(string message) : Exception(message);

/// <summary>The headless server: its own <see cref="Runtime.Ipc.HostCommandDispatcher"/> answers.</summary>
internal sealed class DispatcherBackend(Runtime.Ipc.HostCommandDispatcher dispatcher, ServerLog log) : IEnvelopeBackend
{
    public Task RunSocketAsync(WebSocket socket, Action? onMessage, bool clientSpeaksFirst, CancellationToken cancellationToken) =>
        WebSocketTransport.RunAsync(socket, dispatcher, log, cancellationToken, onMessage, clientSpeaksFirst);

    public async Task<IpcMessage?> SendOneAsync(IpcMessage request, CancellationToken cancellationToken)
    {
        var session = new OneShotSession(request.Id);
        if (!dispatcher.RejectIfIncompatible(session, request))
            await dispatcher.DispatchAsync(session, request, cancellationToken).ConfigureAwait(false);
        return await session.Reply.WaitAsync(TimeSpan.FromMinutes(5), cancellationToken).ConfigureAwait(false);
    }
}

/// <summary>
/// The HTTP side shared by the server and the web bridge: Kestrel on the configured addresses only, the guards, sign-in,
/// <c>/api/ws</c>, <c>/api/ipc</c>, uploads and the static web UI from <c>wwwroot</c> next to the program.
/// </summary>
internal static class WebFront
{
    public static WebApplication Build(ServerOptions options, IEnvelopeBackend backend, ServerLog log, ServerAuth auth, TorrentUploads uploads)
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
        if (options.Bridge)
        {
            // The web bridge on Windows: no other program (another Windows account's included) may bind the same port on
            // a more specific address and answer in its place. IPv6 sockets don't take IPv4 too: each family is bound as listed.
            builder.WebHost.UseSockets(sockets => sockets.CreateBoundListenSocket = BindExclusive);
        }

        // The server's own log carries what matters; ASP.NET's request logging would repeat addresses it can't redact.
        builder.Logging.ClearProviders();
        if (!options.Bridge) builder.Logging.AddSimpleConsole().SetMinimumLevel(LogLevel.Warning);

        var web = builder.Build();
        // The address the connection really came from, before a proxy's header replaces it: the setup code's
        // "this machine" rule goes by it (ServerAuth.NeedsSetupCode), and so does the bridge's ticket sign-in.
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
                await backend.RunSocketAsync(socket, session is null ? null : () => auth.Sessions.Touch(session), clientSpeaksFirst, lifetime.Token)
                    .ConfigureAwait(false);
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
            using var lifetime = CancellationTokenSource.CreateLinkedTokenSource(context.RequestAborted, stopping, tokenEnded);
            IpcMessage? reply = null;
            try
            {
                if (!tokenEnded.IsCancellationRequested)
                    reply = await backend.SendOneAsync(request, lifetime.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (tokenEnded.IsCancellationRequested)
            {
                // The pairing was removed meanwhile (answered below).
            }
            catch (BackendUnavailableException ex)
            {
                context.Response.StatusCode = StatusCodes.Status503ServiceUnavailable;
                await context.Response.WriteAsJsonAsync(new { ok = false, error = ex.Message }, stopping).ConfigureAwait(false);
                return;
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
    /// A listening socket no other socket can share (<c>SO_EXCLUSIVEADDRUSE</c> on Windows). Without it a program of
    /// another Windows account could bind 127.0.0.1 on the same port after the bridge took every address, and take the
    /// connections meant for it, the "Open in browser" ticket included.
    /// </summary>
    internal static Socket BindExclusive(EndPoint endpoint)
    {
        var socket = endpoint is IPEndPoint ip
            ? new Socket(ip.AddressFamily, SocketType.Stream, ProtocolType.Tcp)
            : new Socket(endpoint.AddressFamily, SocketType.Stream, ProtocolType.Unspecified);
        try
        {
            if (OperatingSystem.IsWindows()) socket.ExclusiveAddressUse = true;
            if (endpoint is IPEndPoint { AddressFamily: AddressFamily.InterNetworkV6 }) socket.DualMode = false;
            socket.Bind(endpoint);
            return socket;
        }
        catch
        {
            socket.Dispose();
            throw;
        }
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
}

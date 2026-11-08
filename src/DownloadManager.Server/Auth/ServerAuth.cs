using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using DownloadManager.Contracts;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;

namespace DownloadManager.Server.Auth;

/// <summary>
/// Sign-in for the server (D2): one user, one password. On by default; <c>DOWNLOADMANAGER_AUTH=off</c> turns it off.
/// <list type="bullet">
/// <item>The password is set on the setup page the first time, or by <c>DOWNLOADMANAGER_PASSWORD</c> /
/// <c>DOWNLOADMANAGER_PASSWORD_FILE</c>; only its hash is kept (<see cref="PasswordHash"/>, <see cref="AuthStore"/>).
/// Until it is set, a browser that is not on this machine must also give the setup code the server writes to its log,
/// so whoever reaches a new server first can't take it.</item>
/// <item>A signed-in browser holds a session cookie: <c>HttpOnly</c>, <c>SameSite=Strict</c>, <c>Secure</c> when the
/// request came over HTTPS, 32 random bytes, ending when idle and at an absolute limit (<see cref="SessionStore"/>).</item>
/// <item>Wrong passwords and codes make that address wait, longer each time (<see cref="SignInLimiter"/>). A try
/// counts as wrong from its start until it turns out right, so parallel guesses can't all slip past the wait.</item>
/// <item>A changed password also unpairs every browser extension, unless the change asks to keep them.</item>
/// <item>Only <c>/health</c>, the sign-in page's own files under <c>/signin/</c> and the sign-in calls answer without a
/// session. A paired browser extension uses a bearer token on <c>POST /api/ipc</c> only (<see cref="Pairing"/>).</item>
/// </list>
/// </summary>
public sealed class ServerAuth
{
    public const string CookieName = "inbound_session";
    public const string SignInPath = "/signin/";
    internal const string SessionItem = "inbound.session";
    internal const string TokenItem = "inbound.token";
    /// <summary>The address the connection really came from, noted before a trusted proxy's headers replace it.</summary>
    internal const string PeerItem = "inbound.peer";
    /// <summary>The most a sign-in, setup or pairing body may be, chunked or not.</summary>
    internal const int MaxFormBytes = 16 * 1024;

    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    private readonly ServerLog _log;
    private readonly TimeProvider _time;
    private readonly ServerOptions _options;
    private readonly SemaphoreSlim _passwordGate = new(1, 1);
    private string? _setupCode;

    private ServerAuth(bool required, AuthStore store, bool passwordFromEnvironment, ServerOptions options, ServerLog log)
    {
        Required = required;
        Store = store;
        PasswordFromEnvironment = passwordFromEnvironment;
        _log = log;
        _time = options.Time;
        _options = options;
        Sessions = new SessionStore(_time, options.SessionIdleTimeout, options.SessionAbsoluteTimeout);
        Limiter = new SignInLimiter(_time);
        Pairing = new Pairing(store, _time);
    }

    public bool Required { get; }
    public AuthStore Store { get; }
    public SessionStore Sessions { get; }
    public SignInLimiter Limiter { get; }
    public Pairing Pairing { get; }
    public bool PasswordFromEnvironment { get; }
    public bool SetupNeeded => Required && Store.Password is null;

    /// <summary>The code a browser from elsewhere needs on the setup page; null once a password is set.</summary>
    internal string? SetupCode => SetupNeeded ? _setupCode : null;

    public static async Task<ServerAuth> CreateAsync(ServerOptions options, string dataFolder, ServerLog log, CancellationToken cancellationToken)
    {
        var store = AuthStore.Open(dataFolder);
        var password = options.Password?.Value;
        if (password is null && !string.IsNullOrWhiteSpace(options.PasswordFile))
        {
            try
            {
                // A Docker secret ends with a line break more often than not; it isn't part of the password.
                password = (await File.ReadAllTextAsync(options.PasswordFile, Encoding.UTF8, cancellationToken).ConfigureAwait(false)).TrimEnd('\r', '\n');
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                throw new ServerStartException($"The password file {options.PasswordFile} ({ServerOptions.PasswordFileVariable}) can't be read: {ex.Message}");
            }
        }

        var fromEnvironment = false;
        if (password is not null)
        {
            if (PasswordHash.Problem(password) is { } problem)
                throw new ServerStartException($"{ServerOptions.PasswordVariable}: {problem}");
            fromEnvironment = true;
            if (!await PasswordHash.VerifyAsync(password, store.Password, cancellationToken).ConfigureAwait(false))
            {
                store.SetPassword(await PasswordHash.CreateAsync(password, options.Time.GetUtcNow(), "environment", cancellationToken).ConfigureAwait(false));
                log.Info("The sign-in password was set from the environment.");
            }
        }

        var auth = new ServerAuth(options.SignInRequired, store, fromEnvironment, options, log);
        if (!options.SignInRequired)
        {
            log.Warn($"Sign-in is off ({ServerOptions.AuthVariable}=off): anyone who can reach this server controls it.");
        }
        else if (auth.SetupNeeded)
        {
            auth._setupCode = NewSetupCode();
            log.Info($"No password is set yet. Open the web UI to choose one. A browser on another computer is asked for this setup code: {auth._setupCode}");
        }
        else
        {
            log.Info("Sign-in is on.");
        }

        return auth;
    }

    private static string NewSetupCode()
    {
        var code = new StringBuilder(9);
        for (var i = 0; i < 8; i++)
        {
            if (i == 4) code.Append('-');
            code.Append(Pairing.Alphabet[RandomNumberGenerator.GetInt32(Pairing.Alphabet.Length)]);
        }

        return code.ToString();
    }

    // ---- The gate ----------------------------------------------------------------------------------------------

    /// <summary>What answers without a session.</summary>
    internal static bool IsPublic(PathString path) =>
        path.Equals("/health", StringComparison.OrdinalIgnoreCase) ||
        path.Equals("/favicon.svg", StringComparison.OrdinalIgnoreCase) ||
        path.Equals("/theme.js", StringComparison.OrdinalIgnoreCase) ||
        path.Equals("/signin", StringComparison.OrdinalIgnoreCase) ||
        path.StartsWithSegments("/signin", StringComparison.OrdinalIgnoreCase) ||
        path.Equals("/api/auth/status", StringComparison.OrdinalIgnoreCase) ||
        path.Equals("/api/auth/setup", StringComparison.OrdinalIgnoreCase) ||
        path.Equals("/api/auth/signin", StringComparison.OrdinalIgnoreCase) ||
        path.Equals("/api/auth/signout", StringComparison.OrdinalIgnoreCase) ||
        path.Equals("/api/pair", StringComparison.OrdinalIgnoreCase);

    public Func<HttpContext, Func<Task>, Task> Middleware => async (context, next) =>
    {
        var request = context.Request;
        // A paired extension: its token, on POST /api/ipc only, whether sign-in is on or not.
        if (request.Path.Equals("/api/ipc", StringComparison.OrdinalIgnoreCase) && request.Headers.Authorization.Count > 0)
        {
            var header = request.Headers.Authorization.ToString();
            var token = header.StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase) ? Pairing.Authenticate(header[7..].Trim()) : null;
            if (token is null)
            {
                await Refuse(context, StatusCodes.Status401Unauthorized, "This browser isn't paired with the server, or its pairing was removed. Pair it again.").ConfigureAwait(false);
                return;
            }

            context.Items[TokenItem] = token;
            await next().ConfigureAwait(false);
            return;
        }

        var session = Sessions.Find(request.Cookies[CookieName]);
        if (session is not null) context.Items[SessionItem] = session;
        if (!Required || session is not null || IsPublic(request.Path))
        {
            await next().ConfigureAwait(false);
            return;
        }

        if (request.Path.StartsWithSegments("/api"))
        {
            await Refuse(context, StatusCodes.Status401Unauthorized, "Sign in first.").ConfigureAwait(false);
            return;
        }

        if (HttpMethods.IsGet(request.Method) || HttpMethods.IsHead(request.Method))
        {
            context.Response.Redirect(SignInPath);
            return;
        }

        context.Response.StatusCode = StatusCodes.Status401Unauthorized;
    };

    internal static Session? SessionOf(HttpContext context) => context.Items.TryGetValue(SessionItem, out var value) ? value as Session : null;

    internal static TokenDto? TokenOf(HttpContext context) => context.Items.TryGetValue(TokenItem, out var value) ? value as TokenDto : null;

    // ---- Endpoints ---------------------------------------------------------------------------------------------

    public void Map(WebApplication web)
    {
        web.MapGet("/api/auth/status", (HttpContext context) => Results.Json(Status(context)));
        web.MapPost("/api/auth/setup", (Func<HttpContext, Task<IResult>>)SetupAsync);
        web.MapPost("/api/auth/signin", (Func<HttpContext, Task<IResult>>)SignInAsync);
        web.MapPost("/api/auth/signout", (HttpContext context) =>
        {
            if (SessionOf(context) is { } session) Sessions.Remove(session);
            ExpireCookie(context);
            return Results.Json(new { ok = true });
        });
        web.MapPost("/api/auth/password", (Func<HttpContext, Task<IResult>>)ChangePasswordAsync);
        web.MapPost("/api/auth/pairing/code", () =>
        {
            var (code, expires) = Pairing.NewCode();
            return Results.Json(new { ok = true, code, expiresAt = expires });
        });
        web.MapDelete("/api/auth/pairing/code", () =>
        {
            Pairing.CancelCode();
            return Results.Json(new { ok = true });
        });
        web.MapGet("/api/auth/tokens", () => Results.Json(new { ok = true, tokens = Pairing.List() }));
        web.MapDelete("/api/auth/tokens/{id}", (string id) =>
            Pairing.Revoke(id)
                ? Results.Json(new { ok = true })
                : Results.Json(new { ok = false, error = "No paired browser has that id." }, statusCode: StatusCodes.Status404NotFound));
        web.MapPost("/api/pair", (Func<HttpContext, Task<IResult>>)PairAsync);
    }

    private object Status(HttpContext context) => new
    {
        signInRequired = Required,
        signedIn = !Required || SessionOf(context) is not null,
        setupNeeded = SetupNeeded,
        setupCodeNeeded = SetupNeeded && NeedsSetupCode(context),
        passwordFromEnvironment = PasswordFromEnvironment,
        sessionEndsAt = SessionOf(context) is { } session ? Sessions.AbsoluteEnd(session) : (DateTimeOffset?)null,
        // Only for a browser that may use the server: a version tells a stranger which flaws to try.
        version = !Required || SessionOf(context) is not null ? Protocol.HostVersion : null,
    };

    private sealed class PasswordForm
    {
        public string? Password { get; set; }
        public string? Current { get; set; }
        public string? SetupCode { get; set; }
        /// <summary>On a password change: leave the paired browser extensions paired (they are unpaired otherwise).</summary>
        public bool KeepPairedBrowsers { get; set; }
    }

    private sealed class PairForm
    {
        public string? Code { get; set; }
        public string? Name { get; set; }
    }

    private async Task<IResult> SetupAsync(HttpContext context)
    {
        if (!Required) return Error(StatusCodes.Status400BadRequest, "Sign-in is turned off on this server.");
        var form = await ReadAsync<PasswordForm>(context).ConfigureAwait(false);
        if (form is null) return Error(StatusCodes.Status400BadRequest, "A JSON body with the password is expected.");
        if (!SetupNeeded) return Error(StatusCodes.Status409Conflict, "A password is set already. Sign in with it.");
        var address = AddressOf(context);
        var needsCode = NeedsSetupCode(context);
        // Only a try with a setup code is a guess; it counts as wrong until the code turns out right.
        var ifWrong = TimeSpan.Zero;
        if ((needsCode ? Limiter.Begin(address, out ifWrong) : Limiter.Wait(address)) is { } wait) return TooSoon(context, wait);

        await _passwordGate.WaitAsync(context.RequestAborted).ConfigureAwait(false);
        try
        {
            if (!SetupNeeded) return Error(StatusCodes.Status409Conflict, "A password is set already. Sign in with it.");
            if (needsCode)
            {
                var given = Pairing.Normalize(form.SetupCode);
                var expected = Pairing.Normalize(_setupCode);
                if (given is null || expected is null ||
                    !CryptographicOperations.FixedTimeEquals(Encoding.ASCII.GetBytes(given), Encoding.ASCII.GetBytes(expected)))
                {
                    _log.Warn($"A setup try with a wrong setup code from {address}.");
                    return Error(StatusCodes.Status403Forbidden, "That setup code is wrong. It is in the server's log (docker logs).", ifWrong);
                }

                Limiter.Succeeded(address);
            }

            if (PasswordHash.Problem(form.Password) is { } problem) return Error(StatusCodes.Status400BadRequest, problem);
            var record = await PasswordHash.CreateAsync(form.Password!, _time.GetUtcNow(), "setup", context.RequestAborted).ConfigureAwait(false);
            if (!TrySave(() => Store.SetPassword(record))) return NotSaved();
            _setupCode = null;
            _log.Info($"The sign-in password was set on the setup page from {address}.");
        }
        finally
        {
            _passwordGate.Release();
        }

        StartSession(context);
        return Results.Json(new { ok = true });
    }

    private async Task<IResult> SignInAsync(HttpContext context)
    {
        if (!Required) return Results.Json(new { ok = true });
        var form = await ReadAsync<PasswordForm>(context).ConfigureAwait(false);
        if (form is null) return Error(StatusCodes.Status400BadRequest, "A JSON body with the password is expected.");
        if (SetupNeeded) return Error(StatusCodes.Status409Conflict, "No password is set yet. Set one first.");
        var address = AddressOf(context);
        // Counted as wrong before the slow check, so the tries waiting for it can't skip the wait.
        if (Limiter.Begin(address, out var ifWrong) is { } wait) return TooSoon(context, wait);

        if (!await PasswordHash.VerifyAsync(form.Password, Store.Password, context.RequestAborted).ConfigureAwait(false))
        {
            _log.Warn($"A sign-in with a wrong password from {address} ({Limiter.Failures(address)} in a row).");
            return Error(StatusCodes.Status401Unauthorized, "That password is wrong.", ifWrong);
        }

        Limiter.Succeeded(address);
        StartSession(context);
        return Results.Json(new { ok = true });
    }

    private async Task<IResult> ChangePasswordAsync(HttpContext context)
    {
        if (!Required) return Error(StatusCodes.Status400BadRequest, "Sign-in is turned off on this server.");
        if (PasswordFromEnvironment)
            return Error(StatusCodes.Status409Conflict, $"The password comes from {ServerOptions.PasswordVariable} or {ServerOptions.PasswordFileVariable}. Change it there and restart the server.");
        var form = await ReadAsync<PasswordForm>(context).ConfigureAwait(false);
        if (form is null) return Error(StatusCodes.Status400BadRequest, "A JSON body with the passwords is expected.");
        var address = AddressOf(context);
        if (Limiter.Begin(address, out var ifWrong) is { } wait) return TooSoon(context, wait);

        await _passwordGate.WaitAsync(context.RequestAborted).ConfigureAwait(false);
        try
        {
            if (!await PasswordHash.VerifyAsync(form.Current, Store.Password, context.RequestAborted).ConfigureAwait(false))
                return Error(StatusCodes.Status403Forbidden, "The current password is wrong.", ifWrong);

            Limiter.Succeeded(address);
            if (PasswordHash.Problem(form.Password) is { } problem) return Error(StatusCodes.Status400BadRequest, problem);
            var record = await PasswordHash.CreateAsync(form.Password!, _time.GetUtcNow(), "changed", context.RequestAborted).ConfigureAwait(false);
            if (!TrySave(() => Store.SetPassword(record))) return NotSaved();
        }
        finally
        {
            _passwordGate.Release();
        }

        // Every other browser signs in again with the new password; this one stays. Someone who knew the old one may
        // have paired an extension too, so those go as well unless the change says to keep them.
        Sessions.RemoveAllExcept(SessionOf(context));
        var unpaired = 0;
        if (!form.KeepPairedBrowsers && !TrySave(() => unpaired = Pairing.RevokeAll()))
        {
            _log.Warn("The sign-in password was changed and other browsers were signed out, but the paired browsers could not be removed.");
            return Error(StatusCodes.Status500InternalServerError,
                "The password was changed, but the paired browsers could not be removed. Remove them under Paired browsers.");
        }

        _log.Info(form.KeepPairedBrowsers
            ? "The sign-in password was changed; other browsers were signed out, paired browsers were kept."
            : $"The sign-in password was changed; other browsers were signed out and {unpaired} paired browser(s) removed.");
        return Results.Json(new { ok = true, unpaired });
    }

    private async Task<IResult> PairAsync(HttpContext context)
    {
        var form = await ReadAsync<PairForm>(context).ConfigureAwait(false);
        if (form is null) return Error(StatusCodes.Status400BadRequest, "A JSON body with the code is expected.");
        var address = AddressOf(context);
        if (Limiter.Begin(address, out var ifWrong) is { } wait) return TooSoon(context, wait);
        if (Pairing.Redeem(form.Code, form.Name) is not { } paired)
        {
            _log.Warn($"A pairing try with a wrong or expired code from {address}.");
            return Error(StatusCodes.Status401Unauthorized, "That code is wrong, used or expired. Show a new one in the web UI (Settings, Paired browsers).", ifWrong);
        }

        Limiter.Succeeded(address);
        _log.Info($"A browser was paired: {paired.Record.Name} ({paired.Record.Id}).");
        return Results.Json(new { ok = true, token = paired.Token, id = paired.Record.Id, name = paired.Record.Name });
    }

    // ---- Helpers -----------------------------------------------------------------------------------------------

    /// <summary>
    /// A browser on this machine, not through a proxy, sets the first password without the code. What counts is the
    /// address the connection really came from (<see cref="PeerItem"/>), not one a proxy's header gave: a connection
    /// from a listed proxy (<c>DOWNLOADMANAGER_TRUST_PROXY</c>) or one carrying forwarding headers can be from anywhere.
    /// A proxy on this machine that adds no such header can't be told apart; it must be listed (docs/docker.md).
    /// </summary>
    private bool NeedsSetupCode(HttpContext context)
    {
        var peer = context.Items.TryGetValue(PeerItem, out var noted) && noted is IPAddress address ? address : context.Connection.RemoteIpAddress;
        if (peer is null || !IPAddress.IsLoopback(peer) || _options.IsTrustedProxy(peer)) return true;
        var headers = context.Request.Headers;
        return headers.ContainsKey("X-Forwarded-For") || headers.ContainsKey("Forwarded") || headers.ContainsKey("X-Original-For");
    }

    /// <summary>Runs a write to auth.json; false when it couldn't be saved (nothing changed then, in memory or on disk).</summary>
    private bool TrySave(Action save)
    {
        try
        {
            save();
            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            _log.Error($"The sign-in file could not be saved: {ex.GetType().Name}");
            return false;
        }
    }

    private static IResult NotSaved() =>
        Error(StatusCodes.Status500InternalServerError, "The password could not be saved, so nothing was changed. Check the data folder's space and permissions.");

    private void StartSession(HttpContext context)
    {
        var (id, session) = Sessions.Create();
        context.Response.Cookies.Append(CookieName, id, new CookieOptions
        {
            HttpOnly = true,
            SameSite = SameSiteMode.Strict,
            Secure = context.Request.IsHttps,
            Path = "/",
            Expires = Sessions.AbsoluteEnd(session),
            IsEssential = true,
        });
    }

    private static void ExpireCookie(HttpContext context) =>
        context.Response.Cookies.Delete(CookieName, new CookieOptions
        {
            HttpOnly = true,
            SameSite = SameSiteMode.Strict,
            Secure = context.Request.IsHttps,
            Path = "/",
        });

    private static string AddressOf(HttpContext context) => SignInLimiter.AddressOf(context.Connection.RemoteIpAddress);

    /// <summary>
    /// The JSON form, or null. At most <see cref="MaxFormBytes"/> are read, whether the length was given or the body
    /// came chunked: these calls answer without a session, before any password is checked.
    /// </summary>
    private static async Task<T?> ReadAsync<T>(HttpContext context) where T : class
    {
        if (context.Request.ContentLength is > MaxFormBytes) return null;
        var buffer = new byte[MaxFormBytes + 1];
        var length = 0;
        try
        {
            int read;
            while (length < buffer.Length &&
                   (read = await context.Request.Body.ReadAsync(buffer.AsMemory(length), context.RequestAborted).ConfigureAwait(false)) > 0)
                length += read;
        }
        catch (BadHttpRequestException)
        {
            return null;
        }

        if (length > MaxFormBytes) return null;
        try
        {
            return JsonSerializer.Deserialize<T>(buffer.AsSpan(0, length), Json);
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private static IResult Error(int status, string message, TimeSpan? retryAfter = null) =>
        Results.Json(new { ok = false, error = message, retryAfterSeconds = retryAfter is { } wait ? (int?)Math.Ceiling(wait.TotalSeconds) : null },
            statusCode: status);

    private static IResult TooSoon(HttpContext context, TimeSpan wait)
    {
        var seconds = (int)Math.Ceiling(wait.TotalSeconds);
        context.Response.Headers.RetryAfter = seconds.ToString(System.Globalization.CultureInfo.InvariantCulture);
        return Error(StatusCodes.Status429TooManyRequests, $"Too many wrong tries. Wait {seconds} s and try again.", wait);
    }

    private static Task Refuse(HttpContext context, int status, string message)
    {
        context.Response.StatusCode = status;
        return context.Response.WriteAsJsonAsync(new { ok = false, error = message, signIn = SignInPath });
    }
}

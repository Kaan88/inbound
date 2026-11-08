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
/// <item>Wrong passwords and codes make that address wait, longer each time (<see cref="SignInLimiter"/>).</item>
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

    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    private readonly ServerLog _log;
    private readonly TimeProvider _time;
    private readonly SemaphoreSlim _passwordGate = new(1, 1);
    private string? _setupCode;

    private ServerAuth(bool required, AuthStore store, bool passwordFromEnvironment, ServerOptions options, ServerLog log)
    {
        Required = required;
        Store = store;
        PasswordFromEnvironment = passwordFromEnvironment;
        _log = log;
        _time = options.Time;
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
        var address = AddressOf(context);
        if (Limiter.Wait(address) is { } wait) return TooSoon(context, wait);

        await _passwordGate.WaitAsync(context.RequestAborted).ConfigureAwait(false);
        try
        {
            if (!SetupNeeded) return Error(StatusCodes.Status409Conflict, "A password is set already. Sign in with it.");
            if (NeedsSetupCode(context))
            {
                var given = Pairing.Normalize(form.SetupCode);
                var expected = Pairing.Normalize(_setupCode);
                if (given is null || expected is null ||
                    !CryptographicOperations.FixedTimeEquals(Encoding.ASCII.GetBytes(given), Encoding.ASCII.GetBytes(expected)))
                {
                    var next = Limiter.Failed(address);
                    _log.Warn($"A setup try with a wrong setup code from {address}.");
                    return Error(StatusCodes.Status403Forbidden, "That setup code is wrong. It is in the server's log (docker logs).", next);
                }
            }

            if (PasswordHash.Problem(form.Password) is { } problem) return Error(StatusCodes.Status400BadRequest, problem);
            Store.SetPassword(await PasswordHash.CreateAsync(form.Password!, _time.GetUtcNow(), "setup", context.RequestAborted).ConfigureAwait(false));
            _setupCode = null;
            Limiter.Succeeded(address);
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
        if (Limiter.Wait(address) is { } wait) return TooSoon(context, wait);

        if (!await PasswordHash.VerifyAsync(form.Password, Store.Password, context.RequestAborted).ConfigureAwait(false))
        {
            var next = Limiter.Failed(address);
            _log.Warn($"A sign-in with a wrong password from {address} ({Limiter.Failures(address)} in a row).");
            return Error(StatusCodes.Status401Unauthorized, "That password is wrong.", next);
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
        if (Limiter.Wait(address) is { } wait) return TooSoon(context, wait);

        await _passwordGate.WaitAsync(context.RequestAborted).ConfigureAwait(false);
        try
        {
            if (!await PasswordHash.VerifyAsync(form.Current, Store.Password, context.RequestAborted).ConfigureAwait(false))
            {
                var next = Limiter.Failed(address);
                return Error(StatusCodes.Status403Forbidden, "The current password is wrong.", next);
            }

            if (PasswordHash.Problem(form.Password) is { } problem) return Error(StatusCodes.Status400BadRequest, problem);
            Store.SetPassword(await PasswordHash.CreateAsync(form.Password!, _time.GetUtcNow(), "changed", context.RequestAborted).ConfigureAwait(false));
        }
        finally
        {
            _passwordGate.Release();
        }

        Limiter.Succeeded(address);
        // Every other browser signs in again with the new password; this one stays.
        Sessions.RemoveAllExcept(SessionOf(context));
        _log.Info("The sign-in password was changed; other browsers were signed out.");
        return Results.Json(new { ok = true });
    }

    private async Task<IResult> PairAsync(HttpContext context)
    {
        var form = await ReadAsync<PairForm>(context).ConfigureAwait(false);
        if (form is null) return Error(StatusCodes.Status400BadRequest, "A JSON body with the code is expected.");
        var address = AddressOf(context);
        if (Limiter.Wait(address) is { } wait) return TooSoon(context, wait);
        if (Pairing.Redeem(form.Code, form.Name) is not { } paired)
        {
            var next = Limiter.Failed(address);
            _log.Warn($"A pairing try with a wrong or expired code from {address}.");
            return Error(StatusCodes.Status401Unauthorized, "That code is wrong, used or expired. Show a new one in the web UI (Settings, Paired browsers).", next);
        }

        Limiter.Succeeded(address);
        _log.Info($"A browser was paired: {paired.Record.Name} ({paired.Record.Id}).");
        return Results.Json(new { ok = true, token = paired.Token, id = paired.Record.Id, name = paired.Record.Name });
    }

    // ---- Helpers -----------------------------------------------------------------------------------------------

    /// <summary>A browser on this machine, not through a proxy, sets the first password without the code.</summary>
    private static bool NeedsSetupCode(HttpContext context) =>
        context.Connection.RemoteIpAddress is not { } remote || !IPAddress.IsLoopback(remote) ||
        context.Request.Headers.ContainsKey("X-Forwarded-For") || context.Request.Headers.ContainsKey("Forwarded");

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

    private static async Task<T?> ReadAsync<T>(HttpContext context) where T : class
    {
        if (context.Request.ContentLength is > 16 * 1024) return null;
        try
        {
            return await JsonSerializer.DeserializeAsync<T>(context.Request.Body, Json, context.RequestAborted).ConfigureAwait(false);
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

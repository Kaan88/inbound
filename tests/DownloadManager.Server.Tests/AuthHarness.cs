using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using DownloadManager.Contracts;
using DownloadManager.Server.Auth;

namespace DownloadManager.Server.Tests;

/// <summary>A clock the test moves by hand.</summary>
internal sealed class ManualTime(DateTimeOffset start) : TimeProvider
{
    private DateTimeOffset _now = start;

    public override DateTimeOffset GetUtcNow() => _now;

    public void Advance(TimeSpan by) => _now += by;
}

/// <summary>An HTTP client of a signed-in-capable server that handles its own cookie and never follows redirects.</summary>
internal sealed class AuthClient : IDisposable
{
    private readonly HttpClient _http;

    public AuthClient(Uri baseAddress)
    {
        _http = new HttpClient(new HttpClientHandler { UseCookies = false, AllowAutoRedirect = false })
        {
            BaseAddress = baseAddress,
            Timeout = TimeSpan.FromSeconds(60),
        };
    }

    /// <summary>The session cookie's value, once a sign-in or setup gave one.</summary>
    public string? Session { get; set; }

    /// <summary>The last Set-Cookie header for the session cookie, as sent.</summary>
    public string? LastSetCookie { get; private set; }

    public string? Origin { get; set; }

    public async Task<HttpResponseMessage> SendAsync(HttpMethod method, string path, object? body = null,
        Action<HttpRequestMessage>? change = null)
    {
        var request = new HttpRequestMessage(method, path);
        if (body is not null) request.Content = JsonContent.Create(body, body.GetType(), options: body is IpcMessage ? IpcJson.Options : null);
        if (Session is not null) request.Headers.Add("Cookie", $"{ServerAuth.CookieName}={Session}");
        if (Origin is not null) request.Headers.Add("Origin", Origin);
        change?.Invoke(request);
        var response = await _http.SendAsync(request);
        if (response.Headers.TryGetValues("Set-Cookie", out var cookies))
        {
            foreach (var cookie in cookies.Where(c => c.StartsWith(ServerAuth.CookieName + "=", StringComparison.Ordinal)))
            {
                LastSetCookie = cookie;
                var value = cookie[(ServerAuth.CookieName.Length + 1)..].Split(';')[0];
                Session = value.Length == 0 ? null : value;
            }
        }

        return response;
    }

    public Task<HttpResponseMessage> PostAsync(string path, object? body = null, Action<HttpRequestMessage>? change = null) =>
        SendAsync(HttpMethod.Post, path, body ?? new { }, change);

    public Task<HttpResponseMessage> GetAsync(string path, Action<HttpRequestMessage>? change = null) => SendAsync(HttpMethod.Get, path, null, change);

    public async Task<JsonElement> JsonAsync(HttpResponseMessage response)
    {
        using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        return document.RootElement.Clone();
    }

    /// <summary>One envelope to <c>/api/ipc</c>, with whatever this client carries.</summary>
    public async Task<(HttpStatusCode Status, IpcMessage? Reply)> IpcAsync(string type, object payload, string? bearer = null)
    {
        var request = IpcMessage.Create(type, payload);
        using var response = await SendAsync(HttpMethod.Post, "/api/ipc", request,
            message => { if (bearer is not null) message.Headers.Add("Authorization", "Bearer " + bearer); });
        IpcMessage? reply = null;
        if (response.Content.Headers.ContentType?.MediaType == "application/json")
        {
            var text = await response.Content.ReadAsStringAsync();
            if (text.Contains("\"type\"", StringComparison.Ordinal)) reply = JsonSerializer.Deserialize<IpcMessage>(text, IpcJson.Options);
        }

        return (response.StatusCode, reply);
    }

    public void Dispose() => _http.Dispose();
}

internal static class AuthServer
{
    public const string Password = "correct horse battery";

    public static Task<ServerHarness> StartAsync(ManualTime? time = null, Func<ServerOptions, ServerOptions>? change = null, string? work = null) =>
        ServerHarness.StartAsync(options =>
        {
            options = options with { SignInRequired = true };
            if (time is not null) options = options with { Time = time };
            return change is null ? options : change(options);
        }, work);

    /// <summary>The setup code the server wrote to its log.</summary>
    public static string SetupCode(ServerHarness server)
    {
        var log = string.Join("\n", Directory.GetFiles(Path.Combine(server.Data, "logs")).Select(File.ReadAllText));
        const string marker = "setup code: ";
        return log[(log.IndexOf(marker, StringComparison.Ordinal) + marker.Length)..].Split('\n')[0].Trim();
    }

    /// <summary>Sets the first password from this machine (with the setup code when asked) and keeps the session.</summary>
    public static async Task<AuthClient> SetUpAsync(ServerHarness server, string password = Password, string? setupCode = null)
    {
        var client = new AuthClient(server.Base);
        using var response = await client.PostAsync("/api/auth/setup", setupCode is null ? new { password } : new { password, setupCode });
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.NotNull(client.Session);
        return client;
    }
}

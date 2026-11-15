using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace DownloadManager.Sites;

/// <summary>A call rclone answered with an error. The message has every secret the client knows of replaced.</summary>
public sealed class RcloneCallException(string command, int status, string message)
    : Exception($"rclone {command} failed ({status}): {message}")
{
    public string Command { get; } = command;
    public int Status { get; } = status;
}

/// <summary>rclone is not running (it exited, or was never reached).</summary>
public sealed class RcloneUnavailableException(string message, Exception? inner = null) : Exception(message, inner);

/// <summary>
/// rclone's remote-control API (<c>rclone rcd</c>) over loopback HTTP with the per-start user and password: every command is
/// a POST of a JSON object to <c>/&lt;command&gt;</c> answered by a JSON object.
/// <para>rclone repeats the parameters it was given in its error bodies, passwords included (SITES.0, point 3). So no
/// error text leaves this class unredacted: every secret registered with <see cref="AddSecret"/> (and the rc password) is
/// replaced by <c>***</c>. Callers register a site's secrets before the first call that carries them.</para>
/// </summary>
public sealed class RcloneClient : IDisposable
{
    private const string Redacted = "***";
    private const string Withheld = "(rclone's text was withheld because it may contain a password)";
    private const int MinimumReplaceable = 4;
    private readonly HttpClient _http;
    private volatile bool _disposed;
    private readonly Func<bool> _exited;
    private readonly object _secretsLock = new();
    private string[] _secrets;

    public RcloneClient(Uri baseAddress, string user, string password, Func<bool>? exited = null, HttpMessageHandler? handler = null)
    {
        _http = handler is null ? new HttpClient() : new HttpClient(handler);
        _http.BaseAddress = baseAddress;
        // Long calls (a big listing, a server that is slow to answer) are cancelled by their callers, not by a fixed clock.
        _http.Timeout = Timeout.InfiniteTimeSpan;
        _http.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Basic",
            Convert.ToBase64String(Encoding.UTF8.GetBytes(user + ":" + password)));
        _exited = exited ?? (() => false);
        _secrets = [];
        AddSecret(password);
    }

    public Uri BaseAddress => _http.BaseAddress!;

    /// <summary>
    /// SITES.6c: the named in-memory remotes of sign-in sites this rclone holds (<c>config/create</c>), each with the token
    /// it was last given or seen with, so a token rclone renewed can be told apart and written back.
    /// </summary>
    public System.Collections.Concurrent.ConcurrentDictionary<string, string> SignedInRemotes { get; } = new(StringComparer.Ordinal);

    /// <summary>
    /// SITES.6c: told the site id and rclone's settings for it when rclone renewed a site's token, so the Host seals and
    /// stores them. Set by the Host for each rclone.
    /// </summary>
    public Func<string, string, JsonObject, CancellationToken, Task>? SignInRenewed { get; set; }

    /// <summary>
    /// A value that must never appear in an error message or a log line. One under four characters is not replaced
    /// inside the text: a text that contains it is withheld whole (<see cref="Redact"/>).
    /// </summary>
    public void AddSecret(string? secret)
    {
        if (string.IsNullOrEmpty(secret)) return;
        lock (_secretsLock)
        {
            if (_secrets.Contains(secret, StringComparer.Ordinal)) return;
            _secrets = [.. _secrets.Append(secret).OrderByDescending(s => s.Length)];
        }
    }

    /// <summary>
    /// <paramref name="text"/> with every known secret replaced. Every place any secret occurs is found in the original text
    /// first and the spans are merged, so secrets that overlap each other leave no tail behind.
    /// </summary>
    public string Redact(string text)
    {
        var secrets = Volatile.Read(ref _secrets);
        if (text.Length == 0 || secrets.Length == 0) return text;
        // A secret under four characters (a short site password) can't be replaced without mangling ordinary words, and
        // a partial job would point at it. If one occurs at all, none of the text is passed on.
        foreach (var secret in secrets)
            if (secret.Length < MinimumReplaceable && text.Contains(secret, StringComparison.Ordinal)) return Withheld;

        var hidden = new bool[text.Length];
        var any = false;
        foreach (var secret in secrets)
        {
            if (secret.Length < MinimumReplaceable) continue;
            for (var at = text.IndexOf(secret, StringComparison.Ordinal); at >= 0; at = text.IndexOf(secret, at + 1, StringComparison.Ordinal))
            {
                Array.Fill(hidden, true, at, secret.Length);
                any = true;
            }
        }

        if (!any) return text;
        var result = new StringBuilder(text.Length);
        for (var i = 0; i < text.Length; i++)
        {
            if (!hidden[i]) result.Append(text[i]);
            else if (i == 0 || !hidden[i - 1]) result.Append(Redacted);
        }

        return result.ToString();
    }

    public async Task<JsonObject> CallAsync(string command, JsonObject? input = null, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(command);
        using var content = new StringContent((input ?? new JsonObject()).ToJsonString(), Encoding.UTF8, "application/json");
        HttpResponseMessage response;
        try
        {
            response = await _http.PostAsync(command, content, cancellationToken).ConfigureAwait(false);
        }
        catch (HttpRequestException e)
        {
            // The exception text can hold the address, never a secret; still, nothing of the request body is in it.
            throw new RcloneUnavailableException(_exited() ? "rclone has exited." : $"rclone could not be reached: {e.Message}", e);
        }
        catch (Exception e) when (_disposed && !cancellationToken.IsCancellationRequested && e is ObjectDisposedException or OperationCanceledException)
        {
            // The instance behind this client was stopped (it crashed, went idle, or Sites is stopping) while a lease
            // still held it, before or during the call: the same as rclone being gone. Only when this client was
            // disposed, so a caller's own disposed token source is not mistaken for it.
            throw new RcloneUnavailableException("rclone has stopped.", e);
        }

        using (response)
        {
            string body;
            try
            {
                body = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
            }
            catch (Exception e) when (_disposed && !cancellationToken.IsCancellationRequested && e is ObjectDisposedException or OperationCanceledException)
            {
                throw new RcloneUnavailableException("rclone has stopped.", e);
            }
            catch (Exception e) when (e is HttpRequestException or IOException)
            {
                // rclone went away half way through its answer (it quits, or crashed).
                throw new RcloneUnavailableException(_exited() ? "rclone has exited." : $"rclone's answer was cut off: {e.Message}", e);
            }

            if (response.StatusCode == HttpStatusCode.OK)
            {
                try
                {
                    return JsonNode.Parse(body) as JsonObject ?? throw new JsonException("not an object");
                }
                catch (JsonException)
                {
                    throw new RcloneCallException(command, 200, "rclone's answer was not a JSON object.");
                }
            }

            if (response.StatusCode == HttpStatusCode.Unauthorized)
                throw new RcloneCallException(command, 401, "rclone refused the Host's credentials.");
            throw new RcloneCallException(command, (int)response.StatusCode, Redact(ErrorText(body)));
        }
    }

    /// <summary>rclone's "obscured" form of a password, which its backends expect in <c>pass</c>-type options (SITES.0).</summary>
    public async Task<string> ObscureAsync(string clear, CancellationToken cancellationToken = default)
    {
        AddSecret(clear);
        var result = await CallAsync("core/obscure", new JsonObject { ["clear"] = clear }, cancellationToken).ConfigureAwait(false);
        var obscured = result["obscured"]?.GetValue<string>() ?? throw new RcloneCallException("core/obscure", 200, "no obscured value in the answer.");
        // The obscured form is reversible, so it is as secret as the password.
        AddSecret(obscured);
        return obscured;
    }

    private static string ErrorText(string body)
    {
        try
        {
            if (JsonNode.Parse(body) is JsonObject error && error["error"] is JsonValue text && text.TryGetValue<string>(out var message))
                return message;
        }
        catch (JsonException)
        {
        }

        // Not rclone's error object: the raw body could hold a secret in an escaped form the redaction would miss.
        return "rclone gave no error text.";
    }

    public void Dispose()
    {
        _disposed = true;
        _http.Dispose();
    }
}

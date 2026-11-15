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
    private readonly HttpClient _http;
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

    /// <summary>A value that must never appear in an error message or a log line. Empty and very short values are ignored.</summary>
    public void AddSecret(string? secret)
    {
        // Under four characters a replacement would mangle ordinary words and still hide almost nothing.
        if (string.IsNullOrEmpty(secret) || secret.Length < 4) return;
        lock (_secretsLock)
        {
            if (_secrets.Contains(secret, StringComparer.Ordinal)) return;
            // Longest first, so a secret that contains another is replaced whole.
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
        var hidden = new bool[text.Length];
        var any = false;
        foreach (var secret in secrets)
        {
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

        using (response)
        {
            var body = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
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

    public void Dispose() => _http.Dispose();
}

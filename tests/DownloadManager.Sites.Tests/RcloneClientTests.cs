using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json.Nodes;
using DownloadManager.Sites;

namespace DownloadManager.Sites.Tests;

/// <summary>The rc client over a fake handler: request shape, error redaction and the failure kinds. No rclone, no network.</summary>
public sealed class RcloneClientTests
{
    private const string User = "user";
    private const string RcPassword = "rc-pass-9876";
    private static readonly Uri Base = new("http://127.0.0.1:9/");

    [Fact]
    public async Task ConditionalS3UploadHasRequestLocalPreconditionAndNoUnconditionalFallback()
    {
        var handler = new FakeHandler(_ => Reply(HttpStatusCode.InternalServerError,
            """{"error":"S3 PutObject StatusCode: 412 api error PreconditionFailed"}"""));
        using var client = new RcloneClient(Base, User, RcPassword, handler: handler);
        using var content = new MemoryStream("abc"u8.ToArray());
        var refusal = await Assert.ThrowsAsync<RcloneCallException>(() =>
            client.CreateS3FileAsync("test-remote", "/bucket", "proof.bin", content));

        Assert.Contains("PreconditionFailed", refusal.Message);
        var sent = Assert.Single(handler.Requests);
        var query = Uri.UnescapeDataString(sent.Path);
        Assert.Contains("/operations/uploadfile?fs=test-remote:/&remote=bucket", query);
        Assert.Contains("""{"Key":"If-None-Match","Value":"*"}""", query);
        Assert.Contains("proof.bin", sent.Body);
        Assert.Contains("abc", sent.Body);
    }

    [Fact]
    public async Task A_json_object_answer_is_returned_and_the_request_is_a_basic_auth_json_post()
    {
        var handler = new FakeHandler(_ => Reply(HttpStatusCode.OK, """{"ok":true}"""));
        using var client = new RcloneClient(Base, User, RcPassword, handler: handler);

        var answer = await client.CallAsync("operations/list", new JsonObject { ["fs"] = "remote:" });

        Assert.True(answer["ok"]!.GetValue<bool>());
        var sent = Assert.Single(handler.Requests);
        Assert.Equal("POST", sent.Method);
        Assert.Equal("/operations/list", sent.Path);
        Assert.Equal("application/json", sent.ContentType);
        Assert.Equal("""{"fs":"remote:"}""", sent.Body);
        Assert.Equal("Basic", sent.Authorization?.Scheme);
        Assert.Equal(Convert.ToBase64String(Encoding.UTF8.GetBytes("user:" + RcPassword)), sent.Authorization?.Parameter);
    }

    [Fact]
    public async Task A_call_on_a_client_whose_rclone_was_stopped_is_unavailable_not_disposed()
    {
        // A lease can outlive its instance: rclone crashed or went idle and the supervisor disposed the client.
        var client = new RcloneClient(Base, User, RcPassword, handler: new FakeHandler(_ => Reply(HttpStatusCode.OK, "{}")));
        client.Dispose();

        await Assert.ThrowsAsync<RcloneUnavailableException>(() => client.CallAsync("core/version"));
    }

    [Fact]
    public async Task A_call_in_flight_when_the_client_is_disposed_is_unavailable()
    {
        var started = new TaskCompletionSource();
        var handler = new FakeHandler(async (_, ct) =>
        {
            started.SetResult();
            await Task.Delay(Timeout.Infinite, ct);
            return Reply(HttpStatusCode.OK, "{}");
        });
        var client = new RcloneClient(Base, User, RcPassword, handler: handler);
        var call = client.CallAsync("core/version");
        await started.Task;
        client.Dispose();

        await Assert.ThrowsAsync<RcloneUnavailableException>(() => call);
    }

    [Fact]
    public async Task A_disposed_token_source_of_the_caller_is_not_reported_as_rclone_stopping()
    {
        using var client = new RcloneClient(Base, User, RcPassword, handler: new FakeHandler(_ => Reply(HttpStatusCode.OK, "{}")));
        var source = new CancellationTokenSource();
        var token = source.Token;
        source.Dispose();

        // Whatever the HTTP stack makes of it, it is not "rclone has stopped".
        var error = await Record.ExceptionAsync(() => client.CallAsync("core/version", cancellationToken: token));
        Assert.IsNotType<RcloneUnavailableException>(error);
    }

    [Fact]
    public async Task A_null_input_is_sent_as_an_empty_object()
    {
        var handler = new FakeHandler(_ => Reply(HttpStatusCode.OK, "{}"));
        using var client = new RcloneClient(Base, User, RcPassword, handler: handler);

        await client.CallAsync("rc/noop");

        Assert.Equal("{}", Assert.Single(handler.Requests).Body);
    }

    [Fact]
    public async Task A_500_error_text_is_redacted_of_the_secret_and_the_rc_password()
    {
        const string body = """{"error":"failed for pass=SECRETVALUE1 and the rc password rc-pass-9876","input":{"pass":"SECRETVALUE1"},"status":500}""";
        var handler = new FakeHandler(_ => Reply(HttpStatusCode.InternalServerError, body));
        using var client = new RcloneClient(Base, User, RcPassword, handler: handler);
        client.AddSecret("SECRETVALUE1");

        var ex = await Assert.ThrowsAsync<RcloneCallException>(() => client.CallAsync("config/create"));

        Assert.Contains("***", ex.Message);
        Assert.DoesNotContain("SECRETVALUE1", ex.Message);
        Assert.DoesNotContain(RcPassword, ex.Message);
        Assert.Equal(500, ex.Status);
        Assert.Equal("config/create", ex.Command);
    }

    [Fact]
    public async Task A_non_json_error_body_is_not_passed_on()
    {
        var handler = new FakeHandler(_ => Reply(HttpStatusCode.InternalServerError, "<html>SECRETVALUE1 oops</html>"));
        using var client = new RcloneClient(Base, User, RcPassword, handler: handler);
        client.AddSecret("SECRETVALUE1");

        var ex = await Assert.ThrowsAsync<RcloneCallException>(() => client.CallAsync("config/create"));

        Assert.Contains("rclone gave no error text.", ex.Message);
        Assert.DoesNotContain("SECRETVALUE1", ex.Message);
        Assert.DoesNotContain("<html>", ex.Message);
    }

    [Fact]
    public async Task A_401_is_refused_without_the_password()
    {
        var handler = new FakeHandler(_ => Reply(HttpStatusCode.Unauthorized, $"bad credentials {RcPassword}"));
        using var client = new RcloneClient(Base, User, RcPassword, handler: handler);

        var ex = await Assert.ThrowsAsync<RcloneCallException>(() => client.CallAsync("rc/noop"));

        Assert.Equal(401, ex.Status);
        Assert.DoesNotContain(RcPassword, ex.Message);
    }

    [Fact]
    public async Task A_failed_connection_is_unavailable_and_says_so()
    {
        var handler = new FakeHandler(_ => throw new HttpRequestException("connection refused"));
        using var client = new RcloneClient(Base, User, RcPassword, handler: handler);

        var ex = await Assert.ThrowsAsync<RcloneUnavailableException>(() => client.CallAsync("rc/noop"));

        Assert.Contains("could not be reached", ex.Message);
    }

    [Fact]
    public async Task A_failed_connection_after_rclone_exited_says_it_has_exited()
    {
        var handler = new FakeHandler(_ => throw new HttpRequestException("connection refused"));
        using var client = new RcloneClient(Base, User, RcPassword, exited: () => true, handler: handler);

        var ex = await Assert.ThrowsAsync<RcloneUnavailableException>(() => client.CallAsync("rc/noop"));

        Assert.Equal("rclone has exited.", ex.Message);
    }

    [Fact]
    public void A_short_secret_withholds_any_text_that_contains_it()
    {
        using var client = new RcloneClient(Base, User, RcPassword, handler: new FakeHandler(_ => Reply(HttpStatusCode.OK, "{}")));

        client.AddSecret(null);
        client.AddSecret("");
        Assert.Equal("no secret here", client.Redact("no secret here"));

        client.AddSecret("q7z");
        Assert.Equal("no secret here", client.Redact("no secret here"));
        var withheld = client.Redact("login failed for pass=q7z");
        Assert.DoesNotContain("q7z", withheld);
        Assert.Contains("withheld", withheld);
    }

    [Fact]
    public void Overlapping_secrets_leave_no_tail()
    {
        using var client = new RcloneClient(Base, User, RcPassword, handler: new FakeHandler(_ => Reply(HttpStatusCode.OK, "{}")));
        client.AddSecret("abcdefgh");
        client.AddSecret("cdefghij");
        Assert.Equal("<***>", client.Redact("<abcdefghij>"));
        Assert.Equal("*** and ***", client.Redact("abcdefgh and cdefghij"));
    }

    [Fact]
    public void A_secret_that_contains_another_is_replaced_whole()
    {
        using var client = new RcloneClient(Base, User, RcPassword, handler: new FakeHandler(_ => Reply(HttpStatusCode.OK, "{}")));

        client.AddSecret("abcd");
        client.AddSecret("abcdefgh");

        Assert.Equal("x***x", client.Redact("xabcdefghx"));
    }

    [Fact]
    public async Task A_200_answer_that_is_not_a_json_object_throws()
    {
        var handler = new FakeHandler(_ => Reply(HttpStatusCode.OK, "[1]"));
        using var client = new RcloneClient(Base, User, RcPassword, handler: handler);

        await Assert.ThrowsAsync<RcloneCallException>(() => client.CallAsync("rc/noop"));
    }

    private sealed record Sent(string Method, string Path, string? ContentType, AuthenticationHeaderValue? Authorization, string Body);

    /// <summary>Records each request (copied out before the handler returns) and answers with <paramref name="respond"/>.</summary>
    [Fact]
    public async Task A_password_is_obscured_once_until_a_call_fails_then_obscured_anew()
    {
        // P6: the same obscured form gives rclone the same fs string, so it reuses its connection; after a failure a new
        // form makes it build a fresh backend instead of one still retrying a server that went away.
        var obscures = 0;
        var fail = false;
        var handler = new FakeHandler(request =>
        {
            var path = request.RequestUri!.AbsolutePath;
            if (path.EndsWith("core/obscure")) return Reply(HttpStatusCode.OK, "{\"obscured\":\"form" + ++obscures + "\"}");
            if (path.EndsWith("job/status")) return Reply(HttpStatusCode.OK, fail ? "{\"error\":\"connection refused\"}" : "{\"error\":\"\"}");
            return Reply(HttpStatusCode.InternalServerError, "{\"error\":\"dial tcp: connection refused\"}");
        });
        using var client = new RcloneClient(Base, User, RcPassword, handler: handler);

        Assert.Equal("form1", await client.ObscureAsync("site-password"));
        Assert.Equal("form1", await client.ObscureAsync("site-password"));
        await client.CallAsync("job/status", new System.Text.Json.Nodes.JsonObject());
        Assert.Equal("form1", await client.ObscureAsync("site-password"));

        await Assert.ThrowsAsync<RcloneCallException>(() => client.CallAsync("operations/list"));
        Assert.Equal("form2", await client.ObscureAsync("site-password"));

        fail = true;
        await client.CallAsync("job/status", new System.Text.Json.Nodes.JsonObject());
        Assert.Equal("form3", await client.ObscureAsync("site-password"));
        Assert.Equal(3, obscures);
    }

    private sealed class FakeHandler(Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> respond) : HttpMessageHandler
    {
        public FakeHandler(Func<HttpRequestMessage, HttpResponseMessage> respond)
            : this((request, _) => Task.FromResult(respond(request)))
        {
        }

        public List<Sent> Requests { get; } = [];

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var body = request.Content is null ? string.Empty : await request.Content.ReadAsStringAsync(cancellationToken);
            Requests.Add(new Sent(
                request.Method.Method,
                request.RequestUri!.PathAndQuery,
                request.Content?.Headers.ContentType?.MediaType,
                request.Headers.Authorization,
                body));
            return await respond(request, cancellationToken);
        }
    }

    private static HttpResponseMessage Reply(HttpStatusCode status, string body) =>
        new(status) { Content = new StringContent(body, Encoding.UTF8, "application/json") };
}

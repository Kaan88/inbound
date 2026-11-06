using System.Net;
using System.Net.Sockets;
using System.Text;
using static DownloadManager.Containers.Tests.ContainerFixtures;

namespace DownloadManager.Containers.Tests;

/// <summary>Every test here injects a handler: none contacts a real JDownloader, or opens a socket at all.</summary>
public class JDownloaderBridgeTests
{
    private static string WorkFile(string name, byte[] content)
    {
        var directory = Path.Combine(AppContext.BaseDirectory, "test-work", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        var path = Path.Combine(directory, name);
        File.WriteAllBytes(path, content);
        return path;
    }

    private static JDownloaderBridge Bridge(FakeJDownloader fake) => new(fake);

    [Fact]
    public async Task Constructing_the_bridge_contacts_nothing()
    {
        var fake = new FakeJDownloader();
        using var bridge = Bridge(fake);
        await Task.Yield();
        Assert.Empty(fake.Requests);
    }

    [Fact]
    public async Task Running_is_true_only_for_an_answer_from_jdownloader()
    {
        using var running = Bridge(new FakeJDownloader());
        Assert.True(await running.IsRunningAsync());

        using var other = Bridge(new FakeJDownloader { Alive = _ => Text("some other service") });
        Assert.False(await other.IsRunningAsync());

        using var error = Bridge(new FakeJDownloader { Alive = _ => new HttpResponseMessage(HttpStatusCode.InternalServerError) });
        Assert.False(await error.IsRunningAsync());
    }

    [Fact]
    public async Task Jd_not_running_is_detected_and_nothing_is_posted()
    {
        var fake = new FakeJDownloader { Alive = _ => throw new HttpRequestException("refused", new SocketException((int)SocketError.ConnectionRefused)) };
        using var bridge = Bridge(fake);

        Assert.False(await bridge.IsRunningAsync());

        var result = await bridge.SendLinksAsync([UrlA]);

        Assert.Equal(JDownloaderBridgeStatus.NotRunning, result.Status);
        Assert.DoesNotContain(fake.Requests, r => r.Method == HttpMethod.Post);
    }

    [Fact]
    public async Task A_port_that_accepts_but_never_answers_counts_as_not_running()
    {
        var fake = new FakeJDownloader { AliveHangs = true };
        using var bridge = new JDownloaderBridge(fake, probeTimeout: TimeSpan.FromMilliseconds(50));

        Assert.False(await bridge.IsRunningAsync());
        Assert.Equal(JDownloaderBridgeStatus.NotRunning, (await bridge.SendLinksAsync([UrlA])).Status);
    }

    [Fact]
    public async Task Another_service_on_the_port_is_not_sent_the_links()
    {
        var fake = new FakeJDownloader { Alive = _ => Text("hello") };
        using var bridge = Bridge(fake);

        var result = await bridge.SendLinksAsync([UrlA]);

        Assert.Equal(JDownloaderBridgeStatus.NotRunning, result.Status);
        Assert.DoesNotContain(fake.Requests, r => r.Method == HttpMethod.Post);
    }

    [Fact]
    public async Task Links_go_to_the_add_route_and_the_message_says_they_do_not_come_back()
    {
        var fake = new FakeJDownloader();
        using var bridge = Bridge(fake);

        var result = await bridge.SendLinksAsync([UrlA, UrlB], "My package", Password);

        Assert.Equal(JDownloaderBridgeStatus.Sent, result.Status);
        Assert.Contains("do not come back", result.Message, StringComparison.Ordinal);
        Assert.Contains("ask you to confirm", result.Message, StringComparison.Ordinal);

        var post = fake.Requests.Single(r => r.Method == HttpMethod.Post);
        Assert.Equal("/flash/add", post.Uri.AbsolutePath);
        Assert.Equal($"{UrlA}\r\n{UrlB}", post.Form["urls"]);
        Assert.Equal("My package", post.Form["package"]);
        Assert.Equal(Password, post.Form["passwords"]);
    }

    [Fact]
    public async Task A_dlc_file_goes_to_the_addcrypted_route_as_its_text()
    {
        var fake = new FakeJDownloader();
        using var bridge = Bridge(fake);
        var dlc = Dlc();
        var path = WorkFile("links.dlc", Encoding.ASCII.GetBytes(dlc + "\r\n"));

        var result = await bridge.SendFileAsync(path);

        Assert.Equal(JDownloaderBridgeStatus.Sent, result.Status);
        var post = fake.Requests.Single(r => r.Method == HttpMethod.Post);
        Assert.Equal("/flash/addcrypted", post.Uri.AbsolutePath);
        Assert.Equal(dlc, post.Form["crypted"]);
    }

    [Fact]
    public async Task A_ccf_file_goes_to_the_add_route_as_a_file_url()
    {
        var fake = new FakeJDownloader();
        using var bridge = Bridge(fake);
        var path = WorkFile("my links.CCF", CcfLegacy(CryptLoadXml()));

        var result = await bridge.SendFileAsync(path);

        Assert.Equal(JDownloaderBridgeStatus.Sent, result.Status);
        var post = fake.Requests.Single(r => r.Method == HttpMethod.Post);
        Assert.Equal("/flash/add", post.Uri.AbsolutePath);
        var url = post.Form["urls"];
        Assert.StartsWith("file:/", url, StringComparison.Ordinal);
        Assert.DoesNotContain("file:///", url, StringComparison.Ordinal);
        Assert.EndsWith("my%20links.CCF", url, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Jd_saying_failed_is_a_rejection()
    {
        var fake = new FakeJDownloader { Post = _ => Text("failed declined https://files.example.test/leak\r\n") };
        using var bridge = Bridge(fake);

        var result = await bridge.SendLinksAsync([UrlA]);

        Assert.Equal(JDownloaderBridgeStatus.Rejected, result.Status);
        Assert.DoesNotContain("example.test", result.Message, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(HttpStatusCode.Forbidden)]
    [InlineData(HttpStatusCode.InternalServerError)]
    [InlineData(HttpStatusCode.Found)]
    public async Task An_error_or_redirect_status_is_a_rejection_and_is_not_followed(HttpStatusCode status)
    {
        var fake = new FakeJDownloader
        {
            Post = _ =>
            {
                var response = new HttpResponseMessage(status);
                response.Headers.Location = new Uri("http://elsewhere.example.test/steal");
                return response;
            },
        };
        using var bridge = Bridge(fake);

        var result = await bridge.SendLinksAsync([UrlA]);

        Assert.Equal(JDownloaderBridgeStatus.Rejected, result.Status);
        Assert.All(fake.Requests, r => Assert.Equal("127.0.0.1", r.Uri.Host));
    }

    [Fact]
    public async Task An_empty_answer_is_not_treated_as_success()
    {
        using var bridge = Bridge(new FakeJDownloader { Post = _ => Text(string.Empty) });
        Assert.Equal(JDownloaderBridgeStatus.Rejected, (await bridge.SendLinksAsync([UrlA])).Status);
    }

    [Fact]
    public async Task No_answer_from_the_dialog_in_time_is_reported_as_possibly_still_open()
    {
        var fake = new FakeJDownloader { PostHangs = true };
        using var bridge = new JDownloaderBridge(fake, answerTimeout: TimeSpan.FromMilliseconds(50));

        var result = await bridge.SendLinksAsync([UrlA]);

        Assert.Equal(JDownloaderBridgeStatus.TimedOut, result.Status);
        Assert.Contains("may still be open", result.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_connection_lost_during_the_send_is_not_called_sent()
    {
        var fake = new FakeJDownloader { Post = _ => throw new HttpRequestException("reset") };
        using var bridge = Bridge(fake);

        var result = await bridge.SendLinksAsync([UrlA]);

        Assert.Equal(JDownloaderBridgeStatus.Failed, result.Status);
        Assert.DoesNotContain("reset", result.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task The_callers_cancellation_propagates()
    {
        var fake = new FakeJDownloader { PostHangs = true };
        using var bridge = Bridge(fake);
        using var cancellation = new CancellationTokenSource(TimeSpan.FromMilliseconds(50));

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => bridge.SendLinksAsync([UrlA], cancellationToken: cancellation.Token));
    }

    [Fact]
    public async Task Every_request_goes_to_loopback_on_port_9666_and_never_to_3128()
    {
        var fake = new FakeJDownloader();
        using var bridge = Bridge(fake);

        await bridge.IsRunningAsync();
        await bridge.SendLinksAsync([UrlA]);
        await bridge.SendFileAsync(WorkFile("a.dlc", Encoding.ASCII.GetBytes(Dlc())));
        await bridge.SendFileAsync(WorkFile("a.ccf", new byte[16]));

        Assert.True(fake.Requests.Count >= 7);
        Assert.All(fake.Requests, request =>
        {
            Assert.Equal("http", request.Uri.Scheme);
            Assert.Equal("127.0.0.1", request.Uri.Host);
            Assert.True(IPAddress.IsLoopback(IPAddress.Parse(request.Uri.Host)));
            Assert.Equal(9666, request.Uri.Port);
        });
    }

    [Fact]
    public void The_public_surface_has_no_way_to_choose_a_host_or_port()
    {
        var constructor = Assert.Single(typeof(JDownloaderBridge).GetConstructors());
        Assert.DoesNotContain(constructor.GetParameters(), p => p.ParameterType == typeof(string) || p.ParameterType == typeof(Uri) || p.ParameterType == typeof(int));
        Assert.DoesNotContain(typeof(JDownloaderBridge).GetProperties(), p => p.CanWrite);
    }

    [Fact]
    public void The_default_handler_uses_no_proxy_and_follows_no_redirect()
    {
        using var handler = JDownloaderBridge.CreateDefaultHandler();

        Assert.False(handler.UseProxy);
        Assert.False(handler.AllowAutoRedirect);
        Assert.False(handler.UseCookies);
    }

    [Fact]
    public async Task Requests_do_not_use_jdownloaders_skip_the_dialog_routes()
    {
        var fake = new FakeJDownloader();
        using var bridge = Bridge(fake);

        await bridge.SendLinksAsync([UrlA]);

        var post = fake.Requests.Single(r => r.Method == HttpMethod.Post);
        Assert.Null(post.Referrer);
        Assert.False(post.HasRandomNumberHeader);
        Assert.False(post.HasOrigin);
        Assert.Equal("Inbound Download Manager", post.UserAgent);
        Assert.False(post.Form.ContainsKey("source"));
    }

    [Theory]
    [InlineData("")]
    [InlineData("relative.dlc")]
    [InlineData(@"\\server\share\links.dlc")]
    [InlineData("//server/share/links.ccf")]
    [InlineData(@"\\.\C:\links.dlc")]
    public async Task Paths_that_are_not_full_local_paths_are_refused(string path)
    {
        var fake = new FakeJDownloader();
        using var bridge = Bridge(fake);

        var result = await bridge.SendFileAsync(path);

        Assert.Equal(JDownloaderBridgeStatus.InvalidInput, result.Status);
        Assert.Empty(fake.Requests);
    }

    [Fact]
    public async Task Other_file_types_missing_files_and_non_dlc_content_are_refused()
    {
        var fake = new FakeJDownloader();
        using var bridge = Bridge(fake);

        Assert.Equal(JDownloaderBridgeStatus.InvalidInput, (await bridge.SendFileAsync(WorkFile("links.rsdf", new byte[1]))).Status);

        var missing = Path.Combine(Path.GetDirectoryName(WorkFile("x.txt", new byte[1]))!, "missing.dlc");
        Assert.Equal(JDownloaderBridgeStatus.InvalidInput, (await bridge.SendFileAsync(missing)).Status);

        var notDlc = await bridge.SendFileAsync(WorkFile("links.dlc", "<html>nope</html>"u8.ToArray()));
        Assert.Equal(JDownloaderBridgeStatus.InvalidInput, notDlc.Status);

        Assert.Empty(fake.Requests);
    }

    [Theory]
    [InlineData("https://files.example.test/a\r\nfile:/C:/secret.dlc")]
    [InlineData("https://files.example.test/a file:/C:/secret.dlc")]
    [InlineData("https://files.example.test/a\tb")]
    [InlineData("file:///C:/Windows/win.ini")]
    [InlineData("not a url")]
    [InlineData("")]
    public async Task Links_that_could_smuggle_or_read_local_files_are_refused(string url)
    {
        var fake = new FakeJDownloader();
        using var bridge = Bridge(fake);

        var result = await bridge.SendLinksAsync([UrlA, url]);

        Assert.Equal(JDownloaderBridgeStatus.InvalidInput, result.Status);
        Assert.DoesNotContain("secret", result.Message, StringComparison.Ordinal);
        Assert.Empty(fake.Requests);
    }

    [Fact]
    public async Task No_links_and_control_characters_in_metadata_are_refused()
    {
        var fake = new FakeJDownloader();
        using var bridge = Bridge(fake);

        Assert.Equal(JDownloaderBridgeStatus.InvalidInput, (await bridge.SendLinksAsync([])).Status);
        Assert.Equal(JDownloaderBridgeStatus.InvalidInput, (await bridge.SendLinksAsync([UrlA], "a\nb")).Status);
        Assert.Equal(JDownloaderBridgeStatus.InvalidInput, (await bridge.SendLinksAsync([UrlA], null, "a\nb")).Status);
        Assert.Empty(fake.Requests);
    }

    private static HttpResponseMessage Text(string body) => new(HttpStatusCode.OK) { Content = new StringContent(body) };

    private sealed record RecordedRequest(
        HttpMethod Method,
        Uri Uri,
        Dictionary<string, string> Form,
        Uri? Referrer,
        bool HasRandomNumberHeader,
        bool HasOrigin,
        string UserAgent);

    private sealed class FakeJDownloader : HttpMessageHandler
    {
        public List<RecordedRequest> Requests { get; } = [];
        public Func<HttpRequestMessage, HttpResponseMessage> Alive { get; init; } = _ => Text("JDownloader\r\n");
        public Func<HttpRequestMessage, HttpResponseMessage> Post { get; init; } = _ => Text("success\r\n");
        public bool AliveHangs { get; init; }
        public bool PostHangs { get; init; }

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var form = new Dictionary<string, string>();
            if (request.Content is not null)
            {
                foreach (var pair in (await request.Content.ReadAsStringAsync(cancellationToken)).Split('&'))
                {
                    var parts = pair.Split('=', 2);
                    form[WebUtility.UrlDecode(parts[0])] = WebUtility.UrlDecode(parts[1]);
                }
            }

            Requests.Add(new RecordedRequest(
                request.Method,
                request.RequestUri!,
                form,
                request.Headers.Referrer,
                request.Headers.Contains("jd.randomnumber"),
                request.Headers.Contains("Origin"),
                string.Join(' ', request.Headers.GetValues("User-Agent"))));

            var isPost = request.Method == HttpMethod.Post;
            if (isPost ? PostHangs : AliveHangs)
            {
                await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
            }

            return isPost ? Post(request) : Alive(request);
        }
    }
}

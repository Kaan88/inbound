using DownloadManager.Contracts;
using DownloadManager.Mobile;
using DownloadManager.Mobile.Remote;

namespace DownloadManager.Mobile.Tests;

public sealed class FailureWordsTests
{
    private static JobDto Failed(string kind, int? status = null, string message = "", string? note = null) => new()
    {
        State = "failed",
        StatusNote = note,
        Failure = new FailureDto { Kind = kind, HttpStatus = status, Message = message },
    };

    [Theory]
    [InlineData("forbidden", 403, "server refused (403)")]
    [InlineData("http", 403, "server refused (403)")]
    [InlineData("http", 404, "not found on the server (404)")]
    [InlineData("http", 503, "server error (503)")]
    [InlineData("http", 418, "server answered 418")]
    [InlineData("notFound", null, "not found on the server")]
    [InlineData("dns", null, "address not found")]
    [InlineData("diskFull", null, "no space left")]
    [InlineData("timeout", null, "timed out")]
    [InlineData("expiredUrl", null, "link expired")]
    public void Kinds_and_status_codes_become_short_words(string kind, int? status, string expected) =>
        Assert.Equal(expected, FailureWords.Short(Failed(kind, status, "ignored")));

    [Fact]
    public void Without_a_known_kind_the_first_clause_of_the_message_is_used_and_cut_short()
    {
        var job = Failed("unknown", null, "The server closed the connection before the file was complete. Try again later.");
        Assert.True(FailureWords.Short(job)!.Length <= 40);
        Assert.StartsWith("The server closed the connection", FailureWords.Short(job));
        Assert.Equal("Disk is read-only", FailureWords.Short(Failed("unknown", null, "Disk is read-only: cannot write.")));
    }

    [Fact]
    public void A_resource_key_is_never_shown()
    {
        var job = Failed("unknown", null, "net_http_client_execution_error");
        Assert.Null(FailureWords.Short(job));
        Assert.Null(FailureWords.Full(job));
    }

    [Fact]
    public void The_note_is_the_fallback_and_the_full_text_stays_whole()
    {
        var job = new JobDto { State = "failed", StatusNote = "Server answered 404: the page is gone." };
        Assert.Equal("Server answered 404", FailureWords.Short(job));
        Assert.Equal("Server answered 404: the page is gone.", FailureWords.Full(job));
    }

    [Fact]
    public void The_row_says_failed_with_the_short_reason_and_details_the_whole_message()
    {
        var row = Jobs.Row(Failed("forbidden", 403, "HTTP 403 Forbidden from cdn.example.org"));
        Assert.Equal("Failed · server refused (403)", row.Subtitle);
        Assert.Equal("HTTP 403 Forbidden from cdn.example.org", DetailsInfo.ProgressLine(row, DateTimeOffset.Now));
    }
}

public sealed class FolderPickerTests
{
    private static (FakeHostConnection Host, DownloadsModel Model) Setup(bool offered)
    {
        var host = new FakeHostConnection();
        var model = new DownloadsModel(host, a => a(), TimeSpan.Zero);
        model.StartAsync(CancellationToken.None).GetAwaiter().GetResult();
        host.Push(IpcMessage.Create(MessageTypes.ServerHello, new HostHelloDto { Capabilities = offered ? ["folders"] : [] }));
        return (host, model);
    }

    private static FolderBrowseResult Listing(string? path, string? parent, params string[] names) => new()
    {
        Ok = true, Path = path, Parent = parent, Roots = ["/data"],
        Folders = names.Select(n => new FolderEntryDto { Name = n, Path = (path ?? "") + "/" + n }).ToList(),
    };

    [Fact]
    public void Browsing_is_offered_only_when_the_hello_lists_it()
    {
        Assert.True(FolderPicker.Offered(Setup(true).Model));
        Assert.False(FolderPicker.Offered(Setup(false).Model));
    }

    [Fact]
    public async Task It_walks_down_and_up_and_the_roots_cannot_be_chosen()
    {
        var (host, model) = Setup(true);
        host.Reply = m => IpcMessage.Create(MessageTypes.FoldersBrowseResult, m.GetPayload<FolderBrowseRequest>().Path switch
        {
            null or "" => Listing(null, null, "data"),
            "/data" => Listing("/data", null, "movies"),
            var p => Listing(p, "/data"),
        }, m.Id);
        var picker = new FolderPicker(model);

        await picker.OpenAsync(null);
        Assert.False(picker.CanChoose);
        await picker.OpenAsync("/data");
        Assert.True(picker.CanChoose);
        Assert.Equal("/data/movies", picker.Folders.Single().Path);
        await picker.OpenAsync("/data/movies");
        Assert.Equal("/data", picker.Parent);
        await picker.UpAsync();
        Assert.Equal("/data", picker.Path);
        await picker.UpAsync();
        Assert.False(picker.CanChoose);
    }

    [Fact]
    public async Task A_start_folder_the_server_refuses_falls_back_to_the_roots()
    {
        var (host, model) = Setup(true);
        host.Reply = m => IpcMessage.Create(MessageTypes.FoldersBrowseResult,
            string.IsNullOrEmpty(m.GetPayload<FolderBrowseRequest>().Path) ? Listing(null, null, "data") : new FolderBrowseResult { Error = "Outside the folders." }, m.Id);
        var picker = new FolderPicker(model);
        await picker.OpenAsync("/home/elsewhere");
        Assert.Null(picker.Error);
        Assert.Null(picker.Path);
        Assert.Equal("data", picker.Folders.Single().Name);
    }

    [Fact]
    public async Task A_dismissed_picker_neither_falls_back_nor_applies_a_listing()
    {
        var (host, model) = Setup(true);
        using var dismissed = new CancellationTokenSource();
        var requests = 0;
        host.Reply = m =>
        {
            ++requests;
            dismissed.Cancel();
            return IpcMessage.Create(MessageTypes.FoldersBrowseResult,
                new FolderBrowseResult { Error = "Outside the folders." }, m.Id);
        };
        var picker = new FolderPicker(model);
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => picker.OpenAsync("/gone", dismissed.Token));
        Assert.Equal(1, requests);
        Assert.False(picker.CanChoose);
        Assert.Null(picker.Error);
    }

    [Fact]
    public async Task A_refusal_is_said_in_words()
    {
        var (host, model) = Setup(true);
        host.Reply = m => IpcMessage.Create(MessageTypes.FoldersBrowseResult, new FolderBrowseResult { Error = "Folder browsing is not available here." }, m.Id);
        var picker = new FolderPicker(model);
        await picker.OpenAsync(null);
        Assert.Equal("Folder browsing is not available here.", picker.Error);
    }

    [Fact]
    public void The_chosen_folder_is_sent_with_the_download_and_read_aloud_in_full()
    {
        var host = new FakeHostConnection();
        var model = new DownloadsModel(host, a => a(), TimeSpan.Zero);
        model.StartAsync(CancellationToken.None).GetAwaiter().GetResult();
        host.Snapshot("/data/default");
        var add = new NewDownloadModel(model) { Address = "http://example.test/a.bin" };
        Assert.Null(add.BuildRequests().Single().DestinationDirectory);
        add.Destination = "/data/movies/long/folder/name";
        Assert.Equal("/data/movies/long/folder/name", add.SaveTo);
        Assert.Equal("/data/movies/long/folder/name", add.BuildRequests().Single().DestinationDirectory);
        Assert.Equal("Save to /data/x on Home NAS", SavePathWords.Label("Home NAS", "/data/x"));
        Assert.Equal("Download", SavePathWords.ForField("/storage/emulated/0/Download"));
    }
}

public sealed class HostNamingTests
{
    [Theory]
    [InlineData("Desk", null, "NAS", "10.0.0.5", "Desk")]
    [InlineData(" ", null, "Home NAS", "10.0.0.5", "Home NAS")]
    [InlineData(null, null, null, "10.0.0.5", "10.0.0.5")]
    [InlineData(null, "10.0.0.5", "Home NAS", "10.0.0.5", "Home NAS")]
    [InlineData(null, "Old name", "Home NAS", "10.0.0.5", "Old name")]
    public void The_name_typed_then_the_server_s_own_then_the_address(string? typed, string? earlier, string? server, string host, string expected) =>
        Assert.Equal(expected, HostNaming.Choose(typed, earlier, server, host));

    [Fact]
    public void The_notification_summary_counts_the_finished_files()
    {
        Assert.Equal("1 download finished", NotificationText.FinishedSummary(1));
        Assert.Equal("3 downloads finished", NotificationText.FinishedSummary(3));
    }
}

public sealed class MiddleEllipsisTests
{
    [Fact]
    public void A_text_that_fits_is_left_alone() => Assert.Equal("/data/a", MiddleEllipsis.Cut("/data/a", s => s.Length <= 10));

    [Fact]
    public void A_long_path_keeps_its_start_and_its_end()
    {
        var cut = MiddleEllipsis.Cut("C:/Users/kaank/Downloads/idm-android-trial/artifacts/roots/Movies", s => s.Length <= 20);
        Assert.Equal(20, cut.Length);
        Assert.StartsWith("C:/Users", cut);
        Assert.EndsWith("Movies", cut);
        Assert.Contains("…", cut);
    }

    [Fact]
    public void Nothing_fitting_gives_just_the_mark() => Assert.Equal("…", MiddleEllipsis.Cut("abcdef", s => s.Length <= 1));
}

using DownloadManager.Contracts;
using DownloadManager.Desktop.WinUI;

namespace DownloadManager.Core.Tests;

/// <summary>SITES.11 R4: the words and choices of All remotes, Add a remote and the sidebar entries.</summary>
public sealed class SitesHomeRulesTests
{
    private static readonly DateTimeOffset Now = new(new DateTime(2026, 11, 17, 12, 0, 0, DateTimeKind.Local));

    private static DateTimeOffset Local(int month, int day, int hour, int minute, int year = 2026)
        => new(new DateTime(year, month, day, hour, minute, 0, DateTimeKind.Local));

    [Fact]
    public void LastUsed_reads_today_yesterday_and_dates()
    {
        Assert.Equal("Today 09:14", SitesHomeRules.LastUsedText(Local(11, 17, 9, 14), Now));
        Assert.Equal("Yesterday 22:15", SitesHomeRules.LastUsedText(Local(11, 16, 22, 15), Now));
        Assert.Equal("Nov 10", SitesHomeRules.LastUsedText(Local(11, 10, 8, 0), Now));
        Assert.Equal("Oct 30, 2025", SitesHomeRules.LastUsedText(Local(10, 30, 8, 0, 2025), Now));
        Assert.Equal("Never", SitesHomeRules.LastUsedText(null, Now));
    }

    private static SiteTransferDto Transfer(string site, string direction, int running, string state = "running", long bytes = 100, long done = 50,
        int filesDone = 0, DateTimeOffset? retryAt = null)
        => new()
        {
            Id = Guid.NewGuid().ToString("N"), SiteId = site, Direction = direction, State = state, FilesRunning = running, Bytes = bytes, BytesDone = done,
            FilesDone = filesDone, RetryAt = retryAt,
        };

    [Fact]
    public void Badge_counts_arrows_waiting_and_progress_for_one_remote()
    {
        List<SiteTransferDto> transfers =
        [
            Transfer("a", "download", 1), Transfer("a", "upload", 1, bytes: 300, done: 250),
            Transfer("a", "download", 0, done: 0), Transfer("b", "download", 4), Transfer("a", "download", 2, state: "paused"),
        ];
        var badge = SitesHomeRules.Badge(transfers, "a");
        Assert.Equal((1, 1, 1, 2), (badge.Down, badge.Up, badge.Waiting, badge.Running));
        Assert.Equal("↓1 ↑1", SitesHomeRules.ArrowsText(badge));
        Assert.Equal("1", SitesHomeRules.WaitingCount(badge));
        Assert.Equal("2 transfers running", SitesHomeRules.ActivityText(badge));
        Assert.Equal("1 downloading, 1 uploading, 1 waiting", SitesHomeRules.ActivityWords(badge));
        // The bytes of the batches that move; the waiting one has not started.
        Assert.Equal(300.0 / 400 * 100, badge.Progress!.Value, 3);
    }

    [Fact]
    public void Waiting_follows_the_running_tabs_started_rule_and_retry()
    {
        var retry = DateTimeOffset.Now.AddSeconds(30);
        List<SiteTransferDto> transfers =
        [
            // Between files: started, not waiting.
            Transfer("a", "download", 0, done: 0, filesDone: 2),
            // Bytes moved, waiting to try its server again: waiting.
            Transfer("a", "download", 0, done: 40, retryAt: retry),
            // Not started at all: waiting.
            Transfer("a", "upload", 0, done: 0),
        ];
        var badge = SitesHomeRules.Badge(transfers, "a");
        Assert.Equal((2, 1), (badge.Waiting, badge.Running));
        Assert.Equal("1 transfer running", SitesHomeRules.ActivityText(badge));
        Assert.Equal(transfers.Select(SiteTransferRules.IsStarted), [true, false, false]);
    }

    [Fact]
    public void The_line_shows_while_batches_move_even_before_sizes_are_known()
    {
        var bySize = new SiteTransferDto { SiteId = "a", Direction = "download", State = "running", FilesRunning = 1, Files = 4, FilesDone = 1 };
        Assert.Equal(25.0, SitesHomeRules.Badge([bySize], "a").Progress!.Value, 3);
        var nothingYet = new SiteTransferDto { SiteId = "a", Direction = "download", State = "running", FilesRunning = 1 };
        Assert.Equal(0.0, SitesHomeRules.Badge([nothingYet], "a").Progress);
        // Only waiting: no line.
        Assert.Null(SitesHomeRules.Badge([Transfer("a", "download", 0, done: 0)], "a").Progress);
    }

    [Fact]
    public void Transfers_running_counts_batches_not_files()
    {
        var badge = SitesHomeRules.Badge([Transfer("a", "download", 4), Transfer("a", "download", 3)], "a");
        Assert.Equal(7, badge.Down);
        Assert.Equal("2 transfers running", SitesHomeRules.ActivityText(badge));
        Assert.Equal("1 transfer running", SitesHomeRules.ActivityText(SitesHomeRules.Badge([Transfer("a", "upload", 4)], "a")));
    }

    [Fact]
    public void Idle_remote_has_no_badge_and_no_activity()
    {
        var badge = SitesHomeRules.Badge([], "a");
        Assert.Equal("", SitesHomeRules.ArrowsText(badge));
        Assert.Equal("", SitesHomeRules.WaitingCount(badge));
        Assert.Null(SitesHomeRules.ActivityText(badge));
        Assert.Null(SitesHomeRules.ActivityWords(badge));
        Assert.Null(badge.Progress);
    }

    [Fact]
    public void Waiting_only_says_waiting_and_one_file_is_singular()
    {
        Assert.Equal("1 waiting", SitesHomeRules.ActivityText(SitesHomeRules.Badge([Transfer("a", "upload", 0, done: 0)], "a")));
        Assert.Equal("1 transfer running", SitesHomeRules.ActivityText(SitesHomeRules.Badge([Transfer("a", "upload", 1)], "a")));
    }

    [Theory]
    [InlineData(0, "No remotes yet")]
    [InlineData(1, "1 remote")]
    [InlineData(5, "5 remotes")]
    public void Footer_counts_remotes(int count, string expected) => Assert.Equal(expected, SitesHomeRules.FooterText(count));

    [Fact]
    public void Tiles_map_to_kinds_and_leave_out_what_the_Host_lacks()
    {
        Assert.Equal(["SFTP", "FTP", "WebDAV", "Google Drive", "OneDrive", "Dropbox", "S3 or B2"], SitesHomeRules.Tiles.Select(t => t.Name));
        Assert.Equal(["s3", "b2"], SitesHomeRules.TileOf(SiteKindCatalog.B2)!.Kinds);
        Assert.Equal("Add an SFTP remote", SitesHomeRules.TileOf(SiteKindCatalog.Sftp)!.FormTitle);
        Assert.Equal("Add an S3 or B2 remote", SitesHomeRules.TileOf(SiteKindCatalog.S3)!.FormTitle);
        Assert.DoesNotContain(SitesHomeRules.Tiles, t => t.Name.StartsWith("More", StringComparison.Ordinal));

        var all = SitesHomeRules.TilesFor(SiteKindCatalog.All);
        Assert.Equal(SitesHomeRules.Tiles.Count, all.Count);
        var onlyFtp = SitesHomeRules.TilesFor(SiteKindCatalog.All.Where(k => k.Kind == SiteKindCatalog.Ftp));
        Assert.Equal(["FTP"], onlyFtp.Select(t => t.Name));
        Assert.Equal(["SFTP"], SitesHomeRules.TilesFor([]).Select(t => t.Name));
    }

    [Theory]
    [InlineData("sftp", "nas.home.lan", null, null, "nas.home.lan")]
    [InlineData("webdav", "", "https://cloud.example.net/dav", null, "cloud.example.net")]
    [InlineData("s3", "", null, "https://s3.example.com", "s3.example.com")]
    [InlineData("drive", "", null, null, "Google Drive")]
    [InlineData("b2", "", null, null, "S3 or B2")]
    public void Default_name_is_the_server_or_the_service(string kind, string host, string? url, string? endpoint, string expected)
        => Assert.Equal(expected, SitesHomeRules.DefaultName(kind, host, url, endpoint));

    [Fact]
    public void Dot_warns_for_trouble_missing_key_and_missing_sign_in()
    {
        var ok = new SiteDto { Kind = SiteKindCatalog.Ftp, Reach = SiteReach.Ok };
        Assert.False(SitesHomeRules.InTrouble(ok));
        Assert.True(SitesHomeRules.InTrouble(new SiteDto { Kind = SiteKindCatalog.Ftp, Reach = SiteReach.Trouble }));
        Assert.True(SitesHomeRules.InTrouble(new SiteDto { Kind = SiteKindCatalog.Sftp }));
        Assert.True(SitesHomeRules.InTrouble(new SiteDto { Kind = SiteKindCatalog.Drive, SignedIn = false }));
        Assert.False(SitesHomeRules.InTrouble(new SiteDto { Kind = SiteKindCatalog.Drive, SignedIn = true }));
    }

    // ---- The form's request and Test (W11) ---------------------------------------------------------------------

    [Fact]
    public void Form_name_is_as_typed_or_the_servers()
    {
        Assert.Equal("My NAS", SitesHomeRules.FormName("My NAS", "sftp", "nas.home.lan", null, null));
        Assert.Equal("nas.home.lan", SitesHomeRules.FormName("   ", "sftp", " nas.home.lan ", null, null));
        Assert.Equal("Dropbox", SitesHomeRules.FormName("", "dropbox", "", null, null));
    }

    [Theory]
    [InlineData("  C:\\Users\\me\\.ssh\\id_ed25519 ", "C:\\Users\\me\\.ssh\\id_ed25519")]
    [InlineData("\"C:\\keys\\my key\"", "C:\\keys\\my key")]
    public void Key_file_path_drops_spaces_and_explorers_quotes(string typed, string expected) => Assert.Equal(expected, SitesHomeRules.KeyFilePath(typed));

    [Fact]
    public void Key_file_is_refused_when_missing_or_too_big()
    {
        Assert.Equal("The key file wasn't found.", SitesHomeRules.KeyFileProblem(exists: false, 0));
        Assert.Equal("That file is too big to be a private key.", SitesHomeRules.KeyFileProblem(exists: true, SitesRules.MostKeyBytes + 1));
        Assert.Null(SitesHomeRules.KeyFileProblem(exists: true, 411));
    }

    [Fact]
    public void An_edit_left_empty_keeps_its_saved_secrets_and_names_the_remote()
    {
        var existing = new SiteDto { Id = "0123456789abcdef0123456789abcdef", Kind = "sftp", HasPassword = true, HasPrivateKey = true, AuthMethod = "key" };
        var values = new SiteFormValues("sftp", SitesHomeRules.FormName("", "sftp", "nas.home.lan", null, null), "nas.home.lan", "22", "me", "key", "", "", "", false, "/", "");
        var request = SitesRules.Request(values, existing, out var problem);
        Assert.Null(problem);
        Assert.Equal((existing.Id, "nas.home.lan"), (request!.Id, request.Name));
        // Null is "keep the saved one"; the Host keeps it only for the same server, port and user.
        Assert.Null(request.PrivateKey);
        Assert.Null(request.KeyPassphrase);
        Assert.Null(request.Password);
    }

    [Fact]
    public void Trust_accepted_in_the_form_is_dropped_when_the_form_points_elsewhere()
    {
        var then = ("nas.home.lan", "22", "", "", (string?)null);
        Assert.True(SitesHomeRules.SameServer(then, ("NAS.home.lan ", " 22", "", "", null)));
        Assert.False(SitesHomeRules.SameServer(then, ("other.lan", "22", "", "", null)));
        Assert.False(SitesHomeRules.SameServer(then, ("nas.home.lan", "2222", "", "", null)));
        Assert.False(SitesHomeRules.SameServer(("h", "21", "", "", "explicit"), ("h", "21", "", "", "implicit")));
        Assert.False(SitesHomeRules.SameServer(("", "", "https://a/dav", "", null), ("", "", "https://b/dav", "", null)));
    }

    [Fact]
    public void Draft_test_words_follow_the_hosts_answer()
    {
        Assert.Equal("It works. Connected and listed the start folder (6 items) in 0.4 s.",
            SitesHomeRules.DraftTestText(new SiteResultDto { Ok = true, ItemCount = 6 }, false, TimeSpan.FromSeconds(0.4)));
        Assert.Contains("(1 item)", SitesHomeRules.DraftTestText(new SiteResultDto { Ok = true, ItemCount = 1 }, false, TimeSpan.Zero));
        Assert.Contains("press Trust", SitesHomeRules.DraftTestText(new SiteResultDto
        {
            Problem = SiteProblems.HostKeyNotAccepted, OfferedKeys = [new SiteHostKeyDto { Fingerprint = "SHA256:x" }],
        }, false, TimeSpan.Zero));
        Assert.Contains("trusted when you save", SitesHomeRules.DraftTestText(new SiteResultDto
        {
            Problem = SiteProblems.CertificateNotTrusted, Certificate = new SiteCertificateDto { Fingerprint = "AA" },
        }, false, TimeSpan.Zero));
        Assert.Contains("can't be used. Expired.", SitesHomeRules.DraftTestText(new SiteResultDto
        {
            Problem = SiteProblems.CertificateNotTrusted, Certificate = new SiteCertificateDto { CannotTrust = "Expired." },
        }, false, TimeSpan.Zero));
        Assert.Contains("browser opens", SitesHomeRules.DraftTestText(new SiteResultDto { Problem = SiteProblems.SignInNeeded }, true, TimeSpan.Zero));
        Assert.Equal("Permission denied.", SitesHomeRules.DraftTestText(new SiteResultDto { Message = "Permission denied." }, false, TimeSpan.Zero));
    }

    [Fact]
    public void An_older_host_is_told_by_its_answer()
    {
        const string doesNotOffer = "doesn't offer";
        Assert.True(SitesHomeRules.HostLacks(null, doesNotOffer, doesNotOffer));
        Assert.True(SitesHomeRules.HostLacks("Unknown Sites request sites.testDraft.", null, doesNotOffer));
        Assert.False(SitesHomeRules.HostLacks("Connected. The folder holds 2 items.", null, doesNotOffer));
        Assert.False(SitesHomeRules.HostLacks(null, "The Host didn't answer.", doesNotOffer));
    }

    // ---- Open once (W2) ---------------------------------------------------------------------------------------

    private static SiteDto Once(string id, bool unsaved = true) => new() { Id = id, Unsaved = unsaved };

    [Fact]
    public void Opening_another_address_forgets_the_oldest_not_shown()
    {
        var open = Enumerable.Range(1, 8).Select(i => Once($"s{i}")).ToList();
        var none = new HashSet<string>();
        Assert.Equal(["s1"], SitesHomeRules.ForgetBeforeOpening(open, shownId: null, none));
        // The oldest is shown: the next oldest goes instead.
        Assert.Equal(["s2"], SitesHomeRules.ForgetBeforeOpening(open, shownId: "s1", none));
        // Under the limit, or saved remotes among them: nothing goes.
        Assert.Empty(SitesHomeRules.ForgetBeforeOpening(open.Take(7).ToList(), null, none)!);
        Assert.Empty(SitesHomeRules.ForgetBeforeOpening([.. open.Take(7), Once("saved", unsaved: false)], null, none)!);
        // Over the limit (opened elsewhere too): as many as needed.
        Assert.Equal(["s1", "s2", "s3"], SitesHomeRules.ForgetBeforeOpening([.. open, Once("s9"), Once("s10")], null, none));
    }

    [Fact]
    public void An_address_with_unfinished_batches_is_never_forgotten()
    {
        var open = Enumerable.Range(1, 8).Select(i => Once($"s{i}")).ToList();
        List<SiteTransferDto> transfers =
        [
            Transfer("s1", "download", 1), Transfer("s2", "upload", 0, state: "paused"), Transfer("s3", "download", 0, state: "done"),
        ];
        var busy = SitesHomeRules.BusyRemotes(transfers);
        Assert.Equal(["s1", "s2"], busy.Order());
        // s1 runs and s2 is paused: s3 (finished batches only) goes.
        Assert.Equal(["s3"], SitesHomeRules.ForgetBeforeOpening(open, null, busy));
        // All in use, or the only free one shown: nothing can go, and the address is not opened.
        var allBusy = open.Select(site => site.Id).ToHashSet();
        Assert.Null(SitesHomeRules.ForgetBeforeOpening(open, null, allBusy));
        Assert.Null(SitesHomeRules.ForgetBeforeOpening(open, "s8", open.Take(7).Select(site => site.Id).ToHashSet()));
        Assert.Equal("Close an opened address first.", SitesHomeRules.CloseOneFirst);
    }

    [Fact]
    public void Only_an_unsaved_remote_asks_for_its_password_and_it_is_remembered_not_edited()
    {
        Assert.Equal(OnceStep.AskPassword, SitesHomeRules.AfterBrowse(SiteProblems.PasswordNeeded, unsaved: true));
        Assert.Equal(OnceStep.Done, SitesHomeRules.AfterBrowse(SiteProblems.PasswordNeeded, unsaved: false));
        Assert.Equal(OnceStep.Done, SitesHomeRules.AfterBrowse(SiteProblems.HostKeyNotAccepted, unsaved: true));
        Assert.Equal(OnceStep.Done, SitesHomeRules.AfterBrowse(null, unsaved: true));
        Assert.False(SitesHomeRules.CanEdit(Once("a")));
        Assert.True(SitesHomeRules.CanEdit(Once("a", unsaved: false)));
        Assert.False(SitesHomeRules.CanForgetCertificate(new SiteDto()));
        Assert.True(SitesHomeRules.CanForgetCertificate(new SiteDto { TrustedCertificates = [new SiteTrustedCertificateDto()] }));
    }

    [Fact]
    public void Save_tests_first_unless_it_was_tested_cannot_or_asked_to_save_anyway()
    {
        Assert.True(SitesHomeRules.NeedsTestBeforeSave(false, true, false, false));
        Assert.False(SitesHomeRules.NeedsTestBeforeSave(true, true, false, false));
        Assert.False(SitesHomeRules.NeedsTestBeforeSave(false, false, false, false));
        Assert.False(SitesHomeRules.NeedsTestBeforeSave(false, true, true, false));
        Assert.False(SitesHomeRules.NeedsTestBeforeSave(false, true, false, true));
        Assert.Equal("Save anyway", SitesHomeRules.SaveAnyway);
    }

    [Fact]
    public void Test_line_and_labels()
    {
        Assert.Equal("It works. Signed in and listed the start folder in 0.4 s.", SitesHomeRules.TestWorks(true, TimeSpan.FromSeconds(0.4)));
        Assert.Equal("Key file", SitesHomeRules.MethodLabel("key"));
        Assert.Equal("Password", SitesHomeRules.MethodLabel("password"));
        Assert.Equal("SFTP", SitesHomeRules.KindText("sftp"));
        Assert.Equal("Drive", SitesHomeRules.KindText("drive"));
    }
}

using DownloadManager.Contracts;
using DownloadManager.Desktop.WinUI;

namespace DownloadManager.Core.Tests;

public sealed class ArchiveRowsTests
{
    private static ArchiveSetDto Set(string state, string? failure = null, string stateText = "words", Action<ArchiveSetDto>? change = null)
    {
        var set = new ArchiveSetDto
        {
            Id = Guid.NewGuid(),
            Name = "movie",
            State = state,
            StateText = stateText,
            FailureKind = failure,
            NeedsPassword = failure is "passwordRequired" or "wrongPassword",
            UpdatedAt = DateTimeOffset.UnixEpoch,
        };
        change?.Invoke(set);
        return set;
    }

    private static ArchiveSelectedFile File(string name, ArchiveSetDto? set = null)
        => new(Guid.NewGuid(), name, $@"C:\files\{name}", set);

    private static readonly Guid PackageId = Guid.NewGuid();

    // ---- Words ------------------------------------------------------------------------------------

    [Theory]
    [InlineData("passwordRequired", "Password required")]
    [InlineData("wrongPassword", "Wrong password")]
    [InlineData("missingVolume", "Missing volume")]
    [InlineData("corrupted", "Corrupted archive")]
    [InlineData("unsupportedVersion", "Unsupported archive version")]
    [InlineData("toolUnavailable", "7-Zip missing")]
    [InlineData("diskSpace", "Not enough disk space")]
    [InlineData("permission", "Permission refused")]
    [InlineData("unsafePath", "Unsafe archive")]
    [InlineData("cancelled", "Extraction cancelled")]
    [InlineData("unknown", "Extraction failed")]
    public void Every_failure_has_its_own_words(string kind, string words)
        => Assert.Equal(words, ArchiveText.Set(Set("failed", kind)).Text);

    [Theory]
    [InlineData("passwordRequired")]
    [InlineData("wrongPassword")]
    [InlineData("corrupted")]
    [InlineData("toolUnavailable")]
    [InlineData("unknown")]
    public void A_failure_never_reads_as_a_download_failure(string kind)
    {
        var view = ArchiveText.Set(Set("failed", kind));
        Assert.DoesNotContain("download failed", view.Text, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("downloaded files are fine", view.ToolTip, StringComparison.Ordinal);
        Assert.StartsWith("Extraction:", view.AccessibleText, StringComparison.Ordinal);
    }

    [Fact]
    public void A_last_volume_named_by_the_tool_reads_like_any_other_part_being_waited_for()
    {
        var stopped = Set("failed", "missingVolume", change: s => s.MissingVolumes = ["a.part3.rar"]);
        var inTheMiddle = Set("waiting", change: s => s.MissingVolumes = ["a.part2.rar"]);

        var view = ArchiveText.Set(stopped);
        Assert.Equal("Waiting: part3 missing", view.Text);
        Assert.Equal(ArchiveText.Set(inTheMiddle).Tone, view.Tone);

        // It is still a stopped set underneath, so it can still be run again by hand.
        Assert.Equal([stopped], ArchiveRules.Plan([], [stopped], null, true).Retry);

        // Named by nothing, there is nothing to say it is waiting for.
        Assert.Equal("Missing volume", ArchiveText.Set(Set("failed", "missingVolume")).Text);
    }

    [Fact]
    public void A_package_with_a_last_volume_missing_is_waiting_and_has_no_problem()
    {
        var stopped = Set("failed", "missingVolume", change: s => s.MissingVolumes = ["a.part3.rar"]);
        var view = ArchiveText.Package([stopped, Set("completed")])!;
        Assert.Equal("Waiting for extraction", view.Text);
        Assert.Equal(RemoteRowTone.Waiting, view.Tone);

        Assert.Equal("1 extraction problem", ArchiveText.Package([Set("failed", "corrupted"), Set("completed")])!.Text);
    }

    [Fact]
    public void A_package_is_spoken_without_a_doubled_full_stop()
    {
        var words = "Password required: This archive needs a password.";
        var view = ArchiveText.Package([Set("failed", "passwordRequired", words), Set("failed", "passwordRequired", words)])!;
        Assert.DoesNotContain("..", view.AccessibleText, StringComparison.Ordinal);
        Assert.Contains("movie: Password required: This archive needs a password. movie:", view.AccessibleText, StringComparison.Ordinal);
    }

    [Fact]
    public void A_set_asking_for_a_password_has_the_row_action_and_another_failure_does_not()
    {
        Assert.True(ArchiveText.Set(Set("failed", "passwordRequired")).NeedsPassword);
        Assert.True(ArchiveText.Set(Set("failed", "wrongPassword")).NeedsPassword);
        Assert.False(ArchiveText.Set(Set("failed", "corrupted")).NeedsPassword);
    }

    [Fact]
    public void Waiting_names_what_it_waits_for()
    {
        Assert.Equal("Waiting: part3 missing", ArchiveText.Set(Set("waiting", change: s => s.MissingVolumes = ["movie.part3.rar"])).Text);
        Assert.Equal("Waiting: part2, part3 downloading", ArchiveText.Set(Set("waiting", change: s => s.DownloadingVolumes = ["movie.part2.rar", "movie.part3.rar"])).Text);
        Assert.Equal("Waiting: .r02 didn't download", ArchiveText.Set(Set("waiting", change: s => s.FailedVolumes = ["movie.r02"])).Text);
        Assert.Equal("Waiting: 3 parts missing", ArchiveText.Set(Set("waiting", change: s => s.MissingVolumes = ["a.part1.rar", "a.part2.rar", "a.part3.rar"])).Text);
        Assert.Equal("Waiting for extraction", ArchiveText.Set(Set("waiting")).Text);
    }

    [Fact]
    public void The_whole_names_are_in_the_tooltip_and_for_a_screen_reader()
    {
        const string Words = "Waiting for extraction: missing movie.part3.rar";
        var view = ArchiveText.Set(Set("waiting", stateText: Words, change: s => s.MissingVolumes = ["movie.part3.rar"]));
        Assert.Contains(Words, view.ToolTip, StringComparison.Ordinal);
        Assert.Contains(Words, view.AccessibleText, StringComparison.Ordinal);
    }

    [Fact]
    public void Extracting_gives_the_percent_and_the_phase()
    {
        Assert.Equal("Extracting 42%", ArchiveText.Set(Set("extracting", change: s => { s.Phase = "extracting"; s.Percent = 42; })).Text);
        Assert.Equal("Extracting", ArchiveText.Set(Set("extracting", change: s => s.Phase = "probing")).Text);
        Assert.Equal("Extracting: checking", ArchiveText.Set(Set("extracting", change: s => { s.Phase = "verifying"; s.Percent = 100; })).Text);
        Assert.Equal("Extracting: moving files", ArchiveText.Set(Set("extracting", change: s => s.Phase = "finalizing")).Text);
    }

    [Fact]
    public void The_other_states_have_their_words()
    {
        Assert.Equal("Extraction completed", ArchiveText.Set(Set("completed")).Text);
        Assert.Equal("Downloaded", ArchiveText.Set(Set("downloaded")).Text);
        Assert.Equal("Downloaded, part2 missing", ArchiveText.Set(Set("downloaded", change: s => s.MissingVolumes = ["a.part2.rar"])).Text);
        Assert.Equal("Not downloaded yet", ArchiveText.Set(Set("downloading")).Text);
    }

    [Fact]
    public void A_sets_note_is_in_its_tooltip_and_its_accessible_name()
    {
        const string Note = "The archives were kept because 2 files were skipped.";
        const string Words = @"Extraction completed: 3 files in D:\out";
        var set = Set("completed", stateText: Words, change: s => s.Note = Note);
        var view = ArchiveText.Set(set);

        Assert.Contains(Note, view.ToolTip, StringComparison.Ordinal);
        Assert.Contains(Note, view.AccessibleText, StringComparison.Ordinal);
        Assert.Equal("Extraction completed", view.Text);
        Assert.Equal($"{Words}. {Note}", ArchiveText.FullWithNote(set));
    }

    [Fact]
    public void A_package_of_several_sets_keeps_each_sets_note()
    {
        const string Note = "7-Zip did not read b.rar: not part of this archive, so not extracted, and it is still there.";
        var view = ArchiveText.Package([Set("completed", change: s => s.Note = Note), Set("completed")])!;

        Assert.Contains(Note, view.ToolTip, StringComparison.Ordinal);
        Assert.Contains(Note, view.AccessibleText, StringComparison.Ordinal);
    }

    [Fact]
    public void A_note_is_never_shown_with_an_address_and_no_note_adds_nothing()
    {
        var set = Set("completed", stateText: "Done.", change: s => s.Note = "See https://example.invalid/x?token=invented");

        Assert.DoesNotContain("invented", ArchiveText.FullWithNote(set));
        Assert.DoesNotContain("invented", ArchiveText.Set(set).AccessibleText);
        Assert.Equal("Done.", ArchiveText.FullWithNote(Set("completed", stateText: "Done.")));
    }

    [Fact]
    public void Volumes_still_coming_with_nobody_asking_leave_the_cell_to_the_Status_column()
    {
        var coming = Set("downloading", stateText: "Downloading: a.rar", change: s => s.DownloadingVolumes = ["a.rar"]);
        var view = ArchiveText.Set(coming);
        Assert.Empty(view.Text);
        Assert.Empty(view.AccessibleText);
        Assert.Contains("Downloading: a.rar", view.ToolTip, StringComparison.Ordinal);
        Assert.Empty(ArchiveText.Package([coming, Set("downloading", change: s => s.DownloadingVolumes = ["b.rar"])])!.Text);
    }

    [Fact]
    public void A_state_from_a_newer_Host_shows_its_own_words()
        => Assert.Equal("Something new", ArchiveText.Set(Set("rethinking", stateText: "Something new")).Text);

    [Fact]
    public void An_address_in_the_Hosts_words_goes_no_further()
    {
        var view = ArchiveText.Set(Set("failed", "unknown", "Extraction failed: https://x.example/file?token=abc123"));
        Assert.DoesNotContain("abc123", view.ToolTip, StringComparison.Ordinal);
        Assert.DoesNotContain("abc123", view.AccessibleText, StringComparison.Ordinal);
    }

    [Fact]
    public void A_short_volume_keeps_what_tells_it_apart()
    {
        Assert.Equal("part03", ArchiveText.ShortVolume("my.movie.part03.rar"));
        Assert.Equal(".r07", ArchiveText.ShortVolume("my.movie.r07"));
        Assert.Equal("my.movie.rar", ArchiveText.ShortVolume("my.movie.rar"));
    }

    // ---- Packages ---------------------------------------------------------------------------------

    [Fact]
    public void A_package_with_no_sets_says_nothing_and_with_one_says_what_it_says()
    {
        Assert.Null(ArchiveText.Package([]));
        Assert.Equal("Wrong password", ArchiveText.Package([Set("failed", "wrongPassword")])!.Text);
    }

    [Fact]
    public void A_package_shows_what_most_needs_attention()
    {
        var done = Set("completed");
        Assert.Equal("Extraction completed", ArchiveText.Package([done, Set("completed")])!.Text);
        Assert.Equal("Extracted 1 of 2", ArchiveText.Package([done, Set("downloaded")])!.Text);
        Assert.Equal("Waiting for extraction", ArchiveText.Package([done, Set("waiting")])!.Text);
        Assert.Equal("1 extraction problem", ArchiveText.Package([done, Set("failed", "corrupted")])!.Text);
        Assert.Equal("Extracting 7%", ArchiveText.Package([Set("failed", "corrupted"), Set("extracting", change: s => s.Percent = 7)])!.Text);

        var asking = ArchiveText.Package([Set("extracting"), Set("failed", "passwordRequired")])!;
        Assert.Equal("1 needs a password", asking.Text);
        Assert.True(asking.NeedsPassword);
        Assert.Equal("2 need a password", ArchiveText.Package([Set("failed", "wrongPassword"), Set("failed", "passwordRequired")])!.Text);
    }

    [Fact]
    public void A_cancelled_set_is_no_problem_in_a_package()
        => Assert.Equal("Extracted 1 of 2", ArchiveText.Package([Set("completed"), Set("failed", "cancelled")])!.Text);

    // ---- What the selection allows ----------------------------------------------------------------

    [Theory]
    [InlineData("a.rar", true)]
    [InlineData("a.part2.RAR", true)]
    [InlineData("a.r07", true)]
    [InlineData("a.z01", true)]
    [InlineData("a.zip", false)]
    [InlineData("a.mp4", false)]
    [InlineData("rar", false)]
    [InlineData("", false)]
    public void A_name_looks_like_a_rar_the_way_the_Host_decides(string name, bool expected)
        => Assert.Equal(expected, ArchiveRules.LooksLikeRar(name));

    [Fact]
    public void Extract_says_why_it_cant_run()
    {
        Assert.Equal("Extract (select a RAR download first)", ArchiveRules.Plan([], [], null, true).ExtractText);
        Assert.Equal("Extract (no RAR archive selected)", ArchiveRules.Plan([File("a.mp4")], [], null, true).ExtractText);
        Assert.Equal("Extract (the background Host isn't connected)", ArchiveRules.Plan([File("a.rar")], [], null, false).ExtractText);
        Assert.Equal("Extract (already extracted)", ArchiveRules.Plan([File("a.rar", Set("completed"))], [], null, true).ExtractText);
        Assert.Equal("Extract (already extracting)", ArchiveRules.Plan([File("a.rar", Set("extracting"))], [], null, true).ExtractText);
        Assert.Equal("Extract (already waiting to extract)", ArchiveRules.Plan([File("a.rar", Set("waiting"))], [], null, true).ExtractText);
        Assert.Equal("Extract (enter the archive's password first)", ArchiveRules.Plan([File("a.rar", Set("failed", "passwordRequired"))], [], null, true).ExtractText);
        Assert.False(ArchiveRules.Plan([File("a.mp4")], [], null, true).CanExtract);
    }

    [Fact]
    public void Extract_asks_only_for_the_rar_files_that_can_go()
    {
        var rar = File("a.rar");
        var downloading = File("b.part1.rar", Set("downloading"));
        var plan = ArchiveRules.Plan([rar, downloading, File("c.nfo"), File("d.rar", Set("completed"))], [], null, true);
        Assert.True(plan.CanExtract);
        Assert.Equal("Extract", plan.ExtractText);
        Assert.Equal([rar.JobId, downloading.JobId], plan.ExtractJobIds);
    }

    [Fact]
    public void A_record_of_its_own_runs_by_its_id()
    {
        var own = Set("downloaded");
        var plan = ArchiveRules.Plan([], [own], null, true);
        Assert.True(plan.CanExtract);
        Assert.Empty(plan.ExtractJobIds);
        Assert.Equal([own], plan.ExtractSets);
    }

    [Fact]
    public void Retry_cancel_remove_and_open_follow_the_state()
    {
        var failed = Set("failed", "corrupted");
        var waiting = Set("waiting");
        var running = Set("extracting");
        var done = Set("completed", change: s => s.Destination = @"C:\out");
        var plan = ArchiveRules.Plan([], [failed, waiting, running, done], null, true);
        Assert.Equal([failed, done], plan.Retry);
        Assert.Equal([waiting, running], plan.Cancel);
        Assert.Equal([failed, waiting, done], plan.Remove);
        Assert.Equal([@"C:\out"], plan.OpenFolders);
    }

    [Fact]
    public void A_finished_records_menu_has_one_line_for_extracting_again()
    {
        var done = Set("completed", change: s => { s.Destination = @"C:\out"; s.Directory = @"C:\in"; });
        var plan = ArchiveRules.Plan([], [done], null, true);

        Assert.False(plan.ShowExtract);
        Assert.Equal([done], plan.Retry);
        Assert.Equal([@"C:\out"], plan.OpenFolders);
        Assert.Equal([@"C:\in"], plan.ArchiveFolders);
    }

    [Fact]
    public void A_failed_set_has_one_line_for_running_again_from_either_row()
    {
        var failed = Set("failed", "missingVolume");
        Assert.False(ArchiveRules.Plan([], [failed], null, true).ShowExtract);
        Assert.False(ArchiveRules.Plan([File("a.rar", failed)], [], null, true).ShowExtract);
    }

    [Fact]
    public void Extract_stays_where_nothing_else_offers_it_or_says_why()
    {
        Assert.True(ArchiveRules.Plan([File("a.rar", Set("downloaded"))], [], null, true).ShowExtract);
        Assert.True(ArchiveRules.Plan([File("a.rar")], [], null, true).ShowExtract);
        Assert.True(ArchiveRules.Plan([File("a.rar"), File("b.rar", Set("failed", "corrupted"))], [], null, true).ShowExtract);

        var archivesGone = Set("completed", change: s => s.ArchivesDeleted = true);
        var plan = ArchiveRules.Plan([], [archivesGone], null, true);
        Assert.True(plan.ShowExtract);
        Assert.Equal("Extract (already extracted)", plan.ExtractText);
    }

    [Fact]
    public void The_archive_folder_is_left_out_when_the_extracted_folder_is_the_same_place()
    {
        var done = Set("completed", change: s => { s.Destination = @"C:\in"; s.Directory = @"C:\IN"; });
        Assert.Empty(ArchiveRules.Plan([], [done], null, true).ArchiveFolders);

        var failed = Set("failed", "corrupted", change: s => s.Directory = @"C:\in");
        Assert.Equal([@"C:\in"], ArchiveRules.Plan([], [failed], null, true).ArchiveFolders);
    }

    [Fact]
    public void Retry_is_not_offered_when_it_could_only_fail()
    {
        var noPassword = Set("failed", "passwordRequired");
        var archivesGone = Set("completed", change: s => s.ArchivesDeleted = true);
        Assert.Empty(ArchiveRules.Plan([], [noPassword, archivesGone], null, true).Retry);

        var withPassword = Set("failed", "wrongPassword", change: s => s.HasPassword = true);
        Assert.Equal([withPassword], ArchiveRules.Plan([], [withPassword], null, true).Retry);
    }

    [Fact]
    public void Several_folders_are_only_listed_once()
    {
        var one = Set("completed", change: s => s.Destination = @"C:\out");
        var two = Set("completed", change: s => s.Destination = @"c:\OUT");
        var other = Set("completed", change: s => s.Destination = @"D:\else");
        Assert.Equal([@"C:\out", @"D:\else"], ArchiveRules.Plan([], [one, two, other], null, true).OpenFolders);
    }

    [Fact]
    public void A_package_alone_takes_the_password_for_itself_even_with_no_sets()
    {
        var target = ArchiveRules.Plan([File("a.bin")], [], (PackageId, "My package"), true).Password;
        Assert.NotNull(target);
        Assert.Equal(PackageId, target.PackageId);
        Assert.Null(target.SetId);
        Assert.False(target.Needed);
    }

    [Fact]
    public void A_package_knows_when_one_of_its_sets_asks()
    {
        var target = ArchiveRules.Plan([], [Set("failed", "wrongPassword"), Set("completed")], (PackageId, "My package"), true).Password;
        Assert.True(target!.Needed);
        Assert.True(target.WasWrong);
    }

    [Fact]
    public void One_set_takes_the_password_for_itself_and_several_take_none()
    {
        var set = Set("failed", "wrongPassword");
        var target = ArchiveRules.Plan([File("a.part1.rar", set), File("a.part2.rar", set)], [], null, true).Password;
        Assert.Equal(set.Id, target!.SetId);
        Assert.True(target.WasWrong);

        Assert.Null(ArchiveRules.Plan([File("a.rar", Set("downloaded")), File("b.rar", Set("downloaded"))], [], null, true).Password);
    }

    [Theory]
    [InlineData("completed")]
    [InlineData("extracting")]
    public void A_set_with_no_use_for_a_password_is_not_offered_one(string state)
        => Assert.Null(ArchiveRules.Plan([File("a.rar", Set(state))], [], null, true).Password);

    [Fact]
    public void A_notice_asks_for_the_package_when_several_of_its_sets_ask()
    {
        var one = Set("failed", "wrongPassword", change: s => s.PackageId = PackageId);
        var two = Set("failed", "wrongPassword", change: s => s.PackageId = PackageId);
        var done = Set("completed", change: s => s.PackageId = PackageId);

        var both = ArchiveRules.NoticeTarget(one, [one, two, done], "My package");
        Assert.Equal(PackageId, both.PackageId);
        Assert.True(both.Needed);

        Assert.Equal(one.Id, ArchiveRules.NoticeTarget(one, [one, done], "My package").SetId);
        Assert.Equal(one.Id, ArchiveRules.NoticeTarget(one, [one, two], null).SetId);
    }

    [Fact]
    public void A_rar_download_with_no_record_takes_a_password_in_advance_by_its_path()
    {
        var target = ArchiveRules.Plan([File("a.rar")], [], null, true).Password;
        Assert.Equal(@"C:\files\a.rar", target!.FilePath);
        Assert.Null(ArchiveRules.Plan([File("a.mp4")], [], null, true).Password);
    }

    [Fact]
    public void The_password_words_say_it_is_not_for_the_download()
    {
        foreach (var target in new[]
        {
            ArchiveRules.Target(Set("failed", "passwordRequired")),
            ArchiveRules.Target(Set("failed", "wrongPassword")),
            ArchiveRules.Target(Set("downloaded")),
            ArchiveRules.PackageTarget(PackageId, "My package", []),
            ArchiveRules.PackageTarget(PackageId, "My package", [Set("failed", "passwordRequired")]),
        })
        {
            Assert.Contains("not for the download", ArchiveText.PasswordHint(target), StringComparison.Ordinal);
        }

        Assert.Contains("wrong", ArchiveText.PasswordHint(ArchiveRules.Target(Set("failed", "wrongPassword"))), StringComparison.Ordinal);
    }

    [Fact]
    public void The_main_action_of_a_record_is_what_it_waits_for()
    {
        Assert.Equal(ArchiveRowAction.EnterPassword, ArchiveRules.Primary(Set("failed", "wrongPassword")));
        Assert.Equal(ArchiveRowAction.OpenFolder, ArchiveRules.Primary(Set("completed", change: s => s.Destination = @"C:\out")));
        Assert.Equal(ArchiveRowAction.Retry, ArchiveRules.Primary(Set("failed", "corrupted")));
        Assert.Equal(ArchiveRowAction.None, ArchiveRules.Primary(Set("extracting")));
    }

    [Fact]
    public void Removing_says_no_file_is_deleted()
    {
        Assert.Contains("No file is deleted", ArchiveText.RemoveQuestion([Set("completed")]), StringComparison.Ordinal);
        Assert.Contains("No file is deleted", ArchiveText.RemoveQuestion([Set("completed"), Set("failed", "corrupted")]), StringComparison.Ordinal);
    }

    [Fact]
    public void The_summary_says_what_went_and_why_the_rest_did_not()
    {
        var result = new ArchiveExtractResult
        {
            Ok = true,
            Sets = [Set("waiting"), Set("completed")],
            Ignored = [new ArchiveExtractIgnored { Item = @"C:\a.rar", Reason = "Already extracted to C:\\out. Use retry to extract it again." }],
        };
        var words = ArchiveText.ExtractSummary(result);
        Assert.Contains("Extraction asked for: movie.", words, StringComparison.Ordinal);
        Assert.Contains("Already extracted", words, StringComparison.Ordinal);
    }

    // ---- The notice above the list ----------------------------------------------------------------

    private static readonly DateTimeOffset Since = new(2026, 11, 6, 12, 0, 0, TimeSpan.Zero);

    [Fact]
    public void A_set_asking_for_a_password_always_gets_a_notice_even_if_it_asked_before_this_window_started()
    {
        var board = new ArchiveNoticeBoard();
        var notice = board.Update([Set("failed", "passwordRequired")], Since, notifyOnCompletion: false);
        Assert.Equal(ArchiveNoticeKind.Password, notice!.Kind);
        Assert.Contains("needs a password", notice.Text, StringComparison.Ordinal);
        Assert.Contains("The download is fine", notice.Text, StringComparison.Ordinal);
    }

    [Fact]
    public void A_wrong_password_says_so()
    {
        var notice = new ArchiveNoticeBoard().Update([Set("failed", "wrongPassword")], Since, true);
        Assert.Contains("was wrong", notice!.Text, StringComparison.Ordinal);
    }

    [Fact]
    public void Dismissing_lasts_until_the_set_has_news()
    {
        var set = Set("failed", "wrongPassword", change: s => s.Attempts = 1);
        var board = new ArchiveNoticeBoard();
        var notice = board.Update([set], Since, true)!;
        board.Dismiss(notice, [set]);
        Assert.Null(board.Update([set], Since, true));

        // Another try that failed the same way is news.
        set.Attempts = 2;
        Assert.NotNull(board.Update([set], Since, true));
    }

    [Fact]
    public void What_finished_before_this_window_started_is_not_news()
    {
        var old = Set("completed", change: s => s.CompletedAt = Since.AddMinutes(-1));
        Assert.Null(new ArchiveNoticeBoard().Update([old], Since, true));
    }

    [Fact]
    public void A_completion_gets_a_notice_with_the_folder_to_open_and_only_when_it_is_wanted()
    {
        var set = Set("extracting");
        var board = new ArchiveNoticeBoard();
        Assert.Null(board.Update([set], Since, true));
        set.State = "completed";
        set.CompletedAt = Since.AddMinutes(1);
        var notice = board.Update([set], Since, true);
        Assert.Equal(ArchiveNoticeKind.Completed, notice!.Kind);
        Assert.Equal("Open Folder", notice.ActionText);
        Assert.Equal("Extraction completed: \"movie\".", notice.Text);

        var quiet = Set("extracting");
        var off = new ArchiveNoticeBoard();
        off.Update([quiet], Since, false);
        quiet.State = "completed";
        quiet.CompletedAt = Since.AddMinutes(1);
        Assert.Null(off.Update([quiet], Since, false));
    }

    [Fact]
    public void A_password_comes_before_a_completion_and_several_completions_share_a_line()
    {
        var board = new ArchiveNoticeBoard();
        var one = Set("completed", change: s => s.CompletedAt = Since.AddMinutes(1));
        var two = Set("completed", change: s => { s.Name = "other"; s.CompletedAt = Since.AddMinutes(2); });
        var asking = Set("failed", "passwordRequired");
        Assert.Equal(ArchiveNoticeKind.Password, board.Update([one, two, asking], Since, true)!.Kind);

        var both = board.Update([one, two], Since, true);
        Assert.Equal("Extraction completed: \"other\" and 1 more.", both!.Text);
        board.Dismiss(both, [one, two]);
        Assert.Null(board.Update([one, two], Since, true));
    }

    [Fact]
    public void A_package_shows_one_line_when_its_last_set_is_done_and_a_lone_set_does_not_wait()
    {
        var package = Guid.NewGuid();
        var board = new ArchiveNoticeBoard();
        var done = Set("completed", change: s => { s.PackageId = package; s.CompletedAt = Since.AddMinutes(1); });
        var next = Set("extracting", change: s => { s.PackageId = package; s.Name = "other"; });
        Assert.Null(board.Update([done, next], Since, true));

        next.State = "completed";
        next.CompletedAt = Since.AddMinutes(2);
        Assert.Equal("Extraction completed: \"other\" and 1 more.", board.Update([done, next], Since, true)!.Text);

        // Short of a volume, a set waits for the user, and that holds nothing back.
        var lone = new ArchiveNoticeBoard();
        var finished = Set("completed", change: s => { s.PackageId = package; s.CompletedAt = Since.AddMinutes(1); });
        var stuck = Set("waiting", change: s => { s.PackageId = package; s.MissingVolumes = ["a.part2.rar"]; });
        Assert.NotNull(lone.Update([finished, stuck], Since, true));
    }

    [Fact]
    public void A_notice_leaves_with_its_set()
    {
        var board = new ArchiveNoticeBoard();
        var done = Set("completed", change: s => s.CompletedAt = Since.AddMinutes(1));
        Assert.NotNull(board.Update([done], Since, true));
        Assert.Null(board.Update([], Since, true));
    }

    [Fact]
    public void A_notice_says_how_many_more_ask()
    {
        var notice = new ArchiveNoticeBoard().Update(
            [Set("failed", "passwordRequired"), Set("failed", "passwordRequired"), Set("failed", "wrongPassword")], Since, true);
        Assert.Contains("2 more archives need one too", notice!.Text, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("Missing volume: Missing volume: a.part3.rar", "Missing volume: a.part3.rar")]
    [InlineData("Password required: This archive needs a password.", "Password required: This archive needs a password.")]
    [InlineData(@"Extraction completed: 1 file in C:\x", @"Extraction completed: 1 file in C:\x")]
    [InlineData("no label", "no label")]
    public void ALabelTheReasonRepeatsIsSaidOnce(string text, string expected)
    {
        Assert.Equal(expected, ArchiveText.Full(Set("failed", stateText: text)));
    }
}

using System.Globalization;
using DownloadManager.Contracts;
using DownloadManager.Desktop.WinUI;

namespace DownloadManager.Core.Tests;

/// <summary>Every fixture here is invented, and none of it holds a real link, key or password.</summary>
public sealed class ContainerImportTests
{
    private const string Provider = "Fetcher";

    private static readonly DateTimeOffset Now = new(2026, 11, 7, 12, 0, 0, TimeSpan.Zero);

    private static RemoteContainerEntryDto Entry(int id, string kind = "hosterLink", int? package = 0, bool canAdd = true, string? name = null, string? host = "files.invalid", string? reason = null, long? size = null)
        => new() { EntryId = id, PackageIndex = package, Kind = kind, CanAdd = canAdd, DisplayName = name ?? $"part{id}.rar", DisplayHost = host, Reason = reason, Size = size };

    private static RemoteContainerPreviewResult Preview(params RemoteContainerEntryDto[] entries) => new()
    {
        Ok = true,
        Format = "ccf",
        FormatVersion = "3.0",
        PreviewId = Guid.NewGuid(),
        ExpiresAt = Now.AddMinutes(15),
        Packages = [new RemoteContainerPackageDto { Index = 0, Name = "Set one", EntryCount = entries.Count(entry => entry.PackageIndex == 0) }],
        Entries = [.. entries],
    };

    // ---- Words and what can be ticked ---------------------------------------------------------------

    [Theory]
    [InlineData("folderLink", "Folder link")]
    [InlineData("magnet", "Magnet")]
    [InlineData("hosterLink", "Hoster through Fetcher")]
    [InlineData("undetermined", "Can't tell")]
    [InlineData("unsupported", "Unsupported")]
    public void Each_kind_has_its_word(string kind, string word)
    {
        var view = new ContainerPicks(Preview(Entry(1, kind, canAdd: kind != "unsupported", reason: kind == "unsupported" ? "No." : null)), Provider).Views()[1];
        Assert.Equal(word, view.Word);
    }

    [Fact]
    public void A_kind_this_build_has_never_heard_of_counts_as_unsupported_whatever_the_entry_says()
    {
        var picks = new ContainerPicks(Preview(Entry(1, "somethingNew", canAdd: true)), Provider);
        var view = picks.Views()[1];

        Assert.Equal("Unsupported", view.Word);
        Assert.False(view.CanCheck);
        Assert.True(view.IsProblem);
        Assert.Equal(0, picks.ChosenCount);
        Assert.False(picks.CanAdd);
        Assert.Contains("This app can't use this.", view.Detail, StringComparison.Ordinal);
    }

    [Fact]
    public void What_cant_be_added_stays_on_show_with_its_reason_and_can_never_be_ticked()
    {
        const string Why = "Fetcher won't take this, because it isn't from a site Fetcher fetches from.";
        var picks = new ContainerPicks(Preview(Entry(1), Entry(2, "unsupported", canAdd: false, reason: Why)), Provider);

        picks.SelectAll();
        picks.Toggle(2, true);

        var views = picks.Views();
        Assert.Contains(Why, views[2].Detail, StringComparison.Ordinal);
        Assert.Contains(Why, views[2].AccessibleName, StringComparison.Ordinal);
        Assert.False(views[2].CanCheck);
        Assert.Equal(false, views[2].Checked);
        Assert.Equal([1], picks.ChosenEntryIds);
        Assert.Equal("1 of 1 selected. 1 can't be added.", picks.Summary());
    }

    [Fact]
    public void Nothing_can_be_ticked_without_a_preview_id_whatever_an_entry_says()
    {
        var preview = Preview(Entry(1));
        preview.PreviewId = null;
        var picks = new ContainerPicks(preview, Provider);

        Assert.False(picks.Views()[1].CanCheck);
        Assert.Equal(0, picks.AddableCount);
        Assert.Equal("Nothing in this file can be added.", picks.Summary());
    }

    [Fact]
    public void What_would_likely_fail_or_nobody_can_vouch_for_starts_unticked_as_in_Add_Links()
    {
        var picks = new ContainerPicks(Preview(
            Entry(1),
            Entry(2, "undetermined", reason: "The list of sites couldn't be read."),
            Entry(3, reason: "Fetcher reports that this site is unavailable right now."),
            Entry(4, "magnet"),
            Entry(5, "folderLink")), Provider);

        Assert.Equal([1, 4, 5], picks.ChosenEntryIds);
        var views = picks.Views();
        Assert.True(views[2].IsProblem);
        Assert.Contains("never requested directly", views[2].Detail, StringComparison.Ordinal);
        Assert.True(views[2].CanCheck);
        Assert.True(views[3].CanCheck);

        picks.Toggle(2, true);
        Assert.Contains(2, picks.ChosenEntryIds);
    }

    // ---- Packages ---------------------------------------------------------------------------------

    [Fact]
    public void A_package_box_speaks_for_what_can_be_added_in_it()
    {
        var picks = new ContainerPicks(Preview(Entry(1), Entry(2), Entry(3, "unsupported", canAdd: false, reason: "No.")), Provider);
        Assert.Equal(true, picks.Views()[0].Checked);

        picks.Toggle(1, false);
        Assert.Null(picks.Views()[0].Checked);

        picks.Toggle(0, false);
        Assert.Equal(false, picks.Views()[0].Checked);
        Assert.Empty(picks.ChosenEntryIds);

        picks.Toggle(0, true);
        Assert.Equal(true, picks.Views()[0].Checked);
        Assert.Equal([1, 2], picks.ChosenEntryIds);
    }

    [Fact]
    public void A_package_with_nothing_to_add_has_a_box_that_does_nothing_so_it_is_off()
    {
        var picks = new ContainerPicks(Preview(Entry(1, "unsupported", canAdd: false, reason: "No.")), Provider);
        Assert.False(picks.Views()[0].CanCheck);
    }

    [Fact]
    public void Links_in_no_package_get_a_heading_only_beside_packages()
    {
        var both = new ContainerPicks(Preview(Entry(1), Entry(2, package: null)), Provider).Views();
        Assert.Equal(["Set one", "part1.rar", "Not in a package", "part2.rar"], both.Select(view => view.Name));
        Assert.Equal(1, both[3].Depth);

        var preview = Preview(Entry(1, package: null));
        preview.Packages.Clear();
        var alone = new ContainerPicks(preview, Provider).Views();
        Assert.Equal(["part1.rar"], alone.Select(view => view.Name));
        Assert.Equal(0, alone[0].Depth);
    }

    [Fact]
    public void An_entry_naming_a_package_that_isnt_there_is_not_lost()
    {
        var views = new ContainerPicks(Preview(Entry(1), Entry(2, package: 9)), Provider).Views();
        Assert.Contains(views, view => view.Name == "part2.rar");
    }

    [Fact]
    public void A_package_with_no_entries_takes_no_room()
    {
        var preview = Preview(Entry(1));
        preview.Packages.Add(new RemoteContainerPackageDto { Index = 1, Name = "Empty" });
        Assert.DoesNotContain(new ContainerPicks(preview, Provider).Views(), view => view.Name == "Empty");
    }

    [Fact]
    public void A_stated_password_is_only_ever_said_to_be_stated()
    {
        var preview = Preview(Entry(1));
        preview.Packages[0].HasArchivePassword = true;
        preview.Packages[0].Comment = "From a friend";

        var header = new ContainerPicks(preview, Provider).Views()[0];

        Assert.Contains("This container states an archive password", header.Detail, StringComparison.Ordinal);
        Assert.Contains("This container states an archive password", header.AccessibleName, StringComparison.Ordinal);
        Assert.Contains("From a friend", header.Detail, StringComparison.Ordinal);

        // The DTO has no field that could carry the value, and this keeps it that way.
        Assert.DoesNotContain(typeof(RemoteContainerPackageDto).GetProperties(), property => property.PropertyType == typeof(string) && property.Name.Contains("Password", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void No_address_gets_through_a_name_a_comment_or_a_reason()
    {
        const string Secret = "invented-token";
        var address = $"https://files.invalid/get?token={Secret}";
        var preview = Preview(Entry(1, name: $"{address}", reason: address));
        preview.Packages[0].Name = address;
        preview.Packages[0].Comment = address;
        preview.Problems = [address];
        preview.AccountProblem = address;

        foreach (var view in new ContainerPicks(preview, Provider).Views())
        {
            Assert.DoesNotContain(Secret, view.Name, StringComparison.Ordinal);
            Assert.DoesNotContain(Secret, view.Detail, StringComparison.Ordinal);
            Assert.DoesNotContain(Secret, view.AccessibleName, StringComparison.Ordinal);
        }

        var read = RemoteText.ContainerRead(preview, "invented.ccf", CultureInfo.InvariantCulture);
        Assert.DoesNotContain(Secret, read.AccountProblem, StringComparison.Ordinal);
        Assert.DoesNotContain(Secret, string.Join(" ", read.Notes), StringComparison.Ordinal);
    }

    [Fact]
    public void Two_links_with_the_same_name_are_told_apart_by_their_package_when_spoken()
    {
        var preview = Preview(Entry(1, name: "same.rar"), Entry(2, name: "same.rar", package: 1));
        preview.Packages.Add(new RemoteContainerPackageDto { Index = 1, Name = "Set two", EntryCount = 1 });

        var views = new ContainerPicks(preview, Provider).Views();

        Assert.Contains("in Set one", views[1].AccessibleName, StringComparison.Ordinal);
        Assert.Contains("in Set two", views[3].AccessibleName, StringComparison.Ordinal);
    }

    [Fact]
    public void A_nameless_link_shows_its_host_and_place_and_a_hostless_one_its_place()
    {
        Assert.Equal("files.invalid (link 3)", new ContainerPicks(Preview(new RemoteContainerEntryDto { EntryId = 3, PackageIndex = 0, Kind = "hosterLink", CanAdd = true, DisplayHost = "files.invalid" }), Provider).Views()[1].Name);
        Assert.Equal("Link 3", new ContainerPicks(Preview(new RemoteContainerEntryDto { EntryId = 3, PackageIndex = 0, Kind = "magnet", CanAdd = true }), Provider).Views()[1].Name);
    }

    [Fact]
    public void A_stated_size_is_said_to_be_only_what_the_container_says()
    {
        var view = new ContainerPicks(Preview(Entry(1, size: 2048)), Provider).Views()[1];
        Assert.Contains("The container says 2.0 KB.", view.Detail, StringComparison.Ordinal);
    }

    [Fact]
    public void A_preview_kept_past_its_time_is_known_to_be_gone()
    {
        var picks = new ContainerPicks(Preview(Entry(1)), Provider);
        Assert.False(picks.IsExpired(Now.AddMinutes(14)));
        Assert.True(picks.IsExpired(Now.AddMinutes(15)));
    }

    // ---- What the Host said about the file ------------------------------------------------------------

    [Fact]
    public void A_DLC_says_not_supported_with_the_Hosts_reason_and_offers_the_hand_over()
    {
        const string Why = "DLC files can't be opened by this app.";
        var read = RemoteText.ContainerRead(new RemoteContainerPreviewResult { Format = "dlc", NotSupported = true, CanSendToJd = true, Error = Why }, "invented.dlc");

        Assert.Equal($"invented.dlc isn't supported here. {Why}", read.Status);
        Assert.True(read.IsProblem);
        Assert.True(read.CanSendToJd);
        Assert.False(read.ShowList);
    }

    [Fact]
    public void A_damaged_file_gives_its_reason_and_nothing_to_read_again_for()
    {
        var read = RemoteText.ContainerRead(new RemoteContainerPreviewResult { Format = "rsdf", Error = "This file is damaged.", Problems = ["Line 2 is unreadable."] }, "invented.rsdf");

        Assert.Equal("This file is damaged.", read.Status);
        Assert.False(read.CanReadAgain);
        Assert.False(read.CanSendToJd);
        Assert.Equal(["Line 2 is unreadable."], read.Notes);
    }

    [Fact]
    public void A_readable_CCF_doesnt_push_the_hand_over_while_it_has_something_to_add()
    {
        var preview = Preview(Entry(1));
        preview.CanSendToJd = true;
        Assert.False(RemoteText.ContainerRead(preview, "invented.ccf").CanSendToJd);

        preview.PreviewId = null;
        preview.ExpiresAt = null;
        var nothing = RemoteText.ContainerRead(preview, "invented.ccf");
        Assert.True(nothing.CanSendToJd);
        Assert.Contains("Nothing in it can be added.", nothing.Status, StringComparison.Ordinal);
    }

    [Fact]
    public void A_read_file_says_what_it_is_how_much_it_holds_and_how_long_it_stays_ready()
    {
        var preview = Preview(Entry(1), Entry(2));
        var read = RemoteText.ContainerRead(preview, "invented.ccf", CultureInfo.InvariantCulture);

        Assert.StartsWith("Read invented.ccf (CCF 3.0): 2 links.", read.Status, StringComparison.Ordinal);
        Assert.Contains("stay ready until", read.Status, StringComparison.Ordinal);
        Assert.False(read.IsProblem);
        Assert.True(read.ShowList);
        Assert.Null(read.AccountProblem);
    }

    [Fact]
    public void An_account_problem_is_said_apart_from_the_file()
    {
        var preview = Preview(Entry(1, "unsupported", canAdd: false, reason: "No."));
        preview.PreviewId = null;
        preview.AccountProblem = "No API key is saved.";

        Assert.Equal("No API key is saved.", RemoteText.ContainerRead(preview, "invented.ccf").AccountProblem);
    }

    [Theory]
    [InlineData(true, "sent", false, false)]
    [InlineData(false, "rejected", false, true)]
    [InlineData(false, "notRunning", true, true)]
    [InlineData(false, "timedOut", true, true)]
    [InlineData(false, "failed", true, true)]
    [InlineData(false, "invalidInput", true, false)]
    [InlineData(false, "somethingNew", true, true)]
    public void The_hand_over_says_the_Hosts_words_and_whether_another_try_makes_sense(bool ok, string status, bool problem, bool again)
    {
        var view = RemoteText.ContainerSend(new RemoteContainerSendResult { Ok = ok, Status = status, Message = "The Host's words." });

        Assert.Equal("The Host's words.", view.Text);
        Assert.Equal(problem, view.IsProblem);
        Assert.Equal(again, view.CanSendAgain);
    }

    [Fact]
    public void Lost_calls_say_what_is_and_isnt_known_in_words_for_each_call()
    {
        Assert.Equal(RemoteText.ContainerReadLost, RemoteText.ContainerReadFailure(RemoteText.HostLost));
        Assert.Equal(RemoteText.ContainerReadLost, RemoteText.ContainerReadFailure(RemoteText.HostTooSlow));
        Assert.Equal(RemoteText.HostBusy, RemoteText.ContainerReadFailure(RemoteText.HostBusy));
        Assert.Equal(RemoteText.ContainerSendLost, RemoteText.ContainerSendFailure(RemoteText.HostLost));
        Assert.Equal(RemoteText.HostNotRunning, RemoteText.ContainerSendFailure(RemoteText.HostNotRunning));
    }

    // ---- Outcomes -----------------------------------------------------------------------------------

    private static RemoteJobDto Job(string state = "waiting", bool approval = false, Action<RemoteJobDto>? change = null)
    {
        var job = new RemoteJobDto { Id = Guid.NewGuid(), Provider = "fetcher", ProviderName = Provider, State = state, Status = "Waiting for Fetcher", ApprovalNeeded = approval, Name = "part1.rar" };
        change?.Invoke(job);
        return job;
    }

    private static ContainerOutcomes Outcomes(bool leftOut, params RemoteContainerAddOutcomeDto[] outcomes)
        => new(new RemoteContainerAddResult { Ok = outcomes.Any(outcome => outcome.Ok), JobsLeftOut = leftOut, Outcomes = [.. outcomes] }, id => $"entry {id}");

    [Fact]
    public void Entries_left_out_of_a_full_answer_read_as_added_and_never_as_failed_or_removed()
    {
        var id = Guid.NewGuid();
        var outcomes = Outcomes(true, new RemoteContainerAddOutcomeDto { EntryId = 1, Ok = true, JobId = id });

        outcomes.Refresh([], Now);
        var view = outcomes.Views()[0];

        Assert.Equal("Added", view.Word);
        Assert.False(view.IsProblem);
        Assert.Equal(1, outcomes.AddedCount);
        Assert.DoesNotContain("Removed", view.Word, StringComparison.Ordinal);
        Assert.StartsWith("Added 1.", outcomes.Summary(Provider), StringComparison.Ordinal);
    }

    [Fact]
    public void Once_the_list_has_been_read_a_left_out_entry_shows_its_job_and_can_be_approved()
    {
        var job = Job("intended", approval: true);
        var outcomes = Outcomes(true, new RemoteContainerAddOutcomeDto { EntryId = 1, Ok = true, JobId = job.Id });

        outcomes.Refresh([job], Now);

        Assert.Equal(1, outcomes.WaitingCount);
        var view = outcomes.Views()[0];
        Assert.Equal("Not cached, needs Fetcher to fetch it", view.Word);
        Assert.True(view.ShowBox);
        Assert.Equal(false, view.Checked);
        Assert.Contains("using your account's quota", view.Detail, StringComparison.Ordinal);
    }

    [Fact]
    public void A_job_that_was_on_the_list_and_has_gone_reads_as_removed()
    {
        var job = Job();
        var outcomes = Outcomes(false, new RemoteContainerAddOutcomeDto { EntryId = 1, Ok = true, JobId = job.Id, Job = job });

        outcomes.Refresh([job], Now);
        outcomes.Refresh([], Now);

        Assert.Equal("Removed from the list", outcomes.Views()[0].Word);
    }

    [Fact]
    public void The_replys_own_job_is_used_until_the_list_catches_up()
    {
        var job = Job("intended", approval: true);
        var outcomes = Outcomes(false, new RemoteContainerAddOutcomeDto { EntryId = 1, Ok = true, JobId = job.Id, Job = job });

        outcomes.Refresh([], Now);

        Assert.Equal(1, outcomes.WaitingCount);
    }

    [Fact]
    public void Approval_is_never_assumed_and_only_what_was_ticked_goes()
    {
        var first = Job("intended", approval: true);
        var second = Job("intended", approval: true);
        var outcomes = Outcomes(false,
            new RemoteContainerAddOutcomeDto { EntryId = 1, Ok = true, JobId = first.Id, Job = first },
            new RemoteContainerAddOutcomeDto { EntryId = 2, Ok = true, JobId = second.Id, Job = second });
        outcomes.Refresh([first, second], Now);

        Assert.Empty(outcomes.ToApprove);
        outcomes.Toggle(1, true);
        Assert.Equal([second.Id], outcomes.ToApprove);

        outcomes.SetAllApproved(true);
        Assert.Equal(2, outcomes.ApprovedCount);

        // No longer waiting means no longer approved, so nothing goes twice.
        first.ApprovalNeeded = false;
        first.State = "waiting";
        outcomes.Refresh([first, second], Now);
        Assert.Equal([second.Id], outcomes.ToApprove);

        // If it comes back to waiting, it starts unticked again.
        first.ApprovalNeeded = true;
        first.State = "intended";
        outcomes.Refresh([first, second], Now);
        Assert.Equal([second.Id], outcomes.ToApprove);
    }

    [Fact]
    public void What_a_package_s_local_files_say_is_asked_once_however_many_of_its_jobs_are_listed()
    {
        var package = Guid.NewGuid();
        var jobs = Enumerable.Range(0, 50).Select(_ => Job("localStarted", change: job => job.PackageId = package)).ToList();
        var loner = Job("localStarted");
        var outcomes = Outcomes(false, [.. jobs.Append(loner).Select((job, index) => new RemoteContainerAddOutcomeDto { EntryId = index, Ok = true, JobId = job.Id, Job = job })]);
        var asked = 0;

        outcomes.Refresh([.. jobs, loner], Now, _ =>
        {
            asked++;
            return "Downloading here";
        });
        Assert.Equal(2, asked);

        // Once per reading, because the answer changes with what the files are doing.
        outcomes.Refresh([.. jobs, loner], Now, _ =>
        {
            asked++;
            return "Done here";
        });
        Assert.Equal(4, asked);
    }

    [Fact]
    public async Task Stopping_approvals_leaves_the_rest_unsent()
    {
        var waiting = Enumerable.Range(0, 5).Select(_ => Job("intended", approval: true)).ToList();
        var outcomes = Outcomes(false, [.. waiting.Select((job, index) => new RemoteContainerAddOutcomeDto { EntryId = index, Ok = true, JobId = job.Id, Job = job })]);
        outcomes.Refresh(waiting, Now);
        outcomes.SetAllApproved(true);
        using var closing = new CancellationTokenSource();
        var asked = new List<Guid>();
        var progress = new List<int>();

        await outcomes.ApproveChosenAsync(
            (id, _) =>
            {
                asked.Add(id);
                if (asked.Count == 2) closing.Cancel();
                return Task.FromResult<string?>(null);
            },
            progress.Add,
            closing.Token);

        Assert.Equal(2, asked.Count);
        Assert.Equal([1, 2], progress);
    }

    [Fact]
    public async Task Approvals_all_go_and_a_refusal_shows_on_its_own_line_and_a_call_cut_off_by_closing_is_no_failure()
    {
        var waiting = Enumerable.Range(0, 3).Select(_ => Job("intended", approval: true)).ToList();
        var outcomes = Outcomes(false, [.. waiting.Select((job, index) => new RemoteContainerAddOutcomeDto { EntryId = index, Ok = true, JobId = job.Id, Job = job })]);
        outcomes.Refresh(waiting, Now);
        outcomes.SetAllApproved(true);
        var sent = 0;

        await outcomes.ApproveChosenAsync((id, _) => Task.FromResult(id == waiting[1].Id ? "Refused here." : null), count => sent = count, CancellationToken.None);

        Assert.Equal(3, sent);
        Assert.Single(outcomes.Views(), view => view.Detail.Contains("Refused here.", StringComparison.Ordinal));

        using var closing = new CancellationTokenSource();
        closing.CancelAfter(TimeSpan.FromMilliseconds(50));
        sent = 0;
        await outcomes.ApproveChosenAsync(async (_, token) =>
        {
            await Task.Delay(Timeout.Infinite, token);
            return null;
        }, count => sent = count, closing.Token);
        Assert.Equal(0, sent);
    }

    [Fact]
    public void An_entry_that_wasnt_added_says_why_comes_first_and_cant_be_approved()
    {
        var job = Job();
        var outcomes = Outcomes(false,
            new RemoteContainerAddOutcomeDto { EntryId = 1, Ok = true, JobId = job.Id, Job = job },
            new RemoteContainerAddOutcomeDto { EntryId = 2, Error = "Fetcher couldn't be reached." });

        outcomes.Refresh([job], Now);
        var failed = outcomes.Views()[0];

        Assert.Equal("entry 2", failed.Name);
        Assert.Equal("Not added", failed.Word);
        Assert.Equal("Fetcher couldn't be reached.", failed.Detail);
        Assert.True(failed.IsProblem);
        Assert.False(failed.ShowBox);
        Assert.StartsWith("Added 1 of 2.", outcomes.Summary(Provider), StringComparison.Ordinal);
    }

    [Fact]
    public void All_failing_says_so()
    {
        var outcomes = Outcomes(false, new RemoteContainerAddOutcomeDto { EntryId = 1, Error = "No." }, new RemoteContainerAddOutcomeDto { EntryId = 2, Error = "No." });
        Assert.Equal("None of these were added.", outcomes.Summary(Provider));
        Assert.Equal(0, outcomes.AddedCount);
    }

    [Fact]
    public void One_already_on_the_list_says_so_with_the_Hosts_note()
    {
        var job = Job();
        var outcomes = Outcomes(false, new RemoteContainerAddOutcomeDto { EntryId = 1, Ok = true, Existing = true, JobId = job.Id, Job = job, Note = "The folder given wasn't used." });

        outcomes.Refresh([job], Now);
        var detail = outcomes.Views()[0].Detail;

        Assert.Contains("It was already on the list.", detail, StringComparison.Ordinal);
        Assert.Contains("The folder given wasn't used.", detail, StringComparison.Ordinal);
    }

    [Fact]
    public void A_link_password_points_to_the_list_because_a_row_here_has_nowhere_safe_to_keep_one()
    {
        var job = Job("failed", change: failed => failed.FailureKind = "linkPasswordRequired");
        var outcomes = Outcomes(false, new RemoteContainerAddOutcomeDto { EntryId = 1, Ok = true, JobId = job.Id, Job = job });

        outcomes.Refresh([job], Now);

        Assert.Contains("select the item and press Enter", outcomes.Views()[0].Detail, StringComparison.Ordinal);
    }

    [Fact]
    public void A_failed_approval_is_said_on_its_row_and_cleared_when_it_goes_through()
    {
        var job = Job("intended", approval: true);
        var outcomes = Outcomes(false, new RemoteContainerAddOutcomeDto { EntryId = 1, Ok = true, JobId = job.Id, Job = job });
        outcomes.Refresh([job], Now);

        outcomes.ShowProblem(job.Id, "Fetcher refused this.");
        Assert.Contains("Fetcher refused this.", outcomes.Views()[0].Detail, StringComparison.Ordinal);
        Assert.True(outcomes.Views()[0].IsProblem);

        outcomes.ShowProblem(job.Id, null);
        Assert.DoesNotContain("refused", outcomes.Views()[0].Detail, StringComparison.Ordinal);
    }

    [Fact]
    public void Local_status_is_asked_for_only_when_a_job_has_local_files()
    {
        var waiting = Job();
        var local = Job("localStarted");
        var asked = new List<Guid>();
        var outcomes = Outcomes(false,
            new RemoteContainerAddOutcomeDto { EntryId = 1, Ok = true, JobId = waiting.Id },
            new RemoteContainerAddOutcomeDto { EntryId = 2, Ok = true, JobId = local.Id });

        outcomes.Refresh([waiting, local], Now, job =>
        {
            asked.Add(job.Id);
            return "Downloading locally";
        });

        Assert.Equal([local.Id], asked);
    }

    // ---- Never looking at the file -----------------------------------------------------------------

    /// <summary>Asking about a file on a network path sends this Windows account's credentials to the host it names, so this side never asks at all.</summary>
    [Theory]
    [InlineData("ContainerImportDialog.cs")]
    [InlineData("Remote/ContainerImport.cs")]
    public void The_import_code_never_looks_at_the_file_itself(string relative)
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && !System.IO.File.Exists(Path.Combine(directory.FullName, "DownloadManager.slnx"))) directory = directory.Parent;
        Assert.NotNull(directory);
        var source = System.IO.File.ReadAllText(Path.Combine(directory.FullName, "src", "DownloadManager.Desktop.WinUI", relative));

        foreach (var call in new[] { "File.", "Directory.", "FileInfo", "DirectoryInfo", "FileStream", "StorageFile", "Path.GetFullPath", "Path.Exists" })
        {
            Assert.DoesNotContain(call, source, StringComparison.Ordinal);
        }
    }
}

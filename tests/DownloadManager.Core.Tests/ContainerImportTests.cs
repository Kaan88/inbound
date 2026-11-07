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

    private static RemoteJobDto Job(string state = "waiting") => new() { Id = Guid.NewGuid(), Provider = "fetcher", ProviderName = Provider, State = state, Status = "Waiting for Fetcher", Name = "part1.rar" };

    private static ContainerOutcomes Outcomes(bool leftOut, params RemoteContainerAddOutcomeDto[] outcomes)
        => new(new RemoteContainerAddResult { Ok = outcomes.Any(outcome => outcome.Ok), JobsLeftOut = leftOut, Outcomes = [.. outcomes] }, id => $"entry {id}");

    [Fact]
    public void Entries_left_out_of_a_full_answer_count_as_added_and_never_as_failed()
    {
        var outcomes = Outcomes(true, new RemoteContainerAddOutcomeDto { EntryId = 1, Ok = true, JobId = Guid.NewGuid() });

        Assert.Equal(1, outcomes.AddedCount);
        Assert.Equal(1, outcomes.NewCount);
        Assert.Empty(outcomes.Problems);
        Assert.Equal("Added 1 item for Fetcher.", outcomes.Summary(Provider));
    }

    [Fact]
    public void Nothing_asks_after_adding_and_only_what_wasnt_added_or_was_there_already_is_listed()
    {
        var job = Job("intended");
        var outcomes = Outcomes(false,
            new RemoteContainerAddOutcomeDto { EntryId = 1, Ok = true, JobId = job.Id, Job = job },
            new RemoteContainerAddOutcomeDto { EntryId = 2, Error = "Fetcher couldn't be reached." },
            new RemoteContainerAddOutcomeDto { EntryId = 3, Ok = true, Existing = true, JobId = job.Id, Job = job, Note = "The folder given wasn't used." });

        Assert.Equal(2, outcomes.AddedCount);
        Assert.Equal(1, outcomes.NewCount);
        Assert.Equal(["entry 2: Fetcher couldn't be reached.", "entry 3: already on the list. The folder given wasn't used."], outcomes.Problems);
        Assert.Equal("Added 1 item for Fetcher.", outcomes.Summary(Provider));
        Assert.DoesNotContain(outcomes.Problems, line => line.Contains("approv", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void What_an_import_lists_afterwards_never_carries_an_address_the_Host_gave()
    {
        const string link = "https://cdn1.tb-cdn.io/dl/1?token=invented-token";
        var job = Job("intended");
        var outcomes = Outcomes(false,
            new RemoteContainerAddOutcomeDto { EntryId = 1, Error = $"Fetcher refused {link}" },
            new RemoteContainerAddOutcomeDto { EntryId = 2, Ok = true, Existing = true, JobId = job.Id, Job = job, Note = $"Already there as {link}." });

        Assert.Equal(2, outcomes.Problems.Count);
        Assert.StartsWith("entry 1: Fetcher refused", outcomes.Problems[0], StringComparison.Ordinal);
        Assert.All(outcomes.Problems, line =>
        {
            Assert.DoesNotContain("invented-token", line, StringComparison.Ordinal);
            Assert.DoesNotContain("tb-cdn", line, StringComparison.Ordinal);
        });
    }

    [Fact]
    public void Every_entry_that_wasnt_added_is_listed_however_many_there_are()
    {
        var outcomes = Outcomes(false, [.. Enumerable.Range(1, 40).Select(id => new RemoteContainerAddOutcomeDto { EntryId = id, Error = "No." })]);

        Assert.Equal(40, outcomes.Problems.Count);
        Assert.Equal("entry 40: No.", outcomes.Problems[^1]);
    }

    [Fact]
    public void All_failing_says_so()
    {
        Assert.Equal("None of these were added.", Outcomes(false, new RemoteContainerAddOutcomeDto { EntryId = 1, Error = "No." }, new RemoteContainerAddOutcomeDto { EntryId = 2, Error = "No." }).Summary(Provider));
        Assert.Equal("This wasn't added.", Outcomes(false, new RemoteContainerAddOutcomeDto { EntryId = 1, Error = "No." }).Summary(Provider));
    }

    [Fact]
    public void What_was_already_on_the_list_isnt_counted_as_added()
    {
        var job = Job();
        var twin = new RemoteContainerAddOutcomeDto { EntryId = 1, Ok = true, Existing = true, JobId = job.Id, Job = job };
        var fresh = new RemoteContainerAddOutcomeDto { EntryId = 2, Ok = true, JobId = job.Id, Job = job };
        var refused = new RemoteContainerAddOutcomeDto { EntryId = 4, Error = "No." };

        Assert.Equal("It was already on the list.", Outcomes(false, twin).Summary(Provider));
        Assert.Equal("These were already on the list.", Outcomes(false, twin, new RemoteContainerAddOutcomeDto { EntryId = 3, Ok = true, Existing = true, JobId = job.Id, Job = job }).Summary(Provider));
        Assert.Equal("Nothing new was added.", Outcomes(false, twin, refused).Summary(Provider));
        Assert.Equal("Added 1 item for Fetcher.", Outcomes(false, twin, fresh).Summary(Provider));
        Assert.Equal(0, Outcomes(false, twin).NewCount);
    }

    [Fact]
    public void A_Host_note_that_already_says_so_isnt_said_twice()
    {
        var job = Job();
        var note = "This is already on the list, so the folder given here wasn't used.";
        var outcomes = Outcomes(false, new RemoteContainerAddOutcomeDto { EntryId = 1, Ok = true, Existing = true, JobId = job.Id, Job = job, Note = note });

        Assert.Equal([$"entry 1: {note}"], outcomes.Problems);
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

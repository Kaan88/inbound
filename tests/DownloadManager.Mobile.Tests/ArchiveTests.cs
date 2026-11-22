using DownloadManager.Contracts;
using DownloadManager.Mobile.Archives;

namespace DownloadManager.Mobile.Tests;

public sealed class ArchiveTests
{
    private static ArchiveSetDto Set(string state, string stateText = "", params Guid[] jobs) => new()
    {
        Id = Guid.NewGuid(), Name = "project-files", State = state, StateText = stateText,
        FirstVolume = "/storage/Download/project-files.part1.rar", JobIds = [.. jobs],
        Volumes = ["project-files.part1.rar", "project-files.part2.rar", "project-files.part3.rar"],
    };

    private static JobDto Job(Guid id, string name, long? total = null) => new() { Id = id, FileName = name, TotalBytes = total };

    private static IpcMessage Reply(IpcMessage request, string type, object payload) => payload switch
    {
        ArchiveListResult p => IpcMessage.Create(type, p, request.Id),
        ArchiveSettingsResult p => IpcMessage.Create(type, p, request.Id),
        ArchiveActionResult p => IpcMessage.Create(type, p, request.Id),
        HostActionResult p => IpcMessage.Create(type, p, request.Id),
        ArchiveSavedPasswordsResult p => IpcMessage.Create(type, p, request.Id),
        _ => throw new ArgumentException("payload"),
    };

    [Fact]
    public void An_extracting_set_says_files_and_percent_with_a_bar()
    {
        var set = Set("extracting");
        set.FilesFinalized = 18; set.FilesTotal = 40; set.Percent = 42;
        var info = ArchiveRowInfo.For(set)!;
        Assert.Equal("Extracting · 18 of 40 files · 42%", info.Subtitle);
        Assert.Equal("Extracting", info.Lead);
        Assert.Equal(ArchiveTone.Amber, info.Tone);
        Assert.True(info.ShowBar);
        Assert.Equal(0.42, info.Progress);
        Assert.Equal(ArchiveIcon.Zip, info.Icon);
        Assert.Equal(ArchiveAction.None, info.Action);
    }

    [Fact]
    public void Unknown_parts_of_the_extracting_line_are_dropped()
    {
        var noPercent = Set("extracting"); noPercent.FilesTotal = 40; noPercent.FilesFinalized = 3;
        var info = ArchiveRowInfo.For(noPercent)!;
        Assert.Equal("Extracting · 3 of 40 files", info.Subtitle);
        Assert.True(info.ShowBar);
        Assert.Null(info.Progress);

        var nothing = ArchiveRowInfo.For(Set("extracting"))!;
        Assert.Equal("Extracting", nothing.Subtitle);

        var overshoot = Set("extracting"); overshoot.FilesTotal = 10; overshoot.FilesFinalized = 12; overshoot.Percent = 180;
        Assert.Equal("Extracting · 10 of 10 files · 100%", ArchiveRowInfo.For(overshoot)!.Subtitle);
    }

    [Fact]
    public void A_set_that_needs_a_password_shows_parts_size_and_the_Enter_button()
    {
        var set = Set("failed"); set.NeedsPassword = true;
        var info = ArchiveRowInfo.For(set, 2_100_000_000)!;
        Assert.Equal("Needs a password · 3 parts · 2.1 GB", info.Subtitle);
        Assert.Equal(ArchiveIcon.Lock, info.Icon);
        Assert.Equal(ArchiveTone.Amber, info.Tone);
        Assert.Equal(ArchiveAction.Enter, info.Action);
        Assert.False(info.ShowBar);

        set.Volumes = ["a.zip"];
        Assert.Equal("Needs a password", ArchiveRowInfo.For(set)!.Subtitle);
    }

    [Fact]
    public void Other_states_use_the_Hosts_words()
    {
        Assert.Null(ArchiveRowInfo.For(Set("downloading", "Downloading")));

        var waiting = ArchiveRowInfo.For(Set("waiting", "Waiting: part 2 downloading"))!;
        Assert.Equal("Waiting: part 2 downloading", waiting.Subtitle);
        Assert.Equal(ArchiveTone.Amber, waiting.Tone);

        var done = ArchiveRowInfo.For(Set("completed", "Extracted 40 files"))!;
        Assert.Equal("Extracted 40 files", done.Subtitle);
        Assert.Equal(ArchiveTone.Done, done.Tone);
        Assert.Equal(ArchiveAction.None, done.Action);

        // With the Host's counts, they come before the folder, which is its last part only (a paired PC's long path).
        var counted = Set("completed", @"Extraction completed: 3 files in C:\Users\someone\Downloads\Torrents\Compressed, 1 renamed");
        counted.FilesWritten = 2; counted.FilesRenamed = 1; counted.Destination = @"C:\Users\someone\Downloads\Torrents\Compressed\";
        Assert.Equal("Extracted · 3 files · 1 renamed · in Compressed", ArchiveRowInfo.For(counted)!.Subtitle);
        counted.FilesRenamed = 0; counted.FilesWritten = 1; counted.ArchivesDeleted = true; counted.Destination = "/storage/emulated/0/Download";
        Assert.Equal("Extracted · 1 file · archive removed · in Download", ArchiveRowInfo.For(counted)!.Subtitle);

        var failed = Set("failed", "Extraction failed"); failed.FailureKind = "corrupted"; failed.FailureMessage = "The archive is damaged.";
        var info = ArchiveRowInfo.For(failed)!;
        Assert.Equal("The archive is damaged.", info.Subtitle);
        Assert.Equal(ArchiveTone.Failed, info.Tone);
        Assert.Equal(ArchiveAction.Retry, info.Action);

        var missing = Set("failed", "Waiting: part 3 missing"); missing.FailureKind = "missingVolume"; missing.MissingVolumes = ["p3"];
        Assert.Equal(ArchiveTone.Amber, ArchiveRowInfo.For(missing)!.Tone);

        Assert.Null(ArchiveRowInfo.For(Set("somethingNew")));
        Assert.Equal("From a newer Host", ArchiveRowInfo.For(Set("somethingNew", "From a newer Host"))!.Subtitle);
    }

    [Fact]
    public void The_password_sheet_names_the_set_and_the_saved_password_only_when_it_failed()
    {
        var set = Set("failed"); set.NeedsPassword = true;
        Assert.Equal("project-files.part1.rar (3 parts).", ArchiveRowInfo.PasswordSheetLine(set));
        set.HasPassword = true;
        Assert.Equal("project-files.part1.rar (3 parts). The password given didn't open it.", ArchiveRowInfo.PasswordSheetLine(set));
        set.SavedPasswordsTried = 1;
        Assert.Equal("project-files.part1.rar (3 parts). The saved password didn't open it.", ArchiveRowInfo.PasswordSheetLine(set));
        set.SavedPasswordsTried = 3;
        Assert.Equal("project-files.part1.rar (3 parts). The 3 saved passwords didn't open it.", ArchiveRowInfo.PasswordSheetLine(set));
        set.NeedsPassword = false;
        Assert.Equal("project-files.part1.rar (3 parts).", ArchiveRowInfo.PasswordSheetLine(set));
        set.Volumes = [];
        set.FirstVolume = @"D:\Downloads\one.zip";
        Assert.Equal("one.zip.", ArchiveRowInfo.PasswordSheetLine(set));
    }

    [Fact]
    public async Task The_password_request_names_only_the_set_and_prints_no_password()
    {
        const string Secret = "hunter2-secret";
        var id = Guid.NewGuid();
        var request = ArchiveClient.PasswordRequest(id, Secret);
        Assert.Equal(id, request.SetId);
        Assert.Null(request.FilePath);
        Assert.Null(request.PackageId);
        Assert.DoesNotContain(Secret, request.ToString());
        Assert.DoesNotContain(Secret, ArchiveRowInfo.For(Set("failed"))!.ToString());
        Assert.Throws<ArgumentException>(() => ArchiveClient.PasswordRequest(id, ""));

        var host = new FakeHostConnection { Reply = r => Reply(r, ArchiveMessageTypes.ActionResult, new ArchiveActionResult { Ok = true, Set = Set("waiting") }) };
        var result = await new ArchiveClient(host).SetPasswordAsync(id, Secret);
        Assert.True(result.Ok);
        var sent = Assert.Single(host.Sent);
        Assert.Equal(ArchiveMessageTypes.SetPassword, sent.Type);
        var payload = sent.GetPayload<ArchiveSetPasswordRequest>();
        Assert.Equal(id, payload.SetId);
        Assert.Equal(Secret, payload.Password);
        Assert.False(payload.SaveForOtherArchives);
        Assert.Null(payload.DeleteArchives);

        var both = ArchiveClient.PasswordRequest(id, Secret, saveForOtherArchives: true, deleteArchives: true);
        Assert.True(both.SaveForOtherArchives);
        Assert.True(both.DeleteArchives);
        Assert.Equal("None yet", ArchiveClient.SavedPasswordsLine(0));
        Assert.Equal("3, tried in order", ArchiveClient.SavedPasswordsLine(3));

        // The saved list comes back as ids and dates; only the add request carries the value.
        var listed = new FakeHostConnection { Reply = r => Reply(r, ArchiveMessageTypes.PasswordsResult, new ArchiveSavedPasswordsResult { Ok = true, Passwords = [new() { Id = id }] }) };
        var saved = await new ArchiveClient(listed).AddSavedPasswordAsync(Secret);
        Assert.Equal(id, Assert.Single(saved.Passwords).Id);
        Assert.Equal(ArchiveMessageTypes.PasswordsAdd, Assert.Single(listed.Sent).Type);
        Assert.DoesNotContain(typeof(ArchiveSavedPasswordDto).GetProperties(), p => p.PropertyType == typeof(string));
        // Nothing the Host sends back carries a password: the set DTO has no such member.
        Assert.DoesNotContain(typeof(ArchiveSetDto).GetProperties(), p => p.Name.Contains("Password") && p.PropertyType == typeof(string));
    }

    [Fact]
    public async Task The_client_reads_lists_and_settings_and_turns_refusals_into_words()
    {
        var host = new FakeHostConnection
        {
            Reply = r => r.Type switch
            {
                ArchiveMessageTypes.List => Reply(r, ArchiveMessageTypes.ListResult, new ArchiveListResult { Sets = [Set("waiting")] }),
                ArchiveMessageTypes.SettingsGet => Reply(r, ArchiveMessageTypes.SettingsResult, new ArchiveSettingsResult
                    { Ok = true, Settings = new ArchiveSettingsDto { AutoExtract = true }, Tool = new ArchiveToolStatusDto { Found = true } }),
                ArchiveMessageTypes.SettingsSet => Reply(r, ArchiveMessageTypes.ActionResult, new ArchiveActionResult { Ok = false, Error = "The request could not be read." }),
                _ => Reply(r, MessageTypes.HostActionResult, new HostActionResult { Ok = false, Message = "No archives here." }),
            },
        };
        var client = new ArchiveClient(host);
        Assert.Single((await client.ListAsync()).Sets);
        Assert.True((await client.SettingsAsync()).Settings.AutoExtract);
        var refused = await Assert.ThrowsAsync<InvalidOperationException>(() => client.UpdateSettingsAsync(new ArchiveSettingsUpdate { DeleteArchives = true }));
        Assert.Equal("The request could not be read.", refused.Message);
        var unknown = await Assert.ThrowsAsync<InvalidOperationException>(() => client.RetryAsync(Guid.NewGuid()));
        Assert.Equal("No archives here.", unknown.Message);
    }

    [Fact]
    public void Archives_are_offered_only_when_the_hello_lists_them()
    {
        Assert.False(ArchiveClient.Offered(null));
        Assert.False(ArchiveClient.Offered(new HostHelloDto { Capabilities = ["remote"] }));
        Assert.True(ArchiveClient.Offered(new HostHelloDto { Capabilities = ["remote", "archives"] }));
    }

    [Fact]
    public void A_set_lands_on_the_row_of_its_first_volume_only()
    {
        Guid first = Guid.NewGuid(), second = Guid.NewGuid(), third = Guid.NewGuid();
        var jobs = new Dictionary<Guid, JobDto>
        {
            [first] = Job(first, "project-files.part1.rar", 1_000_000_000),
            [second] = Job(second, "project-files.part2.rar", 600_000_000),
            [third] = Job(third, "project-files.part3.rar", 500_000_000),
        };
        // The Host lists the jobs in no promised order: the first volume is found by its file name.
        var set = Set("failed", "", third, second, first); set.NeedsPassword = true;
        var store = new ArchiveStore(id => jobs.GetValueOrDefault(id));
        var raised = 0;
        store.Changed += () => raised++;
        store.Load(new ArchiveListResult { Sets = [set] });

        Assert.Equal(1, raised);
        Assert.Same(set, store.SetFor(first));
        Assert.Null(store.SetFor(second));
        Assert.Null(store.SetFor(third));
        Assert.Null(store.SetFor(Guid.NewGuid()));
        Assert.Equal("Needs a password · 3 parts · 2.1 GB", store.InfoFor(first)!.Subtitle);
        Assert.Null(store.InfoFor(second));
    }

    [Fact]
    public void Without_a_known_name_the_sets_first_job_stands_in_and_changes_replace_or_remove()
    {
        Guid a = Guid.NewGuid(), b = Guid.NewGuid();
        var store = new ArchiveStore(_ => null);
        var set = Set("extracting", "", a, b);
        store.Apply(new ArchiveChangedDto { Set = set });
        Assert.Same(set, store.SetFor(a));
        Assert.Null(store.SetFor(b));

        var later = Set("completed", "Extracted", a, b); later.Id = set.Id;
        store.Apply(new ArchiveChangedDto { Set = later });
        Assert.Equal(1, store.Count);
        Assert.Equal("Extracted", store.InfoFor(a)!.Subtitle);

        store.Apply(new ArchiveChangedDto { RemovedId = set.Id });
        Assert.Equal(0, store.Count);
        Assert.Null(store.InfoFor(a));

        store.Apply(new ArchiveChangedDto { Set = set });
        store.Clear();
        Assert.Null(store.SetFor(a));
    }
}

using System.Globalization;
using DownloadManager.Contracts;
using DownloadManager.Desktop.WinUI;

namespace DownloadManager.Core.Tests;

public sealed class RemoteStateTests
{
    private static RemoteJobDto Job(Guid id, int minute = 0, string state = "waiting") => new()
    {
        Id = id,
        State = state,
        UpdatedAt = new DateTimeOffset(2026, 11, 6, 12, minute, 0, TimeSpan.Zero),
    };

    private static RemoteChangedDto Event(RemoteJobDto job) => new() { Change = "job", Job = job };

    [Fact]
    public void AnEventNewerThanTheListThatFollowsItIsKept()
    {
        var state = new RemoteState();
        var id = Guid.NewGuid();
        state.BeginJobList();

        state.Apply(Event(Job(id, minute: 5)));
        state.ApplyJobList([Job(id, minute: 1)]);

        Assert.Equal(5, Assert.Single(state.Jobs).UpdatedAt.Minute);
    }

    [Fact]
    public void AJobRemovedWhileTheListWasOnItsWayDoesNotComeBack()
    {
        var state = new RemoteState();
        var id = Guid.NewGuid();
        state.ApplyJobReply(Job(id));
        state.BeginJobList();

        state.Apply(new RemoteChangedDto { Change = "removed", JobId = id });
        state.ApplyJobList([Job(id)]);

        Assert.Empty(state.Jobs);
    }

    [Fact]
    public void AJobCreatedWhileTheListWasOnItsWayStays()
    {
        var state = new RemoteState();
        var id = Guid.NewGuid();
        state.BeginJobList();

        state.Apply(Event(Job(id)));
        state.ApplyJobList([]);

        Assert.Single(state.Jobs);
    }

    [Fact]
    public void AListReplacesWhatTheHostNoLongerHas()
    {
        var state = new RemoteState();
        state.ApplyJobReply(Job(Guid.NewGuid()));
        var kept = Guid.NewGuid();
        state.BeginJobList();

        state.ApplyJobList([Job(kept)]);

        Assert.Equal(kept, Assert.Single(state.Jobs).Id);
    }

    [Fact]
    public void AnOlderReplyDoesNotUndoAnEvent()
    {
        var state = new RemoteState();
        var id = Guid.NewGuid();
        state.Apply(Event(Job(id, minute: 9)));

        state.ApplyJobReply(Job(id, minute: 2));

        Assert.Equal(9, Assert.Single(state.Jobs).UpdatedAt.Minute);
    }

    [Fact]
    public void AnEventAlwaysCountsEvenWithAnOlderClock()
    {
        var state = new RemoteState();
        var id = Guid.NewGuid();
        state.Apply(Event(Job(id, minute: 9)));

        state.Apply(Event(Job(id, minute: 2)));

        Assert.Equal(2, Assert.Single(state.Jobs).UpdatedAt.Minute);
    }

    [Fact]
    public void AJobInTheRemovedStateLeavesTheList()
    {
        var state = new RemoteState();
        var id = Guid.NewGuid();
        state.Apply(Event(Job(id)));

        state.Apply(Event(Job(id, state: "removed")));

        Assert.Empty(state.Jobs);
    }

    [Fact]
    public void ArchiveEventsFollowTheSameRules()
    {
        var state = new RemoteState();
        var changing = new ArchiveSetDto { Id = Guid.NewGuid(), UpdatedAt = DateTimeOffset.UnixEpoch.AddHours(2) };
        var removed = new ArchiveSetDto { Id = Guid.NewGuid() };
        state.ApplyArchiveReply(removed);
        state.BeginArchiveList();

        state.Apply(new ArchiveChangedDto { Set = changing });
        state.Apply(new ArchiveChangedDto { RemovedId = removed.Id });
        state.ApplyArchiveList([new ArchiveSetDto { Id = changing.Id, UpdatedAt = DateTimeOffset.UnixEpoch }, new ArchiveSetDto { Id = removed.Id }]);

        Assert.Equal(changing.UpdatedAt, Assert.Single(state.ArchiveSets).UpdatedAt);
    }

    [Fact]
    public void AccountChangesAreAnnouncedPerProvider()
    {
        var state = new RemoteState();
        var heard = new List<string>();
        state.AccountChanged += account => heard.Add(account.Provider);
        state.ApplyAccounts([new RemoteAccountDto { Provider = "a" }, new RemoteAccountDto { Provider = "b", Capabilities = ["usenet"] }], wholeList: true);

        state.Apply(new RemoteChangedDto { Change = "account", Account = new RemoteAccountDto { Provider = "a", HasKey = true } });

        Assert.Equal(["a", "b", "a"], heard);
        Assert.True(state.FindAccount("A")!.HasKey);
        Assert.True(state.HasCapability("usenet"));
        Assert.False(state.HasCapability("torrents"));
    }

    [Fact]
    public void AWholeAccountListDropsAProviderThatIsGone()
    {
        var state = new RemoteState();
        state.ApplyAccounts([new RemoteAccountDto { Provider = "a" }, new RemoteAccountDto { Provider = "b" }], wholeList: true);

        state.ApplyAccounts([new RemoteAccountDto { Provider = "b" }], wholeList: true);

        Assert.Equal("b", Assert.Single(state.Accounts).Provider);
    }
}

public sealed class RemoteTextTests
{
    private static readonly DateTimeOffset Now = new(2026, 11, 6, 12, 0, 0, TimeSpan.Zero);
    private static readonly CultureInfo English = CultureInfo.GetCultureInfo("en-US");

    private static RemoteAccountDto Account(Action<RemoteAccountDto>? change = null)
    {
        var account = new RemoteAccountDto { Provider = "acme", ProviderName = "AcmeBox", HasKey = true };
        change?.Invoke(account);
        return account;
    }

    [Fact]
    public void TheHostsOwnWordsAreUsedWhenItSendsThem()
        => Assert.Equal("TorBox refused the API key. Check it in Options.", RemoteText.Failure("TorBox refused the API key. Check it in Options.", "authentication", "TorBox"));

    [Fact]
    public void AnAddressInAFailureNeverReachesTheScreen()
    {
        var shown = RemoteText.Failure("Could not fetch https://cdn.example/dld/abc?token=SECRET or magnet:?xt=urn:btih:abcdef token=SECRET2");

        Assert.DoesNotContain("SECRET", shown);
        Assert.DoesNotContain("cdn.example", shown);
        Assert.DoesNotContain("btih", shown);
    }

    [Theory]
    [InlineData("authentication")]
    [InlineData("accountLimit")]
    [InlineData("accountBusy")]
    [InlineData("rateLimited")]
    [InlineData("notFound")]
    [InlineData("rejected")]
    [InlineData("serviceError")]
    [InlineData("network")]
    [InlineData("badResponse")]
    [InlineData("notCached")]
    [InlineData("alreadyExists")]
    public void EveryFailureKindHasWordsNamingTheProvider(string kind)
        => Assert.Contains("AcmeBox", RemoteText.Failure(null, kind, "AcmeBox"));

    [Fact]
    public void ALinkPasswordIsNotCalledAnArchivePassword()
        => Assert.Contains("isn't an archive password", RemoteText.Failure(null, "linkPasswordRequired"));

    [Fact]
    public void AnUnknownFailureKindStillSaysSomething()
        => Assert.Equal("That could not be done.", RemoteText.Failure("  ", "somethingNew", "AcmeBox"));

    [Fact]
    public void AKeyThatCannotBeReadIsExplainedAndHasToBeEnteredAgain()
    {
        var view = RemoteText.Account(Account(account => account.KeyUnreadable = true), Now, English);

        Assert.True(view.KeyStatusIsProblem);
        Assert.Contains("can't be read on this Windows account", view.KeyStatus);
        Assert.Contains("Enter the key again", view.KeyStatus);
        Assert.False(view.CanValidate);
        Assert.True(view.CanClear);
    }

    [Fact]
    public void NoKeyOffersNeitherValidateNorClear()
    {
        var view = RemoteText.Account(Account(account => account.HasKey = false), Now, English);

        Assert.False(view.KeyStatusIsProblem);
        Assert.Contains("No API key is saved", view.KeyStatus);
        Assert.False(view.CanValidate);
        Assert.False(view.CanClear);
    }

    [Fact]
    public void AnUncheckedKeySaysSoAndClaimsNothingAboutThePlan()
    {
        var view = RemoteText.Account(Account(), Now, English);

        Assert.True(view.CanValidate);
        Assert.True(view.CanClear);
        Assert.Contains("Not checked", view.AccountDetails);
        Assert.Contains("AcmeBox", view.AccountDetails);
        Assert.DoesNotContain("Plan", view.AccountDetails);
    }

    [Fact]
    public void ACheckedKeyShowsPlanAndExpiry()
    {
        var view = RemoteText.Account(Account(account =>
        {
            account.CheckedAt = Now;
            account.Plan = "Pro";
            account.Premium = true;
            account.PremiumUntil = new DateTimeOffset(2030, 1, 15, 12, 0, 0, TimeSpan.Zero);
        }), Now, English);

        Assert.Contains("Plan: Pro", view.AccountDetails);
        Assert.Contains("Premium until", view.AccountDetails);
        Assert.Contains("2030", view.AccountDetails);
        Assert.Contains("Checked", view.AccountDetails);
    }

    [Fact]
    public void AnExpiryInThePastIsNotCalledPremium()
    {
        var view = RemoteText.Account(Account(account =>
        {
            account.CheckedAt = Now;
            account.PremiumUntil = Now.AddDays(-3);
        }), Now, English);

        Assert.Contains("Premium ended on", view.AccountDetails);
        Assert.DoesNotContain("Premium until", view.AccountDetails);
    }

    [Fact]
    public void ACooldownShowsOnlyWhileItLasts()
    {
        var running = RemoteText.Account(Account(account => { account.CheckedAt = Now; account.CooldownUntil = Now.AddHours(1); }), Now, English);
        var over = RemoteText.Account(Account(account => { account.CheckedAt = Now; account.CooldownUntil = Now.AddHours(-1); }), Now, English);

        Assert.Contains("cooldown", running.AccountDetails);
        Assert.DoesNotContain("cooldown", over.AccountDetails);
    }

    [Fact]
    public void TheProviderNameComesFromTheHost()
    {
        var view = RemoteText.Account(Account(), Now, English);

        Assert.Equal("AcmeBox account", view.PageTitle);
        Assert.DoesNotContain("TorBox", string.Join(" ", view.KeyStatus, view.AccountDetails, view.PageTitle, RemoteText.Validated(Account())));
    }

    [Fact]
    public void TheToolStatusIsTheHostsWords()
    {
        var found = RemoteText.ToolStatus(new ArchiveToolStatusDto { Found = true, Path = "C:\\x\\7z.exe", Message = "Extraction uses the 7-Zip that comes with Inbound." });
        Assert.Equal(("Extraction uses the 7-Zip that comes with Inbound.", false), found);

        var missing = RemoteText.ToolStatus(new ArchiveToolStatusDto { Message = "The bundled 7-Zip is missing. Repair or reinstall Inbound." });
        Assert.True(missing.IsProblem);
        Assert.Contains("reinstall", missing.Text);
    }
}

public sealed class ArchiveFormTests
{
    private static ArchiveSettingsDto Saved() => new()
    {
        AutoExtract = false,
        Destination = "nextToArchive",
        Folder = "D:\\Out",
        PreserveFolders = true,
        ConflictPolicy = "rename",
        DeleteArchives = false,
    };

    private static ArchiveFormValues Form() => ArchiveForm.FromSettings(Saved());

    [Fact]
    public void TheFormStartsFromWhatIsSaved()
    {
        var form = ArchiveForm.FromSettings(new ArchiveSettingsDto
        {
            AutoExtract = true,
            Destination = "folder",
            Folder = "D:\\Out",
            PreserveFolders = false,
            ConflictPolicy = "skip",
            DeleteArchives = true,
        });

        Assert.Equal(new ArchiveFormValues(true, 2, "D:\\Out", false, 2, true), form);
    }

    [Fact]
    public void WordsThisWindowDoesNotKnowFallToTheFirstChoice()
    {
        var form = ArchiveForm.FromSettings(new ArchiveSettingsDto { Destination = "somewhereNew", ConflictPolicy = "somethingNew" });

        Assert.Equal(0, form.DestinationIndex);
        Assert.Equal(0, form.ConflictIndex);
    }

    [Fact]
    public void NothingChangedSendsNothing()
    {
        Assert.Null(ArchiveForm.Changes(Saved(), Form(), out var error));
        Assert.Null(error);
    }

    [Fact]
    public void OnlyWhatChangedIsSent()
    {
        var update = ArchiveForm.Changes(Saved(), Form() with { AutoExtract = true }, out _);

        Assert.NotNull(update);
        Assert.True(update.AutoExtract);
        Assert.Null(update.Destination);
        Assert.Null(update.Folder);
        Assert.Null(update.PreserveFolders);
        Assert.Null(update.ConflictPolicy);
        Assert.Null(update.DeleteArchives);
    }

    [Fact]
    public void EachChoiceTurnsIntoTheHostsWord()
    {
        var update = ArchiveForm.Changes(Saved(), Form() with
        {
            DestinationIndex = 1,
            PreserveFolders = false,
            ConflictIndex = 1,
            DeleteArchives = true,
        }, out _);

        Assert.NotNull(update);
        Assert.Equal("subfolder", update.Destination);
        Assert.False(update.PreserveFolders);
        Assert.Equal("overwrite", update.ConflictPolicy);
        Assert.True(update.DeleteArchives);
    }

    [Fact]
    public void AChosenFolderHasToBeThere()
    {
        Assert.Null(ArchiveForm.Changes(Saved(), Form() with { DestinationIndex = 2, Folder = " " }, out var empty));
        Assert.Equal("Choose the folder to extract to.", empty);

        Assert.Null(ArchiveForm.Changes(Saved(), Form() with { DestinationIndex = 2, Folder = "relative\\path" }, out var relative));
        Assert.Contains("full folder path", relative);
    }

    [Fact]
    [Trait("Platform", "Windows")] // desktop code: Windows paths, Explorer or named pipes
    public void AChosenFolderIsSentWithItsDestination()
    {
        var update = ArchiveForm.Changes(Saved(), Form() with { DestinationIndex = 2, Folder = "E:\\New" }, out _);

        Assert.NotNull(update);
        Assert.Equal("folder", update.Destination);
        Assert.Equal("E:\\New", update.Folder);
    }

    [Fact]
    public void TheFolderBoxIsIgnoredWhileAnotherDestinationIsChosen()
    {
        Assert.Null(ArchiveForm.Changes(Saved(), Form() with { DestinationIndex = 0, Folder = "not a path" }, out var error));
        Assert.Null(error);
    }
}

public sealed class CrashLogTextTests
{
    [Fact]
    public void AddressesInAnExceptionAreMasked()
    {
        var failure = new InvalidOperationException(
            "Request to https://cdn.example.test/dld/abc?token=SECRETKEY&x=1 failed. Also magnet:?xt=urn:btih:SECRETHASH and token=SECRETPAIR");

        var entry = CrashLogText.Entry(new DateTimeOffset(2026, 11, 6, 1, 2, 3, TimeSpan.Zero), failure);

        Assert.DoesNotContain("SECRETKEY", entry);
        Assert.DoesNotContain("SECRETHASH", entry);
        Assert.DoesNotContain("SECRETPAIR", entry);
        Assert.Contains("2026-11-06T01:02:03", entry);
        Assert.Contains(nameof(InvalidOperationException), entry);
        Assert.Contains("cdn.example.test", entry);
    }

    [Fact]
    public void NoExceptionStillWritesAnEntry()
        => Assert.Contains("2026", CrashLogText.Entry(new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero), null));
}

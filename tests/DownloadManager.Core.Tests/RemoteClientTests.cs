using System.Threading.Channels;
using DownloadManager.Contracts;
using DownloadManager.Desktop.WinUI;

namespace DownloadManager.Core.Tests;

public sealed class RemoteClientTests
{
    private static readonly TimeSpan Patience = TimeSpan.FromSeconds(10);

    [Fact]
    public async Task AFullPipeIsNotReportedAsAHostThatIsDown()
    {
        var host = new FakeHost { Busy = true };
        await using var client = host.NewClient(new UiQueue());

        var slow = await client.GetItemsAsync(new RemoteItemRequest { Provider = "torbox", Kind = "torrents" });
        var quick = await client.ListAsync();

        Assert.Equal(RemoteText.HostBusy, slow.Error);
        Assert.Equal(RemoteText.HostBusy, quick.Error);
    }

    [Fact]
    [Trait("Platform", "Windows")] // desktop code: Windows paths, Explorer or named pipes
    public async Task APipeWithEveryInstanceTakenStillExistsAndOneNobodyMadeDoesNot()
    {
        var name = "inbound-test-" + Guid.NewGuid().ToString("N");
        Assert.False(HostPipeBusyException.PipeExists(name));

        await using var server = new System.IO.Pipes.NamedPipeServerStream(name, System.IO.Pipes.PipeDirection.InOut, 1, System.IO.Pipes.PipeTransmissionMode.Byte, System.IO.Pipes.PipeOptions.Asynchronous);
        var accepted = server.WaitForConnectionAsync();
        await using var client = new System.IO.Pipes.NamedPipeClientStream(".", name, System.IO.Pipes.PipeDirection.InOut, System.IO.Pipes.PipeOptions.Asynchronous);
        await client.ConnectAsync(5000);
        await accepted;

        Assert.True(HostPipeBusyException.PipeExists(name));
    }

    [Fact]
    public void ATimeoutAfterTheHostWasReachedIsNotReportedAsAHostThatIsDown()
    {
        Assert.Equal(RemoteText.HostTooSlow, RemoteClient.DescribeFailure(new TimeoutException(), reachedHost: true));
        Assert.Equal(RemoteText.HostNotRunning, RemoteClient.DescribeFailure(new TimeoutException(), reachedHost: false));
    }

    [Fact]
    public void ArchiveRepliesReadOnAnotherConnectionAnswerInWords()
    {
        var set = new ArchiveSetDto { Id = Guid.NewGuid(), Name = "archive", State = "downloaded" };

        var package = Guid.NewGuid();
        var listed = RemoteClient.ReadArchiveListReply(IpcMessage.Create(ArchiveMessageTypes.ListResult, new ArchiveListResult { Sets = [set], PackagesWithPassword = [package] }));
        var unknown = RemoteClient.ReadArchiveListReply(IpcMessage.Create(MessageTypes.HostActionResult, new HostActionResult { Ok = false, Message = "Unknown message type." }));
        var saved = RemoteClient.ReadArchiveActionReply(IpcMessage.Create(ArchiveMessageTypes.ActionResult, new ArchiveActionResult { Ok = true, Set = set }));
        var refused = RemoteClient.ReadArchiveActionReply(IpcMessage.Create(MessageTypes.HostActionResult, new HostActionResult { Ok = false, Message = "Unknown message type." }));

        Assert.True(listed.Ok);
        Assert.Equal(set.Id, Assert.Single(listed.Sets).Id);
        Assert.Equal([package], listed.PackagesWithPassword);
        Assert.Equal(RemoteText.HostDoesNotOffer, unknown.Error);
        Assert.Empty(unknown.PackagesWithPassword);
        Assert.True(saved.Ok);
        Assert.Equal(set.Id, saved.Set?.Id);
        Assert.False(refused.Ok);
        Assert.Equal(RemoteText.HostDoesNotOffer, refused.Error);
    }

    [Fact]
    public async Task SavingAKeySaysSoAfterTheAccountIsApplied()
    {
        var host = new FakeHost();
        var ui = new UiQueue();
        await using var client = host.NewClient(ui);
        var heard = new List<string?>();
        var accountsWhenHeard = -1;
        client.State.KeySaved += provider =>
        {
            heard.Add(provider);
            accountsWhenHeard = client.State.Accounts.Count;
        };

        client.Start();
        await ui.UntilAsync(() => client.State.IsReady);
        await client.GetAccountsAsync("torbox");
        await client.ValidateAsync("torbox");
        ui.Flush();
        Assert.Empty(heard);

        await client.SetKeyAsync("typed", "torbox");
        await ui.UntilAsync(() => heard.Count == 1);

        Assert.Equal("torbox", heard[0]);
        Assert.Equal(1, accountsWhenHeard);
    }

    [Fact]
    public async Task AKeyTheHostRefusesToSaveSaysNothingOfTheKind()
    {
        var host = new FakeHost();
        host.Answer(MessageTypes.RemoteAccountSetKey, MessageTypes.RemoteAccountResult, new RemoteAccountResult { Ok = false, Error = "No." });
        var ui = new UiQueue();
        await using var client = host.NewClient(ui);
        var heard = 0;
        client.State.KeySaved += _ => heard++;
        client.Start();
        await ui.UntilAsync(() => client.State.IsReady);

        var result = await client.SetKeyAsync("typed", "torbox");
        ui.Flush();

        Assert.False(result.Ok);
        Assert.Equal(0, heard);
    }

    [Fact]
    public async Task ConnectingReadsAccountsJobsAndArchiveSets()
    {
        var host = new FakeHost();
        var ui = new UiQueue();
        await using var client = host.NewClient(ui);

        client.Start();

        await ui.UntilAsync(() => client.State.IsReady);
        Assert.True(client.State.IsConnected);
        Assert.True(client.State.RemoteSupported);
        Assert.True(client.State.ArchivesSupported);
        Assert.Equal("torbox", Assert.Single(client.State.Accounts).Provider);
        Assert.Equal(host.Job.Id, Assert.Single(client.State.Jobs).Id);
        Assert.Equal(host.Set.Id, Assert.Single(client.State.ArchiveSets).Id);
    }

    [Fact]
    public async Task StateOnlyChangesOnTheUiThread()
    {
        var host = new FakeHost();
        var ui = new UiQueue();
        await using var client = host.NewClient(ui);

        client.Start();
        await host.UntilAsync(() => host.Received(MessageTypes.RemoteList) > 0);
        await Task.Delay(50);

        // Replies have been read, but nothing ran what they posted.
        Assert.False(client.State.IsConnected);
        Assert.Empty(client.State.Accounts);

        await ui.UntilAsync(() => client.State.Accounts.Count == 1);
    }

    [Fact]
    public async Task AHostWithoutRemoteIsNeverAskedAboutIt()
    {
        var host = new FakeHost { Capabilities = ["jobs", "settings"] };
        var ui = new UiQueue();
        await using var client = host.NewClient(ui);

        client.Start();

        await ui.UntilAsync(() => client.State.IsReady);
        Assert.False(client.State.RemoteSupported);
        Assert.Empty(client.State.Accounts);
        Assert.Equal(0, host.Received(MessageTypes.RemoteAccountGet));
        Assert.True(client.State.ArchivesSupported);
    }

    [Fact]
    public async Task AccountsThatCouldNotBeReadKeepEveryLineGoingToTheHost()
    {
        var host = new FakeHost();
        host.Answer(MessageTypes.RemoteAccountGet, MessageTypes.RemoteAccountResult, new RemoteAccountResult { Ok = false, Error = "No." });
        var ui = new UiQueue();
        await using var client = host.NewClient(ui);

        Assert.True(RemoteRules.ProviderAddApplies(client.State));
        client.Start();

        await ui.UntilAsync(() => client.State.IsReady);
        Assert.False(client.State.AccountsKnown);
        Assert.Empty(client.State.Accounts);
        Assert.True(RemoteRules.ProviderAddApplies(client.State));
    }

    [Fact]
    public async Task AHostWithNoRemoteSupportLetsAddingStayOrdinary()
    {
        var host = new FakeHost { Capabilities = ["jobs", "settings"] };
        var ui = new UiQueue();
        await using var client = host.NewClient(ui);

        client.Start();

        await ui.UntilAsync(() => client.State.IsReady);
        Assert.True(client.State.AccountsKnown);
        Assert.False(RemoteRules.ProviderAddApplies(client.State));
    }

    [Theory]
    [InlineData(true, false, true)]
    [InlineData(false, false, false)]
    [InlineData(true, true, false)]
    public async Task AnAccountReadThatWorkedDecidesByTheKey(bool hasKey, bool unreadable, bool expected)
    {
        var host = new FakeHost();
        host.Account.HasKey = hasKey;
        host.Account.KeyUnreadable = unreadable;
        var ui = new UiQueue();
        await using var client = host.NewClient(ui);

        client.Start();

        await ui.UntilAsync(() => client.State.IsReady);
        Assert.True(client.State.AccountsKnown);
        Assert.Equal(expected, RemoteRules.ProviderAddApplies(client.State));
    }

    [Fact]
    public async Task AHostThatDoesNotKnowArchiveMessagesHidesThem()
    {
        var host = new FakeHost { KnowsArchives = false };
        var ui = new UiQueue();
        await using var client = host.NewClient(ui);

        client.Start();

        await ui.UntilAsync(() => client.State.IsReady);
        Assert.False(client.State.ArchivesSupported);
        Assert.True(client.State.RemoteSupported);

        var result = await client.GetArchiveSettingsAsync();

        Assert.False(result.Ok);
        Assert.Equal(RemoteText.HostDoesNotOffer, result.Error);
    }

    [Fact]
    public async Task ASlowCallLeavesTheEventConnectionFree()
    {
        var host = new FakeHost();
        var ui = new UiQueue();
        await using var client = host.NewClient(ui);
        client.Start();
        await ui.UntilAsync(() => client.State.IsReady);
        var answerValidate = host.Hold(MessageTypes.RemoteAccountValidate);

        var validating = client.ValidateAsync();
        await host.UntilAsync(() => host.Received(MessageTypes.RemoteAccountValidate) == 1);

        // A quick call still gets its answer while the slow one is out.
        var settings = await client.GetArchiveSettingsAsync().WaitAsync(Patience);
        Assert.True(settings.Ok);
        Assert.False(validating.IsCompleted);

        Assert.NotSame(host.ConnectionOf(MessageTypes.RemoteAccountValidate), host.ConnectionOf(ArchiveMessageTypes.SettingsGet));
        Assert.Same(host.Connections[0], host.ConnectionOf(ArchiveMessageTypes.SettingsGet));

        answerValidate();
        Assert.True((await validating.WaitAsync(Patience)).Ok);
        await host.UntilAsync(() => host.ConnectionOf(MessageTypes.RemoteAccountValidate).Disposed);
        Assert.False(host.Connections[0].Disposed);
    }

    [Fact]
    public async Task AClassifierAsksManyThingsOnOneConnectionOfItsOwn()
    {
        var host = new FakeHost();
        var ui = new UiQueue();
        await using var client = host.NewClient(ui);
        client.Start();
        await ui.UntilAsync(() => client.State.IsReady);
        var before = host.Connections.Count;

        await using (var classifier = client.OpenClassifier())
        {
            Assert.Equal(before, host.Connections.Count);
            var first = await classifier.ClassifyAsync("one", CancellationToken.None).WaitAsync(Patience);
            var second = await classifier.ClassifyAsync("two", CancellationToken.None).WaitAsync(Patience);

            Assert.True(first.Ok);
            Assert.True(second.Ok);
            Assert.False(classifier.Broken);
            Assert.Equal(before + 1, host.Connections.Count);
            Assert.Equal(2, host.Received(MessageTypes.RemoteClassify));
            Assert.NotSame(host.Connections[0], host.ConnectionOf(MessageTypes.RemoteClassify));
        }

        Assert.True(host.Connections[^1].Disposed);
    }

    [Fact]
    public async Task AClassifierForAddingAsksForFilesOnlyWhereTheyCanBeKnown()
    {
        // For a magnet that is a call to the provider, so nothing else asks for it.
        var host = new FakeHost();
        var ui = new UiQueue();
        await using var client = host.NewClient(ui);
        client.Start();
        await ui.UntilAsync(() => client.State.IsReady);
        const string Magnet = "magnet:?xt=urn:btih:0123456789abcdef0123456789abcdef01234567";

        async Task<bool> AskedForFiles(Func<string, Task> classify, string text)
        {
            await classify(text).WaitAsync(Patience);
            return host.LastPayload(MessageTypes.RemoteClassify) is { } payload &&
                   payload.TryGetProperty("withFiles", out var withFiles) && withFiles.GetBoolean();
        }

        await using (var adding = client.OpenClassifier(withFiles: true))
        {
            Assert.True(await AskedForFiles(text => adding.ClassifyAsync(text, CancellationToken.None), Magnet));
            Assert.True(await AskedForFiles(text => adding.ClassifyAsync(text, CancellationToken.None), @"C:\Torrents\pack.torrent"));
            Assert.False(await AskedForFiles(text => adding.ClassifyAsync(text, CancellationToken.None), "https://example.com/file.zip"));
        }

        await using (var plain = client.OpenClassifier())
        {
            Assert.False(await AskedForFiles(text => plain.ClassifyAsync(text, CancellationToken.None), Magnet));
        }

        Assert.True(await AskedForFiles(text => client.ClassifyAsync(text, withFiles: true), Magnet));
        Assert.False(await AskedForFiles(text => client.ClassifyAsync(text), Magnet));
    }

    [Fact]
    public async Task ClosingAClassifierEndsItsCallAndGivesItsTurnBack()
    {
        var host = new FakeHost();
        var ui = new UiQueue();
        await using var client = host.NewClient(ui);
        client.Start();
        await ui.UntilAsync(() => client.State.IsReady);
        host.Hold(MessageTypes.RemoteClassify);
        using var cancel = new CancellationTokenSource();

        var classifier = client.OpenClassifier();
        var asking = classifier.ClassifyAsync("one", cancel.Token);
        await host.UntilAsync(() => host.Received(MessageTypes.RemoteClassify) == 1);
        cancel.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => asking.WaitAsync(Patience));
        Assert.True(classifier.Broken);
        await classifier.DisposeAsync();
        Assert.True(host.ConnectionOf(MessageTypes.RemoteClassify).Disposed);

        // Three more slow calls can run, so the turn it held came back.
        host.Answer(MessageTypes.RemoteFiles, MessageTypes.RemoteFilesResult, new RemoteFilesResultDto { Ok = true });
        var calls = Enumerable.Range(0, 4).Select(_ => client.GetFilesAsync(Guid.NewGuid())).ToArray();
        await Task.WhenAll(calls).WaitAsync(Patience);
        Assert.All(calls, call => Assert.True(call.Result.Ok));
    }

    [Fact]
    public async Task AClassifierWhoseHostGoesAwaySaysSoInWordsAndStops()
    {
        var host = new FakeHost();
        var ui = new UiQueue();
        await using var client = host.NewClient(ui);
        client.Start();
        await ui.UntilAsync(() => client.State.IsReady);
        host.Hold(MessageTypes.RemoteClassify);

        await using var classifier = client.OpenClassifier();
        var asking = classifier.ClassifyAsync("one", CancellationToken.None);
        await host.UntilAsync(() => host.Received(MessageTypes.RemoteClassify) == 1);
        host.ConnectionOf(MessageTypes.RemoteClassify).Drop();
        var result = await asking.WaitAsync(Patience);

        Assert.False(result.Ok);
        Assert.Equal(RemoteText.ClassifyLost, result.Error);
        Assert.True(classifier.Broken);
        Assert.Equal(RemoteText.ClassifyLost, (await classifier.ClassifyAsync("two", CancellationToken.None).WaitAsync(Patience)).Error);
        Assert.Equal(1, host.Received(MessageTypes.RemoteClassify));
    }

    [Fact]
    public async Task AClassifierOnAHostThatDoesntKnowTheMessageStops()
    {
        var host = new FakeHost();
        host.AnswerRaw(MessageTypes.RemoteClassify, request => IpcMessage.Create(MessageTypes.HostActionResult, new HostActionResult { Ok = false, Message = "Unknown message type." }, request.Id));
        var ui = new UiQueue();
        await using var client = host.NewClient(ui);
        client.Start();
        await ui.UntilAsync(() => client.State.IsReady);

        await using var classifier = client.OpenClassifier();
        var result = await classifier.ClassifyAsync("one", CancellationToken.None).WaitAsync(Patience);

        Assert.False(result.Ok);
        Assert.Equal(RemoteText.HostDoesNotOffer, result.Error);
        Assert.True(classifier.Broken);
    }

    [Fact]
    public async Task AClassifierThatCantConnectSaysSo()
    {
        var host = new FakeHost { Refuse = true };
        var ui = new UiQueue();
        await using var client = host.NewClient(ui);

        await using var classifier = client.OpenClassifier();
        var result = await classifier.ClassifyAsync("one", CancellationToken.None).WaitAsync(Patience);

        Assert.False(result.Ok);
        Assert.Equal(RemoteText.HostNotRunning, result.Error);
        Assert.True(classifier.Broken);
    }

    [Theory]
    [InlineData(MessageTypes.RemoteClassify)]
    [InlineData(MessageTypes.RemoteAdd)]
    [InlineData(MessageTypes.RemoteItemGet)]
    [InlineData(MessageTypes.RemoteFiles)]
    [InlineData(MessageTypes.RemoteSelect)]
    [InlineData(MessageTypes.RemoteDeleteRemote)]
    [InlineData(MessageTypes.RemoteAccountValidate)]
    [InlineData(MessageTypes.RemoteAccountGet + "?refresh")]
    public async Task EveryCallThatMayWaitOnTheNetworkGetsAConnectionOfItsOwn(string call)
    {
        var host = new FakeHost();
        var ui = new UiQueue();
        await using var client = host.NewClient(ui);
        client.Start();
        await ui.UntilAsync(() => client.State.IsReady);
        var before = host.Connections.Count;

        switch (call)
        {
            case MessageTypes.RemoteClassify: await client.ClassifyAsync("text"); break;
            case MessageTypes.RemoteAdd: await client.AddAsync(new RemoteAddRequestDto { Input = "input" }); break;
            case MessageTypes.RemoteItemGet: await client.GetItemsAsync(new RemoteItemRequest { Kind = "torrents" }); break;
            case MessageTypes.RemoteFiles: await client.GetFilesAsync(Guid.NewGuid()); break;
            case MessageTypes.RemoteSelect: await client.SelectAsync(new RemoteSelectRequest { JobId = Guid.NewGuid(), All = true }); break;
            case MessageTypes.RemoteDeleteRemote: await client.DeleteRemoteAsync(Guid.NewGuid(), confirmed: true); break;
            case MessageTypes.RemoteAccountValidate: await client.ValidateAsync(); break;
            default: await client.GetAccountsAsync(refresh: true); break;
        }

        Assert.Equal(before + 1, host.Connections.Count);
    }

    [Fact]
    public async Task CallsThatOnlyTouchLocalStateStayOnTheEventConnection()
    {
        var host = new FakeHost();
        var ui = new UiQueue();
        await using var client = host.NewClient(ui);
        client.Start();
        await ui.UntilAsync(() => client.State.IsReady);
        var before = host.Connections.Count;

        await client.GetAccountsAsync();
        await client.SetKeyAsync("typed-key");
        await client.ClearKeyAsync();
        await client.ListAsync();
        await client.RetryAsync(new RemoteRetryRequest { JobId = host.Job.Id });
        await client.CheckNowAsync();
        await client.RemoveAsync(host.Job.Id);
        await client.GetArchiveSettingsAsync();
        await client.SetArchiveSettingsAsync(new ArchiveSettingsUpdate { AutoExtract = true });
        await client.ListArchivesAsync();
        await client.ExtractAsync(new ArchiveExtractRequest { FilePath = "C:\\a.rar" });
        await client.SetArchivePasswordAsync(new ArchiveSetPasswordRequest { SetId = host.Set.Id, Password = "typed" });
        await client.RetryArchiveAsync(host.Set.Id);
        await client.CancelArchiveAsync(host.Set.Id);
        await client.RemoveArchiveAsync(host.Set.Id);

        Assert.Equal(before, host.Connections.Count);
    }

    [Fact]
    public async Task CancellingASlowCallClosesOnlyItsConnection()
    {
        var host = new FakeHost();
        var ui = new UiQueue();
        await using var client = host.NewClient(ui);
        client.Start();
        await ui.UntilAsync(() => client.State.IsReady);
        host.Hold(MessageTypes.RemoteItemGet);
        using var cancel = new CancellationTokenSource();

        var reading = client.GetItemsAsync(new RemoteItemRequest { Kind = "torrents" }, cancel.Token);
        await host.UntilAsync(() => host.Received(MessageTypes.RemoteItemGet) == 1);
        cancel.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => reading.WaitAsync(Patience));
        Assert.True(host.ConnectionOf(MessageTypes.RemoteItemGet).Disposed);
        Assert.False(host.Connections[0].Disposed);
        Assert.True((await client.GetArchiveSettingsAsync().WaitAsync(Patience)).Ok);
    }

    [Fact]
    public async Task SlowCallsWaitTheirTurnSoTheHostKeepsPipesForOthers()
    {
        var host = new FakeHost();
        var ui = new UiQueue();
        await using var client = host.NewClient(ui);
        client.Start();
        await ui.UntilAsync(() => client.State.IsReady);
        var answer = host.Hold(MessageTypes.RemoteClassify);

        var calls = Enumerable.Range(0, 6).Select(_ => client.ClassifyAsync("text")).ToList();
        await host.UntilAsync(() => host.Received(MessageTypes.RemoteClassify) == 3);
        await Task.Delay(100);
        Assert.Equal(3, host.Received(MessageTypes.RemoteClassify));

        answer();
        await Task.WhenAll(calls).WaitAsync(Patience);
        Assert.Equal(6, host.Received(MessageTypes.RemoteClassify));
    }

    [Fact]
    public async Task AfterAHostRestartItConnectsAgainAndReadsEverythingAnew()
    {
        var host = new FakeHost();
        var ui = new UiQueue();
        await using var client = host.NewClient(ui);
        client.Start();
        await ui.UntilAsync(() => client.State.IsReady);
        await ui.UntilAsync(() => client.State.Jobs.Count == 1);
        var replaced = new RemoteJobDto { Id = Guid.NewGuid(), Provider = "torbox", State = "waiting", Status = "Waiting" };
        host.Job = replaced;

        host.Connections[0].Drop();

        await ui.UntilAsync(() => host.Connections.Count == 2 && client.State.IsConnected &&
            client.State.Jobs.Count == 1 && client.State.Jobs[0].Id == replaced.Id);
    }

    [Fact]
    public async Task EventsReachTheStateInWhole()
    {
        var host = new FakeHost();
        var ui = new UiQueue();
        await using var client = host.NewClient(ui);
        client.Start();
        await ui.UntilAsync(() => client.State.IsReady && client.State.ArchiveSets.Count == 1);

        var job = new RemoteJobDto { Id = Guid.NewGuid(), Provider = "torbox", State = "waiting", Status = "Processing" };
        host.Connections[0].Push(IpcMessage.Create(MessageTypes.RemoteChanged, new RemoteChangedDto { Change = "job", Job = job }));
        await ui.UntilAsync(() => client.State.Jobs.Any(item => item.Id == job.Id));

        host.Connections[0].Push(IpcMessage.Create(MessageTypes.RemoteChanged, new RemoteChangedDto { Change = "removed", JobId = job.Id }));
        await ui.UntilAsync(() => client.State.Jobs.All(item => item.Id != job.Id));

        host.Connections[0].Push(IpcMessage.Create(MessageTypes.RemoteChanged, new RemoteChangedDto
        {
            Change = "account",
            Account = new RemoteAccountDto { Provider = "torbox", ProviderName = "TorBox", HasKey = true },
        }));
        await ui.UntilAsync(() => client.State.FindAccount("torbox")!.HasKey);

        var set = new ArchiveSetDto { Id = Guid.NewGuid(), Name = "new", State = "waiting" };
        host.Connections[0].Push(IpcMessage.Create(ArchiveMessageTypes.Changed, new ArchiveChangedDto { Set = set }));
        await ui.UntilAsync(() => client.State.ArchiveSets.Any(item => item.Id == set.Id));

        host.Connections[0].Push(IpcMessage.Create(ArchiveMessageTypes.Changed, new ArchiveChangedDto { RemovedId = set.Id }));
        await ui.UntilAsync(() => client.State.ArchiveSets.All(item => item.Id != set.Id));
    }

    [Fact]
    public async Task AMalformedEventDoesNotCostTheConnection()
    {
        var host = new FakeHost();
        var ui = new UiQueue();
        await using var client = host.NewClient(ui);
        client.Start();
        await ui.UntilAsync(() => client.State.IsReady);

        host.Connections[0].Push(new IpcMessage(Protocol.Version, MessageTypes.RemoteChanged, "x", null, null));
        var settings = await client.GetArchiveSettingsAsync().WaitAsync(Patience);

        Assert.True(settings.Ok);
        Assert.Single(host.Connections);
    }

    [Fact]
    public async Task AValidateAnswerFeedsTheStateBecauseTheHostSendsNoEventForIt()
    {
        var host = new FakeHost();
        var ui = new UiQueue();
        await using var client = host.NewClient(ui);
        client.Start();
        await ui.UntilAsync(() => client.State.IsReady);
        host.Account.Plan = "Pro";

        var result = await client.ValidateAsync();

        Assert.True(result.Ok);
        await ui.UntilAsync(() => client.State.FindAccount("torbox")?.Plan == "Pro");
    }

    [Fact]
    public async Task AnOkFalseReplyKeepsItsWordsAndFailureKind()
    {
        var host = new FakeHost();
        host.Answer(MessageTypes.RemoteAccountValidate, MessageTypes.RemoteAccountResult,
            new RemoteAccountResult { Ok = false, Error = "TorBox refused the API key.", FailureKind = "authentication" });
        var ui = new UiQueue();
        await using var client = host.NewClient(ui);

        var result = await client.ValidateAsync();

        Assert.False(result.Ok);
        Assert.Equal("TorBox refused the API key.", result.Error);
        Assert.Equal("authentication", result.FailureKind);
    }

    [Fact]
    public async Task AFailedArchiveSaveReadsAsAFailureEvenInAnotherResultType()
    {
        var host = new FakeHost();
        host.Answer(ArchiveMessageTypes.SettingsSet, ArchiveMessageTypes.ActionResult,
            new ArchiveActionResult { Ok = false, Error = "The change could not be saved: IOException." });
        var ui = new UiQueue();
        await using var client = host.NewClient(ui);

        var result = await client.SetArchiveSettingsAsync(new ArchiveSettingsUpdate { AutoExtract = true });

        Assert.False(result.Ok);
        Assert.Equal("The change could not be saved: IOException.", result.Error);
    }

    [Fact]
    public async Task AnUnknownMessageReplyBecomesPlainWords()
    {
        var host = new FakeHost();
        host.Answer(MessageTypes.RemoteClassify, MessageTypes.HostActionResult,
            new HostActionResult { Ok = false, Message = "Unknown message type 'remote.classify'." });
        var ui = new UiQueue();
        await using var client = host.NewClient(ui);

        var result = await client.ClassifyAsync("text");

        Assert.False(result.Ok);
        Assert.Equal(RemoteText.HostDoesNotOffer, result.Error);
    }

    [Fact]
    public async Task AHostThatIsNotRunningIsAPlainFailureAndNoException()
    {
        var host = new FakeHost { Refuse = true };
        var ui = new UiQueue();
        await using var client = host.NewClient(ui, quickTimeout: TimeSpan.FromMilliseconds(200));

        var slow = await client.ValidateAsync().WaitAsync(Patience);
        var quick = await client.GetArchiveSettingsAsync().WaitAsync(Patience);

        Assert.False(slow.Ok);
        Assert.Equal(RemoteText.HostNotRunning, slow.Error);
        Assert.False(quick.Ok);
        Assert.Equal(RemoteText.HostNotRunning, quick.Error);
    }

    [Fact]
    public async Task LosingTheConnectionDuringACallSaysItMayHaveGoneThrough()
    {
        var host = new FakeHost();
        var ui = new UiQueue();
        await using var client = host.NewClient(ui);
        client.Start();
        await ui.UntilAsync(() => client.State.IsReady);
        host.Hold(MessageTypes.RemoteAdd);
        host.Hold(MessageTypes.RemoteRetry);

        var adding = client.AddAsync(new RemoteAddRequestDto { Input = "input" });
        var retrying = client.RetryAsync(new RemoteRetryRequest { JobId = host.Job.Id });
        await host.UntilAsync(() => host.Received(MessageTypes.RemoteAdd) == 1 && host.Received(MessageTypes.RemoteRetry) == 1);
        host.ConnectionOf(MessageTypes.RemoteAdd).Drop();
        host.Connections[0].Drop();

        Assert.Equal(RemoteText.HostLost, (await adding.WaitAsync(Patience)).Error);
        Assert.Equal(RemoteText.HostLost, (await retrying.WaitAsync(Patience)).Error);
        Assert.Equal(1, host.Received(MessageTypes.RemoteAdd));
    }

    [Fact]
    public async Task AHostThatNeverAnswersTimesOutWithoutHoldingACallForever()
    {
        var host = new FakeHost();
        var ui = new UiQueue();
        await using var client = host.NewClient(ui, slowTimeout: TimeSpan.FromMilliseconds(100));
        host.Hold(MessageTypes.RemoteFiles);

        var result = await client.GetFilesAsync(Guid.NewGuid()).WaitAsync(Patience);

        Assert.False(result.Ok);
        Assert.Equal(RemoteText.HostTooSlow, result.Error);
        Assert.True(host.ConnectionOf(MessageTypes.RemoteFiles).Disposed);
    }

    [Fact]
    public async Task AnUnreadableAnswerIsAPlainFailure()
    {
        var host = new FakeHost();
        host.AnswerRaw(MessageTypes.RemoteClassify, request => new IpcMessage(Protocol.Version, MessageTypes.RemoteClassifyResult, "y", request.Id, null));
        var ui = new UiQueue();
        await using var client = host.NewClient(ui);

        var result = await client.ClassifyAsync("text");

        Assert.False(result.Ok);
        Assert.Equal(RemoteText.HostUnreadable, result.Error);
    }

    [Fact]
    public async Task NoExceptionWordsEverReachAMessage()
    {
        const string key = "the-key-being-saved";
        var host = new FakeHost();
        var ui = new UiQueue();
        await using var client = host.NewClient(ui);
        client.Start();
        await ui.UntilAsync(() => client.State.IsReady);
        host.Connections[0].FailNextSend = new IOException($"could not send {key}");

        var result = await client.SetKeyAsync(key).WaitAsync(Patience);

        Assert.False(result.Ok);
        Assert.DoesNotContain(key, result.Error);
    }

    [Fact]
    public async Task TheKeyGoesToTheHostAndComesBackNowhere()
    {
        const string key = "the-key-being-saved";
        var host = new FakeHost();
        var ui = new UiQueue();
        await using var client = host.NewClient(ui);
        client.Start();
        await ui.UntilAsync(() => client.State.IsReady);

        var result = await client.SetKeyAsync(key).WaitAsync(Patience);

        Assert.True(result.Ok);
        Assert.Equal(key, host.LastPayload(MessageTypes.RemoteAccountSetKey)!.Value.GetProperty("key").GetString());
        await ui.UntilAsync(() => client.State.FindAccount("torbox") is not null);
        var seen = System.Text.Json.JsonSerializer.Serialize(client.State.Accounts);
        Assert.DoesNotContain(key, seen);
    }

    // ---- remote.container.* ----------------------------------------------------------------------

    [Fact]
    public async Task AContainerIsReadOnAConnectionOfItsOwnWhichClosingEnds()
    {
        var host = new FakeHost();
        var ui = new UiQueue();
        await using var client = host.NewClient(ui);
        client.Start();
        await ui.UntilAsync(() => client.State.IsReady);
        host.Hold(MessageTypes.RemoteContainerPreview);
        using var closing = new CancellationTokenSource();

        var read = client.PreviewContainerAsync(@"D:\files\invented.ccf", closing.Token);
        await host.UntilAsync(() => host.Received(MessageTypes.RemoteContainerPreview) == 1);

        Assert.NotSame(host.Connections[0], host.ConnectionOf(MessageTypes.RemoteContainerPreview));
        Assert.False(host.ConnectionOf(MessageTypes.RemoteContainerPreview).Disposed);

        // The main connection isn't held up by it.
        Assert.True((await client.ListAsync().WaitAsync(Patience)).Ok);

        closing.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => read.WaitAsync(Patience));
        await host.UntilAsync(() => host.ConnectionOf(MessageTypes.RemoteContainerPreview).Disposed);
        Assert.False(host.Connections[0].Disposed);
    }

    [Fact]
    public async Task ANetworkPathGoesToTheHostUntouchedAndItsRefusalComesBackInWords()
    {
        const string Path = @"\\files.invalid\share\invented.ccf";
        const string Refusal = "Only files on a drive of this computer can be opened, not ones on a network path.";
        var host = new FakeHost();
        host.Answer(MessageTypes.RemoteContainerPreview, MessageTypes.RemoteContainerPreviewResult, new RemoteContainerPreviewResult { Error = Refusal });
        var ui = new UiQueue();
        await using var client = host.NewClient(ui);

        var result = await client.PreviewContainerAsync(Path).WaitAsync(Patience);
        var view = RemoteText.ContainerRead(result, "invented.ccf");

        Assert.Equal(Path, host.LastPayload(MessageTypes.RemoteContainerPreview)!.Value.GetProperty("path").GetString());
        Assert.False(result.Ok);
        Assert.Equal(Refusal, view.Status);
        Assert.True(view.IsProblem);
        Assert.False(view.CanSendToJd);
        Assert.False(view.CanReadAgain);
    }

    [Fact]
    public async Task ALostReadSaysNothingWasAddedAndOffersToTryAgain()
    {
        var host = new FakeHost();
        var ui = new UiQueue();
        await using var client = host.NewClient(ui, slowTimeout: TimeSpan.FromMilliseconds(100));
        host.Hold(MessageTypes.RemoteContainerPreview);

        var result = await client.PreviewContainerAsync(@"D:\invented.rsdf").WaitAsync(Patience);

        Assert.Equal(RemoteText.ContainerReadLost, result.Error);
        Assert.True(RemoteText.ContainerRead(result, "invented.rsdf").CanReadAgain);
    }

    [Fact]
    public void AReadThatNeverReachedTheHostOffersToTryAgainInItsOwnWords()
    {
        foreach (var words in new[] { RemoteText.HostBusy, RemoteText.HostNotRunning })
        {
            var view = RemoteText.ContainerRead(new RemoteContainerPreviewResult { Error = words }, "invented.rsdf");
            Assert.True(view.CanReadAgain);
            Assert.Equal(words, view.Status);
        }
    }

    [Fact]
    public void OnlyAnUncheckedSiteStopsAnOrdinaryLinkGoingStraightToADirectRequest()
    {
        const string Note = "Fetcher lists a site this app couldn't check this link against, so this link may be one it can handle.";
        var uncheckedLine = new AddLine("https://site.invalid/a") { Result = new RemoteClassifyResult { Ok = true, Kind = "ordinaryLink", Note = Note, CheckIncomplete = true } };
        var staleLine = new AddLine("https://site.invalid/a") { Result = new RemoteClassifyResult { Ok = true, Kind = "ordinaryLink", Note = "The list is old." } };

        Assert.True(uncheckedLine.CheckIncomplete);
        Assert.Equal(AddRoute.Direct, uncheckedLine.Route);
        Assert.False(staleLine.CheckIncomplete);
        Assert.StartsWith(Note, RemoteText.CheckIncompleteWords(uncheckedLine.Result!, "Fetcher"), StringComparison.Ordinal);
        Assert.Contains("Fetcher", RemoteText.CheckIncompleteWords(new RemoteClassifyResult(), "Fetcher"), StringComparison.Ordinal);
    }

    [Fact]
    public async Task AddingAContainerOutlastsTheOrdinarySlowLimitBecauseRunningOutWouldCancelTheRest()
    {
        var job = new RemoteJobDto { Id = Guid.NewGuid(), Provider = "torbox", State = "intended", ApprovalNeeded = true };
        var host = new FakeHost();
        host.Answer(MessageTypes.RemoteContainerAdd, MessageTypes.RemoteContainerAddResult, new RemoteContainerAddResult
        {
            Ok = true,
            Outcomes = [new RemoteContainerAddOutcomeDto { EntryId = 1, Ok = true, JobId = job.Id, Job = job }],
        });
        var ui = new UiQueue();
        await using var client = host.NewClient(ui, slowTimeout: TimeSpan.FromMilliseconds(50));
        var answer = host.Hold(MessageTypes.RemoteContainerAdd);

        var adding = client.AddFromContainerAsync(new RemoteContainerAddRequest { PreviewId = Guid.NewGuid(), EntryIds = [1] });
        await Task.Delay(300);
        Assert.False(adding.IsCompleted);
        answer();
        var result = await adding.WaitAsync(Patience);

        Assert.True(result.Ok);
        await ui.UntilAsync(() => client.State.Jobs.Any(known => known.Id == job.Id));
    }

    [Fact]
    public async Task JobsLeftOutOfAnAddAreReadFromTheListAndNotTreatedAsFailures()
    {
        var host = new FakeHost();
        var id = host.Job.Id;
        host.Answer(MessageTypes.RemoteContainerAdd, MessageTypes.RemoteContainerAddResult, new RemoteContainerAddResult
        {
            Ok = true,
            JobsLeftOut = true,
            Outcomes = [new RemoteContainerAddOutcomeDto { EntryId = 1, Ok = true, JobId = id }],
        });
        var ui = new UiQueue();
        await using var client = host.NewClient(ui);
        client.Start();
        await ui.UntilAsync(() => client.State.IsReady);
        var listsBefore = host.Received(MessageTypes.RemoteList);

        var result = await client.AddFromContainerAsync(new RemoteContainerAddRequest { PreviewId = Guid.NewGuid(), EntryIds = [1] }).WaitAsync(Patience);

        Assert.True(result.Ok);
        Assert.True(result.JobsLeftOut);
        Assert.Equal(listsBefore + 1, host.Received(MessageTypes.RemoteList));
        ui.Flush();
        Assert.Contains(client.State.Jobs, job => job.Id == id);
    }

    [Fact]
    public async Task AWholeAnswerOfJobsDoesNotReadTheListAgain()
    {
        var host = new FakeHost();
        host.Answer(MessageTypes.RemoteContainerAdd, MessageTypes.RemoteContainerAddResult, new RemoteContainerAddResult { Ok = true, Outcomes = [new RemoteContainerAddOutcomeDto { EntryId = 1, Ok = true, JobId = host.Job.Id, Job = host.Job }] });
        var ui = new UiQueue();
        await using var client = host.NewClient(ui);
        client.Start();
        await ui.UntilAsync(() => client.State.IsReady);
        var listsBefore = host.Received(MessageTypes.RemoteList);

        await client.AddFromContainerAsync(new RemoteContainerAddRequest { PreviewId = Guid.NewGuid(), EntryIds = [1] }).WaitAsync(Patience);

        Assert.Equal(listsBefore, host.Received(MessageTypes.RemoteList));
    }

    [Fact]
    public async Task TheHandOverWaitsLongerThanJDownloadersFiveMinutes()
    {
        Assert.True(RemoteClient.SendToJdTimeout > TimeSpan.FromMinutes(5));

        var host = new FakeHost();
        host.Answer(MessageTypes.RemoteContainerSendToJd, MessageTypes.RemoteContainerSendResult, new RemoteContainerSendResult { Ok = true, Status = "sent", JdAnswered = true, Message = "Sent to JDownloader." });
        var ui = new UiQueue();
        await using var client = host.NewClient(ui, slowTimeout: TimeSpan.FromMilliseconds(50));
        var answer = host.Hold(MessageTypes.RemoteContainerSendToJd);

        var sending = client.SendContainerToJdAsync(@"D:\invented.dlc");
        await Task.Delay(300);
        Assert.False(sending.IsCompleted);
        answer();

        Assert.Equal("sent", (await sending.WaitAsync(Patience)).Status);
    }

    [Fact]
    public async Task CancellingTheHandOverClosesItsConnection()
    {
        var host = new FakeHost();
        var ui = new UiQueue();
        await using var client = host.NewClient(ui);
        host.Hold(MessageTypes.RemoteContainerSendToJd);
        using var stop = new CancellationTokenSource();

        var sending = client.SendContainerToJdAsync(@"D:\invented.dlc", stop.Token);
        await host.UntilAsync(() => host.Received(MessageTypes.RemoteContainerSendToJd) == 1);
        stop.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => sending.WaitAsync(Patience));
        await host.UntilAsync(() => host.ConnectionOf(MessageTypes.RemoteContainerSendToJd).Disposed);
    }

    [Fact]
    public async Task AHostThatDoesntKnowContainersGetsSaidInWords()
    {
        var host = new FakeHost();
        host.AnswerRaw(MessageTypes.RemoteContainerPreview, request => IpcMessage.Create(MessageTypes.HostActionResult, new HostActionResult { Ok = false, Message = "Unknown message type." }, request.Id));
        host.AnswerRaw(MessageTypes.RemoteContainerSendToJd, request => IpcMessage.Create(MessageTypes.HostActionResult, new HostActionResult { Ok = false, Message = "Unknown message type." }, request.Id));
        var ui = new UiQueue();
        await using var client = host.NewClient(ui);

        Assert.Equal(RemoteText.HostDoesNotOffer, (await client.PreviewContainerAsync(@"D:\invented.ccf").WaitAsync(Patience)).Error);
        var sent = await client.SendContainerToJdAsync(@"D:\invented.ccf").WaitAsync(Patience);
        Assert.False(sent.Ok);
        Assert.Equal("failed", sent.Status);
        Assert.Equal(RemoteText.HostDoesNotOffer, sent.Message);
    }

    /// <summary>What a window's dispatcher queue does: nothing runs until the UI thread gets to it.</summary>
    private sealed class UiQueue
    {
        private readonly Lock _sync = new();
        private readonly Queue<Action> _actions = new();

        public void Post(Action action)
        {
            lock (_sync) _actions.Enqueue(action);
        }

        public void Flush()
        {
            while (true)
            {
                Action action;
                lock (_sync)
                {
                    if (_actions.Count == 0) return;
                    action = _actions.Dequeue();
                }

                action();
            }
        }

        public async Task UntilAsync(Func<bool> condition)
        {
            var deadline = DateTime.UtcNow + Patience;
            while (true)
            {
                Flush();
                if (condition()) return;
                if (DateTime.UtcNow > deadline) Assert.Fail("The expected state never arrived.");
                await Task.Delay(5);
            }
        }
    }

    private sealed class FakeHost
    {
        private readonly Lock _sync = new();
        private readonly List<(string Type, FakePipe Pipe, IpcMessage Message)> _received = [];
        private readonly Dictionary<string, TaskCompletionSource> _held = [];
        private readonly Dictionary<string, Func<IpcMessage, IpcMessage>> _answers = [];

        public List<string> Capabilities { get; set; } = ["jobs", "settings", "remote"];
        public bool KnowsArchives { get; set; } = true;
        public bool Refuse { get; set; }
        public bool Busy { get; set; }
        public List<FakePipe> Connections { get; } = [];
        public RemoteAccountDto Account { get; } = new() { Provider = "torbox", ProviderName = "TorBox", Capabilities = ["account"] };
        public RemoteJobDto Job { get; set; } = new() { Id = Guid.NewGuid(), Provider = "torbox", State = "waiting", Status = "Waiting for TorBox" };
        public ArchiveSetDto Set { get; } = new() { Id = Guid.NewGuid(), Name = "archive", State = "downloaded" };

        public RemoteClient NewClient(UiQueue ui, TimeSpan? quickTimeout = null, TimeSpan? slowTimeout = null)
            => new(ui.Post, ConnectAsync, TimeSpan.FromMilliseconds(10), quickTimeout, slowTimeout);

        public int Received(string type)
        {
            lock (_sync) return _received.Count(entry => entry.Type == type);
        }

        public FakePipe ConnectionOf(string type)
        {
            lock (_sync) return _received.Last(entry => entry.Type == type).Pipe;
        }

        public System.Text.Json.JsonElement? LastPayload(string type)
        {
            lock (_sync) return _received.Last(entry => entry.Type == type).Message.Payload;
        }

        public async Task UntilAsync(Func<bool> condition)
        {
            var deadline = DateTime.UtcNow + Patience;
            while (!condition())
            {
                if (DateTime.UtcNow > deadline) Assert.Fail("The Host never saw what was expected.");
                await Task.Delay(5);
            }
        }

        /// <summary>Calls of this type go unanswered until the returned action runs.</summary>
        public Action Hold(string type)
        {
            var gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            lock (_sync) _held[type] = gate;
            return () => gate.TrySetResult();
        }

        public void Answer<T>(string type, string replyType, T reply)
            => AnswerRaw(type, request => IpcMessage.Create(replyType, reply, request.Id));

        public void AnswerRaw(string type, Func<IpcMessage, IpcMessage> answer)
        {
            lock (_sync) _answers[type] = answer;
        }

        private Task<IRemotePipe> ConnectAsync(TimeSpan timeout, CancellationToken cancellationToken)
        {
            if (Refuse) throw new TimeoutException();
            if (Busy) throw new HostPipeBusyException();
            var pipe = new FakePipe(this);
            lock (_sync) Connections.Add(pipe);
            pipe.Push(IpcMessage.Create(MessageTypes.ServerHello, new HostHelloDto { ProtocolVersion = Protocol.Version, Capabilities = Capabilities }));
            pipe.Push(IpcMessage.Create(MessageTypes.JobsSnapshot, new SnapshotDto()));
            return Task.FromResult<IRemotePipe>(pipe);
        }

        public void Receive(FakePipe pipe, IpcMessage request)
        {
            TaskCompletionSource? held;
            lock (_sync)
            {
                _received.Add((request.Type, pipe, request));
                _held.TryGetValue(request.Type, out held);
            }

            _ = Task.Run(async () =>
            {
                if (held is not null) await held.Task;
                pipe.Push(Reply(request));
            });
        }

        private IpcMessage Reply(IpcMessage request)
        {
            lock (_sync)
            {
                if (_answers.TryGetValue(request.Type, out var answer)) return answer(request);
            }

            if (!KnowsArchives && request.Type.StartsWith(ArchiveMessageTypes.Prefix, StringComparison.Ordinal))
            {
                return IpcMessage.Create(MessageTypes.HostActionResult, new HostActionResult { Ok = false, Message = "Unknown message type." }, request.Id);
            }

            return request.Type switch
            {
                MessageTypes.RemoteAccountGet or MessageTypes.RemoteAccountSetKey or MessageTypes.RemoteAccountClearKey or MessageTypes.RemoteAccountValidate
                    => IpcMessage.Create(MessageTypes.RemoteAccountResult, new RemoteAccountResult { Ok = true, Accounts = [Account] }, request.Id),
                MessageTypes.RemoteList => IpcMessage.Create(MessageTypes.RemoteListResult, new RemoteListResult { Ok = true, Jobs = [Job] }, request.Id),
                MessageTypes.RemoteClassify => IpcMessage.Create(MessageTypes.RemoteClassifyResult, new RemoteClassifyResult { Ok = true, Kind = "magnet" }, request.Id),
                MessageTypes.RemoteAdd => IpcMessage.Create(MessageTypes.RemoteAddResult, new RemoteAddResultDto { Ok = true }, request.Id),
                MessageTypes.RemoteItemGet => IpcMessage.Create(MessageTypes.RemoteItemResult, new RemoteItemResult { Ok = true }, request.Id),
                MessageTypes.RemoteFiles => IpcMessage.Create(MessageTypes.RemoteFilesResult, new RemoteFilesResultDto { Ok = true }, request.Id),
                MessageTypes.RemoteSelect or MessageTypes.RemoteRetry or MessageTypes.RemoteCheckNow or MessageTypes.RemoteRemove or MessageTypes.RemoteDeleteRemote
                    => IpcMessage.Create(MessageTypes.RemoteActionResult, new RemoteActionResultDto { Ok = true }, request.Id),
                ArchiveMessageTypes.SettingsGet or ArchiveMessageTypes.SettingsSet
                    => IpcMessage.Create(ArchiveMessageTypes.SettingsResult, new ArchiveSettingsResult { Ok = true }, request.Id),
                ArchiveMessageTypes.List => IpcMessage.Create(ArchiveMessageTypes.ListResult, new ArchiveListResult { Sets = [Set] }, request.Id),
                ArchiveMessageTypes.Extract => IpcMessage.Create(ArchiveMessageTypes.ExtractResult, new ArchiveExtractResult { Ok = true }, request.Id),
                _ => IpcMessage.Create(ArchiveMessageTypes.ActionResult, new ArchiveActionResult { Ok = true }, request.Id),
            };
        }
    }

    private sealed class FakePipe(FakeHost host) : IRemotePipe
    {
        private readonly Channel<IpcMessage?> _toClient = Channel.CreateUnbounded<IpcMessage?>();

        public bool Disposed { get; private set; }

        public Exception? FailNextSend { get; set; }

        public void Push(IpcMessage message) => _toClient.Writer.TryWrite(message);

        /// <summary>The far end goes away: the reader sees the end of the stream.</summary>
        public void Drop() => _toClient.Writer.TryWrite(null);

        public Task SendAsync(IpcMessage message, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (FailNextSend is { } failure)
            {
                FailNextSend = null;
                throw failure;
            }

            host.Receive(this, message);
            return Task.CompletedTask;
        }

        public async Task<IpcMessage?> ReadAsync(CancellationToken cancellationToken)
            => await _toClient.Reader.ReadAsync(cancellationToken);

        public ValueTask DisposeAsync()
        {
            Disposed = true;
            return ValueTask.CompletedTask;
        }
    }
}

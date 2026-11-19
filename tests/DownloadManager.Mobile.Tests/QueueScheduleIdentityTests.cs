using DownloadManager.Contracts;
using DownloadManager.Mobile;

namespace DownloadManager.Mobile.Tests;

public sealed class QueueScheduleIdentityTests
{
    private static readonly Guid QueueGeneration = Guid.Parse("11111111-1111-1111-1111-111111111111");
    private static readonly Guid ReplacementGeneration = Guid.Parse("22222222-2222-2222-2222-222222222222");
    private static readonly Guid ScheduleId = Guid.Parse("33333333-3333-3333-3333-333333333333");
    private static ScheduleDto Schedule(string days = "Mon,Tue,Wed,Thu,Fri,Sat,Sun") => new()
    {
        Id = ScheduleId, QueueId = 5, Name = "Night", StartTime = "01:00", Days = days,
    };
    private static SettingsDto Snapshot(Guid generation, ScheduleDto schedule, long revision) => new()
    {
        Revision = revision, Queues = [new QueueDto { Id = 5, Name = "Night queue", Generation = generation }], Schedules = [schedule],
    };
    private static async Task<(QueueSettingsModel Model, DownloadsModel Downloads, FakeHostConnection Host)> Setup(SettingsDto shown, SettingsDto latest)
    {
        var host = new FakeHostConnection();
        host.Reply = m => IpcMessage.Create(MessageTypes.SettingsResult, new SettingsResult
        {
            Ok = true, Settings = m.Type == MessageTypes.SettingsUpdate ? m.GetPayload<SettingsUpdateRequest>().Settings : latest,
        }, m.Id);
        var downloads = new DownloadsModel(host, action => action());
        await downloads.StartAsync(default);
        host.Push(IpcMessage.Create(MessageTypes.JobsSnapshot, new SnapshotDto { Settings = shown }));
        return (new QueueSettingsModel(downloads, host), downloads, host);
    }
    private static Task<CommandResult> Execute(QueueSettingsModel model, string operation, ScheduleDto baseline, Guid generation)
    {
        if (operation == "delete") return model.DeleteScheduleAsync(baseline, expectedQueueGeneration: generation);
        if (operation == "create") return model.SaveScheduleAsync(new ScheduleDto { Id = Guid.NewGuid(), QueueId = baseline.QueueId, Name = "New" }, null, expectedQueueGeneration: generation);
        var edited = Schedule(); edited.Name = "Edited";
        return model.SaveScheduleAsync(edited, baseline, expectedQueueGeneration: generation);
    }

    [Theory]
    [InlineData("create")]
    [InlineData("edit")]
    [InlineData("delete")]
    public async Task Parent_queue_replaced_with_identical_visible_data_refuses_schedule_mutation(string operation)
    {
        var baseline = Schedule();
        var shown = Snapshot(QueueGeneration, baseline, 7);
        var latest = Snapshot(ReplacementGeneration, Schedule(), 8);
        var (model, downloads, host) = await Setup(shown, latest);
        using (downloads)
        {
            var result = await Execute(model, operation, baseline, QueueGeneration);
            Assert.False(result.Ok);
            Assert.Contains("replaced", result.Error);
            Assert.Equal(MessageTypes.SettingsGet, Assert.Single(host.Sent).Type);
            Assert.Equal("Night", Assert.Single(downloads.Settings.Schedules).Name);
            Assert.Equal("Night", Assert.Single(latest.Schedules).Name);
        }
    }

    [Theory]
    [InlineData("create")]
    [InlineData("edit")]
    [InlineData("delete")]
    public async Task Same_parent_generation_saves_with_latest_compare_and_swap_revision(string operation)
    {
        var baseline = Schedule();
        var latest = Snapshot(QueueGeneration, Schedule(), 42);
        var (model, downloads, host) = await Setup(Snapshot(QueueGeneration, baseline, 4), latest);
        using (downloads)
        {
            var result = await Execute(model, operation, baseline, QueueGeneration);
            Assert.True(result.Ok, result.Error);
            Assert.Equal([MessageTypes.SettingsGet, MessageTypes.SettingsUpdate], host.Sent.Select(m => m.Type));
            var update = host.Sent[1].GetPayload<SettingsUpdateRequest>();
            Assert.Equal(42, update.ExpectedRevision);
            Assert.Equal(42, update.Settings.Revision);
            Assert.Equal(QueueGeneration, Assert.Single(update.Settings.Queues).Generation);
            if (operation == "create") Assert.Equal(2, update.Settings.Schedules.Count);
            else if (operation == "edit") Assert.Equal("Edited", Assert.Single(update.Settings.Schedules).Name);
            else Assert.Empty(update.Settings.Schedules);
            Assert.Equal("Night", Assert.Single(downloads.Settings.Schedules).Name);
        }
    }

    [Theory]
    [InlineData("Monday;Tuesday;Wednesday;Thursday;Friday;Saturday;Sunday")]
    [InlineData("")]
    public async Task Equivalent_legacy_days_compare_safely_without_losing_host_run_history(string hostDays)
    {
        var baseline = Schedule();
        var current = Schedule(hostDays); current.LastRunAt = DateTimeOffset.UnixEpoch; current.LastRunError = "Last failure";
        var (model, downloads, host) = await Setup(Snapshot(QueueGeneration, baseline, 2), Snapshot(QueueGeneration, current, 9));
        using (downloads)
        {
            var edited = Schedule(); edited.StartTime = "02:00";
            var result = await model.SaveScheduleAsync(edited, baseline, expectedQueueGeneration: QueueGeneration);
            Assert.True(result.Ok, result.Error);
            var update = host.Sent.Last().GetPayload<SettingsUpdateRequest>();
            var sent = Assert.Single(update.Settings.Schedules);
            Assert.Equal("02:00", sent.StartTime);
            Assert.Equal("Mon,Tue,Wed,Thu,Fri,Sat,Sun", sent.Days);
            Assert.Equal(DateTimeOffset.UnixEpoch, sent.LastRunAt);
            Assert.Equal("Last failure", sent.LastRunError);
            Assert.Equal(9, update.ExpectedRevision);
        }
    }

    [Theory]
    [InlineData("edit")]
    [InlineData("delete")]
    public async Task Unknown_weekday_syntax_remains_read_only_even_when_baseline_matches(string operation)
    {
        var baseline = Schedule("Monday;Funday");
        var latest = Snapshot(QueueGeneration, Schedule("Monday;Funday"), 5);
        var (model, downloads, host) = await Setup(Snapshot(QueueGeneration, baseline, 5), latest);
        using (downloads)
        {
            var result = await Execute(model, operation, baseline, QueueGeneration);
            Assert.False(result.Ok);
            Assert.Equal(MessageTypes.SettingsGet, Assert.Single(host.Sent).Type);
            Assert.Equal("Monday;Funday", Assert.Single(latest.Schedules).Days);
            Assert.Equal("Monday;Funday", Assert.Single(downloads.Settings.Schedules).Days);
        }
    }
    [Theory]
    [InlineData(false, false)]
    [InlineData(true, false)]
    [InlineData(false, true)]
    public async Task Remote_queue_and_schedule_writes_require_both_CAS_and_queue_identity(bool cas, bool identity)
    {
        var baseline = Schedule();
        var snapshot = Snapshot(QueueGeneration, baseline, 7);
        var (_, downloads, host) = await Setup(snapshot, snapshot);
        using (downloads)
        {
            var capabilities = new List<string>();
            if (cas) capabilities.Add("settingsCompareAndSwap");
            if (identity) capabilities.Add("queueIdentity");
            host.Push(IpcMessage.Create(MessageTypes.ServerHello, new HostHelloDto { Capabilities = capabilities }));
            var model = new QueueSettingsModel(downloads, host, requireCapabilities: true);
            Assert.Equal(cas, model.CanWriteSafely);
            Assert.False(model.CanEditQueuesSafely);
            var queue = new QueueDto { Id = 5, Name = "Night queue", Generation = QueueGeneration };
            Assert.False((await model.SaveQueueAsync(queue, queue)).Ok);
            Assert.False((await model.SaveQueueAsync(new QueueDto { Name = "New" }, null)).Ok);
            Assert.False((await model.SaveScheduleAsync(Schedule(), baseline, expectedQueueGeneration: QueueGeneration)).Ok);
            Assert.False((await model.SaveScheduleAsync(new ScheduleDto { QueueId = 5 }, null, expectedQueueGeneration: QueueGeneration)).Ok);
            Assert.False((await model.DeleteScheduleAsync(baseline, expectedQueueGeneration: QueueGeneration)).Ok);
            Assert.Empty(host.Sent);
        }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Remote_missing_or_empty_generation_refuses_queue_and_schedule_edits_before_any_request(bool empty)
    {
        var baseline = Schedule();
        var snapshot = Snapshot(QueueGeneration, baseline, 7);
        var (_, downloads, host) = await Setup(snapshot, snapshot);
        using (downloads)
        {
            host.Push(IpcMessage.Create(MessageTypes.ServerHello, new HostHelloDto { Capabilities = ["settingsCompareAndSwap", "queueIdentity"] }));
            var model = new QueueSettingsModel(downloads, host, requireCapabilities: true);
            Assert.True(model.CanEditQueuesSafely);
            Guid? missing = empty ? Guid.Empty : null;
            var queue = new QueueDto { Id = 5, Name = "Night queue", Generation = missing };
            Assert.False((await model.SaveQueueAsync(queue, queue)).Ok);
            Assert.False((await model.SaveScheduleAsync(Schedule(), baseline, expectedQueueGeneration: missing)).Ok);
            Assert.False((await model.SaveScheduleAsync(new ScheduleDto { QueueId = 5 }, null, expectedQueueGeneration: missing)).Ok);
            Assert.False((await model.DeleteScheduleAsync(baseline, expectedQueueGeneration: missing)).Ok);
            Assert.Empty(host.Sent);
        }
    }

    [Fact]
    public async Task Remote_scalar_setting_still_saves_with_CAS_only()
    {
        var snapshot = Snapshot(QueueGeneration, Schedule(), 12);
        var (_, downloads, host) = await Setup(snapshot, snapshot);
        using (downloads)
        {
            host.Push(IpcMessage.Create(MessageTypes.ServerHello, new HostHelloDto { Capabilities = ["settingsCompareAndSwap"] }));
            var model = new QueueSettingsModel(downloads, host, requireCapabilities: true);
            Assert.True(model.CanWriteSafely);
            Assert.False(model.CanEditQueuesSafely);
            var result = await model.UpdateSettingAsync(s => s.DefaultConnections = 4);
            Assert.True(result.Ok, result.Error);
            Assert.Equal([MessageTypes.SettingsGet, MessageTypes.SettingsUpdate], host.Sent.Select(m => m.Type));
            var update = host.Sent[1].GetPayload<SettingsUpdateRequest>();
            Assert.Equal(12, update.ExpectedRevision);
            Assert.Equal(4, update.Settings.DefaultConnections);
        }
    }
}

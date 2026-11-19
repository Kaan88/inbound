using DownloadManager.Contracts;
using DownloadManager.Mobile;

namespace DownloadManager.Mobile.Tests;

public sealed class QueueSettingsModelTests
{
    private static async Task<(QueueSettingsModel Model, DownloadsModel Downloads, FakeHostConnection Host)> Setup(SettingsDto shown, SettingsDto latest)
    {
        var host = new FakeHostConnection();
        host.Reply = m => IpcMessage.Create(MessageTypes.SettingsResult,
            new SettingsResult { Ok = true, Settings = m.Type == MessageTypes.SettingsUpdate ? m.GetPayload<SettingsUpdateRequest>().Settings : latest }, m.Id);
        var downloads = new DownloadsModel(host, a => a());
        await downloads.StartAsync(default);
        host.Push(IpcMessage.Create(MessageTypes.JobsSnapshot, new SnapshotDto { Settings = shown }));
        return (new QueueSettingsModel(downloads, host), downloads, host);
    }

    [Fact]
    public async Task Older_remote_refuses_whole_settings_writes_but_can_refresh_for_review()
    {
        var (_, downloads, host) = await Setup(new(), new());
        using (downloads)
        {
            var model = new QueueSettingsModel(downloads, host, requireCapabilities: true);
            Assert.False(model.CanWriteSafely);
            Assert.False((await model.SaveQueueAsync(new QueueDto { Name = "Night" }, null)).Ok);
            Assert.False((await model.SaveScheduleAsync(new ScheduleDto(), null)).Ok);
            Assert.False((await model.DeleteScheduleAsync(new ScheduleDto())).Ok);
            Assert.False((await model.UpdateSettingAsync(s => s.DefaultConnections = 2)).Ok);
            Assert.Empty(host.Sent);
            Assert.True((await model.RefreshAsync()).Ok);
            Assert.Equal(MessageTypes.SettingsGet, Assert.Single(host.Sent).Type);
            Assert.NotNull(model.LastResult);
            host.Push(IpcMessage.Create(MessageTypes.ServerHello, new HostHelloDto { Capabilities = ["settingsCompareAndSwap"] }));
            Assert.True(model.CanWriteSafely);
            Assert.True((await model.UpdateSettingAsync(s => s.DefaultConnections = 2)).Ok);
        }
    }

    [Fact]
    public async Task Reused_queue_identity_refuses_stale_edit_even_with_equal_visible_fields()
    {
        var baseline = new QueueDto { Id = 3, Name = "Night", Generation = Guid.NewGuid() };
        var replacement = new QueueDto { Id = 3, Name = "Night", Generation = Guid.NewGuid() };
        var (model, downloads, host) = await Setup(new SettingsDto { Queues = [baseline] }, new SettingsDto { Queues = [replacement] });
        using (downloads)
        {
            Assert.False((await model.SaveQueueAsync(new QueueDto { Id = 3, Name = "Edited" }, baseline)).Ok);
            Assert.Equal(MessageTypes.SettingsGet, Assert.Single(host.Sent).Type);
        }
    }

    [Theory]
    [InlineData("start")]
    [InlineData("stop")]
    [InlineData("create")]
    public async Task Queue_actions_use_the_host_command_and_return_its_result(string action)
    {
        var (model, downloads, host) = await Setup(new(), new());
        using (downloads)
        {
            var result = await model.QueueActionAsync(new QueueActionRequest { QueueId = 2, Action = action, Name = "Night", MaxConcurrentDownloads = 2 });
            Assert.True(result.Ok);
            var sent = Assert.Single(host.Sent);
            Assert.Equal(MessageTypes.QueueAction, sent.Type);
            Assert.Equal(action, sent.GetPayload<QueueActionRequest>().Action);
            Assert.NotNull(model.LastResult);
        }
    }

    [Fact]
    public async Task New_queue_is_created_by_host_not_inserted_into_settings()
    {
        var (model, downloads, host) = await Setup(new(), new());
        using (downloads)
        {
            Assert.True((await model.SaveQueueAsync(new QueueDto { Name = "Night", MaxConcurrentDownloads = 2 }, null)).Ok);
            Assert.Equal("create", Assert.Single(host.Sent).GetPayload<QueueActionRequest>().Action);
            Assert.Empty(downloads.Settings.Queues);
        }
    }

    [Fact]
    public async Task Queue_edit_preserves_host_state_and_other_settings_without_mutating_snapshot()
    {
        var baseline = new QueueDto { Id = 3, Name = "Old", MaxConcurrentDownloads = 2, IsRunning = false };
        var shown = new SettingsDto { Queues = [baseline], DefaultDownloadDirectory = "/old" };
        var latest = new SettingsDto { Queues = [new QueueDto { Id = 3, Name = "Old", MaxConcurrentDownloads = 2, IsRunning = true, QuotaBytesUsed = 123 }, new QueueDto { Id = 9, Name = "Other" }], DefaultDownloadDirectory = "/new" };
        var (model, downloads, host) = await Setup(shown, latest);
        using (downloads)
        {
            var edited = new QueueDto { Id = 3, Name = "Edited", MaxConcurrentDownloads = 2 };
            Assert.True((await model.SaveQueueAsync(edited, baseline)).Ok);
            Assert.Equal([MessageTypes.SettingsGet, MessageTypes.SettingsUpdate], host.Sent.Select(x => x.Type));
            var sent = host.Sent[1].GetPayload<SettingsUpdateRequest>().Settings;
            Assert.Equal("/new", sent.DefaultDownloadDirectory);
            Assert.Equal(2, sent.Queues.Count);
            Assert.Equal("Edited", sent.Queues[0].Name);
            Assert.True(sent.Queues[0].IsRunning);
            Assert.Equal(123, sent.Queues[0].QuotaBytesUsed);
            Assert.Equal("Old", downloads.Settings.Queues[0].Name);
            Assert.Equal("Old", latest.Queues[0].Name);
        }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Stale_or_deleted_queue_is_not_recreated(bool deleted)
    {
        var baseline = new QueueDto { Id = 3, Name = "Old" };
        var latest = new SettingsDto { Queues = deleted ? [] : [new QueueDto { Id = 3, Name = "Changed" }] };
        var (model, downloads, host) = await Setup(new SettingsDto { Queues = [baseline] }, latest);
        using (downloads)
        {
            Assert.False((await model.SaveQueueAsync(new QueueDto { Id = 3, Name = "Mine" }, baseline)).Ok);
            Assert.Equal(MessageTypes.SettingsGet, Assert.Single(host.Sent).Type);
        }
    }

    [Fact]
    public async Task Schedule_save_and_delete_reject_conflicts_and_preserve_run_history()
    {
        var id = Guid.NewGuid();
        var baseline = new ScheduleDto { Id = id, Name = "Old" };
        var latest = new SettingsDto { Schedules = [new ScheduleDto { Id = id, Name = "Old", LastRunError = "failed", LastRunAt = DateTimeOffset.UnixEpoch }, new ScheduleDto { Id = Guid.NewGuid(), Name = "Other" }] };
        var (model, downloads, host) = await Setup(new SettingsDto { Schedules = [baseline] }, latest);
        using (downloads)
        {
            Assert.True((await model.SaveScheduleAsync(new ScheduleDto { Id = id, Name = "Mine" }, baseline)).Ok);
            var saved = host.Sent[1].GetPayload<SettingsUpdateRequest>().Settings.Schedules;
            Assert.Equal(2, saved.Count);
            Assert.Equal("failed", saved[0].LastRunError);
            Assert.Equal(DateTimeOffset.UnixEpoch, saved[0].LastRunAt);
            latest.Schedules[0].Name = "Changed elsewhere";
            host.Sent.Clear();
            Assert.False((await model.DeleteScheduleAsync(baseline)).Ok);
            Assert.Single(host.Sent);
            latest.Schedules.Clear();
            host.Sent.Clear();
            Assert.False((await model.SaveScheduleAsync(new ScheduleDto { Id = id, Name = "Mine" }, baseline)).Ok);
            Assert.Single(host.Sent);
        }
    }

    [Fact]
    public async Task New_and_deleted_schedules_keep_other_schedules()
    {
        var existing = new ScheduleDto { Id = Guid.NewGuid(), Name = "Existing" };
        var latest = new SettingsDto { Schedules = [existing] };
        var (model, downloads, host) = await Setup(new SettingsDto { Schedules = [existing] }, latest);
        using (downloads)
        {
            Assert.True((await model.SaveScheduleAsync(new ScheduleDto { Name = "New" }, null)).Ok);
            var saved = host.Sent[1].GetPayload<SettingsUpdateRequest>().Settings;
            Assert.Equal(2, saved.Schedules.Count);
            Assert.NotEqual(Guid.Empty, saved.Schedules[1].Id);
            host.Sent.Clear();
            Assert.True((await model.DeleteScheduleAsync(existing)).Ok);
            Assert.Empty(host.Sent[1].GetPayload<SettingsUpdateRequest>().Settings.Schedules);
            Assert.Single(latest.Schedules);
        }
    }
    [Fact]
    public async Task Setting_edit_uses_fresh_unrelated_fields_and_does_not_change_screen_state()
    {
        var shown = new SettingsDto { DefaultConnections = 2, DefaultDownloadDirectory = "/old" };
        var latest = new SettingsDto { DefaultConnections = 3, DefaultDownloadDirectory = "/new", Proxy = new ProxyDto { Mode = "http", HasPassword = true } };
        var (model, downloads, host) = await Setup(shown, latest);
        using (downloads)
        {
            Assert.True((await model.UpdateSettingAsync(s => s.DefaultConnections = 5)).Ok);
            var sent = host.Sent[1].GetPayload<SettingsUpdateRequest>().Settings;
            Assert.Equal(5, sent.DefaultConnections);
            Assert.Equal("/new", sent.DefaultDownloadDirectory);
            Assert.True(sent.Proxy.HasPassword);
            Assert.Equal(2, downloads.Settings.DefaultConnections);
        }
    }

    [Fact]
    public async Task Wrong_reply_and_refusal_are_reported_and_do_not_trigger_update()
    {
        var (model, downloads, host) = await Setup(new(), new());
        using (downloads)
        {
            host.Reply = m => IpcMessage.Create(MessageTypes.JobActionResult, new JobActionResponse { Ok = true }, m.Id);
            Assert.False((await model.UpdateSettingAsync(s => s.DefaultConnections = 4)).Ok);
            Assert.Single(host.Sent);
            host.Sent.Clear();
            host.Reply = m => IpcMessage.Create(MessageTypes.SettingsResult, new SettingsResult { Ok = false, Error = "No access" }, m.Id);
            var result = await model.QueueActionAsync(new QueueActionRequest { Action = "start" });
            Assert.False(result.Ok);
            Assert.Equal("No access", result.Error);
        }
    }

    [Fact]
    public async Task Cancellation_is_propagated_before_any_request()
    {
        var (model, downloads, host) = await Setup(new(), new());
        using (downloads)
        {
            using var cancelled = new CancellationTokenSource(); cancelled.Cancel();
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => model.UpdateSettingAsync(s => s.DefaultConnections = 4, cancelled.Token));
            Assert.Empty(host.Sent);
        }
    }
}

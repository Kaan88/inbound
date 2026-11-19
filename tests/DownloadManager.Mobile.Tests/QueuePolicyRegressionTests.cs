using DownloadManager.Contracts;
using DownloadManager.Mobile;

namespace DownloadManager.Mobile.Tests;

public sealed class QueuePolicyRegressionTests
{
    [Fact]
    public async Task Clearing_the_global_speed_limit_is_sent_as_null()
    {
        var shown = new SettingsDto { Revision = 3, GlobalSpeedLimitBytesPerSecond = 2_000 };
        var latest = new SettingsDto { Revision = 9, GlobalSpeedLimitBytesPerSecond = 2_000, DefaultConnections = 5 };
        var host = new FakeHostConnection();
        host.Reply = request => SettingsResponse(request, latest);
        using var downloads = await Start(host, shown);
        var model = new QueueSettingsModel(downloads, host);

        var result = await model.UpdateSettingAsync(settings => settings.GlobalSpeedLimitBytesPerSecond = null);

        Assert.True(result.Ok);
        var update = host.Sent.Single(request => request.Type == MessageTypes.SettingsUpdate)
            .GetPayload<SettingsUpdateRequest>();
        Assert.Null(update.Settings.GlobalSpeedLimitBytesPerSecond);
    }

    [Fact]
    public async Task Wrong_reply_after_success_clears_the_previous_last_result()
    {
        var host = new FakeHostConnection();
        host.Reply = request => IpcMessage.Create(MessageTypes.SettingsResult, new SettingsResult { Ok = true }, request.Id);
        using var downloads = await Start(host, new SettingsDto());
        var model = new QueueSettingsModel(downloads, host);

        Assert.True((await model.QueueActionAsync(new QueueActionRequest { QueueId = 2, Action = "start" })).Ok);
        Assert.NotNull(model.LastResult);
        host.Reply = request => IpcMessage.Create(MessageTypes.JobActionResult, new JobActionResponse { Ok = true }, request.Id);

        var result = await model.QueueActionAsync(new QueueActionRequest { QueueId = 2, Action = "stop" });

        Assert.False(result.Ok);
        Assert.Null(model.LastResult);
    }

    [Fact]
    public async Task Settings_update_uses_revision_from_the_fresh_host_read()
    {
        var shown = new SettingsDto { Revision = 2, DefaultConnections = 3 };
        var latest = new SettingsDto { Revision = 17, DefaultConnections = 4 };
        var host = new FakeHostConnection();
        host.Reply = request => SettingsResponse(request, latest);
        using var downloads = await Start(host, shown);
        var model = new QueueSettingsModel(downloads, host);

        Assert.True((await model.UpdateSettingAsync(settings => settings.DefaultConnections = 6)).Ok);

        var update = host.Sent.Single(request => request.Type == MessageTypes.SettingsUpdate)
            .GetPayload<SettingsUpdateRequest>();
        Assert.Equal(17, update.ExpectedRevision);
        Assert.Equal(17, update.Settings.Revision);
    }

    [Fact]
    public async Task Queue_commands_are_sent_only_to_their_own_target_host()
    {
        var phoneHost = new FakeHostConnection();
        var remoteHost = new FakeHostConnection();
        phoneHost.Reply = request => IpcMessage.Create(MessageTypes.SettingsResult, new SettingsResult { Ok = true }, request.Id);
        remoteHost.Reply = request => IpcMessage.Create(MessageTypes.SettingsResult, new SettingsResult { Ok = true }, request.Id);
        using var phoneDownloads = await Start(phoneHost, new SettingsDto { DefaultDownloadDirectory = "/phone" });
        using var remoteDownloads = await Start(remoteHost, new SettingsDto { DefaultDownloadDirectory = "/server" });
        var phoneQueues = new QueueSettingsModel(phoneDownloads, phoneHost);
        var remoteQueues = new QueueSettingsModel(remoteDownloads, remoteHost);

        Assert.True((await phoneQueues.QueueActionAsync(new QueueActionRequest { QueueId = 1, Action = "start" })).Ok);
        Assert.Equal("/phone", phoneQueues.Settings.DefaultDownloadDirectory);
        Assert.Equal("/server", remoteQueues.Settings.DefaultDownloadDirectory);
        Assert.Equal("start", Assert.Single(phoneHost.Sent).GetPayload<QueueActionRequest>().Action);
        Assert.Empty(remoteHost.Sent);

        Assert.True((await remoteQueues.QueueActionAsync(new QueueActionRequest { QueueId = 4, Action = "stop" })).Ok);
        Assert.Equal("start", phoneHost.Sent.Single().GetPayload<QueueActionRequest>().Action);
        Assert.Equal("stop", Assert.Single(remoteHost.Sent).GetPayload<QueueActionRequest>().Action);
    }

    [Fact]
    public void A_queue_condition_wait_excludes_queued_jobs_but_not_active_jobs_and_reactivation_updates_summary()
    {
        const int queueId = 3;
        using var state = new ActivityState(interval: TimeSpan.FromHours(1));
        var queued = Jobs.Make("queued", "waiting.zip");
        queued.QueueId = queueId;
        var waitingQueue = new QueueDto { Id = queueId, IsRunning = true, ConditionWaitReason = "charging" };

        state.ApplySnapshot([queued], queues: [waitingQueue]);
        Assert.False(state.Summary.KeepAlive);
        Assert.Equal(0, state.Summary.ActiveCount);

        state.SetQueues([new QueueDto { Id = queueId, IsRunning = true }]);
        Assert.True(state.Summary.KeepAlive);

        state.SetQueues([waitingQueue]);
        Assert.False(state.Summary.KeepAlive);
        var active = Jobs.Make("downloading", "active.zip", total: 1_000, rate: 100, id: Guid.NewGuid());
        active.QueueId = queueId;
        state.OnJobChanged(active, "updated");
        Assert.True(state.Summary.KeepAlive);
        Assert.Equal(1, state.Summary.ActiveCount);
    }

    private static async Task<DownloadsModel> Start(FakeHostConnection host, SettingsDto settings)
    {
        var downloads = new DownloadsModel(host, action => action());
        await downloads.StartAsync(CancellationToken.None);
        host.Push(IpcMessage.Create(MessageTypes.JobsSnapshot, new SnapshotDto { Settings = settings }));
        return downloads;
    }

    private static IpcMessage SettingsResponse(IpcMessage request, SettingsDto latest)
    {
        var settings = request.Type == MessageTypes.SettingsUpdate
            ? request.GetPayload<SettingsUpdateRequest>().Settings
            : latest;
        return IpcMessage.Create(MessageTypes.SettingsResult, new SettingsResult { Ok = true, Settings = settings }, request.Id);
    }
}

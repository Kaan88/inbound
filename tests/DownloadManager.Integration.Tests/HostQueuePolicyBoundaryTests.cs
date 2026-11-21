using DownloadManager.Contracts;

namespace DownloadManager.Integration.Tests;

public sealed partial class HostCommandDispatcherTests
{
    [Fact]
    public async Task UnsupportedPlatformRejectsQueueConditionsButAcceptsHttpQueueCaps()
    {
        var web = await WebAsync();
        var before = web.Service.GetSettings();
        var id = before.DefaultQueueId;
        var invalid = (await AskAsync(web.Dispatcher, web.Pipe, MessageTypes.QueueAction,
            new QueueActionRequest { Action = "create", Name = "Phone-only", MaxConcurrentDownloads = 1, RequireCharging = true })).GetPayload<SettingsResult>();
        Assert.False(invalid.Ok);
        Assert.DoesNotContain(web.Service.GetSettings().Queues, q => q.Name == "Phone-only");
        var requested = web.Service.GetSettings(); requested.Queues.Single(q => q.Id == id).RequireBatterySaverOff = true;
        var refused = (await AskAsync(web.Dispatcher, web.Pipe, MessageTypes.SettingsUpdate,
            new SettingsUpdateRequest { Settings = requested, ExpectedRevision = requested.Revision })).GetPayload<SettingsResult>();
        Assert.False(refused.Ok);
        Assert.NotEqual(true, web.Service.GetSettings().Queues.Single(q => q.Id == id).RequireBatterySaverOff);
        requested = web.Service.GetSettings(); requested.Queues.Single(q => q.Id == id).SpeedLimitBytesPerSecond = 65536;
        var saved = (await AskAsync(web.Dispatcher, web.Pipe, MessageTypes.SettingsUpdate,
            new SettingsUpdateRequest { Settings = requested, ExpectedRevision = requested.Revision })).GetPayload<SettingsResult>();
        Assert.True(saved.Ok, saved.Error);
        Assert.Equal(65536, saved.Settings.Queues.Single(q => q.Id == id).SpeedLimitBytesPerSecond);
    }

    [Fact]
    public async Task UnsupportedPlatformLetsAClientClearAQueueConditionItCannotSet()
    {
        // Review S1: a condition that arrived another way (copied settings) can always be turned off.
        var web = await WebAsync();
        var current = web.Service.GetSettings();
        var id = current.DefaultQueueId;
        current.Queues.Single(q => q.Id == id).RequireCharging = true;
        current.Queues.Single(q => q.Id == id).RequireBatterySaverOff = true;
        Assert.True((await web.Service.UpdateSettingsAsync(current)).Ok);

        var requested = web.Service.GetSettings();
        requested.Queues.Single(q => q.Id == id).RequireCharging = false;
        requested.Queues.Single(q => q.Id == id).RequireBatterySaverOff = false;
        var cleared = (await AskAsync(web.Dispatcher, web.Pipe, MessageTypes.SettingsUpdate,
            new SettingsUpdateRequest { Settings = requested, ExpectedRevision = requested.Revision })).GetPayload<SettingsResult>();
        Assert.True(cleared.Ok, cleared.Error);
        var queue = web.Service.GetSettings().Queues.Single(q => q.Id == id);
        Assert.NotEqual(true, queue.RequireCharging);
        Assert.NotEqual(true, queue.RequireBatterySaverOff);
    }

    [Fact]
    public async Task SettingsWireCompareAndSwapRejectsStaleWriteWithoutChangingOtherFields()
    {
        var web = await WebAsync();
        var old = web.Service.GetSettings();
        var first = web.Service.GetSettings(); first.DefaultConnections = first.DefaultConnections == 2 ? 3 : 2;
        var accepted = (await AskAsync(web.Dispatcher, web.Pipe, MessageTypes.SettingsUpdate,
            new SettingsUpdateRequest { Settings = first, ExpectedRevision = first.Revision })).GetPayload<SettingsResult>();
        Assert.True(accepted.Ok, accepted.Error);
        old.MaxConcurrentDownloads = 16;
        var refused = (await AskAsync(web.Dispatcher, web.Pipe, MessageTypes.SettingsUpdate,
            new SettingsUpdateRequest { Settings = old, ExpectedRevision = old.Revision })).GetPayload<SettingsResult>();
        Assert.False(refused.Ok);
        Assert.Equal(accepted.Settings.DefaultConnections, web.Service.GetSettings().DefaultConnections);
        Assert.Equal(accepted.Settings.MaxConcurrentDownloads, web.Service.GetSettings().MaxConcurrentDownloads);
    }
}

using DownloadManager.Contracts;
using DownloadManager.Mobile;

namespace DownloadManager.Mobile.Tests;

public sealed class NewDownloadQueueTests
{
    private static readonly Guid DefaultGeneration = Guid.Parse("11111111-1111-1111-1111-111111111111");
    private static readonly Guid NightGeneration = Guid.Parse("22222222-2222-2222-2222-222222222222");
    private static async Task<(NewDownloadModel Model, DownloadsModel Downloads, FakeHostConnection Host)> Setup(bool remote = false, bool identity = false)
    {
        var host = new FakeHostConnection();
        var downloads = new DownloadsModel(host, action => action(), TimeSpan.Zero);
        await downloads.StartAsync(default);
        if (identity) host.Push(IpcMessage.Create(MessageTypes.ServerHello, new HostHelloDto { Capabilities = ["queueIdentity"] }));
        host.Push(IpcMessage.Create(MessageTypes.JobsSnapshot, new SnapshotDto
        {
            Settings = new SettingsDto { DefaultQueueId = 7, Queues = [new QueueDto { Id = 7, Name = "Default", Generation = DefaultGeneration }, new QueueDto { Id = 9, Name = "Night", Generation = NightGeneration }] }
        }));
        var model = new NewDownloadModel(downloads, requireQueueIdentity: remote) { Address = "https://example.test/file[01-02].zip" };
        return (model, downloads, host);
    }

    [Fact]
    public async Task Chosen_queue_and_queue_start_go_to_every_request()
    {
        var (model, downloads, host) = await Setup();
        using (downloads)
        {
            model.QueueId = 9; model.WaitForQueue = true;
            var result = await model.SubmitAsync();
            Assert.Null(result.Error);
            Assert.Equal(2, result.Added);
            Assert.Equal(2, host.Sent.Count);
            Assert.All(host.Sent, sent =>
            {
                Assert.Equal(MessageTypes.JobAdd, sent.Type);
                var request = sent.GetPayload<AddJobRequest>();
                Assert.Equal(9, request.QueueId);
                Assert.True(request.RequireQueue);
                Assert.Equal(NightGeneration, request.ExpectedQueueGeneration);
                Assert.Equal(StartModes.Queue, request.StartMode);
            });
        }
    }

    [Fact]
    public async Task Later_has_precedence_over_queue_start_for_compatibility()
    {
        var (model, downloads, _) = await Setup();
        using (downloads)
        {
            model.QueueId = 9; model.WaitForQueue = true; model.StartLater = true;
            Assert.All(model.BuildRequests(), r => { Assert.Equal(StartModes.Later, r.StartMode); Assert.Equal(9, r.QueueId); });
        }
    }

    [Fact]
    public async Task Unselected_queue_uses_current_host_default_and_is_not_strict()
    {
        var (model, downloads, host) = await Setup();
        using (downloads)
        {
            Assert.All(model.BuildRequests(), r => { Assert.Equal(7, r.QueueId); Assert.False(r.RequireQueue); Assert.Equal(StartModes.StartNow, r.StartMode); });
            host.Push(IpcMessage.Create(MessageTypes.SettingsChanged, new SettingsDto { DefaultQueueId = 9, Queues = [new QueueDto { Id = 9 }] }));
            Assert.All(model.BuildRequests(), r => Assert.Equal(9, r.QueueId));
        }
    }

    [Fact]
    public async Task Deleted_selected_queue_refuses_without_sending_any_downloads()
    {
        var (model, downloads, host) = await Setup();
        using (downloads)
        {
            model.QueueId = 9; model.WaitForQueue = true;
            host.Push(IpcMessage.Create(MessageTypes.SettingsChanged, new SettingsDto { DefaultQueueId = 7, Queues = [new QueueDto { Id = 7 }] }));
            Assert.False(model.CanDownload);
            Assert.Contains("deleted", model.QueueError);
            var result = await model.SubmitAsync();
            Assert.Contains("deleted", result.Error);
            Assert.Empty(host.Sent);
            model.QueueId = null;
            Assert.True(model.CanDownload);
        }
    }

    [Fact]
    public async Task Queue_deleted_after_first_batch_item_stops_remaining_items()
    {
        var (model, downloads, host) = await Setup();
        using (downloads)
        {
            model.QueueId = 9;
            host.Reply = request =>
            {
                host.Push(IpcMessage.Create(MessageTypes.SettingsChanged, new SettingsDto { DefaultQueueId = 7, Queues = [new QueueDto { Id = 7 }] }));
                return IpcMessage.Create(MessageTypes.JobAddResult, new AddJobResponse { Accepted = true }, request.Id);
            };
            var result = await model.SubmitAsync();
            Assert.Equal(1, result.Added);
            Assert.Contains("deleted", result.Error);
            Assert.Single(host.Sent);
        }
    }
    [Fact]
    public async Task Reused_queue_id_does_not_rebind_an_existing_selection()
    {
        var (model, downloads, host) = await Setup(remote: true, identity: true);
        using (downloads)
        {
            model.QueueId = 9;
            Assert.Equal(NightGeneration, model.QueueGeneration);
            var replacement = Guid.NewGuid();
            host.Push(IpcMessage.Create(MessageTypes.SettingsChanged, new SettingsDto
            {
                DefaultQueueId = 7, Queues = [new QueueDto { Id = 7, Generation = DefaultGeneration }, new QueueDto { Id = 9, Generation = replacement }]
            }));
            model.QueueId = 9; // A settings/UI refresh does not bind the replacement.
            Assert.Equal(NightGeneration, model.QueueGeneration);
            Assert.False(model.CanDownload);
            var result = await model.SubmitAsync();
            Assert.Contains("replaced", result.Error);
            Assert.Empty(host.Sent);
            model.SetQueueSelection(9, replacement); // A deliberate new choice is permitted.
            Assert.True(model.CanDownload);
        }
    }

    [Fact]
    public async Task Replacement_during_batch_stops_before_next_admission()
    {
        var (model, downloads, host) = await Setup(remote: true, identity: true);
        using (downloads)
        {
            model.QueueId = 9;
            host.Reply = request =>
            {
                host.Push(IpcMessage.Create(MessageTypes.SettingsChanged, new SettingsDto
                {
                    DefaultQueueId = 7, Queues = [new QueueDto { Id = 9, Generation = Guid.NewGuid() }]
                }));
                return IpcMessage.Create(MessageTypes.JobAddResult, new AddJobResponse { Accepted = true }, request.Id);
            };
            var result = await model.SubmitAsync();
            Assert.Equal(1, result.Added);
            Assert.Contains("replaced", result.Error);
            Assert.Single(host.Sent);
        }
    }

    [Fact]
    public async Task Older_remote_host_blocks_explicit_queue_but_keeps_default_available()
    {
        var (model, downloads, host) = await Setup(remote: true, identity: false);
        using (downloads)
        {
            Assert.True(model.CanDownload);
            model.QueueId = 9;
            Assert.False(model.CanDownload);
            Assert.Contains("Update", model.QueueError);
            Assert.Contains("Update", (await model.SubmitAsync()).Error);
            Assert.Empty(host.Sent);
            model.QueueId = null;
            var result = await model.SubmitAsync();
            Assert.Null(result.Error);
            Assert.Equal(2, result.Added);
            Assert.All(host.Sent, sent =>
            {
                var request = sent.GetPayload<AddJobRequest>();
                Assert.False(request.RequireQueue);
                Assert.Null(request.ExpectedQueueGeneration);
            });
        }
    }

    [Fact]
    public async Task Restored_queue_identity_is_not_rebound_to_the_current_queue()
    {
        var (model, downloads, host) = await Setup();
        using (downloads)
        {
            var old = Guid.NewGuid();
            model.SetQueueSelection(9, old);
            Assert.Equal(old, model.QueueGeneration);
            Assert.Contains("replaced", model.QueueError);
            Assert.Empty(host.Sent);
            model.SetQueueSelection(9, null);
            Assert.Contains("identity", model.QueueError);
        }
    }
}

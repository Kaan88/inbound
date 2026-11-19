using DownloadManager.Contracts;
using DownloadManager.Mobile;

namespace DownloadManager.Mobile.Tests;

public sealed class QueueMembershipTests
{
    [Theory]
    [InlineData(8)]
    [InlineData(null)]
    public async Task AssignmentAndDetachmentUseSelectedHostCommand(int? queueId)
    {
        var host = new FakeHostConnection { Reply = request => IpcMessage.Create(MessageTypes.JobActionResult, new JobActionResponse { Ok = true }, request.Id) };
        using var model = new DownloadsModel(host, action => action());
        var id = Guid.NewGuid();
        Assert.True((await model.MoveToQueueAsync(id, queueId)).Ok);
        var message = Assert.Single(host.Sent); Assert.Equal(MessageTypes.JobMoveToQueue, message.Type);
        var payload = message.GetPayload<JobQueueRequest>();
        Assert.Equal(id, payload.JobId); Assert.Equal(queueId is null, payload.Detach); Assert.Equal(queueId ?? 0, payload.QueueId);
    }
}

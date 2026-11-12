using DownloadManager.Contracts;

namespace DownloadManager.Integration.Tests;

/// <summary>Audit 1, C-3: an add takes "now", "queue" and "later" (and the canonical names) and refuses anything else.</summary>
public sealed partial class RuntimeServiceTests
{
    [Theory]
    [InlineData("now", "startNow")]
    [InlineData("later", "later")]
    [InlineData("queue", "queue")]
    public async Task AnAddAcceptsTheStartModeWordsClientsSend(string sent, string stored)
    {
        await using var ctx = await ServiceContext.CreateAsync(_server);
        var added = await ctx.Service.AddJobAsync(new AddJobRequest
        {
            Url = $"{_server.BaseAddress}/file/start-{sent}.bin?size=2048&seed=7",
            DestinationDirectory = ctx.Destination,
            StartMode = sent,
        });
        Assert.True(added.Accepted, added.Error);
        Assert.Equal(stored, added.Job!.StartMode);
    }

    [Theory]
    [InlineData("latr")]
    [InlineData("1")]
    public async Task AnAddRefusesAStartModeItDoesNotKnow(string sent)
    {
        await using var ctx = await ServiceContext.CreateAsync(_server);
        var added = await ctx.Service.AddJobAsync(new AddJobRequest
        {
            Url = $"{_server.BaseAddress}/file/start-bad.bin?size=2048&seed=8",
            DestinationDirectory = ctx.Destination,
            StartMode = sent,
        });
        Assert.False(added.Accepted);
        Assert.Equal("invalid_start_mode", added.ErrorCode);
        Assert.Empty(ctx.Service.GetSnapshot().Jobs);
    }
}

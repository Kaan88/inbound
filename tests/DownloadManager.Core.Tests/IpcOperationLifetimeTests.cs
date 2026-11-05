using DownloadManager.Contracts;

namespace DownloadManager.Core.Tests;

public sealed class IpcOperationLifetimeTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task DisconnectCancelsTheRunningOperation(bool brokenPipe)
    {
        var next = new TaskCompletionSource<IpcMessage?>(TaskCreationOptions.RunContinuationsAsynchronously);
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var cancelled = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var running = IpcOperationLifetime.RunAsync(async token =>
        {
            started.SetResult();
            try { await Task.Delay(Timeout.Infinite, token); }
            catch (OperationCanceledException) { cancelled.SetResult(); throw; }
        }, next.Task, default);
        await started.Task.WaitAsync(TimeSpan.FromSeconds(2));
        if (brokenPipe) next.SetException(new IOException("Disconnected"));
        else next.SetResult(null);
        await cancelled.Task.WaitAsync(TimeSpan.FromSeconds(2));
        if (brokenPipe) await Assert.ThrowsAsync<IOException>(() => running);
        else await Assert.ThrowsAnyAsync<OperationCanceledException>(() => running);
    }

    [Fact]
    public async Task PipelinedMessageIsRetainedAndDoesNotCancelTheCurrentOperation()
    {
        var nextMessage = IpcMessage.Create(MessageTypes.HostPing);
        var next = Task.FromResult<IpcMessage?>(nextMessage);
        var finish = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var running = IpcOperationLifetime.RunAsync(token => finish.Task.WaitAsync(token), next, default);
        Assert.False(running.IsCompleted);
        Assert.Same(nextMessage, await next);
        finish.SetResult();
        await running.WaitAsync(TimeSpan.FromSeconds(2));
        Assert.Same(nextMessage, await next);
    }

    [Fact]
    public async Task CompletedOperationDoesNotWaitForTheNextRequest()
    {
        var next = new TaskCompletionSource<IpcMessage?>(TaskCreationOptions.RunContinuationsAsynchronously);
        await IpcOperationLifetime.RunAsync(_ => Task.CompletedTask, next.Task, default).WaitAsync(TimeSpan.FromSeconds(2));
        Assert.False(next.Task.IsCompleted);
        next.SetResult(null);
    }
}

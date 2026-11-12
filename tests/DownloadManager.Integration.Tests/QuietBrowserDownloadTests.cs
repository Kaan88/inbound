using System.Collections.Concurrent;
using System.Text;
using DownloadManager.Contracts;
using DownloadManager.Runtime;

namespace DownloadManager.Integration.Tests;

/// <summary>
/// EXT.R12, on real downloads that finish: a download the browser sent with QuietWindows opens no app window, neither as
/// it starts nor when it finishes, alone or in a package, and the app's own completion notice stays away (the browser
/// flashes its icon or opens its popup instead). One sent without the flag still opens its windows and is told about.
/// </summary>
[Collection("runtime-archive")]
public sealed class QuietBrowserDownloadTests(ArchiveFileServer server)
{
    private static readonly byte[] Bytes = Encoding.ASCII.GetBytes("bytes the browser sent to the app");

    private static async Task SettleAsync() => await Task.Delay(400);

    [Fact]
    public async Task AQuietDownloadThatFinishesOpensNoWindowAndIsNotToldAbout()
    {
        await using var run = await ArchiveHarness.StartAsync(server);
        var windows = new ConcurrentQueue<DownloadWindowRequest>();
        var said = new ConcurrentQueue<DownloadNotice>();
        using var decisions = new DownloadWindows(run.Service, () => true, windows.Enqueue);
        using var notices = new DownloadNotices(run.Service, run.Archives, said.Enqueue);

        var quiet = await run.DownloadAsync("quiet.bin", Bytes, source: "chromeAuto", quietWindows: true);
        Assert.True((await run.WaitForJobAsync(quiet.Id, "completed")).QuietWindows);
        await SettleAsync();
        Assert.Empty(windows);
        Assert.Empty(said);

        // The same download without the flag: its windows open (start and completion) and the app says it finished.
        var loud = await run.DownloadAsync("loud.bin", Bytes, source: "chromeAuto");
        await run.WaitForJobAsync(loud.Id, "completed");
        await SettleAsync();
        Assert.NotEmpty(windows);
        Assert.All(windows, window => Assert.Equal(loud.Id, window.Id));
        Assert.Contains(new DownloadNotice(DownloadNoticeKind.Completed, "Download complete", "loud.bin"), said);
    }

    [Fact]
    public async Task AQuietPackageThatFinishesOpensNoWindowAndIsNotToldAbout()
    {
        await using var run = await ArchiveHarness.StartAsync(server);
        var windows = new ConcurrentQueue<DownloadWindowRequest>();
        var said = new ConcurrentQueue<DownloadNotice>();
        using var decisions = new DownloadWindows(run.Service, () => true, windows.Enqueue);
        using var notices = new DownloadNotices(run.Service, run.Archives, said.Enqueue);

        var first = await run.DownloadAsync("one.bin", Bytes, packageKey: "quiet-pack", source: "chromeAuto", quietWindows: true);
        var second = await run.DownloadAsync("two.bin", Bytes, packageKey: "quiet-pack", source: "chromeAuto", quietWindows: true);
        await run.WaitForJobAsync(first.Id, "completed");
        await run.WaitForJobAsync(second.Id, "completed");
        await SettleAsync();
        Assert.Empty(windows);
        Assert.DoesNotContain(said, notice => notice.Kind == DownloadNoticeKind.Completed);

        // A package sent without the flag opens its completion window and is told about once.
        var third = await run.DownloadAsync("three.bin", Bytes, packageKey: "loud-pack", source: "chromeAuto");
        var fourth = await run.DownloadAsync("four.bin", Bytes, packageKey: "loud-pack", source: "chromeAuto");
        await run.WaitForJobAsync(third.Id, "completed");
        await run.WaitForJobAsync(fourth.Id, "completed");
        var deadline = DateTimeOffset.UtcNow + TimeSpan.FromSeconds(15);
        while (!said.Any(notice => notice.Kind == DownloadNoticeKind.Completed) && DateTimeOffset.UtcNow < deadline) await Task.Delay(20);
        await SettleAsync();
        Assert.NotEmpty(windows);
        Assert.Single(said, notice => notice.Kind == DownloadNoticeKind.Completed);
    }
}

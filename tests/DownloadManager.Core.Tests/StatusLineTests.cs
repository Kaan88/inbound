using DownloadManager.Desktop.WinUI;

namespace DownloadManager.Core.Tests;

/// <summary>TB.F14: nothing is left on the status bar after what it was about has passed.</summary>
public sealed class StatusLineTests
{
    private sealed class FakeTimer(Action tick) : IOneShotTimer
    {
        public int Restarts { get; private set; }
        public bool Running { get; private set; }

        public void Restart()
        {
            Restarts++;
            Running = true;
        }

        public void Stop() => Running = false;

        public void Fire()
        {
            Assert.True(Running, "A timer that isn't running doesn't tick.");
            Running = false;
            tick();
        }
    }

    private static (PassingLine Line, List<FakeTimer> Made, Func<int> Ended) NewLine()
    {
        var made = new List<FakeTimer>();
        var line = new PassingLine(tick =>
        {
            var timer = new FakeTimer(tick);
            made.Add(timer);
            return timer;
        });
        var ended = 0;
        line.Ended += () => ended++;
        return (line, made, () => ended);
    }

    [Fact]
    public void ItsOneTimerIsMadeOnceAndKeptForEveryMessage()
    {
        // A timer held only by the method that started it was let go and never ticked: "Added 1 item for TorBox."
        // stayed for minutes after an add whose downloads were done within the five seconds.
        var (line, made, _) = NewLine();
        line.Show("Adding to TorBox…");
        line.Show("Added 1 item for TorBox.");
        line.Show("All files will download when ready.");

        var timer = Assert.Single(made);
        Assert.Equal(3, timer.Restarts);
        Assert.True(timer.Running);
    }

    [Fact]
    public void WhenItsTimeIsUpTheUsualLineComesBack()
    {
        var (line, made, ended) = NewLine();
        line.Show("Added 2 downloads.");
        Assert.Equal("Added 2 downloads.", line.Words);

        made[0].Fire();

        Assert.Null(line.Words);
        Assert.Equal(1, ended());
    }

    [Fact]
    public void ANewerMessageTakesTheBarWithATimeOfItsOwn()
    {
        var (line, made, _) = NewLine();
        line.Show("Adding to TorBox…");
        line.Show("Added 1 item for TorBox.");

        Assert.Equal("Added 1 item for TorBox.", line.Words);
        Assert.Equal(2, made[0].Restarts);
        Assert.Equal(TimeSpan.FromSeconds(5), PassingLine.Time);

        made[0].Fire();
        Assert.Null(line.Words);
    }

    [Fact]
    public void EndingItNowStopsTheTimerAndDrawsNothingMore()
    {
        var (line, made, ended) = NewLine();
        line.Show("Adding to TorBox…");

        line.End();

        Assert.Null(line.Words);
        Assert.False(made[0].Running);
        Assert.Equal(0, ended());
    }

    [Fact]
    public void NothingIsMadeOrShownBeforeTheFirstMessage()
    {
        var (line, made, ended) = NewLine();

        line.End();

        Assert.Null(line.Words);
        Assert.Empty(made);
        Assert.Equal(0, ended());
    }

    [Theory]
    [InlineData("MainWindow.Remote.cs", "private void SyncRemoteRows()")]
    [InlineData("MainWindow.Archives.cs", "private void SyncArchiveState()")]
    public void TheLineQuotingASelectedRowIsDrawnAgainWheneverThoseRowsChange(string file, string method)
    {
        // One selected row with no local file is quoted on the status bar, and such rows change in place. Only a
        // change of selection or of a local download drew the line again, so it went on saying "Next check 16:42:38"
        // under a row that by then said Choose files. A window can't run here, so this reads the method itself.
        var body = MethodBody(file, method);

        Assert.Contains("UpdateStatusText();", body, StringComparison.Ordinal);
    }

    private static string MethodBody(string file, string signature)
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "DownloadManager.slnx"))) directory = directory.Parent;
        Assert.NotNull(directory);

        var text = File.ReadAllText(Path.Combine(directory.FullName, "src", "DownloadManager.Desktop.WinUI", file));
        var start = text.IndexOf(signature, StringComparison.Ordinal);
        Assert.True(start >= 0, $"{signature} has moved out of {file}.");
        var open = text.IndexOf('{', start);
        var depth = 0;
        for (var i = open; i < text.Length; i++)
        {
            if (text[i] == '{') depth++;
            else if (text[i] == '}' && --depth == 0) return text[open..(i + 1)];
        }

        throw new InvalidOperationException($"{signature} in {file} has no end.");
    }
}

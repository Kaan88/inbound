using DownloadManager.Contracts;
using Xunit.Abstractions;

namespace DownloadManager.Integration.Tests;

/// <summary>
/// UI.B12: a Host started right after taskkill /F of the last one, while that one may still be exiting with its
/// mutex open, used to take it for a running copy and exit with 0 and no log line. The real process, its own profile.
/// </summary>
[Trait("Platform", "Windows")]
public sealed class HostRestartAfterTaskkillTests(ITestOutputHelper output)
{
    private const int Rounds = 5;

    // No account key is ever set, so nothing calls this; it only keeps the Host off the real provider.
    private const string UnusedApi = "http://127.0.0.1:9/";

    [Fact]
    public async Task AHostStartedRightAfterTaskkillRunsAndLogsWhetherItTookOver()
    {
        await using var host = await SandboxHost.StartAsync(UnusedApi);
        var stillExiting = 0;
        for (var round = 1; round <= Rounds; round++)
        {
            var killed = await host.TaskkillWithoutWaitingAsync();
            // Informative only: taskkill returns once the end is asked for, which is usually before the handles close.
            if (!killed.HasExited) stillExiting++;

            // Throws if the new Host exits, as the bug had it, or doesn't get ready.
            await host.RestartAsync();
            Assert.NotNull(host.ProcessId);
            await using (var connection = await host.ConnectAsync())
            {
                var settings = await connection.AskAsync<SettingsResult>(MessageTypes.SettingsGet, new { });
                Assert.NotNull(settings.Settings);
            }

            await SandboxHost.WaitUntilGoneAsync(killed);
            killed.Dispose();
        }

        var lines = host.LogLines();
        var tookOver = lines.Count(line => line.Contains("; taking over.", StringComparison.Ordinal));
        output.WriteLine($"{Rounds} rounds; old Host still exiting when taskkill returned: {stillExiting}; logged take-overs: {tookOver}.");
        // A dying Host never answers, so nothing may have been taken for a running copy.
        Assert.DoesNotContain(lines, line => line.Contains("Another Host", StringComparison.Ordinal));
        Assert.Equal(Rounds + 1, lines.Count(line => line.Contains("Host ready.", StringComparison.Ordinal)));
        await host.StopAsync();
        Assert.Equal(0, host.LastExitCode);
    }

    /// <summary>
    /// The moment taskkill can't be relied on to hit: the mutex still open in a process that no longer answers.
    /// A thread here holds it, as the dying Host's handle does, and ends without releasing it.
    /// </summary>
    [Fact]
    public async Task AStartWaitsForAnAbandonedMutexAndTakesOver()
    {
        await using var host = await SandboxHost.StartAsync(UnusedApi);
        await host.StopAsync();
        using var holder = new MutexHolder(host.MutexName);

        var restart = host.RestartAsync();
        await Task.Delay(TimeSpan.FromSeconds(1.5));
        Assert.False(restart.IsCompleted, "The Host should wait while the mutex is held and nothing answers.");
        holder.End(release: false);
        await restart;

        Assert.Contains(host.LogLines(), line => line.Contains("ended without releasing this profile (killed or crashed); taking over.", StringComparison.Ordinal));
        await AssertAnswersAsync(host);
        await host.StopAsync();
        Assert.Equal(0, host.LastExitCode);
    }

    [Fact]
    public async Task AStartWaitsForAReleasedMutexAndTakesOver()
    {
        await using var host = await SandboxHost.StartAsync(UnusedApi);
        await host.StopAsync();
        using var holder = new MutexHolder(host.MutexName);

        var restart = host.RestartAsync();
        await Task.Delay(TimeSpan.FromSeconds(1.5));
        Assert.False(restart.IsCompleted, "The Host should wait while the mutex is held and nothing answers.");
        holder.End(release: true);
        await restart;

        Assert.Contains(host.LogLines(), line => line.Contains("released this profile while this one was starting; taking over.", StringComparison.Ordinal));
        await AssertAnswersAsync(host);
        await host.StopAsync();
    }

    /// <summary>A copy that keeps the mutex and never answers keeps the profile: no second writer, and a line says why.</summary>
    [Fact]
    public async Task AStartGivesUpOnAMutexThatStaysHeldAndLogsIt()
    {
        await using var host = await SandboxHost.StartAsync(UnusedApi);
        await host.StopAsync();
        using var holder = new MutexHolder(host.MutexName);
        var started = host.LogLines().Count(line => line.Contains("Host starting;", StringComparison.Ordinal));

        var watch = System.Diagnostics.Stopwatch.StartNew();
        var exitCode = await host.StartExpectingExitAsync(TimeSpan.FromSeconds(30));
        watch.Stop();
        holder.End(release: true);

        output.WriteLine($"Gave up after {watch.Elapsed.TotalSeconds:0.0} s with exit code {exitCode}.");
        Assert.Equal(0, exitCode);
        Assert.InRange(watch.Elapsed, TimeSpan.FromSeconds(4.5), TimeSpan.FromSeconds(20));
        var lines = host.LogLines();
        Assert.Contains(lines, line => line.Contains("Another Host holds this profile but did not answer within 5 seconds; exiting.", StringComparison.Ordinal));
        Assert.Equal(started, lines.Count(line => line.Contains("Host starting;", StringComparison.Ordinal)));
    }

    // A second start beside a live Host is not run here: the live Host answers by opening the desktop window, or a
    // message box when the build has none beside it, on the desktop of whoever runs the tests.

    private static async Task AssertAnswersAsync(SandboxHost host)
    {
        await using var connection = await host.ConnectAsync();
        var settings = await connection.AskAsync<SettingsResult>(MessageTypes.SettingsGet, new { });
        Assert.NotNull(settings.Settings);
    }

    /// <summary>Owns the named mutex on a thread of its own, which can end with or without releasing it.</summary>
    private sealed class MutexHolder : IDisposable
    {
        private readonly Thread _thread;
        private readonly ManualResetEventSlim _held = new();
        private readonly ManualResetEventSlim _finish = new();
        private bool _release;
        private Exception? _failure;

        public MutexHolder(string name)
        {
            _thread = new Thread(() =>
            {
                try
                {
                    var mutex = new Mutex(initiallyOwned: true, name, out var createdNew);
                    // The stopped Host may not have closed its handle yet.
                    if (!createdNew && !mutex.WaitOne(TimeSpan.FromSeconds(10))) throw new InvalidOperationException("The mutex stayed held.");
                    _held.Set();
                    _finish.Wait();
                    if (_release) mutex.ReleaseMutex();
                    // Closing the handle without releasing leaves it owned until this thread ends, which abandons it.
                    mutex.Dispose();
                }
                catch (Exception ex)
                {
                    _failure = ex;
                    _held.Set();
                }
            }) { IsBackground = true };
            _thread.Start();
            Assert.True(_held.Wait(TimeSpan.FromSeconds(10)));
            Assert.Null(_failure);
        }

        public void End(bool release)
        {
            if (_finish.IsSet) return;
            _release = release;
            _finish.Set();
            Assert.True(_thread.Join(TimeSpan.FromSeconds(10)));
            Assert.Null(_failure);
        }

        public void Dispose()
        {
            if (!_finish.IsSet)
            {
                _release = true;
                _finish.Set();
                _thread.Join(TimeSpan.FromSeconds(10));
            }

            _held.Dispose();
            _finish.Dispose();
        }
    }
}

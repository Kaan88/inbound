using Microsoft.UI.Dispatching;

namespace DownloadManager.Desktop.WinUI;

/// <summary>A one-tick timer on a window's thread. Whoever holds this holds the timer, which only ticks while it is held.</summary>
internal sealed class DispatcherOneShot : IOneShotTimer
{
    private readonly DispatcherQueueTimer _timer;

    public DispatcherOneShot(DispatcherQueue queue, TimeSpan after, Action tick)
    {
        _timer = queue.CreateTimer();
        _timer.IsRepeating = false;
        _timer.Interval = after;
        _timer.Tick += (_, _) => tick();
    }

    public void Restart()
    {
        _timer.Stop();
        _timer.Start();
    }

    public void Stop() => _timer.Stop();
}

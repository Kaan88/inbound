using Microsoft.UI.Dispatching;

namespace DownloadManager.Desktop.WinUI;

/// <summary>
/// T3: tells the Host while a window shows running torrents (<c>torrent.watch</c>), so their progress comes every second
/// rather than every 5 seconds. A watch lasts <see cref="TorrentControlRules.LeaseSeconds"/> and is renewed every
/// <see cref="TorrentControlRules.RenewEvery"/> while it is wanted; the timer runs only then. A Host that doesn't know the
/// request is not asked again until <see cref="Reset"/> (a new connection).
/// </summary>
internal sealed class TorrentWatch
{
    private readonly DispatcherQueueTimer _timer;
    private readonly Func<bool, Task<bool>> _send;
    private bool _on;
    private bool _refused;

    /// <param name="send">Sends <c>torrent.watch</c> on or off; false when the Host doesn't know it.</param>
    /// <param name="renewed">Also runs on each renewal, while watching (the main window reads the DHT nodes then).</param>
    public TorrentWatch(DispatcherQueue queue, Func<bool, Task<bool>> send, Action? renewed = null)
    {
        _send = send;
        _timer = queue.CreateTimer();
        _timer.IsRepeating = true;
        _timer.Interval = TorrentControlRules.RenewEvery;
        _timer.Tick += async (_, _) =>
        {
            if (!_on) return;
            await SendAsync(true);
            renewed?.Invoke();
        };
    }

    public bool IsWatching => _on;

    /// <summary>Starts or ends the watch as the window now wants it.</summary>
    public void Sync(bool wanted)
    {
        if (_refused) wanted = false;
        if (wanted == _on) return;
        _on = wanted;
        if (wanted) _timer.Start();
        else _timer.Stop();
        _ = SendAsync(wanted);
    }

    /// <summary>A new connection: the Host forgot the watch, and a refusal from the last one no longer holds.</summary>
    public void Reset()
    {
        _on = false;
        _refused = false;
        _timer.Stop();
    }

    public void Stop()
    {
        _timer.Stop();
        if (_on) _ = SendAsync(false);
        _on = false;
    }

    private async Task SendAsync(bool on)
    {
        try
        {
            if (await _send(on)) return;
            _refused = true;
            _on = false;
            _timer.Stop();
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            UiTrace.Write($"Torrent watch not sent: {ex.GetType().Name}");
        }
    }
}

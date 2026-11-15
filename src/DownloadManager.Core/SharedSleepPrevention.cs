namespace DownloadManager.Core;

/// <summary>
/// One power request held for several users (downloads, site transfers): it is held while any of them asks, and released
/// when the last one lets go. Each user gets its own <see cref="Share"/>, which it drives as if it were the only one.
/// </summary>
public sealed class SharedSleepPrevention(ISleepPrevention inner) : IDisposable
{
    private readonly Lock _sync = new();
    private readonly HashSet<Handle> _asking = [];
    private bool _held;
    private bool _disposed;

    /// <summary>A handle for one user. Disposing it lets go of its ask; the request itself stays with this object.</summary>
    public ISleepPrevention Share() => new Handle(this);

    private void Set(Handle handle, bool active)
    {
        // Under one lock, so two users can't apply their answers out of order and leave the PC awake with nothing to do.
        lock (_sync)
        {
            if (_disposed) return;
            if (active) _asking.Add(handle);
            else _asking.Remove(handle);
            var hold = _asking.Count > 0;
            if (hold == _held) return;
            _held = hold;
            inner.SetActive(hold);
        }
    }

    public void Dispose()
    {
        lock (_sync)
        {
            if (_disposed) return;
            _disposed = true;
            _asking.Clear();
            if (_held) inner.SetActive(false);
            _held = false;
        }

        inner.Dispose();
    }

    private sealed class Handle(SharedSleepPrevention owner) : ISleepPrevention
    {
        public void SetActive(bool active) => owner.Set(this, active);

        public void Dispose() => owner.Set(this, false);
    }
}

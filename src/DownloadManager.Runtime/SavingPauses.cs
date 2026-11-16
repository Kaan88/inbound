namespace DownloadManager.Runtime;

/// <summary>
/// MAC.6: when Options' "Pause downloads on a phone hotspot, in Low Data Mode or in Low Power Mode" applies. The Host
/// reports what the Mac says (the menu-bar helper watches the network path and the power state); the hold itself is
/// the service's (<see cref="DownloadManagerService.SetSavingHoldAsync"/>), so the queue can't start others meanwhile.
/// </summary>
public sealed class SavingPauses(Func<bool, Task> setHold)
{
    private readonly Lock _gate = new();
    private readonly SemaphoreSlim _order = new(1, 1); // one change applied before the next
    private bool _costly;
    private bool _lowPower;
    private bool _enabled;

    /// <summary>Why downloads wait, or null when they don't: for the menu.</summary>
    public string? Reason
    {
        get
        {
            lock (_gate)
            {
                if (!_enabled) return null;
                return _lowPower ? "Low Power Mode" : _costly ? "a costly network" : null;
            }
        }
    }

    /// <summary>The helper's <c>network expensive|constrained|normal</c>.</summary>
    public Task NetworkAsync(string kind)
    {
        lock (_gate) _costly = kind is "expensive" or "constrained";
        return ApplyAsync();
    }

    /// <summary>The helper's <c>power low|normal</c>.</summary>
    public Task PowerAsync(string state)
    {
        lock (_gate) _lowPower = state == "low";
        return ApplyAsync();
    }

    /// <summary>The setting; turning it off ends the hold.</summary>
    public Task EnabledAsync(bool on)
    {
        lock (_gate) _enabled = on;
        return ApplyAsync();
    }

    /// <summary>The helper has gone, so nothing will say when saving ends: downloads don't wait for it.</summary>
    public Task HelperEndedAsync()
    {
        lock (_gate)
        {
            _costly = false;
            _lowPower = false;
        }

        return ApplyAsync();
    }

    private async Task ApplyAsync()
    {
        await _order.WaitAsync().ConfigureAwait(false);
        try
        {
            bool hold;
            lock (_gate) hold = _enabled && (_costly || _lowPower);
            await setHold(hold).ConfigureAwait(false);
        }
        finally
        {
            _order.Release();
        }
    }
}

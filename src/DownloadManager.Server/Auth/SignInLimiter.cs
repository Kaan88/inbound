using System.Net;

namespace DownloadManager.Server.Auth;

/// <summary>
/// A short, growing wait per client address after a wrong password, setup code or pairing code: 1 second after the
/// first, then 2, 4, 8 and so on up to 5 minutes. While an address waits, its tries are answered 429 without being
/// checked. A right answer clears its count; an hour after its last failure an address is forgotten.
/// </summary>
public sealed class SignInLimiter(TimeProvider time)
{
    public static readonly TimeSpan LongestWait = TimeSpan.FromMinutes(5);
    private static readonly TimeSpan Forget = TimeSpan.FromHours(1);
    private const int MaxAddresses = 10_000;

    private readonly object _gate = new();
    private readonly Dictionary<string, Entry> _entries = new(StringComparer.Ordinal);

    private sealed class Entry
    {
        public int Failures;
        public DateTimeOffset LastFailure;
        public DateTimeOffset WaitUntil;
    }

    /// <summary>How long this address must still wait, or null when it may try now.</summary>
    public TimeSpan? Wait(string address)
    {
        lock (_gate)
        {
            if (!_entries.TryGetValue(address, out var entry)) return null;
            var left = entry.WaitUntil - time.GetUtcNow();
            return left > TimeSpan.Zero ? left : null;
        }
    }

    public int Failures(string address)
    {
        lock (_gate) return _entries.TryGetValue(address, out var entry) ? entry.Failures : 0;
    }

    public TimeSpan Failed(string address)
    {
        lock (_gate)
        {
            var now = time.GetUtcNow();
            if (!_entries.TryGetValue(address, out var entry))
            {
                Prune(now);
                entry = new Entry();
                _entries[address] = entry;
            }

            entry.Failures++;
            entry.LastFailure = now;
            var wait = Delay(entry.Failures);
            entry.WaitUntil = now + wait;
            return wait;
        }
    }

    public void Succeeded(string address)
    {
        lock (_gate) _entries.Remove(address);
    }

    public static TimeSpan Delay(int failures)
    {
        if (failures < 1) return TimeSpan.Zero;
        var seconds = failures >= 10 ? LongestWait.TotalSeconds : Math.Min(LongestWait.TotalSeconds, Math.Pow(2, failures - 1));
        return TimeSpan.FromSeconds(seconds);
    }

    /// <summary>The address a request came from: an IPv4 address inside IPv6 is the IPv4 address.</summary>
    public static string AddressOf(IPAddress? address)
    {
        if (address is null) return "unknown";
        if (address.IsIPv4MappedToIPv6) address = address.MapToIPv4();
        return address.ToString();
    }

    private void Prune(DateTimeOffset now)
    {
        if (_entries.Count < 1000) return;
        foreach (var stale in _entries.Where(pair => now - pair.Value.LastFailure > Forget && now >= pair.Value.WaitUntil).Select(pair => pair.Key).ToList())
            _entries.Remove(stale);
        // Still too many (a flood from many addresses): the oldest go, waits included, rather than memory without end.
        while (_entries.Count >= MaxAddresses)
            _entries.Remove(_entries.OrderBy(pair => pair.Value.LastFailure).First().Key);
    }
}

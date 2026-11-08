using System.Net;
using System.Net.Sockets;

namespace DownloadManager.Server.Auth;

/// <summary>
/// A short, growing wait per client address after a wrong password, setup code or pairing code: 1 second after the
/// first, then 2, 4, 8 and so on up to 5 minutes. While an address waits, its tries are answered 429 without being
/// checked. A right answer clears its count; an hour after its last failure an address is forgotten.
/// <para>
/// A try is counted as wrong when it starts (<see cref="Begin"/>), before the slow password check, and cleared only
/// when it turns out right (<see cref="Succeeded"/>). Checking first and counting after would let a burst of parallel
/// guesses all pass the check before the first of them failed.
/// </para>
/// <para>
/// An IPv6 client counts by its /64 network: one machine can choose any address in it. IPv4 counts by the address.
/// There is no count for the whole server, on purpose: anyone could then keep the owner waiting. The password check's
/// own gate (two at a time, <see cref="PasswordHash"/>) is the limit across addresses.
/// </para>
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

    /// <summary>How long this address must still wait, or null when it may try now. Only looks.</summary>
    public TimeSpan? Wait(string address)
    {
        lock (_gate)
        {
            if (!_entries.TryGetValue(address, out var entry)) return null;
            var left = entry.WaitUntil - time.GetUtcNow();
            return left > TimeSpan.Zero ? left : null;
        }
    }

    /// <summary>
    /// Starts a try from this address: the wait still left (and nothing counted), or null with the try already
    /// counted as wrong. <paramref name="ifWrong"/> is the wait that try has started, for the answer when it is wrong.
    /// A right one is cleared with <see cref="Succeeded"/>.
    /// </summary>
    public TimeSpan? Begin(string address, out TimeSpan ifWrong)
    {
        lock (_gate)
        {
            var now = time.GetUtcNow();
            if (_entries.TryGetValue(address, out var entry))
            {
                var left = entry.WaitUntil - now;
                if (left > TimeSpan.Zero)
                {
                    ifWrong = left;
                    return left;
                }
            }
            else
            {
                Prune(now);
                entry = new Entry();
                _entries[address] = entry;
            }

            entry.Failures++;
            entry.LastFailure = now;
            ifWrong = Delay(entry.Failures);
            entry.WaitUntil = now + ifWrong;
            return null;
        }
    }

    public int Failures(string address)
    {
        lock (_gate) return _entries.TryGetValue(address, out var entry) ? entry.Failures : 0;
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

    /// <summary>
    /// What a request's address counts as: an IPv4 address inside IPv6 is the IPv4 address, IPv4 and IPv6 loopback are
    /// themselves, and any other IPv6 address is its /64 network.
    /// </summary>
    public static string AddressOf(IPAddress? address)
    {
        if (address is null) return "unknown";
        if (address.IsIPv4MappedToIPv6) address = address.MapToIPv4();
        if (address.AddressFamily != AddressFamily.InterNetworkV6 || IPAddress.IsLoopback(address)) return address.ToString();
        var bytes = address.GetAddressBytes();
        Array.Clear(bytes, 8, 8);
        return new IPAddress(bytes) + "/64";
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

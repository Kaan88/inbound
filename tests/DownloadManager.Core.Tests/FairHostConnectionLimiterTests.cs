using DownloadManager.Core;

namespace DownloadManager.Core.Tests;

/// <summary>UI.B7: connections to one host are shared out between jobs, and a changed limit applies at once.</summary>
public sealed class FairHostConnectionLimiterTests
{
    private const string Host = "cdn.example";
    private static readonly TimeSpan Wait = TimeSpan.FromSeconds(5);

    [Fact]
    public async Task NewOwnerIsGrantedBeforeOwnersHoldingSlots()
    {
        var limiter = new FairHostConnectionLimiter(2);
        var a = Guid.NewGuid();
        var b = Guid.NewGuid();
        var first = await limiter.AcquireAsync(Host, a, yieldable: false, CancellationToken.None);
        var second = await limiter.AcquireAsync(Host, a, yieldable: false, CancellationToken.None);

        // A asks for a third first, then B for its first; the freed connection goes to B.
        var aThird = limiter.AcquireAsync(Host, a, yieldable: false, CancellationToken.None);
        var bFirst = limiter.AcquireAsync(Host, b, yieldable: false, CancellationToken.None);
        Assert.False(aThird.IsCompleted);
        Assert.False(bFirst.IsCompleted);
        Assert.Equal(Host, limiter.WaitingHost(b));
        Assert.Null(limiter.WaitingHost(a));

        first.Dispose();
        var granted = await bFirst.WaitAsync(Wait);
        Assert.Equal(b, granted.OwnerId);
        Assert.False(aThird.IsCompleted);
        Assert.Null(limiter.WaitingHost(b));

        second.Dispose();
        (await aThird.WaitAsync(Wait)).Dispose();
        granted.Dispose();
        Assert.Equal(0, limiter.ActiveOn(Host));
    }

    [Fact]
    public async Task StarvedOwnerTriggersYieldOnLargestHolder()
    {
        var limiter = new FairHostConnectionLimiter(8);
        var a = Guid.NewGuid();
        var b = Guid.NewGuid();
        var c = Guid.NewGuid();
        var slots = new List<HostSlot>();
        for (var i = 0; i < 5; i++) slots.Add(await limiter.AcquireAsync(Host, a, yieldable: true, CancellationToken.None));
        for (var i = 0; i < 3; i++) slots.Add(await limiter.AcquireAsync(Host, b, yieldable: true, CancellationToken.None));
        Assert.DoesNotContain(slots, slot => slot.YieldRequested);

        var waits = new List<string>();
        limiter.WaitingChanged += owner => { lock (waits) waits.Add($"{(owner == c ? "c" : "?")}:{limiter.WaitingHost(owner) ?? "-"}"); };
        var cFirst = limiter.AcquireAsync(Host, c, yieldable: true, CancellationToken.None);
        Assert.False(cFirst.IsCompleted);

        // Fair share is max(1, 8 / 3) = 2: C, with none, takes one from A, which holds the most.
        var asked = slots.Where(slot => slot.YieldRequested).ToList();
        var yielded = Assert.Single(asked);
        Assert.Equal(a, yielded.OwnerId);
        Assert.True(yielded.YieldToken.IsCancellationRequested);

        yielded.Dispose();
        var granted = await cFirst.WaitAsync(Wait);
        Assert.Equal(c, granted.OwnerId);
        Assert.Equal(4, limiter.HeldBy(Host, a));
        Assert.Equal(3, limiter.HeldBy(Host, b));
        lock (waits) Assert.Equal(["c:" + Host, "c:-"], waits);

        // C asks for one more: it is below its share of 2, and A (4) is the furthest above it.
        var cSecond = limiter.AcquireAsync(Host, c, yieldable: true, CancellationToken.None);
        var next = Assert.Single(slots, slot => slot.YieldRequested && slot != yielded);
        Assert.Equal(a, next.OwnerId);
        next.Dispose();
        (await cSecond.WaitAsync(Wait)).Dispose();
        granted.Dispose();
        foreach (var slot in slots) slot.Dispose();
        Assert.Equal(0, limiter.ActiveOn(Host));
    }

    [Fact]
    public async Task SlotsThatCannotYieldAreNeverAsked()
    {
        var limiter = new FairHostConnectionLimiter(2);
        var a = Guid.NewGuid();
        var held = new[]
        {
            await limiter.AcquireAsync(Host, a, yieldable: false, CancellationToken.None),
            await limiter.AcquireAsync(Host, a, yieldable: false, CancellationToken.None),
        };

        var waiting = limiter.AcquireAsync(Host, Guid.NewGuid(), yieldable: true, CancellationToken.None);
        Assert.All(held, slot => Assert.False(slot.YieldRequested));
        held[0].Dispose();
        (await waiting.WaitAsync(Wait)).Dispose();
        held[1].Dispose();
    }

    [Fact]
    public async Task LoweringLimitNeverAllowsMoreThanTheNewLimit()
    {
        var limiter = new FairHostConnectionLimiter(4);
        var a = Guid.NewGuid();
        var b = Guid.NewGuid();
        var slots = new List<HostSlot>();
        for (var i = 0; i < 4; i++) slots.Add(await limiter.AcquireAsync(Host, a, yieldable: true, CancellationToken.None));

        limiter.UpdateLimit(2);
        Assert.Equal(2, limiter.Limit);
        // The excess is asked to go; nothing is taken by force.
        Assert.Equal(2, slots.Count(slot => slot.YieldRequested));
        Assert.Equal(4, limiter.ActiveOn(Host));

        // B, with none, is below its share of max(1, 2 / 2) = 1, so A is asked for one more.
        var bWait = limiter.AcquireAsync(Host, b, yieldable: true, CancellationToken.None);
        var asked = slots.Where(slot => slot.YieldRequested).ToList();
        Assert.Equal(3, asked.Count);

        // Each release first brings the gate down to the new limit; only then does B get in.
        asked[0].Dispose();
        Assert.Equal(3, limiter.ActiveOn(Host));
        asked[1].Dispose();
        Assert.Equal(2, limiter.ActiveOn(Host));
        Assert.False(bWait.IsCompleted);
        asked[2].Dispose();
        var granted = await bWait.WaitAsync(Wait);
        Assert.Equal(2, limiter.ActiveOn(Host));
        foreach (var slot in asked) slots.Remove(slot);

        // A new waiter can't get in either while the gate is at its limit.
        var late = limiter.AcquireAsync(Host, Guid.NewGuid(), yieldable: false, CancellationToken.None);
        Assert.False(late.IsCompleted);
        Assert.True(limiter.ActiveOn(Host) <= 2);
        granted.Dispose();
        (await late.WaitAsync(Wait)).Dispose();
        foreach (var slot in slots) slot.Dispose();
        Assert.Equal(0, limiter.ActiveOn(Host));
    }

    [Fact]
    public async Task RaisingTheLimitGrantsWaitersAtOnce()
    {
        var limiter = new FairHostConnectionLimiter(1);
        var held = await limiter.AcquireAsync(Host, Guid.NewGuid(), yieldable: false, CancellationToken.None);
        var waiting = limiter.AcquireAsync(Host, Guid.NewGuid(), yieldable: false, CancellationToken.None);
        Assert.False(waiting.IsCompleted);

        limiter.UpdateLimit(2);
        (await waiting.WaitAsync(Wait)).Dispose();
        held.Dispose();
        Assert.Equal(2, limiter.Peak);
    }

    [Fact]
    public async Task CancelledWaiterLeavesAndIsNotGranted()
    {
        var limiter = new FairHostConnectionLimiter(1);
        var owner = Guid.NewGuid();
        var held = await limiter.AcquireAsync(Host, Guid.NewGuid(), yieldable: false, CancellationToken.None);
        using var cancel = new CancellationTokenSource();
        var waiting = limiter.AcquireAsync(Host, owner, yieldable: false, cancel.Token);
        Assert.Equal(Host, limiter.WaitingHost(owner));

        cancel.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => waiting);
        Assert.Null(limiter.WaitingHost(owner));
        held.Dispose();
        Assert.Equal(0, limiter.ActiveOn(Host));
    }

    [Fact]
    public async Task HostsAreLimitedSeparately()
    {
        var limiter = new FairHostConnectionLimiter(1);
        var owner = Guid.NewGuid();
        var one = await limiter.AcquireAsync("a.example", owner, yieldable: false, CancellationToken.None);
        var two = await limiter.AcquireAsync("b.example", owner, yieldable: false, CancellationToken.None);
        one.Dispose();
        two.Dispose();
        one.Dispose();
        Assert.Equal(0, limiter.Active);
    }

    /// <summary>Many owners taking and giving back at random never see more than the limit out.</summary>
    [Fact]
    public async Task UnderLoadTheLimitHoldsAndEveryoneGetsThrough()
    {
        var limiter = new FairHostConnectionLimiter(4);
        var owners = Enumerable.Range(0, 6).Select(_ => Guid.NewGuid()).ToArray();
        var over = 0;
        var done = 0;
        var tasks = owners.SelectMany(owner => Enumerable.Range(0, 3).Select(async worker =>
        {
            var random = new Random(owner.GetHashCode() ^ worker);
            for (var i = 0; i < 40; i++)
            {
                using var slot = await limiter.AcquireAsync(Host, owner, yieldable: true, CancellationToken.None);
                if (limiter.ActiveOn(Host) > 4) Interlocked.Increment(ref over);
                try { await Task.Delay(random.Next(0, 3), slot.YieldToken); }
                catch (OperationCanceledException) { }
            }

            Interlocked.Increment(ref done);
        })).ToArray();

        await Task.WhenAll(tasks).WaitAsync(TimeSpan.FromSeconds(60));
        Assert.Equal(0, over);
        Assert.Equal(18, done);
        Assert.True(limiter.Peak <= 4, $"peak {limiter.Peak}");
        Assert.Equal(0, limiter.Active);
    }
}

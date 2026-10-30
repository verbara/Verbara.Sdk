using System.Collections.Concurrent;
using System.Diagnostics;
using FluentAssertions;

namespace Verbara.Sdk.Live.Tests;

/// <summary>What the outbox costs when it has nothing to raise, and how soon a waiting notification gets its turn.</summary>
public sealed class LiveEventOutboxAllocationTests
{
    private static readonly TimeSpan WaitBound = TimeSpan.FromSeconds(10);

    [Fact]
    public void Drain_ShouldAllocateNothing_WhenNothingIsPosted()
    {
        var outbox = new LiveEventOutbox(_ => { }, LiveEventOutbox.DefaultTurnWait);
        for (var i = 0; i < 3_000; i++)
            outbox.Drain();

        var before = GC.GetAllocatedBytesForCurrentThread();
        for (var i = 0; i < 1_000; i++)
            outbox.Drain();
        var allocated = GC.GetAllocatedBytesForCurrentThread() - before;

        allocated.Should().Be(0, "a drain with nothing posted has nothing to raise");
    }

    [Fact]
    public void Drain_ShouldRaiseAWaitingNotificationAsSoonAsTheEarlierOneFinishes_WhenAnotherThreadIsRaising()
    {
        // A turn wait far longer than the bound below: a waiter that is never woken is raised only when it runs out.
        var reported = new ConcurrentQueue<Exception>();
        var outbox = new LiveEventOutbox(reported.Enqueue, WaitBound);
        using var aRaising = new ManualResetEventSlim();
        using var releaseA = new ManualResetEventSlim();
        var clock = Stopwatch.StartNew();
        long aReturnedAt = 0;
        long bRaisedAt = 0;
        Exception? failure = null;

        var callerA = new Thread(() =>
        {
            try
            {
                outbox.Post(() =>
                {
                    aRaising.Set();
                    if (!releaseA.Wait(WaitBound + WaitBound))
                        throw new TimeoutException("caller A's handler was never released");
                    Interlocked.Exchange(ref aReturnedAt, clock.ElapsedTicks);
                });
                outbox.Drain();
            }
            catch (Exception ex) when (ex is not OutOfMemoryException)
            {
                failure = ex;
            }
        }) { IsBackground = true, Name = "outbox-raising-caller-A" };
        var callerB = new Thread(() =>
        {
            try
            {
                outbox.Post(() => Interlocked.Exchange(ref bRaisedAt, clock.ElapsedTicks));
                outbox.Drain();
            }
            catch (Exception ex) when (ex is not OutOfMemoryException)
            {
                failure = ex;
            }
        }) { IsBackground = true, Name = "outbox-waiting-caller-B" };

        callerA.Start();
        aRaising.Wait(WaitBound).Should().BeTrue("caller A's handler must start");
        callerB.Start();
        // fence-allow: LOOP-DRIVER — waits for caller B to block on its turn before A's handler is released, so the wake-up is what releases B
        SpinWait.SpinUntil(() => outbox.WaitingDrains == 1, WaitBound)
            .Should().BeTrue("caller B must be waiting for its turn");
        var releasedAt = clock.Elapsed;
        releaseA.Set();

        callerA.Join(WaitBound + WaitBound).Should().BeTrue();
        callerB.Join(WaitBound + WaitBound).Should().BeTrue();

        failure.Should().BeNull();
        reported.Should().BeEmpty("B's notification waited for A's instead of being raised alongside it");
        bRaisedAt.Should().BeGreaterThanOrEqualTo(aReturnedAt, "B's notification is raised after A's handler returned");
        (TimeSpan.FromTicks((long)(bRaisedAt * (TimeSpan.TicksPerSecond / (double)Stopwatch.Frequency))) - releasedAt)
            .Should().BeLessThan(TimeSpan.FromSeconds(2), "the finishing raise wakes the waiter instead of leaving it to its turn wait");
    }
}

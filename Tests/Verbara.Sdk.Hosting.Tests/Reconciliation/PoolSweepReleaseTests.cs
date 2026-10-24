using System.Diagnostics.CodeAnalysis;
using FluentAssertions;
using Microsoft.Extensions.Logging;
using Verbara.Sdk.Sessions;
using Verbara.Sdk.Sessions.Manager;

namespace Verbara.Sdk.Hosting.Tests.Reconciliation;

/// <summary>
/// The pool sweep against a server detached with its calls released: a released call is neither counted among the held
/// sessions whose server the pool does not hold, nor verified, nor ended. A server detached with "keep" keeps today's
/// count. The <c>Release</c> twins of the in-process pins in <see cref="PoolSweepIsolationTests"/> and
/// <see cref="PoolSweepEndsLostHangupsTests"/>, and the three scenarios of the sweep's requirement.
/// </summary>
/// <remarks>
/// Red on the stub of the overload, which detaches and releases nothing, save the "keep" scenario, a pin that is green
/// on it by construction.
/// </remarks>
[Collection(SweepCounterGroup.Name)]
[SuppressMessage("Reliability", "CA1001:Types that own disposable fields should be disposable", Justification = "Disposed via IAsyncLifetime")]
public sealed class PoolSweepReleaseTests : IAsyncLifetime
{
    private readonly PoolSweepRig _rig = new();

    public Task InitializeAsync() => Task.CompletedTask;

    public Task DisposeAsync() => _rig.DisposeAsync().AsTask().WaitAsync(SweepRig.Bound);

    private static bool HasEnded(CallSession call) =>
        call.State is CallSessionState.Completed or CallSessionState.Failed or CallSessionState.TimedOut;

    /// <summary>The Release twin of <c>PoolSweep_ShouldNotThrowAndShouldVerifyTheOther_WhenAServerIsRemovedWhileATickHoldsIt</c>.</summary>
    [Fact]
    public async Task PoolSweep_ShouldNeitherAskNorCountTheReleasedCall_WhenAServerIsRemovedWithReleaseWhileATickHoldsIt()
    {
        var removed = _rig.AddServer("a");
        var healthy = _rig.AddServer("b");
        var left = removed.LostCall(_rig.Manager, "on-removed");
        removed.StatusReply = removed.Hang;
        healthy.LostCall(_rig.Manager, "on-healthy");
        healthy.AsteriskLists();
        await _rig.StartAsync();

        var ticks = _rig.Ticks;
        _rig.Clock.Advance(PoolSweepRig.Interval);
        var held = await _rig.WaitUntilAsync(() => removed.StatusRequests == 1);
        var released = _rig.Manager.DetachFromServer("a", DetachedCalls.Release);
        await _rig.Pool!.RemoveServerAsync("a").AsTask().WaitAsync(SweepRig.Bound);
        var tickCompleted = await _rig.WaitUntilAsync(() => _rig.Ticks > ticks);
        var nextTick = await _rig.TickAsync();

        new
        {
            held,
            tickCompleted,
            nextTick,
            Released = released.Select(c => c.SessionId).ToArray(),
            RemovedAsked = removed.StatusRequests,
            LeftState = left.State,
            LeftEndings = _rig.EndingsOf(left),
            Errors = _rig.Log.Entries.Count(e => e.Level >= LogLevel.Error),
            Serverless = _rig.LastServerless,
        }.Should().BeEquivalentTo(
            new
            {
                held = true, tickCompleted = true, nextTick = true, Released = new[] { left.SessionId }, RemovedAsked = 1,
                LeftState = CallSessionState.Connected, LeftEndings = 0, Errors = 0, Serverless = (int?)0,
            },
            "a server removed with its calls released under the tick ends nothing; the released call is not asked "
            + $"about, not ended and not counted by the next tick. Measured: {_rig.Describe()}");
    }

    /// <summary>The Release twin of <c>PoolSweep_ShouldLeaveAndCountASessionWhoseServerIsNotInThePool_WhenItsCallLostItsHangup</c>.</summary>
    [Fact]
    public async Task PoolSweep_ShouldNotCountASessionWhoseServerIsNotInThePool_WhenThatServerWasDetachedWithRelease()
    {
        _rig.AddServer("a");
        var gone = _rig.AddServer("gone", inPool: false);
        var call = gone.LostCall(_rig.Manager, "orphan");
        gone.AsteriskLists();
        var before = SweepRig.Look(call);
        var released = _rig.Manager.DetachFromServer("gone", DetachedCalls.Release);
        await _rig.StartAsync();

        await _rig.TickAsync();

        new
        {
            Released = released.Select(c => c.SessionId).ToArray(),
            Look = SweepRig.Look(call),
            gone.StatusRequests,
            Serverless = _rig.LastServerless,
        }.Should().BeEquivalentTo(
            new { Released = new[] { call.SessionId }, Look = before, StatusRequests = 0, Serverless = (int?)0 },
            "a released session of a server the pool does not hold is neither verified nor ended nor counted. "
            + $"Measured: {_rig.Describe()}");
    }

    [Fact]
    public async Task PoolSweep_ShouldCountNothingAndSendNothing_WhenAServerIsDetachedWithItsCallsReleasedAndRemoved()
    {
        var a = _rig.AddServer("a");
        var b = _rig.AddServer("b");
        var calls = new[] { a.LostCall(_rig.Manager, "r1"), a.LostCall(_rig.Manager, "r2"), a.LostCall(_rig.Manager, "r3") };
        await _rig.StartAsync();

        var released = _rig.Manager.DetachFromServer("a", DetachedCalls.Release);
        await _rig.Pool!.RemoveServerAsync("a").AsTask().WaitAsync(SweepRig.Bound);
        var tick = await _rig.TickAsync();

        new
        {
            tick,
            Released = released.Count,
            Serverless = _rig.LastServerless,
            Asked = a.StatusRequests + b.StatusRequests,
            Ended = calls.Count(HasEnded),
        }.Should().BeEquivalentTo(
            new { tick = true, Released = 3, Serverless = (int?)0, Asked = 0, Ended = 0 },
            "a server removed with its 3 calls released leaves the next sweep nothing to count and nothing to send, "
            + $"and none of the calls ended. Measured: {_rig.Describe()}");
    }

    /// <summary>Pin (green on the stub by construction): a server removed with "keep" keeps today's count.</summary>
    [Fact]
    public async Task PoolSweep_ShouldCountTheHeldCalls_WhenAServerIsDetachedWithKeepAndRemoved()
    {
        var a = _rig.AddServer("a");
        _rig.AddServer("b");
        var calls = new[] { a.LostCall(_rig.Manager, "k1"), a.LostCall(_rig.Manager, "k2"), a.LostCall(_rig.Manager, "k3") };
        await _rig.StartAsync();

        var kept = _rig.Manager.DetachFromServer("a", DetachedCalls.Keep);
        await _rig.Pool!.RemoveServerAsync("a").AsTask().WaitAsync(SweepRig.Bound);
        var tick = await _rig.TickAsync();

        new { tick, Returned = kept.Count, Serverless = _rig.LastServerless, Asked = a.StatusRequests, Ended = calls.Count(HasEnded) }
            .Should().BeEquivalentTo(new { tick = true, Returned = 0, Serverless = (int?)3, Asked = 0, Ended = 0 },
                "a server removed with keep leaves its 3 calls held: the sweep counts them and neither verifies nor "
                + $"ends them. Measured: {_rig.Describe()}");
    }

    [Fact]
    public async Task PoolSweep_ShouldStopCountingTheCalls_WhenTheHostReleasesThemAfterAKeep()
    {
        var a = _rig.AddServer("a");
        _rig.AddServer("b");
        a.LostCall(_rig.Manager, "l1");
        a.LostCall(_rig.Manager, "l2");
        a.LostCall(_rig.Manager, "l3");
        await _rig.StartAsync();
        _rig.Manager.DetachFromServer("a", DetachedCalls.Keep);
        await _rig.Pool!.RemoveServerAsync("a").AsTask().WaitAsync(SweepRig.Bound);
        await _rig.TickAsync();
        var countedWhileKept = _rig.LastServerless;

        var released = _rig.Manager.DetachFromServer("a", DetachedCalls.Release);
        var tick = await _rig.TickAsync();

        new { countedWhileKept, Released = released.Count, tick, Serverless = _rig.LastServerless }.Should().BeEquivalentTo(
            new { countedWhileKept = (int?)3, Released = 3, tick = true, Serverless = (int?)0 },
            "calls kept on detach are counted by the sweep; once the host releases them the next sweep counts 0. "
            + $"Measured: {_rig.Describe()}");
    }
}

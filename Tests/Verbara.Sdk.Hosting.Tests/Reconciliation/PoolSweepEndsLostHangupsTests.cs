using System.Diagnostics.CodeAnalysis;
using FluentAssertions;
using Verbara.Sdk.Enums;
using Verbara.Sdk.Sessions;

namespace Verbara.Sdk.Hosting.Tests.Reconciliation;

/// <summary>
/// The pool sweep a multi-server registration adds verifies each server of the pool against its own <c>Status</c>, with
/// the held calls of that server as its candidates: a lost hangup ends as a reload ends it, a live call stays, and a
/// session whose server is not in the pool is left alone and counted.
/// </summary>
[Collection(SweepCounterGroup.Name)]
[SuppressMessage("Reliability", "CA1001:Types that own disposable fields should be disposable", Justification = "Disposed via IAsyncLifetime")]
public sealed class PoolSweepEndsLostHangupsTests : IAsyncLifetime
{
    private readonly PoolSweepRig _rig = new();

    public Task InitializeAsync() => Task.CompletedTask;

    public Task DisposeAsync() => _rig.DisposeAsync().AsTask().WaitAsync(SweepRig.Bound);

    private sealed record Ending(CallSessionState State, HangupCause? HangupCause, string? Cause, int CallEndedEvents);

    private Ending EndingOf(CallSession call) =>
        new(call.State, call.HangupCause, call.Metadata.GetValueOrDefault("cause"), _rig.EndingsOf(call));

    private static readonly Ending EndedAsAReload = new(CallSessionState.Completed, null, "reload", 1);

    [Fact]
    public async Task PoolSweep_ShouldEndALostHangupOnOneServerAsAReloadOnce_WhenThatAsteriskNoLongerListsIt()
    {
        var a = _rig.AddServer("a");
        _rig.AddServer("b");
        var call = a.LostCall(_rig.Manager, "lost");
        a.AsteriskLists();
        await _rig.StartAsync();

        await _rig.TickAsync();
        await _rig.TickAsync();

        new { Ending = EndingOf(call), a.StatusRequests }.Should().BeEquivalentTo(
            new { Ending = EndedAsAReload, StatusRequests = 1 },
            "the pool sweep verifies server A once and its completed Status lists none of the call's channels, so the "
            + $"call ends as a reload ends it, once. Measured: {_rig.Describe()}");
    }

    [Fact]
    public async Task PoolSweep_ShouldLeaveALiveCallAlone_WhenItsAsteriskStillListsIt()
    {
        _rig.PoolSweep.Should().NotBeNull("the multi-server registration registers the pool sweep");
        _rig.AddServer("a");
        var b = _rig.AddServer("b");
        var call = b.LostCall(_rig.Manager, "live");
        b.AsteriskLists("live");
        var before = SweepRig.Look(call);
        await _rig.StartAsync();

        await _rig.TickAsync();

        new { Look = SweepRig.Look(call), Endings = _rig.EndingsOf(call), b.StatusRequests }.Should().BeEquivalentTo(
            new { Look = before, Endings = 0, StatusRequests = 1 },
            $"server B lists every channel of the call, so the verification changes nothing. Measured: {_rig.Describe()}");
    }

    [Fact]
    public async Task PoolSweep_ShouldAskOnlyTheServerThatHoldsTheCandidate_WhenTheOtherServersTableHoldsTheSameUniqueId()
    {
        var a = _rig.AddServer("a");
        var call = a.LostCall(_rig.Manager, "same");
        a.AsteriskLists();

        // B's table holds a channel with the same unique id as one of A's legs, announced before the manager attached to
        // B, so no session holds it. Only the ServerId filter keeps A's call from being B's candidate.
        var b = _rig.AddServer("b", attach: false);
        b.ChannelWithoutSession(a.UniqueIdsOf("same")[0]);
        _rig.Manager.AttachToServer(b.Server, "b");
        await _rig.StartAsync();

        await _rig.TickAsync();

        new { A = a.StatusRequests, B = b.StatusRequests, Ending = EndingOf(call) }.Should().BeEquivalentTo(
            new { A = 1, B = 0, Ending = EndedAsAReload },
            $"a server's candidates are the calls whose ServerId is its pool id. Measured: {_rig.Describe()}");
    }

    [Fact]
    public async Task PoolSweep_ShouldVerifyEveryServerOfTheTick_WhenBothServersHoldALostHangup()
    {
        var a = _rig.AddServer("a");
        var b = _rig.AddServer("b");
        var calls = new Dictionary<string, CallSession>(StringComparer.Ordinal)
        {
            ["a"] = a.LostCall(_rig.Manager, "lost-a"),
            ["b"] = b.LostCall(_rig.Manager, "lost-b"),
        };
        a.AsteriskLists();
        b.AsteriskLists();
        var second = _rig.PoolOrder[1];
        await _rig.StartAsync();

        await _rig.TickAsync();

        new { Second = EndingOf(calls[second]), First = EndingOf(calls[_rig.PoolOrder[0]]) }.Should().BeEquivalentTo(
            new { Second = EndedAsAReload, First = EndedAsAReload },
            $"every server of the pool is verified on each tick, the second in the pool's order ({second}) as much as the "
            + $"first. Measured: {_rig.Describe()}");
    }

    [Fact]
    public async Task PoolSweep_ShouldLeaveAndCountASessionWhoseServerIsNotInThePool_WhenItsCallLostItsHangup()
    {
        _rig.PoolSweep.Should().NotBeNull("the multi-server registration registers the pool sweep");
        _rig.AddServer("a");
        var gone = _rig.AddServer("gone", inPool: false);
        var call = gone.LostCall(_rig.Manager, "orphan");
        gone.AsteriskLists();
        var before = SweepRig.Look(call);
        await _rig.StartAsync();

        await _rig.TickAsync();

        new { Look = SweepRig.Look(call), gone.StatusRequests, Serverless = _rig.LastServerless }.Should().BeEquivalentTo(
            new { Look = before, StatusRequests = 0, Serverless = (int?)1 },
            "a session whose server the pool does not hold is neither verified nor ended, and the tick reports it. "
            + $"Measured: {_rig.Describe()}");
    }

    [Fact]
    public async Task PoolSweepAndAnOwnLoop_ShouldSendTwoStatusAndEndALostHangupOnce_WhenTheHostKeptItsOwnReconciliation()
    {
        var a = _rig.AddServer("a");
        var live = a.LostCall(_rig.Manager, "live");
        var lost = a.LostCall(_rig.Manager, "lost");
        a.AsteriskLists("live");
        await _rig.StartAsync();

        // One interval of a host that still runs the 2.7.0 loop of its own beside the pool sweep.
        await _rig.TickAsync();
        await a.Server.ReconcileChannelsAsync().AsTask().WaitAsync(SweepRig.Bound);

        new { a.StatusRequests, Lost = EndingOf(lost), Live = SweepRig.Look(live).State }.Should().BeEquivalentTo(
            new { StatusRequests = 2, Lost = EndedAsAReload, Live = CallSessionState.Connected },
            "each reconciliation keeps its own read window: the server is asked twice per interval and the lost call "
            + $"still ends once. Measured: {_rig.Describe()}");
    }
}

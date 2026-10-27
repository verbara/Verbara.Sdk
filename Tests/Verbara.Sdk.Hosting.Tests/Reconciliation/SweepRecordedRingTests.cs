using FluentAssertions;
using FluentAssertions.Execution;
using Verbara.Sdk.Enums;
using Verbara.Sdk.Sessions;

namespace Verbara.Sdk.Hosting.Tests.Reconciliation;

/// <summary>
/// The reconciliation sweep treats a call in its initial state whose ring was recorded exactly as it treats the same
/// call with no ring: both become candidates at the same age, both are left alone while Asterisk still lists their
/// channel, and both end <c>Failed</c> with no hangup cause once it does not. No ringing timeout applies to either.
/// </summary>
/// <remarks>
/// The call is driven the same way in both runs, the ring by its leg's <c>Newstate: Ringing</c>. Where no handler
/// recorded that ring, the ring time is set through its public setter, so this pin binds what the sweep does with a
/// recorded ring, not how the ring came to be recorded.
/// </remarks>
[Collection(SweepCounterGroup.Name)]
public sealed class SweepRecordedRingTests
{
    private const string Id = "rang";

    /// <summary>What one run observed: what each sweep asked and what it left the call as.</summary>
    private sealed record Outcome(
        int StatusRequestsWhileYoung,
        int StatusRequestsWhileListed,
        bool LeftAsItWasWhileListed,
        int EndingsWhileListed,
        int StatusRequestsWhenGone,
        CallSessionState FinalState,
        HangupCause? FinalHangupCause,
        int Endings,
        CounterDeltas Counters);

    [Fact]
    public async Task Sweep_ShouldTreatTheCallAsItTreatsOneWithNoRing_WhenItsRingWasRecordedInTheInitialState()
    {
        var withRing = await RunAsync(ring: true);
        var withoutRing = await RunAsync(ring: false);

        using var scope = new AssertionScope();
        scope.AddReportable("with the ring", withRing.Describe);
        scope.AddReportable("without the ring", withoutRing.Describe);
        withRing.Outcome.Should().BeEquivalentTo(withoutRing.Outcome,
            "the sweep reads no ring: a call whose ring was recorded is swept exactly as the same call with none");
        withRing.Outcome.Should().BeEquivalentTo(
            new
            {
                StatusRequestsWhileYoung = 0,
                StatusRequestsWhileListed = 1,
                LeftAsItWasWhileListed = true,
                EndingsWhileListed = 0,
                StatusRequestsWhenGone = 1,
                FinalState = CallSessionState.Failed,
                FinalHangupCause = default(HangupCause?),
                Endings = 1,
                Counters = new CounterDeltas(Completed: 0, Failed: 1, TimedOut: 0, Orphaned: 0),
            },
            "a young call is no candidate; an old one is verified, left alone while listed, and ended failed with no "
            + "cause when Asterisk no longer lists it — never timed out");
    }

    private static async Task<(string Describe, Outcome Outcome)> RunAsync(bool ring)
    {
        await using var rig = new SweepRig();
        using var counters = new SessionCounters();
        var call = rig.CreatedCall(Id);
        if (ring)
        {
            rig.Server.Channels.OnNewState(rig.UniqueIdsOf(Id)[0], ChannelState.Ringing);
            call.RingingAt ??= DateTimeOffset.UtcNow;
        }

        call.State.Should().Be(CallSessionState.Created, "premise: the ring does not move the call");
        call.RingingAt.HasValue.Should().Be(ring, "premise: the call's ring is recorded only in the run with the ring");
        var sweep = rig.BuildSweep();

        rig.AsteriskLists(Id);
        await SweepRig.SweepOnceAsync(sweep);
        var whileYoung = rig.StatusRequests;

        rig.Age(call);
        var before = SweepRig.Look(call);
        await SweepRig.SweepOnceAsync(sweep);
        var whileListed = rig.StatusRequests - whileYoung;
        var afterTheListedSweep = SweepRig.Look(call);
        var endingsWhileListed = rig.EndingsOf(call);

        rig.AsteriskLists();
        await SweepRig.SweepOnceAsync(sweep);
        var whenGone = rig.StatusRequests - whileYoung - whileListed;

        return ($"ring={ring} before={before} afterTheListedSweep={afterTheListedSweep} {rig.Describe()}", new Outcome(
            whileYoung, whileListed, afterTheListedSweep == before, endingsWhileListed, whenGone,
            call.State, call.HangupCause, rig.EndingsOf(call), counters.Deltas));
    }
}

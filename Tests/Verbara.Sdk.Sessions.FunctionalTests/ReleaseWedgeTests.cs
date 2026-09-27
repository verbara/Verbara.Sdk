using FluentAssertions;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using Verbara.Sdk.Sessions.Diagnostics;
using Verbara.Sdk.Sessions.FunctionalTests.Infrastructure;

namespace Verbara.Sdk.Sessions.FunctionalTests;

/// <summary>
/// Binds that releasing ended calls cannot be switched off by the state of its own bookkeeping.
///
/// <para><b>The wedge.</b> The manager releases an ended call by walking a queue of the calls whose
/// endings it delivered, oldest first, and stopping at the first one still inside
/// <see cref="Manager.SessionOptions.CompletedRetention"/>. If the entry at the head cannot be
/// evaluated — it names a call the manager no longer holds, or a call with no completion time — a
/// walk that only removes what it can evaluate stops there, on every later call, for the life of the
/// process: every call that ends afterwards is held forever.</para>
///
/// <para><b>How the head goes bad.</b> Three ordinary routes put an unusable entry at the head: a
/// leg that arrives with the correlation (<c>linkedid</c>) of a call that already ended joins it and,
/// when it hangs up, ends it a second time, so its id is queued twice and the second copy outlives
/// the first; a reconnect reload that ends a call and, from the same snapshot, admits a leg of it the
/// SDK never saw, which then hangs up — the same double entry; and a consumer clearing an ended
/// call's <see cref="CallSession.CompletedAt"/>, which the public setter allows and which no
/// comparison with a cutoff can ever be true for. The control has no trigger at all.</para>
///
/// <para>Each test drives its trigger inside the retention window, moves the release cutoff past it
/// (no wall-clock wait — <see cref="ResidencyRig.MovePastRetention"/>), ends
/// <see cref="FurtherCalls"/> more calls, and asserts that no ended call past retention is still
/// held. <see cref="FurtherCalls"/> exceeds the health check's cap so that its reading, too, can tell
/// a working release from a wedged one.</para>
/// </summary>
public sealed class ReleaseWedgeTests
{
    /// <summary>Calls ended after the cutoff moved; more than <see cref="HealthCheckCap"/>.</summary>
    private const int FurtherCalls = 200;

    /// <summary><see cref="SessionHealthCheck"/> reads <c>GetRecentCompleted(100)</c>.</summary>
    private const int HealthCheckCap = 100;

    [Fact]
    public async Task Release_ShouldFreeEveryCallPastRetention_WhenNoTriggerPrecededIt()
    {
        await using var rig = new ResidencyRig();
        var victim = rig.Call("v");

        rig.MovePastRetention();
        rig.Calls("n", FurtherCalls);

        new
        {
            VictimHeld = rig.Manager.GetById(victim.SessionId) is not null,
            EndedHeldPastRetention = rig.EndedHeldPastRetention(),
        }.Should().BeEquivalentTo(
            new { VictimHeld = false, EndedHeldPastRetention = 0 },
            "with nothing but ordinary calls, every call that ended before the cutoff is released by "
            + $"the next ending. Measured: {rig.Describe()}");
    }

    [Fact]
    public async Task Release_ShouldFreeEveryCallPastRetention_WhenALegReusedAnEndedCallsLinkedIdAndHungUp()
    {
        await using var rig = new ResidencyRig();
        var victim = rig.Call("v");

        rig.LegJoins("x-v", "v");
        (rig.Manager.GetByChannelId("x-v")?.SessionId).Should().Be(victim.SessionId,
            "premise: a leg carrying an ended call's linkedid joins that call while it is retained");
        rig.LegLeaves("x-v");

        rig.MovePastRetention();
        rig.Calls("n", FurtherCalls);

        new
        {
            VictimHeld = rig.Manager.GetById(victim.SessionId) is not null,
            EndedHeldPastRetention = rig.EndedHeldPastRetention(),
        }.Should().BeEquivalentTo(
            new { VictimHeld = false, EndedHeldPastRetention = 0 },
            "a late leg ending a call a second time must not stop release for every call after it. "
            + $"Measured: {rig.Describe()}");
    }

    [Fact]
    public async Task Release_ShouldFreeEveryCallPastRetention_WhenAReloadEndedACallAndAdmittedAnUnseenLegOfItThatHungUp()
    {
        await using var rig = new ResidencyRig();
        await rig.StartAsync();
        var victim = rig.OpenAnsweredCall("v");

        // During the outage both legs the SDK knew hung up, while a leg it never saw, with the same
        // linkedid, is still up: the reload ends the call and admits that leg into it.
        await rig.ReconnectAsync(ResidencyRig.StatusLeg("x-v", "v"));
        new { victim.State, JoinedBy = rig.Manager.GetByChannelId("x-v")?.SessionId }.Should().BeEquivalentTo(
            new { State = CallSessionState.Completed, JoinedBy = victim.SessionId },
            $"premise: the reload ended the call and admitted the unseen leg into it. Measured: {rig.Describe()}");
        rig.LegLeaves("x-v");

        rig.MovePastRetention();
        rig.Calls("n", FurtherCalls);

        new
        {
            VictimHeld = rig.Manager.GetById(victim.SessionId) is not null,
            EndedHeldPastRetention = rig.EndedHeldPastRetention(),
        }.Should().BeEquivalentTo(
            new { VictimHeld = false, EndedHeldPastRetention = 0 },
            "the unseen leg's hangup ending the call a second time must not stop release for every call "
            + $"after it. Measured: {rig.Describe()}");
    }

    [Fact]
    public async Task Release_ShouldFreeEveryCallPastRetention_WhenAConsumerClearedAnEndedCallsCompletedAt()
    {
        await using var rig = new ResidencyRig();
        var victim = rig.Call("v");

        // CallSession.CompletedAt has a public setter. The victim itself is not asserted on: with no
        // completion time it is not past retention by any clock. What must not happen is that it
        // stops the release of every call that ends after it.
        victim.CompletedAt = null;

        rig.MovePastRetention();
        rig.Calls("n", FurtherCalls);

        rig.EndedHeldPastRetention().Should().Be(0,
            "an entry with no completion time must not stop release for every call after it. "
            + $"Measured: {rig.Describe()}");
    }

    [Fact]
    public async Task HealthCheck_ShouldCountOnlyCallsWithinRetention_WhenALegReusedAnEndedCallsLinkedIdAndHungUp()
    {
        await using var rig = new ResidencyRig();
        var ended = new List<CallSession> { rig.Call("v") };
        rig.LegJoins("x-v", "v");
        rig.LegLeaves("x-v");

        rig.MovePastRetention();
        ended.AddRange(rig.Calls("n", FurtherCalls));

        var health = await new SessionHealthCheck(rig.Manager).CheckHealthAsync(new HealthCheckContext());
        var cutoff = rig.Cutoff;
        var withinRetention = ended.Count(s => s.CompletedAt >= cutoff);

        health.Data["recentCompleted"].Should().Be(Math.Min(HealthCheckCap, withinRetention),
            "the health check reports recent completed calls, and a call that ended before the "
            + $"retention cutoff is not recent: {withinRetention} of the {ended.Count} calls this test "
            + $"ended lie within retention. Measured: {rig.Describe()}");
    }
}

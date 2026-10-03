using System.Diagnostics.CodeAnalysis;
using FluentAssertions;
using Verbara.Sdk.Enums;
using Verbara.Sdk.Sessions;

namespace Verbara.Sdk.Hosting.Tests.Reconciliation;

/// <summary>
/// A call is counted once, by what really ended it: the sweep's clock adds to no counter, and a call it
/// verified alive and that later hangs up with a failure cause is one <c>sessions.failed</c>.
/// </summary>
[Collection(SweepCounterGroup.Name)]
[SuppressMessage("Reliability", "CA1001:Types that own disposable fields should be disposable", Justification = "Disposed via IAsyncLifetime")]
public sealed class SweepCountsOnceTests : IAsyncLifetime
{
    private readonly SweepRig _rig = new();

    public Task InitializeAsync() => Task.CompletedTask;

    public Task DisposeAsync() => _rig.DisposeAsync().AsTask().WaitAsync(SweepRig.Bound);

    [Fact]
    public async Task Sweep_ShouldCountTheCallOnceAsFailed_WhenAVerifiedDialingCallLaterHangsUpWithAFailureCause()
    {
        var call = _rig.DialingCall("busy");
        _rig.Age(call);
        _rig.AsteriskLists("busy");
        var sweep = _rig.BuildSweep();
        using var counters = new SessionCounters();

        await SweepRig.SweepOnceAsync(sweep);
        _rig.HangUp("busy", HangupCause.UserBusy);

        new { State = call.State, Counters = counters.Deltas }.Should().BeEquivalentTo(
            new { State = CallSessionState.Failed, Counters = CounterDeltas.None with { Failed = 1 } },
            "Asterisk listed the call at the sweep, so the sweep counted nothing; the hangup with a failure cause "
            + $"ends it failed and counts it once. Measured: {_rig.Describe()}");
    }
}

using System.Diagnostics.CodeAnalysis;
using FluentAssertions;
using Verbara.Sdk.Ami.Actions;

namespace Verbara.Sdk.Hosting.Tests.Reconciliation;

/// <summary>
/// When the sweep asks Asterisk and what it asks: nothing while no held call is old enough, and one
/// <c>Status</c> — no <c>QueueStatus</c>, no <c>Agents</c> — for a sweep that holds any number of candidates.
/// </summary>
[Collection(SweepCounterGroup.Name)]
[SuppressMessage("Reliability", "CA1001:Types that own disposable fields should be disposable", Justification = "Disposed via IAsyncLifetime")]
public sealed class SweepAsksOnceTests : IAsyncLifetime
{
    private readonly SweepRig _rig = new();

    public Task InitializeAsync() => Task.CompletedTask;

    public Task DisposeAsync() => _rig.DisposeAsync().AsTask().WaitAsync(SweepRig.Bound);

    [Fact]
    public async Task Sweep_ShouldSendNothing_WhenNoHeldCallIsOlderThanTheDialingTimeout()
    {
        _rig.DialingCall("young-dialing");
        _rig.ConnectedCall("young-connected");
        _rig.AsteriskLists("young-dialing", "young-connected");
        var sweep = _rig.BuildSweep();

        await SweepRig.SweepOnceAsync(sweep);

        _rig.ActionsSent().Should().BeEmpty(
            "no held call is older than the dialing timeout, so the sweep has nothing to verify and asks nothing");
    }

    [Fact]
    public async Task Sweep_ShouldSendOneStatusAndNothingElse_WhenItHoldsThreeCandidatesInDifferentStates()
    {
        _rig.Age(_rig.DialingCall("dialing"));
        _rig.Age(_rig.RingingCall("ringing"));
        _rig.Age(_rig.ConnectedCall("connected"));
        _rig.AsteriskLists("dialing", "ringing", "connected");
        var sweep = _rig.BuildSweep();

        await SweepRig.SweepOnceAsync(sweep);

        new
        {
            Status = _rig.Sent<StatusAction>(),
            QueueStatus = _rig.Sent<QueueStatusAction>(),
            Agents = _rig.Sent<AgentsAction>(),
        }.Should().BeEquivalentTo(
            new { Status = 1, QueueStatus = 0, Agents = 0 },
            "one verification serves the whole sweep, and it is the reload's channel half only: queues and "
            + $"agents are not reloaded for it. Measured: {_rig.Describe()}");
    }
}

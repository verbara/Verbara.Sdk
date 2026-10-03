using System.Diagnostics.CodeAnalysis;
using FluentAssertions;
using Verbara.Sdk.Ami.Actions;
using Verbara.Sdk.Enums;
using Verbara.Sdk.Sessions;

namespace Verbara.Sdk.Hosting.Tests.Reconciliation;

/// <summary>
/// What the sweep's neighbours do, which a change to the sweep must leave as it is: an observed hangup ends the call
/// with its cause, the full load asks for the channels, the queues and the agents, and the reload after a reconnect
/// still reloads the queues and the agents and ends a dropped call once.
/// </summary>
[Collection(SweepCounterGroup.Name)]
[SuppressMessage("Reliability", "CA1001:Types that own disposable fields should be disposable", Justification = "Disposed via IAsyncLifetime")]
public sealed class SweepNeighbourPinsTests : IAsyncLifetime
{
    private readonly SweepRig _rig = new();

    public Task InitializeAsync() => Task.CompletedTask;

    public Task DisposeAsync() => _rig.DisposeAsync().AsTask().WaitAsync(SweepRig.Bound);

    [Fact]
    public void Hangup_ShouldEndTheCallFailedWithItsCause_WhenTheHangupIsObserved()
    {
        using var counters = new SessionCounters();
        var call = _rig.DialingCall("busy");

        _rig.HangUp("busy", HangupCause.UserBusy);

        new
        {
            call.State,
            call.HangupCause,
            Cause = call.Metadata.GetValueOrDefault("cause"),
            Endings = _rig.EndingsOf(call),
            Counters = counters.Deltas,
        }.Should().BeEquivalentTo(
            new
            {
                State = CallSessionState.Failed,
                HangupCause = (HangupCause?)HangupCause.UserBusy,
                Cause = default(string),
                Endings = 1,
                Counters = new CounterDeltas(Completed: 0, Failed: 1, TimedOut: 0, Orphaned: 0),
            },
            $"an observed hangup ends the call with the cause Asterisk gave, once. Measured: {_rig.Describe()}");
    }

    [Fact]
    public async Task RequestInitialStateAsync_ShouldAskForTheChannelsTheQueuesAndTheAgents_WhenItRuns()
    {
        await _rig.Server.RequestInitialStateAsync().AsTask().WaitAsync(SweepRig.Bound);

        new
        {
            Status = _rig.Sent<StatusAction>(),
            QueueStatus = _rig.Sent<QueueStatusAction>(),
            Agents = _rig.Sent<AgentsAction>(),
        }.Should().BeEquivalentTo(
            new { Status = 1, QueueStatus = 1, Agents = 1 },
            $"the full load reads the channels, the queues and the agents, one request each. Measured: {_rig.Describe()}");
    }

    [Fact]
    public async Task Reconnect_ShouldReloadTheQueuesAndTheAgentsAndEndTheDroppedCallOnce_WhenAsteriskNoLongerListsIt()
    {
        var dropped = _rig.ConnectedCall("dropped");
        var kept = _rig.DialingCall("kept");
        _rig.AsteriskLists("dropped", "kept");
        await _rig.Server.StartAsync().WaitAsync(SweepRig.Bound);
        var keptBefore = SweepRig.Look(kept);
        var atStart = _rig.ActionsSent();
        atStart.Should().BeEquivalentTo(
            new Dictionary<string, int> { ["StatusAction"] = 1, ["QueueStatusAction"] = 1, ["AgentsAction"] = 1 },
            "premise: the start loaded once and ended nothing");
        _rig.AsteriskLists("kept");

        _rig.Reconnect();

        (await _rig.WaitUntilAsync(() => _rig.Sent<AgentsAction>() >= 2 && _rig.EndingsOf(dropped) >= 1)).Should().BeTrue(
            $"premise: the reload ran to its last request and ended the dropped call. Measured: {_rig.Describe()}");
        new
        {
            Status = _rig.Sent<StatusAction>(),
            QueueStatus = _rig.Sent<QueueStatusAction>(),
            Agents = _rig.Sent<AgentsAction>(),
            Dropped = new { dropped.State, Cause = dropped.Metadata.GetValueOrDefault("cause"), Endings = _rig.EndingsOf(dropped) },
            Kept = SweepRig.Look(kept),
        }.Should().BeEquivalentTo(
            new
            {
                Status = 2,
                QueueStatus = 2,
                Agents = 2,
                Dropped = new { State = CallSessionState.Completed, Cause = "reload", Endings = 1 },
                Kept = keptBefore,
            },
            "the reload after a reconnect reads the channels, the queues and the agents once more, ends the call Asterisk no "
            + $"longer lists once, and leaves the one it lists. Measured: {_rig.Describe()}");
    }
}

using FluentAssertions;
using FluentAssertions.Execution;
using Verbara.Sdk.Enums;
using Verbara.Sdk.Sessions.Manager;
using Verbara.Sdk.Sessions.Tests.Harness;
using Verbara.Sdk.Tests.Shared.Sessions;

namespace Verbara.Sdk.Sessions.Tests.Manager;

public sealed class CallSessionManagerBridgeCleanupTests
{
    [Fact]
    public async Task Ending_ShouldLeaveNoPriorBridgeAlias_WhenTheCallMovesAndItsSessionIdIsReusedOnTheSameServer()
    {
        var clock = new FakeTimeProvider();
        await using var rig = await ReleaseRig.StartAsync(clock,
            new SessionOptions { CompletedRetention = TimeSpan.FromSeconds(1) }, serverIds: ["a", "b"]);
        var old = rig.ConnectedCall("a", "moving");
        var first = ReleaseRig.BridgeOf("moving");
        const string second = "moving-second-bridge";
        var bridges = rig["a"].Server.Bridges;
        var other = rig.Arrival("b", "other");
        rig["b"].Server.Bridges.OnBridgeCreated(first, "mixing", "simple_bridge", null, null);
        rig["b"].Server.Bridges.OnChannelEntered(first, ReleaseRig.CallerLeg("other"));
        bridges.OnBridgeCreated(second, "mixing", "simple_bridge", null, null);
        bridges.OnChannelLeft(first, ReleaseRig.CallerLeg("moving"));
        bridges.OnChannelLeft(first, ReleaseRig.CalleeLeg("moving"));
        bridges.OnChannelEntered(second, ReleaseRig.CallerLeg("moving"));
        bridges.OnChannelEntered(second, ReleaseRig.CalleeLeg("moving"));
        old.BridgeId.Should().Be(second, "the complete move was delivered before checking the indexes");

        using var assertions = new AssertionScope();
        rig.Manager.GetByBridgeId("a", first).Should().BeNull("only the call's current bridge remains indexed");
        rig.Manager.GetByBridgeId("a", second).Should().BeSameAs(old);
        rig.Manager.GetByBridgeId("b", first).Should().BeSameAs(other);
        Action invariantAfterMove = () => SessionIndexInvariant.Assert(rig.Manager);
        invariantAfterMove.Should().NotThrow();
        KeyValuePair<string, string>[]? atEnding = null;
        using var subscription = rig.Manager.Events.Subscribe(new CallSessionManagerDetachReleaseTests.Observer(e =>
        {
            if (e is CallEndedEvent && e.SessionId == old.SessionId)
                atEnding = rig.Manager.InspectIndexes().ServerIndexes.Single(i => i.ServerId == "a").Bridges.ToArray();
        }));
        rig.HangUpCall("a", "moving");
        old.EndingDelivered.Should().BeTrue();
        rig.Manager.GetById(old.SessionId).Should().BeSameAs(old, "retention has not yet expired");
        atEnding.Should().NotBeNull();
        atEnding.Should().BeEmpty("the subscriber runs before any completion-driven retention walk");
        rig.Manager.InspectIndexes().ServerIndexes.Single(i => i.ServerId == "a").Bridges.Should().BeEmpty();
        clock.Advance(old.CompletedAt!.Value + TimeSpan.FromSeconds(2) - clock.GetUtcNow());
        rig.Arrival("a", "walk-trigger");
        rig.Manager.GetById(old.SessionId).Should().BeNull("an ordinary arrival performed the expired-call walk");

        rig.Manager.DetachFromServer("a", DetachedCalls.Keep);
        rig["a"].Server.Channels.OnNewChannel("replacement-live", "PJSIP/replacement-live", ChannelState.Up,
            linkedId: "replacement-L");
        rig.Manager.AttachToServer(rig["a"].Server, "a");
        const string replacementBridge = "replacement-bridge";
        var replacement = new CallSession(old.SessionId, "replacement-L", "a", CallDirection.Inbound)
        { BridgeId = replacementBridge, State = CallSessionState.Connected };
        replacement.AddParticipant(new SessionParticipant
        { UniqueId = "replacement-live", Channel = "PJSIP/replacement-live", Technology = "PJSIP" });
        rig.Manager.RegisterReconstructedSession(replacement).Should().BeTrue();
        rig.Manager.GetById(old.SessionId).Should().BeSameAs(replacement);
        rig.Manager.GetByBridgeId("a", first).Should().BeNull("the old hint must not resolve a reused session id");
        rig.Manager.GetByBridgeId("a", second).Should().BeNull();
        rig.Manager.GetByBridgeId("a", replacementBridge).Should().BeSameAs(replacement);
        rig.Manager.GetByBridgeId("b", first).Should().BeSameAs(other);
        rig.Manager.GetByBridgeId(first).Should().BeSameAs(other);
        Action invariantAfterReuse = () => SessionIndexInvariant.Assert(rig.Manager);
        invariantAfterReuse.Should().NotThrow();
    }

    [Fact]
    public async Task OnBridgeChannelEntered_ShouldPreserveAnotherOwnerOfThePriorBridge_WhenTheCallMoves()
    {
        await using var rig = await ReleaseRig.StartAsync("a");
        var moving = rig.ConnectedCall("a", "moving");
        var first = ReleaseRig.BridgeOf("moving");
        var survivor = rig.Arrival("a", "survivor");
        var bridges = rig["a"].Server.Bridges;
        bridges.OnChannelEntered(first, ReleaseRig.CallerLeg("survivor"));
        rig.Manager.GetByBridgeId("a", first).Should().BeSameAs(survivor, "the old entry is already reassigned");
        const string second = "moving-second-bridge";
        bridges.OnBridgeCreated(second, "mixing", "simple_bridge", null, null);
        bridges.OnChannelEntered(second, ReleaseRig.CallerLeg("moving"));
        rig.Manager.GetByBridgeId("a", first).Should().BeSameAs(survivor);
        rig.Manager.GetByBridgeId("a", second).Should().BeSameAs(moving);
        SessionIndexInvariant.Assert(rig.Manager);
        rig.HangUpCall("a", "moving");
        rig.Manager.GetByBridgeId("a", first).Should().BeSameAs(survivor);
        rig.Manager.GetByBridgeId("a", second).Should().BeNull();
        SessionIndexInvariant.Assert(rig.Manager);
    }

    [Fact]
    public async Task OnBridgeChannelEntered_ShouldPublishNoBridgeEntry_WhenTheCapturedCallEndedBeforeItsLockWasTaken()
    {
        await using var rig = await ReleaseRig.StartAsync("a");
        var call = rig.ConnectedCall("a", "late");
        var first = call.BridgeId;
        const string lateBridge = "late-second-bridge";
        var bridges = rig["a"].Server.Bridges;
        bridges.OnBridgeCreated(lateBridge, "mixing", "simple_bridge", null, null);
        using var parker = StepParker.Install(rig.Manager, HandlerStep.BridgeEntryFound, "a", call.SessionId);
        var handler = ReleaseRig.OnThread(() => bridges.OnChannelEntered(lateBridge, ReleaseRig.CallerLeg("late")));
        try
        {
            parker.WaitUntilParked(ReleaseRig.Bound);
            rig.HangUpCall("a", "late");
            call.EndingDelivered.Should().BeTrue("the captured call ends while its bridge handler remains parked");
            rig.Manager.GetById(call.SessionId).Should().BeSameAs(call, "the ended call is still retained");
            rig.Manager.InspectIndexes().ServerIndexes.Single(i => i.ServerId == "a").Bridges.Should().BeEmpty();
            var eventCount = call.Events.Count;
            var sequence = rig.Sequence;
            parker.LetGo();
            await handler.WaitAsync(ReleaseRig.Bound);
            call.BridgeId.Should().Be(first);
            call.Events.Should().HaveCount(eventCount);
            rig.Sequence.Should().Be(sequence);
            rig.Manager.GetByBridgeId("a", lateBridge).Should().BeNull();
            rig.Manager.InspectIndexes().ServerIndexes.Single(i => i.ServerId == "a").Bridges.Should().BeEmpty();
            SessionIndexInvariant.Assert(rig.Manager);
        }
        finally
        {
            parker.LetGo();
            await handler.WaitAsync(ReleaseRig.Bound);
        }
    }
}

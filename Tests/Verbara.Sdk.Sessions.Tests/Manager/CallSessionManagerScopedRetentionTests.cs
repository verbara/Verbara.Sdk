using FluentAssertions;
using FluentAssertions.Execution;
using Verbara.Sdk.Enums;
using Verbara.Sdk.Sessions.Manager;
using Verbara.Sdk.Sessions.Tests.Harness;

namespace Verbara.Sdk.Sessions.Tests.Manager;

public sealed class CallSessionManagerScopedRetentionTests
{
    [Fact]
    public async Task Ending_ShouldDropTheBridgeBeforeAnyRetentionWalk_WhenAnEndedCallIsStillRetained()
    {
        var clock = new FakeTimeProvider();
        await using var rig = await ReleaseRig.StartAsync(clock,
            new SessionOptions { CompletedRetention = TimeSpan.FromSeconds(1) }, serverIds: ["a", "b"]);
        var old = rig.ConnectedCall("a", "retained");
        var bridge = ReleaseRig.BridgeOf("retained");
        KeyValuePair<string, string>[]? bridgesBeforeRetentionWalk = null;
        CallSessionState? stateAtEndingNotification = null;
        using var endingSubscription = rig.Manager.Events.Subscribe(new CallSessionManagerDetachReleaseTests.Observer(evt =>
        {
            if (evt is not CallEndedEvent || evt.SessionId != old.SessionId) return;
            stateAtEndingNotification = old.State;
            bridgesBeforeRetentionWalk = rig.Manager.InspectIndexes().ServerIndexes.Single(i => i.ServerId == "a")
                .Bridges.ToArray();
        }));
        rig.HangUpCall("a", "retained");
        stateAtEndingNotification.Should().Be(CallSessionState.Completed, "the synchronous ending notification ran");
        bridgesBeforeRetentionWalk.Should().NotBeNull();
        bridgesBeforeRetentionWalk.Should().BeEmpty(
            "the synchronous ending subscriber runs before completion's retention walk and must already see no bridge");
        rig.Manager.GetById(old.SessionId).Should().BeSameAs(old, "the ended call is initially retained");
        rig.Manager.InspectIndexes().ServerIndexes.Single(i => i.ServerId == "a").Bridges.Should().BeEmpty(
            "the ending drops its bridge while the ended call is still retained, before any retention walk");
        clock.Advance(old.CompletedAt!.Value + TimeSpan.FromSeconds(2) - clock.GetUtcNow());
        rig.Arrival("a", "walk-trigger");
        rig.Manager.GetById(old.SessionId).Should().BeNull("an actual arrival has run the expired-call walk");

        rig.Manager.DetachFromServer("b", DetachedCalls.Keep);
        rig["b"].Server.Channels.OnNewChannel("b-live", "PJSIP/b-live", ChannelState.Up, linkedId: "b-L");
        rig.Manager.AttachToServer(rig["b"].Server, "b");
        var replacement = new CallSession(old.SessionId, "b-L", "b", CallDirection.Inbound)
        { BridgeId = bridge, State = CallSessionState.Connected };
        replacement.AddParticipant(new SessionParticipant { UniqueId = "b-live", Channel = "PJSIP/b-live", Technology = "PJSIP" });
        rig.Manager.RegisterReconstructedSession(replacement).Should().BeTrue();
        var eventsBefore = replacement.Events.Count;
        var notificationsBefore = rig.KindsOf(replacement.SessionId).Length;

        using var assertions = new AssertionScope();
        rig.Manager.InspectIndexes().ServerIndexes.Single(i => i.ServerId == "a").Bridges.Should().BeEmpty();
        rig.Manager.GetByBridgeId("a", bridge).Should().BeNull("a cannot resolve b through an expired session-id hint");
        rig["a"].Server.Bridges.OnBlindTransfer(bridge, "PJSIP/target", "200", "internal");
        replacement.State.Should().Be(CallSessionState.Connected);
        replacement.Events.Count.Should().Be(eventsBefore);
        rig.KindsOf(replacement.SessionId).Length.Should().Be(notificationsBefore);
        rig["a"].Server.Bridges.OnBridgeDestroyed(bridge);
        replacement.BridgeId.Should().Be(bridge, "a's destruction cannot clear b's bridge");
        rig.Manager.GetByBridgeId("b", bridge).Should().BeSameAs(replacement);
        rig.Manager.GetById(old.SessionId).Should().BeSameAs(replacement);
    }
    [Fact]
    public async Task Ending_ShouldKeepTheBridgeOfAnotherSession_WhenTheBridgeWasReassignedBeforeEnding()
    {
        await using var rig = await ReleaseRig.StartAsync(serverIds: ["a"]);
        var ending = rig.ConnectedCall("a", "ending");
        var bridge = ReleaseRig.BridgeOf("ending");
        var current = rig.Arrival("a", "current");
        rig["a"].Server.Bridges.OnChannelEntered(bridge, ReleaseRig.CallerLeg("current"));
        rig.Manager.GetByBridgeId("a", bridge).Should().BeSameAs(current, "the bridge now names the current call");

        rig.HangUpCall("a", "ending");

        using var assertions = new AssertionScope();
        ending.State.Should().Be(CallSessionState.Completed);
        current.State.Should().Be(CallSessionState.Created, "the current one-leg call remains in its original state");
        rig.Manager.GetByBridgeId("a", bridge).Should().BeSameAs(current);
        rig.Manager.InspectIndexes().ServerIndexes.Single(i => i.ServerId == "a").Bridges
            .Should().ContainSingle().Which.Should().Be(new KeyValuePair<string, string>(bridge, current.SessionId),
                "an ending removes the bridge only while it still names the ending session");
    }

    [Fact]
    public async Task Retention_ShouldLetAnArrivalFinishInsideTheEndingSubscriber_WhenThatArrivalReleasesTheNotifiedCall()
    {
        var clock = new FakeTimeProvider();
        await using var rig = await ReleaseRig.StartAsync(clock,
            new SessionOptions { CompletedRetention = TimeSpan.FromSeconds(1) }, serverIds: ["a", "b"]);
        var call = rig.Arrival("a", "notified");
        Task? arrival = null;
        var subscriberRan = false;
        var callLockHeldInSubscriber = false;
        var helperStartedInSubscriber = false;
        var arrivalFinishedInSubscriber = false;
        using var helperStarted = new ManualResetEventSlim();
        using var subscription = rig.Manager.Events.Subscribe(new CallSessionManagerDetachReleaseTests.Observer(evt =>
        {
            if (evt is not CallEndedEvent || evt.SessionId != call.SessionId) return;
            subscriberRan = true;
            callLockHeldInSubscriber = call.SyncRoot.IsHeldByCurrentThread;
            clock.Advance(call.CompletedAt!.Value + TimeSpan.FromSeconds(2) - clock.GetUtcNow());
            rig.Manager.ReleaseQueueEntriesFor(call.SessionId).Should().Be(1, "the ending queued this exact call before notifying");
            arrival = ReleaseRig.OnThread(() =>
            {
                helperStarted.Set();
                return rig.Arrival("b", "from-subscriber");
            });
            helperStartedInSubscriber = helperStarted.Wait(ReleaseRig.Bound);
            arrivalFinishedInSubscriber = ReleaseRig.Returns(arrival, TimeSpan.FromSeconds(1));
        }));
        try
        {
            rig.HangUp("a", ReleaseRig.CallerLeg("notified"));
        }
        finally
        {
            // The synchronous subscriber has unwound and released the call lock before cleanup waits for the helper.
            if (arrival is not null) await arrival.WaitAsync(ReleaseRig.Bound);
        }
        new { subscriberRan, callLockHeldInSubscriber, helperStartedInSubscriber, arrivalFinishedInSubscriber,
            HelperJoinedInCleanup = arrival?.IsCompletedSuccessfully, OldReleased = rig.Manager.GetById(call.SessionId) is null }
            .Should().BeEquivalentTo(new { subscriberRan = true, callLockHeldInSubscriber = true,
                helperStartedInSubscriber = true, arrivalFinishedInSubscriber = true,
                HelperJoinedInCleanup = (bool?)true, OldReleased = true },
                "the public synchronous ending subscriber can observe a completed arrival on b while a's notified call is past retention; cleanup always joins the helper");
    }

}

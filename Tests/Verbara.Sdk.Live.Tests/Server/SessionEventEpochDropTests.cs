using FluentAssertions;
using FluentAssertions.Execution;
using Verbara.Sdk.Ami.Actions;
using Verbara.Sdk.Ami.Events;
using Verbara.Sdk.Live.Tests.Harness;
using Verbara.Sdk.Sessions;
using Verbara.Sdk.Sessions.Manager;
using Xunit.Abstractions;

namespace Verbara.Sdk.Live.Tests.Server;

/// <summary>
/// The loss mark a session notification carries (<see cref="SessionDomainEvent.Epoch"/>) across a dropped event and on
/// the paths that raise outside an event's delivery: a drop between a call's start and its end shows on the ending; an
/// opening (an admission from the channel table, a reload's snapshot admission) carries a mark no later than the content
/// it announces, so a drop after that content shows on the call's next notification; a closing raised outside a
/// delivery (a reload's ending, a wrap-up's timer, a release) carries a mark no earlier than its content; a server
/// re-attached to a new connection instance carries a new connection identity; and a wrap-up closed by a connect on
/// another server keeps its own server's mark.
/// </summary>
/// <remarks>
/// <para>
/// A real <see cref="Verbara.Sdk.Ami.Connection.AmiConnection"/> over <see cref="Run"/>: every frame goes over the wire
/// and through the connection's pump. A drop is made on a pump of capacity one by holding its dispatch inside an
/// observer, queueing one event behind it and sending one more, which the full buffer refuses
/// (<see cref="EpochRig.DropOneEventAsync"/>).
/// </para>
/// <para>
/// Gate-sequenced, never timed: a held dispatch or a parked engine step sets its entered signal before it blocks and the
/// test waits on it; an event is known to be read by a <c>Ping</c> answered after it; every gate is released only after
/// the reads. Every wait is bounded by <see cref="Run.Bound"/> and ends on its signal.
/// </para>
/// </remarks>
public sealed class SessionEventEpochDropTests(ITestOutputHelper output)
{
    private const string ServerId = "srv-epoch";
    private const string Connected = "[AMI] Connected";
    private const string Reconnecting = "[AMI] Reconnecting";
    private const string StateLoaded = "[LIVE] State loaded";
    private const string Caller = "1700000500.1";
    private const string CallerChannel = "PJSIP/pstn-00000501";
    private const string Other = "1700000500.2";
    private const string OtherChannel = "PJSIP/pstn-00000502";
    private const string BufferedId = "1700000500.9";

    [Fact]
    public async Task CallEnded_ShouldCarryADifferentDroppedCount_WhenAnEventWasDroppedBeforeTheHangup()
    {
        var peer = new BootingAsterisk { BootedAtLogin = true };
        await using var run = await Run.ConnectAsync(peer, eventPumpCapacity: 1);
        await run.StartServerAsync();
        await using var manager = EpochRig.NewManager(new FakeTimeProvider());
        var recorder = EpochRecorder.On(manager);
        manager.AttachToServer(run.Server, ServerId);
        await peer.SendEventAsync("Newchannel", AsteriskFrames.Newchannel(Caller, CallerChannel, Caller));
        var started = await recorder.WaitAsync<CallStartedEvent>();

        await EpochRig.DropOneEventAsync(run, peer);
        await peer.SendEventAsync("Hangup", AsteriskFrames.Hangup(Caller, CallerChannel, Caller));
        var ended = await recorder.WaitAsync<CallEndedEvent>();

        using (new AssertionScope())
        {
            started.Epoch.Should().NotBeNull("premise: the call started over the SDK's connection");
            started.Epoch?.DroppedBefore.Should().Be(0, "premise: nothing was dropped before the call started");
            ended.Epoch.Should().NotBeNull("the call ended over the SDK's connection");
            ended.Epoch?.AmiSession.Should().Be(1, "the hangup was delivered in the session the call started in");
            ended.Epoch?.DroppedBefore.Should().NotBe(started.Epoch?.DroppedBefore ?? -1,
                "the connection dropped an event between the call's start and its hangup");
            SessionEventEpoch.LossBetween(started.Epoch, ended.Epoch).Should().Be(SessionEventLoss.Possible,
                "the dropped event may have been one of the call's");
        }
    }

    [Fact]
    public async Task AdmittedCallStarted_ShouldDifferFromALaterEvent_WhenADropIsAheadOfTheTable()
    {
        // The buffer holds an event the engine has not applied, and the reader has then dropped one: an admission run now
        // reads a table that predates the drop, so its opening must not count it.
        var peer = new BootingAsterisk { BootedAtLogin = true };
        await using var run = await Run.ConnectAsync(peer, eventPumpCapacity: 1);
        await run.StartServerAsync();
        await using var manager = EpochRig.NewManager(new FakeTimeProvider());
        var recorder = EpochRecorder.On(manager);
        await DeliverAsync(run, peer, "Newchannel", AsteriskFrames.Newchannel(Caller, CallerChannel, Caller), Caller);
        manager.AttachToServer(run.Server, ServerId);

        using var gate = new DispatchGate();
        using var held = run.Connection.Subscribe(gate);
        using var buffered = new CallEventLog(BufferedId);
        using var bufferedSeen = run.Connection.Subscribe(buffered);
        try
        {
            await peer.SendEventAsync("UserEvent", DispatchGate.Frame());
            await gate.Held.WaitAsync(Run.Bound);
            await peer.SendEventAsync("UserEvent",
                [new("Privilege", "user,all"), new("UserEvent", "buffered"), new("Uniqueid", BufferedId)]);
            await peer.SendEventAsync("UserEvent", [new("Privilege", "user,all"), new("UserEvent", "dropped")]);
            // Answered after both, so the connection has read them: one buffered, one refused.
            await run.Connection.SendActionAsync(new PingAction()).AsTask().WaitAsync(Run.Bound);
            var readerSide = run.Server.ReadCurrentEventLossEpoch();

            await manager.AdmitExistingCalls(ServerId).AsTask().WaitAsync(Run.Bound);
            var started = await recorder.WaitAsync<CallStartedEvent>();
            gate.Release();
            // The pump holds one event: the hold is sent only once the buffered one has been delivered, or the full
            // buffer refuses it too.
            await buffered.First.WaitAsync(Run.Bound);
            await peer.SendEventAsync("Hold", Frames.Hold());
            var later = await recorder.WaitAsync<CallHeldEvent>();

            using (new AssertionScope())
            {
                readerSide.DroppedBefore.Should().Be(1, "premise: the reader had dropped one event when the admission ran");
                started.Epoch.Should().NotBeNull("the call was admitted over the SDK's connection");
                started.Epoch?.DroppedBefore.Should().Be(0,
                    "the admission read a table no event received after the drop had reached, so its opening does not count it");
                later.Epoch?.DroppedBefore.Should().Be(1, "premise: the hold was received after the drop");
                SessionEventEpoch.LossBetween(started.Epoch, later.Epoch).Should().Be(SessionEventLoss.Possible,
                    "the dropped event lies between the table the admission read and the hold");
            }
        }
        finally
        {
            gate.Release();
        }
    }

    [Fact]
    public async Task ReloadEnding_ShouldCarryTheNewSession_WhenNoEventOfTheCallIsDeliveredInTheNewSession()
    {
        var first = new BootingAsterisk { BootedAtLogin = true };
        // The call hung up during the outage: the next session's Status no longer lists it.
        var second = new BootingAsterisk { BootedAtLogin = true };
        await using var run = await Run.ConnectAsync(first, second, autoReconnect: true);
        await run.StartServerAsync();
        await using var manager = EpochRig.NewManager(new FakeTimeProvider());
        var recorder = EpochRecorder.On(manager);
        manager.AttachToServer(run.Server, ServerId);
        await first.SendEventAsync("Newchannel", AsteriskFrames.Newchannel(Caller, CallerChannel, Caller));
        var started = await recorder.WaitAsync<CallStartedEvent>();

        using var callEvents = new CallEventLog(Caller);
        using var watched = run.Connection.Subscribe(callEvents);
        first.CloseSession();
        run.ReleaseSecondPeer();
        var ended = await recorder.WaitAsync<CallEndedEvent>();
        await run.ServerLog.Logged(StateLoaded, 2).WaitAsync(Run.Bound);

        using (new AssertionScope())
        {
            callEvents.Count.Should().Be(0, "premise: no event of the call was delivered after the first session ended");
            run.Server.ChannelTableComplete.Should().BeTrue(
                "premise: the reload's Status barrier was reached, so its reconciliation removed the call's channel");
            started.Epoch?.AmiSession.Should().Be(1, "premise: the call started in the first AMI session");
            ended.Epoch.Should().NotBeNull("the reload ended the call over the SDK's connection");
            ended.Epoch?.AmiSession.Should().Be(2,
                "the reload's ending follows its barrier, which the new session's list completion reached, so it carries " +
                "the new session");
            SessionEventEpoch.LossBetween(started.Epoch, ended.Epoch).Should().Be(SessionEventLoss.Possible,
                "an outage lies between the call's start and its ending");
        }
    }

    [Fact]
    public async Task WrapUpClosedByTimer_ShouldCarryTheLastDeliveredMark_WhenTheConnectionIsDown()
    {
        var first = new BootingAsterisk { BootedAtLogin = true };
        var second = new BootingAsterisk { BootedAtLogin = true };
        await using var run = await Run.ConnectAsync(first, second, autoReconnect: true);
        await run.StartServerAsync();
        var wrapUpClock = new FakeTimeProvider();
        await using var manager = EpochRig.NewManager(wrapUpClock);
        var recorder = EpochRecorder.On(manager);
        manager.AttachToServer(run.Server, ServerId);
        await first.SendEventAsync("Newchannel", AsteriskFrames.Newchannel(Caller, CallerChannel, Caller));
        await first.SendEventAsync("AgentConnect", Frames.AgentConnect(Caller, CallerChannel));
        await recorder.WaitAsync<CallConnectedEvent>();
        await first.SendEventAsync("Hangup", AsteriskFrames.Hangup(Caller, CallerChannel, Caller));
        var ended = await recorder.WaitAsync<CallEndedEvent>();

        // The connection is lost and its reconnect is held: no event is delivered after the call's hangup.
        first.CloseSession();
        await run.ConnectionLog.Logged(Reconnecting).WaitAsync(Run.Bound);
        // The wrap-up opens right after the ending is raised; its timer is the only one the engine creates on its clock.
        await wrapUpClock.TimersCreated.ReadAsync().AsTask().WaitAsync(Run.Bound);
        wrapUpClock.Advance(new SessionOptions().WrapUpDuration);
        var wrapUp = await recorder.WaitAsync<CallWrapUpEvent>();

        using (new AssertionScope())
        {
            ended.Epoch.Should().NotBeNull("premise: the call ended over the SDK's connection");
            wrapUp.Epoch.Should().NotBeNull("the wrap-up's server is still attached over the SDK's connection");
            wrapUp.Epoch.Should().Be(ended.Epoch,
                "no event was delivered after the hangup that opened the wrap-up: its closing carries the last delivered " +
                "mark, not one of a session that has not begun");
            SessionEventEpoch.LossBetween(ended.Epoch, wrapUp.Epoch).Should().Be(SessionEventLoss.NoneKnown,
                "nothing was delivered, and so nothing could be known lost, between the hangup and the wrap-up's close");
        }
    }

    [Fact]
    public async Task AdmittedCallStarted_ShouldDifferFromItsFirstEventAfterTheReconnect_WhenAdmittedDuringTheOutage()
    {
        var first = new BootingAsterisk { BootedAtLogin = true };
        // The call is still up in the next session: its reload keeps it.
        var second = new BootingAsterisk { BootedAtLogin = true, StatusChannels = [new StatusChannel(Caller, CallerChannel, Caller)] };
        await using var run = await Run.ConnectAsync(first, second, autoReconnect: true);
        await run.StartServerAsync();
        await using var manager = EpochRig.NewManager(new FakeTimeProvider());
        var recorder = EpochRecorder.On(manager);
        await DeliverAsync(run, first, "Newchannel", AsteriskFrames.Newchannel(Caller, CallerChannel, Caller), Caller);
        manager.AttachToServer(run.Server, ServerId);

        first.CloseSession();
        await run.ConnectionLog.Logged(Reconnecting).WaitAsync(Run.Bound);
        await manager.AdmitExistingCalls(ServerId).AsTask().WaitAsync(Run.Bound);
        var started = await recorder.WaitAsync<CallStartedEvent>();
        var admittedWhileDown = run.Connection.State != Verbara.Sdk.Enums.AmiConnectionState.Connected;

        run.ReleaseSecondPeer();
        await run.ServerLog.Logged(StateLoaded, 2).WaitAsync(Run.Bound);
        await second.SendEventAsync("Hangup", AsteriskFrames.Hangup(Caller, CallerChannel, Caller));
        var ended = await recorder.WaitAsync<CallEndedEvent>();

        using (new AssertionScope())
        {
            admittedWhileDown.Should().BeTrue("premise: the call was admitted while the connection was lost");
            started.Epoch.Should().NotBeNull("the call was admitted over the SDK's connection");
            started.Epoch?.AmiSession.Should().Be(1,
                "the admission read a table of the first session; it is not stamped with a session that has not begun");
            ended.Epoch?.AmiSession.Should().Be(2, "premise: the hangup was delivered in the second session");
            ended.Epoch.Should().NotBe(started.Epoch, "the outage lies between the table the admission read and the hangup");
            SessionEventEpoch.LossBetween(started.Epoch, ended.Epoch).Should().Be(SessionEventLoss.Possible,
                "the SDK may have missed an event of the call while the connection was down");
        }
    }

    [Fact]
    public async Task CallEnded_ShouldDifferInConnection_WhenTheServerIsReattachedToANewInstance()
    {
        var firstPeer = new BootingAsterisk { BootedAtLogin = true };
        await using var firstRun = await Run.StartAsync(firstPeer);
        await using var manager = EpochRig.NewManager(new FakeTimeProvider());
        var recorder = EpochRecorder.On(manager);
        manager.AttachToServer(firstRun.Server, ServerId);
        await firstPeer.SendEventAsync("Newchannel", AsteriskFrames.Newchannel(Caller, CallerChannel, Caller));
        var started = await recorder.WaitAsync<CallStartedEvent>();

        manager.DetachFromServer(ServerId);
        var secondPeer = new BootingAsterisk
        {
            BootedAtLogin = true,
            StatusChannels = [new StatusChannel(Caller, CallerChannel, Caller)],
        };
        await using var secondRun = await Run.StartAsync(secondPeer);
        manager.AttachToServer(secondRun.Server, ServerId);
        await secondPeer.SendEventAsync("Hangup", AsteriskFrames.Hangup(Caller, CallerChannel, Caller));
        var ended = await recorder.WaitAsync<CallEndedEvent>();

        using (new AssertionScope())
        {
            started.Epoch.Should().NotBeNull("premise: the call started over the first connection");
            ended.Epoch.Should().NotBeNull("the call ended over the second connection");
            (started.Epoch?.AmiSession, started.Epoch?.DroppedBefore).Should().Be((1L, 0L),
                "premise: the call started in the first instance's first session with no drop");
            (ended.Epoch?.AmiSession, ended.Epoch?.DroppedBefore).Should().Be((1L, 0L),
                "premise: the call ended in the second instance's first session with no drop");
            ended.Epoch?.ConnectionId.Should().NotBe(started.Epoch?.ConnectionId ?? Guid.Empty,
                "a new connection instance carries a new identity, so its first session never equals the old one's");
            SessionEventEpoch.LossBetween(started.Epoch, ended.Epoch).Should().Be(SessionEventLoss.Possible,
                "the server moved to another connection between the call's start and its end");
        }
    }

    [Fact]
    public async Task Reconnects_ShouldBeRecordedAtTheFirstNewSessionNotification_WhenNoHandlerBlocks()
    {
        // Negative control of the reconnect-order tests, informational: nothing blocks the connection's notification
        // queue, so whether Live has counted the reconnect when the new session's first notification is raised is a race.
        // The count is recorded for the repeat-under-load record and not asserted; the stamp is.
        var first = new BootingAsterisk { BootedAtLogin = true };
        var second = new BootingAsterisk { BootedAtLogin = true, StatusChannels = [new StatusChannel(Caller, CallerChannel, Caller)] };
        await using var run = await Run.ConnectAsync(first, second, autoReconnect: true);
        await run.StartServerAsync();
        await using var manager = EpochRig.NewManager(new FakeTimeProvider());
        // Subscribed before the recorder, so the count is read before the test hears of the ending.
        var reconnects = new ReconnectsAtEnding(run);
        manager.Events.Subscribe(reconnects);
        var recorder = EpochRecorder.On(manager);
        manager.AttachToServer(run.Server, ServerId);
        await first.SendEventAsync("Newchannel", AsteriskFrames.Newchannel(Caller, CallerChannel, Caller));
        var started = await recorder.WaitAsync<CallStartedEvent>();

        first.CloseSession();
        run.ReleaseSecondPeer();
        await run.ConnectionLog.Logged(Connected, 2).WaitAsync(Run.Bound);
        await second.SendEventAsync("Hangup", AsteriskFrames.Hangup(Caller, CallerChannel, Caller));
        var ended = await recorder.WaitAsync<CallEndedEvent>();
        output.WriteLine(FormattableString.Invariant(
            $"reconnects-counted-at-the-first-new-session-notification={reconnects.Counted}"));

        using (new AssertionScope())
        {
            started.Epoch?.AmiSession.Should().Be(1, "premise: the call started in the first AMI session");
            ended.Epoch.Should().NotBeNull("the call ended over the SDK's connection");
            ended.Epoch?.AmiSession.Should().Be(2, "the new session's notification carries the new session");
        }
    }

    [Fact]
    public async Task AdmittedCallStarted_ShouldDifferFromALaterEvent_WhenADropLandsBetweenItsTableReadAndItsPublish()
    {
        var peer = new BootingAsterisk { BootedAtLogin = true };
        await using var run = await Run.ConnectAsync(peer, eventPumpCapacity: 1);
        await run.StartServerAsync();
        await using var manager = EpochRig.NewManager(new FakeTimeProvider());
        var recorder = EpochRecorder.On(manager);
        await DeliverAsync(run, peer, "Newchannel", AsteriskFrames.Newchannel(Caller, CallerChannel, Caller), Caller);
        manager.AttachToServer(run.Server, ServerId);

        using var park = new NotificationGate();
        manager.StepHook = (step, _, id) =>
        {
            if (step == HandlerStep.AdmissionCandidateHeld && id == Caller)
                park.Hold();
        };
        var admission = manager.AdmitExistingCalls(ServerId).AsTask();
        try
        {
            await park.Entered.Task.WaitAsync(Run.Bound);
            await EpochRig.DropOneEventAsync(run, peer);
            // Another channel's event, received after the drop; the parked call's own channel is locked by the visit.
            await peer.SendEventAsync("Newchannel", AsteriskFrames.Newchannel(Other, OtherChannel, Other));
            var other = await recorder.WaitAsync<CallStartedEvent>();
            park.Release();
            await admission.WaitAsync(Run.Bound);
            manager.StepHook = null;
            var started = await recorder.WaitAsync<CallStartedEvent>(e => e.SessionId != other.SessionId);
            await peer.SendEventAsync("Hold", Frames.Hold());
            var later = await recorder.WaitAsync<CallHeldEvent>();

            using (new AssertionScope())
            {
                other.Epoch?.DroppedBefore.Should().Be(1,
                    "premise: the other channel's event, whose delivery started before the admission announced, follows the drop");
                started.Epoch.Should().NotBeNull("the call was admitted over the SDK's connection");
                started.Epoch?.DroppedBefore.Should().Be(0,
                    "the opening carries the mark sampled before its table read, which predates the drop");
                later.Epoch?.DroppedBefore.Should().Be(1, "premise: the hold was received after the drop");
                SessionEventEpoch.LossBetween(started.Epoch, later.Epoch).Should().Be(SessionEventLoss.Possible,
                    "the drop landed between the admission's table read and its announcement");
            }
        }
        finally
        {
            manager.StepHook = null;
            park.Release();
            await admission.WaitAsync(Run.Bound);
        }
    }

    [Fact]
    public async Task ReloadAdmittedCallStarted_ShouldDifferFromALaterEvent_WhenADropIsReceivedDuringTheList()
    {
        // The pump holds 8 events: the drop is made inside the Status list, after its row and before its completion, with
        // the peer busy writing the list, so it cannot answer a Ping (EpochRig.DropOneEventWhilePeerIsBusyAsync).
        const int capacity = 8;
        var peer = new BootingAsterisk { BootedAtLogin = true };
        await using var run = await Run.ConnectAsync(peer, eventPumpCapacity: capacity);
        await run.StartServerAsync();
        await using var manager = EpochRig.NewManager(new FakeTimeProvider());
        var recorder = EpochRecorder.On(manager);
        manager.AttachToServer(run.Server, ServerId);

        peer.StatusChannels = [new StatusChannel(Caller, CallerChannel, Caller)];
        peer.BeforeStatusComplete = () => EpochRig.DropOneEventWhilePeerIsBusyAsync(run, peer, capacity);
        await run.Server.ReconcileChannelsAsync().AsTask().WaitAsync(Run.Bound);
        peer.BeforeStatusComplete = null;
        var started = await recorder.WaitAsync<CallStartedEvent>();
        var startedSide = run.Server.ReadSessionLossMark();
        await peer.SendEventAsync("Hold", Frames.Hold());
        var later = await recorder.WaitAsync<CallHeldEvent>();

        using (new AssertionScope())
        {
            startedSide?.DroppedBefore.Should().Be(1, "premise: the list's completion was delivered after the drop");
            started.Epoch.Should().NotBeNull("the call was admitted from the snapshot over the SDK's connection");
            started.Epoch?.DroppedBefore.Should().Be(0,
                "the snapshot's admission carries the mark sampled before Status was sent, not the completion's");
            later.Epoch?.DroppedBefore.Should().Be(1, "premise: the hold was received after the drop");
            SessionEventEpoch.LossBetween(started.Epoch, later.Epoch).Should().Be(SessionEventLoss.Possible,
                "the drop was received after the row the call was admitted from");
        }
    }

    [Fact]
    public async Task CallReleased_ShouldCarryTheStartedMark_WhenAnObserverOfTheLastEventIsStillRunning()
    {
        var peer = new BootingAsterisk { BootedAtLogin = true };
        await using var run = await Run.ConnectAsync(peer, eventPumpCapacity: 1);
        await run.StartServerAsync();
        await using var manager = EpochRig.NewManager(new FakeTimeProvider());
        var recorder = EpochRecorder.On(manager);
        manager.AttachToServer(run.Server, ServerId);
        await peer.SendEventAsync("Newchannel", AsteriskFrames.Newchannel(Caller, CallerChannel, Caller));
        var started = await recorder.WaitAsync<CallStartedEvent>();
        await EpochRig.DropOneEventAsync(run, peer);

        // Registered after the server's observer: it blocks once Live has applied the other channel's arrival.
        using var gate = new ArrivalGate(Other);
        using var held = run.Connection.Subscribe(gate);
        try
        {
            await peer.SendEventAsync("Newchannel", AsteriskFrames.Newchannel(Other, OtherChannel, Other));
            await gate.Entered.WaitAsync(Run.Bound);
            var startedSide = run.Server.ReadSessionLossMark();
            var appliedSide = run.Server.ReadAppliedSessionLossMark();

            manager.DetachFromServer(ServerId, DetachedCalls.Release);
            var released = await recorder.WaitAsync<CallReleasedEvent>(e => e.SessionId == started.SessionId);

            using (new AssertionScope())
            {
                startedSide?.DroppedBefore.Should().Be(1, "premise: the other channel's event, after the drop, has started");
                appliedSide?.DroppedBefore.Should().Be(0, "premise: an observer of that event is still running");
                started.Epoch?.DroppedBefore.Should().Be(0, "premise: the call started before the drop");
                released.Epoch.Should().NotBeNull("the release closed an attachment over the SDK's connection");
                released.Epoch?.DroppedBefore.Should().Be(1,
                    "a closing raised outside a delivery carries the started side, no earlier than its content");
                SessionEventEpoch.LossBetween(started.Epoch, released.Epoch).Should().Be(SessionEventLoss.Possible,
                    "an event was dropped between the call's start and its release");
            }
        }
        finally
        {
            gate.Release();
        }
    }

    [Fact]
    public async Task CallWrapUp_ShouldCarryItsOwnServersMark_WhenClosedByTheAgentsConnectOnAnotherServer()
    {
        var peerA = new BootingAsterisk { BootedAtLogin = true };
        var peerB = new BootingAsterisk { BootedAtLogin = true };
        await using var runA = await Run.ConnectAsync(peerA, eventPumpCapacity: 1);
        await runA.StartServerAsync();
        await using var runB = await Run.StartAsync(peerB);
        var wrapUpClock = new FakeTimeProvider();
        await using var manager = EpochRig.NewManager(wrapUpClock);
        var recorder = EpochRecorder.On(manager);
        manager.AttachToServer(runA.Server, "srv-a");
        manager.AttachToServer(runB.Server, "srv-b");

        // The known agent's call on B ends, and its wrap-up opens; no event of B is delivered after it.
        await peerB.SendEventAsync("Newchannel", AsteriskFrames.Newchannel(Caller, CallerChannel, Caller));
        await peerB.SendEventAsync("AgentConnect", Frames.AgentConnect(Caller, CallerChannel));
        await recorder.WaitAsync<CallConnectedEvent>(e => e.ServerId == "srv-b");
        await peerB.SendEventAsync("Hangup", AsteriskFrames.Hangup(Caller, CallerChannel, Caller));
        var endedOnB = await recorder.WaitAsync<CallEndedEvent>(e => e.ServerId == "srv-b");
        // The wrap-up opens right after the ending is raised; its timer is the only one the engine creates on its clock.
        await wrapUpClock.TimersCreated.ReadAsync().AsTask().WaitAsync(Run.Bound);

        // A's connection drops one event; then the agent connects on A, which closes the wrap-up inside A's delivery.
        await EpochRig.DropOneEventAsync(runA, peerA);
        await peerA.SendEventAsync("Newchannel", AsteriskFrames.Newchannel(Other, OtherChannel, Other));
        // A's pump holds one event: the connect is sent once the arrival has been delivered, so the buffer has room.
        await recorder.WaitAsync<CallStartedEvent>(e => e.ServerId == "srv-a");
        await peerA.SendEventAsync("AgentConnect", Frames.AgentConnect(Other, OtherChannel));
        var wrapUp = await recorder.WaitAsync<CallWrapUpEvent>();
        var connectedOnA = await recorder.WaitAsync<CallConnectedEvent>(e => e.ServerId == "srv-a");

        using (new AssertionScope())
        {
            wrapUp.ServerId.Should().Be("srv-b", "premise: the wrap-up is of B's call");
            connectedOnA.Epoch?.ConnectionId.Should().Be(runA.Server.ConnectionIdentity!.Value,
                "premise: the connect that closed the wrap-up was delivered by A's connection");
            connectedOnA.Epoch?.DroppedBefore.Should().Be(1, "premise: A's connection dropped one event before the connect");
            endedOnB.Epoch.Should().NotBeNull("premise: B's call ended over the SDK's connection");
            wrapUp.Epoch?.ConnectionId.Should().Be(runB.Server.ConnectionIdentity!.Value,
                "the wrap-up carries its own server's connection, not that of the delivery that closed it");
            wrapUp.Epoch.Should().Be(endedOnB.Epoch, "no event of B was delivered after the hangup that opened the wrap-up");
            SessionEventEpoch.LossBetween(endedOnB.Epoch, wrapUp.Epoch).Should().Be(SessionEventLoss.NoneKnown,
                "A's drop is not B's");
        }
    }

    /// <summary>Sends one channel event and returns once every observer of the connection has seen it.</summary>
    private static async Task DeliverAsync(Run run, BootingAsterisk peer, string eventType,
        IEnumerable<KeyValuePair<string, string>> fields, string uniqueId)
    {
        using var seen = new CallEventLog(uniqueId);
        using var subscription = run.Connection.Subscribe(seen);
        await peer.SendEventAsync(eventType, fields);
        await seen.First.WaitAsync(Run.Bound);
    }

    /// <summary>
    /// Counts the AMI events of one channel the connection delivers to it; <see cref="First"/> completes at the first.
    /// Subscribed after the server's observer, it sees an event once Live has applied it.
    /// </summary>
    private sealed class CallEventLog(string uniqueId) : IObserver<ManagerEvent>, IDisposable
    {
        private readonly TaskCompletionSource _first = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private int _count;

        public Task First => _first.Task;

        public int Count => Volatile.Read(ref _count);

        public void OnNext(ManagerEvent value)
        {
            if (value.RawFields?.GetValueOrDefault("Uniqueid") != uniqueId)
                return;

            Interlocked.Increment(ref _count);
            _first.TrySetResult();
        }

        public void OnError(Exception error)
        {
            // The connection never faults its observers in these tests; nothing to do.
        }

        public void OnCompleted()
        {
            // Completed when the connection is disposed; nothing to do.
        }

        public void Dispose()
        {
            // Nothing held; the subscription is disposed by the caller.
        }
    }

    /// <summary>The reconnect count Live has recorded when the first call ending is raised.</summary>
    private sealed class ReconnectsAtEnding(Run run) : IObserver<SessionDomainEvent>
    {
        private long _counted = -1;

        public long Counted => Interlocked.Read(ref _counted);

        public void OnNext(SessionDomainEvent value)
        {
            if (value is CallEndedEvent)
                Interlocked.CompareExchange(ref _counted, run.Server.ReadEventLossEpoch().Reconnects, -1);
        }

        public void OnError(Exception error)
        {
            // The engine never faults its subscribers in these tests; nothing to do.
        }

        public void OnCompleted()
        {
            // Completed when the engine is disposed; nothing to do.
        }
    }

    /// <summary>An observer that blocks the delivery of one channel's <c>Newchannel</c> until the test releases it.</summary>
    private sealed class ArrivalGate(string uniqueId) : IObserver<ManagerEvent>, IDisposable
    {
        private readonly TaskCompletionSource _entered = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly ManualResetEventSlim _gate = new(initialState: false);

        /// <summary>Completes once the delivery is blocked inside this observer.</summary>
        public Task Entered => _entered.Task;

        public void Release() => _gate.Set();

        public void OnNext(ManagerEvent value)
        {
            if (value is not NewChannelEvent arrival || arrival.UniqueId != uniqueId)
                return;

            _entered.TrySetResult();
            _gate.Wait(DispatchGate.HoldBound);
        }

        public void OnError(Exception error)
        {
            // The connection never faults its observers in these tests; nothing to do.
        }

        public void OnCompleted()
        {
            // Completed when the connection is disposed; nothing to do.
        }

        public void Dispose()
        {
            _gate.Set();
            _gate.Dispose();
        }
    }

    private static class Frames
    {
        /// <summary>
        /// app_queue's <c>AgentConnect</c> for the peer's known agent (<see cref="BootingAsterisk.AgentId"/>) answering a
        /// call the engine saw join no queue: the agent's leg opens its wrap-up when the call ends.
        /// </summary>
        public static KeyValuePair<string, string>[] AgentConnect(string uniqueId, string channel) =>
        [
            new("Privilege", "agent,all"), new("Channel", channel), new("Uniqueid", uniqueId), new("Linkedid", uniqueId),
            new("DestChannel", "PJSIP/agent-1001-" + uniqueId), new("DestUniqueid", uniqueId + "9"),
            new("DestLinkedid", uniqueId), new("Queue", BootingAsterisk.QueueName), new("Interface", "PJSIP/agent-1001"),
            new("MemberName", "Agent " + BootingAsterisk.AgentId), new("Agent", BootingAsterisk.AgentId),
            new("HoldTime", "0"), new("RingTime", "1"),
        ];

        /// <summary><c>Hold</c> of the caller's channel.</summary>
        public static KeyValuePair<string, string>[] Hold() =>
        [
            new("Privilege", "call,all"), new("Channel", CallerChannel), new("ChannelState", "6"),
            new("ChannelStateDesc", "Up"), new("Uniqueid", Caller), new("Linkedid", Caller), new("MusicClass", "default"),
        ];
    }
}

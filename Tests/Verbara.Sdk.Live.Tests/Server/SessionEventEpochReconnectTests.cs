using FluentAssertions;
using FluentAssertions.Execution;
using Verbara.Sdk.Ami.Actions;
using Verbara.Sdk.Ami.Events;
using Verbara.Sdk.Enums;
using Verbara.Sdk.Live.Tests.Harness;
using Verbara.Sdk.Sessions;

namespace Verbara.Sdk.Live.Tests.Server;

/// <summary>
/// The loss mark a session notification carries across a reconnect (<see cref="SessionDomainEvent.Epoch"/>): the first
/// notification raised from an event of the new AMI session carries the new session, after an automatic reconnect and
/// after the caller's own connect, while a handler of the connection's state changes still holds the connection's
/// notification queue (so Live has not yet counted the reconnect); and the lost session's buffered tail, delivered after
/// the next session has logged in, keeps the lost session.
/// </summary>
/// <remarks>
/// <para>
/// A real <see cref="Verbara.Sdk.Ami.Connection.AmiConnection"/> over <see cref="Run"/>: every frame goes over the wire
/// and through the connection's pump, so a notification raised by a handler is raised inside its event's delivery.
/// </para>
/// <para>
/// Gate-sequenced, never timed: a blocked handler sets its entered signal before it blocks and the test waits on it; the
/// new session is known to have logged in by the connection's <c>[AMI] Connected</c> line; an event is known to be read
/// into the session's buffer by a <c>Ping</c> answered after it; every gate is released only after the reads. Every wait
/// is bounded by <see cref="Run.Bound"/> and ends on its signal.
/// </para>
/// </remarks>
public sealed class SessionEventEpochReconnectTests
{
    private const string ServerId = "srv-epoch";
    private const string Connected = "[AMI] Connected";
    private const string Caller = "1700000400.1";
    private const string CallerChannel = "PJSIP/pstn-00000401";
    private const string Other = "1700000400.2";
    private const string OtherChannel = "PJSIP/pstn-00000402";
    private const string Member = "PJSIP/agent-2001";
    private const string MemberLeg = "1700000400.3";
    private const string MemberChannel = "PJSIP/agent-2001-00000403";

    [Fact]
    public async Task CallEnded_ShouldCarryTheNewSession_WhenTheStateHandlerBlocksAcrossAReconnect()
    {
        var first = new BootingAsterisk { BootedAtLogin = true };
        var second = new BootingAsterisk { BootedAtLogin = true };
        await using var run = await Run.ConnectAsync(first, second, autoReconnect: true);
        await run.StartServerAsync();
        await using var manager = EpochRig.NewManager(new FakeTimeProvider());
        var recorder = EpochRecorder.On(manager);
        manager.AttachToServer(run.Server, ServerId);
        await first.SendEventAsync("Newchannel", AsteriskFrames.Newchannel(Caller, CallerChannel, Caller));
        var started = await recorder.WaitAsync<CallStartedEvent>();

        using var hold = new StateHandlerHold();
        run.Connection.StateChanged += hold.OnStateChanged;
        try
        {
            first.CloseSession();
            await hold.Entered.WaitAsync(Run.Bound);
            run.ReleaseSecondPeer();
            await run.ConnectionLog.Logged(Connected, 2).WaitAsync(Run.Bound);
            await second.SendEventAsync("Hangup", AsteriskFrames.Hangup(Caller, CallerChannel, Caller));
            var ended = await recorder.WaitAsync<CallEndedEvent>();
            var reconnectsWhileHeld = run.Server.ReadEventLossEpoch().Reconnects;
            var stillHeld = !hold.Released;

            using (new AssertionScope())
            {
                stillHeld.Should().BeTrue("premise: the state handler still blocks the connection's notification queue");
                reconnectsWhileHeld.Should().Be(0,
                    "premise: Live counts the reconnect from the Reconnected notification, which waits behind the blocked handler");
                started.Epoch?.AmiSession.Should().Be(1, "premise: the call started in the first AMI session");
                ended.Epoch.Should().NotBeNull("the call ended over the SDK's connection");
                ended.Epoch?.AmiSession.Should().Be(2,
                    "the notification raised from the new session's first event carries the new session, whatever the " +
                    "connection's state handlers do");
                ended.Epoch.Should().NotBe(started.Epoch, "a reconnect lies between the call's start and its end");
                SessionEventEpoch.LossBetween(started.Epoch, ended.Epoch).Should().Be(SessionEventLoss.Possible,
                    "the SDK may have missed an event of the call while the connection was down");
            }
        }
        finally
        {
            hold.Release();
        }
    }

    [Fact]
    public async Task Events_ShouldCarryTheNewSession_WhenTheCallerReconnects()
    {
        var first = new BootingAsterisk { BootedAtLogin = true };
        var second = new BootingAsterisk { BootedAtLogin = true };
        await using var run = await Run.ConnectAsync(first, second, autoReconnect: false);
        await run.StartServerAsync();
        await using var manager = EpochRig.NewManager(new FakeTimeProvider());
        var recorder = EpochRecorder.On(manager);
        manager.AttachToServer(run.Server, ServerId);
        await first.SendEventAsync("Newchannel", AsteriskFrames.Newchannel(Caller, CallerChannel, Caller));
        var started = await recorder.WaitAsync<CallStartedEvent>();

        using var hold = new StateHandlerHold();
        run.Connection.StateChanged += hold.OnStateChanged;
        try
        {
            first.CloseSession();
            await hold.Entered.WaitAsync(Run.Bound);
            run.ReleaseSecondPeer();
            await run.Connection.ConnectAsync().AsTask().WaitAsync(Run.Bound);
            await second.SendEventAsync("Hangup", AsteriskFrames.Hangup(Caller, CallerChannel, Caller));
            var ended = await recorder.WaitAsync<CallEndedEvent>();
            var reconnectsWhileHeld = run.Server.ReadEventLossEpoch().Reconnects;
            var stillHeld = !hold.Released;

            using (new AssertionScope())
            {
                hold.Seen.Should().NotBe(AmiConnectionState.Reconnecting,
                    "premise: with automatic reconnection off the connection does not reconnect by itself");
                stillHeld.Should().BeTrue("premise: the state handler still blocks the connection's notification queue");
                reconnectsWhileHeld.Should().Be(0,
                    "premise: Live counts the caller's reconnect from a notification that waits behind the blocked handler");
                started.Epoch?.AmiSession.Should().Be(1, "premise: the call started in the first AMI session");
                ended.Epoch.Should().NotBeNull("the call ended over the SDK's connection");
                ended.Epoch?.AmiSession.Should().Be(2,
                    "the caller's own connect starts a new AMI session, and its events carry it");
                SessionEventEpoch.LossBetween(started.Epoch, ended.Epoch).Should().Be(SessionEventLoss.Possible,
                    "the SDK may have missed an event of the call while the connection was down");
            }
        }
        finally
        {
            hold.Release();
        }
    }

    [Fact]
    public async Task BufferedTailNotifications_ShouldKeepTheLostSession_WhenDeliveredAfterTheNextSessionLoggedIn()
    {
        var first = new BootingAsterisk { BootedAtLogin = true };
        // The next session's reload still finds the call, so the reload keeps it.
        var second = new BootingAsterisk
        {
            BootedAtLogin = true,
            StatusChannels = [new StatusChannel(Caller, CallerChannel, Caller)],
        };
        await using var run = await Run.ConnectAsync(first, second, autoReconnect: true);
        await run.StartServerAsync();
        await using var manager = EpochRig.NewManager(new FakeTimeProvider());
        var recorder = EpochRecorder.On(manager);
        manager.AttachToServer(run.Server, ServerId);
        await first.SendEventAsync("Newchannel", AsteriskFrames.Newchannel(Caller, CallerChannel, Caller));
        var started = await recorder.WaitAsync<CallStartedEvent>();
        await first.SendEventAsync("QueueCallerJoin",
            AsteriskFrames.QueueCallerJoin(Caller, CallerChannel, BootingAsterisk.QueueName, 1, 1));
        await first.SendEventAsync("AgentConnect", Frames.AgentConnect());
        await recorder.WaitAsync<CallConnectedEvent>();

        // Registered after the server's observer: it blocks after Live has applied the other channel's arrival.
        using var gate = new ArrivalGate(Other);
        using var held = run.Connection.Subscribe(gate);
        try
        {
            await first.SendEventAsync("Newchannel", AsteriskFrames.Newchannel(Other, OtherChannel, Other));
            await gate.Entered.WaitAsync(Run.Bound);
            await first.SendEventAsync("Hold", Frames.Hold("Hold"));
            // Answered after the Hold, so the connection has read it into the first session's buffer.
            await run.Connection.SendActionAsync(new PingAction()).AsTask().WaitAsync(Run.Bound);

            first.CloseSession();
            run.ReleaseSecondPeer();
            await run.ConnectionLog.Logged(Connected, 2).WaitAsync(Run.Bound);
            await second.SendEventAsync("Unhold", Frames.Hold("Unhold"));
            // Answered after the Unhold, so the connection has read it into the second session's buffer.
            await run.Connection.SendActionAsync(new PingAction()).AsTask().WaitAsync(Run.Bound);
            var heldBeforeRelease = recorder.Seen.Any(s => s.Notification is CallHeldEvent);

            gate.Release();
            var heldCall = await recorder.WaitAsync<CallHeldEvent>();
            var resumed = await recorder.WaitAsync<CallResumedEvent>();

            using (new AssertionScope())
            {
                heldBeforeRelease.Should().BeFalse(
                    "premise: the Hold was still buffered in the first session when the second session logged in");
                started.Epoch.Should().NotBeNull("premise: the call started over the SDK's connection");
                started.Epoch?.AmiSession.Should().Be(1, "premise: the call started in the first AMI session");
                heldCall.Epoch?.AmiSession.Should().Be(1,
                    "the lost session's buffered tail is delivered after the next session logged in, and keeps the lost session");
                heldCall.Epoch.Should().Be(started.Epoch,
                    "nothing was dropped in the first session between the call's start and its hold");
                SessionEventEpoch.LossBetween(started.Epoch, heldCall.Epoch).Should().Be(SessionEventLoss.NoneKnown,
                    "the hold was delivered from the session the call started in, with no drop between them");
                resumed.Epoch?.AmiSession.Should().Be(2, "the second session's Unhold carries the second session");
            }
        }
        finally
        {
            gate.Release();
        }
    }

    /// <summary>
    /// A <c>StateChanged</c> handler that blocks the connection's notification queue from the first change that leaves
    /// <c>Connected</c> until the test releases it, so the <c>Reconnected</c> (or the caller's reconnect) notification
    /// queued behind it waits.
    /// </summary>
    private sealed class StateHandlerHold : IDisposable
    {
        private readonly TaskCompletionSource _entered = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly ManualResetEventSlim _gate = new(initialState: false);
        private int _taken;

        /// <summary>Completes once the handler is blocked.</summary>
        public Task Entered => _entered.Task;

        /// <summary>The state the blocked change moved to.</summary>
        public AmiConnectionState Seen { get; private set; }

        public bool Released => _gate.IsSet;

        public void OnStateChanged(AmiConnectionStateChange change)
        {
            if (change.Previous != AmiConnectionState.Connected || Interlocked.Exchange(ref _taken, 1) != 0)
                return;

            Seen = change.Current;
            _entered.TrySetResult();
            _gate.Wait(DispatchGate.HoldBound);
        }

        public void Release() => _gate.Set();

        public void Dispose()
        {
            _gate.Set();
            _gate.Dispose();
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
        public static KeyValuePair<string, string>[] AgentConnect() =>
        [
            new("Privilege", "agent,all"), new("Channel", CallerChannel), new("Uniqueid", Caller), new("Linkedid", Caller),
            new("DestChannel", MemberChannel), new("DestUniqueid", MemberLeg), new("DestLinkedid", Caller),
            new("Queue", BootingAsterisk.QueueName), new("Interface", Member), new("MemberName", Member),
            new("HoldTime", "0"), new("RingTime", "1"),
        ];

        /// <summary><c>Hold</c> or <c>Unhold</c> of the caller's channel.</summary>
        public static KeyValuePair<string, string>[] Hold(string eventType) =>
        [
            new("Privilege", "call,all"), new("Channel", CallerChannel), new("ChannelState", "6"),
            new("ChannelStateDesc", "Up"), new("Uniqueid", Caller), new("Linkedid", Caller),
            .. eventType == "Hold" ? [new KeyValuePair<string, string>("MusicClass", "default")] : Array.Empty<KeyValuePair<string, string>>(),
        ];
    }
}

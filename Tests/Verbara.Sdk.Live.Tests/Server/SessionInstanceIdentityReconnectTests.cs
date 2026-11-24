using FluentAssertions;
using FluentAssertions.Execution;
using Verbara.Sdk.Ami.Actions;
using Verbara.Sdk.Ami.Events;
using Verbara.Sdk.Live.Server;
using Verbara.Sdk.Live.Tests.Harness;
using Verbara.Sdk.Sessions;

namespace Verbara.Sdk.Live.Tests.Server;

/// <summary>
/// The instance identity across a reconnect of the same connection object, on both routes that open a new AMI session
/// (the reconnect loop and the caller's own connect): the new session's identity is read at its login, so its first
/// notification already carries it; a notification raised before the reconnect keeps the identity it was sealed with; a
/// new session whose read gives nothing (an empty <c>SystemName</c>, or <c>CoreSettings</c> refused) names no instance
/// for the whole session and never the previous session's; and the lost session's buffered tail, delivered after the
/// next session logged in, keeps the lost session's identity.
/// </summary>
/// <remarks>
/// <para>
/// A real <see cref="Verbara.Sdk.Ami.Connection.AmiConnection"/> over <see cref="Run"/>: one connection object, one
/// attach, and only what the second peer's <c>CoreSettings</c> returns differs from the first's. A fake connection has
/// no loss mark, so it has no AMI session to observe; those cases are in
/// <see cref="SessionInstanceIdentityNoMarkTests"/>.
/// </para>
/// <para>
/// Gate-sequenced, never timed: the new session is known to have logged in by the connection's <c>[AMI] Connected</c>
/// line (the loop) or by the return of the caller's connect; an event is known to be read into a session's buffer by a
/// <c>Ping</c> answered after it. Every wait is bounded by <see cref="Run.Bound"/> and ends on its signal.
/// </para>
/// </remarks>
public sealed class SessionInstanceIdentityReconnectTests
{
    private const string ServerId = "srv-identity";
    private const string Connected = "[AMI] Connected";
    private const string Caller = "1700000600.1";
    private const string CallerChannel = "PJSIP/pstn-00000601";
    private const string Next = "1700000600.5";
    private const string NextChannel = "PJSIP/pstn-00000605";
    private const string Other = "1700000600.2";
    private const string OtherChannel = "PJSIP/pstn-00000602";
    private const string Member = "PJSIP/agent-2001";
    private const string MemberLeg = "1700000600.3";
    private const string MemberChannel = "PJSIP/agent-2001-00000603";

    [Theory]
    [InlineData(ReconnectRoute.Loop)]
    [InlineData(ReconnectRoute.CallerConnect)]
    public async Task Events_ShouldCarryTheRefreshedIdentity_WhenTheSameConnectionReconnectsToAnInstanceThatNowReportsAnother(
        ReconnectRoute route)
    {
        var first = new BootingAsterisk { BootedAtLogin = true, SystemName = "pbx-one" };
        var second = new BootingAsterisk { BootedAtLogin = true, SystemName = "pbx-two" };

        IdentityReading? nextStarted = null;
        IdentityReading? nextEnded = null;
        var (started, recorder) = await AcrossAReconnectAsync(first, second, route, async (seen, startedFirst) =>
        {
            await second.SendEventAsync("Newchannel", AsteriskFrames.Newchannel(Next, NextChannel, Next));
            nextStarted = await seen.WaitAsync<CallStartedEvent>(e => e.SessionId != startedFirst.SessionId);
            await second.SendEventAsync("Hangup", AsteriskFrames.Hangup(Next, NextChannel, Next));
            nextEnded = await seen.WaitAsync<CallEndedEvent>(e => e.SessionId == nextStarted!.Notification.SessionId);
        });
        var ofTheNewSession = recorder.Seen.Where(r => r.Epoch?.AmiSession == 2).ToList();

        using (new AssertionScope())
        {
            started.Epoch?.AmiSession.Should().Be(1, "premise: the first call started in the first AMI session");
            nextStarted!.Epoch?.AmiSession.Should().Be(2, "premise: the next call started in the new AMI session");
            started.SystemName.Should().Be("pbx-one", "premise: the first session's instance reported pbx-one");
            recorder.Seen.First(r => r.Epoch?.AmiSession == 2).SystemName.Should().Be("pbx-two",
                "the new session's identity is read at its login, so its first notification already carries it");
            nextStarted!.SystemName.Should().Be("pbx-two", "the new session's instance reports pbx-two");
            nextEnded!.SystemName.Should().Be("pbx-two", "and every later notification of it carries the refreshed value");
            ofTheNewSession.Should().OnlyContain(r => r.SystemName == "pbx-two",
                "every notification whose loss mark names the new AMI session carries pbx-two, never pbx-one");
            InstanceIdentity(started.Notification).Should().Be("pbx-one",
                "the notification raised before the reconnect keeps the identity it was sealed with");
        }
    }

    [Theory]
    [InlineData(ReconnectRoute.Loop)]
    [InlineData(ReconnectRoute.CallerConnect)]
    public async Task CallStarted_ShouldCarryANullIdentity_WhenTheSameConnectionReconnectsToAnInstanceThatGivesNone(
        ReconnectRoute route)
    {
        var first = new BootingAsterisk { BootedAtLogin = true, SystemName = "pbx-one" };
        var second = new BootingAsterisk { BootedAtLogin = true, SystemName = "" };

        IdentityReading? nextStarted = null;
        var (started, recorder) = await AcrossAReconnectAsync(first, second, route, async (seen, startedFirst) =>
        {
            await second.SendEventAsync("Newchannel", AsteriskFrames.Newchannel(Next, NextChannel, Next));
            nextStarted = await seen.WaitAsync<CallStartedEvent>(e => e.SessionId != startedFirst.SessionId);
        });

        using (new AssertionScope())
        {
            started.SystemName.Should().Be("pbx-one", "premise: the first session's instance reported pbx-one");
            nextStarted!.Epoch?.AmiSession.Should().Be(2, "premise: the next call started in the new AMI session");
            nextStarted!.Identity.Should().BeNull(
                "the new session's instance gives no identity: unknown for that session, never the previous session's");
        }
    }

    /// <summary>
    /// The lookup the stamping makes, below the session engine's per-attachment reuse of its last answer: keyed by the
    /// session a loss mark names, so the lost session's name still answers after the next login read another, and a mark
    /// of another connection instance, or of a session this one never started, names none.
    /// </summary>
    [Fact]
    public async Task ReadSystemName_ShouldAnswerTheMarksSession_WhenTheConnectionHasSinceLoggedInToAnotherName()
    {
        var first = new BootingAsterisk { BootedAtLogin = true, SystemName = "pbx-one" };
        var second = new BootingAsterisk { BootedAtLogin = true, SystemName = "pbx-two" };
        await using var run = await Run.StartAsync(first, second, autoReconnect: true);
        var connection = run.Server.ConnectionIdentity;
        var beforeTheLoss = run.Server.ReadSystemName(new SessionLossMark(connection!.Value, 1, 0));

        first.CloseSession();
        run.ReleaseSecondPeer();
        await run.Reconnected.WaitAsync(Run.Bound);

        using (new AssertionScope())
        {
            beforeTheLoss.Should().Be("pbx-one", "premise: the first login read pbx-one");
            run.Server.ReadSessionLossMark()?.Session.Should().Be(2, "premise: the connection is now in its second session");
            run.Server.ReadSystemName(new SessionLossMark(connection.Value, 1, 0)).Should().Be("pbx-one",
                "a mark of the lost session answers that session's read, never the connection's current one");
            run.Server.ReadSystemName(new SessionLossMark(connection.Value, 2, 0)).Should().Be("pbx-two",
                "the new session's login read pbx-two");
            run.Server.ReadSystemName(new SessionLossMark(connection.Value, 3, 0)).Should().BeNull(
                "a session the connection never started has no read");
            run.Server.ReadSystemName(new SessionLossMark(Guid.NewGuid(), 1, 0)).Should().BeNull(
                "a mark of another connection instance names none of this one's sessions");
        }
    }

    /// <summary>Ordering test (a): the failed read.</summary>
    [Theory]
    [InlineData(ReconnectRoute.Loop)]
    [InlineData(ReconnectRoute.CallerConnect)]
    public async Task CallStarted_ShouldCarryANullIdentity_WhenTheNewSessionsCoreSettingsIsRefused(ReconnectRoute route)
    {
        var first = new BootingAsterisk { BootedAtLogin = true, SystemName = "pbx-one" };
        // The same instance, logged in by a user who is now refused CoreSettings: a name exists, the read gets none.
        var second = new BootingAsterisk { BootedAtLogin = true, SystemName = "pbx-one", RefusesCoreSettings = true };

        IdentityReading? nextStarted = null;
        var (started, recorder) = await AcrossAReconnectAsync(first, second, route, async (seen, startedFirst) =>
        {
            await second.SendEventAsync("Newchannel", AsteriskFrames.Newchannel(Next, NextChannel, Next));
            nextStarted = await seen.WaitAsync<CallStartedEvent>(e => e.SessionId != startedFirst.SessionId);
        });
        var ofTheNewSession = recorder.Seen.Where(r => r.Epoch?.AmiSession == 2).ToList();

        using (new AssertionScope())
        {
            started.SystemName.Should().Be("pbx-one", "premise: the first session's read gave pbx-one");
            second.Asked("CoreSettings").Should().Be(1, "premise: the new session's login asked CoreSettings, and was refused");
            nextStarted!.Epoch?.AmiSession.Should().Be(2, "premise: the next call started in the new AMI session");
            nextStarted!.Identity.Should().BeNull(
                "the new session's read failed: its notifications name no instance, never pbx-one");
            ofTheNewSession.Should().OnlyContain(r => r.Identity == null,
                "null holds for the whole session the failed read names, with no fallback to the last known identity");
        }
    }

    /// <summary>
    /// Ordering test (b): the buffered tail. The gated delivery of
    /// <see cref="SessionEventEpochReconnectTests.BufferedTailNotifications_ShouldKeepTheLostSession_WhenDeliveredAfterTheNextSessionLoggedIn"/>:
    /// a first-session <c>Hold</c> held in its buffer until the second session has logged in (and read its identity),
    /// then delivered before the second session's <c>Unhold</c>.
    /// </summary>
    [Fact]
    public async Task BufferedTailNotification_ShouldKeepTheLostSessionsIdentity_WhenDeliveredAfterTheNextSessionLoggedIn()
    {
        var first = new BootingAsterisk { BootedAtLogin = true, SystemName = "pbx-one" };
        // The next session's reload still finds the call, so the reload keeps it.
        var second = new BootingAsterisk
        {
            BootedAtLogin = true,
            SystemName = "pbx-two",
            StatusChannels = [new StatusChannel(Caller, CallerChannel, Caller)],
        };
        await using var run = await Run.ConnectAsync(first, second, autoReconnect: true);
        await run.StartServerAsync();
        await using var manager = EpochRig.NewManager(new FakeTimeProvider());
        var recorder = IdentityRecorder.On(manager);
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
            var heldBeforeRelease = recorder.Seen.Any(r => r.Notification is CallHeldEvent);
            var secondLoginRead = second.Asked("CoreSettings");

            gate.Release();
            var heldCall = await recorder.WaitAsync<CallHeldEvent>();
            var resumed = await recorder.WaitAsync<CallResumedEvent>();

            using (new AssertionScope())
            {
                heldBeforeRelease.Should().BeFalse(
                    "premise: the Hold was still buffered in the first session when the second session logged in");
                secondLoginRead.Should().Be(1,
                    "premise: the second session's login had read its CoreSettings before the buffered Hold was delivered");
                started.SystemName.Should().Be("pbx-one", "premise: the call started in the first session, on pbx-one");
                heldCall.Epoch?.AmiSession.Should().Be(1, "premise: the buffered Hold's loss mark names the lost session");
                heldCall.SystemName.Should().Be("pbx-one",
                    "the buffered tail carries the identity read for the session its mark names, never the connection's "
                    + "current one");
                resumed.Epoch?.AmiSession.Should().Be(2, "premise: the Unhold's loss mark names the new session");
                resumed.SystemName.Should().Be("pbx-two", "the new session's event carries the new session's identity");
            }
        }
        finally
        {
            gate.Free();
        }
    }

    /// <summary>
    /// Connects over <paramref name="first"/> on <paramref name="route"/>, attaches one engine, starts a call in the first
    /// session, loses it, opens the next session on the same connection object over <paramref name="second"/>, then runs
    /// <paramref name="inTheNewSession"/>, which sends the new session's events and waits for their notifications (it is
    /// given the recorder and the first call's start). Returns the first call's start, read at receipt, and the recorder;
    /// the run and the engine are disposed once the scenario's waits have ended, so the caller asserts on what was
    /// recorded.
    /// </summary>
    private static async Task<(IdentityReading Started, IdentityRecorder Recorder)> AcrossAReconnectAsync(
        BootingAsterisk first, BootingAsterisk second, ReconnectRoute route,
        Func<IdentityRecorder, SessionDomainEvent, Task> inTheNewSession)
    {
        var run = await Run.ConnectAsync(first, second, autoReconnect: route == ReconnectRoute.Loop);
        await using var manager = EpochRig.NewManager(new FakeTimeProvider());
        var recorder = IdentityRecorder.On(manager);
        await using (run)
        {
            await run.StartServerAsync();
            manager.AttachToServer(run.Server, ServerId);
            await first.SendEventAsync("Newchannel", AsteriskFrames.Newchannel(Caller, CallerChannel, Caller));
            var started = await recorder.WaitAsync<CallStartedEvent>();

            var lost = new SessionLostSignal();
            run.Connection.StateChanged += lost.OnStateChanged;
            first.CloseSession();
            await lost.Left.WaitAsync(Run.Bound);
            run.ReleaseSecondPeer();
            if (route == ReconnectRoute.Loop)
                await run.ConnectionLog.Logged(Connected, 2).WaitAsync(Run.Bound);
            else
                await run.Connection.ConnectAsync().AsTask().WaitAsync(Run.Bound);

            await inTheNewSession(recorder, started.Notification);
            return (started, recorder);
        }
    }

    private static string? InstanceIdentity(SessionDomainEvent notification) =>
        Verbara.Sdk.Tests.Shared.Sessions.InstanceIdentityNames.SystemNameOf(notification);

    /// <summary>An observer that blocks the delivery of one channel's <c>Newchannel</c> until the test releases it.</summary>
    private sealed class ArrivalGate(string uniqueId) : IObserver<ManagerEvent>, IDisposable
    {
        private readonly ThreadHold _hold = new();

        /// <summary>Completes once the delivery is blocked inside this observer.</summary>
        public Task Entered => _hold.Held;

        public void Release() => _hold.Release();

        public void Free() => _hold.Free();

        public void OnNext(ManagerEvent value)
        {
            if (value is not NewChannelEvent arrival || arrival.UniqueId != uniqueId)
                return;

            _hold.Hold(DispatchGate.HoldBound);
        }

        public void OnError(Exception error)
        {
            // The connection never faults its observers in these tests; nothing to do.
        }

        public void OnCompleted()
        {
            // Completed when the connection is disposed; nothing to do.
        }

        public void Dispose() => _hold.Free();
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

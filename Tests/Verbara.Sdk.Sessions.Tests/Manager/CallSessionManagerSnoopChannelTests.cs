using System.Collections.Concurrent;
using System.Diagnostics.CodeAnalysis;
using System.Diagnostics.Metrics;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using NSubstitute;
using Verbara.Sdk.Enums;
using Verbara.Sdk.Live.Server;
using Verbara.Sdk.Sessions.Diagnostics;
using Verbara.Sdk.Sessions.Internal;
using Verbara.Sdk.Sessions.Manager;

namespace Verbara.Sdk.Sessions.Tests.Manager;

/// <summary>
/// A snoop channel is a tap Asterisk creates on a call, not a call: its arrival opens and joins nothing, and its
/// departure decides nothing.
///
/// <para>The channels carry the shape measured on Asterisk 22.12.0, 23.6.0 and 24.0.0 for a real ARI snoop: the name
/// <c>Snoop/&lt;target uniqueid&gt;-&lt;n&gt;</c>, whatever id the client passed, and a linkedid that is the snoop's own
/// uniqueid, not its target's. The removal-by-linkedid and reconstruction cases give the snoop its call's linkedid
/// instead: no Asterisk measured does that, but a channel that does reaches the call by that linkedid alone, so those
/// routes are bound too.</para>
///
/// <para>The session counters are process-wide and untagged and this assembly runs classes in parallel, so the capture
/// counts only what is recorded on the test's own thread. Every step a test drives is synchronous (the channel manager
/// raises its events inline and the session manager acts inside them), so the capture sees exactly what this test's
/// channels produced. The active-sessions gauge is this manager's own and is read from its meter.</para>
/// </summary>
[SuppressMessage("Reliability", "CA1001:Types that own disposable fields should be disposable", Justification = "Disposed via IAsyncLifetime")]
public sealed class CallSessionManagerSnoopChannelTests : IAsyncLifetime
{
    private const string ServerId = "srv-1";

    /// <summary>The snoop as Asterisk named it in the lab run: after the channel it taps.</summary>
    private const string SnoopName = "Snoop/1794794992.7-00000001";

    /// <summary>The snoop's own uniqueid, which Asterisk also gives it as its linkedid.</summary>
    private const string SnoopUniqueId = "1794794993.11";

    private readonly IAmiConnection _connection = Substitute.For<IAmiConnection>();
    private readonly VerbaraServer _server;
    private readonly CallSessionManager _sut;
    private readonly List<SessionDomainEvent> _events = [];
    private readonly IDisposable _subscription;

    public CallSessionManagerSnoopChannelTests()
    {
        _connection.AsteriskVersion.Returns("22.0.0");
        _server = new VerbaraServer(_connection, NullLogger<VerbaraServer>.Instance);
        _sut = new CallSessionManager(
            Options.Create(new SessionOptions()), NullLogger<CallSessionManager>.Instance, new InMemorySessionStore());
        _subscription = _sut.Events.Subscribe(_events.Add);
    }

    /// <summary>Bound on the class cleanup, so a hang there fails the test instead of stalling the lane.</summary>
    private static readonly TimeSpan CleanupBound = TimeSpan.FromSeconds(30);

    public Task InitializeAsync() => Task.CompletedTask;

    public Task DisposeAsync() => ReleaseAsync().WaitAsync(CleanupBound);

    private async Task ReleaseAsync()
    {
        _subscription.Dispose();
        await _sut.DisposeAsync();
        await _server.DisposeAsync();
    }

    // --- live route: a snoop with a linkedid of its own -------------------------------------------------------------

    [Theory]
    [InlineData(SnoopName, SnoopUniqueId)]
    [InlineData("snoop/1794794992.7-00000002", "1794794993.12")]
    public void OnChannelAdded_ShouldOpenNoCall_WhenASnoopWithItsOwnLinkedIdArrivesAndHangsUp(string name, string uniqueId)
    {
        _sut.AttachToServer(_server, ServerId);
        using var meters = new SessionMeterCapture(_sut);

        _server.Channels.OnNewChannel(uniqueId, name, ChannelState.Up, linkedId: uniqueId);
        var whileUp = Read(uniqueId, uniqueId, meters);
        _server.Channels.OnHangup(uniqueId, HangupCause.NormalClearing);
        var afterHangup = Read(uniqueId, uniqueId, meters);

        new { WhileUp = whileUp, AfterHangup = afterHangup }.Should().BeEquivalentTo(
            new { WhileUp = Nothing, AfterHangup = Nothing },
            $"'{name}' is a tap Asterisk creates on a call, not a call (the prefix is matched ignoring case): it opens no "
            + "session, publishes no call event and moves no session meter, while it is up or after it hangs up");
    }

    /// <summary>Positive control: the identical sequence on an ordinary channel opens one call and ends it once.</summary>
    [Fact]
    public void OnChannelAdded_ShouldOpenExactlyOneCall_WhenAnOrdinaryChannelArrivesAndHangsUpTheSameWay()
    {
        const string uniqueId = "1794794993.13";
        _sut.AttachToServer(_server, ServerId);
        using var meters = new SessionMeterCapture(_sut);

        _server.Channels.OnNewChannel(uniqueId, "PJSIP/trunk-00000001", ChannelState.Up, linkedId: uniqueId);
        var whileUp = Read(uniqueId, uniqueId, meters);
        _server.Channels.OnHangup(uniqueId, HangupCause.NormalClearing);
        var afterHangup = Read(uniqueId, uniqueId, meters);

        new { WhileUp = whileUp, AfterHangup = afterHangup }.Should().BeEquivalentTo(
            new
            {
                WhileUp = new Reading(FoundByChannel: true, FoundByLinkedId: true, Active: 1, ActiveGauge: 1,
                    Started: 1, Endings: 0, Created: 1, Completed: 0, Failed: 0),
                AfterHangup = new Reading(FoundByChannel: false, FoundByLinkedId: true, Active: 0, ActiveGauge: 0,
                    Started: 1, Endings: 1, Created: 1, Completed: afterHangup.Completed, Failed: afterHangup.Failed),
            },
            "the same arrival and hangup on an ordinary channel is one call, started once and ended once: this is what "
            + "the snoop cases would read if the snoop were treated as a call");
        (afterHangup.Completed + afterHangup.Failed).Should().Be(1, "the call's one ending is counted once");
    }

    // --- removal by linkedid: a snoop that carries a live call's linkedid -------------------------------------------

    [Fact]
    public void OnChannelRemoved_ShouldLeaveTheLiveCallAsIfNoSnoopExisted_WhenASnoopCarryingItsLinkedIdArrivesAndHangsUp()
    {
        _sut.AttachToServer(_server, ServerId);
        var twin = AnsweredCall("1794794990.1", "1794794990.2");
        var call = AnsweredCall("1794794992.7", "1794794992.8");
        var participantsBefore = Legs(call);
        var eventsBefore = _events.Count;

        // A snoop on the call's first leg that carries the call's linkedid.
        const string snoopId = "1794794993.14";
        const string snoop = "Snoop/1794794992.7-00000001";
        _server.Channels.OnNewChannel(snoopId, snoop, ChannelState.Up, linkedId: call.LinkedId);
        var participantsWhileSnoopUp = Legs(call);
        _server.Channels.OnHangup(snoopId, HangupCause.NormalClearing);

        var afterSnoop = new
        {
            Participants = Legs(call),
            ParticipantsWhileSnoopUp = participantsWhileSnoopUp,
            AuditNamingTheSnoop = call.Events.Where(e => e.SourceChannel == snoop).Select(e => e.Type.ToString()).ToArray(),
            FoundBySnoop = _sut.GetByChannelId(snoopId) is not null,
            LastDeparture = call.LastDeparture?.ToString() ?? "none",
            CallStillLive = _sut.ActiveSessions.Any(s => ReferenceEquals(s, call)),
            NotificationsDuringSnoop = _events.Skip(eventsBefore).Select(e => e.GetType().Name).ToArray(),
        };

        HangUpBoth(twin, "1794794990.1", "1794794990.2");
        HangUpBoth(call, "1794794992.7", "1794794992.8");

        new
        {
            AfterSnoop = afterSnoop,
            Ending = Ending(call),
        }.Should().BeEquivalentTo(
            new
            {
                AfterSnoop = new
                {
                    Participants = participantsBefore,
                    ParticipantsWhileSnoopUp = participantsBefore,
                    AuditNamingTheSnoop = Array.Empty<string>(),
                    FoundBySnoop = false,
                    LastDeparture = "none",
                    CallStillLive = true,
                    NotificationsDuringSnoop = Array.Empty<string>(),
                },
                Ending = Ending(twin),
            },
            "a snoop is not a party to the call whose linkedid it carries: it joins nothing and its hangup records no "
            + "departure, so the call keeps its two legs and later ends exactly as the same call with no snoop ends");
    }

    /// <summary>
    /// The order Asterisk gives a real snoop: it ends after the channel it taps. The call's legs hang up while the snoop
    /// carrying its linkedid is still held, and the call ends right then, from its legs, as the same call ends with no
    /// snoop; the snoop's later hangup changes nothing.
    /// </summary>
    [Fact]
    public void OnChannelRemoved_ShouldEndTheCallFromItsLegs_WhenTheyHangUpWhileASnoopCarryingItsLinkedIdIsStillHeld()
    {
        _sut.AttachToServer(_server, ServerId);
        var twin = AnsweredCall("1794794990.1", "1794794990.2");
        var call = AnsweredCall("1794794992.7", "1794794992.8");

        const string snoopId = "1794794993.15";
        const string snoop = "Snoop/1794794992.7-00000001";
        _server.Channels.OnNewChannel(snoopId, snoop, ChannelState.Up, linkedId: call.LinkedId);

        HangUpBoth(twin, "1794794990.1", "1794794990.2");
        _server.Channels.OnHangup("1794794992.8", HangupCause.NormalClearing);
        _server.Channels.OnHangup("1794794992.7", HangupCause.NormalClearing);
        var endedWhileSnoopHeld = !_sut.ActiveSessions.Any(s => ReferenceEquals(s, call));
        var endingWhileSnoopHeld = Ending(call);
        var eventsBeforeSnoopHangup = _events.Count;
        _server.Channels.OnHangup(snoopId, HangupCause.NormalClearing);

        new
        {
            EndedWhileSnoopHeld = endedWhileSnoopHeld,
            Ending = endingWhileSnoopHeld,
            NotificationsAtSnoopHangup = _events.Skip(eventsBeforeSnoopHangup).Select(e => e.GetType().Name).ToArray(),
            AuditNamingTheSnoop = call.Events.Where(e => e.SourceChannel == snoop).Select(e => e.Type.ToString()).ToArray(),
            Active = _sut.ActiveSessions.Count(),
        }.Should().BeEquivalentTo(
            new
            {
                EndedWhileSnoopHeld = true,
                Ending = Ending(twin),
                NotificationsAtSnoopHangup = Array.Empty<string>(),
                AuditNamingTheSnoop = Array.Empty<string>(),
                Active = 0,
            },
            "a held snoop is not one of the call's channels: when the last real leg leaves the call ends then, exactly as "
            + "the same call with no snoop ends, and the snoop's later hangup decides nothing");
    }

    // --- reconstruction: a held snoop under the reconstructed call's linkedid ---------------------------------------

    [Fact]
    public void RegisterReconstructedSession_ShouldNotTakeInTheSnoop_WhenTheServerHoldsASnoopUnderTheCallsLinkedId()
    {
        var call = ReconstructWithSecondChannel(SnoopName, SnoopUniqueId);

        new
        {
            Participants = Legs(call),
            FoundBySecondChannel = _sut.GetByChannelId(SnoopUniqueId) is not null,
        }.Should().BeEquivalentTo(
            new { Participants = OnlyTheLeg, FoundBySecondChannel = false },
            "a reconstructed call takes in the held channels of its linkedid that are its legs, and a snoop on one of "
            + "them is a tap, not a leg");
    }

    /// <summary>Positive control: the same registration takes in an ordinary second leg held under that linkedid.</summary>
    [Fact]
    public void RegisterReconstructedSession_ShouldTakeInTheSecondLeg_WhenTheServerHoldsAnOrdinaryChannelUnderTheCallsLinkedId()
    {
        const string secondLeg = "1794794992.8";
        var call = ReconstructWithSecondChannel("PJSIP/100-00000002", secondLeg);

        new
        {
            Participants = Legs(call),
            FoundBySecondChannel = ReferenceEquals(_sut.GetByChannelId(secondLeg), call),
        }.Should().BeEquivalentTo(
            new { Participants = new[] { "1794794992.7", secondLeg }, FoundBySecondChannel = true },
            "the registration joins every held channel of its linkedid that no other call owns: this is what the snoop "
            + "case would read if the snoop were treated as a leg");
    }

    private static readonly string[] OnlyTheLeg = ["1794794992.7"];

    // --- helpers ----------------------------------------------------------------------------------------------------

    private static readonly Reading Nothing = new(FoundByChannel: false, FoundByLinkedId: false, Active: 0,
        ActiveGauge: 0, Started: 0, Endings: 0, Created: 0, Completed: 0, Failed: 0);

    private Reading Read(string uniqueId, string linkedId, SessionMeterCapture meters) => new(
        _sut.GetByChannelId(uniqueId) is not null,
        _sut.GetByLinkedId(linkedId) is not null,
        _sut.ActiveSessions.Count(),
        meters.ReadActiveGauge(),
        _events.OfType<CallStartedEvent>().Count(),
        Endings(),
        meters.Get("sessions.created"),
        meters.Get("sessions.completed"),
        meters.Get("sessions.failed"));

    /// <summary>
    /// A two-leg call answered in front of the manager: the caller rings, dials the agent, the agent answers. The
    /// linkedid is the caller's uniqueid, as Asterisk assigns it.
    /// </summary>
    private CallSession AnsweredCall(string caller, string agent)
    {
        _server.Channels.OnNewChannel(caller, $"PJSIP/trunk-{caller}", ChannelState.Ring,
            context: "from-trunk", linkedId: caller);
        _server.Channels.OnNewChannel(agent, $"PJSIP/100-{agent}", ChannelState.Ring, linkedId: caller);
        _server.Channels.OnDialBegin(caller, agent, $"PJSIP/100-{agent}", null);
        _server.Channels.OnNewState(agent, ChannelState.Up);
        _server.Channels.OnNewState(caller, ChannelState.Up);
        var call = _sut.GetByLinkedId(caller)
            ?? throw new InvalidOperationException($"premise: a call was opened for '{caller}'");
        Legs(call).Should().Equal([caller, agent], "premise: the call holds its two legs");
        return call;
    }

    private void HangUpBoth(CallSession call, string caller, string agent)
    {
        _server.Channels.OnHangup(agent, HangupCause.NormalClearing);
        _server.Channels.OnHangup(caller, HangupCause.NormalClearing);
        _sut.ActiveSessions.Should().NotContain(call, "premise: the call ended with both legs hung up");
    }

    /// <summary>How a call ended: its state and cause, and the ending notifications published for it.</summary>
    private object Ending(CallSession call) => new
    {
        State = call.State.ToString(),
        Cause = call.HangupCause?.ToString() ?? "none",
        Notifications = EndingEvents()
            .Where(e => e.SessionId == call.SessionId)
            .Select(ended => $"ended cause={ended.Cause?.ToString() ?? "none"}")
            .ToArray(),
    };

    private int Endings() => EndingEvents().Length;

    /// <summary>
    /// The endings published so far. Every ending is a <see cref="CallEndedEvent"/>, a failed one included, so a
    /// CallFailedEvent published beside it fails the reading rather than slipping past it.
    /// </summary>
    private CallEndedEvent[] EndingEvents()
    {
#pragma warning disable VSDK0009 // Named on purpose: the SDK must never publish it.
        _events.OfType<CallFailedEvent>().Should().BeEmpty("every ending, a failed one included, is a CallEndedEvent");
#pragma warning restore VSDK0009
        return [.. _events.OfType<CallEndedEvent>()];
    }

    private static string[] Legs(CallSession call) => call.Participants.Select(p => p.UniqueId).ToArray();

    /// <summary>
    /// The server holds the call's leg and a second channel under one linkedid before the manager attaches (so the
    /// manager opened nothing for either), then the call is registered as reconstructed with the leg alone.
    /// </summary>
    private CallSession ReconstructWithSecondChannel(string secondName, string secondUniqueId)
    {
        const string leg = "1794794992.7";
        _server.Channels.OnNewChannel(leg, "PJSIP/trunk-00000001", ChannelState.Up, linkedId: leg);
        _server.Channels.OnNewChannel(secondUniqueId, secondName, ChannelState.Up, linkedId: leg);
        _sut.AttachToServer(_server, ServerId);
        _sut.GetByLinkedId(leg).Should().BeNull("premise: channels held before the attach open no call");

        var call = new CallSession("reconstructed-1", leg, ServerId, CallDirection.Inbound);
        call.AddParticipant(new SessionParticipant
        {
            UniqueId = leg, Channel = "PJSIP/trunk-00000001", Technology = "PJSIP", Role = ParticipantRole.Caller,
        });
        _sut.RegisterReconstructedSession(call).Should().BeTrue("premise: the registration is accepted");
        return call;
    }

    private sealed record Reading(
        bool FoundByChannel,
        bool FoundByLinkedId,
        int Active,
        long? ActiveGauge,
        int Started,
        int Endings,
        long Created,
        long Completed,
        long Failed);

    /// <summary>
    /// The process-wide session counters, counted only on the thread that created the capture, plus this manager's
    /// own active-sessions gauge.
    /// </summary>
    private sealed class SessionMeterCapture : IDisposable
    {
        private readonly int _thread = Environment.CurrentManagedThreadId;
        private readonly MeterListener _counters = new();
        private readonly MeterListener _gauges = new();
        private readonly ConcurrentDictionary<string, long> _sums = new(StringComparer.Ordinal);
        private long? _active;

        public SessionMeterCapture(CallSessionManager manager)
        {
            // Read before the listener starts: a first read of SessionMetrics runs its static initializer, which
            // publishes the instruments while the field is still null.
            var meter = SessionMetrics.Meter;
            var residency = manager.ResidencyMeter;

            _counters.InstrumentPublished = (instrument, listener) =>
            {
                if (ReferenceEquals(instrument.Meter, meter))
                    listener.EnableMeasurementEvents(instrument);
            };
            _counters.SetMeasurementEventCallback<long>((instrument, value, _, _) =>
            {
                if (Environment.CurrentManagedThreadId == _thread)
                    _sums.AddOrUpdate(instrument.Name, value, (_, sum) => sum + value);
            });
            _counters.Start();

            _gauges.InstrumentPublished = (instrument, listener) =>
            {
                if (ReferenceEquals(instrument.Meter, residency) && instrument.Name == "sessions.active")
                    listener.EnableMeasurementEvents(instrument);
            };
            _gauges.SetMeasurementEventCallback<long>((_, value, _, _) => _active = value);
            _gauges.Start();
        }

        public long Get(string name) => _sums.GetValueOrDefault(name);

        public long? ReadActiveGauge()
        {
            _active = null;
            _gauges.RecordObservableInstruments();
            return _active;
        }

        public void Dispose()
        {
            _gauges.Dispose();
            _counters.Dispose();
        }
    }
}

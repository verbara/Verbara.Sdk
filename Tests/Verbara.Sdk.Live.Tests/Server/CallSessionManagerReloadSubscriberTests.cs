using System.Collections.Concurrent;
using System.Diagnostics.Metrics;
using FluentAssertions;
using FluentAssertions.Execution;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using NSubstitute;
using Verbara.Sdk.Ami.Events;
using Verbara.Sdk.Enums;
using Verbara.Sdk.Live.Channels;
using Verbara.Sdk.Live.Server;
using Verbara.Sdk.Live.Tests.Harness;
using Verbara.Sdk.Sessions;
using Verbara.Sdk.Sessions.Extensions;
using Verbara.Sdk.Sessions.Manager;

namespace Verbara.Sdk.Live.Tests.Server;

/// <summary>
/// What a subscriber of the session notifications that throws does one layer down, to the code that raised the
/// notification: a reload's reconciliation of the channel table, and the AMI connection whose events the session
/// engine is fed by.
/// </summary>
/// <remarks>
/// <para>The tests live in <c>Verbara.Sdk.Live.Tests</c>, not in <c>Verbara.Sdk.Sessions.Tests</c>: the reload's
/// reconciliation and the piped AMI harness are internal to <c>Verbara.Sdk.Live</c>. The manager is built with its
/// public constructor over a test-local <see cref="SessionStoreBase"/>.</para>
/// <para>The AMI test reads the connection's log and its <c>ami.events.observer_faults</c> counter. Its second cell
/// makes an AMI observer itself throw, so the same reading is shown to see a failure the connection does report.</para>
/// </remarks>
[Collection(SessionSubscriberFaultsGroup.Name)]
public sealed class CallSessionManagerReloadSubscriberTests
{
    private const string ServerId = "srv-1";
    private const string ObserverFaultLine = "Observer threw on";

    private static readonly string[] Gone = ["gone-1", "gone-2", "gone-3"];
    private static readonly string[] GoneLinked = ["G-1", "G-2", "G-3"];
    private static readonly string[] Listed = ["new-1", "new-2"];
    private static readonly string[] ListedLinked = ["N-1", "N-2"];

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ReconcileWithSnapshot_ShouldEndEveryGoneCallAndAdmitEveryListedChannel_WhenACallEndedSubscriberThrows(
        bool subscriberThrows)
    {
        var connection = Substitute.For<IAmiConnection>();
        connection.AsteriskVersion.Returns("22.9.0");
        await using var server = new VerbaraServer(connection, NullLogger<VerbaraServer>.Instance);
        await using var manager = new CallSessionManager(
            Options.Create(new SessionOptions()), NullLogger<CallSessionManager>.Instance, new NullStore());
        manager.AttachToServer(server, ServerId);
        using var thrower = subscriberThrows ? manager.Events.Subscribe(new ThrowsOn(e => e is CallEndedEvent)) : null;
        var ended = 0;
        var started = 0;
        using var counter = manager.Events.Subscribe(e =>
        {
            if (e is CallEndedEvent) ended++;
            if (e is CallStartedEvent) started++;
        });
        for (var i = 0; i < Gone.Length; i++)
            server.Channels.OnNewChannel(Gone[i], $"PJSIP/{Gone[i]}", ChannelState.Up, context: "from-trunk",
                linkedId: GoneLinked[i]);
        var startedBefore = started;

        string? escaped = null;
        using (var window = server.Channels.OpenReadWindow())
        {
            List<ChannelSnapshotEntry> snapshot =
            [
                new(Listed[0], $"PJSIP/{Listed[0]}", ChannelState.Up, LinkedId: ListedLinked[0]),
                new(Listed[1], $"PJSIP/{Listed[1]}", ChannelState.Up, LinkedId: ListedLinked[1]),
            ];
            try
            {
                server.Channels.ReconcileWithSnapshot(snapshot, window);
            }
            catch (Exception ex) when (ex is not OutOfMemoryException)
            {
                escaped = ex.GetType().Name;
            }
        }

        new
        {
            Escaped = escaped,
            GoneStillHeld = Gone.Count(id => server.Channels.GetByUniqueId(id) is not null),
            GoneCallsEnded = GoneLinked.Count(l => manager.GetByLinkedId(l)?.State is CallSessionState.Completed or CallSessionState.Failed),
            CallEndedEvents = ended,
            ListedAdmitted = Listed.Count(id => server.Channels.GetByUniqueId(id) is not null),
            ListedCallsOpened = ListedLinked.Count(l => manager.GetByLinkedId(l) is not null),
            CallStartedEvents = started - startedBefore,
        }.Should().BeEquivalentTo(
            new
            {
                Escaped = default(string),
                GoneStillHeld = 0,
                GoneCallsEnded = 3,
                CallEndedEvents = 3,
                ListedAdmitted = 2,
                ListedCallsOpened = 2,
                CallStartedEvents = 2,
            },
            "the reload removes the three channels the snapshot no longer lists and ends their calls, and admits the "
            + "two it lists and opens a call for each, whatever a subscriber of the endings throws");
    }

    [Fact]
    public async Task ReconcileWithSnapshot_ShouldKeepAnAnsweredCallsReportedState_WhenAnEarlierAdmissionSubscriberOffloadsItsNormalHangup()
    {
        const string UniqueId = "snapshot-answered";
        const string LinkedId = "snapshot-call";
        var connection = Substitute.For<IAmiConnection>();
        connection.AsteriskVersion.Returns("22.9.0");
        await using var server = new VerbaraServer(connection, NullLogger<VerbaraServer>.Instance);
        var store = new RecordingStateStore();
        await using var manager = new CallSessionManager(
            Options.Create(new SessionOptions()), NullLogger<CallSessionManager>.Instance, store);
        var probe = new HandlerOffloadProbe();
        AsteriskChannel? admitted = null;
        AsteriskChannel? heldBeforeHangup = null;
        AsteriskChannel? removed = null;
        Thread? hangupWorker = null;
        var hangupReturned = false;
        var hangupReturnedBeforeSessions = false;
        ChannelState? stateBeforeSessions = null;
        CallSessionState? initialSessionState = null;
        CallSessionState? endingSessionState = null;
        var endings = new List<CallEndedEvent>();

        // This subscriber must precede Sessions: its worker mutates the public live object first.
        server.Channels.ChannelAdded += channel =>
        {
            admitted = channel;
            heldBeforeHangup = server.Channels.GetByUniqueId(UniqueId);
            probe.Offload(() =>
            {
                hangupWorker = Thread.CurrentThread;
                server.Channels.OnHangup(UniqueId, HangupCause.NormalClearing);
                Volatile.Write(ref hangupReturned, true);
            });
            hangupReturnedBeforeSessions = Volatile.Read(ref hangupReturned);
            stateBeforeSessions = channel.State;
        };
        manager.AttachToServer(server, ServerId);
        server.Channels.ChannelRemoved += channel => removed = channel;
        using var events = manager.Events.Subscribe(e =>
        {
            if (e is CallStartedEvent)
                initialSessionState = manager.GetByLinkedId(LinkedId)?.State;
            if (e is CallEndedEvent ended)
            {
                endings.Add(ended);
                endingSessionState = manager.GetByLinkedId(LinkedId)?.State;
            }
        });

        var finishedInsideHandler = false;
        var workerJoined = false;
        try
        {
            using var window = server.Channels.OpenReadWindow();
            server.Channels.ReconcileWithSnapshot(
                [new(UniqueId, "PJSIP/snapshot-answered", ChannelState.Up, LinkedId: LinkedId)], window);
            // The probe records its two-second handler wait; assertions run after worker cleanup.
            finishedInsideHandler = probe.FinishedInsideTheHandler();
        }
        finally
        {
            workerJoined = hangupWorker is not null && hangupWorker.Join(HandlerOffloadProbe.Drain);
        }

        var session = manager.GetByLinkedId(LinkedId);
        using var scope = new AssertionScope();
        finishedInsideHandler.Should().BeTrue("the hangup must finish while the first admission subscriber waits");
        workerJoined.Should().BeTrue("the offloaded physical worker must be reaped");
        hangupReturnedBeforeSessions.Should().BeTrue();
        stateBeforeSessions.Should().Be(ChannelState.Down, "the earlier subscriber must expose the live-object race");
        admitted.Should().NotBeNull();
        heldBeforeHangup.Should().BeSameAs(admitted);
        removed.Should().BeSameAs(admitted, "public events keep the table's same live channel instance");
        admitted?.State.Should().Be(ChannelState.Down);
        server.Channels.GetByUniqueId(UniqueId).Should().BeNull();
        initialSessionState.Should().Be(CallSessionState.Connected, "the snapshot reported an answered call");
        endingSessionState.Should().Be(CallSessionState.Completed);
        (session is not null).Should().BeTrue("the answered snapshot call must retain its completed session");
        if (session is not null)
        {
            session.State.Should().Be(CallSessionState.Completed);
            session.Metadata.Should().Contain("origin", "reload");
            session.Events.Should().ContainSingle(e => e.Type == CallSessionEventType.Connected && e.Detail == "reload");
            session.ConnectedAt.Should().BeNull("a reload never observed when the call answered");
            endings.Should().ContainSingle().Which.SessionId.Should().Be(session.SessionId);
            store.Saves.Where(saved => saved.SessionId == session.SessionId).Select(saved => saved.State)
                .Should().Equal(CallSessionState.Connected, CallSessionState.Completed);
        }
        endings.Should().ContainSingle().Which.Cause.Should().Be(HangupCause.NormalClearing);
    }

    /// <summary>Which subscriber throws on every call notification of the test.</summary>
    public enum Failing
    {
        /// <summary>A subscriber of the session engine's notifications.</summary>
        SessionSubscriber,

        /// <summary>An observer of the AMI connection's events, subscribed after the server.</summary>
        AmiObserver,
    }

    [Theory]
    [InlineData(Failing.SessionSubscriber, 0)]
    [InlineData(Failing.AmiObserver, 2)]
    public async Task AmiConnection_ShouldReportOnlyItsOwnObserversFailures_WhenASubscriberThrowsOnEveryCallNotification(
        Failing failing, int expectedReports)
    {
        var peer = new BootingAsterisk { BootedAtLogin = true };
        await using var run = await Run.ConnectAsync(peer);
        await using var manager = new CallSessionManager(
            Options.Create(new SessionOptions()), NullLogger<CallSessionManager>.Instance, new NullStore());
        manager.AttachToServer(run.Server, ServerId);
        await run.StartServerAsync();
        using var faults = new ObserverFaultCounter();

        using var sessionThrower = failing == Failing.SessionSubscriber
            ? manager.Events.Subscribe(new ThrowsOn(_ => true))
            : null;
        using var amiThrower = failing == Failing.AmiObserver
            ? run.Connection.Subscribe(new AmiObserver(e =>
            {
                if (e is NewChannelEvent or HangupEvent)
                    throw new InvalidOperationException($"An AMI observer failed on {e.EventType}.");
            }))
            : null;
        var hangupDispatched = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        // Subscribed last: the connection calls its observers in order, so once this one has seen the Hangup, the
        // server's observer and every subscriber behind it have handled it.
        using var last = run.Connection.Subscribe(new AmiObserver(e =>
        {
            if (e is HangupEvent)
                hangupDispatched.TrySetResult();
        }));
        var linesBefore = ObserverFaultLines(run);

        await peer.SendEventAsync("Newchannel",
        [
            new("Channel", "PJSIP/trunk-00000001"), new("ChannelState", "6"), new("ChannelStateDesc", "Up"),
            new("CallerIDNum", "5551234"), new("Context", "from-trunk"), new("Exten", "100"), new("Priority", "1"),
            new("Uniqueid", "1700000000.1"), new("Linkedid", "1700000000.1"),
        ]);
        await peer.SendEventAsync("Hangup",
        [
            new("Channel", "PJSIP/trunk-00000001"), new("Uniqueid", "1700000000.1"), new("Linkedid", "1700000000.1"),
            new("Cause", "16"), new("Cause-txt", "Normal Clearing"),
        ]);
        await hangupDispatched.Task.WaitAsync(Run.Bound);

        new
        {
            Lines = ObserverFaultLines(run) - linesBefore,
            Counted = faults.Total,
            CallEnded = manager.GetByLinkedId("1700000000.1")?.State is CallSessionState.Completed or CallSessionState.Failed,
        }.Should().BeEquivalentTo(
            new { Lines = expectedReports, Counted = (long)expectedReports, CallEnded = true },
            $"the AMI connection reports as an observer's failure only what its own observers throw ({failing}: "
            + $"{expectedReports} reports); a session subscriber's failure is not its observer's. Log: "
            + string.Join(" | ", run.ConnectionLog.Entries.Select(e => e.Line)));
    }

    private static int ObserverFaultLines(Run run) =>
        run.ConnectionLog.Entries.Count(e => e.Line.Contains(ObserverFaultLine, StringComparison.Ordinal));

    /// <summary>Sums what is added to <c>ami.events.observer_faults</c> while it is alive.</summary>
    private sealed class ObserverFaultCounter : IDisposable
    {
        private readonly MeterListener _listener = new();
        private long _total;

        public ObserverFaultCounter()
        {
            _listener.InstrumentPublished = (instrument, listener) =>
            {
                if (instrument.Meter.Name == "Verbara.Sdk.Ami" && instrument.Name == "ami.events.observer_faults")
                    listener.EnableMeasurementEvents(instrument);
            };
            _listener.SetMeasurementEventCallback<long>((_, value, _, _) => Interlocked.Add(ref _total, value));
            _listener.Start();
        }

        public long Total => Interlocked.Read(ref _total);

        public void Dispose() => _listener.Dispose();
    }

    /// <summary>An AMI observer that runs <c>onNext</c> on each event.</summary>
    private sealed class AmiObserver(Action<ManagerEvent> onNext) : IObserver<ManagerEvent>
    {
        public void OnNext(ManagerEvent value) => onNext(value);

        public void OnError(Exception error)
        {
            // The connection never faults its observers in these tests; nothing to do.
        }

        public void OnCompleted()
        {
            // Completed when the connection is disposed; nothing to do.
        }
    }

    /// <summary>A session subscriber that throws on the notifications <paramref name="throwsOn"/> selects.</summary>
    private sealed class ThrowsOn(Func<SessionDomainEvent, bool> throwsOn) : IObserver<SessionDomainEvent>
    {
        public void OnNext(SessionDomainEvent value)
        {
            if (throwsOn(value))
                throw new InvalidOperationException($"A session subscriber failed on {value.GetType().Name}.");
        }

        public void OnError(Exception error)
        {
            // The manager's stream never faults; nothing to do.
        }

        public void OnCompleted()
        {
            // Completed when the manager is disposed; nothing to do.
        }
    }

    /// <summary>Copies each saved state immediately, rather than retaining a mutable session reference.</summary>
    private sealed class RecordingStateStore : SessionStoreBase
    {
        public ConcurrentQueue<(string SessionId, CallSessionState State)> Saves { get; } = new();

        public override ValueTask SaveAsync(CallSession session, CancellationToken ct)
        {
            Saves.Enqueue((session.SessionId, session.State));
            return ValueTask.CompletedTask;
        }

        public override ValueTask<CallSession?> GetAsync(string sessionId, CancellationToken ct) =>
            ValueTask.FromResult<CallSession?>(null);
    }

    /// <summary>A store that saves nothing; the tests read the manager and the channel table.</summary>
    private sealed class NullStore : SessionStoreBase
    {
        public override ValueTask SaveAsync(CallSession session, CancellationToken ct) => ValueTask.CompletedTask;

        public override ValueTask<CallSession?> GetAsync(string sessionId, CancellationToken ct) =>
            ValueTask.FromResult<CallSession?>(null);
    }
}

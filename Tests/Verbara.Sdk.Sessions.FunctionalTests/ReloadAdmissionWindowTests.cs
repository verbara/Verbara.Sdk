using System.Collections;
using System.Diagnostics.CodeAnalysis;
using System.Reflection;
using System.Runtime.CompilerServices;
using FluentAssertions;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using NSubstitute;
using Verbara.Sdk.Ami.Actions;
using Verbara.Sdk.Ami.Events;
using Verbara.Sdk.Enums;
using Verbara.Sdk.Live.Channels;
using Verbara.Sdk.Live.Server;
using Verbara.Sdk.Sessions.Internal;
using Verbara.Sdk.Sessions.Manager;

namespace Verbara.Sdk.Sessions.FunctionalTests;

/// <summary>
/// Binds the window this change opened: a reload MUST NOT end a call that arrived after its
/// snapshot was taken (ADR-0062, design D6).
///
/// <para><c>VerbaraServer.OnReconnected</c> re-subscribes the event observer <b>before</b> it awaits
/// the reload, so live events resume while the snapshot is still being read. A call that starts in
/// that window is admitted to the channel table, is legitimately absent from the older snapshot, and
/// a reconciliation that read that absence as evidence would end a call that is up. On a large
/// estate the window is a full <c>Status</c> round trip.</para>
///
/// <para>This is a regression <b>this change introduces</b>, not a pre-existing one: before it,
/// <c>Channels.Clear()</c> removed every channel in silence and raised nothing, so no call ever
/// ended from a reload and none could end wrongly. The pair of tests below is therefore two-sided on
/// purpose — the call that arrived mid-read survives, and the ordinary stale call is still ended.
/// The second is what stops the window being closed by weakening the removal path instead.</para>
///
/// <para>Deterministic by construction, with no wall-clock wait anywhere in the premise: the
/// mid-read arrival is raised from inside the fake's <c>Status</c> reply, which by definition runs
/// after <c>VerbaraServer</c> captured its admission mark and before the snapshot is reconciled.
/// The only clock is the guard that bounds the wait for <c>async void OnReconnected</c>.</para>
/// </summary>
[SuppressMessage("Reliability", "CA1001:Types that own disposable fields should be disposable", Justification = "Disposed via IAsyncLifetime")]
public sealed class ReloadAdmissionWindowTests : IAsyncLifetime
{
    private const string ServerId = "test-srv";
    private const string CallerUid = "caller-001";
    private const string AgentUid = "agent-001";
    private const string LinkedId = "linked-001";

    /// <summary>The call that starts while the reload is reading its snapshot.</summary>
    private const string LateUid = "late-001";
    private const string LateName = "PJSIP/trunk-late";
    private const string LateLinkedId = "linked-late";

    private readonly IAmiConnection _connection = Substitute.For<IAmiConnection>();
    private readonly VerbaraServer _server;
    private readonly CallSessionManager _sessions;
    private readonly ReloadLogger _serverLog = new();

    private readonly List<SessionDomainEvent> _sessionEvents = [];
    private readonly List<AsteriskChannel> _removed = [];
    private readonly List<AsteriskChannel> _added = [];
    private readonly IDisposable _sessionSubscription;

    private IReadOnlyList<StatusEvent> _statusReply = [];

    /// <summary>
    /// Fired once, from inside the <c>Status</c> reply, to stand in for the AMI observer delivering
    /// a <c>NewChannel</c> while the reload is in flight. Armed per reload so the initial
    /// <c>StartAsync</c> load does not trip it.
    /// </summary>
    private Action? _whileTheSnapshotIsRead;

    public ReloadAdmissionWindowTests()
    {
        _connection.AsteriskVersion.Returns("21.0.0");
        _connection
            .SendEventGeneratingActionAsync(Arg.Any<ManagerAction>(), Arg.Any<CancellationToken>())
            .Returns(ci => Reply(ci.ArgAt<ManagerAction>(0), ci.ArgAt<CancellationToken>(1)));

        _server = new VerbaraServer(_connection, _serverLog.For<VerbaraServer>());
        _sessions = new CallSessionManager(
            Options.Create(new SessionOptions()),
            Microsoft.Extensions.Logging.Abstractions.NullLogger<CallSessionManager>.Instance,
            new InMemorySessionStore());

        _sessions.AttachToServer(_server, ServerId);
        _sessionSubscription = _sessions.Events.Subscribe(_sessionEvents.Add);
        _server.Channels.ChannelRemoved += _removed.Add;
        _server.Channels.ChannelAdded += _added.Add;
    }

    /// <summary>
    /// Answers the three actions the load sends. The <c>Status</c> leg first lets the test push a
    /// live arrival into the table: this body only runs once the enumeration has started, which is
    /// strictly after <c>VerbaraServer</c> read the admission mark the snapshot will be judged
    /// against, so the ordering the requirement is about needs no clock to arrange. Like a real
    /// connection, it stops with the load's cancellation once that is requested.
    /// </summary>
    private async IAsyncEnumerable<ManagerEvent> Reply(
        ManagerAction action, [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        await Task.Yield();

        if (action is AgentsAction)
        {
            _serverLog.ReloadFinished.TrySetResult();
            yield break;
        }

        if (action is not StatusAction)
            yield break;

        var arrival = Interlocked.Exchange(ref _whileTheSnapshotIsRead, null);
        arrival?.Invoke();
        cancellationToken.ThrowIfCancellationRequested();

        foreach (var status in _statusReply)
            yield return status;
    }

    private async Task GivenAStartedServer() => await _server.StartAsync();

    /// <summary>A two-leg inbound call, answered and bridged: one session, state Connected.</summary>
    private void GivenAnAnsweredCall()
    {
        _server.Channels.OnNewChannel(CallerUid, "PJSIP/trunk-001", ChannelState.Ring,
            callerIdNum: "5551234", context: "from-trunk", linkedId: LinkedId);
        _server.Channels.OnNewChannel(AgentUid, "PJSIP/100-001", ChannelState.Ring,
            linkedId: LinkedId);
        _server.Channels.OnDialBegin(CallerUid, AgentUid, "PJSIP/100-001", null);
        _server.Channels.OnNewState(AgentUid, ChannelState.Up);
        _server.Bridges.OnBridgeCreated("bridge-001", "mixing", "simple_bridge", null, null);
        _server.Bridges.OnChannelEntered("bridge-001", CallerUid);
        _server.Bridges.OnChannelEntered("bridge-001", AgentUid);
    }

    /// <summary>The live <c>NewChannel</c> that lands while the snapshot is being read.</summary>
    private void ACallArrivesLive() =>
        _server.Channels.OnNewChannel(LateUid, LateName, ChannelState.Ring,
            callerIdNum: "5559999", context: "from-trunk", linkedId: LateLinkedId);

    /// <summary>
    /// Raises <c>Reconnected</c> and waits for the reload to end — at its last action when it
    /// completed, at the catch-all's log line when it failed.
    /// </summary>
    private Task WhenTheConnectionReconnects(params StatusEvent[] channelsAsteriskStillHas) =>
        WhenTheConnectionReconnectsWhile(ACallArrivesLive, channelsAsteriskStillHas);

    /// <summary>
    /// <see cref="WhenTheConnectionReconnects"/> with what happens live while the snapshot is read
    /// chosen by the test: <paramref name="whileTheSnapshotIsRead"/> runs once, from inside the
    /// <c>Status</c> reply, before the reply's first frame is handed back.
    /// </summary>
    private async Task WhenTheConnectionReconnectsWhile(
        Action? whileTheSnapshotIsRead, params StatusEvent[] channelsAsteriskStillHas)
    {
        _statusReply = channelsAsteriskStillHas;
        _whileTheSnapshotIsRead = whileTheSnapshotIsRead;
        _serverLog.RearmReloadSignals();
        _connection.Reconnected += Raise.Event<Action>();

        // async void OnReconnected cannot be awaited, so the wait is on its two log-line signals.
        // fence-allow: GUARD-TIMEOUT — bounds that wait so a reload that never ran reports the log
        var timeout = Task.Delay(TimeSpan.FromSeconds(5));
        var ended = await Task.WhenAny(
            _serverLog.ReloadFinished.Task, _serverLog.ReloadFailed.Task, timeout);

        ReferenceEquals(ended, timeout).Should().BeFalse(
            "the reconnect reload neither finished nor logged a failure. Server log:"
            + $"{Environment.NewLine}{_serverLog}");
    }

    /// <summary>The measurement in one line, so a failure reports numbers and not a dump.</summary>
    private string Describe() =>
        $"{_sessions.ActiveSessions.Count()} active session(s) [" +
        string.Join(" | ", _sessions.ActiveSessions.Select(
            s => $"linked={s.LinkedId} state={s.State} participants={s.Participants.Count}")) +
        $"]; {_server.Channels.ChannelCount} channel(s) held [" +
        string.Join(", ", _server.Channels.ActiveChannels.Select(c => c.UniqueId)) +
        $"]; ChannelRemoved raised for [{string.Join(", ", _removed.Select(c => c.UniqueId))}]" +
        $"; domain events [{string.Join(", ", _sessionEvents.Select(e => e.GetType().Name))}]";

    /// <summary>
    /// A channel as <c>Status</c> really reports it: the state arrives as the numeric
    /// <c>ChannelState</c> header in <c>RawFields</c>, never as <c>StatusEvent.State</c>, which no
    /// supported Asterisk version populates (ADR-0062, design D5).
    /// </summary>
    private static StatusEvent Leg(string uniqueId, string channel, string linkedId = LinkedId) => new()
    {
        UniqueId = uniqueId,
        Channel = channel,
        LinkedId = linkedId,
        RawFields = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["ChannelState"] = "6",       // AST_STATE_UP, exactly as the frame carries it
            ["ChannelStateDesc"] = "Up",
        },
    };

    // --- scenario: a call starts while the reload is still reading -------------------------------

    [Fact]
    public async Task Reconnect_ShouldNotEndTheCallThatArrivedWhileTheSnapshotWasRead_WhenTheCompletedSnapshotOmitsIt()
    {
        await GivenAStartedServer();
        GivenAnAnsweredCall();

        // The snapshot completes and reports only the call Asterisk had when it was asked. The
        // call that started during the read is missing from it because it could not be in it.
        await WhenTheConnectionReconnects(
            Leg(CallerUid, "PJSIP/trunk-001"),
            Leg(AgentUid, "PJSIP/100-001"));

        _server.Channels.GetByUniqueId(LateUid).Should().NotBeNull(
            "the snapshot was requested before this channel existed, so its absence says nothing "
            + $"about it; removing it would end a call that is up. Measured: {Describe()}");
        _removed.Select(c => c.UniqueId).Should().NotContain(LateUid,
            $"nothing proved this channel gone. Measured: {Describe()}");
        _sessionEvents.OfType<CallEndedEvent>().Should().BeEmpty(
            "no call ended: one was still in the snapshot and the other is newer than it. "
            + $"Measured: {Describe()}");
        _sessions.ActiveSessions.Select(s => s.LinkedId).Should().Contain(LateLinkedId,
            $"the call that arrived mid-read is still in progress. Measured: {Describe()}");
    }

    // --- scenario: the ordinary stale channel is still ended --------------------------------------

    [Fact]
    public async Task Reconnect_ShouldStillEndTheCallHeldBeforeTheReload_WhenTheCompletedSnapshotOmitsIt()
    {
        await GivenAStartedServer();
        GivenAnAnsweredCall();
        var sessionIdBefore = _sessions.ActiveSessions.Single().SessionId;

        // Asterisk answers the reload with nothing: the held call ended while the socket was down.
        // A call still arrives mid-read, so both sides of the mark are exercised at once — the fix
        // cannot have been implemented by making the reload stop removing things.
        await WhenTheConnectionReconnects();

        _server.Channels.GetByUniqueId(CallerUid).Should().BeNull(
            "the completed snapshot could have reported this channel and did not. Measured: "
            + Describe());
        _removed.Select(c => c.UniqueId).Should().Contain([CallerUid, AgentUid],
            $"both legs of the held call are gone. Measured: {Describe()}");
        _sessionEvents.OfType<CallEndedEvent>().Should().ContainSingle(
            "the reload is the only notification the consumer will ever get that the held call "
            + $"ended, and the window must not have swallowed it. Measured: {Describe()}");
        _sessions.ActiveSessions.Should().NotContain(s => s.SessionId == sessionIdBefore,
            $"the held call is over. Measured: {Describe()}");

        _server.Channels.GetByUniqueId(LateUid).Should().NotBeNull(
            "and the call that arrived during the same read is untouched — the mark separates the "
            + $"two, nothing else does. Measured: {Describe()}");
        _sessions.ActiveSessions.Select(s => s.LinkedId).Should().Contain(LateLinkedId,
            $"Measured: {Describe()}");
    }

    // --- scenario: a leg hangs up while the snapshot is read, and the snapshot still lists it --------

    [Fact]
    public async Task Reconnect_ShouldEndTheCallOnItsLastHangup_WhenALegHungUpWhileTheSnapshotListingItWasRead()
    {
        await GivenAStartedServer();
        GivenAnAnsweredCall();
        var call = _sessions.ActiveSessions.Single();

        // The caller hangs up after Status was sent; Asterisk's answer was written before that and
        // still lists both legs.
        await WhenTheConnectionReconnectsWhile(
            () => _server.Channels.OnHangup(CallerUid, HangupCause.NormalClearing),
            Leg(CallerUid, "PJSIP/trunk-001"),
            Leg(AgentUid, "PJSIP/100-001"));
        _server.Channels.OnHangup(AgentUid, HangupCause.NormalClearing);

        new
        {
            Participants = call.Participants.Count,
            Left = call.Participants.Count(p => p.LeftAt.HasValue),
            Ended = _sessionEvents.OfType<CallEndedEvent>().Count(e => e.SessionId == call.SessionId),
        }.Should().BeEquivalentTo(
            new { Participants = 2, Left = 2, Ended = 1 },
            "both legs hung up, so the call has its two legs, both left, and ended once; a leg the stale "
            + $"snapshot brought back would never leave and hold the call forever. Measured: {Describe()}");
    }

    [Fact]
    public async Task Reconnect_ShouldHoldEachLegOnceAndEndTheCall_WhenTheSnapshotReportedTheCallBeforeItsLiveArrival()
    {
        const string first = "fresh-001";
        const string second = "fresh-002";
        const string freshLinkedId = "linked-fresh";
        await GivenAStartedServer();

        // The answer lists a call whose live NewChannel events are still queued behind it.
        await WhenTheConnectionReconnectsWhile(
            whileTheSnapshotIsRead: null,
            Leg(first, "PJSIP/trunk-fresh", freshLinkedId),
            Leg(second, "PJSIP/100-fresh", freshLinkedId));
        _server.Channels.OnNewChannel(first, "PJSIP/trunk-fresh", ChannelState.Up,
            context: "from-trunk", linkedId: freshLinkedId);
        _server.Channels.OnNewChannel(second, "PJSIP/100-fresh", ChannelState.Up, linkedId: freshLinkedId);
        var calls = _sessions.ActiveSessions.Where(s => s.LinkedId == freshLinkedId).ToList();
        _server.Channels.OnHangup(first, HangupCause.NormalClearing);
        _server.Channels.OnHangup(second, HangupCause.NormalClearing);

        new
        {
            Sessions = calls.Count,
            Participants = calls.Sum(s => s.Participants.Count),
            Ended = _sessionEvents.OfType<CallEndedEvent>().Count(e => calls.Any(s => s.SessionId == e.SessionId)),
        }.Should().BeEquivalentTo(
            new { Sessions = 1, Participants = 2, Ended = 1 },
            "a channel is admitted once whichever route reports it first, so the call holds each leg once and "
            + $"its two hangups end it. Measured: {Describe()}");
    }

    [Fact]
    public async Task Reconnect_ShouldOpenNoCall_WhenAChannelNeverHeldHungUpWhileTheSnapshotListingItWasRead()
    {
        const string unseen = "unseen-001";
        const string unseenLinkedId = "linked-unseen";
        await GivenAStartedServer();
        _added.Clear();

        // A call that started while the connection was down hangs up after Status was sent.
        await WhenTheConnectionReconnectsWhile(
            () => _server.Channels.OnHangup(unseen, HangupCause.NormalClearing),
            Leg(unseen, "PJSIP/trunk-unseen", unseenLinkedId));

        new
        {
            Announced = _added.Count(c => c.UniqueId == unseen),
            Held = _server.Channels.GetByUniqueId(unseen) is not null,
            Sessions = _sessions.ActiveSessions.Count(s => s.LinkedId == unseenLinkedId),
        }.Should().BeEquivalentTo(
            new { Announced = 0, Held = false, Sessions = 0 },
            "the SDK observed the hangup after the snapshot was requested; no hangup will ever follow an "
            + $"admission from the stale snapshot, so it would be a call that never ends. Measured: {Describe()}");
    }

    // --- scenario: an answered call in its initial state hangs up while the snapshot is read ---------

    /// <summary>
    /// The dialplan answered the leg in front of the SDK and nothing dialed or queued it, so the call is still
    /// in its initial state. It hangs up at normal clearing after <c>Status</c> was sent; Asterisk's answer was
    /// written before that and still lists the leg. The observed hangup ends the call, completed, once; the
    /// stale snapshot brings no leg back, and the next reload, which no longer lists it, changes nothing. The
    /// test waits for the ending itself, under a bound.
    /// </summary>
    [Fact]
    public async Task Reconnect_ShouldKeepTheObservedCompletedEnding_WhenAnAnsweredCallInItsInitialStateHungUpWhileTheSnapshotListingItWasRead()
    {
        const string ivrUid = "ivr-001";
        const string ivrName = "PJSIP/trunk-ivr";
        const string ivrLinkedId = "linked-ivr";
        await GivenAStartedServer();
        _server.Channels.OnNewChannel(ivrUid, ivrName, ChannelState.Ring,
            callerIdNum: "5551234", context: "from-trunk", linkedId: ivrLinkedId);
        _server.Channels.OnNewState(ivrUid, ChannelState.Up);
        var call = _sessions.ActiveSessions.Single(s => s.LinkedId == ivrLinkedId);
        call.State.Should().Be(CallSessionState.Created, "premise: nothing dialed or queued the answered call");
        var ending = new TaskCompletionSource<CallEndedEvent>(TaskCreationOptions.RunContinuationsAsynchronously);
        using var endings = _sessions.Events.Subscribe(e =>
        {
            if (e is CallEndedEvent ended && ended.SessionId == call.SessionId)
                ending.TrySetResult(ended);
        });

        await WhenTheConnectionReconnectsWhile(
            () => _server.Channels.OnHangup(ivrUid, HangupCause.NormalClearing),
            Leg(ivrUid, ivrName, ivrLinkedId));
        await ending.Task.WaitAsync(TimeSpan.FromSeconds(5));
        var afterTheReload = Outcome(call);

        await WhenTheConnectionReconnectsWhile(whileTheSnapshotIsRead: null);

        new { AfterTheReload = afterTheReload, AfterTheNextReload = Outcome(call) }.Should().BeEquivalentTo(
            new
            {
                AfterTheReload = new CallOutcome(CallSessionState.Completed, HangupCause.NormalClearing, Marker: null, Participants: 1, Endings: 1),
                AfterTheNextReload = new CallOutcome(CallSessionState.Completed, HangupCause.NormalClearing, Marker: null, Participants: 1, Endings: 1),
            },
            "the SDK observed the answer and the normal hangup, so the call ends completed with that cause and no reload "
            + "marker, once, holding its one leg; the stale snapshot brings no leg back, and the next reload leaves the "
            + $"ended call as it is. Measured: {Describe()}");
    }

    /// <summary>How a call stands: its state, cause and reload marker, its participants and its endings.</summary>
    private sealed record CallOutcome(CallSessionState State, HangupCause? Cause, string? Marker, int Participants, int Endings);

    private CallOutcome Outcome(CallSession call) => new(
        call.State,
        call.HangupCause,
        call.Metadata.GetValueOrDefault("cause"),
        call.Participants.Count,
        _sessionEvents.OfType<CallEndedEvent>().Count(e => e.SessionId == call.SessionId));

    // --- what a read keeps once it has ended, by each way it can end --------------------------------

    /// <summary>
    /// A load of the state, as <see cref="WhenTheConnectionReconnectsWhile"/> drives one but awaited directly, with
    /// <paramref name="whileTheSnapshotIsRead"/> run inside the <c>Status</c> reply. Returns how many departures the
    /// channel table kept while the read was open, and what the load ended with.
    /// </summary>
    private async Task<(int RecordedWhileOpen, Exception? Outcome)> WhenAStateLoadEndsAfter(
        Action whileTheSnapshotIsRead, CancellationToken cancellationToken = default)
    {
        var recordedWhileOpen = -1;
        _statusReply = [Leg(CallerUid, "PJSIP/trunk-001"), Leg(AgentUid, "PJSIP/100-001")];
        _whileTheSnapshotIsRead = () =>
        {
            // A held leg and a channel never held hang up while the read is open.
            _server.Channels.OnHangup(CallerUid, HangupCause.NormalClearing);
            _server.Channels.OnHangup("never-held-001", HangupCause.NormalClearing);
            recordedWhileOpen = RecordedDepartures(_server.Channels);
            whileTheSnapshotIsRead();
        };

        var outcome = await Record.ExceptionAsync(
            () => _server.RequestInitialStateAsync(cancellationToken).AsTask().WaitAsync(TimeSpan.FromSeconds(5)));
        return (recordedWhileOpen, outcome);
    }

    [Fact]
    public async Task RequestInitialStateAsync_ShouldKeepNoDeparture_WhenTheReadItWasRecordedForCompleted()
    {
        await GivenAStartedServer();
        GivenAnAnsweredCall();

        var (recordedWhileOpen, outcome) = await WhenAStateLoadEndsAfter(static () => { });

        new
        {
            RecordedWhileOpen = recordedWhileOpen,
            Threw = outcome is not null,
            KeptAfter = RecordedDepartures(_server.Channels),
            OpenWindows = OpenReadWindows(_server.Channels),
        }.Should().BeEquivalentTo(
            new { RecordedWhileOpen = 2, Threw = false, KeptAfter = 0, OpenWindows = 0 },
            $"both departures were recorded for the read, and none outlives it once it completed. Measured: {Describe()}");
    }

    [Fact]
    public async Task RequestInitialStateAsync_ShouldKeepNoDeparture_WhenTheReadItWasRecordedForThrew()
    {
        await GivenAStartedServer();
        GivenAnAnsweredCall();

        var (recordedWhileOpen, outcome) = await WhenAStateLoadEndsAfter(
            static () => throw new InvalidOperationException("the connection failed while Status was read"));

        new
        {
            RecordedWhileOpen = recordedWhileOpen,
            Failure = outcome?.GetType().Name,
            KeptAfter = RecordedDepartures(_server.Channels),
            OpenWindows = OpenReadWindows(_server.Channels),
        }.Should().BeEquivalentTo(
            new { RecordedWhileOpen = 2, Failure = nameof(InvalidOperationException), KeptAfter = 0, OpenWindows = 0 },
            $"a read that throws still closes its window, and nothing it recorded outlives it. Measured: {Describe()}");
    }

    [Fact]
    public async Task RequestInitialStateAsync_ShouldKeepNoDeparture_WhenTheReadItWasRecordedForWasCancelled()
    {
        await GivenAStartedServer();
        GivenAnAnsweredCall();
        using var cancellation = new CancellationTokenSource();

        var (recordedWhileOpen, outcome) = await WhenAStateLoadEndsAfter(cancellation.Cancel, cancellation.Token);

        new
        {
            RecordedWhileOpen = recordedWhileOpen,
            Cancelled = outcome is OperationCanceledException,
            KeptAfter = RecordedDepartures(_server.Channels),
            OpenWindows = OpenReadWindows(_server.Channels),
        }.Should().BeEquivalentTo(
            new { RecordedWhileOpen = 2, Cancelled = true, KeptAfter = 0, OpenWindows = 0 },
            $"a cancelled read still closes its window, and nothing it recorded outlives it. Measured: {Describe()}");
    }

    /// <summary>How many departures the channel table keeps for its open read windows; private, read by reflection.</summary>
    private static int RecordedDepartures(ChannelManager channels) => PrivateCount(channels, "_departures");

    /// <summary>How many read windows the channel table holds open; private, read by reflection.</summary>
    private static int OpenReadWindows(ChannelManager channels) => PrivateCount(channels, "_openReadMarks");

    private static int PrivateCount(ChannelManager channels, string fieldName)
    {
        var field = typeof(ChannelManager).GetField(fieldName, BindingFlags.Instance | BindingFlags.NonPublic)
            ?? throw new InvalidOperationException($"ChannelManager has no private field {fieldName}.");
        var collection = field.GetValue(channels) as ICollection
            ?? throw new InvalidOperationException($"ChannelManager.{fieldName} is not a collection.");
        return collection.Count;
    }

    /// <summary>Bound on the class cleanup, so a hang there fails the test instead of stalling the lane.</summary>
    private static readonly TimeSpan CleanupBound = TimeSpan.FromSeconds(30);

    public Task InitializeAsync() => Task.CompletedTask;

    public Task DisposeAsync() => ReleaseAsync().WaitAsync(CleanupBound);

    private async Task ReleaseAsync()
    {
        _sessionSubscription.Dispose();
        await _sessions.DisposeAsync();
        await _server.DisposeAsync();
    }

    /// <summary>
    /// Records every line the server logs and completes one of two signals when a reload ends: the
    /// last action for a reload that finished, the catch-all's line for one that failed. Both are
    /// rearmed per reload, because <c>StartAsync</c>'s own load trips the first one.
    /// </summary>
    private sealed class ReloadLogger
    {
        private readonly List<string> _lines = [];

        public TaskCompletionSource ReloadFinished { get; private set; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        public TaskCompletionSource ReloadFailed { get; private set; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        public void RearmReloadSignals()
        {
            ReloadFinished = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            ReloadFailed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        }

        public ILogger<T> For<T>() => new Sink<T>(this);

        private void Add(string line)
        {
            lock (_lines)
            {
                _lines.Add(line);
            }

            if (line.Contains("Reconnect reload failed", StringComparison.Ordinal))
                ReloadFailed.TrySetResult();
        }

        public override string ToString()
        {
            lock (_lines)
            {
                return _lines.Count == 0
                    ? "  (the server logged nothing)"
                    : "  " + string.Join($"{Environment.NewLine}  ", _lines);
            }
        }

        private sealed class Sink<T>(ReloadLogger owner) : ILogger<T>
        {
            public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

            public bool IsEnabled(LogLevel logLevel) => true;

            public void Log<TState>(
                LogLevel logLevel,
                EventId eventId,
                TState state,
                Exception? exception,
                Func<TState, Exception?, string> formatter)
            {
                var message = formatter(state, exception);
                owner.Add(exception is null
                    ? $"[{logLevel}] {message}"
                    : $"[{logLevel}] {message} -> {exception.GetType().Name}: {exception.Message}");
            }
        }
    }
}

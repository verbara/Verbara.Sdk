using System.Collections.Concurrent;
using System.Diagnostics;
using Verbara.Sdk.Ami;
using Verbara.Sdk.Live.Channels;
using Verbara.Sdk.Live.Server;
using Verbara.Sdk.Live.Tests.Harness;
using Verbara.Sdk.Sessions;
using Verbara.Sdk.Sessions.Extensions;
using Verbara.Sdk.Sessions.Manager;
using FluentAssertions;
using FluentAssertions.Execution;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace Verbara.Sdk.Live.Tests.Server;

/// <summary>
/// <see cref="VerbaraServer.ReconcileChannelsAsync"/>: the channel part of a load and nothing else, held to the rules
/// of a load's channels. A channel the completed snapshot omits is removed as a reload removes it and ends its call with
/// no cause; one admitted while the snapshot is read is kept; a refused <c>Status</c> or an AMI session that ends during
/// the read reconciles nothing; the queues and the agents are neither asked for nor touched.
/// </summary>
/// <remarks>
/// <para>
/// Each test runs a real <see cref="Verbara.Sdk.Ami.Connection.AmiConnection"/> and a <see cref="VerbaraServer"/> through
/// <see cref="Run"/>, over a <see cref="BootingAsterisk"/> that has finished starting. The reconciliation is awaited, and
/// it removes and admits channels inline, before it returns, so what a test reads after the await is what it did.
/// </para>
/// <para>
/// Where a test reads <c>Debug</c> lines it builds its own server over the run's connection with a logger that keeps
/// every level, since the run's own logger keeps <c>Information</c> and above.
/// </para>
/// </remarks>
public sealed class VerbaraServerReconcileChannelsTests
{
    private const string ServerId = "srv-1";
    private const string StatusRefused = "[LIVE] Status refused";
    private const string StateLoaded = "[LIVE] State loaded";
    private const string ChannelsReconciled = "[LIVE] Channels reconciled";
    private const string ReconcileSpan = "live channel-reconcile";
    private const string StateLoadSpan = "live state-load";

    private static IReadOnlyList<StatusChannel> OneCallTwoLegs() =>
    [
        new StatusChannel("1700000000.1", "PJSIP/1001-00000001", LinkedId: "1700000000.1"),
        new StatusChannel("1700000000.2", "PJSIP/1002-00000002", LinkedId: "1700000000.1"),
    ];

    [Fact]
    public async Task ReconcileChannelsAsync_ShouldRemoveBothLegsByReloadAndEndTheCallOnceWithNoCause_WhenTheSnapshotListsNeither()
    {
        var peer = new BootingAsterisk { BootedAtLogin = true, StatusChannels = OneCallTwoLegs() };
        await using var run = await Run.ConnectAsync(peer);
        await using var manager = NewManager();
        var events = new DomainEvents();
        using var subscription = manager.Events.Subscribe(events);
        manager.AttachToServer(run.Server, ServerId);
        await run.StartServerAsync();
        var startedAtStart = events.Count<CallStartedEvent>();
        var removed = new ConcurrentQueue<AsteriskChannel>();
        run.Server.Channels.ChannelRemoved += removed.Enqueue;

        peer.StatusChannels = [];
        await run.Server.ReconcileChannelsAsync().AsTask().WaitAsync(Run.Bound);

        var endings = events.Of<CallEndedEvent>();
        using (new AssertionScope())
        {
            startedAtStart.Should().Be(1, "the start loaded both legs of one call, and the manager opened that call");
            removed.Select(c => c.UniqueId).Should().BeEquivalentTo(["1700000000.1", "1700000000.2"],
                "a completed snapshot that lists neither leg is evidence that both are gone");
            removed.Should().OnlyContain(c => c.RemovedByReload, "both carry the reload's provenance");
            run.Server.Channels.ChannelCount.Should().Be(0);
            endings.Should().ContainSingle("the call ends once, through the path consumers already observe");
            endings.Should().OnlyContain(e => e.Cause == null, "no hangup was observed, so the ending carries no cause");
            peer.Fault.Should().BeNull("the peer served the session without failing");
        }
    }

    [Fact]
    public async Task ReconcileChannelsAsync_ShouldAdmitTheChannelAsTheSnapshotReportsIt_WhenTheTableLacksIt()
    {
        var peer = new BootingAsterisk { BootedAtLogin = true, StatusChannels = OneCallTwoLegs() };
        await using var run = await Run.StartAsync(peer);
        var added = new ConcurrentQueue<AsteriskChannel>();
        run.Server.Channels.ChannelAdded += added.Enqueue;

        peer.StatusChannels =
        [
            .. OneCallTwoLegs(),
            new StatusChannel("1700000000.7", "PJSIP/1007-00000007", LinkedId: "1700000000.7", ChannelState: 4),
        ];
        await run.Server.ReconcileChannelsAsync().AsTask().WaitAsync(Run.Bound);

        using (new AssertionScope())
        {
            added.Select(c => c.UniqueId).Should().Equal(["1700000000.7"],
                "only the channel the table lacked is admitted; the two held legs are left as they are");
            added.Should().OnlyContain(c => c.AdmittedFromSnapshot, "it was admitted from a snapshot, not as a new channel");
            added.Should().OnlyContain(c => c.State == Verbara.Sdk.Enums.ChannelState.Ring,
                "it is admitted in the state the snapshot reports (4, ringing)");
            run.Server.Channels.ChannelCount.Should().Be(3);
            peer.Fault.Should().BeNull("the peer served the session without failing");
        }
    }

    [Fact]
    public async Task ReconcileChannelsAsync_ShouldAskOnlyStatusAndLeaveQueuesAndAgentsAsTheyAre_WhenTheServerHoldsBoth()
    {
        var peer = new BootingAsterisk { BootedAtLogin = true, StatusChannels = OneCallTwoLegs() };
        await using var run = await Run.StartAsync(peer);
        var queueBefore = run.Server.Queues.GetByName(BootingAsterisk.QueueName);
        var agentBefore = run.Server.Agents.GetById(BootingAsterisk.AgentId);
        var asksBefore = (Status: peer.Asked("Status"), Queues: peer.Asked("QueueStatus"), Agents: peer.Asked("Agents"));

        await run.Server.ReconcileChannelsAsync().AsTask().WaitAsync(Run.Bound);

        using (new AssertionScope())
        {
            queueBefore.Should().NotBeNull("the start loaded the peer's queue");
            agentBefore.Should().NotBeNull("the start loaded the peer's agent");
            (peer.Asked("Status") - asksBefore.Status).Should().Be(1, "the reconciliation asks for the channel snapshot once");
            (peer.Asked("QueueStatus") - asksBefore.Queues).Should().Be(0, "it does not ask for the queues");
            (peer.Asked("Agents") - asksBefore.Agents).Should().Be(0, "it does not ask for the agents");
            run.Server.Queues.QueueCount.Should().Be(1, "the queue held is still held");
            run.Server.Queues.GetByName(BootingAsterisk.QueueName).Should().BeSameAs(queueBefore,
                "no manager is cleared: the queue held is the same object");
            run.Server.Agents.AgentCount.Should().Be(1, "the agent held is still held");
            run.Server.Agents.GetById(BootingAsterisk.AgentId).Should().BeSameAs(agentBefore,
                "no manager is cleared: the agent held is the same object");
            run.Server.Channels.ChannelCount.Should().Be(2, "the snapshot listed both legs");
            peer.Fault.Should().BeNull("the peer served the session without failing");
        }
    }

    [Fact]
    public async Task ReconcileChannelsAsync_ShouldKeepTheChannelAndEndNoCall_WhenItIsAdmittedWhileTheSnapshotIsRead()
    {
        var peer = new BootingAsterisk { BootedAtLogin = true, StatusChannels = OneCallTwoLegs() };
        await using var run = await Run.ConnectAsync(peer);
        await using var manager = NewManager();
        var events = new DomainEvents();
        using var subscription = manager.Events.Subscribe(events);
        manager.AttachToServer(run.Server, ServerId);
        await run.StartServerAsync();
        var removed = new ConcurrentQueue<string>();
        run.Server.Channels.ChannelRemoved += channel => removed.Enqueue(channel.UniqueId);
        var answer = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        peer.StatusAnsweredAfter = answer.Task;
        try
        {
            var reconcile = run.Server.ReconcileChannelsAsync().AsTask();
            // The peer counts the Status before it answers, and the reconciliation opened its window before it sent it.
            var readOpen = await CompletesWithinBoundAsync(peer.AskedAtLeast("Status", 2));
            run.Server.Channels.OnNewChannel("1700000000.8", "PJSIP/1008-00000008", Verbara.Sdk.Enums.ChannelState.Ring,
                callerIdNum: "1008", context: "from-internal", linkedId: "1700000000.8");
            var startedDuringTheRead = events.Count<CallStartedEvent>();
            answer.SetResult();
            var outcome = await Record.ExceptionAsync(() => reconcile.WaitAsync(Run.Bound));

            using (new AssertionScope())
            {
                readOpen.Should().BeTrue("the reconciliation asked Status");
                startedDuringTheRead.Should().Be(2, "the start's call and the one that arrived during the read");
                outcome.Should().BeNull();
                removed.Should().BeEmpty(
                    "the snapshot was requested before the new channel was admitted, so its silence is no evidence");
                run.Server.Channels.GetByUniqueId("1700000000.8").Should().NotBeNull("the channel that arrived is kept");
                run.Server.Channels.ChannelCount.Should().Be(3);
                events.Count<CallEndedEvent>().Should().Be(0, "no call is ended for it, nor for the call the snapshot lists");
                peer.Fault.Should().BeNull("the peer served the session without failing");
            }
        }
        finally
        {
            answer.TrySetResult();
        }
    }

    [Fact]
    public async Task ReconcileChannelsAsync_ShouldKeepTheChannelEndNoCallAndNotThrow_WhenAsteriskRefusesStatus()
    {
        var peer = new BootingAsterisk { BootedAtLogin = true, StatusChannels = OneCallTwoLegs() };
        await using var run = await Run.ConnectAsync(peer);
        await using var manager = NewManager();
        var events = new DomainEvents();
        using var subscription = manager.Events.Subscribe(events);
        manager.AttachToServer(run.Server, ServerId);
        await run.StartServerAsync();
        var removed = new ConcurrentQueue<string>();
        run.Server.Channels.ChannelRemoved += channel => removed.Enqueue(channel.UniqueId);

        peer.StatusRefusedFromAsk = 2;
        var outcome = await Record.ExceptionAsync(
            () => run.Server.ReconcileChannelsAsync().AsTask().WaitAsync(Run.Bound));

        using (new AssertionScope())
        {
            outcome.Should().BeNull("a refused Status does not make the reconciliation throw");
            peer.Asked("Status").Should().Be(2, "the start's Status and the refused one");
            removed.Should().BeEmpty("a refusal is no evidence that a channel is gone");
            run.Server.Channels.ChannelCount.Should().Be(2, "both legs Asterisk still holds are still held");
            events.Count<CallEndedEvent>().Should().Be(0, "no call is ended on a refusal");
            ReadWindowRecord.OpenWindows(run.Server.Channels).Should().Be(0, "the refused read's window is closed");
            peer.Fault.Should().BeNull("the peer served the session without failing");
        }
    }

    [Fact]
    public async Task ReconcileChannelsAsync_ShouldWarnOfARefusalOncePerAmiSession_WhenItRunsTwiceInEachOfTwoSessions()
    {
        var first = new BootingAsterisk { BootedAtLogin = true, StatusChannels = OneCallTwoLegs(), StatusRefusedFromAsk = 2 };
        var second = new BootingAsterisk { BootedAtLogin = true, StatusChannels = OneCallTwoLegs(), StatusRefusedFromAsk = 2 };
        await using var run = await Run.ConnectAsync(first, second, autoReconnect: true);
        var log = new EveryLevelLog();
        await using var server = new VerbaraServer(run.Connection, log) { TimeProvider = run.Clock };
        await server.StartAsync().WaitAsync(Run.Bound);

        await server.ReconcileChannelsAsync().AsTask().WaitAsync(Run.Bound);
        await server.ReconcileChannelsAsync().AsTask().WaitAsync(Run.Bound);
        var inTheFirstSession = Refusals(log);

        run.ReleaseSecondPeer();
        first.CloseSession();
        var reconnected = await CompletesWithinBoundAsync(run.Reconnected);
        // The reload's Status is the second session's first, which the peer answers: it asks Agents only after it.
        var pastTheReload = await CompletesWithinBoundAsync(second.AskedAtLeast("Agents", 1));
        await server.ReconcileChannelsAsync().AsTask().WaitAsync(Run.Bound);
        await server.ReconcileChannelsAsync().AsTask().WaitAsync(Run.Bound);
        var all = Refusals(log);

        using (new AssertionScope())
        {
            inTheFirstSession.Select(r => r.Level).Should().Equal([LogLevel.Warning, LogLevel.Debug],
                "the first refusal in an AMI session is a Warning, and the one after it in the same session is Debug");
            reconnected.Should().BeTrue("the connection reconnected to the second session");
            pastTheReload.Should().BeTrue("the reload after the reconnect got past its Status");
            all.Select(r => r.Level).Should().Equal(
                [LogLevel.Warning, LogLevel.Debug, LogLevel.Warning, LogLevel.Debug],
                "a reconnect starts a new AMI session, whose first refusal is a Warning again");
            first.Asked("Status").Should().Be(3, "the start's Status, answered, and the two refused ones");
            second.Asked("Status").Should().Be(3, "the reload's Status, answered, and the two refused ones");
            server.Channels.ChannelCount.Should().Be(2, "no refusal removed a channel");
            first.Fault.Should().BeNull("the first peer served its session without failing");
            second.Fault.Should().BeNull("the second peer served its session without failing");
        }
    }

    [Fact]
    public async Task ReconcileChannelsAsync_ShouldReconcileNothingAndThrowNotConnected_WhenTheSessionEndsDuringTheRead()
    {
        var peer = new BootingAsterisk
        {
            BootedAtLogin = true,
            StatusChannels =
            [
                new StatusChannel("1700000000.1", "PJSIP/1001-00000001", LinkedId: "1700000000.1"),
                new StatusChannel("1700000000.2", "PJSIP/1002-00000002", LinkedId: "1700000000.2"),
            ],
        };
        await using var run = await Run.ConnectAsync(peer);
        await using var manager = NewManager();
        var events = new DomainEvents();
        using var subscription = manager.Events.Subscribe(events);
        manager.AttachToServer(run.Server, ServerId);
        await run.StartServerAsync();
        var removed = new ConcurrentQueue<string>();
        run.Server.Channels.ChannelRemoved += channel => removed.Enqueue(channel.UniqueId);

        peer.Close = PeerClose.DuringStatus;
        peer.StatusChannelsBeforeClose = 1;
        var outcome = await Record.ExceptionAsync(
            () => run.Server.ReconcileChannelsAsync().AsTask().WaitAsync(Run.Bound));

        using (new AssertionScope())
        {
            outcome.Should().BeOfType<AmiNotConnectedException>(
                "the reconciliation throws what a load throws when its session ends, and reconciles no partial snapshot");
            outcome?.Message.Should().Contain("live channel reconciliation", "the message names what was reading");
            removed.Should().BeEmpty("a snapshot the session ended before it completed removes nothing");
            run.Server.Channels.ChannelCount.Should().Be(2, "both channels are still held");
            events.Count<CallEndedEvent>().Should().Be(0, "no call is ended");
            ReadWindowRecord.OpenWindows(run.Server.Channels).Should().Be(0, "the ended read's window is closed");
            ReadWindowRecord.Departures(run.Server.Channels).Should().Be(0, "nothing the read recorded outlives it");
            peer.Asked("Status").Should().Be(2, "the start's snapshot and the reconciliation's");
            peer.Fault.Should().BeNull("the peer served the session without failing");
        }
    }

    [Fact]
    public async Task ReconcileChannelsAsync_ShouldReconcileNothingAndThrowCanceled_WhenTheTokenIsCancelledDuringTheRead()
    {
        var peer = new BootingAsterisk { BootedAtLogin = true, StatusChannels = OneCallTwoLegs() };
        await using var run = await Run.StartAsync(peer);
        var removed = new ConcurrentQueue<string>();
        run.Server.Channels.ChannelRemoved += channel => removed.Enqueue(channel.UniqueId);
        var answer = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        peer.StatusChannels = [];
        peer.StatusAnsweredAfter = answer.Task;
        using var cts = new CancellationTokenSource();
        try
        {
            var reconcile = run.Server.ReconcileChannelsAsync(cts.Token).AsTask();
            var readOpen = await CompletesWithinBoundAsync(peer.AskedAtLeast("Status", 2));
            await cts.CancelAsync();
            answer.SetResult();
            var outcome = await Record.ExceptionAsync(() => reconcile.WaitAsync(Run.Bound));

            using (new AssertionScope())
            {
                readOpen.Should().BeTrue("the reconciliation asked Status");
                outcome.Should().BeAssignableTo<OperationCanceledException>("a cancelled read is not a completed one");
                removed.Should().BeEmpty("a snapshot the token cut short removes nothing, though Asterisk listed none");
                run.Server.Channels.ChannelCount.Should().Be(2);
                ReadWindowRecord.OpenWindows(run.Server.Channels).Should().Be(0, "the cancelled read's window is closed");
                peer.Fault.Should().BeNull("the peer served the session without failing");
            }
        }
        finally
        {
            answer.TrySetResult();
        }
    }

    [Fact]
    public async Task ReconcileChannelsAsync_ShouldReconcileAsRequestInitialStateAsyncLoads_WhenTheServerWasNeverStarted()
    {
        var peer = new BootingAsterisk { BootedAtLogin = true, StatusChannels = OneCallTwoLegs() };
        await using var run = await Run.ConnectAsync(peer);

        var reconcileOutcome = await Record.ExceptionAsync(
            () => run.Server.ReconcileChannelsAsync().AsTask().WaitAsync(Run.Bound));
        var reconciled = run.Server.Channels.ChannelCount;
        var loadOutcome = await Record.ExceptionAsync(
            () => run.Server.RequestInitialStateAsync().AsTask().WaitAsync(Run.Bound));

        using (new AssertionScope())
        {
            reconcileOutcome.Should().BeNull("like RequestInitialStateAsync, it does not check that the server started");
            loadOutcome.Should().BeNull("RequestInitialStateAsync does not check it either");
            reconciled.Should().Be(2, "the reconciliation admitted both legs the snapshot listed");
            peer.Asked("Status").Should().Be(2, "the reconciliation's Status and the load's");
            peer.Asked("QueueStatus").Should().Be(1, "only the load asked for the queues");
            peer.Fault.Should().BeNull("the peer served the session without failing");
        }
    }

    [Fact]
    public async Task ReconcileChannelsAsync_ShouldReconcileAsRequestInitialStateAsyncLoads_WhenTheServerWasDisposed()
    {
        var peer = new BootingAsterisk { BootedAtLogin = true, StatusChannels = OneCallTwoLegs() };
        await using var run = await Run.ConnectAsync(peer);
        await using var server = new VerbaraServer(run.Connection, NullLogger<VerbaraServer>.Instance)
        {
            TimeProvider = run.Clock,
        };
        await server.StartAsync().WaitAsync(Run.Bound);
        await server.DisposeAsync();

        peer.StatusChannels = [];
        var reconcileOutcome = await Record.ExceptionAsync(
            () => server.ReconcileChannelsAsync().AsTask().WaitAsync(Run.Bound));
        var reconciled = server.Channels.ChannelCount;
        var loadOutcome = await Record.ExceptionAsync(
            () => server.RequestInitialStateAsync().AsTask().WaitAsync(Run.Bound));

        using (new AssertionScope())
        {
            reconcileOutcome.Should().BeNull("like RequestInitialStateAsync, it does not check that the server is disposed");
            loadOutcome.Should().BeNull("RequestInitialStateAsync does not check it either");
            reconciled.Should().Be(0, "the completed snapshot listed neither leg");
            peer.Asked("Status").Should().Be(3, "the start's, the reconciliation's and the load's");
            peer.Fault.Should().BeNull("the peer served the session without failing");
        }
    }

    [Fact]
    public async Task ReconcileChannelsAsync_ShouldThrowWhatRequestInitialStateAsyncThrows_WhenTheConnectionIsNotEstablished()
    {
        var peer = new BootingAsterisk { BootedAtLogin = true, StatusChannels = OneCallTwoLegs() };
        await using var run = await Run.ConnectAsync(peer);
        await run.Connection.DisconnectAsync().AsTask().WaitAsync(Run.Bound);

        var reconcileOutcome = await Record.ExceptionAsync(
            () => run.Server.ReconcileChannelsAsync().AsTask().WaitAsync(Run.Bound));
        var loadOutcome = await Record.ExceptionAsync(
            () => run.Server.RequestInitialStateAsync().AsTask().WaitAsync(Run.Bound));

        using (new AssertionScope())
        {
            loadOutcome.Should().BeOfType<AmiNotConnectedException>("a load on a connection not established throws it");
            reconcileOutcome.Should().BeOfType<AmiNotConnectedException>("the reconciliation throws the same");
            run.Server.Channels.ChannelCount.Should().Be(0, "nothing was reconciled");
        }
    }

    [Fact]
    public async Task ReconcileChannelsAsync_ShouldTraceItsOwnSpanAndLogAtDebug_WhenItReconciles()
    {
        var peer = new BootingAsterisk { BootedAtLogin = true, StatusChannels = OneCallTwoLegs() };
        await using var run = await Run.ConnectAsync(peer);
        var log = new EveryLevelLog();
        await using var server = new VerbaraServer(run.Connection, log) { TimeProvider = run.Clock };
        await server.StartAsync().WaitAsync(Run.Bound);
        var linesBefore = log.Entries.Count;

        using var testSource = new ActivitySource("Verbara.Sdk.Live.Tests.ReconcileChannels");
        var stopped = new ConcurrentQueue<Activity>();
        using var listener = new ActivityListener
        {
            ShouldListenTo = source => source.Name is "Verbara.Sdk.Live" or "Verbara.Sdk.Live.Tests.ReconcileChannels",
            Sample = (ref ActivityCreationOptions<ActivityContext> _) => ActivitySamplingResult.AllDataAndRecorded,
            ActivityStopped = stopped.Enqueue,
        };
        ActivitySource.AddActivityListener(listener);

        string parentId;
        using (var parent = testSource.StartActivity("reconcile-channels test"))
        {
            parentId = parent?.Id ?? throw new InvalidOperationException("The test's parent activity was not sampled.");
            await server.ReconcileChannelsAsync().AsTask().WaitAsync(Run.Bound);
            peer.StatusRefusedFromAsk = 3;
            await server.ReconcileChannelsAsync().AsTask().WaitAsync(Run.Bound);
        }

        var mine = stopped.Where(a => a.ParentId == parentId).ToList();
        var lines = log.Entries.Skip(linesBefore).ToList();
        using (new AssertionScope())
        {
            mine.Select(a => a.OperationName).Should().Equal([ReconcileSpan, ReconcileSpan],
                "each run is traced as a channel reconciliation, never as a state load");
            mine.Should().OnlyContain(a => a.Kind == ActivityKind.Client);
            mine.Should().OnlyContain(a => Equals(a.GetTagItem("live.channels"), 2), "both legs are held after each run");
            mine.Select(a => a.GetTagItem("live.status.refused")).Should().Equal([null, BootingAsterisk.PermissionDenied],
                "the refused run's trace carries Asterisk's message, the answered one none");
            mine.Should().NotContain(a => a.OperationName == StateLoadSpan);
            lines.Should().NotContain(e => e.Line.StartsWith(StateLoaded, StringComparison.Ordinal),
                "no Information state-load line is written");
            lines.Where(e => e.Line.StartsWith(ChannelsReconciled, StringComparison.Ordinal))
                .Select(e => (e.Level, e.Line)).Should().Equal(
                [
                    (LogLevel.Debug, $"{ChannelsReconciled}: channels=2 status_refused=False"),
                    (LogLevel.Debug, $"{ChannelsReconciled}: channels=2 status_refused=True"),
                ], "one Debug result line per run");
            peer.Fault.Should().BeNull("the peer served the session without failing");
        }
    }

    private static List<(LogLevel Level, string Line)> Refusals(EveryLevelLog log) =>
        [.. log.Entries.Where(e => e.Line.StartsWith(StatusRefused, StringComparison.Ordinal))];

    private static CallSessionManager NewManager() =>
        new(Options.Create(new SessionOptions()), NullLogger<CallSessionManager>.Instance, new TestStore());

    private static async Task<bool> CompletesWithinBoundAsync(Task task)
    {
        try
        {
            await task.WaitAsync(Run.Bound);
            return true;
        }
        catch (TimeoutException)
        {
            return false;
        }
    }

    /// <summary>A logger that keeps every line at every level, in order.</summary>
    private sealed class EveryLevelLog : ILogger<VerbaraServer>
    {
        private readonly Lock _gate = new();
        private readonly List<(LogLevel Level, string Line)> _entries = [];

        public IReadOnlyList<(LogLevel Level, string Line)> Entries
        {
            get
            {
                lock (_gate)
                {
                    return [.. _entries];
                }
            }
        }

        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception,
            Func<TState, Exception?, string> formatter)
        {
            var line = formatter(state, exception);
            lock (_gate)
            {
                _entries.Add((logLevel, line));
            }
        }
    }

    /// <summary>The manager's domain events, kept in the order it raised them.</summary>
    private sealed class DomainEvents : IObserver<SessionDomainEvent>
    {
        private readonly ConcurrentQueue<SessionDomainEvent> _events = new();

        public int Count<T>() where T : SessionDomainEvent => _events.OfType<T>().Count();

        public IReadOnlyList<T> Of<T>() where T : SessionDomainEvent => [.. _events.OfType<T>()];

        public void OnNext(SessionDomainEvent value) => _events.Enqueue(value);

        public void OnError(Exception error)
        {
        }

        public void OnCompleted()
        {
        }
    }

    /// <summary>A store that keeps the last save of each call; the tests read the manager's events, not the store.</summary>
    private sealed class TestStore : SessionStoreBase
    {
        private readonly ConcurrentDictionary<string, CallSession> _sessions = new(StringComparer.Ordinal);

        public override ValueTask SaveAsync(CallSession session, CancellationToken ct)
        {
            _sessions[session.SessionId] = session;
            return ValueTask.CompletedTask;
        }

        public override ValueTask<CallSession?> GetAsync(string sessionId, CancellationToken ct) =>
            ValueTask.FromResult(_sessions.GetValueOrDefault(sessionId));
    }
}

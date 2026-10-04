using System.Collections.Concurrent;
using System.Globalization;
using System.Runtime.CompilerServices;
using Verbara.Sdk.Ami.Actions;
using Verbara.Sdk.Ami.Events;
using Verbara.Sdk.Enums;
using Verbara.Sdk.Live.Server;
using Verbara.Sdk.Sessions;
using Verbara.Sdk.Sessions.Extensions;
using Verbara.Sdk.Sessions.Manager;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using NSubstitute;

namespace Verbara.Sdk.Hosting.Tests;

/// <summary>
/// A host shaped as a consumer builds it — <c>AddVerbara</c> and then <c>AddVerbaraSessions</c> or
/// <c>AddVerbaraSessionsBuilder</c> — starting while Asterisk already has calls. The server's first load reads the
/// <c>Status</c> snapshot and announces every channel it lists; the session engine has to hear that announcement, or
/// the calls stay invisible to it until the next reconnect.
/// </summary>
/// <remarks>
/// The AMI connection is a substitute registered before <c>AddVerbara</c>, as in <see cref="SessionsHostStartTests"/>;
/// it answers <c>Status</c> and <c>QueueStatus</c> from the scripted Asterisk below and every other request with
/// nothing. Events "during the start" are delivered through the observer the server subscribed, from inside the
/// <c>Status</c> answer before it completes — the read window the load holds open.
/// </remarks>
public sealed class CallsUpAtHostStartTests
{
    /// <summary>Bounds the host's start and stop, so a hang fails instead of stalling the lane.</summary>
    private static readonly TimeSpan Bound = TimeSpan.FromSeconds(30);

    private const string Queue = "sales";

    public enum Registration
    {
        AddVerbaraSessions,
        AddVerbaraSessionsBuilder,
    }

    // --- (a) calls already up at start each have one session ------------------------------------------------------

    [Theory]
    [InlineData(Registration.AddVerbaraSessions, 1)]
    [InlineData(Registration.AddVerbaraSessions, 10)]
    [InlineData(Registration.AddVerbaraSessionsBuilder, 1)]
    [InlineData(Registration.AddVerbaraSessionsBuilder, 10)]
    public async Task HostStartAsync_ShouldOpenOneReloadSessionPerCall_WhenCallsAreAlreadyUpAtStart(
        Registration registration, int calls)
    {
        var asterisk = new ScriptedAsterisk();
        var expected = Enumerable.Range(0, calls).Select(asterisk.AddAnsweredCall).ToList();
        using var host = Build(registration, asterisk);
        var manager = host.Services.GetRequiredService<ICallSessionManager>();
        using var seen = Recorder.On(manager);

        await host.StartAsync().WaitAsync(Bound);
        try
        {
            var sessions = manager.ActiveSessions.ToList();
            sessions.Select(s => s.LinkedId).Should().BeEquivalentTo(expected,
                "every call Asterisk reported up at start has exactly one session, one per linkedid. "
                + $"Measured: {Describe(sessions, seen)}");
            sessions.Should().OnlyContain(s => s.State == CallSessionState.Connected,
                "the snapshot reported every leg Up, and a reloaded call opens in the state Asterisk reported");
            sessions.Should().OnlyContain(s => s.Metadata.GetValueOrDefault("origin") == "reload",
                "the SDK did not see these calls start, and says so on each session");
            seen.Count<CallStartedEvent>().Should().Be(calls,
                "a subscriber taken before the start is told about each call once. "
                + $"Measured: {Describe(sessions, seen)}");
        }
        finally
        {
            await host.StopAsync().WaitAsync(Bound);
        }
    }

    // --- (b) those calls end through the ordinary ending path -----------------------------------------------------

    [Theory]
    [InlineData(Registration.AddVerbaraSessions, 1)]
    [InlineData(Registration.AddVerbaraSessions, 10)]
    [InlineData(Registration.AddVerbaraSessionsBuilder, 1)]
    [InlineData(Registration.AddVerbaraSessionsBuilder, 10)]
    public async Task HostStartAsync_ShouldEndEveryReloadSessionOnce_WhenTheCallsUpAtStartHangUp(
        Registration registration, int calls)
    {
        var asterisk = new ScriptedAsterisk();
        foreach (var i in Enumerable.Range(0, calls))
            asterisk.AddAnsweredCall(i);
        using var host = Build(registration, asterisk);
        var manager = host.Services.GetRequiredService<ICallSessionManager>();
        using var seen = Recorder.On(manager);

        await host.StartAsync().WaitAsync(Bound);
        try
        {
            asterisk.HangUpEverything();

            // The count of endings, not only the empty set: with nothing opened the set is empty too.
            seen.Count<CallEndedEvent>().Should().Be(calls,
                "every call up at start had a session, and each ends once when its legs hang up. "
                + $"Measured: {Describe(manager.ActiveSessions.ToList(), seen)}");
            manager.ActiveSessions.Should().BeEmpty("no call is left in progress after Asterisk hung them all up");
        }
        finally
        {
            await host.StopAsync().WaitAsync(Bound);
        }
    }

    // --- (e) control: nothing up at start, nothing opened ---------------------------------------------------------

    [Theory]
    [InlineData(Registration.AddVerbaraSessions)]
    [InlineData(Registration.AddVerbaraSessionsBuilder)]
    public async Task HostStartAsync_ShouldOpenNoSession_WhenNoCallIsUpAtStart(Registration registration)
    {
        var asterisk = new ScriptedAsterisk();
        using var host = Build(registration, asterisk);
        var manager = host.Services.GetRequiredService<ICallSessionManager>();
        using var seen = Recorder.On(manager);

        await host.StartAsync().WaitAsync(Bound);
        try
        {
            manager.ActiveSessions.Should().BeEmpty("an empty snapshot reports no call to open");
            seen.Count<CallStartedEvent>().Should().Be(0);
        }
        finally
        {
            await host.StopAsync().WaitAsync(Bound);
        }
    }

    // --- (f) a call that starts while the host is starting -------------------------------------------------------

    [Theory]
    [InlineData(Registration.AddVerbaraSessions)]
    [InlineData(Registration.AddVerbaraSessionsBuilder)]
    public async Task HostStartAsync_ShouldOpenOneSession_WhenACallStartsWhileTheLoadIsReading(
        Registration registration)
    {
        var asterisk = new ScriptedAsterisk();
        const string uniqueId = "1700000100.1";
        asterisk.DuringFirstStatus = observer => observer.OnNext(new NewChannelEvent
        {
            UniqueId = uniqueId,
            Channel = "Local/born@lab-00000100;1",
            ChannelState = "6",
            Linkedid = uniqueId,
            Context = "lab",
            Exten = "born",
        });
        using var host = Build(registration, asterisk);
        var manager = host.Services.GetRequiredService<ICallSessionManager>();
        using var seen = Recorder.On(manager);

        await host.StartAsync().WaitAsync(Bound);
        try
        {
            asterisk.FirstStatusDelivered.Should().BeTrue("the harness delivered the Newchannel inside the read");
            manager.ActiveSessions.Select(s => s.LinkedId).Should().Equal([uniqueId],
                "a call that starts while the host is starting has exactly one session. "
                + $"Measured: {Describe(manager.ActiveSessions.ToList(), seen)}");
            seen.Count<CallStartedEvent>().Should().Be(1, "and exactly one call-started notification");
        }
        finally
        {
            await host.StopAsync().WaitAsync(Bound);
        }
    }

    // --- (g) a call that ends while the host is starting ---------------------------------------------------------

    [Theory]
    [InlineData(Registration.AddVerbaraSessions)]
    [InlineData(Registration.AddVerbaraSessionsBuilder)]
    public async Task HostStartAsync_ShouldLeaveNothingOpen_WhenACallUpAtStartEndsWhileTheLoadIsReading(
        Registration registration)
    {
        var asterisk = new ScriptedAsterisk();
        asterisk.AddAnsweredCall(0);
        asterisk.DuringFirstStatus = _ => asterisk.HangUpEverything();
        using var host = Build(registration, asterisk);
        var manager = host.Services.GetRequiredService<ICallSessionManager>();
        using var seen = Recorder.On(manager);

        await host.StartAsync().WaitAsync(Bound);
        try
        {
            asterisk.FirstStatusDelivered.Should().BeTrue("the harness delivered the Hangups inside the read");
            manager.ActiveSessions.Should().BeEmpty(
                "a call Asterisk no longer has leaves no session in progress after the start. "
                + $"Measured: {Describe(manager.ActiveSessions.ToList(), seen)}");
            var started = seen.Of<CallStartedEvent>().Select(e => e.SessionId).ToList();
            var ended = seen.Of<CallEndedEvent>().Select(e => e.SessionId).ToList();
            ended.Should().BeEquivalentTo(started,
                "every call-started notification is followed by exactly one ending, and no ending is raised for a "
                + "session that was never announced");
        }
        finally
        {
            await host.StopAsync().WaitAsync(Bound);
        }
    }

    // --- (j) a call already waiting in a queue at start ----------------------------------------------------------

    [Theory]
    [InlineData(Registration.AddVerbaraSessions)]
    [InlineData(Registration.AddVerbaraSessionsBuilder)]
    public async Task HostStartAsync_ShouldOpenTheQueueVisitFromTheReportedWait_WhenACallIsWaitingInAQueueAtStart(
        Registration registration)
    {
        var asterisk = new ScriptedAsterisk();
        var (linkedId, channel) = asterisk.AddAnsweredCaller(0);
        asterisk.QueueEntries.Add(new QueueEntryEvent
        {
            Queue = Queue,
            Channel = channel,
            Uniqueid = linkedId,
            CallerIDNum = "5552100",
            Position = 1,
            Wait = 5,
        });
        using var host = Build(registration, asterisk);
        var manager = host.Services.GetRequiredService<ICallSessionManager>();
        // Resolved before the start, as a consumer that reads queue metrics holds it: it counts from the events.
        var tracker = host.Services.GetRequiredService<IQueueSessionTracker>();
        using var seen = Recorder.On(manager);

        var before = DateTimeOffset.UtcNow;
        await host.StartAsync().WaitAsync(Bound);
        var after = DateTimeOffset.UtcNow;
        try
        {
            var session = manager.ActiveSessions.Should().ContainSingle(
                "the caller up at start has one session. "
                + $"Measured: {Describe(manager.ActiveSessions.ToList(), seen)}").Subject;
            session.State.Should().Be(CallSessionState.Connected,
                "the reported state stays: Connected → Queued is not a transition of the session's state table");
            session.Metadata.GetValueOrDefault("origin").Should().Be("reload");
            session.QueueName.Should().Be(Queue, "the queue snapshot reported the caller waiting in it");
            var queued = seen.Of<CallQueuedEvent>().Should().ContainSingle(
                "the visit the snapshot reported is opened and announced once").Subject;
            queued.Timestamp.Should().BeOnOrAfter(before.AddSeconds(-6)).And.BeOnOrBefore(after.AddSeconds(-4),
                "the visit starts when Asterisk says the caller joined: now minus the reported 5 s wait, within ±1 s");
            tracker.GetByQueueName(Queue)?.CallsOffered.Should().Be(1, "the queue counts the visit as one offer");
        }
        finally
        {
            await host.StopAsync().WaitAsync(Bound);
        }
    }

    [Theory]
    [InlineData(Registration.AddVerbaraSessions)]
    [InlineData(Registration.AddVerbaraSessionsBuilder)]
    public async Task HostStartAsync_ShouldOpenTheSessionButNoQueueVisit_WhenTheQueueSnapshotListsNoCaller(
        Registration registration)
    {
        // Control for (j): the same answer with the QueueEntry removed. A harness that queued every session would
        // pass (j) for the wrong reason; this keeps the queue visit tied to the entry.
        var asterisk = new ScriptedAsterisk();
        asterisk.AddAnsweredCaller(0);
        using var host = Build(registration, asterisk);
        var manager = host.Services.GetRequiredService<ICallSessionManager>();
        using var seen = Recorder.On(manager);

        await host.StartAsync().WaitAsync(Bound);
        try
        {
            seen.Count<CallQueuedEvent>().Should().Be(0, "no caller was reported waiting");
            manager.ActiveSessions.Should().ContainSingle(
                "the snapshot still reported the call up. "
                + $"Measured: {Describe(manager.ActiveSessions.ToList(), seen)}")
                .Which.QueueName.Should().BeNull();
        }
        finally
        {
            await host.StopAsync().WaitAsync(Bound);
        }
    }

    // --- (c) and (h): the hosted service driven on its own --------------------------------------------------------

    [Fact]
    public async Task StartAsync_ShouldAttachOnce_WhenTheServiceIsStartedOnItsOwn()
    {
        await using var rig = DirectRig.Create();
        var sut = new SessionManagerHostedService(rig.Manager, rig.Server);

        await sut.StartAsync(CancellationToken.None);
        rig.Server.Channels.OnNewChannel("uid-1", "PJSIP/100-001", ChannelState.Ring, linkedId: "linked-1");

        rig.Manager.ActiveSessions.Should().ContainSingle("a start invoked alone still attaches the manager");
        rig.Seen.Count<CallStartedEvent>().Should().Be(1, "attached once, so the channel is announced once");
        await sut.StopAsync(CancellationToken.None);
        sut.Dispose();
    }

    [Fact]
    public async Task StartAsync_ShouldAttachAgain_WhenTheServiceIsStartedAfterAStop()
    {
        await using var rig = DirectRig.Create();
        var sut = new SessionManagerHostedService(rig.Manager, rig.Server);

        await sut.StartAsync(CancellationToken.None);
        await sut.StopAsync(CancellationToken.None);
        await sut.StartAsync(CancellationToken.None);
        rig.Server.Channels.OnNewChannel("uid-1", "PJSIP/100-001", ChannelState.Ring, linkedId: "linked-1");

        rig.Manager.ActiveSessions.Should().ContainSingle(
            "the stop detached the manager, so the start after it attaches it again");
        rig.Seen.Count<CallStartedEvent>().Should().Be(1, "attached once, not twice");
        await sut.StopAsync(CancellationToken.None);
        sut.Dispose();
    }

    // --- the harness -----------------------------------------------------------------------------------------------

    private static IHost Build(Registration registration, ScriptedAsterisk asterisk) =>
        new HostBuilder()
            .ConfigureServices(services =>
            {
                services.AddSingleton(asterisk.Connection);
                services.AddVerbara(o =>
                {
                    o.Ami.Hostname = "localhost";
                    o.Ami.Username = "admin";
                    o.Ami.Password = "secret";
                    o.AgiPort = 0;
                });
                if (registration == Registration.AddVerbaraSessions)
                    services.AddVerbaraSessions();
                else
                    services.AddVerbaraSessionsBuilder();
            })
            .Build();

    private static string Describe(List<CallSession> sessions, Recorder seen) =>
        string.Create(CultureInfo.InvariantCulture,
            $"sessions={sessions.Count} [{string.Join(", ", sessions.Select(s => $"{s.LinkedId}:{s.State}:{s.Metadata.GetValueOrDefault("origin")}"))}], "
            + $"started={seen.Count<CallStartedEvent>()}, ended={seen.Count<CallEndedEvent>()}, queued={seen.Count<CallQueuedEvent>()}");

    /// <summary>
    /// The Asterisk the host talks to: the channels it reports on <c>Status</c>, the callers it reports on
    /// <c>QueueStatus</c>, and the observer the server subscribed, through which live events reach it.
    /// </summary>
    private sealed class ScriptedAsterisk
    {
        private readonly List<StatusEvent> _channels = [];
        private readonly Lock _gate = new();
        private IObserver<ManagerEvent>? _observer;
        private int _statusAnswers;

        public ScriptedAsterisk()
        {
            Connection = Substitute.For<IAmiConnection>();
            Connection.AsteriskVersion.Returns("22.9.0");
            Connection.State.Returns(AmiConnectionState.Connected);
            Connection.Subscribe(Arg.Do<IObserver<ManagerEvent>>(obs => _observer = obs))
                .Returns(_ => Substitute.For<IDisposable>());
            Connection
                .SendEventGeneratingActionAsync(Arg.Any<ManagerAction>(), Arg.Any<CancellationToken>())
                .Returns(call => call.Arg<ManagerAction>() switch
                {
                    StatusAction => AnswerStatus(),
                    QueueStatusAction => AnswerQueueStatus(),
                    _ => Answer([]),
                });
        }

        public IAmiConnection Connection { get; }

        public List<QueueEntryEvent> QueueEntries { get; } = [];

        /// <summary>Run once, inside the first <c>Status</c> answer, after its channels and before it completes.</summary>
        public Action<IObserver<ManagerEvent>>? DuringFirstStatus { get; set; }

        public bool FirstStatusDelivered { get; private set; }

        /// <summary>One answered call of two legs sharing a linkedid; returns the linkedid.</summary>
        public string AddAnsweredCall(int index)
        {
            var linkedId = string.Create(CultureInfo.InvariantCulture, $"1700000000.{index * 2}");
            var name = string.Create(CultureInfo.InvariantCulture, $"Local/up{index}@lab-{index:x8}");
            AddChannel(linkedId, name + ";1", linkedId);
            AddChannel(string.Create(CultureInfo.InvariantCulture, $"1700000000.{(index * 2) + 1}"), name + ";2", linkedId);
            return linkedId;
        }

        /// <summary>One answered caller of one leg (its linkedid is its uniqueid); returns both and the name.</summary>
        public (string UniqueId, string Channel) AddAnsweredCaller(int index)
        {
            var uniqueId = string.Create(CultureInfo.InvariantCulture, $"1700000200.{index}");
            var name = string.Create(CultureInfo.InvariantCulture, $"PJSIP/caller-{index:x8}");
            AddChannel(uniqueId, name, uniqueId);
            return (uniqueId, name);
        }

        /// <summary>Asterisk hangs up every channel it holds: a <c>Hangup</c> per leg, and <c>Status</c> lists none.</summary>
        public void HangUpEverything()
        {
            List<StatusEvent> gone;
            lock (_gate)
            {
                gone = [.. _channels];
                _channels.Clear();
            }
            foreach (var channel in gone)
                _observer!.OnNext(new HangupEvent { UniqueId = channel.UniqueId, Channel = channel.Channel, Cause = 16 });
        }

        private void AddChannel(string uniqueId, string name, string linkedId)
        {
            lock (_gate)
            {
                _channels.Add(new StatusEvent
                {
                    UniqueId = uniqueId,
                    Channel = name,
                    LinkedId = linkedId,
                    Extension = "up",
                    RawFields = new Dictionary<string, string>(StringComparer.Ordinal)
                    {
                        ["ChannelState"] = "6",       // AST_STATE_UP
                        ["ChannelStateDesc"] = "Up",
                        ["CallerIDNum"] = "5552100",
                        ["Context"] = "lab",
                    },
                });
            }
        }

        private async IAsyncEnumerable<ManagerEvent> AnswerStatus(
            [EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            await Task.Yield();
            cancellationToken.ThrowIfCancellationRequested();
            List<StatusEvent> listed;
            lock (_gate)
                listed = [.. _channels];
            foreach (var channel in listed)
                yield return channel;

            if (Interlocked.Increment(ref _statusAnswers) == 1 && DuringFirstStatus is { } during)
            {
                during(_observer!);
                FirstStatusDelivered = true;
            }

            yield return new StatusCompleteEvent();
        }

        private IAsyncEnumerable<ManagerEvent> AnswerQueueStatus()
        {
            ManagerEvent[] events = QueueEntries.Count == 0
                ? [new QueueParamsEvent { Queue = Queue }, new QueueStatusCompleteEvent()]
                : [new QueueParamsEvent { Queue = Queue, Calls = QueueEntries.Count }, .. QueueEntries, new QueueStatusCompleteEvent()];
            return Answer(events);
        }

        private static async IAsyncEnumerable<ManagerEvent> Answer(
            ManagerEvent[] events, [EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            await Task.Yield();
            cancellationToken.ThrowIfCancellationRequested();
            foreach (var evt in events)
                yield return evt;
        }
    }

    /// <summary>Every event the session engine published to a subscriber taken when this was created.</summary>
    private sealed class Recorder : IObserver<SessionDomainEvent>, IDisposable
    {
        private readonly ConcurrentQueue<SessionDomainEvent> _seen = new();
        private IDisposable? _subscription;

        public static Recorder On(ICallSessionManager manager)
        {
            var recorder = new Recorder();
            recorder._subscription = manager.Events.Subscribe(recorder);
            return recorder;
        }

        public IEnumerable<T> Of<T>() where T : SessionDomainEvent => _seen.OfType<T>();

        public int Count<T>() where T : SessionDomainEvent => _seen.OfType<T>().Count();

        public void OnNext(SessionDomainEvent value) => _seen.Enqueue(value);

        public void OnError(Exception error) { }

        public void OnCompleted() { }

        public void Dispose() => _subscription?.Dispose();
    }

    /// <summary>A server and a manager with no host, for driving the hosted service's lifecycle calls by hand.</summary>
    private sealed class DirectRig : IAsyncDisposable
    {
        private DirectRig(VerbaraServer server, CallSessionManager manager)
        {
            Server = server;
            Manager = manager;
            Seen = Recorder.On(manager);
        }

        public VerbaraServer Server { get; }

        public CallSessionManager Manager { get; }

        public Recorder Seen { get; }

        public static DirectRig Create()
        {
            var connection = Substitute.For<IAmiConnection>();
            connection.AsteriskVersion.Returns("22.9.0");
            var server = new VerbaraServer(connection, NullLogger<VerbaraServer>.Instance);
            var manager = new CallSessionManager(
                Options.Create(new SessionOptions()), NullLogger<CallSessionManager>.Instance, new NoStore());
            return new DirectRig(server, manager);
        }

        public async ValueTask DisposeAsync()
        {
            Seen.Dispose();
            await Manager.DisposeAsync();
            await Server.DisposeAsync();
        }
    }

    private sealed class NoStore : SessionStoreBase
    {
        public override ValueTask SaveAsync(CallSession session, CancellationToken ct) => ValueTask.CompletedTask;

        public override ValueTask<CallSession?> GetAsync(string sessionId, CancellationToken ct) =>
            ValueTask.FromResult<CallSession?>(null);
    }
}

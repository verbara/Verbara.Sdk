using System.Collections.Concurrent;
using System.Diagnostics.CodeAnalysis;
using System.Globalization;
using FluentAssertions;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using NSubstitute;
using Verbara.Sdk.Enums;
using Verbara.Sdk.Live.Server;
using Verbara.Sdk.Sessions.Internal;
using Verbara.Sdk.Sessions.Manager;

namespace Verbara.Sdk.Sessions.Tests.Manager;

/// <summary>
/// Binds the report the session manager makes when a leg from one server carries a channel id or a
/// <c>linkedid</c> that its shared indexes already hold for a call of another server: one Warning per
/// unordered server pair per manager, Debug for every later collision of that pair, at most one entry
/// per arrival, and nothing at all for ids that differ across servers or that one server reuses.
///
/// <para>The manager keeps one channel-id index and one <c>linkedid</c> index for every server attached
/// to it, and Asterisk channel ids are unique across servers only when each Asterisk has its own
/// <c>systemname</c>. Every server here is a <see cref="VerbaraServer"/> over a substituted AMI
/// connection, and each leg is announced through its channel table on the test's thread, so every step
/// but the concurrency smoke test is synchronous. Entries are selected by the two event names the
/// report uses, never by level alone.</para>
/// </summary>
[SuppressMessage("Reliability", "CA1001:Types that own disposable fields should be disposable", Justification = "Disposed via IAsyncLifetime")]
public sealed class CallSessionManagerServerCollisionTests : IAsyncLifetime
{
    private const string CollisionEvent = "LogCrossServerCollision";
    private const string CollisionAgainEvent = "LogCrossServerCollisionAgain";

    /// <summary>Bound on the class cleanup, so a hang there fails the test instead of stalling the lane.</summary>
    private static readonly TimeSpan CleanupBound = TimeSpan.FromSeconds(30);

    private static readonly int[] ExpectedCallSizes = [2, 1];
    private static readonly string[] SharedCalls = ["1700000000.1", "1700000000.2", "1700000000.3"];
    private static readonly string[] ExpectedOpeners = ["a", "a", "b"];
    private static readonly string[] ExpectedOwnLegs = ["X"];

    private readonly RecordingLogger _logger = new();
    private readonly CallSessionManager _sut;
    private readonly List<VerbaraServer> _servers = [];
    private readonly ConcurrentQueue<SessionDomainEvent> _events = new();
    private readonly IDisposable _subscription;

    public CallSessionManagerServerCollisionTests()
    {
        _sut = new CallSessionManager(Options.Create(new SessionOptions()), _logger, new InMemorySessionStore());
        _subscription = _sut.Events.Subscribe(_events.Enqueue);
    }

    public Task InitializeAsync() => Task.CompletedTask;

    public Task DisposeAsync() => ReleaseAsync().WaitAsync(CleanupBound);

    private async Task ReleaseAsync()
    {
        _subscription.Dispose();
        await _sut.DisposeAsync();
        foreach (var server in _servers)
            await server.DisposeAsync();
    }

    /// <summary>A fresh server over a substituted AMI connection, attached to the manager under <paramref name="serverId"/>.</summary>
    private VerbaraServer Attach(string serverId)
    {
        var connection = Substitute.For<IAmiConnection>();
        connection.AsteriskVersion.Returns("21.0.0");
        var server = new VerbaraServer(connection, NullLogger<VerbaraServer>.Instance);
        _servers.Add(server);
        _sut.AttachToServer(server, serverId);
        return server;
    }

    /// <summary>
    /// Announces a leg. With no <paramref name="linkedId"/> the leg is a call's first leg, whose
    /// <c>linkedid</c> Asterisk sets to its own channel id.
    /// </summary>
    private static void Leg(VerbaraServer server, string uniqueId, string? linkedId = null) =>
        server.Channels.OnNewChannel(uniqueId, "PJSIP/trunk-" + uniqueId, ChannelState.Up,
            context: "from-trunk", linkedId: linkedId ?? uniqueId);

    private List<LogEntry> Collisions(string eventName) =>
        _logger.Entries.Where(e => e.EventId.Name == eventName).ToList();

    private int CollisionEntries => Collisions(CollisionEvent).Count + Collisions(CollisionAgainEvent).Count;

    private CallSession SessionOf(string linkedId) =>
        _sut.GetByLinkedId(linkedId)
            ?? throw new InvalidOperationException($"premise: a session is indexed under linkedid {linkedId}");

    [Fact]
    public void OnChannelAdded_ShouldLogOneWarningNamingBothServersTheIdAndBothCauses_WhenASecondServerReportsAHeldChannelId()
    {
        var a = Attach("a");
        var b = Attach("b");

        Leg(a, "1700000000.1");
        Leg(b, "1700000000.1");

        var warnings = Collisions(CollisionEvent);
        warnings.Should().ContainSingle(
            "the first collision between two servers is reported once at Warning; this is the path where the "
            + "second server's first leg finds the held call by its linkedid and is taken for a leg already "
            + "present, so without systemname that server's call is lost without a word");
        var warning = warnings[0];
        new
        {
            warning.Level,
            Servers = new[] { warning.State["ServerId"], warning.State["OtherServerId"] }.Order().ToArray(),
            Id = warning.State["Id"],
            NamesSystemname = warning.Message.Contains("systemname", StringComparison.Ordinal),
            NamesSecondCause = warning.Message.Contains("attach it once", StringComparison.Ordinal),
        }.Should().BeEquivalentTo(
            new
            {
                Level = LogLevel.Warning,
                Servers = new object?[] { "a", "b" },
                Id = (object?)"1700000000.1",
                NamesSystemname = true,
                NamesSecondCause = true,
            },
            "the entry names both servers and the id, and tells the operator both causes: Asterisk servers "
            + "without their own systemname, or two server ids attached to one Asterisk");
    }

    [Fact]
    public void OnChannelAdded_ShouldLogLaterCollisionsOfThePairAtDebugInEitherDirection_WhenThePairWasAlreadyWarned()
    {
        var a = Attach("a");
        var b = Attach("b");
        Leg(a, "1700000000.1");
        Leg(b, "1700000000.1");

        Leg(a, "1700000000.2");
        Leg(b, "1700000000.2");
        Leg(b, "1700000000.3");
        Leg(a, "1700000000.3");

        new
        {
            Warnings = Collisions(CollisionEvent).Count,
            Debugs = Collisions(CollisionAgainEvent).Count(e => e.Level == LogLevel.Debug),
        }.Should().BeEquivalentTo(
            new { Warnings = 1, Debugs = 2 },
            "{a, b} and {b, a} are one pair: once it was warned about, every later collision of the two "
            + "servers, whichever reports first, is logged at Debug");
    }

    [Fact]
    public void OnChannelAdded_ShouldLogOneWarningNamingTheLinkedId_WhenOnlyTheLinkedIdIsHeldByAnotherServer()
    {
        var a = Attach("a");
        var b = Attach("b");
        Leg(a, "a-1", linkedId: "L");

        Leg(b, "b-1", linkedId: "L");

        var warnings = Collisions(CollisionEvent);
        warnings.Should().ContainSingle("a linkedid held for another server's call is a collision of the linkedid index");
        new { Id = warnings[0].State["Id"], Kind = warnings[0].State["IdKind"] }.Should().BeEquivalentTo(
            new { Id = (object?)"L", Kind = (object?)"linkedid" },
            "the entry names the linkedid that collided, not the leg's own channel id, which no server held");
    }

    [Fact]
    public void OnChannelAdded_ShouldLogExactlyOneEntry_WhenOneArrivalCollidesOnBothItsChannelIdAndItsLinkedId()
    {
        var a = Attach("a");
        var b = Attach("b");
        Leg(a, "1700000000.1");

        Leg(b, "1700000000.1");

        CollisionEntries.Should().Be(1,
            "one arrival is one report, even when both its channel id and its linkedid are held by another server");
    }

    [Fact]
    public void OnChannelAdded_ShouldWarnOncePerPair_WhenAThirdServerCollidesAfterAPairWasWarned()
    {
        var a = Attach("a");
        var b = Attach("b");
        var c = Attach("c");
        Leg(a, "1700000000.1");
        Leg(b, "1700000000.1");

        Leg(a, "1700000000.2");
        Leg(c, "1700000000.2");

        Collisions(CollisionEvent)
            .Select(e => string.Join(',', new[] { e.State["ServerId"], e.State["OtherServerId"] }.Order()))
            .Should().BeEquivalentTo(["a,b", "a,c"],
                "every unordered server pair gets its own Warning, so a third server is not hidden by the first pair");
    }

    [Fact]
    public void OnChannelAdded_ShouldLogNothing_WhenIdsDifferAcrossServersAndOneServerReportsASecondLegOfItsOwnCall()
    {
        var a = Attach("a");
        var b = Attach("b");

        Leg(a, "a-1", linkedId: "La");
        Leg(b, "b-1", linkedId: "Lb");
        Leg(a, "a-2", linkedId: "La");

        new { Calls = new[] { SessionOf("La").Participants.Count, SessionOf("Lb").Participants.Count }, Entries = CollisionEntries }
            .Should().BeEquivalentTo(
                new { Calls = ExpectedCallSizes, Entries = 0 },
                "ids that differ across servers, and a linkedid one server reuses for its own call, collide with nothing");
    }

    [Fact]
    public void OnChannelAdded_ShouldCorrelateAndEndLegsExactlyAsBefore_WhenTwoServersReportTheSameIds()
    {
        var a = Attach("a");
        var b = Attach("b");
        Leg(a, "1700000000.1");
        Leg(a, "1700000000.9", linkedId: "1700000000.1");
        Leg(b, "1700000000.1");
        Leg(a, "1700000000.2");
        Leg(b, "1700000000.2");
        Leg(b, "1700000000.3");
        Leg(a, "1700000000.3");
        var first = SessionOf("1700000000.1");
        var participantsAfterAdds = first.Participants.Count;

        a.Channels.OnHangup("1700000000.1", HangupCause.NormalClearing);
        b.Channels.OnHangup("1700000000.1", HangupCause.NormalClearing);
        a.Channels.OnHangup("1700000000.9", HangupCause.NormalClearing);
        foreach (var server in new[] { a, b })
        {
            server.Channels.OnHangup("1700000000.2", HangupCause.NormalClearing);
            server.Channels.OnHangup("1700000000.3", HangupCause.NormalClearing);
        }

        var events = _events.ToArray();
        new
        {
            Participants = participantsAfterAdds,
            Sessions = SharedCalls
                .Select(l => SessionOf(l).ServerId).ToArray(),
            Started = events.OfType<CallStartedEvent>().Count(),
            Ended = events.OfType<CallEndedEvent>().Count(),
        }.Should().BeEquivalentTo(
            new
            {
                Participants = 2,
                Sessions = ExpectedOpeners,
                Started = 3,
                Ended = 3,
            },
            "the report changes nothing about correlation: a second server's leg still joins, or is taken for, the "
            + "call its ids name, and each call still starts once and ends once");
    }

    [Fact]
    public void OnChannelAdded_ShouldLogOneWarningAndThenDebug_WhenTwoServersReportTheSameIdsConcurrently()
    {
        const int Ids = 200;
        var a = Attach("a");
        var b = Attach("b");
        using var barrier = new Barrier(2);

        void Report(VerbaraServer server)
        {
            barrier.SignalAndWait();
            for (var i = 0; i < Ids; i++)
                Leg(server, "1700000000." + i.ToString(CultureInfo.InvariantCulture));
        }

        var threads = new[] { new Thread(() => Report(a)), new Thread(() => Report(b)) };
        foreach (var thread in threads) thread.Start();
        foreach (var thread in threads) thread.Join(CleanupBound).Should().BeTrue("premise: the reporting thread finished");

        new
        {
            Warnings = Collisions(CollisionEvent).Count,
            AnyDebug = Collisions(CollisionAgainEvent).Count > 0,
        }.Should().BeEquivalentTo(
            new { Warnings = 1, AnyDebug = true },
            "a smoke test of the once-per-pair gate under contention: two servers colliding at the same moment "
            + "on two threads still produce one Warning, and the rest at Debug");
    }

    [Fact]
    public void OnChannelAdded_ShouldLogOneWarningNamingTheChannelId_WhenOnlyTheChannelIdIsHeldByAnotherServer()
    {
        var a = Attach("a");
        var b = Attach("b");
        Leg(a, "X", linkedId: "La");

        Leg(b, "X", linkedId: "Lb");

        var own = SessionOf("Lb");
        var warnings = Collisions(CollisionEvent);
        warnings.Should().ContainSingle("a channel id held for another server's call is a collision of the channel-id index");
        new
        {
            Id = warnings[0].State["Id"],
            Kind = warnings[0].State["IdKind"],
            OwnServer = own.ServerId,
            OwnLegs = own.Participants.Select(p => p.UniqueId).ToArray(),
        }.Should().BeEquivalentTo(
            new { Id = (object?)"X", Kind = (object?)"channel id", OwnServer = "b", OwnLegs = ExpectedOwnLegs },
            "the check judges what the index held before the arrival overwrote it, and the leg still opens its own call");
    }

    [Fact]
    public void OnChannelAdded_ShouldLogOneWarningNamingTheLinkedId_WhenTheLinkedIdIsHeldByAnotherServersEndedRetainedCall()
    {
        var a = Attach("a");
        var b = Attach("b");
        Leg(a, "a-1", linkedId: "L");
        var ended = SessionOf("L");
        a.Channels.OnHangup("a-1", HangupCause.NormalClearing);
        _events.OfType<CallEndedEvent>().Count(e => e.SessionId == ended.SessionId)
            .Should().Be(1, "premise: server a's call ended and is retained");

        Leg(b, "b-1", linkedId: "L");

        var replacement = SessionOf("L");
        var warnings = Collisions(CollisionEvent);
        warnings.Should().ContainSingle(
            "a linkedid entry of another server's ended, retained call is replaced by this arrival, so it is a collision");
        new
        {
            Id = warnings[0].State["Id"],
            NewCall = replacement.SessionId != ended.SessionId,
            NewServer = replacement.ServerId,
        }.Should().BeEquivalentTo(
            new { Id = (object?)"L", NewCall = true, NewServer = "b" },
            "the entry names the linkedid, and the leg opens a call of its own exactly as before");
    }

    [Fact]
    public void OnChannelAdded_ShouldLogNothing_WhenAServerIsDetachedAndAttachedAgainUnderTheSameId()
    {
        var a = Attach("a");
        Leg(a, "X");
        _sut.DetachFromServer("a");
        var again = Attach("a");

        Leg(again, "X");

        new { Entries = CollisionEntries, Legs = SessionOf("X").Participants.Count }.Should().BeEquivalentTo(
            new { Entries = 0, Legs = 1 },
            "a server that comes back under the same id reports ids its own sessions hold; that is no collision");
    }

    /// <summary>Keeps every entry written to it, with its event, message and structured properties.</summary>
    private sealed class RecordingLogger : ILogger<CallSessionManager>
    {
        private readonly ConcurrentQueue<LogEntry> _entries = new();

        public IReadOnlyCollection<LogEntry> Entries => _entries.ToArray();

        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception,
            Func<TState, Exception?, string> formatter)
        {
            var properties = new Dictionary<string, object?>(StringComparer.Ordinal);
            if (state is IEnumerable<KeyValuePair<string, object?>> pairs)
            {
                foreach (var pair in pairs)
                    properties[pair.Key] = pair.Value;
            }

            _entries.Enqueue(new LogEntry(logLevel, eventId, formatter(state, exception), properties));
        }
    }

    private sealed record LogEntry(LogLevel Level, EventId EventId, string Message, IReadOnlyDictionary<string, object?> State);
}

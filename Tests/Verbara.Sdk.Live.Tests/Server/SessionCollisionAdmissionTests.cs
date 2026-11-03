using System.Collections.Concurrent;
using FluentAssertions;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Verbara.Sdk.Enums;
using Verbara.Sdk.Live.Server;
using Verbara.Sdk.Live.Tests.Harness;
using Verbara.Sdk.Sessions;
using Verbara.Sdk.Sessions.Internal;
using Verbara.Sdk.Sessions.Manager;
using Verbara.Sdk.Tests.Shared.Sessions;

namespace Verbara.Sdk.Live.Tests.Server;

public sealed class SessionCollisionAdmissionTests
{
    [Fact]
    public async Task OnChannelAdded_ShouldExplainScopedIsolationAndUnscopedAmbiguity_WhenTwoServersReportTheSameId()
    {
        await using var a = await StartAsync();
        await using var b = await StartAsync();
        var log = new CollisionLog();
        await using var manager = CreateManager(log);
        manager.AttachToServer(a.Server, "a");
        manager.AttachToServer(b.Server, "b");

        Add(a.Server, "X", "L");
        Add(b.Server, "X", "L");

        var warnings = log.Collisions(1);
        warnings.Should().ContainSingle();
        var entry = warnings.Single();
        entry.Level.Should().Be(LogLevel.Warning);
        entry.EventId.Name.Should().Be("LogCrossServerCollision");
        entry.State["ServerId"].Should().Be("b");
        entry.State["OtherServerId"].Should().Be("a");
        entry.State["Id"].Should().Be("X");
        entry.Message.Should().Contain("kept apart");
        entry.Message.Should().Contain("lookup naming no server");
        entry.Message.Should().Contain("consumer keyed only by linkedid");
        entry.Message.Should().Contain("cannot tell them apart");
        entry.Message.Should().Contain("systemname");
        entry.Message.Should().Contain("attach it once");
        entry.Message.Should().NotContain("shares one index");
        entry.Message.Should().NotContain("can be correlated together");
        manager.GetByChannelId("a", "X").Should().NotBeSameAs(manager.GetByChannelId("b", "X"));
        manager.GetByChannelId("X").Should().BeNull();
        SessionIndexInvariant.Assert(manager);
    }

    [Theory]
    [InlineData("X", "La", "X", "Lb", "channel id", "X")]
    [InlineData("a-leg", "L", "b-leg", "L", "linkedid", "L")]
    [InlineData("X", "L", "X", "L", "channel id", "X")]
    public async Task AdmitExistingCalls_ShouldReportTheCollisionBeforePublishingItsOwnCall_WhenBothTablesWereLoadedBeforeAttach(
        string aId, string aLinkedId, string bId, string bLinkedId, string idKind, string id)
    {
        await using var a = await StartAsync();
        await using var b = await StartAsync();
        Add(a.Server, aId, aLinkedId);
        Add(b.Server, bId, bLinkedId);
        var log = new CollisionLog();
        var events = new ConcurrentQueue<SessionDomainEvent>();
        await using var manager = CreateManager(log);
        using var subscription = manager.Events.Subscribe(new CollisionObserver(events.Enqueue));
        manager.AttachToServer(a.Server, "a");
        manager.AttachToServer(b.Server, "b");
        manager.ActiveSessions.Should().BeEmpty("attaching a loaded server does not admit its calls");
        log.CollisionCount.Should().Be(0, "no live arrival reached this manager before either admission");
        var aAsks = a.FirstPeer.Asked("Status");
        var bAsks = b.FirstPeer.Asked("Status");

        await manager.AdmitExistingCalls("a");
        log.CollisionCount.Should().Be(0, "the other index still holds no call");
        var bPublicationCollisionCount = -1;
        var lookupAtDiagnostic = new ConcurrentQueue<(CallSession? ChannelOwner, CallSession? LinkedOwner)>();
        log.CollisionRecorded = _ => lookupAtDiagnostic.Enqueue((
            manager.GetByChannelId("b", bId), manager.GetByLinkedId("b", bLinkedId)));
        using var publication = manager.Events.Subscribe(new CollisionObserver(e =>
        {
            if (e is CallStartedEvent started && started.ServerId == "b")
                bPublicationCollisionCount = log.CollisionCount;
        }));
        await manager.AdmitExistingCalls("b");

        var warnings = log.Collisions(1);
        warnings.Should().ContainSingle("the first collision must be reported by the cold admission itself");
        var warning = warnings.Single();
        warning.Level.Should().Be(LogLevel.Warning);
        warning.EventId.Name.Should().Be("LogCrossServerCollision");
        warning.State["ServerId"].Should().Be("b");
        warning.State["OtherServerId"].Should().Be("a");
        warning.State["IdKind"].Should().Be(idKind);
        warning.State["Id"].Should().Be(id);
        bPublicationCollisionCount.Should().Be(1, "the diagnostic precedes the call-start publication");
        lookupAtDiagnostic.Should().ContainSingle("the synchronous ILogger callback must observe the actual diagnostic");
        var atDiagnostic = lookupAtDiagnostic.Single();
        atDiagnostic.ChannelOwner.Should().BeNull("the new cold leg must still be unindexed at the diagnostic call");
        atDiagnostic.LinkedOwner.Should().BeNull("the new cold call must not have published its linkedid at the diagnostic call");
        log.CollisionCount.Should().Be(1, "one visit reports at most once even if both ids collide");
        var aCall = manager.GetByChannelId("a", aId)!;
        var bCall = manager.GetByChannelId("b", bId)!;
        aCall.Should().NotBeNull();
        bCall.Should().NotBeNull().And.NotBeSameAs(aCall);
        aCall.ServerId.Should().Be("a");
        bCall.ServerId.Should().Be("b");
        aCall.Participants.Should().ContainSingle(p => p.UniqueId == aId && !p.LeftAt.HasValue);
        bCall.Participants.Should().ContainSingle(p => p.UniqueId == bId && !p.LeftAt.HasValue);
        manager.ActiveSessions.Should().HaveCount(2);
        events.OfType<CallStartedEvent>().Should().HaveCount(2);
        events.OfType<CallEndedEvent>().Should().BeEmpty();
        a.FirstPeer.Asked("Status").Should().Be(aAsks);
        b.FirstPeer.Asked("Status").Should().Be(bAsks);
        SessionIndexInvariant.Assert(manager);
    }

    [Fact]
    public async Task OnChannelAdded_ShouldWarnOncePerPairInEachManager_WhenBothWatchTheSameServerPair()
    {
        await using var a = await StartAsync();
        await using var b = await StartAsync();
        var firstLog = new CollisionLog();
        var secondLog = new CollisionLog();
        await using var first = CreateManager(firstLog);
        await using var second = CreateManager(secondLog);
        first.AttachToServer(a.Server, "a");
        first.AttachToServer(b.Server, "b");
        second.AttachToServer(a.Server, "a");
        second.AttachToServer(b.Server, "b");

        Add(a.Server, "X", "Lx");
        Add(b.Server, "X", "Lx");
        Add(b.Server, "Y", "Ly");
        Add(a.Server, "Y", "Ly");

        firstLog.Collisions(1).Should().ContainSingle();
        secondLog.Collisions(1).Should().ContainSingle();
        firstLog.Collisions(2).Should().ContainSingle(e => e.Level == LogLevel.Debug);
        secondLog.Collisions(2).Should().ContainSingle(e => e.Level == LogLevel.Debug);
        first.ActiveSessions.Should().HaveCount(4);
        second.ActiveSessions.Should().HaveCount(4);
        SessionIndexInvariant.Assert(first);
        SessionIndexInvariant.Assert(second);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task AdmitExistingCalls_ShouldReportNoCollision_WhenNoOtherAttachedIndexHoldsItsIds(bool attachOtherServer)
    {
        await using var a = await StartAsync();
        await using var b = await StartAsync();
        Add(a.Server, "a-leg", "La");
        Add(b.Server, "b-leg", "Lb");
        var log = new CollisionLog();
        await using var manager = CreateManager(log);
        manager.AttachToServer(a.Server, "a");
        if (attachOtherServer) manager.AttachToServer(b.Server, "b");

        await manager.AdmitExistingCalls("a");
        if (attachOtherServer) await manager.AdmitExistingCalls("b");

        log.CollisionCount.Should().Be(0);
        manager.ActiveSessions.Should().HaveCount(attachOtherServer ? 2 : 1);
        SessionIndexInvariant.Assert(manager);
    }

    private static Task<Run> StartAsync() => Run.StartAsync(new BootingAsterisk { BootedAtLogin = true });

    private static CallSessionManager CreateManager(CollisionLog log) =>
        new(Options.Create(new SessionOptions()), log, new InMemorySessionStore());

    private static void Add(VerbaraServer server, string id, string linkedId) =>
        server.Channels.OnNewChannel(id, "PJSIP/" + id, ChannelState.Up, linkedId: linkedId);

    private sealed class CollisionObserver(Action<SessionDomainEvent> onNext) : IObserver<SessionDomainEvent>
    {
        public void OnNext(SessionDomainEvent value) => onNext(value);
        public void OnError(Exception error) { }
        public void OnCompleted() { }
    }

    private sealed class CollisionLog : ILogger<CallSessionManager>
    {
        private readonly ConcurrentQueue<CollisionEntry> _entries = new();
        public int CollisionCount => _entries.Count(e => e.EventId.Id is 1 or 2);
        public Action<CollisionEntry>? CollisionRecorded { get; set; }
        public CollisionEntry[] Collisions(int eventId) =>
            _entries.Where(e => e.EventId.Id == eventId).ToArray();
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
        public bool IsEnabled(LogLevel logLevel) => true;
        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception,
            Func<TState, Exception?, string> formatter)
        {
            var properties = new Dictionary<string, object?>(StringComparer.Ordinal);
            if (state is IEnumerable<KeyValuePair<string, object?>> pairs)
                foreach (var pair in pairs) properties[pair.Key] = pair.Value;
            var entry = new CollisionEntry(logLevel, eventId, formatter(state, exception), properties);
            _entries.Enqueue(entry);
            if (eventId.Id is 1 or 2) CollisionRecorded?.Invoke(entry);
        }
    }

    private sealed record CollisionEntry(LogLevel Level, EventId EventId, string Message,
        IReadOnlyDictionary<string, object?> State);
}

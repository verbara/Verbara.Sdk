using Asterisk.Sdk.Enums;
using Asterisk.Sdk.Redis.Spike.Fixtures;
using Asterisk.Sdk.Redis.Spike.Store;
using Asterisk.Sdk.Sessions;
using FluentAssertions;
using Xunit;

namespace Asterisk.Sdk.Redis.Spike.Tests;

[Collection("Redis")]
[Trait("Category", "Integration")]
public sealed class RedisSessionStoreTests : IAsyncLifetime
{
    private readonly RedisFixture _fixture;
    private readonly RedisSessionStoreOptions _options;

    public RedisSessionStoreTests(RedisFixture fixture)
    {
        _fixture = fixture;
        _options = new RedisSessionStoreOptions
        {
            KeyPrefix = "test:",
            CompletedRetention = TimeSpan.FromMinutes(10),
        };
    }

    public Task InitializeAsync() => _fixture.FlushAsync();
    public Task DisposeAsync() => Task.CompletedTask;

    private RedisSessionStore CreateStore() => new(_fixture.Redis, _options);

    private RedisSessionStore CreateStore(RedisSessionStoreOptions options) => new(_fixture.Redis, options);

    private static CallSession CreateSession(string id)
    {
        var session = new CallSession(id, $"linked-{id}", "server-1", CallDirection.Outbound);
        return session;
    }

    [Fact]
    public async Task SaveAndGet_ShouldRoundtrip()
    {
        // Arrange
        var store = CreateStore();
        var session = CreateSession("round-1");
        session.QueueName = "support";
        session.AgentId = "agent-42";
        session.SetMetadata("campaign", "summer-sale");

        session.AddParticipant(new SessionParticipant
        {
            UniqueId = "chan-1",
            Channel = "SIP/100-0001",
            Technology = "SIP",
            Role = ParticipantRole.Caller,
            CallerIdNum = "5551234",
            CallerIdName = "John Doe",
        });

        session.AddEvent(new CallSessionEvent(
            DateTimeOffset.UtcNow, CallSessionEventType.Created, "SIP/100-0001", null, "test event"));

        // Advance: Created -> Dialing -> Ringing -> Connected
        session.Transition(CallSessionState.Dialing);
        session.Transition(CallSessionState.Ringing);
        session.Transition(CallSessionState.Connected);

        // Act
        await store.SaveAsync(session, CancellationToken.None);
        var loaded = await store.GetAsync("round-1", CancellationToken.None);

        // Assert
        loaded.Should().NotBeNull();
        loaded!.SessionId.Should().Be("round-1");
        loaded.LinkedId.Should().Be("linked-round-1");
        loaded.ServerId.Should().Be("server-1");
        loaded.State.Should().Be(CallSessionState.Connected);
        loaded.Direction.Should().Be(CallDirection.Outbound);
        loaded.QueueName.Should().Be("support");
        loaded.AgentId.Should().Be("agent-42");
        loaded.Metadata.Should().ContainKey("campaign").WhoseValue.Should().Be("summer-sale");
        loaded.Participants.Should().HaveCount(1);
        loaded.Participants[0].CallerIdNum.Should().Be("5551234");
        loaded.Participants[0].CallerIdName.Should().Be("John Doe");
        loaded.Events.Should().HaveCount(1);
        loaded.Events[0].Type.Should().Be(CallSessionEventType.Created);
        loaded.ConnectedAt.Should().NotBeNull();
    }

    [Fact]
    public async Task SaveAndGetByLinkedId_ShouldResolve()
    {
        // Arrange
        var store = CreateStore();
        var session = CreateSession("link-1");

        // Act
        await store.SaveAsync(session, CancellationToken.None);
        var loaded = await store.GetByLinkedIdAsync("linked-link-1", CancellationToken.None);

        // Assert
        loaded.Should().NotBeNull();
        loaded!.SessionId.Should().Be("link-1");
    }

    [Fact]
    public async Task GetActive_ShouldReturnOnlyActiveSessions()
    {
        // Arrange
        var store = CreateStore();

        var active1 = CreateSession("active-1");
        active1.Transition(CallSessionState.Dialing);

        var active2 = CreateSession("active-2");
        active2.Transition(CallSessionState.Dialing);
        active2.Transition(CallSessionState.Ringing);

        var completed = CreateSession("completed-1");
        completed.Transition(CallSessionState.Failed);

        await store.SaveAsync(active1, CancellationToken.None);
        await store.SaveAsync(active2, CancellationToken.None);
        await store.SaveAsync(completed, CancellationToken.None);

        // Act
        var active = (await store.GetActiveAsync(CancellationToken.None)).ToList();

        // Assert
        active.Should().HaveCount(2);
        active.Select(s => s.SessionId).Should().BeEquivalentTo(["active-1", "active-2"]);
    }

    [Fact]
    public async Task Delete_ShouldRemoveSessionAndIndices()
    {
        // Arrange
        var store = CreateStore();
        var session = CreateSession("del-1");
        await store.SaveAsync(session, CancellationToken.None);

        // Act
        await store.DeleteAsync("del-1", CancellationToken.None);

        // Assert
        var byId = await store.GetAsync("del-1", CancellationToken.None);
        byId.Should().BeNull();

        var byLinked = await store.GetByLinkedIdAsync("linked-del-1", CancellationToken.None);
        byLinked.Should().BeNull();

        var active = (await store.GetActiveAsync(CancellationToken.None)).ToList();
        active.Should().BeEmpty();
    }

    [Fact]
    public async Task SaveBatch_ShouldPipelineAll()
    {
        // Arrange
        var store = CreateStore();
        var sessions = Enumerable.Range(0, 100)
            .Select(i => CreateSession($"batch-{i:D4}"))
            .ToList();

        // Act
        await store.SaveBatchAsync(sessions, CancellationToken.None);

        // Assert
        var active = (await store.GetActiveAsync(CancellationToken.None)).ToList();
        active.Should().HaveCount(100);

        var spot = await store.GetAsync("batch-0042", CancellationToken.None);
        spot.Should().NotBeNull();
        spot!.SessionId.Should().Be("batch-0042");
    }

    [Fact]
    public async Task CompletedSession_ShouldExpire()
    {
        // Arrange
        var options = new RedisSessionStoreOptions
        {
            KeyPrefix = "test:",
            CompletedRetention = TimeSpan.FromSeconds(5),
        };
        var store = CreateStore(options);

        var session = CreateSession("expire-1");
        session.Transition(CallSessionState.Failed);

        // Act
        await store.SaveAsync(session, CancellationToken.None);

        var immediate = await store.GetAsync("expire-1", CancellationToken.None);
        immediate.Should().NotBeNull();

        // Wait for Redis TTL to expire
        await Task.Delay(TimeSpan.FromSeconds(6));

        var expired = await store.GetAsync("expire-1", CancellationToken.None);

        // Assert
        expired.Should().BeNull();
    }

    [Fact]
    public async Task SnapshotPreservesPrivateState()
    {
        // Arrange
        var store = CreateStore();
        var session = CreateSession("hold-1");
        session.Transition(CallSessionState.Dialing);
        session.Transition(CallSessionState.Connected);

        // Start and end a hold cycle
        session.StartHold();
        await Task.Delay(50); // accumulate some hold time
        session.EndHold();

        // Act
        await store.SaveAsync(session, CancellationToken.None);
        var loaded = await store.GetAsync("hold-1", CancellationToken.None);

        // Assert
        loaded.Should().NotBeNull();
        loaded!.HoldTime.Should().BeGreaterThan(TimeSpan.Zero);
    }

    [Fact]
    public async Task ConcurrentSaves_ShouldNotCorrupt()
    {
        // Arrange
        var store = CreateStore();
        var tasks = new Task[50];

        for (var i = 0; i < 50; i++)
        {
            var index = i;
            tasks[i] = Task.Run(async () =>
            {
                var session = CreateSession("concurrent-1");
                session.SetMetadata("writer", $"task-{index}");
                await store.SaveAsync(session, CancellationToken.None);
            });
        }

        // Act
        await Task.WhenAll(tasks);

        // Assert
        var loaded = await store.GetAsync("concurrent-1", CancellationToken.None);
        loaded.Should().NotBeNull();
        loaded!.SessionId.Should().Be("concurrent-1");
        loaded.Metadata.Should().ContainKey("writer");
        loaded.Metadata["writer"].Should().StartWith("task-");
    }

    [Fact]
    public async Task EnumSerialization_ShouldRoundtrip()
    {
        // Arrange
        var store = CreateStore();
        var session = CreateSession("enum-1");
        session.HangupCause = HangupCause.NormalClearing;
        session.Transition(CallSessionState.Dialing);
        session.Transition(CallSessionState.Ringing);

        session.AddParticipant(new SessionParticipant
        {
            UniqueId = "chan-agent-1",
            Channel = "SIP/200-0002",
            Technology = "SIP",
            Role = ParticipantRole.Agent,
            HangupCause = HangupCause.UserBusy,
        });

        // Act
        await store.SaveAsync(session, CancellationToken.None);
        var loaded = await store.GetAsync("enum-1", CancellationToken.None);

        // Assert
        loaded.Should().NotBeNull();
        loaded!.State.Should().Be(CallSessionState.Ringing);
        loaded.HangupCause.Should().Be(HangupCause.NormalClearing);
        loaded.Participants.Should().HaveCount(1);
        loaded.Participants[0].Role.Should().Be(ParticipantRole.Agent);
        loaded.Participants[0].HangupCause.Should().Be(HangupCause.UserBusy);
    }
}

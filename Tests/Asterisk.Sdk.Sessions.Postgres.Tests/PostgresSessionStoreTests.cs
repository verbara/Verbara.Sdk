using Asterisk.Sdk.Enums;
using Asterisk.Sdk.Sessions;
using FluentAssertions;
using Xunit;

namespace Asterisk.Sdk.Sessions.Postgres.Tests;

[Collection("Postgres")]
[Trait("Category", "Integration")]
public sealed class PostgresSessionStoreTests : IAsyncLifetime
{
    private readonly PostgresFixture _fixture;
    private readonly PostgresSessionStoreOptions _options;

    public PostgresSessionStoreTests(PostgresFixture fixture)
    {
        _fixture = fixture;
        _options = new PostgresSessionStoreOptions
        {
            TableName = "asterisk_call_sessions",
            SchemaName = "public",
        };
    }

    public Task InitializeAsync() => _fixture.FlushAsync();
    public Task DisposeAsync() => Task.CompletedTask;

    private PostgresSessionStore CreateStore() => new(_fixture.DataSource, _options);

    private static CallSession CreateSession(string id)
    {
        var session = new CallSession(id, $"linked-{id}", "server-1", CallDirection.Outbound);
        return session;
    }

    [Fact]
    public async Task SaveAndGet_ShouldRoundtrip_WhenSessionHasFullState()
    {
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

        session.Transition(CallSessionState.Dialing);
        session.Transition(CallSessionState.Ringing);
        session.Transition(CallSessionState.Connected);

        await store.SaveAsync(session, CancellationToken.None);
        var loaded = await store.GetAsync("round-1", CancellationToken.None);

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
    public async Task SaveAndGetByLinkedId_ShouldResolve_WhenLinkedIndexIsPresent()
    {
        var store = CreateStore();
        var session = CreateSession("link-1");

        await store.SaveAsync(session, CancellationToken.None);
        var loaded = await store.GetByLinkedIdAsync("linked-link-1", CancellationToken.None);

        loaded.Should().NotBeNull();
        loaded!.SessionId.Should().Be("link-1");
    }

    [Fact]
    public async Task GetActive_ShouldReturnOnlyActiveSessions_WhenCompletedExistsToo()
    {
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

        var active = (await store.GetActiveAsync(CancellationToken.None)).ToList();

        active.Should().HaveCount(2);
        active.Select(s => s.SessionId).Should().BeEquivalentTo(["active-1", "active-2"]);
    }

    [Fact]
    public async Task Delete_ShouldRemoveSessionAndIndices_WhenSessionExists()
    {
        var store = CreateStore();
        var session = CreateSession("del-1");
        await store.SaveAsync(session, CancellationToken.None);

        await store.DeleteAsync("del-1", CancellationToken.None);

        var byId = await store.GetAsync("del-1", CancellationToken.None);
        byId.Should().BeNull();

        var byLinked = await store.GetByLinkedIdAsync("linked-del-1", CancellationToken.None);
        byLinked.Should().BeNull();

        var active = (await store.GetActiveAsync(CancellationToken.None)).ToList();
        active.Should().BeEmpty();
    }

    [Fact]
    public async Task SaveBatch_ShouldPersistAll_When100SessionsSaved()
    {
        var store = CreateStore();
        var sessions = Enumerable.Range(0, 100)
            .Select(i => CreateSession($"batch-{i:D4}"))
            .ToList();

        await store.SaveBatchAsync(sessions, CancellationToken.None);

        var active = (await store.GetActiveAsync(CancellationToken.None)).ToList();
        active.Should().HaveCount(100);

        var spot = await store.GetAsync("batch-0042", CancellationToken.None);
        spot.Should().NotBeNull();
        spot!.SessionId.Should().Be("batch-0042");
    }

    [Fact]
    public async Task SaveAsync_ShouldUpsert_WhenSessionIdAlreadyExists()
    {
        var store = CreateStore();
        var session = CreateSession("upsert-1");
        session.SetMetadata("version", "1");

        await store.SaveAsync(session, CancellationToken.None);

        // Update the same session — UPSERT should replace, not append
        session.SetMetadata("version", "2");
        session.Transition(CallSessionState.Dialing);
        await store.SaveAsync(session, CancellationToken.None);

        var loaded = await store.GetAsync("upsert-1", CancellationToken.None);
        loaded.Should().NotBeNull();
        loaded!.Metadata["version"].Should().Be("2");
        loaded.State.Should().Be(CallSessionState.Dialing);

        // Single row only — GetActive must return exactly one
        var active = (await store.GetActiveAsync(CancellationToken.None)).ToList();
        active.Should().HaveCount(1);
    }

    [Fact]
    public async Task SnapshotPreservesPrivateState_ShouldRoundtripHoldTime_WhenSessionHeld()
    {
        var store = CreateStore();
        var session = CreateSession("hold-1");
        session.Transition(CallSessionState.Dialing);
        session.Transition(CallSessionState.Connected);

        session.StartHold();
        await Task.Delay(50);
        session.EndHold();

        await store.SaveAsync(session, CancellationToken.None);
        var loaded = await store.GetAsync("hold-1", CancellationToken.None);

        loaded.Should().NotBeNull();
        loaded!.HoldTime.Should().BeGreaterThan(TimeSpan.Zero);
    }

    [Fact]
    public async Task ConcurrentSaves_ShouldNotCorrupt_When50WritersRaceOnSameId()
    {
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

        await Task.WhenAll(tasks);

        var loaded = await store.GetAsync("concurrent-1", CancellationToken.None);
        loaded.Should().NotBeNull();
        loaded!.SessionId.Should().Be("concurrent-1");
        loaded.Metadata.Should().ContainKey("writer");
        loaded.Metadata["writer"].Should().StartWith("task-");
    }

    [Fact]
    public async Task SaveAsync_ShouldThrowOperationCanceled_WhenTokenAlreadyCancelled()
    {
        var store = CreateStore();
        var session = CreateSession("cancel-1");
        using var cts = new CancellationTokenSource();
        await cts.CancelAsync();

        var act = async () => await store.SaveAsync(session, cts.Token);

        await act.Should().ThrowAsync<OperationCanceledException>();
    }

    [Fact]
    public async Task EnumSerialization_ShouldRoundtrip_WhenSessionCarriesHangupCauseAndRoles()
    {
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

        await store.SaveAsync(session, CancellationToken.None);
        var loaded = await store.GetAsync("enum-1", CancellationToken.None);

        loaded.Should().NotBeNull();
        loaded!.State.Should().Be(CallSessionState.Ringing);
        loaded.HangupCause.Should().Be(HangupCause.NormalClearing);
        loaded.Participants.Should().HaveCount(1);
        loaded.Participants[0].Role.Should().Be(ParticipantRole.Agent);
        loaded.Participants[0].HangupCause.Should().Be(HangupCause.UserBusy);
    }
}

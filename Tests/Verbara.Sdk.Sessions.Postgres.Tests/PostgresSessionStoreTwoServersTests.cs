using Verbara.Sdk.Enums;
using Verbara.Sdk.Sessions;
using FluentAssertions;
using Xunit;

namespace Verbara.Sdk.Sessions.Postgres.Tests;

/// <summary>
/// Two Asterisk servers that report the same <c>linkedid</c> are two calls, one per server
/// (server-scoped-sessions: "A leg SHALL be correlated only with calls of the server that reported
/// it"), and a lookup that names no server answers only when exactly one server holds the id
/// ("A lookup that names no server SHALL answer only when one server holds the id"). These tests hold
/// the store to the same contract: each server's session is kept apart, and nothing written or
/// removed for one server overwrites or hides the other's.
/// </summary>
[Collection("Postgres")]
[Trait("Category", "Integration")]
public sealed class PostgresSessionStoreTwoServersTests : IAsyncLifetime
{
    private const string SharedLinkedId = "1700000000.1";

    private readonly PostgresFixture _fixture;

    public PostgresSessionStoreTwoServersTests(PostgresFixture fixture) => _fixture = fixture;

    public Task InitializeAsync() => _fixture.FlushAsync();
    public Task DisposeAsync() => Task.CompletedTask;

    private PostgresSessionStore CreateStore() =>
        new(_fixture.DataSource, new PostgresSessionStoreOptions
        {
            TableName = "asterisk_call_sessions",
            SchemaName = "public",
        });

    private static CallSession CreateSession(string sessionId, string serverId)
    {
        var session = new CallSession(sessionId, SharedLinkedId, serverId, CallDirection.Inbound);
        session.Transition(CallSessionState.Dialing);
        return session;
    }

    [Fact]
    public async Task GetAsync_ShouldReturnEachServersOwnSession_WhenTwoServersShareOneLinkedId()
    {
        var store = CreateStore();
        await store.SaveAsync(CreateSession("session-a", "server-a"), CancellationToken.None);
        await store.SaveAsync(CreateSession("session-b", "server-b"), CancellationToken.None);

        var a = await store.GetAsync("session-a", CancellationToken.None);
        var b = await store.GetAsync("session-b", CancellationToken.None);

        a.Should().NotBeNull();
        a!.ServerId.Should().Be("server-a");
        a.LinkedId.Should().Be(SharedLinkedId);
        b.Should().NotBeNull();
        b!.ServerId.Should().Be("server-b");
        b.LinkedId.Should().Be(SharedLinkedId);
    }

    [Fact]
    public async Task GetActiveAsync_ShouldListBothServersSessions_WhenTwoServersShareOneLinkedId()
    {
        var store = CreateStore();
        await store.SaveBatchAsync(
            [CreateSession("session-a", "server-a"), CreateSession("session-b", "server-b")],
            CancellationToken.None);

        var active = (await store.GetActiveAsync(CancellationToken.None)).ToList();

        active.Select(s => (s.SessionId, s.ServerId)).Should().BeEquivalentTo(
            [("session-a", "server-a"), ("session-b", "server-b")]);
    }

    [Fact]
    public async Task GetByLinkedIdAsync_ShouldReturnNothing_WhenTwoServersHoldTheLinkedId()
    {
        var store = CreateStore();
        await store.SaveAsync(CreateSession("session-a", "server-a"), CancellationToken.None);
        await store.SaveAsync(CreateSession("session-b", "server-b"), CancellationToken.None);

        var found = await store.GetByLinkedIdAsync(SharedLinkedId, CancellationToken.None);

        found.Should().BeNull(
            "two servers hold the linkedid, so a lookup that names no server cannot pick one of them");
    }

    [Fact]
    public async Task DeleteAsync_ShouldLeaveTheOtherServersSessionFoundByLinkedId_WhenTwoServersShareOneLinkedId()
    {
        var store = CreateStore();
        await store.SaveAsync(CreateSession("session-a", "server-a"), CancellationToken.None);
        await store.SaveAsync(CreateSession("session-b", "server-b"), CancellationToken.None);

        await store.DeleteAsync("session-a", CancellationToken.None);

        (await store.GetAsync("session-a", CancellationToken.None)).Should().BeNull();
        var b = await store.GetAsync("session-b", CancellationToken.None);
        b.Should().NotBeNull();
        b!.ServerId.Should().Be("server-b");
        (await store.GetActiveAsync(CancellationToken.None)).Select(s => s.SessionId)
            .Should().BeEquivalentTo(["session-b"]);
        var byLinked = await store.GetByLinkedIdAsync(SharedLinkedId, CancellationToken.None);
        byLinked.Should().NotBeNull("server-b still holds the linkedid, and it is now the only server that does");
        byLinked!.SessionId.Should().Be("session-b");
    }

    [Fact]
    public async Task DeleteAsync_ShouldLeaveTheOtherServersSessionFoundByLinkedId_WhenTheDeletedOneWasSavedLast()
    {
        var store = CreateStore();
        await store.SaveAsync(CreateSession("session-b", "server-b"), CancellationToken.None);
        await store.SaveAsync(CreateSession("session-a", "server-a"), CancellationToken.None);

        await store.DeleteAsync("session-a", CancellationToken.None);

        var byLinked = await store.GetByLinkedIdAsync(SharedLinkedId, CancellationToken.None);
        byLinked.Should().NotBeNull("server-b still holds the linkedid, and it is now the only server that does");
        byLinked!.SessionId.Should().Be("session-b");
    }

    [Fact]
    public async Task GetByLinkedIdAsync_ShouldReturnTheNamedServersSession_WhenTwoServersHoldTheLinkedId()
    {
        var store = CreateStore();
        await store.SaveAsync(CreateSession("session-a", "server-a"), CancellationToken.None);
        await store.SaveAsync(CreateSession("session-b", "server-b"), CancellationToken.None);

        var a = await store.GetByLinkedIdAsync("server-a", SharedLinkedId, CancellationToken.None);
        var b = await store.GetByLinkedIdAsync("server-b", SharedLinkedId, CancellationToken.None);
        var c = await store.GetByLinkedIdAsync("server-c", SharedLinkedId, CancellationToken.None);

        new { A = a?.SessionId, B = b?.SessionId, C = c?.SessionId }.Should().BeEquivalentTo(
            new { A = "session-a", B = "session-b", C = default(string) },
            "a lookup that names a server returns that server's session, and nothing for a server holding none");
    }

    [Fact]
    public async Task GetByLinkedIdAsync_ShouldReturnTheNewestSession_WhenOneServerHoldsTheLinkedIdTwice()
    {
        var store = CreateStore();
        var now = DateTimeOffset.UtcNow;
        await store.SaveAsync(
            new CallSession("session-newer", SharedLinkedId, "server-a", CallDirection.Inbound) { CreatedAt = now },
            CancellationToken.None);
        await store.SaveAsync(
            new CallSession("session-older", SharedLinkedId, "server-a", CallDirection.Inbound) { CreatedAt = now.AddMinutes(-1) },
            CancellationToken.None);

        var unscoped = await store.GetByLinkedIdAsync(SharedLinkedId, CancellationToken.None);
        var scoped = await store.GetByLinkedIdAsync("server-a", SharedLinkedId, CancellationToken.None);

        new { Unscoped = unscoped?.SessionId, Scoped = scoped?.SessionId }.Should().BeEquivalentTo(
            new { Unscoped = "session-newer", Scoped = "session-newer" },
            "one server holds the linkedid, so both lookups answer with its newest session");
    }

    [Fact]
    public async Task GetByLinkedIdAsync_ShouldReturnNothing_WhenTwoServersHoldTheLinkedIdAndOneHasEnded()
    {
        var store = CreateStore();
        var a = CreateSession("session-a", "server-a");
        await store.SaveAsync(a, CancellationToken.None);
        await store.SaveAsync(CreateSession("session-b", "server-b"), CancellationToken.None);
        a.Transition(CallSessionState.Failed);
        await store.SaveAsync(a, CancellationToken.None);

        var found = await store.GetByLinkedIdAsync(SharedLinkedId, CancellationToken.None);

        found.Should().BeNull("this store keeps ended rows, so both servers still hold the linkedid");
    }
}

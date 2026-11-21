using System.Text.Json;
using Verbara.Sdk.Enums;
using Verbara.Sdk.Sessions;
using Verbara.Sdk.Sessions.Serialization;
using FluentAssertions;
using StackExchange.Redis;
using Xunit;

namespace Verbara.Sdk.Sessions.Redis.Tests;

/// <summary>
/// Two Asterisk servers that report the same <c>linkedid</c> are two calls, one per server
/// (server-scoped-sessions: "A leg SHALL be correlated only with calls of the server that reported
/// it"), and a lookup that names no server answers only when exactly one server holds the id
/// ("A lookup that names no server SHALL answer only when one server holds the id"). These tests hold
/// the store to the same contract: each server's session is kept apart, and nothing written or
/// removed for one server overwrites or hides the other's.
/// </summary>
[Collection("Redis")]
[Trait("Category", "Integration")]
public sealed class RedisSessionStoreTwoServersTests : IAsyncLifetime
{
    private const string SharedLinkedId = "1700000000.1";

    private readonly RedisFixture _fixture;

    public RedisSessionStoreTwoServersTests(RedisFixture fixture) => _fixture = fixture;

    public Task InitializeAsync() => _fixture.FlushAsync();
    public Task DisposeAsync() => Task.CompletedTask;

    private RedisSessionStore CreateStore(TimeSpan? completedRetention = null) =>
        new(_fixture.Redis, new RedisSessionStoreOptions
        {
            KeyPrefix = "test:",
            CompletedRetention = completedRetention ?? TimeSpan.FromMinutes(10),
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
    public async Task SaveAsync_ShouldNotHideTheOtherServersLiveSession_WhenOneServersEndedSessionExpires()
    {
        var store = CreateStore(completedRetention: TimeSpan.FromSeconds(2));
        var a = CreateSession("session-a", "server-a");
        await store.SaveAsync(a, CancellationToken.None);
        await store.SaveAsync(CreateSession("session-b", "server-b"), CancellationToken.None);

        a.Transition(CallSessionState.Failed);
        await store.SaveAsync(a, CancellationToken.None);
        await Task.Delay(TimeSpan.FromSeconds(3)); // fence-allow: SETTLE — outlasts the 2 s retention, whose expiry Redis runs on its own clock

        (await store.GetAsync("session-a", CancellationToken.None)).Should().BeNull("server-a's ended session expired");
        (await store.GetAsync("session-b", CancellationToken.None)).Should().NotBeNull("server-b's session is live");
        var byLinked = await store.GetByLinkedIdAsync(SharedLinkedId, CancellationToken.None);
        byLinked.Should().NotBeNull("server-b's live session is the only one holding the linkedid");
        byLinked!.SessionId.Should().Be("session-b");
    }

    private IDatabase Database => _fixture.Redis.GetDatabase();

    private static string HashKey(string linkedId) => $"test:idx:linked-by-server:{linkedId}";

    private static string LegacyKey(string linkedId) => $"test:idx:linked:{linkedId}";

    /// <summary>Writes a session the way a 2.10.x store did: session string, one shared linkedid string, active set.</summary>
    private async Task SeedAs210Async(CallSession session)
    {
        var json = JsonSerializer.Serialize(
            CallSessionSnapshot.FromSession(session), SessionJsonContext.Default.CallSessionSnapshot);
        await Database.StringSetAsync($"test:session:{session.SessionId}", json);
        await Database.StringSetAsync(LegacyKey(session.LinkedId), session.SessionId);
        await Database.SetAddAsync("test:sessions:active", session.SessionId);
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
    public async Task SaveBatchAsync_ShouldIndexEachServersSession_WhenTwoServersShareOneLinkedId()
    {
        var store = CreateStore();
        await store.SaveBatchAsync(
            [CreateSession("session-a", "server-a"), CreateSession("session-b", "server-b")],
            CancellationToken.None);

        var a = await store.GetByLinkedIdAsync("server-a", SharedLinkedId, CancellationToken.None);
        var b = await store.GetByLinkedIdAsync("server-b", SharedLinkedId, CancellationToken.None);
        var unscoped = await store.GetByLinkedIdAsync(SharedLinkedId, CancellationToken.None);

        new { A = a?.SessionId, B = b?.SessionId, Unscoped = unscoped?.SessionId }.Should().BeEquivalentTo(
            new { A = "session-a", B = "session-b", Unscoped = default(string) },
            "a batch indexes each server's session on its own, and two servers make the unscoped lookup ambiguous");
    }

    [Fact]
    public async Task DeleteAsync_ShouldLeaveTheNewerSessionFound_WhenAnOlderSessionOfTheSameServerIsDeleted()
    {
        var store = CreateStore();
        await store.SaveAsync(CreateSession("session-old", "server-a"), CancellationToken.None);
        await store.SaveAsync(CreateSession("session-new", "server-a"), CancellationToken.None);

        await store.DeleteAsync("session-old", CancellationToken.None);

        var unscoped = await store.GetByLinkedIdAsync(SharedLinkedId, CancellationToken.None);
        var scoped = await store.GetByLinkedIdAsync("server-a", SharedLinkedId, CancellationToken.None);
        new { Unscoped = unscoped?.SessionId, Scoped = scoped?.SessionId }.Should().BeEquivalentTo(
            new { Unscoped = "session-new", Scoped = "session-new" },
            "the server's index entry names the newer session, so deleting the older one leaves it");
    }

    [Fact]
    public async Task SaveAsync_ShouldLetTheIndexExpire_WhenEveryServersSessionHasEnded()
    {
        var store = CreateStore(completedRetention: TimeSpan.FromSeconds(2));
        var a = CreateSession("session-a", "server-a");
        var b = CreateSession("session-b", "server-b");
        await store.SaveAsync(a, CancellationToken.None);
        await store.SaveAsync(b, CancellationToken.None);

        a.Transition(CallSessionState.Failed);
        await store.SaveAsync(a, CancellationToken.None);
        b.Transition(CallSessionState.Failed);
        await store.SaveAsync(b, CancellationToken.None);
        var existedBefore = await Database.KeyExistsAsync(HashKey(SharedLinkedId));
        await Task.Delay(TimeSpan.FromSeconds(3)); // fence-allow: SETTLE — outlasts the 2 s retention, whose expiry Redis runs on its own clock
        var existsAfter = await Database.KeyExistsAsync(HashKey(SharedLinkedId));

        new { ExistedBefore = existedBefore, ExistsAfter = existsAfter }.Should().BeEquivalentTo(
            new { ExistedBefore = true, ExistsAfter = false },
            "the index key holds both ended sessions during their retention, and expires with the last of them");
    }

    [Fact]
    public async Task SaveAsync_ShouldKeepTheEndedSessionFound_WhileItsRetentionLasts()
    {
        var store = CreateStore();
        var a = CreateSession("session-a", "server-a");
        await store.SaveAsync(a, CancellationToken.None);
        a.Transition(CallSessionState.Failed);
        await store.SaveAsync(a, CancellationToken.None);

        var unscoped = await store.GetByLinkedIdAsync(SharedLinkedId, CancellationToken.None);
        var scoped = await store.GetByLinkedIdAsync("server-a", SharedLinkedId, CancellationToken.None);

        new { Unscoped = unscoped?.SessionId, Scoped = scoped?.SessionId }.Should().BeEquivalentTo(
            new { Unscoped = "session-a", Scoped = "session-a" },
            "an ended session is kept for its retention and stays found by linkedid while it is kept");
    }

    [Fact]
    public async Task GetByLinkedIdAsync_ShouldFindASessionIndexedBy210_WhenNoNewEntryExists()
    {
        var store = CreateStore();
        await SeedAs210Async(CreateSession("session-b", "server-b"));

        var unscoped = await store.GetByLinkedIdAsync(SharedLinkedId, CancellationToken.None);
        var scopedB = await store.GetByLinkedIdAsync("server-b", SharedLinkedId, CancellationToken.None);
        var scopedA = await store.GetByLinkedIdAsync("server-a", SharedLinkedId, CancellationToken.None);

        new { Unscoped = unscoped?.SessionId, ScopedB = scopedB?.SessionId, ScopedA = scopedA?.SessionId }
            .Should().BeEquivalentTo(
                new { Unscoped = "session-b", ScopedB = "session-b", ScopedA = default(string) },
                "a session an earlier version indexed is found through its index entry, for its own server only");
    }

    [Fact]
    public async Task GetByLinkedIdAsync_ShouldCountASessionIndexedBy210AsItsServer_WhenAnotherServerSavesTheLinkedId()
    {
        var store = CreateStore();
        await SeedAs210Async(CreateSession("session-b", "server-b"));
        await store.SaveAsync(CreateSession("session-a", "server-a"), CancellationToken.None);

        var unscoped = await store.GetByLinkedIdAsync(SharedLinkedId, CancellationToken.None);
        var scopedB = await store.GetByLinkedIdAsync("server-b", SharedLinkedId, CancellationToken.None);
        var scopedA = await store.GetByLinkedIdAsync("server-a", SharedLinkedId, CancellationToken.None);
        await store.DeleteAsync("session-a", CancellationToken.None);
        var afterDelete = await store.GetByLinkedIdAsync(SharedLinkedId, CancellationToken.None);
        var legacy = await Database.StringGetAsync(LegacyKey(SharedLinkedId));

        new
        {
            Unscoped = unscoped?.SessionId,
            ScopedB = scopedB?.SessionId,
            ScopedA = scopedA?.SessionId,
            AfterDelete = afterDelete?.SessionId,
            Legacy = legacy.ToString(),
        }.Should().BeEquivalentTo(
            new
            {
                Unscoped = default(string),
                ScopedB = "session-b",
                ScopedA = "session-a",
                AfterDelete = "session-b",
                Legacy = "session-b",
            },
            "the earlier version's entry counts as its session's server, and this store never writes over it");
    }

    [Fact]
    public async Task SaveAsync_ShouldGiveThe210IndexKeyItsRetention_WhenTheSessionItNamesEnds()
    {
        var store = CreateStore(completedRetention: TimeSpan.FromMinutes(5));
        var b = CreateSession("session-b", "server-b");
        await SeedAs210Async(b);
        await SeedAs210Async(new CallSession("session-x", "other-linked", "server-b", CallDirection.Inbound));
        var y = new CallSession("session-y", "other-linked", "server-a", CallDirection.Inbound);

        b.Transition(CallSessionState.Failed);
        await store.SaveAsync(b, CancellationToken.None);
        y.Transition(CallSessionState.Failed);
        await store.SaveAsync(y, CancellationToken.None);

        var ownTtl = await Database.KeyTimeToLiveAsync(LegacyKey(SharedLinkedId));
        var otherTtl = await Database.KeyTimeToLiveAsync(LegacyKey("other-linked"));
        ownTtl.Should().NotBeNull("the entry names the session that ended, so it gets that session's retention");
        ownTtl!.Value.Should().BeGreaterThan(TimeSpan.Zero).And.BeLessThanOrEqualTo(TimeSpan.FromMinutes(5));
        otherTtl.Should().BeNull("the entry names another session, which has not ended");
    }

    [Fact]
    public async Task DeleteAsync_ShouldRemoveThe210IndexKey_OnlyWhenItNamesTheDeletedSession()
    {
        var store = CreateStore();
        await SeedAs210Async(CreateSession("session-b", "server-b"));
        await SeedAs210Async(new CallSession("session-x", "other-linked", "server-b", CallDirection.Inbound));
        await store.SaveAsync(
            new CallSession("session-y", "other-linked", "server-a", CallDirection.Inbound), CancellationToken.None);

        await store.DeleteAsync("session-b", CancellationToken.None);
        await store.DeleteAsync("session-y", CancellationToken.None);

        var own = await Database.KeyExistsAsync(LegacyKey(SharedLinkedId));
        var other = await Database.StringGetAsync(LegacyKey("other-linked"));
        new { Own = own, Other = other.ToString() }.Should().BeEquivalentTo(
            new { Own = false, Other = "session-x" },
            "a delete removes the earlier version's entry only when it names the deleted session");
    }

    [Fact]
    public async Task SaveAsync_ShouldSucceed_AfterTheServerForgetsItsScripts()
    {
        var store = CreateStore();
        await store.SaveAsync(CreateSession("session-a", "server-a"), CancellationToken.None);
        var server = _fixture.Redis.GetServer(_fixture.Redis.GetEndPoints()[0]);
        await server.ScriptFlushAsync();

        await store.SaveAsync(CreateSession("session-b", "server-b"), CancellationToken.None);
        await store.DeleteAsync("session-a", CancellationToken.None);

        var found = await store.GetByLinkedIdAsync(SharedLinkedId, CancellationToken.None);
        found.Should().NotBeNull("the store's scripts are sent again after the server forgot them");
        found!.SessionId.Should().Be("session-b");
    }
}

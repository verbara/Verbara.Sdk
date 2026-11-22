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

    /// <summary>The session id a lookup that finds nothing answers with, typed for the anonymous expectations.</summary>
    private const string? NoSession = null;

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

    private static CallSession CreateSession(string sessionId, string serverId, string linkedId)
    {
        var session = new CallSession(sessionId, linkedId, serverId, CallDirection.Inbound);
        session.Transition(CallSessionState.Dialing);
        return session;
    }

    [Fact]
    public async Task SaveAsync_ShouldClearTheIndexExpiry_WhenALiveSessionIsSavedAfterTheOtherServersEnded()
    {
        var store = CreateStore(completedRetention: TimeSpan.FromSeconds(2));
        var a = CreateSession("session-a", "server-a");
        await store.SaveAsync(a, CancellationToken.None);
        a.Transition(CallSessionState.Failed);
        await store.SaveAsync(a, CancellationToken.None);

        await store.SaveAsync(CreateSession("session-b", "server-b"), CancellationToken.None);
        await Task.Delay(TimeSpan.FromSeconds(3)); // fence-allow: SETTLE — outlasts the 2 s retention, whose expiry Redis runs on its own clock

        var ttl = await Database.KeyTimeToLiveAsync(HashKey(SharedLinkedId));
        var found = await store.GetByLinkedIdAsync(SharedLinkedId, CancellationToken.None);
        new { Found = found?.SessionId, HasTtl = ttl.HasValue }.Should().BeEquivalentTo(
            new { Found = "session-b", HasTtl = false },
            "a live save clears the expiry the other server's ended session left on the index key");
    }

    [Fact]
    public async Task DeleteAsync_ShouldRemoveTheDeletedSessionsIndexField_AndLetTheKeyExpireAfterTheOtherEnds()
    {
        var store = CreateStore(completedRetention: TimeSpan.FromSeconds(30));
        await store.SaveAsync(CreateSession("session-a", "server-a"), CancellationToken.None);
        var b = CreateSession("session-b", "server-b");
        await store.SaveAsync(b, CancellationToken.None);

        await store.DeleteAsync("session-a", CancellationToken.None);
        var fieldA = await Database.HashExistsAsync(HashKey(SharedLinkedId), "server-a");
        b.Transition(CallSessionState.Failed);
        await store.SaveAsync(b, CancellationToken.None);
        var ttl = await Database.KeyTimeToLiveAsync(HashKey(SharedLinkedId));

        new { FieldA = fieldA, HasTtl = ttl.HasValue }.Should().BeEquivalentTo(
            new { FieldA = false, HasTtl = true },
            "a delete removes its server's field, so the key expires once the remaining session has ended");
    }

    [Fact]
    public async Task SaveAsync_ShouldKeepBothServersEntries_WhenTwoServersWriteTheSameLinkedIdsConcurrently()
    {
        await using var muxB = await ConnectionMultiplexer.ConnectAsync(_fixture.ConnectionString);
        var storeA = CreateStore(completedRetention: TimeSpan.FromMinutes(5));
        var storeB = new RedisSessionStore(muxB, new RedisSessionStoreOptions
        {
            KeyPrefix = "test:",
            CompletedRetention = TimeSpan.FromMinutes(5),
        });
        const int n = 300;
        var sa = Enumerable.Range(0, n).Select(i => CreateSession($"a-{i}", "server-a", $"L-{i}")).ToArray();
        var sb = Enumerable.Range(0, n).Select(i => CreateSession($"b-{i}", "server-b", $"L-{i}")).ToArray();

        await Task.WhenAll(
            Task.Run(async () => { foreach (var s in sa) await storeA.SaveAsync(s, CancellationToken.None); }),
            Task.Run(async () => { foreach (var s in sb) await storeB.SaveAsync(s, CancellationToken.None); }),
            Task.Run(async () => await storeA.SaveBatchAsync(sa, CancellationToken.None)),
            Task.Run(async () => await storeB.SaveBatchAsync(sb, CancellationToken.None)));

        // server-a ends every session while server-b re-saves half of its own live and deletes the other half.
        foreach (var s in sa) s.Transition(CallSessionState.Failed);
        await Task.WhenAll(
            Task.Run(async () => await storeA.SaveBatchAsync(sa, CancellationToken.None)),
            Task.Run(async () => { for (var i = 1; i < n; i += 2) await storeB.SaveAsync(sb[i], CancellationToken.None); }),
            Task.Run(async () => { for (var i = 0; i < n; i += 2) await storeB.DeleteAsync($"b-{i}", CancellationToken.None); }));

        var bad = new List<string>();
        for (var i = 0; i < n; i++)
        {
            var a = await storeA.GetByLinkedIdAsync("server-a", $"L-{i}", CancellationToken.None);
            var b = await storeA.GetByLinkedIdAsync("server-b", $"L-{i}", CancellationToken.None);
            var u = await storeA.GetByLinkedIdAsync($"L-{i}", CancellationToken.None);
            var ttl = await Database.KeyTimeToLiveAsync(HashKey($"L-{i}"));
            var bDeleted = i % 2 == 0;
            var ok = a?.SessionId == $"a-{i}"
                && (bDeleted ? b is null : b?.SessionId == $"b-{i}")
                && (bDeleted ? u?.SessionId == $"a-{i}" : u is null)
                && (bDeleted ? ttl.HasValue : !ttl.HasValue);
            if (!ok) bad.Add($"L-{i}: a={a?.SessionId} b={b?.SessionId} u={u?.SessionId} ttl={ttl}");
        }

        bad.Should().BeEmpty("each server's entry is written, removed and expired on its own, whatever the other does meanwhile");
    }

    [Fact]
    public async Task GetByLinkedIdAsync_ShouldCountThe210EntryOnce_WhenItsServerAlsoHasANewEntry()
    {
        var store = CreateStore();
        await SeedAs210Async(CreateSession("session-old", "server-b"));
        await store.SaveAsync(CreateSession("session-new", "server-b"), CancellationToken.None);

        var unscoped = await store.GetByLinkedIdAsync(SharedLinkedId, CancellationToken.None);
        var scoped = await store.GetByLinkedIdAsync("server-b", SharedLinkedId, CancellationToken.None);

        new { Unscoped = unscoped?.SessionId, Scoped = scoped?.SessionId }.Should().BeEquivalentTo(
            new { Unscoped = "session-new", Scoped = "session-new" },
            "the earlier version's entry and the new one belong to the same server, so one server holds the linkedid");
    }

    [Fact]
    public async Task SaveAsync_ShouldClearAnIndexExpiry_WhenAnotherServersEntryIsLive()
    {
        var store = CreateStore();
        // The state a live save of server-b leaves when its connection is cut between its two index commands:
        // a live field, and the expiry an earlier ended session left on the key.
        var b = CreateSession("session-b", "server-b");
        var json = JsonSerializer.Serialize(CallSessionSnapshot.FromSession(b), SessionJsonContext.Default.CallSessionSnapshot);
        await Database.StringSetAsync("test:session:session-b", json);
        await Database.HashSetAsync(HashKey(SharedLinkedId), "server-b", "0|session-b");
        await Database.KeyExpireAsync(HashKey(SharedLinkedId), TimeSpan.FromMinutes(1));

        var a = CreateSession("session-a", "server-a");
        a.Transition(CallSessionState.Failed);
        await store.SaveAsync(a, CancellationToken.None);

        var ttl = await Database.KeyTimeToLiveAsync(HashKey(SharedLinkedId));
        ttl.Should().BeNull("server-b's entry is live, so the next index script clears the key's expiry");
    }

    private async Task<Dictionary<string, long>> ScriptCallCountsAsync()
    {
        var server = _fixture.Redis.GetServer(_fixture.Redis.GetEndPoints()[0]);
        var info = await server.InfoAsync("commandstats");
        var counts = new Dictionary<string, long>(StringComparer.Ordinal)
        {
            ["cmdstat_eval"] = 0,
            ["cmdstat_evalsha"] = 0,
            ["cmdstat_script|load"] = 0,
        };
        var tracked = info.SelectMany(static group => group).Where(pair => counts.ContainsKey(pair.Key)).ToList();
        foreach (var pair in tracked)
        {
            var calls = pair.Value.Split(',')[0];
            counts[pair.Key] = long.Parse(calls["calls=".Length..], System.Globalization.CultureInfo.InvariantCulture);
        }

        return counts;
    }

    [Fact]
    public async Task SaveBatchAsync_ShouldSendNoScript_WhenEverySessionIsLive()
    {
        var store = CreateStore();
        var before = await ScriptCallCountsAsync();

        await store.SaveAsync(CreateSession("session-a", "server-a"), CancellationToken.None);
        await store.SaveBatchAsync(
            [CreateSession("session-b", "server-b"), CreateSession("session-c", "server-c", "other-linked")],
            CancellationToken.None);

        var after = await ScriptCallCountsAsync();
        after.Should().BeEquivalentTo(before, "a live save writes its index entry with plain commands");
        (await store.GetByLinkedIdAsync("server-b", SharedLinkedId, CancellationToken.None))?.SessionId
            .Should().Be("session-b");
    }

    [Fact]
    public async Task SaveBatchAsync_ShouldIndexEndedSessions_AfterTheServerForgetsItsScripts()
    {
        var store = CreateStore();
        var warm = CreateSession("session-warm", "server-a", "other-linked");
        warm.Transition(CallSessionState.Failed);
        await store.SaveAsync(warm, CancellationToken.None);
        var server = _fixture.Redis.GetServer(_fixture.Redis.GetEndPoints()[0]);
        await server.ScriptFlushAsync();

        var a = CreateSession("session-a", "server-a");
        var b = CreateSession("session-b", "server-b");
        a.Transition(CallSessionState.Failed);
        b.Transition(CallSessionState.Failed);
        await store.SaveBatchAsync([a, b], CancellationToken.None).AsTask().WaitAsync(TimeSpan.FromSeconds(30));

        var foundA = await store.GetByLinkedIdAsync("server-a", SharedLinkedId, CancellationToken.None);
        var foundB = await store.GetByLinkedIdAsync("server-b", SharedLinkedId, CancellationToken.None);
        var ttl = await Database.KeyTimeToLiveAsync(HashKey(SharedLinkedId));
        new { A = foundA?.SessionId, B = foundB?.SessionId, HasTtl = ttl.HasValue }.Should().BeEquivalentTo(
            new { A = "session-a", B = "session-b", HasTtl = true },
            "the store's scripts are sent again after the server forgot them, and both ended sessions are indexed");
    }

    private Task<bool> WriteSessionKeyAsync(CallSession session) =>
        Database.StringSetAsync(
            $"test:session:{session.SessionId}",
            JsonSerializer.Serialize(CallSessionSnapshot.FromSession(session), SessionJsonContext.Default.CallSessionSnapshot));

    private static async Task<CallSession?> LookUpAsync(RedisSessionStore store, string? serverId, string linkedId) =>
        serverId is null
            ? await store.GetByLinkedIdAsync(linkedId, CancellationToken.None)
            : await store.GetByLinkedIdAsync(serverId, linkedId, CancellationToken.None);

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task GetByLinkedIdAsync_ShouldKeepALiveIndexEntry_WhenItsSessionKeyIsWrittenLater(bool scoped)
    {
        var store = CreateStore();
        var server = scoped ? "server-b" : null;
        // The order a Redis Cluster allows: the index entry (one node) is there before the session key (another node).
        await Database.HashSetAsync(HashKey(SharedLinkedId), "server-b", "0|session-b");

        var early = await LookUpAsync(store, server, SharedLinkedId);
        await WriteSessionKeyAsync(CreateSession("session-b", "server-b"));
        var late = await LookUpAsync(store, server, SharedLinkedId);

        new { Early = early?.SessionId, Late = late?.SessionId, Field = (string?)await Database.HashGetAsync(HashKey(SharedLinkedId), "server-b") }
            .Should().BeEquivalentTo(
                new { Early = NoSession, Late = "session-b", Field = "0|session-b" },
                "a lookup that does not find an entry's session yet leaves the entry, so the next lookup finds the session");
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task GetByLinkedIdAsync_ShouldKeepAnEndedIndexEntry_WhenItsSessionKeyIsMissing(bool scoped)
    {
        var store = CreateStore();
        var expiry = DateTimeOffset.UtcNow.AddMinutes(5).ToUnixTimeMilliseconds();
        await Database.HashSetAsync(HashKey(SharedLinkedId), "server-b", $"{expiry}|session-b");

        var found = await LookUpAsync(store, scoped ? "server-b" : null, SharedLinkedId);

        found.Should().BeNull();
        (await Database.HashExistsAsync(HashKey(SharedLinkedId), "server-b")).Should().BeTrue(
            "a lookup only reads: an ended entry leaves with its retention, not with a lookup");
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task GetByLinkedIdAsync_ShouldKeepAnIndexEntry_WhenTheSessionItNamesBelongsToAnotherLinkedIdOrServer(bool scoped)
    {
        var store = CreateStore();
        await Database.HashSetAsync(
            HashKey(SharedLinkedId), [new HashEntry("server-b", "0|session-x"), new HashEntry("server-c", "0|session-y")]);
        await WriteSessionKeyAsync(CreateSession("session-x", "server-b", "other-linked"));
        await WriteSessionKeyAsync(CreateSession("session-y", "server-z"));

        var b = await LookUpAsync(store, scoped ? "server-b" : null, SharedLinkedId);
        var c = scoped ? await LookUpAsync(store, "server-c", SharedLinkedId) : null;

        new { B = b?.SessionId, C = c?.SessionId, Fields = await Database.HashLengthAsync(HashKey(SharedLinkedId)) }
            .Should().BeEquivalentTo(
                new { B = NoSession, C = NoSession, Fields = 2L },
                "an entry whose session holds another linkedid or server is not counted, and a lookup removes nothing");
    }

    [Fact]
    public async Task GetByLinkedIdAsync_ShouldSendNoScript_WhenAnIndexEntryNamesNoStoredSession()
    {
        var store = CreateStore();
        await store.SaveAsync(CreateSession("session-a", "server-a"), CancellationToken.None);
        await Database.HashSetAsync(HashKey(SharedLinkedId), "server-b", "0|session-b");
        var before = await ScriptCallCountsAsync();

        var unscoped = await store.GetByLinkedIdAsync(SharedLinkedId, CancellationToken.None);
        var scopedB = await store.GetByLinkedIdAsync("server-b", SharedLinkedId, CancellationToken.None);

        var after = await ScriptCallCountsAsync();
        new { Unscoped = unscoped?.SessionId, ScopedB = scopedB?.SessionId, Scripts = after }
            .Should().BeEquivalentTo(
                new { Unscoped = "session-a", ScopedB = NoSession, Scripts = before },
                "a lookup only reads, so it sends no script even over an entry whose session is not stored");
    }
}

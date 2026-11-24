using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using Verbara.Sdk.Enums;
using Verbara.Sdk.Sessions;
using FluentAssertions;
using StackExchange.Redis;
using Xunit;

namespace Verbara.Sdk.Sessions.Redis.Tests;

/// <summary>
/// With <see cref="RedisSessionStoreOptions.ActiveRecordTtl"/> set and the store armed, a live session's record and its
/// LinkedID index entry stay only while the process that holds the call refreshes them; unset or never armed, the store
/// sends the commands it sent before the option existed. The short expiries come through the internal constructor,
/// which skips the 30-second floor the registrations enforce.
/// </summary>
[Collection("Redis")]
[Trait("Category", "Integration")]
public sealed class RedisSessionStoreActiveRecordTtlTests : IAsyncLifetime
{
    private const string Prefix = "ttl:";

    private readonly RedisFixture _fixture;

    public RedisSessionStoreActiveRecordTtlTests(RedisFixture fixture) => _fixture = fixture;

    public Task InitializeAsync() => _fixture.FlushAsync();
    public Task DisposeAsync() => Task.CompletedTask;

    private IDatabase Db => _fixture.Redis.GetDatabase();

    private IServer Server => _fixture.Redis.GetServer(_fixture.Redis.GetEndPoints()[0]);

    private RedisSessionStore CreateStore(TimeSpan? ttl, TimeSpan? retention = null, bool arm = true)
    {
        var store = new RedisSessionStore(_fixture.Redis, new RedisSessionStoreOptions
        {
            KeyPrefix = Prefix,
            CompletedRetention = retention ?? TimeSpan.FromMinutes(10),
            ActiveRecordTtl = ttl,
        });
        if (arm) store.ArmMaintenance();
        return store;
    }

    private static CallSession Live(string sessionId, string linkedId = "L1", string serverId = "server-a")
    {
        var session = new CallSession(sessionId, linkedId, serverId, CallDirection.Inbound);
        session.Transition(CallSessionState.Dialing);
        return session;
    }

    private static CallSession Ended(string sessionId, string linkedId = "L1", string serverId = "server-a")
    {
        var session = Live(sessionId, linkedId, serverId);
        session.Transition(CallSessionState.Failed);
        return session;
    }

    private static RedisKey SessionKey(string id) => Prefix + "session:" + id;
    private static RedisKey IndexKey(string linkedId) => Prefix + "idx:linked-by-server:" + linkedId;
    private static RedisKey ActiveSet => Prefix + "sessions:active";

    private async Task<long> PttlAsync(RedisKey key) => (long)await Db.ExecuteAsync("PTTL", key);

    private async Task ResetStatsAsync() => await Server.ExecuteAsync("CONFIG", "RESETSTAT");

    /// <summary>Calls per command since the last reset, from <c>INFO commandstats</c>.</summary>
    private async Task<Dictionary<string, long>> CommandCallsAsync()
    {
        var info = (string)(await Server.ExecuteAsync("INFO", "commandstats"))!;
        var calls = new Dictionary<string, long>(StringComparer.Ordinal);
        foreach (var text in info.Split('\n').Select(static l => l.Trim())
                     .Where(static l => l.StartsWith("cmdstat_", StringComparison.Ordinal)))
        {
            var colon = text.IndexOf(':', StringComparison.Ordinal);
            var name = text[8..colon];
            var field = text[(colon + 1)..].Split(',')[0];
            calls[name] = long.Parse(field["calls=".Length..], CultureInfo.InvariantCulture);
        }

        return calls;
    }

    private static long Calls(Dictionary<string, long> calls, string name) => calls.GetValueOrDefault(name);

    // Redis names a loaded script by its SHA-1: the digest identifies the script, it protects nothing.
#pragma warning disable CA5350
    private static string Sha1(string script) =>
        Convert.ToHexStringLower(SHA1.HashData(Encoding.UTF8.GetBytes(script)));
#pragma warning restore CA5350

    private async Task<bool> ScriptLoadedAsync(string script)
    {
        var result = (RedisResult[])(await Server.ExecuteAsync("SCRIPT", "EXISTS", Sha1(script)))!;
        return (long)result[0] == 1;
    }

    [Fact]
    public async Task SaveAsync_ShouldSendTodaysCommands_WhenActiveRecordTtlIsNotSet()
    {
        var store = CreateStore(ttl: null);
        await Server.ExecuteAsync("SCRIPT", "FLUSH");
        await ResetStatsAsync();

        await store.SaveAsync(Live("s1"), CancellationToken.None);

        var calls = await CommandCallsAsync();
        Calls(calls, "set").Should().Be(1);
        Calls(calls, "sadd").Should().Be(1);
        Calls(calls, "hset").Should().Be(1);
        Calls(calls, "persist").Should().Be(1);
        (Calls(calls, "pexpire") + Calls(calls, "expire") + Calls(calls, "pexpireat")).Should().Be(0);
        (await PttlAsync(SessionKey("s1"))).Should().Be(-1);
        (await PttlAsync(IndexKey("L1"))).Should().Be(-1);

        // A terminal save and a delete run the script the store ran before the option existed, never the armed one.
        await store.SaveAsync(Ended("s2", linkedId: "L2"), CancellationToken.None);
        await store.SaveAsync(Live("s3", linkedId: "L3"), CancellationToken.None);
        await store.DeleteAsync("s3", CancellationToken.None);
        (await ScriptLoadedAsync(RedisSessionStore.LinkedIndexScript)).Should().BeTrue();
        (await ScriptLoadedAsync(RedisSessionStore.LinkedIndexScriptArmed)).Should().BeFalse();
        var after = await CommandCallsAsync();
        (Calls(after, "pexpire") + Calls(after, "expire")).Should().Be(0);
    }

    [Fact]
    public async Task SaveAsync_ShouldGiveALiveSessionAndItsIndexAnExpiry_WhenArmed()
    {
        var ttl = TimeSpan.FromSeconds(30);
        var store = CreateStore(ttl, retention: TimeSpan.FromSeconds(20));

        await store.SaveAsync(Live("s1"), CancellationToken.None);

        (await PttlAsync(SessionKey("s1"))).Should().BeInRange(1, 30_000);
        (await PttlAsync(IndexKey("L1"))).Should().BeInRange(1, 30_000);
    }

    [Fact]
    public async Task SaveAsync_ShouldNotExpireALiveSession_WhenTheStoreWasNeverArmed()
    {
        var store = CreateStore(TimeSpan.FromSeconds(30), arm: false);

        await store.SaveAsync(Live("s1"), CancellationToken.None);

        (await PttlAsync(SessionKey("s1"))).Should().Be(-1);
        (await PttlAsync(IndexKey("L1"))).Should().Be(-1);
    }

    [Fact]
    public async Task MaintainAsync_ShouldKeepAHeldSessionFindable_PastItsExpiry()
    {
        var ttl = TimeSpan.FromMilliseconds(1500);
        var store = CreateStore(ttl, retention: ttl);
        var session = Live("s1");
        await store.SaveAsync(session, CancellationToken.None);

        await Task.Delay(1000); // fence-allow: SETTLE — lets Redis's own clock pass the key's expiry, the behaviour under test
        await store.MaintainAsync([session], CancellationToken.None);
        await Task.Delay(1200); // fence-allow: SETTLE — lets Redis's own clock pass the key's expiry, the behaviour under test

        (await store.GetAsync("s1", CancellationToken.None)).Should().NotBeNull();
        (await store.GetByLinkedIdAsync("L1", CancellationToken.None))!.SessionId.Should().Be("s1");
        (await store.GetByLinkedIdAsync("server-a", "L1", CancellationToken.None))!.SessionId.Should().Be("s1");
    }

    [Fact]
    public async Task ALiveSessionNobodyRefreshes_ShouldLeaveWithItsIndexEntry()
    {
        var ttl = TimeSpan.FromMilliseconds(1000);
        var store = CreateStore(ttl, retention: ttl);
        await store.SaveAsync(Live("s1"), CancellationToken.None);

        await Task.Delay(1600); // fence-allow: SETTLE — lets Redis's own clock pass the key's expiry, the behaviour under test

        (await store.GetAsync("s1", CancellationToken.None)).Should().BeNull();
        (await Db.KeyExistsAsync(IndexKey("L1"))).Should().BeFalse();
        (await store.GetByLinkedIdAsync("L1", CancellationToken.None)).Should().BeNull();
        (await store.GetByLinkedIdAsync("server-a", "L1", CancellationToken.None)).Should().BeNull();
    }

    [Fact]
    public async Task ADeadServersSession_ShouldAnswerNothing_WhileAServerSharingItsLinkedIdIsRefreshed()
    {
        var ttl = TimeSpan.FromMilliseconds(1000);
        var store = CreateStore(ttl, retention: ttl);
        var b = Live("b", serverId: "server-b");
        await store.SaveAsync(Live("a", serverId: "server-a"), CancellationToken.None);
        await store.SaveAsync(b, CancellationToken.None);

        // Only server-b's process refreshes: server-a's process is gone.
        await Task.Delay(700); // fence-allow: SETTLE — lets Redis's own clock run toward the keys' expiry, the behaviour under test
        await store.MaintainAsync([b], CancellationToken.None);
        await Task.Delay(700); // fence-allow: SETTLE — lets Redis's own clock pass a's expiry, the behaviour under test
        await store.MaintainAsync([b], CancellationToken.None);

        (await store.GetAsync("a", CancellationToken.None)).Should().BeNull();
        (await store.GetByLinkedIdAsync("server-a", "L1", CancellationToken.None)).Should().BeNull();
        (await store.GetByLinkedIdAsync("L1", CancellationToken.None))!.SessionId.Should().Be("b", "a's field no longer counts");
        (await store.GetByLinkedIdAsync("server-b", "L1", CancellationToken.None))!.SessionId.Should().Be("b");
        (await PttlAsync(IndexKey("L1"))).Should().BeInRange(1, 1000);
        // Not removed on its own: the field outlives its record for as long as the key does.
        (await Db.HashGetAsync(IndexKey("L1"), "server-a")).ToString().Should().Be("0|a");
    }

    [Fact]
    public async Task ADeadServersIndexField_ShouldLeaveWithTheKey_WhenTheLastServerSharingItsLinkedIdEnds()
    {
        var ttl = TimeSpan.FromMilliseconds(1000);
        var retention = TimeSpan.FromMilliseconds(2000);
        var store = CreateStore(ttl, retention);
        var b = Live("b", serverId: "server-b");
        await store.SaveAsync(Live("a", serverId: "server-a"), CancellationToken.None);
        await store.SaveAsync(b, CancellationToken.None);
        await Task.Delay(700); // fence-allow: SETTLE — lets Redis's own clock run toward the keys' expiry, the behaviour under test
        await store.MaintainAsync([b], CancellationToken.None);
        await Task.Delay(700); // fence-allow: SETTLE — lets Redis's own clock pass a's expiry, the behaviour under test
        (await Db.KeyExistsAsync(SessionKey("a"))).Should().BeFalse();

        await store.SaveAsync(Ended("b", serverId: "server-b"), CancellationToken.None);

        // The dead field still reads as live to the script, so the key keeps the later of the ttl and the retention.
        (await PttlAsync(IndexKey("L1"))).Should().BeInRange(1, 2000);
        await Task.Delay(2600); // fence-allow: SETTLE — lets Redis's own clock pass the index key's expiry, the behaviour under test
        (await Db.KeyExistsAsync(IndexKey("L1"))).Should().BeFalse();
    }

    [Fact]
    public async Task ALiveSessionNobodyRefreshes_ShouldAnswerNothing_BeforeItsIndexKeyLeaves_WhenRetentionIsLonger()
    {
        var store = CreateStore(TimeSpan.FromMilliseconds(1000), retention: TimeSpan.FromMilliseconds(3000));
        await store.SaveAsync(Live("s1"), CancellationToken.None);

        await Task.Delay(1600); // fence-allow: SETTLE — lets Redis's own clock pass the record's expiry, the behaviour under test

        (await store.GetAsync("s1", CancellationToken.None)).Should().BeNull();
        (await store.GetByLinkedIdAsync("L1", CancellationToken.None)).Should().BeNull();
        (await store.GetByLinkedIdAsync("server-a", "L1", CancellationToken.None)).Should().BeNull();
        (await PttlAsync(IndexKey("L1"))).Should().BeInRange(1, 3000, "the index key carries max(ttl, retention)");
        await Task.Delay(2000); // fence-allow: SETTLE — lets Redis's own clock pass the index key's expiry, the behaviour under test
        (await Db.KeyExistsAsync(IndexKey("L1"))).Should().BeFalse();
    }

    [Fact]
    public async Task ANewSessionOnADeadServer_ShouldReplaceItsStaleIndexField_AndBeFound()
    {
        var ttl = TimeSpan.FromMilliseconds(1000);
        var store = CreateStore(ttl, retention: TimeSpan.FromSeconds(5));
        await store.SaveAsync(Live("a", serverId: "server-a"), CancellationToken.None);
        await Task.Delay(1600); // fence-allow: SETTLE — lets Redis's own clock pass a's expiry, the behaviour under test
        (await Db.KeyExistsAsync(SessionKey("a"))).Should().BeFalse();
        (await Db.HashGetAsync(IndexKey("L1"), "server-a")).ToString().Should().Be("0|a", "the field outlives its record");

        await store.SaveAsync(Live("a2", serverId: "server-a"), CancellationToken.None);

        (await Db.HashGetAsync(IndexKey("L1"), "server-a")).ToString().Should().Be("0|a2");
        (await store.GetByLinkedIdAsync("L1", CancellationToken.None))!.SessionId.Should().Be("a2");
        (await store.GetByLinkedIdAsync("server-a", "L1", CancellationToken.None))!.SessionId.Should().Be("a2");
    }

    [Fact]
    public async Task MaintainAsync_ShouldSaveAHeldSessionAgain_WhenItsRecordExpired()
    {
        var ttl = TimeSpan.FromMilliseconds(1000);
        var store = CreateStore(ttl, retention: ttl);
        var session = Live("s1");
        await store.SaveAsync(session, CancellationToken.None);
        await Task.Delay(1600); // fence-allow: SETTLE — lets Redis's own clock pass the key's expiry, the behaviour under test
        (await Db.KeyExistsAsync(SessionKey("s1"))).Should().BeFalse();

        await store.MaintainAsync([session], CancellationToken.None);

        (await Db.KeyExistsAsync(SessionKey("s1"))).Should().BeTrue();
        (await Db.SetContainsAsync(ActiveSet, "s1")).Should().BeTrue();
        (await Db.HashGetAsync(IndexKey("L1"), "server-a")).ToString().Should().Be("0|s1");
        (await PttlAsync(SessionKey("s1"))).Should().BeInRange(1, 1000);
    }

    [Fact]
    public async Task MaintainAsync_ShouldRemoveOnlyTheActiveSetMembersWhoseSessionIsGone()
    {
        var store = CreateStore(TimeSpan.FromSeconds(30));
        await store.SaveAsync(Live("dead", linkedId: "L-dead"), CancellationToken.None);
        await store.SaveAsync(Live("alive", linkedId: "L-alive"), CancellationToken.None);
        await Db.KeyDeleteAsync(SessionKey("dead"));
        bool? aliveListedDuringThePurge = null;
        // Read between the removal and the recheck: a live member must never leave, not only come back.
        store.PurgeRecheckHook = async () => aliveListedDuringThePurge = await Db.SetContainsAsync(ActiveSet, "alive");

        await store.MaintainAsync([], CancellationToken.None);

        aliveListedDuringThePurge.Should().BeTrue();
        (await Db.SetContainsAsync(ActiveSet, "dead")).Should().BeFalse();
        (await Db.SetContainsAsync(ActiveSet, "alive")).Should().BeTrue();
        (await Db.KeyExistsAsync(SessionKey("alive"))).Should().BeTrue();
    }

    [Fact]
    public async Task MaintainAsync_ShouldPutAMemberBack_WhenItsSessionWasSavedDuringThePurge()
    {
        var store = CreateStore(TimeSpan.FromSeconds(30));
        await store.SaveAsync(Live("s1"), CancellationToken.None);
        var record = await Db.StringGetAsync(SessionKey("s1"));
        await Db.KeyDeleteAsync(SessionKey("s1"));
        var hookRan = false;
        // A save whose membership command landed before the purge's removal and whose key lands after it: only the
        // key is written here, so nothing but the purge's own recheck can bring the member back.
        store.PurgeRecheckHook = async () =>
        {
            hookRan = true;
            await Db.StringSetAsync(SessionKey("s1"), record, TimeSpan.FromSeconds(30));
        };

        await store.MaintainAsync([], CancellationToken.None);

        hookRan.Should().BeTrue();
        (await Db.SetContainsAsync(ActiveSet, "s1")).Should().BeTrue();
    }

    [Fact]
    public async Task MaintainAsync_ShouldGiveADeadline_ToAnActiveRecordWrittenWithoutOne()
    {
        // Written by a store without the option: no expiry on the record nor on its index key.
        await CreateStore(ttl: null).SaveAsync(Live("old"), CancellationToken.None);
        (await PttlAsync(SessionKey("old"))).Should().Be(-1);
        (await PttlAsync(IndexKey("L1"))).Should().Be(-1);
        var ttl = TimeSpan.FromMilliseconds(1000);
        var store = CreateStore(ttl, retention: ttl);

        await store.MaintainAsync([], CancellationToken.None);

        (await PttlAsync(SessionKey("old"))).Should().BeInRange(1, 1000);
        (await PttlAsync(IndexKey("L1"))).Should().BeInRange(1, 1000);
        await Task.Delay(1600); // fence-allow: SETTLE — lets Redis's own clock pass the key's expiry, the behaviour under test
        (await Db.KeyExistsAsync(SessionKey("old"))).Should().BeFalse();
        (await Db.KeyExistsAsync(IndexKey("L1"))).Should().BeFalse();
    }

    [Fact]
    public async Task MaintainAsync_ShouldGiveTheIndexKeyTheRetention_ToAnActiveRecordWrittenWithoutOne_WhenRetentionIsLonger()
    {
        await CreateStore(ttl: null, retention: TimeSpan.FromSeconds(3)).SaveAsync(Live("old"), CancellationToken.None);
        (await PttlAsync(SessionKey("old"))).Should().Be(-1);
        (await PttlAsync(IndexKey("L1"))).Should().Be(-1);
        var store = CreateStore(TimeSpan.FromMilliseconds(1000), retention: TimeSpan.FromSeconds(3));

        await store.MaintainAsync([], CancellationToken.None);

        (await PttlAsync(SessionKey("old"))).Should().BeInRange(1, 1000, "the record gets the live expiry");
        (await PttlAsync(IndexKey("L1"))).Should().BeInRange(1001, 3000, "the index key gets the longer of the two");
        await Task.Delay(1600); // fence-allow: SETTLE — lets Redis's own clock pass the record's expiry, the behaviour under test
        (await Db.KeyExistsAsync(SessionKey("old"))).Should().BeFalse();
        (await store.GetAsync("old", CancellationToken.None)).Should().BeNull();
        (await store.GetByLinkedIdAsync("L1", CancellationToken.None)).Should().BeNull();
        (await Db.KeyExistsAsync(IndexKey("L1"))).Should().BeTrue("the index key outlives the record up to the retention");
        await Task.Delay(2000); // fence-allow: SETTLE — lets Redis's own clock pass the index key's expiry, the behaviour under test
        (await Db.KeyExistsAsync(IndexKey("L1"))).Should().BeFalse();
    }

    [Fact]
    public async Task SaveAsync_ShouldKeepAnotherServersEndedEntry_WhenTheLiveOwnerStopsRefreshing()
    {
        var store = CreateStore(TimeSpan.FromMilliseconds(1000), retention: TimeSpan.FromSeconds(5));
        await store.SaveAsync(Ended("a", serverId: "server-a"), CancellationToken.None);
        await store.SaveAsync(Live("b", serverId: "server-b"), CancellationToken.None);

        (await PttlAsync(IndexKey("L1"))).Should().BeGreaterThan(4000);
        await Task.Delay(1600); // fence-allow: SETTLE — lets Redis's own clock pass the key's expiry, the behaviour under test

        (await store.GetAsync("b", CancellationToken.None)).Should().BeNull();
        (await store.GetByLinkedIdAsync("server-a", "L1", CancellationToken.None))!.SessionId.Should().Be("a");
        (await PttlAsync(IndexKey("L1"))).Should().BeGreaterThan(2000);
    }

    [Fact]
    public async Task SaveAsync_ShouldLeaveTheIndexAnExpiry_WhenOneServerEndsAndAnotherIsLive()
    {
        var store = CreateStore(TimeSpan.FromSeconds(3), retention: TimeSpan.FromSeconds(2));
        await store.SaveAsync(Live("b", serverId: "server-b"), CancellationToken.None);
        await store.SaveAsync(Live("a", serverId: "server-a"), CancellationToken.None);

        await store.SaveAsync(Ended("a", serverId: "server-a"), CancellationToken.None);

        (await PttlAsync(IndexKey("L1"))).Should().BeInRange(1, 3000);
        await store.DeleteAsync("a", CancellationToken.None);
        (await PttlAsync(IndexKey("L1"))).Should().BeInRange(1, 3000);
    }

    [Fact]
    public async Task MaintainAsync_ShouldNotTouchAnEndedSession()
    {
        var store = CreateStore(TimeSpan.FromSeconds(1), retention: TimeSpan.FromSeconds(30));
        var ended = Ended("s1");
        await store.SaveAsync(ended, CancellationToken.None);

        await store.MaintainAsync([ended], CancellationToken.None);

        (await PttlAsync(SessionKey("s1"))).Should().BeGreaterThan(25_000);
        (await PttlAsync(IndexKey("L1"))).Should().BeGreaterThan(25_000);
    }

    [Fact]
    public async Task MaintainAsync_ShouldSendTwoExpiresPerSessionAndNoScript()
    {
        const int count = 50;
        var store = CreateStore(TimeSpan.FromSeconds(30));
        var sessions = Enumerable.Range(0, count).Select(i => Live($"s{i}", linkedId: $"L{i}")).ToList();
        await store.SaveBatchAsync(sessions, CancellationToken.None);
        await store.MaintainAsync(sessions, CancellationToken.None); // the first run also cleans the active set
        await ResetStatsAsync();

        await store.MaintainAsync(sessions, CancellationToken.None);

        var calls = await CommandCallsAsync();
        (Calls(calls, "pexpire") + Calls(calls, "expire")).Should().Be(2 * count);
        (Calls(calls, "evalsha") + Calls(calls, "eval")).Should().Be(0);
        Calls(calls, "set").Should().Be(0);
        Calls(calls, "sadd").Should().Be(0);
        Calls(calls, "hset").Should().Be(0);
    }
}

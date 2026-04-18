using System.Text.Json;
using Asterisk.Sdk.Sessions.Extensions;
using Asterisk.Sdk.Sessions.Serialization;
using Microsoft.Extensions.Options;
using StackExchange.Redis;

namespace Asterisk.Sdk.Sessions.Redis;

/// <summary>
/// Redis-backed <see cref="SessionStoreBase"/>. Persists active sessions as JSON strings
/// keyed by <c>{prefix}session:{sessionId}</c>, maintains a secondary index by LinkedID,
/// and tracks the active/completed set for fast enumeration. All JSON is emitted via
/// <see cref="SessionJsonContext"/> (source-generated) so the store is AOT-safe.
/// </summary>
public sealed class RedisSessionStore : SessionStoreBase
{
    private static readonly CallSessionState[] TerminalStates =
        [CallSessionState.Completed, CallSessionState.Failed, CallSessionState.TimedOut];

    private readonly IConnectionMultiplexer _redis;
    private readonly RedisSessionStoreOptions _options;

    /// <summary>Create a new store from a multiplexer and options value.</summary>
    public RedisSessionStore(IConnectionMultiplexer redis, RedisSessionStoreOptions options)
    {
        ArgumentNullException.ThrowIfNull(redis);
        ArgumentNullException.ThrowIfNull(options);
        _redis = redis;
        _options = options;
    }

    /// <summary>Create a new store from a multiplexer and bound <see cref="IOptions{TOptions}"/>.</summary>
    public RedisSessionStore(IConnectionMultiplexer redis, IOptions<RedisSessionStoreOptions> options)
        : this(redis, (options ?? throw new ArgumentNullException(nameof(options))).Value)
    {
    }

    private IDatabase GetDatabase() => _redis.GetDatabase(_options.DatabaseIndex);

    private RedisKey SessionKey(string sessionId) =>
        $"{_options.KeyPrefix}session:{sessionId}";

    private RedisKey LinkedIndexKey(string linkedId) =>
        $"{_options.KeyPrefix}idx:linked:{linkedId}";

    private RedisKey ActiveSetKey() =>
        $"{_options.KeyPrefix}sessions:active";

    private RedisKey CompletedSetKey() =>
        $"{_options.KeyPrefix}sessions:completed";

    private static bool IsTerminal(CallSessionState state) =>
        Array.IndexOf(TerminalStates, state) >= 0;

    private static string Serialize(CallSessionSnapshot snapshot) =>
        JsonSerializer.Serialize(snapshot, SessionJsonContext.Default.CallSessionSnapshot);

    private static CallSessionSnapshot? Deserialize(RedisValue value) =>
        value.IsNullOrEmpty
            ? null
            : JsonSerializer.Deserialize(value.ToString(), SessionJsonContext.Default.CallSessionSnapshot);

    /// <inheritdoc />
    public override async ValueTask SaveAsync(CallSession session, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(session);

        var snapshot = CallSessionSnapshot.FromSession(session);
        var json = Serialize(snapshot);
        var db = GetDatabase();
        var batch = db.CreateBatch();
        var sessionKey = SessionKey(session.SessionId);

        var tasks = new List<Task>();

        if (IsTerminal(snapshot.State))
        {
            var completedAtMs = (snapshot.CompletedAt ?? DateTimeOffset.UtcNow).ToUnixTimeMilliseconds();

            tasks.Add(batch.StringSetAsync(sessionKey, json, _options.CompletedRetention));
            tasks.Add(batch.StringSetAsync(
                LinkedIndexKey(snapshot.LinkedId), session.SessionId, _options.CompletedRetention));
            tasks.Add(batch.SetRemoveAsync(ActiveSetKey(), session.SessionId));
            tasks.Add(batch.SortedSetAddAsync(CompletedSetKey(), session.SessionId, completedAtMs));
        }
        else
        {
            tasks.Add(batch.StringSetAsync(sessionKey, json));
            tasks.Add(batch.StringSetAsync(LinkedIndexKey(snapshot.LinkedId), session.SessionId));
            tasks.Add(batch.SetAddAsync(ActiveSetKey(), session.SessionId));
        }

        batch.Execute();
        await Task.WhenAll(tasks).ConfigureAwait(false);

        // Trim stale completed entries
        if (IsTerminal(snapshot.State))
        {
            var cutoff = DateTimeOffset.UtcNow.Add(-_options.CompletedRetention).ToUnixTimeMilliseconds();
            var stale = await db.SortedSetRangeByScoreAsync(
                CompletedSetKey(), double.NegativeInfinity, cutoff).ConfigureAwait(false);

            if (stale.Length > 0)
            {
                await db.SortedSetRemoveAsync(CompletedSetKey(), stale).ConfigureAwait(false);
            }
        }
    }

    /// <inheritdoc />
    public override async ValueTask<CallSession?> GetAsync(string sessionId, CancellationToken ct)
    {
        var db = GetDatabase();
        var value = await db.StringGetAsync(SessionKey(sessionId)).ConfigureAwait(false);
        var snapshot = Deserialize(value);
        return snapshot?.ToSession();
    }

    /// <inheritdoc />
    public override async ValueTask<CallSession?> GetByLinkedIdAsync(string linkedId, CancellationToken ct)
    {
        var db = GetDatabase();
        var sessionId = await db.StringGetAsync(LinkedIndexKey(linkedId)).ConfigureAwait(false);
        if (sessionId.IsNullOrEmpty) return null;
        return await GetAsync(sessionId.ToString(), ct).ConfigureAwait(false);
    }

    /// <inheritdoc />
    public override async ValueTask<IEnumerable<CallSession>> GetActiveAsync(CancellationToken ct)
    {
        var db = GetDatabase();
        var sessions = new List<CallSession>();

        // Cursor-based scan to avoid blocking on large sets
        var memberIds = new List<RedisValue>();
        await foreach (var entry in db.SetScanAsync(ActiveSetKey(), pageSize: 500).ConfigureAwait(false))
        {
            memberIds.Add(entry);
        }

        if (memberIds.Count == 0) return sessions;

        // Pipeline GET for all active session IDs
        var batch = db.CreateBatch();
        var getTasks = new Task<RedisValue>[memberIds.Count];
        for (var i = 0; i < memberIds.Count; i++)
        {
            getTasks[i] = batch.StringGetAsync(SessionKey(memberIds[i].ToString()));
        }

        batch.Execute();
        await Task.WhenAll(getTasks).ConfigureAwait(false);

        foreach (var task in getTasks)
        {
            var snapshot = Deserialize(task.Result);
            if (snapshot is not null)
                sessions.Add(snapshot.ToSession());
        }

        return sessions;
    }

    /// <inheritdoc />
    public override async ValueTask DeleteAsync(string sessionId, CancellationToken ct)
    {
        var db = GetDatabase();

        // Read session first to get linkedId
        var value = await db.StringGetAsync(SessionKey(sessionId)).ConfigureAwait(false);
        var snapshot = Deserialize(value);

        var batch = db.CreateBatch();
        var tasks = new List<Task>
        {
            batch.KeyDeleteAsync(SessionKey(sessionId)),
            batch.SetRemoveAsync(ActiveSetKey(), sessionId),
            batch.SortedSetRemoveAsync(CompletedSetKey(), sessionId),
        };

        if (snapshot is not null)
        {
            tasks.Add(batch.KeyDeleteAsync(LinkedIndexKey(snapshot.LinkedId)));
        }

        batch.Execute();
        await Task.WhenAll(tasks).ConfigureAwait(false);
    }

    /// <inheritdoc />
    public override async ValueTask SaveBatchAsync(IReadOnlyList<CallSession> sessions, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(sessions);
        if (sessions.Count == 0) return;

        var db = GetDatabase();
        var batch = db.CreateBatch();
        var tasks = new List<Task>();
        var hasTerminal = false;

        foreach (var session in sessions)
        {
            var snapshot = CallSessionSnapshot.FromSession(session);
            var json = Serialize(snapshot);
            var sessionKey = SessionKey(session.SessionId);

            if (IsTerminal(snapshot.State))
            {
                hasTerminal = true;
                var completedAtMs = (snapshot.CompletedAt ?? DateTimeOffset.UtcNow).ToUnixTimeMilliseconds();

                tasks.Add(batch.StringSetAsync(sessionKey, json, _options.CompletedRetention));
                tasks.Add(batch.StringSetAsync(
                    LinkedIndexKey(snapshot.LinkedId), session.SessionId, _options.CompletedRetention));
                tasks.Add(batch.SetRemoveAsync(ActiveSetKey(), session.SessionId));
                tasks.Add(batch.SortedSetAddAsync(CompletedSetKey(), session.SessionId, completedAtMs));
            }
            else
            {
                tasks.Add(batch.StringSetAsync(sessionKey, json));
                tasks.Add(batch.StringSetAsync(LinkedIndexKey(snapshot.LinkedId), session.SessionId));
                tasks.Add(batch.SetAddAsync(ActiveSetKey(), session.SessionId));
            }
        }

        batch.Execute();
        await Task.WhenAll(tasks).ConfigureAwait(false);

        // Trim stale completed entries
        if (hasTerminal)
        {
            var cutoff = DateTimeOffset.UtcNow.Add(-_options.CompletedRetention).ToUnixTimeMilliseconds();
            var stale = await db.SortedSetRangeByScoreAsync(
                CompletedSetKey(), double.NegativeInfinity, cutoff).ConfigureAwait(false);

            if (stale.Length > 0)
            {
                await db.SortedSetRemoveAsync(CompletedSetKey(), stale).ConfigureAwait(false);
            }
        }
    }
}

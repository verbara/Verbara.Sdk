using System.Text.Json;
using Verbara.Sdk.Sessions.Extensions;
using Verbara.Sdk.Sessions.Serialization;
using Microsoft.Extensions.Options;
using StackExchange.Redis;

namespace Verbara.Sdk.Sessions.Redis;

/// <summary>
/// Redis-backed <see cref="SessionStoreBase"/>. Persists active sessions as JSON strings
/// keyed by <c>{prefix}session:{sessionId}</c>, maintains a secondary index by LinkedID with one
/// entry per server (a hash at <c>{prefix}idx:linked-by-server:{linkedId}</c>, one field per server
/// id), and tracks the active/completed set for fast enumeration. All JSON is emitted via
/// <see cref="SessionJsonContext"/> (source-generated) so the store is AOT-safe.
/// </summary>
/// <remarks>
/// Two Asterisk servers can report the same LinkedID for two unrelated calls. Each server's entry in the
/// LinkedID index is written, removed and expired on its own: removing or expiring one server's session never
/// hides another server's. A live save writes its server's entry with plain commands in the save's batch; ending or
/// deleting a session, and pruning during a lookup, go through a single-key Lua script that reads the server clock
/// with <c>TIME</c> before it writes, so the store requires Redis 5.0 or later and a connection allowed to run
/// scripts. Every script names one key, so the store stays valid on Redis Cluster. The index key of earlier versions,
/// <c>{prefix}idx:linked:{linkedId}</c> (a string), is still read so that sessions those versions wrote are found,
/// and is never written again; it is only given its retention or removed when it names a session this store
/// ends or deletes.
/// </remarks>
public sealed class RedisSessionStore : SessionStoreBase
{
    private static readonly CallSessionState[] TerminalStates =
        [CallSessionState.Completed, CallSessionState.Failed, CallSessionState.TimedOut];

    /// <summary>
    /// Maintains the per-server LinkedID index for an ended or deleted session. KEYS[1] = the index hash;
    /// ARGV[1] = <c>save</c> (the session ended) or <c>delete</c>; ARGV[2] = the server id (the hash field);
    /// ARGV[3] = the session id; ARGV[4] = the retention in milliseconds (save only). A save writes the field as
    /// <c>expiryMs|sessionId</c>; a delete removes the field only while it still names that session. Both then prune
    /// the fields whose expiry passed and set the key's expiry from every field: none while any is live (expiry 0,
    /// written by a live save without this script), else the latest expiry. The <c>PERSIST</c> on a live field also
    /// repairs a key whose live writer was cut between its two commands.
    /// </summary>
    private const string LinkedIndexScript = """
        local t = redis.call('TIME')
        local now = t[1] * 1000 + math.floor(t[2] / 1000)
        local changed = 0
        if ARGV[1] == 'save' then
          local exp = now + tonumber(ARGV[4])
          redis.call('HSET', KEYS[1], ARGV[2], string.format('%.0f', exp) .. '|' .. ARGV[3])
          changed = 1
        else
          local v = redis.call('HGET', KEYS[1], ARGV[2])
          if v and string.match(v, '^%d+|(.*)$') == ARGV[3] then
            redis.call('HDEL', KEYS[1], ARGV[2])
            changed = 1
          end
        end
        local all = redis.call('HGETALL', KEYS[1])
        local live = false
        local maxexp = 0
        for i = 1, #all, 2 do
          local e = tonumber(string.match(all[i + 1], '^(%d+)|'))
          if e == 0 then live = true
          elseif e <= now then redis.call('HDEL', KEYS[1], all[i])
          elseif e > maxexp then maxexp = e end
        end
        if live then redis.call('PERSIST', KEYS[1])
        elseif maxexp > 0 then redis.call('PEXPIREAT', KEYS[1], maxexp) end
        return changed
        """;

    /// <summary>
    /// Touches the LinkedID index key of earlier versions only while it names the given session. KEYS[1] = that
    /// string key; ARGV[1] = <c>expire</c> or <c>delete</c>; ARGV[2] = the session id; ARGV[3] = the retention in
    /// milliseconds (expire only).
    /// </summary>
    private const string LegacyLinkedIndexScript = """
        if redis.call('GET', KEYS[1]) == ARGV[2] then
          if ARGV[1] == 'delete' then redis.call('DEL', KEYS[1]) else redis.call('PEXPIRE', KEYS[1], ARGV[3]) end
          return 1
        end
        return 0
        """;

    private readonly IConnectionMultiplexer _redis;
    private readonly RedisSessionStoreOptions _options;

    /// <summary>
    /// Create a new store from a multiplexer and options value.
    /// Internal to avoid DI ambiguity if a consumer registers <see cref="RedisSessionStoreOptions"/>
    /// as a singleton directly. Use the <see cref="IOptions{TOptions}"/> ctor from DI; tests reach
    /// this via <c>InternalsVisibleTo</c>.
    /// </summary>
    internal RedisSessionStore(IConnectionMultiplexer redis, RedisSessionStoreOptions options)
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
        $"{_options.KeyPrefix}idx:linked-by-server:{linkedId}";

    /// <summary>The LinkedID index key of earlier versions: read, and touched only for its own session.</summary>
    private RedisKey LegacyLinkedIndexKey(string linkedId) =>
        $"{_options.KeyPrefix}idx:linked:{linkedId}";

    private static RedisValue ServerField(string? serverId) => serverId ?? string.Empty;

    private long RetentionMilliseconds() =>
        Math.Max(1L, (long)_options.CompletedRetention.TotalMilliseconds);

    private static Task<RedisResult> SaveIndexEntryAsync(
        IDatabaseAsync db, RedisKey key, string? serverId, string sessionId, long retentionMs) =>
        db.ScriptEvaluateAsync(
            LinkedIndexScript, [key], ["save", ServerField(serverId), sessionId, retentionMs]);

    private static Task<RedisResult> DeleteIndexEntryAsync(IDatabaseAsync db, RedisKey key, string? serverId, string sessionId) =>
        db.ScriptEvaluateAsync(LinkedIndexScript, [key], ["delete", ServerField(serverId), sessionId]);

    /// <summary>
    /// Queues the plain writes of one session (its key, its set membership and, for a live session, its server's
    /// entry in the LinkedID index) on <paramref name="batch"/>, shared by <see cref="SaveAsync"/> and
    /// <see cref="SaveBatchAsync"/>. Returns whether the session is terminal.
    /// </summary>
    private bool QueueSaveCommands(IBatch batch, CallSessionSnapshot snapshot, List<Task> tasks)
    {
        var json = Serialize(snapshot);
        var sessionKey = SessionKey(snapshot.SessionId);

        if (IsTerminal(snapshot.State))
        {
            var completedAtMs = (snapshot.CompletedAt ?? DateTimeOffset.UtcNow).ToUnixTimeMilliseconds();
            tasks.Add(batch.StringSetAsync(sessionKey, json, _options.CompletedRetention));
            tasks.Add(batch.SetRemoveAsync(ActiveSetKey(), snapshot.SessionId));
            tasks.Add(batch.SortedSetAddAsync(CompletedSetKey(), snapshot.SessionId, completedAtMs));
            return true;
        }

        tasks.Add(batch.StringSetAsync(sessionKey, json));
        tasks.Add(batch.SetAddAsync(ActiveSetKey(), snapshot.SessionId));

        // The field first, then the key's expiry is cleared. The reverse order lets another server's ending script
        // run in between, see no live field yet, and leave an expiry on a key that then holds a live field.
        var indexKey = LinkedIndexKey(snapshot.LinkedId);
        tasks.Add(batch.HashSetAsync(indexKey, ServerField(snapshot.ServerId), "0|" + snapshot.SessionId));
        tasks.Add(batch.KeyPersistAsync(indexKey));
        return false;
    }

    /// <summary>
    /// Sends the index scripts of one ended session: its server's entry in the LinkedID index and the retention of
    /// the earlier versions' entry when it names this session. A live session sends none: its entry went in the
    /// batch. Sent on the database, never inside a batch, and after the batch that wrote the session key, so that a
    /// reader never sees an entry whose session was not written yet. (Inside a batch, a script the server no longer
    /// knows — after <c>SCRIPT FLUSH</c> or a restart — was never resent and its task never completed.)
    /// </summary>
    private void SendSaveScripts(IDatabaseAsync db, CallSessionSnapshot snapshot, bool terminal, List<Task> tasks)
    {
        if (!terminal) return;
        var retentionMs = RetentionMilliseconds();
        tasks.Add(SaveIndexEntryAsync(
            db, LinkedIndexKey(snapshot.LinkedId), snapshot.ServerId, snapshot.SessionId, retentionMs));
        tasks.Add(db.ScriptEvaluateAsync(
            LegacyLinkedIndexScript,
            [LegacyLinkedIndexKey(snapshot.LinkedId)],
            ["expire", snapshot.SessionId, retentionMs]));
    }

    private async ValueTask TrimCompletedAsync(IDatabase db, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        var cutoff = DateTimeOffset.UtcNow.Add(-_options.CompletedRetention).ToUnixTimeMilliseconds();
        var stale = await db.SortedSetRangeByScoreAsync(
            CompletedSetKey(), double.NegativeInfinity, cutoff).ConfigureAwait(false);

        if (stale.Length > 0)
        {
            await db.SortedSetRemoveAsync(CompletedSetKey(), stale).ConfigureAwait(false);
        }
    }

    /// <summary>The session id an index field names: the text after the first <c>|</c>.</summary>
    private static string? IndexedSessionId(RedisValue entry)
    {
        if (entry.IsNullOrEmpty) return null;
        var text = entry.ToString();
        var bar = text.IndexOf('|', StringComparison.Ordinal);
        return bar < 0 ? null : text[(bar + 1)..];
    }

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
        ct.ThrowIfCancellationRequested();
        ArgumentNullException.ThrowIfNull(session);

        var snapshot = CallSessionSnapshot.FromSession(session);
        var db = GetDatabase();
        var batch = db.CreateBatch();
        var tasks = new List<Task>();
        var terminal = QueueSaveCommands(batch, snapshot, tasks);

        batch.Execute();
        SendSaveScripts(db, snapshot, terminal, tasks);
        await Task.WhenAll(tasks).WaitAsync(ct).ConfigureAwait(false);

        // Trim stale completed entries
        if (terminal)
        {
            await TrimCompletedAsync(db, ct).ConfigureAwait(false);
        }
    }

    /// <inheritdoc />
    public override async ValueTask<CallSession?> GetAsync(string sessionId, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        var db = GetDatabase();
        var value = await db.StringGetAsync(SessionKey(sessionId)).ConfigureAwait(false);
        var snapshot = Deserialize(value);
        return snapshot?.ToSession();
    }

    /// <inheritdoc />
    /// <remarks>
    /// Answers only when exactly one server holds a stored session under <paramref name="linkedId"/>, live or within
    /// its retention; with two or more it returns <c>null</c>. Use
    /// <see cref="GetByLinkedIdAsync(string, string, CancellationToken)"/> to name the server.
    /// </remarks>
    public override async ValueTask<CallSession?> GetByLinkedIdAsync(string linkedId, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        ArgumentNullException.ThrowIfNull(linkedId);
        var db = GetDatabase();
        var indexKey = LinkedIndexKey(linkedId);

        var batch = db.CreateBatch();
        var entriesTask = batch.HashGetAllAsync(indexKey);
        var legacyTask = batch.StringGetAsync(LegacyLinkedIndexKey(linkedId));
        batch.Execute();
        var entries = await entriesTask.WaitAsync(ct).ConfigureAwait(false);
        var legacy = await legacyTask.WaitAsync(ct).ConfigureAwait(false);

        // Candidates: one per index field (its server, its session id), plus the earlier versions' entry when it
        // names a session no field names.
        var candidates = new List<(string? Server, string SessionId)>(entries.Length + 1);
        foreach (var entry in entries)
        {
            var sessionId = IndexedSessionId(entry.Value);
            if (sessionId is not null) candidates.Add((entry.Name.ToString(), sessionId));
        }

        var legacyId = legacy.IsNullOrEmpty ? null : legacy.ToString();
        if (legacyId is not null && !candidates.Exists(c => string.Equals(c.SessionId, legacyId, StringComparison.Ordinal)))
        {
            candidates.Add((null, legacyId));
        }

        if (candidates.Count == 0) return null;

        var snapshots = await ReadSnapshotsAsync(db, candidates, ct).ConfigureAwait(false);

        CallSessionSnapshot? found = null;
        CallSessionSnapshot? legacyFound = null;
        var servers = new HashSet<string>(StringComparer.Ordinal);
        List<Task>? prunes = null;
        for (var i = 0; i < candidates.Count; i++)
        {
            var (server, sessionId) = candidates[i];
            var snapshot = snapshots[i];
            if (server is null)
            {
                if (snapshot is not null && string.Equals(snapshot.LinkedId, linkedId, StringComparison.Ordinal))
                    legacyFound = snapshot;
                continue;
            }

            if (snapshot is null
                || !string.Equals(snapshot.LinkedId, linkedId, StringComparison.Ordinal)
                || !string.Equals(ServerField(snapshot.ServerId).ToString(), server, StringComparison.Ordinal))
            {
                (prunes ??= []).Add(DeleteIndexEntryAsync(db, indexKey, server, sessionId));
                continue;
            }

            servers.Add(server);
            found = snapshot;
        }

        // The earlier versions' entry counts as its session's server unless that server already has a field.
        if (legacyFound is not null && servers.Add(ServerField(legacyFound.ServerId).ToString()))
        {
            found = legacyFound;
        }

        if (prunes is not null) await Task.WhenAll(prunes).WaitAsync(ct).ConfigureAwait(false);

        return servers.Count == 1 ? found!.ToSession() : null;
    }

    /// <inheritdoc />
    /// <remarks>
    /// Returns the session the named server holds under <paramref name="linkedId"/>, live or within its retention,
    /// whatever another server holds under the same id.
    /// </remarks>
    public override async ValueTask<CallSession?> GetByLinkedIdAsync(
        string serverId, string linkedId, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        ArgumentNullException.ThrowIfNull(serverId);
        ArgumentNullException.ThrowIfNull(linkedId);
        var db = GetDatabase();
        var indexKey = LinkedIndexKey(linkedId);

        var batch = db.CreateBatch();
        var entryTask = batch.HashGetAsync(indexKey, ServerField(serverId));
        var legacyTask = batch.StringGetAsync(LegacyLinkedIndexKey(linkedId));
        batch.Execute();
        var entry = await entryTask.WaitAsync(ct).ConfigureAwait(false);
        var legacy = await legacyTask.WaitAsync(ct).ConfigureAwait(false);

        var candidates = new List<(string? Server, string SessionId)>(2);
        var indexedId = IndexedSessionId(entry);
        if (indexedId is not null) candidates.Add((serverId, indexedId));
        var legacyId = legacy.IsNullOrEmpty ? null : legacy.ToString();
        if (legacyId is not null && !string.Equals(legacyId, indexedId, StringComparison.Ordinal))
        {
            candidates.Add((null, legacyId));
        }

        if (candidates.Count == 0) return null;

        var snapshots = await ReadSnapshotsAsync(db, candidates, ct).ConfigureAwait(false);

        CallSessionSnapshot? legacyFound = null;
        for (var i = 0; i < candidates.Count; i++)
        {
            var snapshot = snapshots[i];
            var matches = snapshot is not null
                && string.Equals(snapshot.LinkedId, linkedId, StringComparison.Ordinal)
                && string.Equals(ServerField(snapshot.ServerId).ToString(), serverId, StringComparison.Ordinal);
            if (candidates[i].Server is null)
            {
                if (matches) legacyFound = snapshot;
                continue;
            }

            if (matches) return snapshot!.ToSession();
            await DeleteIndexEntryAsync(db, indexKey, serverId, candidates[i].SessionId).WaitAsync(ct).ConfigureAwait(false);
        }

        return legacyFound?.ToSession();
    }

    private async ValueTask<CallSessionSnapshot?[]> ReadSnapshotsAsync(
        IDatabase db, List<(string? Server, string SessionId)> candidates, CancellationToken ct)
    {
        var batch = db.CreateBatch();
        var reads = new Task<RedisValue>[candidates.Count];
        for (var i = 0; i < candidates.Count; i++)
        {
            reads[i] = batch.StringGetAsync(SessionKey(candidates[i].SessionId));
        }

        batch.Execute();
        var values = await Task.WhenAll(reads).WaitAsync(ct).ConfigureAwait(false);
        return Array.ConvertAll(values, Deserialize);
    }

    /// <inheritdoc />
    public override async ValueTask<IEnumerable<CallSession>> GetActiveAsync(CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        var db = GetDatabase();
        var sessions = new List<CallSession>();

        // Cursor-based scan to avoid blocking on large sets
        var memberIds = new List<RedisValue>();
        await foreach (var entry in db.SetScanAsync(ActiveSetKey(), pageSize: 500).WithCancellation(ct).ConfigureAwait(false))
        {
            memberIds.Add(entry);
        }

        if (memberIds.Count == 0) return sessions;

        ct.ThrowIfCancellationRequested();

        // Pipeline GET for all active session IDs
        var batch = db.CreateBatch();
        var getTasks = new Task<RedisValue>[memberIds.Count];
        for (var i = 0; i < memberIds.Count; i++)
        {
            getTasks[i] = batch.StringGetAsync(SessionKey(memberIds[i].ToString()));
        }

        batch.Execute();
        var values = await Task.WhenAll(getTasks).WaitAsync(ct).ConfigureAwait(false);

        sessions.AddRange(values
            .Select(Deserialize)
            .OfType<CallSessionSnapshot>()
            .Select(static snapshot => snapshot.ToSession()));

        return sessions;
    }

    /// <inheritdoc />
    public override async ValueTask DeleteAsync(string sessionId, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        var db = GetDatabase();

        // Read session first to get linkedId
        var value = await db.StringGetAsync(SessionKey(sessionId)).ConfigureAwait(false);
        var snapshot = Deserialize(value);

        ct.ThrowIfCancellationRequested();

        var batch = db.CreateBatch();
        var tasks = new List<Task>
        {
            batch.KeyDeleteAsync(SessionKey(sessionId)),
            batch.SetRemoveAsync(ActiveSetKey(), sessionId),
            batch.SortedSetRemoveAsync(CompletedSetKey(), sessionId),
        };

        batch.Execute();

        if (snapshot is not null)
        {
            // Only this server's entry, and only while it still names this session; scripts never go in a batch.
            tasks.Add(DeleteIndexEntryAsync(db, LinkedIndexKey(snapshot.LinkedId), snapshot.ServerId, sessionId));
            tasks.Add(db.ScriptEvaluateAsync(
                LegacyLinkedIndexScript, [LegacyLinkedIndexKey(snapshot.LinkedId)], ["delete", sessionId]));
        }

        await Task.WhenAll(tasks).WaitAsync(ct).ConfigureAwait(false);
    }

    /// <inheritdoc />
    public override async ValueTask SaveBatchAsync(IReadOnlyList<CallSession> sessions, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        ArgumentNullException.ThrowIfNull(sessions);
        if (sessions.Count == 0) return;

        var db = GetDatabase();
        var batch = db.CreateBatch();
        var tasks = new List<Task>();
        var hasTerminal = false;

        var snapshots = new CallSessionSnapshot[sessions.Count];
        var terminal = new bool[sessions.Count];
        for (var i = 0; i < sessions.Count; i++)
        {
            snapshots[i] = CallSessionSnapshot.FromSession(sessions[i]);
            terminal[i] = QueueSaveCommands(batch, snapshots[i], tasks);
            hasTerminal |= terminal[i];
        }

        batch.Execute();
        for (var i = 0; i < snapshots.Length; i++)
        {
            SendSaveScripts(db, snapshots[i], terminal[i], tasks);
        }
        await Task.WhenAll(tasks).WaitAsync(ct).ConfigureAwait(false);

        // Trim stale completed entries
        if (hasTerminal)
        {
            await TrimCompletedAsync(db, ct).ConfigureAwait(false);
        }
    }
}

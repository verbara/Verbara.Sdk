using System.Text.Json;
using Verbara.Sdk.Sessions.Extensions;
using Verbara.Sdk.Sessions.Serialization;
using Microsoft.Extensions.Logging;
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
/// deleting a session goes through a single-key Lua script that reads the server clock with <c>TIME</c> before it
/// writes, so the store requires Redis 5.0 or later and a connection allowed to run scripts. A lookup only reads: it
/// counts an entry while its session key exists and never removes one. Every script names one key, so none is refused
/// with <c>CROSSSLOT</c> on Redis Cluster. The index key of earlier versions,
/// <c>{prefix}idx:linked:{linkedId}</c> (a string), is still read so that sessions those versions wrote are found,
/// and is never written again; it is only given its retention or removed when it names a session this store
/// ends or deletes.
/// <para>
/// With <see cref="RedisSessionStoreOptions.ActiveRecordTtl"/> set and the store armed by the SDK's sessions host, a live
/// session's key and its LinkedID index key are written with an expiry, which the host's maintenance refreshes for the
/// calls this process holds; the record of a call whose process is gone leaves on its own. Unset or never armed, the
/// store sends exactly the commands it sent before the option existed.
/// </para>
/// </remarks>
public sealed partial class RedisSessionStore : SessionStoreBase
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
    /// repairs a key whose live writer was cut between its two commands. A lookup never runs it: a lookup only reads.
    /// </summary>
    internal const string LinkedIndexScript = """
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
    /// <see cref="LinkedIndexScript"/> for an armed store (<see cref="RedisSessionStoreOptions.ActiveRecordTtl"/> set):
    /// the same arguments plus ARGV[5] = that value in milliseconds. While a field is live it never clears the key's
    /// expiry: it sets it to the later of now + ARGV[5] and the latest ended field's expiry, so a live field whose owner
    /// stops refreshing it leaves with the key, and an ended field is never cut short.
    /// </summary>
    internal const string LinkedIndexScriptArmed = """
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
        if live then
          local at = now + tonumber(ARGV[5])
          if maxexp > at then at = maxexp end
          redis.call('PEXPIREAT', KEYS[1], string.format('%.0f', at))
        elseif maxexp > 0 then redis.call('PEXPIREAT', KEYS[1], maxexp) end
        return changed
        """;

    /// <summary>How many sessions one refresh pipeline carries.</summary>
    private const int RefreshPipelineSize = 1000;

    /// <summary>How many active-set members one purge page reads.</summary>
    private const int PurgePageSize = 500;

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

    /// <summary>Set once by <see cref="ArmMaintenance"/>, when the option is set: from then on live records expire.</summary>
    private volatile bool _armed;

    /// <summary>How many maintenance runs went out: the purge runs on the first and then on every third.</summary>
    private long _maintenanceRuns;

    /// <summary>
    /// Test seam: awaited by the purge between removing the members whose key is gone and checking those keys again.
    /// </summary>
    internal Func<ValueTask>? PurgeRecheckHook { get; set; }

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

    /// <summary>The live records' expiry, or <see langword="null"/> while the store is not armed.</summary>
    private TimeSpan? ArmedTtl => _armed ? _options.ActiveRecordTtl : null;

    /// <summary>
    /// The LinkedID index key's expiry while a live field is in it: the live expiry, or the retention when that is
    /// longer, so refreshing a live field never cuts short another server's ended field.
    /// </summary>
    private TimeSpan IndexTtl(TimeSpan ttl) =>
        ttl > _options.CompletedRetention ? ttl : _options.CompletedRetention;

    private Task<RedisResult> SaveIndexEntryAsync(
        IDatabaseAsync db, RedisKey key, string? serverId, string sessionId, long retentionMs) =>
        ArmedTtl is { } ttl
            ? db.ScriptEvaluateAsync(
                LinkedIndexScriptArmed, [key],
                ["save", ServerField(serverId), sessionId, retentionMs, (long)ttl.TotalMilliseconds])
            : db.ScriptEvaluateAsync(
                LinkedIndexScript, [key], ["save", ServerField(serverId), sessionId, retentionMs]);

    private Task<RedisResult> DeleteIndexEntryAsync(IDatabaseAsync db, RedisKey key, string? serverId, string sessionId) =>
        ArmedTtl is { } ttl
            ? db.ScriptEvaluateAsync(
                LinkedIndexScriptArmed, [key],
                ["delete", ServerField(serverId), sessionId, 0, (long)ttl.TotalMilliseconds])
            : db.ScriptEvaluateAsync(LinkedIndexScript, [key], ["delete", ServerField(serverId), sessionId]);

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

        var indexKey = LinkedIndexKey(snapshot.LinkedId);
        if (ArmedTtl is { } ttl)
        {
            // Armed: the same four commands, the key and the index key with an expiry the host's refresh renews.
            // Field first, then the expiry, for the reason below.
            tasks.Add(batch.StringSetAsync(sessionKey, json, ttl));
            tasks.Add(batch.SetAddAsync(ActiveSetKey(), snapshot.SessionId));
            tasks.Add(batch.HashSetAsync(indexKey, ServerField(snapshot.ServerId), "0|" + snapshot.SessionId));
            tasks.Add(batch.KeyExpireAsync(indexKey, IndexTtl(ttl)));
            return false;
        }

        tasks.Add(batch.StringSetAsync(sessionKey, json));
        tasks.Add(batch.SetAddAsync(ActiveSetKey(), snapshot.SessionId));

        // The field first, then the key's expiry is cleared. The reverse order lets another server's ending script
        // run in between, see no live field yet, and leave an expiry on a key that then holds a live field.
        tasks.Add(batch.HashSetAsync(indexKey, ServerField(snapshot.ServerId), "0|" + snapshot.SessionId));
        tasks.Add(batch.KeyPersistAsync(indexKey));
        return false;
    }

    /// <summary>
    /// Sends the index scripts of one ended session: its server's entry in the LinkedID index and the retention of
    /// the earlier versions' entry when it names this session. A live session sends none: its entry went in the
    /// batch. Sent on the database, never inside a batch, and after the batch that wrote the session key, so that, on
    /// a single node, a reader never sees an entry whose session was not written yet. (Inside a batch, a script the
    /// server no longer knows — after <c>SCRIPT FLUSH</c> or a restart — was never resent and its task never
    /// completed.)
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
        for (var i = 0; i < candidates.Count; i++)
        {
            var (server, _) = candidates[i];
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
                // Not counted and not removed: on Redis Cluster the session key can arrive after its field.
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

    /// <inheritdoc />
    /// <remarks>A third of <see cref="RedisSessionStoreOptions.ActiveRecordTtl"/>; <see langword="null"/> when it is unset.</remarks>
    internal override TimeSpan? MaintenanceInterval => _options.ActiveRecordTtl / 3;

    /// <inheritdoc />
    internal override void ArmMaintenance()
    {
        if (_options.ActiveRecordTtl is not null)
            _armed = true;
    }

    /// <inheritdoc />
    /// <remarks>
    /// Renews the expiry of every live held session's key and index key, and saves again, whole, a held session whose
    /// key or index key was already gone. On the first run and then every third, it also cleans the active set: a
    /// member whose key is gone leaves it (unless a save brings the key back meanwhile), and a live record written
    /// without an expiry, before the option was set, gets one.
    /// </remarks>
    internal override async ValueTask MaintainAsync(IReadOnlyList<CallSession> heldActive, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(heldActive);
        if (ArmedTtl is not { } ttl) return;
        var db = GetDatabase();

        await RefreshAsync(db, heldActive, ttl, ct).ConfigureAwait(false);

        if (Interlocked.Increment(ref _maintenanceRuns) % 3 == 1)
            await PurgeAsync(db, ttl, ct).ConfigureAwait(false);
    }

    private async ValueTask RefreshAsync(IDatabase db, IReadOnlyList<CallSession> heldActive, TimeSpan ttl, CancellationToken ct)
    {
        var indexTtl = IndexTtl(ttl);
        List<CallSession>? lost = null;
        var live = new List<CallSession>(Math.Min(heldActive.Count, RefreshPipelineSize));
        for (var start = 0; start < heldActive.Count; start += RefreshPipelineSize)
        {
            ct.ThrowIfCancellationRequested();
            live.Clear();
            var end = Math.Min(start + RefreshPipelineSize, heldActive.Count);
            for (var i = start; i < end; i++)
            {
                if (!IsTerminal(heldActive[i].State)) live.Add(heldActive[i]);
            }

            if (live.Count == 0) continue;

            var batch = db.CreateBatch();
            var renewals = new Task<bool>[live.Count * 2];
            for (var i = 0; i < live.Count; i++)
            {
                renewals[2 * i] = batch.KeyExpireAsync(SessionKey(live[i].SessionId), ttl);
                renewals[(2 * i) + 1] = batch.KeyExpireAsync(LinkedIndexKey(live[i].LinkedId), indexTtl);
            }

            batch.Execute();
            var renewed = await Task.WhenAll(renewals).WaitAsync(ct).ConfigureAwait(false);
            for (var i = 0; i < live.Count; i++)
            {
                // A false means the key had already expired (or was never written): the record is written again, whole.
                if (!renewed[2 * i] || !renewed[(2 * i) + 1]) (lost ??= []).Add(live[i]);
            }
        }

        if (lost is not null)
            await SaveBatchAsync(lost, ct).ConfigureAwait(false);
    }

    private async ValueTask PurgeAsync(IDatabase db, TimeSpan ttl, CancellationToken ct)
    {
        var members = new List<RedisValue>();
        await foreach (var member in db.SetScanAsync(ActiveSetKey(), pageSize: PurgePageSize).WithCancellation(ct).ConfigureAwait(false))
        {
            members.Add(member);
        }

        var removed = 0L;
        var deadlined = 0L;
        for (var start = 0; start < members.Count; start += PurgePageSize)
        {
            ct.ThrowIfCancellationRequested();
            var page = members.GetRange(start, Math.Min(PurgePageSize, members.Count - start));
            var (pageRemoved, pageDeadlined) = await PurgePageAsync(db, page, ttl, ct).ConfigureAwait(false);
            removed += pageRemoved;
            deadlined += pageDeadlined;
        }

        if (removed > 0 || deadlined > 0)
            LogPurged(MaintenanceLogger, removed, deadlined);
    }

    private async ValueTask<(long Removed, long Deadlined)> PurgePageAsync(
        IDatabase db, List<RedisValue> page, TimeSpan ttl, CancellationToken ct)
    {
        var batch = db.CreateBatch();
        var ttls = new Task<RedisResult>[page.Count];
        for (var i = 0; i < page.Count; i++)
        {
            ttls[i] = batch.ExecuteAsync("PTTL", SessionKey(page[i].ToString()));
        }

        batch.Execute();
        var results = await Task.WhenAll(ttls).WaitAsync(ct).ConfigureAwait(false);

        var gone = new List<RedisValue>();
        var undated = new List<RedisValue>();
        for (var i = 0; i < page.Count; i++)
        {
            var remaining = (long)results[i];
            if (remaining == -2) gone.Add(page[i]);
            else if (remaining == -1) undated.Add(page[i]);
        }

        var removed = gone.Count == 0 ? 0L : await RemoveGoneMembersAsync(db, gone, ct).ConfigureAwait(false);
        var deadlined = undated.Count == 0 ? 0L : await GiveDeadlinesAsync(db, undated, ttl, ct).ConfigureAwait(false);
        return (removed, deadlined);
    }

    /// <summary>
    /// Removes the members whose key is gone, then checks those keys again and puts back a member whose key a save wrote
    /// in between: a save writes the key before its set membership only on one node, so the recheck keeps a live
    /// session listed.
    /// </summary>
    private async ValueTask<long> RemoveGoneMembersAsync(IDatabase db, List<RedisValue> gone, CancellationToken ct)
    {
        await db.SetRemoveAsync(ActiveSetKey(), [.. gone]).WaitAsync(ct).ConfigureAwait(false);

        if (PurgeRecheckHook is { } hook)
            await hook().ConfigureAwait(false);

        var batch = db.CreateBatch();
        var exists = new Task<bool>[gone.Count];
        for (var i = 0; i < gone.Count; i++)
        {
            exists[i] = batch.KeyExistsAsync(SessionKey(gone[i].ToString()));
        }

        batch.Execute();
        var back = await Task.WhenAll(exists).WaitAsync(ct).ConfigureAwait(false);
        var returned = new List<RedisValue>();
        for (var i = 0; i < gone.Count; i++)
        {
            if (back[i]) returned.Add(gone[i]);
        }

        if (returned.Count > 0)
            await db.SetAddAsync(ActiveSetKey(), [.. returned]).WaitAsync(ct).ConfigureAwait(false);

        return gone.Count - returned.Count;
    }

    /// <summary>
    /// Gives a live record written without an expiry (before the option was set) the live expiry, and its index key the
    /// longer of that and the retention: a process that still holds the call renews them, and one that is gone leaves
    /// them to expire. An ended record is left as it is.
    /// </summary>
    private async ValueTask<long> GiveDeadlinesAsync(IDatabase db, List<RedisValue> undated, TimeSpan ttl, CancellationToken ct)
    {
        var batch = db.CreateBatch();
        var reads = new Task<RedisValue>[undated.Count];
        for (var i = 0; i < undated.Count; i++)
        {
            reads[i] = batch.StringGetAsync(SessionKey(undated[i].ToString()));
        }

        batch.Execute();
        var values = await Task.WhenAll(reads).WaitAsync(ct).ConfigureAwait(false);

        var indexTtl = IndexTtl(ttl);
        var expiries = new List<Task<bool>>();
        batch = db.CreateBatch();
        var deadlined = 0L;
        foreach (var snapshot in values.Select(Deserialize).OfType<CallSessionSnapshot>().Where(static s => !IsTerminal(s.State)))
        {
            expiries.Add(batch.KeyExpireAsync(SessionKey(snapshot.SessionId), ttl));
            expiries.Add(batch.KeyExpireAsync(LinkedIndexKey(snapshot.LinkedId), indexTtl));
            deadlined++;
        }

        if (expiries.Count == 0) return 0;
        batch.Execute();
        await Task.WhenAll(expiries).WaitAsync(ct).ConfigureAwait(false);
        return deadlined;
    }

    [LoggerMessage(EventId = 1, Level = LogLevel.Information,
        Message = "Session store cleanup: {Removed} active-set entries whose session had expired were removed, and {Deadlined} live records written without an expiry were given one.")]
    private static partial void LogPurged(ILogger logger, long removed, long deadlined);
}

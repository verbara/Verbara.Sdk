using System.Globalization;
using System.Text.Json;
using Verbara.Sdk.Data.Npgsql;
using Verbara.Sdk.Sessions.Extensions;
using Verbara.Sdk.Sessions.Serialization;
using Microsoft.Extensions.Options;
using Npgsql;
using NpgsqlTypes;

namespace Verbara.Sdk.Sessions.Postgres;

/// <summary>
/// Postgres-backed <see cref="SessionStoreBase"/>. Persists active and completed sessions as
/// JSONB rows in a single table, with secondary indexes on <c>linked_id</c> and partial index
/// on active sessions (<c>completed_at IS NULL</c>). All JSON is emitted via
/// <see cref="SessionJsonContext"/> (source-generated) so the store is AOT-safe. Npgsql
/// parameters are passed by value only — table/schema identifiers are validated at
/// registration time and interpolated safely into SQL.
/// </summary>
public sealed class PostgresSessionStore : SessionStoreBase
{
    private static readonly CallSessionState[] TerminalStates =
        [CallSessionState.Completed, CallSessionState.Failed, CallSessionState.TimedOut];

    private readonly NpgsqlDataSource _dataSource;
    private readonly PostgresSessionStoreOptions _options;
    private readonly string _qualifiedTable;
    private readonly string _upsertSql;
    private readonly string _getByIdSql;
    private readonly string _getActiveSql;
    private readonly string _getByLinkedIdSql;
    private readonly string _deleteSql;

    /// <summary>
    /// Create a new store from a data source and options value.
    /// Internal to avoid DI ambiguity if a consumer registers <see cref="PostgresSessionStoreOptions"/>
    /// as a singleton directly. Use the <see cref="IOptions{TOptions}"/> ctor from DI; tests reach
    /// this via <c>InternalsVisibleTo</c>.
    /// </summary>
    internal PostgresSessionStore(NpgsqlDataSource dataSource, PostgresSessionStoreOptions options)
    {
        ArgumentNullException.ThrowIfNull(dataSource);
        ArgumentNullException.ThrowIfNull(options);
        _dataSource = dataSource;
        _options = options;

        _qualifiedTable = $"\"{_options.SchemaName}\".\"{_options.TableName}\"";

        _upsertSql = string.Format(
            CultureInfo.InvariantCulture,
            """
            INSERT INTO {0} (session_id, linked_id, server_id, state, direction, created_at, updated_at, completed_at, snapshot)
            VALUES (@session_id, @linked_id, @server_id, @state, @direction, @created_at, @updated_at, @completed_at, @snapshot)
            ON CONFLICT (session_id) DO UPDATE SET
                linked_id    = EXCLUDED.linked_id,
                server_id    = EXCLUDED.server_id,
                state        = EXCLUDED.state,
                direction    = EXCLUDED.direction,
                updated_at   = EXCLUDED.updated_at,
                completed_at = EXCLUDED.completed_at,
                snapshot     = EXCLUDED.snapshot
            """,
            _qualifiedTable);

        _getByIdSql = string.Format(
            CultureInfo.InvariantCulture,
            "SELECT snapshot FROM {0} WHERE session_id = @id",
            _qualifiedTable);

        _getActiveSql = string.Format(
            CultureInfo.InvariantCulture,
            "SELECT snapshot FROM {0} WHERE completed_at IS NULL",
            _qualifiedTable);

        _getByLinkedIdSql = string.Format(
            CultureInfo.InvariantCulture,
            "SELECT snapshot FROM {0} WHERE linked_id = @linked ORDER BY created_at DESC LIMIT 1",
            _qualifiedTable);

        _deleteSql = string.Format(
            CultureInfo.InvariantCulture,
            "DELETE FROM {0} WHERE session_id = @id",
            _qualifiedTable);
    }

    /// <summary>Create a new store from an <see cref="NpgsqlDataSource"/> and bound <see cref="IOptions{TOptions}"/>.</summary>
    public PostgresSessionStore(NpgsqlDataSource dataSource, IOptions<PostgresSessionStoreOptions> options)
        : this(dataSource, (options ?? throw new ArgumentNullException(nameof(options))).Value)
    {
    }

    private static bool IsTerminal(CallSessionState state) =>
        Array.IndexOf(TerminalStates, state) >= 0;

    private static string Serialize(CallSessionSnapshot snapshot) =>
        JsonSerializer.Serialize(snapshot, SessionJsonContext.Default.CallSessionSnapshot);

    private static CallSessionSnapshot? Deserialize(string? json) =>
        string.IsNullOrEmpty(json)
            ? null
            : JsonSerializer.Deserialize(json, SessionJsonContext.Default.CallSessionSnapshot);

    private static void BindSaveParameters(
        NpgsqlParameterCollection p, CallSession session, CallSessionSnapshot snapshot, string json)
    {
        var now = DateTimeOffset.UtcNow;
        DateTimeOffset? completedAt = IsTerminal(snapshot.State) ? snapshot.CompletedAt ?? now : null;
        p.Add(new NpgsqlParameter("session_id", session.SessionId));
        p.Add(new NpgsqlParameter("linked_id", (object?)snapshot.LinkedId ?? DBNull.Value));
        p.Add(new NpgsqlParameter("server_id", (object?)snapshot.ServerId ?? DBNull.Value));
        p.Add(new NpgsqlParameter("state", (short)snapshot.State));
        p.Add(new NpgsqlParameter("direction", (short)snapshot.Direction));
        p.Add(new NpgsqlParameter("created_at", snapshot.CreatedAt));
        p.Add(new NpgsqlParameter("updated_at", now));
        p.Add(new NpgsqlParameter("completed_at", (object?)completedAt ?? DBNull.Value));
        p.Add(new NpgsqlParameter("snapshot", NpgsqlDbType.Jsonb) { Value = json });
    }

    /// <inheritdoc />
    public override async ValueTask SaveAsync(CallSession session, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        ArgumentNullException.ThrowIfNull(session);
        var snapshot = CallSessionSnapshot.FromSession(session);
        var json = Serialize(snapshot);
        await _dataSource.ExecuteAsync(_upsertSql,
            p => BindSaveParameters(p, session, snapshot, json), ct).ConfigureAwait(false);
    }

    /// <inheritdoc />
    public override async ValueTask<CallSession?> GetAsync(string sessionId, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        ArgumentNullException.ThrowIfNull(sessionId);
        var json = await _dataSource.QuerySingleOrDefaultAsync(_getByIdSql,
            p => p.Add(new NpgsqlParameter("id", sessionId)),
            static r => r.GetStringOrNull("snapshot"), ct).ConfigureAwait(false);
        return Deserialize(json)?.ToSession();
    }

    /// <inheritdoc />
    public override async ValueTask<CallSession?> GetByLinkedIdAsync(string linkedId, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        ArgumentNullException.ThrowIfNull(linkedId);
        var json = await _dataSource.QuerySingleOrDefaultAsync(_getByLinkedIdSql,
            p => p.Add(new NpgsqlParameter("linked", linkedId)),
            static r => r.GetStringOrNull("snapshot"), ct).ConfigureAwait(false);
        return Deserialize(json)?.ToSession();
    }

    /// <inheritdoc />
    public override async ValueTask<IEnumerable<CallSession>> GetActiveAsync(CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        var jsons = await _dataSource.QueryListAsync(_getActiveSql,
            static _ => { }, static r => r.GetStringOrNull("snapshot"), ct).ConfigureAwait(false);
        var sessions = new List<CallSession>();
        foreach (var json in jsons)
        {
            var snapshot = Deserialize(json);
            if (snapshot is not null) sessions.Add(snapshot.ToSession());
        }
        return sessions;
    }

    /// <inheritdoc />
    public override async ValueTask DeleteAsync(string sessionId, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        ArgumentNullException.ThrowIfNull(sessionId);
        await _dataSource.ExecuteAsync(_deleteSql,
            p => p.Add(new NpgsqlParameter("id", sessionId)), ct).ConfigureAwait(false);
    }

    /// <inheritdoc />
    public override async ValueTask SaveBatchAsync(IReadOnlyList<CallSession> sessions, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        ArgumentNullException.ThrowIfNull(sessions);
        if (sessions.Count == 0) return;
        await using var conn = await _dataSource.OpenConnectionAsync(ct).ConfigureAwait(false);
        await using var tx = await conn.BeginTransactionAsync(ct).ConfigureAwait(false);
        try
        {
            foreach (var session in sessions)
            {
                ct.ThrowIfCancellationRequested();
                var snapshot = CallSessionSnapshot.FromSession(session);
                var json = Serialize(snapshot);
                await conn.ExecuteAsync(_upsertSql,
                    p => BindSaveParameters(p, session, snapshot, json), tx, ct).ConfigureAwait(false);
            }
            await tx.CommitAsync(ct).ConfigureAwait(false);
        }
        catch
        {
            await tx.RollbackAsync(CancellationToken.None).ConfigureAwait(false);
            throw;
        }
    }
}

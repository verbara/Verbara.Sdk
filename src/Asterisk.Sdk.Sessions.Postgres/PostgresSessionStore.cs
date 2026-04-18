using System.Data;
using System.Globalization;
using System.Text.Json;
using Asterisk.Sdk.Sessions.Extensions;
using Asterisk.Sdk.Sessions.Serialization;
using Dapper;
using Microsoft.Extensions.Options;
using Npgsql;
using NpgsqlTypes;

namespace Asterisk.Sdk.Sessions.Postgres;

/// <summary>
/// Postgres-backed <see cref="SessionStoreBase"/>. Persists active and completed sessions as
/// JSONB rows in a single table, with secondary indexes on <c>linked_id</c> and partial index
/// on active sessions (<c>completed_at IS NULL</c>). All JSON is emitted via
/// <see cref="SessionJsonContext"/> (source-generated) so the store is AOT-safe. Dapper
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

    private static DynamicParameters BuildSaveParameters(CallSession session, CallSessionSnapshot snapshot, string json)
    {
        var now = DateTimeOffset.UtcNow;
        DateTimeOffset? completedAt = IsTerminal(snapshot.State)
            ? snapshot.CompletedAt ?? now
            : null;

        var parameters = new DynamicParameters();
        parameters.Add("session_id", session.SessionId, DbType.String);
        parameters.Add("linked_id", snapshot.LinkedId, DbType.String);
        parameters.Add("server_id", snapshot.ServerId, DbType.String);
        parameters.Add("state", (short)snapshot.State, DbType.Int16);
        parameters.Add("direction", (short)snapshot.Direction, DbType.Int16);
        parameters.Add("created_at", snapshot.CreatedAt, DbType.DateTimeOffset);
        parameters.Add("updated_at", now, DbType.DateTimeOffset);
        parameters.Add("completed_at", completedAt, DbType.DateTimeOffset);
        // Dapper routes NpgsqlDbType via a typed parameter
        parameters.Add("snapshot", new JsonbParameter(json));
        return parameters;
    }

    /// <inheritdoc />
    public override async ValueTask SaveAsync(CallSession session, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        ArgumentNullException.ThrowIfNull(session);

        var snapshot = CallSessionSnapshot.FromSession(session);
        var json = Serialize(snapshot);

        await using var conn = await _dataSource.OpenConnectionAsync(ct).ConfigureAwait(false);
        var parameters = BuildSaveParameters(session, snapshot, json);
        await conn.ExecuteAsync(new CommandDefinition(
            _upsertSql, parameters, cancellationToken: ct)).ConfigureAwait(false);
    }

    /// <inheritdoc />
    public override async ValueTask<CallSession?> GetAsync(string sessionId, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        ArgumentNullException.ThrowIfNull(sessionId);

        await using var conn = await _dataSource.OpenConnectionAsync(ct).ConfigureAwait(false);
        var json = await conn.QuerySingleOrDefaultAsync<string?>(new CommandDefinition(
            _getByIdSql, new { id = sessionId }, cancellationToken: ct)).ConfigureAwait(false);
        return Deserialize(json)?.ToSession();
    }

    /// <inheritdoc />
    public override async ValueTask<CallSession?> GetByLinkedIdAsync(string linkedId, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        ArgumentNullException.ThrowIfNull(linkedId);

        await using var conn = await _dataSource.OpenConnectionAsync(ct).ConfigureAwait(false);
        var json = await conn.QuerySingleOrDefaultAsync<string?>(new CommandDefinition(
            _getByLinkedIdSql, new { linked = linkedId }, cancellationToken: ct)).ConfigureAwait(false);
        return Deserialize(json)?.ToSession();
    }

    /// <inheritdoc />
    public override async ValueTask<IEnumerable<CallSession>> GetActiveAsync(CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();

        await using var conn = await _dataSource.OpenConnectionAsync(ct).ConfigureAwait(false);
        var rows = await conn.QueryAsync<string>(new CommandDefinition(
            _getActiveSql, cancellationToken: ct)).ConfigureAwait(false);

        var sessions = new List<CallSession>();
        foreach (var json in rows)
        {
            var snapshot = Deserialize(json);
            if (snapshot is not null)
                sessions.Add(snapshot.ToSession());
        }
        return sessions;
    }

    /// <inheritdoc />
    public override async ValueTask DeleteAsync(string sessionId, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        ArgumentNullException.ThrowIfNull(sessionId);

        await using var conn = await _dataSource.OpenConnectionAsync(ct).ConfigureAwait(false);
        await conn.ExecuteAsync(new CommandDefinition(
            _deleteSql, new { id = sessionId }, cancellationToken: ct)).ConfigureAwait(false);
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
                var parameters = BuildSaveParameters(session, snapshot, json);
                await conn.ExecuteAsync(new CommandDefinition(
                    _upsertSql, parameters, transaction: tx, cancellationToken: ct)).ConfigureAwait(false);
            }

            await tx.CommitAsync(ct).ConfigureAwait(false);
        }
        catch
        {
            await tx.RollbackAsync(CancellationToken.None).ConfigureAwait(false);
            throw;
        }
    }

    /// <summary>
    /// Wraps a JSON string as a JSONB Npgsql parameter so Dapper routes it with
    /// <see cref="NpgsqlDbType.Jsonb"/> instead of plain text, giving Postgres the
    /// JSONB representation directly.
    /// </summary>
    private sealed class JsonbParameter(string json) : SqlMapper.ICustomQueryParameter
    {
        private readonly string _json = json;

        public void AddParameter(IDbCommand command, string name)
        {
            ArgumentNullException.ThrowIfNull(command);
            var parameter = new NpgsqlParameter(name, NpgsqlDbType.Jsonb)
            {
                Value = _json,
            };
            command.Parameters.Add(parameter);
        }
    }
}

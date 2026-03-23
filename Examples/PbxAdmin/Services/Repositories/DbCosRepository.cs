using Dapper;
using Npgsql;
using PbxAdmin.Models;

namespace PbxAdmin.Services.Repositories;

internal static partial class DbCosLog
{
    [LoggerMessage(Level = LogLevel.Debug, Message = "[COS_DB] GetLevels: server={ServerId} count={Count}")]
    public static partial void GetLevels(ILogger logger, string serverId, int count);

    [LoggerMessage(Level = LogLevel.Debug, Message = "[COS_DB] GetLevelRules: cosLevelId={CosLevelId} count={Count}")]
    public static partial void GetLevelRules(ILogger logger, int cosLevelId, int count);

    [LoggerMessage(Level = LogLevel.Debug, Message = "[COS_DB] CreatedLevel: id={Id} name={Name}")]
    public static partial void CreatedLevel(ILogger logger, int id, string name);

    [LoggerMessage(Level = LogLevel.Debug, Message = "[COS_DB] GetExtensionOverrides: server={ServerId} count={Count}")]
    public static partial void GetExtensionOverrides(ILogger logger, string serverId, int count);

    [LoggerMessage(Level = LogLevel.Debug, Message = "[COS_DB] GetTimeWindows: server={ServerId} count={Count}")]
    public static partial void GetTimeWindows(ILogger logger, string serverId, int count);

    [LoggerMessage(Level = LogLevel.Debug, Message = "[COS_DB] GetTimeOverrides: cosLevelId={CosLevelId} count={Count}")]
    public static partial void GetTimeOverrides(ILogger logger, int cosLevelId, int count);

    [LoggerMessage(Level = LogLevel.Debug, Message = "[COS_DB] CreatedTimeOverride: id={Id}")]
    public static partial void CreatedTimeOverride(ILogger logger, int id);

    [LoggerMessage(Level = LogLevel.Error, Message = "[COS_DB] Operation failed: operation={Operation}")]
    public static partial void OperationFailed(ILogger logger, Exception exception, string operation);
}

/// <summary>
/// PostgreSQL/Dapper implementation of <see cref="ICosRepository"/>.
/// </summary>
public sealed class DbCosRepository : ICosRepository
{
    private readonly string _connectionString;
    private readonly ILogger<DbCosRepository> _logger;

    public DbCosRepository(string connectionString, ILogger<DbCosRepository> logger)
    {
        _connectionString = connectionString;
        _logger = logger;
    }

    // ──────────────────────────── Levels ────────────────────────────

    public async Task<List<CosLevel>> GetLevelsAsync(string serverId, CancellationToken ct = default)
    {
        try
        {
            await using var conn = new NpgsqlConnection(_connectionString);
            await conn.OpenAsync(ct);

            const string sql = """
                SELECT id AS Id,
                       server_id AS ServerId,
                       name AS Name,
                       description AS Description,
                       priority AS Priority,
                       asterisk_context AS AsteriskContext,
                       is_built_in AS IsBuiltIn,
                       allow_premium AS AllowPremium,
                       allow_mobile AS AllowMobile,
                       allow_forward_external AS AllowForwardExternal,
                       allow_conference_external AS AllowConferenceExternal,
                       allow_recording_control AS AllowRecordingControl,
                       enabled AS Enabled,
                       updated_at AS UpdatedAt
                FROM cos_levels
                WHERE server_id = @ServerId
                ORDER BY priority, name
                """;

            var result = (await conn.QueryAsync<CosLevel>(
                new CommandDefinition(sql, new { ServerId = serverId }, cancellationToken: ct))).AsList();

            DbCosLog.GetLevels(_logger, serverId, result.Count);
            return result;
        }
        catch (Exception ex)
        {
            DbCosLog.OperationFailed(_logger, ex, "GetLevels");
            return [];
        }
    }

    public async Task<CosLevel?> GetLevelAsync(int id, CancellationToken ct = default)
    {
        try
        {
            await using var conn = new NpgsqlConnection(_connectionString);
            await conn.OpenAsync(ct);

            const string levelSql = """
                SELECT id AS Id,
                       server_id AS ServerId,
                       name AS Name,
                       description AS Description,
                       priority AS Priority,
                       asterisk_context AS AsteriskContext,
                       is_built_in AS IsBuiltIn,
                       allow_premium AS AllowPremium,
                       allow_mobile AS AllowMobile,
                       allow_forward_external AS AllowForwardExternal,
                       allow_conference_external AS AllowConferenceExternal,
                       allow_recording_control AS AllowRecordingControl,
                       enabled AS Enabled,
                       updated_at AS UpdatedAt
                FROM cos_levels
                WHERE id = @Id
                """;

            var level = await conn.QueryFirstOrDefaultAsync<CosLevel>(
                new CommandDefinition(levelSql, new { Id = id }, cancellationToken: ct));

            if (level is null) return null;

            const string rulesSql = """
                SELECT r.id AS Id,
                       r.cos_level_id AS CosLevelId,
                       r.pattern_group_id AS PatternGroupId,
                       r.action AS Action,
                       r.sequence AS Sequence,
                       g.name AS PatternGroupName
                FROM cos_level_rules r
                LEFT JOIN cos_pattern_groups g ON g.id = r.pattern_group_id
                WHERE r.cos_level_id = @Id
                ORDER BY r.sequence
                """;

            var rules = await conn.QueryAsync<CosLevelRule>(
                new CommandDefinition(rulesSql, new { Id = id }, cancellationToken: ct));

            level.Rules = rules.AsList();
            return level;
        }
        catch (Exception ex)
        {
            DbCosLog.OperationFailed(_logger, ex, "GetLevel");
            return null;
        }
    }

    public async Task<int> CreateLevelAsync(CosLevel level, CancellationToken ct = default)
    {
        try
        {
            await using var conn = new NpgsqlConnection(_connectionString);
            await conn.OpenAsync(ct);

            const string sql = """
                INSERT INTO cos_levels
                    (server_id, name, description, priority, asterisk_context, is_built_in,
                     allow_premium, allow_mobile, allow_forward_external, allow_conference_external,
                     allow_recording_control, enabled)
                VALUES
                    (@ServerId, @Name, @Description, @Priority, @AsteriskContext, @IsBuiltIn,
                     @AllowPremium, @AllowMobile, @AllowForwardExternal, @AllowConferenceExternal,
                     @AllowRecordingControl, @Enabled)
                RETURNING id
                """;

            var id = await conn.ExecuteScalarAsync<int>(
                new CommandDefinition(sql, level, cancellationToken: ct));

            DbCosLog.CreatedLevel(_logger, id, level.Name);
            return id;
        }
        catch (Exception ex)
        {
            DbCosLog.OperationFailed(_logger, ex, "CreateLevel");
            return 0;
        }
    }

    public async Task<bool> UpdateLevelAsync(CosLevel level, CancellationToken ct = default)
    {
        try
        {
            await using var conn = new NpgsqlConnection(_connectionString);
            await conn.OpenAsync(ct);

            const string sql = """
                UPDATE cos_levels
                SET name                      = @Name,
                    description               = @Description,
                    priority                  = @Priority,
                    asterisk_context          = @AsteriskContext,
                    is_built_in               = @IsBuiltIn,
                    allow_premium             = @AllowPremium,
                    allow_mobile              = @AllowMobile,
                    allow_forward_external    = @AllowForwardExternal,
                    allow_conference_external = @AllowConferenceExternal,
                    allow_recording_control   = @AllowRecordingControl,
                    enabled                   = @Enabled,
                    updated_at                = NOW()
                WHERE id = @Id AND updated_at = @UpdatedAt
                """;

            var rows = await conn.ExecuteAsync(
                new CommandDefinition(sql, level, cancellationToken: ct));

            if (rows == 0)
                throw new ConcurrencyConflictException($"CosLevel {level.Id} was modified by another user.");

            return true;
        }
        catch (ConcurrencyConflictException)
        {
            throw;
        }
        catch (Exception ex)
        {
            DbCosLog.OperationFailed(_logger, ex, "UpdateLevel");
            return false;
        }
    }

    public async Task<bool> DeleteLevelAsync(int id, CancellationToken ct = default)
    {
        try
        {
            await using var conn = new NpgsqlConnection(_connectionString);
            await conn.OpenAsync(ct);

            const string sql = "DELETE FROM cos_levels WHERE id = @Id";
            var rows = await conn.ExecuteAsync(
                new CommandDefinition(sql, new { Id = id }, cancellationToken: ct));
            return rows > 0;
        }
        catch (Exception ex)
        {
            DbCosLog.OperationFailed(_logger, ex, "DeleteLevel");
            return false;
        }
    }

    // ──────────────────────────── Level Rules ────────────────────────────

    public async Task<List<CosLevelRule>> GetLevelRulesAsync(int cosLevelId, CancellationToken ct = default)
    {
        try
        {
            await using var conn = new NpgsqlConnection(_connectionString);
            await conn.OpenAsync(ct);

            const string sql = """
                SELECT r.id AS Id,
                       r.cos_level_id AS CosLevelId,
                       r.pattern_group_id AS PatternGroupId,
                       r.action AS Action,
                       r.sequence AS Sequence,
                       g.name AS PatternGroupName
                FROM cos_level_rules r
                LEFT JOIN cos_pattern_groups g ON g.id = r.pattern_group_id
                WHERE r.cos_level_id = @CosLevelId
                ORDER BY r.sequence
                """;

            var result = (await conn.QueryAsync<CosLevelRule>(
                new CommandDefinition(sql, new { CosLevelId = cosLevelId }, cancellationToken: ct))).AsList();

            DbCosLog.GetLevelRules(_logger, cosLevelId, result.Count);
            return result;
        }
        catch (Exception ex)
        {
            DbCosLog.OperationFailed(_logger, ex, "GetLevelRules");
            return [];
        }
    }

    public async Task<bool> SetLevelRulesAsync(int cosLevelId, List<CosLevelRule> rules, CancellationToken ct = default)
    {
        try
        {
            await using var conn = new NpgsqlConnection(_connectionString);
            await conn.OpenAsync(ct);
            await using var tx = await conn.BeginTransactionAsync(ct);

            await conn.ExecuteAsync(new CommandDefinition(
                "DELETE FROM cos_level_rules WHERE cos_level_id = @CosLevelId",
                new { CosLevelId = cosLevelId }, tx, cancellationToken: ct));

            const string insertSql = """
                INSERT INTO cos_level_rules (cos_level_id, pattern_group_id, action, sequence)
                VALUES (@CosLevelId, @PatternGroupId, @Action, @Sequence)
                """;

            foreach (var rule in rules)
            {
                await conn.ExecuteAsync(new CommandDefinition(
                    insertSql,
                    new { CosLevelId = cosLevelId, rule.PatternGroupId, rule.Action, rule.Sequence },
                    tx,
                    cancellationToken: ct));
            }

            await tx.CommitAsync(ct);
            return true;
        }
        catch (Exception ex)
        {
            DbCosLog.OperationFailed(_logger, ex, "SetLevelRules");
            return false;
        }
    }

    // ──────────────────────────── Extension Overrides ────────────────────────────

    public async Task<List<CosExtensionOverride>> GetExtensionOverridesAsync(string serverId, CancellationToken ct = default)
    {
        try
        {
            await using var conn = new NpgsqlConnection(_connectionString);
            await conn.OpenAsync(ct);

            const string sql = """
                SELECT id AS Id,
                       server_id AS ServerId,
                       extension AS Extension,
                       cos_level_id AS CosLevelId,
                       pattern_overrides AS PatternOverrides,
                       notes AS Notes
                FROM cos_extension_overrides
                WHERE server_id = @ServerId
                ORDER BY extension
                """;

            var result = (await conn.QueryAsync<CosExtensionOverride>(
                new CommandDefinition(sql, new { ServerId = serverId }, cancellationToken: ct))).AsList();

            DbCosLog.GetExtensionOverrides(_logger, serverId, result.Count);
            return result;
        }
        catch (Exception ex)
        {
            DbCosLog.OperationFailed(_logger, ex, "GetExtensionOverrides");
            return [];
        }
    }

    public async Task<CosExtensionOverride?> GetExtensionOverrideAsync(string serverId, string extension, CancellationToken ct = default)
    {
        try
        {
            await using var conn = new NpgsqlConnection(_connectionString);
            await conn.OpenAsync(ct);

            const string sql = """
                SELECT id AS Id,
                       server_id AS ServerId,
                       extension AS Extension,
                       cos_level_id AS CosLevelId,
                       pattern_overrides AS PatternOverrides,
                       notes AS Notes
                FROM cos_extension_overrides
                WHERE server_id = @ServerId AND extension = @Extension
                """;

            return await conn.QueryFirstOrDefaultAsync<CosExtensionOverride>(
                new CommandDefinition(sql, new { ServerId = serverId, Extension = extension }, cancellationToken: ct));
        }
        catch (Exception ex)
        {
            DbCosLog.OperationFailed(_logger, ex, "GetExtensionOverride");
            return null;
        }
    }

    public async Task<bool> UpsertExtensionOverrideAsync(CosExtensionOverride entry, CancellationToken ct = default)
    {
        try
        {
            await using var conn = new NpgsqlConnection(_connectionString);
            await conn.OpenAsync(ct);

            const string sql = """
                INSERT INTO cos_extension_overrides
                    (server_id, extension, cos_level_id, pattern_overrides, notes)
                VALUES
                    (@ServerId, @Extension, @CosLevelId, @PatternOverrides::jsonb, @Notes)
                ON CONFLICT (server_id, extension) DO UPDATE SET
                    cos_level_id      = EXCLUDED.cos_level_id,
                    pattern_overrides = EXCLUDED.pattern_overrides,
                    notes             = EXCLUDED.notes,
                    updated_at        = NOW()
                """;

            var rows = await conn.ExecuteAsync(
                new CommandDefinition(sql, entry, cancellationToken: ct));
            return rows > 0;
        }
        catch (Exception ex)
        {
            DbCosLog.OperationFailed(_logger, ex, "UpsertExtensionOverride");
            return false;
        }
    }

    public async Task<bool> DeleteExtensionOverrideAsync(string serverId, string extension, CancellationToken ct = default)
    {
        try
        {
            await using var conn = new NpgsqlConnection(_connectionString);
            await conn.OpenAsync(ct);

            const string sql = "DELETE FROM cos_extension_overrides WHERE server_id = @ServerId AND extension = @Extension";
            var rows = await conn.ExecuteAsync(
                new CommandDefinition(sql, new { ServerId = serverId, Extension = extension }, cancellationToken: ct));
            return rows > 0;
        }
        catch (Exception ex)
        {
            DbCosLog.OperationFailed(_logger, ex, "DeleteExtensionOverride");
            return false;
        }
    }

    // ──────────────────────────── Time Windows ────────────────────────────

    public async Task<List<CosTimeWindow>> GetTimeWindowsAsync(string serverId, CancellationToken ct = default)
    {
        try
        {
            await using var conn = new NpgsqlConnection(_connectionString);
            await conn.OpenAsync(ct);

            const string sql = """
                SELECT id AS Id,
                       name AS Name,
                       server_id AS ServerId,
                       day_of_week AS DayOfWeek,
                       start_time AS StartTime,
                       end_time AS EndTime
                FROM cos_time_windows
                WHERE server_id = @ServerId
                ORDER BY name, day_of_week, start_time
                """;

            var result = (await conn.QueryAsync<CosTimeWindow>(
                new CommandDefinition(sql, new { ServerId = serverId }, cancellationToken: ct))).AsList();

            DbCosLog.GetTimeWindows(_logger, serverId, result.Count);
            return result;
        }
        catch (Exception ex)
        {
            DbCosLog.OperationFailed(_logger, ex, "GetTimeWindows");
            return [];
        }
    }

    // ──────────────────────────── Time Overrides ────────────────────────────

    public async Task<List<CosTimeOverride>> GetTimeOverridesAsync(int cosLevelId, CancellationToken ct = default)
    {
        try
        {
            await using var conn = new NpgsqlConnection(_connectionString);
            await conn.OpenAsync(ct);

            const string sql = """
                SELECT id AS Id,
                       cos_level_id AS CosLevelId,
                       override_cos_level_id AS OverrideCosLevelId,
                       time_window_id AS TimeWindowId,
                       priority AS Priority
                FROM cos_time_overrides
                WHERE cos_level_id = @CosLevelId
                ORDER BY priority
                """;

            var result = (await conn.QueryAsync<CosTimeOverride>(
                new CommandDefinition(sql, new { CosLevelId = cosLevelId }, cancellationToken: ct))).AsList();

            DbCosLog.GetTimeOverrides(_logger, cosLevelId, result.Count);
            return result;
        }
        catch (Exception ex)
        {
            DbCosLog.OperationFailed(_logger, ex, "GetTimeOverrides");
            return [];
        }
    }

    public async Task<int> CreateTimeOverrideAsync(CosTimeOverride entry, CancellationToken ct = default)
    {
        try
        {
            await using var conn = new NpgsqlConnection(_connectionString);
            await conn.OpenAsync(ct);

            const string sql = """
                INSERT INTO cos_time_overrides
                    (cos_level_id, override_cos_level_id, time_window_id, priority)
                VALUES
                    (@CosLevelId, @OverrideCosLevelId, @TimeWindowId, @Priority)
                RETURNING id
                """;

            var id = await conn.ExecuteScalarAsync<int>(
                new CommandDefinition(sql, entry, cancellationToken: ct));

            DbCosLog.CreatedTimeOverride(_logger, id);
            return id;
        }
        catch (Exception ex)
        {
            DbCosLog.OperationFailed(_logger, ex, "CreateTimeOverride");
            return 0;
        }
    }

    public async Task<bool> DeleteTimeOverrideAsync(int id, CancellationToken ct = default)
    {
        try
        {
            await using var conn = new NpgsqlConnection(_connectionString);
            await conn.OpenAsync(ct);

            const string sql = "DELETE FROM cos_time_overrides WHERE id = @Id";
            var rows = await conn.ExecuteAsync(
                new CommandDefinition(sql, new { Id = id }, cancellationToken: ct));
            return rows > 0;
        }
        catch (Exception ex)
        {
            DbCosLog.OperationFailed(_logger, ex, "DeleteTimeOverride");
            return false;
        }
    }

    // ──────────────────────────── Audit Log ────────────────────────────

    public async Task AppendAuditLogAsync(CosAuditEntry entry, CancellationToken ct = default)
    {
        try
        {
            await using var conn = new NpgsqlConnection(_connectionString);
            await conn.OpenAsync(ct);

            const string sql = """
                INSERT INTO cos_audit_log
                    (server_id, entity_type, entity_id, action, old_value, new_value, changed_by)
                VALUES
                    (@ServerId, @EntityType, @EntityId, @Action, @OldValue, @NewValue, @ChangedBy)
                """;

            await conn.ExecuteAsync(
                new CommandDefinition(sql, entry, cancellationToken: ct));
        }
        catch (Exception ex)
        {
            DbCosLog.OperationFailed(_logger, ex, "AppendAuditLog");
        }
    }
}

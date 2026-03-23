using Dapper;
using Npgsql;
using PbxAdmin.Models;

namespace PbxAdmin.Services.Repositories;

internal static partial class DbCosPatternGroupLog
{
    [LoggerMessage(Level = LogLevel.Debug, Message = "[COS_PG_DB] GetGroups: server={ServerId} count={Count}")]
    public static partial void GetGroups(ILogger logger, string serverId, int count);

    [LoggerMessage(Level = LogLevel.Debug, Message = "[COS_PG_DB] CreatedGroup: id={Id} name={Name}")]
    public static partial void CreatedGroup(ILogger logger, int id, string name);

    [LoggerMessage(Level = LogLevel.Error, Message = "[COS_PG_DB] Operation failed: operation={Operation}")]
    public static partial void OperationFailed(ILogger logger, Exception exception, string operation);
}

/// <summary>
/// PostgreSQL/Dapper implementation of <see cref="ICosPatternGroupRepository"/>.
/// </summary>
public sealed class DbCosPatternGroupRepository : ICosPatternGroupRepository
{
    private readonly string _connectionString;
    private readonly ILogger<DbCosPatternGroupRepository> _logger;

    public DbCosPatternGroupRepository(string connectionString, ILogger<DbCosPatternGroupRepository> logger)
    {
        _connectionString = connectionString;
        _logger = logger;
    }

    public async Task<List<CosPatternGroup>> GetGroupsAsync(string serverId, CancellationToken ct = default)
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
                       patterns AS Patterns,
                       country_code AS CountryCode,
                       is_built_in AS IsBuiltIn,
                       updated_at AS UpdatedAt
                FROM cos_pattern_groups
                WHERE server_id = @ServerId
                ORDER BY name
                """;

            var result = (await conn.QueryAsync<CosPatternGroup>(
                new CommandDefinition(sql, new { ServerId = serverId }, cancellationToken: ct))).AsList();

            DbCosPatternGroupLog.GetGroups(_logger, serverId, result.Count);
            return result;
        }
        catch (Exception ex)
        {
            DbCosPatternGroupLog.OperationFailed(_logger, ex, "GetGroups");
            return [];
        }
    }

    public async Task<CosPatternGroup?> GetGroupAsync(int id, CancellationToken ct = default)
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
                       patterns AS Patterns,
                       country_code AS CountryCode,
                       is_built_in AS IsBuiltIn,
                       updated_at AS UpdatedAt
                FROM cos_pattern_groups
                WHERE id = @Id
                """;

            return await conn.QueryFirstOrDefaultAsync<CosPatternGroup>(
                new CommandDefinition(sql, new { Id = id }, cancellationToken: ct));
        }
        catch (Exception ex)
        {
            DbCosPatternGroupLog.OperationFailed(_logger, ex, "GetGroup");
            return null;
        }
    }

    public async Task<int> CreateGroupAsync(CosPatternGroup group, CancellationToken ct = default)
    {
        try
        {
            await using var conn = new NpgsqlConnection(_connectionString);
            await conn.OpenAsync(ct);

            const string sql = """
                INSERT INTO cos_pattern_groups
                    (server_id, name, description, patterns, country_code, is_built_in)
                VALUES
                    (@ServerId, @Name, @Description, @Patterns, @CountryCode, @IsBuiltIn)
                RETURNING id
                """;

            var id = await conn.ExecuteScalarAsync<int>(
                new CommandDefinition(sql, group, cancellationToken: ct));

            DbCosPatternGroupLog.CreatedGroup(_logger, id, group.Name);
            return id;
        }
        catch (Exception ex)
        {
            DbCosPatternGroupLog.OperationFailed(_logger, ex, "CreateGroup");
            return 0;
        }
    }

    public async Task<bool> UpdateGroupAsync(CosPatternGroup group, CancellationToken ct = default)
    {
        try
        {
            await using var conn = new NpgsqlConnection(_connectionString);
            await conn.OpenAsync(ct);

            const string sql = """
                UPDATE cos_pattern_groups
                SET name         = @Name,
                    description  = @Description,
                    patterns     = @Patterns,
                    country_code = @CountryCode,
                    is_built_in  = @IsBuiltIn,
                    updated_at   = NOW()
                WHERE id = @Id AND updated_at = @UpdatedAt
                """;

            var rows = await conn.ExecuteAsync(
                new CommandDefinition(sql, group, cancellationToken: ct));

            if (rows == 0)
                throw new ConcurrencyConflictException($"CosPatternGroup {group.Id} was modified by another user.");

            return true;
        }
        catch (ConcurrencyConflictException)
        {
            throw;
        }
        catch (Exception ex)
        {
            DbCosPatternGroupLog.OperationFailed(_logger, ex, "UpdateGroup");
            return false;
        }
    }

    public async Task<bool> DeleteGroupAsync(int id, CancellationToken ct = default)
    {
        try
        {
            await using var conn = new NpgsqlConnection(_connectionString);
            await conn.OpenAsync(ct);

            const string sql = "DELETE FROM cos_pattern_groups WHERE id = @Id";
            var rows = await conn.ExecuteAsync(
                new CommandDefinition(sql, new { Id = id }, cancellationToken: ct));
            return rows > 0;
        }
        catch (Exception ex)
        {
            DbCosPatternGroupLog.OperationFailed(_logger, ex, "DeleteGroup");
            return false;
        }
    }

    public async Task<bool> IsGroupReferencedAsync(int id, CancellationToken ct = default)
    {
        try
        {
            await using var conn = new NpgsqlConnection(_connectionString);
            await conn.OpenAsync(ct);

            const string sql = "SELECT COUNT(*) FROM cos_level_rules WHERE pattern_group_id = @Id";
            var count = await conn.ExecuteScalarAsync<int>(
                new CommandDefinition(sql, new { Id = id }, cancellationToken: ct));
            return count > 0;
        }
        catch (Exception ex)
        {
            DbCosPatternGroupLog.OperationFailed(_logger, ex, "IsGroupReferenced");
            return false;
        }
    }
}

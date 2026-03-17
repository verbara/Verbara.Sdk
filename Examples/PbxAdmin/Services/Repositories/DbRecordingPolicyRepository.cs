using Dapper;
using PbxAdmin.Models;
using Npgsql;

namespace PbxAdmin.Services.Repositories;

public sealed partial class DbRecordingPolicyRepository : IRecordingPolicyRepository
{
    private readonly string _connectionString;
    private readonly ILogger<DbRecordingPolicyRepository> _logger;

    private const string PolicyColumns = """
        id AS Id, server_id AS ServerId, name AS Name, mode AS Mode,
        format AS Format, storage_path AS StoragePath, retention_days AS RetentionDays,
        mix_monitor_options AS MixMonitorOptions,
        created_at AS CreatedAt, updated_at AS UpdatedAt
        """;

    private const string TargetColumns = """
        id AS Id, policy_id AS PolicyId, target_type AS TargetType, target_value AS TargetValue
        """;

    public DbRecordingPolicyRepository(string connectionString, ILogger<DbRecordingPolicyRepository> logger)
    {
        _connectionString = connectionString;
        _logger = logger;
    }

    public async Task<List<RecordingPolicy>> GetAllAsync(string serverId, CancellationToken ct = default)
    {
        try
        {
            await using var conn = new NpgsqlConnection(_connectionString);
            await conn.OpenAsync(ct);

            var policies = (await conn.QueryAsync<RecordingPolicy>(
                new CommandDefinition($"SELECT {PolicyColumns} FROM recording_policies WHERE server_id = @ServerId ORDER BY name",
                    new { ServerId = serverId }, cancellationToken: ct))).AsList();

            if (policies.Count == 0) return policies;

            var policyIds = policies.Select(p => p.Id).ToArray();
            var targets = (await conn.QueryAsync<PolicyTarget>(
                new CommandDefinition($"SELECT {TargetColumns} FROM recording_policy_targets WHERE policy_id = ANY(@Ids)",
                    new { Ids = policyIds }, cancellationToken: ct))).ToLookup(t => t.PolicyId);

            foreach (var p in policies)
                p.Targets = targets[p.Id].ToList();

            return policies;
        }
        catch (Exception ex)
        {
            GetAllFailed(_logger, ex, serverId);
            return [];
        }
    }

    public async Task<RecordingPolicy?> GetByIdAsync(int id, CancellationToken ct = default)
    {
        try
        {
            await using var conn = new NpgsqlConnection(_connectionString);
            await conn.OpenAsync(ct);

            var policy = await conn.QuerySingleOrDefaultAsync<RecordingPolicy>(
                new CommandDefinition($"SELECT {PolicyColumns} FROM recording_policies WHERE id = @Id",
                    new { Id = id }, cancellationToken: ct));

            if (policy is not null)
            {
                policy.Targets = (await conn.QueryAsync<PolicyTarget>(
                    new CommandDefinition($"SELECT {TargetColumns} FROM recording_policy_targets WHERE policy_id = @Id",
                        new { Id = id }, cancellationToken: ct))).AsList();
            }

            return policy;
        }
        catch (Exception ex)
        {
            GetByIdFailed(_logger, ex, id);
            return null;
        }
    }

    public async Task<RecordingPolicy?> GetByNameAsync(string serverId, string name, CancellationToken ct = default)
    {
        try
        {
            await using var conn = new NpgsqlConnection(_connectionString);
            return await conn.QuerySingleOrDefaultAsync<RecordingPolicy>(
                new CommandDefinition($"SELECT {PolicyColumns} FROM recording_policies WHERE server_id = @ServerId AND name = @Name",
                    new { ServerId = serverId, Name = name }, cancellationToken: ct));
        }
        catch (Exception ex)
        {
            GetByNameFailed(_logger, ex, serverId, name);
            return null;
        }
    }

    public async Task<int> InsertAsync(RecordingPolicy policy, CancellationToken ct = default)
    {
        await using var conn = new NpgsqlConnection(_connectionString);
        await conn.OpenAsync(ct);
        await using var tx = await conn.BeginTransactionAsync(ct);

        try
        {
            var id = await conn.QuerySingleAsync<int>(new CommandDefinition("""
                INSERT INTO recording_policies (server_id, name, mode, format, storage_path, retention_days, mix_monitor_options)
                VALUES (@ServerId, @Name, @Mode, @Format, @StoragePath, @RetentionDays, @MixMonitorOptions)
                RETURNING id
                """, new
            {
                policy.ServerId, policy.Name, Mode = policy.Mode.ToString(),
                policy.Format, policy.StoragePath, policy.RetentionDays, policy.MixMonitorOptions
            }, transaction: tx, cancellationToken: ct));

            if (policy.Targets.Count > 0)
            {
                foreach (var t in policy.Targets)
                {
                    await conn.ExecuteAsync(new CommandDefinition("""
                        INSERT INTO recording_policy_targets (policy_id, target_type, target_value)
                        VALUES (@PolicyId, @TargetType, @TargetValue)
                        """, new { PolicyId = id, t.TargetType, t.TargetValue },
                        transaction: tx, cancellationToken: ct));
                }
            }

            await tx.CommitAsync(ct);
            InsertOk(_logger, id, policy.Name);
            return id;
        }
        catch
        {
            await tx.RollbackAsync(ct);
            throw;
        }
    }

    public async Task UpdateAsync(RecordingPolicy policy, CancellationToken ct = default)
    {
        await using var conn = new NpgsqlConnection(_connectionString);
        await conn.OpenAsync(ct);
        await using var tx = await conn.BeginTransactionAsync(ct);

        try
        {
            await conn.ExecuteAsync(new CommandDefinition("""
                UPDATE recording_policies
                SET name = @Name, mode = @Mode, format = @Format, storage_path = @StoragePath,
                    retention_days = @RetentionDays, mix_monitor_options = @MixMonitorOptions,
                    updated_at = now()
                WHERE id = @Id
                """, new
            {
                policy.Id, policy.Name, Mode = policy.Mode.ToString(),
                policy.Format, policy.StoragePath, policy.RetentionDays, policy.MixMonitorOptions
            }, transaction: tx, cancellationToken: ct));

            await conn.ExecuteAsync(new CommandDefinition(
                "DELETE FROM recording_policy_targets WHERE policy_id = @Id",
                new { policy.Id }, transaction: tx, cancellationToken: ct));

            foreach (var t in policy.Targets)
            {
                await conn.ExecuteAsync(new CommandDefinition("""
                    INSERT INTO recording_policy_targets (policy_id, target_type, target_value)
                    VALUES (@PolicyId, @TargetType, @TargetValue)
                    """, new { PolicyId = policy.Id, t.TargetType, t.TargetValue },
                    transaction: tx, cancellationToken: ct));
            }

            await tx.CommitAsync(ct);
            UpdateOk(_logger, policy.Id);
        }
        catch
        {
            await tx.RollbackAsync(ct);
            throw;
        }
    }

    public async Task DeleteAsync(int id, CancellationToken ct = default)
    {
        try
        {
            await using var conn = new NpgsqlConnection(_connectionString);
            await conn.ExecuteAsync(new CommandDefinition(
                "DELETE FROM recording_policies WHERE id = @Id",
                new { Id = id }, cancellationToken: ct));
            DeleteOk(_logger, id);
        }
        catch (Exception ex)
        {
            DeleteFailed(_logger, ex, id);
            throw;
        }
    }

    [LoggerMessage(Level = LogLevel.Error, Message = "GetAll recording policies failed for server {ServerId}")]
    private static partial void GetAllFailed(ILogger logger, Exception ex, string serverId);

    [LoggerMessage(Level = LogLevel.Error, Message = "GetById recording policy failed for id {Id}")]
    private static partial void GetByIdFailed(ILogger logger, Exception ex, int id);

    [LoggerMessage(Level = LogLevel.Error, Message = "GetByName recording policy failed for {ServerId}/{Name}")]
    private static partial void GetByNameFailed(ILogger logger, Exception ex, string serverId, string name);

    [LoggerMessage(Level = LogLevel.Information, Message = "Inserted recording policy {Id}: {Name}")]
    private static partial void InsertOk(ILogger logger, int id, string name);

    [LoggerMessage(Level = LogLevel.Information, Message = "Updated recording policy {Id}")]
    private static partial void UpdateOk(ILogger logger, int id);

    [LoggerMessage(Level = LogLevel.Information, Message = "Deleted recording policy {Id}")]
    private static partial void DeleteOk(ILogger logger, int id);

    [LoggerMessage(Level = LogLevel.Error, Message = "Delete recording policy failed for id {Id}")]
    private static partial void DeleteFailed(ILogger logger, Exception ex, int id);
}

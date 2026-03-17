using Dapper;
using PbxAdmin.Models;
using Npgsql;

namespace PbxAdmin.Services.Repositories;

public sealed partial class DbConferenceConfigRepository : IConferenceConfigRepository
{
    private readonly string _connectionString;
    private readonly ILogger<DbConferenceConfigRepository> _logger;

    private const string Columns = """
        id AS Id, server_id AS ServerId, name AS Name, number AS Number,
        max_members AS MaxMembers, pin AS Pin, admin_pin AS AdminPin,
        record AS Record, music_on_hold AS MusicOnHold,
        created_at AS CreatedAt, updated_at AS UpdatedAt
        """;

    public DbConferenceConfigRepository(string connectionString, ILogger<DbConferenceConfigRepository> logger)
    {
        _connectionString = connectionString;
        _logger = logger;
    }

    public async Task<List<ConferenceConfig>> GetAllAsync(string serverId, CancellationToken ct = default)
    {
        try
        {
            await using var conn = new NpgsqlConnection(_connectionString);
            return (await conn.QueryAsync<ConferenceConfig>(
                new CommandDefinition($"SELECT {Columns} FROM conference_configs WHERE server_id = @ServerId ORDER BY name",
                    new { ServerId = serverId }, cancellationToken: ct))).AsList();
        }
        catch (Exception ex)
        {
            GetAllFailed(_logger, ex, serverId);
            return [];
        }
    }

    public async Task<ConferenceConfig?> GetByIdAsync(int id, CancellationToken ct = default)
    {
        try
        {
            await using var conn = new NpgsqlConnection(_connectionString);
            return await conn.QuerySingleOrDefaultAsync<ConferenceConfig>(
                new CommandDefinition($"SELECT {Columns} FROM conference_configs WHERE id = @Id",
                    new { Id = id }, cancellationToken: ct));
        }
        catch (Exception ex)
        {
            GetByIdFailed(_logger, ex, id);
            return null;
        }
    }

    public async Task<ConferenceConfig?> GetByNameAsync(string serverId, string name, CancellationToken ct = default)
    {
        try
        {
            await using var conn = new NpgsqlConnection(_connectionString);
            return await conn.QuerySingleOrDefaultAsync<ConferenceConfig>(
                new CommandDefinition($"SELECT {Columns} FROM conference_configs WHERE server_id = @ServerId AND name = @Name",
                    new { ServerId = serverId, Name = name }, cancellationToken: ct));
        }
        catch (Exception ex)
        {
            GetByNameFailed(_logger, ex, serverId, name);
            return null;
        }
    }

    public async Task<int> InsertAsync(ConferenceConfig config, CancellationToken ct = default)
    {
        await using var conn = new NpgsqlConnection(_connectionString);
        var id = await conn.QuerySingleAsync<int>(new CommandDefinition("""
            INSERT INTO conference_configs (server_id, name, number, max_members, pin, admin_pin, record, music_on_hold)
            VALUES (@ServerId, @Name, @Number, @MaxMembers, @Pin, @AdminPin, @Record, @MusicOnHold)
            RETURNING id
            """, new
        {
            config.ServerId, config.Name, config.Number, config.MaxMembers,
            config.Pin, config.AdminPin, config.Record, config.MusicOnHold
        }, cancellationToken: ct));

        InsertOk(_logger, id, config.Name);
        return id;
    }

    public async Task UpdateAsync(ConferenceConfig config, CancellationToken ct = default)
    {
        await using var conn = new NpgsqlConnection(_connectionString);
        await conn.ExecuteAsync(new CommandDefinition("""
            UPDATE conference_configs
            SET name = @Name, number = @Number, max_members = @MaxMembers,
                pin = @Pin, admin_pin = @AdminPin, record = @Record,
                music_on_hold = @MusicOnHold, updated_at = now()
            WHERE id = @Id
            """, new
        {
            config.Id, config.Name, config.Number, config.MaxMembers,
            config.Pin, config.AdminPin, config.Record, config.MusicOnHold
        }, cancellationToken: ct));

        UpdateOk(_logger, config.Id);
    }

    public async Task DeleteAsync(int id, CancellationToken ct = default)
    {
        try
        {
            await using var conn = new NpgsqlConnection(_connectionString);
            await conn.ExecuteAsync(new CommandDefinition(
                "DELETE FROM conference_configs WHERE id = @Id",
                new { Id = id }, cancellationToken: ct));
            DeleteOk(_logger, id);
        }
        catch (Exception ex)
        {
            DeleteFailed(_logger, ex, id);
            throw;
        }
    }

    [LoggerMessage(Level = LogLevel.Error, Message = "GetAll conference configs failed for server {ServerId}")]
    private static partial void GetAllFailed(ILogger logger, Exception ex, string serverId);

    [LoggerMessage(Level = LogLevel.Error, Message = "GetById conference config failed for id {Id}")]
    private static partial void GetByIdFailed(ILogger logger, Exception ex, int id);

    [LoggerMessage(Level = LogLevel.Error, Message = "GetByName conference config failed for {ServerId}/{Name}")]
    private static partial void GetByNameFailed(ILogger logger, Exception ex, string serverId, string name);

    [LoggerMessage(Level = LogLevel.Information, Message = "Inserted conference config {Id}: {Name}")]
    private static partial void InsertOk(ILogger logger, int id, string name);

    [LoggerMessage(Level = LogLevel.Information, Message = "Updated conference config {Id}")]
    private static partial void UpdateOk(ILogger logger, int id);

    [LoggerMessage(Level = LogLevel.Information, Message = "Deleted conference config {Id}")]
    private static partial void DeleteOk(ILogger logger, int id);

    [LoggerMessage(Level = LogLevel.Error, Message = "Delete conference config failed for id {Id}")]
    private static partial void DeleteFailed(ILogger logger, Exception ex, int id);
}

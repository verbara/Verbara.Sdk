using Dapper;
using PbxAdmin.Models;
using Npgsql;

namespace PbxAdmin.Services.Repositories;

public sealed partial class DbMohClassRepository : IMohClassRepository
{
    private readonly string _connectionString;
    private readonly ILogger<DbMohClassRepository> _logger;

    private const string Columns = """
        id AS Id, server_id AS ServerId, name AS Name, mode AS Mode,
        directory AS Directory, sort AS Sort, custom_application AS CustomApplication,
        created_at AS CreatedAt, updated_at AS UpdatedAt
        """;

    public DbMohClassRepository(string connectionString, ILogger<DbMohClassRepository> logger)
    {
        _connectionString = connectionString;
        _logger = logger;
    }

    public async Task<List<MohClass>> GetAllAsync(string serverId, CancellationToken ct = default)
    {
        try
        {
            await using var conn = new NpgsqlConnection(_connectionString);
            return (await conn.QueryAsync<MohClass>(
                new CommandDefinition($"SELECT {Columns} FROM moh_classes WHERE server_id = @ServerId ORDER BY name",
                    new { ServerId = serverId }, cancellationToken: ct))).AsList();
        }
        catch (Exception ex)
        {
            GetAllFailed(_logger, ex, serverId);
            return [];
        }
    }

    public async Task<MohClass?> GetByIdAsync(int id, CancellationToken ct = default)
    {
        try
        {
            await using var conn = new NpgsqlConnection(_connectionString);
            return await conn.QuerySingleOrDefaultAsync<MohClass>(
                new CommandDefinition($"SELECT {Columns} FROM moh_classes WHERE id = @Id",
                    new { Id = id }, cancellationToken: ct));
        }
        catch (Exception ex)
        {
            GetByIdFailed(_logger, ex, id);
            return null;
        }
    }

    public async Task<MohClass?> GetByNameAsync(string serverId, string name, CancellationToken ct = default)
    {
        try
        {
            await using var conn = new NpgsqlConnection(_connectionString);
            return await conn.QuerySingleOrDefaultAsync<MohClass>(
                new CommandDefinition($"SELECT {Columns} FROM moh_classes WHERE server_id = @ServerId AND name = @Name",
                    new { ServerId = serverId, Name = name }, cancellationToken: ct));
        }
        catch (Exception ex)
        {
            GetByNameFailed(_logger, ex, serverId, name);
            return null;
        }
    }

    public async Task<int> InsertAsync(MohClass mohClass, CancellationToken ct = default)
    {
        await using var conn = new NpgsqlConnection(_connectionString);
        var id = await conn.QuerySingleAsync<int>(new CommandDefinition("""
            INSERT INTO moh_classes (server_id, name, mode, directory, sort, custom_application)
            VALUES (@ServerId, @Name, @Mode, @Directory, @Sort, @CustomApplication)
            RETURNING id
            """, new
        {
            mohClass.ServerId, mohClass.Name, mohClass.Mode,
            mohClass.Directory, mohClass.Sort, mohClass.CustomApplication
        }, cancellationToken: ct));

        InsertOk(_logger, id, mohClass.Name);
        return id;
    }

    public async Task UpdateAsync(MohClass mohClass, CancellationToken ct = default)
    {
        await using var conn = new NpgsqlConnection(_connectionString);
        await conn.ExecuteAsync(new CommandDefinition("""
            UPDATE moh_classes
            SET name = @Name, mode = @Mode, directory = @Directory, sort = @Sort,
                custom_application = @CustomApplication, updated_at = now()
            WHERE id = @Id
            """, new
        {
            mohClass.Id, mohClass.Name, mohClass.Mode,
            mohClass.Directory, mohClass.Sort, mohClass.CustomApplication
        }, cancellationToken: ct));

        UpdateOk(_logger, mohClass.Id);
    }

    public async Task DeleteAsync(int id, CancellationToken ct = default)
    {
        try
        {
            await using var conn = new NpgsqlConnection(_connectionString);
            await conn.ExecuteAsync(new CommandDefinition(
                "DELETE FROM moh_classes WHERE id = @Id",
                new { Id = id }, cancellationToken: ct));
            DeleteOk(_logger, id);
        }
        catch (Exception ex)
        {
            DeleteFailed(_logger, ex, id);
            throw;
        }
    }

    [LoggerMessage(Level = LogLevel.Error, Message = "GetAll MOH classes failed for server {ServerId}")]
    private static partial void GetAllFailed(ILogger logger, Exception ex, string serverId);

    [LoggerMessage(Level = LogLevel.Error, Message = "GetById MOH class failed for id {Id}")]
    private static partial void GetByIdFailed(ILogger logger, Exception ex, int id);

    [LoggerMessage(Level = LogLevel.Error, Message = "GetByName MOH class failed for {ServerId}/{Name}")]
    private static partial void GetByNameFailed(ILogger logger, Exception ex, string serverId, string name);

    [LoggerMessage(Level = LogLevel.Information, Message = "Inserted MOH class {Id}: {Name}")]
    private static partial void InsertOk(ILogger logger, int id, string name);

    [LoggerMessage(Level = LogLevel.Information, Message = "Updated MOH class {Id}")]
    private static partial void UpdateOk(ILogger logger, int id);

    [LoggerMessage(Level = LogLevel.Information, Message = "Deleted MOH class {Id}")]
    private static partial void DeleteOk(ILogger logger, int id);

    [LoggerMessage(Level = LogLevel.Error, Message = "Delete MOH class failed for id {Id}")]
    private static partial void DeleteFailed(ILogger logger, Exception ex, int id);
}

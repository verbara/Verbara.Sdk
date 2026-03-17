using Dapper;
using PbxAdmin.Models;
using Npgsql;

namespace PbxAdmin.Services.Repositories;

public sealed partial class DbFeatureCodeRepository : IFeatureCodeRepository
{
    private readonly string _connectionString;
    private readonly ILogger<DbFeatureCodeRepository> _logger;

    private const string FeatureCodeColumns = """
        id AS Id, server_id AS ServerId, code AS Code, name AS Name,
        description AS Description, enabled AS Enabled,
        created_at AS CreatedAt, updated_at AS UpdatedAt
        """;

    private const string ParkingLotColumns = """
        id AS Id, server_id AS ServerId, name AS Name,
        parking_start_slot AS ParkingStartSlot, parking_end_slot AS ParkingEndSlot,
        parking_timeout AS ParkingTimeout, music_on_hold AS MusicOnHold,
        context AS Context, created_at AS CreatedAt, updated_at AS UpdatedAt
        """;

    public DbFeatureCodeRepository(string connectionString, ILogger<DbFeatureCodeRepository> logger)
    {
        _connectionString = connectionString;
        _logger = logger;
    }

    // --- Feature Codes ---

    public async Task<List<FeatureCode>> GetAllFeatureCodesAsync(string serverId, CancellationToken ct = default)
    {
        try
        {
            await using var conn = new NpgsqlConnection(_connectionString);
            return (await conn.QueryAsync<FeatureCode>(
                new CommandDefinition($"SELECT {FeatureCodeColumns} FROM feature_codes WHERE server_id = @ServerId ORDER BY code",
                    new { ServerId = serverId }, cancellationToken: ct))).AsList();
        }
        catch (Exception ex)
        {
            GetAllCodesFailed(_logger, ex, serverId);
            return [];
        }
    }

    public async Task<FeatureCode?> GetFeatureCodeByIdAsync(int id, CancellationToken ct = default)
    {
        try
        {
            await using var conn = new NpgsqlConnection(_connectionString);
            return await conn.QuerySingleOrDefaultAsync<FeatureCode>(
                new CommandDefinition($"SELECT {FeatureCodeColumns} FROM feature_codes WHERE id = @Id",
                    new { Id = id }, cancellationToken: ct));
        }
        catch (Exception ex)
        {
            GetCodeByIdFailed(_logger, ex, id);
            return null;
        }
    }

    public async Task<FeatureCode?> GetFeatureCodeByCodeAsync(string serverId, string code, CancellationToken ct = default)
    {
        try
        {
            await using var conn = new NpgsqlConnection(_connectionString);
            return await conn.QuerySingleOrDefaultAsync<FeatureCode>(
                new CommandDefinition($"SELECT {FeatureCodeColumns} FROM feature_codes WHERE server_id = @ServerId AND code = @Code",
                    new { ServerId = serverId, Code = code }, cancellationToken: ct));
        }
        catch (Exception ex)
        {
            GetCodeByCodeFailed(_logger, ex, serverId, code);
            return null;
        }
    }

    public async Task<int> InsertFeatureCodeAsync(FeatureCode featureCode, CancellationToken ct = default)
    {
        await using var conn = new NpgsqlConnection(_connectionString);
        var id = await conn.QuerySingleAsync<int>(new CommandDefinition("""
            INSERT INTO feature_codes (server_id, code, name, description, enabled)
            VALUES (@ServerId, @Code, @Name, @Description, @Enabled)
            RETURNING id
            """, new
        {
            featureCode.ServerId, featureCode.Code, featureCode.Name,
            featureCode.Description, featureCode.Enabled
        }, cancellationToken: ct));

        InsertCodeOk(_logger, id, featureCode.Code);
        return id;
    }

    public async Task UpdateFeatureCodeAsync(FeatureCode featureCode, CancellationToken ct = default)
    {
        await using var conn = new NpgsqlConnection(_connectionString);
        await conn.ExecuteAsync(new CommandDefinition("""
            UPDATE feature_codes
            SET code = @Code, name = @Name, description = @Description,
                enabled = @Enabled, updated_at = now()
            WHERE id = @Id
            """, new
        {
            featureCode.Id, featureCode.Code, featureCode.Name,
            featureCode.Description, featureCode.Enabled
        }, cancellationToken: ct));

        UpdateCodeOk(_logger, featureCode.Id);
    }

    public async Task DeleteFeatureCodeAsync(int id, CancellationToken ct = default)
    {
        try
        {
            await using var conn = new NpgsqlConnection(_connectionString);
            await conn.ExecuteAsync(new CommandDefinition(
                "DELETE FROM feature_codes WHERE id = @Id",
                new { Id = id }, cancellationToken: ct));
            DeleteCodeOk(_logger, id);
        }
        catch (Exception ex)
        {
            DeleteCodeFailed(_logger, ex, id);
            throw;
        }
    }

    // --- Parking Lots ---

    public async Task<List<ParkingLotConfig>> GetAllParkingLotsAsync(string serverId, CancellationToken ct = default)
    {
        try
        {
            await using var conn = new NpgsqlConnection(_connectionString);
            return (await conn.QueryAsync<ParkingLotConfig>(
                new CommandDefinition($"SELECT {ParkingLotColumns} FROM parking_lot_configs WHERE server_id = @ServerId ORDER BY name",
                    new { ServerId = serverId }, cancellationToken: ct))).AsList();
        }
        catch (Exception ex)
        {
            GetAllLotsFailed(_logger, ex, serverId);
            return [];
        }
    }

    public async Task<ParkingLotConfig?> GetParkingLotByIdAsync(int id, CancellationToken ct = default)
    {
        try
        {
            await using var conn = new NpgsqlConnection(_connectionString);
            return await conn.QuerySingleOrDefaultAsync<ParkingLotConfig>(
                new CommandDefinition($"SELECT {ParkingLotColumns} FROM parking_lot_configs WHERE id = @Id",
                    new { Id = id }, cancellationToken: ct));
        }
        catch (Exception ex)
        {
            GetLotByIdFailed(_logger, ex, id);
            return null;
        }
    }

    public async Task<ParkingLotConfig?> GetParkingLotByNameAsync(string serverId, string name, CancellationToken ct = default)
    {
        try
        {
            await using var conn = new NpgsqlConnection(_connectionString);
            return await conn.QuerySingleOrDefaultAsync<ParkingLotConfig>(
                new CommandDefinition($"SELECT {ParkingLotColumns} FROM parking_lot_configs WHERE server_id = @ServerId AND name = @Name",
                    new { ServerId = serverId, Name = name }, cancellationToken: ct));
        }
        catch (Exception ex)
        {
            GetLotByNameFailed(_logger, ex, serverId, name);
            return null;
        }
    }

    public async Task<int> InsertParkingLotAsync(ParkingLotConfig lot, CancellationToken ct = default)
    {
        await using var conn = new NpgsqlConnection(_connectionString);
        var id = await conn.QuerySingleAsync<int>(new CommandDefinition("""
            INSERT INTO parking_lot_configs (server_id, name, parking_start_slot, parking_end_slot, parking_timeout, music_on_hold, context)
            VALUES (@ServerId, @Name, @ParkingStartSlot, @ParkingEndSlot, @ParkingTimeout, @MusicOnHold, @Context)
            RETURNING id
            """, new
        {
            lot.ServerId, lot.Name, lot.ParkingStartSlot, lot.ParkingEndSlot,
            lot.ParkingTimeout, lot.MusicOnHold, lot.Context
        }, cancellationToken: ct));

        InsertLotOk(_logger, id, lot.Name);
        return id;
    }

    public async Task UpdateParkingLotAsync(ParkingLotConfig lot, CancellationToken ct = default)
    {
        await using var conn = new NpgsqlConnection(_connectionString);
        await conn.ExecuteAsync(new CommandDefinition("""
            UPDATE parking_lot_configs
            SET name = @Name, parking_start_slot = @ParkingStartSlot, parking_end_slot = @ParkingEndSlot,
                parking_timeout = @ParkingTimeout, music_on_hold = @MusicOnHold, context = @Context,
                updated_at = now()
            WHERE id = @Id
            """, new
        {
            lot.Id, lot.Name, lot.ParkingStartSlot, lot.ParkingEndSlot,
            lot.ParkingTimeout, lot.MusicOnHold, lot.Context
        }, cancellationToken: ct));

        UpdateLotOk(_logger, lot.Id);
    }

    public async Task DeleteParkingLotAsync(int id, CancellationToken ct = default)
    {
        try
        {
            await using var conn = new NpgsqlConnection(_connectionString);
            await conn.ExecuteAsync(new CommandDefinition(
                "DELETE FROM parking_lot_configs WHERE id = @Id",
                new { Id = id }, cancellationToken: ct));
            DeleteLotOk(_logger, id);
        }
        catch (Exception ex)
        {
            DeleteLotFailed(_logger, ex, id);
            throw;
        }
    }

    // --- LoggerMessages: Feature Codes ---

    [LoggerMessage(Level = LogLevel.Error, Message = "GetAll feature codes failed for server {ServerId}")]
    private static partial void GetAllCodesFailed(ILogger logger, Exception ex, string serverId);

    [LoggerMessage(Level = LogLevel.Error, Message = "GetById feature code failed for id {Id}")]
    private static partial void GetCodeByIdFailed(ILogger logger, Exception ex, int id);

    [LoggerMessage(Level = LogLevel.Error, Message = "GetByCode feature code failed for {ServerId}/{Code}")]
    private static partial void GetCodeByCodeFailed(ILogger logger, Exception ex, string serverId, string code);

    [LoggerMessage(Level = LogLevel.Information, Message = "Inserted feature code {Id}: {Code}")]
    private static partial void InsertCodeOk(ILogger logger, int id, string code);

    [LoggerMessage(Level = LogLevel.Information, Message = "Updated feature code {Id}")]
    private static partial void UpdateCodeOk(ILogger logger, int id);

    [LoggerMessage(Level = LogLevel.Information, Message = "Deleted feature code {Id}")]
    private static partial void DeleteCodeOk(ILogger logger, int id);

    [LoggerMessage(Level = LogLevel.Error, Message = "Delete feature code failed for id {Id}")]
    private static partial void DeleteCodeFailed(ILogger logger, Exception ex, int id);

    // --- LoggerMessages: Parking Lots ---

    [LoggerMessage(Level = LogLevel.Error, Message = "GetAll parking lots failed for server {ServerId}")]
    private static partial void GetAllLotsFailed(ILogger logger, Exception ex, string serverId);

    [LoggerMessage(Level = LogLevel.Error, Message = "GetById parking lot failed for id {Id}")]
    private static partial void GetLotByIdFailed(ILogger logger, Exception ex, int id);

    [LoggerMessage(Level = LogLevel.Error, Message = "GetByName parking lot failed for {ServerId}/{Name}")]
    private static partial void GetLotByNameFailed(ILogger logger, Exception ex, string serverId, string name);

    [LoggerMessage(Level = LogLevel.Information, Message = "Inserted parking lot {Id}: {Name}")]
    private static partial void InsertLotOk(ILogger logger, int id, string name);

    [LoggerMessage(Level = LogLevel.Information, Message = "Updated parking lot {Id}")]
    private static partial void UpdateLotOk(ILogger logger, int id);

    [LoggerMessage(Level = LogLevel.Information, Message = "Deleted parking lot {Id}")]
    private static partial void DeleteLotOk(ILogger logger, int id);

    [LoggerMessage(Level = LogLevel.Error, Message = "Delete parking lot failed for id {Id}")]
    private static partial void DeleteLotFailed(ILogger logger, Exception ex, int id);
}

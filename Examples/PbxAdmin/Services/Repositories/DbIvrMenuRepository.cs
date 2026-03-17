using Dapper;
using PbxAdmin.Models;
using Npgsql;

namespace PbxAdmin.Services.Repositories;

internal static partial class DbIvrMenuRepositoryLog
{
    [LoggerMessage(Level = LogLevel.Debug, Message = "[IVR_DB] GetMenus: server={ServerId} count={Count}")]
    public static partial void GetMenus(ILogger logger, string serverId, int count);

    [LoggerMessage(Level = LogLevel.Debug, Message = "[IVR_DB] Created menu: id={Id} name={Name}")]
    public static partial void CreatedMenu(ILogger logger, int id, string name);

    [LoggerMessage(Level = LogLevel.Debug, Message = "[IVR_DB] Updated menu: id={Id} name={Name}")]
    public static partial void UpdatedMenu(ILogger logger, int id, string name);

    [LoggerMessage(Level = LogLevel.Debug, Message = "[IVR_DB] Deleted menu: id={Id}")]
    public static partial void DeletedMenu(ILogger logger, int id);

    [LoggerMessage(Level = LogLevel.Error, Message = "[IVR_DB] Operation failed: operation={Operation}")]
    public static partial void OperationFailed(ILogger logger, Exception exception, string operation);
}

public sealed class DbIvrMenuRepository : IIvrMenuRepository
{
    private readonly string _connectionString;
    private readonly ILogger<DbIvrMenuRepository> _logger;

    private const string MenuColumns = """
        id AS Id,
        server_id AS ServerId,
        name AS Name,
        label AS Label,
        greeting AS Greeting,
        timeout AS Timeout,
        max_retries AS MaxRetries,
        invalid_dest_type AS InvalidDestType,
        invalid_dest AS InvalidDest,
        timeout_dest_type AS TimeoutDestType,
        timeout_dest AS TimeoutDest,
        enabled AS Enabled,
        notes AS Notes
        """;

    private const string ItemColumns = """
        id AS Id,
        menu_id AS MenuId,
        digit AS Digit,
        label AS Label,
        dest_type AS DestType,
        dest_target AS DestTarget,
        trunk AS Trunk
        """;

    public DbIvrMenuRepository(string connectionString, ILogger<DbIvrMenuRepository> logger)
    {
        _connectionString = connectionString;
        _logger = logger;
    }

    public async Task<List<IvrMenuConfig>> GetMenusAsync(string serverId, CancellationToken ct = default)
    {
        try
        {
            await using var conn = new NpgsqlConnection(_connectionString);
            await conn.OpenAsync(ct);

            var sql = $"SELECT {MenuColumns} FROM ivr_menus WHERE server_id = @ServerId ORDER BY name";
            var menus = (await conn.QueryAsync<IvrMenuConfig>(
                new CommandDefinition(sql, new { ServerId = serverId }, cancellationToken: ct))).AsList();

            if (menus.Count > 0)
            {
                var menuIds = menus.Select(m => m.Id).ToArray();
                var itemSql = $"SELECT {ItemColumns} FROM ivr_menu_items WHERE menu_id = ANY(@Ids) ORDER BY menu_id, digit";
                var items = (await conn.QueryAsync<IvrMenuItemConfig>(
                    new CommandDefinition(itemSql, new { Ids = menuIds }, cancellationToken: ct))).AsList();

                var lookup = items.ToLookup(i => i.MenuId);
                foreach (var menu in menus)
                    menu.Items = lookup[menu.Id].ToList();
            }

            DbIvrMenuRepositoryLog.GetMenus(_logger, serverId, menus.Count);
            return menus;
        }
        catch (Exception ex)
        {
            DbIvrMenuRepositoryLog.OperationFailed(_logger, ex, "GetMenus");
            return [];
        }
    }

    public async Task<IvrMenuConfig?> GetMenuAsync(int id, CancellationToken ct = default)
    {
        try
        {
            await using var conn = new NpgsqlConnection(_connectionString);
            await conn.OpenAsync(ct);

            var sql = $"SELECT {MenuColumns} FROM ivr_menus WHERE id = @Id";
            var menu = await conn.QueryFirstOrDefaultAsync<IvrMenuConfig>(
                new CommandDefinition(sql, new { Id = id }, cancellationToken: ct));

            if (menu is not null)
            {
                var itemSql = $"SELECT {ItemColumns} FROM ivr_menu_items WHERE menu_id = @MenuId ORDER BY digit";
                menu.Items = (await conn.QueryAsync<IvrMenuItemConfig>(
                    new CommandDefinition(itemSql, new { MenuId = id }, cancellationToken: ct))).AsList();
            }

            return menu;
        }
        catch (Exception ex)
        {
            DbIvrMenuRepositoryLog.OperationFailed(_logger, ex, "GetMenu");
            return null;
        }
    }

    public async Task<IvrMenuConfig?> GetMenuByNameAsync(string serverId, string name, CancellationToken ct = default)
    {
        try
        {
            await using var conn = new NpgsqlConnection(_connectionString);
            await conn.OpenAsync(ct);

            var sql = $"SELECT {MenuColumns} FROM ivr_menus WHERE server_id = @ServerId AND name = @Name";
            var menu = await conn.QueryFirstOrDefaultAsync<IvrMenuConfig>(
                new CommandDefinition(sql, new { ServerId = serverId, Name = name }, cancellationToken: ct));

            if (menu is not null)
            {
                var itemSql = $"SELECT {ItemColumns} FROM ivr_menu_items WHERE menu_id = @MenuId ORDER BY digit";
                menu.Items = (await conn.QueryAsync<IvrMenuItemConfig>(
                    new CommandDefinition(itemSql, new { MenuId = menu.Id }, cancellationToken: ct))).AsList();
            }

            return menu;
        }
        catch (Exception ex)
        {
            DbIvrMenuRepositoryLog.OperationFailed(_logger, ex, "GetMenuByName");
            return null;
        }
    }

    public async Task<int> CreateMenuAsync(IvrMenuConfig config, CancellationToken ct = default)
    {
        await using var conn = new NpgsqlConnection(_connectionString);
        await conn.OpenAsync(ct);
        await using var tx = await conn.BeginTransactionAsync(ct);

        try
        {
            var sql = """
                INSERT INTO ivr_menus (server_id, name, label, greeting, timeout, max_retries,
                    invalid_dest_type, invalid_dest, timeout_dest_type, timeout_dest, enabled, notes)
                VALUES (@ServerId, @Name, @Label, @Greeting, @Timeout, @MaxRetries,
                    @InvalidDestType, @InvalidDest, @TimeoutDestType, @TimeoutDest, @Enabled, @Notes)
                RETURNING id
                """;
            var id = await conn.ExecuteScalarAsync<int>(
                new CommandDefinition(sql, config, tx, cancellationToken: ct));

            if (config.Items.Count > 0)
            {
                var itemSql = """
                    INSERT INTO ivr_menu_items (menu_id, digit, label, dest_type, dest_target, trunk)
                    VALUES (@MenuId, @Digit, @Label, @DestType, @DestTarget, @Trunk)
                    """;
                foreach (var item in config.Items)
                {
                    item.MenuId = id;
                    await conn.ExecuteAsync(new CommandDefinition(itemSql, item, tx, cancellationToken: ct));
                }
            }

            await tx.CommitAsync(ct);
            DbIvrMenuRepositoryLog.CreatedMenu(_logger, id, config.Name);
            return id;
        }
        catch
        {
            await tx.RollbackAsync(ct);
            throw;
        }
    }

    public async Task UpdateMenuAsync(IvrMenuConfig config, CancellationToken ct = default)
    {
        await using var conn = new NpgsqlConnection(_connectionString);
        await conn.OpenAsync(ct);
        await using var tx = await conn.BeginTransactionAsync(ct);

        try
        {
            var sql = """
                UPDATE ivr_menus SET label = @Label, greeting = @Greeting, timeout = @Timeout,
                    max_retries = @MaxRetries, invalid_dest_type = @InvalidDestType,
                    invalid_dest = @InvalidDest, timeout_dest_type = @TimeoutDestType,
                    timeout_dest = @TimeoutDest, enabled = @Enabled, notes = @Notes
                WHERE id = @Id
                """;
            await conn.ExecuteAsync(new CommandDefinition(sql, config, tx, cancellationToken: ct));

            await conn.ExecuteAsync(new CommandDefinition(
                "DELETE FROM ivr_menu_items WHERE menu_id = @Id",
                new { config.Id }, tx, cancellationToken: ct));

            if (config.Items.Count > 0)
            {
                var itemSql = """
                    INSERT INTO ivr_menu_items (menu_id, digit, label, dest_type, dest_target, trunk)
                    VALUES (@MenuId, @Digit, @Label, @DestType, @DestTarget, @Trunk)
                    """;
                foreach (var item in config.Items)
                {
                    item.MenuId = config.Id;
                    await conn.ExecuteAsync(new CommandDefinition(itemSql, item, tx, cancellationToken: ct));
                }
            }

            await tx.CommitAsync(ct);
            DbIvrMenuRepositoryLog.UpdatedMenu(_logger, config.Id, config.Name);
        }
        catch
        {
            await tx.RollbackAsync(ct);
            throw;
        }
    }

    public async Task DeleteMenuAsync(int id, CancellationToken ct = default)
    {
        try
        {
            await using var conn = new NpgsqlConnection(_connectionString);
            await conn.OpenAsync(ct);
            await conn.ExecuteAsync(new CommandDefinition(
                "DELETE FROM ivr_menus WHERE id = @Id", new { Id = id }, cancellationToken: ct));
            DbIvrMenuRepositoryLog.DeletedMenu(_logger, id);
        }
        catch (Exception ex)
        {
            DbIvrMenuRepositoryLog.OperationFailed(_logger, ex, "DeleteMenu");
            throw;
        }
    }

    public async Task<bool> IsMenuReferencedAsync(int id, CancellationToken ct = default)
    {
        try
        {
            await using var conn = new NpgsqlConnection(_connectionString);
            await conn.OpenAsync(ct);

            var menu = await conn.QueryFirstOrDefaultAsync<(string Name, string ServerId)>(
                new CommandDefinition(
                    "SELECT name AS Name, server_id AS ServerId FROM ivr_menus WHERE id = @Id",
                    new { Id = id }, cancellationToken: ct));

            if (menu == default) return false;

            var ivrRef = await conn.ExecuteScalarAsync<bool>(new CommandDefinition(
                """
                SELECT EXISTS(
                    SELECT 1 FROM ivr_menu_items
                    WHERE dest_type = 'ivr' AND dest_target = @Name
                    AND menu_id NOT IN (SELECT id FROM ivr_menus WHERE name = @Name AND server_id = @ServerId))
                """,
                new { menu.Name, menu.ServerId }, cancellationToken: ct));
            if (ivrRef) return true;

            var routeRef = await conn.ExecuteScalarAsync<bool>(new CommandDefinition(
                "SELECT EXISTS(SELECT 1 FROM routes_inbound WHERE destination_type = 'ivr' AND destination = @Name AND server_id = @ServerId)",
                new { menu.Name, menu.ServerId }, cancellationToken: ct));
            if (routeRef) return true;

            var tcRef = await conn.ExecuteScalarAsync<bool>(new CommandDefinition(
                """
                SELECT EXISTS(
                    SELECT 1 FROM time_conditions
                    WHERE server_id = @ServerId
                    AND ((match_dest_type = 'ivr' AND match_dest = @Name)
                         OR (nomatch_dest_type = 'ivr' AND nomatch_dest = @Name)))
                """,
                new { menu.Name, menu.ServerId }, cancellationToken: ct));
            return tcRef;
        }
        catch (Exception ex)
        {
            DbIvrMenuRepositoryLog.OperationFailed(_logger, ex, "IsMenuReferenced");
            return false;
        }
    }
}

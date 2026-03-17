using System.Text;
using Asterisk.Sdk.Ami.Responses;
using Dapper;
using Npgsql;

namespace PbxAdmin.Services;

internal static partial class DbConfigLog
{
    [LoggerMessage(Level = LogLevel.Debug, Message = "[CONFIG_DB] GetCategories: filename={Filename} tables={TableCount} rows={RowCount}")]
    public static partial void GetCategories(ILogger logger, string filename, int tableCount, int rowCount);

    [LoggerMessage(Level = LogLevel.Debug, Message = "[CONFIG_DB] CreateSection: table={Table} id={Id}")]
    public static partial void CreateSection(ILogger logger, string table, string id);

    [LoggerMessage(Level = LogLevel.Debug, Message = "[CONFIG_DB] UpdateSection: table={Table} id={Id} rows_affected={RowsAffected}")]
    public static partial void UpdateSection(ILogger logger, string table, string id, int rowsAffected);

    [LoggerMessage(Level = LogLevel.Debug, Message = "[CONFIG_DB] DeleteSection: id={Id} tables={TableCount}")]
    public static partial void DeleteSection(ILogger logger, string id, int tableCount);

    [LoggerMessage(Level = LogLevel.Warning, Message = "[CONFIG_DB] No table mapping: filename={Filename}")]
    public static partial void NoTableMapping(ILogger logger, string filename);

    [LoggerMessage(Level = LogLevel.Error, Message = "[CONFIG_DB] Operation failed: filename={Filename} section={Section}")]
    public static partial void OperationFailed(ILogger logger, Exception exception, string filename, string? section);
}

/// <summary>
/// Reads and writes Asterisk configuration via PostgreSQL (Realtime backend).
/// AMI-only operations (<see cref="ExecuteCommandAsync"/>, <see cref="ReloadModuleAsync"/>) are
/// delegated to <see cref="PbxConfigManager"/>.
/// </summary>
public sealed class DbConfigProvider : IConfigProvider
{
    private readonly string _connectionString;
    private readonly PbxConfigManager _amiProvider;
    private readonly ILogger<DbConfigProvider> _logger;

    // Columns to exclude from the variables dictionary (they are structural, not config)
    private static readonly HashSet<string> ExcludedColumns = new(StringComparer.OrdinalIgnoreCase)
    {
        "id", "name", "mailbox", "queue_name",
    };

    public DbConfigProvider(string connectionString, PbxConfigManager amiProvider, ILogger<DbConfigProvider> logger)
    {
        _connectionString = connectionString;
        _amiProvider = amiProvider;
        _logger = logger;
    }

    public async Task<List<ConfigCategory>> GetCategoriesAsync(string serverId, string filename, CancellationToken ct = default)
    {
        var tables = RealtimeTableMap.GetTables(filename);
        if (tables.Count == 0)
        {
            DbConfigLog.NoTableMapping(_logger, filename);
            return [];
        }

        var result = new List<ConfigCategory>();

        try
        {
            await using var conn = new NpgsqlConnection(_connectionString);
            await conn.OpenAsync(ct);

            foreach (var table in tables)
            {
                var sql = $"SELECT * FROM {table.TableName}";
                var rows = (await conn.QueryAsync(new CommandDefinition(sql, cancellationToken: ct))).AsList();

                foreach (IDictionary<string, object?> row in rows)
                {
                    var id = row[table.IdColumn]?.ToString();
                    if (id is null) continue;

                    var variables = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

                    // Add type info if the table has a TypeValue (e.g., PJSIP sections)
                    if (table.TypeColumn is not null && table.TypeValue is not null)
                    {
                        variables[table.TypeColumn] = table.TypeValue;
                    }

                    foreach (var (key, value) in row)
                    {
                        if (ExcludedColumns.Contains(key) || value is null)
                            continue;

                        var strValue = value.ToString()!;
                        if (strValue.Length > 0)
                            variables[key] = strValue;
                    }

                    result.Add(new ConfigCategory(id, variables));
                }
            }

            DbConfigLog.GetCategories(_logger, filename, tables.Count, result.Count);
        }
        catch (Exception ex)
        {
            DbConfigLog.OperationFailed(_logger, ex, filename, null);
        }

        return result;
    }

    public async Task<Dictionary<string, string>?> GetSectionAsync(string serverId, string filename, string section, CancellationToken ct = default)
    {
        var tables = RealtimeTableMap.GetTables(filename);
        if (tables.Count == 0) return null;

        try
        {
            await using var conn = new NpgsqlConnection(_connectionString);
            await conn.OpenAsync(ct);

            foreach (var table in tables)
            {
                var sql = $"SELECT * FROM {table.TableName} WHERE {table.IdColumn} = @Id";
                if (await conn.QueryFirstOrDefaultAsync(new CommandDefinition(sql, new { Id = section }, cancellationToken: ct))
                    is not IDictionary<string, object?> row) continue;

                var variables = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

                if (table.TypeColumn is not null && table.TypeValue is not null)
                {
                    variables[table.TypeColumn] = table.TypeValue;
                }

                foreach (var (key, value) in row)
                {
                    if (ExcludedColumns.Contains(key) || value is null)
                        continue;

                    var strValue = value.ToString()!;
                    if (strValue.Length > 0)
                        variables[key] = strValue;
                }

                return variables;
            }
        }
        catch (Exception ex)
        {
            DbConfigLog.OperationFailed(_logger, ex, filename, section);
        }

        return null;
    }

    public async Task<bool> CreateSectionAsync(string serverId, string filename, string section,
        Dictionary<string, string> variables, string? templateName = null, CancellationToken ct = default)
    {
        var tables = RealtimeTableMap.GetTables(filename);
        var table = RealtimeTableMap.ResolveTable(tables, variables);
        if (table is null)
        {
            DbConfigLog.NoTableMapping(_logger, filename);
            return false;
        }

        try
        {
            await using var conn = new NpgsqlConnection(_connectionString);
            await conn.OpenAsync(ct);

            var columns = new List<string> { table.IdColumn };
            var paramNames = new List<string> { "@p0" };
            var parameters = new DynamicParameters();
            parameters.Add("p0", section);

            var idx = 1;
            foreach (var (key, value) in variables)
            {
                // Skip the type column for PJSIP tables — it's implicit in the table
                if (string.Equals(key, table.TypeColumn, StringComparison.OrdinalIgnoreCase)
                    && table.TypeValue is not null)
                    continue;

                if (ExcludedColumns.Contains(key))
                    continue;

                columns.Add(key);
                paramNames.Add($"@p{idx}");
                parameters.Add($"p{idx}", value);
                idx++;
            }

            var sql = $"INSERT INTO {table.TableName} ({string.Join(", ", columns)}) VALUES ({string.Join(", ", paramNames)})";
            await conn.ExecuteAsync(new CommandDefinition(sql, parameters, cancellationToken: ct));

            DbConfigLog.CreateSection(_logger, table.TableName, section);
            return true;
        }
        catch (Exception ex)
        {
            DbConfigLog.OperationFailed(_logger, ex, filename, section);
            return false;
        }
    }

    /// <summary>
    /// DB provider: delegates to AMI since extensions.conf with duplicate keys
    /// (exten/same directives) is file-based only.
    /// </summary>
    public Task<bool> CreateSectionWithLinesAsync(string serverId, string filename, string section,
        List<KeyValuePair<string, string>> lines, CancellationToken ct = default)
        => _amiProvider.CreateSectionWithLinesAsync(serverId, filename, section, lines, ct);

    public async Task<bool> UpdateSectionAsync(string serverId, string filename, string section,
        Dictionary<string, string> variables, CancellationToken ct = default)
    {
        var tables = RealtimeTableMap.GetTables(filename);
        if (tables.Count == 0) return false;

        try
        {
            await using var conn = new NpgsqlConnection(_connectionString);
            await conn.OpenAsync(ct);

            // Try to update in each table; if the row exists in one, update it there
            foreach (var table in tables)
            {
                var setClauses = new List<string>();
                var parameters = new DynamicParameters();
                parameters.Add("Id", section);

                var idx = 0;
                foreach (var (key, value) in variables)
                {
                    if (string.Equals(key, table.TypeColumn, StringComparison.OrdinalIgnoreCase)
                        && table.TypeValue is not null)
                        continue;

                    if (ExcludedColumns.Contains(key))
                        continue;

                    setClauses.Add($"{key} = @p{idx}");
                    parameters.Add($"p{idx}", value);
                    idx++;
                }

                if (setClauses.Count == 0) continue;

                var sql = $"UPDATE {table.TableName} SET {string.Join(", ", setClauses)} WHERE {table.IdColumn} = @Id";
                var rowsAffected = await conn.ExecuteAsync(new CommandDefinition(sql, parameters, cancellationToken: ct));

                if (rowsAffected > 0)
                {
                    DbConfigLog.UpdateSection(_logger, table.TableName, section, rowsAffected);
                    return true;
                }
            }

            // Fallback: INSERT if no existing row was found
            return await CreateSectionAsync(serverId, filename, section, variables, ct: ct);
        }
        catch (Exception ex)
        {
            DbConfigLog.OperationFailed(_logger, ex, filename, section);
            return false;
        }
    }

    public async Task<bool> DeleteSectionAsync(string serverId, string filename, string section, CancellationToken ct = default)
    {
        var tables = RealtimeTableMap.GetTables(filename);
        if (tables.Count == 0) return false;

        try
        {
            await using var conn = new NpgsqlConnection(_connectionString);
            await conn.OpenAsync(ct);

            var deleted = false;
            foreach (var table in tables)
            {
                var sql = $"DELETE FROM {table.TableName} WHERE {table.IdColumn} = @Id";
                var rowsAffected = await conn.ExecuteAsync(new CommandDefinition(sql, new { Id = section }, cancellationToken: ct));
                if (rowsAffected > 0) deleted = true;
            }

            DbConfigLog.DeleteSection(_logger, section, tables.Count);
            return deleted;
        }
        catch (Exception ex)
        {
            DbConfigLog.OperationFailed(_logger, ex, filename, section);
            return false;
        }
    }

    /// <summary>Delegates to AMI — there is no database equivalent for CLI commands.</summary>
    public Task<string?> ExecuteCommandAsync(string serverId, string command, CancellationToken ct = default)
        => _amiProvider.ExecuteCommandAsync(serverId, command, ct);

    /// <summary>Delegates to AMI — module reload requires CLI access.</summary>
    public Task<bool> ReloadModuleAsync(string serverId, string moduleName, CancellationToken ct = default)
        => _amiProvider.ReloadModuleAsync(serverId, moduleName, ct);
}

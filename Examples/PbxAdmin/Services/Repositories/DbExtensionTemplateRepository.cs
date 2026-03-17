using System.Text.Json;
using Dapper;
using Npgsql;
using PbxAdmin.Models;

namespace PbxAdmin.Services.Repositories;

internal static partial class DbExtensionTemplateRepositoryLog
{
    [LoggerMessage(Level = LogLevel.Debug, Message = "[TEMPLATE_DB] GetAll: count={Count}")]
    public static partial void GetAll(ILogger logger, int count);

    [LoggerMessage(Level = LogLevel.Debug, Message = "[TEMPLATE_DB] Created template: id={Id} name={Name}")]
    public static partial void CreatedTemplate(ILogger logger, int id, string name);

    [LoggerMessage(Level = LogLevel.Debug, Message = "[TEMPLATE_DB] Deleted template: id={Id}")]
    public static partial void DeletedTemplate(ILogger logger, int id);

    [LoggerMessage(Level = LogLevel.Error, Message = "[TEMPLATE_DB] Operation failed: operation={Operation}")]
    public static partial void OperationFailed(ILogger logger, Exception exception, string operation);
}

/// <summary>
/// PostgreSQL/Dapper implementation of <see cref="IExtensionTemplateRepository"/>.
/// </summary>
public sealed class DbExtensionTemplateRepository : IExtensionTemplateRepository
{
    private readonly string _connectionString;
    private readonly ILogger<DbExtensionTemplateRepository> _logger;

    private static readonly JsonSerializerOptions JsonOptions = new();

    public DbExtensionTemplateRepository(string connectionString, ILogger<DbExtensionTemplateRepository> logger)
    {
        _connectionString = connectionString;
        _logger = logger;
    }

    public async Task<IReadOnlyList<ExtensionTemplate>> GetAllAsync()
    {
        try
        {
            await using var conn = new NpgsqlConnection(_connectionString);
            await conn.OpenAsync();

            const string sql = """
                SELECT id, name, description, is_built_in, config_json, created_at
                FROM extension_templates
                ORDER BY is_built_in DESC, name
                """;

            var rows = (await conn.QueryAsync(sql)).AsList();
            var templates = rows.Select(MapRow).ToList();

            DbExtensionTemplateRepositoryLog.GetAll(_logger, templates.Count);
            return templates;
        }
        catch (Exception ex)
        {
            DbExtensionTemplateRepositoryLog.OperationFailed(_logger, ex, "GetAll");
            return [];
        }
    }

    public async Task<ExtensionTemplate?> GetByIdAsync(int id)
    {
        try
        {
            await using var conn = new NpgsqlConnection(_connectionString);
            await conn.OpenAsync();

            const string sql = """
                SELECT id, name, description, is_built_in, config_json, created_at
                FROM extension_templates
                WHERE id = @Id
                """;

            var row = await conn.QueryFirstOrDefaultAsync(
                new CommandDefinition(sql, new { Id = id }));

            return row is null ? null : MapRow(row);
        }
        catch (Exception ex)
        {
            DbExtensionTemplateRepositoryLog.OperationFailed(_logger, ex, "GetById");
            return null;
        }
    }

    public async Task<int> CreateAsync(ExtensionTemplate extensionTemplate)
    {
        try
        {
            await using var conn = new NpgsqlConnection(_connectionString);
            await conn.OpenAsync();

            var configJson = JsonSerializer.Serialize(extensionTemplate.Config, JsonOptions);

            const string sql = """
                INSERT INTO extension_templates (name, description, is_built_in, config_json)
                VALUES (@Name, @Description, @IsBuiltIn, @ConfigJson::jsonb)
                RETURNING id
                """;

            var id = await conn.ExecuteScalarAsync<int>(
                new CommandDefinition(sql, new
                {
                    extensionTemplate.Name,
                    extensionTemplate.Description,
                    extensionTemplate.IsBuiltIn,
                    ConfigJson = configJson,
                }));

            DbExtensionTemplateRepositoryLog.CreatedTemplate(_logger, id, extensionTemplate.Name);
            return id;
        }
        catch (Exception ex)
        {
            DbExtensionTemplateRepositoryLog.OperationFailed(_logger, ex, "Create");
            return 0;
        }
    }

    public async Task DeleteAsync(int id)
    {
        try
        {
            await using var conn = new NpgsqlConnection(_connectionString);
            await conn.OpenAsync();

            const string sql = "DELETE FROM extension_templates WHERE id = @Id AND is_built_in = FALSE";
            await conn.ExecuteAsync(new CommandDefinition(sql, new { Id = id }));

            DbExtensionTemplateRepositoryLog.DeletedTemplate(_logger, id);
        }
        catch (Exception ex)
        {
            DbExtensionTemplateRepositoryLog.OperationFailed(_logger, ex, "Delete");
        }
    }

    // ──────────────────────────── Helpers ────────────────────────────

    private static ExtensionTemplate MapRow(dynamic row)
    {
        var configJson = (string)row.config_json;
        var config = JsonSerializer.Deserialize<ExtensionConfig>(configJson, JsonOptions) ?? new ExtensionConfig();

        return new ExtensionTemplate
        {
            Id = (int)row.id,
            Name = (string)row.name,
            Description = (string)(row.description ?? ""),
            IsBuiltIn = (bool)row.is_built_in,
            Config = config,
            CreatedAt = (DateTime)row.created_at,
        };
    }
}

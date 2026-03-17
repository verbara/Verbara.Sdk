using System.Collections.Concurrent;
using Dapper;
using Npgsql;

namespace PbxAdmin.Services;

public sealed partial class RecordingMohSchemaManager : IRecordingMohSchemaManager
{
    private readonly string? _connectionString;
    private readonly ILogger<RecordingMohSchemaManager> _logger;
    private readonly ConcurrentDictionary<string, bool> _initialized = new();

    public RecordingMohSchemaManager(IConfiguration config, ILogger<RecordingMohSchemaManager> logger)
    {
        _logger = logger;
        _connectionString = config.GetSection("Asterisk:Servers").GetChildren()
            .Where(s => string.Equals(s["ConfigMode"], "Realtime", StringComparison.OrdinalIgnoreCase))
            .Select(s => s["RealtimeConnectionString"])
            .FirstOrDefault()
            ?? config.GetConnectionString("QueueConfig");
    }

    public bool IsAvailable => _connectionString is not null;

    public async Task EnsureSchemaAsync(CancellationToken ct = default)
    {
        if (_connectionString is null) return;
        if (!_initialized.TryAdd("schema", true)) return;

        try
        {
            await using var conn = new NpgsqlConnection(_connectionString);
            await conn.OpenAsync(ct);
            await conn.ExecuteAsync(new CommandDefinition(SchemaSql, cancellationToken: ct));
            SchemaCreated(_logger);
        }
        catch (Exception ex)
        {
            _initialized.TryRemove("schema", out _);
            SchemaFailed(_logger, ex);
            throw;
        }
    }

    private const string SchemaSql = """
        CREATE TABLE IF NOT EXISTS recording_policies (
            id              SERIAL PRIMARY KEY,
            server_id       TEXT NOT NULL,
            name            TEXT NOT NULL,
            mode            TEXT NOT NULL DEFAULT 'Always',
            format          TEXT NOT NULL DEFAULT 'wav',
            storage_path    TEXT NOT NULL DEFAULT '/var/spool/asterisk/monitor/',
            retention_days  INT NOT NULL DEFAULT 0,
            mix_monitor_options TEXT,
            created_at      TIMESTAMPTZ NOT NULL DEFAULT now(),
            updated_at      TIMESTAMPTZ NOT NULL DEFAULT now(),
            UNIQUE (server_id, name)
        );

        CREATE TABLE IF NOT EXISTS recording_policy_targets (
            id          SERIAL PRIMARY KEY,
            policy_id   INT NOT NULL REFERENCES recording_policies(id) ON DELETE CASCADE,
            target_type TEXT NOT NULL,
            target_value TEXT NOT NULL
        );

        CREATE INDEX IF NOT EXISTS idx_recording_targets_policy ON recording_policy_targets(policy_id);
        CREATE INDEX IF NOT EXISTS idx_recording_policies_server ON recording_policies(server_id);

        CREATE TABLE IF NOT EXISTS moh_classes (
            id                  SERIAL PRIMARY KEY,
            server_id           TEXT NOT NULL,
            name                TEXT NOT NULL,
            mode                TEXT NOT NULL DEFAULT 'files',
            directory           TEXT NOT NULL,
            sort                TEXT NOT NULL DEFAULT 'random',
            custom_application  TEXT,
            created_at          TIMESTAMPTZ NOT NULL DEFAULT now(),
            updated_at          TIMESTAMPTZ NOT NULL DEFAULT now(),
            UNIQUE (server_id, name)
        );

        CREATE INDEX IF NOT EXISTS idx_moh_classes_server ON moh_classes(server_id);

        CREATE TABLE IF NOT EXISTS conference_configs (
            id              SERIAL PRIMARY KEY,
            server_id       TEXT NOT NULL,
            name            TEXT NOT NULL,
            number          TEXT NOT NULL DEFAULT '',
            max_members     INT NOT NULL DEFAULT 0,
            pin             TEXT,
            admin_pin       TEXT,
            record          BOOLEAN NOT NULL DEFAULT false,
            music_on_hold   TEXT NOT NULL DEFAULT 'default',
            created_at      TIMESTAMPTZ NOT NULL DEFAULT now(),
            updated_at      TIMESTAMPTZ NOT NULL DEFAULT now(),
            UNIQUE (server_id, name)
        );

        CREATE INDEX IF NOT EXISTS idx_conference_configs_server ON conference_configs(server_id);

        CREATE TABLE IF NOT EXISTS feature_codes (
            id              SERIAL PRIMARY KEY,
            server_id       TEXT NOT NULL,
            code            TEXT NOT NULL,
            name            TEXT NOT NULL,
            description     TEXT NOT NULL DEFAULT '',
            enabled         BOOLEAN NOT NULL DEFAULT true,
            created_at      TIMESTAMPTZ NOT NULL DEFAULT now(),
            updated_at      TIMESTAMPTZ NOT NULL DEFAULT now(),
            UNIQUE (server_id, code)
        );

        CREATE INDEX IF NOT EXISTS idx_feature_codes_server ON feature_codes(server_id);

        CREATE TABLE IF NOT EXISTS parking_lot_configs (
            id                  SERIAL PRIMARY KEY,
            server_id           TEXT NOT NULL,
            name                TEXT NOT NULL,
            parking_start_slot  INT NOT NULL DEFAULT 701,
            parking_end_slot    INT NOT NULL DEFAULT 720,
            parking_timeout     INT NOT NULL DEFAULT 45,
            music_on_hold       TEXT NOT NULL DEFAULT 'default',
            context             TEXT NOT NULL DEFAULT 'parkedcalls',
            created_at          TIMESTAMPTZ NOT NULL DEFAULT now(),
            updated_at          TIMESTAMPTZ NOT NULL DEFAULT now(),
            UNIQUE (server_id, name)
        );

        CREATE INDEX IF NOT EXISTS idx_parking_lot_configs_server ON parking_lot_configs(server_id);
        """;

    [LoggerMessage(Level = LogLevel.Information, Message = "Recording/MOH schema created successfully")]
    private static partial void SchemaCreated(ILogger logger);

    [LoggerMessage(Level = LogLevel.Error, Message = "Failed to create Recording/MOH schema")]
    private static partial void SchemaFailed(ILogger logger, Exception ex);
}

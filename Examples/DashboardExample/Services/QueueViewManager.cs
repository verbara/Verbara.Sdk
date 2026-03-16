using System.Collections.Concurrent;
using System.Text.RegularExpressions;
using Dapper;
using Npgsql;

namespace DashboardExample.Services;

internal static partial class QueueViewManagerLog
{
    [LoggerMessage(Level = LogLevel.Information, Message = "[QUEUE-VIEW] Created views for server={ServerId}")]
    public static partial void ViewsCreated(ILogger logger, string serverId);

    [LoggerMessage(Level = LogLevel.Error, Message = "[QUEUE-VIEW] Failed to create views for server={ServerId}")]
    public static partial void ViewsFailed(ILogger logger, Exception exception, string serverId);
}

public sealed partial class QueueViewManager : IQueueViewManager
{
    private readonly string _connectionString;
    private readonly ILogger<QueueViewManager> _logger;
    private readonly ConcurrentDictionary<string, bool> _initialized = new(StringComparer.OrdinalIgnoreCase);

    [GeneratedRegex(@"^[a-zA-Z0-9_-]+$")]
    private static partial Regex SafeServerIdRegex();

    public QueueViewManager(IConfiguration config, ILogger<QueueViewManager> logger)
    {
        var servers = config.GetSection("Asterisk:Servers").GetChildren();
        _connectionString = servers
            .Where(s => string.Equals(s["ConfigMode"], "Realtime", StringComparison.OrdinalIgnoreCase))
            .Select(s => s["RealtimeConnectionString"])
            .FirstOrDefault()
            ?? config.GetConnectionString("QueueConfig")
            ?? throw new InvalidOperationException("No Realtime connection string found for QueueViewManager");
        _logger = logger;
    }

    public async Task EnsureViewsExistAsync(string serverId, CancellationToken ct = default)
    {
        if (_initialized.ContainsKey(serverId))
            return;

        if (!SafeServerIdRegex().IsMatch(serverId))
            throw new ArgumentException($"Invalid serverId '{serverId}' — must be alphanumeric, hyphens, or underscores only.", nameof(serverId));

        try
        {
            await using var conn = new NpgsqlConnection(_connectionString);
            await conn.OpenAsync(ct);

            var queueViewSql = $"""
                CREATE OR REPLACE VIEW queue_table_srv_{serverId} AS
                SELECT name, strategy, timeout, retry, maxlen, wrapuptime, servicelevel,
                       musiconhold AS musicclass, weight, joinempty, leavewhenempty, ringinuse,
                       announce_frequency, announce_holdtime, announce_position,
                       periodic_announce, periodic_announce_frequency,
                       queue_youarenext, queue_thereare, queue_callswaiting,
                       NULL::VARCHAR(128) AS announce,
                       NULL::VARCHAR(128) AS context,
                       'yes'::VARCHAR(3)  AS setinterfacevar,
                       'yes'::VARCHAR(3)  AS setqueuevar,
                       'yes'::VARCHAR(3)  AS setqueueentryvar,
                       'yes'::VARCHAR(3)  AS eventwhencalled,
                       'yes'::VARCHAR(3)  AS eventmemberstatus,
                       'yes'::VARCHAR(3)  AS reportholdtime,
                       'no'::VARCHAR(3)   AS autopause,
                       NULL::VARCHAR(10)  AS monitor_format,
                       NULL::VARCHAR(128) AS membermacro,
                       NULL::VARCHAR(128) AS membergosubcontext,
                       NULL::VARCHAR(128) AS queue_holdtime,
                       NULL::VARCHAR(128) AS queue_minutes,
                       NULL::VARCHAR(128) AS queue_seconds,
                       NULL::VARCHAR(128) AS queue_thankyou
                FROM queues_config
                WHERE server_id = '{serverId}' AND enabled = true
                """;

            var membersViewSql = $"""
                CREATE OR REPLACE VIEW queue_members_srv_{serverId} AS
                SELECT qc.name AS queue_name, qm.interface, qm.membername,
                       qm.state_interface, qm.penalty, qm.paused,
                       qm.id::VARCHAR(40) AS uniqueid,
                       NULL::VARCHAR(80) AS reason_paused
                FROM queue_members_config qm
                JOIN queues_config qc ON qc.id = qm.queue_config_id
                WHERE qc.server_id = '{serverId}' AND qc.enabled = true
                """;

            await conn.ExecuteAsync(new CommandDefinition(queueViewSql, cancellationToken: ct));
            await conn.ExecuteAsync(new CommandDefinition(membersViewSql, cancellationToken: ct));

            _initialized.TryAdd(serverId, true);
            QueueViewManagerLog.ViewsCreated(_logger, serverId);
        }
        catch (Exception ex)
        {
            QueueViewManagerLog.ViewsFailed(_logger, ex, serverId);
            throw;
        }
    }
}

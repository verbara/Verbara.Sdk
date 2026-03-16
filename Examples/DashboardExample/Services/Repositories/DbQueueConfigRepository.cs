using Dapper;
using DashboardExample.Models;
using Npgsql;

namespace DashboardExample.Services.Repositories;

internal static partial class DbQueueConfigRepositoryLog
{
    [LoggerMessage(Level = LogLevel.Debug, Message = "[QUEUE_DB] GetQueues: server={ServerId} count={Count}")]
    public static partial void GetQueues(ILogger logger, string serverId, int count);

    [LoggerMessage(Level = LogLevel.Debug, Message = "[QUEUE_DB] Created queue: id={Id} name={Name}")]
    public static partial void CreatedQueue(ILogger logger, int id, string name);

    [LoggerMessage(Level = LogLevel.Debug, Message = "[QUEUE_DB] Added member: id={Id} interface={Interface}")]
    public static partial void AddedMember(ILogger logger, int id, string @interface);

    [LoggerMessage(Level = LogLevel.Error, Message = "[QUEUE_DB] Operation failed: operation={Operation}")]
    public static partial void OperationFailed(ILogger logger, Exception exception, string operation);
}

/// <summary>
/// PostgreSQL/Dapper implementation of <see cref="IQueueConfigRepository"/>.
/// </summary>
public sealed class DbQueueConfigRepository : IQueueConfigRepository
{
    private readonly string _connectionString;
    private readonly ILogger<DbQueueConfigRepository> _logger;

    private const string QueueColumns = """
        id AS Id,
        server_id AS ServerId,
        name AS Name,
        strategy AS Strategy,
        timeout AS Timeout,
        retry AS Retry,
        maxlen AS MaxLen,
        wrapuptime AS WrapUpTime,
        servicelevel AS ServiceLevel,
        musiconhold AS MusicOnHold,
        weight AS Weight,
        joinempty AS JoinEmpty,
        leavewhenempty AS LeaveWhenEmpty,
        ringinuse AS RingInUse,
        announce_frequency AS AnnounceFrequency,
        announce_holdtime AS AnnounceHoldTime,
        announce_position AS AnnouncePosition,
        periodic_announce AS PeriodicAnnounce,
        periodic_announce_frequency AS PeriodicAnnounceFrequency,
        queue_youarenext AS QueueYouAreNext,
        queue_thereare AS QueueThereAre,
        queue_callswaiting AS QueueCallsWaiting,
        enabled AS Enabled,
        notes AS Notes
        """;

    private const string MemberColumns = """
        id AS Id,
        queue_config_id AS QueueConfigId,
        interface AS Interface,
        membername AS MemberName,
        state_interface AS StateInterface,
        penalty AS Penalty,
        paused AS Paused
        """;

    public DbQueueConfigRepository(string connectionString, ILogger<DbQueueConfigRepository> logger)
    {
        _connectionString = connectionString;
        _logger = logger;
    }

    // ──────────────────────────── Queue CRUD ────────────────────────────

    public async Task<List<QueueConfigDto>> GetQueuesAsync(string serverId, CancellationToken ct = default)
    {
        try
        {
            await using var conn = new NpgsqlConnection(_connectionString);
            await conn.OpenAsync(ct);

            var sql = $"""
                SELECT {QueueColumns}
                FROM queues_config
                WHERE server_id = @ServerId
                ORDER BY name
                """;

            var queues = (await conn.QueryAsync<QueueConfigDto>(
                new CommandDefinition(sql, new { ServerId = serverId }, cancellationToken: ct))).AsList();

            if (queues.Count > 0)
            {
                var queueIds = queues.Select(q => q.Id).ToArray();

                var memberSql = $"""
                    SELECT {MemberColumns}
                    FROM queue_members_config
                    WHERE queue_config_id = ANY(@Ids)
                    ORDER BY queue_config_id, penalty, interface
                    """;

                var members = (await conn.QueryAsync<QueueMemberConfigDto>(
                    new CommandDefinition(memberSql, new { Ids = queueIds }, cancellationToken: ct))).AsList();

                var memberLookup = members
                    .GroupBy(m => m.QueueConfigId)
                    .ToDictionary(g => g.Key, g => g.ToList());

                foreach (var queue in queues)
                {
                    if (memberLookup.TryGetValue(queue.Id, out var queueMembers))
                        queue.Members = queueMembers;
                }
            }

            DbQueueConfigRepositoryLog.GetQueues(_logger, serverId, queues.Count);
            return queues;
        }
        catch (Exception ex)
        {
            DbQueueConfigRepositoryLog.OperationFailed(_logger, ex, "GetQueues");
            return [];
        }
    }

    public async Task<QueueConfigDto?> GetQueueAsync(int id, CancellationToken ct = default)
    {
        try
        {
            await using var conn = new NpgsqlConnection(_connectionString);
            await conn.OpenAsync(ct);

            var sql = $"""
                SELECT {QueueColumns}
                FROM queues_config
                WHERE id = @Id
                """;

            var queue = await conn.QueryFirstOrDefaultAsync<QueueConfigDto>(
                new CommandDefinition(sql, new { Id = id }, cancellationToken: ct));

            if (queue is null) return null;

            var memberSql = $"""
                SELECT {MemberColumns}
                FROM queue_members_config
                WHERE queue_config_id = @Id
                ORDER BY penalty, interface
                """;

            var members = await conn.QueryAsync<QueueMemberConfigDto>(
                new CommandDefinition(memberSql, new { Id = id }, cancellationToken: ct));

            queue.Members = members.AsList();
            return queue;
        }
        catch (Exception ex)
        {
            DbQueueConfigRepositoryLog.OperationFailed(_logger, ex, "GetQueue");
            return null;
        }
    }

    public async Task<QueueConfigDto?> GetQueueByNameAsync(string serverId, string name, CancellationToken ct = default)
    {
        try
        {
            await using var conn = new NpgsqlConnection(_connectionString);
            await conn.OpenAsync(ct);

            var sql = $"""
                SELECT {QueueColumns}
                FROM queues_config
                WHERE server_id = @ServerId AND name = @Name
                """;

            var queue = await conn.QueryFirstOrDefaultAsync<QueueConfigDto>(
                new CommandDefinition(sql, new { ServerId = serverId, Name = name }, cancellationToken: ct));

            if (queue is null) return null;

            var memberSql = $"""
                SELECT {MemberColumns}
                FROM queue_members_config
                WHERE queue_config_id = @Id
                ORDER BY penalty, interface
                """;

            var members = await conn.QueryAsync<QueueMemberConfigDto>(
                new CommandDefinition(memberSql, new { Id = queue.Id }, cancellationToken: ct));

            queue.Members = members.AsList();
            return queue;
        }
        catch (Exception ex)
        {
            DbQueueConfigRepositoryLog.OperationFailed(_logger, ex, "GetQueueByName");
            return null;
        }
    }

    public async Task<int> CreateQueueAsync(QueueConfigDto config, CancellationToken ct = default)
    {
        try
        {
            await using var conn = new NpgsqlConnection(_connectionString);
            await conn.OpenAsync(ct);
            await using var tx = await conn.BeginTransactionAsync(ct);

            const string sql = """
                INSERT INTO queues_config
                    (server_id, name, strategy, timeout, retry, maxlen, wrapuptime, servicelevel,
                     musiconhold, weight, joinempty, leavewhenempty, ringinuse,
                     announce_frequency, announce_holdtime, announce_position,
                     periodic_announce, periodic_announce_frequency,
                     queue_youarenext, queue_thereare, queue_callswaiting,
                     enabled, notes)
                VALUES
                    (@ServerId, @Name, @Strategy, @Timeout, @Retry, @MaxLen, @WrapUpTime, @ServiceLevel,
                     @MusicOnHold, @Weight, @JoinEmpty, @LeaveWhenEmpty, @RingInUse,
                     @AnnounceFrequency, @AnnounceHoldTime, @AnnouncePosition,
                     @PeriodicAnnounce, @PeriodicAnnounceFrequency,
                     @QueueYouAreNext, @QueueThereAre, @QueueCallsWaiting,
                     @Enabled, @Notes)
                RETURNING id
                """;

            var id = await conn.ExecuteScalarAsync<int>(
                new CommandDefinition(sql, config, tx, cancellationToken: ct));

            if (config.Members.Count > 0)
            {
                await InsertMembersAsync(conn, tx, id, config.Members, ct);
            }

            await tx.CommitAsync(ct);
            DbQueueConfigRepositoryLog.CreatedQueue(_logger, id, config.Name);
            return id;
        }
        catch (Exception ex)
        {
            DbQueueConfigRepositoryLog.OperationFailed(_logger, ex, "CreateQueue");
            return 0;
        }
    }

    public async Task<bool> UpdateQueueAsync(QueueConfigDto config, CancellationToken ct = default)
    {
        try
        {
            await using var conn = new NpgsqlConnection(_connectionString);
            await conn.OpenAsync(ct);
            await using var tx = await conn.BeginTransactionAsync(ct);

            const string sql = """
                UPDATE queues_config
                SET server_id                    = @ServerId,
                    name                         = @Name,
                    strategy                     = @Strategy,
                    timeout                      = @Timeout,
                    retry                        = @Retry,
                    maxlen                       = @MaxLen,
                    wrapuptime                   = @WrapUpTime,
                    servicelevel                 = @ServiceLevel,
                    musiconhold                  = @MusicOnHold,
                    weight                       = @Weight,
                    joinempty                    = @JoinEmpty,
                    leavewhenempty               = @LeaveWhenEmpty,
                    ringinuse                    = @RingInUse,
                    announce_frequency           = @AnnounceFrequency,
                    announce_holdtime            = @AnnounceHoldTime,
                    announce_position            = @AnnouncePosition,
                    periodic_announce            = @PeriodicAnnounce,
                    periodic_announce_frequency  = @PeriodicAnnounceFrequency,
                    queue_youarenext             = @QueueYouAreNext,
                    queue_thereare               = @QueueThereAre,
                    queue_callswaiting           = @QueueCallsWaiting,
                    enabled                      = @Enabled,
                    notes                        = @Notes
                WHERE id = @Id
                """;

            var rows = await conn.ExecuteAsync(
                new CommandDefinition(sql, config, tx, cancellationToken: ct));

            if (rows == 0)
            {
                await tx.RollbackAsync(ct);
                return false;
            }

            // Replace members: delete all, re-insert
            await conn.ExecuteAsync(new CommandDefinition(
                "DELETE FROM queue_members_config WHERE queue_config_id = @Id",
                new { config.Id }, tx, cancellationToken: ct));

            if (config.Members.Count > 0)
            {
                await InsertMembersAsync(conn, tx, config.Id, config.Members, ct);
            }

            await tx.CommitAsync(ct);
            return true;
        }
        catch (Exception ex)
        {
            DbQueueConfigRepositoryLog.OperationFailed(_logger, ex, "UpdateQueue");
            return false;
        }
    }

    public async Task<bool> DeleteQueueAsync(int id, CancellationToken ct = default)
    {
        try
        {
            await using var conn = new NpgsqlConnection(_connectionString);
            await conn.OpenAsync(ct);

            // queue_members_config cascades via FK
            const string sql = "DELETE FROM queues_config WHERE id = @Id";
            var rows = await conn.ExecuteAsync(
                new CommandDefinition(sql, new { Id = id }, cancellationToken: ct));
            return rows > 0;
        }
        catch (Exception ex)
        {
            DbQueueConfigRepositoryLog.OperationFailed(_logger, ex, "DeleteQueue");
            return false;
        }
    }

    // ──────────────────────────── Member CRUD ────────────────────────────

    public async Task<List<QueueMemberConfigDto>> GetMembersAsync(int queueConfigId, CancellationToken ct = default)
    {
        try
        {
            await using var conn = new NpgsqlConnection(_connectionString);
            await conn.OpenAsync(ct);

            var sql = $"""
                SELECT {MemberColumns}
                FROM queue_members_config
                WHERE queue_config_id = @QueueConfigId
                ORDER BY penalty, interface
                """;

            var members = (await conn.QueryAsync<QueueMemberConfigDto>(
                new CommandDefinition(sql, new { QueueConfigId = queueConfigId }, cancellationToken: ct))).AsList();

            return members;
        }
        catch (Exception ex)
        {
            DbQueueConfigRepositoryLog.OperationFailed(_logger, ex, "GetMembers");
            return [];
        }
    }

    public async Task<int> AddMemberAsync(QueueMemberConfigDto member, CancellationToken ct = default)
    {
        try
        {
            await using var conn = new NpgsqlConnection(_connectionString);
            await conn.OpenAsync(ct);

            const string sql = """
                INSERT INTO queue_members_config
                    (queue_config_id, interface, membername, state_interface, penalty, paused)
                VALUES
                    (@QueueConfigId, @Interface, @MemberName, @StateInterface, @Penalty, @Paused)
                RETURNING id
                """;

            var id = await conn.ExecuteScalarAsync<int>(
                new CommandDefinition(sql, member, cancellationToken: ct));

            DbQueueConfigRepositoryLog.AddedMember(_logger, id, member.Interface);
            return id;
        }
        catch (Exception ex)
        {
            DbQueueConfigRepositoryLog.OperationFailed(_logger, ex, "AddMember");
            return 0;
        }
    }

    public async Task<bool> UpdateMemberAsync(QueueMemberConfigDto member, CancellationToken ct = default)
    {
        try
        {
            await using var conn = new NpgsqlConnection(_connectionString);
            await conn.OpenAsync(ct);

            const string sql = """
                UPDATE queue_members_config
                SET interface       = @Interface,
                    membername      = @MemberName,
                    state_interface = @StateInterface,
                    penalty         = @Penalty,
                    paused          = @Paused
                WHERE id = @Id
                """;

            var rows = await conn.ExecuteAsync(
                new CommandDefinition(sql, member, cancellationToken: ct));
            return rows > 0;
        }
        catch (Exception ex)
        {
            DbQueueConfigRepositoryLog.OperationFailed(_logger, ex, "UpdateMember");
            return false;
        }
    }

    public async Task<bool> RemoveMemberAsync(int memberId, CancellationToken ct = default)
    {
        try
        {
            await using var conn = new NpgsqlConnection(_connectionString);
            await conn.OpenAsync(ct);

            const string sql = "DELETE FROM queue_members_config WHERE id = @Id";
            var rows = await conn.ExecuteAsync(
                new CommandDefinition(sql, new { Id = memberId }, cancellationToken: ct));
            return rows > 0;
        }
        catch (Exception ex)
        {
            DbQueueConfigRepositoryLog.OperationFailed(_logger, ex, "RemoveMember");
            return false;
        }
    }

    // ──────────────────────────── Helpers ────────────────────────────

    private static async Task InsertMembersAsync(
        NpgsqlConnection conn,
        NpgsqlTransaction tx,
        int queueConfigId,
        List<QueueMemberConfigDto> members,
        CancellationToken ct)
    {
        const string sql = """
            INSERT INTO queue_members_config
                (queue_config_id, interface, membername, state_interface, penalty, paused)
            VALUES
                (@QueueConfigId, @Interface, @MemberName, @StateInterface, @Penalty, @Paused)
            """;

        foreach (var member in members)
        {
            await conn.ExecuteAsync(new CommandDefinition(
                sql,
                new
                {
                    QueueConfigId = queueConfigId,
                    member.Interface,
                    member.MemberName,
                    member.StateInterface,
                    member.Penalty,
                    member.Paused,
                },
                tx,
                cancellationToken: ct));
        }
    }
}

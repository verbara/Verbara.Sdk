using Dapper;
using PbxAdmin.Models;
using Npgsql;

namespace PbxAdmin.Services.Repositories;

internal static partial class DbRouteLog
{
    [LoggerMessage(Level = LogLevel.Debug, Message = "[ROUTE_DB] GetInboundRoutes: server={ServerId} count={Count}")]
    public static partial void GetInboundRoutes(ILogger logger, string serverId, int count);

    [LoggerMessage(Level = LogLevel.Debug, Message = "[ROUTE_DB] GetOutboundRoutes: server={ServerId} count={Count}")]
    public static partial void GetOutboundRoutes(ILogger logger, string serverId, int count);

    [LoggerMessage(Level = LogLevel.Debug, Message = "[ROUTE_DB] GetTimeConditions: server={ServerId} count={Count}")]
    public static partial void GetTimeConditions(ILogger logger, string serverId, int count);

    [LoggerMessage(Level = LogLevel.Debug, Message = "[ROUTE_DB] Created inbound route: id={Id} name={Name}")]
    public static partial void CreatedInbound(ILogger logger, int id, string name);

    [LoggerMessage(Level = LogLevel.Debug, Message = "[ROUTE_DB] Created outbound route: id={Id} name={Name}")]
    public static partial void CreatedOutbound(ILogger logger, int id, string name);

    [LoggerMessage(Level = LogLevel.Debug, Message = "[ROUTE_DB] Created time condition: id={Id} name={Name}")]
    public static partial void CreatedTimeCondition(ILogger logger, int id, string name);

    [LoggerMessage(Level = LogLevel.Error, Message = "[ROUTE_DB] Operation failed: operation={Operation}")]
    public static partial void OperationFailed(ILogger logger, Exception exception, string operation);
}

/// <summary>
/// PostgreSQL/Dapper implementation of <see cref="IRouteRepository"/>.
/// </summary>
public sealed class DbRouteRepository : IRouteRepository
{
    private readonly string _connectionString;
    private readonly ILogger<DbRouteRepository> _logger;

    public DbRouteRepository(string connectionString, ILogger<DbRouteRepository> logger)
    {
        _connectionString = connectionString;
        _logger = logger;
    }

    // ──────────────────────────── Inbound Routes ────────────────────────────

    public async Task<List<InboundRouteConfig>> GetInboundRoutesAsync(string serverId, CancellationToken ct = default)
    {
        try
        {
            await using var conn = new NpgsqlConnection(_connectionString);
            await conn.OpenAsync(ct);

            const string sql = """
                SELECT id AS Id,
                       server_id AS ServerId,
                       name AS Name,
                       did_pattern AS DidPattern,
                       destination_type AS DestinationType,
                       destination AS Destination,
                       priority AS Priority,
                       enabled AS Enabled,
                       notes AS Notes
                FROM routes_inbound
                WHERE server_id = @ServerId
                ORDER BY priority, name
                """;

            var result = (await conn.QueryAsync<InboundRouteConfig>(
                new CommandDefinition(sql, new { ServerId = serverId }, cancellationToken: ct))).AsList();

            DbRouteLog.GetInboundRoutes(_logger, serverId, result.Count);
            return result;
        }
        catch (Exception ex)
        {
            DbRouteLog.OperationFailed(_logger, ex, "GetInboundRoutes");
            return [];
        }
    }

    public async Task<InboundRouteConfig?> GetInboundRouteAsync(int id, CancellationToken ct = default)
    {
        try
        {
            await using var conn = new NpgsqlConnection(_connectionString);
            await conn.OpenAsync(ct);

            const string sql = """
                SELECT id AS Id,
                       server_id AS ServerId,
                       name AS Name,
                       did_pattern AS DidPattern,
                       destination_type AS DestinationType,
                       destination AS Destination,
                       priority AS Priority,
                       enabled AS Enabled,
                       notes AS Notes
                FROM routes_inbound
                WHERE id = @Id
                """;

            return await conn.QueryFirstOrDefaultAsync<InboundRouteConfig>(
                new CommandDefinition(sql, new { Id = id }, cancellationToken: ct));
        }
        catch (Exception ex)
        {
            DbRouteLog.OperationFailed(_logger, ex, "GetInboundRoute");
            return null;
        }
    }

    public async Task<int> CreateInboundRouteAsync(InboundRouteConfig config, CancellationToken ct = default)
    {
        try
        {
            await using var conn = new NpgsqlConnection(_connectionString);
            await conn.OpenAsync(ct);

            const string sql = """
                INSERT INTO routes_inbound
                    (server_id, name, did_pattern, destination_type, destination, priority, enabled, notes)
                VALUES
                    (@ServerId, @Name, @DidPattern, @DestinationType, @Destination, @Priority, @Enabled, @Notes)
                RETURNING id
                """;

            var id = await conn.ExecuteScalarAsync<int>(
                new CommandDefinition(sql, config, cancellationToken: ct));

            DbRouteLog.CreatedInbound(_logger, id, config.Name);
            return id;
        }
        catch (Exception ex)
        {
            DbRouteLog.OperationFailed(_logger, ex, "CreateInboundRoute");
            return 0;
        }
    }

    public async Task<bool> UpdateInboundRouteAsync(InboundRouteConfig config, CancellationToken ct = default)
    {
        try
        {
            await using var conn = new NpgsqlConnection(_connectionString);
            await conn.OpenAsync(ct);

            const string sql = """
                UPDATE routes_inbound
                SET server_id        = @ServerId,
                    name             = @Name,
                    did_pattern      = @DidPattern,
                    destination_type = @DestinationType,
                    destination      = @Destination,
                    priority         = @Priority,
                    enabled          = @Enabled,
                    notes            = @Notes
                WHERE id = @Id
                """;

            var rows = await conn.ExecuteAsync(
                new CommandDefinition(sql, config, cancellationToken: ct));
            return rows > 0;
        }
        catch (Exception ex)
        {
            DbRouteLog.OperationFailed(_logger, ex, "UpdateInboundRoute");
            return false;
        }
    }

    public async Task<bool> DeleteInboundRouteAsync(int id, CancellationToken ct = default)
    {
        try
        {
            await using var conn = new NpgsqlConnection(_connectionString);
            await conn.OpenAsync(ct);

            const string sql = "DELETE FROM routes_inbound WHERE id = @Id";
            var rows = await conn.ExecuteAsync(
                new CommandDefinition(sql, new { Id = id }, cancellationToken: ct));
            return rows > 0;
        }
        catch (Exception ex)
        {
            DbRouteLog.OperationFailed(_logger, ex, "DeleteInboundRoute");
            return false;
        }
    }

    // ──────────────────────────── Outbound Routes ────────────────────────────

    public async Task<List<OutboundRouteConfig>> GetOutboundRoutesAsync(string serverId, CancellationToken ct = default)
    {
        try
        {
            await using var conn = new NpgsqlConnection(_connectionString);
            await conn.OpenAsync(ct);

            const string routeSql = """
                SELECT id AS Id,
                       server_id AS ServerId,
                       name AS Name,
                       dial_pattern AS DialPattern,
                       prepend AS Prepend,
                       prefix AS Prefix,
                       priority AS Priority,
                       enabled AS Enabled,
                       notes AS Notes
                FROM routes_outbound
                WHERE server_id = @ServerId
                ORDER BY priority, name
                """;

            var routes = (await conn.QueryAsync<OutboundRouteConfig>(
                new CommandDefinition(routeSql, new { ServerId = serverId }, cancellationToken: ct))).AsList();

            if (routes.Count > 0)
            {
                var routeIds = routes.Select(r => r.Id).ToArray();

                const string trunkSql = """
                    SELECT outbound_route_id AS RouteId,
                           trunk_name AS TrunkName,
                           trunk_technology AS TrunkTechnology,
                           sequence AS Sequence
                    FROM route_trunks
                    WHERE outbound_route_id = ANY(@Ids)
                    ORDER BY outbound_route_id, sequence
                    """;

                var trunks = await conn.QueryAsync<(int RouteId, string TrunkName, string TrunkTechnology, int Sequence)>(
                    new CommandDefinition(trunkSql, new { Ids = routeIds }, cancellationToken: ct));

                var trunkLookup = trunks
                    .GroupBy(t => t.RouteId)
                    .ToDictionary(g => g.Key, g => g.Select(t => new RouteTrunk
                    {
                        TrunkName = t.TrunkName,
                        TrunkTechnology = t.TrunkTechnology,
                        Sequence = t.Sequence,
                    }).ToList());

                foreach (var route in routes)
                {
                    if (trunkLookup.TryGetValue(route.Id, out var routeTrunks))
                        route.Trunks = routeTrunks;
                }
            }

            DbRouteLog.GetOutboundRoutes(_logger, serverId, routes.Count);
            return routes;
        }
        catch (Exception ex)
        {
            DbRouteLog.OperationFailed(_logger, ex, "GetOutboundRoutes");
            return [];
        }
    }

    public async Task<OutboundRouteConfig?> GetOutboundRouteAsync(int id, CancellationToken ct = default)
    {
        try
        {
            await using var conn = new NpgsqlConnection(_connectionString);
            await conn.OpenAsync(ct);

            const string routeSql = """
                SELECT id AS Id,
                       server_id AS ServerId,
                       name AS Name,
                       dial_pattern AS DialPattern,
                       prepend AS Prepend,
                       prefix AS Prefix,
                       priority AS Priority,
                       enabled AS Enabled,
                       notes AS Notes
                FROM routes_outbound
                WHERE id = @Id
                """;

            var route = await conn.QueryFirstOrDefaultAsync<OutboundRouteConfig>(
                new CommandDefinition(routeSql, new { Id = id }, cancellationToken: ct));

            if (route is null) return null;

            const string trunkSql = """
                SELECT trunk_name AS TrunkName,
                       trunk_technology AS TrunkTechnology,
                       sequence AS Sequence
                FROM route_trunks
                WHERE outbound_route_id = @Id
                ORDER BY sequence
                """;

            var trunks = await conn.QueryAsync<RouteTrunk>(
                new CommandDefinition(trunkSql, new { Id = id }, cancellationToken: ct));

            route.Trunks = trunks.AsList();
            return route;
        }
        catch (Exception ex)
        {
            DbRouteLog.OperationFailed(_logger, ex, "GetOutboundRoute");
            return null;
        }
    }

    public async Task<int> CreateOutboundRouteAsync(OutboundRouteConfig config, CancellationToken ct = default)
    {
        try
        {
            await using var conn = new NpgsqlConnection(_connectionString);
            await conn.OpenAsync(ct);
            await using var tx = await conn.BeginTransactionAsync(ct);

            const string routeSql = """
                INSERT INTO routes_outbound
                    (server_id, name, dial_pattern, prepend, prefix, priority, enabled, notes)
                VALUES
                    (@ServerId, @Name, @DialPattern, @Prepend, @Prefix, @Priority, @Enabled, @Notes)
                RETURNING id
                """;

            var id = await conn.ExecuteScalarAsync<int>(
                new CommandDefinition(routeSql, config, tx, cancellationToken: ct));

            if (config.Trunks.Count > 0)
            {
                const string trunkSql = """
                    INSERT INTO route_trunks (outbound_route_id, trunk_name, trunk_technology, sequence)
                    VALUES (@RouteId, @TrunkName, @TrunkTechnology, @Sequence)
                    """;

                foreach (var trunk in config.Trunks)
                {
                    await conn.ExecuteAsync(new CommandDefinition(
                        trunkSql,
                        new { RouteId = id, trunk.TrunkName, trunk.TrunkTechnology, trunk.Sequence },
                        tx,
                        cancellationToken: ct));
                }
            }

            await tx.CommitAsync(ct);
            DbRouteLog.CreatedOutbound(_logger, id, config.Name);
            return id;
        }
        catch (Exception ex)
        {
            DbRouteLog.OperationFailed(_logger, ex, "CreateOutboundRoute");
            return 0;
        }
    }

    public async Task<bool> UpdateOutboundRouteAsync(OutboundRouteConfig config, CancellationToken ct = default)
    {
        try
        {
            await using var conn = new NpgsqlConnection(_connectionString);
            await conn.OpenAsync(ct);
            await using var tx = await conn.BeginTransactionAsync(ct);

            const string routeSql = """
                UPDATE routes_outbound
                SET server_id    = @ServerId,
                    name         = @Name,
                    dial_pattern = @DialPattern,
                    prepend      = @Prepend,
                    prefix       = @Prefix,
                    priority     = @Priority,
                    enabled      = @Enabled,
                    notes        = @Notes
                WHERE id = @Id
                """;

            var rows = await conn.ExecuteAsync(
                new CommandDefinition(routeSql, config, tx, cancellationToken: ct));

            if (rows == 0)
            {
                await tx.RollbackAsync(ct);
                return false;
            }

            // Replace trunks
            await conn.ExecuteAsync(new CommandDefinition(
                "DELETE FROM route_trunks WHERE outbound_route_id = @Id",
                new { config.Id }, tx, cancellationToken: ct));

            const string trunkSql = """
                INSERT INTO route_trunks (outbound_route_id, trunk_name, trunk_technology, sequence)
                VALUES (@RouteId, @TrunkName, @TrunkTechnology, @Sequence)
                """;

            foreach (var trunk in config.Trunks)
            {
                await conn.ExecuteAsync(new CommandDefinition(
                    trunkSql,
                    new { RouteId = config.Id, trunk.TrunkName, trunk.TrunkTechnology, trunk.Sequence },
                    tx,
                    cancellationToken: ct));
            }

            await tx.CommitAsync(ct);
            return true;
        }
        catch (Exception ex)
        {
            DbRouteLog.OperationFailed(_logger, ex, "UpdateOutboundRoute");
            return false;
        }
    }

    public async Task<bool> DeleteOutboundRouteAsync(int id, CancellationToken ct = default)
    {
        try
        {
            await using var conn = new NpgsqlConnection(_connectionString);
            await conn.OpenAsync(ct);

            // route_trunks cascades via FK
            const string sql = "DELETE FROM routes_outbound WHERE id = @Id";
            var rows = await conn.ExecuteAsync(
                new CommandDefinition(sql, new { Id = id }, cancellationToken: ct));
            return rows > 0;
        }
        catch (Exception ex)
        {
            DbRouteLog.OperationFailed(_logger, ex, "DeleteOutboundRoute");
            return false;
        }
    }

    // ──────────────────────────── Time Conditions ────────────────────────────

    public async Task<List<TimeConditionConfig>> GetTimeConditionsAsync(string serverId, CancellationToken ct = default)
    {
        try
        {
            await using var conn = new NpgsqlConnection(_connectionString);
            await conn.OpenAsync(ct);

            const string tcSql = """
                SELECT id AS Id,
                       server_id AS ServerId,
                       name AS Name,
                       match_dest_type AS MatchDestType,
                       match_dest AS MatchDest,
                       nomatch_dest_type AS NoMatchDestType,
                       nomatch_dest AS NoMatchDest,
                       enabled AS Enabled
                FROM time_conditions
                WHERE server_id = @ServerId
                ORDER BY name
                """;

            var tcs = (await conn.QueryAsync<TimeConditionConfig>(
                new CommandDefinition(tcSql, new { ServerId = serverId }, cancellationToken: ct))).AsList();

            if (tcs.Count > 0)
            {
                var tcIds = tcs.Select(t => t.Id).ToArray();

                const string rangeSql = """
                    SELECT time_condition_id AS TimeConditionId,
                           day_of_week AS DayOfWeek,
                           start_time AS StartTime,
                           end_time AS EndTime
                    FROM time_ranges
                    WHERE time_condition_id = ANY(@Ids)
                    ORDER BY time_condition_id, day_of_week, start_time
                    """;

                var rangeRows = await conn.QueryAsync<(int TimeConditionId, int DayOfWeek, TimeOnly StartTime, TimeOnly EndTime)>(
                    new CommandDefinition(rangeSql, new { Ids = tcIds }, cancellationToken: ct));

                const string holidaySql = """
                    SELECT time_condition_id AS TimeConditionId,
                           name AS Name,
                           month AS Month,
                           day AS Day,
                           recurring AS Recurring
                    FROM holidays
                    WHERE time_condition_id = ANY(@Ids)
                    ORDER BY time_condition_id, month, day
                    """;

                var holidayRows = await conn.QueryAsync<(int TimeConditionId, string Name, int Month, int Day, bool Recurring)>(
                    new CommandDefinition(holidaySql, new { Ids = tcIds }, cancellationToken: ct));

                var rangeLookup = rangeRows
                    .GroupBy(r => r.TimeConditionId)
                    .ToDictionary(g => g.Key, g => g.Select(r => new TimeRangeEntry
                    {
                        DayOfWeek = (DayOfWeek)r.DayOfWeek,
                        StartTime = r.StartTime,
                        EndTime = r.EndTime,
                    }).ToList());

                var holidayLookup = holidayRows
                    .GroupBy(h => h.TimeConditionId)
                    .ToDictionary(g => g.Key, g => g.Select(h => new HolidayEntry
                    {
                        Name = h.Name,
                        Month = h.Month,
                        Day = h.Day,
                        Recurring = h.Recurring,
                    }).ToList());

                foreach (var tc in tcs)
                {
                    if (rangeLookup.TryGetValue(tc.Id, out var ranges))
                        tc.Ranges = ranges;
                    if (holidayLookup.TryGetValue(tc.Id, out var holidays))
                        tc.Holidays = holidays;
                }
            }

            DbRouteLog.GetTimeConditions(_logger, serverId, tcs.Count);
            return tcs;
        }
        catch (Exception ex)
        {
            DbRouteLog.OperationFailed(_logger, ex, "GetTimeConditions");
            return [];
        }
    }

    public async Task<TimeConditionConfig?> GetTimeConditionAsync(int id, CancellationToken ct = default)
    {
        try
        {
            await using var conn = new NpgsqlConnection(_connectionString);
            await conn.OpenAsync(ct);

            const string tcSql = """
                SELECT id AS Id,
                       server_id AS ServerId,
                       name AS Name,
                       match_dest_type AS MatchDestType,
                       match_dest AS MatchDest,
                       nomatch_dest_type AS NoMatchDestType,
                       nomatch_dest AS NoMatchDest,
                       enabled AS Enabled
                FROM time_conditions
                WHERE id = @Id
                """;

            var tc = await conn.QueryFirstOrDefaultAsync<TimeConditionConfig>(
                new CommandDefinition(tcSql, new { Id = id }, cancellationToken: ct));

            if (tc is null) return null;

            const string rangeSql = """
                SELECT day_of_week AS DayOfWeek,
                       start_time AS StartTime,
                       end_time AS EndTime
                FROM time_ranges
                WHERE time_condition_id = @Id
                ORDER BY day_of_week, start_time
                """;

            var ranges = await conn.QueryAsync<(int DayOfWeek, TimeOnly StartTime, TimeOnly EndTime)>(
                new CommandDefinition(rangeSql, new { Id = id }, cancellationToken: ct));

            tc.Ranges = ranges.Select(r => new TimeRangeEntry
            {
                DayOfWeek = (DayOfWeek)r.DayOfWeek,
                StartTime = r.StartTime,
                EndTime = r.EndTime,
            }).ToList();

            const string holidaySql = """
                SELECT name AS Name,
                       month AS Month,
                       day AS Day,
                       recurring AS Recurring
                FROM holidays
                WHERE time_condition_id = @Id
                ORDER BY month, day
                """;

            var holidays = await conn.QueryAsync<HolidayEntry>(
                new CommandDefinition(holidaySql, new { Id = id }, cancellationToken: ct));

            tc.Holidays = holidays.AsList();
            return tc;
        }
        catch (Exception ex)
        {
            DbRouteLog.OperationFailed(_logger, ex, "GetTimeCondition");
            return null;
        }
    }

    public async Task<int> CreateTimeConditionAsync(TimeConditionConfig config, CancellationToken ct = default)
    {
        try
        {
            await using var conn = new NpgsqlConnection(_connectionString);
            await conn.OpenAsync(ct);
            await using var tx = await conn.BeginTransactionAsync(ct);

            const string tcSql = """
                INSERT INTO time_conditions
                    (server_id, name, match_dest_type, match_dest, nomatch_dest_type, nomatch_dest, enabled)
                VALUES
                    (@ServerId, @Name, @MatchDestType, @MatchDest, @NoMatchDestType, @NoMatchDest, @Enabled)
                RETURNING id
                """;

            var id = await conn.ExecuteScalarAsync<int>(
                new CommandDefinition(tcSql, config, tx, cancellationToken: ct));

            await InsertRangesAndHolidaysAsync(conn, tx, id, config, ct);

            await tx.CommitAsync(ct);
            DbRouteLog.CreatedTimeCondition(_logger, id, config.Name);
            return id;
        }
        catch (Exception ex)
        {
            DbRouteLog.OperationFailed(_logger, ex, "CreateTimeCondition");
            return 0;
        }
    }

    public async Task<bool> UpdateTimeConditionAsync(TimeConditionConfig config, CancellationToken ct = default)
    {
        try
        {
            await using var conn = new NpgsqlConnection(_connectionString);
            await conn.OpenAsync(ct);
            await using var tx = await conn.BeginTransactionAsync(ct);

            const string tcSql = """
                UPDATE time_conditions
                SET server_id          = @ServerId,
                    name               = @Name,
                    match_dest_type    = @MatchDestType,
                    match_dest         = @MatchDest,
                    nomatch_dest_type  = @NoMatchDestType,
                    nomatch_dest       = @NoMatchDest,
                    enabled            = @Enabled
                WHERE id = @Id
                """;

            var rows = await conn.ExecuteAsync(
                new CommandDefinition(tcSql, config, tx, cancellationToken: ct));

            if (rows == 0)
            {
                await tx.RollbackAsync(ct);
                return false;
            }

            // Replace child rows
            await conn.ExecuteAsync(new CommandDefinition(
                "DELETE FROM time_ranges WHERE time_condition_id = @Id",
                new { config.Id }, tx, cancellationToken: ct));

            await conn.ExecuteAsync(new CommandDefinition(
                "DELETE FROM holidays WHERE time_condition_id = @Id",
                new { config.Id }, tx, cancellationToken: ct));

            await InsertRangesAndHolidaysAsync(conn, tx, config.Id, config, ct);

            await tx.CommitAsync(ct);
            return true;
        }
        catch (Exception ex)
        {
            DbRouteLog.OperationFailed(_logger, ex, "UpdateTimeCondition");
            return false;
        }
    }

    public async Task<bool> DeleteTimeConditionAsync(int id, CancellationToken ct = default)
    {
        try
        {
            await using var conn = new NpgsqlConnection(_connectionString);
            await conn.OpenAsync(ct);

            // time_ranges and holidays cascade via FK
            const string sql = "DELETE FROM time_conditions WHERE id = @Id";
            var rows = await conn.ExecuteAsync(
                new CommandDefinition(sql, new { Id = id }, cancellationToken: ct));
            return rows > 0;
        }
        catch (Exception ex)
        {
            DbRouteLog.OperationFailed(_logger, ex, "DeleteTimeCondition");
            return false;
        }
    }

    public async Task<bool> IsTimeConditionReferencedAsync(int timeConditionId, CancellationToken ct = default)
    {
        try
        {
            await using var conn = new NpgsqlConnection(_connectionString);
            await conn.OpenAsync(ct);

            // Get the TC name first
            var name = await conn.ExecuteScalarAsync<string?>(new CommandDefinition(
                "SELECT name FROM time_conditions WHERE id = @Id",
                new { Id = timeConditionId },
                cancellationToken: ct));

            if (name is null) return false;

            var count = await conn.ExecuteScalarAsync<int>(new CommandDefinition(
                "SELECT COUNT(*) FROM routes_inbound WHERE destination_type = 'time_condition' AND destination = @Name",
                new { Name = name },
                cancellationToken: ct));

            return count > 0;
        }
        catch (Exception ex)
        {
            DbRouteLog.OperationFailed(_logger, ex, "IsTimeConditionReferenced");
            return false;
        }
    }

    // ──────────────────────────── Helpers ────────────────────────────

    private static async Task InsertRangesAndHolidaysAsync(
        NpgsqlConnection conn,
        NpgsqlTransaction tx,
        int tcId,
        TimeConditionConfig config,
        CancellationToken ct)
    {
        if (config.Ranges.Count > 0)
        {
            const string rangeSql = """
                INSERT INTO time_ranges (time_condition_id, day_of_week, start_time, end_time)
                VALUES (@TimeConditionId, @DayOfWeek, @StartTime, @EndTime)
                """;

            foreach (var range in config.Ranges)
            {
                await conn.ExecuteAsync(new CommandDefinition(
                    rangeSql,
                    new { TimeConditionId = tcId, DayOfWeek = (int)range.DayOfWeek, range.StartTime, range.EndTime },
                    tx,
                    cancellationToken: ct));
            }
        }

        if (config.Holidays.Count > 0)
        {
            const string holidaySql = """
                INSERT INTO holidays (time_condition_id, name, month, day, recurring)
                VALUES (@TimeConditionId, @Name, @Month, @Day, @Recurring)
                """;

            foreach (var holiday in config.Holidays)
            {
                await conn.ExecuteAsync(new CommandDefinition(
                    holidaySql,
                    new { TimeConditionId = tcId, holiday.Name, holiday.Month, holiday.Day, holiday.Recurring },
                    tx,
                    cancellationToken: ct));
            }
        }
    }
}

using Asterisk.Sdk;
using Asterisk.Sdk.Ami.Actions;
using Asterisk.Sdk.Ami.Responses;
using DashboardExample.Models;
using DashboardExample.Services.Dialplan;
using DashboardExample.Services.Repositories;

namespace DashboardExample.Services;

internal static partial class TimeConditionServiceLog
{
    [LoggerMessage(Level = LogLevel.Information, Message = "[TC] Created: server={ServerId} name={Name}")]
    public static partial void Created(ILogger logger, string serverId, string name);

    [LoggerMessage(Level = LogLevel.Information, Message = "[TC] Updated: server={ServerId} id={Id}")]
    public static partial void Updated(ILogger logger, string serverId, int id);

    [LoggerMessage(Level = LogLevel.Information, Message = "[TC] Deleted: server={ServerId} id={Id}")]
    public static partial void Deleted(ILogger logger, string serverId, int id);

    [LoggerMessage(Level = LogLevel.Information, Message = "[TC] Override set: server={ServerId} name={Name} forceOpen={ForceOpen}")]
    public static partial void OverrideSet(ILogger logger, string serverId, string name, bool? forceOpen);

    [LoggerMessage(Level = LogLevel.Warning, Message = "[TC] Override operation failed: server={ServerId} name={Name}")]
    public static partial void OverrideFailed(ILogger logger, Exception exception, string serverId, string name);

    [LoggerMessage(Level = LogLevel.Warning, Message = "[TC] Get current state failed: server={ServerId} name={Name}")]
    public static partial void GetStateFailed(ILogger logger, Exception exception, string serverId, string name);
}

/// <summary>
/// CRUD service for time conditions with AstDB override support.
/// </summary>
public sealed class TimeConditionService
{
    private readonly IRouteRepositoryResolver _repoResolver;
    private readonly IDialplanProviderResolver _dialplanResolver;
    private readonly AsteriskMonitorService _monitor;
    private readonly ILogger<TimeConditionService> _logger;

    public TimeConditionService(
        IRouteRepositoryResolver repoResolver,
        IDialplanProviderResolver dialplanResolver,
        AsteriskMonitorService monitor,
        ILogger<TimeConditionService> logger)
    {
        _repoResolver = repoResolver;
        _dialplanResolver = dialplanResolver;
        _monitor = monitor;
        _logger = logger;
    }

    // -----------------------------------------------------------------------
    // Static evaluation
    // -----------------------------------------------------------------------

    /// <summary>
    /// Evaluates whether the current time falls within any open range.
    /// Holidays are checked first (any match = Closed).
    /// </summary>
    public static TimeConditionState EvaluateState(
        List<TimeRangeEntry> ranges,
        List<HolidayEntry> holidays,
        DateTime now)
    {
        // Check holidays first
        foreach (var h in holidays)
        {
            if (h.Month == now.Month && h.Day == now.Day)
                return TimeConditionState.Closed;
        }

        // Check time ranges
        var currentTime = TimeOnly.FromDateTime(now);
        foreach (var r in ranges)
        {
            if (r.DayOfWeek == now.DayOfWeek && currentTime >= r.StartTime && currentTime < r.EndTime)
                return TimeConditionState.Open;
        }

        return TimeConditionState.Closed;
    }

    // -----------------------------------------------------------------------
    // CRUD
    // -----------------------------------------------------------------------

    /// <summary>Gets all time conditions for a server with destination labels and current state.</summary>
    public async Task<List<TimeConditionViewModel>> GetTimeConditionsAsync(string serverId, CancellationToken ct = default)
    {
        var repo = _repoResolver.GetRepository(serverId);
        var conditions = await repo.GetTimeConditionsAsync(serverId, ct);
        var viewModels = new List<TimeConditionViewModel>(conditions.Count);

        foreach (var tc in conditions)
        {
            viewModels.Add(new TimeConditionViewModel
            {
                Id = tc.Id,
                Name = tc.Name,
                MatchDestType = tc.MatchDestType,
                MatchDest = tc.MatchDest,
                MatchDestLabel = ResolveDestinationLabel(tc.MatchDestType, tc.MatchDest),
                NoMatchDestType = tc.NoMatchDestType,
                NoMatchDest = tc.NoMatchDest,
                NoMatchDestLabel = ResolveDestinationLabel(tc.NoMatchDestType, tc.NoMatchDest),
                Enabled = tc.Enabled,
                CurrentState = EvaluateState(tc.Ranges, tc.Holidays, DateTime.Now),
                RangeCount = tc.Ranges.Count,
                HolidayCount = tc.Holidays.Count,
            });
        }

        return viewModels;
    }

    /// <summary>Gets a single time condition by ID, searching all servers.</summary>
    public async Task<TimeConditionConfig?> GetTimeConditionAsync(int id, CancellationToken ct = default)
    {
        foreach (var kvp in _monitor.Servers)
        {
            var repo = _repoResolver.GetRepository(kvp.Key);
            var tc = await repo.GetTimeConditionAsync(id, ct);
            if (tc is not null) return tc;
        }
        return null;
    }

    /// <summary>Creates a time condition.</summary>
    public async Task<(bool Success, string? Error)> CreateTimeConditionAsync(TimeConditionConfig config, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(config.Name))
            return (false, "Name is required");

        if (string.IsNullOrWhiteSpace(config.MatchDestType) || string.IsNullOrWhiteSpace(config.MatchDest))
            return (false, "Match destination is required");

        if (string.IsNullOrWhiteSpace(config.NoMatchDestType) || string.IsNullOrWhiteSpace(config.NoMatchDest))
            return (false, "No-match destination is required");

        var repo = _repoResolver.GetRepository(config.ServerId);
        await repo.CreateTimeConditionAsync(config, ct);
        await RegenerateDialplanAsync(config.ServerId, ct);

        TimeConditionServiceLog.Created(_logger, config.ServerId, config.Name);
        return (true, null);
    }

    /// <summary>Updates a time condition.</summary>
    public async Task<(bool Success, string? Error)> UpdateTimeConditionAsync(TimeConditionConfig config, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(config.Name))
            return (false, "Name is required");

        var repo = _repoResolver.GetRepository(config.ServerId);
        var success = await repo.UpdateTimeConditionAsync(config, ct);
        if (!success) return (false, "Time condition not found");

        await RegenerateDialplanAsync(config.ServerId, ct);

        TimeConditionServiceLog.Updated(_logger, config.ServerId, config.Id);
        return (true, null);
    }

    /// <summary>Deletes a time condition if not referenced by routes.</summary>
    public async Task<(bool Success, string? Error)> DeleteTimeConditionAsync(int id, string serverId, CancellationToken ct = default)
    {
        var repo = _repoResolver.GetRepository(serverId);

        if (await repo.IsTimeConditionReferencedAsync(id, ct))
            return (false, "Time condition is referenced by one or more routes");

        var success = await repo.DeleteTimeConditionAsync(id, ct);
        if (!success) return (false, "Time condition not found");

        await RegenerateDialplanAsync(serverId, ct);

        TimeConditionServiceLog.Deleted(_logger, serverId, id);
        return (true, null);
    }

    // -----------------------------------------------------------------------
    // AstDB override management
    // -----------------------------------------------------------------------

    /// <summary>
    /// Sets or clears an AstDB override for a time condition.
    /// forceOpen == null → remove override,
    /// forceOpen == true → force OPEN,
    /// forceOpen == false → force CLOSED.
    /// </summary>
    public async Task SetOverrideAsync(string serverId, string name, bool? forceOpen, CancellationToken ct = default)
    {
        var entry = _monitor.GetServer(serverId);
        if (entry is null) return;

        try
        {
            if (forceOpen is null)
            {
                await entry.Connection.SendActionAsync(
                    new DbDelAction { Family = "TC_OVERRIDE", Key = name }, ct);
            }
            else
            {
                await entry.Connection.SendActionAsync(
                    new DbPutAction { Family = "TC_OVERRIDE", Key = name, Val = forceOpen.Value ? "OPEN" : "CLOSED" }, ct);
            }

            TimeConditionServiceLog.OverrideSet(_logger, serverId, name, forceOpen);
        }
        catch (Exception ex)
        {
            TimeConditionServiceLog.OverrideFailed(_logger, ex, serverId, name);
        }
    }

    /// <summary>Gets the current state of a time condition, checking AstDB override first.</summary>
    public async Task<TimeConditionState> GetCurrentStateAsync(string serverId, string name, CancellationToken ct = default)
    {
        try
        {
            // Check override first
            var overrides = await GetOverridesBatchAsync(serverId, ct);
            if (overrides.TryGetValue(name, out var overrideValue))
            {
                return string.Equals(overrideValue, "OPEN", StringComparison.OrdinalIgnoreCase)
                    ? TimeConditionState.OverrideOpen
                    : TimeConditionState.OverrideClosed;
            }

            // Fall back to time-based evaluation
            var repo = _repoResolver.GetRepository(serverId);
            var conditions = await repo.GetTimeConditionsAsync(serverId, ct);
            var tc = conditions.FirstOrDefault(c => string.Equals(c.Name, name, StringComparison.OrdinalIgnoreCase));
            if (tc is null) return TimeConditionState.Closed;

            return EvaluateState(tc.Ranges, tc.Holidays, DateTime.Now);
        }
        catch (Exception ex)
        {
            TimeConditionServiceLog.GetStateFailed(_logger, ex, serverId, name);
            return TimeConditionState.Closed;
        }
    }

    /// <summary>Gets all TC_OVERRIDE entries from AstDB in a single command.</summary>
    public async Task<Dictionary<string, string>> GetOverridesBatchAsync(string serverId, CancellationToken ct = default)
    {
        var entry = _monitor.GetServer(serverId);
        if (entry is null) return new Dictionary<string, string>();

        try
        {
            var response = await entry.Connection.SendActionAsync<CommandResponse>(
                new CommandAction { Command = "database show TC_OVERRIDE" }, ct);

            return ParseDatabaseShowOutput(response.Output);
        }
        catch
        {
            return new Dictionary<string, string>();
        }
    }

    /// <summary>Parses Asterisk "database show" output in /Family/Key : Value format.</summary>
    internal static Dictionary<string, string> ParseDatabaseShowOutput(string? output)
    {
        var result = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        if (string.IsNullOrEmpty(output)) return result;

        foreach (var line in output.Split('\n'))
        {
            var trimmed = line.Trim();
            // Format: /TC_OVERRIDE/name                          : value
            if (!trimmed.StartsWith('/')) continue;

            var colonIdx = trimmed.IndexOf(':');
            if (colonIdx < 0) continue;

            var keyPath = trimmed[..colonIdx].Trim();
            var value = trimmed[(colonIdx + 1)..].Trim();

            // Extract the name from /TC_OVERRIDE/name
            var lastSlash = keyPath.LastIndexOf('/');
            if (lastSlash < 0) continue;

            var name = keyPath[(lastSlash + 1)..].Trim();
            if (!string.IsNullOrEmpty(name))
            {
                result[name] = value;
            }
        }

        return result;
    }

    // -----------------------------------------------------------------------
    // Private helpers
    // -----------------------------------------------------------------------

    private static string ResolveDestinationLabel(string type, string target) => type switch
    {
        "extension" => $"Ext {target}",
        "queue" => $"Queue: {target}",
        "time_condition" => $"TC: {target}",
        _ => target
    };

    private async Task RegenerateDialplanAsync(string serverId, CancellationToken ct)
    {
        var repo = _repoResolver.GetRepository(serverId);
        var inbound = await repo.GetInboundRoutesAsync(serverId, ct);
        var outbound = await repo.GetOutboundRoutesAsync(serverId, ct);
        var timeConditions = await repo.GetTimeConditionsAsync(serverId, ct);

        var data = new DialplanData(inbound, outbound, timeConditions);
        var dialplanProvider = _dialplanResolver.GetProvider(serverId);

        await dialplanProvider.GenerateDialplanAsync(serverId, data, ct);
        await dialplanProvider.ReloadAsync(serverId, ct);
    }
}

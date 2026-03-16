using System.Text.RegularExpressions;
using DashboardExample.Models;
using DashboardExample.Services.Dialplan;
using DashboardExample.Services.Repositories;

namespace DashboardExample.Services;

internal static partial class RouteServiceLog
{
    [LoggerMessage(Level = LogLevel.Information, Message = "[ROUTE] Created inbound: server={ServerId} did={DidPattern}")]
    public static partial void InboundCreated(ILogger logger, string serverId, string didPattern);

    [LoggerMessage(Level = LogLevel.Information, Message = "[ROUTE] Updated inbound: server={ServerId} id={Id}")]
    public static partial void InboundUpdated(ILogger logger, string serverId, int id);

    [LoggerMessage(Level = LogLevel.Information, Message = "[ROUTE] Deleted inbound: server={ServerId} id={Id}")]
    public static partial void InboundDeleted(ILogger logger, string serverId, int id);

    [LoggerMessage(Level = LogLevel.Information, Message = "[ROUTE] Created outbound: server={ServerId} pattern={DialPattern}")]
    public static partial void OutboundCreated(ILogger logger, string serverId, string dialPattern);

    [LoggerMessage(Level = LogLevel.Information, Message = "[ROUTE] Updated outbound: server={ServerId} id={Id}")]
    public static partial void OutboundUpdated(ILogger logger, string serverId, int id);

    [LoggerMessage(Level = LogLevel.Information, Message = "[ROUTE] Deleted outbound: server={ServerId} id={Id}")]
    public static partial void OutboundDeleted(ILogger logger, string serverId, int id);

    [LoggerMessage(Level = LogLevel.Warning, Message = "[ROUTE] Dialplan regeneration failed: server={ServerId}")]
    public static partial void RegenerateFailed(ILogger logger, Exception exception, string serverId);
}

/// <summary>
/// CRUD service for inbound and outbound routes with dialplan generation.
/// </summary>
public sealed partial class RouteService
{
    private readonly IRouteRepositoryResolver _repoResolver;
    private readonly DialplanRegenerator _regenerator;
    private readonly AsteriskMonitorService _monitor;
    private readonly TrunkService _trunkService;
    private readonly ILogger<RouteService> _logger;

    public RouteService(
        IRouteRepositoryResolver repoResolver,
        DialplanRegenerator regenerator,
        AsteriskMonitorService monitor,
        TrunkService trunkService,
        ILogger<RouteService> logger)
    {
        _repoResolver = repoResolver;
        _regenerator = regenerator;
        _monitor = monitor;
        _trunkService = trunkService;
        _logger = logger;
    }

    // -----------------------------------------------------------------------
    // Pattern validation
    // -----------------------------------------------------------------------

    [GeneratedRegex(@"^(_[0-9XZNW\[\]\-\.!]+|[0-9\*#]+)$")]
    private static partial Regex DialPatternRegex();

    public static bool IsValidDialPattern(string pattern) =>
        !string.IsNullOrWhiteSpace(pattern) && DialPatternRegex().IsMatch(pattern);

    // -----------------------------------------------------------------------
    // Inbound CRUD
    // -----------------------------------------------------------------------

    /// <summary>Gets all inbound routes for a server with destination labels.</summary>
    public async Task<List<InboundRouteViewModel>> GetInboundRoutesAsync(string serverId, CancellationToken ct = default)
    {
        var repo = _repoResolver.GetRepository(serverId);
        var routes = await repo.GetInboundRoutesAsync(serverId, ct);
        var viewModels = new List<InboundRouteViewModel>(routes.Count);

        foreach (var r in routes)
        {
            viewModels.Add(new InboundRouteViewModel
            {
                Id = r.Id,
                Name = r.Name,
                DidPattern = r.DidPattern,
                DestinationType = r.DestinationType,
                Destination = r.Destination,
                DestinationLabel = await ResolveDestinationLabelAsync(serverId, r.DestinationType, r.Destination),
                Priority = r.Priority,
                Enabled = r.Enabled,
            });
        }

        return viewModels;
    }

    /// <summary>Gets a single inbound route by ID, searching all servers.</summary>
    public async Task<InboundRouteConfig?> GetInboundRouteAsync(int id, CancellationToken ct = default)
    {
        foreach (var kvp in _monitor.Servers)
        {
            var repo = _repoResolver.GetRepository(kvp.Key);
            var route = await repo.GetInboundRouteAsync(id, ct);
            if (route is not null) return route;
        }
        return null;
    }

    /// <summary>Creates an inbound route after validation.</summary>
    public async Task<(bool Success, string? Error)> CreateInboundRouteAsync(InboundRouteConfig config, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(config.DidPattern))
            return (false, "DID pattern is required");

        if (string.IsNullOrWhiteSpace(config.DestinationType) || string.IsNullOrWhiteSpace(config.Destination))
            return (false, "Destination is required");

        var repo = _repoResolver.GetRepository(config.ServerId);

        // Check for duplicate DID
        var existing = await repo.GetInboundRoutesAsync(config.ServerId, ct);
        if (existing.Any(r => string.Equals(r.DidPattern, config.DidPattern, StringComparison.OrdinalIgnoreCase)))
            return (false, $"DID pattern '{config.DidPattern}' already exists");

        await repo.CreateInboundRouteAsync(config, ct);
        await RegenerateDialplanAsync(config.ServerId, ct);

        RouteServiceLog.InboundCreated(_logger, config.ServerId, config.DidPattern);
        return (true, null);
    }

    /// <summary>Updates an inbound route after validation.</summary>
    public async Task<(bool Success, string? Error)> UpdateInboundRouteAsync(InboundRouteConfig config, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(config.DidPattern))
            return (false, "DID pattern is required");

        if (string.IsNullOrWhiteSpace(config.DestinationType) || string.IsNullOrWhiteSpace(config.Destination))
            return (false, "Destination is required");

        var repo = _repoResolver.GetRepository(config.ServerId);
        var success = await repo.UpdateInboundRouteAsync(config, ct);
        if (!success) return (false, "Route not found");

        await RegenerateDialplanAsync(config.ServerId, ct);

        RouteServiceLog.InboundUpdated(_logger, config.ServerId, config.Id);
        return (true, null);
    }

    /// <summary>Deletes an inbound route.</summary>
    public async Task<bool> DeleteInboundRouteAsync(int id, string serverId, CancellationToken ct = default)
    {
        var repo = _repoResolver.GetRepository(serverId);
        var success = await repo.DeleteInboundRouteAsync(id, ct);
        if (!success) return false;

        await RegenerateDialplanAsync(serverId, ct);

        RouteServiceLog.InboundDeleted(_logger, serverId, id);
        return true;
    }

    // -----------------------------------------------------------------------
    // Outbound CRUD
    // -----------------------------------------------------------------------

    /// <summary>Gets all outbound routes for a server.</summary>
    public async Task<List<OutboundRouteViewModel>> GetOutboundRoutesAsync(string serverId, CancellationToken ct = default)
    {
        var repo = _repoResolver.GetRepository(serverId);
        var routes = await repo.GetOutboundRoutesAsync(serverId, ct);
        var viewModels = new List<OutboundRouteViewModel>(routes.Count);

        foreach (var r in routes)
        {
            viewModels.Add(new OutboundRouteViewModel
            {
                Id = r.Id,
                Name = r.Name,
                DialPattern = r.DialPattern,
                Priority = r.Priority,
                Enabled = r.Enabled,
                Trunks = r.Trunks,
                PrimaryTrunk = r.Trunks.OrderBy(t => t.Sequence).FirstOrDefault()?.TrunkName ?? "",
            });
        }

        return viewModels;
    }

    /// <summary>Gets a single outbound route by ID, searching all servers.</summary>
    public async Task<OutboundRouteConfig?> GetOutboundRouteAsync(int id, CancellationToken ct = default)
    {
        foreach (var kvp in _monitor.Servers)
        {
            var repo = _repoResolver.GetRepository(kvp.Key);
            var route = await repo.GetOutboundRouteAsync(id, ct);
            if (route is not null) return route;
        }
        return null;
    }

    /// <summary>Creates an outbound route after validation.</summary>
    public async Task<(bool Success, string? Error)> CreateOutboundRouteAsync(OutboundRouteConfig config, CancellationToken ct = default)
    {
        if (!IsValidDialPattern(config.DialPattern))
            return (false, "Invalid dial pattern");

        if (config.Trunks.Count == 0)
            return (false, "At least one trunk is required");

        var repo = _repoResolver.GetRepository(config.ServerId);
        await repo.CreateOutboundRouteAsync(config, ct);
        await RegenerateDialplanAsync(config.ServerId, ct);

        RouteServiceLog.OutboundCreated(_logger, config.ServerId, config.DialPattern);
        return (true, null);
    }

    /// <summary>Updates an outbound route after validation.</summary>
    public async Task<(bool Success, string? Error)> UpdateOutboundRouteAsync(OutboundRouteConfig config, CancellationToken ct = default)
    {
        if (!IsValidDialPattern(config.DialPattern))
            return (false, "Invalid dial pattern");

        if (config.Trunks.Count == 0)
            return (false, "At least one trunk is required");

        var repo = _repoResolver.GetRepository(config.ServerId);
        var success = await repo.UpdateOutboundRouteAsync(config, ct);
        if (!success) return (false, "Route not found");

        await RegenerateDialplanAsync(config.ServerId, ct);

        RouteServiceLog.OutboundUpdated(_logger, config.ServerId, config.Id);
        return (true, null);
    }

    /// <summary>Deletes an outbound route.</summary>
    public async Task<bool> DeleteOutboundRouteAsync(int id, string serverId, CancellationToken ct = default)
    {
        var repo = _repoResolver.GetRepository(serverId);
        var success = await repo.DeleteOutboundRouteAsync(id, ct);
        if (!success) return false;

        await RegenerateDialplanAsync(serverId, ct);

        RouteServiceLog.OutboundDeleted(_logger, serverId, id);
        return true;
    }

    // -----------------------------------------------------------------------
    // Destination resolution
    // -----------------------------------------------------------------------

    /// <summary>Returns a human-readable label for a destination.</summary>
    public Task<string> ResolveDestinationLabelAsync(string serverId, string type, string target)
    {
        var label = type switch
        {
            "extension" => $"Ext {target}",
            "queue" => $"Queue: {target}",
            "time_condition" => $"TC: {target}",
            _ => target
        };
        return Task.FromResult(label);
    }

    // -----------------------------------------------------------------------
    // Dialplan regeneration
    // -----------------------------------------------------------------------

    /// <summary>Regenerates the dialplan for a server from all routes and time conditions.</summary>
    public async Task RegenerateDialplanAsync(string serverId, CancellationToken ct = default)
    {
        try
        {
            await _regenerator.RegenerateAsync(serverId, ct);
        }
        catch (Exception ex)
        {
            RouteServiceLog.RegenerateFailed(_logger, ex, serverId);
        }
    }
}

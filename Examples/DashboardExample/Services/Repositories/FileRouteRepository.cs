using System.Text.Json;
using DashboardExample.Models;

namespace DashboardExample.Services.Repositories;

internal static partial class FileRouteLog
{
    [LoggerMessage(Level = LogLevel.Debug, Message = "[ROUTE_FILE] Loaded: server={ServerId} inbound={InboundCount} outbound={OutboundCount} tc={TcCount}")]
    public static partial void Loaded(ILogger logger, string serverId, int inboundCount, int outboundCount, int tcCount);

    [LoggerMessage(Level = LogLevel.Error, Message = "[ROUTE_FILE] Operation failed: operation={Operation} server={ServerId}")]
    public static partial void OperationFailed(ILogger logger, Exception exception, string operation, string serverId);
}

/// <summary>
/// JSON file-based fallback implementation of <see cref="IRouteRepository"/> for File-mode servers.
/// Stores all route data in <c>{dataDir}/routes-{serverId}.json</c>.
/// </summary>
public sealed class FileRouteRepository : IRouteRepository
{
    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };

    private readonly string _dataDir;
    private readonly ILogger<FileRouteRepository> _logger;
    private readonly object _lock = new();

    public FileRouteRepository(string dataDir, ILogger<FileRouteRepository> logger)
    {
        _dataDir = dataDir;
        _logger = logger;
        Directory.CreateDirectory(dataDir);
    }

    // ──────────────────────────── Inbound Routes ────────────────────────────

    public Task<List<InboundRouteConfig>> GetInboundRoutesAsync(string serverId, CancellationToken ct = default)
    {
        var data = Load(serverId);
        return Task.FromResult(data.InboundRoutes.Where(r => r.ServerId == serverId).ToList());
    }

    public Task<InboundRouteConfig?> GetInboundRouteAsync(int id, CancellationToken ct = default)
    {
        var data = LoadAny();
        return Task.FromResult(data.InboundRoutes.FirstOrDefault(r => r.Id == id));
    }

    public Task<int> CreateInboundRouteAsync(InboundRouteConfig config, CancellationToken ct = default)
    {
        lock (_lock)
        {
            var data = Load(config.ServerId);
            config.Id = ++data.NextId;
            data.InboundRoutes.Add(config);
            Save(config.ServerId, data);
            return Task.FromResult(config.Id);
        }
    }

    public Task<bool> UpdateInboundRouteAsync(InboundRouteConfig config, CancellationToken ct = default)
    {
        lock (_lock)
        {
            var data = Load(config.ServerId);
            var idx = data.InboundRoutes.FindIndex(r => r.Id == config.Id);
            if (idx < 0) return Task.FromResult(false);
            data.InboundRoutes[idx] = config;
            Save(config.ServerId, data);
            return Task.FromResult(true);
        }
    }

    public Task<bool> DeleteInboundRouteAsync(int id, CancellationToken ct = default)
    {
        lock (_lock)
        {
            var data = LoadAny();
            var serverId = data.InboundRoutes.FirstOrDefault(r => r.Id == id)?.ServerId;
            if (serverId is null) return Task.FromResult(false);
            data = Load(serverId);
            var removed = data.InboundRoutes.RemoveAll(r => r.Id == id) > 0;
            if (removed) Save(serverId, data);
            return Task.FromResult(removed);
        }
    }

    // ──────────────────────────── Outbound Routes ────────────────────────────

    public Task<List<OutboundRouteConfig>> GetOutboundRoutesAsync(string serverId, CancellationToken ct = default)
    {
        var data = Load(serverId);
        return Task.FromResult(data.OutboundRoutes.Where(r => r.ServerId == serverId).ToList());
    }

    public Task<OutboundRouteConfig?> GetOutboundRouteAsync(int id, CancellationToken ct = default)
    {
        var data = LoadAny();
        return Task.FromResult(data.OutboundRoutes.FirstOrDefault(r => r.Id == id));
    }

    public Task<int> CreateOutboundRouteAsync(OutboundRouteConfig config, CancellationToken ct = default)
    {
        lock (_lock)
        {
            var data = Load(config.ServerId);
            config.Id = ++data.NextId;
            data.OutboundRoutes.Add(config);
            Save(config.ServerId, data);
            return Task.FromResult(config.Id);
        }
    }

    public Task<bool> UpdateOutboundRouteAsync(OutboundRouteConfig config, CancellationToken ct = default)
    {
        lock (_lock)
        {
            var data = Load(config.ServerId);
            var idx = data.OutboundRoutes.FindIndex(r => r.Id == config.Id);
            if (idx < 0) return Task.FromResult(false);
            data.OutboundRoutes[idx] = config;
            Save(config.ServerId, data);
            return Task.FromResult(true);
        }
    }

    public Task<bool> DeleteOutboundRouteAsync(int id, CancellationToken ct = default)
    {
        lock (_lock)
        {
            var data = LoadAny();
            var serverId = data.OutboundRoutes.FirstOrDefault(r => r.Id == id)?.ServerId;
            if (serverId is null) return Task.FromResult(false);
            data = Load(serverId);
            var removed = data.OutboundRoutes.RemoveAll(r => r.Id == id) > 0;
            if (removed) Save(serverId, data);
            return Task.FromResult(removed);
        }
    }

    // ──────────────────────────── Time Conditions ────────────────────────────

    public Task<List<TimeConditionConfig>> GetTimeConditionsAsync(string serverId, CancellationToken ct = default)
    {
        var data = Load(serverId);
        return Task.FromResult(data.TimeConditions.Where(t => t.ServerId == serverId).ToList());
    }

    public Task<TimeConditionConfig?> GetTimeConditionAsync(int id, CancellationToken ct = default)
    {
        var data = LoadAny();
        return Task.FromResult(data.TimeConditions.FirstOrDefault(t => t.Id == id));
    }

    public Task<int> CreateTimeConditionAsync(TimeConditionConfig config, CancellationToken ct = default)
    {
        lock (_lock)
        {
            var data = Load(config.ServerId);
            config.Id = ++data.NextId;
            data.TimeConditions.Add(config);
            Save(config.ServerId, data);
            return Task.FromResult(config.Id);
        }
    }

    public Task<bool> UpdateTimeConditionAsync(TimeConditionConfig config, CancellationToken ct = default)
    {
        lock (_lock)
        {
            var data = Load(config.ServerId);
            var idx = data.TimeConditions.FindIndex(t => t.Id == config.Id);
            if (idx < 0) return Task.FromResult(false);
            data.TimeConditions[idx] = config;
            Save(config.ServerId, data);
            return Task.FromResult(true);
        }
    }

    public Task<bool> DeleteTimeConditionAsync(int id, CancellationToken ct = default)
    {
        lock (_lock)
        {
            var data = LoadAny();
            var serverId = data.TimeConditions.FirstOrDefault(t => t.Id == id)?.ServerId;
            if (serverId is null) return Task.FromResult(false);
            data = Load(serverId);
            var removed = data.TimeConditions.RemoveAll(t => t.Id == id) > 0;
            if (removed) Save(serverId, data);
            return Task.FromResult(removed);
        }
    }

    public Task<bool> IsTimeConditionReferencedAsync(int timeConditionId, CancellationToken ct = default)
    {
        var data = LoadAny();
        var tc = data.TimeConditions.FirstOrDefault(t => t.Id == timeConditionId);
        if (tc is null) return Task.FromResult(false);

        var referenced = data.InboundRoutes.Any(r =>
            string.Equals(r.DestinationType, "time_condition", StringComparison.OrdinalIgnoreCase)
            && string.Equals(r.Destination, tc.Name, StringComparison.OrdinalIgnoreCase));

        return Task.FromResult(referenced);
    }

    // ──────────────────────────── Persistence ────────────────────────────

    private string FilePath(string serverId) =>
        Path.Combine(_dataDir, $"routes-{serverId}.json");

    private RouteData Load(string serverId)
    {
        var path = FilePath(serverId);
        if (!File.Exists(path)) return new RouteData();

        try
        {
            var json = File.ReadAllText(path);
            return JsonSerializer.Deserialize<RouteData>(json) ?? new RouteData();
        }
        catch (Exception ex)
        {
            FileRouteLog.OperationFailed(_logger, ex, "Load", serverId);
            return new RouteData();
        }
    }

    /// <summary>Scans all route files to find a record by id (used for delete/get-by-id across servers).</summary>
    private RouteData LoadAny()
    {
        var merged = new RouteData();
        if (!Directory.Exists(_dataDir)) return merged;

        foreach (var file in Directory.GetFiles(_dataDir, "routes-*.json"))
        {
            try
            {
                var json = File.ReadAllText(file);
                var data = JsonSerializer.Deserialize<RouteData>(json);
                if (data is null) continue;
                merged.InboundRoutes.AddRange(data.InboundRoutes);
                merged.OutboundRoutes.AddRange(data.OutboundRoutes);
                merged.TimeConditions.AddRange(data.TimeConditions);
            }
            catch
            {
                // Skip unreadable files
            }
        }

        return merged;
    }

    private void Save(string serverId, RouteData data)
    {
        var path = FilePath(serverId);
        try
        {
            var json = JsonSerializer.Serialize(data, JsonOptions);
            File.WriteAllText(path, json);
            FileRouteLog.Loaded(_logger, serverId, data.InboundRoutes.Count, data.OutboundRoutes.Count, data.TimeConditions.Count);
        }
        catch (Exception ex)
        {
            FileRouteLog.OperationFailed(_logger, ex, "Save", serverId);
        }
    }

    private sealed class RouteData
    {
        public int NextId { get; set; }
        public List<InboundRouteConfig> InboundRoutes { get; set; } = [];
        public List<OutboundRouteConfig> OutboundRoutes { get; set; } = [];
        public List<TimeConditionConfig> TimeConditions { get; set; } = [];
    }
}

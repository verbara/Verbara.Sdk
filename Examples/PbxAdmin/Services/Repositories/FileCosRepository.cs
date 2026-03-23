using System.Text.Json;
using PbxAdmin.Models;

namespace PbxAdmin.Services.Repositories;

internal static partial class FileCosLog
{
    [LoggerMessage(Level = LogLevel.Debug, Message = "[COS_FILE] Loaded: server={ServerId} levels={LevelCount} overrides={OverrideCount}")]
    public static partial void Loaded(ILogger logger, string serverId, int levelCount, int overrideCount);

    [LoggerMessage(Level = LogLevel.Error, Message = "[COS_FILE] Operation failed: operation={Operation} server={ServerId}")]
    public static partial void OperationFailed(ILogger logger, Exception exception, string operation, string serverId);
}

/// <summary>
/// JSON file-based fallback implementation of <see cref="ICosRepository"/> for File-mode servers.
/// Stores all COS data in <c>{dataDir}/cos-{serverId}.json</c>.
/// </summary>
public sealed class FileCosRepository : ICosRepository
{
    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };

    private readonly string _dataDir;
    private readonly ILogger<FileCosRepository> _logger;
    private readonly object _lock = new();

    public FileCosRepository(string dataDir, ILogger<FileCosRepository> logger)
    {
        _dataDir = dataDir;
        _logger = logger;
        Directory.CreateDirectory(dataDir);
    }

    // ──────────────────────────── Levels ────────────────────────────

    public Task<List<CosLevel>> GetLevelsAsync(string serverId, CancellationToken ct = default)
    {
        var data = Load(serverId);
        return Task.FromResult(data.Levels.Where(l => l.ServerId == serverId).ToList());
    }

    public Task<CosLevel?> GetLevelAsync(int id, CancellationToken ct = default)
    {
        var data = LoadAny();
        var level = data.Levels.FirstOrDefault(l => l.Id == id);
        if (level is not null)
        {
            level.Rules = data.Rules.Where(r => r.CosLevelId == id).OrderBy(r => r.Sequence).ToList();
        }

        return Task.FromResult(level);
    }

    public Task<int> CreateLevelAsync(CosLevel level, CancellationToken ct = default)
    {
        lock (_lock)
        {
            var data = Load(level.ServerId);
            level.Id = ++data.NextId;
            level.UpdatedAt = DateTime.UtcNow;
            data.Levels.Add(level);
            Save(level.ServerId, data);
            return Task.FromResult(level.Id);
        }
    }

    public Task<bool> UpdateLevelAsync(CosLevel level, CancellationToken ct = default)
    {
        lock (_lock)
        {
            var data = Load(level.ServerId);
            var idx = data.Levels.FindIndex(l => l.Id == level.Id);
            if (idx < 0) return Task.FromResult(false);

            var existing = data.Levels[idx];
            if (existing.UpdatedAt != level.UpdatedAt)
                throw new ConcurrencyConflictException($"CosLevel {level.Id} was modified by another user.");

            level.UpdatedAt = DateTime.UtcNow;
            data.Levels[idx] = level;
            Save(level.ServerId, data);
            return Task.FromResult(true);
        }
    }

    public Task<bool> DeleteLevelAsync(int id, CancellationToken ct = default)
    {
        lock (_lock)
        {
            var data = LoadAny();
            var serverId = data.Levels.FirstOrDefault(l => l.Id == id)?.ServerId;
            if (serverId is null) return Task.FromResult(false);
            data = Load(serverId);
            var removed = data.Levels.RemoveAll(l => l.Id == id) > 0;
            if (removed)
            {
                data.Rules.RemoveAll(r => r.CosLevelId == id);
                data.TimeOverrides.RemoveAll(t => t.CosLevelId == id);
                Save(serverId, data);
            }

            return Task.FromResult(removed);
        }
    }

    // ──────────────────────────── Level Rules ────────────────────────────

    public Task<List<CosLevelRule>> GetLevelRulesAsync(int cosLevelId, CancellationToken ct = default)
    {
        var data = LoadAny();
        var rules = data.Rules
            .Where(r => r.CosLevelId == cosLevelId)
            .OrderBy(r => r.Sequence)
            .ToList();
        return Task.FromResult(rules);
    }

    public Task<bool> SetLevelRulesAsync(int cosLevelId, List<CosLevelRule> rules, CancellationToken ct = default)
    {
        lock (_lock)
        {
            // Find the server that owns this level
            var all = LoadAny();
            var level = all.Levels.FirstOrDefault(l => l.Id == cosLevelId);
            if (level is null) return Task.FromResult(false);

            var data = Load(level.ServerId);
            data.Rules.RemoveAll(r => r.CosLevelId == cosLevelId);

            var nextRuleId = data.Rules.Count > 0 ? data.Rules.Max(r => r.Id) : 0;
            foreach (var rule in rules)
            {
                rule.Id = ++nextRuleId;
                rule.CosLevelId = cosLevelId;
                data.Rules.Add(rule);
            }

            Save(level.ServerId, data);
            return Task.FromResult(true);
        }
    }

    // ──────────────────────────── Extension Overrides ────────────────────────────

    public Task<List<CosExtensionOverride>> GetExtensionOverridesAsync(string serverId, CancellationToken ct = default)
    {
        var data = Load(serverId);
        return Task.FromResult(data.ExtensionOverrides.Where(o => o.ServerId == serverId).OrderBy(o => o.Extension).ToList());
    }

    public Task<CosExtensionOverride?> GetExtensionOverrideAsync(string serverId, string extension, CancellationToken ct = default)
    {
        var data = Load(serverId);
        return Task.FromResult(data.ExtensionOverrides.FirstOrDefault(
            o => o.ServerId == serverId && string.Equals(o.Extension, extension, StringComparison.Ordinal)));
    }

    public Task<bool> UpsertExtensionOverrideAsync(CosExtensionOverride entry, CancellationToken ct = default)
    {
        lock (_lock)
        {
            var data = Load(entry.ServerId);
            var idx = data.ExtensionOverrides.FindIndex(
                o => o.ServerId == entry.ServerId && string.Equals(o.Extension, entry.Extension, StringComparison.Ordinal));

            if (idx >= 0)
            {
                entry.Id = data.ExtensionOverrides[idx].Id;
                data.ExtensionOverrides[idx] = entry;
            }
            else
            {
                entry.Id = ++data.NextId;
                data.ExtensionOverrides.Add(entry);
            }

            Save(entry.ServerId, data);
            return Task.FromResult(true);
        }
    }

    public Task<bool> DeleteExtensionOverrideAsync(string serverId, string extension, CancellationToken ct = default)
    {
        lock (_lock)
        {
            var data = Load(serverId);
            var removed = data.ExtensionOverrides.RemoveAll(
                o => o.ServerId == serverId && string.Equals(o.Extension, extension, StringComparison.Ordinal)) > 0;
            if (removed) Save(serverId, data);
            return Task.FromResult(removed);
        }
    }

    // ──────────────────────────── Time Windows ────────────────────────────

    public Task<List<CosTimeWindow>> GetTimeWindowsAsync(string serverId, CancellationToken ct = default)
    {
        var data = Load(serverId);
        return Task.FromResult(data.TimeWindows.Where(t => t.ServerId == serverId).OrderBy(t => t.Name).ToList());
    }

    // ──────────────────────────── Time Overrides ────────────────────────────

    public Task<List<CosTimeOverride>> GetTimeOverridesAsync(int cosLevelId, CancellationToken ct = default)
    {
        var data = LoadAny();
        return Task.FromResult(data.TimeOverrides.Where(t => t.CosLevelId == cosLevelId).OrderBy(t => t.Priority).ToList());
    }

    public Task<int> CreateTimeOverrideAsync(CosTimeOverride entry, CancellationToken ct = default)
    {
        lock (_lock)
        {
            var all = LoadAny();
            var level = all.Levels.FirstOrDefault(l => l.Id == entry.CosLevelId);
            if (level is null) return Task.FromResult(0);

            var data = Load(level.ServerId);
            entry.Id = ++data.NextId;
            data.TimeOverrides.Add(entry);
            Save(level.ServerId, data);
            return Task.FromResult(entry.Id);
        }
    }

    public Task<bool> DeleteTimeOverrideAsync(int id, CancellationToken ct = default)
    {
        lock (_lock)
        {
            var all = LoadAny();
            var existing = all.TimeOverrides.FirstOrDefault(t => t.Id == id);
            if (existing is null) return Task.FromResult(false);

            var level = all.Levels.FirstOrDefault(l => l.Id == existing.CosLevelId);
            if (level is null) return Task.FromResult(false);

            var data = Load(level.ServerId);
            var removed = data.TimeOverrides.RemoveAll(t => t.Id == id) > 0;
            if (removed) Save(level.ServerId, data);
            return Task.FromResult(removed);
        }
    }

    // ──────────────────────────── Audit Log ────────────────────────────

    public Task AppendAuditLogAsync(CosAuditEntry entry, CancellationToken ct = default)
    {
        lock (_lock)
        {
            var data = Load(entry.ServerId);
            data.AuditLog.Add(entry);
            Save(entry.ServerId, data);
        }

        return Task.CompletedTask;
    }

    // ──────────────────────────── Persistence ────────────────────────────

    private string FilePath(string serverId) =>
        Path.Combine(_dataDir, $"cos-{serverId}.json");

    private CosData Load(string serverId)
    {
        var path = FilePath(serverId);
        if (!File.Exists(path)) return new CosData();

        try
        {
            var json = File.ReadAllText(path);
            return JsonSerializer.Deserialize<CosData>(json) ?? new CosData();
        }
        catch (Exception ex)
        {
            FileCosLog.OperationFailed(_logger, ex, "Load", serverId);
            return new CosData();
        }
    }

    /// <summary>Scans all COS files to find a record by id (used for get-by-id/delete across servers).</summary>
    private CosData LoadAny()
    {
        var merged = new CosData();
        if (!Directory.Exists(_dataDir)) return merged;

        foreach (var file in Directory.GetFiles(_dataDir, "cos-*.json"))
        {
            try
            {
                var json = File.ReadAllText(file);
                var data = JsonSerializer.Deserialize<CosData>(json);
                if (data is null) continue;
                merged.Levels.AddRange(data.Levels);
                merged.Rules.AddRange(data.Rules);
                merged.ExtensionOverrides.AddRange(data.ExtensionOverrides);
                merged.TimeWindows.AddRange(data.TimeWindows);
                merged.TimeOverrides.AddRange(data.TimeOverrides);
                merged.AuditLog.AddRange(data.AuditLog);
            }
            catch
            {
                // Skip unreadable files
            }
        }

        return merged;
    }

    private void Save(string serverId, CosData data)
    {
        var path = FilePath(serverId);
        try
        {
            var json = JsonSerializer.Serialize(data, JsonOptions);
            File.WriteAllText(path, json);
            FileCosLog.Loaded(_logger, serverId, data.Levels.Count, data.ExtensionOverrides.Count);
        }
        catch (Exception ex)
        {
            FileCosLog.OperationFailed(_logger, ex, "Save", serverId);
        }
    }

    private sealed class CosData
    {
        public int NextId { get; set; }
        public List<CosLevel> Levels { get; set; } = [];
        public List<CosLevelRule> Rules { get; set; } = [];
        public List<CosExtensionOverride> ExtensionOverrides { get; set; } = [];
        public List<CosTimeWindow> TimeWindows { get; set; } = [];
        public List<CosTimeOverride> TimeOverrides { get; set; } = [];
        public List<CosAuditEntry> AuditLog { get; set; } = [];
    }
}

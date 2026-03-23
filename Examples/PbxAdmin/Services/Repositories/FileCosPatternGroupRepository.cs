using System.Text.Json;
using PbxAdmin.Models;

namespace PbxAdmin.Services.Repositories;

internal static partial class FileCosPatternGroupLog
{
    [LoggerMessage(Level = LogLevel.Debug, Message = "[COS_PG_FILE] Loaded: server={ServerId} groups={GroupCount}")]
    public static partial void Loaded(ILogger logger, string serverId, int groupCount);

    [LoggerMessage(Level = LogLevel.Error, Message = "[COS_PG_FILE] Operation failed: operation={Operation} server={ServerId}")]
    public static partial void OperationFailed(ILogger logger, Exception exception, string operation, string serverId);
}

/// <summary>
/// JSON file-based fallback implementation of <see cref="ICosPatternGroupRepository"/> for File-mode servers.
/// Stores all pattern group data in <c>{dataDir}/patterns-{serverId}.json</c>.
/// </summary>
public sealed class FileCosPatternGroupRepository : ICosPatternGroupRepository
{
    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };

    private readonly string _dataDir;
    private readonly ILogger<FileCosPatternGroupRepository> _logger;
    private readonly object _lock = new();

    public FileCosPatternGroupRepository(string dataDir, ILogger<FileCosPatternGroupRepository> logger)
    {
        _dataDir = dataDir;
        _logger = logger;
        Directory.CreateDirectory(dataDir);
    }

    // ──────────────────────────── Groups ────────────────────────────

    public Task<List<CosPatternGroup>> GetGroupsAsync(string serverId, CancellationToken ct = default)
    {
        var data = Load(serverId);
        return Task.FromResult(data.Groups.Where(g => g.ServerId == serverId).OrderBy(g => g.Name).ToList());
    }

    public Task<CosPatternGroup?> GetGroupAsync(int id, CancellationToken ct = default)
    {
        var data = LoadAny();
        return Task.FromResult(data.Groups.FirstOrDefault(g => g.Id == id));
    }

    public Task<int> CreateGroupAsync(CosPatternGroup group, CancellationToken ct = default)
    {
        lock (_lock)
        {
            var data = Load(group.ServerId);
            group.Id = ++data.NextId;
            group.UpdatedAt = DateTime.UtcNow;
            data.Groups.Add(group);
            Save(group.ServerId, data);
            return Task.FromResult(group.Id);
        }
    }

    public Task<bool> UpdateGroupAsync(CosPatternGroup group, CancellationToken ct = default)
    {
        lock (_lock)
        {
            var data = Load(group.ServerId);
            var idx = data.Groups.FindIndex(g => g.Id == group.Id);
            if (idx < 0) return Task.FromResult(false);

            var existing = data.Groups[idx];
            if (existing.UpdatedAt != group.UpdatedAt)
                throw new ConcurrencyConflictException($"CosPatternGroup {group.Id} was modified by another user.");

            group.UpdatedAt = DateTime.UtcNow;
            data.Groups[idx] = group;
            Save(group.ServerId, data);
            return Task.FromResult(true);
        }
    }

    public Task<bool> DeleteGroupAsync(int id, CancellationToken ct = default)
    {
        lock (_lock)
        {
            var all = LoadAny();
            var serverId = all.Groups.FirstOrDefault(g => g.Id == id)?.ServerId;
            if (serverId is null) return Task.FromResult(false);
            var data = Load(serverId);
            var removed = data.Groups.RemoveAll(g => g.Id == id) > 0;
            if (removed) Save(serverId, data);
            return Task.FromResult(removed);
        }
    }

    public Task<bool> IsGroupReferencedAsync(int id, CancellationToken ct = default)
    {
        // Scan all cos-*.json files to check if any CosLevelRule references this pattern group
        if (!Directory.Exists(_dataDir))
            return Task.FromResult(false);

        foreach (var file in Directory.GetFiles(_dataDir, "cos-*.json"))
        {
            try
            {
                var json = File.ReadAllText(file);
                var cosData = JsonSerializer.Deserialize<CosDataView>(json);
                if (cosData?.Rules is not null && cosData.Rules.Exists(r => r.PatternGroupId == id))
                    return Task.FromResult(true);
            }
            catch
            {
                // Skip unreadable files
            }
        }

        return Task.FromResult(false);
    }

    // ──────────────────────────── Persistence ────────────────────────────

    private string FilePath(string serverId) =>
        Path.Combine(_dataDir, $"patterns-{serverId}.json");

    private PatternGroupData Load(string serverId)
    {
        var path = FilePath(serverId);
        if (!File.Exists(path)) return new PatternGroupData();

        try
        {
            var json = File.ReadAllText(path);
            return JsonSerializer.Deserialize<PatternGroupData>(json) ?? new PatternGroupData();
        }
        catch (Exception ex)
        {
            FileCosPatternGroupLog.OperationFailed(_logger, ex, "Load", serverId);
            return new PatternGroupData();
        }
    }

    /// <summary>Scans all pattern group files to find a record by id across servers.</summary>
    private PatternGroupData LoadAny()
    {
        var merged = new PatternGroupData();
        if (!Directory.Exists(_dataDir)) return merged;

        foreach (var file in Directory.GetFiles(_dataDir, "patterns-*.json"))
        {
            try
            {
                var json = File.ReadAllText(file);
                var data = JsonSerializer.Deserialize<PatternGroupData>(json);
                if (data is null) continue;
                merged.Groups.AddRange(data.Groups);
            }
            catch
            {
                // Skip unreadable files
            }
        }

        return merged;
    }

    private void Save(string serverId, PatternGroupData data)
    {
        var path = FilePath(serverId);
        try
        {
            var json = JsonSerializer.Serialize(data, JsonOptions);
            File.WriteAllText(path, json);
            FileCosPatternGroupLog.Loaded(_logger, serverId, data.Groups.Count);
        }
        catch (Exception ex)
        {
            FileCosPatternGroupLog.OperationFailed(_logger, ex, "Save", serverId);
        }
    }

    private sealed class PatternGroupData
    {
        public int NextId { get; set; }
        public List<CosPatternGroup> Groups { get; set; } = [];
    }

    /// <summary>Minimal view of COS data for cross-file reference checking.</summary>
    private sealed class CosDataView
    {
        public List<CosLevelRule> Rules { get; set; } = [];
    }
}

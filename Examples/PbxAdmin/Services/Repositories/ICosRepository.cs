using PbxAdmin.Models;

namespace PbxAdmin.Services.Repositories;

public interface ICosRepository
{
    Task<List<CosLevel>> GetLevelsAsync(string serverId, CancellationToken ct = default);
    Task<CosLevel?> GetLevelAsync(int id, CancellationToken ct = default);
    Task<int> CreateLevelAsync(CosLevel level, CancellationToken ct = default);
    Task<bool> UpdateLevelAsync(CosLevel level, CancellationToken ct = default);
    Task<bool> DeleteLevelAsync(int id, CancellationToken ct = default);

    Task<List<CosLevelRule>> GetLevelRulesAsync(int cosLevelId, CancellationToken ct = default);
    Task<bool> SetLevelRulesAsync(int cosLevelId, List<CosLevelRule> rules, CancellationToken ct = default);

    Task<List<CosExtensionOverride>> GetExtensionOverridesAsync(string serverId, CancellationToken ct = default);
    Task<CosExtensionOverride?> GetExtensionOverrideAsync(string serverId, string extension, CancellationToken ct = default);
    Task<bool> UpsertExtensionOverrideAsync(CosExtensionOverride entry, CancellationToken ct = default);
    Task<bool> DeleteExtensionOverrideAsync(string serverId, string extension, CancellationToken ct = default);

    Task<List<CosTimeWindow>> GetTimeWindowsAsync(string serverId, CancellationToken ct = default);

    Task<List<CosTimeOverride>> GetTimeOverridesAsync(int cosLevelId, CancellationToken ct = default);
    Task<int> CreateTimeOverrideAsync(CosTimeOverride entry, CancellationToken ct = default);
    Task<bool> DeleteTimeOverrideAsync(int id, CancellationToken ct = default);

    Task AppendAuditLogAsync(CosAuditEntry entry, CancellationToken ct = default);
}

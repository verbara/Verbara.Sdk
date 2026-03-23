using PbxAdmin.Models;

namespace PbxAdmin.Services.Repositories;

public interface ICosPatternGroupRepository
{
    Task<List<CosPatternGroup>> GetGroupsAsync(string serverId, CancellationToken ct = default);
    Task<CosPatternGroup?> GetGroupAsync(int id, CancellationToken ct = default);
    Task<int> CreateGroupAsync(CosPatternGroup group, CancellationToken ct = default);
    Task<bool> UpdateGroupAsync(CosPatternGroup group, CancellationToken ct = default);
    Task<bool> DeleteGroupAsync(int id, CancellationToken ct = default);
    Task<bool> IsGroupReferencedAsync(int id, CancellationToken ct = default);
}

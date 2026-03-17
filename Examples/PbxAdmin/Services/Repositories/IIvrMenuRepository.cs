using PbxAdmin.Models;

namespace PbxAdmin.Services.Repositories;

public interface IIvrMenuRepository
{
    Task<List<IvrMenuConfig>> GetMenusAsync(string serverId, CancellationToken ct = default);
    Task<IvrMenuConfig?> GetMenuAsync(int id, CancellationToken ct = default);
    Task<IvrMenuConfig?> GetMenuByNameAsync(string serverId, string name, CancellationToken ct = default);
    Task<int> CreateMenuAsync(IvrMenuConfig config, CancellationToken ct = default);
    Task UpdateMenuAsync(IvrMenuConfig config, CancellationToken ct = default);
    Task DeleteMenuAsync(int id, CancellationToken ct = default);
    Task<bool> IsMenuReferencedAsync(int id, CancellationToken ct = default);
}

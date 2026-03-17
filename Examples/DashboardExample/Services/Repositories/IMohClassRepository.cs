using DashboardExample.Models;

namespace DashboardExample.Services.Repositories;

public interface IMohClassRepository
{
    Task<List<MohClass>> GetAllAsync(string serverId, CancellationToken ct = default);
    Task<MohClass?> GetByIdAsync(int id, CancellationToken ct = default);
    Task<MohClass?> GetByNameAsync(string serverId, string name, CancellationToken ct = default);
    Task<int> InsertAsync(MohClass mohClass, CancellationToken ct = default);
    Task UpdateAsync(MohClass mohClass, CancellationToken ct = default);
    Task DeleteAsync(int id, CancellationToken ct = default);
}

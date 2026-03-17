using DashboardExample.Models;

namespace DashboardExample.Services.Repositories;

public interface IConferenceConfigRepository
{
    Task<List<ConferenceConfig>> GetAllAsync(string serverId, CancellationToken ct = default);
    Task<ConferenceConfig?> GetByIdAsync(int id, CancellationToken ct = default);
    Task<ConferenceConfig?> GetByNameAsync(string serverId, string name, CancellationToken ct = default);
    Task<int> InsertAsync(ConferenceConfig config, CancellationToken ct = default);
    Task UpdateAsync(ConferenceConfig config, CancellationToken ct = default);
    Task DeleteAsync(int id, CancellationToken ct = default);
}

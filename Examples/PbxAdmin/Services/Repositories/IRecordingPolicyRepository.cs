using PbxAdmin.Models;

namespace PbxAdmin.Services.Repositories;

public interface IRecordingPolicyRepository
{
    Task<List<RecordingPolicy>> GetAllAsync(string serverId, CancellationToken ct = default);
    Task<RecordingPolicy?> GetByIdAsync(int id, CancellationToken ct = default);
    Task<RecordingPolicy?> GetByNameAsync(string serverId, string name, CancellationToken ct = default);
    Task<int> InsertAsync(RecordingPolicy policy, CancellationToken ct = default);
    Task UpdateAsync(RecordingPolicy policy, CancellationToken ct = default);
    Task DeleteAsync(int id, CancellationToken ct = default);
}

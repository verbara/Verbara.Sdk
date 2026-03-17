using PbxAdmin.Models;

namespace PbxAdmin.Services.Repositories;

public interface IQueueConfigRepository
{
    Task<List<QueueConfigDto>> GetQueuesAsync(string serverId, CancellationToken ct = default);
    Task<QueueConfigDto?> GetQueueAsync(int id, CancellationToken ct = default);
    Task<QueueConfigDto?> GetQueueByNameAsync(string serverId, string name, CancellationToken ct = default);
    Task<int> CreateQueueAsync(QueueConfigDto config, CancellationToken ct = default);
    Task<bool> UpdateQueueAsync(QueueConfigDto config, CancellationToken ct = default);
    Task<bool> DeleteQueueAsync(int id, CancellationToken ct = default);

    Task<List<QueueMemberConfigDto>> GetMembersAsync(int queueConfigId, CancellationToken ct = default);
    Task<int> AddMemberAsync(QueueMemberConfigDto member, CancellationToken ct = default);
    Task<bool> UpdateMemberAsync(QueueMemberConfigDto member, CancellationToken ct = default);
    Task<bool> RemoveMemberAsync(int memberId, CancellationToken ct = default);
}

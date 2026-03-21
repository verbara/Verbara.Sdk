using PbxAdmin.Models;

namespace PbxAdmin.Services;

/// <summary>
/// CRUD service for Asterisk queue configuration via Realtime DB.
/// </summary>
public interface IQueueConfigService
{
    Task<List<QueueConfigDto>> GetQueuesAsync(string serverId, CancellationToken ct = default);
    Task<QueueConfigDto?> GetQueueAsync(int id, CancellationToken ct = default);
    Task<(bool Success, string? Error)> CreateQueueAsync(QueueConfigDto config, CancellationToken ct = default);
    Task<(bool Success, string? Error)> UpdateQueueAsync(QueueConfigDto config, CancellationToken ct = default);
    Task<(bool Success, string? Error)> DeleteQueueAsync(string serverId, int id, CancellationToken ct = default);
    Task<List<QueueMemberConfigDto>> GetMembersAsync(int queueConfigId, CancellationToken ct = default);

    Task<(bool Success, string? Error)> AddMemberAsync(
        string serverId, string queueName, QueueMemberConfigDto member, CancellationToken ct = default);

    Task<(bool Success, string? Error)> UpdateMemberAsync(
        string serverId, QueueMemberConfigDto member, CancellationToken ct = default);

    Task<(bool Success, string? Error)> RemoveMemberAsync(
        string serverId, int memberId, CancellationToken ct = default);
}

using PbxAdmin.Models;

namespace PbxAdmin.Services;

/// <summary>
/// CRUD service for Asterisk trunks (PJSIP, SIP, IAX2).
/// </summary>
public interface ITrunkService
{
    Task<List<TrunkViewModel>> GetTrunksAsync(string serverId, CancellationToken ct = default);

    Task<TrunkDetailViewModel?> GetTrunkDetailAsync(
        string serverId, string name, TrunkTechnology technology, CancellationToken ct = default);

    Task<bool> CreateTrunkAsync(string serverId, TrunkConfig config, CancellationToken ct = default);

    Task<bool> UpdateTrunkAsync(string serverId, TrunkConfig config, CancellationToken ct = default);

    Task<bool> DeleteTrunkAsync(
        string serverId, string name, TrunkTechnology technology, CancellationToken ct = default);
}

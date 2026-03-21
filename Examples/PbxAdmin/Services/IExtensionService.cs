using PbxAdmin.Models;

namespace PbxAdmin.Services;

/// <summary>
/// CRUD service for Asterisk extensions (PJSIP, SIP, IAX2).
/// </summary>
public interface IExtensionService
{
    (int Start, int End) GetExtensionRange(string serverId);

    Task SetDeviceFeaturesAsync(
        string serverId, string extension, bool dnd,
        string? cfUnconditional, string? cfBusy, string? cfNoAnswer, int cfnaTimeout,
        CancellationToken ct = default);

    Task<List<ExtensionViewModel>> GetExtensionsAsync(string serverId, CancellationToken ct = default);

    Task<ExtensionDetailViewModel?> GetExtensionDetailAsync(
        string serverId, string extension, ExtensionTechnology technology, CancellationToken ct = default);

    Task<bool> CreateExtensionAsync(string serverId, ExtensionConfig config, CancellationToken ct = default);

    Task<bool> UpdateExtensionAsync(string serverId, string extension, ExtensionConfig config, CancellationToken ct = default);

    Task<bool> DeleteExtensionAsync(
        string serverId, string extension, ExtensionTechnology technology, CancellationToken ct = default);

    Task<bool> ExtensionExistsAsync(string serverId, string extension, CancellationToken ct = default);
}

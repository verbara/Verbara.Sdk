using Asterisk.Sdk.Ami.Responses;

namespace DashboardExample.Services;

/// <summary>
/// Abstraction for reading and modifying Asterisk configuration.
/// Implemented by <see cref="PbxConfigManager"/> (AMI-based) and <see cref="DbConfigProvider"/> (database-based).
/// </summary>
public interface IConfigProvider
{
    Task<List<ConfigCategory>> GetCategoriesAsync(string serverId, string filename, CancellationToken ct = default);
    Task<Dictionary<string, string>?> GetSectionAsync(string serverId, string filename, string section, CancellationToken ct = default);
    Task<bool> CreateSectionAsync(string serverId, string filename, string section, Dictionary<string, string> variables, string? templateName = null, CancellationToken ct = default);
    Task<bool> UpdateSectionAsync(string serverId, string filename, string section, Dictionary<string, string> variables, CancellationToken ct = default);
    Task<bool> DeleteSectionAsync(string serverId, string filename, string section, CancellationToken ct = default);
    Task<string?> ExecuteCommandAsync(string serverId, string command, CancellationToken ct = default);
    Task<bool> ReloadModuleAsync(string serverId, string moduleName, CancellationToken ct = default);
}

using Asterisk.Sdk;
using Asterisk.Sdk.Ami.Actions;
using Asterisk.Sdk.Ami.Responses;

namespace DashboardExample.Services;

internal static partial class PbxConfigLog
{
    [LoggerMessage(Level = LogLevel.Error, Message = "[CONFIG_AMI] Create failed: server={ServerId} filename={Filename} section={Section}")]
    public static partial void CreateSectionFailed(ILogger logger, Exception exception, string serverId, string filename, string section);

    [LoggerMessage(Level = LogLevel.Error, Message = "[CONFIG_AMI] Update failed: server={ServerId} filename={Filename} section={Section}")]
    public static partial void UpdateSectionFailed(ILogger logger, Exception exception, string serverId, string filename, string section);

    [LoggerMessage(Level = LogLevel.Error, Message = "[CONFIG_AMI] Delete failed: server={ServerId} filename={Filename} section={Section}")]
    public static partial void DeleteSectionFailed(ILogger logger, Exception exception, string serverId, string filename, string section);

    [LoggerMessage(Level = LogLevel.Error, Message = "[CONFIG_AMI] Command failed: server={ServerId} command={Command}")]
    public static partial void ExecuteCommandFailed(ILogger logger, Exception exception, string serverId, string command);
}

/// <summary>
/// Reads and modifies Asterisk configuration files via the dedicated config AMI connection.
/// Uses <see cref="AsteriskMonitorService.ServerEntry.ConfigConnection"/> which has a longer
/// response timeout (30s) to handle slow config operations on non-standard paths.
/// </summary>
public sealed class PbxConfigManager : IConfigProvider
{
    private readonly AsteriskMonitorService _monitor;
    private readonly ILogger<PbxConfigManager> _logger;

    public PbxConfigManager(AsteriskMonitorService monitor, ILogger<PbxConfigManager> logger)
    {
        _monitor = monitor;
        _logger = logger;
    }

    public async Task<GetConfigResponse?> GetConfigAsync(string serverId, string filename, CancellationToken ct = default)
    {
        var entry = _monitor.GetServer(serverId);
        if (entry is null) return null;

        var response = await entry.ConfigConnection.SendActionAsync<GetConfigResponse>(
            new GetConfigAction { Filename = filename }, ct);

        return response.Response == "Success" ? response : null;
    }

    public async Task<List<ConfigCategory>> GetCategoriesAsync(string serverId, string filename, CancellationToken ct = default)
    {
        var response = await GetConfigAsync(serverId, filename, ct);
        return response?.Categories.ToList() ?? [];
    }

    public async Task<Dictionary<string, string>?> GetSectionAsync(string serverId, string filename, string section, CancellationToken ct = default)
    {
        var response = await GetConfigAsync(serverId, filename, ct);
        if (response is null) return null;

        var category = response.Categories.FirstOrDefault(c =>
            string.Equals(c.Name, section, StringComparison.OrdinalIgnoreCase));
        return category?.Variables;
    }

    public async Task<bool> CreateSectionAsync(string serverId, string filename, string section,
        Dictionary<string, string> variables, string? templateName = null, CancellationToken ct = default)
    {
        var entry = _monitor.GetServer(serverId);
        if (entry is null) return false;

        var action = new UpdateConfigAction
        {
            SrcFilename = filename,
            DstFilename = filename,
        };

        action.AddNewCategory(section, templateName);

        foreach (var (key, value) in variables)
        {
            action.AddAppend(section, key, value);
        }

        try
        {
            var response = await entry.ConfigConnection.SendActionAsync(action, ct);
            return response.Response == "Success";
        }
        catch (Exception ex)
        {
            PbxConfigLog.CreateSectionFailed(_logger, ex, serverId, filename, section);
            return false;
        }
    }

    public async Task<bool> UpdateSectionAsync(string serverId, string filename, string section,
        Dictionary<string, string> variables, CancellationToken ct = default)
    {
        var entry = _monitor.GetServer(serverId);
        if (entry is null) return false;

        // Delete and recreate the section with new values
        var action = new UpdateConfigAction
        {
            SrcFilename = filename,
            DstFilename = filename,
        };

        action.AddDeleteCategory(section);
        action.AddNewCategory(section);

        foreach (var (key, value) in variables)
        {
            action.AddAppend(section, key, value);
        }

        try
        {
            var response = await entry.ConfigConnection.SendActionAsync(action, ct);
            return response.Response == "Success";
        }
        catch (Exception ex)
        {
            PbxConfigLog.UpdateSectionFailed(_logger, ex, serverId, filename, section);
            return false;
        }
    }

    public async Task<bool> DeleteSectionAsync(string serverId, string filename, string section, CancellationToken ct = default)
    {
        var entry = _monitor.GetServer(serverId);
        if (entry is null) return false;

        var action = new UpdateConfigAction
        {
            SrcFilename = filename,
            DstFilename = filename,
        };
        action.AddDeleteCategory(section);

        try
        {
            var response = await entry.ConfigConnection.SendActionAsync(action, ct);
            return response.Response == "Success";
        }
        catch (Exception ex)
        {
            PbxConfigLog.DeleteSectionFailed(_logger, ex, serverId, filename, section);
            return false;
        }
    }

    public async Task<string?> ExecuteCommandAsync(string serverId, string command, CancellationToken ct = default)
    {
        var entry = _monitor.GetServer(serverId);
        if (entry is null) return null;

        try
        {
            var response = await entry.ConfigConnection.SendActionAsync<CommandResponse>(
                new CommandAction { Command = command }, ct);
            return response.Output ?? response.Message;
        }
        catch (Exception ex)
        {
            PbxConfigLog.ExecuteCommandFailed(_logger, ex, serverId, command);
            return null;
        }
    }

    public async Task<bool> ReloadModuleAsync(string serverId, string moduleName, CancellationToken ct = default)
    {
        var result = await ExecuteCommandAsync(serverId, $"module reload {moduleName}", ct);
        return result is not null;
    }
}

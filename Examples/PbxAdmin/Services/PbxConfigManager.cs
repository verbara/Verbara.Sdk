using System.Diagnostics;
using Asterisk.Sdk;
using Asterisk.Sdk.Ami.Actions;
using Asterisk.Sdk.Ami.Responses;

namespace PbxAdmin.Services;

internal static partial class PbxConfigLog
{
    [LoggerMessage(Level = LogLevel.Information, Message = "[CONFIG_AMI] >> {Operation}: server={ServerId} filename={Filename} section={Section} connection={ConnectionMode}")]
    public static partial void OperationStart(ILogger logger, string operation, string serverId, string filename, string? section, string connectionMode);

    [LoggerMessage(Level = LogLevel.Information, Message = "[CONFIG_AMI] << {Operation}: server={ServerId} filename={Filename} section={Section} result={Result} elapsed_ms={ElapsedMs}")]
    public static partial void OperationEnd(ILogger logger, string operation, string serverId, string filename, string? section, string result, long elapsedMs);

    [LoggerMessage(Level = LogLevel.Error, Message = "[CONFIG_AMI] !! {Operation}: server={ServerId} filename={Filename} section={Section} elapsed_ms={ElapsedMs}")]
    public static partial void OperationFailed(ILogger logger, Exception exception, string operation, string serverId, string filename, string? section, long elapsedMs);

    [LoggerMessage(Level = LogLevel.Information, Message = "[CONFIG_AMI] >> Command: server={ServerId} command={Command} connection={ConnectionMode}")]
    public static partial void CommandStart(ILogger logger, string serverId, string command, string connectionMode);

    [LoggerMessage(Level = LogLevel.Information, Message = "[CONFIG_AMI] << Command: server={ServerId} command={Command} result={Result} elapsed_ms={ElapsedMs}")]
    public static partial void CommandEnd(ILogger logger, string serverId, string command, string result, long elapsedMs);

    [LoggerMessage(Level = LogLevel.Error, Message = "[CONFIG_AMI] !! Command: server={ServerId} command={Command} elapsed_ms={ElapsedMs}")]
    public static partial void CommandFailed(ILogger logger, Exception exception, string serverId, string command, long elapsedMs);
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

        var mode = GetConnectionMode(entry);
        PbxConfigLog.OperationStart(_logger, "GetConfig", serverId, filename, null, mode);
        var sw = Stopwatch.GetTimestamp();

        try
        {
            var response = await entry.ConfigConnection.SendActionAsync<GetConfigResponse>(
                new GetConfigAction { Filename = filename }, ct);

            var ms = ElapsedMs(sw);
            var result = response.Response ?? "null";
            PbxConfigLog.OperationEnd(_logger, "GetConfig", serverId, filename, null, result, ms);
            return response.Response == "Success" ? response : null;
        }
        catch (Exception ex)
        {
            var ms = ElapsedMs(sw);
            PbxConfigLog.OperationFailed(_logger, ex, "GetConfig", serverId, filename, null, ms);
            return null;
        }
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

        var mode = GetConnectionMode(entry);
        PbxConfigLog.OperationStart(_logger, "CreateSection", serverId, filename, section, mode);
        var sw = Stopwatch.GetTimestamp();

        try
        {
            var response = await entry.ConfigConnection.SendActionAsync(action, ct);
            var ms = ElapsedMs(sw);
            var result = response.Response ?? "null";
            PbxConfigLog.OperationEnd(_logger, "CreateSection", serverId, filename, section, result, ms);
            return response.Response == "Success";
        }
        catch (Exception ex)
        {
            var ms = ElapsedMs(sw);
            PbxConfigLog.OperationFailed(_logger, ex, "CreateSection", serverId, filename, section, ms);
            return false;
        }
    }

    public async Task<bool> CreateSectionWithLinesAsync(string serverId, string filename, string section,
        List<KeyValuePair<string, string>> lines, CancellationToken ct = default)
    {
        var entry = _monitor.GetServer(serverId);
        if (entry is null) return false;

        var action = new UpdateConfigAction
        {
            SrcFilename = filename,
            DstFilename = filename,
        };

        action.AddDeleteCategory(section);
        action.AddNewCategory(section);

        foreach (var (key, value) in lines)
        {
            action.AddAppend(section, key, value);
        }

        var mode = GetConnectionMode(entry);
        PbxConfigLog.OperationStart(_logger, "CreateSectionWithLines", serverId, filename, section, mode);
        var sw = Stopwatch.GetTimestamp();

        try
        {
            var response = await entry.ConfigConnection.SendActionAsync(action, ct);
            var ms = ElapsedMs(sw);
            var result = response.Response ?? "null";
            PbxConfigLog.OperationEnd(_logger, "CreateSectionWithLines", serverId, filename, section, result, ms);
            return response.Response == "Success";
        }
        catch (Exception ex)
        {
            var ms = ElapsedMs(sw);
            PbxConfigLog.OperationFailed(_logger, ex, "CreateSectionWithLines", serverId, filename, section, ms);
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

        var mode = GetConnectionMode(entry);
        PbxConfigLog.OperationStart(_logger, "UpdateSection", serverId, filename, section, mode);
        var sw = Stopwatch.GetTimestamp();

        try
        {
            var response = await entry.ConfigConnection.SendActionAsync(action, ct);
            var ms = ElapsedMs(sw);
            var result = response.Response ?? "null";
            PbxConfigLog.OperationEnd(_logger, "UpdateSection", serverId, filename, section, result, ms);
            return response.Response == "Success";
        }
        catch (Exception ex)
        {
            var ms = ElapsedMs(sw);
            PbxConfigLog.OperationFailed(_logger, ex, "UpdateSection", serverId, filename, section, ms);
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

        var mode = GetConnectionMode(entry);
        PbxConfigLog.OperationStart(_logger, "DeleteSection", serverId, filename, section, mode);
        var sw = Stopwatch.GetTimestamp();

        try
        {
            var response = await entry.ConfigConnection.SendActionAsync(action, ct);
            var ms = ElapsedMs(sw);
            var result = response.Response ?? "null";
            PbxConfigLog.OperationEnd(_logger, "DeleteSection", serverId, filename, section, result, ms);
            return response.Response == "Success";
        }
        catch (Exception ex)
        {
            var ms = ElapsedMs(sw);
            PbxConfigLog.OperationFailed(_logger, ex, "DeleteSection", serverId, filename, section, ms);
            return false;
        }
    }

    public async Task<string?> ExecuteCommandAsync(string serverId, string command, CancellationToken ct = default)
    {
        var entry = _monitor.GetServer(serverId);
        if (entry is null) return null;

        var mode = GetConnectionMode(entry);
        PbxConfigLog.CommandStart(_logger, serverId, command, mode);
        var sw = Stopwatch.GetTimestamp();

        try
        {
            var response = await entry.ConfigConnection.SendActionAsync<CommandResponse>(
                new CommandAction { Command = command }, ct);
            var ms = ElapsedMs(sw);
            var result = response.Response ?? "null";
            PbxConfigLog.CommandEnd(_logger, serverId, command, result, ms);
            return response.Output ?? response.Message;
        }
        catch (Exception ex)
        {
            var ms = ElapsedMs(sw);
            PbxConfigLog.CommandFailed(_logger, ex, serverId, command, ms);
            return null;
        }
    }

    public async Task<bool> ReloadModuleAsync(string serverId, string moduleName, CancellationToken ct = default)
    {
        var result = await ExecuteCommandAsync(serverId, $"module reload {moduleName}", ct);
        return result is not null;
    }

    /// <summary>Returns "dedicated" if ConfigConnection is a separate connection, "shared" if using fallback.</summary>
    private static string GetConnectionMode(AsteriskMonitorService.ServerEntry entry) =>
        ReferenceEquals(entry.ConfigConnection, entry.Connection) ? "shared (fallback)" : "dedicated";

    private static long ElapsedMs(long startTimestamp) =>
        (long)Stopwatch.GetElapsedTime(startTimestamp).TotalMilliseconds;
}

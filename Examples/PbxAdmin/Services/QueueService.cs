using PbxAdmin.Models;

namespace PbxAdmin.Services;

internal static partial class QueueServiceLog
{
    [LoggerMessage(Level = LogLevel.Information, Message = "[QUEUE] Deleted: server={ServerId} queue={QueueName}")]
    public static partial void Deleted(ILogger logger, string serverId, string queueName);

    [LoggerMessage(Level = LogLevel.Error, Message = "[QUEUE] Delete failed: server={ServerId} queue={QueueName}")]
    public static partial void DeleteFailed(ILogger logger, Exception exception, string serverId, string queueName);

    [LoggerMessage(Level = LogLevel.Warning, Message = "[QUEUE] Module reload failed after deleting queue {QueueName} on server {ServerId}")]
    public static partial void ReloadFailed(ILogger logger, string queueName, string serverId);

    [LoggerMessage(Level = LogLevel.Information, Message = "[QUEUE] Member removed: server={ServerId} queue={QueueName} interface={Interface}")]
    public static partial void MemberRemoved(ILogger logger, string serverId, string queueName, string @interface);

    [LoggerMessage(Level = LogLevel.Error, Message = "[QUEUE] Remove member failed: server={ServerId} queue={QueueName} interface={Interface}")]
    public static partial void RemoveMemberFailed(ILogger logger, Exception exception, string serverId, string queueName, string @interface);
}

/// <summary>
/// Service for managing Asterisk queue configuration (delete via config provider).
/// </summary>
public sealed class QueueService
{
    private readonly IConfigProviderResolver _resolver;
    private readonly PbxConfigManager _pbxConfig;
    private readonly AsteriskMonitorService _monitor;
    private readonly ILogger<QueueService> _logger;

    public QueueService(IConfigProviderResolver resolver, PbxConfigManager pbxConfig,
        AsteriskMonitorService monitor, ILogger<QueueService> logger)
    {
        _resolver = resolver;
        _pbxConfig = pbxConfig;
        _monitor = monitor;
        _logger = logger;
    }

    /// <summary>
    /// Deletes a queue and all its members from configuration, then reloads app_queue.
    /// </summary>
    public async Task<bool> DeleteQueueAsync(string serverId, string queueName, CancellationToken ct = default)
    {
        try
        {
            var provider = _resolver.GetProvider(serverId);

            var deleted = await provider.DeleteSectionAsync(serverId, "queues.conf", queueName, ct);
            if (!deleted)
                return false;

            if (!await provider.ReloadModuleAsync(serverId, "app_queue.so", ct))
                QueueServiceLog.ReloadFailed(_logger, queueName, serverId);

            // Remove from Live layer so UI updates immediately
            _monitor.GetServer(serverId)?.Server.Queues.RemoveQueue(queueName);

            QueueServiceLog.Deleted(_logger, serverId, queueName);
            return true;
        }
        catch (Exception ex)
        {
            QueueServiceLog.DeleteFailed(_logger, ex, serverId, queueName);
            return false;
        }
    }

    /// <summary>
    /// Removes a queue member by interface. Handles both File mode (rewrite queues.conf)
    /// and Realtime mode (DELETE from queue_members table).
    /// </summary>
    public async Task<(bool Success, string? Error)> RemoveMemberAsync(
        string serverId, string queueName, string iface, CancellationToken ct = default)
    {
        try
        {
            var mode = _resolver.GetConfigMode(serverId);
            bool success;

            if (mode == ConfigMode.File)
            {
                success = await RemoveFileMemberAsync(serverId, queueName, iface, ct);
            }
            else
            {
                var provider = _resolver.GetProvider(serverId);
                success = await ((DbConfigProvider)provider).RemoveQueueMemberAsync(queueName, iface, ct);
            }

            if (!success)
                return (false, $"Member '{iface}' not found in queue '{queueName}'");

            if (!await _resolver.GetProvider(serverId).ReloadModuleAsync(serverId, "app_queue.so", ct))
                QueueServiceLog.ReloadFailed(_logger, queueName, serverId);

            // Notify the in-memory QueueManager so the UI updates immediately
            _monitor.GetServer(serverId)?.Server.Queues.OnMemberRemoved(queueName, iface);

            QueueServiceLog.MemberRemoved(_logger, serverId, queueName, iface);
            return (true, null);
        }
        catch (Exception ex)
        {
            QueueServiceLog.RemoveMemberFailed(_logger, ex, serverId, queueName, iface);
            return (false, $"Failed to remove member: {ex.Message}");
        }
    }

    private async Task<bool> RemoveFileMemberAsync(
        string serverId, string queueName, string iface, CancellationToken ct)
    {
        var lines = await _pbxConfig.GetSectionLinesAsync(serverId, "queues.conf", queueName, ct);
        if (lines is null) return false;

        var filtered = lines.Where(kv =>
            !(string.Equals(kv.Key, "member", StringComparison.OrdinalIgnoreCase)
              && kv.Value.StartsWith(iface, StringComparison.OrdinalIgnoreCase))).ToList();

        if (filtered.Count == lines.Count)
            return false; // member not found

        return await _pbxConfig.CreateSectionWithLinesAsync(serverId, "queues.conf", queueName, filtered, ct);
    }
}

namespace PbxAdmin.Services;

internal static partial class QueueServiceLog
{
    [LoggerMessage(Level = LogLevel.Information, Message = "[QUEUE] Deleted: server={ServerId} queue={QueueName}")]
    public static partial void Deleted(ILogger logger, string serverId, string queueName);

    [LoggerMessage(Level = LogLevel.Error, Message = "[QUEUE] Delete failed: server={ServerId} queue={QueueName}")]
    public static partial void DeleteFailed(ILogger logger, Exception exception, string serverId, string queueName);

    [LoggerMessage(Level = LogLevel.Warning, Message = "[QUEUE] Module reload failed after deleting queue {QueueName} on server {ServerId}")]
    public static partial void ReloadFailed(ILogger logger, string queueName, string serverId);
}

/// <summary>
/// Service for managing Asterisk queue configuration (delete via config provider).
/// </summary>
public sealed class QueueService
{
    private readonly IConfigProviderResolver _resolver;
    private readonly ILogger<QueueService> _logger;

    public QueueService(IConfigProviderResolver resolver, ILogger<QueueService> logger)
    {
        _resolver = resolver;
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

            QueueServiceLog.Deleted(_logger, serverId, queueName);
            return true;
        }
        catch (Exception ex)
        {
            QueueServiceLog.DeleteFailed(_logger, ex, serverId, queueName);
            return false;
        }
    }
}

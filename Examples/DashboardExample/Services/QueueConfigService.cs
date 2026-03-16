using System.Text.RegularExpressions;
using DashboardExample.Models;
using DashboardExample.Services.Repositories;

namespace DashboardExample.Services;

internal static partial class QueueConfigServiceLog
{
    [LoggerMessage(Level = LogLevel.Information, Message = "[QUEUE-CFG] Created: server={ServerId} queue={QueueName}")]
    public static partial void Created(ILogger logger, string serverId, string queueName);

    [LoggerMessage(Level = LogLevel.Information, Message = "[QUEUE-CFG] Updated: server={ServerId} queue={QueueName}")]
    public static partial void Updated(ILogger logger, string serverId, string queueName);

    [LoggerMessage(Level = LogLevel.Information, Message = "[QUEUE-CFG] Deleted: server={ServerId} queueId={QueueId}")]
    public static partial void Deleted(ILogger logger, string serverId, int queueId);

    [LoggerMessage(Level = LogLevel.Information, Message = "[QUEUE-CFG] Member added: queue={QueueName} interface={Interface}")]
    public static partial void MemberAdded(ILogger logger, string queueName, string @interface);

    [LoggerMessage(Level = LogLevel.Information, Message = "[QUEUE-CFG] Member removed: memberId={MemberId}")]
    public static partial void MemberRemoved(ILogger logger, int memberId);

    [LoggerMessage(Level = LogLevel.Error, Message = "[QUEUE-CFG] Operation failed: server={ServerId}")]
    public static partial void OperationFailed(ILogger logger, Exception exception, string serverId);
}

public sealed partial class QueueConfigService
{
    private static readonly string[] ValidStrategies =
        ["ringall", "roundrobin", "leastrecent", "fewestcalls", "random", "rrmemory", "linear", "wrandom"];

    private static readonly string[] ValidJoinLeave = ["yes", "no", "strict", "loose"];
    private static readonly string[] ValidYesNoOnce = ["yes", "no", "once"];
    private static readonly string[] ValidYesNo = ["yes", "no"];

    [GeneratedRegex(@"^[a-zA-Z0-9_-]+$")]
    private static partial Regex ValidNameRegex();

    [GeneratedRegex(@"^[A-Za-z]+/.+$")]
    private static partial Regex ValidInterfaceRegex();

    private readonly IQueueConfigRepository _repo;
    private readonly IQueueViewManager _viewManager;
    private readonly IConfigProviderResolver _providerResolver;
    private readonly ILogger<QueueConfigService> _logger;

    public QueueConfigService(
        IQueueConfigRepository repo,
        IQueueViewManager viewManager,
        IConfigProviderResolver providerResolver,
        ILogger<QueueConfigService> logger)
    {
        _repo = repo;
        _viewManager = viewManager;
        _providerResolver = providerResolver;
        _logger = logger;
    }

    public Task<List<QueueConfigDto>> GetQueuesAsync(string serverId, CancellationToken ct = default)
        => _repo.GetQueuesAsync(serverId, ct);

    public Task<QueueConfigDto?> GetQueueAsync(int id, CancellationToken ct = default)
        => _repo.GetQueueAsync(id, ct);

    public async Task<(bool Success, string? Error)> CreateQueueAsync(QueueConfigDto config, CancellationToken ct = default)
    {
        var error = ValidateQueue(config);
        if (error is not null)
            return (false, error);

        var existing = await _repo.GetQueueByNameAsync(config.ServerId, config.Name, ct);
        if (existing is not null)
            return (false, $"Queue '{config.Name}' already exists on this server");

        var seenInterfaces = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var m in config.Members)
        {
            var memberError = ValidateMember(m);
            if (memberError is not null)
                return (false, memberError);
            if (!seenInterfaces.Add(m.Interface))
                return (false, $"Duplicate member interface '{m.Interface}'");
        }

        try
        {
            await _viewManager.EnsureViewsExistAsync(config.ServerId, ct);
            config.Id = await _repo.CreateQueueAsync(config, ct);
            await ReloadAsync(config.ServerId, ct);
            QueueConfigServiceLog.Created(_logger, config.ServerId, config.Name);
            return (true, null);
        }
        catch (Exception ex)
        {
            QueueConfigServiceLog.OperationFailed(_logger, ex, config.ServerId);
            return (false, $"Failed to create queue: {ex.Message}");
        }
    }

    public async Task<(bool Success, string? Error)> UpdateQueueAsync(QueueConfigDto config, CancellationToken ct = default)
    {
        var error = ValidateQueue(config);
        if (error is not null)
            return (false, error);

        var existing = await _repo.GetQueueByNameAsync(config.ServerId, config.Name, ct);
        if (existing is not null && existing.Id != config.Id)
            return (false, $"Queue '{config.Name}' already exists on this server");

        var seenInterfaces = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var m in config.Members)
        {
            var memberError = ValidateMember(m);
            if (memberError is not null)
                return (false, memberError);
            if (!seenInterfaces.Add(m.Interface))
                return (false, $"Duplicate member interface '{m.Interface}'");
        }

        try
        {
            await _viewManager.EnsureViewsExistAsync(config.ServerId, ct);
            var updated = await _repo.UpdateQueueAsync(config, ct);
            if (!updated)
                return (false, "Queue not found");

            await ReloadAsync(config.ServerId, ct);
            QueueConfigServiceLog.Updated(_logger, config.ServerId, config.Name);
            return (true, null);
        }
        catch (Exception ex)
        {
            QueueConfigServiceLog.OperationFailed(_logger, ex, config.ServerId);
            return (false, $"Failed to update queue: {ex.Message}");
        }
    }

    public async Task<(bool Success, string? Error)> DeleteQueueAsync(string serverId, int id, CancellationToken ct = default)
    {
        try
        {
            await _viewManager.EnsureViewsExistAsync(serverId, ct);
            var deleted = await _repo.DeleteQueueAsync(id, ct);
            if (!deleted)
                return (false, "Queue not found");

            await ReloadAsync(serverId, ct);
            QueueConfigServiceLog.Deleted(_logger, serverId, id);
            return (true, null);
        }
        catch (Exception ex)
        {
            QueueConfigServiceLog.OperationFailed(_logger, ex, serverId);
            return (false, $"Failed to delete queue: {ex.Message}");
        }
    }

    public Task<List<QueueMemberConfigDto>> GetMembersAsync(int queueConfigId, CancellationToken ct = default)
        => _repo.GetMembersAsync(queueConfigId, ct);

    public async Task<(bool Success, string? Error)> AddMemberAsync(
        string serverId, string queueName, QueueMemberConfigDto member, CancellationToken ct = default)
    {
        var error = ValidateMember(member);
        if (error is not null)
            return (false, error);

        try
        {
            member.Id = await _repo.AddMemberAsync(member, ct);
            await ReloadAsync(serverId, ct);
            QueueConfigServiceLog.MemberAdded(_logger, queueName, member.Interface);
            return (true, null);
        }
        catch (Exception ex)
        {
            QueueConfigServiceLog.OperationFailed(_logger, ex, serverId);
            return (false, $"Failed to add member: {ex.Message}");
        }
    }

    public async Task<(bool Success, string? Error)> UpdateMemberAsync(
        string serverId, QueueMemberConfigDto member, CancellationToken ct = default)
    {
        var error = ValidateMember(member);
        if (error is not null)
            return (false, error);

        try
        {
            var updated = await _repo.UpdateMemberAsync(member, ct);
            if (!updated)
                return (false, "Member not found");

            await ReloadAsync(serverId, ct);
            return (true, null);
        }
        catch (Exception ex)
        {
            QueueConfigServiceLog.OperationFailed(_logger, ex, serverId);
            return (false, $"Failed to update member: {ex.Message}");
        }
    }

    public async Task<(bool Success, string? Error)> RemoveMemberAsync(
        string serverId, int memberId, CancellationToken ct = default)
    {
        try
        {
            var removed = await _repo.RemoveMemberAsync(memberId, ct);
            if (!removed)
                return (false, "Member not found");

            await ReloadAsync(serverId, ct);
            QueueConfigServiceLog.MemberRemoved(_logger, memberId);
            return (true, null);
        }
        catch (Exception ex)
        {
            QueueConfigServiceLog.OperationFailed(_logger, ex, serverId);
            return (false, $"Failed to remove member: {ex.Message}");
        }
    }

    internal static string? ValidateQueue(QueueConfigDto config)
    {
        if (string.IsNullOrWhiteSpace(config.Name))
            return "Queue name is required";
        if (config.Name.Length > 128)
            return "Queue name must be 128 characters or less";
        if (!ValidNameRegex().IsMatch(config.Name))
            return "Queue name must contain only letters, numbers, hyphens, and underscores";
        if (string.IsNullOrWhiteSpace(config.ServerId))
            return "Server ID is required";
        if (!ValidStrategies.Contains(config.Strategy, StringComparer.OrdinalIgnoreCase))
            return $"Invalid strategy '{config.Strategy}'";
        if (config.Timeout < 1)
            return "Timeout must be at least 1 second";
        if (config.Retry < 0)
            return "Retry must be 0 or greater";
        if (config.MaxLen < 0)
            return "Max callers must be 0 or greater";
        if (config.WrapUpTime < 0)
            return "Wrap-up time must be 0 or greater";
        if (config.ServiceLevel < 0)
            return "Service level must be 0 or greater";
        if (config.Weight < 0)
            return "Weight must be 0 or greater";
        if (!ValidYesNo.Contains(config.RingInUse, StringComparer.OrdinalIgnoreCase))
            return $"Invalid ringinuse value '{config.RingInUse}'";
        if (!ValidJoinLeave.Contains(config.JoinEmpty, StringComparer.OrdinalIgnoreCase))
            return $"Invalid joinempty value '{config.JoinEmpty}'";
        if (!ValidJoinLeave.Contains(config.LeaveWhenEmpty, StringComparer.OrdinalIgnoreCase))
            return $"Invalid leavewhenempty value '{config.LeaveWhenEmpty}'";
        if (!ValidYesNoOnce.Contains(config.AnnounceHoldTime, StringComparer.OrdinalIgnoreCase))
            return $"Invalid announce_holdtime value '{config.AnnounceHoldTime}'";
        if (!ValidYesNoOnce.Contains(config.AnnouncePosition, StringComparer.OrdinalIgnoreCase))
            return $"Invalid announce_position value '{config.AnnouncePosition}'";
        if (config.AnnounceFrequency < 0)
            return "Announce frequency must be 0 or greater";
        if (config.PeriodicAnnounceFrequency < 0)
            return "Periodic announce frequency must be 0 or greater";

        return null;
    }

    internal static string? ValidateMember(QueueMemberConfigDto member)
    {
        if (string.IsNullOrWhiteSpace(member.Interface))
            return "Member interface is required";
        if (!ValidInterfaceRegex().IsMatch(member.Interface))
            return "Member interface must be in format Technology/resource (e.g. PJSIP/2001)";
        if (member.MemberName is { Length: > 128 })
            return "Member name must be 128 characters or less";
        if (member.Penalty < 0)
            return "Penalty must be 0 or greater";

        return null;
    }

    private async Task ReloadAsync(string serverId, CancellationToken ct)
    {
        var provider = _providerResolver.GetProvider(serverId);
        await provider.ReloadModuleAsync(serverId, "app_queue.so", ct);
    }
}

using System.Collections.Concurrent;
using Asterisk.Sdk.Ami.Actions;
using PbxAdmin.Models;
using PbxAdmin.Services.Repositories;

namespace PbxAdmin.Services;

public sealed partial class RecordingService
{
    private readonly IRecordingPolicyRepository _repo;
    private readonly IRecordingMohSchemaManager _schema;
    private readonly AsteriskMonitorService _monitor;
    private readonly AudioFileService _audioSvc;
    private readonly ILogger<RecordingService> _logger;

    private static readonly string[] ValidFormats = ["wav", "wav49", "gsm"];

    // Active recordings tracked via MixMonitorStart/Stop events
    private readonly ConcurrentDictionary<string, ActiveRecording> _activeRecordings = new();

    public RecordingService(
        IRecordingPolicyRepository repo,
        IRecordingMohSchemaManager schema,
        AsteriskMonitorService monitor,
        AudioFileService audioSvc,
        ILogger<RecordingService> logger)
    {
        _repo = repo;
        _schema = schema;
        _monitor = monitor;
        _audioSvc = audioSvc;
        _logger = logger;
    }

    public async Task<List<RecordingPolicy>> GetPoliciesAsync(string serverId, CancellationToken ct = default)
    {
        await _schema.EnsureSchemaAsync(ct);
        return await _repo.GetAllAsync(serverId, ct);
    }

    public async Task<RecordingPolicy?> GetPolicyAsync(int id, CancellationToken ct = default)
    {
        await _schema.EnsureSchemaAsync(ct);
        return await _repo.GetByIdAsync(id, ct);
    }

    public async Task<(bool Success, string? Error)> CreatePolicyAsync(
        string serverId, RecordingPolicy policy, CancellationToken ct = default)
    {
        var error = ValidatePolicy(policy);
        if (error is not null) return (false, error);

        await _schema.EnsureSchemaAsync(ct);

        var existing = await _repo.GetByNameAsync(serverId, policy.Name, ct);
        if (existing is not null) return (false, $"Policy '{policy.Name}' already exists");

        policy.ServerId = serverId;
        var id = await _repo.InsertAsync(policy, ct);
        PolicyCreated(_logger, id, policy.Name);
        return (true, null);
    }

    public async Task<(bool Success, string? Error)> UpdatePolicyAsync(
        string serverId, RecordingPolicy policy, CancellationToken ct = default)
    {
        var error = ValidatePolicy(policy);
        if (error is not null) return (false, error);

        await _schema.EnsureSchemaAsync(ct);

        var existing = await _repo.GetByNameAsync(serverId, policy.Name, ct);
        if (existing is not null && existing.Id != policy.Id)
            return (false, $"Policy '{policy.Name}' already exists");

        policy.ServerId = serverId;
        await _repo.UpdateAsync(policy, ct);
        PolicyUpdated(_logger, policy.Id);
        return (true, null);
    }

    public async Task<(bool Success, string? Error)> DeletePolicyAsync(int id, CancellationToken ct = default)
    {
        await _schema.EnsureSchemaAsync(ct);
        await _repo.DeleteAsync(id, ct);
        PolicyDeleted(_logger, id);
        return (true, null);
    }

    // --- Live recording control (AMI) ---

    public async Task<(bool Success, string? Error)> StartRecordingAsync(
        string serverId, string channel, int policyId, CancellationToken ct = default)
    {
        await _schema.EnsureSchemaAsync(ct);
        var policy = await _repo.GetByIdAsync(policyId, ct);
        if (policy is null) return (false, "Policy not found");

        var entry = _monitor.GetServer(serverId);
        if (entry is null) return (false, "No server connection");

        var timestamp = DateTime.UtcNow.ToString("yyyyMMdd-HHmmss", System.Globalization.CultureInfo.InvariantCulture);
        var filename = $"{policy.StoragePath}/{channel}-{timestamp}.{policy.Format}";
        var action = new MixMonitorAction
        {
            Channel = channel,
            File = filename,
            Options = policy.MixMonitorOptions ?? ""
        };

        var response = await entry.ConfigConnection.SendActionAsync(action, ct);
        if (response.Response != "Success")
            return (false, $"MixMonitor failed: {response.Message}");

        RecordingStarted(_logger, channel, filename);
        return (true, null);
    }

    public async Task<(bool Success, string? Error)> StopRecordingAsync(
        string serverId, string channel, CancellationToken ct = default)
    {
        var entry = _monitor.GetServer(serverId);
        if (entry is null) return (false, "No server connection");

        var action = new StopMixMonitorAction { Channel = channel };
        var response = await entry.ConfigConnection.SendActionAsync(action, ct);
        if (response.Response != "Success")
            return (false, $"StopMixMonitor failed: {response.Message}");

        RecordingStopped(_logger, channel);
        return (true, null);
    }

    // --- File browser ---

    public static bool IsFileAccessAvailable(string storagePath) => Directory.Exists(storagePath);

    public async Task<List<AudioFileInfo>> GetRecordingFilesAsync(
        string storagePath, string? filter = null, CancellationToken ct = default)
    {
        var files = await _audioSvc.GetFilesAsync(storagePath, ct);
        if (!string.IsNullOrWhiteSpace(filter))
            files = files.Where(f => f.Name.Contains(filter, StringComparison.OrdinalIgnoreCase)).ToList();
        return files;
    }

    public FileStream? GetRecordingStream(string storagePath, string filename)
        => _audioSvc.GetStream(storagePath, filename);

    public async Task<List<RetentionWarning>> GetRetentionWarningsAsync(
        string serverId, CancellationToken ct = default)
    {
        var policies = await GetPoliciesAsync(serverId, ct);
        var warnings = new List<RetentionWarning>();

        foreach (var policy in policies.Where(p => p.RetentionDays > 0))
        {
            var files = await _audioSvc.GetFilesAsync(policy.StoragePath, ct);
            var cutoff = DateTime.UtcNow.AddDays(-policy.RetentionDays);

            warnings.AddRange(files
                .Where(f => f.LastModified < cutoff)
                .Select(f => new RetentionWarning(
                    f.Name,
                    (int)(DateTime.UtcNow - f.LastModified).TotalDays,
                    policy.RetentionDays)));
        }

        return warnings;
    }

    // --- Active recordings (event-driven) ---

    public List<ActiveRecording> GetActiveRecordings() => [.. _activeRecordings.Values];

    public void OnMixMonitorStarted(string channel, string fileName)
    {
        _activeRecordings[channel] = new ActiveRecording
        {
            Channel = channel,
            FileName = fileName,
            StartedAt = DateTime.UtcNow
        };
    }

    public void OnMixMonitorStopped(string channel)
    {
        _activeRecordings.TryRemove(channel, out _);
    }

    // --- Validation ---

    private static string? ValidatePolicy(RecordingPolicy policy)
    {
        if (string.IsNullOrWhiteSpace(policy.Name))
            return "Name is required";
        if (!ValidFormats.Contains(policy.Format, StringComparer.OrdinalIgnoreCase))
            return $"Format must be one of: {string.Join(", ", ValidFormats)}";
        if (policy.RetentionDays < 0)
            return "Retention days must be >= 0";
        if (policy.Targets.Count == 0)
            return "At least one target is required";
        return null;
    }

    [LoggerMessage(Level = LogLevel.Information, Message = "Created recording policy {Id}: {Name}")]
    private static partial void PolicyCreated(ILogger logger, int id, string name);

    [LoggerMessage(Level = LogLevel.Information, Message = "Updated recording policy {Id}")]
    private static partial void PolicyUpdated(ILogger logger, int id);

    [LoggerMessage(Level = LogLevel.Information, Message = "Deleted recording policy {Id}")]
    private static partial void PolicyDeleted(ILogger logger, int id);

    [LoggerMessage(Level = LogLevel.Information, Message = "Started recording on {Channel}: {FileName}")]
    private static partial void RecordingStarted(ILogger logger, string channel, string fileName);

    [LoggerMessage(Level = LogLevel.Information, Message = "Stopped recording on {Channel}")]
    private static partial void RecordingStopped(ILogger logger, string channel);
}

public sealed record RetentionWarning(string FileName, int AgeDays, int PolicyRetentionDays);

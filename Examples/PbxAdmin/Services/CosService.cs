using System.Globalization;
using PbxAdmin.Models;
using PbxAdmin.Services.Repositories;

namespace PbxAdmin.Services;

internal static partial class CosServiceLog
{
    [LoggerMessage(Level = LogLevel.Debug, Message = "[COS] GetLevels: server={ServerId} count={Count}")]
    public static partial void GetLevels(ILogger logger, string serverId, int count);

    [LoggerMessage(Level = LogLevel.Information, Message = "[COS] Level created: id={Id} name={Name}")]
    public static partial void LevelCreated(ILogger logger, int id, string name);

    [LoggerMessage(Level = LogLevel.Information, Message = "[COS] Level updated: id={Id} name={Name}")]
    public static partial void LevelUpdated(ILogger logger, int id, string name);

    [LoggerMessage(Level = LogLevel.Information, Message = "[COS] Level deleted: server={ServerId} id={Id}")]
    public static partial void LevelDeleted(ILogger logger, string serverId, int id);

    [LoggerMessage(Level = LogLevel.Information, Message = "[COS] Assigned: server={ServerId} ext={Extension} cos={CosLevelName}")]
    public static partial void Assigned(ILogger logger, string serverId, string extension, string cosLevelName);

    [LoggerMessage(Level = LogLevel.Information, Message = "[COS] Override removed: server={ServerId} ext={Extension}")]
    public static partial void OverrideRemoved(ILogger logger, string serverId, string extension);

    [LoggerMessage(Level = LogLevel.Error, Message = "[COS] Operation failed: operation={Operation}")]
    public static partial void OperationFailed(ILogger logger, Exception exception, string operation);
}

/// <summary>
/// Service for managing Class-of-Service (COS) levels, extension assignments,
/// and dial simulation delegation.
/// </summary>
public sealed class CosService
{
    private readonly ICosRepositoryResolver _repoResolver;
    private readonly IConfiguration _config;
    private readonly ILogger<CosService> _logger;

    public CosService(
        ICosRepositoryResolver repoResolver,
        IConfiguration config,
        ILogger<CosService> logger)
    {
        _repoResolver = repoResolver;
        _config = config;
        _logger = logger;
    }

    // -----------------------------------------------------------------------
    // CRUD: Levels
    // -----------------------------------------------------------------------

    /// <summary>Gets all COS levels for a server.</summary>
    public async Task<List<CosLevel>> GetLevelsAsync(string serverId, CancellationToken ct = default)
    {
        var repo = _repoResolver.GetCosRepository(serverId);
        var levels = await repo.GetLevelsAsync(serverId, ct);
        CosServiceLog.GetLevels(_logger, serverId, levels.Count);
        return levels;
    }

    /// <summary>Gets a single COS level by id.</summary>
    public async Task<CosLevel?> GetLevelAsync(int id, string serverId, CancellationToken ct = default)
    {
        var repo = _repoResolver.GetCosRepository(serverId);
        return await repo.GetLevelAsync(id, ct);
    }

    /// <summary>Creates a COS level after validation.</summary>
    public async Task<(bool Success, string? Error)> CreateLevelAsync(CosLevel level, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(level.Name))
            return (false, "Name is required.");

        if (string.IsNullOrWhiteSpace(level.AsteriskContext))
            return (false, "Asterisk context is required.");

        var repo = _repoResolver.GetCosRepository(level.ServerId);

        var existing = await repo.GetLevelsAsync(level.ServerId, ct);
        if (existing.Any(l => string.Equals(l.Name, level.Name, StringComparison.OrdinalIgnoreCase)))
            return (false, "A COS level with this name already exists on the server.");

        var id = await repo.CreateLevelAsync(level, ct);

        await repo.AppendAuditLogAsync(new CosAuditEntry
        {
            ServerId = level.ServerId,
            EntityType = "level",
            EntityId = id.ToString(CultureInfo.InvariantCulture),
            Action = "created",
            NewValue = level.Name,
        }, ct);

        CosServiceLog.LevelCreated(_logger, id, level.Name);
        return (true, null);
    }

    /// <summary>Updates a COS level after validation.</summary>
    public async Task<(bool Success, string? Error)> UpdateLevelAsync(CosLevel level, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(level.Name))
            return (false, "Name is required.");

        if (string.IsNullOrWhiteSpace(level.AsteriskContext))
            return (false, "Asterisk context is required.");

        var repo = _repoResolver.GetCosRepository(level.ServerId);
        var success = await repo.UpdateLevelAsync(level, ct);
        if (!success) return (false, "COS level not found.");

        await repo.AppendAuditLogAsync(new CosAuditEntry
        {
            ServerId = level.ServerId,
            EntityType = "level",
            EntityId = level.Id.ToString(CultureInfo.InvariantCulture),
            Action = "updated",
            NewValue = level.Name,
        }, ct);

        CosServiceLog.LevelUpdated(_logger, level.Id, level.Name);
        return (true, null);
    }

    /// <summary>
    /// Deletes a COS level if no extensions are currently assigned to it.
    /// </summary>
    public async Task<(bool Success, string? Error)> DeleteLevelAsync(int id, string serverId, CancellationToken ct = default)
    {
        var repo = _repoResolver.GetCosRepository(serverId);

        var overrides = await repo.GetExtensionOverridesAsync(serverId, ct);
        if (overrides.Any(o => o.CosLevelId == id))
            return (false, "Cannot delete: extensions are still assigned to this COS level.");

        var success = await repo.DeleteLevelAsync(id, ct);
        if (!success) return (false, "COS level not found.");

        await repo.AppendAuditLogAsync(new CosAuditEntry
        {
            ServerId = serverId,
            EntityType = "level",
            EntityId = id.ToString(CultureInfo.InvariantCulture),
            Action = "deleted",
        }, ct);

        CosServiceLog.LevelDeleted(_logger, serverId, id);
        return (true, null);
    }

    // -----------------------------------------------------------------------
    // Assignment
    // -----------------------------------------------------------------------

    /// <summary>
    /// Assigns a COS level to a single extension by writing a per-extension override.
    /// AstDB flag writing will be integrated in Task 9.
    /// </summary>
    public async Task<(bool Success, string? Error)> AssignToExtensionAsync(
        string serverId, string extension, int cosLevelId, CancellationToken ct = default)
    {
        var repo = _repoResolver.GetCosRepository(serverId);
        var level = await repo.GetLevelAsync(cosLevelId, ct);
        if (level is null) return (false, "COS level not found.");

        var cosOverride = new CosExtensionOverride
        {
            ServerId = serverId,
            Extension = extension,
            CosLevelId = cosLevelId,
        };
        await repo.UpsertExtensionOverrideAsync(cosOverride, ct);

        await repo.AppendAuditLogAsync(new CosAuditEntry
        {
            ServerId = serverId,
            EntityType = "extension",
            EntityId = extension,
            Action = "assigned",
            NewValue = level.Name,
        }, ct);

        CosServiceLog.Assigned(_logger, serverId, extension, level.Name);
        return (true, null);
    }

    /// <summary>
    /// Assigns a COS level to multiple extensions in sequence, reporting progress.
    /// </summary>
    public async Task<(int Succeeded, int Failed, List<string> Errors)> BulkAssignAsync(
        string serverId, string[] extensions, int cosLevelId,
        Action<int>? progressCallback = null, CancellationToken ct = default)
    {
        var repo = _repoResolver.GetCosRepository(serverId);
        var level = await repo.GetLevelAsync(cosLevelId, ct);
        if (level is null)
            return (0, 0, ["COS level not found."]);

        var succeeded = 0;
        var failed = 0;
        var errors = new List<string>();

        for (var i = 0; i < extensions.Length; i++)
        {
            ct.ThrowIfCancellationRequested();
            var ext = extensions[i];

            try
            {
                var cosOverride = new CosExtensionOverride
                {
                    ServerId = serverId,
                    Extension = ext,
                    CosLevelId = cosLevelId,
                };
                await repo.UpsertExtensionOverrideAsync(cosOverride, ct);

                await repo.AppendAuditLogAsync(new CosAuditEntry
                {
                    ServerId = serverId,
                    EntityType = "extension",
                    EntityId = ext,
                    Action = "assigned",
                    NewValue = level.Name,
                }, ct);

                succeeded++;
            }
            catch (Exception ex)
            {
                failed++;
                errors.Add($"Extension {ext}: {ex.Message}");
                CosServiceLog.OperationFailed(_logger, ex, $"BulkAssign({ext})");
            }

            progressCallback?.Invoke(i + 1);
        }

        return (succeeded, failed, errors);
    }

    // -----------------------------------------------------------------------
    // COS resolution (override vs default hierarchy)
    // -----------------------------------------------------------------------

    /// <summary>
    /// Resolves the effective COS level for an extension.
    /// Priority: per-extension override, then system default from configuration.
    /// </summary>
    public async Task<CosLevel?> GetExtensionCosAsync(
        string serverId, string extension, CancellationToken ct = default)
    {
        var repo = _repoResolver.GetCosRepository(serverId);

        // 1. Check per-extension override
        var extOverride = await repo.GetExtensionOverrideAsync(serverId, extension, ct);
        if (extOverride?.CosLevelId is not null)
            return await repo.GetLevelAsync(extOverride.CosLevelId.Value, ct);

        // 2. Fall back to system default
        var defaultCosId = GetDefaultCosLevelId(serverId);
        if (defaultCosId is not null)
            return await repo.GetLevelAsync(defaultCosId.Value, ct);

        return null;
    }

    // -----------------------------------------------------------------------
    // Extension overrides
    // -----------------------------------------------------------------------

    /// <summary>Gets all extension overrides for a server.</summary>
    public async Task<List<CosExtensionOverride>> GetExtensionOverridesAsync(
        string serverId, CancellationToken ct = default)
    {
        var repo = _repoResolver.GetCosRepository(serverId);
        return await repo.GetExtensionOverridesAsync(serverId, ct);
    }

    /// <summary>Removes a per-extension COS override.</summary>
    public async Task<(bool Success, string? Error)> DeleteExtensionOverrideAsync(
        string serverId, string extension, CancellationToken ct = default)
    {
        var repo = _repoResolver.GetCosRepository(serverId);
        var success = await repo.DeleteExtensionOverrideAsync(serverId, extension, ct);
        if (!success) return (false, "Extension override not found.");

        await repo.AppendAuditLogAsync(new CosAuditEntry
        {
            ServerId = serverId,
            EntityType = "extension",
            EntityId = extension,
            Action = "override-removed",
        }, ct);

        CosServiceLog.OverrideRemoved(_logger, serverId, extension);
        return (true, null);
    }

    // -----------------------------------------------------------------------
    // Dial simulation (stub — will delegate to DialSimulator in Task 6)
    // -----------------------------------------------------------------------

    /// <summary>
    /// Simulates dialing a number from an extension. Currently a stub that returns an error;
    /// will be connected to <c>DialSimulator</c> in Task 6.
    /// </summary>
    public Task<DialSimulatorResult> SimulateDialAsync(
        string serverId, string extension, string dialedNumber, CancellationToken ct = default)
    {
        return Task.FromResult(new DialSimulatorResult(
            DialSimulatorVerdict.Error,
            MatchingRuleName: null,
            MatchingPattern: null,
            MatchingPatternGroup: null,
            FlagChecks: [],
            TrunkName: null,
            EffectiveDialString: null,
            ResolvedCosName: null,
            TimeOverrideApplied: null,
            ErrorMessage: "Dial simulator not yet initialized.",
            TraceLog: []));
    }

    // -----------------------------------------------------------------------
    // Configuration helpers
    // -----------------------------------------------------------------------

    private int? GetDefaultCosLevelId(string serverId)
    {
        var servers = _config.GetSection("Asterisk:Servers").GetChildren();
        foreach (var section in servers)
        {
            if (string.Equals(section["Id"], serverId, StringComparison.OrdinalIgnoreCase))
            {
                var val = section["DefaultCosLevelId"];
                if (int.TryParse(val, out var id)) return id;
            }
        }

        return null;
    }
}

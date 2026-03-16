using Asterisk.Sdk;
using Asterisk.Sdk.Ami.Actions;
using Asterisk.Sdk.Ami.Events;
using Asterisk.Sdk.Ami.Responses;

namespace DashboardExample.Services.Helpers;

internal sealed record DeviceFeatures(bool Dnd, string? CfUnconditional, string? CfBusy, string? CfNoAnswer, int CfnaTimeout)
{
    public static readonly DeviceFeatures Empty = new(false, null, null, null, 20);
}

internal static partial class DeviceFeatureLog
{
    [LoggerMessage(Level = LogLevel.Debug, Message = "[DEVICE] Get features: server={ServerId} ext={Extension}")]
    public static partial void GetFeatures(ILogger logger, string serverId, string extension);

    [LoggerMessage(Level = LogLevel.Debug, Message = "[DEVICE] Set features: server={ServerId} ext={Extension}")]
    public static partial void SetFeatures(ILogger logger, string serverId, string extension);

    [LoggerMessage(Level = LogLevel.Debug, Message = "[DEVICE] Cleanup features: server={ServerId} ext={Extension}")]
    public static partial void Cleanup(ILogger logger, string serverId, string extension);

    [LoggerMessage(Level = LogLevel.Warning, Message = "[DEVICE] Operation failed: server={ServerId} ext={Extension}")]
    public static partial void OperationFailed(ILogger logger, Exception exception, string serverId, string extension);
}

internal sealed class DeviceFeatureHelper
{
    private readonly AsteriskMonitorService _monitor;
    private readonly ILogger<DeviceFeatureHelper> _logger;

    public DeviceFeatureHelper(AsteriskMonitorService monitor, ILogger<DeviceFeatureHelper> logger)
    {
        _monitor = monitor;
        _logger = logger;
    }

    /// <summary>
    /// Gets device features for a single extension via typed DbGet actions.
    /// </summary>
    public async Task<DeviceFeatures> GetAsync(string serverId, string extension, CancellationToken ct = default)
    {
        DeviceFeatureLog.GetFeatures(_logger, serverId, extension);

        var conn = GetConfigConnection(serverId);

        var cfUnconditional = await DbGetAsync(conn, "CF", extension, ct);
        var cfBusy = await DbGetAsync(conn, "CFB", extension, ct);
        var cfNoAnswer = await DbGetAsync(conn, "CFNA", extension, ct);
        var dndValue = await DbGetAsync(conn, "DND", extension, ct);
        var dnd = string.Equals(dndValue, "YES", StringComparison.OrdinalIgnoreCase);

        return new DeviceFeatures(dnd, cfUnconditional, cfBusy, cfNoAnswer, 20);
    }

    /// <summary>
    /// Sets device features for an extension. Writes or deletes AstDB entries as needed.
    /// </summary>
    public async Task SetAsync(string serverId, string extension, DeviceFeatures features, CancellationToken ct = default)
    {
        DeviceFeatureLog.SetFeatures(_logger, serverId, extension);

        var conn = GetConfigConnection(serverId);

        await SetOrDeleteAsync(conn, "CF", extension, features.CfUnconditional, ct);
        await SetOrDeleteAsync(conn, "CFB", extension, features.CfBusy, ct);
        await SetOrDeleteAsync(conn, "CFNA", extension, features.CfNoAnswer, ct);
        await SetOrDeleteAsync(conn, "DND", extension, features.Dnd ? "YES" : null, ct);
    }

    /// <summary>
    /// Deletes all feature entries for an extension (best-effort, for extension deletion).
    /// </summary>
    public async Task CleanupAsync(string serverId, string extension, CancellationToken ct = default)
    {
        DeviceFeatureLog.Cleanup(_logger, serverId, extension);

        var conn = GetConfigConnection(serverId);

        string[] families = ["CF", "CFB", "CFNA", "DND"];
        foreach (var family in families)
        {
            try
            {
                await conn.SendActionAsync(new DbDelAction { Family = family, Key = extension }, ct);
            }
            catch (Exception ex)
            {
                DeviceFeatureLog.OperationFailed(_logger, ex, serverId, extension);
            }
        }
    }

    /// <summary>
    /// Bulk reads feature entries for multiple extensions via CLI "database show" commands.
    /// More efficient than individual DbGet for large extension lists.
    /// </summary>
    public async Task<Dictionary<string, DeviceFeatures>> GetBatchAsync(
        string serverId, IReadOnlyCollection<string> extensions, CancellationToken ct = default)
    {
        var conn = GetConfigConnection(serverId);

        var cfMap = await GetFamilyMapAsync(conn, "CF", ct);
        var cfbMap = await GetFamilyMapAsync(conn, "CFB", ct);
        var cfnaMap = await GetFamilyMapAsync(conn, "CFNA", ct);
        var dndMap = await GetFamilyMapAsync(conn, "DND", ct);

        var result = new Dictionary<string, DeviceFeatures>(extensions.Count);

        foreach (var ext in extensions)
        {
            var dnd = dndMap.TryGetValue(ext, out var dndVal)
                && string.Equals(dndVal, "YES", StringComparison.OrdinalIgnoreCase);
            cfMap.TryGetValue(ext, out var cf);
            cfbMap.TryGetValue(ext, out var cfb);
            cfnaMap.TryGetValue(ext, out var cfna);

            if (dnd || cf is not null || cfb is not null || cfna is not null)
                result[ext] = new DeviceFeatures(dnd, cf, cfb, cfna, 20);
        }

        return result;
    }

    /// <summary>
    /// Parses Asterisk CLI "database show" output into a dictionary of key/value pairs.
    /// Format: "/Family/Key                          : Value"
    /// </summary>
    public static Dictionary<string, string> ParseDatabaseShowOutput(string? output)
    {
        var result = new Dictionary<string, string>();

        if (string.IsNullOrEmpty(output))
            return result;

        foreach (var line in output.AsSpan().EnumerateLines())
        {
            var trimmed = line.Trim();
            if (trimmed.IsEmpty || trimmed.EndsWith("results found."))
                continue;

            // Format: /Family/Key<spaces>: Value
            var colonIdx = trimmed.IndexOf(':');
            if (colonIdx < 0)
                continue;

            var keyPart = trimmed[..colonIdx].Trim();
            var valuePart = trimmed[(colonIdx + 1)..].Trim();

            // Extract key from "/Family/Key" — take the last segment
            var lastSlash = keyPart.LastIndexOf('/');
            if (lastSlash < 0)
                continue;

            var key = keyPart[(lastSlash + 1)..].Trim();
            if (key.IsEmpty)
                continue;

            result[key.ToString()] = valuePart.ToString();
        }

        return result;
    }

    private IAmiConnection GetConfigConnection(string serverId)
    {
        var entry = _monitor.GetServer(serverId)
            ?? throw new InvalidOperationException($"Server '{serverId}' not found.");
        return entry.ConfigConnection;
    }

    private static async Task<string?> DbGetAsync(IAmiConnection conn, string family, string key, CancellationToken ct)
    {
        try
        {
            var action = new DbGetAction { Family = family, Key = key };
            await foreach (var evt in conn.SendEventGeneratingActionAsync(action, ct))
            {
                if (evt is DbGetResponseEvent dbEvt)
                    return dbEvt.Val;
            }
        }
        catch
        {
            // Key not found returns error response — treat as null
        }

        return null;
    }

    private static async Task SetOrDeleteAsync(
        IAmiConnection conn, string family, string key, string? value, CancellationToken ct)
    {
        if (value is not null)
        {
            await conn.SendActionAsync(new DbPutAction { Family = family, Key = key, Val = value }, ct);
        }
        else
        {
            try
            {
                await conn.SendActionAsync(new DbDelAction { Family = family, Key = key }, ct);
            }
            catch
            {
                // Key may not exist — ignore delete failures
            }
        }
    }

    private static async Task<Dictionary<string, string>> GetFamilyMapAsync(
        IAmiConnection conn, string family, CancellationToken ct)
    {
        try
        {
            var response = await conn.SendActionAsync<CommandResponse>(
                new CommandAction { Command = $"database show {family}" }, ct);

            return ParseDatabaseShowOutput(response.Output);
        }
        catch
        {
            return [];
        }
    }
}

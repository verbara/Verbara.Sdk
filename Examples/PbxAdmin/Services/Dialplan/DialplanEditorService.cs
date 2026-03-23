using Dapper;
using Npgsql;
using PbxAdmin.Models;

namespace PbxAdmin.Services.Dialplan;

internal static partial class DialplanEditorLog
{
    [LoggerMessage(Level = LogLevel.Information, Message = "[DP_EDITOR] Extension added: server={ServerId} context={Context} exten={Exten} priority={Priority}")]
    public static partial void ExtensionAdded(ILogger logger, string serverId, string context, string exten, int priority);

    [LoggerMessage(Level = LogLevel.Information, Message = "[DP_EDITOR] Extension removed: server={ServerId} context={Context} exten={Exten}")]
    public static partial void ExtensionRemoved(ILogger logger, string serverId, string context, string exten);

    [LoggerMessage(Level = LogLevel.Information, Message = "[DP_EDITOR] Include added: server={ServerId} parent={ParentContext} include={IncludeContext}")]
    public static partial void IncludeAdded(ILogger logger, string serverId, string parentContext, string includeContext);

    [LoggerMessage(Level = LogLevel.Information, Message = "[DP_EDITOR] Include removed: server={ServerId} parent={ParentContext} include={IncludeContext}")]
    public static partial void IncludeRemoved(ILogger logger, string serverId, string parentContext, string includeContext);

    [LoggerMessage(Level = LogLevel.Information, Message = "[DP_EDITOR] Context created: server={ServerId} context={Context}")]
    public static partial void ContextCreated(ILogger logger, string serverId, string context);

    [LoggerMessage(Level = LogLevel.Information, Message = "[DP_EDITOR] Context removed: server={ServerId} context={Context}")]
    public static partial void ContextRemoved(ILogger logger, string serverId, string context);

    [LoggerMessage(Level = LogLevel.Error, Message = "[DP_EDITOR] Operation failed: server={ServerId} operation={Operation}")]
    public static partial void OperationFailed(ILogger logger, Exception exception, string serverId, string operation);
}

public sealed class DialplanEditorService
{
    private readonly PbxConfigManager _configManager;
    private readonly DialplanDiscoveryService _discovery;
    private readonly IConfiguration _config;
    private readonly ILogger<DialplanEditorService> _logger;

    public DialplanEditorService(
        PbxConfigManager configManager,
        DialplanDiscoveryService discovery,
        IConfiguration config,
        ILogger<DialplanEditorService> logger)
    {
        _configManager = configManager;
        _discovery = discovery;
        _config = config;
        _logger = logger;
    }

    public async Task<(bool Success, string? Error)> AddExtensionAsync(
        string serverId, string context, string exten, int priority,
        string app, string appData, CancellationToken ct = default)
    {
        try
        {
            if (IsRealtimeMode(serverId))
            {
                var connStr = GetConnectionString(serverId);
                if (connStr is null)
                    return (false, "No connection string configured for Realtime mode.");

                await using var conn = new NpgsqlConnection(connStr);
                await conn.OpenAsync(ct);

                const string sql = """
                    INSERT INTO extensions (context, exten, priority, app, appdata)
                    VALUES (@Context, @Exten, @Priority, @App, @AppData)
                    """;

                await conn.ExecuteAsync(new CommandDefinition(
                    sql,
                    new { Context = context, Exten = exten, Priority = priority, App = app, AppData = appData },
                    cancellationToken: ct));

                await _configManager.ExecuteCommandAsync(serverId, "dialplan reload", ct);
            }
            else
            {
                var cmd = BuildAddExtensionCommand(context, exten, priority, app, appData);
                var result = await _configManager.ExecuteCommandAsync(serverId, cmd, ct);
                if (result is null)
                    return (false, "AMI command failed: no response.");

                await _configManager.ExecuteCommandAsync(serverId, "dialplan save", ct);
            }

            await _discovery.RefreshAsync(serverId, ct);
            DialplanEditorLog.ExtensionAdded(_logger, serverId, context, exten, priority);
            return (true, null);
        }
        catch (Exception ex)
        {
            DialplanEditorLog.OperationFailed(_logger, ex, serverId, "AddExtension");
            return (false, ex.Message);
        }
    }

    public async Task<(bool Success, string? Error)> RemoveExtensionAsync(
        string serverId, string context, string exten, CancellationToken ct = default)
    {
        try
        {
            if (IsRealtimeMode(serverId))
            {
                var connStr = GetConnectionString(serverId);
                if (connStr is null)
                    return (false, "No connection string configured for Realtime mode.");

                await using var conn = new NpgsqlConnection(connStr);
                await conn.OpenAsync(ct);

                const string sql = "DELETE FROM extensions WHERE context = @Context AND exten = @Exten";

                await conn.ExecuteAsync(new CommandDefinition(
                    sql,
                    new { Context = context, Exten = exten },
                    cancellationToken: ct));

                await _configManager.ExecuteCommandAsync(serverId, "dialplan reload", ct);
            }
            else
            {
                var cmd = BuildRemoveExtensionCommand(context, exten);
                var result = await _configManager.ExecuteCommandAsync(serverId, cmd, ct);
                if (result is null)
                    return (false, "AMI command failed: no response.");

                await _configManager.ExecuteCommandAsync(serverId, "dialplan save", ct);
            }

            await _discovery.RefreshAsync(serverId, ct);
            DialplanEditorLog.ExtensionRemoved(_logger, serverId, context, exten);
            return (true, null);
        }
        catch (Exception ex)
        {
            DialplanEditorLog.OperationFailed(_logger, ex, serverId, "RemoveExtension");
            return (false, ex.Message);
        }
    }

    public async Task<(bool Success, string? Error)> AddIncludeAsync(
        string serverId, string parentContext, string includeContext, CancellationToken ct = default)
    {
        try
        {
            if (IsRealtimeMode(serverId))
                return (false, "Include management is not supported in Realtime mode. Includes must be configured in extensions.conf.");

            // Check for circular includes
            var snapshot = await _discovery.GetSnapshotAsync(serverId, ct);
            if (snapshot is not null && HasCircularInclude(snapshot.Contexts, parentContext, includeContext))
                return (false, $"Adding include '{includeContext}' into '{parentContext}' would create a circular dependency.");

            var cmd = BuildAddIncludeCommand(parentContext, includeContext);
            var result = await _configManager.ExecuteCommandAsync(serverId, cmd, ct);
            if (result is null)
                return (false, "AMI command failed: no response.");

            await _configManager.ExecuteCommandAsync(serverId, "dialplan save", ct);
            await _discovery.RefreshAsync(serverId, ct);
            DialplanEditorLog.IncludeAdded(_logger, serverId, parentContext, includeContext);
            return (true, null);
        }
        catch (Exception ex)
        {
            DialplanEditorLog.OperationFailed(_logger, ex, serverId, "AddInclude");
            return (false, ex.Message);
        }
    }

    public async Task<(bool Success, string? Error)> RemoveIncludeAsync(
        string serverId, string parentContext, string includeContext, CancellationToken ct = default)
    {
        try
        {
            if (IsRealtimeMode(serverId))
                return (false, "Include management is not supported in Realtime mode. Includes must be configured in extensions.conf.");

            var cmd = BuildRemoveIncludeCommand(parentContext, includeContext);
            var result = await _configManager.ExecuteCommandAsync(serverId, cmd, ct);
            if (result is null)
                return (false, "AMI command failed: no response.");

            await _configManager.ExecuteCommandAsync(serverId, "dialplan save", ct);
            await _discovery.RefreshAsync(serverId, ct);
            DialplanEditorLog.IncludeRemoved(_logger, serverId, parentContext, includeContext);
            return (true, null);
        }
        catch (Exception ex)
        {
            DialplanEditorLog.OperationFailed(_logger, ex, serverId, "RemoveInclude");
            return (false, ex.Message);
        }
    }

    public Task<(bool Success, string? Error)> CreateContextAsync(
        string serverId, string contextName, CancellationToken ct = default)
    {
        DialplanEditorLog.ContextCreated(_logger, serverId, contextName);
        return AddExtensionAsync(serverId, contextName, "s", 1, "NoOp", "placeholder", ct);
    }

    public async Task<(bool Success, string? Error)> RemoveContextAsync(
        string serverId, string contextName, CancellationToken ct = default)
    {
        try
        {
            if (IsRealtimeMode(serverId))
            {
                var connStr = GetConnectionString(serverId);
                if (connStr is null)
                    return (false, "No connection string configured for Realtime mode.");

                await using var conn = new NpgsqlConnection(connStr);
                await conn.OpenAsync(ct);

                const string sql = "DELETE FROM extensions WHERE context = @Context";

                await conn.ExecuteAsync(new CommandDefinition(
                    sql,
                    new { Context = contextName },
                    cancellationToken: ct));

                await _configManager.ExecuteCommandAsync(serverId, "dialplan reload", ct);
            }
            else
            {
                var cmd = BuildRemoveContextCommand(contextName);
                var result = await _configManager.ExecuteCommandAsync(serverId, cmd, ct);
                if (result is null)
                    return (false, "AMI command failed: no response.");

                await _configManager.ExecuteCommandAsync(serverId, "dialplan save", ct);
            }

            await _discovery.RefreshAsync(serverId, ct);
            DialplanEditorLog.ContextRemoved(_logger, serverId, contextName);
            return (true, null);
        }
        catch (Exception ex)
        {
            DialplanEditorLog.OperationFailed(_logger, ex, serverId, "RemoveContext");
            return (false, ex.Message);
        }
    }

    // ── Internal static helpers (testable) ──

    internal static string BuildAddExtensionCommand(string context, string exten, int priority, string app, string appData) =>
        $"dialplan add extension {exten},{priority},{app}({appData}) into {context}";

    internal static string BuildRemoveExtensionCommand(string context, string exten) =>
        $"dialplan remove extension {exten}@{context}";

    internal static string BuildAddIncludeCommand(string parentContext, string includeContext) =>
        $"dialplan add include {includeContext} into {parentContext}";

    internal static string BuildRemoveIncludeCommand(string parentContext, string includeContext) =>
        $"dialplan remove include {includeContext} from {parentContext}";

    internal static string BuildRemoveContextCommand(string contextName) =>
        $"dialplan remove context {contextName}";

    /// <summary>
    /// Detects whether adding an include from <paramref name="parentContext"/> to
    /// <paramref name="newInclude"/> would create a circular dependency.
    /// Uses iterative BFS from <paramref name="newInclude"/> through the existing
    /// include graph to see if <paramref name="parentContext"/> is reachable.
    /// </summary>
    internal static bool HasCircularInclude(
        IReadOnlyList<DiscoveredContext> contexts, string parentContext, string newInclude)
    {
        // Self-reference is always circular
        if (string.Equals(parentContext, newInclude, StringComparison.Ordinal))
            return true;

        // Build a lookup for quick context resolution
        var contextMap = new Dictionary<string, DiscoveredContext>(StringComparer.Ordinal);
        foreach (var ctx in contexts)
            contextMap[ctx.Name] = ctx;

        // BFS: starting from newInclude, can we reach parentContext via existing includes?
        var visited = new HashSet<string>(StringComparer.Ordinal);
        var queue = new Queue<string>();
        queue.Enqueue(newInclude);

        while (queue.Count > 0)
        {
            var current = queue.Dequeue();
            if (!visited.Add(current))
                continue;

            if (!contextMap.TryGetValue(current, out var ctx))
                continue;

            foreach (var include in ctx.Includes)
            {
                if (string.Equals(include, parentContext, StringComparison.Ordinal))
                    return true;

                queue.Enqueue(include);
            }
        }

        return false;
    }

    // ── Private helpers ──

    private bool IsRealtimeMode(string serverId)
    {
        var servers = _config.GetSection("Asterisk:Servers").GetChildren();
        foreach (var section in servers)
        {
            var id = section["Id"] ?? "default";
            if (!string.Equals(id, serverId, StringComparison.OrdinalIgnoreCase))
                continue;

            var mode = section["ConfigMode"] ?? "File";
            return string.Equals(mode, "Realtime", StringComparison.OrdinalIgnoreCase);
        }

        return false;
    }

    private string? GetConnectionString(string serverId)
    {
        var servers = _config.GetSection("Asterisk:Servers").GetChildren();
        foreach (var section in servers)
        {
            var id = section["Id"] ?? "default";
            if (!string.Equals(id, serverId, StringComparison.OrdinalIgnoreCase))
                continue;

            return section["RealtimeConnectionString"];
        }

        return null;
    }
}

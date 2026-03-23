using Asterisk.Sdk.Ami.Actions;
using Asterisk.Sdk.Ami.Events;
using PbxAdmin.Models;

namespace PbxAdmin.Services.Dialplan;

internal static partial class DialplanDiscoveryLog
{
    [LoggerMessage(Level = LogLevel.Debug, Message = "[DP_DISCOVERY] Refreshed: server={ServerId} contexts={ContextCount} extensions={ExtensionCount}")]
    public static partial void Refreshed(ILogger logger, string serverId, int contextCount, int extensionCount);

    [LoggerMessage(Level = LogLevel.Warning, Message = "[DP_DISCOVERY] Refresh failed: server={ServerId}")]
    public static partial void RefreshFailed(ILogger logger, Exception exception, string serverId);

    [LoggerMessage(Level = LogLevel.Debug, Message = "[DP_DISCOVERY] Cache hit: server={ServerId} age={AgeSeconds}s")]
    public static partial void CacheHit(ILogger logger, string serverId, int ageSeconds);
}

public sealed class DialplanDiscoveryService : IDisposable
{
    private static readonly TimeSpan CacheTtl = TimeSpan.FromMinutes(5);

    private static readonly HashSet<string> UserRegistrars = new(StringComparer.OrdinalIgnoreCase)
    {
        "pbx_config", "pbx_realtime", "pbx_lua", "pbx_ael"
    };

    private static readonly HashSet<string> SystemContextNames = new(StringComparer.OrdinalIgnoreCase)
    {
        "parkedcalls", "default-hints", "adhoc-conference",
        "findmefollow-ringallv2", "ext-queues", "macro-dial",
        "macro-hangupcall", "macro-exten-vm", "macro-vm",
        "from-pstn", "from-sip-external"
    };

    private volatile Dictionary<string, DialplanSnapshot> _snapshots = new();
    private readonly AsteriskMonitorService _monitor;
    private readonly ILogger<DialplanDiscoveryService> _logger;
    private Timer? _refreshTimer;

    public DialplanDiscoveryService(
        AsteriskMonitorService monitor,
        ILogger<DialplanDiscoveryService> logger)
    {
        _monitor = monitor;
        _logger = logger;
        _refreshTimer = new Timer(RefreshAllCallback, null, CacheTtl, CacheTtl);
    }

    /// <summary>
    /// Refreshes the dialplan snapshot for a specific server by sending a ShowDialplan AMI action
    /// and collecting the ListDialplanEvent responses.
    /// </summary>
    public async Task RefreshAsync(string serverId, CancellationToken ct = default)
    {
        try
        {
            var entry = _monitor.GetServer(serverId);
            if (entry is null) return;

            var events = new List<ListDialplanEvent>();
            await foreach (var evt in entry.ConfigConnection.SendEventGeneratingActionAsync(new ShowDialplanAction(), ct))
            {
                if (evt is ListDialplanEvent dialplanEvent)
                    events.Add(dialplanEvent);
            }

            var snapshot = BuildSnapshot(serverId, events);

            var dict = new Dictionary<string, DialplanSnapshot>(_snapshots) { [serverId] = snapshot };
            _snapshots = dict;

            var extensionCount = snapshot.Contexts.Sum(c => c.Extensions.Count);
            DialplanDiscoveryLog.Refreshed(_logger, serverId,
                snapshot.Contexts.Count, extensionCount);
        }
        catch (Exception ex)
        {
            DialplanDiscoveryLog.RefreshFailed(_logger, ex, serverId);
        }
    }

    /// <summary>
    /// Gets the cached snapshot for a server. Returns null if no snapshot is available.
    /// Automatically triggers a refresh if the cache is stale.
    /// </summary>
    public async Task<DialplanSnapshot?> GetSnapshotAsync(string serverId, CancellationToken ct = default)
    {
        if (_snapshots.TryGetValue(serverId, out var snapshot))
        {
            var age = DateTime.UtcNow - snapshot.RefreshedAt;
            if (age < CacheTtl)
            {
                DialplanDiscoveryLog.CacheHit(_logger, serverId, (int)age.TotalSeconds);
                return snapshot;
            }
        }

        await RefreshAsync(serverId, ct);
        _snapshots.TryGetValue(serverId, out snapshot);
        return snapshot;
    }

    /// <summary>
    /// Builds a DialplanSnapshot from raw ListDialplanEvent data.
    /// Internal static for direct unit testing without AMI dependencies.
    /// </summary>
    internal static DialplanSnapshot BuildSnapshot(string serverId, List<ListDialplanEvent> events)
    {
        var contextMap = new Dictionary<string, ContextBuilder>(StringComparer.Ordinal);

        foreach (var evt in events)
        {
            if (evt.Context is null) continue;

            if (!contextMap.TryGetValue(evt.Context, out var builder))
            {
                builder = new ContextBuilder(evt.Context);
                contextMap[evt.Context] = builder;
            }

            // Track registrar (first one wins for the context)
            builder.Registrar ??= evt.Registrar;

            // Handle include directives
            if (!string.IsNullOrEmpty(evt.IncludeContext))
            {
                builder.Includes.Add(evt.IncludeContext);
                continue;
            }

            // Handle extension priorities
            if (evt.Extension is null || evt.Application is null) continue;

            if (!builder.Extensions.TryGetValue(evt.Extension, out var priorities))
            {
                priorities = [];
                builder.Extensions[evt.Extension] = priorities;
            }

            priorities.Add(new DialplanPriority
            {
                Number = evt.Priority ?? 0,
                Label = evt.ExtensionLabel,
                Application = evt.Application,
                ApplicationData = evt.AppData ?? "",
                Source = evt.Registrar
            });
        }

        var contexts = contextMap.Values.Select(b =>
        {
            var isSystem = IsSystemContext(b.Name, b.Registrar);
            return new DiscoveredContext
            {
                Name = b.Name,
                CreatedBy = b.Registrar ?? "unknown",
                IsSystem = isSystem,
                Extensions = b.Extensions.Select(kvp => new DialplanExtension
                {
                    Pattern = kvp.Key,
                    Priorities = kvp.Value
                }).ToList(),
                Includes = b.Includes.ToList()
            };
        }).ToList();

        return new DialplanSnapshot
        {
            ServerId = serverId,
            RefreshedAt = DateTime.UtcNow,
            Contexts = contexts
        };
    }

    /// <summary>
    /// Returns only user-defined (non-system) contexts from a snapshot.
    /// </summary>
    internal static IReadOnlyList<DiscoveredContext> GetUserContexts(DialplanSnapshot snapshot) =>
        snapshot.Contexts.Where(c => !c.IsSystem).ToList();

    /// <summary>
    /// Checks whether a context exists in the given snapshot.
    /// </summary>
    internal static bool ContextExists(DialplanSnapshot snapshot, string contextName) =>
        snapshot.Contexts.Any(c => string.Equals(c.Name, contextName, StringComparison.Ordinal));

    private static bool IsSystemContext(string name, string? registrar)
    {
        // Contexts with double-underscore prefix are internal Asterisk hooks
        if (name.StartsWith("__", StringComparison.Ordinal))
            return true;

        // Known system context names
        if (SystemContextNames.Contains(name))
            return true;

        // Contexts from non-user registrars (modules like res_parking, func_periodic_hook, etc.)
        if (registrar is not null && !UserRegistrars.Contains(registrar))
            return true;

        return false;
    }

    private void RefreshAllCallback(object? state)
    {
        _ = RefreshAllServersAsync();
    }

    private async Task RefreshAllServersAsync()
    {
        foreach (var (serverId, _) in _monitor.Servers)
        {
            await RefreshAsync(serverId);
        }
    }

    public void Dispose()
    {
        _refreshTimer?.Dispose();
        _refreshTimer = null;
    }

    private sealed class ContextBuilder(string name)
    {
        public string Name { get; } = name;
        public string? Registrar { get; set; }
        public Dictionary<string, List<DialplanPriority>> Extensions { get; } = new(StringComparer.Ordinal);
        public List<string> Includes { get; } = [];
    }
}

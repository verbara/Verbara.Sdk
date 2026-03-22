using System.Collections.Frozen;

namespace PbxAdmin.Services.Repositories;

internal static partial class RouteRepositoryResolverLog
{
    [LoggerMessage(Level = LogLevel.Information, Message = "[ROUTE_RESOLVER] Registered: server={ServerId} mode={Mode}")]
    public static partial void Registered(ILogger logger, string serverId, string mode);

    [LoggerMessage(Level = LogLevel.Warning, Message = "[ROUTE_RESOLVER] Unknown server, using fallback: server={ServerId}")]
    public static partial void UnknownServer(ILogger logger, string serverId);
}

/// <summary>
/// Resolves the <see cref="IRouteRepository"/> for a given server id.
/// Realtime servers use <see cref="DbRouteRepository"/>; File servers use <see cref="FileRouteRepository"/>.
/// </summary>
public sealed class RouteRepositoryResolver : IRouteRepositoryResolver
{
    private readonly FrozenDictionary<string, IRouteRepository> _repositories;
    private readonly IRouteRepository _fallback;
    private readonly ILogger<RouteRepositoryResolver> _logger;

    public RouteRepositoryResolver(IConfiguration config, ILoggerFactory loggerFactory)
    {
        _logger = loggerFactory.CreateLogger<RouteRepositoryResolver>();

        var fileDataDir = Path.Combine(AppContext.BaseDirectory, "data", "routes");
        var fallbackRepo = new FileRouteRepository(fileDataDir, loggerFactory.CreateLogger<FileRouteRepository>());
        _fallback = fallbackRepo;

        var dict = new Dictionary<string, IRouteRepository>(StringComparer.OrdinalIgnoreCase);
        var servers = config.GetSection("Asterisk:Servers").GetChildren();

        foreach (var section in servers)
        {
            var id = section["Id"] ?? "default";
            var modeStr = section["ConfigMode"] ?? "File";
            var isRealtime = string.Equals(modeStr, "Realtime", StringComparison.OrdinalIgnoreCase);

            IRouteRepository repo;
            if (isRealtime)
            {
                var connStr = section["RealtimeConnectionString"]
                    ?? throw new InvalidOperationException(
                        $"Asterisk:Servers entry '{id}' has ConfigMode=Realtime but no RealtimeConnectionString.");
                repo = new DbRouteRepository(connStr, loggerFactory.CreateLogger<DbRouteRepository>());
            }
            else
            {
                var serverDataDir = Path.Combine(AppContext.BaseDirectory, "data", "routes");
                repo = new FileRouteRepository(serverDataDir, loggerFactory.CreateLogger<FileRouteRepository>());
            }

            dict[id] = repo;
            RouteRepositoryResolverLog.Registered(_logger, id, isRealtime ? "Realtime" : "File");
        }

        _repositories = dict.ToFrozenDictionary();
    }

    public IRouteRepository GetRepository(string serverId)
    {
        if (_repositories.TryGetValue(serverId, out var repo))
            return repo;

        RouteRepositoryResolverLog.UnknownServer(_logger, serverId);
        return _fallback;
    }
}

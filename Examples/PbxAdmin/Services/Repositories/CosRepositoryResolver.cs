using System.Collections.Frozen;

namespace PbxAdmin.Services.Repositories;

internal static partial class CosRepositoryResolverLog
{
    [LoggerMessage(Level = LogLevel.Information, Message = "[COS_RESOLVER] Registered: server={ServerId} mode={Mode}")]
    public static partial void Registered(ILogger logger, string serverId, string mode);

    [LoggerMessage(Level = LogLevel.Warning, Message = "[COS_RESOLVER] Unknown server, using fallback: server={ServerId}")]
    public static partial void UnknownServer(ILogger logger, string serverId);
}

/// <summary>
/// Resolves the <see cref="ICosRepository"/> and <see cref="ICosPatternGroupRepository"/> for a given server id.
/// Realtime servers use DB implementations; File servers use JSON file implementations.
/// </summary>
public sealed class CosRepositoryResolver : ICosRepositoryResolver
{
    private readonly FrozenDictionary<string, ICosRepository> _cosRepos;
    private readonly FrozenDictionary<string, ICosPatternGroupRepository> _patternRepos;
    private readonly ICosRepository _fallbackCos;
    private readonly ICosPatternGroupRepository _fallbackPattern;
    private readonly ILogger<CosRepositoryResolver> _logger;

    public CosRepositoryResolver(IConfiguration config, ILoggerFactory loggerFactory)
    {
        _logger = loggerFactory.CreateLogger<CosRepositoryResolver>();

        var fileDataDir = Path.Combine(AppContext.BaseDirectory, "data", "cos");
        _fallbackCos = new FileCosRepository(fileDataDir, loggerFactory.CreateLogger<FileCosRepository>());
        _fallbackPattern = new FileCosPatternGroupRepository(fileDataDir, loggerFactory.CreateLogger<FileCosPatternGroupRepository>());

        var cosDict = new Dictionary<string, ICosRepository>(StringComparer.OrdinalIgnoreCase);
        var patternDict = new Dictionary<string, ICosPatternGroupRepository>(StringComparer.OrdinalIgnoreCase);

        foreach (var section in config.GetSection("Asterisk:Servers").GetChildren())
        {
            var id = section["Id"] ?? "default";
            var modeStr = section["ConfigMode"] ?? "File";
            var isRealtime = string.Equals(modeStr, "Realtime", StringComparison.OrdinalIgnoreCase);

            if (isRealtime)
            {
                var connStr = section["RealtimeConnectionString"]
                    ?? throw new InvalidOperationException(
                        $"Asterisk:Servers entry '{id}' has ConfigMode=Realtime but no RealtimeConnectionString.");
                cosDict[id] = new DbCosRepository(connStr, loggerFactory.CreateLogger<DbCosRepository>());
                patternDict[id] = new DbCosPatternGroupRepository(connStr, loggerFactory.CreateLogger<DbCosPatternGroupRepository>());
            }
            else
            {
                var dataDir = Path.Combine(AppContext.BaseDirectory, "data", "cos");
                cosDict[id] = new FileCosRepository(dataDir, loggerFactory.CreateLogger<FileCosRepository>());
                patternDict[id] = new FileCosPatternGroupRepository(dataDir, loggerFactory.CreateLogger<FileCosPatternGroupRepository>());
            }

            CosRepositoryResolverLog.Registered(_logger, id, isRealtime ? "Realtime" : "File");
        }

        _cosRepos = cosDict.ToFrozenDictionary();
        _patternRepos = patternDict.ToFrozenDictionary();
    }

    public ICosRepository GetCosRepository(string serverId)
    {
        if (_cosRepos.TryGetValue(serverId, out var repo))
            return repo;

        CosRepositoryResolverLog.UnknownServer(_logger, serverId);
        return _fallbackCos;
    }

    public ICosPatternGroupRepository GetPatternGroupRepository(string serverId)
    {
        if (_patternRepos.TryGetValue(serverId, out var repo))
            return repo;

        CosRepositoryResolverLog.UnknownServer(_logger, serverId);
        return _fallbackPattern;
    }
}

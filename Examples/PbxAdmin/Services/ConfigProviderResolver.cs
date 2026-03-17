using System.Collections.Frozen;
using PbxAdmin.Models;

namespace PbxAdmin.Services;

public interface IConfigProviderResolver
{
    IConfigProvider GetProvider(string serverId);
    ConfigMode GetConfigMode(string serverId);
}

public sealed class ConfigProviderResolver : IConfigProviderResolver
{
    private readonly FrozenDictionary<string, (ConfigMode Mode, IConfigProvider Provider)> _providers;
    private readonly IConfigProvider _fallback;

    public ConfigProviderResolver(
        IConfiguration config,
        PbxConfigManager amiProvider,
        ILoggerFactory loggerFactory)
    {
        var dict = new Dictionary<string, (ConfigMode, IConfigProvider)>(StringComparer.OrdinalIgnoreCase);
        var servers = config.GetSection("Asterisk:Servers").GetChildren();

        foreach (var section in servers)
        {
            var id = section["Id"] ?? "default";
            var modeStr = section["ConfigMode"] ?? "File";
            var mode = string.Equals(modeStr, "Realtime", StringComparison.OrdinalIgnoreCase)
                ? ConfigMode.Realtime
                : ConfigMode.File;

            IConfigProvider provider;
            if (mode == ConfigMode.Realtime)
            {
                var connStr = section["RealtimeConnectionString"]
                    ?? throw new InvalidOperationException(
                        $"Asterisk:Servers entry '{id}' has ConfigMode=Realtime but no RealtimeConnectionString.");
                provider = new DbConfigProvider(connStr, amiProvider, loggerFactory.CreateLogger<DbConfigProvider>());
            }
            else
            {
                provider = amiProvider;
            }

            dict[id] = (mode, provider);
        }

        _providers = dict.ToFrozenDictionary();
        _fallback = amiProvider;
    }

    public IConfigProvider GetProvider(string serverId) =>
        _providers.TryGetValue(serverId, out var entry) ? entry.Provider : _fallback;

    public ConfigMode GetConfigMode(string serverId) =>
        _providers.TryGetValue(serverId, out var entry) ? entry.Mode : ConfigMode.File;
}

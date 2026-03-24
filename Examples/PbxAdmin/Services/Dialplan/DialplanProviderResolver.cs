using System.Collections.Frozen;
using PbxAdmin.Models;
using PbxAdmin.Services;

namespace PbxAdmin.Services.Dialplan;

public sealed class DialplanProviderResolver : IDialplanProviderResolver
{
    private readonly FrozenDictionary<string, IDialplanProvider> _providers;
    private readonly IDialplanProvider _fallback;

    public DialplanProviderResolver(
        IConfiguration config,
        PbxConfigManager amiProvider,
        ILoggerFactory loggerFactory)
    {
        var dict = new Dictionary<string, IDialplanProvider>(StringComparer.OrdinalIgnoreCase);
        var servers = config.GetSection("Asterisk:Servers").GetChildren();

        foreach (var section in servers)
        {
            var id = section["Id"] ?? "default";
            var modeStr = section["ConfigMode"] ?? "File";
            var isRealtime = string.Equals(modeStr, "Realtime", StringComparison.OrdinalIgnoreCase);

            if (isRealtime)
            {
                var connStr = section["RealtimeConnectionString"]
                    ?? throw new InvalidOperationException(
                        $"Asterisk:Servers entry '{id}' has ConfigMode=Realtime but no RealtimeConnectionString.");
                dict[id] = new RealtimeDialplanProvider(connStr, amiProvider, loggerFactory.CreateLogger<RealtimeDialplanProvider>());
            }
            else
            {
                dict[id] = new FileDialplanProvider(amiProvider, loggerFactory.CreateLogger<FileDialplanProvider>());
            }
        }

        _providers = dict.ToFrozenDictionary();
        _fallback = new FileDialplanProvider(amiProvider, loggerFactory.CreateLogger<FileDialplanProvider>());
    }

    public IDialplanProvider GetProvider(string serverId) =>
        _providers.TryGetValue(serverId, out var provider) ? provider : _fallback;
}

using DashboardExample.Services;

namespace DashboardExample.Services.Dialplan;

internal static partial class FileDialplanLog
{
    [LoggerMessage(Level = LogLevel.Information, Message = "[DIALPLAN] Generated {Count} contexts in extensions.conf for server {ServerId}")]
    public static partial void Generated(ILogger logger, int count, string serverId);

    [LoggerMessage(Level = LogLevel.Error, Message = "[DIALPLAN] Failed to generate extensions.conf for server {ServerId}")]
    public static partial void GenerateFailed(ILogger logger, Exception exception, string serverId);
}

internal sealed class FileDialplanProvider(PbxConfigManager configManager, ILogger<FileDialplanProvider> logger) : IDialplanProvider
{
    public async Task<bool> GenerateDialplanAsync(string serverId, DialplanData data, CancellationToken ct = default)
    {
        var lines = DialplanGenerator.Generate(data);
        var byContext = lines.GroupBy(l => l.Context).ToList();

        try
        {
            foreach (var group in byContext)
            {
                await configManager.DeleteSectionAsync(serverId, "extensions.conf", group.Key, ct);
            }

            foreach (var group in byContext)
            {
                var vars = new Dictionary<string, string>();
                foreach (var line in group.OrderBy(l => l.Priority))
                {
                    var appWithData = string.IsNullOrEmpty(line.AppData)
                        ? line.App
                        : $"{line.App}({line.AppData})";

                    if (line.Priority == 1)
                        vars[$"exten"] = $"{line.Exten},{line.Priority},{appWithData}";
                    else
                        vars[$"same"] = $"n,{appWithData}";
                }

                await configManager.CreateSectionAsync(serverId, "extensions.conf", group.Key, vars, ct: ct);
            }

            FileDialplanLog.Generated(logger, byContext.Count, serverId);
            return true;
        }
        catch (Exception ex)
        {
            FileDialplanLog.GenerateFailed(logger, ex, serverId);
            return false;
        }
    }

    public async Task<bool> ReloadAsync(string serverId, CancellationToken ct = default) =>
        await configManager.ReloadModuleAsync(serverId, "pbx_config", ct);
}

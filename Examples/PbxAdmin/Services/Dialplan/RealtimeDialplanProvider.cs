using Dapper;
using Npgsql;

namespace PbxAdmin.Services.Dialplan;

internal static partial class RealtimeDialplanLog
{
    [LoggerMessage(Level = LogLevel.Information, Message = "[DIALPLAN] No contexts to generate for server {ServerId}")]
    public static partial void NoContexts(ILogger logger, string serverId);

    [LoggerMessage(Level = LogLevel.Information, Message = "[DIALPLAN] Generated {Count} lines in {CtxCount} contexts for server {ServerId}")]
    public static partial void Generated(ILogger logger, int count, int ctxCount, string serverId);

    [LoggerMessage(Level = LogLevel.Error, Message = "[DIALPLAN] Failed to generate dialplan for server {ServerId}")]
    public static partial void GenerateFailed(ILogger logger, Exception exception, string serverId);
}

internal sealed class RealtimeDialplanProvider(
    string connectionString,
    PbxConfigManager configManager,
    ILogger<RealtimeDialplanProvider> logger) : IDialplanProvider
{
    public async Task<bool> GenerateDialplanAsync(string serverId, DialplanData data, CancellationToken ct = default)
    {
        var lines = DialplanGenerator.Generate(data);
        var contexts = lines.Select(l => l.Context).Distinct().ToArray();

        if (contexts.Length == 0)
        {
            RealtimeDialplanLog.NoContexts(logger, serverId);
            return true;
        }

        // 1. Persist to DB (for PbxAdmin queries and data durability)
        await using var conn = new NpgsqlConnection(connectionString);
        await conn.OpenAsync(ct);
        await using var tx = await conn.BeginTransactionAsync(ct);

        try
        {
            await conn.ExecuteAsync(
                "DELETE FROM extensions WHERE context = ANY(@Contexts)",
                new { Contexts = contexts }, tx);

            foreach (var line in lines)
            {
                await conn.ExecuteAsync(
                    "INSERT INTO extensions (context, exten, priority, app, appdata) VALUES (@Context, @Exten, @Priority, @App, @AppData)",
                    new { line.Context, line.Exten, line.Priority, line.App, line.AppData }, tx);
            }

            await tx.CommitAsync(ct);
        }
        catch (Exception ex)
        {
            await tx.RollbackAsync(ct);
            RealtimeDialplanLog.GenerateFailed(logger, ex, serverId);
            return false;
        }

        // 2. Also write to extensions.conf via AMI (so Asterisk can execute the contexts)
        var byContext = lines.GroupBy(l => l.Context).ToList();
        try
        {
            foreach (var group in byContext)
            {
                var kvLines = new List<KeyValuePair<string, string>>();
                foreach (var line in group.OrderBy(l => l.Exten).ThenBy(l => l.Priority))
                {
                    var appWithData = string.IsNullOrEmpty(line.AppData)
                        ? line.App
                        : $"{line.App}({line.AppData})";

                    if (line.Priority == 1)
                        kvLines.Add(new KeyValuePair<string, string>("exten", $"{line.Exten},{line.Priority},{appWithData}"));
                    else
                        kvLines.Add(new KeyValuePair<string, string>("same", $"n,{appWithData}"));
                }

                await configManager.CreateSectionWithLinesAsync(serverId, "extensions.conf", group.Key, kvLines, ct);
            }
        }
        catch (Exception ex)
        {
            RealtimeDialplanLog.GenerateFailed(logger, ex, serverId);
            // DB write succeeded, AMI write failed — partial success
        }

        RealtimeDialplanLog.Generated(logger, lines.Count, contexts.Length, serverId);
        return true;
    }

    public async Task<bool> ReloadAsync(string serverId, CancellationToken ct = default) =>
        await configManager.ReloadModuleAsync(serverId, "pbx_config", ct);
}

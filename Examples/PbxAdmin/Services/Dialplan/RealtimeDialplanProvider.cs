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

internal sealed class RealtimeDialplanProvider(string connectionString, ILogger<RealtimeDialplanProvider> logger) : IDialplanProvider
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
            RealtimeDialplanLog.Generated(logger, lines.Count, contexts.Length, serverId);
            return true;
        }
        catch (Exception ex)
        {
            await tx.RollbackAsync(ct);
            RealtimeDialplanLog.GenerateFailed(logger, ex, serverId);
            return false;
        }
    }

    public Task<bool> ReloadAsync(string serverId, CancellationToken ct = default) =>
        Task.FromResult(true);
}

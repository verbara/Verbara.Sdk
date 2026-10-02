namespace Verbara.Sdk.Push.AspNetCore;

using Microsoft.Extensions.Logging;

/// <summary>Source-generated log messages for the SSE push endpoint.</summary>
internal static partial class SsePushLog
{
    /// <summary>The log category of the SSE push endpoint.</summary>
    internal const string Category = "Verbara.Sdk.Push.AspNetCore.SsePushEndpoints";

    // Warning, like the SDK's other handled client refusals (AudioSocket, ARI outbound). The authorizer's
    // reason stays here, server-side: the 403 body is fixed text.
    [LoggerMessage(EventId = 1, Level = LogLevel.Warning,
        Message = "[Push] SSE stream refused (403): tenant={TenantId} user={UserId} deniedTopics={DeniedTopics} reason={Reason}")]
    public static partial void StreamRefused(ILogger logger, string tenantId, string? userId, string deniedTopics, string? reason);

    // Warning once per gap episode (Q1(e)): the first drop not yet reported to the client; never once per frame.
    [LoggerMessage(EventId = 2, Level = LogLevel.Warning,
        Message = "[Push] SSE stream dropping event frames at its bound of {BoundBytes} bytes: tenant={TenantId} user={UserId}; the client is sent one .gap with the count before its next event")]
    public static partial void GapEpisodeStarted(ILogger logger, long boundBytes, string tenantId, string? userId);

    // A disconnect or a reset is how every stream ends; it is not an error.
    [LoggerMessage(EventId = 3, Level = LogLevel.Debug,
        Message = "[Push] SSE stream ended by the connection: tenant={TenantId} user={UserId} ({Reason})")]
    public static partial void StreamEnded(ILogger logger, string tenantId, string? userId, string reason);
}

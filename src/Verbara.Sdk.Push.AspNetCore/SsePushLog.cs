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
}

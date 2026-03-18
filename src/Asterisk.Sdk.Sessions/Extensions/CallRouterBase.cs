namespace Asterisk.Sdk.Sessions.Extensions;

public abstract class CallRouterBase
{
    public abstract ValueTask<string> SelectNodeAsync(CallSession session, CancellationToken ct);

    public virtual ValueTask<bool> CanRouteAsync(CallSession session, CancellationToken ct)
        => ValueTask.FromResult(true);

    /// <summary>
    /// Selects a node for an outbound origination before a CallSession exists.
    /// Default implementation creates a synthetic session and delegates to SelectNodeAsync.
    /// Override in cluster routers to implement queue-aware or phone-aware routing.
    /// </summary>
    public virtual ValueTask<string> SelectNodeForOriginateAsync(
        string? queueName,
        string? phoneNumber,
        Dictionary<string, string>? metadata,
        CancellationToken ct)
    {
        var syntheticSession = new CallSession(
            sessionId: $"originate-{Guid.NewGuid():N}",
            linkedId: "",
            serverId: "",
            direction: CallDirection.Outbound);

        if (queueName is not null)
            syntheticSession.QueueName = queueName;

        if (metadata is not null)
        {
            foreach (var kvp in metadata)
                syntheticSession.SetMetadata(kvp.Key, kvp.Value);
        }

        return SelectNodeAsync(syntheticSession, ct);
    }
}

namespace Asterisk.Sdk.Sessions.Extensions;

public abstract class CallRouterBase
{
    public abstract ValueTask<string> SelectNodeAsync(CallSession session, CancellationToken ct);
    public virtual ValueTask<bool> CanRouteAsync(CallSession session, CancellationToken ct)
        => ValueTask.FromResult(true);
}

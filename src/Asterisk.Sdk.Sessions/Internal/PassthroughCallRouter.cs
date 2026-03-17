using Asterisk.Sdk.Sessions.Extensions;

namespace Asterisk.Sdk.Sessions.Internal;

internal sealed class PassthroughCallRouter : CallRouterBase
{
    public override ValueTask<string> SelectNodeAsync(CallSession session, CancellationToken ct)
        => ValueTask.FromResult(session.ServerId);
}

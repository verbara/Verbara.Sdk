using Verbara.Sdk.Live.Agents;
using Verbara.Sdk.Live.Queues;
using Verbara.Sdk.Sessions.Extensions;
using Verbara.Sdk.Sessions;

namespace Verbara.Sdk.Sessions.Internal;

internal sealed class NativeAgentSelector : AgentSelectorBase
{
    public override ValueTask<AsteriskAgent?> SelectAgentAsync(
        AsteriskQueue queue, CallSession session, CancellationToken ct)
        => ValueTask.FromResult<AsteriskAgent?>(null); // Let Asterisk decide
}

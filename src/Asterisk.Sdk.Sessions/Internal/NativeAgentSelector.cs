using Asterisk.Sdk.Live.Agents;
using Asterisk.Sdk.Live.Queues;
using Asterisk.Sdk.Sessions.Extensions;
using Asterisk.Sdk.Sessions;

namespace Asterisk.Sdk.Sessions.Internal;

internal sealed class NativeAgentSelector : AgentSelectorBase
{
    public override ValueTask<AsteriskAgent?> SelectAgentAsync(
        AsteriskQueue queue, CallSession session, CancellationToken ct)
        => ValueTask.FromResult<AsteriskAgent?>(null); // Let Asterisk decide
}

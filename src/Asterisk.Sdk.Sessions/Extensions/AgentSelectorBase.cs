using Asterisk.Sdk.Live.Agents;
using Asterisk.Sdk.Live.Queues;
using Asterisk.Sdk.Sessions;

namespace Asterisk.Sdk.Sessions.Extensions;

public abstract class AgentSelectorBase
{
    public abstract ValueTask<AsteriskAgent?> SelectAgentAsync(
        AsteriskQueue queue, CallSession session, CancellationToken ct);

    public virtual ValueTask<IReadOnlyList<AsteriskAgent>> RankAgentsAsync(
        AsteriskQueue queue, CallSession session,
        IReadOnlyList<AsteriskAgent> candidates, CancellationToken ct)
        => ValueTask.FromResult(candidates);
}

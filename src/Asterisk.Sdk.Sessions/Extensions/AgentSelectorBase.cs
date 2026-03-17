using Asterisk.Sdk.Live.Agents;
using Asterisk.Sdk.Live.Queues;

namespace Asterisk.Sdk.Sessions.Extensions;

public abstract class AgentSelectorBase
{
    public abstract ValueTask<AsteriskAgent?> SelectAgentAsync(AsteriskQueue queue, CancellationToken ct);
    public virtual ValueTask<IReadOnlyList<AsteriskAgent>> RankAgentsAsync(
        AsteriskQueue queue, IReadOnlyList<AsteriskAgent> candidates, CancellationToken ct)
        => ValueTask.FromResult(candidates);
}

using Verbara.Sdk.Live.Agents;
using Verbara.Sdk.Live.Queues;
using Verbara.Sdk.Sessions;

namespace Verbara.Sdk.Sessions.Extensions;

public abstract class AgentSelectorBase
{
    public abstract ValueTask<AsteriskAgent?> SelectAgentAsync(
        AsteriskQueue queue, CallSession session, CancellationToken ct);

    public virtual ValueTask<IReadOnlyList<AsteriskAgent>> RankAgentsAsync(
        AsteriskQueue queue, CallSession session,
        IReadOnlyList<AsteriskAgent> candidates, CancellationToken ct)
        => ValueTask.FromResult(candidates);
}

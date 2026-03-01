using Asterisk.NetAot.Abstractions;
using Asterisk.NetAot.Live.Channels;
using Asterisk.NetAot.Live.Queues;
using Asterisk.NetAot.Live.Agents;
using Microsoft.Extensions.Logging;

namespace Asterisk.NetAot.Live.Server;

/// <summary>
/// Aggregate root for real-time Asterisk state tracking.
/// Listens to AMI events and maintains live domain objects.
/// </summary>
public sealed class AsteriskServer : IAsyncDisposable
{
    private readonly IAmiConnection _connection;
    private readonly ILogger<AsteriskServer> _logger;

    public ChannelManager Channels { get; }
    public QueueManager Queues { get; }
    public AgentManager Agents { get; }

    public AsteriskServer(IAmiConnection connection, ILogger<AsteriskServer> logger)
    {
        _connection = connection;
        _logger = logger;
        Channels = new ChannelManager(logger);
        Queues = new QueueManager(logger);
        Agents = new AgentManager(logger);
    }

    /// <summary>Initialize state by querying current Asterisk status.</summary>
    public async ValueTask InitializeAsync(CancellationToken cancellationToken = default)
    {
        // TODO: Send StatusAction, QueueStatusAction, AgentsAction
        // TODO: Subscribe to events for real-time updates
        throw new NotImplementedException();
    }

    public ValueTask DisposeAsync()
    {
        // TODO: Unsubscribe from events
        return ValueTask.CompletedTask;
    }
}

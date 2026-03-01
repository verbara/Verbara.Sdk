using System.Collections.Concurrent;
using Asterisk.Sdk.Ami.Connection;
using Microsoft.Extensions.Logging;

namespace Asterisk.Sdk.Live.Server;

/// <summary>
/// Manages multiple AsteriskServer instances connected to different Asterisk PBX servers.
/// Provides federated agent routing so callers can locate which server owns a given agent.
/// Designed for 100K+ agents distributed across 20-50 Asterisk instances.
/// </summary>
public sealed class AsteriskServerPool : IAsyncDisposable
{
    private readonly IAmiConnectionFactory _connectionFactory;
    private readonly ILoggerFactory _loggerFactory;
    private readonly ConcurrentDictionary<string, AsteriskServer> _servers = new();
    private readonly ConcurrentDictionary<string, string> _agentRouting = new();

    public AsteriskServerPool(
        IAmiConnectionFactory connectionFactory,
        ILoggerFactory loggerFactory)
    {
        _connectionFactory = connectionFactory;
        _loggerFactory = loggerFactory;
    }

    /// <summary>Number of servers in the pool.</summary>
    public int ServerCount => _servers.Count;

    /// <summary>Total agents tracked across all servers.</summary>
    public int TotalAgentCount => _servers.Values.Sum(s => s.Agents.AgentCount);

    /// <summary>All servers in the pool.</summary>
    public IEnumerable<KeyValuePair<string, AsteriskServer>> Servers => _servers;

    /// <summary>
    /// Add a new Asterisk server to the pool, connect, and start tracking its state.
    /// </summary>
    public async ValueTask<AsteriskServer> AddServerAsync(
        string serverId,
        AmiConnectionOptions options,
        CancellationToken cancellationToken = default)
    {
        var connection = await _connectionFactory.CreateAndConnectAsync(options, cancellationToken);
        var logger = _loggerFactory.CreateLogger<AsteriskServer>();
        var server = new AsteriskServer(connection, logger);

        if (!_servers.TryAdd(serverId, server))
        {
            await server.DisposeAsync();
            throw new InvalidOperationException($"Server '{serverId}' already exists in pool");
        }

        // Subscribe to agent events to maintain routing table
        server.Agents.AgentLoggedIn += a => _agentRouting[a.AgentId] = serverId;
        server.Agents.AgentLoggedOff += a => _agentRouting.TryRemove(a.AgentId, out _);

        server.StartTracking();
        await server.RequestInitialStateAsync(cancellationToken);

        // Index existing agents after initial state load
        foreach (var agent in server.Agents.Agents)
        {
            _agentRouting[agent.AgentId] = serverId;
        }

        return server;
    }

    /// <summary>
    /// Remove and disconnect a server from the pool.
    /// </summary>
    public async ValueTask RemoveServerAsync(string serverId)
    {
        if (_servers.TryRemove(serverId, out var server))
        {
            // Clean up routing entries for this server's agents
            foreach (var agent in server.Agents.Agents)
            {
                _agentRouting.TryRemove(agent.AgentId, out _);
            }
            await server.DisposeAsync();
        }
    }

    /// <summary>
    /// Get the server that owns a specific agent.
    /// </summary>
    public AsteriskServer? GetServerForAgent(string agentId)
    {
        if (_agentRouting.TryGetValue(agentId, out var serverId)
            && _servers.TryGetValue(serverId, out var server))
        {
            return server;
        }
        return null;
    }

    /// <summary>
    /// Get a server by its ID.
    /// </summary>
    public AsteriskServer? GetServer(string serverId) =>
        _servers.GetValueOrDefault(serverId);

    public async ValueTask DisposeAsync()
    {
        foreach (var server in _servers.Values)
        {
            await server.DisposeAsync();
        }
        _servers.Clear();
        _agentRouting.Clear();
    }
}

# Asterisk.Sdk.Live

Real-time domain objects for Asterisk PBX, built on AMI events.

## Features

- Live tracking of channels, queues, agents, and conferences
- `AsteriskServer` aggregates all managers from a single AMI connection
- `AsteriskServerPool` federates multiple servers with agent routing (100K+ agents)
- Thread-safe with per-entity locks and `ConcurrentDictionary`
- Lazy queries: `GetAgentsByState()`, `GetChannelsByState()`, `GetQueuesForMember()`
- `System.Diagnostics.Metrics` for observable gauges (active channels, queue sizes)

## Quick Start

```csharp
var server = new AsteriskServer(connection, logger);
await server.StartAsync();

// Access live state
foreach (var channel in server.Channels.GetChannelsByState(ChannelState.Up))
    Console.WriteLine($"Active call: {channel.CallerId} -> {channel.Extension}");

// React to changes
server.Agents.AgentStateChanged += agent =>
    Console.WriteLine($"Agent {agent.AgentId}: {agent.State}");
```

## Multi-Server

```csharp
var pool = new AsteriskServerPool(connectionFactory, loggerFactory);
await pool.AddServerAsync("pbx-east", eastOptions);
await pool.AddServerAsync("pbx-west", westOptions);

// Federated routing
var server = pool.GetServerForAgent("Agent/1001");
```

## Documentation

- [High-Load Tuning Guide](../../docs/high-load-tuning.md)

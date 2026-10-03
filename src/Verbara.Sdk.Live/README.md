# Verbara.Sdk.Live

Real-time domain objects for Asterisk PBX, built on AMI events.

## Features

- Live tracking of channels, queues, agents, and conferences
- `AsteriskServer` aggregates all managers from a single AMI connection
- `AsteriskServerPool` federates multiple servers with agent routing (designed for 100K+ agents; sizing guidance in [`high-load-tuning.md`](../../docs/guides/high-load-tuning.md) — no load test at that scale exists)
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

## Reconciling the channels

`VerbaraServer.ReconcileChannelsAsync()` asks Asterisk for its channel snapshot (`Status`), reads it to the end and
reconciles the channel table against it: the channel part of a state load, and nothing else. It asks for no queue or
agent state.

```csharp
await server.ReconcileChannelsAsync(cancellationToken);
```

- A channel the snapshot no longer lists is removed as a reload removes it, so a session manager attached to the server
  ends its call with no hangup cause. A channel the snapshot lists and the table lacks is admitted as reported.
- A channel that arrives, or hangs up, while the snapshot is being read is left as the live events made it.
- A snapshot that did not complete reconciles nothing: a cancelled read throws `OperationCanceledException`, and a read
  whose AMI session ended, or a connection that is not established, throws `AmiNotConnectedException`.
- A `Status` that Asterisk refuses (an AMI user whose `write` has none of `system`, `call` or `reporting`) reconciles
  nothing and does not throw. It is logged at Warning the first time in an AMI session and at Debug after that.
- Each run is traced as a `live channel-reconcile` activity, tagged `live.channels` and, on a refusal,
  `live.status.refused`, and logged at Debug. It is not reported as a state load.

The session engine's reconciliation sweep (`AddVerbaraSessions`) calls it. A host can also call it on a schedule of its
own: for each server of a multi-server host, for instance, since `AddVerbaraSessionsMultiServer` registers no sweep.

## Documentation

- [High-Load Tuning Guide](../../docs/guides/high-load-tuning.md)

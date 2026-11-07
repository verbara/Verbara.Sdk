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

## Pauses

`server.Queues.MemberPaused` is raised for every pause or unpause Asterisk reports for a queue member the table holds,
whatever the member's interface, after the table applied it.

`server.Agents.AgentStateChanged` reports `AgentState.Paused` only for an app_agent_pool member (`Agent/<id>`, the
prefix in any letter case), and only while every one of that agent's memberships on that server is paused; an unpause
in any of its queues makes it `Available` again. A call and a logoff win over a pause: an agent on a call or logged off
keeps that state. A device member (`PJSIP/…`, `SIP/…`, `Local/…`) changes no agent; read `MemberPaused` for it.

A load, a reload, a login and an `Agent/<id>` member add or remove keep the pause. `OnCall` and the return from a call
are reached only through `OnAgentConnect`/`OnAgentComplete` called by the host: Asterisk's `AgentConnect`/`AgentComplete`
frames name no agent.

## Multi-Server

```csharp
var pool = new AsteriskServerPool(connectionFactory, loggerFactory);
await pool.AddServerAsync("pbx-east", eastOptions);
await pool.AddServerAsync("pbx-west", westOptions);

// Federated routing
var server = pool.GetServerForAgent("Agent/1001");
```

## Where a channel is

A channel's `Context`, `Extension` and `Priority` follow each `Newexten` the connection receives, and its
`ExtensionHistory` keeps the last 100 visits; `CallerIdNum` and `CallerIdName` follow `NewCallerid`. `Newexten` needs the
`dialplan` class, and the filter the [troubleshooting guide](../../docs/guides/troubleshooting.md#the-cost-of-dialplan-and-a-filter-that-removes-it)
recommends excludes it to save most of that class's volume, so behind it a channel keeps the location it was created with.
A `ChannelManager` subscriber that throws is logged at Warning and counted on `live.channels.subscriber_faults`; the other
subscribers still receive the event.

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
- Every refusal on a load, reload or reconciliation increments `live.status.refused`, tagged with
  `live.status.reader` (`load` or `reconciliation`) and `server.id` when the pool id is known. A refusal before the
  server enters a pool is counted without that id and is never counted again when it joins.
- `server.LastStatusRefusal` exposes an immutable `Message` and UTC `OccurredAt`, or `null`. A later Status read in
  full clears it even if its reconciliation is deferred; a cancelled read or one cut short by its AMI session does not.
  The pool health check reports an otherwise healthy pool as `Degraded` while any server has a refusal. Its data adds
  one `status-refused` string entry (`serverId: message` per refusal, separated by `; ` in ordinal server-id order),
  while each server's own entry remains its connection state's name.
- Each run is traced as a `live channel-reconcile` activity, tagged `live.channels` and, on a refusal,
  `live.status.refused`, and logged at Debug. It is not reported as a state load.

The session engine's reconciliation sweep calls it: `AddVerbaraSessions` for the single server, and
`AddVerbaraSessionsMultiServer` for every server of the pool. A host can also call it on a schedule of its own; on a
multi-server host, switch the pool sweep off first, or each server receives two `Status` per interval.

## Documentation

- [High-Load Tuning Guide](../../docs/guides/high-load-tuning.md)

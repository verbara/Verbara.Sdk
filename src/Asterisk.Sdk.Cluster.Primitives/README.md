# Asterisk.Sdk.Cluster.Primitives

MIT-licensed abstractions for distributed cluster transport, membership, and locking. Part of the `Asterisk.Sdk` open-core family.

## What it does

Exposes three orthogonal primitives that production-grade cluster implementations (Pro, custom backends, partner integrations) implement:

| Primitive | Role |
|---|---|
| `IClusterTransport` | Publishes/subscribes to `ClusterEvent` messages across instances (pub/sub). |
| `IDistributedLock` | Owner-scoped cluster-wide locks with automatic expiry. |
| `IMembershipProvider` | Tracks node registrations, lifecycle state, and instance heartbeats. |

Reference in-memory implementations (`InMemoryClusterTransport`, `InMemoryDistributedLock`, `InMemoryMembershipProvider`) ship in the package for tests and single-instance deployments.

## Why it exists

Cluster primitives are infrastructure without domain logic: circuit breakers, retries, and membership protocols are already commoditized across JVM (Resilience4j, Hystrix), Go (failsafe-go, hashicorp/raft), and .NET (Polly, Orleans). Keeping them in the MIT base of the SDK — analogous to `Asterisk.Sdk.Resilience` — allows open-source consumers to build their own cluster implementations against a stable contract, and keeps commercial extensions focused on domain logic (e.g. Asterisk PBX-specific session recovery, AMI/ARI routing, skill-based assignment).

## Install

```sh
dotnet add package Asterisk.Sdk.Cluster.Primitives
```

## Quick start

```csharp
using Asterisk.Sdk.Cluster.Primitives;
using Asterisk.Sdk.Cluster.Primitives.InMemory;

// Publish/subscribe
await using var transport = new InMemoryClusterTransport();
_ = Task.Run(async () =>
{
    await foreach (var evt in transport.SubscribeAsync())
    {
        Console.WriteLine($"Got {evt.GetType().Name} from {evt.SourceInstanceId}");
    }
});

await transport.PublishAsync(new NodeJoined("instance-A", DateTimeOffset.UtcNow, "node-1"));

// Locks
var locks = new InMemoryDistributedLock();
if (await locks.TryAcquireAsync("resource-x", owner: "instance-A", expiry: TimeSpan.FromSeconds(30)))
{
    try { /* critical section */ }
    finally { await locks.ReleaseAsync("resource-x", "instance-A"); }
}

// Membership
var members = new InMemoryMembershipProvider();
await members.RegisterNodeAsync(new NodeInfo("node-1", NodeState.Healthy) { OwnerInstanceId = "instance-A" });
await members.HeartbeatAsync("instance-A", TimeSpan.FromSeconds(10));

public sealed record NodeJoined(string SourceInstanceId, DateTimeOffset Timestamp, string NodeId)
    : ClusterEvent(SourceInstanceId, Timestamp);
```

## Tracing

`ClusterEvent` carries an optional `TraceContext` property (W3C `traceparent`) for cross-node distributed tracing. Publishers inject the ambient `Activity.Current` traceparent; subscribers extract it to continue the trace. Null when no trace listener is active — zero wire overhead.

## AOT

All types are trimming/AOT-safe. No reflection, no dynamic code generation, no runtime codegen. Pair with `[JsonSerializable]` source generation for event payload serialization across production transports (Redis, Postgres, NATS).

## License

MIT. See repository root.

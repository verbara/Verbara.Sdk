# ADR-0033: `IEventLog` (SDK MIT) vs `IEventStore` (Pro) — tier split

- **Status:** Proposed
- **Date:** 2026-04-20
- **Deciders:** Harold Reina
- **Related:**
  - PSD v2: `Asterisk.Platform/docs/specs/2026-04-19-product-strategy-v2.md` §3, §12.4
  - ADR-0030 (CloudEvents adoption — define `CloudEvent` shape)
  - ADR-0011 (Push bus in-memory non-durable — tier boundary)
  - Pro ADR-0002 (hardening baseline — Pro.EventStore origin)

## Context

Propuesta original del análisis: crear `IEventStore` en SDK con API `AppendAsync` + `AppendManyAsync` + `ReadStreamAsync` + `ReadByTypeAsync`.

**Análisis profundo identificó que esta API es INCOMPLETA para event sourcing real:**

1. **Expected version en Append ausente** → corruption en concurrent writes al mismo aggregate. Todos los event stores production-grade (EventStoreDB, Marten, Axon) lo tienen.
2. **Consumer checkpoints ausentes** → consumers no pueden trackear "hasta dónde leí". Sin esto no hay durable consumers ni replay incremental.
3. **Durable subscriptions ausentes** → `ReadStreamAsync` es pull. Consumers always-on requieren push subscription.
4. **Snapshots ausentes** → aggregate con 10K+ events hace replay prohibitivo.

**Pero:** poner todo lo anterior en SDK MIT cruza 2 gates de §3.1:
- **Scale gate:** snapshots + durable subscriptions + consumer group coordination son algoritmos que cambian con N nodes.
- **Compliance gate:** durable event log con audit trail típicamente requiere tenant isolation + retention compliance.

**Decisión arquitectónica:** split en dos interfaces. MIT ships mínimo append-only log; Pro ships event store completo.

## Decision

**Dos interfaces stacked:**

### `IEventLog` (SDK MIT) — append-only log básico

```csharp
namespace Asterisk.Sdk.EventLog;

public interface IEventLog
{
    /// <summary>
    /// Appends a single event. No optimistic concurrency.
    /// Best-effort durability (in-memory default; storage adapters opt-in).
    /// </summary>
    Task AppendAsync(CloudEvent envelope, CancellationToken ct);

    /// <summary>
    /// Reads events from a stream (partition key). Pull-based.
    /// No durable subscription semantics.
    /// </summary>
    IAsyncEnumerable<CloudEvent> ReadStreamAsync(
        string streamId,
        long? fromVersion = null,
        CancellationToken ct = default);

    /// <summary>
    /// Reads events by type. Pull-based.
    /// </summary>
    IAsyncEnumerable<CloudEvent> ReadByTypeAsync(
        string eventType,
        DateTimeOffset? fromUtc = null,
        CancellationToken ct = default);
}
```

**Default implementation (SDK):** `InMemoryEventLog` — append-only list + pull readers. Suitable for single-node, single-tenant, non-durable scenarios. **Sufficient for SDK users not needing Pro.**

**Guarantees:** best-effort. No optimistic concurrency. No durable subscriptions. No snapshots. No consumer groups. No cross-tenant isolation.

### `IEventStore : IEventLog` (Pro) — event sourcing completo

```csharp
namespace Asterisk.Sdk.Pro.EventStore;

public interface IEventStore : IEventLog
{
    /// <summary>
    /// Optimistic concurrency append — throws ConcurrencyException if expectedVersion mismatches.
    /// </summary>
    Task AppendAsync(
        string streamId,
        long expectedVersion,
        CloudEvent[] events,
        CancellationToken ct);

    /// <summary>
    /// Returns current stream version (for optimistic concurrency precheck).
    /// </summary>
    Task<long> GetStreamVersionAsync(string streamId, CancellationToken ct);

    // Durable subscriptions con checkpointing
    Task<long> GetCheckpointAsync(string consumerName, CancellationToken ct);
    Task SaveCheckpointAsync(string consumerName, long position, CancellationToken ct);
    Task<ISubscription> SubscribeAsync(
        string consumerName,
        long fromPosition,
        Func<CloudEvent, Task> handler,
        CancellationToken ct);

    // Snapshots
    Task SaveSnapshotAsync(
        string streamId,
        long version,
        ReadOnlyMemory<byte> data,
        CancellationToken ct);
    Task<Snapshot?> GetLatestSnapshotAsync(string streamId, CancellationToken ct);

    // DLQ routing
    Task RouteToDlqAsync(CloudEvent envelope, string reason, CancellationToken ct);
}

public interface ISubscription : IAsyncDisposable { }

public sealed record Snapshot(string StreamId, long Version, ReadOnlyMemory<byte> Data, DateTimeOffset CreatedAt);
```

**Implementations (Pro):**
- `PostgresEventStore` (existing `Pro.EventStore.Postgres` refactored).
- `InMemoryEventStore` (testing only).
- Future: `EventStoreDB` adapter (Pro commercial option).

**Guarantees:** durable (via backing store), optimistic concurrency, tenant-aware isolation, consumer checkpointing, snapshot replay O(1), DLQ routing, retention integration (Pro.Storage.Common).

## Migration path

**v2.0:** Ambos interfaces shipped. SDK consumers usan `IEventLog`; Pro consumers usan `IEventStore`.

**Pro.EventStore refactor:**
- `ISessionEventStore` existing se convierte en `IEventStore<SessionDomainEvent>` (derived) o sigue como contract separado con adapter to `IEventStore`.
- `EventStoreSubscriber` migra a usar `ISubscription` durable.
- Postgres storage schema extendido con `consumer_checkpoints` table + `snapshots` table.

**`PushEventMetadata` + `RemotePushEvent`:** consumen `IEventLog.AppendAsync(CloudEvent)` (MIT default). Pro consumers pueden upgrade a `IEventStore` para durability.

## Consequences

**Positivas:**
- Tier split coherente con §3.1 5-gates rule.
- MIT users obtienen event log básico sin necesidad de Pro.
- Pro maintains su valor comercial (optimistic concurrency + subscriptions + snapshots + DLQ son value-add real).
- Clear contract: `IEventLog` guarantees son mínimos + honestos. `IEventStore` es el upgrade path.
- Consumers pueden depender de `IEventLog` (lowest common denominator) y correr contra impl Pro si compran.

**Negativas:**
- Dos interfaces vs una — surface pública ligeramente mayor.
- Developers deben saber cuál depender (`IEventLog` abstract para portability, `IEventStore` cuando necesitan features Pro).
- Migration cost: existing Pro.EventStore refactor para implementar nuevos métodos.

**Mitigación:**
- Docs explican claramente cuándo usar cada uno.
- Dependency injection: si no hay `IEventStore` registrado, fallback a `IEventLog` (`InMemoryEventLog`).
- Pro.EventStore migration path en migration guide v1.x → v2.0.

## Alternatives considered

- **Single `IEventStore` en SDK completo:** rechazado — cruza 2 gates (scale + compliance), inapropiado para MIT tier.
- **Single `IEventLog` simple + Pro extende via decoration (sin separate interface):** rechazado — consumers no pueden type-depend en Pro features explicitly.
- **Put everything en Pro:** rechazado — MIT users pierden event log básico que es primitive esencial (cross-ref ADR-0030 necesita un store).
- **Adopter existing EventStoreDB client directly:** rechazado — heavy dep, Pro-only use case, mejor como optional adapter.

## References

- PSD §3 tier split + §12.4 interfaces
- EventStoreDB API reference: https://www.eventstore.com/
- Marten (Postgres event store): https://martendb.io/
- Pro.EventStore current implementation (src/Asterisk.Sdk.Pro.EventStore/)

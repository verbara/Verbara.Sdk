# Verbara.Sdk.Push

Real-time push primitives for .NET. AOT-safe, host-agnostic, MIT licensed.

## What it does

- Typed, in-memory **event bus** (`IPushEventBus`) backed by a bounded `System.Threading.Channels.Channel<T>` with configurable backpressure.
- **Delivery filter** (`IEventDeliveryFilter`) that enforces tenant isolation and optional per-user targeting before an event reaches a subscriber.
- **Subscription registry** (`ISubscriptionRegistry`) for tracking active subscribers per tenant with automatic cleanup on disposal.
- First-class **diagnostics** via `System.Diagnostics.Metrics` (meter name `Verbara.Sdk.Push`).

No ASP.NET Core dependency. No reflection. Trim-safe.

## Install

```
dotnet add package Verbara.Sdk.Push
```

## Quick start

```csharp
using Verbara.Sdk.Push.Bus;
using Verbara.Sdk.Push.Delivery;
using Verbara.Sdk.Push.Events;
using Verbara.Sdk.Push.Hosting;
using Verbara.Sdk.Push.Subscriptions;
using Microsoft.Extensions.DependencyInjection;

// 1) Define a concrete event
public sealed record ConversationAssigned : PushEvent
{
    public required string ConversationId { get; init; }
    public override string EventType => "conversation.assigned";
}

// 2) Register services
var services = new ServiceCollection();
services.AddLogging();
services.AddVerbaraPush(options =>
{
    options.BufferCapacity = 512;
    options.BackpressureStrategy = BackpressureStrategy.DropOldest;
});

using var sp = services.BuildServiceProvider();
var bus      = sp.GetRequiredService<IPushEventBus>();
var filter   = sp.GetRequiredService<IEventDeliveryFilter>();
var registry = sp.GetRequiredService<ISubscriptionRegistry>();

// 3) Register a subscriber and subscribe to a typed stream
var subscriber = new SubscriberContext(
    TenantId:    "tenant-1",
    UserId:      "user-42",
    Roles:       new HashSet<string> { "agent" },
    Permissions: new HashSet<string> { "conversation:read" });

using var _registration = registry.Register(subscriber);

using var subscription = bus.OfType<ConversationAssigned>().Subscribe(evt =>
{
    if (filter.IsDeliverableToSubscriber(evt, subscriber))
    {
        Console.WriteLine($"{evt.EventType}: {evt.ConversationId}");
    }
});

// 4) Publish
await bus.PublishAsync(new ConversationAssigned
{
    ConversationId = "c-123",
    Metadata = new PushEventMetadata(
        TenantId:      "tenant-1",
        UserId:        "user-42",
        OccurredAt:    DateTimeOffset.UtcNow,
        CorrelationId: null),
});
```

## Contract

### `PushEventBusOptions`

| Setting | Default | Description |
|---------|---------|-------------|
| `BufferCapacity` | `256` | Max in-flight events before the backpressure strategy kicks in. Must be `>= 1`. |
| `BackpressureStrategy` | `DropOldest` | Behavior when the buffer is full. |

### `BackpressureStrategy`

- **`DropOldest`** *(default)* — evict the oldest buffered event to make room for the new one. Favors freshness; appropriate for live UI streams where stale events are worthless.
- **`DropNewest`** — evict the most recently *buffered* event to admit the new one (the BCL `DropNewest` mode). The oldest buffered events survive; the event being published is always enqueued.
- **`Block`** — await buffer space. Use only when publishers can tolerate backpressure (batch pipelines, not hot request paths).

Every event a full buffer evicts under `DropOldest` or `DropNewest` increments `asterisk.push.events.dropped` once, tagged `reason="buffer_full"`; `Block` never evicts. A publish that races the bus's disposal is counted with `reason="writer_closed"`.

## Observability

The package exposes a `System.Diagnostics.Metrics.Meter` named **`Verbara.Sdk.Push`** with the following instruments:

| Instrument | Kind | Description |
|------------|------|-------------|
| `asterisk.push.events.published` | Counter&lt;long&gt; | Events accepted by `PublishAsync`. |
| `asterisk.push.events.delivered` | Counter&lt;long&gt; | Events dispatched to at least one observer. |
| `asterisk.push.events.dropped`   | Counter&lt;long&gt; | Events discarded (tag: `reason=buffer_full\|writer_closed`). |
| `asterisk.push.sse.events.dropped` | Counter&lt;long&gt; | Event frames an SSE connection of `Verbara.Sdk.Push.AspNetCore` dropped at its per-connection bound (each reported to that client in a `.gap` frame). |
| `asterisk.push.subscribers.active` | ObservableGauge&lt;int&gt; | Current active subscriptions (bound via `PushMetrics.BindActiveSubscribersGauge`). |

Wire into OpenTelemetry with `meterProvider.AddMeter("Verbara.Sdk.Push")`.

### Tracing

The bus also starts spans on an `ActivitySource` named **`Verbara.Sdk.Push`**; register it with
`tracerProvider.AddSource("Verbara.Sdk.Push")`.

- **Publish.** `PublishAsync` starts `push publish <eventType>` (`Producer`) under the publisher's current
  activity, and records that activity's W3C `traceparent` in the event's `Metadata.TraceContext` when the
  publisher left it empty. A `TraceContext` the publisher set explicitly is kept as it is.
- **Deliver.** The dispatch loop starts `push deliver <eventType>` (`Internal`) as a **child of the event's
  `TraceContext`**. Every span a subscriber starts while handling the event — a webhook POST's HTTP client
  span, a NATS publish — is therefore in the publisher's trace, and the `traceparent` written on the wire
  (HTTP header, NATS header) belongs to that trace:

  ```text
  api.handler (your activity)
  ├── push publish order.created
  └── push deliver order.created
      └── subscriber spans (HTTP POST, NATS publish, …)
  ```

- **Root case.** An event with no `TraceContext`, or one that does not parse as a W3C `traceparent`, is
  delivered under a root `push deliver` span, as before; the event is never dropped for it.
- **Who built the bus does not matter.** The dispatch loop starts without the constructing code's ambient
  activity, so a bus first resolved inside a host start-up span or inside the first HTTP request never puts
  later deliveries in that trace.
- **Sampling.** Because the delivery is now a child of the publisher's span, a parent-based sampler makes
  the delivery (and the subscriber spans inside it) follow the publisher's sampling decision instead of
  deciding afresh at a root.
- **Cost.** With no listener on the source, no span is started and the trace context is not parsed.

## AOT

This package is **Native AOT compatible**. The shipping build verifies zero trim warnings (`IL2026` / `IL2070` / `IL2075` / `IL3050` / ...) via the repo's `AotCanary` publish (`tools/verify-aot.sh`). No reflection, no `DataAnnotations` runtime validator, no dynamic code.

## Relation with Verbara.Sdk.Pro.Push

This package provides **in-memory primitives** suitable for single-node hosts. For NATS-backed multi-node fan-out in the MIT surface, see **`Verbara.Sdk.Push.Nats`** (available since v1.12). For cluster-wide fan-out over Redis pub/sub or Postgres LISTEN/NOTIFY backplanes (neither stores events for replay), advanced authorization, and enterprise observability, see **`Verbara.Sdk.Pro.Push`** — both build on top of this package's abstractions, so the contract is forward-compatible.

## Links

- Repository: [github.com/verbara/Verbara.Sdk](https://github.com/verbara/Verbara.Sdk)
- Parent SDK README: [../../README.md](../../README.md)
- Changelog: [../../CHANGELOG.md](../../CHANGELOG.md)

Licensed under the MIT License.

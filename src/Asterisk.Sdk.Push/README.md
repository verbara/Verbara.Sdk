# Asterisk.Sdk.Push

Real-time push primitives for .NET. AOT-safe, host-agnostic, MIT licensed.

## What it does

- Typed, in-memory **event bus** (`IPushEventBus`) backed by a bounded `System.Threading.Channels.Channel<T>` with configurable backpressure.
- **Delivery filter** (`IEventDeliveryFilter`) that enforces tenant isolation and optional per-user targeting before an event reaches a subscriber.
- **Subscription registry** (`ISubscriptionRegistry`) for tracking active subscribers per tenant with automatic cleanup on disposal.
- First-class **diagnostics** via `System.Diagnostics.Metrics` (meter name `Asterisk.Sdk.Push`).

No ASP.NET Core dependency. No reflection. Trim-safe.

## Install

```
dotnet add package Asterisk.Sdk.Push
```

## Quick start

```csharp
using Asterisk.Sdk.Push.Bus;
using Asterisk.Sdk.Push.Delivery;
using Asterisk.Sdk.Push.Events;
using Asterisk.Sdk.Push.Hosting;
using Asterisk.Sdk.Push.Subscriptions;
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
services.AddAsteriskPush(options =>
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
- **`DropNewest`** — refuse to enqueue the new event, preserving the buffered backlog. Favors FIFO ordering.
- **`Block`** — await buffer space. Use only when publishers can tolerate backpressure (batch pipelines, not hot request paths).

Dropped events increment `asterisk.push.events.dropped` with a `reason="buffer_full"` tag.

## Observability

The package exposes a `System.Diagnostics.Metrics.Meter` named **`Asterisk.Sdk.Push`** with the following instruments:

| Instrument | Kind | Description |
|------------|------|-------------|
| `asterisk.push.events.published` | Counter&lt;long&gt; | Events accepted by `PublishAsync`. |
| `asterisk.push.events.delivered` | Counter&lt;long&gt; | Events dispatched to at least one observer. |
| `asterisk.push.events.dropped`   | Counter&lt;long&gt; | Events discarded (tag: `reason=buffer_full\|filter_rejected`). |
| `asterisk.push.subscribers.active` | ObservableGauge&lt;int&gt; | Current active subscriptions (bound via `PushMetrics.BindActiveSubscribersGauge`). |

Wire into OpenTelemetry with `meterProvider.AddMeter("Asterisk.Sdk.Push")`.

## AOT

This package is **Native AOT compatible**. The shipping build verifies zero trim warnings (`IL2026` / `IL2070` / `IL2075` / `IL3050` / ...) via the repo's `AotCanary` publish (`tools/verify-aot.sh`). No reflection, no `DataAnnotations` runtime validator, no dynamic code.

## Relation with Asterisk.Sdk.Pro.Push

This package provides **in-memory primitives** suitable for single-node hosts. For NATS-backed multi-node fan-out in the MIT surface, see **`Asterisk.Sdk.Push.Nats`** (available since v1.12). For cluster-wide subscription routing over durable backplanes (Redis / Postgres LISTEN/NOTIFY), advanced authorization, and enterprise observability, see **`Asterisk.Sdk.Pro.Push`** — both build on top of this package's abstractions, so the contract is forward-compatible.

## Links

- Repository: [github.com/ipcom/Asterisk.Sdk](https://github.com/ipcom/Asterisk.Sdk)
- Parent SDK README: [../../README.md](../../README.md)
- Changelog: [../../CHANGELOG.md](../../CHANGELOG.md)

Licensed under the MIT License.

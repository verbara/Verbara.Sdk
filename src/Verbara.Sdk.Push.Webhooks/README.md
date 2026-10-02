# Verbara.Sdk.Push.Webhooks

Outbound HTTP webhook delivery for `Verbara.Sdk.Push`. Consumes events from the in-process Push bus, matches them against `WebhookSubscription` topic patterns, and POSTs to configured URLs with HMAC-SHA256 signing and exponential retry/backoff.

## Usage

```csharp
using Verbara.Sdk.Push.Topics;
using Verbara.Sdk.Push.Webhooks;

builder.Services.AddVerbaraPush()
                .AddVerbaraPushWebhooks(opts =>
                {
                    opts.MaxRetries = 5;
                    opts.InitialDelay = TimeSpan.FromSeconds(1);
                    opts.MaxDelay = TimeSpan.FromSeconds(60);
                    opts.TimeoutPerAttempt = TimeSpan.FromSeconds(10);
                });

// Runtime registration:
var store = app.Services.GetRequiredService<IWebhookSubscriptionStore>();
await store.AddAsync(new WebhookSubscription
{
    Id = "crm-prod",
    TopicPattern = TopicPattern.Parse("calls.**"),
    TargetUrl = new("https://crm.example.com/hooks/calls"),
    Secret = "<shared secret>"
});
```

## Accepted option values

`WebhookDeliveryOptions` is checked when the options are first resolved (at the latest when the host builds the
`WebhookDeliveryService` hosted service at start) and again by both `WebhookDeliveryService` constructors. A value
delivery could never use stops the host from starting instead of losing deliveries later:

| Option | Accepted | Default |
|--------|----------|---------|
| `MaxRetries` | `0` or more | `5` |
| `InitialDelay` | `TimeSpan.Zero` to `int.MaxValue` ms (about 24.8 days) | 1 s |
| `MaxDelay` | `InitialDelay` to `int.MaxValue` ms | 60 s |
| `TimeoutPerAttempt` | greater than zero and at most `int.MaxValue` ms, or `Timeout.InfiniteTimeSpan` | 10 s |

Through `AddVerbaraPushWebhooks` a rejected value fails with an `OptionsValidationException` that lists every
offending option, for example
`MaxDelay: MaxDelay = 00:00:00.5000000 cannot be used by webhook delivery: it must be at least InitialDelay.`
A service constructed directly throws `ArgumentOutOfRangeException` whose `ParamName` is the first offending option
(in the order `MaxRetries`, `InitialDelay`, `MaxDelay`, `TimeoutPerAttempt`).

The service reads the options object it was built with, so a value changed on that object after start is not
re-validated. If such a change makes a retry delay unusable, the delivery that needed it is dead-lettered
(`deliveries.dead_letter`) with one `Error` entry (EventId 8) naming the subscription, the event type and the
exception; other deliveries and later events are unaffected.

## Delivery headers

| Header | Value |
|--------|-------|
| `Content-Type` | `application/json` |
| `X-Signature` | `sha256=<hex>` (absent if subscription has no secret) |
| `X-Event-Type` | `PushEvent.EventType` |
| `User-Agent` | `WebhookDeliveryOptions.UserAgent` (default `Verbara.Sdk.Push.Webhooks/1.0`) |
| `traceparent` | `PushEventMetadata.TraceContext` (absent if null) |

Extra per-subscription headers are appended from `WebhookSubscription.Headers`.

## Extension points

- **Custom payload shape:** implement `IWebhookPayloadSerializer` and register as singleton before `AddVerbaraPushWebhooks`.
- **Custom signature:** implement `IWebhookSigner` (e.g., JWT, asymmetric signatures) and register as singleton.
- **Durable subscriptions:** implement `IWebhookSubscriptionStore` (SQL/Redis/Postgres) and register as singleton. The default `InMemoryWebhookSubscriptionStore` is process-local.

## Observability

Counters on `Verbara.Sdk.Push.Webhooks` meter:

- `asterisk.push.webhooks.deliveries.succeeded`
- `asterisk.push.webhooks.deliveries.failed`
- `asterisk.push.webhooks.deliveries.retried`
- `asterisk.push.webhooks.deliveries.dead_letter`
- `asterisk.push.webhooks.circuit.opened`
- `asterisk.push.webhooks.circuit.skipped`

`deliveries.dead_letter` counts deliveries that failed all `MaxRetries + 1` attempts and were dropped, and deliveries that ended early because their retry delay could not be computed or waited, or because they failed outside their attempts (for example in the payload serializer). It is an operational signal, not a queue: nothing is stored and nothing can be replayed. A delivery skipped because its URL's circuit is open is dropped without an attempt and counted in `circuit.skipped`, not `dead_letter`. Retries still in flight when the host stops are lost without being counted, and every counter starts again from zero in the new process.

Failures logged at `Error` (category `Verbara.Sdk.Push.Webhooks.WebhookDeliveryService`):

| EventId | When |
|---------|------|
| 1 | the bus observer received an error |
| 2 | the subscription store could not be enumerated |
| 5 | a delivery exhausted its retries (dead-lettered) |
| 8 | a delivery's retry delay could not be computed or waited (dead-lettered) |
| 9 | an event could not be dispatched, for example because its `TopicPath` does not parse; later events are unaffected |
| 10 | a delivery failed outside its attempts, for example in the payload serializer (dead-lettered) |

Neither the dispatch nor a delivery can leave a faulted task behind: every failure ends in one of these entries.

Enroll via `Verbara.Sdk.OpenTelemetry` — `WithAllSources()` includes this meter automatically; it is already registered in `VerbaraTelemetry.MeterNames`.

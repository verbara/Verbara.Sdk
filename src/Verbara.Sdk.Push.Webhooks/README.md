# Asterisk.Sdk.Push.Webhooks

Outbound HTTP webhook delivery for `Asterisk.Sdk.Push`. Consumes events from the in-process Push bus, matches them against `WebhookSubscription` topic patterns, and POSTs to configured URLs with HMAC-SHA256 signing and exponential retry/backoff.

## Usage

```csharp
using Asterisk.Sdk.Push.Topics;
using Asterisk.Sdk.Push.Webhooks;

builder.Services.AddAsteriskPush()
                .AddAsteriskPushWebhooks(opts =>
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

## Delivery headers

| Header | Value |
|--------|-------|
| `Content-Type` | `application/json` |
| `X-Signature` | `sha256=<hex>` (absent if subscription has no secret) |
| `X-Event-Type` | `PushEvent.EventType` |
| `User-Agent` | `WebhookDeliveryOptions.UserAgent` (default `Asterisk.Sdk.Push.Webhooks/1.0`) |
| `traceparent` | `PushEventMetadata.TraceContext` (absent if null) |

Extra per-subscription headers are appended from `WebhookSubscription.Headers`.

## Extension points

- **Custom payload shape:** implement `IWebhookPayloadSerializer` and register as singleton before `AddAsteriskPushWebhooks`.
- **Custom signature:** implement `IWebhookSigner` (e.g., JWT, asymmetric signatures) and register as singleton.
- **Durable subscriptions:** implement `IWebhookSubscriptionStore` (SQL/Redis/Postgres) and register as singleton. The default `InMemoryWebhookSubscriptionStore` is process-local.

## Observability

Counters on `Asterisk.Sdk.Push.Webhooks` meter:

- `asterisk.push.webhooks.deliveries.succeeded`
- `asterisk.push.webhooks.deliveries.failed`
- `asterisk.push.webhooks.deliveries.retried`
- `asterisk.push.webhooks.deliveries.dead_letter`

Enroll via `Asterisk.Sdk.OpenTelemetry` — `WithAllSources()` includes this meter automatically once the meter name is added to `AsteriskTelemetry.MeterNames`.

# Verbara.Sdk.Push.AspNetCore

ASP.NET Core SSE delivery endpoints for Verbara.Sdk.Push. First SDK package with an AspNetCore `FrameworkReference` dependency.

## Features

- SSE streaming endpoint (`GET {prefix}/stream`) with topic filtering
- Tenant isolation via `tenantId` JWT claim
- Authorization check via `ISubscriptionAuthorizer`
- Delivery check via `IEventDeliveryFilter`
- 15-second heartbeat to keep connections alive through proxies
- AOT-compatible (no reflection)

## Usage

```csharp
// Register services
builder.Services.AddVerbaraPushAspNetCore();

// Map endpoint (default prefix: /api/v1/push)
app.MapPushEndpoints();

// Or with a custom prefix:
app.MapPushEndpoints("/push");
```

## Client usage

```
GET /api/v1/push/stream?topic=queue.*.updated&topic=agent.**
Authorization: Bearer <jwt-with-tenantId-claim>
Accept: text/event-stream
```

### What a stream request is answered

The endpoint decides the answer before it writes any event-stream byte and before it subscribes to the bus.
Every requested `topic` is parsed first; then `ISubscriptionAuthorizer` is asked about each named topic, and
only about those. A request with no `topic` (or only blank ones) is a request for `**`, authorized like any
other topic.

| Request | Status | Body / stream |
|---------|--------|---------------|
| No `tenantId` claim | `400 Bad Request` | `Missing tenantId claim.` |
| Any `topic` fails to parse (alone or next to valid ones) | `400 Bad Request` | `text/plain`, the invalid topics percent-encoded; the authorizer is asked nothing |
| Every requested topic denied (no topic = `**` denied) | `403 Forbidden` | `text/plain`, the fixed text `Subscription denied.` |
| At least one requested topic allowed | `200 OK` | `text/event-stream` with the events of the allowed topics only |

A denied topic is never replaced by a wider pattern: an authorizer that denies `billing.**` keeps the client
off `billing.**` even when it would allow `**`. The authorizer's `Reason` is never returned to the client; the
endpoint logs one `Warning` per refused request (category `Verbara.Sdk.Push.AspNetCore.SsePushEndpoints`) with
the tenant, the user, the denied topics percent-encoded and the first reason.

When some requested topics are allowed and others denied, the stream is served and the response header
`X-Push-Denied-Topics` lists the denied topics exactly as requested, each percent-encoded (`Uri.EscapeDataString`),
joined by commas — for example `X-Push-Denied-Topics: billing.%2A%2A`. The header is absent when nothing was
denied. Two caveats for browser clients:

- `EventSource` cannot read response headers at all. A browser client that needs to know about a partial
  denial uses `fetch` with a streamed body instead.
- A cross-origin `fetch` client can read the header only when the host's CORS policy lists it in
  `Access-Control-Expose-Headers` (for example `policy.WithExposedHeaders("X-Push-Denied-Topics")`).
  Server-side clients always can.

Events are emitted in SSE format:

```
event: queue.42.updated
data: {"eventType":"queue.42.updated","metadata":{...},...}

: heartbeat
```

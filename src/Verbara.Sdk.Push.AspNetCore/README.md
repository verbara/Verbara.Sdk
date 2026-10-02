# Verbara.Sdk.Push.AspNetCore

ASP.NET Core SSE delivery endpoints for Verbara.Sdk.Push. First SDK package with an AspNetCore `FrameworkReference` dependency.

## Features

- SSE streaming endpoint (`GET {prefix}/stream`) with topic filtering
- Tenant isolation via `tenantId` JWT claim
- Authorization check via `ISubscriptionAuthorizer`
- Delivery check via `IEventDeliveryFilter`
- 15-second heartbeat to keep connections alive through proxies
- One writer per connection over a bounded queue (1 MiB by default): a slow client never slows the bus or other
  clients, and is told how many events it lost
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
| At least one requested topic allowed | `200 OK` | `text/event-stream` with the events whose name matches an allowed topic only (see below) |

A denied topic is never replaced by a wider pattern: an authorizer that denies `billing.**` keeps the client
off `billing.**` even when it would allow `**`. The authorizer's `Reason` is never returned to the client; the
endpoint logs one `Warning` per refused request (category `Verbara.Sdk.Push.AspNetCore.SsePushEndpoints`) with
the tenant, the user, the denied topics percent-encoded and the first reason.

An event is matched by its **name**: its `TopicPath`, or its `EventType` when the topic path is null or
empty. A topic-less `billing.invoice.created` event therefore reaches a stream allowed `billing.**` and no
stream allowed only `queue.**`, and `{self}` resolves in the event type as it does in a topic path. An event
type that is not a valid topic (for example `billing..x`, or `.gap`) is never matched by its text: a topic-less
event of such a type reaches only streams allowed `**`. An event whose non-empty `TopicPath` is not a valid
topic (for example `a..b`) reaches no stream, `**` included, and never falls back to its event type; the
endpoint logs one `Warning` per such event, however many streams evaluated it, with the tenant and the event
type and topic path percent-encoded. For a **null** topic path this is the convention the NATS bridge uses to
build its subject (`TopicPath ?? EventType`).

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

The `event:` value is the name the event was matched by: its topic path, or its event type when the topic
path is null or empty. A CR or LF in it is written
percent-encoded (`%0D`, `%0A`), so an event can never add lines to its frame; `data:` is JSON on one line.

### Heartbeat, the per-connection bound and `.gap`

The response headers are sent as soon as the request is admitted. Every write is asynchronous, so the stream
works on a default Kestrel host (`AllowSynchronousIO = false`). Each connection has one writer: events and the
`: heartbeat` comment (every 15 seconds) are queued as whole frames and written one at a time, so frames never
interleave and the heartbeat keeps running while a slow client is being written to.

Each connection's queue is bounded in bytes — the frames' UTF-8 size, as written on the wire —
**1 MiB by default**. The bus hands an event to the connection without ever waiting for the client. When an
event would take the queue past the bound, the **oldest** queued frames are dropped, and before its next event
the client receives one marker carrying the number of event frames dropped:

```
event: .gap
data: {"dropped":3413}
```

Every event after the marker is newer than every dropped one. The memory behind the queue is bounded; the
freshness of a slow client is not: the operating system's and the server's socket buffers still hold frames
written before the drops, so a client that keeps falling behind keeps receiving `.gap` markers.

- **Heartbeats are outside the bound.** A heartbeat is not queued while drops are waiting to be reported or
  while it does not fit, and is never counted in `dropped`. A connection below the bound gets its heartbeats.
- **A single event larger than the bound is still delivered**, alone: everything queued before it is dropped
  and reported.
- **`.gap` is reserved.** An event whose name would be `.gap` (an `EventType` of `.gap` with no topic path) is
  written as `event: %2Egap`. `.gap` is not a valid topic, so such an event reaches only streams allowed `**`. An `EventSource` client listens with `addEventListener('.gap', …)`; a client
  that does not listen ignores it.
- **Operators see the drops** on the `asterisk.push.sse.events.dropped` counter of the `Verbara.Sdk.Push` meter
  (one per dropped event frame) and in one `Warning` per run of drops (category
  `Verbara.Sdk.Push.AspNetCore.SsePushEndpoints`), never one per frame.

Set the bound with the public option `SsePushStreamOptions.MaxQueuedBytesPerConnection` (bytes, at least 1):

```csharp
builder.Services.AddVerbaraPushAspNetCore();
builder.Services.Configure<SsePushStreamOptions>(o => o.MaxQueuedBytesPerConnection = 4 * 1024 * 1024);
```

`AddVerbaraPushAspNetCore` validates it when the host starts. A host set up with `AddVerbaraPush()` only gets
the defaults, or the value it configures, without that start-up check.

A client disconnect or reset ends the stream: the heartbeat stops, the connection unsubscribes from the bus and
the request completes, logged at `Debug` only.

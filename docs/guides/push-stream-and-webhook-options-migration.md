# Migrating to a push stream that delivers only what was allowed

Required by the release cadence: a minor that carries a breaking change ships a migration guide.

Five behaviours changed in `Verbara.Sdk.Push`, `Verbara.Sdk.Push.AspNetCore` and `Verbara.Sdk.Push.Webhooks`:
**which SSE stream requests are served**, **which events a served stream delivers and to whom**, **how the stream
writes**, **which trace a delivery belongs to**, and **which webhook delivery options are accepted**. One public
type was added, `SsePushStreamOptions`, with five properties: `MaxQueuedBytesPerConnection` and four claim-type
lists, `TenantIdClaimTypes`, `UserIdClaimTypes`, `RoleClaimTypes` and `PermissionClaimTypes`. No existing
signature changed, so nothing stops compiling.

## What happened

- **A denied SSE request could receive everything.** When every `topic` a client named was denied by
  `ISubscriptionAuthorizer`, or failed to parse, the endpoint asked the authorizer about `**` instead and, if that was
  allowed, subscribed the client to every event of its tenant. With a deny-list authorizer (deny `billing.**`, allow
  the rest), asking exactly for `billing.**` delivered `billing.**` and everything else. The package README stated
  this fallback as the contract. Tenant and user isolation held: the delivery filter never let another tenant's
  event, or an event targeted at another user, through.
- **An event published without a topic path reached every admitted stream of its tenant.** The stream matched an
  event's `TopicPath` against the topics the client was allowed; an event whose `TopicPath` was null passed every
  pattern. A client allowed only `queue.**` therefore also received every topic-less event of its tenant, whatever
  its type. An event whose `TopicPath` was not a valid topic (for example `a..b`) was skipped by every stream, and
  nothing said so.
- **The authorizer never saw the subscriber's roles or permissions, and on a default `JwtBearer` host no user.**
  The stream built the subscriber from the literal claims `tenantId` and `sub`, and always with empty `Roles` and
  `Permissions`. ASP.NET Core's `JwtBearer` handler renames `sub` to `ClaimTypes.NameIdentifier` by default
  (`MapInboundClaims = true`), so the user was null: no event targeted at a user, and no `{self}` pattern, was ever
  delivered on such a host, and an authorizer that decides by role or permission had nothing to decide on.
- **On a default Kestrel host the stream did not work at all.** The endpoint flushed synchronously, which Kestrel
  refuses unless `AllowSynchronousIO = true`, so every stream request answered `500`. The authorization defect was
  therefore reachable only on hosts that had turned synchronous I/O on; fixing the write path alone would have
  exposed it on every host, so both ship together.
- **One slow client could stall the bus.** The bus handed each event to the stream with a synchronous write on the
  bus's single dispatch loop, and the heartbeat wrote to the same writer from another thread. A client that stopped
  reading held every subscriber of the bus behind it, and a slow one could receive torn frames.
- **A delivery span started a trace of its own.** `push deliver` was started with no parent, so a webhook POST or a
  NATS publish made while delivering an event was not in the trace of the request that published it. A bus first
  resolved inside an activity (a host start-up span, the first HTTP request) put every later delivery in that one
  trace.
- **A webhook delivery option that could never work was accepted.** With `MaxDelay` below `InitialDelay`, a negative
  `InitialDelay`, or a delay longer than .NET can wait, an event whose first attempt failed was never retried and
  nothing said so: no `Error` entry, no `dead_letter` count, only an exception in a task nobody observed. A negative
  `MaxRetries`, a zero `TimeoutPerAttempt` or one above `int.MaxValue` ms delivered nothing either.

## What the SDK does now

### 1. A stream request is served what it asked for and was allowed, or refused

The endpoint decides before it writes any event-stream byte and before it subscribes to the bus. Every requested
`topic` is parsed first; the authorizer is then asked about each named topic, and only about those. **A request
with no `topic` is a request for `**`**, authorized like any other topic. A denied topic is never replaced by a
wider pattern.

| Request | Status | Body / stream |
|---------|--------|---------------|
| No tenant claim (no value under any of `TenantIdClaimTypes`, `tenantId` by default) | `400 Bad Request` | `Missing tenantId claim.` (unchanged text, whatever the list holds) |
| Any `topic` fails to parse, alone or next to valid ones | `400 Bad Request` | `text/plain`, the invalid topics percent-encoded; the authorizer is asked nothing |
| Every requested topic denied (no topic = `**` denied) | `403 Forbidden` | `text/plain`, the fixed text `Subscription denied.` |
| At least one requested topic allowed | `200 OK` | `text/event-stream`, the events whose name matches an allowed topic only (section 2) |

What each case answered before and answers now (`AllowSynchronousIO = true`; on a default Kestrel host every row
answered `500` before):

| Request | Authorizer allows | Before | Now |
|---------|-------------------|--------|-----|
| no `topic` | everything | `200`, every event | `200`, every event |
| `billing.**` | only `**` | `200`, every event | `403` |
| `billing.**` | everything except `billing.*` and `queue.*` | `200`, every event, `billing.**` included | `403` |
| `queue.**` + `billing.**` | only `**` | `200`, every event | `403` |
| `billing.**` | only `agent.u1.*` | `200`, an empty stream | `403` |
| no `topic` | only `agent.u1.*` | `200`, an empty stream | `403` |
| `queue.**` + `billing.**` | only `queue.*` | `200`, `queue.**` only, the denial silent | `200`, `queue.**` only, `X-Push-Denied-Topics: billing.%2A%2A` |
| `a..b` (does not parse) | everything | `200`, every event | `400` |
| `queue.**` + `a..b` | everything | `200`, `queue.**` only, `a..b` dropped silently | `400` |
| `a..b` + `billing.**` | everything except `billing.*` and `queue.*` | `200`, every event | `400` |

**The partial-denial header.** When some requested topics are allowed and others denied, the stream is served and
`X-Push-Denied-Topics` lists the denied topics exactly as requested, each percent-encoded
(`Uri.EscapeDataString`), joined by commas. It is absent when nothing was denied. Browser clients:

- `EventSource` cannot read response headers. A browser client that needs to know about a partial denial uses
  `fetch` with a streamed body.
- A cross-origin `fetch` client can read the header only when the host's CORS policy lists it in
  `Access-Control-Expose-Headers`, for example `policy.WithExposedHeaders("X-Push-Denied-Topics")`.

**The authorizer's reason stays on the server.** The `403` body is the fixed text; the endpoint logs one `Warning`
per refused request (category `Verbara.Sdk.Push.AspNetCore.SsePushEndpoints`) with the tenant, the user, the denied
topics percent-encoded and the authorizer's first reason.

### 2. A topic-less event reaches only the streams allowed to see its type

An event is matched by its **name**: its `TopicPath`, or its `EventType` when the topic path is null or empty.

| The event's `TopicPath` | Matched by | When that name is not a valid topic |
|-------------------------|------------|-------------------------------------|
| a valid topic | the topic path | — |
| null or empty | the event type, `{self}` resolved as in a topic path | delivered only to streams allowed `**` |
| non-empty, not a valid topic | — | delivered to no stream, `**` included; one `Warning` |

- A topic-less `billing.invoice.created` reaches a stream allowed `billing.**`, and no longer a stream allowed only
  `queue.**`.
- An event type that is not a valid topic (for example `billing..x`) is never matched by its text: a topic-less
  event of that type reaches only `**` streams.
- An event whose non-empty `TopicPath` does not parse never falls back to its event type. The endpoint logs one
  `Warning` per such event, however many streams evaluated it (category
  `Verbara.Sdk.Push.AspNetCore.SsePushEndpoints`, EventId 4), with the tenant and the event type and topic path
  percent-encoded. An event no stream evaluated is not logged.
- The `event:` name on the wire is the name the event was matched by, so a topic-less event is named by its type
  (as before for a null topic path; an empty one is now named by its type too).
- Tenant and user isolation are unchanged: the delivery filter still decides first.

For a **null** topic path this is the convention the NATS bridge already used to build its subject
(`TopicPath ?? EventType`). It is not the same for an **empty** one: the bridge publishes an event with an empty
topic path to the bare subject prefix, not under its event type. **Webhooks are not changed**: a webhook
subscription still never receives an event whose topic path is null or empty.

### 3. The stream knows who the subscriber is, from claim types you can configure

The tenant, user, roles and permissions handed to `ISubscriptionAuthorizer` and `IEventDeliveryFilter` are read
from the authenticated principal through four lists on `SsePushStreamOptions`:

| Option | Default | Read as |
|--------|---------|---------|
| `TenantIdClaimTypes` | `tenantId` | the first listed type, in list order, with a non-empty value |
| `UserIdClaimTypes` | `sub`, `ClaimTypes.NameIdentifier` | the first listed type, in list order, with a non-empty value |
| `RoleClaimTypes` | `ClaimTypes.Role`, `role`, `roles` | every value of every listed type, plus every value of each identity's own `RoleClaimType` |
| `PermissionClaimTypes` | `permission` | every value of every listed type |

Claim types are compared ignoring case, values ordinally and whole (never split on spaces or commas); empty values
are ignored. With the defaults, `JwtBearer` finds the user and the roles whether `MapInboundClaims` is true or
false. `AddVerbaraPushAspNetCore` validates the lists at start: the tenant and user lists must not be empty, and
no list may hold a null, empty or whitespace entry. The role and permission lists may be empty.

`tid` is deliberately not a default tenant claim type: in Microsoft Entra ID it names the directory, not your
application's tenant.

### 4. The stream writes asynchronously, through one bounded writer per connection

The response headers are sent as soon as the request is admitted, and every write is asynchronous, so the stream
works on a default Kestrel host. Each connection has one writer: events and the `: heartbeat` comment (every 15
seconds) are queued as whole frames and written one at a time. Frames never interleave, the heartbeat keeps running
while a slow client is written to, and the bus never waits for a client.

Each connection's queue is bounded in bytes (the frames' UTF-8 size, as written), **1 MiB by default**. When an event
would take the queue past the bound, the **oldest** queued frames are dropped, and before its next event the client
receives one marker with the number of event frames dropped:

```
event: .gap
data: {"dropped":3413}
```

- Every event after the marker is newer than every dropped one.
- Heartbeats are outside the bound: one is not queued while drops are waiting to be reported or while it does not
  fit, and it is never counted in `dropped`.
- A single event larger than the bound is still delivered, alone; what was queued before it is dropped and reported.
- `.gap` is reserved: an event whose name would be `.gap` is written as `event: %2Egap`. `.gap` is not a valid
  topic, so a topic-less event of that type reaches only `**` streams.
- A CR or LF in an `event:` value is written percent-encoded (`%0D`, `%0A`), so an event can never add lines to its
  frame.
- Operators see the drops on the `asterisk.push.sse.events.dropped` counter of the `Verbara.Sdk.Push` meter, one per
  dropped event frame, and in one `Warning` per run of drops, never one per frame.
- A client disconnect or reset ends the stream cleanly: the heartbeat stops, the connection unsubscribes from the
  bus, and the request completes, logged at `Debug` only.

The bound is the new public option `SsePushStreamOptions.MaxQueuedBytesPerConnection` (bytes, at least 1).
`AddVerbaraPushAspNetCore` validates it when the host starts.

### 5. A delivery continues the publisher's trace

`push deliver <eventType>` is now a child of the event's `Metadata.TraceContext`, the W3C `traceparent` that
`PublishAsync` records from the publisher's activity. Every span a subscriber starts while handling the event is
in the publisher's trace, and so is the `traceparent` written on the wire (the webhook's HTTP header, the NATS
header):

```text
Before                                         Now
trace 1  api.handler                           api.handler
         └── push publish order.created        ├── push publish order.created
trace 2  push deliver order.created  (root)    └── push deliver order.created
         └── HTTP POST, NATS publish, …            └── HTTP POST, NATS publish, …
```

- An event with no `TraceContext`, or one that does not parse, is delivered under a root `push deliver`, as before.
- The dispatch loop no longer inherits the activity of whoever built the bus.
- With no listener on the `Verbara.Sdk.Push` source, nothing is started and nothing is parsed.

### 6. A webhook option that could never deliver is rejected

`WebhookDeliveryOptions` is checked when the options are resolved (at the latest when the host builds the
`WebhookDeliveryService` hosted service at start) and by both `WebhookDeliveryService` constructors:

| Option | Accepted | Default |
|--------|----------|---------|
| `MaxRetries` | `0` or more | `5` |
| `InitialDelay` | `TimeSpan.Zero` to `int.MaxValue` ms (about 24.8 days) | 1 s |
| `MaxDelay` | `InitialDelay` to `int.MaxValue` ms | 60 s |
| `TimeoutPerAttempt` | greater than zero and at most `int.MaxValue` ms, or `Timeout.InfiniteTimeSpan` | 10 s |

- **Through `AddVerbaraPushWebhooks`** the start fails with an `OptionsValidationException` listing every offending
  option, for example
  `MaxDelay: MaxDelay = 00:00:00.5000000 cannot be used by webhook delivery: it must be at least InitialDelay.`
- **A service constructed directly** throws `ArgumentOutOfRangeException` whose `ParamName` is the first offending
  option, in the order `MaxRetries`, `InitialDelay`, `MaxDelay`, `TimeoutPerAttempt`.

If a value changed on the options object after start makes a retry delay unusable, the delivery that needed it is
dead-lettered (`deliveries.dead_letter`) with one `Error` entry (EventId 8). An event that cannot be dispatched (for
example a `TopicPath` that does not parse) is logged at `Error` (EventId 9), and a delivery that fails outside its
attempts is dead-lettered with one `Error` entry (EventId 10). Neither leaves a faulted task behind, and later events
are delivered.

## What you have to do

**Update the packages**, then check the situations below.

### 1. Your authorizer relied on deciding `**`

The old README told you that an authorizer that scopes subscribers "must therefore also decide `**`", because `**`
was the fallback for a denied request. That fallback is gone: `**` is asked only when the client named no topic.

The authorizer decides on the pattern as the client wrote it, and the stream then delivers every event that pattern
matches and the delivery filter lets through. So an authorizer that **allows `**`** still lets a topic-less client
receive every event of its tenant, `billing.*` included. If a subscriber must not see some topics:

- deny `**`, and any wildcard pattern that would match those topics, for that subscriber; or
- write an allow-list: allow only the patterns the subscriber may see (`agent.u1.**`), deny everything else.

```csharp
public sealed class AgentScopedAuthorizer : ISubscriptionAuthorizer
{
    public AuthorizationResult CanSubscribe(SubscriberContext subscriber, TopicPattern requestedPattern)
        => requestedPattern.ToString().StartsWith($"agent.{subscriber.UserId}.", StringComparison.Ordinal)
            ? AuthorizationResult.Allow()
            : AuthorizationResult.Deny("outside the agent's own topics");
}
```

With that authorizer a client that asks for its own topics is served them; one that asks for nothing, or for
`billing.**`, gets `403` instead of an empty stream that never delivers.

### 2. You publish events without a topic path

A topic-less event now reaches only the streams whose allowed topics match its event type. If a client should
keep receiving it:

- publish it with a `TopicPath` the client's topics match (`PushEventMetadata.TopicPath`); or
- have the client subscribe to the event type (`?topic=billing.invoice.created`, or `billing.**`), and let your
  authorizer allow it.

An event type that is not a valid topic reaches only `**` streams; give such events a `TopicPath`. Watch for the
new `Warning` (EventId 4): it names events whose `TopicPath` does not parse and that no stream received.

**Your authorizer now governs event types too.** It is still asked only about the topics a client requested, but
a topic-less event is then matched by its type against those topics, so an authorizer that reasons by prefix
(`billing.` is for finance) also decides which topic-less events of a `billing.`-prefixed type a client sees.
Choose event types that read as topics.

### 3. Your authorizer or tokens use roles, permissions or other claim names

- **An authorizer that decides by role or permission starts working**, and one written in the negative form
  ("deny when the subscriber has role X") **starts denying**: until now `Roles` and `Permissions` were always
  empty. Review it before you update.
- **On a default `JwtBearer` host the user is now found**, so events targeted at a user and `{self}` patterns are
  delivered.
- **Your tokens carry the tenant or permissions under other names?** Add them deliberately:

  ```csharp
  builder.Services.AddVerbaraPushAspNetCore();
  builder.Services.Configure<SsePushStreamOptions>(o =>
  {
      o.TenantIdClaimTypes = ["tenant_id", "tid"];                    // first match wins, in this order
      o.PermissionClaimTypes = ["permission", "permissions", "scp"]; // a plural permissions claim, OAuth scopes
  });
  ```

  Values are taken whole: an `scp` claim of `read write` is one permission, `read write`, not two.

  Assigning a list replaces its default. **Binding from configuration appends** the configured entries after the
  default ones (the configuration binder's array behaviour): a configuration entry `TenantIdClaimTypes:0 = tid` (a
  one-element array in `appsettings.json`) gives `["tenantId", "tid"]`. Set the list in code when the default must
  go.
- A host set up with `AddVerbaraPush()` only is not validated: an unusable tenant list there answers `400`.

### 4. Your clients assumed every request is answered `200`

Handle the refusals:

- `403`: nothing the client asked for is allowed. Retrying the same request gets the same answer while the
  authorizer decides the same way; change the topics or the subscriber's grants.
- `400`: a topic does not parse. The body names it, percent-encoded.
- `200` with `X-Push-Denied-Topics`: part of the request was denied. Read the header when the client can (see the
  browser caveats above).

An `EventSource` client treats a non-`200` answer as a failure and closes; it does not retry a `403` or `400`.

### 5. Your clients read the stream

- **Listen for `.gap`** if missing events matters: `source.addEventListener('.gap', e => …)`, where
  `JSON.parse(e.data).dropped` is the number of events lost before the next one. A client that does not listen ignores
  it. There is no replay; refetch state if you need it.
- **Do not publish an event named `.gap`.** It is delivered as `%2Egap`.
- **Raise the bound** if your clients legitimately fall behind by more than 1 MiB, for example on large events:

  ```csharp
  builder.Services.AddVerbaraPushAspNetCore();
  builder.Services.Configure<SsePushStreamOptions>(o => o.MaxQueuedBytesPerConnection = 4 * 1024 * 1024);
  ```

  The bound is per connection, so the memory it allows grows with the number of slow clients. A host set up with
  `AddVerbaraPush()` only gets the defaults, or the value it configures, without the start-up check.
- If you had turned on `AllowSynchronousIO` only to make the stream work, you can turn it off again.

### 6. You read push traces

Deliveries now appear inside the publishing request's trace instead of as one trace per event (or all inside one
long-gone request). Dashboards or alerts that counted `push deliver` root spans will see fewer roots.

**Sampling:** with a parent-based sampler, a delivery now follows the publisher's sampling decision instead of being
decided afresh at a root.

### 7. Your webhook host no longer starts, or a construction throws

Fix the value the exception names. For example, a `MaxDelay` of `00:00:00.5` under an `InitialDelay` of `00:00:01`
was accepted before, and every delivery that failed once was lost without a trace. Now the start fails naming
`MaxDelay`; raise it to at least the initial delay:

```csharp
builder.Services.AddVerbaraPush()
                .AddVerbaraPushWebhooks(opts =>
                {
                    opts.InitialDelay = TimeSpan.FromSeconds(1);
                    opts.MaxDelay = TimeSpan.FromSeconds(30);
                });
```

If you need no retry at all, set `MaxRetries = 0` rather than an unusable delay. A subscription's own `MaxRetries`
overrides the option, so the delays are checked whatever `MaxRetries` is.

The SDK does not add `ValidateOnStart` for these options; the hosted service's constructor still fails the start.
If you build the service yourself, the constructor is the check.

## What did not change

- The default tenant claim, `tenantId`, and the text of its `400` (the claim type is now configurable).
- Tenant and user isolation: the delivery filter decides per event, as before.
- Webhooks: an event whose topic path is null or empty is still not delivered to any webhook subscription.
- `ISubscriptionAuthorizer`, `IEventDeliveryFilter` and their signatures; the default authorizer still allows
  everything.
- The SSE frame format for every event whose name has no CR or LF and is not `.gap`.
- The heartbeat's 15-second interval.
- The bus's own buffer and its `asterisk.push.events.dropped` counter.
- `PushEventMetadata.TraceContext` capture; a `TraceContext` the publisher set explicitly is kept.
- The webhook option names, types and defaults; the webhook `traceparent` header.

## How to check

- Request the stream with a topic your authorizer denies: the answer is `403`, and the log shows one `Warning` from
  `Verbara.Sdk.Push.AspNetCore.SsePushEndpoints` with the reason.
- Publish a topic-less event of type `billing.invoice.created` with one stream open on `queue.**` and one on
  `billing.**`: only the second receives it, named `billing.invoice.created`.
- Publish an event whose `TopicPath` is `a..b`: no stream receives it and the log shows one `Warning` (EventId 4).
- With an authorizer that records the `SubscriberContext` it is handed, open a stream with your real token: the
  tenant, user, roles and permissions are the ones the token carries.
- On a host with `AllowSynchronousIO` off, request an allowed topic: the answer is `200 text/event-stream` and a
  `: heartbeat` arrives about 15 seconds later.
- Publish an event inside a request with tracing on and `AddSource("Verbara.Sdk.Push")`: `push deliver` and the
  webhook's HTTP span are in that request's trace.
- Start the webhook host: if it starts, every webhook delivery option is usable.

Details per package: [`Verbara.Sdk.Push.AspNetCore` README](../../src/Verbara.Sdk.Push.AspNetCore/README.md) (its
[claim types](../../src/Verbara.Sdk.Push.AspNetCore/README.md#who-a-connection-belongs-to)),
[`Verbara.Sdk.Push` README](../../src/Verbara.Sdk.Push/README.md#tracing),
[`Verbara.Sdk.Push.Webhooks` README](../../src/Verbara.Sdk.Push.Webhooks/README.md#accepted-option-values).

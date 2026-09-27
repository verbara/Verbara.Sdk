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

If the request names no `topic`, or every named pattern is denied by `ISubscriptionAuthorizer` or fails to parse, the stream asks the authorizer about `**` and, if that is allowed, delivers every event the delivery filter lets through. An authorizer that scopes subscribers must therefore also decide `**`.

Events are emitted in SSE format:

```
event: queue.42.updated
data: {"eventType":"queue.42.updated","metadata":{...},...}

: heartbeat
```

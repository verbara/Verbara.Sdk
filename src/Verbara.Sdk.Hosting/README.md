# Verbara.Sdk.Hosting

The recommended entry point to the [Verbara.Sdk](https://github.com/verbara/Verbara.Sdk) family — single `dotnet add` brings in AMI, AGI, ARI, Live, Activities, Sessions, and Config plus a `Microsoft.Extensions.DependencyInjection` extension that wires everything into your `IHost` with one call. Native AOT, zero reflection, MIT licensed.

## What it does

- **`AddVerbara(IConfiguration | Action<VerbaraOptions>)`** — registers `IAmiConnection`, `IAriClient`, `IAgiServer`, the Live API (`VerbaraServer`), `IActivityRegistry`, `ISessionEngine`, and the supporting hosted services. Source-generator-validated.
- **`AddVerbaraAri(Action<AriClientOptions>)`** — registers `IAriClient` and nothing else, for a host that needs only ARI ([below](#ari-only-hosts)).
- **`VerbaraOptions`** — strongly-typed configuration model with `[OptionsValidator]` source-generated validation (no runtime reflection). Bind directly from `appsettings.json` or configure inline.
- **Hosted lifecycle** — `IHostedService` implementations connect AMI on `StartAsync`, drain on `StopAsync`. AGI server, ARI WebSocket, and `VerbaraServer` (Live aggregate) follow the same pattern.
- **Health checks** — `AmiHealthCheck` (`ami`, the same state table as `live`), `LiveHealthCheck` (`live`) and `AgiHealthCheck` (`agi`) auto-registered, `AriHealthCheck` (`ari`, the same state table as `ami`, ARI's `Faulted` as `Unhealthy`) with an `Ari` section; `AddVerbaraMultiServer` adds `VerbaraServerPoolHealthCheck` (`verbara-pool`); all with no tags unless you name them (`VerbaraHealthCheckOptions.Tags`). Expose them at a diagnostics endpoint such as `/health`; point Kubernetes probes at endpoints filtered by tag, and never put an SDK check on the tag your readiness probe reads ([Health endpoint](#health-endpoint)).
- **Multi-server support** — register multiple `VerbaraServer` instances via `VerbaraServerPool` for federated deployments.

This is a **meta-package**: it does not contain its own runtime types. It transitively pulls in `Verbara.Sdk`, `Verbara.Sdk.Ami`, `Verbara.Sdk.Agi`, `Verbara.Sdk.Ari`, `Verbara.Sdk.Live`, `Verbara.Sdk.Activities`, `Verbara.Sdk.Sessions`, and `Verbara.Sdk.Config`. Add Voice AI / Push / OpenTelemetry packages on top as needed.

## Install

```sh
dotnet add package Verbara.Sdk.Hosting
```

## Quick start — bind from config

```csharp
using Verbara.Sdk.Hosting;
using Microsoft.Extensions.Hosting;

var builder = Host.CreateApplicationBuilder(args);
builder.Services.AddVerbara(builder.Configuration);

var host = builder.Build();
await host.RunAsync();
```

```json
{
  "Asterisk": {
    "Ami": { "Hostname": "pbx.example.com", "Username": "admin", "Password": "secret" },
    "Ari": { "BaseUrl": "http://pbx.example.com:8088", "Username": "admin", "Password": "secret", "Application": "my-app" },
    "AgiPort": 4573,
    "HealthCheckTags": ["voice"]
  }
}
```

`Asterisk:Ami` and `Asterisk:Ari` take every option of `AmiConnectionOptions` and `AriClientOptions`, by name (`"MaxReconnectAttempts": 10`, `"ReconnectInitialDelay": "00:00:01"`); values are read with the invariant culture, and one that cannot be read as the option's type makes `AddVerbara` throw an `InvalidOperationException` naming its key. A value of the right type that the options do not accept (with `AutoReconnect` on, a reconnect delay or multiplier the backoff cannot use, for example; see the [high-load tuning guide](../../docs/guides/high-load-tuning.md#reconnection-tuning)) fails the host's start with an `OptionsValidationException` naming the option. The `Ari` section is optional: without it no ARI client is registered. `AgiPort` is the FastAGI server's port. `HealthCheckTags` names the tags every SDK health check carries ([Health endpoint](#health-endpoint)); without it they carry none.

## Quick start — inline configure

```csharp
builder.Services.AddVerbara(options =>
{
    options.Ami.Hostname = "192.168.1.100";
    options.Ami.Username = "admin";
    options.Ami.Password = "secret";
    // Optional: tune reconnect / heartbeat
    options.Ami.ReconnectInitialDelay = TimeSpan.FromSeconds(1);
    options.Ami.HeartbeatInterval = TimeSpan.FromSeconds(30);
});
builder.Services.Configure<VerbaraHealthCheckOptions>(o => o.Tags.Add("voice"));
```

After `host.RunAsync()`, resolve services in your code:

```csharp
var ami = host.Services.GetRequiredService<IAmiConnection>();
var server = host.Services.GetRequiredService<VerbaraServer>();   // Live API aggregate
var ari = host.Services.GetRequiredService<IAriClient>();
```

## ARI-only hosts

`AddVerbara` always registers the AMI connection, and its hosted service dials AMI when the host starts: a failed dial
stops the host. A host that needs only ARI registers it with `AddVerbaraAri` instead:

```csharp
builder.Services.AddVerbaraAri(options =>
{
    options.BaseUrl = "http://pbx.example.com:8088";
    options.Username = "admin";
    options.Password = "secret";
    options.Application = "my-app";
    options.EventsEnabled = false;
});
```

It registers what `AddVerbara` registers for ARI, and no more: the client, its options (every value set in the callback
is delivered, and validated when the host starts), the `ari` health check, the hosted service that connects the client,
and the audio servers when `ConfigureAudioServer` is set. No AMI connection, AGI server or Live server is registered, and
no `ami`, `agi` or `live` check. With `EventsEnabled = false` the client opens no events WebSocket, so the host starts and
stops with no Asterisk reachable; with events on, the client dials at start and a failed dial stops the host.

- The `ari` check asks Asterisk on each run, so on a host with no reachable Asterisk it reads `Unhealthy`. Keep it, like
  every SDK check, off the endpoint your readiness probe reads (see [Health endpoint](#health-endpoint)).
- The ARI client is registered once. Calling `AddVerbaraAri` twice, or on a collection where `AddVerbara` set its `Ari`
  options, throws an `InvalidOperationException` naming both methods. `AddVerbara` with no `Ari` options and
  `AddVerbaraAri` on the same collection is fine.
- The session engine (`AddVerbaraSessions*`) and `AddVerbaraMultiServer` read the Live server, which needs AMI: they need
  `AddVerbara`, not `AddVerbaraAri`.

## Session options from configuration

Each sessions registration accepts the host configuration: `AddVerbaraSessions`, `AddVerbaraSessionsBuilder`,
`AddVerbaraSessionsMultiServer`, and `AddVerbaraSessionsMultiServerBuilder`. Each reads every settable
`SessionOptions` property from the `Sessions` section; the builder variants still return the builder for
`UseRedis(...)` or `UsePostgres(...)`.

```csharp
builder.Services.AddVerbaraSessions(builder.Configuration, options =>
{
    // Runs after binding; override the configured retention for this host.
    options.CompletedRetention = TimeSpan.FromMinutes(1);
});
```

```json
{
  "Sessions": {
    "ReconciliationInterval": "00:00:10",
    "DialingTimeout": "00:01:00",
    "CompletedRetention": "00:05:00",
    "InboundContextPatterns": ["from-carrier"],
    "OutboundContextPatterns": ["to-carrier"]
  }
}
```

For a pool, pass the configuration to `AddVerbaraSessionsMultiServer(builder.Configuration)` after
`AddVerbaraMultiServer()`. A configured context list replaces its default list, including when the provider exposes
an empty list key. An absent list keeps its own default, and an absent `Sessions` section keeps all defaults.
Values use invariant culture; a malformed value throws an `InvalidOperationException` naming its configuration key
when options are resolved. The existing options validator runs on startup, just as with inline configuration.
`"ReconciliationInterval": "-00:00:00.001"` switches the sweep off without starting a timer. The overloads use
source-generated binding and support Native AOT. Existing calls with a configure delegate or a literal `null` keep
using the existing overloads.

## Health endpoint

The package auto-registers `IHealthCheck` for AMI/ARI/AGI (`AddVerbaraAri`: for ARI only). Wire to ASP.NET Core:

```csharp
builder.Services.AddHealthChecks();
// ...
app.MapHealthChecks("/health");   // diagnostics: every check, the SDK's included — no probe reads this endpoint
```

The `ami` check reads the AMI connection's state: `Connected` is `Healthy`, `Reconnecting`, `Connecting` or not yet connected is `Degraded` (the connection may come back, or nobody has connected it yet), and `Disconnecting` or `Disconnected` is `Unhealthy`; its data carries the state as `amiState`. It uses the same table as `live`, so the two never disagree on a state ([migration guide](../../docs/guides/ami-connection-state-and-health-migration.md#the-ami-health-check)). The `ari` check reads the ARI client's state with the same table, ARI's `Faulted` as `Unhealthy`; its data carries the state as `ariState` ([migration guide](../../docs/guides/ari-connection-state-and-accept-loop-migration.md#the-ari-health-check)).

A client with `EventsEnabled = false` reads `RestOnly`, and its `ari` check sends one `GET /ari/asterisk/ping` per run
instead: an answer reads `Healthy`, anything else `Unhealthy`. To keep it out of a host endpoint that is not a probe,
filter that endpoint by name (a readiness probe reads none of the SDK's checks: see below):

```csharp
app.MapHealthChecks("/health/pbx", new HealthCheckOptions { Predicate = check => check.Name != "ari" });
```

The `live` check reads the AMI connection before the state it holds: `Connected` reports on the state as before, `Reconnecting`, `Connecting` or not yet connected is `Degraded` (the state is not being updated, but the connection may come back), and `Disconnecting` or `Disconnected` is `Unhealthy`; its data carries the connection's state as `amiState`.

Every SDK check (`ami`, `live`, `agi`, `ari`, `sessions`, `verbara-pool`) is registered with no tags unless you name them
in `VerbaraHealthCheckOptions.Tags` — `builder.Services.Configure<VerbaraHealthCheckOptions>(o => o.Tags.Add("voice"))`,
before or after the SDK's registrations — or, with `AddVerbara(IConfiguration)`, in the array `Asterisk:HealthCheckTags`.
Never name the tag your readiness probe reads: an Asterisk outage would take the pod out of service. To tag one check, or
keep one off the host-wide tag, see the `PostConfigure` recipe under [Multi-server](#multi-server-federation).

## Multi-server (federation)

Register multi-server support at DI time, then resolve the pool after `Build()` and add servers at runtime:

```csharp
builder.Services.AddVerbaraMultiServer();

var host = builder.Build();

var pool = host.Services.GetRequiredService<VerbaraServerPool>();
await pool.AddServerAsync("pbx-east", new AmiConnectionOptions
{
    Hostname = "pbx-east",
    Port = 5038,
    Username = "admin",
    Password = "secret"
});
await pool.AddServerAsync("pbx-west", new AmiConnectionOptions
{
    Hostname = "pbx-west",
    Port = 5038,
    Username = "admin",
    Password = "secret"
});
```

`AddVerbaraMultiServer` also registers a health check named `verbara-pool`, with no tags unless you name them (`VerbaraHealthCheckOptions.Tags`), once however often it is called. It reads every server's AMI connection when it runs: `Healthy` when every server is connected or the pool holds none, `Unhealthy` when every server's connection has ended (`Disconnecting` or `Disconnected`), and `Degraded` otherwise; its data maps each server id to its connection's state. A pool whose servers are all connected reads `Degraded` while any server's last `Status` was refused (`VerbaraServer.LastStatusRefusal`), and its data then adds one `status-refused` entry naming each such server and Asterisk's message; every refusal also counts on `live.status.refused`. An unfiltered `/health` endpoint includes it, so point a liveness probe at an endpoint filtered by tag ([migration guide](../../docs/guides/ami-connection-state-and-health-migration.md)).

To tag one SDK check, or to keep one off the host-wide tags, edit its registration in a `PostConfigure`:

```csharp
// Before or after AddVerbaraMultiServer(): PostConfigure runs after every Configure, so the SDK's registration is there.
builder.Services.PostConfigure<HealthCheckServiceOptions>(o =>
{
    foreach (var r in o.Registrations)
        if (r.Name == "verbara-pool") r.Tags.Add("voice");   // your tag, your endpoint
});
```

`r.Tags.Remove("voice")` in the same `PostConfigure` keeps that check off a tag `VerbaraHealthCheckOptions.Tags` names. Write
it as `PostConfigure`, not `Configure`: a `Configure<HealthCheckServiceOptions>` registered before an SDK registration
(`AddVerbara`, `AddVerbaraMultiServer()` or a sessions registration) sees none of that registration's checks, because each
of them is added from a `Configure` too. Never put an SDK check on the tag your readiness probe reads.

For call sessions, `AddVerbaraSessionsMultiServer` registers the session engine and a reconciliation sweep for every server of the pool. It attaches no server: attach each one under its id in the pool, admit the calls it already carries, and detach it when it leaves. Never attach from inside a subscriber of the session engine's `Events`: that throws `InvalidOperationException`.

```csharp
builder.Services.AddVerbaraMultiServer();
builder.Services.AddVerbaraSessionsMultiServer();
// ...
var server = await pool.AddServerAsync("pbx-east", options);
var sessions = host.Services.GetRequiredService<ICallSessionManager>();
sessions.AttachToServer(server, "pbx-east");
await sessions.AdmitExistingCalls("pbx-east");
```

`AdmitExistingCalls` opens a session for each call the server's channel table holds that no session holds, so calls already up when the server is attached are followed too. The session engine indexes calls per server id: find a call with `GetByChannelId(serverId, uniqueId)`, `GetByLinkedId(serverId, linkedId)` or `GetByBridgeId(serverId, bridgeId)`. The lookups that name no server return `null` when two servers hold the id.

On each `SessionOptions.ReconciliationInterval` tick (30 s by default) the sweep verifies, for each server, the held calls attached under its id against one `Status` of that server, and ends a call whose hangup was lost as a reload ends it; one server's failure never stops the others. `o => o.ReconciliationInterval = Timeout.InfiniteTimeSpan` in the `configure` delegate switches it off. Give every Asterisk of the pool its own `systemname`, so no two servers issue the same channel ids: calls of two servers are kept apart without it, but a lookup that names no server, or a consumer keyed by `linkedid` alone, cannot tell them apart. See [the migration guide](../../docs/guides/call-session-ending-migration.md#the-sweep-on-a-multi-server-host-280) and, for what 2.10.0 changes, [the 2.10.0 migration guide](../../docs/guides/migration-2.10.0.md#sessions).

See `Examples/MultiServerExample/` for a full federation walkthrough.

## Native AOT

`AddVerbara` and `AddVerbaraAri` are fully AOT-safe: options validation comes from a source generator, no `Type.GetType` lookups, no `Activator.CreateInstance`. 0 trim warnings.

## License

MIT. Part of the [Verbara.Sdk](https://github.com/verbara/Verbara.Sdk) project.

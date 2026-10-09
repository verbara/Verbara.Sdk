# Verbara.Sdk.Hosting

The recommended entry point to the [Verbara.Sdk](https://github.com/verbara/Verbara.Sdk) family — single `dotnet add` brings in AMI, AGI, ARI, Live, Activities, Sessions, and Config plus a `Microsoft.Extensions.DependencyInjection` extension that wires everything into your `IHost` with one call. Native AOT, zero reflection, MIT licensed.

## What it does

- **`AddVerbara(IConfiguration | Action<VerbaraOptions>)`** — registers `IAmiConnection`, `IAriClient`, `IAgiServer`, the Live API (`VerbaraServer`), `IActivityRegistry`, `ISessionEngine`, and the supporting hosted services. Idempotent and source-generator-validated.
- **`VerbaraOptions`** — strongly-typed configuration model with `[OptionsValidator]` source-generated validation (no runtime reflection). Bind directly from `appsettings.json` or configure inline.
- **Hosted lifecycle** — `IHostedService` implementations connect AMI on `StartAsync`, drain on `StopAsync`. AGI server, ARI WebSocket, and `VerbaraServer` (Live aggregate) follow the same pattern.
- **Health checks** — `AmiHealthCheck` (`ami`, the same state table as `live`), `LiveHealthCheck` (`live`) and `AgiHealthCheck` (`agi`) auto-registered, `AriHealthCheck` (`ari`, the same state table as `ami`, ARI's `Faulted` as `Unhealthy`) with an `Ari` section; `AddVerbaraMultiServer` adds `VerbaraServerPoolHealthCheck` (`verbara-pool`). Expose at `/health` for Kubernetes probes.
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
    "AgiPort": 4573
  }
}
```

`Asterisk:Ami` and `Asterisk:Ari` take every option of `AmiConnectionOptions` and `AriClientOptions`, by name (`"MaxReconnectAttempts": 10`, `"ReconnectInitialDelay": "00:00:01"`); values are read with the invariant culture, and one that cannot be read as the option's type makes `AddVerbara` throw an `InvalidOperationException` naming its key. A value of the right type that the options do not accept (with `AutoReconnect` on, a reconnect delay or multiplier the backoff cannot use, for example; see the [high-load tuning guide](../../docs/guides/high-load-tuning.md#reconnection-tuning)) fails the host's start with an `OptionsValidationException` naming the option. The `Ari` section is optional: without it no ARI client is registered. `AgiPort` is the FastAGI server's port.

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
```

After `host.RunAsync()`, resolve services in your code:

```csharp
var ami = host.Services.GetRequiredService<IAmiConnection>();
var server = host.Services.GetRequiredService<VerbaraServer>();   // Live API aggregate
var ari = host.Services.GetRequiredService<IAriClient>();
```

## Health endpoint

The package auto-registers `IHealthCheck` for AMI/ARI/AGI. Wire to ASP.NET Core:

```csharp
builder.Services.AddHealthChecks();
// ...
app.MapHealthChecks("/health");
```

The `ami` check reads the AMI connection's state: `Connected` is `Healthy`, `Reconnecting`, `Connecting` or not yet connected is `Degraded` (the connection may come back, or nobody has connected it yet), and `Disconnecting` or `Disconnected` is `Unhealthy`; its data carries the state as `amiState`. It uses the same table as `live`, so the two never disagree on a state ([migration guide](../../docs/guides/ami-connection-state-and-health-migration.md#the-ami-health-check)). The `ari` check reads the ARI client's state with the same table, ARI's `Faulted` as `Unhealthy`; its data carries the state as `ariState` ([migration guide](../../docs/guides/ari-connection-state-and-accept-loop-migration.md#the-ari-health-check)).

The `live` check reads the AMI connection before the state it holds: `Connected` reports on the state as before, `Reconnecting`, `Connecting` or not yet connected is `Degraded` (the state is not being updated, but the connection may come back), and `Disconnecting` or `Disconnected` is `Unhealthy`; its data carries the connection's state as `amiState`.

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

`AddVerbaraMultiServer` also registers a health check named `verbara-pool`, with no tags, once however often it is called. It reads every server's AMI connection when it runs: `Healthy` when every server is connected or the pool holds none, `Unhealthy` when every server's connection has ended (`Disconnecting` or `Disconnected`), and `Degraded` otherwise; its data maps each server id to its connection's state. An unfiltered `/health` endpoint includes it, so point a liveness probe at an endpoint filtered by tag ([migration guide](../../docs/guides/ami-connection-state-and-health-migration.md)).

For call sessions, `AddVerbaraSessionsMultiServer` registers the session engine and a reconciliation sweep for every server of the pool. It attaches no server: attach each one under its id in the pool, and detach it when it leaves.

```csharp
builder.Services.AddVerbaraMultiServer();
builder.Services.AddVerbaraSessionsMultiServer();
// ...
var server = await pool.AddServerAsync("pbx-east", options);
host.Services.GetRequiredService<ICallSessionManager>().AttachToServer(server, "pbx-east");
```

On each `SessionOptions.ReconciliationInterval` tick (30 s by default) the sweep verifies, for each server, the held calls attached under its id against one `Status` of that server, and ends a call whose hangup was lost as a reload ends it; one server's failure never stops the others. `o => o.ReconciliationInterval = Timeout.InfiniteTimeSpan` in the `configure` delegate switches it off. Give every Asterisk of the pool its own `systemname`, so no two servers issue the same channel ids. See [the migration guide](../../docs/guides/call-session-ending-migration.md#the-sweep-on-a-multi-server-host-280).

See `Examples/MultiServerExample/` for a full federation walkthrough.

## Native AOT

`AddVerbara` is fully AOT-safe: options validation comes from a source generator, no `Type.GetType` lookups, no `Activator.CreateInstance`. 0 trim warnings.

## License

MIT. Part of the [Verbara.Sdk](https://github.com/verbara/Verbara.Sdk) project.

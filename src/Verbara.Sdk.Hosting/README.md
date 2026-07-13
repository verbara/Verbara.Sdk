# Verbara.Sdk.Hosting

The recommended entry point to the [Verbara.Sdk](https://github.com/verbara/Verbara.Sdk) family — single `dotnet add` brings in AMI, AGI, ARI, Live, Activities, Sessions, and Config plus a `Microsoft.Extensions.DependencyInjection` extension that wires everything into your `IHost` with one call. Native AOT, zero reflection, MIT licensed.

## What it does

- **`AddVerbara(IConfiguration | Action<VerbaraOptions>)`** — registers `IAmiConnection`, `IAriClient`, `IAgiServer`, the Live API (`VerbaraServer`), `IActivityRegistry`, `ISessionEngine`, and the supporting hosted services. Idempotent and source-generator-validated.
- **`VerbaraOptions`** — strongly-typed configuration model with `[OptionsValidator]` source-generated validation (no runtime reflection). Bind directly from `appsettings.json` or configure inline.
- **Hosted lifecycle** — `IHostedService` implementations connect AMI on `StartAsync`, drain on `StopAsync`. AGI server, ARI WebSocket, and `VerbaraServer` (Live aggregate) follow the same pattern.
- **Health checks** — `AmiHealthCheck`, `AriHealthCheck`, `AgiHealthCheck` auto-registered. Expose at `/health` for Kubernetes probes.
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
    "Ari": { "BaseUrl": "http://pbx.example.com:8088", "Username": "admin", "Password": "secret", "ApplicationName": "my-app" },
    "Agi": { "Port": 4573 }
  }
}
```

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

See `Examples/MultiServerExample/` for a full federation walkthrough.

## Native AOT

`AddVerbara` is fully AOT-safe: options validation comes from a source generator, no `Type.GetType` lookups, no `Activator.CreateInstance`. 0 trim warnings.

## License

MIT. Part of the [Verbara.Sdk](https://github.com/verbara/Verbara.Sdk) project.

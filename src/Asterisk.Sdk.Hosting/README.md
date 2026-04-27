# Asterisk.Sdk.Hosting

The recommended entry point to the [Asterisk.Sdk](https://github.com/Harol-Reina/Asterisk.Sdk) family — single `dotnet add` brings in AMI, AGI, ARI, Live, Activities, Sessions, and Config plus a `Microsoft.Extensions.DependencyInjection` extension that wires everything into your `IHost` with one call. Native AOT, zero reflection, MIT licensed.

## What it does

- **`AddAsterisk(IConfiguration | Action<AsteriskOptions>)`** — registers `IAmiConnection`, `IAriClient`, `IAgiServer`, the Live API (`AsteriskServer`), `IActivityRegistry`, `ISessionEngine`, and the supporting hosted services. Idempotent and source-generator-validated.
- **`AsteriskOptions`** — strongly-typed configuration model with `[OptionsValidator]` source-generated validation (no runtime reflection). Bind directly from `appsettings.json` or configure inline.
- **Hosted lifecycle** — `IHostedService` implementations connect AMI on `StartAsync`, drain on `StopAsync`. AGI server, ARI WebSocket, and `AsteriskServer` (Live aggregate) follow the same pattern.
- **Health checks** — `AmiHealthCheck`, `AriHealthCheck`, `AgiHealthCheck` auto-registered. Expose at `/health` for Kubernetes probes.
- **Multi-server support** — register multiple `AsteriskServer` instances via `AsteriskServerPool` for federated deployments.

This is a **meta-package**: it does not contain its own runtime types. It transitively pulls in `Asterisk.Sdk`, `Asterisk.Sdk.Ami`, `Asterisk.Sdk.Agi`, `Asterisk.Sdk.Ari`, `Asterisk.Sdk.Live`, `Asterisk.Sdk.Activities`, `Asterisk.Sdk.Sessions`, and `Asterisk.Sdk.Config`. Add Voice AI / Push / OpenTelemetry packages on top as needed.

## Install

```sh
dotnet add package Asterisk.Sdk.Hosting
```

## Quick start — bind from config

```csharp
using Asterisk.Sdk.Hosting;
using Microsoft.Extensions.Hosting;

var builder = Host.CreateApplicationBuilder(args);
builder.Services.AddAsterisk(builder.Configuration);

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
builder.Services.AddAsterisk(options =>
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
var server = host.Services.GetRequiredService<AsteriskServer>();   // Live API aggregate
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

```csharp
builder.Services.AddAsteriskServerPool(pool =>
{
    pool.AddServer("dc-east", o => { o.Ami.Hostname = "pbx-east"; /* ... */ });
    pool.AddServer("dc-west", o => { o.Ami.Hostname = "pbx-west"; /* ... */ });
});
```

See `Examples/MultiServerExample/` for a full federation walkthrough.

## Native AOT

`AddAsterisk` is fully AOT-safe: options validation comes from a source generator, no `Type.GetType` lookups, no `Activator.CreateInstance`. 0 trim warnings.

## License

MIT. Part of the [Asterisk.Sdk](https://github.com/Harol-Reina/Asterisk.Sdk) project.

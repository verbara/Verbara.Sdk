# Asterisk.Sdk

**.NET 10 Native AOT SDK for Asterisk PBX (AMI, AGI, ARI, Live API)**

[![NuGet](https://img.shields.io/nuget/v/Asterisk.Sdk?label=NuGet&color=blue)](https://www.nuget.org/packages/Asterisk.Sdk)
[![.NET](https://img.shields.io/badge/.NET-10.0-purple)](https://dotnet.microsoft.com/)
[![License](https://img.shields.io/badge/License-MIT-green)](LICENSE)

Asterisk.Sdk is a high-performance, Native AOT-compatible .NET library for integrating with [Asterisk PBX](https://www.asterisk.org/). It provides full support for AMI (Manager Interface), AGI (Gateway Interface), ARI (REST Interface), and a real-time Live API for tracking channels, queues, and agents -- all with zero runtime reflection.

Ported from [asterisk-java](https://github.com/asterisk-java/asterisk-java) 3.42.0, redesigned from the ground up for .NET 10 and Native AOT.

---

## Features

- **AMI Client** -- Connect to the Asterisk Manager Interface over TCP with MD5 challenge-response authentication, 111 actions, 215 events, and 17 typed responses. Auto-reconnect with configurable exponential backoff. Configurable heartbeat/keepalive with auto-disconnect on timeout.
- **FastAGI Server** -- Async TCP server for the Asterisk Gateway Interface with 54 AGI commands, pluggable script mapping strategies, and zero-copy I/O via `System.IO.Pipelines`. Per-connection timeout, status 511 hangup detection, and `AgiMetrics` instrumentation.
- **ARI Client** -- REST + WebSocket client for the Asterisk REST Interface. Manage channels, bridges, playbacks, recordings, endpoints, applications, and sounds. Domain exceptions (`AriNotFoundException`, `AriConflictException`) for HTTP error mapping. WebSocket reconnect with exponential backoff.
- **Live API** -- Real-time in-memory tracking of channels, queues, agents, and conference rooms from AMI events. Secondary indices for O(1) lookups by name. Observable gauges and event counters via `System.Diagnostics.Metrics`.
- **Activities** -- High-level telephony operations (Dial, Hold, Transfer, Park, Bridge, Conference) modeled as async state machines with `IObservable<ActivityStatus>` tracking. Real cancellation support, re-entrance guards, and channel variable capture (`DIALSTATUS`, `QUEUESTATUS`).
- **Config Parser** -- Read and parse Asterisk `.conf` files and `extensions.conf` dialplans. Quote-aware comment stripping.
- **Hosting** -- `IHostedService` for AMI and Live API lifecycle. `IHealthCheck` for AMI connection state. AOT-safe `IConfiguration` binding.
- **Native AOT** -- Zero reflection at runtime. Four source generators replace runtime code generation. 0 trim warnings.
- **Multi-Server** -- Federate multiple Asterisk servers with `AsteriskServerPool` and agent routing.

---

## What's New in v0.2.0-beta

- **AMI Heartbeat** -- Configurable periodic ping with auto-disconnect on timeout
- **AMI Health Check** -- `IHealthCheck` implementation for K8s readiness/liveness probes
- **Hosted Services** -- `IHostedService` for AMI connection and AsteriskServer lifecycle
- **IConfiguration Binding** -- AOT-safe `AddAsterisk(IConfiguration)` overload for `appsettings.json`
- **ARI Domain Exceptions** -- `AriNotFoundException` (404) and `AriConflictException` (409) from all resources
- **AGI Hardening** -- Status 511 hangup detection, per-connection timeout, `AgiMetrics` instrumentation
- **Activities Rebuild** -- Real cancellation via `CancellationTokenSource`, re-entrance guards, `DIALSTATUS`/`QUEUESTATUS` capture, corrected `Queue`/`Park`/`Hangup` arguments
- **LiveMetrics Expansion** -- Event counters for channels, queues, agents + queue wait time histogram
- **Config Fixes** -- `ConfigParseException` inherits `AsteriskException`, quote-aware semicolon stripping

---

## Installation

```bash
dotnet add package Asterisk.Sdk.Hosting
```

The `Asterisk.Sdk.Hosting` meta-package includes all sub-packages and DI extensions. To install individual packages, see the [Packages](#packages) table below.

---

## Quick Start

### AMI: Hosted Service with Automatic Lifecycle

```csharp
using Asterisk.Sdk.Hosting;
using Microsoft.Extensions.Hosting;

var builder = Host.CreateApplicationBuilder(args);
builder.Services.AddAsterisk(options =>
{
    options.Ami.Hostname = "192.168.1.100";
    options.Ami.Username = "admin";
    options.Ami.Password = "secret";
});

var host = builder.Build();
await host.RunAsync();
// AMI connects on start, disconnects on shutdown via IHostedService.
// Health check available at /health for K8s probes.
```

Or bind from `appsettings.json`:

```csharp
builder.Services.AddAsterisk(builder.Configuration);
```

```json
{
  "Asterisk": {
    "Ami": { "Hostname": "pbx.example.com", "Username": "admin", "Password": "secret" }
  }
}
```

### AMI: Manual Connect, Ping, and Events

```csharp
using Asterisk.Sdk;
using Asterisk.Sdk.Ami.Actions;
using Asterisk.Sdk.Hosting;
using Microsoft.Extensions.DependencyInjection;

var services = new ServiceCollection();
services.AddLogging();
services.AddAsterisk(options =>
{
    options.Ami.Hostname = "192.168.1.100";
    options.Ami.Username = "admin";
    options.Ami.Password = "secret";
});

await using var provider = services.BuildServiceProvider();
var ami = provider.GetRequiredService<IAmiConnection>();

await ami.ConnectAsync();
Console.WriteLine($"Connected to Asterisk {ami.AsteriskVersion}");

// Send a Ping action
var response = await ami.SendActionAsync(new PingAction());
Console.WriteLine($"Ping response: {response.Response}");

// Subscribe to all events
ami.OnEvent += async evt =>
{
    Console.WriteLine($"Event: {evt.EventType}");
    await ValueTask.CompletedTask;
};

// Keep running until Ctrl+C...
await ami.DisconnectAsync();
```

### AGI: FastAGI Server with Script Handler

```csharp
using Asterisk.Sdk;
using Asterisk.Sdk.Agi.Mapping;
using Asterisk.Sdk.Hosting;
using Microsoft.Extensions.DependencyInjection;

var mapping = new SimpleMappingStrategy();
mapping.Add("hello-world", new HelloWorldScript());

var services = new ServiceCollection();
services.AddLogging();
services.AddAsterisk(options =>
{
    options.Ami.Username = "admin";
    options.Ami.Password = "secret";
    options.AgiPort = 4573;
    options.AgiMappingStrategy = mapping;
});

await using var provider = services.BuildServiceProvider();
var agi = provider.GetRequiredService<IAgiServer>();

await agi.StartAsync();
Console.WriteLine($"FastAGI server listening on port {agi.Port}");

// Wait for Ctrl+C
await Task.Delay(Timeout.Infinite, default(CancellationToken));

// In your Asterisk dialplan:
//   same => n,AGI(agi://your-server:4573/hello-world)

class HelloWorldScript : IAgiScript
{
    public async ValueTask ExecuteAsync(
        IAgiChannel channel, IAgiRequest request, CancellationToken ct)
    {
        await channel.AnswerAsync(ct);
        await channel.StreamFileAsync("hello-world", cancellationToken: ct);
        await channel.HangupAsync(ct);
    }
}
```

### ARI: Connect WebSocket and Originate a Call

```csharp
using Asterisk.Sdk;
using Asterisk.Sdk.Ari.Client;
using Asterisk.Sdk.Hosting;
using Microsoft.Extensions.DependencyInjection;

var services = new ServiceCollection();
services.AddLogging();
services.AddAsterisk(options =>
{
    options.Ami.Username = "admin";
    options.Ami.Password = "secret";
    options.Ari = new AriClientOptions
    {
        BaseUrl = "http://192.168.1.100:8088",
        Username = "ariuser",
        Password = "aripass",
        Application = "my-stasis-app"
    };
});

await using var provider = services.BuildServiceProvider();
var ari = provider.GetRequiredService<IAriClient>();

// Subscribe to ARI events before connecting
var subscription = ari.Subscribe(new AriEventObserver());

await ari.ConnectAsync();
Console.WriteLine("ARI WebSocket connected");

// Originate a call into the Stasis application
var channel = await ari.Channels.OriginateAsync(
    endpoint: "PJSIP/6001",
    extension: "s",
    context: "my-stasis-app");
Console.WriteLine($"Originated channel: {channel.Id}");

// Keep running until Ctrl+C...
subscription.Dispose();
await ari.DisconnectAsync();

class AriEventObserver : IObserver<AriEvent>
{
    public void OnNext(AriEvent evt) =>
        Console.WriteLine($"ARI Event: {evt.Type}");
    public void OnError(Exception error) =>
        Console.WriteLine($"ARI Error: {error.Message}");
    public void OnCompleted() =>
        Console.WriteLine("ARI connection closed");
}
```

### Live API: Track Channels and Queues in Real-Time

```csharp
using Asterisk.Sdk;
using Asterisk.Sdk.Hosting;
using Asterisk.Sdk.Live.Server;
using Microsoft.Extensions.DependencyInjection;

var services = new ServiceCollection();
services.AddLogging();
services.AddAsterisk(options =>
{
    options.Ami.Hostname = "192.168.1.100";
    options.Ami.Port = 5038;
    options.Ami.Username = "admin";
    options.Ami.Password = "secret";
});

await using var provider = services.BuildServiceProvider();
var ami = provider.GetRequiredService<IAmiConnection>();
var server = provider.GetRequiredService<AsteriskServer>();

// Connect AMI first, then start live tracking
await ami.ConnectAsync();
await server.StartAsync();

Console.WriteLine($"Asterisk {server.AsteriskVersion}");
Console.WriteLine($"Active channels: {server.Channels.ChannelCount}");
Console.WriteLine($"Configured queues: {server.Queues.QueueCount}");

// Print each active channel
foreach (var ch in server.Channels.ActiveChannels)
    Console.WriteLine($"  Channel: {ch.Name} [{ch.State}]");

// Print each queue and its members
foreach (var q in server.Queues.Queues)
    Console.WriteLine($"  Queue: {q.Name} ({q.MemberCount} members, {q.EntryCount} callers)");

// Subscribe to channel lifecycle events
server.Channels.ChannelAdded += ch =>
    Console.WriteLine($"  + Channel added: {ch.Name}");
server.Channels.ChannelRemoved += ch =>
    Console.WriteLine($"  - Channel removed: {ch.Name}");

await ami.DisconnectAsync();
```

---

## Packages

| Package | Description |
|---------|-------------|
| **Asterisk.Sdk** | Core abstractions: interfaces, base classes, enums, attributes |
| **Asterisk.Sdk.Ami** | AMI client with System.IO.Pipelines transport, source-generated serialization |
| **Asterisk.Sdk.Agi** | FastAGI server with pluggable script mapping strategies |
| **Asterisk.Sdk.Ari** | ARI REST + WebSocket client with source-generated JSON serialization |
| **Asterisk.Sdk.Live** | Real-time channel, queue, agent, and conference tracking from AMI events |
| **Asterisk.Sdk.Activities** | High-level telephony activities: Dial, Hold, Transfer, Park, Bridge, Conference |
| **Asterisk.Sdk.Config** | Asterisk `.conf` and `extensions.conf` file parsers |
| **Asterisk.Sdk.Hosting** | DI extensions (`AddAsterisk`) and meta-package referencing all above |

---

## Requirements

- **.NET 10.0** or later
- **Asterisk 13+** (tested through Asterisk 21.x LTS)

---

## License

This project is licensed under the [MIT License](LICENSE).

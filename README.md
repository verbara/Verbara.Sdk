# Asterisk.Sdk

> The modern .NET SDK for Asterisk PBX. AMI, AGI, ARI, Live API, Sessions, Voice AI — all in one package. Native AOT. Zero reflection. MIT licensed.

[![CI](https://img.shields.io/github/actions/workflow/status/Harol-Reina/Asterisk.Sdk/ci.yml?branch=main&label=CI)](https://github.com/Harol-Reina/Asterisk.Sdk/actions/workflows/ci.yml)
[![AOT Trim](https://img.shields.io/github/actions/workflow/status/Harol-Reina/Asterisk.Sdk/aot-trim-check.yml?branch=main&label=AOT%20Trim)](https://github.com/Harol-Reina/Asterisk.Sdk/actions/workflows/aot-trim-check.yml)
[![NuGet](https://img.shields.io/nuget/v/Asterisk.Sdk?label=NuGet&color=blue)](https://www.nuget.org/packages/Asterisk.Sdk)
[![Downloads](https://img.shields.io/nuget/dt/Asterisk.Sdk?label=Downloads&color=blue)](https://www.nuget.org/packages/Asterisk.Sdk)
[![.NET](https://img.shields.io/badge/.NET-10.0-purple)](https://dotnet.microsoft.com/)
[![License](https://img.shields.io/badge/License-MIT-green)](LICENSE)
[![Trim-safe](https://img.shields.io/badge/Native%20AOT-ready-brightgreen)](docs/research/benchmark-analysis.md)

---

## Why Asterisk.Sdk?

The .NET Asterisk ecosystem is stalled. AsterNET targets .NET Framework 4.0 and has been abandoned since 2018. AsterNET.NetStandard is a minimal fork stuck at .NET Standard 2.0. Sufficit covers AMI and AGI on .NET 9 but offers no ARI, no Live objects, no Session Engine, and no Voice AI. None of them support Native AOT.

Asterisk.Sdk is the complete, modern alternative. One NuGet meta-package gives you AMI + AGI + ARI + Live API + Sessions with full DI integration. Add Voice AI packages to build AI-powered telephony — turn-based STT/TTS pipelines or a direct OpenAI Realtime bridge. Everything is AOT-safe, zero-reflection, and backed by four source generators that replace runtime code generation entirely.

The SDK is ported from [asterisk-java](https://github.com/asterisk-java/asterisk-java) 3.42.0 — the most mature Asterisk library in any language, with 790+ classes covering every protocol detail. Asterisk.Sdk takes that foundation and redesigns it from the ground up for .NET 10 performance patterns: `System.IO.Pipelines` for zero-copy TCP parsing, `System.Threading.Channels` for async event dispatch, `System.Reactive` for observable state machines, and `System.Text.Json` source generation for ARI serialization.

---

## How It Compares

| Capability | Asterisk.Sdk | AsterNET | Sufficit | asterisk-java |
|---|:---:|:---:|:---:|:---:|
| AMI | Yes | Yes | Yes | Yes |
| AGI | Yes | Yes | Yes | Yes |
| ARI | Yes | Separate pkg | No | No |
| Live Objects | Yes | No | No | Basic |
| Session Engine | Yes | No | No | No |
| Voice AI | Yes | No | No | No |
| Native AOT | Yes | No | No | N/A |
| .NET 10 | Yes | .NET Fx 4.0 | .NET 9 | Java 8+ |
| Active | Yes | No (2018) | Yes | Yes |

---

## Features

- **AMI Client** — Connect to the Asterisk Manager Interface over TCP with MD5 challenge-response authentication, 148 actions, 278 events, and 18 typed responses. Auto-reconnect with configurable exponential backoff. Configurable heartbeat/keepalive with auto-disconnect on timeout.
- **FastAGI Server** — Async TCP server for the Asterisk Gateway Interface with 54 AGI commands, pluggable script mapping strategies, and zero-copy I/O via `System.IO.Pipelines`. Per-connection timeout, status 511 hangup detection, and `AgiMetrics` instrumentation.
- **ARI Client** — REST + WebSocket client for the Asterisk REST Interface. Manage channels, bridges, playbacks, recordings, endpoints, applications, and sounds. Domain exceptions (`AriNotFoundException`, `AriConflictException`) for HTTP error mapping. WebSocket reconnect with exponential backoff.
- **Live API** — Real-time in-memory tracking of channels, queues, agents, and conference rooms from AMI events. Secondary indices for O(1) lookups by name. Observable gauges and event counters via `System.Diagnostics.Metrics`.
- **Activities** — High-level telephony operations (Dial, Hold, Transfer, Park, Bridge, Conference) modeled as async state machines with `IObservable<ActivityStatus>` tracking. Real cancellation support, re-entrance guards, and channel variable capture (`DIALSTATUS`, `QUEUESTATUS`).
- **Session Engine** — Correlate AMI events into unified call sessions using LinkedId grouping. State-machine lifecycle (Ringing, Answered, OnHold, Transferred, Completed), domain events (`SessionStarted`, `SessionEnded`, `SessionStateChanged`), automatic orphan detection via `SessionReconciler`, and pluggable extension points (`ISessionEnricher`, `ISessionPolicy`, `ISessionEventHandler`).
- **Voice AI** — Full stack for AI-powered telephony: PCM audio processing (resampler, VAD, gain), AudioSocket transport, STT/TTS abstraction layer with pluggable providers (Deepgram, ElevenLabs, Azure, Google, Whisper), barge-in pipeline with turn-taking, and a direct OpenAI Realtime API bridge.
- **Config Parser** — Read and parse Asterisk `.conf` files and `extensions.conf` dialplans. Quote-aware comment stripping.
- **Hosting** — `IHostedService` for AMI and Live API lifecycle. `IHealthCheck` for AMI connection state. AOT-safe `IConfiguration` binding.
- **Native AOT** — Zero reflection at runtime. Four source generators replace runtime code generation. 0 trim warnings.
- **Multi-Server** — Federate multiple Asterisk servers with `AsteriskServerPool` and agent routing.

---

## Status

**v1.10.2** — 19 NuGet packages, 0 build warnings, 0 trim warnings. Full VoiceAi telemetry stack (Metrics + HealthCheck + ActivitySource) across 5 packages. Push event bus now carries W3C traceparent across process/network boundaries (`PushEventMetadata.TraceContext`, ambient capture in `RxPushEventBus`). API coverage: 148/152 AMI actions (97%), 94/98 ARI endpoints (96%), 46/46 ARI event types (100%). Compatible with Asterisk 18, 20, 22, and 23.

---

## Documentation

| Topic | Link |
|-------|------|
| Getting started for operators | [docs/README-technical.md](docs/README-technical.md) |
| Commercial overview / positioning | [docs/README-commercial.md](docs/README-commercial.md) |
| High-load tuning (10K / 100K agents) | [docs/guides/high-load-tuning.md](docs/guides/high-load-tuning.md) |
| Session store backends (InMemory / Redis / Postgres) | [docs/guides/session-store-backends.md](docs/guides/session-store-backends.md) |
| Troubleshooting (connection, auth, events, tracing) | [docs/guides/troubleshooting.md](docs/guides/troubleshooting.md) |
| Asterisk 18/20/22/23 version compatibility | [docs/guides/asterisk-version-compatibility.md](docs/guides/asterisk-version-compatibility.md) |
| Asterisk Realtime (ODBC) setup | [docs/guides/manual-asterisk-realtime-setup.md](docs/guides/manual-asterisk-realtime-setup.md) |
| Benchmarks (AMD Ryzen 9 9900X, .NET 10) | [docs/research/benchmark-analysis.md](docs/research/benchmark-analysis.md) |
| Release notes | [CHANGELOG.md](CHANGELOG.md) |
| Contributing (dev setup, conventions, hooks) | [CONTRIBUTING.md](CONTRIBUTING.md) |
| Security policy | [SECURITY.md](SECURITY.md) |

---

## Performance

Benchmarked on AMD Ryzen 9 9900X (12C/24T), .NET 10.0.5, BenchmarkDotNet v0.14.0 (v1.11.0 full re-run, 2026-04-18):

| Operation | Throughput / Latency |
|-----------|----------------------|
| AMI event parse + dispatch | **1.53M events/sec** (653 ns) |
| ARI JSON deserialize `Channel` | **3.54M ops/sec** (283 ns) |
| ARI parse `StasisStart` event | **595K events/sec** (1.68 µs) — *2.7× faster than v1.0* |
| `ChannelManager.GetById` (secondary index) | **163.9M lookups/sec** (6.1 ns) |
| Observer dispatch (copy-on-write array) | **~0.21 ns / observer** (zero-alloc) |
| Session store Redis `SaveAsync` | **~12.6K saves/sec** (p50 79 µs) / batch 65,738 sess/sec |
| Session store Postgres `SaveAsync` | **~500 saves/sec** (p50 1.97 ms) / batch 9,491 sess/sec |

Full methodology, machine-readable results, and cross-language comparison (asterisk-java, asterisk-ami-client, pyst2) in [docs/research/benchmark-analysis.md](docs/research/benchmark-analysis.md). Raw BenchmarkDotNet reports are under `BenchmarkDotNet.Artifacts/results/`. Reproduce: `dotnet run -c Release --project Tests/Asterisk.Sdk.Benchmarks/`.

---

## Observability

Every package ships a `Meter`, `ActivitySource`, and `IHealthCheck`. Registered names are exposed as runtime-discoverable lists so consumers don't hard-code strings:

```csharp
using Asterisk.Sdk.Hosting;
using OpenTelemetry;
using OpenTelemetry.Metrics;
using OpenTelemetry.Trace;

builder.Services
    .AddOpenTelemetry()
    .WithTracing(t => t.AddSource([.. AsteriskTelemetry.ActivitySourceNames])
                       .AddOtlpExporter())
    .WithMetrics(m => m.AddMeter([.. AsteriskTelemetry.MeterNames])
                       .AddOtlpExporter());
```

- **9 `ActivitySource`s** — AMI, ARI, AGI, Live, Sessions, Push, VoiceAi, VoiceAi.AudioSocket, VoiceAi.OpenAiRealtime
- **12 `Meter`s** — all of the above plus Ari.Audio, VoiceAi.Stt, VoiceAi.Tts
- **11 `IHealthCheck`s** auto-registered — 6 core + 5 VoiceAi

See the [high-load tuning guide](docs/guides/high-load-tuning.md) for metric definitions and sizing recommendations at 10K / 100K agent scale.

---

## Installation

```bash
# Core SDK + protocol clients + hosting
dotnet add package Asterisk.Sdk.Hosting

# Voice AI — turn-based pipeline (STT + TTS)
dotnet add package Asterisk.Sdk.VoiceAi.AudioSocket
dotnet add package Asterisk.Sdk.VoiceAi
dotnet add package Asterisk.Sdk.VoiceAi.Stt      # STT providers
dotnet add package Asterisk.Sdk.VoiceAi.Tts      # TTS providers

# Voice AI — OpenAI Realtime (GPT-4o direct bridge)
dotnet add package Asterisk.Sdk.VoiceAi.OpenAiRealtime
```

The `Asterisk.Sdk.Hosting` meta-package includes all core sub-packages and DI extensions. Install VoiceAi packages individually as needed.

---

## Quick Start

**First contact in 10 lines.** Create a new console app, install `Asterisk.Sdk.Hosting`, and drop the snippet below into `Program.cs` — on start it connects to your Asterisk over AMI, exposes a `/health` endpoint, and auto-disconnects on shutdown.

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

Need a full runnable example (including `appsettings.json` config and event subscriptions)? Jump to **[Examples/BasicAmiExample/](Examples/BasicAmiExample/)** — it's the fastest path to a "working demo on your machine" with Docker-backed Asterisk 23.

### AMI: Bind from `appsettings.json`

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

### AMI: Events and Actions

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

var response = await ami.SendActionAsync(new PingAction());
Console.WriteLine($"Ping response: {response.Response}");

ami.OnEvent += async evt =>
{
    Console.WriteLine($"Event: {evt.EventType}");
    await ValueTask.CompletedTask;
};

await ami.DisconnectAsync();
```

### AGI: FastAGI Server with Script Handler

```csharp
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
await Task.Delay(Timeout.Infinite);

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
    options.Ami.Username = "admin";
    options.Ami.Password = "secret";
});

await using var provider = services.BuildServiceProvider();
var ami = provider.GetRequiredService<IAmiConnection>();
var server = provider.GetRequiredService<AsteriskServer>();

await ami.ConnectAsync();
await server.StartAsync();

Console.WriteLine($"Active channels: {server.Channels.ChannelCount}");
Console.WriteLine($"Configured queues: {server.Queues.QueueCount}");

server.Channels.ChannelAdded += ch =>
    Console.WriteLine($"+ Channel: {ch.Name}");

await ami.DisconnectAsync();
```

### Voice AI: Turn-Based Pipeline (STT + TTS)

Connect Asterisk AudioSocket to a conversation handler powered by Deepgram STT + ElevenLabs TTS:

```csharp
using Asterisk.Sdk.VoiceAi;
using Asterisk.Sdk.VoiceAi.AudioSocket.DependencyInjection;
using Asterisk.Sdk.VoiceAi.DependencyInjection;
using Asterisk.Sdk.VoiceAi.Stt.DependencyInjection;
using Asterisk.Sdk.VoiceAi.Tts.DependencyInjection;
using Microsoft.Extensions.Hosting;

var host = Host.CreateDefaultBuilder(args)
    .ConfigureServices((ctx, services) =>
    {
        services.AddAudioSocketServer(o => o.Port = 9091);

        services.AddDeepgramSpeechRecognizer(o =>
            o.ApiKey = ctx.Configuration["Deepgram:ApiKey"]!);

        services.AddElevenLabsSpeechSynthesizer(o =>
            o.ApiKey = ctx.Configuration["ElevenLabs:ApiKey"]!);

        services.AddVoiceAiPipeline<MyConversationHandler>();
    })
    .Build();

await host.RunAsync();

// Asterisk dialplan:
//   same => n,AudioSocket(your-server:9091)

class MyConversationHandler : IConversationHandler
{
    public ValueTask<string> HandleAsync(
        string userInput, ConversationContext ctx, CancellationToken ct)
        => ValueTask.FromResult($"You said: {userInput}");
}
```

### Voice AI: OpenAI Realtime Bridge (GPT-4o direct)

Replace the entire STT+LLM+TTS chain with a single WebSocket to OpenAI Realtime API:

```csharp
using Asterisk.Sdk.VoiceAi.OpenAiRealtime;
using Asterisk.Sdk.VoiceAi.OpenAiRealtime.DependencyInjection;
using Asterisk.Sdk.VoiceAi.AudioSocket.DependencyInjection;
using Microsoft.Extensions.Hosting;
using System.Reactive.Linq;

var host = Host.CreateDefaultBuilder(args)
    .ConfigureServices((ctx, services) =>
    {
        services.AddAudioSocketServer(o => o.Port = 9092);

        services.AddOpenAiRealtimeBridge(o =>
        {
            o.ApiKey       = ctx.Configuration["OpenAI:ApiKey"]!;
            o.Model        = "gpt-4o-realtime-preview";
            o.Voice        = "alloy";
            o.Instructions = "You are a helpful contact center assistant.";
        })
        .AddFunction<GetWeatherFunction>();
    })
    .Build();

// Subscribe to events
var bridge = host.Services.GetRequiredService<OpenAiRealtimeBridge>();

bridge.Events
    .OfType<RealtimeTranscriptEvent>()
    .Where(e => e.IsFinal)
    .Subscribe(e => Console.WriteLine($"[{e.ChannelId:D}] User: {e.Transcript}"));

bridge.Events
    .OfType<RealtimeFunctionCalledEvent>()
    .Subscribe(e => Console.WriteLine($"[{e.ChannelId:D}] Tool '{e.FunctionName}' → {e.ResultJson}"));

Console.WriteLine("Bridge ready on AudioSocket port 9092. Dial your Asterisk extension.");
await host.RunAsync();

// Implement a function tool
class GetWeatherFunction : IRealtimeFunctionHandler
{
    public string Name => "get_weather";
    public string Description => "Returns current weather for a city.";
    public string ParametersSchema =>
        """{"type":"object","properties":{"city":{"type":"string"}},"required":["city"]}""";

    public ValueTask<string> ExecuteAsync(string argumentsJson, CancellationToken ct = default)
        => ValueTask.FromResult("""{"temperature":"22°C","condition":"sunny"}""");
}
```

---

## Packages

### Core

| Package | Description |
|---------|-------------|
| **Asterisk.Sdk** | Core abstractions: interfaces, base classes, enums, attributes |
| **Asterisk.Sdk.Ami** | AMI client with System.IO.Pipelines transport, source-generated serialization |
| **Asterisk.Sdk.Agi** | FastAGI server with pluggable script mapping strategies |
| **Asterisk.Sdk.Ari** | ARI REST + WebSocket client with source-generated JSON serialization |
| **Asterisk.Sdk.Live** | Real-time channel, queue, agent, and conference tracking from AMI events |
| **Asterisk.Sdk.Activities** | High-level telephony activities: Dial, Hold, Transfer, Park, Bridge, Conference |
| **Asterisk.Sdk.Sessions** | Session Engine: call session correlation, state machines, and domain events |
| **Asterisk.Sdk.Config** | Asterisk `.conf` and `extensions.conf` file parsers |
| **Asterisk.Sdk.Hosting** | DI extensions (`AddAsterisk`) and meta-package referencing all core packages |

### Session Store Backends

| Package | Description |
|---------|-------------|
| **Asterisk.Sdk.Sessions.Redis** | `RedisSessionStore` + `UseRedis(...)` fluent builder (StackExchange.Redis, pipelined I/O, TTL-driven retention, AOT-safe) |
| **Asterisk.Sdk.Sessions.Postgres** | `PostgresSessionStore` + `UsePostgres(...)` fluent builder (Npgsql + Dapper + JSONB, UPSERT on `ON CONFLICT`, migration SQL shipped in the nupkg) |

### Observability & Integrations

| Package | Description |
|---------|-------------|
| **Asterisk.Sdk.OpenTelemetry** | Batteries-included OpenTelemetry wiring: one call enrolls every ActivitySource + Meter, ships Console/OTLP/Prometheus exporter helpers |
| **Asterisk.Sdk.Push** | Real-time push primitives: topic hierarchy, subscription management, authorization, in-memory event fan-out |
| **Asterisk.Sdk.Push.AspNetCore** | SSE streaming endpoint for the Push bus (ASP.NET Core) |
| **Asterisk.Sdk.Push.Webhooks** | Outbound HTTP webhooks: HMAC-SHA256 signing, exponential retry/backoff, topic-pattern matching |

### Voice AI

| Package | Description |
|---------|-------------|
| **Asterisk.Sdk.Audio** | Polyphase FIR resampler, VAD, PCM16 processing — zero dependencies |
| **Asterisk.Sdk.VoiceAi** | Pipeline orchestration (`VoiceAiPipeline`), `ISessionHandler`, `IConversationHandler` |
| **Asterisk.Sdk.VoiceAi.AudioSocket** | AudioSocket server/client with `System.IO.Pipelines` bidirectional streaming |
| **Asterisk.Sdk.VoiceAi.Stt** | STT providers: Deepgram (WebSocket), Whisper, Azure Whisper, Google Speech |
| **Asterisk.Sdk.VoiceAi.Tts** | TTS providers: ElevenLabs (WebSocket), Azure TTS |
| **Asterisk.Sdk.VoiceAi.OpenAiRealtime** | OpenAI Realtime API bridge (GPT-4o): dual-loop WebSocket, function calling, observability events |
| **Asterisk.Sdk.VoiceAi.Testing** | Fake STT/TTS/handler implementations for unit testing pipelines |

---

## Examples

The `Examples/` directory contains standalone console applications demonstrating each SDK layer:

| Example | Description |
|---------|-------------|
| `BasicAmiExample` | Connect to AMI, send actions, subscribe to events |
| `AmiAdvancedExample` | Advanced AMI patterns: originate, redirect, queue management |
| `FastAgiServerExample` | FastAGI server with script handlers |
| `AgiIvrExample` | Interactive Voice Response (IVR) menu via AGI |
| `AriStasisExample` | ARI WebSocket connection and Stasis event handling |
| `AriChannelControlExample` | ARI channel origination and bridge management |
| `LiveApiExample` | Real-time channel and queue tracking via Live API |
| `MultiServerExample` | Federated multi-server management with agent routing |
| `PbxActivitiesExample` | High-level telephony activities (Dial, Hold, Transfer) |
| `SessionExample` | Session Engine: call session correlation and domain events |
| `SessionExtensionsExample` | Session Engine extension points: enrichers, policies, event handlers |
| `VoiceAiExample` | Turn-based Voice AI pipeline: Deepgram STT + ElevenLabs TTS + echo handler |
| `OpenAiRealtimeExample` | GPT-4o direct bridge via OpenAI Realtime API with function calling |

See also [Asterisk.Sdk.PbxAdmin](https://github.com/Harol-Reina/Asterisk.Sdk.PbxAdmin) — a full Blazor Server PBX administration panel built with this SDK.

---

## Enterprise

Need skill-based routing, predictive dialer, real-time analytics, or AI agent assist?

[Asterisk.Sdk.Pro](https://github.com/Harol-Reina/Asterisk.Sdk.Pro) extends this SDK
with enterprise contact center capabilities — same architecture, same AOT guarantees,
composable NuGet packages for clustering, outbound campaigns, event sourcing, and more.

---

## Learn More

- **For decision-makers** — [docs/README-commercial.md](docs/README-commercial.md)
- **For developers** — [docs/README-technical.md](docs/README-technical.md)
- **Pro (enterprise)** — [Asterisk.Sdk.Pro](https://github.com/Harol-Reina/Asterisk.Sdk.Pro)

---

## Requirements

- **.NET 10.0** or later
- **Asterisk 18+** (tested with Asterisk 18, 20, 22, and 23)

---

## License

This project is licensed under the [MIT License](LICENSE).

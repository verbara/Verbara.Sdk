# Verbara Sdk (formerly Asterisk.Sdk)

> The modern .NET SDK for Asterisk PBX. AMI, AGI, ARI, Live API, Sessions, Voice AI — all in one package. Native AOT. Zero reflection. MIT licensed.
>
> **Rebrand history:** released as `Asterisk.Sdk.*` through v1.15.3, fully renamed to `Verbara.Sdk.*` in **v2.0.0** (2026-05-06) — see [ADR-0036](docs/decisions/0036-rebrand-to-verbara.md). The legacy `Asterisk.Sdk.*` packages on nuget.org are deprecated; each points to its `Verbara.Sdk.*` replacement.

[![CI](https://img.shields.io/github/actions/workflow/status/verbara/Verbara.Sdk/ci.yml?branch=main&label=CI)](https://github.com/verbara/Verbara.Sdk/actions/workflows/ci.yml)
[![AOT](https://img.shields.io/github/actions/workflow/status/verbara/Verbara.Sdk/aot-validate.yml?branch=main&label=AOT)](https://github.com/verbara/Verbara.Sdk/actions/workflows/aot-validate.yml)
[![NuGet](https://img.shields.io/nuget/v/Verbara.Sdk?label=NuGet&color=blue)](https://www.nuget.org/packages/Verbara.Sdk)
[![Downloads](https://img.shields.io/nuget/dt/Verbara.Sdk?label=Downloads&color=blue)](https://www.nuget.org/packages/Verbara.Sdk)
[![.NET](https://img.shields.io/badge/.NET-10.0-purple)](https://dotnet.microsoft.com/)
[![License](https://img.shields.io/badge/License-MIT-green)](LICENSE)
[![Trim-safe](https://img.shields.io/badge/Native%20AOT-ready-brightgreen)](docs/research/benchmark-analysis.md)

---

## Why Verbara Sdk?

The .NET Asterisk ecosystem is stalled. AsterNET targets .NET Framework 4.0 and has been abandoned since 2018. AsterNET.NetStandard is a minimal fork stuck at .NET Standard 2.0. Sufficit covers AMI and AGI on .NET 9 but offers no ARI, no Live objects, no Session Engine, and no Voice AI. None of them support Native AOT.

Verbara Sdk is the complete, modern alternative. One NuGet meta-package gives you AMI + AGI + ARI + Live API + Sessions with full DI integration. Add Voice AI packages to build AI-powered telephony — turn-based STT/TTS pipelines or a direct OpenAI Realtime bridge. Everything is AOT-safe, zero-reflection, and backed by four source generators that replace runtime code generation entirely.

The SDK is ported from [asterisk-java](https://github.com/asterisk-java/asterisk-java) 3.42.0 — the most mature Asterisk library in any language, with 790+ classes covering every protocol detail. Verbara Sdk takes that foundation and redesigns it from the ground up for .NET 10 performance patterns: `System.IO.Pipelines` for zero-copy TCP parsing, `System.Threading.Channels` for async event dispatch, `System.Reactive` for observable state machines, and `System.Text.Json` source generation for ARI serialization.

---

## How It Compares

| Capability | Verbara.Sdk | AsterNET | Sufficit | asterisk-java |
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
- **Voice AI** — Full stack for AI-powered telephony: PCM audio processing (resampler, VAD, gain), AudioSocket and `chan_websocket` transports with the Asterisk 22/23 JSON control protocol, STT/TTS abstraction layer with pluggable providers (Deepgram, Google, Whisper, Azure Whisper, Cartesia, AssemblyAI, Speechmatics for STT; ElevenLabs Flash 2.5, Deepgram Aura 2, LMNT, Azure, Cartesia, Speechmatics for TTS), `tts.synthesis.ttfa_ms` Time-To-First-Audio metric for latency observability, barge-in pipeline with turn-taking, and a direct OpenAI Realtime API bridge.
- **Config Parser** — Read and parse Asterisk `.conf` files and `extensions.conf` dialplans. Quote-aware comment stripping.
- **Hosting** — `IHostedService` for AMI and Live API lifecycle. `IHealthCheck` for AMI connection state. AOT-safe `IConfiguration` binding.
- **Native AOT** — Zero reflection at runtime. Four source generators replace runtime code generation. 0 trim warnings.
- **Multi-Server** — Federate multiple Asterisk servers with `VerbaraServerPool` and agent routing.

---

## Status

**v2.2.1** — 29 NuGet packages, 0 build warnings, 0 trim warnings, ~2,924 unit tests + 154 functional + 65 integration (Testcontainers). Latest releases:

- **v2.2.1** (2026-05-23) — **`Verbara.Sdk.Cluster.Postgres`** (new): Postgres-backed implementation of the cluster primitives shipped in v2.2.0, with `PostgresDistributedLock` (advisory-lock-backed `IDistributedLock`) running on `Verbara.Sdk.Data.Npgsql` — zero Dapper, AOT-clean. Plus CI hardening (merge queue activation on `main`, Dependabot auto-merge for analyzers/actions, LFS-in-CI for the ONNX model) and the v2.2.0+ dependency bump train (OnnxRuntime 1.22→1.26, NATS 2.7.3→2.8, microsoft-extensions, etc.).
- **v2.2.0** (2026-05-20) — **ADR-0022 Phase D: Dapper removed cross-repo.** New **`Verbara.Sdk.Data.Npgsql`** package — reflection-free Postgres facade with a `NpgsqlExecutor` (Dapper-parity surface) + name-based `NpgsqlDataReader` getters + hand-written `static Map(NpgsqlDataReader)` row mapping. `Verbara.Sdk.Sessions.Postgres` migrated off Dapper; the dead `Verbara.Sdk.Dapper.Stubs` canary was removed; a permanent `BanDapperPackageReferences` MSBuild guard makes the ban load-bearing.
- **v2.1.2** (2026-05-08) — `SmartTurnDetectorOptionsValidator` (`[OptionsValidator]` + `[Range]` + `ValidateOnStart()`), Hann window aligned to the periodic formula (`2πi/N`) matching HuggingFace WhisperFeatureExtractor + librosa, 8 dedicated `MelFilterBank` tests + 7 options-validation tests, ONNX model (8.3 MB) migrated to Git LFS.
- **v2.1.1** (2026-05-07) — Package metadata fix: `RepositoryUrl` and `PackageProjectUrl` corrected to `verbara/Verbara.Sdk`.
- **v2.1.0** (2026-05-07) — **`Verbara.Sdk.VoiceAi.TurnDetection`** (new): ML-based turn detector using the [Pipecat smart-turn-v3.2-cpu](https://huggingface.co/pipecat-ai/smart-turn-v3/blob/main/benchmarks/smart-turn-v3.2-cpu.md) ONNX model. Pipecat publish 94.26% English accuracy for this model version on their 31,527-sample benchmark. CPU inference latency on this SDK's own path is measured but not published — see [the claim registry](docs/claim-registry.md#deferrals--declared-with-their-blocker-adr-0042-d9). Drop-in replacement for `SilenceTurnDetector` via `services.AddSmartTurnDetection()`. Package validation enabled with v2.0.0 baseline.
- **v2.0.0** (2026-05-06) — **Full rebrand** from `Asterisk.Sdk.*` → `Verbara.Sdk.*` (ADR-0036). Breaking change: all namespaces, assemblies, and NuGet packages renamed. Pluggable turn detection: `ITurnDetector` interface + `SilenceTurnDetector` default + `FakeTurnDetector` in `Verbara.Sdk.VoiceAi.Testing`. 26 NuGet packages, 2,868 unit tests passing.

For historical v1.x releases (Asterisk.Sdk era), see [`CHANGELOG.md`](CHANGELOG.md).

API coverage (cumulative): 148/152 AMI actions (97%), 94/98 ARI endpoints (96%), 46/46 ARI event types (100%), 27/27 ARI models (100%), 278 AMI events covering Asterisk 18-23. Asterisk 22.5+ outbound WebSocket and Asterisk 22.8/23.2+ `chan_websocket` JSON control protocol both supported. Compatible with **Asterisk 18, 20, 22 LTS, and 23 Standard** — see [`docs/guides/asterisk-version-matrix.md`](docs/guides/asterisk-version-matrix.md) for lifecycle and break-change risk areas.

Architecture decisions: **37 ADRs** in [`docs/decisions/`](docs/decisions/) covering AOT-first design, source-generator-over-reflection policy, three-tier test strategy, push-bus design, cadence commitment, resilience/cluster primitive split between MIT and Pro, the rebrand to Verbara, and the cross-repo ADR reference convention.

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
| AMI event parse + dispatch | **1.62M events/sec** (617.6 ns) |
| ARI JSON deserialize `Channel` | **3.54M ops/sec** (283 ns) |
| ARI parse `StasisStart` event | **595K events/sec** (1.68 µs) — *2.7× faster than v1.0* |
| `ChannelManager.GetById` (secondary index) | **163.9M lookups/sec** (6.1 ns) |
| Observer dispatch (copy-on-write array) | **~0.21 ns / observer** (zero-alloc) |
| Session store Redis `SaveAsync` | **~12.6K saves/sec** (p50 79 µs) / batch 65,738 sess/sec |
| Session store Postgres `SaveAsync` | **~500 saves/sec** (p50 1.97 ms) / batch 9,491 sess/sec |

Full methodology, machine-readable results, and cross-language comparison (asterisk-java, asterisk-ami-client, pyst2) in [docs/research/benchmark-analysis.md](docs/research/benchmark-analysis.md). Raw BenchmarkDotNet reports are under `BenchmarkDotNet.Artifacts/results/`. Reproduce: `dotnet run -c Release --project Tests/Verbara.Sdk.Benchmarks/`.

---

## Observability

Every package ships a `Meter`, `ActivitySource`, and `IHealthCheck`. Registered names are exposed as runtime-discoverable lists so consumers don't hard-code strings:

<!-- skip-doc-snippet -->
```csharp
using Verbara.Sdk.Hosting;
using OpenTelemetry;
using OpenTelemetry.Metrics;
using OpenTelemetry.Trace;

builder.Services
    .AddOpenTelemetry()
    .WithTracing(t => t.AddSource([.. VerbaraTelemetry.ActivitySourceNames])
                       .AddOtlpExporter())
    .WithMetrics(m => m.AddMeter([.. VerbaraTelemetry.MeterNames])
                       .AddOtlpExporter());
```

- **9 `ActivitySource`s** — AMI, ARI, AGI, Live, Sessions, Push, VoiceAi, VoiceAi.AudioSocket, VoiceAi.OpenAiRealtime
- **15 `Meter`s** — all of the above plus Ari.Audio, VoiceAi.Stt, VoiceAi.Tts, Push.Webhooks, Push.Nats, Resilience
- **11 `IHealthCheck`s** auto-registered — 6 core + 5 VoiceAi
- **`VerbaraSemanticConventions`** — public static catalog of 60 const strings across 14 nested classes (Resource, Channel, Bridge, Calls, Dialplan, Sip, Media, Queues, Agent, VoiceAi, Events, Tenant, Event, Node) — pinned by 14+ unit tests so dashboard queries stay stable across SDK versions

See the [high-load tuning guide](docs/guides/high-load-tuning.md) for metric definitions and sizing recommendations at 10K / 100K agent scale.

---

## Installation

```bash
# Core SDK + protocol clients + hosting
dotnet add package Verbara.Sdk.Hosting

# Voice AI — turn-based pipeline (STT + TTS)
dotnet add package Verbara.Sdk.VoiceAi.AudioSocket
dotnet add package Verbara.Sdk.VoiceAi
dotnet add package Verbara.Sdk.VoiceAi.Stt      # STT providers
dotnet add package Verbara.Sdk.VoiceAi.Tts      # TTS providers

# Voice AI — OpenAI Realtime (GPT-4o direct bridge)
dotnet add package Verbara.Sdk.VoiceAi.OpenAiRealtime
```

The `Verbara.Sdk.Hosting` meta-package includes all core sub-packages and DI extensions. Install VoiceAi packages individually as needed.

---

## Quick Start

**First contact in 10 lines.** Create a new console app, install `Verbara.Sdk.Hosting`, and drop the snippet below into `Program.cs` — on start it connects to your Asterisk over AMI, exposes a `/health` endpoint, and auto-disconnects on shutdown.

```csharp
using Verbara.Sdk.Hosting;
using Microsoft.Extensions.Hosting;

var builder = Host.CreateApplicationBuilder(args);
builder.Services.AddVerbara(options =>
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

<!-- skip-doc-snippet -->
```csharp
builder.Services.AddVerbara(builder.Configuration);
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
using Verbara.Sdk;
using Verbara.Sdk.Ami.Actions;
using Verbara.Sdk.Hosting;
using Microsoft.Extensions.DependencyInjection;

var services = new ServiceCollection();
services.AddLogging();
services.AddVerbara(options =>
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
using Verbara.Sdk.Agi.Mapping;
using Verbara.Sdk.Hosting;
using Microsoft.Extensions.DependencyInjection;

var mapping = new SimpleMappingStrategy();
mapping.Add("hello-world", new HelloWorldScript());

var services = new ServiceCollection();
services.AddLogging();
services.AddVerbara(options =>
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
using Verbara.Sdk;
using Verbara.Sdk.Hosting;
using Verbara.Sdk.Live.Server;
using Microsoft.Extensions.DependencyInjection;

var services = new ServiceCollection();
services.AddLogging();
services.AddVerbara(options =>
{
    options.Ami.Hostname = "192.168.1.100";
    options.Ami.Username = "admin";
    options.Ami.Password = "secret";
});

await using var provider = services.BuildServiceProvider();
var ami = provider.GetRequiredService<IAmiConnection>();
var server = provider.GetRequiredService<VerbaraServer>();

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
using Verbara.Sdk.VoiceAi;
using Verbara.Sdk.VoiceAi.AudioSocket.DependencyInjection;
using Verbara.Sdk.VoiceAi.DependencyInjection;
using Verbara.Sdk.VoiceAi.Stt.DependencyInjection;
using Verbara.Sdk.VoiceAi.Tts.DependencyInjection;
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

<!-- skip-doc-snippet -->
```csharp
using Verbara.Sdk.VoiceAi.OpenAiRealtime;
using Verbara.Sdk.VoiceAi.OpenAiRealtime.DependencyInjection;
using Verbara.Sdk.VoiceAi.AudioSocket.DependencyInjection;
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
| **Verbara.Sdk** | Core abstractions: interfaces, base classes, enums, attributes |
| **Verbara.Sdk.Ami** | AMI client with System.IO.Pipelines transport, source-generated serialization |
| **Verbara.Sdk.Agi** | FastAGI server with pluggable script mapping strategies |
| **Verbara.Sdk.Ari** | ARI REST + WebSocket client with source-generated JSON serialization |
| **Verbara.Sdk.Live** | Real-time channel, queue, agent, and conference tracking from AMI events |
| **Verbara.Sdk.Activities** | High-level telephony activities: Dial, Hold, Transfer, Park, Bridge, Conference |
| **Verbara.Sdk.Sessions** | Session Engine: call session correlation, state machines, and domain events |
| **Verbara.Sdk.Config** | Asterisk `.conf` and `extensions.conf` file parsers |
| **Verbara.Sdk.Hosting** | DI extensions (`AddVerbara`) and meta-package referencing all core packages |

### Data Access

| Package | Description |
|---------|-------------|
| **Verbara.Sdk.Data.Npgsql** | Reflection-free Postgres data-access facade — `NpgsqlExecutor` (Dapper-parity surface: `ExecuteAsync`, `QueryAsync<T>`, `QuerySingleAsync`, etc.) + name-based `NpgsqlDataReader` getters, hand-written `static Map(NpgsqlDataReader)` row mapping. AOT-clean replacement for Dapper (ADR-0022 Phase D). |

### Session Store Backends

| Package | Description |
|---------|-------------|
| **Verbara.Sdk.Sessions.Redis** | `RedisSessionStore` + `UseRedis(...)` fluent builder (StackExchange.Redis, pipelined I/O, TTL-driven retention, AOT-safe) |
| **Verbara.Sdk.Sessions.Postgres** | `PostgresSessionStore` + `UsePostgres(...)` fluent builder (Npgsql + **Verbara.Sdk.Data.Npgsql** + JSONB, UPSERT on `ON CONFLICT`, migration SQL shipped in the nupkg). Dapper-free as of v2.2.0. |

### Cluster Primitives

| Package | Description |
|---------|-------------|
| **Verbara.Sdk.Cluster.Primitives** | `IClusterTransport`, `IDistributedLock`, `IMembershipProvider` contracts + in-memory reference implementations for single-node dev/test. |
| **Verbara.Sdk.Cluster.Postgres** | Postgres-backed implementation of the cluster primitives. `PostgresDistributedLock` (advisory-lock-backed) + `MigrationRunner` + `AddPostgresClusterPrimitives(...)` DI helper. Built on `Verbara.Sdk.Data.Npgsql`. |

### Resilience

| Package | Description |
|---------|-------------|
| **Verbara.Sdk.Resilience** | Composable circuit breaker + retry + timeout primitives (`BackoffSchedule`, `RetryBudget`) shared by AMI/ARI/Webhook reconnect loops. |

### Observability & Integrations

| Package | Description |
|---------|-------------|
| **Verbara.Sdk.OpenTelemetry** | Batteries-included OpenTelemetry wiring: one call enrolls every ActivitySource + Meter, ships Console/OTLP/Prometheus exporter helpers |
| **Verbara.Sdk.Push** | Real-time push primitives: topic hierarchy, subscription management, authorization, in-memory event fan-out |
| **Verbara.Sdk.Push.AspNetCore** | SSE streaming endpoint for the Push bus (ASP.NET Core) |
| **Verbara.Sdk.Push.Webhooks** | Outbound HTTP webhooks: HMAC-SHA256 signing, exponential retry/backoff, topic-pattern matching |
| **Verbara.Sdk.Push.Nats** | NATS bridge for `RxPushEventBus`: mirrors topic hierarchy to subject tree for multi-node deployments |

### Voice AI

| Package | Description |
|---------|-------------|
| **Verbara.Sdk.Audio** | Polyphase FIR resampler, VAD, PCM16 processing — zero dependencies |
| **Verbara.Sdk.VoiceAi** | Pipeline orchestration (`VoiceAiPipeline`), `ISessionHandler`, `IConversationHandler`, `ITurnDetector` (pluggable turn detection) |
| **Verbara.Sdk.VoiceAi.AudioSocket** | AudioSocket + `chan_websocket` (JSON control protocol) servers with `System.IO.Pipelines` bidirectional streaming |
| **Verbara.Sdk.VoiceAi.Stt** | STT providers: AssemblyAI, Cartesia (Ink-Whisper), Deepgram, Google Speech, Speechmatics, Whisper (cloud REST), Azure Whisper |
| **Verbara.Sdk.VoiceAi.Tts** | TTS providers: ElevenLabs (Flash 2.5), Azure, Cartesia (Sonic-3, 40-90 ms TTFA), Speechmatics, Deepgram (Aura 2 WS), LMNT (WS+HTTP) |
| **Verbara.Sdk.VoiceAi.OpenAiRealtime** | OpenAI Realtime API bridge (GPT-4o): dual-loop WebSocket, function calling, observability events |
| **Verbara.Sdk.VoiceAi.TurnDetection** | ML-based turn detector using the Pipecat [smart-turn-v3.2-cpu](https://huggingface.co/pipecat-ai/smart-turn-v3/blob/main/benchmarks/smart-turn-v3.2-cpu.md) ONNX model, for which Pipecat publish 94.26% English accuracy. Replaces `SilenceTurnDetector` via `AddSmartTurnDetection()`. |
| **Verbara.Sdk.VoiceAi.Testing** | Fake STT/TTS/handler/turn-detector implementations for unit testing pipelines |

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
| `AriOutboundExample` | ARI outbound WebSocket listener (Asterisk connects to your app) |
| `LiveApiExample` | Real-time channel and queue tracking via Live API |
| `MultiServerExample` | Federated multi-server management with agent routing |
| `PbxActivitiesExample` | High-level telephony activities (Dial, Hold, Transfer) |
| `SessionExample` | Session Engine: call session correlation and domain events |
| `SessionExtensionsExample` | Session Engine extension points: enrichers, policies, event handlers |
| `SessionsRedisExample` | Redis-backed distributed session store |
| `SessionsPostgresExample` | PostgreSQL-backed persistent session store |
| `ContactCenterSupervisionExample` | Contact center supervision: ChanSpy, Barge, Snoop |
| `NatsBridgeExample` | Push.Nats bridge for cross-service event distribution |
| `WebhookSubscriberExample` | Webhook subscriber with HMAC-signed delivery |
| `VoiceAiExample` | Turn-based Voice AI pipeline: Deepgram STT + ElevenLabs TTS + echo handler |
| `VoiceAiAssemblyAiExample` | Voice AI with AssemblyAI STT provider |
| `VoiceAiCartesiaExample` | Voice AI with Cartesia Sonic TTS provider |
| `VoiceAiSpeechmaticsExample` | Voice AI with Speechmatics STT provider |
| `VoiceAiCustomProviderExample` | Custom VoiceAi provider implementation pattern |
| `OpenAiRealtimeExample` | GPT-4o direct bridge via OpenAI Realtime API with function calling |
| `WebSocketMediaExample` | chan_websocket audio + JSON control protocol |
| `TelemetryExample` | OpenTelemetry discovery: ActivitySources, Meters, HealthChecks |
| `PbxAdmin` | _(Moved to [own repo](https://github.com/verbara/Verbara.Sdk.PbxAdmin))_ |

---

## Enterprise

Need skill-based routing, predictive dialer, real-time analytics, or AI agent assist?

[Verbara.Sdk.Pro](https://github.com/verbara/Verbara.Sdk.Pro) extends this SDK
with enterprise contact center capabilities — same architecture, same AOT guarantees,
composable NuGet packages for clustering, outbound campaigns, event sourcing, and more.

---

## Learn More

- **For decision-makers** — [docs/README-commercial.md](docs/README-commercial.md)
- **For developers** — [docs/README-technical.md](docs/README-technical.md)
- **Pro (enterprise)** — [Verbara.Sdk.Pro](https://github.com/verbara/Verbara.Sdk.Pro)

---

## Requirements

- **.NET 10.0** or later
- **Asterisk 18+** (tested with Asterisk 18, 20, 22, and 23)

---

## License

This project is licensed under the **[MIT License](LICENSE)**. See [`NOTICE`](NOTICE) for attributions.

This is the open-source base SDK of the **Verbara** open-core contact-center stack:

| Repository | License | Role |
|---|---|---|
| **Verbara Sdk** (this repository) | **MIT** | Telephony primitives (AMI / AGI / ARI / Live API / Sessions / Voice AI) — community attractor |
| **Verbara Web** | Apache 2.0 | Frontend UI (admin / agent / analytics / operations) |
| **Verbara Platform** | Apache 2.0 | Backend application — full contact-center engine |
| **Verbara Sdk Pro** | Commercial | Enterprise overlays (multi-tenant, analytics, cluster, licensing) |

**Why MIT here:** the SDK is the community attractor of the Verbara stack — maximum permissive license to encourage adoption, evaluation, and contributions. Pro features (skill routing, predictive dialer, real-time analytics, cluster, multi-tenant) are commercial via a separate package family. See [ADR-0027 (stewardship pledge)](docs/decisions/0027-stewardship-pledge-mit-commercial.md) and [ADR-0036 (rebrand to Verbara)](docs/decisions/0036-rebrand-to-verbara.md).

**Trademark note:** "Asterisk" is a registered trademark of **Sangoma Technologies / Digium** and refers to the Asterisk PBX product. This SDK *targets* Asterisk PBX as a runtime dependency; the SDK itself is rebranding to **Verbara** to avoid trademark conflict. References to "Asterisk" in API names, documentation, and code comments refer to the PBX product (Sangoma trademark) and remain accurate.

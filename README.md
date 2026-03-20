# Asterisk.Sdk

> The modern .NET SDK for Asterisk PBX. AMI, AGI, ARI, Live API, Sessions, Voice AI — all in one package. Native AOT. Zero reflection. MIT licensed.

[![NuGet](https://img.shields.io/nuget/v/Asterisk.Sdk?label=NuGet&color=blue)](https://www.nuget.org/packages/Asterisk.Sdk)
[![.NET](https://img.shields.io/badge/.NET-10.0-purple)](https://dotnet.microsoft.com/)
[![License](https://img.shields.io/badge/License-MIT-green)](LICENSE)

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

- **AMI Client** — Connect to the Asterisk Manager Interface over TCP with MD5 challenge-response authentication, 111 actions, 215 events, and 17 typed responses. Auto-reconnect with configurable exponential backoff. Configurable heartbeat/keepalive with auto-disconnect on timeout.
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

## What's New in v0.6.0-beta

### Voice AI Stack (Sprints 21–24)

**`Asterisk.Sdk.Audio`** — Pure C# polyphase FIR resampler with 12 pre-computed rate pairs (8kHz↔16kHz↔24kHz↔48kHz), zero-alloc output buffers, PCM16 processing, RMS energy measurement, and voice activity detection. No external dependencies.

**`Asterisk.Sdk.VoiceAi.AudioSocket`** — AudioSocket server and client using `System.IO.Pipelines` for zero-copy PCM streaming. `AudioSocketSession` handles bidirectional audio with backpressure. `AudioSocketClient` enables local testing without a real Asterisk instance.

**`Asterisk.Sdk.VoiceAi`** — Core pipeline abstractions. `VoiceAiPipeline` orchestrates VAD → STT → conversation handler → TTS in a dual-loop design (audio monitor + pipeline), with barge-in detection via a volatile `CancellationTokenSource`. `ISessionHandler` is the interchange point: both `VoiceAiPipeline` and `OpenAiRealtimeBridge` implement it, making them drop-in replacements.

**`Asterisk.Sdk.VoiceAi.Stt`** — Speech-to-text providers: Deepgram (WebSocket streaming, real-time), OpenAI Whisper (batch REST), Azure Whisper, and Google Speech (REST). All registered via `AddDeepgramSpeechRecognizer()` / `AddWhisperSpeechRecognizer()` / `AddAzureWhisperSpeechRecognizer()` / `AddGoogleSpeechRecognizer()`.

**`Asterisk.Sdk.VoiceAi.Tts`** — Text-to-speech providers: ElevenLabs (WebSocket streaming, ultra-low-latency) and Azure TTS (REST). Registered via `AddElevenLabsSpeechSynthesizer()` / `AddAzureTtsSpeechSynthesizer()`.

**`Asterisk.Sdk.VoiceAi.OpenAiRealtime`** — Bridges Asterisk AudioSocket directly to the [OpenAI Realtime API](https://platform.openai.com/docs/guides/realtime), bypassing the STT+LLM+TTS chain entirely. A single persistent WebSocket carries bidirectional PCM (resampled 8kHz↔24kHz). Supports server-side VAD, function calling (`IRealtimeFunctionHandler`), and emits typed events (`RealtimeSpeechStartedEvent`, `RealtimeTranscriptEvent`, `RealtimeFunctionCalledEvent`, etc.) via `IObservable<RealtimeEvent>`.

**`Asterisk.Sdk.VoiceAi.Testing`** — Fake implementations (`FakeSpeechRecognizer`, `FakeSpeechSynthesizer`, `FakeConversationHandler`) for unit testing Voice AI pipelines without real API calls.

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

## PBX Admin

The `Examples/PbxAdmin` project is a Blazor Server application showcasing the full SDK in a real-world PBX administration panel. It includes:

**Monitoring** — Live call matrix, queue status, agent tracking, channel list, parked calls, traffic analytics, Prometheus-style metrics, event log, and CLI console.

**PBX Management** — CRUD pages for Extensions, Trunks, Routes, IVR Menus, Queue Config, and Time Conditions. Both file-based (AMI `GetConfig`/`UpdateConfig`) and Realtime (PostgreSQL + Dapper) backends.

**Media & Features** — Recording policies with on-demand MixMonitor, Music on Hold class management with audio upload/conversion, ConfBridge profile configuration, Feature Codes with star-code CRUD, and Parking Lot slot/timeout configuration.

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
| `PbxAdmin` | Full Blazor Server PBX administration panel (see above) |

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
- **Asterisk 13+** (tested through Asterisk 21.x LTS)

---

## License

This project is licensed under the [MIT License](LICENSE).

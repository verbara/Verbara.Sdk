# Asterisk.Sdk -- Technical Reference

> Complete .NET 10 SDK for Asterisk PBX. AMI, AGI, ARI, Live, Sessions, Voice AI.
> Native AOT. Zero reflection. MIT licensed.

---

## Prerequisites

- **.NET 10.0.100+** (pinned in `global.json`)
- **Asterisk 13+** with AMI enabled (tested through 21.x LTS)
- For ARI: Asterisk HTTP and WebSocket enabled
- For Voice AI: API keys for your chosen providers (Deepgram, ElevenLabs, OpenAI, Azure, Google)

---

## Installation

### Core packages (via meta-package)

```bash
# Pulls in Sdk, Ami, Agi, Ari, Live, Activities, Sessions, Config
dotnet add package Asterisk.Sdk.Hosting
```

### Individual core packages

```bash
dotnet add package Asterisk.Sdk
dotnet add package Asterisk.Sdk.Ami
dotnet add package Asterisk.Sdk.Agi
dotnet add package Asterisk.Sdk.Ari
dotnet add package Asterisk.Sdk.Live
dotnet add package Asterisk.Sdk.Activities
dotnet add package Asterisk.Sdk.Sessions
dotnet add package Asterisk.Sdk.Config
```

### Voice AI packages

```bash
dotnet add package Asterisk.Sdk.Audio
dotnet add package Asterisk.Sdk.VoiceAi
dotnet add package Asterisk.Sdk.VoiceAi.AudioSocket
dotnet add package Asterisk.Sdk.VoiceAi.Stt
dotnet add package Asterisk.Sdk.VoiceAi.Tts
dotnet add package Asterisk.Sdk.VoiceAi.OpenAiRealtime
dotnet add package Asterisk.Sdk.VoiceAi.Testing
```

---

## Quick Starts

### AMI Connection

```csharp
var builder = Host.CreateApplicationBuilder(args);

builder.Services.AddAsterisk(options =>
{
    options.Ami.Hostname = "192.168.1.100";
    options.Ami.Username = "admin";
    options.Ami.Password = "secret";
});

var host = builder.Build();
await host.RunAsync();
```

Or bind from `appsettings.json`:

```csharp
builder.Services.AddAsterisk(builder.Configuration);
```

```json
{
  "Asterisk": {
    "Ami": {
      "Hostname": "192.168.1.100",
      "Username": "admin",
      "Password": "secret"
    }
  }
}
```

### FastAGI Server

```csharp
var mapping = new SimpleMappingStrategy();
mapping.Add("hello", new HelloScript());

builder.Services.AddAsterisk(options =>
{
    options.Ami.Hostname = "192.168.1.100";
    options.Ami.Username = "admin";
    options.Ami.Password = "secret";
    options.AgiPort = 4573;
    options.AgiMappingStrategy = mapping;
});
```

Asterisk dialplan:

```
exten => 100,1,AGI(agi://your-server:4573/hello)
```

### Live API + Sessions

```csharp
builder.Services.AddAsterisk(options =>
{
    options.Ami.Hostname = "192.168.1.100";
    options.Ami.Username = "admin";
    options.Ami.Password = "secret";
});

var host = builder.Build();

// AsteriskServer auto-starts when AMI connects
var server = host.Services.GetRequiredService<AsteriskServer>();

// Real-time state
int channels = server.Channels.ChannelCount;
int queues = server.Queues.QueueCount;

// Events
server.Channels.ChannelAdded += (sender, channel) =>
{
    Console.WriteLine($"New channel: {channel.Name}");
};

// Sessions auto-correlate AMI events by LinkedId
var sessionManager = host.Services.GetRequiredService<ICallSessionManager>();

// Active sessions
foreach (var session in sessionManager.Sessions)
{
    Console.WriteLine($"Session {session.LinkedId}: {session.State}");
}

// Observable stream
sessionManager.Events.Subscribe(evt =>
{
    Console.WriteLine($"Session event: {evt.GetType().Name}");
});
```

### Voice AI Pipeline

```csharp
services.AddAudioSocketServer(o => o.Port = 9091);
services.AddDeepgramSpeechRecognizer(o => o.ApiKey = "dg-key");
services.AddElevenLabsSpeechSynthesizer(o => o.ApiKey = "el-key");
services.AddVoiceAiPipeline<MyConversationHandler>();
```

Asterisk dialplan:

```
exten => 200,1,Answer()
 same => n,AudioSocket(your-server:9091)
```

Implement `IConversationHandler`:

```csharp
public class MyConversationHandler : IConversationHandler
{
    public Task OnTranscriptAsync(string text, CancellationToken ct)
    {
        // Process STT result, generate response, send to TTS
    }
}
```

---

## Packages

### Core (9 packages)

| Package | Description |
|---------|-------------|
| `Asterisk.Sdk` | Core abstractions: interfaces, base classes, enums, attributes |
| `Asterisk.Sdk.Ami` | AMI client with System.IO.Pipelines, source-generated serialization |
| `Asterisk.Sdk.Agi` | FastAGI server with pluggable script mapping |
| `Asterisk.Sdk.Ari` | ARI REST + WebSocket client with source-generated JSON |
| `Asterisk.Sdk.Live` | Real-time channel, queue, agent, conference tracking |
| `Asterisk.Sdk.Activities` | High-level telephony: Dial, Hold, Transfer, Park, Bridge, Conference |
| `Asterisk.Sdk.Sessions` | Session Engine: call correlation, state machines, domain events |
| `Asterisk.Sdk.Config` | Asterisk `.conf` and `extensions.conf` parsers |
| `Asterisk.Sdk.Hosting` | DI extensions (`AddAsterisk`) + meta-package |

### Voice AI (7 packages)

| Package | Description |
|---------|-------------|
| `Asterisk.Sdk.Audio` | Polyphase FIR resampler, VAD, PCM16 processing |
| `Asterisk.Sdk.VoiceAi` | Pipeline orchestration, `ISessionHandler`, `IConversationHandler` |
| `Asterisk.Sdk.VoiceAi.AudioSocket` | AudioSocket server/client with Pipelines streaming |
| `Asterisk.Sdk.VoiceAi.Stt` | STT providers: Deepgram, Whisper, Azure Whisper, Google Speech |
| `Asterisk.Sdk.VoiceAi.Tts` | TTS providers: ElevenLabs, Azure TTS |
| `Asterisk.Sdk.VoiceAi.OpenAiRealtime` | OpenAI Realtime API bridge (GPT-4o voice-to-voice) |
| `Asterisk.Sdk.VoiceAi.Testing` | Fakes for unit testing Voice AI pipelines |

### Source Generators (1 analyzer)

| Package | Description |
|---------|-------------|
| `Asterisk.Sdk.Ami.SourceGenerators` | Compile-time AMI action/event/response serializers (ships as analyzer with `Asterisk.Sdk.Ami`) |

---

## Architecture

### Dependency Graph

```
Asterisk.Sdk (core)
     |
    Ami (+Ami.SourceGenerators as analyzer)
     |
   Agi (-> Sdk + Ami)
  Live (-> Sdk + Ami)
     |
Sessions   (-> Sdk + Ami + Live)
Activities (-> Sdk + Ami + Agi + Live)
   Ari     (-> Sdk only)
Config     (-> Sdk only)
Hosting    (-> all core packages)

Audio                    (standalone, zero external deps)
VoiceAi                  (-> Audio)
VoiceAi.AudioSocket      (-> VoiceAi + Audio)
VoiceAi.Stt              (-> VoiceAi)
VoiceAi.Tts              (-> VoiceAi)
VoiceAi.OpenAiRealtime   (-> VoiceAi + Audio + AudioSocket)
VoiceAi.Testing          (-> VoiceAi)
```

### Design Decisions

**Zero-copy TCP parsing.** AMI and AGI protocols are parsed with `System.IO.Pipelines` via `PipelineSocketConnection`. Configurable `MemoryPool`, backpressure thresholds, and inline schedulers minimize allocations on the hot path.

**Async event pump.** `AsyncEventPump` uses `System.Threading.Channels` with configurable capacity (`EventPumpCapacity`). When the channel is full, events are dropped with metrics incremented and an `OnEventDropped` callback invoked.

**Four source generators replace all runtime reflection:**

- `ActionSerializerGenerator` -- serializes AMI actions to wire format
- `EventDeserializerGenerator` -- deserializes AMI events from parsed key-value pairs
- `EventRegistryGenerator` -- maps event names to types at compile time
- `ResponseDeserializerGenerator` -- deserializes AMI responses

**Observable state machines.** Live and Activities layers use `System.Reactive` (`IObservable<T>`, `BehaviorSubject<T>`) for state transitions. Subscribers receive real-time updates on channel, queue, and agent state changes.

**Thread-safe state management.** `ConcurrentDictionary` for all entity collections. Per-entity `Lock` for atomic property updates on `AsteriskAgent`, `AsteriskChannel`, `AsteriskQueue`. Copy-on-write volatile arrays for observer dispatch (zero-alloc, lock-free hot path).

**AMI request/response correlation.** Outbound actions are tracked via `ConcurrentDictionary<string, TaskCompletionSource<AmiMessage>>`. Responses are matched by `ActionId` and complete the corresponding `TaskCompletionSource`.

**Writer serialization.** `SemaphoreSlim` (`_writeLock`) serializes concurrent `PipeWriter` writes in `AmiConnection`, preventing interleaved AMI action frames on the wire.

**AMI authentication.** Supports both plaintext `Login` and MD5 challenge-response (`Challenge` + `Login` with key).

**Multi-server federation.** `IAmiConnectionFactory` creates connections dynamically. `AsteriskServerPool` federates N servers with an agent routing table. `IAmiConnection.Reconnected` event triggers state reload.

**Observability.** `AmiMetrics` and `LiveMetrics` expose counters, histograms, and observable gauges via `System.Diagnostics.Metrics` (events received/dropped/dispatched, action roundtrip latency, reconnection count, channel/queue/agent gauges).

**AOT-safe options validation.** `[OptionsValidator]` source generator replaces reflection-based `ValidateDataAnnotations` for all option types.

**JSON serialization.** ARI uses `System.Text.Json` with `[JsonSerializable]` source-generated contexts (`AriJsonContext`). Zero runtime reflection.

---

## Build and Test

```bash
# Build entire solution
dotnet build Asterisk.Sdk.slnx

# Run all tests
dotnet test Asterisk.Sdk.slnx

# Run a single test project
dotnet test Tests/Asterisk.Sdk.Ami.Tests/

# Run tests by name filter
dotnet test Tests/Asterisk.Sdk.Ami.Tests/ --filter "FullyQualifiedName~AmiProtocolReaderTests"

# Run benchmarks
dotnet run --project Tests/Asterisk.Sdk.Benchmarks/

# Integration tests (requires Docker)
docker compose -f docker/docker-compose.test.yml up --build
```

All projects enforce `TreatWarningsAsErrors`. The build must produce zero warnings. Central package management is handled via `Directory.Packages.props`.

---

## Project Structure

```
Asterisk.Sdk/
|-- Asterisk.Sdk/
|-- Asterisk.Sdk.Ami/
|-- Asterisk.Sdk.Ami.SourceGenerators/
|-- Asterisk.Sdk.Agi/
|-- Asterisk.Sdk.Ari/
|-- Asterisk.Sdk.Live/
|-- Asterisk.Sdk.Activities/
|-- Asterisk.Sdk.Sessions/
|-- Asterisk.Sdk.Config/
|-- Asterisk.Sdk.Hosting/
|-- Asterisk.Sdk.Audio/
|-- Asterisk.Sdk.VoiceAi/
|-- Asterisk.Sdk.VoiceAi.AudioSocket/
|-- Asterisk.Sdk.VoiceAi.Stt/
|-- Asterisk.Sdk.VoiceAi.Tts/
|-- Asterisk.Sdk.VoiceAi.OpenAiRealtime/
|-- Asterisk.Sdk.VoiceAi.Testing/
|-- Examples/
|   |-- BasicAmiExample/
|   |-- AmiAdvancedExample/
|   |-- FastAgiServerExample/
|   |-- AriChannelControlExample/
|   |-- AriStasisExample/
|   |-- LiveApiExample/
|   |-- MultiServerExample/
|   |-- PbxActivitiesExample/
|   |-- SessionExample/
|   |-- SessionExtensionsExample/
|   |-- VoiceAiExample/
|   |-- OpenAiRealtimeExample/
|   |-- AgiIvrExample/
|   +-- PbxAdmin/              (Blazor Server dashboard)
|-- Tests/
|   |-- Asterisk.Sdk.Ami.Tests/
|   |-- Asterisk.Sdk.Agi.Tests/
|   |-- Asterisk.Sdk.Live.Tests/
|   |-- Asterisk.Sdk.Benchmarks/
|   +-- ...
+-- docker/
    +-- docker-compose.test.yml
```

---

## DI Registration Reference

### Core -- `AddAsterisk`

```csharp
// Lambda configuration
services.AddAsterisk(options =>
{
    // AMI connection (required)
    options.Ami.Hostname = "pbx.local";
    options.Ami.Port = 5038;           // default
    options.Ami.Username = "admin";
    options.Ami.Password = "secret";

    // FastAGI server (optional)
    options.AgiPort = 4573;
    options.AgiMappingStrategy = new SimpleMappingStrategy();

    // Event pump tuning (optional)
    options.EventPumpCapacity = 10_000; // default
});

// Or from IConfiguration
services.AddAsterisk(configuration);
```

Registers: `IAmiConnection`, `AsteriskServer`, `ChannelManager`, `QueueManager`, `AgentManager`, `ICallSessionManager`, `FastAgiServer` (if AGI port configured).

### Voice AI

```csharp
// AudioSocket server
services.AddAudioSocketServer(o => o.Port = 9091);

// STT providers (pick one)
services.AddDeepgramSpeechRecognizer(o => o.ApiKey = "...");
services.AddWhisperSpeechRecognizer(o => { o.ApiKey = "..."; });
services.AddAzureWhisperSpeechRecognizer(o => { o.Endpoint = "..."; o.ApiKey = "..."; });
services.AddGoogleSpeechRecognizer(o => o.CredentialsJson = "...");

// TTS providers (pick one)
services.AddElevenLabsSpeechSynthesizer(o => { o.ApiKey = "..."; o.VoiceId = "..."; });
services.AddAzureSpeechSynthesizer(o => { o.SubscriptionKey = "..."; o.Region = "..."; });

// Pipeline with your handler
services.AddVoiceAiPipeline<MyConversationHandler>();

// Or OpenAI Realtime (voice-to-voice, no separate STT/TTS needed)
services.AddOpenAiRealtimeBridge(o => { o.ApiKey = "..."; o.Model = "gpt-4o-realtime"; });
```

---

## Key Types

| Type | Namespace | Purpose |
|------|-----------|---------|
| `IAmiConnection` | `Asterisk.Sdk.Ami` | Send actions, receive events/responses |
| `AmiAction` | `Asterisk.Sdk.Ami.Actions` | Base class for all 111 AMI actions |
| `AmiEvent` | `Asterisk.Sdk.Ami.Events` | Base class for all 215 AMI events |
| `AsteriskServer` | `Asterisk.Sdk.Live` | Top-level live model: channels, queues, agents |
| `ChannelManager` | `Asterisk.Sdk.Live` | Real-time channel tracking with secondary indices |
| `QueueManager` | `Asterisk.Sdk.Live` | Real-time queue tracking with member reverse index |
| `AgentManager` | `Asterisk.Sdk.Live` | Real-time agent state tracking |
| `AsteriskServerPool` | `Asterisk.Sdk.Live` | Multi-server federation with agent routing |
| `ICallSessionManager` | `Asterisk.Sdk.Sessions` | Call session correlation and lifecycle |
| `FastAgiServer` | `Asterisk.Sdk.Agi` | FastAGI TCP server |
| `AgiChannel` | `Asterisk.Sdk.Agi` | Per-call AGI command interface |
| `AriClient` | `Asterisk.Sdk.Ari` | ARI REST + WebSocket client |
| `IAudioSocketServer` | `Asterisk.Sdk.VoiceAi.AudioSocket` | AudioSocket protocol server |
| `IConversationHandler` | `Asterisk.Sdk.VoiceAi` | Voice AI pipeline callback interface |

---

## Conventions

- **Async-first.** All I/O returns `ValueTask` or `Task` with `CancellationToken` support.
- **Private fields.** `_camelCase` prefix (enforced by `.editorconfig`).
- **File-scoped namespaces.** Warning-level enforcement across all projects.
- **Test naming.** `Method_ShouldExpected_WhenCondition` (CA1707 suppressed in test projects).
- **Test stack.** xunit, FluentAssertions, NSubstitute.
- **AOT constraint.** No reflection at runtime. Source generators, `[JsonSerializable]`, `[OptionsValidator]`, and static dispatch only.

---

## See Also

- [Repository overview](../README.md)
- [For decision-makers](README-commercial.md)
- [Pro (enterprise extension)](https://github.com/Harol-Reina/Asterisk.Sdk.Pro)
- [Architecture review (high-load)](architecture/architecture-review-high-load.md)

---

## License

MIT. See [LICENSE](../LICENSE) in the repository root.

# Sprint 24: OpenAI Realtime Bridge Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Implement `Asterisk.Sdk.VoiceAi.OpenAiRealtime` — a bridge that connects Asterisk AudioSocket directly to the OpenAI Realtime API (GPT-4o), replacing the full STT+LLM+TTS chain with a single persistent WebSocket. Includes `ISessionHandler` abstraction in VoiceAi, function calling support, observability events, and a working demo.

**Architecture:** `ISessionHandler` interface added to `Asterisk.Sdk.VoiceAi` so `VoiceAiPipeline` (Sprint 23) and the new `OpenAiRealtimeBridge` are interchangeable via DI. Bridge is a singleton; each session is fully isolated (local `ClientWebSocket`, `SemaphoreSlim`, and `PolyphaseResampler` per `HandleSessionAsync` call). Dual-loop pattern: `InputLoop` streams Asterisk audio → OpenAI; `OutputLoop` streams OpenAI audio → Asterisk and handles function calls.

**Tech Stack:** `System.Net.WebSockets.ClientWebSocket`, `System.Text.Json` source-gen, `System.Buffers.ArrayBufferWriter<byte>`, `Utf8JsonWriter` (for raw JSON insertion in `session.update`), `System.Reactive` (Subject/IObservable), `Asterisk.Sdk.Audio.ResamplerFactory` (8kHz↔24kHz), xunit + FluentAssertions, `HttpListener`-based in-process fake WebSocket server.

---

## File Map

### Modified files (`Asterisk.Sdk.VoiceAi`)

| File | Change |
|------|--------|
| `src/Asterisk.Sdk.VoiceAi/ISessionHandler.cs` | **CREATE** — new public interface |
| `src/Asterisk.Sdk.VoiceAi/Pipeline/VoiceAiPipeline.cs` | Add `: ISessionHandler` (1 line) |
| `src/Asterisk.Sdk.VoiceAi/Pipeline/VoiceAiSessionBroker.cs` | Inject `ISessionHandler` instead of `VoiceAiPipeline` |
| `src/Asterisk.Sdk.VoiceAi/DependencyInjection/VoiceAiServiceCollectionExtensions.cs` | Register `VoiceAiPipeline` as `ISessionHandler` |

### New package (`Asterisk.Sdk.VoiceAi.OpenAiRealtime`)

| File | Responsibility |
|------|---------------|
| `src/Asterisk.Sdk.VoiceAi.OpenAiRealtime/Asterisk.Sdk.VoiceAi.OpenAiRealtime.csproj` | Project file, NuGet refs |
| `src/Asterisk.Sdk.VoiceAi.OpenAiRealtime/VadMode.cs` | Enum: `ServerSide`, `Disabled` |
| `src/Asterisk.Sdk.VoiceAi.OpenAiRealtime/OpenAiRealtimeOptions.cs` | Options class with `ApiKey`, `Model`, `Voice`, `Instructions`, `VadMode`, `InputFormat` |
| `src/Asterisk.Sdk.VoiceAi.OpenAiRealtime/OpenAiRealtimeOptionsValidator.cs` | `[OptionsValidator]` partial class |
| `src/Asterisk.Sdk.VoiceAi.OpenAiRealtime/RealtimeEvents.cs` | 7 `record` event types + abstract `RealtimeEvent` base |
| `src/Asterisk.Sdk.VoiceAi.OpenAiRealtime/FunctionCalling/IRealtimeFunctionHandler.cs` | Public interface for function handlers |
| `src/Asterisk.Sdk.VoiceAi.OpenAiRealtime/FunctionCalling/RealtimeFunctionRegistry.cs` | Internal singleton that collects handlers |
| `src/Asterisk.Sdk.VoiceAi.OpenAiRealtime/Internal/RealtimeProtocol.cs` | String constants for event names |
| `src/Asterisk.Sdk.VoiceAi.OpenAiRealtime/Internal/RealtimeMessages.cs` | Inbound + outbound DTOs (internal) |
| `src/Asterisk.Sdk.VoiceAi.OpenAiRealtime/Internal/RealtimeJsonContext.cs` | `[JsonSerializable]` source-gen context |
| `src/Asterisk.Sdk.VoiceAi.OpenAiRealtime/Internal/RealtimeLog.cs` | `[LoggerMessage]` static log methods |
| `src/Asterisk.Sdk.VoiceAi.OpenAiRealtime/OpenAiRealtimeBridge.cs` | Main bridge: `ISessionHandler`, dual-loop, `BuildSessionUpdate`, `DisposeAsync` |
| `src/Asterisk.Sdk.VoiceAi.OpenAiRealtime/DependencyInjection/RealtimeServiceCollectionExtensions.cs` | `AddOpenAiRealtimeBridge()` + `AddFunction<T>()` |

### Tests

| File | Coverage |
|------|----------|
| `Tests/Asterisk.Sdk.VoiceAi.OpenAiRealtime.Tests/Asterisk.Sdk.VoiceAi.OpenAiRealtime.Tests.csproj` | Project file |
| `Tests/Asterisk.Sdk.VoiceAi.OpenAiRealtime.Tests/Internal/RealtimeFakeServer.cs` | In-process WebSocket fake — simulates OpenAI Realtime protocol |
| `Tests/Asterisk.Sdk.VoiceAi.OpenAiRealtime.Tests/Bridge/OpenAiRealtimeBridgeTests.cs` | Bridge integration tests (audio, lifecycle, error) |
| `Tests/Asterisk.Sdk.VoiceAi.OpenAiRealtime.Tests/FunctionCalling/FunctionCallTests.cs` | Function call dispatch tests |

### Demo

| File | Description |
|------|-------------|
| `Examples/OpenAiRealtimeExample/OpenAiRealtimeExample.csproj` | Project file |
| `Examples/OpenAiRealtimeExample/GetCurrentTimeFunction.cs` | Example function handler (returns current time as JSON) |
| `Examples/OpenAiRealtimeExample/Program.cs` | Demo host with `AddOpenAiRealtimeBridge` + subscription to events |
| `Examples/OpenAiRealtimeExample/appsettings.json` | Config template |

### Solution file

| File | Change |
|------|--------|
| `Asterisk.Sdk.slnx` | Add new `src/` project + `Tests/` project + `Examples/` project |

---

## Task 1: Add `ISessionHandler` to `Asterisk.Sdk.VoiceAi`

**Files:**
- Create: `src/Asterisk.Sdk.VoiceAi/ISessionHandler.cs`
- Modify: `src/Asterisk.Sdk.VoiceAi/Pipeline/VoiceAiPipeline.cs`
- Modify: `src/Asterisk.Sdk.VoiceAi/Pipeline/VoiceAiSessionBroker.cs`
- Modify: `src/Asterisk.Sdk.VoiceAi/DependencyInjection/VoiceAiServiceCollectionExtensions.cs`
- Test: `Tests/Asterisk.Sdk.VoiceAi.Tests/` (existing tests must still pass)

- [ ] **Step 1: Create `ISessionHandler.cs`**

```csharp
// src/Asterisk.Sdk.VoiceAi/ISessionHandler.cs
using Asterisk.Sdk.VoiceAi.AudioSocket;

namespace Asterisk.Sdk.VoiceAi;

/// <summary>
/// Handles a single AudioSocket session end-to-end.
/// Implementations include <see cref="Pipeline.VoiceAiPipeline"/> (turn-based STT+LLM+TTS)
/// and <c>OpenAiRealtimeBridge</c> (streaming WebSocket to OpenAI Realtime API).
/// </summary>
public interface ISessionHandler
{
    /// <summary>
    /// Runs the session until the AudioSocket disconnects or <paramref name="ct"/> is cancelled.
    /// </summary>
    ValueTask HandleSessionAsync(AudioSocketSession session, CancellationToken ct = default);
}
```

- [ ] **Step 2: Add `: ISessionHandler` to `VoiceAiPipeline`**

In `src/Asterisk.Sdk.VoiceAi/Pipeline/VoiceAiPipeline.cs`, line 20, change:
```csharp
public sealed class VoiceAiPipeline : IAsyncDisposable
```
to:
```csharp
public sealed class VoiceAiPipeline : ISessionHandler, IAsyncDisposable
```

No other changes needed — `HandleSessionAsync` already has the correct signature.

- [ ] **Step 3: Inject `ISessionHandler` in `VoiceAiSessionBroker`**

Replace the entire content of `src/Asterisk.Sdk.VoiceAi/Pipeline/VoiceAiSessionBroker.cs`:

```csharp
using Asterisk.Sdk.VoiceAi.AudioSocket;
using Asterisk.Sdk.VoiceAi.Internal;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Asterisk.Sdk.VoiceAi.Pipeline;

/// <summary>
/// Hosted service that wires <see cref="AudioSocketServer.OnSessionStarted"/>
/// to <see cref="ISessionHandler.HandleSessionAsync"/>, spawning a handler
/// loop for each incoming AudioSocket session.
/// </summary>
public sealed class VoiceAiSessionBroker : IHostedService
{
    private readonly AudioSocketServer _server;
    private readonly ISessionHandler _handler;
    private readonly ILogger<VoiceAiSessionBroker> _logger;
    private CancellationToken _stoppingToken;

    /// <summary>Creates a new session broker.</summary>
    public VoiceAiSessionBroker(
        AudioSocketServer server,
        ISessionHandler handler,
        ILogger<VoiceAiSessionBroker> logger)
    {
        _server = server;
        _handler = handler;
        _logger = logger;
    }

    /// <inheritdoc/>
    public Task StartAsync(CancellationToken cancellationToken)
    {
        _stoppingToken = cancellationToken;

        _server.OnSessionStarted += session =>
        {
            _ = _handler.HandleSessionAsync(session, _stoppingToken)
                .AsTask()
                .ContinueWith(
                    t => VoiceAiLog.SessionError(_logger, session.ChannelId, t.Exception!),
                    TaskContinuationOptions.OnlyOnFaulted);
            return ValueTask.CompletedTask;
        };

        return Task.CompletedTask;
    }

    /// <inheritdoc/>
    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;
}
```

- [ ] **Step 4: Register `VoiceAiPipeline` as `ISessionHandler` in DI**

In `src/Asterisk.Sdk.VoiceAi/DependencyInjection/VoiceAiServiceCollectionExtensions.cs`, replace the body of `AddVoiceAiPipeline<THandler>`:

```csharp
public static IServiceCollection AddVoiceAiPipeline<[DynamicallyAccessedMembers(DynamicallyAccessedMemberTypes.PublicConstructors)] THandler>(
    this IServiceCollection services,
    Action<VoiceAiPipelineOptions>? configure = null)
    where THandler : class, IConversationHandler
{
    services.TryAddScoped<IConversationHandler, THandler>();
    services.TryAddSingleton<VoiceAiPipeline>();
    services.TryAddSingleton<ISessionHandler>(sp => sp.GetRequiredService<VoiceAiPipeline>());
    services.TryAddSingleton<VoiceAiSessionBroker>();
    services.AddHostedService<VoiceAiSessionBroker>(sp => sp.GetRequiredService<VoiceAiSessionBroker>());

    if (configure is not null)
        services.Configure(configure);
    else
        services.AddOptions<VoiceAiPipelineOptions>();

    return services;
}
```

- [ ] **Step 5: Run existing VoiceAi tests to verify no breakage**

```bash
dotnet test Tests/Asterisk.Sdk.VoiceAi.Tests/ -v minimal
```

Expected: All tests pass, 0 failures, 0 warnings.

- [ ] **Step 6: Commit**

```bash
git add src/Asterisk.Sdk.VoiceAi/ISessionHandler.cs \
        src/Asterisk.Sdk.VoiceAi/Pipeline/VoiceAiPipeline.cs \
        src/Asterisk.Sdk.VoiceAi/Pipeline/VoiceAiSessionBroker.cs \
        src/Asterisk.Sdk.VoiceAi/DependencyInjection/VoiceAiServiceCollectionExtensions.cs
git commit -m "feat(voiceai): extract ISessionHandler for bridge interchangeability"
```

---

## Task 2: Scaffold `Asterisk.Sdk.VoiceAi.OpenAiRealtime` Package

**Files:**
- Create: `src/Asterisk.Sdk.VoiceAi.OpenAiRealtime/Asterisk.Sdk.VoiceAi.OpenAiRealtime.csproj`
- Create: `src/Asterisk.Sdk.VoiceAi.OpenAiRealtime/VadMode.cs`
- Create: `src/Asterisk.Sdk.VoiceAi.OpenAiRealtime/Internal/RealtimeProtocol.cs`
- Modify: `Asterisk.Sdk.slnx`

- [ ] **Step 1: Create directory structure**

```bash
mkdir -p src/Asterisk.Sdk.VoiceAi.OpenAiRealtime/FunctionCalling
mkdir -p src/Asterisk.Sdk.VoiceAi.OpenAiRealtime/Internal
mkdir -p src/Asterisk.Sdk.VoiceAi.OpenAiRealtime/DependencyInjection
```

- [ ] **Step 2: Create `.csproj`**

```xml
<!-- src/Asterisk.Sdk.VoiceAi.OpenAiRealtime/Asterisk.Sdk.VoiceAi.OpenAiRealtime.csproj -->
<Project Sdk="Microsoft.NET.Sdk">
  <PropertyGroup>
    <Description>OpenAI Realtime API bridge for Asterisk.Sdk.VoiceAi — connects Asterisk AudioSocket directly to GPT-4o in real time, with function calling and observability. Zero third-party dependencies.</Description>
    <PackageTags>$(PackageTags);voiceai;openai;realtime;gpt4o;speech;function-calling</PackageTags>
  </PropertyGroup>
  <ItemGroup>
    <InternalsVisibleTo Include="Asterisk.Sdk.VoiceAi.OpenAiRealtime.Tests" />
  </ItemGroup>
  <ItemGroup>
    <ProjectReference Include="..\Asterisk.Sdk.VoiceAi\Asterisk.Sdk.VoiceAi.csproj" />
    <ProjectReference Include="..\Asterisk.Sdk.Audio\Asterisk.Sdk.Audio.csproj" />
  </ItemGroup>
  <ItemGroup>
    <PackageReference Include="Microsoft.Extensions.Logging.Abstractions" />
    <PackageReference Include="Microsoft.Extensions.DependencyInjection.Abstractions" />
    <PackageReference Include="Microsoft.Extensions.Options" />
    <PackageReference Include="System.Reactive" />
  </ItemGroup>
</Project>
```

- [ ] **Step 3: Create `VadMode.cs`**

```csharp
// src/Asterisk.Sdk.VoiceAi.OpenAiRealtime/VadMode.cs
namespace Asterisk.Sdk.VoiceAi.OpenAiRealtime;

/// <summary>Voice Activity Detection mode for the OpenAI Realtime session.</summary>
public enum VadMode
{
    /// <summary>OpenAI detects speech boundaries server-side (default, recommended).</summary>
    ServerSide,

    /// <summary>
    /// VAD disabled — caller must send <c>input_audio_buffer.commit</c> manually.
    /// Use only when driving turn boundaries externally.
    /// </summary>
    Disabled
}
```

- [ ] **Step 4: Create `Internal/RealtimeProtocol.cs`**

```csharp
// src/Asterisk.Sdk.VoiceAi.OpenAiRealtime/Internal/RealtimeProtocol.cs
namespace Asterisk.Sdk.VoiceAi.OpenAiRealtime.Internal;

/// <summary>OpenAI Realtime API event type name constants.</summary>
internal static class RealtimeProtocol
{
    // Inbound (OpenAI → client)
    public const string SessionCreated                   = "session.created";
    public const string ResponseAudioDelta               = "response.audio.delta";
    public const string ResponseAudioDone                = "response.audio.done";
    public const string ResponseAudioTranscriptDelta     = "response.audio_transcript.delta";
    public const string ResponseAudioTranscriptDone      = "response.audio_transcript.done";
    public const string ResponseCreated                  = "response.created";
    public const string ResponseDone                     = "response.done";
    public const string ResponseCancelled                = "response.cancelled";
    public const string ResponseFunctionCallArgumentsDone = "response.function_call_arguments.done";
    public const string InputAudioBufferSpeechStarted    = "input_audio_buffer.speech_started";
    public const string InputAudioBufferSpeechStopped    = "input_audio_buffer.speech_stopped";
    public const string Error                            = "error";

    // Outbound (client → OpenAI)
    public const string SessionUpdate                    = "session.update";
    public const string InputAudioBufferAppend           = "input_audio_buffer.append";
    public const string InputAudioBufferCommit           = "input_audio_buffer.commit";
    public const string ConversationItemCreate           = "conversation.item.create";
    public const string ResponseCreate                   = "response.create";
}
```

- [ ] **Step 5: Add projects to `Asterisk.Sdk.slnx`**

In `Asterisk.Sdk.slnx`, inside the `<Folder Name="/src/">` block, add after the last VoiceAi entry:
```xml
    <Project Path="src/Asterisk.Sdk.VoiceAi.OpenAiRealtime/Asterisk.Sdk.VoiceAi.OpenAiRealtime.csproj" />
```

In the `<Folder Name="/Tests/">` block, add:
```xml
    <Project Path="Tests/Asterisk.Sdk.VoiceAi.OpenAiRealtime.Tests/Asterisk.Sdk.VoiceAi.OpenAiRealtime.Tests.csproj" />
```

In the `<Folder Name="/Examples/">` block, add:
```xml
    <Project Path="Examples/OpenAiRealtimeExample/OpenAiRealtimeExample.csproj" />
```

- [ ] **Step 6: Verify package builds**

```bash
dotnet build src/Asterisk.Sdk.VoiceAi.OpenAiRealtime/Asterisk.Sdk.VoiceAi.OpenAiRealtime.csproj
```

Expected: Build succeeded, 0 warnings.

- [ ] **Step 7: Commit**

```bash
git add src/Asterisk.Sdk.VoiceAi.OpenAiRealtime/ Asterisk.Sdk.slnx
git commit -m "feat(realtime): scaffold Asterisk.Sdk.VoiceAi.OpenAiRealtime package"
```

---

## Task 3: DTOs + `RealtimeJsonContext`

**Files:**
- Create: `src/Asterisk.Sdk.VoiceAi.OpenAiRealtime/Internal/RealtimeMessages.cs`
- Create: `src/Asterisk.Sdk.VoiceAi.OpenAiRealtime/Internal/RealtimeJsonContext.cs`

- [ ] **Step 1: Write the failing test (verify source-gen compiles and round-trips)**

Create `Tests/Asterisk.Sdk.VoiceAi.OpenAiRealtime.Tests/Internal/` directory and a placeholder file for the test to verify the JSON context is correct. We'll expand this in Task 9. For now, just stub:

```csharp
// Tests/Asterisk.Sdk.VoiceAi.OpenAiRealtime.Tests/Internal/RealtimeJsonContextTests.cs
using System.Text.Json;
using Asterisk.Sdk.VoiceAi.OpenAiRealtime.Internal;

namespace Asterisk.Sdk.VoiceAi.OpenAiRealtime.Tests.Internal;

public class RealtimeJsonContextTests
{
    [Fact]
    public void ServerEventBase_DeserializesType()
    {
        const string json = """{"type":"response.created"}""";
        var evt = JsonSerializer.Deserialize(json, RealtimeJsonContext.Default.ServerEventBase)!;
        evt.Type.Should().Be("response.created");
    }
}
```

- [ ] **Step 2: Create test project to run this test**

```
mkdir -p Tests/Asterisk.Sdk.VoiceAi.OpenAiRealtime.Tests/Internal
```

```xml
<!-- Tests/Asterisk.Sdk.VoiceAi.OpenAiRealtime.Tests/Asterisk.Sdk.VoiceAi.OpenAiRealtime.Tests.csproj -->
<Project Sdk="Microsoft.NET.Sdk">
  <ItemGroup>
    <ProjectReference Include="..\..\src\Asterisk.Sdk.VoiceAi.OpenAiRealtime\Asterisk.Sdk.VoiceAi.OpenAiRealtime.csproj" />
    <!-- AudioSocket needed for AudioSocketServer + AudioSocketClient in bridge tests and DI tests -->
    <ProjectReference Include="..\..\src\Asterisk.Sdk.VoiceAi.AudioSocket\Asterisk.Sdk.VoiceAi.AudioSocket.csproj" />
  </ItemGroup>
  <ItemGroup>
    <PackageReference Include="Microsoft.NET.Test.Sdk" />
    <PackageReference Include="xunit" />
    <PackageReference Include="xunit.runner.visualstudio" />
    <PackageReference Include="FluentAssertions" />
    <PackageReference Include="coverlet.collector" />
    <PackageReference Include="Microsoft.Extensions.DependencyInjection" />
    <PackageReference Include="Microsoft.Extensions.Logging" />
  </ItemGroup>
</Project>
```

- [ ] **Step 3: Run test to see it fail (types not yet defined)**

```bash
dotnet test Tests/Asterisk.Sdk.VoiceAi.OpenAiRealtime.Tests/ -v minimal
```

Expected: Build failure — `ServerEventBase`, `RealtimeJsonContext` not yet defined.

- [ ] **Step 4: Create `Internal/RealtimeMessages.cs`**

```csharp
// src/Asterisk.Sdk.VoiceAi.OpenAiRealtime/Internal/RealtimeMessages.cs
namespace Asterisk.Sdk.VoiceAi.OpenAiRealtime.Internal;

// ─── Outbound (client → OpenAI) ───────────────────────────────────────────────
// Note: session.update is built via Utf8JsonWriter in OpenAiRealtimeBridge.BuildSessionUpdate().
// Only the simpler outbound messages below use JsonSerializer + RealtimeJsonContext.

internal sealed class InputAudioBufferAppendRequest
{
    public string Type => RealtimeProtocol.InputAudioBufferAppend;
    public string Audio { get; set; } = "";
}

internal sealed class InputAudioBufferCommitRequest
{
    public string Type => RealtimeProtocol.InputAudioBufferCommit;
}

internal sealed class ConversationItemCreateRequest
{
    public string Type => RealtimeProtocol.ConversationItemCreate;
    public ConversationItem Item { get; set; } = default!;
}

internal sealed class ConversationItem
{
    public string Type { get; set; } = "";
    public string? CallId { get; set; }
    public string? Output { get; set; }
}

internal sealed class ResponseCreateRequest
{
    public string Type => RealtimeProtocol.ResponseCreate;
}

// ─── Inbound (OpenAI → client) ────────────────────────────────────────────────
// Only the fields the bridge actually reads are mapped — extra fields are ignored.

internal sealed class ServerEventBase
{
    public string Type { get; set; } = "";
}

internal sealed class ResponseAudioDeltaEvent
{
    public string Type { get; set; } = "";
    public string Delta { get; set; } = "";
}

internal sealed class ResponseAudioTranscriptDeltaEvent
{
    public string Type { get; set; } = "";
    public string Delta { get; set; } = "";
}

internal sealed class ResponseAudioTranscriptDoneEvent
{
    public string Type { get; set; } = "";
    public string Transcript { get; set; } = "";
}

internal sealed class FunctionCallArgumentsDoneEvent
{
    public string Type { get; set; } = "";
    public string CallId { get; set; } = "";
    public string Name { get; set; } = "";
    public string Arguments { get; set; } = "";
}

internal sealed class ServerErrorEvent
{
    public string Type { get; set; } = "";
    public ServerError? Error { get; set; }
}

internal sealed class ServerError
{
    public string Message { get; set; } = "";
}
```

- [ ] **Step 5: Create `Internal/RealtimeJsonContext.cs`**

```csharp
// src/Asterisk.Sdk.VoiceAi.OpenAiRealtime/Internal/RealtimeJsonContext.cs
using System.Text.Json.Serialization;

namespace Asterisk.Sdk.VoiceAi.OpenAiRealtime.Internal;

// session.update is built with Utf8JsonWriter — its types are NOT included here.
// Only types that pass through JsonSerializer.Deserialize / JsonSerializer.Serialize
// need [JsonSerializable] entries.
[JsonSerializable(typeof(InputAudioBufferAppendRequest))]
[JsonSerializable(typeof(InputAudioBufferCommitRequest))]
[JsonSerializable(typeof(ConversationItemCreateRequest))]
[JsonSerializable(typeof(ConversationItem))]
[JsonSerializable(typeof(ResponseCreateRequest))]
[JsonSerializable(typeof(ServerEventBase))]
[JsonSerializable(typeof(ResponseAudioDeltaEvent))]
[JsonSerializable(typeof(ResponseAudioTranscriptDeltaEvent))]
[JsonSerializable(typeof(ResponseAudioTranscriptDoneEvent))]
[JsonSerializable(typeof(FunctionCallArgumentsDoneEvent))]
[JsonSerializable(typeof(ServerErrorEvent))]
[JsonSerializable(typeof(ServerError))]
[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.SnakeCaseLower)]
internal sealed partial class RealtimeJsonContext : JsonSerializerContext { }
```

- [ ] **Step 6: Run test to verify it passes**

```bash
dotnet test Tests/Asterisk.Sdk.VoiceAi.OpenAiRealtime.Tests/ --filter "RealtimeJsonContextTests" -v minimal
```

Expected: 1 test passed, 0 failures.

- [ ] **Step 7: Build full solution to verify 0 warnings**

```bash
dotnet build Asterisk.Sdk.slnx
```

Expected: Build succeeded, 0 warnings.

- [ ] **Step 8: Commit**

```bash
git add src/Asterisk.Sdk.VoiceAi.OpenAiRealtime/Internal/ \
        Tests/Asterisk.Sdk.VoiceAi.OpenAiRealtime.Tests/
git commit -m "feat(realtime): add DTOs, RealtimeJsonContext, and test project"
```

---

## Task 4: Observability Events (`RealtimeEvents.cs`)

**Files:**
- Create: `src/Asterisk.Sdk.VoiceAi.OpenAiRealtime/RealtimeEvents.cs`
- Test: `Tests/Asterisk.Sdk.VoiceAi.OpenAiRealtime.Tests/Bridge/OpenAiRealtimeBridgeTests.cs` (started here, expanded in Task 9)

- [ ] **Step 1: Write the failing test**

```csharp
// Tests/Asterisk.Sdk.VoiceAi.OpenAiRealtime.Tests/Events/RealtimeEventsTests.cs
using Asterisk.Sdk.VoiceAi.OpenAiRealtime;

namespace Asterisk.Sdk.VoiceAi.OpenAiRealtime.Tests.Events;

public class RealtimeEventsTests
{
    [Fact]
    public void RealtimeTranscriptEvent_IsARealtimeEvent()
    {
        var channelId = Guid.NewGuid();
        var ts = DateTimeOffset.UtcNow;
        RealtimeEvent evt = new RealtimeTranscriptEvent(channelId, ts, "hello", IsFinal: true);

        evt.ChannelId.Should().Be(channelId);
        evt.Timestamp.Should().Be(ts);
        evt.Should().BeOfType<RealtimeTranscriptEvent>();
    }

    [Fact]
    public void RealtimeResponseEndedEvent_ExposesChannelIdAndDuration()
    {
        var id = Guid.NewGuid();
        var duration = TimeSpan.FromSeconds(2.5);
        var evt = new RealtimeResponseEndedEvent(id, DateTimeOffset.UtcNow, duration);

        evt.ChannelId.Should().Be(id);
        evt.Duration.Should().Be(duration);
    }
}
```

- [ ] **Step 2: Run to confirm failure (types not yet defined)**

```bash
dotnet test Tests/Asterisk.Sdk.VoiceAi.OpenAiRealtime.Tests/ --filter "RealtimeEventsTests" -v minimal
```

Expected: Build failure — `RealtimeEvent`, `RealtimeTranscriptEvent`, etc. not defined.

- [ ] **Step 3: Create `RealtimeEvents.cs`**

```csharp
// src/Asterisk.Sdk.VoiceAi.OpenAiRealtime/RealtimeEvents.cs
namespace Asterisk.Sdk.VoiceAi.OpenAiRealtime;

/// <summary>Base record for all Realtime bridge observability events.</summary>
/// <param name="ChannelId">Identifies which AudioSocket session produced this event.</param>
/// <param name="Timestamp">UTC wall-clock time when the event was created.</param>
public abstract record RealtimeEvent(Guid ChannelId, DateTimeOffset Timestamp);

/// <summary>OpenAI detected that the caller started speaking.</summary>
public sealed record RealtimeSpeechStartedEvent(Guid ChannelId, DateTimeOffset Timestamp)
    : RealtimeEvent(ChannelId, Timestamp);

/// <summary>OpenAI detected that the caller stopped speaking.</summary>
public sealed record RealtimeSpeechStoppedEvent(Guid ChannelId, DateTimeOffset Timestamp)
    : RealtimeEvent(ChannelId, Timestamp);

/// <summary>A transcript fragment or complete transcript was received from OpenAI.</summary>
/// <param name="Transcript">The partial or final transcript text.</param>
/// <param name="IsFinal"><c>true</c> when this is the complete, final transcript for the turn.</param>
public sealed record RealtimeTranscriptEvent(
    Guid ChannelId, DateTimeOffset Timestamp, string Transcript, bool IsFinal)
    : RealtimeEvent(ChannelId, Timestamp);

/// <summary>OpenAI started generating a response.</summary>
public sealed record RealtimeResponseStartedEvent(Guid ChannelId, DateTimeOffset Timestamp)
    : RealtimeEvent(ChannelId, Timestamp);

/// <summary>OpenAI finished (or cancelled) a response.</summary>
/// <param name="Duration">Wall-clock time from <c>response.created</c> to <c>response.done</c>/<c>response.cancelled</c>.</param>
public sealed record RealtimeResponseEndedEvent(
    Guid ChannelId, DateTimeOffset Timestamp, TimeSpan Duration)
    : RealtimeEvent(ChannelId, Timestamp);

/// <summary>A function tool was invoked by OpenAI and its result was sent back.</summary>
public sealed record RealtimeFunctionCalledEvent(
    Guid ChannelId, DateTimeOffset Timestamp,
    string FunctionName, string ArgumentsJson, string ResultJson)
    : RealtimeEvent(ChannelId, Timestamp);

/// <summary>An error event was received from OpenAI.</summary>
public sealed record RealtimeErrorEvent(Guid ChannelId, DateTimeOffset Timestamp, string Message)
    : RealtimeEvent(ChannelId, Timestamp);
```

- [ ] **Step 4: Run tests to confirm they pass**

```bash
dotnet test Tests/Asterisk.Sdk.VoiceAi.OpenAiRealtime.Tests/ --filter "RealtimeEventsTests" -v minimal
```

Expected: 2 tests passed.

- [ ] **Step 5: Commit**

```bash
git add src/Asterisk.Sdk.VoiceAi.OpenAiRealtime/RealtimeEvents.cs \
        Tests/Asterisk.Sdk.VoiceAi.OpenAiRealtime.Tests/Events/RealtimeEventsTests.cs
git commit -m "feat(realtime): add RealtimeEvent hierarchy for observability"
```

---

## Task 5: Function Calling — `IRealtimeFunctionHandler` + `RealtimeFunctionRegistry`

**Files:**
- Create: `src/Asterisk.Sdk.VoiceAi.OpenAiRealtime/FunctionCalling/IRealtimeFunctionHandler.cs`
- Create: `src/Asterisk.Sdk.VoiceAi.OpenAiRealtime/FunctionCalling/RealtimeFunctionRegistry.cs`
- Test: `Tests/Asterisk.Sdk.VoiceAi.OpenAiRealtime.Tests/FunctionCalling/FunctionCallTests.cs` (partial, expanded in Task 10)

- [ ] **Step 1: Write the failing test**

```csharp
// Tests/Asterisk.Sdk.VoiceAi.OpenAiRealtime.Tests/FunctionCalling/FunctionCallTests.cs
using Asterisk.Sdk.VoiceAi.OpenAiRealtime.FunctionCalling;

namespace Asterisk.Sdk.VoiceAi.OpenAiRealtime.Tests.FunctionCalling;

public class FunctionCallTests
{
    private sealed class AddFunction : IRealtimeFunctionHandler
    {
        public string Name => "add";
        public string Description => "Adds two numbers";
        public string ParametersSchema => """{"type":"object","properties":{"a":{"type":"number"},"b":{"type":"number"}},"required":["a","b"]}""";
        public ValueTask<string> ExecuteAsync(string argumentsJson, CancellationToken ct = default)
            => ValueTask.FromResult("""{"result":42}""");
    }

    [Fact]
    public void Registry_TryGetHandler_ReturnsRegisteredHandler()
    {
        var registry = new RealtimeFunctionRegistry([new AddFunction()]);
        var found = registry.TryGetHandler("add", out var handler);

        found.Should().BeTrue();
        handler.Should().NotBeNull();
        handler!.Name.Should().Be("add");
    }

    [Fact]
    public void Registry_TryGetHandler_ReturnsFalseForUnknown()
    {
        var registry = new RealtimeFunctionRegistry([new AddFunction()]);
        var found = registry.TryGetHandler("unknown", out var handler);

        found.Should().BeFalse();
        handler.Should().BeNull();
    }

    [Fact]
    public void Registry_AllHandlers_ContainsRegisteredHandlers()
    {
        var handler = new AddFunction();
        var registry = new RealtimeFunctionRegistry([handler]);

        registry.AllHandlers.Should().ContainSingle()
            .Which.Name.Should().Be("add");
    }
}
```

- [ ] **Step 2: Run to confirm failure**

```bash
dotnet test Tests/Asterisk.Sdk.VoiceAi.OpenAiRealtime.Tests/ --filter "FunctionCallTests" -v minimal
```

Expected: Build failure — types not defined.

- [ ] **Step 3: Create `IRealtimeFunctionHandler.cs`**

```csharp
// src/Asterisk.Sdk.VoiceAi.OpenAiRealtime/FunctionCalling/IRealtimeFunctionHandler.cs
namespace Asterisk.Sdk.VoiceAi.OpenAiRealtime.FunctionCalling;

/// <summary>
/// Represents a tool (function) that can be invoked by the OpenAI Realtime model.
/// Implementations must be registered as singleton or transient — never scoped.
/// </summary>
public interface IRealtimeFunctionHandler
{
    /// <summary>The unique function name sent to OpenAI in the session configuration.</summary>
    string Name { get; }

    /// <summary>Human-readable description of what the function does.</summary>
    string Description { get; }

    /// <summary>
    /// JSON Schema literal that describes the function's parameters.
    /// Inserted verbatim into <c>session.update</c> via <c>Utf8JsonWriter.WriteRawValue</c>.
    /// </summary>
    string ParametersSchema { get; }

    /// <summary>
    /// Executes the function and returns a JSON string result.
    /// On failure, return a JSON error object — do not throw.
    /// </summary>
    ValueTask<string> ExecuteAsync(string argumentsJson, CancellationToken ct = default);
}
```

- [ ] **Step 4: Create `RealtimeFunctionRegistry.cs`**

```csharp
// src/Asterisk.Sdk.VoiceAi.OpenAiRealtime/FunctionCalling/RealtimeFunctionRegistry.cs
namespace Asterisk.Sdk.VoiceAi.OpenAiRealtime.FunctionCalling;

/// <summary>
/// Collects all registered <see cref="IRealtimeFunctionHandler"/> implementations
/// and provides fast lookup by function name.
/// Registered as a singleton by <c>AddOpenAiRealtimeBridge()</c>.
/// </summary>
internal sealed class RealtimeFunctionRegistry
{
    private readonly Dictionary<string, IRealtimeFunctionHandler> _handlers;

    public RealtimeFunctionRegistry(IEnumerable<IRealtimeFunctionHandler> handlers)
    {
        _handlers = handlers.ToDictionary(h => h.Name, StringComparer.Ordinal);
    }

    /// <summary>All registered handlers — used by <c>BuildSessionUpdate</c> to enumerate tools.</summary>
    public IReadOnlyCollection<IRealtimeFunctionHandler> AllHandlers => _handlers.Values;

    /// <summary>
    /// Looks up a handler by function name.
    /// Returns <c>false</c> if no handler is registered for <paramref name="name"/>.
    /// </summary>
    public bool TryGetHandler(string name, [System.Diagnostics.CodeAnalysis.NotNullWhen(true)] out IRealtimeFunctionHandler? handler)
        => _handlers.TryGetValue(name, out handler);
}
```

- [ ] **Step 5: Run tests to confirm they pass**

```bash
dotnet test Tests/Asterisk.Sdk.VoiceAi.OpenAiRealtime.Tests/ --filter "FunctionCallTests" -v minimal
```

Expected: 3 tests passed.

- [ ] **Step 6: Commit**

```bash
git add src/Asterisk.Sdk.VoiceAi.OpenAiRealtime/FunctionCalling/ \
        Tests/Asterisk.Sdk.VoiceAi.OpenAiRealtime.Tests/FunctionCalling/FunctionCallTests.cs
git commit -m "feat(realtime): add IRealtimeFunctionHandler and RealtimeFunctionRegistry"
```

---

## Task 6: Options + Validator + Log

**Files:**
- Create: `src/Asterisk.Sdk.VoiceAi.OpenAiRealtime/OpenAiRealtimeOptions.cs`
- Create: `src/Asterisk.Sdk.VoiceAi.OpenAiRealtime/OpenAiRealtimeOptionsValidator.cs`
- Create: `src/Asterisk.Sdk.VoiceAi.OpenAiRealtime/Internal/RealtimeLog.cs`

- [ ] **Step 1: Create `OpenAiRealtimeOptions.cs`**

```csharp
// src/Asterisk.Sdk.VoiceAi.OpenAiRealtime/OpenAiRealtimeOptions.cs
using System.ComponentModel.DataAnnotations;
using Asterisk.Sdk.Audio;

namespace Asterisk.Sdk.VoiceAi.OpenAiRealtime;

/// <summary>Configuration for the <see cref="OpenAiRealtimeBridge"/>.</summary>
public sealed class OpenAiRealtimeOptions
{
    /// <summary>OpenAI API key (required).</summary>
    [Required]
    public string ApiKey { get; set; } = string.Empty;

    /// <summary>OpenAI Realtime model identifier (required).</summary>
    [Required]
    public string Model { get; set; } = "gpt-4o-realtime-preview";

    /// <summary>Voice for TTS output. Defaults to <c>alloy</c>.</summary>
    public string Voice { get; set; } = "alloy";

    /// <summary>System instructions sent to the model in <c>session.update</c>.</summary>
    public string Instructions { get; set; } = string.Empty;

    /// <summary>VAD mode. <see cref="VadMode.ServerSide"/> (default) lets OpenAI detect turn boundaries.</summary>
    public VadMode VadMode { get; set; } = VadMode.ServerSide;

    /// <summary>
    /// Audio format of the Asterisk AudioSocket stream.
    /// The bridge resamples between <see cref="AudioFormat.SampleRate"/> and 24000 Hz (OpenAI's required rate).
    /// If <see cref="AudioFormat.SampleRate"/> is already 24000, no resampling is applied.
    /// </summary>
    public AudioFormat InputFormat { get; set; } = AudioFormat.Slin16Mono8kHz;
}
```

- [ ] **Step 2: Create `OpenAiRealtimeOptionsValidator.cs`**

```csharp
// src/Asterisk.Sdk.VoiceAi.OpenAiRealtime/OpenAiRealtimeOptionsValidator.cs
using Microsoft.Extensions.Options;

namespace Asterisk.Sdk.VoiceAi.OpenAiRealtime;

/// <summary>AOT-safe validator for <see cref="OpenAiRealtimeOptions"/>.</summary>
[OptionsValidator]
public sealed partial class OpenAiRealtimeOptionsValidator : IValidateOptions<OpenAiRealtimeOptions> { }
```

- [ ] **Step 3: Create `Internal/RealtimeLog.cs`**

```csharp
// src/Asterisk.Sdk.VoiceAi.OpenAiRealtime/Internal/RealtimeLog.cs
using Microsoft.Extensions.Logging;

namespace Asterisk.Sdk.VoiceAi.OpenAiRealtime.Internal;

internal static partial class RealtimeLog
{
    [LoggerMessage(Level = LogLevel.Information, Message = "[{ChannelId}] Realtime session started")]
    public static partial void SessionStarted(ILogger logger, Guid channelId);

    [LoggerMessage(Level = LogLevel.Information, Message = "[{ChannelId}] Realtime session ended")]
    public static partial void SessionEnded(ILogger logger, Guid channelId);

    [LoggerMessage(Level = LogLevel.Information, Message = "[{ChannelId}] WebSocket connected to OpenAI Realtime API")]
    public static partial void WebSocketConnected(ILogger logger, Guid channelId);

    [LoggerMessage(Level = LogLevel.Information, Message = "[{ChannelId}] session.created received")]
    public static partial void SessionCreated(ILogger logger, Guid channelId);

    [LoggerMessage(Level = LogLevel.Warning, Message = "[{ChannelId}] Barge-in: response cancelled by OpenAI")]
    public static partial void ResponseCancelled(ILogger logger, Guid channelId);

    [LoggerMessage(Level = LogLevel.Warning, Message = "[{ChannelId}] Unknown function tool '{FunctionName}' — ignoring")]
    public static partial void UnknownFunction(ILogger logger, Guid channelId, string functionName);

    [LoggerMessage(Level = LogLevel.Error, Message = "[{ChannelId}] Realtime session error: {Message}")]
    public static partial void SessionError(ILogger logger, Guid channelId, string message);

    [LoggerMessage(Level = LogLevel.Error, Message = "[{ChannelId}] OpenAI error: {Message}")]
    public static partial void OpenAiError(ILogger logger, Guid channelId, string message);
}
```

- [ ] **Step 4: Build to verify 0 warnings**

```bash
dotnet build src/Asterisk.Sdk.VoiceAi.OpenAiRealtime/Asterisk.Sdk.VoiceAi.OpenAiRealtime.csproj
```

Expected: Build succeeded, 0 warnings.

- [ ] **Step 5: Commit**

```bash
git add src/Asterisk.Sdk.VoiceAi.OpenAiRealtime/OpenAiRealtimeOptions.cs \
        src/Asterisk.Sdk.VoiceAi.OpenAiRealtime/OpenAiRealtimeOptionsValidator.cs \
        src/Asterisk.Sdk.VoiceAi.OpenAiRealtime/Internal/RealtimeLog.cs
git commit -m "feat(realtime): add OpenAiRealtimeOptions, validator, and log helpers"
```

---

## Task 7: `OpenAiRealtimeBridge` — Main Implementation

**Files:**
- Create: `src/Asterisk.Sdk.VoiceAi.OpenAiRealtime/OpenAiRealtimeBridge.cs`

This is the core file. Study the spec carefully before writing — the dual-loop, `BuildSessionUpdate`, write lock, and resampling are all detailed in the spec's "Ciclo de vida de WebSocket" and "DTOs internos" sections.

- [ ] **Step 1: Create `OpenAiRealtimeBridge.cs`**

```csharp
// src/Asterisk.Sdk.VoiceAi.OpenAiRealtime/OpenAiRealtimeBridge.cs
using System.Buffers;
using System.Net.WebSockets;
using System.Reactive.Subjects;
using System.Text;
using System.Text.Json;
using Asterisk.Sdk.Audio;
using Asterisk.Sdk.Audio.Processing;
using Asterisk.Sdk.VoiceAi.AudioSocket;
using Asterisk.Sdk.VoiceAi.OpenAiRealtime.FunctionCalling;
using Asterisk.Sdk.VoiceAi.OpenAiRealtime.Internal;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Asterisk.Sdk.VoiceAi.OpenAiRealtime;

/// <summary>
/// Bridges an Asterisk AudioSocket session to the OpenAI Realtime API via a persistent WebSocket.
/// Replaces the full STT→LLM→TTS chain with a single streaming connection.
/// </summary>
/// <remarks>
/// This class is a singleton. Each call to <see cref="HandleSessionAsync"/> creates fully isolated
/// per-session state (WebSocket, write lock, resamplers) as local variables — no shared mutable state.
/// </remarks>
public sealed class OpenAiRealtimeBridge : ISessionHandler, IAsyncDisposable
{
    private static readonly Uri BaseUri = new("wss://api.openai.com/v1/realtime");

    private readonly OpenAiRealtimeOptions _options;
    private readonly RealtimeFunctionRegistry _registry;
    private readonly ILogger<OpenAiRealtimeBridge> _logger;
    private readonly Subject<RealtimeEvent> _events = new();

    /// <summary>Observable stream of Realtime bridge events from all active sessions.</summary>
    public IObservable<RealtimeEvent> Events => _events;

    /// <summary>Creates a new bridge instance.</summary>
    public OpenAiRealtimeBridge(
        IOptions<OpenAiRealtimeOptions> options,
        RealtimeFunctionRegistry registry,
        ILogger<OpenAiRealtimeBridge> logger)
    {
        _options = options.Value;
        _registry = registry;
        _logger = logger;
    }

    /// <inheritdoc/>
    public async ValueTask HandleSessionAsync(AudioSocketSession session, CancellationToken ct = default)
    {
        var channelId = session.ChannelId;
        RealtimeLog.SessionStarted(_logger, channelId);

        // ── Per-session state (stack-lifetime) ──────────────────────────────
        using var ws = new ClientWebSocket();
        ws.Options.SetRequestHeader("Authorization", $"Bearer {_options.ApiKey}");
        ws.Options.SetRequestHeader("OpenAI-Beta", "realtime=v1");

        var uri = new Uri($"{BaseUri}?model={Uri.EscapeDataString(_options.Model)}");
        await ws.ConnectAsync(uri, ct).ConfigureAwait(false);
        RealtimeLog.WebSocketConnected(_logger, channelId);

        using var wsWriteLock = new SemaphoreSlim(1, 1);

        var inputRate = _options.InputFormat.SampleRate;
        var upsampler   = inputRate != 24000 ? ResamplerFactory.Create(inputRate, 24000) : null;
        var downsampler = inputRate != 24000 ? ResamplerFactory.Create(24000, inputRate) : null;

        // Send session.update (voice, instructions, VAD, tools)
        var sessionUpdateBytes = BuildSessionUpdate(_registry.AllHandlers, _options);
        await wsWriteLock.WaitAsync(ct).ConfigureAwait(false);
        try { await ws.SendAsync(sessionUpdateBytes, WebSocketMessageType.Text, true, ct).ConfigureAwait(false); }
        finally { wsWriteLock.Release(); }

        DateTimeOffset responseStartTime = default;

        try
        {
            await Task.WhenAll(
                InputLoop(session, ws, wsWriteLock, upsampler, ct),
                OutputLoop(session, ws, wsWriteLock, downsampler, ref responseStartTime, ct)
            ).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { /* expected */ }
        catch (Exception ex)
        {
            RealtimeLog.SessionError(_logger, channelId, ex.Message);
            throw;
        }
        finally
        {
            // Clean close — do not use ct (already cancelled)
            await wsWriteLock.WaitAsync(CancellationToken.None).ConfigureAwait(false);
            try
            {
                if (ws.State == WebSocketState.Open)
                    await ws.CloseOutputAsync(WebSocketCloseStatus.NormalClosure, "", CancellationToken.None).ConfigureAwait(false);
            }
            catch { /* ignore close errors */ }
            finally { wsWriteLock.Release(); }

            RealtimeLog.SessionEnded(_logger, channelId);
        }
    }

    // ── InputLoop — Asterisk audio → OpenAI ─────────────────────────────────
    private async Task InputLoop(
        AudioSocketSession session,
        ClientWebSocket ws,
        SemaphoreSlim wsWriteLock,
        PolyphaseResampler? upsampler,
        CancellationToken ct)
    {
        await foreach (var frame in session.ReadAudioAsync(ct).ConfigureAwait(false))
        {
            // Resample 8kHz → 24kHz if needed
            var pcm = upsampler is not null ? upsampler.Process(frame) : frame;

            // Base64-encode and send as input_audio_buffer.append
            var audio = Convert.ToBase64String(pcm.Span);
            var req = new InputAudioBufferAppendRequest { Audio = audio };
            var bytes = JsonSerializer.SerializeToUtf8Bytes(req, RealtimeJsonContext.Default.InputAudioBufferAppendRequest);

            await wsWriteLock.WaitAsync(ct).ConfigureAwait(false);
            try { await ws.SendAsync(bytes, WebSocketMessageType.Text, true, ct).ConfigureAwait(false); }
            finally { wsWriteLock.Release(); }
        }
    }

    // ── OutputLoop — OpenAI events → Asterisk + event stream ────────────────
    private async Task OutputLoop(
        AudioSocketSession session,
        ClientWebSocket ws,
        SemaphoreSlim wsWriteLock,
        PolyphaseResampler? downsampler,
        ref DateTimeOffset responseStartTime,
        CancellationToken ct)
    {
        var channelId = session.ChannelId;
        var buf = new byte[1024 * 64];

        while (ws.State is WebSocketState.Open or WebSocketState.CloseSent)
        {
            WebSocketReceiveResult result;
            try
            {
                result = await ws.ReceiveAsync(buf.AsMemory(), ct).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested) { return; }
            catch { return; }

            if (result.MessageType == WebSocketMessageType.Close) return;
            if (result.MessageType != WebSocketMessageType.Text) continue;

            var json = Encoding.UTF8.GetString(buf, 0, result.Count);

            // Two-pass decode: first read type, then deserialize to specific DTO
            var baseEvt = JsonSerializer.Deserialize(json, RealtimeJsonContext.Default.ServerEventBase);
            if (baseEvt is null) continue;

            switch (baseEvt.Type)
            {
                case RealtimeProtocol.SessionCreated:
                    RealtimeLog.SessionCreated(_logger, channelId);
                    break;

                case RealtimeProtocol.ResponseAudioDelta:
                {
                    var audioEvt = JsonSerializer.Deserialize(json, RealtimeJsonContext.Default.ResponseAudioDeltaEvent);
                    if (audioEvt is null) break;
                    var pcm16 = Convert.FromBase64String(audioEvt.Delta).AsMemory();
                    var pcm = downsampler is not null ? downsampler.Process(pcm16) : pcm16;
                    await session.WriteAudioAsync(pcm, ct).ConfigureAwait(false);
                    break;
                }

                case RealtimeProtocol.ResponseAudioTranscriptDelta:
                {
                    var tEvt = JsonSerializer.Deserialize(json, RealtimeJsonContext.Default.ResponseAudioTranscriptDeltaEvent);
                    if (tEvt is not null)
                        Publish(new RealtimeTranscriptEvent(channelId, DateTimeOffset.UtcNow, tEvt.Delta, IsFinal: false));
                    break;
                }

                case RealtimeProtocol.ResponseAudioTranscriptDone:
                {
                    var tEvt = JsonSerializer.Deserialize(json, RealtimeJsonContext.Default.ResponseAudioTranscriptDoneEvent);
                    if (tEvt is not null)
                        Publish(new RealtimeTranscriptEvent(channelId, DateTimeOffset.UtcNow, tEvt.Transcript, IsFinal: true));
                    break;
                }

                case RealtimeProtocol.ResponseCreated:
                    responseStartTime = DateTimeOffset.UtcNow;
                    Publish(new RealtimeResponseStartedEvent(channelId, responseStartTime));
                    break;

                case RealtimeProtocol.ResponseDone:
                {
                    var duration = responseStartTime != default
                        ? DateTimeOffset.UtcNow - responseStartTime
                        : TimeSpan.Zero;
                    Publish(new RealtimeResponseEndedEvent(channelId, DateTimeOffset.UtcNow, duration));
                    responseStartTime = default;
                    break;
                }

                case RealtimeProtocol.ResponseCancelled:
                {
                    RealtimeLog.ResponseCancelled(_logger, channelId);
                    if (responseStartTime != default)
                    {
                        var duration = DateTimeOffset.UtcNow - responseStartTime;
                        Publish(new RealtimeResponseEndedEvent(channelId, DateTimeOffset.UtcNow, duration));
                        responseStartTime = default;
                    }
                    break;
                }

                case RealtimeProtocol.ResponseFunctionCallArgumentsDone:
                    await HandleFunctionCallAsync(
                        json, channelId, ws, wsWriteLock, ct).ConfigureAwait(false);
                    break;

                case RealtimeProtocol.InputAudioBufferSpeechStarted:
                    Publish(new RealtimeSpeechStartedEvent(channelId, DateTimeOffset.UtcNow));
                    break;

                case RealtimeProtocol.InputAudioBufferSpeechStopped:
                    Publish(new RealtimeSpeechStoppedEvent(channelId, DateTimeOffset.UtcNow));
                    break;

                case RealtimeProtocol.Error:
                {
                    var errEvt = JsonSerializer.Deserialize(json, RealtimeJsonContext.Default.ServerErrorEvent);
                    var msg = errEvt?.Error?.Message ?? "unknown error";
                    RealtimeLog.OpenAiError(_logger, channelId, msg);
                    Publish(new RealtimeErrorEvent(channelId, DateTimeOffset.UtcNow, msg));
                    break;
                }

                // All other events (response.audio.done, session.updated, etc.) are intentionally ignored.
            }
        }
    }

    private async Task HandleFunctionCallAsync(
        string json,
        Guid channelId,
        ClientWebSocket ws,
        SemaphoreSlim wsWriteLock,
        CancellationToken ct)
    {
        var fnEvt = JsonSerializer.Deserialize(json, RealtimeJsonContext.Default.FunctionCallArgumentsDoneEvent);
        if (fnEvt is null) return;

        if (!_registry.TryGetHandler(fnEvt.Name, out var handler))
        {
            RealtimeLog.UnknownFunction(_logger, channelId, fnEvt.Name);
            return;
        }

        string resultJson;
        try
        {
            resultJson = await handler.ExecuteAsync(fnEvt.Arguments, ct).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            resultJson = $"{{\"error\":\"{ex.Message.Replace("\"", "\\\"", StringComparison.Ordinal)}\"}}";
        }

        var itemCreate = new ConversationItemCreateRequest
        {
            Item = new ConversationItem
            {
                Type = "function_call_output",
                CallId = fnEvt.CallId,
                Output = resultJson
            }
        };
        var itemCreateBytes = JsonSerializer.SerializeToUtf8Bytes(
            itemCreate, RealtimeJsonContext.Default.ConversationItemCreateRequest);

        var responseCreate = new ResponseCreateRequest();
        var responseCreateBytes = JsonSerializer.SerializeToUtf8Bytes(
            responseCreate, RealtimeJsonContext.Default.ResponseCreateRequest);

        await wsWriteLock.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            await ws.SendAsync(itemCreateBytes, WebSocketMessageType.Text, true, ct).ConfigureAwait(false);
            await ws.SendAsync(responseCreateBytes, WebSocketMessageType.Text, true, ct).ConfigureAwait(false);
        }
        finally { wsWriteLock.Release(); }

        Publish(new RealtimeFunctionCalledEvent(
            channelId, DateTimeOffset.UtcNow, fnEvt.Name, fnEvt.Arguments, resultJson));
    }

    // ── session.update builder (Utf8JsonWriter — NOT JsonSerializer) ─────────
    // Uses WriteRawValue for tools[].parameters to insert literal JSON schema strings.
    private static ReadOnlyMemory<byte> BuildSessionUpdate(
        IReadOnlyCollection<IRealtimeFunctionHandler> tools,
        OpenAiRealtimeOptions opts)
    {
        var buffer = new ArrayBufferWriter<byte>();
        using var writer = new Utf8JsonWriter(buffer);

        writer.WriteStartObject();
        writer.WriteString("type", RealtimeProtocol.SessionUpdate);
        writer.WritePropertyName("session");
        writer.WriteStartObject();
        writer.WriteString("voice", opts.Voice);
        writer.WriteStartArray("modalities");
        writer.WriteStringValue("audio");
        writer.WriteStringValue("text");
        writer.WriteEndArray();
        writer.WriteString("instructions", opts.Instructions);

        if (opts.VadMode == VadMode.ServerSide)
        {
            writer.WritePropertyName("turn_detection");
            writer.WriteStartObject();
            writer.WriteString("type", "server_vad");
            writer.WriteEndObject();
        }

        writer.WriteStartArray("tools");
        foreach (var handler in tools)
        {
            writer.WriteStartObject();
            writer.WriteString("type", "function");
            writer.WriteString("name", handler.Name);
            writer.WriteString("description", handler.Description);
            writer.WritePropertyName("parameters");
            writer.WriteRawValue(handler.ParametersSchema, skipInputValidation: false);
            writer.WriteEndObject();
        }
        writer.WriteEndArray();
        writer.WriteEndObject(); // session
        writer.WriteEndObject(); // root
        writer.Flush();

        return buffer.WrittenMemory;
    }

    private void Publish(RealtimeEvent evt) => _events.OnNext(evt);

    /// <inheritdoc/>
    public ValueTask DisposeAsync()
    {
        GC.SuppressFinalize(this);
        _events.OnCompleted();
        _events.Dispose();
        return ValueTask.CompletedTask;
    }
}
```

- [ ] **Step 2: Build the package to verify 0 warnings**

```bash
dotnet build src/Asterisk.Sdk.VoiceAi.OpenAiRealtime/Asterisk.Sdk.VoiceAi.OpenAiRealtime.csproj
```

Expected: Build succeeded, 0 warnings. If there are issues with `PolyphaseResampler.Process` signature (it may return `ReadOnlyMemory<byte>` — check `Asterisk.Sdk.Audio`), adjust accordingly.

- [ ] **Step 3: Commit**

```bash
git add src/Asterisk.Sdk.VoiceAi.OpenAiRealtime/OpenAiRealtimeBridge.cs
git commit -m "feat(realtime): implement OpenAiRealtimeBridge with dual-loop and function calling"
```

---

## Task 8: DI Extension — `RealtimeServiceCollectionExtensions`

**Files:**
- Create: `src/Asterisk.Sdk.VoiceAi.OpenAiRealtime/DependencyInjection/RealtimeServiceCollectionExtensions.cs`

- [ ] **Step 1: Create the DI extension**

```csharp
// src/Asterisk.Sdk.VoiceAi.OpenAiRealtime/DependencyInjection/RealtimeServiceCollectionExtensions.cs
using System.Diagnostics.CodeAnalysis;
using Asterisk.Sdk.VoiceAi.OpenAiRealtime.FunctionCalling;
using Asterisk.Sdk.VoiceAi.Pipeline;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace Asterisk.Sdk.VoiceAi.OpenAiRealtime.DependencyInjection;

/// <summary>Extension methods for registering the OpenAI Realtime bridge in the DI container.</summary>
public static class RealtimeServiceCollectionExtensions
{
    /// <summary>
    /// Registers <see cref="OpenAiRealtimeBridge"/> as the <see cref="ISessionHandler"/> singleton
    /// and starts <see cref="VoiceAiSessionBroker"/> as a hosted service.
    /// </summary>
    /// <remarks>
    /// Prerequisite: <c>services.AddAudioSocketServer()</c> must be called before this method.
    /// </remarks>
    /// <returns>The service collection for chaining <c>AddFunction&lt;T&gt;()</c> calls.</returns>
    public static IServiceCollection AddOpenAiRealtimeBridge(
        this IServiceCollection services,
        Action<OpenAiRealtimeOptions>? configure = null)
    {
        if (configure is not null)
            services.Configure(configure);
        else
            services.AddOptions<OpenAiRealtimeOptions>();

        services.AddSingleton<OpenAiRealtimeOptionsValidator>();
        services.AddSingleton<Microsoft.Extensions.Options.IValidateOptions<OpenAiRealtimeOptions>>(
            sp => sp.GetRequiredService<OpenAiRealtimeOptionsValidator>());

        services.TryAddSingleton<RealtimeFunctionRegistry>();
        services.TryAddSingleton<OpenAiRealtimeBridge>();
        services.TryAddSingleton<ISessionHandler>(
            sp => sp.GetRequiredService<OpenAiRealtimeBridge>());

        services.TryAddSingleton<VoiceAiSessionBroker>();
        services.AddHostedService<VoiceAiSessionBroker>(
            sp => sp.GetRequiredService<VoiceAiSessionBroker>());

        return services;
    }

    /// <summary>
    /// Registers a function tool that can be invoked by the OpenAI Realtime model.
    /// Multiple calls to <c>AddFunction</c> add multiple handlers.
    /// </summary>
    /// <typeparam name="THandler">The <see cref="IRealtimeFunctionHandler"/> implementation. Must be singleton-safe.</typeparam>
    public static IServiceCollection AddFunction<[DynamicallyAccessedMembers(DynamicallyAccessedMemberTypes.PublicConstructors)] THandler>(
        this IServiceCollection services)
        where THandler : class, IRealtimeFunctionHandler
    {
        // Intentionally NOT TryAddSingleton — allows multiple different handlers.
        services.AddSingleton<IRealtimeFunctionHandler, THandler>();
        return services;
    }
}
```

- [ ] **Step 2: Build full solution**

```bash
dotnet build Asterisk.Sdk.slnx
```

Expected: Build succeeded, 0 warnings.

- [ ] **Step 3: Commit**

```bash
git add src/Asterisk.Sdk.VoiceAi.OpenAiRealtime/DependencyInjection/
git commit -m "feat(realtime): add RealtimeServiceCollectionExtensions DI registration"
```

---

## Task 9: `RealtimeFakeServer` + Bridge Integration Tests

**Files:**
- Create: `Tests/Asterisk.Sdk.VoiceAi.OpenAiRealtime.Tests/Internal/RealtimeFakeServer.cs`
- Create: `Tests/Asterisk.Sdk.VoiceAi.OpenAiRealtime.Tests/Bridge/OpenAiRealtimeBridgeTests.cs`

The fake server pattern follows `ElevenLabsFakeServer` exactly — uses `HttpListener` to accept WebSocket connections in-process (`http://localhost:{port}/`). `ClientWebSocket` connects with `ws://localhost:{port}/` — this works because `ws://` = unencrypted WebSocket over HTTP, which is what `HttpListener` serves. No TLS needed for local tests.

**`AudioSocketSession` is `internal`-constructed** — it cannot be instantiated directly. Tests must spin up a real `AudioSocketServer` (port=0 for OS assignment) and connect an `AudioSocketClient`, matching the pattern used by `VoiceAiPipelineTests.cs`. The bridge's `InputLoop` ends when the `AudioSocketClient` calls `SendHangupAsync()` (or the CancellationToken is cancelled).

**Important:** The bridge connects to `wss://api.openai.com/...` — tests need a way to override this. Add an `internal Uri? TestBaseUri { get; set; }` property (accessible via `InternalsVisibleTo`). If set, the bridge uses it instead.

- [ ] **Step 1: Add internal test hook to `OpenAiRealtimeBridge`**

In `OpenAiRealtimeBridge.cs`, add after the `BaseUri` field:

```csharp
// Test hook — allows unit tests to redirect WebSocket connections to a local fake server.
// Set via InternalsVisibleTo before calling HandleSessionAsync.
internal Uri? TestBaseUri { get; set; }
```

And update `HandleSessionAsync` to use it:
```csharp
var effectiveBaseUri = TestBaseUri ?? BaseUri;
var uri = new Uri($"{effectiveBaseUri}?model={Uri.EscapeDataString(_options.Model)}");
```

- [ ] **Step 2: Create `RealtimeFakeServer.cs`**

```csharp
// Tests/Asterisk.Sdk.VoiceAi.OpenAiRealtime.Tests/Internal/RealtimeFakeServer.cs
using System.Net;
using System.Net.WebSockets;
using System.Text;

namespace Asterisk.Sdk.VoiceAi.OpenAiRealtime.Tests.Internal;

/// <summary>
/// In-process WebSocket server that simulates the OpenAI Realtime API protocol.
/// Sends <c>session.created</c> on connect, then delivers configured events.
/// Bridge connects with <c>ws://localhost:{Port}/</c> via TestBaseUri override.
/// </summary>
internal sealed class RealtimeFakeServer : IAsyncDisposable
{
    private readonly HttpListener _listener;
    private readonly CancellationTokenSource _cts = new();
    private Task? _acceptLoop;

    public int Port { get; }
    public List<string> ReceivedMessages { get; } = [];

    /// <summary>JSON event strings to send after session.created, in order.</summary>
    public List<string> EventsToSend { get; } = [];

    public RealtimeFakeServer()
    {
        for (int attempt = 0; attempt < 10; attempt++)
        {
            var tcp = new System.Net.Sockets.TcpListener(IPAddress.Loopback, 0);
            tcp.Start();
            var port = ((IPEndPoint)tcp.LocalEndpoint).Port;
            tcp.Stop();

            var listener = new HttpListener();
            listener.Prefixes.Add($"http://localhost:{port}/");
            try
            {
                listener.Start();
                _listener = listener;
                Port = port;
                goto success;
            }
            catch (HttpListenerException) when (attempt < 9)
            {
                listener.Close();
            }
        }
        throw new InvalidOperationException("Failed to allocate a port for the fake Realtime server.");

        success: ;
    }

    public void Start() => _acceptLoop = Task.Run(AcceptLoopAsync);

    private async Task AcceptLoopAsync()
    {
        try
        {
            while (!_cts.Token.IsCancellationRequested)
            {
                var ctx = await _listener.GetContextAsync().WaitAsync(_cts.Token).ConfigureAwait(false);
                if (ctx.Request.IsWebSocketRequest)
                    _ = Task.Run(() => HandleWebSocketAsync(ctx), _cts.Token);
                else
                    ctx.Response.Close();
            }
        }
        catch (OperationCanceledException) { }
        catch (HttpListenerException) { }
    }

    private async Task HandleWebSocketAsync(HttpListenerContext ctx)
    {
        var wsCtx = await ctx.AcceptWebSocketAsync(null).ConfigureAwait(false);
        var ws = wsCtx.WebSocket;
        var buf = new byte[65536];

        // Receive loop in background (captures client messages)
        var receiveTask = Task.Run(async () =>
        {
            while (ws.State is WebSocketState.Open or WebSocketState.CloseSent)
            {
                try
                {
                    var result = await ws.ReceiveAsync(buf.AsMemory(), _cts.Token).ConfigureAwait(false);
                    if (result.MessageType == WebSocketMessageType.Text)
                        ReceivedMessages.Add(Encoding.UTF8.GetString(buf, 0, result.Count));
                    else if (result.MessageType == WebSocketMessageType.Close)
                        break;
                }
                catch { break; }
            }
        });

        // Send session.created first
        await SendJsonAsync(ws, """{"type":"session.created","session":{}}""").ConfigureAwait(false);

        // Small delay to let client process session.created and send session.update
        await Task.Delay(30).ConfigureAwait(false);

        // Send configured events in sequence
        var events = EventsToSend.ToList();
        foreach (var evt in events)
        {
            if (ws.State is not (WebSocketState.Open or WebSocketState.CloseReceived)) break;
            await SendJsonAsync(ws, evt).ConfigureAwait(false);
            await Task.Delay(5).ConfigureAwait(false);
        }

        // Wait briefly then close — this ends the OutputLoop in the bridge
        await Task.Delay(100).ConfigureAwait(false);

        try
        {
            if (ws.State == WebSocketState.Open)
                await ws.CloseAsync(WebSocketCloseStatus.NormalClosure, "done", CancellationToken.None).ConfigureAwait(false);
            else if (ws.State == WebSocketState.CloseReceived)
                await ws.CloseOutputAsync(WebSocketCloseStatus.NormalClosure, "done", CancellationToken.None).ConfigureAwait(false);
        }
        catch { }

        try { await receiveTask.ConfigureAwait(false); } catch { }
    }

    private static async Task SendJsonAsync(WebSocket ws, string json)
    {
        var bytes = Encoding.UTF8.GetBytes(json);
        await ws.SendAsync(bytes.AsMemory(), WebSocketMessageType.Text, true, CancellationToken.None)
            .ConfigureAwait(false);
    }

    public async ValueTask DisposeAsync()
    {
        GC.SuppressFinalize(this);
        _cts.Cancel();
        _listener.Stop();
        if (_acceptLoop is not null)
            try { await _acceptLoop.ConfigureAwait(false); } catch { }
        _cts.Dispose();
    }
}
```

- [ ] **Step 3: Create `Bridge/OpenAiRealtimeBridgeTests.cs`**

**Pattern:** Use a real `AudioSocketServer` (port=0) + `AudioSocketClient` to obtain an `AudioSocketSession` — exactly as `VoiceAiPipelineTests` does. After sending events, the fake server closes the WebSocket (ending OutputLoop), and the test cancels the CancellationToken to also end InputLoop. Each test creates its own server+client pair.

```csharp
// Tests/Asterisk.Sdk.VoiceAi.OpenAiRealtime.Tests/Bridge/OpenAiRealtimeBridgeTests.cs
using Asterisk.Sdk.Audio;
using Asterisk.Sdk.VoiceAi.AudioSocket;
using Asterisk.Sdk.VoiceAi.OpenAiRealtime;
using Asterisk.Sdk.VoiceAi.OpenAiRealtime.FunctionCalling;
using Asterisk.Sdk.VoiceAi.OpenAiRealtime.Tests.Internal;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace Asterisk.Sdk.VoiceAi.OpenAiRealtime.Tests.Bridge;

public class OpenAiRealtimeBridgeTests
{
    /// <summary>Spin up a real AudioSocket server+client and capture the session.</summary>
    private static async Task<(AudioSocketSession session, AudioSocketServer audioServer, AudioSocketClient client)>
        CreateAudioSessionAsync()
    {
        var audioServer = new AudioSocketServer(
            new AudioSocketOptions { Port = 0 },
            NullLogger<AudioSocketServer>.Instance);

        var tcs = new TaskCompletionSource<AudioSocketSession>();
        audioServer.OnSessionStarted += session =>
        {
            tcs.TrySetResult(session);
            return ValueTask.CompletedTask;
        };

        await audioServer.StartAsync(CancellationToken.None);

        var client = new AudioSocketClient("127.0.0.1", audioServer.BoundPort, Guid.NewGuid());
        await client.ConnectAsync(CancellationToken.None);

        var session = await tcs.Task.WaitAsync(TimeSpan.FromSeconds(2));
        return (session, audioServer, client);
    }

    private static OpenAiRealtimeBridge CreateBridge(
        RealtimeFakeServer fakeOpenAi,
        IEnumerable<IRealtimeFunctionHandler>? handlers = null)
    {
        var options = Options.Create(new OpenAiRealtimeOptions
        {
            ApiKey = "test-key",
            Model = "gpt-4o-realtime-preview",
            Voice = "alloy",
            InputFormat = AudioFormat.Slin16Mono8kHz,
        });
        var registry = new RealtimeFunctionRegistry(handlers ?? []);
        var bridge = new OpenAiRealtimeBridge(options, registry, NullLogger<OpenAiRealtimeBridge>.Instance);
        // Redirect WebSocket to local fake server (ws:// — HttpListener accepts unencrypted WS)
        bridge.TestBaseUri = new Uri($"ws://localhost:{fakeOpenAi.Port}/");
        return bridge;
    }

    [Fact]
    public async Task HandleSessionAsync_SendsSessionUpdate_OnConnect()
    {
        await using var fakeOpenAi = new RealtimeFakeServer();
        fakeOpenAi.Start();

        var (session, audioServer, client) = await CreateAudioSessionAsync();
        var bridge = CreateBridge(fakeOpenAi);

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        // After fake server closes WS (OutputLoop exits), cancel to end InputLoop
        var bridgeTask = bridge.HandleSessionAsync(session, cts.Token).AsTask();

        // Fake server closes after sending session.created — cancel InputLoop after that
        await Task.Delay(200);
        await cts.CancelAsync();
        await client.SendHangupAsync();

        try { await bridgeTask.WaitAsync(TimeSpan.FromSeconds(3)); } catch { }

        var sessionUpdate = fakeOpenAi.ReceivedMessages
            .FirstOrDefault(m => m.Contains("\"type\":\"session.update\""));

        sessionUpdate.Should().NotBeNull("bridge must send session.update after connecting");
        sessionUpdate!.Should().Contain("\"voice\":\"alloy\"");

        await audioServer.StopAsync(CancellationToken.None);
    }

    [Fact]
    public async Task HandleSessionAsync_PublishesResponseStartedAndEndedEvents()
    {
        await using var fakeOpenAi = new RealtimeFakeServer();
        fakeOpenAi.EventsToSend.Add("""{"type":"response.created"}""");
        fakeOpenAi.EventsToSend.Add("""{"type":"response.done"}""");
        fakeOpenAi.Start();

        var (session, audioServer, client) = await CreateAudioSessionAsync();
        var bridge = CreateBridge(fakeOpenAi);
        var events = new List<RealtimeEvent>();
        bridge.Events.Subscribe(e => events.Add(e));

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        var bridgeTask = bridge.HandleSessionAsync(session, cts.Token).AsTask();

        await Task.Delay(300);
        await cts.CancelAsync();
        await client.SendHangupAsync();

        try { await bridgeTask.WaitAsync(TimeSpan.FromSeconds(3)); } catch { }

        events.Should().ContainSingle(e => e is RealtimeResponseStartedEvent);
        events.Should().ContainSingle(e => e is RealtimeResponseEndedEvent);

        await audioServer.StopAsync(CancellationToken.None);
    }

    [Fact]
    public async Task HandleSessionAsync_PublishesTranscriptEvents()
    {
        await using var fakeOpenAi = new RealtimeFakeServer();
        fakeOpenAi.EventsToSend.Add("""{"type":"response.audio_transcript.delta","delta":"hell"}""");
        fakeOpenAi.EventsToSend.Add("""{"type":"response.audio_transcript.done","transcript":"hello world"}""");
        fakeOpenAi.Start();

        var (session, audioServer, client) = await CreateAudioSessionAsync();
        var bridge = CreateBridge(fakeOpenAi);
        var events = new List<RealtimeEvent>();
        bridge.Events.Subscribe(e => events.Add(e));

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        var bridgeTask = bridge.HandleSessionAsync(session, cts.Token).AsTask();

        await Task.Delay(300);
        await cts.CancelAsync();
        await client.SendHangupAsync();
        try { await bridgeTask.WaitAsync(TimeSpan.FromSeconds(3)); } catch { }

        events.OfType<RealtimeTranscriptEvent>().Where(e => !e.IsFinal)
            .Should().ContainSingle(e => e.Transcript == "hell");
        events.OfType<RealtimeTranscriptEvent>().Where(e => e.IsFinal)
            .Should().ContainSingle(e => e.Transcript == "hello world");

        await audioServer.StopAsync(CancellationToken.None);
    }

    [Fact]
    public async Task HandleSessionAsync_PublishesErrorEvent_OnOpenAiError()
    {
        await using var fakeOpenAi = new RealtimeFakeServer();
        fakeOpenAi.EventsToSend.Add("""{"type":"error","error":{"message":"invalid api key"}}""");
        fakeOpenAi.Start();

        var (session, audioServer, client) = await CreateAudioSessionAsync();
        var bridge = CreateBridge(fakeOpenAi);
        var events = new List<RealtimeEvent>();
        bridge.Events.Subscribe(e => events.Add(e));

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        var bridgeTask = bridge.HandleSessionAsync(session, cts.Token).AsTask();

        await Task.Delay(300);
        await cts.CancelAsync();
        await client.SendHangupAsync();
        try { await bridgeTask.WaitAsync(TimeSpan.FromSeconds(3)); } catch { }

        events.OfType<RealtimeErrorEvent>()
            .Should().ContainSingle(e => e.Message == "invalid api key");

        await audioServer.StopAsync(CancellationToken.None);
    }

    [Fact]
    public async Task HandleSessionAsync_CancellationToken_TerminatesBothLoops()
    {
        await using var fakeOpenAi = new RealtimeFakeServer();
        // No events — loops would hang without cancellation
        fakeOpenAi.Start();

        var (session, audioServer, client) = await CreateAudioSessionAsync();
        var bridge = CreateBridge(fakeOpenAi);

        using var cts = new CancellationTokenSource(TimeSpan.FromMilliseconds(200));
        var bridgeTask = bridge.HandleSessionAsync(session, cts.Token).AsTask();

        // Should complete within 3 seconds after ct is cancelled
        try { await bridgeTask.WaitAsync(TimeSpan.FromSeconds(3)); } catch { }
        bridgeTask.IsCompleted.Should().BeTrue("both loops must terminate on cancellation");

        await audioServer.StopAsync(CancellationToken.None);
    }

    [Fact]
    public async Task HandleSessionAsync_PublishesSpeechEvents()
    {
        await using var fakeOpenAi = new RealtimeFakeServer();
        fakeOpenAi.EventsToSend.Add("""{"type":"input_audio_buffer.speech_started"}""");
        fakeOpenAi.EventsToSend.Add("""{"type":"input_audio_buffer.speech_stopped"}""");
        fakeOpenAi.Start();

        var (session, audioServer, client) = await CreateAudioSessionAsync();
        var bridge = CreateBridge(fakeOpenAi);
        var events = new List<RealtimeEvent>();
        bridge.Events.Subscribe(e => events.Add(e));

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        var bridgeTask = bridge.HandleSessionAsync(session, cts.Token).AsTask();

        await Task.Delay(300);
        await cts.CancelAsync();
        await client.SendHangupAsync();
        try { await bridgeTask.WaitAsync(TimeSpan.FromSeconds(3)); } catch { }

        events.Should().ContainSingle(e => e is RealtimeSpeechStartedEvent);
        events.Should().ContainSingle(e => e is RealtimeSpeechStoppedEvent);

        await audioServer.StopAsync(CancellationToken.None);
    }
}
```

- [ ] **Step 4: Run bridge tests**

```bash
dotnet test Tests/Asterisk.Sdk.VoiceAi.OpenAiRealtime.Tests/ --filter "OpenAiRealtimeBridgeTests" -v minimal
```

Expected: All tests pass. If `AudioSocketClient.SendHangupAsync` doesn't exist by that name, check `AudioSocketClient.cs` for the actual API and adjust.

- [ ] **Step 5: Commit**

```bash
git add Tests/Asterisk.Sdk.VoiceAi.OpenAiRealtime.Tests/Internal/ \
        Tests/Asterisk.Sdk.VoiceAi.OpenAiRealtime.Tests/Bridge/ \
        src/Asterisk.Sdk.VoiceAi.OpenAiRealtime/OpenAiRealtimeBridge.cs
git commit -m "test(realtime): add RealtimeFakeServer and bridge integration tests"
```

---

## Task 10: Function Call Integration Tests

**Files:**
- Modify: `Tests/Asterisk.Sdk.VoiceAi.OpenAiRealtime.Tests/FunctionCalling/FunctionCallTests.cs`

Add integration tests that go through the full bridge → fake server → function handler → result-send cycle.

- [ ] **Step 1: Add bridge-level function call tests to `FunctionCallTests.cs`**

Replace the entire `FunctionCallTests.cs` file with this complete version that includes both the original unit tests (Task 5) and the new bridge integration tests:

```csharp
// Tests/Asterisk.Sdk.VoiceAi.OpenAiRealtime.Tests/FunctionCalling/FunctionCallTests.cs
using Asterisk.Sdk.Audio;
using Asterisk.Sdk.VoiceAi.AudioSocket;
using Asterisk.Sdk.VoiceAi.OpenAiRealtime;
using Asterisk.Sdk.VoiceAi.OpenAiRealtime.FunctionCalling;
using Asterisk.Sdk.VoiceAi.OpenAiRealtime.Tests.Internal;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace Asterisk.Sdk.VoiceAi.OpenAiRealtime.Tests.FunctionCalling;

public class FunctionCallTests
{
    // ── Shared test implementations ─────────────────────────────────────────

    private sealed class AddFunction : IRealtimeFunctionHandler
    {
        public string Name => "add";
        public string Description => "Adds two numbers";
        public string ParametersSchema => """{"type":"object","properties":{"a":{"type":"number"},"b":{"type":"number"}},"required":["a","b"]}""";
        public ValueTask<string> ExecuteAsync(string argumentsJson, CancellationToken ct = default)
            => ValueTask.FromResult("""{"result":42}""");
    }

    private sealed class MultiplyFunction : IRealtimeFunctionHandler
    {
        public string Name => "multiply";
        public string Description => "Multiplies two numbers";
        public string ParametersSchema => """{"type":"object","properties":{"x":{"type":"number"},"y":{"type":"number"}},"required":["x","y"]}""";
        public ValueTask<string> ExecuteAsync(string argumentsJson, CancellationToken ct = default)
            => ValueTask.FromResult("""{"result":100}""");
    }

    private sealed class ThrowingFunction : IRealtimeFunctionHandler
    {
        public string Name => "boom";
        public string Description => "Always throws";
        public string ParametersSchema => """{"type":"object","properties":{}}""";
        public ValueTask<string> ExecuteAsync(string argumentsJson, CancellationToken ct = default)
            => throw new InvalidOperationException("intentional failure");
    }

    // ── Registry unit tests (from Task 5) ───────────────────────────────────

    [Fact]
    public void Registry_TryGetHandler_ReturnsRegisteredHandler()
    {
        var registry = new RealtimeFunctionRegistry([new AddFunction()]);
        var found = registry.TryGetHandler("add", out var handler);

        found.Should().BeTrue();
        handler.Should().NotBeNull();
        handler!.Name.Should().Be("add");
    }

    [Fact]
    public void Registry_TryGetHandler_ReturnsFalseForUnknown()
    {
        var registry = new RealtimeFunctionRegistry([new AddFunction()]);
        var found = registry.TryGetHandler("unknown", out var handler);

        found.Should().BeFalse();
        handler.Should().BeNull();
    }

    [Fact]
    public void Registry_AllHandlers_ContainsRegisteredHandlers()
    {
        var handler = new AddFunction();
        var registry = new RealtimeFunctionRegistry([handler]);

        registry.AllHandlers.Should().ContainSingle()
            .Which.Name.Should().Be("add");
    }

    // ── Bridge integration tests (new in Task 10) ────────────────────────────

    private static async Task<(AudioSocketSession session, AudioSocketServer audioServer, AudioSocketClient client)>
        CreateAudioSessionAsync()
    {
        var audioServer = new AudioSocketServer(
            new AudioSocketOptions { Port = 0 },
            NullLogger<AudioSocketServer>.Instance);

        var tcs = new TaskCompletionSource<AudioSocketSession>();
        audioServer.OnSessionStarted += session =>
        {
            tcs.TrySetResult(session);
            return ValueTask.CompletedTask;
        };

        await audioServer.StartAsync(CancellationToken.None);

        var client = new AudioSocketClient("127.0.0.1", audioServer.BoundPort, Guid.NewGuid());
        await client.ConnectAsync(CancellationToken.None);

        var session = await tcs.Task.WaitAsync(TimeSpan.FromSeconds(2));
        return (session, audioServer, client);
    }

    private static OpenAiRealtimeBridge CreateBridge(
        RealtimeFakeServer fakeOpenAi,
        IEnumerable<IRealtimeFunctionHandler> handlers)
    {
        var options = Options.Create(new OpenAiRealtimeOptions
        {
            ApiKey = "test-key",
            Model = "gpt-4o-realtime-preview",
            Voice = "alloy",
            InputFormat = AudioFormat.Slin16Mono8kHz
        });
        var registry = new RealtimeFunctionRegistry(handlers);
        var bridge = new OpenAiRealtimeBridge(options, registry, NullLogger<OpenAiRealtimeBridge>.Instance);
        bridge.TestBaseUri = new Uri($"ws://localhost:{fakeOpenAi.Port}/");
        return bridge;
    }

    [Fact]
    public async Task Bridge_ExecutesFunction_AndSendsResultToServer()
    {
        await using var fakeOpenAi = new RealtimeFakeServer();
        fakeOpenAi.EventsToSend.Add(
            """{"type":"response.function_call_arguments.done","call_id":"call-1","name":"multiply","arguments":"{\"x\":10,\"y\":10}"}""");
        fakeOpenAi.Start();

        var (session, audioServer, client) = await CreateAudioSessionAsync();
        var bridge = CreateBridge(fakeOpenAi, [new MultiplyFunction()]);
        var events = new List<RealtimeEvent>();
        bridge.Events.Subscribe(e => events.Add(e));

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        var bridgeTask = bridge.HandleSessionAsync(session, cts.Token).AsTask();

        await Task.Delay(300);
        await cts.CancelAsync();
        await client.SendHangupAsync();
        try { await bridgeTask.WaitAsync(TimeSpan.FromSeconds(3)); } catch { }

        fakeOpenAi.ReceivedMessages
            .Should().Contain(m => m.Contains("\"type\":\"conversation.item.create\"") && m.Contains("\"result\":100"));
        fakeOpenAi.ReceivedMessages
            .Should().Contain(m => m.Contains("\"type\":\"response.create\""));
        events.OfType<RealtimeFunctionCalledEvent>()
            .Should().ContainSingle(e => e.FunctionName == "multiply");

        await audioServer.StopAsync(CancellationToken.None);
    }

    [Fact]
    public async Task Bridge_FunctionThrows_SendsErrorJsonToServer()
    {
        await using var fakeOpenAi = new RealtimeFakeServer();
        fakeOpenAi.EventsToSend.Add(
            """{"type":"response.function_call_arguments.done","call_id":"call-err","name":"boom","arguments":"{}"}""");
        fakeOpenAi.Start();

        var (session, audioServer, client) = await CreateAudioSessionAsync();
        var bridge = CreateBridge(fakeOpenAi, [new ThrowingFunction()]);

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        var bridgeTask = bridge.HandleSessionAsync(session, cts.Token).AsTask();

        await Task.Delay(300);
        await cts.CancelAsync();
        await client.SendHangupAsync();
        try { await bridgeTask.WaitAsync(TimeSpan.FromSeconds(3)); } catch { }

        // Result must contain error JSON — handler must not cause the bridge to throw
        fakeOpenAi.ReceivedMessages
            .Should().Contain(m => m.Contains("\"type\":\"conversation.item.create\"") && m.Contains("\"error\""));

        await audioServer.StopAsync(CancellationToken.None);
    }

    [Fact]
    public async Task Bridge_UnknownFunction_DoesNotCrash()
    {
        await using var fakeOpenAi = new RealtimeFakeServer();
        fakeOpenAi.EventsToSend.Add(
            """{"type":"response.function_call_arguments.done","call_id":"call-x","name":"nonexistent","arguments":"{}"}""");
        fakeOpenAi.Start();

        var (session, audioServer, client) = await CreateAudioSessionAsync();
        var bridge = CreateBridge(fakeOpenAi, []); // no handlers

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        var bridgeTask = bridge.HandleSessionAsync(session, cts.Token).AsTask();

        await Task.Delay(300);
        await cts.CancelAsync();
        await client.SendHangupAsync();

        // Should complete without throwing
        try { await bridgeTask.WaitAsync(TimeSpan.FromSeconds(3)); } catch (OperationCanceledException) { }

        await audioServer.StopAsync(CancellationToken.None);
    }
}
```

- [ ] **Step 2: Run function call tests**

```bash
dotnet test Tests/Asterisk.Sdk.VoiceAi.OpenAiRealtime.Tests/ --filter "FunctionCallTests" -v minimal
```

Expected: All tests (unit + integration) pass.

- [ ] **Step 3: Commit**

```bash
git add Tests/Asterisk.Sdk.VoiceAi.OpenAiRealtime.Tests/FunctionCalling/
git commit -m "test(realtime): add function call bridge integration tests"
```

---

## Task 11: DI Tests

**Files:**
- Create: `Tests/Asterisk.Sdk.VoiceAi.OpenAiRealtime.Tests/DependencyInjection/RealtimeDiTests.cs`

- [ ] **Step 1: Write the failing test**

```csharp
// Tests/Asterisk.Sdk.VoiceAi.OpenAiRealtime.Tests/DependencyInjection/RealtimeDiTests.cs
using Asterisk.Sdk.VoiceAi.AudioSocket;
using Asterisk.Sdk.VoiceAi.AudioSocket.DependencyInjection;  // AddAudioSocketServer
using Asterisk.Sdk.VoiceAi.OpenAiRealtime;
using Asterisk.Sdk.VoiceAi.OpenAiRealtime.DependencyInjection;
using Asterisk.Sdk.VoiceAi.Pipeline;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

namespace Asterisk.Sdk.VoiceAi.OpenAiRealtime.Tests.DependencyInjection;

public class RealtimeDiTests
{
    private static IServiceProvider BuildProvider(Action<IServiceCollection>? extra = null)
    {
        var services = new ServiceCollection();
        services.AddLogging();

        // AudioSocketServer is a prerequisite for AddOpenAiRealtimeBridge.
        // Port = 0 means OS assigns a port; we never call StartAsync in DI tests.
        services.AddAudioSocketServer(o => o.Port = 0);

        services.AddOpenAiRealtimeBridge(o =>
        {
            o.ApiKey = "test-key";
            o.Model  = "gpt-4o-realtime-preview";
        });

        extra?.Invoke(services);
        return services.BuildServiceProvider();
    }

    [Fact]
    public void ISessionHandler_ResolvesAs_OpenAiRealtimeBridge()
    {
        using var sp = BuildProvider();
        var handler = sp.GetRequiredService<ISessionHandler>();
        handler.Should().BeOfType<OpenAiRealtimeBridge>();
    }

    [Fact]
    public void OpenAiRealtimeBridge_IsSingleton()
    {
        using var sp = BuildProvider();
        var a = sp.GetRequiredService<OpenAiRealtimeBridge>();
        var b = sp.GetRequiredService<OpenAiRealtimeBridge>();
        a.Should().BeSameAs(b);
    }

    [Fact]
    public void VoiceAiSessionBroker_IsRegisteredAsHostedService()
    {
        using var sp = BuildProvider();
        var hostedServices = sp.GetServices<IHostedService>();
        hostedServices.Should().ContainSingle(s => s is VoiceAiSessionBroker);
    }

    [Fact]
    public void AddFunction_RegistersMultipleHandlers()
    {
        using var sp = BuildProvider(s =>
            s.AddFunction<TestFunctionA>()
             .AddFunction<TestFunctionB>());

        var handlers = sp.GetServices<IRealtimeFunctionHandler>().ToList();
        handlers.Should().HaveCount(2);
        handlers.Select(h => h.Name).Should().BeEquivalentTo(["func-a", "func-b"]);
    }

    private sealed class TestFunctionA : IRealtimeFunctionHandler
    {
        public string Name => "func-a";
        public string Description => "A";
        public string ParametersSchema => """{"type":"object","properties":{}}""";
        public ValueTask<string> ExecuteAsync(string argumentsJson, CancellationToken ct = default)
            => ValueTask.FromResult("{}");
    }

    private sealed class TestFunctionB : IRealtimeFunctionHandler
    {
        public string Name => "func-b";
        public string Description => "B";
        public string ParametersSchema => """{"type":"object","properties":{}}""";
        public ValueTask<string> ExecuteAsync(string argumentsJson, CancellationToken ct = default)
            => ValueTask.FromResult("{}");
    }
}
```

**Note:** `AddAudioSocketServer` is from `Asterisk.Sdk.VoiceAi.AudioSocket.DependencyInjection` — the test project `.csproj` already references this in Task 3 (via the `AudioSocket` project reference).

- [ ] **Step 2: Run DI tests**

```bash
dotnet test Tests/Asterisk.Sdk.VoiceAi.OpenAiRealtime.Tests/ --filter "RealtimeDiTests" -v minimal
```

Expected: All 4 tests pass.

- [ ] **Step 3: Commit**

```bash
git add Tests/Asterisk.Sdk.VoiceAi.OpenAiRealtime.Tests/DependencyInjection/
git commit -m "test(realtime): add DI registration tests"
```

---

## Task 12: Demo — `OpenAiRealtimeExample`

**Files:**
- Create: `Examples/OpenAiRealtimeExample/OpenAiRealtimeExample.csproj`
- Create: `Examples/OpenAiRealtimeExample/GetCurrentTimeFunction.cs`
- Create: `Examples/OpenAiRealtimeExample/Program.cs`
- Create: `Examples/OpenAiRealtimeExample/appsettings.json`

- [ ] **Step 1: Create project file**

```xml
<!-- Examples/OpenAiRealtimeExample/OpenAiRealtimeExample.csproj -->
<Project Sdk="Microsoft.NET.Sdk">
  <ItemGroup>
    <ProjectReference Include="..\..\src\Asterisk.Sdk.VoiceAi.OpenAiRealtime\Asterisk.Sdk.VoiceAi.OpenAiRealtime.csproj" />
    <ProjectReference Include="..\..\src\Asterisk.Sdk.VoiceAi.AudioSocket\Asterisk.Sdk.VoiceAi.AudioSocket.csproj" />
  </ItemGroup>
  <ItemGroup>
    <PackageReference Include="Microsoft.Extensions.Hosting" />
    <PackageReference Include="Microsoft.Extensions.Logging.Console" />
    <PackageReference Include="Microsoft.Extensions.Configuration" />
  </ItemGroup>
  <ItemGroup>
    <Content Include="appsettings.json">
      <CopyToOutputDirectory>Always</CopyToOutputDirectory>
    </Content>
  </ItemGroup>
</Project>
```

- [ ] **Step 2: Create `GetCurrentTimeFunction.cs`**

```csharp
// Examples/OpenAiRealtimeExample/GetCurrentTimeFunction.cs
using Asterisk.Sdk.VoiceAi.OpenAiRealtime.FunctionCalling;

namespace OpenAiRealtimeExample;

/// <summary>
/// Example function tool: returns the current UTC time as JSON.
/// A real function would query a database, call an API, look up caller info, etc.
/// </summary>
public sealed class GetCurrentTimeFunction : IRealtimeFunctionHandler
{
    public string Name => "get_current_time";
    public string Description => "Returns the current UTC date and time.";
    public string ParametersSchema => """{"type":"object","properties":{},"required":[]}""";

    public ValueTask<string> ExecuteAsync(string argumentsJson, CancellationToken ct = default)
    {
        var now = DateTimeOffset.UtcNow;
        return ValueTask.FromResult($"{{\"utc\":\"{now:O}\",\"readable\":\"{now:yyyy-MM-dd HH:mm:ss} UTC\"}}");
    }
}
```

- [ ] **Step 3: Create `Program.cs`**

```csharp
// Examples/OpenAiRealtimeExample/Program.cs
using Asterisk.Sdk.VoiceAi.OpenAiRealtime;
using Asterisk.Sdk.VoiceAi.OpenAiRealtime.DependencyInjection;
using Asterisk.Sdk.VoiceAi.AudioSocket.DependencyInjection;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using OpenAiRealtimeExample;
using System.Reactive.Linq;

var host = Host.CreateDefaultBuilder(args)
    .ConfigureServices((ctx, services) =>
    {
        // Step 1: Start the AudioSocket server (Asterisk dials in here)
        services.AddAudioSocketServer(o => o.Port = 9092);

        // Step 2: Connect to OpenAI Realtime API
        services.AddOpenAiRealtimeBridge(o =>
        {
            o.ApiKey       = ctx.Configuration["OpenAI:ApiKey"]
                ?? throw new InvalidOperationException("OpenAI:ApiKey is required. Set it in appsettings.json or environment variables.");
            o.Model        = "gpt-4o-realtime-preview";
            o.Voice        = "alloy";
            o.Instructions = "You are a friendly contact center assistant. Always respond in English. Be concise.";
        })
        .AddFunction<GetCurrentTimeFunction>();
    })
    .Build();

// Subscribe to events to see what's happening in real time
var bridge = host.Services.GetRequiredService<OpenAiRealtimeBridge>();

bridge.Events
    .OfType<RealtimeTranscriptEvent>()
    .Where(e => e.IsFinal)
    .Subscribe(e => Console.WriteLine($"[{e.ChannelId:D}] User said: {e.Transcript}"));

bridge.Events
    .OfType<RealtimeResponseStartedEvent>()
    .Subscribe(e => Console.WriteLine($"[{e.ChannelId:D}] AI responding..."));

bridge.Events
    .OfType<RealtimeFunctionCalledEvent>()
    .Subscribe(e => Console.WriteLine($"[{e.ChannelId:D}] Tool '{e.FunctionName}' called → {e.ResultJson}"));

bridge.Events
    .OfType<RealtimeErrorEvent>()
    .Subscribe(e => Console.Error.WriteLine($"[{e.ChannelId:D}] ERROR: {e.Message}"));

Console.WriteLine("OpenAI Realtime bridge listening on AudioSocket port 9092.");
Console.WriteLine("Dial your Asterisk number to start a conversation with GPT-4o.");
Console.WriteLine("Press Ctrl+C to stop.");

await host.RunAsync();
```

- [ ] **Step 4: Create `appsettings.json`**

```json
{
  "OpenAI": {
    "ApiKey": ""
  },
  "Logging": {
    "LogLevel": {
      "Default": "Information",
      "Microsoft.Hosting.Lifetime": "Information"
    }
  }
}
```

- [ ] **Step 5: Build demo**

```bash
dotnet build Examples/OpenAiRealtimeExample/OpenAiRealtimeExample.csproj
```

Expected: Build succeeded, 0 warnings.

- [ ] **Step 6: Commit**

```bash
git add Examples/OpenAiRealtimeExample/
git commit -m "feat(realtime): add OpenAiRealtimeExample demo"
```

---

## Task 13: Final Verification + Solution Build

- [ ] **Step 1: Run all tests**

```bash
dotnet test Asterisk.Sdk.slnx -v minimal
```

Expected: All tests pass (existing + ~18 new), 0 failures.

- [ ] **Step 2: Build full solution with 0 warnings**

```bash
dotnet build Asterisk.Sdk.slnx
```

Expected: Build succeeded, 0 errors, 0 warnings.

- [ ] **Step 3: Verify test count**

```bash
dotnet test Asterisk.Sdk.slnx --list-tests 2>/dev/null | grep -c "Asterisk.Sdk.VoiceAi.OpenAiRealtime"
```

Expected: ~18 tests in the new package.

- [ ] **Step 4: Final commit (if any cleanup needed)**

```bash
git add -A
git commit -m "feat(realtime): Sprint 24 complete — OpenAI Realtime bridge"
```

---

## Quick Reference: Event Type → Bridge Action

| OpenAI Event | Bridge Action |
|---|---|
| `session.created` | Log |
| `response.created` | Capture `responseStartTime` + publish `RealtimeResponseStartedEvent` |
| `response.audio.delta` | Base64 decode → resample 24k→inputRate → `AudioSocket.WriteAudioAsync` |
| `response.audio.done` | (ignored) |
| `response.audio_transcript.delta` | Publish `RealtimeTranscriptEvent(IsFinal=false)` |
| `response.audio_transcript.done` | Publish `RealtimeTranscriptEvent(IsFinal=true)` |
| `response.done` | Publish `RealtimeResponseEndedEvent(Duration = now - startTime)` |
| `response.cancelled` | Log + publish `RealtimeResponseEndedEvent` (if `startTime != default`) |
| `response.function_call_arguments.done` | Dispatch to `IRealtimeFunctionHandler` → send result → publish `RealtimeFunctionCalledEvent` |
| `input_audio_buffer.speech_started` | Publish `RealtimeSpeechStartedEvent` |
| `input_audio_buffer.speech_stopped` | Publish `RealtimeSpeechStoppedEvent` |
| `error` | Publish `RealtimeErrorEvent` + log |
| all others | Ignore |

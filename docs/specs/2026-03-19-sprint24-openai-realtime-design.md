# Sprint 24: Asterisk.Sdk.VoiceAi.OpenAiRealtime

**Fecha:** 2026-03-19
**Estado:** Aprobado
**Objetivo:** OpenAI Realtime API bridge — conecta Asterisk AudioSocket directamente a GPT-4o en tiempo real, con soporte de function calling y observabilidad. Demo E2E incluido.

---

## Contexto

Sprint 23 entregó el pipeline turn-based: AudioSocket → VAD local → STT (Deepgram/Whisper/Google/Azure) → IConversationHandler → TTS (ElevenLabs/Azure). Sprint 24 agrega un segundo modo de operación: un bridge directo al OpenAI Realtime API que reemplaza toda la cadena STT+LLM+TTS con un solo WebSocket persistente de ultra-baja latencia.

**Diferencia fundamental de paradigmas:**

| Aspecto | VoiceAiPipeline (Sprint 23) | OpenAiRealtimeBridge (Sprint 24) |
|---|---|---|
| VAD | Cliente — AudioProcessor local | Servidor — OpenAI detecta automáticamente |
| Flujo | Turn-based: utterance → STT → handler → TTS | Streaming continuo: audio raw in/out permanente |
| Latencia | 3 round-trips API | 1 WebSocket, respuesta comienza mientras el usuario habla |
| Estado conversación | Local (`List<ConversationTurn>`) | Server-side (context window OpenAI) |
| Interrupciones | Barge-in manual con CancellationTokenSource | Automático — OpenAI para al detectar habla |
| Formato audio | PCM16 frames raw | Base64 PCM16 24kHz en eventos JSON |

---

## Decisiones de Diseño

### ISessionHandler — extensión mínima a VoiceAi existente

Se extrae `ISessionHandler` de `Asterisk.Sdk.VoiceAi` con un único método:

```csharp
public interface ISessionHandler
{
    ValueTask HandleSessionAsync(AudioSocketSession session, CancellationToken ct = default);
}
```

`VoiceAiPipeline` implementa `ISessionHandler`. `VoiceAiSessionBroker` inyecta `ISessionHandler`. `AddVoiceAiPipeline<T>()` registra `VoiceAiPipeline` como `ISessionHandler`. `AddOpenAiRealtimeBridge()` registra `OpenAiRealtimeBridge` como `ISessionHandler` (`services.TryAddSingleton<ISessionHandler>(sp => sp.GetRequiredService<OpenAiRealtimeBridge>())`). Ambos métodos registran `VoiceAiSessionBroker` como `IHostedService`. **Zero breaking changes** — ~17 líneas modificadas en total.

### Modelo de concurrencia — N sesiones simultáneas, estado por sesión

`OpenAiRealtimeBridge` es **singleton**. Cada llamada a `HandleSessionAsync` crea estado completamente aislado en variables locales:
- `ClientWebSocket ws = new()` — conexión independiente a OpenAI por sesión
- `SemaphoreSlim wsWriteLock = new(1, 1)` — serializa envíos de InputLoop y OutputLoop para esta sesión
- `PolyphaseResampler? upsampler` y `PolyphaseResampler? downsampler` — delay lines por sesión

El singleton no tiene campos mutables por sesión. Soporta **N sesiones simultáneas** sin interferencia. La instancia de `ClientWebSocket`, el lock y los resamplers viven dentro del stack de `HandleSessionAsync` — se descartan automáticamente al terminar la sesión.

Todos los eventos publicados en `IObservable<RealtimeEvent>` incluyen `Guid ChannelId` para que los consumidores puedan filtrar por sesión cuando hay concurrencia.

### Resampling — por sesión, bypass si no necesario

`ResamplerFactory.Create(inputRate, 24000)` y `ResamplerFactory.Create(24000, inputRate)` se crean dentro de `HandleSessionAsync`. Si `InputFormat.SampleRate == 24000`, no se crean resamplers (bypass directo). El `inputRate` proviene de `options.InputFormat.SampleRate`.

### WebSocket write serialization — ambos loops comparten el lock

Tanto InputLoop (que envía `input_audio_buffer.append` frames) como OutputLoop (que envía resultados de function calls) deben adquirir `wsWriteLock` antes de llamar `ws.SendTextAsync`. `ClientWebSocket.SendAsync` no es thread-safe para envíos concurrentes. Este patrón es idéntico al `_writeLock` de `AmiConnection`.

### Sin SDKs de terceros — ClientWebSocket + Utf8JsonWriter + source-generated JSON

El protocolo se implementa directamente. La mayoría de mensajes salientes se serializa con `RealtimeJsonContext`. **Excepción:** `session.update` se construye manualmente con `Utf8JsonWriter` porque el array `tools` contiene `parameters` como raw JSON literal (`ToolConfig.ParametersSchema`). `JsonSerializer` source-generated no soporta `WriteRawValue` dentro de tipos anotados. Por tanto, `BuildSessionUpdate()` escribe el JSON completo del mensaje vía `Utf8JsonWriter` directo a un `ArrayBufferWriter<byte>` — no usa `JsonSerializer.Serialize` para el top-level. `ToolConfig` se elimina de `RealtimeJsonContext` (no necesita el contexto).

Los mensajes inbound siempre se deserializan con `JsonSerializer.Deserialize` + `RealtimeJsonContext`.

### Options validation

`[OptionsValidator]` source generator. `[Required]` en `ApiKey` y `Model`.

### Function calling — handlers como singleton, RealtimeFunctionRegistry

`AddFunction<THandler>()` llama `services.AddSingleton<IRealtimeFunctionHandler, THandler>()` (**sin `Try`** — permite encadenar múltiples handlers). Los handlers **deben ser singleton o transient** — nunca scoped.

`RealtimeFunctionRegistry` se inyecta en `OpenAiRealtimeBridge`. Su contrato:
```csharp
internal sealed class RealtimeFunctionRegistry
{
    public RealtimeFunctionRegistry(IEnumerable<IRealtimeFunctionHandler> handlers);
    public IReadOnlyCollection<IRealtimeFunctionHandler> AllHandlers { get; }  // para BuildSessionUpdate
    public bool TryGetHandler(string name, out IRealtimeFunctionHandler? handler);
}
```
Se registra como `services.AddSingleton<RealtimeFunctionRegistry>()` dentro de `AddOpenAiRealtimeBridge()`. El dispatch ocurre en OutputLoop en `response.function_call_arguments.done`. Los envíos de resultado usan `wsWriteLock`.

### Observabilidad — stream compartido con ChannelId

`IObservable<RealtimeEvent>` expone todos los eventos de todas las sesiones activas. Cada evento incluye `ChannelId` del `AudioSocketSession` para filtrado. Mismo patrón que `VoiceAiPipeline.Events`.

### Eventos de transcripción — nomenclatura correcta de la API

En modo audio (conversación), los eventos de transcripción son:
- `response.audio_transcript.delta` → `RealtimeTranscriptEvent(IsFinal=false)`
- `response.audio_transcript.done` → `RealtimeTranscriptEvent(IsFinal=true)`

Mapping correcto para start/end de response:
- `response.created` → `RealtimeResponseStartedEvent`
- `response.done` → `RealtimeResponseEndedEvent`

---

## Estructura de Archivos

### Cambios a `Asterisk.Sdk.VoiceAi` (mínimos)

```
src/Asterisk.Sdk.VoiceAi/
├── ISessionHandler.cs                              ← NUEVO
├── Pipeline/
│   ├── VoiceAiPipeline.cs                         ← : ISessionHandler (1 línea)
│   └── VoiceAiSessionBroker.cs                    ← inyecta ISessionHandler
└── DependencyInjection/
    └── VoiceAiServiceCollectionExtensions.cs       ← registra VoiceAiPipeline como ISessionHandler
```

### Nuevo paquete `Asterisk.Sdk.VoiceAi.OpenAiRealtime`

```
src/Asterisk.Sdk.VoiceAi.OpenAiRealtime/
├── OpenAiRealtimeBridge.cs
├── OpenAiRealtimeOptions.cs
├── OpenAiRealtimeOptionsValidator.cs
├── VadMode.cs
├── RealtimeEvents.cs
├── FunctionCalling/
│   ├── IRealtimeFunctionHandler.cs
│   └── RealtimeFunctionRegistry.cs
├── Internal/
│   ├── RealtimeProtocol.cs                         ← constantes de nombres de eventos (strings)
│   ├── RealtimeMessages.cs                         ← DTOs de mensajes (inbound + outbound)
│   ├── RealtimeJsonContext.cs                      ← [JsonSerializable] source gen
│   └── RealtimeLog.cs                              ← [LoggerMessage] source gen
└── DependencyInjection/
    └── RealtimeServiceCollectionExtensions.cs
```

### Tests

```
Tests/Asterisk.Sdk.VoiceAi.OpenAiRealtime.Tests/
├── Bridge/
│   └── OpenAiRealtimeBridgeTests.cs
├── FunctionCalling/
│   └── FunctionCallTests.cs
└── Internal/
    └── RealtimeFakeServer.cs
```

### Demo

```
Examples/OpenAiRealtimeExample/
└── Program.cs
```

---

## Contratos Públicos

### `ISessionHandler`

```csharp
namespace Asterisk.Sdk.VoiceAi;

public interface ISessionHandler
{
    ValueTask HandleSessionAsync(AudioSocketSession session, CancellationToken ct = default);
}
```

### `OpenAiRealtimeOptions`

```csharp
[OptionsValidator]
public sealed partial class OpenAiRealtimeOptionsValidator : IValidateOptions<OpenAiRealtimeOptions> { }

public sealed class OpenAiRealtimeOptions
{
    [Required] public string ApiKey { get; set; } = string.Empty;
    [Required] public string Model { get; set; } = "gpt-4o-realtime-preview";
    public string Voice { get; set; } = "alloy";
    public string Instructions { get; set; } = string.Empty;
    public VadMode VadMode { get; set; } = VadMode.ServerSide;
    /// <summary>
    /// Audio format del stream AudioSocket de Asterisk.
    /// SampleRate se usa para determinar el ratio de resampling (→ 24000).
    /// Si SampleRate ya es 24000, no se aplica resampling.
    /// </summary>
    public AudioFormat InputFormat { get; set; } = AudioFormat.Slin16Mono8kHz;
}

public enum VadMode { ServerSide, Disabled }
```

### `IRealtimeFunctionHandler`

```csharp
public interface IRealtimeFunctionHandler
{
    string Name { get; }
    string Description { get; }
    string ParametersSchema { get; }  // JSON Schema literal — se inserta raw en session.update
    ValueTask<string> ExecuteAsync(string argumentsJson, CancellationToken ct = default);
}
```

### Eventos de observabilidad — todos incluyen `ChannelId`

```csharp
public abstract record RealtimeEvent(Guid ChannelId, DateTimeOffset Timestamp);

public sealed record RealtimeSpeechStartedEvent(Guid ChannelId, DateTimeOffset Timestamp)
    : RealtimeEvent(ChannelId, Timestamp);

public sealed record RealtimeSpeechStoppedEvent(Guid ChannelId, DateTimeOffset Timestamp)
    : RealtimeEvent(ChannelId, Timestamp);

public sealed record RealtimeTranscriptEvent(Guid ChannelId, DateTimeOffset Timestamp, string Transcript, bool IsFinal)
    : RealtimeEvent(ChannelId, Timestamp);

public sealed record RealtimeResponseStartedEvent(Guid ChannelId, DateTimeOffset Timestamp)
    : RealtimeEvent(ChannelId, Timestamp);

public sealed record RealtimeResponseEndedEvent(Guid ChannelId, DateTimeOffset Timestamp, TimeSpan Duration)
    : RealtimeEvent(ChannelId, Timestamp);
// Duration = DateTimeOffset.UtcNow (response.done local time) - startTime (captured at response.created local time)

public sealed record RealtimeFunctionCalledEvent(Guid ChannelId, DateTimeOffset Timestamp, string FunctionName, string ArgumentsJson, string ResultJson)
    : RealtimeEvent(ChannelId, Timestamp);

public sealed record RealtimeErrorEvent(Guid ChannelId, DateTimeOffset Timestamp, string Message)
    : RealtimeEvent(ChannelId, Timestamp);
```

### `OpenAiRealtimeBridge`

```csharp
public sealed class OpenAiRealtimeBridge : ISessionHandler, IAsyncDisposable
{
    // Instance fields (shared, immutable per bridge lifetime):
    //   IOptions<OpenAiRealtimeOptions> _options
    //   RealtimeFunctionRegistry _registry
    //   ILogger<OpenAiRealtimeBridge> _logger
    //   Subject<RealtimeEvent> _events  ← backing field for Events

    // Per-session state created as LOCAL VARIABLES inside HandleSessionAsync:
    //   ClientWebSocket ws
    //   SemaphoreSlim wsWriteLock
    //   PolyphaseResampler? upsampler / downsampler

    public IObservable<RealtimeEvent> Events { get; }  // merged stream from all active sessions

    public ValueTask HandleSessionAsync(AudioSocketSession session, CancellationToken ct = default);

    // DisposeAsync: _events.OnCompleted(); _events.Dispose() — same as VoiceAiPipeline
    public ValueTask DisposeAsync();
}
```

### DI

```csharp
// AddOpenAiRealtimeBridge() registra:
//   OpenAiRealtimeBridge (singleton)
//   ISessionHandler → OpenAiRealtimeBridge (singleton factory)
//   VoiceAiSessionBroker (singleton) + IHostedService<VoiceAiSessionBroker>
//   OpenAiRealtimeOptions + OpenAiRealtimeOptionsValidator
// Prerequisito: services.AddAudioSocketServer() debe llamarse antes

services.AddOpenAiRealtimeBridge(o =>
{
    o.ApiKey       = "sk-...";
    o.Model        = "gpt-4o-realtime-preview";
    o.Voice        = "alloy";
    o.Instructions = "Eres un asistente de contact center. Responde en español.";
});

// Con function calling — AddFunction<T> registra como singleton:
services.AddOpenAiRealtimeBridge(o => { ... })
        .AddFunction<GetCurrentTimeFunction>()
        .AddFunction<BuscarClienteFunction>();

// AddFunction<THandler> internamente hace:
// services.AddSingleton<IRealtimeFunctionHandler, THandler>()   ← sin Try (permite múltiples handlers)
// RESTRICCIÓN: IRealtimeFunctionHandler debe ser singleton o transient, nunca scoped.
```

---

## DTOs internos — `RealtimeMessages.cs` + `RealtimeJsonContext.cs`

Todos los DTOs son internos (`internal sealed class`).

### Outbound (Cliente → OpenAI)

**`session.update`** se construye con `Utf8JsonWriter` directo sobre `ArrayBufferWriter<byte>` (NO vía `JsonSerializer`) para poder insertar `parameters` como raw JSON sin reflexión:

```
BuildSessionUpdate(IEnumerable<IRealtimeFunctionHandler> tools, OpenAiRealtimeOptions opts):
  writer.WriteStartObject()
    writer.WriteString("type", "session.update")
    writer.WritePropertyName("session")
    writer.WriteStartObject()
      writer.WriteString("voice", opts.Voice)
      writer.WriteStartArray("modalities") → "audio", "text"
      writer.WriteString("instructions", opts.Instructions)
      if VadMode.ServerSide:
        writer.WritePropertyName("turn_detection")
        writer.WriteStartObject() → writer.WriteString("type","server_vad") writer.WriteEndObject()
      writer.WriteStartArray("tools")
        foreach handler:
          writer.WriteStartObject()
            writer.WriteString("type", "function")
            writer.WriteString("name", handler.Name)
            writer.WriteString("description", handler.Description)
            writer.WritePropertyName("parameters")
            writer.WriteRawValue(handler.ParametersSchema, skipInputValidation: false)
          writer.WriteEndObject()
      writer.WriteEndArray()
    writer.WriteEndObject()
  writer.WriteEndObject()
→ ReadOnlyMemory<byte> para pasar a ws.SendAsync (TextMessage)
```

**Demás mensajes salientes** via `JsonSerializer` + `RealtimeJsonContext`:
```csharp
internal sealed class InputAudioBufferAppendRequest { public string Type => "input_audio_buffer.append"; public string Audio { get; set; } = ""; }
internal sealed class InputAudioBufferCommitRequest { public string Type => "input_audio_buffer.commit"; }
internal sealed class ConversationItemCreateRequest { public string Type => "conversation.item.create"; public ConversationItem Item { get; set; } = default!; }
internal sealed class ConversationItem { public string Type { get; set; } = ""; public string? CallId { get; set; } public string? Output { get; set; } }
internal sealed class ResponseCreateRequest { public string Type => "response.create"; }
```

### Inbound (OpenAI → Cliente) — solo los campos necesarios

```csharp
internal sealed class ServerEventBase { public string Type { get; set; } = ""; }
internal sealed class ResponseAudioDeltaEvent { public string Type { get; set; } = ""; public string Delta { get; set; } = ""; }
internal sealed class ResponseAudioTranscriptDeltaEvent { public string Type { get; set; } = ""; public string Delta { get; set; } = ""; }
internal sealed class ResponseAudioTranscriptDoneEvent { public string Type { get; set; } = ""; public string Transcript { get; set; } = ""; }
internal sealed class FunctionCallArgumentsDoneEvent { public string Type { get; set; } = ""; public string CallId { get; set; } = ""; public string Name { get; set; } = ""; public string Arguments { get; set; } = ""; }
internal sealed class ServerErrorEvent { public string Type { get; set; } = ""; public ServerError? Error { get; set; } }
internal sealed class ServerError { public string Message { get; set; } = ""; }
```

### `RealtimeJsonContext`

`session.update` se construye con `Utf8JsonWriter` — sus tipos NO necesitan `[JsonSerializable]`. Solo los DTOs que pasan por `JsonSerializer` se anotan:

```csharp
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

**Deserialización inbound:** OutputLoop recibe el mensaje como `string`, deserializa `ServerEventBase` para leer `Type`, luego deserializa al DTO específico según el switch.

---

## Protocolo OpenAI Realtime — Eventos Relevantes

### Cliente → OpenAI

| Evento | Cuándo | Usa wsWriteLock |
|---|---|---|
| `session.update` | Al conectar — voz, instructions, VAD, tools | ✅ |
| `input_audio_buffer.append` | Cada frame de audio de Asterisk (base64 PCM16 24kHz) | ✅ |
| `input_audio_buffer.commit` | Solo VadMode.Disabled — señal fin de turno | ✅ |
| `conversation.item.create` | Resultado de un function call | ✅ |
| `response.create` | Después de enviar resultado de function call | ✅ |

### OpenAI → Cliente

| Evento | Acción del bridge |
|---|---|
| `response.audio.delta` | Decode base64 → resample 24k→inputRate → AudioSocket.WriteAudioAsync |
| `response.audio.done` | Log |
| `response.audio_transcript.delta` | Publish RealtimeTranscriptEvent(IsFinal=false) |
| `response.audio_transcript.done` | Publish RealtimeTranscriptEvent(IsFinal=true) |
| `response.created` | Capture startTime (DateTimeOffset.UtcNow) + publish RealtimeResponseStartedEvent |
| `response.done` | Publish RealtimeResponseEndedEvent(Duration = UtcNow - startTime) |
| `response.function_call_arguments.done` | Dispatch a IRealtimeFunctionHandler |
| `input_audio_buffer.speech_started` | Publish RealtimeSpeechStartedEvent |
| `input_audio_buffer.speech_stopped` | Publish RealtimeSpeechStoppedEvent |
| `response.cancelled` | Publish RealtimeResponseEndedEvent(Duration = UtcNow - startTime, si startTime != default) + log (barge-in automático) |
| `session.created` | Log |
| `error` | Publish RealtimeErrorEvent + log |
| otros | Ignorar |

---

## Ciclo de vida de WebSocket (por sesión, variables locales)

```csharp
// Dentro de HandleSessionAsync(AudioSocketSession session, CancellationToken ct):

using var ws = new ClientWebSocket();
ws.Options.SetRequestHeader("Authorization", $"Bearer {_options.ApiKey}");
ws.Options.SetRequestHeader("OpenAI-Beta", "realtime=v1");
await ws.ConnectAsync(uri, ct);  // wss://api.openai.com/v1/realtime?model={Model}

using var wsWriteLock = new SemaphoreSlim(1, 1);

var inputRate = _options.InputFormat.SampleRate;
var upsampler   = inputRate != 24000 ? ResamplerFactory.Create(inputRate, 24000) : null;
var downsampler = inputRate != 24000 ? ResamplerFactory.Create(24000, inputRate) : null;

// Enviar session.update (voz, instructions, VAD, tools)
await wsWriteLock.WaitAsync(ct);
await ws.SendTextAsync(BuildSessionUpdate(), ct);
wsWriteLock.Release();

DateTimeOffset responseStartTime = default;

try
{
    await Task.WhenAll(
        InputLoop(session, ws, wsWriteLock, upsampler, ct),
        OutputLoop(session, ws, wsWriteLock, downsampler, ref responseStartTime, ct)
    );
}
finally
{
    // Cierre limpio — sin cancelar ct para poder enviar el close
    await wsWriteLock.WaitAsync(CancellationToken.None);
    try { await ws.CloseOutputAsync(WebSocketCloseStatus.NormalClosure, "", CancellationToken.None); }
    catch { /* ignore */ }
    finally { wsWriteLock.Release(); }
}
```

---

## Flujo de Function Calling

```
OutputLoop recibe "response.function_call_arguments.done"
    → { call_id, name, arguments }
      ↓
_registry.TryGetHandler(name, out var handler)  → false: log warning, skip
      ↓
handler.ExecuteAsync(arguments, ct) → resultJson
  (si lanza excepción → resultJson = {"error":"<message>"})
      ↓
await wsWriteLock.WaitAsync(ct)
  await ws.SendTextAsync(BuildConversationItemCreate(call_id, resultJson), ct)
  await ws.SendTextAsync(BuildResponseCreate(), ct)
  wsWriteLock.Release()
      ↓
Publish RealtimeFunctionCalledEvent(channelId, ...)
      ↓
OpenAI continúa la conversación
```

---

## Demo — `OpenAiRealtimeExample`

```csharp
var host = Host.CreateDefaultBuilder(args)
    .ConfigureServices((ctx, services) =>
    {
        services.AddAudioSocketServer(o => o.Port = 9092);

        services.AddOpenAiRealtimeBridge(o =>
        {
            o.ApiKey       = ctx.Configuration["OpenAI:ApiKey"]!;
            o.Model        = "gpt-4o-realtime-preview";
            o.Voice        = "alloy";
            o.Instructions = "Eres un asistente de contact center amable. Responde siempre en español.";
        })
        .AddFunction<GetCurrentTimeFunction>();
    })
    .Build();

var bridge = host.Services.GetRequiredService<OpenAiRealtimeBridge>();
bridge.Events
    .OfType<RealtimeTranscriptEvent>()
    .Where(e => e.IsFinal)
    .Subscribe(e => Console.WriteLine($"[{e.ChannelId:D}] {e.Transcript}"));

await host.RunAsync();
```

`GetCurrentTimeFunction` retorna la hora actual en JSON — ejemplo canónico de tool use sin dependencias externas.

---

## Tests — Cobertura objetivo (~18)

| Área | Tests |
|---|---|
| InputLoop envía audio al fake server | 2 |
| OutputLoop recibe audio y escribe a AudioSocket | 2 |
| Resampling 8k→24k→8k roundtrip (fidelidad) | 2 |
| session.update JSON — voice, instructions, tools correctos | 2 |
| Function call — éxito, resultado enviado | 1 |
| Function call — handler lanza excepción → error JSON enviado | 1 |
| Function call — tool desconocido → log warning, no crash | 1 |
| Function call — wsWriteLock serializa con InputLoop en curso | 1 |
| Hangup desde Asterisk → CloseOutputAsync llamado | 1 |
| ct cancelado → ambos loops terminan, no hang | 1 |
| WebSocket ConnectAsync falla → excepción propagada correctamente | 1 |
| session.update recibe `error` de OpenAI → RealtimeErrorEvent publicado | 1 |
| DI — ISessionHandler resuelve como OpenAiRealtimeBridge | 1 |
| DI — VoiceAiSessionBroker registrado como IHostedService | 1 |
| **Total** | **~18** |

`RealtimeFakeServer` — WebSocket in-process que simula el protocolo Realtime. Envía `session.created` al conectar, acepta `session.update`, devuelve audio delta + transcripts + function call arguments según el test. Patrón idéntico a `ElevenLabsFakeServer` de Sprint 23.

---

## Métricas objetivo

- ~18 tests nuevos, 0 warnings, AOT-compatible (`IsAotCompatible=true`)
- 1 paquete NuGet nuevo: `Asterisk.Sdk.VoiceAi.OpenAiRealtime`
- 1 demo funcional: `OpenAiRealtimeExample`
- Cambio a paquete existente `Asterisk.Sdk.VoiceAi`: ~17 líneas, zero breaking changes
- N sesiones simultáneas soportadas (estado completamente aislado por sesión)

---

## Dependencias del paquete

```
Asterisk.Sdk.VoiceAi.OpenAiRealtime
    → Asterisk.Sdk.VoiceAi     (ISessionHandler, AudioSocketSession, VoiceAiSessionBroker)
    → Asterisk.Sdk.Audio       (ResamplerFactory, PolyphaseResampler, AudioFormat)
    → Microsoft.Extensions.DependencyInjection.Abstractions
    → Microsoft.Extensions.Logging.Abstractions
    → Microsoft.Extensions.Options
    → System.Reactive           (Subject<T>, IObservable<T>)
```

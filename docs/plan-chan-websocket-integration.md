# Plan: Integración Completa chan_websocket — Asterisk.Sdk

**Fecha:** 2026-03-04
**Versión Asterisk objetivo:** 23+
**Prerequisito completado:** Fase 0 (TechType.WebSocket, AMI Voicemail actions, ARI endpoints básicos, ChannelToneDetected)

---

## Resumen Ejecutivo

`chan_websocket` (Asterisk 23) permite canales de audio bidireccional sobre WebSocket. La integración completa requiere 4 niveles, desde completar el protocolo ARI REST hasta un servidor de audio streaming con `System.IO.Pipelines`.

**Dependencias entre niveles:**

```
Nivel 1: ARI Core Completeness
    ↓
Nivel 2: Typed Event Dispatch + Métricas
    ↓
Nivel 3: Audio Streaming Server
    ↓
Nivel 4: Activities + Observabilidad
```

---

## Decisiones Arquitectónicas Resueltas

### ADR-1: Servidor de Audio — TcpListener + WebSocket.CreateFromStream

**Decisión:** Usar `TcpListener` (patrón `FastAgiServer`) con HTTP upgrade manual (~40 LOC) y `WebSocket.CreateFromStream()` para obtener un `ManagedWebSocket` con RFC 6455 compliance completa.

**Alternativas evaluadas:**

| Criterio | HttpListener | Kestrel SlimBuilder | Raw TCP + WS manual | **TcpListener + CreateFromStream** |
|----------|:-:|:-:|:-:|:-:|
| AOT compatible | Pobre (Linux) | Excelente | Perfecto | **Excelente** |
| Dependencias nuevas | Ninguna | `Microsoft.AspNetCore.App` | Ninguna | **Ninguna** |
| Soporte Linux | Marginal | Excelente | Excelente | **Excelente** |
| Performance 1000+ streams | Pobre | Excelente | Excelente | **Muy buena** |
| Complejidad | Baja | Baja-Media | Alta (300-500 LOC) | **Baja-Media (~40 LOC handshake)** |
| RFC 6455 compliance | Sí | Sí | Tu responsabilidad | **Sí (ManagedWebSocket)** |
| Consistencia con codebase | No | No | Sí | **Sí (patrón FastAgiServer)** |

**Justificación:**
- Zero dependencias nuevas (el SDK no tiene ASP.NET Core hoy)
- Patrón idéntico a `FastAgiServer` (`TcpListener` + `AcceptTcpClientAsync`)
- RFC 6455 delegado al runtime via `ManagedWebSocket` (no reinventamos frame parsing)
- AOT-safe, zero reflection, zero trim warnings
- Si en el futuro se necesita compartir puerto con HTTP → migrar a Kestrel en proyecto separado `Asterisk.Sdk.Audio.Hosting`

---

### ADR-2: Protocolo de Audio — AudioSocket primario, WebSocket secundario

**Decisión:** Implementar ambos protocolos detrás de `IAudioStream`. AudioSocket como primario, WebSocket como secundario.

| Criterio | WebSocket Binary Frames | **AudioSocket Protocol** |
|----------|:-:|:-:|
| Protocolo | RFC 6455 sobre TCP | `[1B tipo][3B longitud BE][payload]` |
| Identificación de canal | Via URL (manual) | **Frame 0x00 con UUID (automático)** |
| Señal de hangup | Cierre WebSocket (indirecto) | **Frame 0xFF (explícito, <1ms)** |
| Señal de error | Ninguna in-band | **Frame 0x10 con mensaje** |
| Detección de silencio | No distinguible | **Frame 0x02 (protocolo nativo)** |
| Overhead @ 1000 ch × 50 fps | 400 KB/s | **200 KB/s** |
| Integración Pipelines | Buena (ReceiveAsync) | **Perfecta (SequenceReader, como AmiProtocolReader)** |
| Asterisk mínimo | 16.3+ | 16.8+ |

**Justificación:**
- AudioSocket tiene señalización explícita de hangup/error/silence — crítico para cleanup determinístico en 100K+ canales
- El frame parser es idéntico en estructura a `AmiProtocolReader` (header fijo + payload variable con `SequenceReader<byte>`)
- UUID automático al conectar elimina necesidad de parsear URLs
- WebSocket se ofrece para deployments sin `chan_audiosocket` compilado

**Arquitectura resultante:**

```
IAudioStream
  ├── AudioSocketSession   (TCP + SequenceReader, PipelineSocketConnection)  ← primario
  └── WebSocketAudioSession  (WS binary frames, ManagedWebSocket)            ← secundario
```

---

### ADR-3: Índice por Tecnología — Scan lazy ahora, ConcurrentDict después

**Decisión:** Implementar `GetChannelsByTechnology()` con scan lazy sobre `_channelsByName` (O(N)). Migrar a tercer `ConcurrentDictionary` cuando profiling lo justifique.

| Criterio | **Scan lazy (ahora)** | 3er ConcurrentDict (futuro) | FrozenDict periódico |
|----------|:-:|:-:|:-:|
| Lookup @ 100K ch | 500 μs | 10 μs | 0.05 μs |
| Overhead en Add/Remove | **0 ns** | 50 ns | 0 ns |
| Memoria extra @ 100K | **0 bytes** | 4.2 MB | 8.4 MB peak |
| Staleness | Ninguna | Ninguna | 0-5 segundos |
| GC pressure | **Cero** | Cero | 4 MB/rebuild |
| Complejidad | **5 LOC** | 30 LOC | 50 LOC + timer |

**Justificación:**
- `GetChannelsByState()` ya usa el mismo patrón scan → consistencia
- 500μs a 100K canales = 0.05% CPU para un dashboard polling 1x/segundo → aceptable
- Zero overhead en hot path (OnNewChannel/OnHangup) que se ejecuta miles de veces/segundo
- **Trigger de upgrade a ConcurrentDict:** profiling muestra >10 calls/s a 50K+ canales

**FrozenDict descartado:** staleness de 0-5s inaceptable para telefonía real-time, GC pressure de 4MB/rebuild, inconsistente con patrones existentes.

---

## Nivel 1 — ARI Core Completeness (P0) — COMPLETADO

> **Estado:** Completado 2026-03-04. Sprint 1.1 (modelo AriChannel +8 props, +3 clases auxiliares, +4 registros JsonSerializable), Sprint 1.2 (GetVariable/SetVariable), Sprint 1.3 (11 endpoints: Hold, Unhold, Mute, Unmute, SendDtmf, Play, Record, Snoop, Redirect, Continue, CreateWithoutDial). Build 0 warnings, 14 tests nuevos, 48 ARI tests total.
>
> **Objetivo:** Completar el modelo `AriChannel`, agregar los endpoints REST faltantes, y exponer channel variables vía ARI. Sin esto, no se puede negociar formato de audio ni controlar canales WebSocket.

### Sprint 1.1: Modelo `AriChannel` completo

**Archivo:** `src/Asterisk.Sdk/IAriClient.cs`

El modelo actual tiene 3 campos (`Id`, `Name`, `State`). Asterisk ARI devuelve ~15. Agregar los campos faltantes:

```csharp
public sealed class AriChannel
{
    // Existentes
    public string Id { get; set; } = string.Empty;
    public string Name { get; set; } = string.Empty;
    public AriChannelState State { get; set; } = AriChannelState.Unknown;

    // Nuevos — caller identity
    public AriCallerId? Caller { get; set; }
    public AriCallerId? Connected { get; set; }
    public string? Accountcode { get; set; }

    // Nuevos — dialplan location
    public AriDialplanCep? Dialplan { get; set; }
    public string? Language { get; set; }

    // Nuevos — timestamps & protocol
    public DateTimeOffset? Creationtime { get; set; }
    public string? Protocol { get; set; }     // "WebSocket", "PJSIP", etc.

    // Nuevos — channel variables (populated cuando se usa channelVars en subscription)
    public Dictionary<string, string>? ChannelVars { get; set; }
}
```

**Modelos auxiliares nuevos** (mismo archivo):

```csharp
public sealed class AriCallerId
{
    public string Name { get; set; } = string.Empty;
    public string Number { get; set; } = string.Empty;
}

public sealed class AriDialplanCep
{
    public string Context { get; set; } = string.Empty;
    public string Exten { get; set; } = string.Empty;
    public int Priority { get; set; }
    public string? AppName { get; set; }
    public string? AppData { get; set; }
}
```

**Archivos a modificar:**

| Archivo | Cambio |
|---------|--------|
| `src/Asterisk.Sdk/IAriClient.cs` | Agregar propiedades a `AriChannel` + 2 clases nuevas |
| `src/Asterisk.Sdk.Ari/Resources/AriJsonContext.cs` | Registrar `AriCallerId`, `AriDialplanCep`, `Dictionary<string, string>` |
| `Tests/Asterisk.Sdk.Ari.Tests/Resources/AriResourceTests.cs` | Test de deserialización del modelo completo |

**Criterio de aceptación:**
- `AriChannel` deserializa correctamente el JSON completo de Asterisk ARI
- Los campos nuevos son `null` cuando Asterisk no los envía (backward compatible)
- Build 0 warnings

---

### Sprint 1.2: Channel Variable Endpoints

**Archivo:** `src/Asterisk.Sdk/IAriClient.cs` — interfaz `IAriChannelsResource`

```csharp
/// <summary>Get a channel variable. GET /channels/{channelId}/variable</summary>
ValueTask<AriVariable> GetVariableAsync(string channelId, string variable,
    CancellationToken cancellationToken = default);

/// <summary>Set a channel variable. POST /channels/{channelId}/variable</summary>
ValueTask SetVariableAsync(string channelId, string variable, string value,
    CancellationToken cancellationToken = default);
```

**Modelo nuevo:**

```csharp
public sealed class AriVariable
{
    public string Value { get; set; } = string.Empty;
}
```

**Archivo:** `src/Asterisk.Sdk.Ari/Resources/AriChannelsResource.cs` — implementación

```csharp
public async ValueTask<AriVariable> GetVariableAsync(string channelId, string variable, ...)
{
    var response = await _http.GetAsync(
        $"channels/{Uri.EscapeDataString(channelId)}/variable?variable={Uri.EscapeDataString(variable)}", ct);
    response.EnsureSuccessStatusCode();
    var json = await response.Content.ReadAsStringAsync(ct);
    return JsonSerializer.Deserialize(json, AriJsonContext.Default.AriVariable)!;
}

public async ValueTask SetVariableAsync(string channelId, string variable, string value, ...)
{
    var url = $"channels/{Uri.EscapeDataString(channelId)}/variable?variable={Uri.EscapeDataString(variable)}&value={Uri.EscapeDataString(value)}";
    var response = await _http.PostAsync(url, null, ct);
    response.EnsureSuccessStatusCode();
}
```

**Archivos a modificar:**

| Archivo | Cambio |
|---------|--------|
| `src/Asterisk.Sdk/IAriClient.cs` | 2 métodos en interfaz + `AriVariable` |
| `src/Asterisk.Sdk.Ari/Resources/AriChannelsResource.cs` | 2 implementaciones |
| `src/Asterisk.Sdk.Ari/Resources/AriJsonContext.cs` | Registrar `AriVariable` |
| `Tests/Asterisk.Sdk.Ari.Tests/Resources/AriResourceTests.cs` | 2 tests (GetVariable, SetVariable) |

---

### Sprint 1.3: Channel Control Endpoints faltantes

**Archivo:** `src/Asterisk.Sdk/IAriClient.cs` — interfaz `IAriChannelsResource`

```csharp
/// <summary>PUT /channels/{channelId}/hold</summary>
ValueTask HoldAsync(string channelId, CancellationToken cancellationToken = default);

/// <summary>DELETE /channels/{channelId}/hold</summary>
ValueTask UnholdAsync(string channelId, CancellationToken cancellationToken = default);

/// <summary>PUT /channels/{channelId}/mute</summary>
ValueTask MuteAsync(string channelId, string? direction = null, CancellationToken cancellationToken = default);

/// <summary>DELETE /channels/{channelId}/mute</summary>
ValueTask UnmuteAsync(string channelId, string? direction = null, CancellationToken cancellationToken = default);

/// <summary>POST /channels/{channelId}/dtmf</summary>
ValueTask SendDtmfAsync(string channelId, string dtmf, int? before = null,
    int? between = null, int? duration = null, int? after = null,
    CancellationToken cancellationToken = default);

/// <summary>POST /channels/{channelId}/play</summary>
ValueTask<AriPlayback> PlayAsync(string channelId, string media, string? lang = null,
    int? offsetms = null, int? skipms = null, string? playbackId = null,
    CancellationToken cancellationToken = default);

/// <summary>POST /channels/{channelId}/record</summary>
ValueTask<AriLiveRecording> RecordAsync(string channelId, string name, string format,
    int? maxDurationSeconds = null, int? maxSilenceSeconds = null,
    string? ifExists = null, bool? beep = null,
    string? terminateOn = null, CancellationToken cancellationToken = default);

/// <summary>POST /channels/{channelId}/snoop</summary>
ValueTask<AriChannel> SnoopAsync(string channelId, string app, string? spy = null,
    string? whisper = null, string? snoopId = null,
    CancellationToken cancellationToken = default);

/// <summary>POST /channels/{channelId}/redirect</summary>
ValueTask RedirectAsync(string channelId, string endpoint,
    CancellationToken cancellationToken = default);

/// <summary>POST /channels/{channelId}/continue</summary>
ValueTask ContinueAsync(string channelId, string? context = null,
    string? extension = null, int? priority = null,
    string? label = null, CancellationToken cancellationToken = default);

/// <summary>POST /channels/create — create without dialing</summary>
ValueTask<AriChannel> CreateWithoutDialAsync(string endpoint, string app,
    string? channelId = null, string? otherChannelId = null,
    string? originator = null, string? formats = null,
    CancellationToken cancellationToken = default);
```

**Archivos a modificar:**

| Archivo | Cambio |
|---------|--------|
| `src/Asterisk.Sdk/IAriClient.cs` | 11 métodos nuevos en interfaz |
| `src/Asterisk.Sdk.Ari/Resources/AriChannelsResource.cs` | 11 implementaciones |
| `Tests/Asterisk.Sdk.Ari.Tests/Resources/AriResourceTests.cs` | 11 tests |

**Criterio de aceptación:**
- Todos los endpoints de `Asterisk ARI /channels` están cubiertos
- Cada método tiene al menos 1 unit test
- Build 0 warnings

---

## Nivel 2 — Typed Event Dispatch + Métricas ARI (P1) — COMPLETADO

> **Estado:** Completado 2026-03-04. Sprint 2.1 (24 eventos registrados en AriJsonContext), Sprint 2.2 (11 nuevas clases de evento), Sprint 2.3 (ParseEvent tipado con registry estático de 24 tipos + fallback a AriEvent base), Sprint 2.4 (AriEventPump con Channel<AriEvent> backpressure + AriMetrics con 7 instrumentos). Build 0 warnings, 19 tests nuevos (16 ParseEvent + 3 EventPump), 67 ARI tests total.
>
> **Objetivo:** Que `AriClient` emita eventos tipados (`StasisStartEvent`, `ChannelStateChangeEvent`, etc.) en lugar de solo `AriEvent` base. Agregar `AriEventPump` para backpressure y métricas.

### Sprint 2.1: Registrar todos los ARI events en `AriJsonContext`

**Archivo:** `src/Asterisk.Sdk.Ari/Resources/AriJsonContext.cs`

Agregar `[JsonSerializable]` para cada evento tipado existente (13) y los nuevos:

```csharp
// Existentes (no registrados aún)
[JsonSerializable(typeof(StasisStartEvent))]
[JsonSerializable(typeof(StasisEndEvent))]
[JsonSerializable(typeof(ChannelStateChangeEvent))]
[JsonSerializable(typeof(ChannelDtmfReceivedEvent))]
[JsonSerializable(typeof(ChannelHangupRequestEvent))]
[JsonSerializable(typeof(BridgeCreatedEvent))]
[JsonSerializable(typeof(BridgeDestroyedEvent))]
[JsonSerializable(typeof(ChannelEnteredBridgeEvent))]
[JsonSerializable(typeof(ChannelLeftBridgeEvent))]
[JsonSerializable(typeof(PlaybackStartedEvent))]
[JsonSerializable(typeof(PlaybackFinishedEvent))]
[JsonSerializable(typeof(DialEvent))]
[JsonSerializable(typeof(ChannelToneDetectedEvent))]   // ya existe

// Nuevos para chan_websocket y completeness
[JsonSerializable(typeof(ChannelCreatedEvent))]
[JsonSerializable(typeof(ChannelDestroyedEvent))]
[JsonSerializable(typeof(ChannelVarsetEvent))]
[JsonSerializable(typeof(ChannelHoldEvent))]
[JsonSerializable(typeof(ChannelUnholdEvent))]
[JsonSerializable(typeof(ChannelTalkingStartedEvent))]
[JsonSerializable(typeof(ChannelTalkingFinishedEvent))]
[JsonSerializable(typeof(ChannelConnectedLineEvent))]
[JsonSerializable(typeof(RecordingStartedEvent))]
[JsonSerializable(typeof(RecordingFinishedEvent))]
[JsonSerializable(typeof(EndpointStateChangeEvent))]
```

### Sprint 2.2: Nuevos ARI Events

**Archivo:** `src/Asterisk.Sdk.Ari/Events/AriEvents.cs`

```csharp
public sealed class ChannelCreatedEvent : AriEvent
{
    public AriChannel? Channel { get; set; }
}

public sealed class ChannelDestroyedEvent : AriEvent
{
    public AriChannel? Channel { get; set; }
    public int? Cause { get; set; }
    public string? CauseTxt { get; set; }
}

public sealed class ChannelVarsetEvent : AriEvent
{
    public AriChannel? Channel { get; set; }
    public string? Variable { get; set; }
    public string? Value { get; set; }
}

public sealed class ChannelHoldEvent : AriEvent
{
    public AriChannel? Channel { get; set; }
    public string? MusicClass { get; set; }
}

public sealed class ChannelUnholdEvent : AriEvent
{
    public AriChannel? Channel { get; set; }
}

public sealed class ChannelTalkingStartedEvent : AriEvent
{
    public AriChannel? Channel { get; set; }
}

public sealed class ChannelTalkingFinishedEvent : AriEvent
{
    public AriChannel? Channel { get; set; }
    public int? Duration { get; set; }
}

public sealed class ChannelConnectedLineEvent : AriEvent
{
    public AriChannel? Channel { get; set; }
}

public sealed class RecordingStartedEvent : AriEvent
{
    public AriLiveRecording? Recording { get; set; }
}

public sealed class RecordingFinishedEvent : AriEvent
{
    public AriLiveRecording? Recording { get; set; }
}

public sealed class EndpointStateChangeEvent : AriEvent
{
    public AriEndpoint? Endpoint { get; set; }
}
```

**Archivos a crear/modificar:**

| Archivo | Cambio |
|---------|--------|
| `src/Asterisk.Sdk.Ari/Events/AriEvents.cs` | 11 event classes nuevas |
| `src/Asterisk.Sdk.Ari/Resources/AriJsonContext.cs` | 23 registros `[JsonSerializable]` |

---

### Sprint 2.3: Typed Event Dispatch en `AriClient.ParseEvent`

**Archivo:** `src/Asterisk.Sdk.Ari/Client/AriClient.cs`

Reemplazar `ParseEvent` con un registry estático `Dictionary<string, Func<string, AriEvent>>` que usa los type-info de `AriJsonContext`:

```csharp
private static readonly Dictionary<string, Func<string, JsonSerializerContext, AriEvent?>> _eventParsers = new(StringComparer.OrdinalIgnoreCase)
{
    ["StasisStart"] = (json, ctx) => JsonSerializer.Deserialize(json, ((AriJsonContext)ctx).StasisStartEvent),
    ["StasisEnd"] = (json, ctx) => JsonSerializer.Deserialize(json, ((AriJsonContext)ctx).StasisEndEvent),
    ["ChannelStateChange"] = (json, ctx) => JsonSerializer.Deserialize(json, ((AriJsonContext)ctx).ChannelStateChangeEvent),
    // ... todas las demás
};

private static AriEvent ParseEvent(ReadOnlySpan<byte> json)
{
    // 1. Quick-parse "type" field via JsonDocument
    // 2. Lookup in _eventParsers
    // 3. Si existe → deserializar al tipo específico (AOT-safe via AriJsonContext)
    // 4. Si no existe → fallback a AriEvent base con RawJson
}
```

**Beneficio:** Los suscriptores pueden hacer pattern matching:
```csharp
client.Subscribe(Observer.Create<AriEvent>(evt =>
{
    switch (evt)
    {
        case StasisStartEvent start:
            HandleStasisStart(start.Channel!, start.Args);
            break;
        case ChannelVarsetEvent varset:
            HandleVarSet(varset.Channel!, varset.Variable!, varset.Value!);
            break;
    }
}));
```

**Archivos a modificar:**

| Archivo | Cambio |
|---------|--------|
| `src/Asterisk.Sdk.Ari/Client/AriClient.cs` | Reescribir `ParseEvent`, agregar registry estático |
| `Tests/Asterisk.Sdk.Ari.Tests/Client/AriClientTests.cs` | Tests de dispatch tipado (nuevo archivo) |

---

### Sprint 2.4: `AriEventPump` + `AriMetrics`

Copiar patrón de `AsyncEventPump` y `AmiMetrics` para ARI.

**Nuevo archivo:** `src/Asterisk.Sdk.Ari/Internal/AriEventPump.cs`

```csharp
public sealed class AriEventPump : IAsyncDisposable
{
    private readonly Channel<AriEvent> _channel;
    private long _droppedEvents;
    private long _processedEvents;

    public AriEventPump(int capacity = 20_000) { ... }
    public bool TryEnqueue(AriEvent evt) { ... }
    public void Start(Func<AriEvent, ValueTask> handler) { ... }
}
```

**Nuevo archivo:** `src/Asterisk.Sdk.Ari/Diagnostics/AriMetrics.cs`

```csharp
public static class AriMetrics
{
    private static readonly Meter Meter = new("Asterisk.Sdk.Ari", "1.0.0");

    public static readonly Counter<long> EventsReceived = Meter.CreateCounter<long>("ari.events.received");
    public static readonly Counter<long> EventsDropped = Meter.CreateCounter<long>("ari.events.dropped");
    public static readonly Counter<long> EventsDispatched = Meter.CreateCounter<long>("ari.events.dispatched");
    public static readonly Counter<long> RestRequestsSent = Meter.CreateCounter<long>("ari.rest.requests.sent");
    public static readonly Counter<long> Reconnections = Meter.CreateCounter<long>("ari.reconnections");
    public static readonly Histogram<double> RestRoundtrip = Meter.CreateHistogram<double>("ari.rest.roundtrip", "ms");
    public static readonly Histogram<double> EventDispatchTime = Meter.CreateHistogram<double>("ari.event.dispatch", "ms");
}
```

**Integración en `AriClient`:**
- `EventLoopAsync` → `_pump.TryEnqueue(evt)` en lugar de `_eventSubject.OnNext(evt)` directo
- El pump despacha al `Subject<AriEvent>` en su hilo consumidor (desacopla WebSocket thread del procesamiento)
- Métricas se incrementan en cada paso

**Archivos a crear:**

| Archivo | Tipo |
|---------|------|
| `src/Asterisk.Sdk.Ari/Internal/AriEventPump.cs` | Nuevo |
| `src/Asterisk.Sdk.Ari/Diagnostics/AriMetrics.cs` | Nuevo |
| `Tests/Asterisk.Sdk.Ari.Tests/Internal/AriEventPumpTests.cs` | Nuevo |

**Archivos a modificar:**

| Archivo | Cambio |
|---------|--------|
| `src/Asterisk.Sdk.Ari/Client/AriClient.cs` | Integrar pump + métricas en event loop |

**Criterio de aceptación:**
- `AriClient` emite eventos tipados via pattern matching
- Backpressure funciona (eventos se dropean si el consumidor no alcanza)
- Métricas expuestas via `System.Diagnostics.Metrics`
- Build 0 warnings

---

## Nivel 3 — Audio Streaming Server (P1) — COMPLETADO

> **Estado:** Completado 2026-03-04. Sprint 3.1 (IAudioStream interfaz + AudioStreamState/AudioFrameType enums), Sprint 3.2 (AudioSocketProtocol parser con SequenceReader), Sprint 3.3 (AudioSocketServer TCP + AudioSocketSession con Pipe-based I/O), Sprint 3.4 (WebSocketAudioServer con HTTP upgrade manual + WebSocketAudioSession con ManagedWebSocket), Sprint 3.5 (AudioServerOptions + 19 tests nuevos). Build 0 warnings, 86 ARI tests total. 7 nuevos archivos.
>
> **Objetivo:** Implementar servidores AudioSocket (primario) y WebSocket (secundario) que acepten conexiones de audio entrantes de Asterisk. Ambos alimentan la misma interfaz `IAudioStream`.

### Sprint 3.1: Abstracción `IAudioStream`

**Nuevo archivo:** `src/Asterisk.Sdk.Ari/Audio/IAudioStream.cs`

```csharp
/// <summary>
/// Represents a bidirectional audio stream from Asterisk.
/// Implemented by both AudioSocketSession and WebSocketAudioSession.
/// </summary>
public interface IAudioStream : IAsyncDisposable
{
    /// <summary>Unique ID of the external media channel in Asterisk.</summary>
    string ChannelId { get; }

    /// <summary>Audio format (e.g., "slin16", "ulaw", "alaw").</summary>
    string Format { get; }

    /// <summary>Sample rate in Hz derived from format.</summary>
    int SampleRate { get; }

    /// <summary>Whether the stream is actively connected.</summary>
    bool IsConnected { get; }

    /// <summary>Observable for connection state changes.</summary>
    IObservable<AudioStreamState> StateChanges { get; }

    /// <summary>Read the next audio frame. Returns empty when stream ends.</summary>
    ValueTask<ReadOnlyMemory<byte>> ReadFrameAsync(CancellationToken cancellationToken = default);

    /// <summary>Write an audio frame to Asterisk.</summary>
    ValueTask WriteFrameAsync(ReadOnlyMemory<byte> audioData, CancellationToken cancellationToken = default);
}

public enum AudioStreamState { Connecting, Connected, Disconnected, Error }

/// <summary>AudioSocket frame types (wire protocol constants).</summary>
public enum AudioFrameType : byte
{
    /// <summary>Channel UUID (16 bytes, sent once at connection start).</summary>
    Uuid = 0x00,
    /// <summary>Audio data payload.</summary>
    Audio = 0x01,
    /// <summary>Silence indicator (no payload).</summary>
    Silence = 0x02,
    /// <summary>Error message.</summary>
    Error = 0x10,
    /// <summary>Hangup signal (no payload).</summary>
    Hangup = 0xFF
}
```

---

### Sprint 3.2: AudioSocket Protocol Parser (primario)

**Nuevo archivo:** `src/Asterisk.Sdk.Ari/Audio/AudioSocketProtocol.cs`

Parser del protocolo AudioSocket usando `SequenceReader<byte>` — estructura idéntica a `AmiProtocolReader`:

```
Frame format: [1 byte type][3 bytes length big-endian][payload]

┌──────────┬───────────────────────┬─────────────────────┐
│ Tipo (1B)│ Longitud BE (3B)      │ Payload (N bytes)   │
├──────────┼───────────────────────┼─────────────────────┤
│   0x00   │  0x00 0x00 0x10 (16)  │ UUID del canal      │
│   0x01   │  0x01 0x40 (320)      │ Audio slin16 20ms   │
│   0x02   │  0x00 0x00 0x00 (0)   │ (vacío)             │
│   0x10   │  variable             │ Mensaje de error    │
│   0xFF   │  0x00 0x00 0x00 (0)   │ (vacío)             │
└──────────┴───────────────────────┴─────────────────────┘
```

```csharp
/// <summary>
/// AudioSocket protocol frame parser for System.IO.Pipelines.
/// </summary>
internal static class AudioSocketProtocol
{
    /// <summary>Try to parse one frame from the buffer. Returns false if insufficient data.</summary>
    public static bool TryParseFrame(ref SequenceReader<byte> reader,
        out AudioFrameType frameType, out ReadOnlySequence<byte> payload)
    {
        if (reader.Remaining < 4) { frameType = default; payload = default; return false; }

        reader.TryRead(out byte type);
        reader.TryRead(out byte b0);
        reader.TryRead(out byte b1);
        reader.TryRead(out byte b2);
        int length = (b0 << 16) | (b1 << 8) | b2;

        if (reader.Remaining < length) { /* rewind */ return false; }

        frameType = (AudioFrameType)type;
        payload = reader.UnreadSequence.Slice(0, length);
        reader.Advance(length);
        return true;
    }

    /// <summary>Write a frame to the pipe writer.</summary>
    public static void WriteFrame(IBufferWriter<byte> writer, AudioFrameType type, ReadOnlySpan<byte> payload)
    {
        var span = writer.GetSpan(4 + payload.Length);
        span[0] = (byte)type;
        span[1] = (byte)(payload.Length >> 16);
        span[2] = (byte)(payload.Length >> 8);
        span[3] = (byte)(payload.Length);
        payload.CopyTo(span[4..]);
        writer.Advance(4 + payload.Length);
    }
}
```

---

### Sprint 3.3: `AudioSocketServer` + `AudioSocketSession`

**Nuevo archivo:** `src/Asterisk.Sdk.Ari/Audio/AudioSocketServer.cs`

Servidor TCP que acepta conexiones AudioSocket entrantes — patrón `FastAgiServer`:

```csharp
/// <summary>
/// Listens for incoming AudioSocket TCP connections from Asterisk ExternalMedia channels.
/// Each connection becomes an IAudioStream.
/// </summary>
public sealed class AudioSocketServer : IAsyncDisposable
{
    private readonly AudioServerOptions _options;
    private readonly ILogger<AudioSocketServer> _logger;
    private readonly ConcurrentDictionary<string, AudioSocketSession> _streams = new();
    private readonly Subject<IAudioStream> _streamSubject = new();
    private TcpListener? _listener;

    public IObservable<IAudioStream> OnStreamConnected => _streamSubject;
    public IAudioStream? GetStream(string channelId);
    public IEnumerable<IAudioStream> ActiveStreams => _streams.Values;
    public int ActiveStreamCount => _streams.Count;

    public ValueTask StartAsync(CancellationToken cancellationToken = default);
    public ValueTask StopAsync(CancellationToken cancellationToken = default);
}
```

**Nuevo archivo:** `src/Asterisk.Sdk.Ari/Audio/AudioSocketSession.cs`

```csharp
/// <summary>
/// A single AudioSocket connection backed by PipelineSocketConnection.
/// Read pump: PipeReader → SequenceReader → AudioSocketProtocol.TryParseFrame
/// Write pump: PipeWriter → AudioSocketProtocol.WriteFrame
/// </summary>
internal sealed class AudioSocketSession : IAudioStream
{
    private readonly PipelineSocketConnection _connection;  // reutiliza infraestructura existente
    private readonly BehaviorSubject<AudioStreamState> _state;
    private readonly Channel<ReadOnlyMemory<byte>> _audioInChannel;  // audio frames recibidos

    public string ChannelId { get; private set; }  // set al recibir frame UUID (0x00)
    public string Format { get; }
    public int SampleRate { get; }
    public bool IsConnected => _connection.IsConnected;
    public IObservable<AudioStreamState> StateChanges => _state;

    // Read pump: lee de _connection.Input (PipeReader), parsea con AudioSocketProtocol,
    // enqueue audio frames a _audioInChannel
    private async Task ReadPumpAsync(CancellationToken ct) { ... }

    // Consumer API
    public ValueTask<ReadOnlyMemory<byte>> ReadFrameAsync(CancellationToken ct)
        => _audioInChannel.Reader.ReadAsync(ct);

    public ValueTask WriteFrameAsync(ReadOnlyMemory<byte> audioData, CancellationToken ct)
    {
        AudioSocketProtocol.WriteFrame(_connection.Output, AudioFrameType.Audio, audioData.Span);
        return new(_connection.Output.FlushAsync(ct));
    }
}
```

---

### Sprint 3.4: `WebSocketAudioServer` + `WebSocketAudioSession` (secundario)

**Nuevo archivo:** `src/Asterisk.Sdk.Ari/Audio/WebSocketAudioServer.cs`

Servidor WebSocket usando `TcpListener` + HTTP upgrade + `WebSocket.CreateFromStream()` (ADR-1):

```csharp
/// <summary>
/// Listens for incoming WebSocket connections from Asterisk ExternalMedia channels
/// (transport=websocket). Uses TcpListener + manual HTTP upgrade + ManagedWebSocket.
/// </summary>
public sealed class WebSocketAudioServer : IAsyncDisposable
{
    // Accept loop:
    // 1. TcpListener.AcceptTcpClientAsync()
    // 2. Read HTTP headers (until \r\n\r\n)
    // 3. Extract Sec-WebSocket-Key, channel ID from URL path
    // 4. Send HTTP 101 response with Sec-WebSocket-Accept
    // 5. WebSocket.CreateFromStream(stream, isServer: true)
    // 6. Create WebSocketAudioSession, register, emit via Subject

    public IObservable<IAudioStream> OnStreamConnected => _streamSubject;
    public IAudioStream? GetStream(string channelId);
}
```

**Nuevo archivo:** `src/Asterisk.Sdk.Ari/Audio/WebSocketAudioSession.cs`

```csharp
/// <summary>
/// A single WebSocket audio connection. Receives binary frames via ManagedWebSocket.
/// </summary>
internal sealed class WebSocketAudioSession : IAudioStream
{
    private readonly WebSocket _webSocket;

    public async ValueTask<ReadOnlyMemory<byte>> ReadFrameAsync(CancellationToken ct)
    {
        var buffer = ArrayPool<byte>.Shared.Rent(4096);
        var result = await _webSocket.ReceiveAsync(buffer.AsMemory(), ct);
        // Return slice, caller must process before next read
    }

    public async ValueTask WriteFrameAsync(ReadOnlyMemory<byte> audioData, CancellationToken ct)
    {
        await _webSocket.SendAsync(audioData, WebSocketMessageType.Binary, endOfMessage: true, ct);
    }
}
```

---

### Sprint 3.5: `AudioServerOptions` + options unificadas

**Nuevo archivo:** `src/Asterisk.Sdk.Ari/Audio/AudioServerOptions.cs`

```csharp
public sealed class AudioServerOptions
{
    /// <summary>AudioSocket TCP listen port (default: 9092).</summary>
    public int AudioSocketPort { get; set; } = 9092;

    /// <summary>WebSocket listen port (default: 9093). Set to 0 to disable.</summary>
    public int WebSocketPort { get; set; } = 9093;

    /// <summary>Listen address (default: "0.0.0.0").</summary>
    public string ListenAddress { get; set; } = "0.0.0.0";

    /// <summary>Max concurrent audio streams across both protocols.</summary>
    public int MaxConcurrentStreams { get; set; } = 1000;

    /// <summary>Default audio format when not specified by the connection.</summary>
    public string DefaultFormat { get; set; } = "slin16";

    /// <summary>Inactivity timeout before closing a stream.</summary>
    public TimeSpan IdleTimeout { get; set; } = TimeSpan.FromSeconds(60);

    /// <summary>Backpressure threshold for input pipe (bytes).</summary>
    public int PipeBackpressureThreshold { get; set; } = 512 * 1024;
}
```

**Archivos a crear — Nivel 3 completo:**

| Archivo | Descripción |
|---------|-------------|
| `src/Asterisk.Sdk.Ari/Audio/IAudioStream.cs` | Interfaz + enums |
| `src/Asterisk.Sdk.Ari/Audio/AudioSocketProtocol.cs` | Parser SequenceReader |
| `src/Asterisk.Sdk.Ari/Audio/AudioSocketServer.cs` | Servidor TCP (patrón FastAgiServer) |
| `src/Asterisk.Sdk.Ari/Audio/AudioSocketSession.cs` | Session con PipelineSocketConnection |
| `src/Asterisk.Sdk.Ari/Audio/WebSocketAudioServer.cs` | Servidor WS (TcpListener + HTTP upgrade) |
| `src/Asterisk.Sdk.Ari/Audio/WebSocketAudioSession.cs` | Session con ManagedWebSocket |
| `src/Asterisk.Sdk.Ari/Audio/AudioServerOptions.cs` | Opciones unificadas |
| `Tests/Asterisk.Sdk.Ari.Tests/Audio/AudioSocketProtocolTests.cs` | Tests de frame parsing |
| `Tests/Asterisk.Sdk.Ari.Tests/Audio/AudioSocketSessionTests.cs` | Tests de session lifecycle |
| `Tests/Asterisk.Sdk.Ari.Tests/Audio/WebSocketAudioSessionTests.cs` | Tests WS session |

**Archivos a modificar:**

| Archivo | Cambio |
|---------|--------|
| `src/Asterisk.Sdk/IAriClient.cs` | Agregar `IAudioServer` interfaz pública |
| `src/Asterisk.Sdk.Ari/Client/AriClientOptions.cs` | Agregar `AudioServerOptions? AudioServer` |
| `src/Asterisk.Sdk.Hosting/ServiceCollectionExtensions.cs` | Registrar ambos servidores en DI |

**Criterio de aceptación:**
- `AudioSocketServer` acepta conexiones TCP, parsea UUID frame, crea `IAudioStream`
- `WebSocketAudioServer` acepta WebSocket, parsea channel ID de URL, crea `IAudioStream`
- Backpressure via `PipeOptions.pauseWriterThreshold` (AudioSocket) y `Channel<T>` bounded (WebSocket)
- AudioSocket frame parser unit tested con frames conocidos de Asterisk
- Build 0 warnings

---

## Nivel 4 — Activities + Observabilidad (P2) — COMPLETADO

> **Estado:** Completado 2026-03-04. Sprint 4.1 (AudioChannelVars con 14 constantes), Sprint 4.2 (ExternalMediaActivity con state machine + polling de audio servers), Sprint 4.3 (GetChannelsByTechnology/CountChannelsByTechnology en ChannelManager con lazy scan), Sprint 4.4 (AudioStreamMetrics con 10 instrumentos), Sprint 4.5 (DI Registration completa: AudioSocketServer/WebSocketAudioServer implementan IAudioServer, CompositeAudioServer agrega ambos servidores, AriClient recibe IAudioServer? via factory DI, ConfigureAudioServer en AriClientOptions, registro condicional en ServiceCollectionExtensions). Build 0 warnings, 389 tests total.
>
> **Objetivo:** High-level API para sesiones de audio y observabilidad completa.

### Sprint 4.1: Constantes tipadas de Channel Variables

**Nuevo archivo:** `src/Asterisk.Sdk.Ari/Audio/AudioChannelVars.cs`

```csharp
/// <summary>
/// Constants for chan_websocket, chan_audiosocket, and ExternalMedia channel variables.
/// Use with IAriChannelsResource.GetVariableAsync/SetVariableAsync.
/// </summary>
public static class AudioChannelVars
{
    // Channel function variables (set by Asterisk)
    public const string PeerIp = "CHANNEL(peerip)";
    public const string WriteFormat = "CHANNEL(writeformat)";
    public const string ReadFormat = "CHANNEL(readformat)";

    // ExternalMedia-specific
    public const string ExternalMediaProtocol = "EXTERNALMEDIA_PROTOCOL";
    public const string ExternalMediaAddress = "EXTERNALMEDIA_ADDRESS";

    // WebSocket-specific (chan_websocket)
    public const string WebSocketProtocol = "WEBSOCKET_PROTOCOL";
    public const string WebSocketGuid = "WEBSOCKET_GUID";
    public const string WebSocketUri = "WEBSOCKET_URI";

    // Audio format constants
    public const string Slin16 = "slin16";
    public const string Slin8 = "slin";
    public const string Slin48 = "slin48";
    public const string Ulaw = "ulaw";
    public const string Alaw = "alaw";
    public const string Opus = "opus";
    public const string G729 = "g729";
}
```

---

### Sprint 4.2: `ExternalMediaActivity`

**Nuevo archivo:** `src/Asterisk.Sdk.Activities/Activities/ExternalMediaActivity.cs`

State machine para el ciclo de vida completo de una sesión de audio:

```
Pending → Creating → WaitingConnection → Streaming → Completed/Failed/Cancelled
```

```csharp
/// <summary>
/// Activity that creates an ExternalMedia channel, waits for Asterisk
/// to connect back via AudioSocket or WebSocket, and provides an IAudioStream
/// for bidirectional audio streaming.
/// </summary>
public sealed class ExternalMediaActivity : IActivity
{
    private readonly IAriClient _ariClient;
    private readonly BehaviorSubject<ActivityStatus> _status = new(ActivityStatus.Pending);
    private IAudioStream? _audioStream;

    // Configuration
    public required string App { get; init; }
    public required string ExternalHost { get; init; }
    public string Format { get; init; } = "slin16";
    public string? Encapsulation { get; init; }     // "audiosocket" or null
    public string? Transport { get; init; }          // "websocket" or null
    public TimeSpan ConnectionTimeout { get; init; } = TimeSpan.FromSeconds(30);

    // Results
    public IAudioStream? AudioStream => _audioStream;
    public AriChannel? Channel { get; private set; }

    public ActivityStatus Status => _status.Value;
    public IObservable<ActivityStatus> StatusChanges => _status;

    public async ValueTask StartAsync(CancellationToken cancellationToken = default)
    {
        _status.OnNext(ActivityStatus.Starting);

        // 1. Create ExternalMedia channel via ARI
        Channel = await _ariClient.Channels.CreateExternalMediaAsync(
            App, ExternalHost, Format,
            encapsulation: Encapsulation,
            transport: Transport,
            cancellationToken: cancellationToken);

        _status.OnNext(ActivityStatus.InProgress);

        // 2. Wait for Asterisk to connect to audio server
        var server = _ariClient.AudioServer
            ?? throw new InvalidOperationException("Audio server not configured");

        using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeoutCts.CancelAfter(ConnectionTimeout);

        _audioStream = await server.OnStreamConnected
            .Where(s => s.ChannelId == Channel.Id)
            .FirstAsync()
            .ToTask(timeoutCts.Token);

        // 3. Stream is ready for use
    }

    public async ValueTask CancelAsync(CancellationToken cancellationToken = default)
    {
        if (Channel is not null)
            await _ariClient.Channels.HangupAsync(Channel.Id, cancellationToken);

        if (_audioStream is not null)
            await _audioStream.DisposeAsync();

        _status.OnNext(ActivityStatus.Cancelled);
    }

    public async ValueTask DisposeAsync()
    {
        if (_audioStream is not null)
            await _audioStream.DisposeAsync();
        _status.OnCompleted();
        _status.Dispose();
    }
}
```

---

### Sprint 4.3: Filtro por tech en `ChannelManager`

**Archivo:** `src/Asterisk.Sdk.Live/Channels/ChannelManager.cs`

Implementación con scan lazy (ADR-3), zero-alloc via `yield return`:

```csharp
/// <summary>
/// Get channels filtered by technology prefix (lazy, zero-alloc).
/// Example: "WebSocket", "PJSIP", "AudioSocket".
/// </summary>
public IEnumerable<AsteriskChannel> GetChannelsByTechnology(string technology)
{
    var prefix = string.Concat(technology, "/");
    foreach (var kvp in _channelsByName)
    {
        if (kvp.Key.StartsWith(prefix, StringComparison.Ordinal))
            yield return kvp.Value;
    }
}

/// <summary>Count channels by technology without materializing a collection.</summary>
public int CountChannelsByTechnology(string technology)
{
    var prefix = string.Concat(technology, "/");
    var count = 0;
    foreach (var kvp in _channelsByName)
    {
        if (kvp.Key.StartsWith(prefix, StringComparison.Ordinal))
            count++;
    }
    return count;
}
```

---

### Sprint 4.4: `AudioStreamMetrics`

**Nuevo archivo:** `src/Asterisk.Sdk.Ari/Diagnostics/AudioStreamMetrics.cs`

```csharp
public static class AudioStreamMetrics
{
    private static readonly Meter Meter = new("Asterisk.Sdk.Ari.Audio", "1.0.0");

    // Stream lifecycle
    public static readonly Counter<long> StreamsOpened =
        Meter.CreateCounter<long>("audio.streams.opened");
    public static readonly Counter<long> StreamsClosed =
        Meter.CreateCounter<long>("audio.streams.closed");

    // Data transfer
    public static readonly Counter<long> FramesReceived =
        Meter.CreateCounter<long>("audio.frames.received", description: "Audio frames received from Asterisk");
    public static readonly Counter<long> FramesSent =
        Meter.CreateCounter<long>("audio.frames.sent", description: "Audio frames sent to Asterisk");
    public static readonly Counter<long> BytesReceived =
        Meter.CreateCounter<long>("audio.bytes.received", "bytes");
    public static readonly Counter<long> BytesSent =
        Meter.CreateCounter<long>("audio.bytes.sent", "bytes");

    // Health
    public static readonly Counter<long> BufferUnderruns =
        Meter.CreateCounter<long>("audio.buffer.underruns", description: "Write pump starved — no audio to send");
    public static readonly Counter<long> HangupFrames =
        Meter.CreateCounter<long>("audio.hangup.frames", description: "AudioSocket hangup frames received");
    public static readonly Counter<long> ErrorFrames =
        Meter.CreateCounter<long>("audio.error.frames", description: "AudioSocket error frames received");

    // Latency
    public static readonly Histogram<double> FrameLatency =
        Meter.CreateHistogram<double>("audio.frame.latency", "ms", "Time from receive to consumer read");
}
```

---

### Sprint 4.5: DI Registration completa

**Archivo:** `src/Asterisk.Sdk.Hosting/ServiceCollectionExtensions.cs`

```csharp
// Dentro del bloque if (options.Ari is not null):
if (options.Ari.AudioServer is not null)
{
    services.AddOptions<AudioServerOptions>()
        .Configure(o => options.Ari.AudioServer.Invoke(o))
        .ValidateOnStart();
    services.TryAddSingleton<AudioSocketServer>();

    // WebSocket audio server only if port > 0
    services.TryAddSingleton<WebSocketAudioServer>();
}
```

**Archivo:** `src/Asterisk.Sdk.Ari/Client/AriClientOptions.cs`

```csharp
public Action<AudioServerOptions>? AudioServer { get; set; }
```

**Archivo:** `src/Asterisk.Sdk/IAriClient.cs`

```csharp
/// <summary>Unified audio server interface (AudioSocket + WebSocket).</summary>
public interface IAudioServer
{
    IObservable<IAudioStream> OnStreamConnected { get; }
    IAudioStream? GetStream(string channelId);
    IEnumerable<IAudioStream> ActiveStreams { get; }
    int ActiveStreamCount { get; }
}

// En IAriClient:
/// <summary>Access the audio server (null if not configured).</summary>
IAudioServer? AudioServer { get; }
```

**Uso final por el desarrollador:**

```csharp
services.AddAsterisk(options =>
{
    options.Ami = new() { Host = "pbx.local", Username = "admin", Secret = "secret" };
    options.Ari = new()
    {
        BaseUrl = "http://pbx.local:8088",
        Username = "admin",
        Password = "secret",
        Application = "myapp",
        AudioServer = audio =>
        {
            audio.AudioSocketPort = 9092;   // AudioSocket (primario)
            audio.WebSocketPort = 9093;     // WebSocket (secundario, 0=disabled)
            audio.MaxConcurrentStreams = 5000;
            audio.DefaultFormat = "slin16";
        }
    };
});
```

**Archivos a crear/modificar — Nivel 4 completo:**

| Archivo | Acción | Sprint |
|---------|--------|--------|
| `src/Asterisk.Sdk.Ari/Audio/AudioChannelVars.cs` | Nuevo | 4.1 |
| `src/Asterisk.Sdk.Activities/Activities/ExternalMediaActivity.cs` | Nuevo | 4.2 |
| `src/Asterisk.Sdk.Live/Channels/ChannelManager.cs` | Edit: 2 métodos | 4.3 |
| `src/Asterisk.Sdk.Ari/Diagnostics/AudioStreamMetrics.cs` | Nuevo | 4.4 |
| `src/Asterisk.Sdk.Hosting/ServiceCollectionExtensions.cs` | Edit: DI | 4.5 |
| `src/Asterisk.Sdk.Ari/Client/AriClientOptions.cs` | Edit: option | 4.5 |
| `src/Asterisk.Sdk/IAriClient.cs` | Edit: `IAudioServer` + property | 4.5 |
| `Tests/Asterisk.Sdk.Activities.Tests/Activities/ExternalMediaActivityTests.cs` | Nuevo | 4.2 |
| `Tests/Asterisk.Sdk.Live.Tests/Channels/ChannelManagerTests.cs` | Edit: 2 tests | 4.3 |

**Criterio de aceptación:**
- `ExternalMediaActivity` gestiona el ciclo completo: crear canal → esperar conexión → stream listo
- `GetChannelsByTechnology("WebSocket")` filtra canales correctamente
- Métricas de audio expuestas via `System.Diagnostics.Metrics`
- DI configura todo automáticamente con `AddAsterisk(o => o.Ari.AudioServer = ...)`
- Build 0 warnings

---

## Resumen de Archivos por Nivel

### Nivel 1 — ARI Core (3 sprints, ~4 archivos tocados)

| Archivo | Acción |
|---------|--------|
| `src/Asterisk.Sdk/IAriClient.cs` | Edit: modelo AriChannel + 13 métodos + AriCallerId + AriDialplanCep + AriVariable |
| `src/Asterisk.Sdk.Ari/Resources/AriChannelsResource.cs` | Edit: 13 implementaciones |
| `src/Asterisk.Sdk.Ari/Resources/AriJsonContext.cs` | Edit: registros nuevos |
| `Tests/Asterisk.Sdk.Ari.Tests/Resources/AriResourceTests.cs` | Edit: 13+ tests |

### Nivel 2 — Events + Métricas (4 sprints, ~8 archivos)

| Archivo | Acción |
|---------|--------|
| `src/Asterisk.Sdk.Ari/Events/AriEvents.cs` | Edit: 11 events nuevos |
| `src/Asterisk.Sdk.Ari/Resources/AriJsonContext.cs` | Edit: 23 registros |
| `src/Asterisk.Sdk.Ari/Client/AriClient.cs` | Edit: typed dispatch + pump |
| `src/Asterisk.Sdk.Ari/Internal/AriEventPump.cs` | Nuevo |
| `src/Asterisk.Sdk.Ari/Diagnostics/AriMetrics.cs` | Nuevo |
| `Tests/Asterisk.Sdk.Ari.Tests/Client/AriClientTests.cs` | Nuevo |
| `Tests/Asterisk.Sdk.Ari.Tests/Internal/AriEventPumpTests.cs` | Nuevo |

### Nivel 3 — Audio Server (5 sprints, ~13 archivos)

| Archivo | Acción |
|---------|--------|
| `src/Asterisk.Sdk.Ari/Audio/IAudioStream.cs` | Nuevo |
| `src/Asterisk.Sdk.Ari/Audio/AudioSocketProtocol.cs` | Nuevo |
| `src/Asterisk.Sdk.Ari/Audio/AudioSocketServer.cs` | Nuevo |
| `src/Asterisk.Sdk.Ari/Audio/AudioSocketSession.cs` | Nuevo |
| `src/Asterisk.Sdk.Ari/Audio/WebSocketAudioServer.cs` | Nuevo |
| `src/Asterisk.Sdk.Ari/Audio/WebSocketAudioSession.cs` | Nuevo |
| `src/Asterisk.Sdk.Ari/Audio/AudioServerOptions.cs` | Nuevo |
| `src/Asterisk.Sdk/IAriClient.cs` | Edit: IAudioServer |
| `src/Asterisk.Sdk.Ari/Client/AriClientOptions.cs` | Edit: AudioServer option |
| `src/Asterisk.Sdk.Hosting/ServiceCollectionExtensions.cs` | Edit: DI |
| `Tests/Asterisk.Sdk.Ari.Tests/Audio/AudioSocketProtocolTests.cs` | Nuevo |
| `Tests/Asterisk.Sdk.Ari.Tests/Audio/AudioSocketSessionTests.cs` | Nuevo |
| `Tests/Asterisk.Sdk.Ari.Tests/Audio/WebSocketAudioSessionTests.cs` | Nuevo |

### Nivel 4 — Activities + Observabilidad (5 sprints, ~9 archivos)

| Archivo | Acción |
|---------|--------|
| `src/Asterisk.Sdk.Ari/Audio/AudioChannelVars.cs` | Nuevo |
| `src/Asterisk.Sdk.Activities/Activities/ExternalMediaActivity.cs` | Nuevo |
| `src/Asterisk.Sdk.Live/Channels/ChannelManager.cs` | Edit: 2 métodos |
| `src/Asterisk.Sdk.Ari/Diagnostics/AudioStreamMetrics.cs` | Nuevo |
| `src/Asterisk.Sdk.Hosting/ServiceCollectionExtensions.cs` | Edit |
| `src/Asterisk.Sdk.Ari/Client/AriClientOptions.cs` | Edit |
| `src/Asterisk.Sdk/IAriClient.cs` | Edit |
| `Tests/Asterisk.Sdk.Activities.Tests/Activities/ExternalMediaActivityTests.cs` | Nuevo |
| `Tests/Asterisk.Sdk.Live.Tests/Channels/ChannelManagerTests.cs` | Edit |

---

## Verificación Final (cada nivel)

```bash
# Build (0 warnings requerido por TreatWarningsAsErrors)
dotnet build Asterisk.Sdk.slnx

# Unit tests
dotnet test Asterisk.Sdk.slnx --filter "FullyQualifiedName!~IntegrationTests"

# Verificar AOT (0 trim warnings)
dotnet publish src/Asterisk.Sdk.Ari/ -c Release -r linux-x64 --self-contained
```

---

## Riesgos Identificados y Mitigaciones

| # | Riesgo | Impacto | Mitigación |
|---|--------|---------|------------|
| 1 | **Backpressure de audio** — consumidor lento llena la Pipe | Audio frames se pierden o memoria crece | `PipeOptions.pauseWriterThreshold = 512KB` + métrica `BufferUnderruns` + log warning |
| 2 | **Race condition** channel-to-stream — conexión AudioSocket llega antes que `StasisStart` | `GetStream(channelId)` retorna null | `TaskCompletionSource` con timeout en `ExternalMediaActivity` + `OnStreamConnected` observable |
| 3 | **Codecs y transcoding** — formato del canal no coincide con lo esperado | Audio corrupto | SDK no hace transcoding. Documentar que `format` en `CreateExternalMediaAsync` debe coincidir con lo que el consumidor espera |
| 4 | **HTTP upgrade parsing** en WebSocketAudioServer | Edge cases en headers malformados | Parsing defensivo + timeout en handshake (5s) + unit tests con payloads reales de Asterisk |
| 5 | **Memory leaks** en streams huérfanos | Stream conecta pero el canal nunca se crea en ARI | `IdleTimeout` en `AudioServerOptions` + cleanup periódico en el server |

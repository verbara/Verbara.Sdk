# Sprint 23: Asterisk.Sdk.VoiceAi — STT + TTS + Core Pipeline

**Fecha:** 2026-03-19
**Estado:** Aprobado
**Objetivo:** Completar la capa Voice AI del SDK MIT: abstracciones core + pipeline con turn-taking e interrupciones + todos los providers STT/TTS + paquete de testing + ejemplo E2E.

---

## Contexto

Sprint 21-22 entregó `Asterisk.Sdk.Audio` (resampler FIR, AudioProcessor, VAD) y `Asterisk.Sdk.VoiceAi.AudioSocket` (TCP server/client protocolo AudioSocket). Sprint 23 construye sobre esa base con:

- `Asterisk.Sdk.VoiceAi` — abstracciones core + pipeline de orquestación
- `Asterisk.Sdk.VoiceAi.Stt` — 4 providers STT (Deepgram, Whisper, Azure, Google)
- `Asterisk.Sdk.VoiceAi.Tts` — 2 providers TTS (ElevenLabs, Azure)
- `Asterisk.Sdk.VoiceAi.Testing` — fakes MIT publicados en NuGet para usuarios del SDK
- `examples/Asterisk.Sdk.Examples.VoiceAi` — demo E2E con consola

---

## Decisiones de Diseño

### Implementación de providers: raw HTTP/WebSocket, cero dependencias nuevas

Todos los providers se implementan con `HttpClient`/`ClientWebSocket` directamente, sin SDKs oficiales de terceros. Razones:

- `Microsoft.CognitiveServices.Speech` usa P/Invoke sobre biblioteca C++ nativa — incompatible con AOT por diseño
- SDKs de Deepgram y ElevenLabs no están certificados AOT
- `IsAotCompatible=true` es globalmente requerido por `Directory.Build.props`
- Los protocolos de Deepgram y ElevenLabs son suficientemente simples para implementación raw (~250-300 líneas cada uno)
- Consistente con el patrón ARI (raw `HttpClient` + `DelegatingHandler`) y AudioSocket (raw TCP + Pipelines)

### Serialización JSON: source generation obligatoria

Todo JSON se deserializa con `[JsonSerializable]` source generation. Cada paquete define su propio `JsonSerializerContext` interno. No se usa `JsonSerializer.Deserialize<T>(json)` sin contexto.

### Arquitectura del pipeline: dual-loop + state machine + domain events

Combina tres patrones ya establecidos en el codebase:

- **Dual-loop** de `VoiceAi.AudioSocketSession`: `AudioMonitorLoop` (siempre corriendo, único lector del audio) + `PipelineLoop` (orquesta STT→handler→TTS)
- **State machine** de `CallSession`: estados explícitos con `ValidTransitions`, transiciones validadas
- **Domain events** de `CallSessionManager`: `IObservable<VoiceAiPipelineEvent>` vía `Subject<T>` para observabilidad externa

**Separación del audio source:** `AudioSocketSession.ReadAudioAsync()` usa `SingleReader = true`. `AudioMonitorLoop` es el único consumidor. Pasa utterances completas (como `ReadOnlyMemory<byte>[]`) a `PipelineLoop` vía `Channel<ReadOnlyMemory<byte>[]>`. `SpeechRecognizer.StreamAsync` recibe un `IAsyncEnumerable` sobre el array pre-colectado, no una fuente de audio en vivo.

### Testing: VoiceAi.Testing package + fake servers in-process

Dos capas independientes:
1. **`Asterisk.Sdk.VoiceAi.Testing`** (NuGet MIT público) — fakes para que usuarios del SDK testeen sus propias apps sin API keys
2. **Fake servers in-process** (internos a los test projects) — WebSocket loopback que hablan el wire protocol real de cada provider, sin API keys

---

## Estructura de Paquetes

```
Asterisk.Sdk.Audio                      (Sprint 21-22 — ya existe)
Asterisk.Sdk.VoiceAi.AudioSocket        (Sprint 21-22 — ya existe)
     ↑
Asterisk.Sdk.VoiceAi                    (NUEVO — core: abstractions + pipeline)
     ↑
Asterisk.Sdk.VoiceAi.Stt               (NUEVO — Deepgram, Whisper, Azure, Google)
Asterisk.Sdk.VoiceAi.Tts               (NUEVO — ElevenLabs, Azure)
     ↑
Asterisk.Sdk.VoiceAi.Testing           (NUEVO — FakeSpeechRecognizer, FakeSpeechSynthesizer)

examples/Asterisk.Sdk.Examples.VoiceAi (NUEVO — demo E2E consola)
```

**Dependencias NuGet nuevas: cero.** Todos los paquetes dependen únicamente de otros paquetes MIT del SDK.

---

## Asterisk.Sdk.VoiceAi — Core

### Abstracciones públicas

```csharp
// Abstract base — providers extienden esto
public abstract class SpeechRecognizer : IAsyncDisposable
{
    // audioFrames: IAsyncEnumerable sobre un array pre-colectado (SingleReader safe)
    // Providers streaming (Deepgram): yield parciales (IsFinal=false) + final (IsFinal=true)
    // Providers batch (Whisper, Google, Azure): bufferean internamente, yield un único IsFinal=true
    // ct: token del pipeline — si se cancela (barge-in o shutdown), el provider aborta
    public abstract IAsyncEnumerable<SpeechRecognitionResult> StreamAsync(
        IAsyncEnumerable<ReadOnlyMemory<byte>> audioFrames,
        AudioFormat format,
        CancellationToken ct = default);

    public virtual ValueTask DisposeAsync() => default;
}

public abstract class SpeechSynthesizer : IAsyncDisposable
{
    // ct: token del pipeline — si se cancela (barge-in), el provider aborta la síntesis
    public abstract IAsyncEnumerable<ReadOnlyMemory<byte>> SynthesizeAsync(
        string text,
        AudioFormat outputFormat,
        CancellationToken ct = default);

    public virtual ValueTask DisposeAsync() => default;
}

public interface IConversationHandler
{
    ValueTask<string> HandleAsync(
        string transcript,
        ConversationContext context,
        CancellationToken ct = default);
}
```

### Tipos de soporte

```csharp
public readonly record struct SpeechRecognitionResult(
    string Transcript,
    float Confidence,
    bool IsFinal,
    TimeSpan Duration);

public sealed class ConversationContext
{
    public Guid ChannelId { get; init; }
    // History: últimas N turns (pipeline cap: MaxHistoryTurns, default 20)
    // Responsabilidad del pipeline — el IConversationHandler no gestiona el límite
    public IReadOnlyList<ConversationTurn> History { get; init; }
    public AudioFormat InputFormat { get; init; }
}

public readonly record struct ConversationTurn(
    string UserTranscript,
    string AssistantResponse,
    DateTimeOffset Timestamp);
```

### Domain Events

```csharp
public abstract record VoiceAiPipelineEvent(DateTimeOffset Timestamp);

public record SpeechStartedEvent(DateTimeOffset Timestamp)
    : VoiceAiPipelineEvent(Timestamp);

public record SpeechEndedEvent(DateTimeOffset Timestamp, TimeSpan Duration)
    : VoiceAiPipelineEvent(Timestamp);

public record TranscriptReceivedEvent(
    DateTimeOffset Timestamp,
    string Transcript,
    float Confidence,
    bool IsFinal)
    : VoiceAiPipelineEvent(Timestamp);

public record ResponseGeneratedEvent(DateTimeOffset Timestamp, string Response)
    : VoiceAiPipelineEvent(Timestamp);

public record SynthesisStartedEvent(DateTimeOffset Timestamp)
    : VoiceAiPipelineEvent(Timestamp);

public record SynthesisEndedEvent(DateTimeOffset Timestamp, TimeSpan Duration)
    : VoiceAiPipelineEvent(Timestamp);

public record BargInDetectedEvent(DateTimeOffset Timestamp)
    : VoiceAiPipelineEvent(Timestamp);

// Error: pipeline emite este evento y transiciona a Listening (no termina la sesión)
// Excepciones fatales (session desconectada) terminan HandleSessionAsync con la excepción
public record PipelineErrorEvent(DateTimeOffset Timestamp, Exception Error, PipelineErrorSource Source)
    : VoiceAiPipelineEvent(Timestamp);

public enum PipelineErrorSource { Stt, Tts, Handler }
```

### VoiceAiPipeline

```csharp
public sealed class VoiceAiPipeline : IAsyncDisposable
{
    // Observabilidad — igual que CallSessionManager
    public IObservable<VoiceAiPipelineEvent> Events { get; }

    // Entry point: una llamada = una invocación
    // ct: propagado a ambos loops. Cancellation = shutdown limpio (no hangup)
    // Internamente: Task.WhenAll(AudioMonitorLoop(session, ct), PipelineLoop(session, ct))
    public ValueTask HandleSessionAsync(
        AudioSocketSession session,
        CancellationToken ct = default);
}
```

**States del pipeline:**
```
Idle → Listening → Recognizing → Handling → Speaking → Listening (ciclo)
                                                     ↓ (barge-in)
                                               Interrupted → Listening
```

**AudioMonitorLoop** (único consumidor de `session.ReadAudioAsync(ct)`):
- Recibe `ct` — cancela el loop al shutdown
- Lee frames de `session.ReadAudioAsync(ct)` (único lector — `SingleReader=true` en AudioSocketSession)
- Por cada frame `ReadOnlyMemory<byte>`: cast a `ReadOnlySpan<short>` vía `MemoryMarshal.Cast<byte, short>(frame.Span)` antes de pasar a `AudioProcessor.IsSilence(shortSpan, _options.SilenceThresholdDb)`
- **Agrupación en utterances:**
  - Inicio: primer frame `!IsSilence` → emite `SpeechStartedEvent`, comienza buffer
  - Fin: silencio sostenido >= `EndOfUtteranceSilence` → posta buffer como `ReadOnlyMemory<byte>[]` al `_utteranceChannel`, emite `SpeechEndedEvent`
  - Límite: si duración del buffer >= `MaxUtteranceDuration` → fuerza fin del utterance, posta buffer parcial al channel (permite STT procesar lo capturado hasta ese punto), emite `SpeechEndedEvent`
- **Barge-in** (solo activo cuando pipeline state == `Speaking`):
  - Si `!IsSilence` sostenido >= `BargInVoiceThreshold` → cancela `_ttsCts`, emite `BargInDetectedEvent`
  - Comienza nuevo buffer para el utterance que interrumpió

**PipelineLoop** (consume utterances del `_utteranceChannel`):
- Recibe `ct` — cancela el loop al shutdown
- `await foreach (var utterance in _utteranceChannel.Reader.ReadAllAsync(ct))`
- **`_ttsCts`:** campo `volatile CancellationTokenSource?` — `AudioMonitorLoop` accede a él durante barge-in; `PipelineLoop` lo reemplaza por turn. La escritura en `PipelineLoop` y la lectura-cancela en `AudioMonitorLoop` son atómicas por `volatile` (la referencia es asignada antes de entrar al estado `Speaking`)
- **STT:** `_stt.StreamAsync(ToAsyncEnumerable(utterance, ct), format, ct)` — pasa `ct` (shutdown cancela)
  - `ToAsyncEnumerable` es un helper privado estático: `static async IAsyncEnumerable<ReadOnlyMemory<byte>> ToAsyncEnumerable(ReadOnlyMemory<byte>[] frames, [EnumeratorCancellation] CancellationToken ct) { foreach (var f in frames) { ct.ThrowIfCancellationRequested(); yield return f; } }`
  - No requiere `System.Linq.Async` ni ninguna dependencia NuGet adicional
  - En `OperationCanceledException(ct)`: shutdown — propaga excepción, termina HandleSessionAsync
  - En cualquier otra excepción: emite `PipelineErrorEvent(Source: Stt)`, transiciona a `Listening`, continúa loop
- **Handler:** `_handler.HandleAsync(transcript, context, ct)` — pasa `ct`
  - En excepción: emite `PipelineErrorEvent(Source: Handler)`, transiciona a `Listening`, continúa
- **TTS:**
  - Crea `_ttsCts = new CancellationTokenSource()`
  - `using var linked = CancellationTokenSource.CreateLinkedTokenSource(ct, _ttsCts.Token)`
  - `_tts.SynthesizeAsync(response, outputFormat, linked.Token)` — linked combina shutdown + barge-in
  - En `OperationCanceledException(linked.Token)`:
    - Si `ct.IsCancellationRequested` → shutdown, propaga excepción
    - Si `_ttsCts.IsCancellationRequested` → barge-in, transiciona a `Listening`, continúa
  - En cualquier otra excepción: emite `PipelineErrorEvent(Source: Tts)`, transiciona a `Listening`, continúa

**Historial de conversación:**
- Pipeline mantiene `List<ConversationTurn>` internamente
- Trunca a `MaxHistoryTurns` (últimos N) antes de construir `ConversationContext` para cada llamada al handler

### VoiceAiPipelineOptions

```csharp
public sealed class VoiceAiPipelineOptions
{
    public AudioFormat InputFormat { get; set; } = AudioFormat.Slin16Mono8kHz;
    public AudioFormat OutputFormat { get; set; } = AudioFormat.Slin16Mono8kHz;
    public double SilenceThresholdDb { get; set; } = -40.0;
    public TimeSpan EndOfUtteranceSilence { get; set; } = TimeSpan.FromMilliseconds(500);

    // Mínima duración de voz continua (no silencio) durante Speaking para declarar barge-in
    public TimeSpan BargInVoiceThreshold { get; set; } = TimeSpan.FromMilliseconds(200);

    // Duración máxima de un utterance — si se supera, el buffer parcial se envía al STT
    public TimeSpan MaxUtteranceDuration { get; set; } = TimeSpan.FromSeconds(30);

    // Máximo número de turns a mantener en ConversationContext.History (FIFO)
    public int MaxHistoryTurns { get; set; } = 20;
}
```

### DI Registration y Wiring

`AddVoiceAiPipeline<THandler>` registra:
1. `THandler` como `IConversationHandler` (Scoped)
2. `VoiceAiPipeline` como Singleton
3. Un `IHostedService` interno (`VoiceAiSessionBroker`) que en `StartAsync`:
   - Resuelve `AudioSocketServer` del DI container
   - Adjunta a `OnSessionStarted` **sin await** (fire-and-forget con logging de errores):
     ```csharp
     session =>
     {
         _ = _pipeline.HandleSessionAsync(session, stoppingToken)
             .AsTask()
             .ContinueWith(t => _logger.LogError(t.Exception, "VoiceAi session error [{ChannelId}]", session.ChannelId),
                 TaskContinuationOptions.OnlyOnFaulted);
         return ValueTask.CompletedTask;
     }
     ```
   - El delegate retorna inmediatamente para que `HandleConnectionAsync` de `AudioSocketServer` no quede bloqueado durante toda la sesión Voice AI. Excepciones fatales se loguean (no se pierden silenciosamente).

```csharp
services.AddVoiceAiPipeline<EchoConversationHandler>(options =>
    options.EndOfUtteranceSilence = TimeSpan.FromMilliseconds(600));
// El usuario no necesita adjuntar manualmente OnSessionStarted
```

**Resolución de `IConversationHandler` con `IServiceScopeFactory`:**
`VoiceAiPipeline` es Singleton. `IConversationHandler` se registra como Scoped (para permitir inyección de DbContext, ILogger, etc.). El pipeline resuelve el handler vía `IServiceScopeFactory` — crea un scope por sesión de AudioSocket:

```csharp
public ValueTask HandleSessionAsync(AudioSocketSession session, CancellationToken ct = default)
{
    // Un scope por sesión — el handler obtiene dependencias Scoped aisladas por llamada
    using var scope = _scopeFactory.CreateScope();
    var handler = scope.ServiceProvider.GetRequiredService<IConversationHandler>();
    // ... AudioMonitorLoop + PipelineLoop usan este handler durante la vida de la sesión
}
```

Esto evita la captive dependency (Scoped resuelto en Singleton) y es el patrón estándar de .NET para este escenario.

---

## Asterisk.Sdk.VoiceAi.Stt — STT Providers

Todos extienden `SpeechRecognizer`. Cero dependencias de terceros.

### JSON Source Generation

Cada provider define DTOs internos y un `[JsonSerializable]` context:

```csharp
// Interno a Asterisk.Sdk.VoiceAi.Stt — no expuesto públicamente
[JsonSerializable(typeof(DeepgramResultMessage))]
[JsonSerializable(typeof(WhisperTranscriptionResponse))]
[JsonSerializable(typeof(GoogleSpeechRequest))]    // request body serializado con source gen
[JsonSerializable(typeof(GoogleSpeechResponse))]
internal partial class VoiceAiSttJsonContext : JsonSerializerContext { }

// DTOs internos
internal sealed class DeepgramResultMessage
{
    [JsonPropertyName("type")] public string Type { get; set; } = string.Empty;
    [JsonPropertyName("is_final")] public bool IsFinal { get; set; }
    [JsonPropertyName("channel")] public DeepgramChannel? Channel { get; set; }
}
internal sealed class DeepgramChannel
{
    [JsonPropertyName("alternatives")] public DeepgramAlternative[]? Alternatives { get; set; }
}
internal sealed class DeepgramAlternative
{
    [JsonPropertyName("transcript")] public string Transcript { get; set; } = string.Empty;
    [JsonPropertyName("confidence")] public float Confidence { get; set; }
}

internal sealed class WhisperTranscriptionResponse
{
    [JsonPropertyName("text")] public string Text { get; set; } = string.Empty;
}

// Google STT request — serializado con source generation (no string interpolation)
internal sealed class GoogleSpeechRequest
{
    [JsonPropertyName("config")] public GoogleSpeechConfig Config { get; set; } = new();
    [JsonPropertyName("audio")] public GoogleSpeechAudio Audio { get; set; } = new();
}
internal sealed class GoogleSpeechConfig
{
    [JsonPropertyName("encoding")] public string Encoding { get; set; } = "LINEAR16";
    [JsonPropertyName("sampleRateHertz")] public int SampleRateHertz { get; set; }
    [JsonPropertyName("languageCode")] public string LanguageCode { get; set; } = "es-CO";
    [JsonPropertyName("model")] public string Model { get; set; } = "default";
}
internal sealed class GoogleSpeechAudio
{
    [JsonPropertyName("content")] public string Content { get; set; } = string.Empty; // base64
}

internal sealed class GoogleSpeechResponse
{
    [JsonPropertyName("results")] public GoogleSpeechResult[]? Results { get; set; }
}
internal sealed class GoogleSpeechResult
{
    [JsonPropertyName("alternatives")] public GoogleSpeechAlternative[]? Alternatives { get; set; }
}
internal sealed class GoogleSpeechAlternative
{
    [JsonPropertyName("transcript")] public string Transcript { get; set; } = string.Empty;
    [JsonPropertyName("confidence")] public float Confidence { get; set; }
}
```

### DeepgramSpeechRecognizer (WebSocket streaming)

**Protocolo:**
```
wss://api.deepgram.com/v1/listen?encoding=linear16&sample_rate={rate}&channels=1&punctuate=true&interim_results=true
Authorization: Token {key}

→ Binary frames: PCM16 audio
← JSON: {"type":"Results","is_final":true,"channel":{"alternatives":[{"transcript":"...","confidence":0.99}]}}
← JSON: {"type":"Metadata",...} (ignorado)
```

`StreamAsync`: abre `ClientWebSocket`, corre send loop (envía frames del IAsyncEnumerable como binary frames) + receive loop (parsea JSON con `VoiceAiSttJsonContext`, yields `SpeechRecognitionResult`) via `Task.WhenAll`. WebSocket creado por invocación, disposed al completar o al cancelar.

### WhisperSpeechRecognizer (REST batch — OpenAI)

**Protocolo:**
```
POST https://api.openai.com/v1/audio/transcriptions
Authorization: Bearer {key}
Content-Type: multipart/form-data
Body: file={wav_bytes}, model=whisper-1, language={lang}

← JSON: {"text": "transcribed text"}
```

`StreamAsync`: drena IAsyncEnumerable a `MemoryStream`, añade cabecera WAV (PCM16), construye `MultipartFormDataContent`, POST, deserializa con `VoiceAiSttJsonContext.Default.WhisperTranscriptionResponse`, yields un `SpeechRecognitionResult(IsFinal: true)`.

**Nota:** `WhisperOptions.Endpoint` permite apuntar a un endpoint compatible con OpenAI Whisper API (ej. local, proxy). `AzureWhisperOptions` es un tipo separado porque requiere `DeploymentName` en el path y usa `api-key` header en lugar de `Authorization: Bearer`. No son intercambiables.

### AzureWhisperSpeechRecognizer (REST batch — Azure OpenAI)

**Protocolo:**
```
POST https://{resource}.openai.azure.com/openai/deployments/{deployment}/audio/transcriptions?api-version=2024-06-01
api-key: {key}
Content-Type: multipart/form-data

← JSON: {"text": "transcribed text"}
```

Mismo patrón de deserialización que `WhisperSpeechRecognizer` (`WhisperTranscriptionResponse`).

### GoogleSpeechRecognizer (REST batch)

**Protocolo:**
```
POST https://speech.googleapis.com/v1/speech:recognize?key={api_key}
Content-Type: application/json
Body: {"config":{"encoding":"LINEAR16","sampleRateHertz":8000,"languageCode":"es-CO"},"audio":{"content":"{base64}"}}

← JSON: {"results":[{"alternatives":[{"transcript":"...","confidence":0.99}]}]}
```

`StreamAsync`: drena IAsyncEnumerable, base64-encode el resultado en `GoogleSpeechAudio.Content`, construye `GoogleSpeechRequest` con los parámetros de config, serializa con `VoiceAiSttJsonContext.Default.GoogleSpeechRequest`, POST como `application/json`, deserializa respuesta con `VoiceAiSttJsonContext.Default.GoogleSpeechResponse`, yields un `SpeechRecognitionResult(IsFinal: true)`.

### Opciones por Provider

```csharp
public sealed class DeepgramOptions
{
    public string ApiKey { get; set; } = string.Empty;
    public string Model { get; set; } = "nova-2";
    public string Language { get; set; } = "es";
    public bool InterimResults { get; set; } = true;
    public bool Punctuate { get; set; } = true;
}

public sealed class WhisperOptions
{
    public string ApiKey { get; set; } = string.Empty;
    public string Model { get; set; } = "whisper-1";
    public string Language { get; set; } = "es";
    // Permite endpoints compatibles con OpenAI Whisper API (no intercambiable con Azure)
    public Uri Endpoint { get; set; } = new("https://api.openai.com/v1/audio/transcriptions");
}

public sealed class AzureWhisperOptions
{
    public string ApiKey { get; set; } = string.Empty;          // header: api-key (no Bearer)
    public Uri Endpoint { get; set; } = default!;              // https://{resource}.openai.azure.com/...
    public string DeploymentName { get; set; } = string.Empty; // va en el path URL
    public string ApiVersion { get; set; } = "2024-06-01";
}

public sealed class GoogleSpeechOptions
{
    public string ApiKey { get; set; } = string.Empty;
    public string LanguageCode { get; set; } = "es-CO";
    public string Model { get; set; } = "default";
}
```

### DI Registration

```csharp
// Elige uno — registrado como SpeechRecognizer (abstract base)
services.AddDeepgramSpeechRecognizer(opt => { opt.ApiKey = "..."; opt.Language = "es"; });
// o
services.AddWhisperSpeechRecognizer(opt => opt.ApiKey = "...");
// o
services.AddAzureWhisperSpeechRecognizer(opt => { opt.ApiKey = "..."; opt.Endpoint = new Uri("..."); });
// o
services.AddGoogleSpeechRecognizer(opt => { opt.ApiKey = "..."; opt.LanguageCode = "es-CO"; });
```

---

## Asterisk.Sdk.VoiceAi.Tts — TTS Providers

Todos extienden `SpeechSynthesizer`. Cero dependencias de terceros.

### JSON Source Generation

```csharp
[JsonSerializable(typeof(ElevenLabsTextChunk))]
internal partial class VoiceAiTtsJsonContext : JsonSerializerContext { }

internal sealed class ElevenLabsTextChunk
{
    [JsonPropertyName("text")] public string Text { get; set; } = string.Empty;
    [JsonPropertyName("flush")] public bool? Flush { get; set; }
    [JsonPropertyName("voice_settings")] public ElevenLabsVoiceSettings? VoiceSettings { get; set; }
}
internal sealed class ElevenLabsVoiceSettings
{
    [JsonPropertyName("stability")] public float Stability { get; set; }
    [JsonPropertyName("similarity_boost")] public float SimilarityBoost { get; set; }
}
// Nota: mensajes JSON de alignment de ElevenLabs se detectan por MessageType == Text (no deserialization)
// No se define DTO para alignment — evita el uso de object? que viola AOT
```

### ElevenLabsSpeechSynthesizer (WebSocket streaming)

**Protocolo:**
```
wss://api.elevenlabs.io/v1/text-to-speech/{voice_id}/stream-input?model_id=eleven_turbo_v2&output_format=pcm_16000
xi-api-key: {key}

→ JSON: {"text": "Hello", "voice_settings": {"stability": 0.5, "similarity_boost": 0.75}}
→ JSON: {"text": " ", "flush": true}
→ JSON: {"text": ""}   (close)
← Binary frames: PCM audio chunks
← JSON: alignment messages (detectados por primer byte == '{', filtrados, no expuestos)
```

`SynthesizeAsync`: abre `ClientWebSocket`, serializa chunks con `VoiceAiTtsJsonContext`, envía texto + flush, recibe frames: si `MessageType == Binary` → yield; si `MessageType == Text` → descartar (mensajes de alignment, detección por tipo de frame sin deserialización). WebSocket por invocación, disposed al completar o al cancelar.

### AzureTtsSpeechSynthesizer (REST streaming)

**Protocolo:**
```
POST https://{region}.tts.speech.microsoft.com/cognitiveservices/v1
Ocp-Apim-Subscription-Key: {key}
X-Microsoft-OutputFormat: {outputFormat}
Content-Type: application/ssml+xml
Body: <speak version='1.0' xml:lang='es-CO'><voice name='es-CO-SalomeNeural'>{text}</voice></speak>

← Chunked transfer: PCM audio stream
```

`SynthesizeAsync`: XML-escapa el texto con `System.Security.SecurityElement.Escape(text)` (protección contra XML injection cuando el handler devuelve texto con `<`, `>`, `&`), construye SSML por string interpolation, POST, lee response stream con `ReadAsync` en chunks de 4096 bytes, yields cada chunk. `HttpClient` creado una vez en el constructor (reusado entre llamadas).

### Opciones por Provider

```csharp
public sealed class ElevenLabsOptions
{
    public string ApiKey { get; set; } = string.Empty;
    public string VoiceId { get; set; } = string.Empty;
    public string ModelId { get; set; } = "eleven_turbo_v2";
    public float Stability { get; set; } = 0.5f;
    public float SimilarityBoost { get; set; } = 0.75f;
}

public sealed class AzureTtsOptions
{
    public string ApiKey { get; set; } = string.Empty;
    public string Region { get; set; } = string.Empty;    // "eastus"
    public string VoiceName { get; set; } = string.Empty; // "es-CO-SalomeNeural"
    // Constantes válidas definidas en AzureTtsOutputFormat static class
    // Ej: AzureTtsOutputFormat.Raw8Khz16BitMonoPcm, AzureTtsOutputFormat.Raw16Khz16BitMonoPcm
    public string OutputFormat { get; set; } = AzureTtsOutputFormat.Raw8Khz16BitMonoPcm;
}

// Constantes para evitar strings arbitrarios en OutputFormat
public static class AzureTtsOutputFormat
{
    public const string Raw8Khz16BitMonoPcm = "raw-8khz-16bit-mono-pcm";
    public const string Raw16Khz16BitMonoPcm = "raw-16khz-16bit-mono-pcm";
    public const string Raw24Khz16BitMonoPcm = "raw-24khz-16bit-mono-pcm";
    public const string Raw48Khz16BitMonoPcm = "raw-48khz-16bit-mono-pcm";
}
```

### DI Registration

```csharp
// Elige uno — registrado como SpeechSynthesizer (abstract base)
services.AddElevenLabsSpeechSynthesizer(opt => { opt.ApiKey = "..."; opt.VoiceId = "..."; });
// o
services.AddAzureTtsSpeechSynthesizer(opt =>
{
    opt.ApiKey = "...";
    opt.Region = "eastus";
    opt.VoiceName = "es-CO-SalomeNeural";
    opt.OutputFormat = AzureTtsOutputFormat.Raw8Khz16BitMonoPcm;
});
```

---

## Asterisk.Sdk.VoiceAi.Testing

Paquete MIT publicado en NuGet. Permite a usuarios del SDK testear sus apps Voice AI sin API keys.

### FakeSpeechRecognizer

```csharp
public sealed class FakeSpeechRecognizer : SpeechRecognizer
{
    public FakeSpeechRecognizer WithTranscript(string transcript, float confidence = 1.0f);
    public FakeSpeechRecognizer WithTranscripts(IEnumerable<string> transcripts); // cicla en orden
    public FakeSpeechRecognizer WithDelay(TimeSpan delay);
    public FakeSpeechRecognizer WithError(Exception exception, int afterCount = 0);

    // Assertions
    public int CallCount { get; }
    // Frames recibidos por invocación: el fake drena completamente el IAsyncEnumerable
    // antes de retornar, así el count es determinístico
    public IReadOnlyList<int> ReceivedFrameCounts { get; }
}
```

### FakeSpeechSynthesizer

```csharp
public sealed class FakeSpeechSynthesizer : SpeechSynthesizer
{
    public FakeSpeechSynthesizer WithSilence(TimeSpan duration); // genera PCM silence (frames de 20ms)
    public FakeSpeechSynthesizer WithAudio(ReadOnlyMemory<byte> pcmAudio);
    public FakeSpeechSynthesizer WithDelay(TimeSpan delay);
    public FakeSpeechSynthesizer WithError(Exception exception, int afterCount = 0);

    // Assertions
    public int CallCount { get; }
    public IReadOnlyList<string> SynthesizedTexts { get; }
}
```

### FakeConversationHandler

```csharp
public sealed class FakeConversationHandler : IConversationHandler
{
    public FakeConversationHandler WithResponse(string response);
    public FakeConversationHandler WithResponses(IEnumerable<string> responses); // cicla en orden
    public FakeConversationHandler WithDelay(TimeSpan delay);

    // Assertions
    public int CallCount { get; }
    public IReadOnlyList<string> ReceivedTranscripts { get; }
}
```

---

## Ejemplo E2E: `examples/Asterisk.Sdk.Examples.VoiceAi`

App de consola: Asterisk → AudioSocket → Deepgram STT → EchoHandler → ElevenLabs TTS → Asterisk.

```csharp
// Program.cs
var host = Host.CreateDefaultBuilder(args)
    .ConfigureServices((ctx, services) =>
    {
        services.AddAudioSocketServer(opt => opt.Port = 9092);
        services.AddDeepgramSpeechRecognizer(opt =>
            opt.ApiKey = ctx.Configuration["Deepgram:ApiKey"]!);
        services.AddElevenLabsSpeechSynthesizer(opt =>
        {
            opt.ApiKey = ctx.Configuration["ElevenLabs:ApiKey"]!;
            opt.VoiceId = ctx.Configuration["ElevenLabs:VoiceId"]!;
        });
        // AddVoiceAiPipeline registra VoiceAiSessionBroker (IHostedService) que
        // adjunta automáticamente a AudioSocketServer.OnSessionStarted
        services.AddVoiceAiPipeline<EchoConversationHandler>();
    }).Build();

// EchoConversationHandler.cs
public class EchoConversationHandler(ILogger<EchoConversationHandler> logger)
    : IConversationHandler
{
    public ValueTask<string> HandleAsync(string transcript, ConversationContext ctx, CancellationToken ct)
    {
        logger.LogInformation("[{ChannelId}] User: {Transcript}", ctx.ChannelId, transcript);
        return ValueTask.FromResult($"Dijiste: {transcript}");
    }
}
```

Configuración via `appsettings.json`. El wiring `AudioSocketServer → VoiceAiPipeline` es automático vía `VoiceAiSessionBroker`.

---

## Estrategia de Testing

| Tipo | Herramienta | Corre en CI |
|------|-------------|-------------|
| Unit tests pipeline (Idle→Listening, turn-taking, barge-in, MaxUtteranceDuration, error recovery) | `FakeSpeechRecognizer` + `FakeSpeechSynthesizer` + `FakeConversationHandler` | ✅ siempre |
| Protocol tests Deepgram | `DeepgramFakeServer` (WebSocket loopback in-process) | ✅ siempre |
| Protocol tests ElevenLabs | `ElevenLabsFakeServer` (WebSocket loopback in-process) | ✅ siempre |
| Protocol tests REST (Whisper, Azure, Google, AzureTts) | `MockHttpHandler` (DelegatingHandler con respuestas fijas) | ✅ siempre |
| Integration tests reales | `[DeepgramAvailableFact]` — skip si no hay `DEEPGRAM_API_KEY` | Manual / CI secrets |

**Escenarios cubiertos en pipeline unit tests:**
- `Idle → Listening` (transición inicial al recibir primera sesión)
- Turn-taking: silencio detecta fin de utterance → Recognizing
- `MaxUtteranceDuration`: utterance forzada al límite → STT recibe buffer parcial
- Barge-in: cancela TTS, transiciona a Listening
- STT error: emite `PipelineErrorEvent`, vuelve a Listening, no termina sesión
- TTS error: ídem
- Handler error: ídem
- Shutdown (`ct` cancelled): termina HandleSessionAsync limpiamente
- History truncation a `MaxHistoryTurns`

**Tests estimados:** ~70 total
- `Asterisk.Sdk.VoiceAi.Tests`: ~28 (pipeline state machine, turn-taking, barge-in, DI, wiring)
- `Asterisk.Sdk.VoiceAi.Stt.Tests`: ~20 (protocol tests por provider + DI + JSON deserialization)
- `Asterisk.Sdk.VoiceAi.Tts.Tests`: ~12 (protocol tests por provider + DI + JSON filtering)
- `Asterisk.Sdk.VoiceAi.Testing.Tests`: ~10 (fakes behavior + assertions API)

---

## Resumen de Paquetes

| Paquete | Tests | Dependencias nuevas |
|---------|-------|---------------------|
| `Asterisk.Sdk.VoiceAi` | ~28 | 0 |
| `Asterisk.Sdk.VoiceAi.Stt` | ~20 | 0 |
| `Asterisk.Sdk.VoiceAi.Tts` | ~12 | 0 |
| `Asterisk.Sdk.VoiceAi.Testing` | ~10 | 0 |
| Example | 0 | 0 |
| **Total** | **~70** | **0** |

# Sprint 23: Asterisk.Sdk.Audio + Asterisk.Sdk.VoiceAi.AudioSocket — Design Spec

**Fecha:** 2026-03-19
**Estado:** Draft
**Objetivo:** Fundación para Voice AI — audio processing library (resampling, format conversion) + AudioSocket transport bidireccional para streaming de audio Asterisk↔SDK. 2 paquetes MIT (open source).

---

## 1. Decisiones Arquitectónicas

| Decisión | Resultado |
|---|---|
| Paquetes | `Asterisk.Sdk.Audio` (audio processing) + `Asterisk.Sdk.VoiceAi.AudioSocket` (transport) |
| Licencia | MIT (open source) — atrae adopción, monetización en Pro.AgentAssist/Pro.CallAnalytics |
| Resampling | Pure C# polyphase FIR, pre-computed coefficients, zero-alloc hot path |
| Rate pairs | 6 soportados: 8↔16, 8↔24, 8↔48, 16↔24, 16↔48, 24↔48 kHz |
| AudioSocket | Server + Client (client para testing sin Asterisk real) |
| Transport | System.IO.Pipelines (same pattern as AMI/AGI), backpressure, MemoryPool |
| Correlation | UUID = Channel UniqueId por default, customizable |
| AOT | 100% pure managed C#, zero P/Invoke, zero reflection |

---

## 2. Estructura del Proyecto

```
src/Asterisk.Sdk.Audio/
├── Asterisk.Sdk.Audio.csproj
├── AudioFormat.cs                    (value type: sample rate, channels, bits, encoding)
├── AudioEncoding.cs                  (enum: LinearPcm, Float32)
├── Resampling/
│   ├── PolyphaseResampler.cs         (stateful, zero-alloc FIR resampler)
│   ├── ResamplerFactory.cs           (static factory for supported rate pairs)
│   └── ResamplerCoefficients.cs      (pre-computed filter coefficients)
├── Processing/
│   ├── AudioProcessor.cs             (static: gain, PCM16↔float32, clamp)
│   └── IAudioTransform.cs            (interface for chainable transforms)

src/Asterisk.Sdk.VoiceAi.AudioSocket/
├── Asterisk.Sdk.VoiceAi.AudioSocket.csproj
├── AudioSocketServer.cs              (TCP listener, accepts Asterisk connections)
├── AudioSocketClient.cs              (TCP client, for testing)
├── AudioSocketSession.cs             (bidirectional audio stream per connection)
├── AudioSocketFrame.cs               (protocol framing: type + length + payload)
├── AudioSocketFrameType.cs           (enum: Uuid, Audio, Silence, Error, Hangup)
├── AudioSocketOptions.cs             (server/client config)
├── DependencyInjection/
│   └── AudioSocketServiceCollectionExtensions.cs

tests/Asterisk.Sdk.Audio.Tests/
├── Resampling/
│   ├── PolyphaseResamplerTests.cs
│   └── ResamplerFactoryTests.cs
├── Processing/
│   └── AudioProcessorTests.cs

tests/Asterisk.Sdk.VoiceAi.AudioSocket.Tests/
├── AudioSocketServerTests.cs
├── AudioSocketClientTests.cs
├── AudioSocketFrameTests.cs
├── Integration/
│   └── AudioSocketRoundTripTests.cs
```

**Dependencias:**
- `Asterisk.Sdk.Audio` → ninguna (standalone, pure audio processing)
- `Asterisk.Sdk.VoiceAi.AudioSocket` → `Asterisk.Sdk.Audio` + `Microsoft.Extensions.Logging.Abstractions` + `Microsoft.Extensions.Hosting.Abstractions`

---

## 3. Asterisk.Sdk.Audio — Domain Models

### AudioFormat

```csharp
/// <summary>Describes an audio stream's format. Immutable value type.</summary>
public readonly record struct AudioFormat(
    int SampleRate,
    int Channels,
    int BitsPerSample,
    AudioEncoding Encoding)
{
    // ── Common telephony formats ──
    public static readonly AudioFormat Slin16Mono8kHz = new(8000, 1, 16, AudioEncoding.LinearPcm);
    public static readonly AudioFormat Slin16Mono16kHz = new(16000, 1, 16, AudioEncoding.LinearPcm);
    public static readonly AudioFormat Slin16Mono24kHz = new(24000, 1, 16, AudioEncoding.LinearPcm);
    public static readonly AudioFormat Slin16Mono48kHz = new(48000, 1, 16, AudioEncoding.LinearPcm);
    public static readonly AudioFormat Float32Mono16kHz = new(16000, 1, 32, AudioEncoding.Float32);

    /// <summary>Bytes per sample (all channels).</summary>
    public int BytesPerSample => Channels * (BitsPerSample / 8);

    /// <summary>Bytes for a frame of the given duration.</summary>
    public int BytesPerFrame(TimeSpan frameDuration) =>
        (int)(SampleRate * BytesPerSample * frameDuration.TotalSeconds);

    /// <summary>Number of samples for a frame of the given duration.</summary>
    public int SamplesPerFrame(TimeSpan frameDuration) =>
        (int)(SampleRate * frameDuration.TotalSeconds);
}

public enum AudioEncoding { LinearPcm, Float32 }
```

### IAudioTransform

```csharp
/// <summary>Chainable audio processing transform. Operates on raw bytes to support both PCM16 and float32.</summary>
public interface IAudioTransform
{
    /// <summary>Input format expected.</summary>
    AudioFormat InputFormat { get; }

    /// <summary>Output format after processing.</summary>
    AudioFormat OutputFormat { get; }

    /// <summary>Process audio data. Returns number of bytes written to output.</summary>
    int Process(ReadOnlySpan<byte> input, Span<byte> output);
}
```

**Note:** The interface uses `byte` spans (not `short`) to support both PCM16 and float32 transforms in the same chain. `PolyphaseResampler` internally casts to `short` spans. `AudioProcessor.ConvertToFloat32` can be wrapped as an `IAudioTransform` that converts PCM16 bytes to float32 bytes.

---

## 4. PolyphaseResampler

```csharp
/// <summary>
/// Polyphase FIR resampler for telephony rate pairs.
/// Stateful (holds delay line for frame continuity). One instance per audio stream.
/// Zero heap allocation on hot path — operates on caller-provided Spans.
/// </summary>
public sealed class PolyphaseResampler : IAudioTransform, IDisposable
{
    /// <summary>Creates a resampler for the given rate pair.</summary>
    public PolyphaseResampler(int inputRate, int outputRate);

    public AudioFormat OutputFormat { get; }

    /// <summary>
    /// Resamples PCM16 audio. Input/output are signed 16-bit samples.
    /// Returns the number of samples written to output.
    /// Output span must be large enough: input.Length * outputRate / inputRate + filterLength.
    /// </summary>
    public int Process(ReadOnlySpan<short> input, Span<short> output);

    /// <summary>Resets the internal delay line (e.g., on stream restart).</summary>
    public void Reset();

    /// <summary>Returns pooled delay line buffer. Throws ObjectDisposedException on subsequent Process() calls.</summary>
    public void Dispose();
}
```

**Delay line management:** The delay line is rented from `ArrayPool<float>.Shared` at construction (~128 bytes). `Dispose()` returns it and sets a disposed flag. `Process()` throws `ObjectDisposedException` if called after dispose. This prevents pool corruption.

**Output buffer sizing:** Callers should use `ResamplerFactory.CalculateOutputSize(inputSamples, inputRate, outputRate)` to determine the required output buffer size. The formula includes internal filter padding. Do NOT compute manually.

```csharp
// Removed redundant closing to fix markdown
```

**Supported rate pairs (verified at construction, throws ArgumentException otherwise):**

| Input | Output | Ratio | Up | Down |
|-------|--------|-------|----|----|
| 8000 | 16000 | 2:1 | 2 | 1 |
| 16000 | 8000 | 1:2 | 1 | 2 |
| 8000 | 24000 | 3:1 | 3 | 1 |
| 24000 | 8000 | 1:3 | 1 | 3 |
| 16000 | 24000 | 3:2 | 3 | 2 |
| 24000 | 16000 | 2:3 | 2 | 3 |
| 8000 | 48000 | 6:1 | 6 | 1 |
| 48000 | 8000 | 1:6 | 1 | 6 |
| 16000 | 48000 | 3:1 | 3 | 1 |
| 48000 | 16000 | 1:3 | 1 | 3 |
| 24000 | 48000 | 2:1 | 2 | 1 |
| 48000 | 24000 | 1:2 | 1 | 2 |

**Algorithm:** Standard polyphase decomposition:
1. Upsample by L (insert L-1 zeros between samples)
2. Apply lowpass FIR filter (pre-computed coefficients, cutoff = min(inputRate, outputRate) / 2)
3. Downsample by M (take every M-th sample)

Pre-computed coefficients stored as `static ReadOnlySpan<float>` — blittable, zero allocation, AOT-safe.

**Filter design:** 32-tap FIR per polyphase branch. Provides >80 dB stopband attenuation (more than sufficient for telephony). Coefficients generated offline via windowed sinc (Kaiser window, β=8).

### ResamplerFactory

```csharp
/// <summary>Factory for creating pre-configured resamplers.</summary>
public static class ResamplerFactory
{
    /// <summary>Creates a resampler for the given rate pair. Throws if unsupported.</summary>
    public static PolyphaseResampler Create(int inputRate, int outputRate);

    /// <summary>Checks if a rate pair is supported.</summary>
    public static bool IsSupported(int inputRate, int outputRate);

    /// <summary>Calculates the required output buffer size for a given input size.</summary>
    public static int CalculateOutputSize(int inputSamples, int inputRate, int outputRate);
}
```

---

## 5. AudioProcessor

```csharp
/// <summary>Static audio processing utilities. Zero-alloc, AOT-safe.</summary>
public static class AudioProcessor
{
    /// <summary>Apply gain in dB to PCM16 samples. Clamps to short range.</summary>
    public static void ApplyGain(Span<short> samples, float gainDb);

    /// <summary>Convert PCM16 to float32 (-1.0 to 1.0).</summary>
    public static void ConvertToFloat32(ReadOnlySpan<short> pcm16, Span<float> float32);

    /// <summary>Convert float32 (-1.0 to 1.0) to PCM16.</summary>
    public static void ConvertToPcm16(ReadOnlySpan<float> float32, Span<short> pcm16);

    /// <summary>Calculate RMS energy of PCM16 samples (for simple VAD).</summary>
    public static double CalculateRmsEnergy(ReadOnlySpan<short> samples);

    /// <summary>Check if audio frame is silence (below energy threshold).</summary>
    public static bool IsSilence(ReadOnlySpan<short> samples, double thresholdDb = -40.0);
}
```

---

## 6. AudioSocket Protocol

### Framing

```
[1 byte: type][3 bytes: payload length (network byte order)][N bytes: payload]
```

### Frame Types

```csharp
public enum AudioSocketFrameType : byte
{
    Uuid = 0x00,      // 16-byte UUID in big-endian (network byte order), sent once at connection start
                      // IMPORTANT: use new Guid(span, bigEndian: true) to parse correctly on little-endian .NET
    Audio = 0x01,     // PCM16 audio data (signed linear 16-bit, little-endian)
    Silence = 0x02,   // Silence indication (2-byte payload: duration in ms, network byte order)
    Error = 0x04,     // Error (optional error message as UTF-8)
    Hangup = 0xFF,    // Channel hung up
}
```

### AudioSocketFrame

```csharp
/// <summary>A single AudioSocket protocol frame.</summary>
public readonly record struct AudioSocketFrame(
    AudioSocketFrameType Type,
    ReadOnlyMemory<byte> Payload);
```

---

## 7. AudioSocketServer

```csharp
/// <summary>
/// TCP server that accepts AudioSocket connections from Asterisk.
/// Built on System.IO.Pipelines for zero-copy, backpressure-aware I/O.
/// </summary>
public sealed class AudioSocketServer : IHostedService, IAsyncDisposable
{
    public AudioSocketServer(AudioSocketOptions options, ILogger<AudioSocketServer> logger);

    /// <summary>Event fired when a new AudioSocket session starts.</summary>
    public event Func<AudioSocketSession, ValueTask>? OnSessionStarted;

    /// <summary>Number of active sessions.</summary>
    public int ActiveSessionCount { get; }

    public Task StartAsync(CancellationToken ct);
    public Task StopAsync(CancellationToken ct);
    public ValueTask DisposeAsync();
}
```

---

## 8. AudioSocketSession

```csharp
/// <summary>
/// A single AudioSocket connection — bidirectional audio stream.
/// One session per Asterisk channel.
/// </summary>
public sealed class AudioSocketSession : IAsyncDisposable
{
    /// <summary>UUID received from Asterisk (first frame).</summary>
    public Guid ChannelId { get; }

    /// <summary>Remote endpoint address.</summary>
    public string RemoteEndpoint { get; }

    /// <summary>Audio format of incoming audio (detected from Asterisk config).</summary>
    public AudioFormat InputFormat { get; }

    /// <summary>Whether the session is still connected.</summary>
    public bool IsConnected { get; }

    /// <summary>Read incoming audio frames (from caller). Completes on hangup.</summary>
    public IAsyncEnumerable<ReadOnlyMemory<byte>> ReadAudioAsync(CancellationToken ct);

    /// <summary>Write audio back to caller (e.g., TTS output).</summary>
    public ValueTask WriteAudioAsync(ReadOnlyMemory<byte> pcmData, CancellationToken ct);

    /// <summary>Send silence indication.</summary>
    public ValueTask WriteSilenceAsync(CancellationToken ct);

    /// <summary>Signal hangup to Asterisk.</summary>
    public ValueTask HangupAsync(CancellationToken ct);

    /// <summary>Event fired on hangup (from either side).</summary>
    public event Action? OnHangup;

    public ValueTask DisposeAsync();
}
```

**Implementation:**
- `System.IO.Pipelines.PipeReader` for incoming audio (zero-copy)
- `System.IO.Pipelines.PipeWriter` for outgoing audio (backpressure)
- Frame parsing in `AudioSocketFrameReader` (static, span-based)
- Connection tracking via `ConcurrentDictionary<Guid, AudioSocketSession>` in server

---

## 9. AudioSocketClient (for testing)

```csharp
/// <summary>
/// TCP client that simulates Asterisk's AudioSocket behavior.
/// Used for unit/integration testing without a real Asterisk instance.
/// </summary>
public sealed class AudioSocketClient : IAsyncDisposable
{
    public AudioSocketClient(string host, int port, Guid channelId);

    /// <summary>Connect to an AudioSocket server.</summary>
    public ValueTask ConnectAsync(CancellationToken ct);

    /// <summary>Send audio data to the server.</summary>
    public ValueTask SendAudioAsync(ReadOnlyMemory<byte> pcmData, CancellationToken ct);

    /// <summary>Send hangup signal.</summary>
    public ValueTask SendHangupAsync(CancellationToken ct);

    /// <summary>Read audio response from server.</summary>
    public IAsyncEnumerable<ReadOnlyMemory<byte>> ReadAudioAsync(CancellationToken ct);

    public ValueTask DisposeAsync();
}
```

---

## 10. AudioSocketOptions

```csharp
public sealed class AudioSocketOptions
{
    /// <summary>Address to listen on. Default: 0.0.0.0.</summary>
    public string ListenAddress { get; set; } = "0.0.0.0";

    /// <summary>Port to listen on. Default: 9092.</summary>
    public int Port { get; set; } = 9092;

    /// <summary>Maximum concurrent sessions. Default: 1000.</summary>
    public int MaxConcurrentSessions { get; set; } = 1000;

    /// <summary>Default audio format for sessions. Default: Slin16 8kHz mono.</summary>
    public AudioFormat DefaultFormat { get; set; } = AudioFormat.Slin16Mono8kHz;

    /// <summary>Receive buffer size in bytes. Default: 4096.</summary>
    public int ReceiveBufferSize { get; set; } = 4096;

    /// <summary>Connection timeout. Default: 30 seconds.</summary>
    public TimeSpan ConnectionTimeout { get; set; } = TimeSpan.FromSeconds(30);
}
```

---

## 11. DI Registration

```csharp
// AudioSocket server as hosted service
services.AddAudioSocketServer(options =>
{
    options.Port = 9092;
    options.MaxConcurrentSessions = 5000;
    options.DefaultFormat = AudioFormat.Slin16Mono16kHz;
});
```

---

## 12. Sprint Breakdown

### Sprint 23 (2 semanas)

**Asterisk.Sdk.Audio:**
- AudioFormat, AudioEncoding
- PolyphaseResampler (6 rate pairs, pre-computed coefficients)
- ResamplerFactory
- AudioProcessor (gain, PCM16↔float32, RMS energy, silence detection)
- IAudioTransform interface
- Unit tests: ~20

**Asterisk.Sdk.VoiceAi.AudioSocket:**
- AudioSocketFrameType, AudioSocketFrame
- AudioSocketServer (TCP listener, PipeReader/PipeWriter, session management)
- AudioSocketSession (bidirectional audio, hangup detection)
- AudioSocketClient (testing)
- AudioSocketOptions + DI
- Unit tests: ~15
- Integration test: client↔server round-trip

**Total: ~35 tests**

---

## 13. Performance Targets

| Metric | Target |
|--------|--------|
| Resampler per-frame latency (8→24 kHz, 160 samples) | <10 µs |
| Resampler heap allocation per frame | 0 bytes |
| Resampler state per stream | <256 bytes |
| AudioSocket frames/sec at 10K sessions | 500,000 |
| AudioSocket per-session memory | <4 KB (pipe buffers) |
| AudioSocket connection setup | <5 ms |

---

## 14. Asterisk Dialplan Integration Example

```ini
; extensions.conf
[voice-ai]
exten => _X.,1,Answer()
 same => n,Set(UUID=${UNIQUEID})
 same => n,AudioSocket(${UUID},voiceai-server:9092)
 same => n,Hangup()
```

The SDK developer writes:

```csharp
var server = new AudioSocketServer(options, logger);
server.OnSessionStarted += async session =>
{
    // session.ChannelId == Asterisk UNIQUEID
    // Read caller audio, process with AI, write response back
    await foreach (var audioChunk in session.ReadAudioAsync(ct))
    {
        // Feed to STT, get response, TTS, write back
        var response = await ProcessWithAiAsync(audioChunk);
        await session.WriteAudioAsync(response, ct);
    }
};
await server.StartAsync(ct);
```

---

## 15. YAGNI Documentado

| Feature | Status | Razón |
|---|---|---|
| 22.05 kHz resampling | YAGNI | Recomendar 16/24 kHz al provider |
| Arbitrary rate pairs | YAGNI | 6 telephony pairs cubren todos los AI providers |
| Noise reduction | YAGNI | Asterisk DENOISE() o provider-side |
| Echo cancellation | YAGNI | Asterisk media path handles this |
| Codec encoding (Opus, G.711) | YAGNI | AudioSocket usa PCM16 |
| SIMD vectorization | Defer v1.1 | Pure scalar C# sufficient for 10K calls |
| WebRTC transport | Defer | AudioSocket is simpler and sufficient |

# Sprint 23: Asterisk.Sdk.Audio + Asterisk.Sdk.VoiceAi.AudioSocket

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Build the audio processing foundation (resampling, format conversion) and AudioSocket transport (bidirectional TCP audio streaming with Asterisk) as MIT open-source packages.

**Architecture:** `Asterisk.Sdk.Audio` provides pure C# polyphase FIR resampler (12 rate pairs, zero-alloc hot path, pre-computed coefficients) + audio utilities. `Asterisk.Sdk.VoiceAi.AudioSocket` provides TCP server/client implementing Asterisk's AudioSocket protocol with System.IO.Pipelines. Both packages are MIT, AOT-safe, zero reflection.

**Tech Stack:** .NET 10 Native AOT, System.IO.Pipelines, System.Buffers, xunit, FluentAssertions

**Spec:** `docs/superpowers/specs/2026-03-19-sprint23-voiceai-audiosocket-design.md`

**Repo:** `/media/Data/Source/IPcom/Asterisk.Sdk/` (MIT SDK — these are open source packages)

**Execution strategy:** FCM
- Phase A: Audio package (AudioFormat + Resampler + AudioProcessor)
- Phase B: Critical — AudioSocket protocol (framing + server + session)
- Phase C: Integration (client + DI + round-trip tests)

---

## Phase A: Asterisk.Sdk.Audio

### Task 1: Scaffolding + AudioFormat + AudioEncoding

Create `src/Asterisk.Sdk.Audio/Asterisk.Sdk.Audio.csproj` (no dependencies — pure standalone).
Create `Tests/Asterisk.Sdk.Audio.Tests/Asterisk.Sdk.Audio.Tests.csproj`.
Update `Asterisk.Sdk.slnx`.

Create `AudioFormat.cs` — readonly record struct with SampleRate, Channels, BitsPerSample, AudioEncoding. Static presets: Slin16Mono8kHz, Slin16Mono16kHz, Slin16Mono24kHz, Slin16Mono48kHz, Float32Mono16kHz. Methods: BytesPerSample, BytesPerFrame(TimeSpan), SamplesPerFrame(TimeSpan).

Create `AudioEncoding.cs` — enum: LinearPcm, Float32.

Create `IAudioTransform.cs` — interface with InputFormat, OutputFormat, `int Process(ReadOnlySpan<byte> input, Span<byte> output)`.

Build. Commit: `feat(audio): scaffold Asterisk.Sdk.Audio with AudioFormat and IAudioTransform`

### Task 2: PolyphaseResampler + ResamplerFactory (CRITICAL)

Create `Resampling/PolyphaseResampler.cs`:
- Constructor: `(int inputRate, int outputRate)` — validates rate pair, loads pre-computed coefficients
- Delay line rented from `ArrayPool<float>.Shared`
- `int Process(ReadOnlySpan<short> input, Span<short> output)` — zero-alloc hot path
- Also implements `IAudioTransform` (byte spans, internally casts to short spans)
- `Reset()` — clears delay line
- `Dispose()` — returns pooled buffer, sets disposed flag
- Throws `ObjectDisposedException` on `Process()` after dispose

Create `Resampling/ResamplerFactory.cs`:
- `static PolyphaseResampler Create(int inputRate, int outputRate)` — throws ArgumentException if unsupported
- `static bool IsSupported(int inputRate, int outputRate)`
- `static int CalculateOutputSize(int inputSamples, int inputRate, int outputRate)`

Create `Resampling/ResamplerCoefficients.cs`:
- Pre-computed 32-tap polyphase FIR coefficients for each rate pair
- Stored as `static ReadOnlySpan<float>` (blittable, zero-alloc)
- Windowed sinc design (Kaiser window, β=8)
- Generate coefficients in the implementation — the formula is: `h[n] = sinc(n/L) * kaiser(n, β)` normalized

Tests (12):
1. `Create_ShouldSucceed_ForSupportedRatePair`
2. `Create_ShouldThrow_ForUnsupportedRatePair`
3. `IsSupported_ShouldReturnTrue_ForAllRatePairs`
4. `Process_8kTo16k_ShouldDoubleOutputSamples`
5. `Process_16kTo8k_ShouldHalveOutputSamples`
6. `Process_8kTo24k_ShouldTripleOutputSamples`
7. `Process_24kTo8k_ShouldThirdOutputSamples`
8. `Process_16kTo24k_ShouldProduceCorrectCount`
9. `Process_ShouldMaintainContinuityAcrossFrames` (stateful delay line)
10. `Process_ShouldThrow_AfterDispose`
11. `CalculateOutputSize_ShouldReturnCorrectSize`
12. `Process_ShouldNotAllocate` (use `GC.GetAllocatedBytesForCurrentThread` before/after)

Commit: `feat(audio): add PolyphaseResampler with pre-computed FIR coefficients`

### Task 3: AudioProcessor

Create `Processing/AudioProcessor.cs`:
- `static void ApplyGain(Span<short> samples, float gainDb)` — clamp to short range
- `static void ConvertToFloat32(ReadOnlySpan<short> pcm16, Span<float> float32)` — divide by 32768
- `static void ConvertToPcm16(ReadOnlySpan<float> float32, Span<short> pcm16)` — multiply by 32767, clamp
- `static double CalculateRmsEnergy(ReadOnlySpan<short> samples)` — sqrt(sum(s²)/N)
- `static bool IsSilence(ReadOnlySpan<short> samples, double thresholdDb = -40.0)` — compare RMS to threshold

Tests (6):
1. `ApplyGain_ShouldAmplify_PositiveDb`
2. `ApplyGain_ShouldAttenuate_NegativeDb`
3. `ConvertToFloat32_ShouldNormalize`
4. `ConvertToPcm16_ShouldDenormalize`
5. `CalculateRmsEnergy_ShouldReturnZero_ForSilence`
6. `IsSilence_ShouldReturnTrue_ForZeroSamples`

Commit: `feat(audio): add AudioProcessor with gain, format conversion, and silence detection`

---

## Phase B: AudioSocket Protocol + Server

### Task 4: AudioSocketFrame + Protocol Parsing

Create `src/Asterisk.Sdk.VoiceAi.AudioSocket/Asterisk.Sdk.VoiceAi.AudioSocket.csproj` (depends on Asterisk.Sdk.Audio + Extensions).
Create `Tests/Asterisk.Sdk.VoiceAi.AudioSocket.Tests/...csproj`.
Update slnx.

Create `AudioSocketFrameType.cs` — enum (Uuid=0x00, Audio=0x01, Silence=0x02, Error=0x04, Hangup=0xFF).

Create `AudioSocketFrame.cs` — readonly record struct (Type, Payload).

Create internal `AudioSocketFrameCodec.cs`:
- `static bool TryReadFrame(ref ReadOnlySequence<byte> buffer, out AudioSocketFrame frame)` — parse [1 type][3 length BE][N payload]
- `static void WriteFrame(IBufferWriter<byte> writer, AudioSocketFrameType type, ReadOnlySpan<byte> payload)` — write framed
- UUID parsing: `new Guid(span, bigEndian: true)` (.NET 9+)
- Silence frame: always consume 2-byte duration payload

Tests (5):
1. `TryReadFrame_ShouldParseUuidFrame`
2. `TryReadFrame_ShouldParseAudioFrame`
3. `TryReadFrame_ShouldParseSilenceFrame_With2BytePayload`
4. `TryReadFrame_ShouldReturnFalse_WhenIncomplete`
5. `WriteFrame_ShouldProduceValidFrame`

Commit: `feat(audiosocket): add AudioSocket protocol framing with frame codec`

### Task 5: AudioSocketServer + AudioSocketSession

Create `AudioSocketOptions.cs` — ListenAddress, Port (9092), MaxConcurrentSessions, DefaultFormat, ReceiveBufferSize, ConnectionTimeout.

Create `AudioSocketSession.cs`:
- ChannelId (Guid), RemoteEndpoint, InputFormat, IsConnected
- `IAsyncEnumerable<ReadOnlyMemory<byte>> ReadAudioAsync(ct)` — yields audio frames from PipeReader
- `ValueTask WriteAudioAsync(ReadOnlyMemory<byte> pcmData, ct)` — writes via PipeWriter
- `ValueTask WriteSilenceAsync(ct)` — writes silence frame (2-byte duration = 0)
- `ValueTask HangupAsync(ct)` — writes hangup frame
- `event Action? OnHangup`
- Internal: reads frames in a loop, filters Audio type, yields to consumer

Create `AudioSocketServer.cs : IHostedService, IAsyncDisposable`:
- `event Func<AudioSocketSession, ValueTask>? OnSessionStarted`
- `int ActiveSessionCount`
- StartAsync: bind TCP listener, accept connections in background loop
- Per-connection: read UUID frame, create AudioSocketSession, fire event
- Session tracking: `ConcurrentDictionary<Guid, AudioSocketSession>`
- StopAsync: cancel accept loop, dispose all sessions

Tests (5):
1. `Server_ShouldAcceptConnection` (use AudioSocketClient from Task 6)
2. `Session_ReadAudioAsync_ShouldYieldAudioFrames`
3. `Session_WriteAudioAsync_ShouldSendFramedData`
4. `Session_ShouldDetectHangup`
5. `Server_ActiveSessionCount_ShouldTrackConnections`

**Note:** These tests need the Client (Task 6) to drive them. Write tests but defer running until after Task 6.

Commit: `feat(audiosocket): add AudioSocketServer and AudioSocketSession`

---

## Phase C: Client + DI + Integration

### Task 6: AudioSocketClient

Create `AudioSocketClient.cs`:
- Constructor: `(string host, int port, Guid channelId)`
- `ConnectAsync(ct)` — TCP connect, send UUID frame
- `SendAudioAsync(ReadOnlyMemory<byte> pcmData, ct)` — write audio frame
- `SendHangupAsync(ct)` — write hangup frame
- `IAsyncEnumerable<ReadOnlyMemory<byte>> ReadAudioAsync(ct)` — read audio frames from server

Tests already defined in Task 5 — now they can run.

Commit: `feat(audiosocket): add AudioSocketClient for testing`

### Task 7: DI + Integration Tests

Create `DependencyInjection/AudioSocketServiceCollectionExtensions.cs`:
```csharp
services.AddAudioSocketServer(options => { options.Port = 9092; });
```

Create integration test `Integration/AudioSocketRoundTripTests.cs`:
1. `RoundTrip_ShouldEchoAudio` — server echoes received audio back to client
2. `RoundTrip_ShouldHandleMultipleSessions` — 3 concurrent sessions

Tests (3): 1 DI + 2 integration round-trips.

Commit: `feat(audiosocket): add DI registration and integration round-trip tests`

### Task 8: Final Verification

Build: `dotnet build Asterisk.Sdk.slnx` — 0 warnings
Test: `dotnet test Asterisk.Sdk.slnx` — ALL pass

---

## Summary

| Task | Phase | Component | Tests |
|------|-------|-----------|-------|
| 1 | A | AudioFormat + AudioEncoding + IAudioTransform | 0 |
| 2 | A | **PolyphaseResampler** (critical, FIR) | 12 |
| 3 | A | AudioProcessor | 6 |
| 4 | B | AudioSocket framing + codec | 5 |
| 5 | B | AudioSocketServer + Session | 5 |
| 6 | C | AudioSocketClient | 0 (tests in Task 5) |
| 7 | C | DI + integration round-trip | 3 |
| 8 | C | Final verification | 0 |
| **Total** | | | **~31** |

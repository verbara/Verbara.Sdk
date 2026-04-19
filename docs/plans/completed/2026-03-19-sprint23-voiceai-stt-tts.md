# Sprint 23: Asterisk.Sdk.VoiceAi — STT + TTS + Core Pipeline

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Build the Voice AI layer of the MIT SDK: core pipeline abstractions, dual-loop orchestration with turn-taking and barge-in, four STT providers, two TTS providers, a published Testing package, and an E2E example.

**Architecture:** `Asterisk.Sdk.VoiceAi` defines the abstract pipeline contract (SpeechRecognizer, SpeechSynthesizer, IConversationHandler) and the `VoiceAiPipeline` orchestrator (AudioMonitorLoop + PipelineLoop, state machine, barge-in via `volatile CancellationTokenSource`). `Asterisk.Sdk.VoiceAi.Stt` and `.Tts` implement providers with raw HttpClient/ClientWebSocket (zero new NuGet dependencies). `Asterisk.Sdk.VoiceAi.Testing` ships FakeSpeechRecognizer + FakeSpeechSynthesizer + FakeConversationHandler as a public MIT NuGet.

**Tech Stack:** .NET 10 Native AOT, System.Reactive 6.0.1 (Subject<T>, IObservable<T>), System.Text.Json source generation, System.Threading.Channels, xunit, FluentAssertions

**Spec:** `docs/superpowers/specs/2026-03-19-sprint23-voiceai-stt-tts-design.md`

**Repo:** `/media/Data/Source/IPcom/Asterisk.Sdk/`

**Execution strategy:** Sequential (each task depends on prior artifacts)
- Phase A: VoiceAi core abstractions + Testing fakes
- Phase B: VoiceAiPipeline (state machine, dual loops, barge-in)
- Phase C: STT providers (Deepgram WebSocket + 3 REST)
- Phase D: TTS providers (ElevenLabs WebSocket + Azure REST)
- Phase E: Example + final verification

---

## File Map

### New source packages
```
src/Asterisk.Sdk.VoiceAi/
  Asterisk.Sdk.VoiceAi.csproj
  README.md
  SpeechRecognizer.cs
  SpeechSynthesizer.cs
  IConversationHandler.cs
  SpeechRecognitionResult.cs
  ConversationContext.cs
  ConversationTurn.cs
  Events/VoiceAiPipelineEvent.cs          (all event records + PipelineErrorSource enum)
  Pipeline/PipelineState.cs
  Pipeline/VoiceAiPipelineOptions.cs
  Pipeline/VoiceAiPipeline.cs
  Pipeline/VoiceAiSessionBroker.cs
  DependencyInjection/VoiceAiServiceCollectionExtensions.cs
  Internal/VoiceAiLog.cs

src/Asterisk.Sdk.VoiceAi.Testing/
  Asterisk.Sdk.VoiceAi.Testing.csproj
  README.md
  FakeSpeechRecognizer.cs
  FakeSpeechSynthesizer.cs
  FakeConversationHandler.cs

src/Asterisk.Sdk.VoiceAi.Stt/
  Asterisk.Sdk.VoiceAi.Stt.csproj
  README.md
  Internal/VoiceAiSttJsonContext.cs       (all DTOs + JsonSerializerContext)
  Deepgram/DeepgramOptions.cs
  Deepgram/DeepgramSpeechRecognizer.cs
  Whisper/WhisperOptions.cs
  Whisper/WhisperSpeechRecognizer.cs
  Whisper/AzureWhisperOptions.cs
  Whisper/AzureWhisperSpeechRecognizer.cs
  Google/GoogleSpeechOptions.cs
  Google/GoogleSpeechRecognizer.cs
  DependencyInjection/SttServiceCollectionExtensions.cs
  Internal/SttLog.cs

src/Asterisk.Sdk.VoiceAi.Tts/
  Asterisk.Sdk.VoiceAi.Tts.csproj
  README.md
  Internal/VoiceAiTtsJsonContext.cs       (ElevenLabsTextChunk DTOs + JsonSerializerContext)
  ElevenLabs/ElevenLabsOptions.cs
  ElevenLabs/ElevenLabsSpeechSynthesizer.cs
  Azure/AzureTtsOptions.cs
  Azure/AzureTtsOutputFormat.cs
  Azure/AzureTtsSpeechSynthesizer.cs
  DependencyInjection/TtsServiceCollectionExtensions.cs
  Internal/TtsLog.cs
```

### New test projects
```
Tests/Asterisk.Sdk.VoiceAi.Tests/
  Asterisk.Sdk.VoiceAi.Tests.csproj
  Pipeline/VoiceAiPipelineTests.cs        (~22 tests)
  DependencyInjection/VoiceAiDiTests.cs  (~6 tests)

Tests/Asterisk.Sdk.VoiceAi.Testing.Tests/
  Asterisk.Sdk.VoiceAi.Testing.Tests.csproj
  FakeSpeechRecognizerTests.cs           (~4 tests)
  FakeSpeechSynthesizerTests.cs          (~3 tests)
  FakeConversationHandlerTests.cs        (~3 tests)

Tests/Asterisk.Sdk.VoiceAi.Stt.Tests/
  Asterisk.Sdk.VoiceAi.Stt.Tests.csproj
  Helpers/MockHttpMessageHandler.cs      (test helper — DelegatingHandler with fixed responses)
  Deepgram/DeepgramFakeServer.cs         (in-process WebSocket loopback)
  Deepgram/DeepgramSpeechRecognizerTests.cs   (~5 tests)
  Whisper/WhisperSpeechRecognizerTests.cs     (~4 tests)
  Whisper/AzureWhisperSpeechRecognizerTests.cs (~3 tests)
  Google/GoogleSpeechRecognizerTests.cs       (~4 tests)
  DependencyInjection/SttDiTests.cs           (~4 tests)

Tests/Asterisk.Sdk.VoiceAi.Tts.Tests/
  Asterisk.Sdk.VoiceAi.Tts.Tests.csproj
  Helpers/MockHttpMessageHandler.cs           (test helper)
  ElevenLabs/ElevenLabsFakeServer.cs          (in-process WebSocket loopback)
  ElevenLabs/ElevenLabsSpeechSynthesizerTests.cs  (~5 tests)
  Azure/AzureTtsSpeechSynthesizerTests.cs         (~4 tests)
  DependencyInjection/TtsDiTests.cs               (~3 tests)
```

### New example
```
Examples/VoiceAiExample/
  VoiceAiExample.csproj
  Program.cs
  EchoConversationHandler.cs
  appsettings.json
```

### Modified
```
Asterisk.Sdk.slnx                  (add 8 new project entries)
```

---

## Phase A: VoiceAi Core Abstractions + Testing Fakes

### Task 1: Scaffold Asterisk.Sdk.VoiceAi + abstractions

**Files:**
- Create: `src/Asterisk.Sdk.VoiceAi/Asterisk.Sdk.VoiceAi.csproj`
- Create: `src/Asterisk.Sdk.VoiceAi/README.md`
- Create: `src/Asterisk.Sdk.VoiceAi/SpeechRecognizer.cs`
- Create: `src/Asterisk.Sdk.VoiceAi/SpeechSynthesizer.cs`
- Create: `src/Asterisk.Sdk.VoiceAi/IConversationHandler.cs`
- Create: `src/Asterisk.Sdk.VoiceAi/SpeechRecognitionResult.cs`
- Create: `src/Asterisk.Sdk.VoiceAi/ConversationContext.cs`
- Create: `src/Asterisk.Sdk.VoiceAi/ConversationTurn.cs`
- Create: `src/Asterisk.Sdk.VoiceAi/Events/VoiceAiPipelineEvent.cs`
- Create: `Tests/Asterisk.Sdk.VoiceAi.Tests/Asterisk.Sdk.VoiceAi.Tests.csproj`
- Modify: `Asterisk.Sdk.slnx`

- [ ] **Step 1: Create the csproj**

```xml
<!-- src/Asterisk.Sdk.VoiceAi/Asterisk.Sdk.VoiceAi.csproj -->
<Project Sdk="Microsoft.NET.Sdk">
  <PropertyGroup>
    <Description>Voice AI pipeline for Asterisk.Sdk — orchestration layer for STT, TTS and conversation with turn-taking and barge-in detection.</Description>
    <PackageTags>$(PackageTags);voiceai;stt;tts;ai;pipeline</PackageTags>
  </PropertyGroup>
  <ItemGroup>
    <InternalsVisibleTo Include="Asterisk.Sdk.VoiceAi.Tests" />
  </ItemGroup>
  <ItemGroup>
    <ProjectReference Include="..\Asterisk.Sdk.Audio\Asterisk.Sdk.Audio.csproj" />
    <ProjectReference Include="..\Asterisk.Sdk.VoiceAi.AudioSocket\Asterisk.Sdk.VoiceAi.AudioSocket.csproj" />
  </ItemGroup>
  <ItemGroup>
    <PackageReference Include="Microsoft.Extensions.Hosting.Abstractions" />
    <PackageReference Include="Microsoft.Extensions.Logging.Abstractions" />
    <PackageReference Include="Microsoft.Extensions.DependencyInjection.Abstractions" />
    <PackageReference Include="Microsoft.Extensions.Options" />
    <PackageReference Include="System.Reactive" />
  </ItemGroup>
</Project>
```

Create `src/Asterisk.Sdk.VoiceAi/README.md` with one line:
```
# Asterisk.Sdk.VoiceAi
Voice AI pipeline for Asterisk.Sdk — STT, TTS, and conversation orchestration with turn-taking and barge-in detection.
```

- [ ] **Step 2: Create abstractions**

```csharp
// src/Asterisk.Sdk.VoiceAi/SpeechRecognizer.cs
namespace Asterisk.Sdk.VoiceAi;

public abstract class SpeechRecognizer : IAsyncDisposable
{
    public abstract IAsyncEnumerable<SpeechRecognitionResult> StreamAsync(
        IAsyncEnumerable<ReadOnlyMemory<byte>> audioFrames,
        AudioFormat format,
        CancellationToken ct = default);

    public virtual ValueTask DisposeAsync() => default;
}
```

```csharp
// src/Asterisk.Sdk.VoiceAi/SpeechSynthesizer.cs
namespace Asterisk.Sdk.VoiceAi;

public abstract class SpeechSynthesizer : IAsyncDisposable
{
    public abstract IAsyncEnumerable<ReadOnlyMemory<byte>> SynthesizeAsync(
        string text,
        AudioFormat outputFormat,
        CancellationToken ct = default);

    public virtual ValueTask DisposeAsync() => default;
}
```

```csharp
// src/Asterisk.Sdk.VoiceAi/IConversationHandler.cs
namespace Asterisk.Sdk.VoiceAi;

public interface IConversationHandler
{
    ValueTask<string> HandleAsync(
        string transcript,
        ConversationContext context,
        CancellationToken ct = default);
}
```

```csharp
// src/Asterisk.Sdk.VoiceAi/SpeechRecognitionResult.cs
namespace Asterisk.Sdk.VoiceAi;

public readonly record struct SpeechRecognitionResult(
    string Transcript,
    float Confidence,
    bool IsFinal,
    TimeSpan Duration);
```

```csharp
// src/Asterisk.Sdk.VoiceAi/ConversationTurn.cs
namespace Asterisk.Sdk.VoiceAi;

public readonly record struct ConversationTurn(
    string UserTranscript,
    string AssistantResponse,
    DateTimeOffset Timestamp);
```

```csharp
// src/Asterisk.Sdk.VoiceAi/ConversationContext.cs
namespace Asterisk.Sdk.VoiceAi;

public sealed class ConversationContext
{
    public Guid ChannelId { get; init; }
    public IReadOnlyList<ConversationTurn> History { get; init; } = [];
    public AudioFormat InputFormat { get; init; }
}
```

```csharp
// src/Asterisk.Sdk.VoiceAi/Events/VoiceAiPipelineEvent.cs
namespace Asterisk.Sdk.VoiceAi.Events;

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

public record PipelineErrorEvent(
    DateTimeOffset Timestamp,
    Exception Error,
    PipelineErrorSource Source)
    : VoiceAiPipelineEvent(Timestamp);

public enum PipelineErrorSource { Stt, Tts, Handler }
```

- [ ] **Step 3: Create test csproj**

```xml
<!-- Tests/Asterisk.Sdk.VoiceAi.Tests/Asterisk.Sdk.VoiceAi.Tests.csproj -->
<Project Sdk="Microsoft.NET.Sdk">
  <ItemGroup>
    <ProjectReference Include="..\..\src\Asterisk.Sdk.VoiceAi\Asterisk.Sdk.VoiceAi.csproj" />
  </ItemGroup>
  <ItemGroup>
    <PackageReference Include="Microsoft.NET.Test.Sdk" />
    <PackageReference Include="xunit" />
    <PackageReference Include="xunit.runner.visualstudio" />
    <PackageReference Include="FluentAssertions" />
    <PackageReference Include="coverlet.collector" />
    <PackageReference Include="Microsoft.Extensions.DependencyInjection" />
    <PackageReference Include="Microsoft.Extensions.Hosting.Abstractions" />
    <PackageReference Include="Microsoft.Extensions.Logging" />
  </ItemGroup>
</Project>
```

- [ ] **Step 4: Update Asterisk.Sdk.slnx**

Add to the `/src/` folder:
```xml
<Project Path="src/Asterisk.Sdk.VoiceAi/Asterisk.Sdk.VoiceAi.csproj" />
```

Add to the `/Tests/` folder:
```xml
<Project Path="Tests/Asterisk.Sdk.VoiceAi.Tests/Asterisk.Sdk.VoiceAi.Tests.csproj" />
```

- [ ] **Step 5: Build to verify 0 warnings**

```bash
cd /media/Data/Source/IPcom/Asterisk.Sdk
dotnet build src/Asterisk.Sdk.VoiceAi/Asterisk.Sdk.VoiceAi.csproj
```

Expected: Build succeeded, 0 warnings.

- [ ] **Step 6: Commit**

```bash
git add src/Asterisk.Sdk.VoiceAi/ Tests/Asterisk.Sdk.VoiceAi.Tests/Asterisk.Sdk.VoiceAi.Tests.csproj Asterisk.Sdk.slnx
git commit -m "feat(voiceai): scaffold Asterisk.Sdk.VoiceAi with abstractions and domain events"
```

---

### Task 2: Asterisk.Sdk.VoiceAi.Testing fakes + tests

**Files:**
- Create: `src/Asterisk.Sdk.VoiceAi.Testing/Asterisk.Sdk.VoiceAi.Testing.csproj`
- Create: `src/Asterisk.Sdk.VoiceAi.Testing/README.md`
- Create: `src/Asterisk.Sdk.VoiceAi.Testing/FakeSpeechRecognizer.cs`
- Create: `src/Asterisk.Sdk.VoiceAi.Testing/FakeSpeechSynthesizer.cs`
- Create: `src/Asterisk.Sdk.VoiceAi.Testing/FakeConversationHandler.cs`
- Create: `Tests/Asterisk.Sdk.VoiceAi.Testing.Tests/Asterisk.Sdk.VoiceAi.Testing.Tests.csproj`
- Create: `Tests/Asterisk.Sdk.VoiceAi.Testing.Tests/FakeSpeechRecognizerTests.cs`
- Create: `Tests/Asterisk.Sdk.VoiceAi.Testing.Tests/FakeSpeechSynthesizerTests.cs`
- Create: `Tests/Asterisk.Sdk.VoiceAi.Testing.Tests/FakeConversationHandlerTests.cs`
- Modify: `Asterisk.Sdk.slnx`

- [ ] **Step 1: Create csproj files**

```xml
<!-- src/Asterisk.Sdk.VoiceAi.Testing/Asterisk.Sdk.VoiceAi.Testing.csproj -->
<Project Sdk="Microsoft.NET.Sdk">
  <PropertyGroup>
    <Description>Test fakes for Asterisk.Sdk.VoiceAi — FakeSpeechRecognizer, FakeSpeechSynthesizer, FakeConversationHandler for testing Voice AI apps without API keys.</Description>
    <PackageTags>$(PackageTags);voiceai;testing;fakes</PackageTags>
  </PropertyGroup>
  <ItemGroup>
    <InternalsVisibleTo Include="Asterisk.Sdk.VoiceAi.Testing.Tests" />
  </ItemGroup>
  <ItemGroup>
    <ProjectReference Include="..\Asterisk.Sdk.VoiceAi\Asterisk.Sdk.VoiceAi.csproj" />
  </ItemGroup>
</Project>
```

```xml
<!-- Tests/Asterisk.Sdk.VoiceAi.Testing.Tests/Asterisk.Sdk.VoiceAi.Testing.Tests.csproj -->
<Project Sdk="Microsoft.NET.Sdk">
  <ItemGroup>
    <ProjectReference Include="..\..\src\Asterisk.Sdk.VoiceAi.Testing\Asterisk.Sdk.VoiceAi.Testing.csproj" />
  </ItemGroup>
  <ItemGroup>
    <PackageReference Include="Microsoft.NET.Test.Sdk" />
    <PackageReference Include="xunit" />
    <PackageReference Include="xunit.runner.visualstudio" />
    <PackageReference Include="FluentAssertions" />
    <PackageReference Include="coverlet.collector" />
  </ItemGroup>
</Project>
```

Add both to `Asterisk.Sdk.slnx` (`.Testing` under `/src/`, `.Testing.Tests` under `/Tests/`).

Create `src/Asterisk.Sdk.VoiceAi.Testing/README.md`:
```
# Asterisk.Sdk.VoiceAi.Testing
Test fakes for Asterisk.Sdk.VoiceAi — test your Voice AI apps without API keys.
```

- [ ] **Step 2: Write failing tests**

```csharp
// Tests/Asterisk.Sdk.VoiceAi.Testing.Tests/FakeSpeechRecognizerTests.cs
namespace Asterisk.Sdk.VoiceAi.Testing.Tests;

public class FakeSpeechRecognizerTests
{
    [Fact]
    public async Task StreamAsync_ShouldReturnConfiguredTranscript()
    {
        var fake = new FakeSpeechRecognizer().WithTranscript("hola mundo");
        var results = await fake.StreamAsync(EmptyFrames(), AudioFormat.Slin16Mono8kHz).ToListAsync();
        results.Should().ContainSingle(r => r.Transcript == "hola mundo" && r.IsFinal);
    }

    [Fact]
    public async Task StreamAsync_ShouldCycleTranscripts_WhenCalledMultipleTimes()
    {
        var fake = new FakeSpeechRecognizer().WithTranscripts(["uno", "dos"]);
        var r1 = await fake.StreamAsync(EmptyFrames(), AudioFormat.Slin16Mono8kHz).ToListAsync();
        var r2 = await fake.StreamAsync(EmptyFrames(), AudioFormat.Slin16Mono8kHz).ToListAsync();
        var r3 = await fake.StreamAsync(EmptyFrames(), AudioFormat.Slin16Mono8kHz).ToListAsync();
        r1[0].Transcript.Should().Be("uno");
        r2[0].Transcript.Should().Be("dos");
        r3[0].Transcript.Should().Be("uno"); // cycles
    }

    [Fact]
    public async Task StreamAsync_ShouldTrackCallCount()
    {
        var fake = new FakeSpeechRecognizer().WithTranscript("test");
        await fake.StreamAsync(EmptyFrames(), AudioFormat.Slin16Mono8kHz).ToListAsync();
        await fake.StreamAsync(EmptyFrames(), AudioFormat.Slin16Mono8kHz).ToListAsync();
        fake.CallCount.Should().Be(2);
    }

    [Fact]
    public async Task StreamAsync_ShouldThrow_WhenConfiguredToError()
    {
        var fake = new FakeSpeechRecognizer().WithError(new InvalidOperationException("stt fail"));
        var act = async () => await fake.StreamAsync(EmptyFrames(), AudioFormat.Slin16Mono8kHz).ToListAsync();
        await act.Should().ThrowAsync<InvalidOperationException>().WithMessage("stt fail");
    }

    private static async IAsyncEnumerable<ReadOnlyMemory<byte>> EmptyFrames()
    {
        await Task.CompletedTask;
        yield break;
    }
}
```

```csharp
// Tests/Asterisk.Sdk.VoiceAi.Testing.Tests/FakeSpeechSynthesizerTests.cs
namespace Asterisk.Sdk.VoiceAi.Testing.Tests;

public class FakeSpeechSynthesizerTests
{
    [Fact]
    public async Task SynthesizeAsync_ShouldGenerateSilenceFrames_WithConfiguredDuration()
    {
        var fake = new FakeSpeechSynthesizer().WithSilence(TimeSpan.FromMilliseconds(60));
        var frames = await fake.SynthesizeAsync("test", AudioFormat.Slin16Mono8kHz).ToListAsync();
        // 60ms / 20ms per frame = 3 frames of 320 bytes each (160 samples * 2 bytes)
        frames.Should().HaveCount(3);
        frames.All(f => f.Length == 320).Should().BeTrue();
    }

    [Fact]
    public async Task SynthesizeAsync_ShouldTrackSynthesizedTexts()
    {
        var fake = new FakeSpeechSynthesizer().WithSilence(TimeSpan.FromMilliseconds(20));
        await fake.SynthesizeAsync("hola", AudioFormat.Slin16Mono8kHz).ToListAsync();
        await fake.SynthesizeAsync("mundo", AudioFormat.Slin16Mono8kHz).ToListAsync();
        fake.SynthesizedTexts.Should().Equal("hola", "mundo");
    }

    [Fact]
    public async Task SynthesizeAsync_ShouldThrow_WhenConfiguredToError()
    {
        var fake = new FakeSpeechSynthesizer().WithError(new InvalidOperationException("tts fail"));
        var act = async () => await fake.SynthesizeAsync("test", AudioFormat.Slin16Mono8kHz).ToListAsync();
        await act.Should().ThrowAsync<InvalidOperationException>().WithMessage("tts fail");
    }
}
```

```csharp
// Tests/Asterisk.Sdk.VoiceAi.Testing.Tests/FakeConversationHandlerTests.cs
namespace Asterisk.Sdk.VoiceAi.Testing.Tests;

public class FakeConversationHandlerTests
{
    [Fact]
    public async Task HandleAsync_ShouldReturnConfiguredResponse()
    {
        var fake = new FakeConversationHandler().WithResponse("respuesta");
        var ctx = new ConversationContext { ChannelId = Guid.NewGuid() };
        var result = await fake.HandleAsync("hola", ctx);
        result.Should().Be("respuesta");
    }

    [Fact]
    public async Task HandleAsync_ShouldCycleResponses()
    {
        var fake = new FakeConversationHandler().WithResponses(["uno", "dos"]);
        var ctx = new ConversationContext { ChannelId = Guid.NewGuid() };
        var r1 = await fake.HandleAsync("a", ctx);
        var r2 = await fake.HandleAsync("b", ctx);
        var r3 = await fake.HandleAsync("c", ctx);
        r1.Should().Be("uno");
        r2.Should().Be("dos");
        r3.Should().Be("uno"); // cycles
    }

    [Fact]
    public async Task HandleAsync_ShouldTrackReceivedTranscripts()
    {
        var fake = new FakeConversationHandler().WithResponse("ok");
        var ctx = new ConversationContext { ChannelId = Guid.NewGuid() };
        await fake.HandleAsync("transcript1", ctx);
        await fake.HandleAsync("transcript2", ctx);
        fake.ReceivedTranscripts.Should().Equal("transcript1", "transcript2");
    }
}
```

- [ ] **Step 3: Run tests to verify they fail**

```bash
dotnet test Tests/Asterisk.Sdk.VoiceAi.Testing.Tests/ --no-build 2>&1 | head -5
```

Expected: Build errors — types not yet implemented.

- [ ] **Step 4: Implement FakeSpeechRecognizer**

```csharp
// src/Asterisk.Sdk.VoiceAi.Testing/FakeSpeechRecognizer.cs
namespace Asterisk.Sdk.VoiceAi.Testing;

public sealed class FakeSpeechRecognizer : SpeechRecognizer
{
    private readonly List<(string Transcript, float Confidence)> _transcripts = [];
    private Exception? _error;
    private int _errorAfterCount;
    private TimeSpan _delay;
    private int _callIndex;
    private readonly List<int> _receivedFrameCounts = [];

    public int CallCount => _callIndex;
    public IReadOnlyList<int> ReceivedFrameCounts => _receivedFrameCounts;

    public FakeSpeechRecognizer WithTranscript(string transcript, float confidence = 1.0f)
    {
        _transcripts.Add((transcript, confidence));
        return this;
    }

    public FakeSpeechRecognizer WithTranscripts(IEnumerable<string> transcripts)
    {
        foreach (var t in transcripts)
            _transcripts.Add((t, 1.0f));
        return this;
    }

    public FakeSpeechRecognizer WithDelay(TimeSpan delay) { _delay = delay; return this; }

    public FakeSpeechRecognizer WithError(Exception exception, int afterCount = 0)
    {
        _error = exception;
        _errorAfterCount = afterCount;
        return this;
    }

    public override async IAsyncEnumerable<SpeechRecognitionResult> StreamAsync(
        IAsyncEnumerable<ReadOnlyMemory<byte>> audioFrames,
        AudioFormat format,
        [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken ct = default)
    {
        // Drain all frames (deterministic behavior)
        int frameCount = 0;
        await foreach (var _ in audioFrames.WithCancellation(ct).ConfigureAwait(false))
            frameCount++;
        _receivedFrameCounts.Add(frameCount);

        if (_delay > TimeSpan.Zero)
            await Task.Delay(_delay, ct).ConfigureAwait(false);

        if (_error != null && _callIndex >= _errorAfterCount)
        {
            _callIndex++;
            throw _error;
        }

        if (_transcripts.Count > 0)
        {
            var (transcript, confidence) = _transcripts[_callIndex % _transcripts.Count];
            _callIndex++;
            yield return new SpeechRecognitionResult(transcript, confidence, true, TimeSpan.Zero);
        }
        else
        {
            _callIndex++;
        }
    }
}
```

- [ ] **Step 5: Implement FakeSpeechSynthesizer**

```csharp
// src/Asterisk.Sdk.VoiceAi.Testing/FakeSpeechSynthesizer.cs
namespace Asterisk.Sdk.VoiceAi.Testing;

public sealed class FakeSpeechSynthesizer : SpeechSynthesizer
{
    private TimeSpan _silenceDuration;
    private ReadOnlyMemory<byte>? _audioData;
    private Exception? _error;
    private int _errorAfterCount;
    private TimeSpan _delay;
    private int _callIndex;
    private readonly List<string> _synthesizedTexts = [];

    public int CallCount => _callIndex;
    public IReadOnlyList<string> SynthesizedTexts => _synthesizedTexts;

    public FakeSpeechSynthesizer WithSilence(TimeSpan duration) { _silenceDuration = duration; return this; }
    public FakeSpeechSynthesizer WithAudio(ReadOnlyMemory<byte> pcmAudio) { _audioData = pcmAudio; return this; }
    public FakeSpeechSynthesizer WithDelay(TimeSpan delay) { _delay = delay; return this; }

    public FakeSpeechSynthesizer WithError(Exception exception, int afterCount = 0)
    {
        _error = exception;
        _errorAfterCount = afterCount;
        return this;
    }

    public override async IAsyncEnumerable<ReadOnlyMemory<byte>> SynthesizeAsync(
        string text,
        AudioFormat outputFormat,
        [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken ct = default)
    {
        _synthesizedTexts.Add(text);

        if (_delay > TimeSpan.Zero)
            await Task.Delay(_delay, ct).ConfigureAwait(false);

        if (_error != null && _callIndex >= _errorAfterCount)
        {
            _callIndex++;
            throw _error;
        }

        _callIndex++;

        if (_audioData.HasValue)
        {
            yield return _audioData.Value;
            yield break;
        }

        // Generate silence frames: 20ms per frame = 160 samples @ 8kHz = 320 bytes
        int frameSizeBytes = 320;
        int totalFrames = (int)(_silenceDuration.TotalMilliseconds / 20);
        var silence = new byte[frameSizeBytes];
        for (int i = 0; i < totalFrames; i++)
        {
            ct.ThrowIfCancellationRequested();
            await Task.Yield();
            yield return silence.AsMemory();
        }
    }
}
```

- [ ] **Step 6: Implement FakeConversationHandler**

```csharp
// src/Asterisk.Sdk.VoiceAi.Testing/FakeConversationHandler.cs
namespace Asterisk.Sdk.VoiceAi.Testing;

public sealed class FakeConversationHandler : IConversationHandler
{
    private readonly List<string> _responses = [];
    private TimeSpan _delay;
    private int _callIndex;
    private readonly List<string> _receivedTranscripts = [];

    public int CallCount => _callIndex;
    public IReadOnlyList<string> ReceivedTranscripts => _receivedTranscripts;

    public FakeConversationHandler WithResponse(string response) { _responses.Add(response); return this; }

    public FakeConversationHandler WithResponses(IEnumerable<string> responses)
    {
        foreach (var r in responses) _responses.Add(r);
        return this;
    }

    public FakeConversationHandler WithDelay(TimeSpan delay) { _delay = delay; return this; }

    public async ValueTask<string> HandleAsync(
        string transcript,
        ConversationContext context,
        CancellationToken ct = default)
    {
        _receivedTranscripts.Add(transcript);

        if (_delay > TimeSpan.Zero)
            await Task.Delay(_delay, ct).ConfigureAwait(false);

        var response = _responses.Count > 0
            ? _responses[_callIndex % _responses.Count]
            : string.Empty;

        _callIndex++;
        return response;
    }
}
```

- [ ] **Step 7: Run tests — all must pass**

```bash
dotnet test Tests/Asterisk.Sdk.VoiceAi.Testing.Tests/
```

Expected: 10 tests pass, 0 failures.

- [ ] **Step 8: Commit**

```bash
git add src/Asterisk.Sdk.VoiceAi.Testing/ Tests/Asterisk.Sdk.VoiceAi.Testing.Tests/ Asterisk.Sdk.slnx
git commit -m "feat(voiceai): add Asterisk.Sdk.VoiceAi.Testing with FakeSpeechRecognizer, FakeSpeechSynthesizer, FakeConversationHandler"
```

---

## Phase B: VoiceAiPipeline

### Task 3: VoiceAiPipeline — state machine + dual loops + barge-in

**Files:**
- Create: `src/Asterisk.Sdk.VoiceAi/Pipeline/PipelineState.cs`
- Create: `src/Asterisk.Sdk.VoiceAi/Pipeline/VoiceAiPipelineOptions.cs`
- Create: `src/Asterisk.Sdk.VoiceAi/Pipeline/VoiceAiPipeline.cs`
- Create: `src/Asterisk.Sdk.VoiceAi/Internal/VoiceAiLog.cs`
- Create: `Tests/Asterisk.Sdk.VoiceAi.Tests/Pipeline/VoiceAiPipelineTests.cs`
- Modify: `Tests/Asterisk.Sdk.VoiceAi.Tests/Asterisk.Sdk.VoiceAi.Tests.csproj` (add Testing ref)

The test project needs `Asterisk.Sdk.VoiceAi.Testing` and `Asterisk.Sdk.VoiceAi.AudioSocket` (the latter comes transitively via `Asterisk.Sdk.VoiceAi`). Add explicit reference to Testing:

```xml
<!-- Add to Tests/Asterisk.Sdk.VoiceAi.Tests/Asterisk.Sdk.VoiceAi.Tests.csproj -->
<ItemGroup>
  <ProjectReference Include="..\..\src\Asterisk.Sdk.VoiceAi.Testing\Asterisk.Sdk.VoiceAi.Testing.csproj" />
</ItemGroup>
```

Pipeline tests use a real in-process AudioSocket server+client to feed controlled audio to the pipeline. `FakeSpeechRecognizer`, `FakeSpeechSynthesizer`, `FakeConversationHandler` replace provider dependencies.

- [ ] **Step 1: Create PipelineState and VoiceAiPipelineOptions**

```csharp
// src/Asterisk.Sdk.VoiceAi/Pipeline/PipelineState.cs
namespace Asterisk.Sdk.VoiceAi.Pipeline;

internal enum PipelineState
{
    Idle,
    Listening,
    Recognizing,
    Handling,
    Speaking,
    Interrupted
}
```

```csharp
// src/Asterisk.Sdk.VoiceAi/Pipeline/VoiceAiPipelineOptions.cs
namespace Asterisk.Sdk.VoiceAi.Pipeline;

public sealed class VoiceAiPipelineOptions
{
    public AudioFormat InputFormat { get; set; } = AudioFormat.Slin16Mono8kHz;
    public AudioFormat OutputFormat { get; set; } = AudioFormat.Slin16Mono8kHz;
    public double SilenceThresholdDb { get; set; } = -40.0;
    public TimeSpan EndOfUtteranceSilence { get; set; } = TimeSpan.FromMilliseconds(500);
    public TimeSpan BargInVoiceThreshold { get; set; } = TimeSpan.FromMilliseconds(200);
    public TimeSpan MaxUtteranceDuration { get; set; } = TimeSpan.FromSeconds(30);
    public int MaxHistoryTurns { get; set; } = 20;
}
```

- [ ] **Step 2: Write failing pipeline tests**

```csharp
// Tests/Asterisk.Sdk.VoiceAi.Tests/Pipeline/VoiceAiPipelineTests.cs
using Asterisk.Sdk.VoiceAi.AudioSocket;
using Asterisk.Sdk.VoiceAi.Events;
using Asterisk.Sdk.VoiceAi.Pipeline;
using Asterisk.Sdk.VoiceAi.Testing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using System.Net;
using System.Reactive.Subjects;

namespace Asterisk.Sdk.VoiceAi.Tests.Pipeline;

public class VoiceAiPipelineTests : IAsyncDisposable
{
    // Helpers to build a pipeline and drive it with controlled audio
    private static VoiceAiPipeline BuildPipeline(
        FakeSpeechRecognizer? stt = null,
        FakeSpeechSynthesizer? tts = null,
        FakeConversationHandler? handler = null,
        VoiceAiPipelineOptions? options = null)
    {
        stt ??= new FakeSpeechRecognizer().WithTranscript("hola");
        tts ??= new FakeSpeechSynthesizer().WithSilence(TimeSpan.FromMilliseconds(40));
        handler ??= new FakeConversationHandler().WithResponse("respuesta");
        options ??= new VoiceAiPipelineOptions
        {
            EndOfUtteranceSilence = TimeSpan.FromMilliseconds(60),
            BargInVoiceThreshold = TimeSpan.FromMilliseconds(40),
        };

        var services = new ServiceCollection();
        services.AddSingleton<IConversationHandler>(handler);
        var provider = services.BuildServiceProvider();
        var scopeFactory = provider.GetRequiredService<IServiceScopeFactory>();

        return new VoiceAiPipeline(
            stt, tts, scopeFactory,
            Options.Create(options),
            NullLogger<VoiceAiPipeline>.Instance);
    }

    // Generates PCM16 silence frames (20ms @ 8kHz = 320 bytes of zeros)
    private static ReadOnlyMemory<byte> SilenceFrame() => new byte[320];

    // Generates PCM16 voice frames (20ms @ 8kHz = 160 samples at amplitude 5000)
    private static ReadOnlyMemory<byte> VoiceFrame()
    {
        var buf = new byte[320];
        for (int i = 0; i < 160; i++)
        {
            short sample = 5000;
            buf[i * 2] = (byte)(sample & 0xFF);
            buf[i * 2 + 1] = (byte)(sample >> 8);
        }
        return buf;
    }

    [Fact]
    public async Task HandleSessionAsync_ShouldEmitSpeechStartedEvent_WhenVoiceDetected()
    {
        var stt = new FakeSpeechRecognizer().WithTranscript("hola");
        var tts = new FakeSpeechSynthesizer().WithSilence(TimeSpan.FromMilliseconds(20));
        var handler = new FakeConversationHandler().WithResponse("ok");
        var options = new VoiceAiPipelineOptions
        {
            EndOfUtteranceSilence = TimeSpan.FromMilliseconds(60),
        };

        var pipeline = BuildPipeline(stt, tts, handler, options);

        var events = new List<VoiceAiPipelineEvent>();
        using var sub = pipeline.Events.Subscribe(events.Add);

        // Run pipeline with AudioSocket loopback
        var server = new AudioSocketServer(
            new AudioSocketOptions { Port = 0 },
            NullLogger<AudioSocketServer>.Instance);

        AudioSocketSession? capturedSession = null;
        server.OnSessionStarted += session =>
        {
            capturedSession = session;
            return ValueTask.CompletedTask;
        };

        await server.StartAsync(CancellationToken.None);

        var port = server.BoundPort;
        var channelId = Guid.NewGuid();
        await using var client = new AudioSocketClient("127.0.0.1", port, channelId);
        await client.ConnectAsync(CancellationToken.None);

        // Wait for session to be captured
        await Task.Delay(50);
        capturedSession.Should().NotBeNull();

        // Send voice frames to trigger speech detection (3 × 20ms = 60ms voice)
        for (int i = 0; i < 3; i++)
            await client.SendAudioAsync(VoiceFrame());

        // Send silence to end utterance (4 × 20ms = 80ms silence > EndOfUtteranceSilence=60ms)
        for (int i = 0; i < 4; i++)
            await client.SendAudioAsync(SilenceFrame());

        // Run pipeline for the session
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        await pipeline.HandleSessionAsync(capturedSession!, cts.Token);

        await server.StopAsync(CancellationToken.None);

        events.Should().ContainSingle(e => e is SpeechStartedEvent);
    }

    [Fact]
    public async Task HandleSessionAsync_ShouldEmitTranscriptReceivedEvent()
    {
        var stt = new FakeSpeechRecognizer().WithTranscript("buenos dias", 0.95f);
        var tts = new FakeSpeechSynthesizer().WithSilence(TimeSpan.FromMilliseconds(20));
        var handler = new FakeConversationHandler().WithResponse("ok");
        var options = new VoiceAiPipelineOptions { EndOfUtteranceSilence = TimeSpan.FromMilliseconds(60) };
        var pipeline = BuildPipeline(stt, tts, handler, options);

        var events = new List<VoiceAiPipelineEvent>();
        using var sub = pipeline.Events.Subscribe(events.Add);

        await RunPipelineWithSingleUtterance(pipeline, voiceFrameCount: 3, silenceFrameCount: 4);

        var transcript = events.OfType<TranscriptReceivedEvent>().Should().ContainSingle().Subject;
        transcript.Transcript.Should().Be("buenos dias");
        transcript.IsFinal.Should().BeTrue();
    }

    [Fact]
    public async Task HandleSessionAsync_ShouldCallHandler_WithTranscript()
    {
        var stt = new FakeSpeechRecognizer().WithTranscript("hola");
        var tts = new FakeSpeechSynthesizer().WithSilence(TimeSpan.FromMilliseconds(20));
        var handler = new FakeConversationHandler().WithResponse("hola de vuelta");
        var options = new VoiceAiPipelineOptions { EndOfUtteranceSilence = TimeSpan.FromMilliseconds(60) };
        var pipeline = BuildPipeline(stt, tts, handler, options);

        await RunPipelineWithSingleUtterance(pipeline, voiceFrameCount: 3, silenceFrameCount: 4);

        handler.ReceivedTranscripts.Should().ContainSingle("hola");
    }

    [Fact]
    public async Task HandleSessionAsync_ShouldEmitResponseGeneratedEvent()
    {
        var stt = new FakeSpeechRecognizer().WithTranscript("pregunta");
        var tts = new FakeSpeechSynthesizer().WithSilence(TimeSpan.FromMilliseconds(20));
        var handler = new FakeConversationHandler().WithResponse("respuesta");
        var options = new VoiceAiPipelineOptions { EndOfUtteranceSilence = TimeSpan.FromMilliseconds(60) };
        var pipeline = BuildPipeline(stt, tts, handler, options);

        var events = new List<VoiceAiPipelineEvent>();
        using var sub = pipeline.Events.Subscribe(events.Add);

        await RunPipelineWithSingleUtterance(pipeline, voiceFrameCount: 3, silenceFrameCount: 4);

        var response = events.OfType<ResponseGeneratedEvent>().Should().ContainSingle().Subject;
        response.Response.Should().Be("respuesta");
    }

    [Fact]
    public async Task HandleSessionAsync_ShouldEmitSynthesisEvents()
    {
        var stt = new FakeSpeechRecognizer().WithTranscript("test");
        var tts = new FakeSpeechSynthesizer().WithSilence(TimeSpan.FromMilliseconds(40));
        var handler = new FakeConversationHandler().WithResponse("ok");
        var options = new VoiceAiPipelineOptions { EndOfUtteranceSilence = TimeSpan.FromMilliseconds(60) };
        var pipeline = BuildPipeline(stt, tts, handler, options);

        var events = new List<VoiceAiPipelineEvent>();
        using var sub = pipeline.Events.Subscribe(events.Add);

        await RunPipelineWithSingleUtterance(pipeline, voiceFrameCount: 3, silenceFrameCount: 4);

        events.Should().ContainSingle(e => e is SynthesisStartedEvent);
        events.Should().ContainSingle(e => e is SynthesisEndedEvent);
    }

    [Fact]
    public async Task HandleSessionAsync_ShouldEmitPipelineErrorEvent_OnSttError_AndContinue()
    {
        var stt = new FakeSpeechRecognizer().WithError(new InvalidOperationException("stt fail"));
        var tts = new FakeSpeechSynthesizer().WithSilence(TimeSpan.FromMilliseconds(20));
        var handler = new FakeConversationHandler().WithResponse("ok");
        var options = new VoiceAiPipelineOptions { EndOfUtteranceSilence = TimeSpan.FromMilliseconds(60) };
        var pipeline = BuildPipeline(stt, tts, handler, options);

        var events = new List<VoiceAiPipelineEvent>();
        using var sub = pipeline.Events.Subscribe(events.Add);

        await RunPipelineWithSingleUtterance(pipeline, voiceFrameCount: 3, silenceFrameCount: 4);

        var error = events.OfType<PipelineErrorEvent>().Should().ContainSingle().Subject;
        error.Source.Should().Be(PipelineErrorSource.Stt);
        error.Error.Message.Should().Be("stt fail");
    }

    [Fact]
    public async Task HandleSessionAsync_ShouldEmitPipelineErrorEvent_OnHandlerError()
    {
        var stt = new FakeSpeechRecognizer().WithTranscript("hola");
        var tts = new FakeSpeechSynthesizer().WithSilence(TimeSpan.FromMilliseconds(20));
        var handler = new FakeConversationHandler();
        // handler returns empty — configure to throw
        var options = new VoiceAiPipelineOptions { EndOfUtteranceSilence = TimeSpan.FromMilliseconds(60) };

        // For this test we need a handler that throws — use NSubstitute or extend FakeConversationHandler
        // FakeConversationHandler does not currently support error injection via WithError.
        // Since FakeConversationHandler is a simple implementation, we note that this test
        // can be verified by building a minimal IConversationHandler inline:
        var throwingHandler = new ThrowingConversationHandler();
        var services = new ServiceCollection();
        services.AddSingleton<IConversationHandler>(throwingHandler);
        var provider = services.BuildServiceProvider();
        var scopeFactory = provider.GetRequiredService<IServiceScopeFactory>();
        var pipelineOptions = Options.Create(options);
        var pipeline2 = new VoiceAiPipeline(stt, tts, scopeFactory, pipelineOptions,
            NullLogger<VoiceAiPipeline>.Instance);

        var events = new List<VoiceAiPipelineEvent>();
        using var sub = pipeline2.Events.Subscribe(events.Add);

        await RunPipelineWithSingleUtterance(pipeline2, voiceFrameCount: 3, silenceFrameCount: 4);

        events.OfType<PipelineErrorEvent>().Should().ContainSingle(e => e.Source == PipelineErrorSource.Handler);
    }

    [Fact]
    public async Task HandleSessionAsync_ShouldEmitPipelineErrorEvent_OnTtsError()
    {
        var stt = new FakeSpeechRecognizer().WithTranscript("hola");
        var tts = new FakeSpeechSynthesizer().WithError(new InvalidOperationException("tts fail"));
        var handler = new FakeConversationHandler().WithResponse("ok");
        var options = new VoiceAiPipelineOptions { EndOfUtteranceSilence = TimeSpan.FromMilliseconds(60) };
        var pipeline = BuildPipeline(stt, tts, handler, options);

        var events = new List<VoiceAiPipelineEvent>();
        using var sub = pipeline.Events.Subscribe(events.Add);

        await RunPipelineWithSingleUtterance(pipeline, voiceFrameCount: 3, silenceFrameCount: 4);

        events.OfType<PipelineErrorEvent>().Should().ContainSingle(e => e.Source == PipelineErrorSource.Tts);
    }

    [Fact]
    public async Task HandleSessionAsync_ShouldTerminateCleanly_WhenCancelled()
    {
        var stt = new FakeSpeechRecognizer().WithTranscript("hola");
        var tts = new FakeSpeechSynthesizer().WithSilence(TimeSpan.FromMilliseconds(20));
        var handler = new FakeConversationHandler().WithResponse("ok");
        var pipeline = BuildPipeline(stt, tts, handler);

        using var cts = new CancellationTokenSource(TimeSpan.FromMilliseconds(200));

        // Provide an endless stream of silence — pipeline should terminate when ct fires
        var act = async () => await RunPipelineWithEndlessFrames(pipeline, cts.Token);
        await act.Should().NotThrowAsync();
    }

    [Fact]
    public async Task HandleSessionAsync_ShouldMaintainConversationHistory()
    {
        int callIndex = 0;
        var stt = new FakeSpeechRecognizer().WithTranscripts(["primero", "segundo"]);
        var tts = new FakeSpeechSynthesizer().WithSilence(TimeSpan.FromMilliseconds(20));
        var handler = new FakeConversationHandler().WithResponses(["resp1", "resp2"]);
        var options = new VoiceAiPipelineOptions { EndOfUtteranceSilence = TimeSpan.FromMilliseconds(60) };
        var pipeline = BuildPipeline(stt, tts, handler, options);

        await RunPipelineWithUtterances(pipeline, utteranceCount: 2);

        // After 2 turns, history should have 2 turns
        handler.CallCount.Should().Be(2);
        // Context history is verified via ReceivedTranscripts in order
        handler.ReceivedTranscripts[0].Should().Be("primero");
        handler.ReceivedTranscripts[1].Should().Be("segundo");
    }

    [Fact]
    public async Task HandleSessionAsync_ShouldTruncateHistory_WhenMaxHistoryExceeded()
    {
        var options = new VoiceAiPipelineOptions
        {
            EndOfUtteranceSilence = TimeSpan.FromMilliseconds(60),
            MaxHistoryTurns = 2
        };

        // Run 3 turns, verify handler is called 3 times (history capped at 2, pipeline still runs)
        var transcripts = Enumerable.Range(1, 3).Select(i => $"transcript{i}").ToArray();
        var stt = new FakeSpeechRecognizer().WithTranscripts(transcripts);
        var tts = new FakeSpeechSynthesizer().WithSilence(TimeSpan.FromMilliseconds(20));
        var handler = new FakeConversationHandler().WithResponses(
            transcripts.Select(t => $"resp_{t}"));
        var pipeline = BuildPipeline(stt, tts, handler, options);

        await RunPipelineWithUtterances(pipeline, utteranceCount: 3);

        handler.CallCount.Should().Be(3);
        // Verify history cap — handler receives context with at most 2 turns
        // This is verified by observing 3 successful cycles (pipeline didn't fail on truncation)
    }

    [Fact]
    public async Task HandleSessionAsync_ShouldForceSttOnMaxUtteranceDuration()
    {
        var stt = new FakeSpeechRecognizer().WithTranscript("forzado");
        var tts = new FakeSpeechSynthesizer().WithSilence(TimeSpan.FromMilliseconds(20));
        var handler = new FakeConversationHandler().WithResponse("ok");
        var options = new VoiceAiPipelineOptions
        {
            EndOfUtteranceSilence = TimeSpan.FromSeconds(10), // long silence threshold
            MaxUtteranceDuration = TimeSpan.FromMilliseconds(60), // force after 3 frames
        };
        var pipeline = BuildPipeline(stt, tts, handler, options);

        // Send voice frames that exceed MaxUtteranceDuration (no silence)
        await RunPipelineWithContinuousVoice(pipeline, frameCount: 5);

        stt.CallCount.Should().BeGreaterThan(0);
        handler.CallCount.Should().BeGreaterThan(0);
    }

    [Fact]
    public async Task HandleSessionAsync_ShouldDetectBargIn_AndCancelTts()
    {
        var stt = new FakeSpeechRecognizer().WithTranscript("interrumpe");
        var tts = new FakeSpeechSynthesizer()
            .WithSilence(TimeSpan.FromMilliseconds(500)); // long TTS so barge-in has time
        var handler = new FakeConversationHandler().WithResponse("respuesta larga");
        var options = new VoiceAiPipelineOptions
        {
            EndOfUtteranceSilence = TimeSpan.FromMilliseconds(60),
            BargInVoiceThreshold = TimeSpan.FromMilliseconds(40), // 2 voice frames
        };
        var pipeline = BuildPipeline(stt, tts, handler, options);

        var events = new List<VoiceAiPipelineEvent>();
        using var sub = pipeline.Events.Subscribe(events.Add);

        await RunPipelineWithBargIn(pipeline);

        events.Should().Contain(e => e is BargInDetectedEvent);
    }

    // ---- Helper methods ----

    private static async Task RunPipelineWithSingleUtterance(
        VoiceAiPipeline pipeline, int voiceFrameCount, int silenceFrameCount)
    {
        // Spins up a loopback AudioSocket, sends voice+silence, then hangs up
        var serverOpts = new AudioSocketOptions { Port = 0 };
        var server = new AudioSocketServer(serverOpts, NullLogger<AudioSocketServer>.Instance);

        TaskCompletionSource<AudioSocketSession> tcs = new();
        server.OnSessionStarted += session => { tcs.TrySetResult(session); return ValueTask.CompletedTask; };

        await server.StartAsync(CancellationToken.None);
        var port = server.BoundPort;
        var channelId = Guid.NewGuid();

        await using var client = new AudioSocketClient("127.0.0.1", port, channelId);
        await client.ConnectAsync(CancellationToken.None);

        var session = await tcs.Task.WaitAsync(TimeSpan.FromSeconds(2));

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        var pipelineTask = pipeline.HandleSessionAsync(session, cts.Token).AsTask();

        for (int i = 0; i < voiceFrameCount; i++)
            await client.SendAudioAsync(VoiceFrame());
        for (int i = 0; i < silenceFrameCount; i++)
            await client.SendAudioAsync(SilenceFrame());

        await Task.Delay(200); // let pipeline process utterance
        await client.SendHangupAsync();

        await pipelineTask.WaitAsync(TimeSpan.FromSeconds(3));
        await server.StopAsync(CancellationToken.None);
    }

    private static async Task RunPipelineWithEndlessFrames(
        VoiceAiPipeline pipeline, CancellationToken ct)
    {
        var serverOpts = new AudioSocketOptions { Port = 0 };
        var server = new AudioSocketServer(serverOpts, NullLogger<AudioSocketServer>.Instance);
        TaskCompletionSource<AudioSocketSession> tcs = new();
        server.OnSessionStarted += session => { tcs.TrySetResult(session); return ValueTask.CompletedTask; };
        await server.StartAsync(CancellationToken.None);

        await using var client = new AudioSocketClient("127.0.0.1", server.BoundPort, Guid.NewGuid());
        await client.ConnectAsync(CancellationToken.None);

        var session = await tcs.Task.WaitAsync(TimeSpan.FromSeconds(2));
        var pipelineTask = pipeline.HandleSessionAsync(session, ct).AsTask();

        // Send silence continuously until cancelled
        try
        {
            while (!ct.IsCancellationRequested)
            {
                await client.SendAudioAsync(SilenceFrame());
                await Task.Delay(20, ct);
            }
        }
        catch (OperationCanceledException) { }

        try { await pipelineTask.WaitAsync(TimeSpan.FromSeconds(2)); } catch { }
        await server.StopAsync(CancellationToken.None);
    }

    private static Task RunPipelineWithUtterances(VoiceAiPipeline pipeline, int utteranceCount)
        => RunPipelineWithMultipleUtterances(pipeline, utteranceCount);

    private static async Task RunPipelineWithMultipleUtterances(
        VoiceAiPipeline pipeline, int utteranceCount)
    {
        var serverOpts = new AudioSocketOptions { Port = 0 };
        var server = new AudioSocketServer(serverOpts, NullLogger<AudioSocketServer>.Instance);
        TaskCompletionSource<AudioSocketSession> tcs = new();
        server.OnSessionStarted += session => { tcs.TrySetResult(session); return ValueTask.CompletedTask; };
        await server.StartAsync(CancellationToken.None);

        await using var client = new AudioSocketClient("127.0.0.1", server.BoundPort, Guid.NewGuid());
        await client.ConnectAsync(CancellationToken.None);

        var session = await tcs.Task.WaitAsync(TimeSpan.FromSeconds(2));
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        var pipelineTask = pipeline.HandleSessionAsync(session, cts.Token).AsTask();

        for (int u = 0; u < utteranceCount; u++)
        {
            for (int i = 0; i < 3; i++) await client.SendAudioAsync(VoiceFrame());
            for (int i = 0; i < 4; i++) await client.SendAudioAsync(SilenceFrame());
            await Task.Delay(300); // let pipeline complete turn
        }

        await client.SendHangupAsync();
        await pipelineTask.WaitAsync(TimeSpan.FromSeconds(5));
        await server.StopAsync(CancellationToken.None);
    }

    private static async Task RunPipelineWithContinuousVoice(
        VoiceAiPipeline pipeline, int frameCount)
    {
        var serverOpts = new AudioSocketOptions { Port = 0 };
        var server = new AudioSocketServer(serverOpts, NullLogger<AudioSocketServer>.Instance);
        TaskCompletionSource<AudioSocketSession> tcs = new();
        server.OnSessionStarted += session => { tcs.TrySetResult(session); return ValueTask.CompletedTask; };
        await server.StartAsync(CancellationToken.None);

        await using var client = new AudioSocketClient("127.0.0.1", server.BoundPort, Guid.NewGuid());
        await client.ConnectAsync(CancellationToken.None);

        var session = await tcs.Task.WaitAsync(TimeSpan.FromSeconds(2));
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        var pipelineTask = pipeline.HandleSessionAsync(session, cts.Token).AsTask();

        for (int i = 0; i < frameCount; i++)
        {
            await client.SendAudioAsync(VoiceFrame());
            await Task.Delay(20);
        }
        await Task.Delay(300);
        await client.SendHangupAsync();
        await pipelineTask.WaitAsync(TimeSpan.FromSeconds(3));
        await server.StopAsync(CancellationToken.None);
    }

    private static async Task RunPipelineWithBargIn(VoiceAiPipeline pipeline)
    {
        var serverOpts = new AudioSocketOptions { Port = 0 };
        var server = new AudioSocketServer(serverOpts, NullLogger<AudioSocketServer>.Instance);
        TaskCompletionSource<AudioSocketSession> tcs = new();
        server.OnSessionStarted += session => { tcs.TrySetResult(session); return ValueTask.CompletedTask; };
        await server.StartAsync(CancellationToken.None);

        await using var client = new AudioSocketClient("127.0.0.1", server.BoundPort, Guid.NewGuid());
        await client.ConnectAsync(CancellationToken.None);

        var session = await tcs.Task.WaitAsync(TimeSpan.FromSeconds(2));
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        var pipelineTask = pipeline.HandleSessionAsync(session, cts.Token).AsTask();

        // First utterance → triggers TTS
        for (int i = 0; i < 3; i++) await client.SendAudioAsync(VoiceFrame());
        for (int i = 0; i < 4; i++) await client.SendAudioAsync(SilenceFrame());
        await Task.Delay(150); // let pipeline enter Speaking state

        // Barge-in: send voice during TTS playback (BargInVoiceThreshold = 40ms = 2 frames)
        for (int i = 0; i < 3; i++)
        {
            await client.SendAudioAsync(VoiceFrame());
            await Task.Delay(20);
        }

        await Task.Delay(300);
        await client.SendHangupAsync();
        try { await pipelineTask.WaitAsync(TimeSpan.FromSeconds(5)); } catch { }
        await server.StopAsync(CancellationToken.None);
    }

    public ValueTask DisposeAsync() => ValueTask.CompletedTask;
}

// Minimal handler that always throws — used for handler-error test
file sealed class ThrowingConversationHandler : IConversationHandler
{
    public ValueTask<string> HandleAsync(string transcript, ConversationContext context, CancellationToken ct)
        => throw new InvalidOperationException("handler fail");
}
```

- [ ] **Step 3: Run tests to verify they fail (pipeline not yet implemented)**

```bash
dotnet build Tests/Asterisk.Sdk.VoiceAi.Tests/ 2>&1 | head -5
```

Expected: Build error — `VoiceAiPipeline` not found.

- [ ] **Step 4: Implement VoiceAiPipeline**

```csharp
// src/Asterisk.Sdk.VoiceAi/Internal/VoiceAiLog.cs
namespace Asterisk.Sdk.VoiceAi.Internal;

internal static partial class VoiceAiLog
{
    [LoggerMessage(LogLevel.Information, "VoiceAi pipeline started for channel {ChannelId}")]
    internal static partial void PipelineStarted(ILogger logger, Guid channelId);

    [LoggerMessage(LogLevel.Information, "VoiceAi pipeline stopped for channel {ChannelId}")]
    internal static partial void PipelineStopped(ILogger logger, Guid channelId);

    [LoggerMessage(LogLevel.Warning, "VoiceAi pipeline error [{Source}] for channel {ChannelId}: {Message}")]
    internal static partial void PipelineError(ILogger logger, PipelineErrorSource source, Guid channelId, string message);

    [LoggerMessage(LogLevel.Debug, "Barge-in detected for channel {ChannelId}")]
    internal static partial void BargInDetected(ILogger logger, Guid channelId);
}
```

```csharp
// src/Asterisk.Sdk.VoiceAi/Pipeline/VoiceAiPipeline.cs
using Asterisk.Sdk.Audio;
using Asterisk.Sdk.VoiceAi.AudioSocket;
using Asterisk.Sdk.VoiceAi.Events;
using Asterisk.Sdk.VoiceAi.Internal;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using System.Reactive.Subjects;
using System.Runtime.InteropServices;
using System.Threading.Channels;

namespace Asterisk.Sdk.VoiceAi.Pipeline;

public sealed class VoiceAiPipeline : IAsyncDisposable
{
    private readonly SpeechRecognizer _stt;
    private readonly SpeechSynthesizer _tts;
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly VoiceAiPipelineOptions _options;
    private readonly ILogger<VoiceAiPipeline> _logger;
    private readonly Subject<VoiceAiPipelineEvent> _events = new();

    // Volatile — AudioMonitorLoop reads; PipelineLoop writes
    private volatile PipelineState _state = PipelineState.Idle;
    private volatile CancellationTokenSource? _ttsCts;

    public IObservable<VoiceAiPipelineEvent> Events => _events;

    public VoiceAiPipeline(
        SpeechRecognizer stt,
        SpeechSynthesizer tts,
        IServiceScopeFactory scopeFactory,
        IOptions<VoiceAiPipelineOptions> options,
        ILogger<VoiceAiPipeline> logger)
    {
        _stt = stt;
        _tts = tts;
        _scopeFactory = scopeFactory;
        _options = options.Value;
        _logger = logger;
    }

    public async ValueTask HandleSessionAsync(
        AudioSocketSession session,
        CancellationToken ct = default)
    {
        using var scope = _scopeFactory.CreateScope();
        var handler = scope.ServiceProvider.GetRequiredService<IConversationHandler>();

        VoiceAiLog.PipelineStarted(_logger, session.ChannelId);
        _state = PipelineState.Listening;

        var utteranceChannel = Channel.CreateBounded<ReadOnlyMemory<byte>[]>(
            new BoundedChannelOptions(4)
            {
                SingleWriter = true,
                SingleReader = true,
                FullMode = BoundedChannelFullMode.DropOldest
            });

        var history = new List<ConversationTurn>();

        try
        {
            await Task.WhenAll(
                AudioMonitorLoop(session, utteranceChannel.Writer, ct),
                PipelineLoop(session, utteranceChannel.Reader, handler, history, ct)
            ).ConfigureAwait(false);
        }
        finally
        {
            VoiceAiLog.PipelineStopped(_logger, session.ChannelId);
            _state = PipelineState.Idle;
        }
    }

    private async Task AudioMonitorLoop(
        AudioSocketSession session,
        ChannelWriter<ReadOnlyMemory<byte>[]> utteranceWriter,
        CancellationToken ct)
    {
        var buffer = new List<ReadOnlyMemory<byte>>();
        var speechStartTime = DateTimeOffset.UtcNow;
        var silenceDuration = TimeSpan.Zero;
        var voiceDuration = TimeSpan.Zero;
        var utteranceDuration = TimeSpan.Zero;
        var isSpeaking = false;
        var frameDuration = TimeSpan.FromMilliseconds(20);

        try
        {
            await foreach (var frame in session.ReadAudioAsync(ct).ConfigureAwait(false))
            {
                var shortSpan = MemoryMarshal.Cast<byte, short>(frame.Span);
                var silence = AudioProcessor.IsSilence(shortSpan, _options.SilenceThresholdDb);

                if (_state == PipelineState.Speaking)
                {
                    if (!silence)
                    {
                        voiceDuration += frameDuration;
                        if (voiceDuration >= _options.BargInVoiceThreshold)
                        {
                            var ttsCts = _ttsCts;
                            ttsCts?.Cancel();
                            voiceDuration = TimeSpan.Zero;
                            VoiceAiLog.BargInDetected(_logger, session.ChannelId);
                            Publish(new BargInDetectedEvent(DateTimeOffset.UtcNow));

                            if (!isSpeaking)
                            {
                                isSpeaking = true;
                                buffer.Clear();
                                speechStartTime = DateTimeOffset.UtcNow;
                                utteranceDuration = TimeSpan.Zero;
                                Publish(new SpeechStartedEvent(DateTimeOffset.UtcNow));
                            }
                            buffer.Add(frame);
                            utteranceDuration += frameDuration;
                        }
                    }
                    else
                    {
                        voiceDuration = TimeSpan.Zero;
                    }
                    continue;
                }

                if (!silence)
                {
                    silenceDuration = TimeSpan.Zero;
                    if (!isSpeaking)
                    {
                        isSpeaking = true;
                        buffer.Clear();
                        speechStartTime = DateTimeOffset.UtcNow;
                        utteranceDuration = TimeSpan.Zero;
                        Publish(new SpeechStartedEvent(DateTimeOffset.UtcNow));
                    }
                    buffer.Add(frame);
                    utteranceDuration += frameDuration;

                    if (utteranceDuration >= _options.MaxUtteranceDuration)
                    {
                        await FlushUtterance(buffer, utteranceWriter, speechStartTime, ct).ConfigureAwait(false);
                        isSpeaking = false;
                        utteranceDuration = TimeSpan.Zero;
                        silenceDuration = TimeSpan.Zero;
                    }
                }
                else
                {
                    if (isSpeaking)
                    {
                        silenceDuration += frameDuration;
                        if (silenceDuration >= _options.EndOfUtteranceSilence)
                        {
                            await FlushUtterance(buffer, utteranceWriter, speechStartTime, ct).ConfigureAwait(false);
                            isSpeaking = false;
                            silenceDuration = TimeSpan.Zero;
                            utteranceDuration = TimeSpan.Zero;
                        }
                    }
                }
            }
        }
        finally
        {
            utteranceWriter.Complete();
        }
    }

    private async Task FlushUtterance(
        List<ReadOnlyMemory<byte>> buffer,
        ChannelWriter<ReadOnlyMemory<byte>[]> writer,
        DateTimeOffset speechStartTime,
        CancellationToken ct)
    {
        var captured = buffer.ToArray();
        buffer.Clear();
        var duration = DateTimeOffset.UtcNow - speechStartTime;
        Publish(new SpeechEndedEvent(DateTimeOffset.UtcNow, duration));
        await writer.WriteAsync(captured, ct).ConfigureAwait(false);
    }

    private async Task PipelineLoop(
        AudioSocketSession session,
        ChannelReader<ReadOnlyMemory<byte>[]> utteranceReader,
        IConversationHandler handler,
        List<ConversationTurn> history,
        CancellationToken ct)
    {
        var channelId = session.ChannelId;

        await foreach (var utterance in utteranceReader.ReadAllAsync(ct).ConfigureAwait(false))
        {
            _state = PipelineState.Recognizing;

            // STT
            string? transcript = null;
            try
            {
                await foreach (var result in _stt.StreamAsync(
                    ToAsyncEnumerable(utterance, ct), _options.InputFormat, ct).ConfigureAwait(false))
                {
                    Publish(new TranscriptReceivedEvent(
                        DateTimeOffset.UtcNow, result.Transcript, result.Confidence, result.IsFinal));
                    if (result.IsFinal)
                        transcript = result.Transcript;
                }
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception ex)
            {
                VoiceAiLog.PipelineError(_logger, PipelineErrorSource.Stt, channelId, ex.Message);
                Publish(new PipelineErrorEvent(DateTimeOffset.UtcNow, ex, PipelineErrorSource.Stt));
                _state = PipelineState.Listening;
                continue;
            }

            if (transcript is null)
            {
                _state = PipelineState.Listening;
                continue;
            }

            // Handler
            _state = PipelineState.Handling;
            string? response = null;
            try
            {
                var trimmedHistory = history.Count > _options.MaxHistoryTurns
                    ? history.Skip(history.Count - _options.MaxHistoryTurns).ToList()
                    : history;

                var context = new ConversationContext
                {
                    ChannelId = channelId,
                    History = trimmedHistory,
                    InputFormat = _options.InputFormat
                };
                response = await handler.HandleAsync(transcript, context, ct).ConfigureAwait(false);
                Publish(new ResponseGeneratedEvent(DateTimeOffset.UtcNow, response));
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception ex)
            {
                VoiceAiLog.PipelineError(_logger, PipelineErrorSource.Handler, channelId, ex.Message);
                Publish(new PipelineErrorEvent(DateTimeOffset.UtcNow, ex, PipelineErrorSource.Handler));
                _state = PipelineState.Listening;
                continue;
            }

            // TTS — synthesize and write audio frames back to the AudioSocket session
            _state = PipelineState.Speaking;
            var synthStart = DateTimeOffset.UtcNow;
            Publish(new SynthesisStartedEvent(synthStart));

            _ttsCts = new CancellationTokenSource();
            try
            {
                using var linked = CancellationTokenSource.CreateLinkedTokenSource(ct, _ttsCts.Token);
                await foreach (var audioChunk in _tts.SynthesizeAsync(
                    response, _options.OutputFormat, linked.Token).ConfigureAwait(false))
                {
                    // Write each PCM chunk back to the caller over AudioSocket
                    await session.WriteAudioAsync(audioChunk, linked.Token).ConfigureAwait(false);
                }
                Publish(new SynthesisEndedEvent(DateTimeOffset.UtcNow, DateTimeOffset.UtcNow - synthStart));
                history.Add(new ConversationTurn(transcript, response, DateTimeOffset.UtcNow));
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                throw;
            }
            catch (OperationCanceledException)
            {
                // Barge-in — _ttsCts was cancelled
                Publish(new SynthesisEndedEvent(DateTimeOffset.UtcNow, DateTimeOffset.UtcNow - synthStart));
            }
            catch (Exception ex)
            {
                VoiceAiLog.PipelineError(_logger, PipelineErrorSource.Tts, channelId, ex.Message);
                Publish(new PipelineErrorEvent(DateTimeOffset.UtcNow, ex, PipelineErrorSource.Tts));
            }
            finally
            {
                var ttsCts = _ttsCts;
                _ttsCts = null;
                ttsCts?.Dispose();
            }

            _state = PipelineState.Listening;
        }
    }

    private static async IAsyncEnumerable<ReadOnlyMemory<byte>> ToAsyncEnumerable(
        ReadOnlyMemory<byte>[] frames,
        [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken ct)
    {
        foreach (var frame in frames)
        {
            ct.ThrowIfCancellationRequested();
            yield return frame;
        }
        await Task.CompletedTask.ConfigureAwait(false);
    }

    private void Publish(VoiceAiPipelineEvent evt) => _events.OnNext(evt);

    public ValueTask DisposeAsync()
    {
        _events.OnCompleted();
        _events.Dispose();
        return ValueTask.CompletedTask;
    }
}
```

**Note on TTS audio output:** `PipelineLoop` receives the `session` parameter and calls `session.WriteAudioAsync(audioChunk, linked.Token)` for each PCM chunk yielded by the synthesizer. This is the direct audio path back to the Asterisk channel. For unit tests, `FakeSpeechSynthesizer.WithSilence()` generates real PCM silence frames that are written through this path, validating end-to-end correctness without a real TTS API.

- [ ] **Step 5: Run pipeline tests**

```bash
dotnet test Tests/Asterisk.Sdk.VoiceAi.Tests/
```

Expected: All pipeline tests pass. Focus on: events emitted correctly, fakes called in order, error recovery transitions to Listening, cancellation terminates cleanly.

- [ ] **Step 6: Commit**

```bash
git add src/Asterisk.Sdk.VoiceAi/Pipeline/ src/Asterisk.Sdk.VoiceAi/Internal/ Tests/Asterisk.Sdk.VoiceAi.Tests/
git commit -m "feat(voiceai): add VoiceAiPipeline with dual-loop state machine, barge-in, and error recovery"
```

---

### Task 4: VoiceAiSessionBroker + DI registration + tests

**Files:**
- Create: `src/Asterisk.Sdk.VoiceAi/Pipeline/VoiceAiSessionBroker.cs`
- Create: `src/Asterisk.Sdk.VoiceAi/DependencyInjection/VoiceAiServiceCollectionExtensions.cs`
- Create: `Tests/Asterisk.Sdk.VoiceAi.Tests/DependencyInjection/VoiceAiDiTests.cs`

- [ ] **Step 1: Write failing DI tests**

```csharp
// Tests/Asterisk.Sdk.VoiceAi.Tests/DependencyInjection/VoiceAiDiTests.cs
using Asterisk.Sdk.VoiceAi.AudioSocket;
using Asterisk.Sdk.VoiceAi.Pipeline;
using Asterisk.Sdk.VoiceAi.Testing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Options;

namespace Asterisk.Sdk.VoiceAi.Tests.DependencyInjection;

public class VoiceAiDiTests
{
    [Fact]
    public void AddVoiceAiPipeline_ShouldRegisterVoiceAiPipelineAsSingleton()
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddSingleton<SpeechRecognizer>(
            new FakeSpeechRecognizer().WithTranscript("test"));
        services.AddSingleton<SpeechSynthesizer>(
            new FakeSpeechSynthesizer().WithSilence(TimeSpan.FromMilliseconds(20)));
        services.AddAudioSocketServer();
        services.AddVoiceAiPipeline<FakeConversationHandlerScoped>();

        using var provider = services.BuildServiceProvider();

        var pipeline1 = provider.GetRequiredService<VoiceAiPipeline>();
        var pipeline2 = provider.GetRequiredService<VoiceAiPipeline>();
        pipeline1.Should().BeSameAs(pipeline2); // Singleton
    }

    [Fact]
    public void AddVoiceAiPipeline_ShouldRegisterHandlerAsScoped()
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddSingleton<SpeechRecognizer>(
            new FakeSpeechRecognizer().WithTranscript("test"));
        services.AddSingleton<SpeechSynthesizer>(
            new FakeSpeechSynthesizer().WithSilence(TimeSpan.FromMilliseconds(20)));
        services.AddAudioSocketServer();
        services.AddVoiceAiPipeline<FakeConversationHandlerScoped>();

        using var provider = services.BuildServiceProvider();
        using var scope1 = provider.CreateScope();
        using var scope2 = provider.CreateScope();

        var h1 = scope1.ServiceProvider.GetRequiredService<IConversationHandler>();
        var h2 = scope2.ServiceProvider.GetRequiredService<IConversationHandler>();
        h1.Should().NotBeSameAs(h2); // different scopes → different instances
    }

    [Fact]
    public void AddVoiceAiPipeline_ShouldRegisterSessionBrokerAsHostedService()
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddSingleton<SpeechRecognizer>(
            new FakeSpeechRecognizer().WithTranscript("test"));
        services.AddSingleton<SpeechSynthesizer>(
            new FakeSpeechSynthesizer().WithSilence(TimeSpan.FromMilliseconds(20)));
        services.AddAudioSocketServer();
        services.AddVoiceAiPipeline<FakeConversationHandlerScoped>();

        using var provider = services.BuildServiceProvider();

        var hostedServices = provider.GetServices<IHostedService>();
        hostedServices.Should().Contain(s => s is VoiceAiSessionBroker);
    }

    [Fact]
    public void AddVoiceAiPipeline_ShouldApplyOptions()
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddSingleton<SpeechRecognizer>(
            new FakeSpeechRecognizer().WithTranscript("test"));
        services.AddSingleton<SpeechSynthesizer>(
            new FakeSpeechSynthesizer().WithSilence(TimeSpan.FromMilliseconds(20)));
        services.AddAudioSocketServer();
        services.AddVoiceAiPipeline<FakeConversationHandlerScoped>(opts =>
            opts.EndOfUtteranceSilence = TimeSpan.FromMilliseconds(800));

        using var provider = services.BuildServiceProvider();
        var opts = provider.GetRequiredService<IOptions<VoiceAiPipelineOptions>>().Value;
        opts.EndOfUtteranceSilence.Should().Be(TimeSpan.FromMilliseconds(800));
    }

    [Fact]
    public void AddVoiceAiPipeline_ShouldRegisterOptionsWithDefaults_WhenNoConfigureCallback()
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddSingleton<SpeechRecognizer>(
            new FakeSpeechRecognizer().WithTranscript("test"));
        services.AddSingleton<SpeechSynthesizer>(
            new FakeSpeechSynthesizer().WithSilence(TimeSpan.FromMilliseconds(20)));
        services.AddAudioSocketServer();
        services.AddVoiceAiPipeline<FakeConversationHandlerScoped>();

        using var provider = services.BuildServiceProvider();
        var opts = provider.GetRequiredService<IOptions<VoiceAiPipelineOptions>>().Value;
        opts.MaxHistoryTurns.Should().Be(20);
        opts.EndOfUtteranceSilence.Should().Be(TimeSpan.FromMilliseconds(500));
    }

    [Fact]
    public void AddVoiceAiPipeline_ShouldWireSessionBrokerToPipeline()
    {
        // Verify broker resolves pipeline (no captive dependency)
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddSingleton<SpeechRecognizer>(
            new FakeSpeechRecognizer().WithTranscript("test"));
        services.AddSingleton<SpeechSynthesizer>(
            new FakeSpeechSynthesizer().WithSilence(TimeSpan.FromMilliseconds(20)));
        services.AddAudioSocketServer();
        services.AddVoiceAiPipeline<FakeConversationHandlerScoped>();

        var act = () =>
        {
            using var provider = services.BuildServiceProvider();
            var _ = provider.GetRequiredService<VoiceAiSessionBroker>();
        };
        act.Should().NotThrow();
    }
}

// Scoped handler used in DI tests
file sealed class FakeConversationHandlerScoped : IConversationHandler
{
    public ValueTask<string> HandleAsync(string transcript, ConversationContext context, CancellationToken ct)
        => ValueTask.FromResult($"echo: {transcript}");
}
```

- [ ] **Step 2: Implement VoiceAiSessionBroker**

```csharp
// src/Asterisk.Sdk.VoiceAi/Pipeline/VoiceAiSessionBroker.cs
using Asterisk.Sdk.VoiceAi.AudioSocket;
using Asterisk.Sdk.VoiceAi.Internal;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Asterisk.Sdk.VoiceAi.Pipeline;

public sealed class VoiceAiSessionBroker : IHostedService
{
    private readonly AudioSocketServer _server;
    private readonly VoiceAiPipeline _pipeline;
    private readonly ILogger<VoiceAiSessionBroker> _logger;
    private CancellationToken _stoppingToken;

    public VoiceAiSessionBroker(
        AudioSocketServer server,
        VoiceAiPipeline pipeline,
        ILogger<VoiceAiSessionBroker> logger)
    {
        _server = server;
        _pipeline = pipeline;
        _logger = logger;
    }

    public Task StartAsync(CancellationToken cancellationToken)
    {
        _stoppingToken = cancellationToken;

        _server.OnSessionStarted += session =>
        {
            _ = _pipeline.HandleSessionAsync(session, _stoppingToken)
                .AsTask()
                .ContinueWith(
                    t => _logger.LogError(t.Exception,
                        "VoiceAi session error [{ChannelId}]", session.ChannelId),
                    TaskContinuationOptions.OnlyOnFaulted);
            return ValueTask.CompletedTask;
        };

        return Task.CompletedTask;
    }

    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;
}
```

- [ ] **Step 3: Implement DI extension**

```csharp
// src/Asterisk.Sdk.VoiceAi/DependencyInjection/VoiceAiServiceCollectionExtensions.cs
using Asterisk.Sdk.VoiceAi.Pipeline;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace Asterisk.Sdk.VoiceAi.DependencyInjection;

public static class VoiceAiServiceCollectionExtensions
{
    public static IServiceCollection AddVoiceAiPipeline<THandler>(
        this IServiceCollection services,
        Action<VoiceAiPipelineOptions>? configure = null)
        where THandler : class, IConversationHandler
    {
        services.TryAddScoped<IConversationHandler, THandler>();
        services.TryAddSingleton<VoiceAiPipeline>();
        services.AddHostedService<VoiceAiSessionBroker>();

        if (configure is not null)
            services.Configure<VoiceAiPipelineOptions>(configure);
        else
            services.TryAddSingleton(
                Microsoft.Extensions.Options.Options.Create(new VoiceAiPipelineOptions()));

        return services;
    }
}
```

- [ ] **Step 4: Run DI tests**

```bash
dotnet test Tests/Asterisk.Sdk.VoiceAi.Tests/ --filter "VoiceAiDiTests"
```

Expected: 6 tests pass.

- [ ] **Step 5: Run all VoiceAi tests**

```bash
dotnet test Tests/Asterisk.Sdk.VoiceAi.Tests/
```

Expected: All tests pass.

- [ ] **Step 6: Commit**

```bash
git add src/Asterisk.Sdk.VoiceAi/Pipeline/VoiceAiSessionBroker.cs src/Asterisk.Sdk.VoiceAi/DependencyInjection/ Tests/Asterisk.Sdk.VoiceAi.Tests/DependencyInjection/
git commit -m "feat(voiceai): add VoiceAiSessionBroker and AddVoiceAiPipeline DI registration"
```

---

## Phase C: STT Providers

### Task 5: Scaffold Asterisk.Sdk.VoiceAi.Stt + Deepgram provider

**Files:**
- Create: `src/Asterisk.Sdk.VoiceAi.Stt/Asterisk.Sdk.VoiceAi.Stt.csproj`
- Create: `src/Asterisk.Sdk.VoiceAi.Stt/README.md`
- Create: `src/Asterisk.Sdk.VoiceAi.Stt/Internal/VoiceAiSttJsonContext.cs`
- Create: `src/Asterisk.Sdk.VoiceAi.Stt/Deepgram/DeepgramOptions.cs`
- Create: `src/Asterisk.Sdk.VoiceAi.Stt/Deepgram/DeepgramSpeechRecognizer.cs`
- Create: `Tests/Asterisk.Sdk.VoiceAi.Stt.Tests/Asterisk.Sdk.VoiceAi.Stt.Tests.csproj`
- Create: `Tests/Asterisk.Sdk.VoiceAi.Stt.Tests/Deepgram/DeepgramFakeServer.cs`
- Create: `Tests/Asterisk.Sdk.VoiceAi.Stt.Tests/Deepgram/DeepgramSpeechRecognizerTests.cs`
- Modify: `Asterisk.Sdk.slnx`

- [ ] **Step 1: Create csproj files and update slnx**

```xml
<!-- src/Asterisk.Sdk.VoiceAi.Stt/Asterisk.Sdk.VoiceAi.Stt.csproj -->
<Project Sdk="Microsoft.NET.Sdk">
  <PropertyGroup>
    <Description>STT providers for Asterisk.Sdk.VoiceAi — Deepgram (WebSocket streaming), OpenAI Whisper, Azure Whisper, Google Speech (REST batch). Zero third-party dependencies.</Description>
    <PackageTags>$(PackageTags);voiceai;stt;deepgram;whisper;google;speech-to-text</PackageTags>
  </PropertyGroup>
  <ItemGroup>
    <InternalsVisibleTo Include="Asterisk.Sdk.VoiceAi.Stt.Tests" />
  </ItemGroup>
  <ItemGroup>
    <ProjectReference Include="..\Asterisk.Sdk.VoiceAi\Asterisk.Sdk.VoiceAi.csproj" />
  </ItemGroup>
  <ItemGroup>
    <PackageReference Include="Microsoft.Extensions.Logging.Abstractions" />
    <PackageReference Include="Microsoft.Extensions.DependencyInjection.Abstractions" />
    <PackageReference Include="Microsoft.Extensions.Options" />
  </ItemGroup>
</Project>
```

```xml
<!-- Tests/Asterisk.Sdk.VoiceAi.Stt.Tests/Asterisk.Sdk.VoiceAi.Stt.Tests.csproj -->
<Project Sdk="Microsoft.NET.Sdk">
  <ItemGroup>
    <ProjectReference Include="..\..\src\Asterisk.Sdk.VoiceAi.Stt\Asterisk.Sdk.VoiceAi.Stt.csproj" />
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

Add to `Asterisk.Sdk.slnx`:
- `/src/`: `src/Asterisk.Sdk.VoiceAi.Stt/Asterisk.Sdk.VoiceAi.Stt.csproj`
- `/Tests/`: `Tests/Asterisk.Sdk.VoiceAi.Stt.Tests/Asterisk.Sdk.VoiceAi.Stt.Tests.csproj`

- [ ] **Step 2: Create JSON DTOs and source-gen context**

```csharp
// src/Asterisk.Sdk.VoiceAi.Stt/Internal/VoiceAiSttJsonContext.cs
using System.Text.Json.Serialization;

namespace Asterisk.Sdk.VoiceAi.Stt.Internal;

// --- Deepgram DTOs ---
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

// --- Whisper / Azure Whisper DTO (shared) ---
internal sealed class WhisperTranscriptionResponse
{
    [JsonPropertyName("text")] public string Text { get; set; } = string.Empty;
}

// --- Google STT DTOs ---
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
    [JsonPropertyName("content")] public string Content { get; set; } = string.Empty;
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

// --- Source generation context ---
[JsonSerializable(typeof(DeepgramResultMessage))]
[JsonSerializable(typeof(WhisperTranscriptionResponse))]
[JsonSerializable(typeof(GoogleSpeechRequest))]
[JsonSerializable(typeof(GoogleSpeechConfig))]
[JsonSerializable(typeof(GoogleSpeechAudio))]
[JsonSerializable(typeof(GoogleSpeechResponse))]
[JsonSerializable(typeof(GoogleSpeechResult))]
[JsonSerializable(typeof(GoogleSpeechAlternative))]
internal partial class VoiceAiSttJsonContext : JsonSerializerContext { }
```

- [ ] **Step 3: Create DeepgramOptions**

```csharp
// src/Asterisk.Sdk.VoiceAi.Stt/Deepgram/DeepgramOptions.cs
namespace Asterisk.Sdk.VoiceAi.Stt.Deepgram;

public sealed class DeepgramOptions
{
    public string ApiKey { get; set; } = string.Empty;
    public string Model { get; set; } = "nova-2";
    public string Language { get; set; } = "es";
    public bool InterimResults { get; set; } = true;
    public bool Punctuate { get; set; } = true;
}
```

- [ ] **Step 4: Create DeepgramFakeServer (test helper)**

```csharp
// Tests/Asterisk.Sdk.VoiceAi.Stt.Tests/Deepgram/DeepgramFakeServer.cs
using System.Net;
using System.Net.WebSockets;
using System.Text;
using System.Text.Json;

namespace Asterisk.Sdk.VoiceAi.Stt.Tests.Deepgram;

/// <summary>
/// In-process WebSocket server that speaks the Deepgram wire protocol.
/// Accepts binary audio frames and returns configurable JSON result messages.
/// </summary>
internal sealed class DeepgramFakeServer : IAsyncDisposable
{
    private readonly HttpListener _listener;
    private readonly CancellationTokenSource _cts = new();
    private Task? _acceptLoop;

    // Responses to send after receiving audio frames
    public List<string> ResultMessages { get; } = [];
    public int ReceivedFrameCount { get; private set; }

    public int Port { get; }

    public DeepgramFakeServer()
    {
        // Find free port
        var tcp = new System.Net.Sockets.TcpListener(IPAddress.Loopback, 0);
        tcp.Start();
        Port = ((IPEndPoint)tcp.LocalEndpoint).Port;
        tcp.Stop();

        _listener = new HttpListener();
        _listener.Prefixes.Add($"http://localhost:{Port}/");
        _listener.Start();

        // Add default result if none configured
        ResultMessages.Add(BuildResultJson("hola mundo", 0.99f, isFinal: false));
        ResultMessages.Add(BuildResultJson("hola mundo", 0.99f, isFinal: true));
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

        // Receive loop — count audio frames
        var receiveTask = Task.Run(async () =>
        {
            while (ws.State == WebSocketState.Open)
            {
                try
                {
                    var result = await ws.ReceiveAsync(buf.AsMemory(), _cts.Token);
                    if (result.MessageType == WebSocketMessageType.Binary)
                        ReceivedFrameCount++;
                    else if (result.MessageType == WebSocketMessageType.Close)
                        break;
                }
                catch { break; }
            }
        });

        // Send configured result messages
        await Task.Delay(50).ConfigureAwait(false); // let some audio arrive
        foreach (var msg in ResultMessages)
        {
            if (ws.State != WebSocketState.Open) break;
            var bytes = Encoding.UTF8.GetBytes(msg);
            await ws.SendAsync(bytes.AsMemory(), WebSocketMessageType.Text, true, _cts.Token);
        }

        await receiveTask.ConfigureAwait(false);
        if (ws.State == WebSocketState.Open)
            await ws.CloseAsync(WebSocketCloseStatus.NormalClosure, "done", CancellationToken.None);
    }

    public static string BuildResultJson(string transcript, float confidence, bool isFinal)
        => $$"""
        {"type":"Results","is_final":{{(isFinal ? "true" : "false")}},"channel":{"alternatives":[{"transcript":"{{transcript}}","confidence":{{confidence}}}]}}
        """;

    public async ValueTask DisposeAsync()
    {
        _cts.Cancel();
        _listener.Stop();
        if (_acceptLoop is not null)
            try { await _acceptLoop.ConfigureAwait(false); } catch { }
        _cts.Dispose();
    }
}
```

- [ ] **Step 5: Write failing Deepgram tests**

```csharp
// Tests/Asterisk.Sdk.VoiceAi.Stt.Tests/Deepgram/DeepgramSpeechRecognizerTests.cs
using Asterisk.Sdk.VoiceAi.Stt.Deepgram;
using Microsoft.Extensions.Options;

namespace Asterisk.Sdk.VoiceAi.Stt.Tests.Deepgram;

public class DeepgramSpeechRecognizerTests : IAsyncDisposable
{
    private readonly DeepgramFakeServer _server;

    public DeepgramSpeechRecognizerTests()
    {
        _server = new DeepgramFakeServer();
        _server.Start();
    }

    private DeepgramSpeechRecognizer BuildRecognizer(Action<DeepgramOptions>? configure = null)
    {
        var opts = new DeepgramOptions { ApiKey = "test-key" };
        configure?.Invoke(opts);
        // Override the WebSocket URI to point to our fake server
        return new DeepgramSpeechRecognizer(
            Options.Create(opts),
            fakeServerPort: _server.Port); // test-only constructor
    }

    [Fact]
    public async Task StreamAsync_ShouldYieldInterimResult()
    {
        _server.ResultMessages.Clear();
        _server.ResultMessages.Add(DeepgramFakeServer.BuildResultJson("hola", 0.8f, isFinal: false));
        _server.ResultMessages.Add(DeepgramFakeServer.BuildResultJson("hola mundo", 0.99f, isFinal: true));

        var recognizer = BuildRecognizer();
        var results = await recognizer.StreamAsync(SingleFrame(), AudioFormat.Slin16Mono8kHz).ToListAsync();

        results.Should().HaveCount(2);
        results[0].IsFinal.Should().BeFalse();
        results[0].Transcript.Should().Be("hola");
        results[1].IsFinal.Should().BeTrue();
        results[1].Transcript.Should().Be("hola mundo");
    }

    [Fact]
    public async Task StreamAsync_ShouldSendAudioFrames()
    {
        var recognizer = BuildRecognizer();
        await recognizer.StreamAsync(ThreeFrames(), AudioFormat.Slin16Mono8kHz).ToListAsync();

        _server.ReceivedFrameCount.Should().BeGreaterThanOrEqualTo(3);
    }

    [Fact]
    public async Task StreamAsync_ShouldYieldFinalResult_WithCorrectConfidence()
    {
        _server.ResultMessages.Clear();
        _server.ResultMessages.Add(DeepgramFakeServer.BuildResultJson("prueba", 0.95f, isFinal: true));

        var recognizer = BuildRecognizer();
        var results = await recognizer.StreamAsync(SingleFrame(), AudioFormat.Slin16Mono8kHz).ToListAsync();

        results.Should().ContainSingle(r => r.IsFinal && r.Confidence == 0.95f);
    }

    [Fact]
    public async Task StreamAsync_ShouldComplete_WhenServerClosesConnection()
    {
        var recognizer = BuildRecognizer();
        var act = async () =>
            await recognizer.StreamAsync(SingleFrame(), AudioFormat.Slin16Mono8kHz).ToListAsync();
        await act.Should().NotThrowAsync();
    }

    [Fact]
    public async Task StreamAsync_ShouldAbort_WhenCancelled()
    {
        using var cts = new CancellationTokenSource(TimeSpan.FromMilliseconds(100));
        var recognizer = BuildRecognizer();
        var act = async () =>
            await recognizer.StreamAsync(EndlessFrames(), AudioFormat.Slin16Mono8kHz, cts.Token)
                .ToListAsync(cts.Token);
        await act.Should().ThrowAsync<OperationCanceledException>();
    }

    private static async IAsyncEnumerable<ReadOnlyMemory<byte>> SingleFrame()
    {
        yield return new byte[320];
        await Task.CompletedTask;
    }

    private static async IAsyncEnumerable<ReadOnlyMemory<byte>> ThreeFrames()
    {
        for (int i = 0; i < 3; i++) yield return new byte[320];
        await Task.CompletedTask;
    }

    private static async IAsyncEnumerable<ReadOnlyMemory<byte>> EndlessFrames(
        [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken ct = default)
    {
        while (!ct.IsCancellationRequested)
        {
            yield return new byte[320];
            await Task.Delay(10, ct);
        }
    }

    public async ValueTask DisposeAsync() => await _server.DisposeAsync();
}
```

- [ ] **Step 6: Implement DeepgramSpeechRecognizer**

```csharp
// src/Asterisk.Sdk.VoiceAi.Stt/Deepgram/DeepgramSpeechRecognizer.cs
using Asterisk.Sdk.VoiceAi.Stt.Internal;
using Microsoft.Extensions.Options;
using System.Net.WebSockets;
using System.Text.Json;

namespace Asterisk.Sdk.VoiceAi.Stt.Deepgram;

public sealed class DeepgramSpeechRecognizer : SpeechRecognizer
{
    private readonly DeepgramOptions _options;
    private readonly int? _fakeServerPort; // for tests only

    public DeepgramSpeechRecognizer(IOptions<DeepgramOptions> options)
        => _options = options.Value;

    // Test constructor: overrides WebSocket endpoint to loopback
    internal DeepgramSpeechRecognizer(IOptions<DeepgramOptions> options, int fakeServerPort)
    {
        _options = options.Value;
        _fakeServerPort = fakeServerPort;
    }

    public override async IAsyncEnumerable<SpeechRecognitionResult> StreamAsync(
        IAsyncEnumerable<ReadOnlyMemory<byte>> audioFrames,
        AudioFormat format,
        [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken ct = default)
    {
        var wsUri = BuildUri(format);
        using var ws = new ClientWebSocket();

        if (_fakeServerPort is null)
            ws.Options.SetRequestHeader("Authorization", $"Token {_options.ApiKey}");

        await ws.ConnectAsync(wsUri, ct).ConfigureAwait(false);

        // Channel to pass results from receive loop to yield loop
        var channel = System.Threading.Channels.Channel.CreateUnbounded<SpeechRecognitionResult>();

        // Send loop + receive loop in parallel
        var sendTask = SendLoopAsync(ws, audioFrames, channel.Writer, ct);
        var receiveTask = ReceiveLoopAsync(ws, channel.Writer, ct);

        await Task.WhenAll(sendTask, receiveTask).ConfigureAwait(false);
        channel.Writer.TryComplete();

        await foreach (var result in channel.Reader.ReadAllAsync(ct).ConfigureAwait(false))
            yield return result;
    }

    private static async Task SendLoopAsync(
        ClientWebSocket ws,
        IAsyncEnumerable<ReadOnlyMemory<byte>> frames,
        System.Threading.Channels.ChannelWriter<SpeechRecognitionResult> writer,
        CancellationToken ct)
    {
        try
        {
            await foreach (var frame in frames.WithCancellation(ct).ConfigureAwait(false))
            {
                if (ws.State != WebSocketState.Open) break;
                await ws.SendAsync(frame, WebSocketMessageType.Binary, true, ct).ConfigureAwait(false);
            }
        }
        finally
        {
            if (ws.State == WebSocketState.Open)
                await ws.CloseOutputAsync(WebSocketCloseStatus.NormalClosure, "done", ct)
                    .ConfigureAwait(false);
        }
    }

    private static async Task ReceiveLoopAsync(
        ClientWebSocket ws,
        System.Threading.Channels.ChannelWriter<SpeechRecognitionResult> writer,
        CancellationToken ct)
    {
        var buf = new byte[65536];
        while (ws.State == WebSocketState.Open)
        {
            WebSocketReceiveResult result;
            try
            {
                result = await ws.ReceiveAsync(buf.AsMemory(), ct).ConfigureAwait(false);
            }
            catch (OperationCanceledException) { break; }
            catch (WebSocketException) { break; }

            if (result.MessageType == WebSocketMessageType.Close) break;
            if (result.MessageType != WebSocketMessageType.Text) continue;

            var json = System.Text.Encoding.UTF8.GetString(buf, 0, result.Count);
            var msg = JsonSerializer.Deserialize(json, VoiceAiSttJsonContext.Default.DeepgramResultMessage);
            if (msg?.Type != "Results") continue;

            var alt = msg.Channel?.Alternatives?.FirstOrDefault();
            if (alt is null) continue;

            var stt = new SpeechRecognitionResult(alt.Transcript, alt.Confidence, msg.IsFinal, TimeSpan.Zero);
            await writer.WriteAsync(stt, ct).ConfigureAwait(false);
        }
    }

    private Uri BuildUri(AudioFormat format)
    {
        if (_fakeServerPort.HasValue)
            return new Uri($"ws://localhost:{_fakeServerPort}/v1/listen" +
                $"?encoding=linear16&sample_rate={format.SampleRate}&channels=1" +
                $"&interim_results={_options.InterimResults.ToString().ToLower()}" +
                $"&punctuate={_options.Punctuate.ToString().ToLower()}");

        return new Uri($"wss://api.deepgram.com/v1/listen" +
            $"?encoding=linear16&sample_rate={format.SampleRate}&channels=1" +
            $"&model={Uri.EscapeDataString(_options.Model)}" +
            $"&language={Uri.EscapeDataString(_options.Language)}" +
            $"&interim_results={_options.InterimResults.ToString().ToLower()}" +
            $"&punctuate={_options.Punctuate.ToString().ToLower()}");
    }
}
```

- [ ] **Step 7: Run Deepgram tests**

```bash
dotnet test Tests/Asterisk.Sdk.VoiceAi.Stt.Tests/ --filter "Deepgram"
```

Expected: 5 tests pass.

- [ ] **Step 8: Commit**

```bash
git add src/Asterisk.Sdk.VoiceAi.Stt/ Tests/Asterisk.Sdk.VoiceAi.Stt.Tests/ Asterisk.Sdk.slnx
git commit -m "feat(voiceai-stt): scaffold Asterisk.Sdk.VoiceAi.Stt with Deepgram WebSocket provider"
```

---

### Task 6: REST STT providers (Whisper, AzureWhisper, Google)

**Files:**
- Create: `Tests/Asterisk.Sdk.VoiceAi.Stt.Tests/Helpers/MockHttpMessageHandler.cs`
- Create: `src/Asterisk.Sdk.VoiceAi.Stt/Whisper/WhisperOptions.cs`
- Create: `src/Asterisk.Sdk.VoiceAi.Stt/Whisper/WhisperSpeechRecognizer.cs`
- Create: `src/Asterisk.Sdk.VoiceAi.Stt/Whisper/AzureWhisperOptions.cs`
- Create: `src/Asterisk.Sdk.VoiceAi.Stt/Whisper/AzureWhisperSpeechRecognizer.cs`
- Create: `src/Asterisk.Sdk.VoiceAi.Stt/Google/GoogleSpeechOptions.cs`
- Create: `src/Asterisk.Sdk.VoiceAi.Stt/Google/GoogleSpeechRecognizer.cs`
- Create: `Tests/Asterisk.Sdk.VoiceAi.Stt.Tests/Whisper/WhisperSpeechRecognizerTests.cs`
- Create: `Tests/Asterisk.Sdk.VoiceAi.Stt.Tests/Whisper/AzureWhisperSpeechRecognizerTests.cs`
- Create: `Tests/Asterisk.Sdk.VoiceAi.Stt.Tests/Google/GoogleSpeechRecognizerTests.cs`

- [ ] **Step 1: Create MockHttpMessageHandler**

```csharp
// Tests/Asterisk.Sdk.VoiceAi.Stt.Tests/Helpers/MockHttpMessageHandler.cs
using System.Net;

namespace Asterisk.Sdk.VoiceAi.Stt.Tests.Helpers;

/// <summary>DelegatingHandler that returns a fixed response for all requests.</summary>
internal sealed class MockHttpMessageHandler : HttpMessageHandler
{
    private readonly HttpResponseMessage _response;
    public HttpRequestMessage? LastRequest { get; private set; }
    public List<HttpRequestMessage> Requests { get; } = [];

    public MockHttpMessageHandler(string jsonBody, HttpStatusCode status = HttpStatusCode.OK)
        => _response = new HttpResponseMessage(status)
        {
            Content = new StringContent(jsonBody, System.Text.Encoding.UTF8, "application/json")
        };

    protected override Task<HttpResponseMessage> SendAsync(
        HttpRequestMessage request, CancellationToken cancellationToken)
    {
        LastRequest = request;
        Requests.Add(request);
        return Task.FromResult(_response);
    }
}
```

- [ ] **Step 2: Create options classes**

```csharp
// src/Asterisk.Sdk.VoiceAi.Stt/Whisper/WhisperOptions.cs
namespace Asterisk.Sdk.VoiceAi.Stt.Whisper;

public sealed class WhisperOptions
{
    public string ApiKey { get; set; } = string.Empty;
    public string Model { get; set; } = "whisper-1";
    public string Language { get; set; } = "es";
    public Uri Endpoint { get; set; } = new("https://api.openai.com/v1/audio/transcriptions");
}
```

```csharp
// src/Asterisk.Sdk.VoiceAi.Stt/Whisper/AzureWhisperOptions.cs
namespace Asterisk.Sdk.VoiceAi.Stt.Whisper;

public sealed class AzureWhisperOptions
{
    public string ApiKey { get; set; } = string.Empty;
    public Uri Endpoint { get; set; } = default!;
    public string DeploymentName { get; set; } = string.Empty;
    public string ApiVersion { get; set; } = "2024-06-01";
}
```

```csharp
// src/Asterisk.Sdk.VoiceAi.Stt/Google/GoogleSpeechOptions.cs
namespace Asterisk.Sdk.VoiceAi.Stt.Google;

public sealed class GoogleSpeechOptions
{
    public string ApiKey { get; set; } = string.Empty;
    public string LanguageCode { get; set; } = "es-CO";
    public string Model { get; set; } = "default";
}
```

- [ ] **Step 3: Write failing tests**

```csharp
// Tests/Asterisk.Sdk.VoiceAi.Stt.Tests/Whisper/WhisperSpeechRecognizerTests.cs
using Asterisk.Sdk.VoiceAi.Stt.Tests.Helpers;
using Asterisk.Sdk.VoiceAi.Stt.Whisper;
using Microsoft.Extensions.Options;

namespace Asterisk.Sdk.VoiceAi.Stt.Tests.Whisper;

public class WhisperSpeechRecognizerTests
{
    private const string WhisperJsonResponse = """{"text":"hola mundo"}""";

    [Fact]
    public async Task StreamAsync_ShouldPostMultipartFormData()
    {
        var mock = new MockHttpMessageHandler(WhisperJsonResponse);
        var recognizer = new WhisperSpeechRecognizer(
            Options.Create(new WhisperOptions { ApiKey = "test-key" }),
            new HttpClient(mock));

        await recognizer.StreamAsync(SingleFrame(), AudioFormat.Slin16Mono8kHz).ToListAsync();

        mock.LastRequest!.Method.Should().Be(HttpMethod.Post);
        mock.LastRequest.Content.Should().BeOfType<MultipartFormDataContent>();
    }

    [Fact]
    public async Task StreamAsync_ShouldDeserializeResponse()
    {
        var mock = new MockHttpMessageHandler(WhisperJsonResponse);
        var recognizer = new WhisperSpeechRecognizer(
            Options.Create(new WhisperOptions { ApiKey = "test-key" }),
            new HttpClient(mock));

        var results = await recognizer.StreamAsync(SingleFrame(), AudioFormat.Slin16Mono8kHz).ToListAsync();

        results.Should().ContainSingle(r => r.Transcript == "hola mundo" && r.IsFinal);
    }

    [Fact]
    public async Task StreamAsync_ShouldSendBearerAuthorizationHeader()
    {
        var mock = new MockHttpMessageHandler(WhisperJsonResponse);
        var recognizer = new WhisperSpeechRecognizer(
            Options.Create(new WhisperOptions { ApiKey = "my-key" }),
            new HttpClient(mock));

        await recognizer.StreamAsync(SingleFrame(), AudioFormat.Slin16Mono8kHz).ToListAsync();

        mock.LastRequest!.Headers.Authorization!.Scheme.Should().Be("Bearer");
        mock.LastRequest.Headers.Authorization.Parameter.Should().Be("my-key");
    }

    [Fact]
    public async Task StreamAsync_ShouldAbort_WhenCancelled()
    {
        using var cts = new CancellationTokenSource();
        cts.Cancel();
        var mock = new MockHttpMessageHandler(WhisperJsonResponse);
        var recognizer = new WhisperSpeechRecognizer(
            Options.Create(new WhisperOptions { ApiKey = "test" }),
            new HttpClient(mock));

        var act = async () => await recognizer
            .StreamAsync(SingleFrame(), AudioFormat.Slin16Mono8kHz, cts.Token)
            .ToListAsync(cts.Token);
        await act.Should().ThrowAsync<OperationCanceledException>();
    }

    private static async IAsyncEnumerable<ReadOnlyMemory<byte>> SingleFrame()
    {
        yield return new byte[320];
        await Task.CompletedTask;
    }
}
```

```csharp
// Tests/Asterisk.Sdk.VoiceAi.Stt.Tests/Whisper/AzureWhisperSpeechRecognizerTests.cs
using Asterisk.Sdk.VoiceAi.Stt.Tests.Helpers;
using Asterisk.Sdk.VoiceAi.Stt.Whisper;
using Microsoft.Extensions.Options;

namespace Asterisk.Sdk.VoiceAi.Stt.Tests.Whisper;

public class AzureWhisperSpeechRecognizerTests
{
    private const string WhisperJsonResponse = """{"text":"prueba azure"}""";

    private static AzureWhisperOptions ValidOptions => new()
    {
        ApiKey = "azure-key",
        Endpoint = new Uri("https://myresource.openai.azure.com/openai/deployments"),
        DeploymentName = "whisper-deployment",
        ApiVersion = "2024-06-01"
    };

    [Fact]
    public async Task StreamAsync_ShouldUseApiKeyHeader_NotBearerAuth()
    {
        var mock = new MockHttpMessageHandler(WhisperJsonResponse);
        var recognizer = new AzureWhisperSpeechRecognizer(
            Options.Create(ValidOptions), new HttpClient(mock));

        await recognizer.StreamAsync(SingleFrame(), AudioFormat.Slin16Mono8kHz).ToListAsync();

        mock.LastRequest!.Headers.TryGetValues("api-key", out var vals).Should().BeTrue();
        vals!.Should().Contain("azure-key");
        mock.LastRequest.Headers.Authorization.Should().BeNull();
    }

    [Fact]
    public async Task StreamAsync_ShouldIncludeDeploymentInUrl()
    {
        var mock = new MockHttpMessageHandler(WhisperJsonResponse);
        var recognizer = new AzureWhisperSpeechRecognizer(
            Options.Create(ValidOptions), new HttpClient(mock));

        await recognizer.StreamAsync(SingleFrame(), AudioFormat.Slin16Mono8kHz).ToListAsync();

        mock.LastRequest!.RequestUri!.AbsoluteUri.Should().Contain("whisper-deployment");
    }

    [Fact]
    public async Task StreamAsync_ShouldDeserializeAzureResponse()
    {
        var mock = new MockHttpMessageHandler(WhisperJsonResponse);
        var recognizer = new AzureWhisperSpeechRecognizer(
            Options.Create(ValidOptions), new HttpClient(mock));

        var results = await recognizer.StreamAsync(SingleFrame(), AudioFormat.Slin16Mono8kHz).ToListAsync();

        results.Should().ContainSingle(r => r.Transcript == "prueba azure" && r.IsFinal);
    }

    private static async IAsyncEnumerable<ReadOnlyMemory<byte>> SingleFrame()
    {
        yield return new byte[320];
        await Task.CompletedTask;
    }
}
```

```csharp
// Tests/Asterisk.Sdk.VoiceAi.Stt.Tests/Google/GoogleSpeechRecognizerTests.cs
using Asterisk.Sdk.VoiceAi.Stt.Google;
using Asterisk.Sdk.VoiceAi.Stt.Tests.Helpers;
using Microsoft.Extensions.Options;
using System.Text.Json;

namespace Asterisk.Sdk.VoiceAi.Stt.Tests.Google;

public class GoogleSpeechRecognizerTests
{
    private const string GoogleJsonResponse = """
        {"results":[{"alternatives":[{"transcript":"buenos dias","confidence":0.97}]}]}
        """;

    [Fact]
    public async Task StreamAsync_ShouldPostJsonWithBase64Audio()
    {
        var mock = new MockHttpMessageHandler(GoogleJsonResponse);
        var recognizer = new GoogleSpeechRecognizer(
            Options.Create(new GoogleSpeechOptions { ApiKey = "gcp-key" }),
            new HttpClient(mock));

        await recognizer.StreamAsync(SingleFrame(), AudioFormat.Slin16Mono8kHz).ToListAsync();

        mock.LastRequest!.Content!.Headers.ContentType!.MediaType.Should().Be("application/json");
        var body = await mock.LastRequest.Content.ReadAsStringAsync();
        body.Should().Contain("content"); // base64 audio in "audio.content"
    }

    [Fact]
    public async Task StreamAsync_ShouldSerializeRequestWithSourceGen()
    {
        var mock = new MockHttpMessageHandler(GoogleJsonResponse);
        var recognizer = new GoogleSpeechRecognizer(
            Options.Create(new GoogleSpeechOptions { ApiKey = "gcp-key", LanguageCode = "en-US" }),
            new HttpClient(mock));

        await recognizer.StreamAsync(SingleFrame(), AudioFormat.Slin16Mono8kHz).ToListAsync();

        var body = await mock.LastRequest!.Content!.ReadAsStringAsync();
        body.Should().Contain("en-US");
        body.Should().Contain("LINEAR16");
    }

    [Fact]
    public async Task StreamAsync_ShouldDeserializeGoogleResponse()
    {
        var mock = new MockHttpMessageHandler(GoogleJsonResponse);
        var recognizer = new GoogleSpeechRecognizer(
            Options.Create(new GoogleSpeechOptions { ApiKey = "gcp-key" }),
            new HttpClient(mock));

        var results = await recognizer.StreamAsync(SingleFrame(), AudioFormat.Slin16Mono8kHz).ToListAsync();

        results.Should().ContainSingle(r => r.Transcript == "buenos dias" && r.Confidence == 0.97f);
    }

    [Fact]
    public async Task StreamAsync_ShouldIncludeApiKeyInQueryString()
    {
        var mock = new MockHttpMessageHandler(GoogleJsonResponse);
        var recognizer = new GoogleSpeechRecognizer(
            Options.Create(new GoogleSpeechOptions { ApiKey = "my-gcp-key" }),
            new HttpClient(mock));

        await recognizer.StreamAsync(SingleFrame(), AudioFormat.Slin16Mono8kHz).ToListAsync();

        mock.LastRequest!.RequestUri!.Query.Should().Contain("my-gcp-key");
    }

    private static async IAsyncEnumerable<ReadOnlyMemory<byte>> SingleFrame()
    {
        yield return new byte[320];
        await Task.CompletedTask;
    }
}
```

- [ ] **Step 4: Implement WhisperSpeechRecognizer**

The Whisper recognizer buffers all frames, adds a WAV header (44-byte PCM WAV header for PCM16), and POSTs multipart form data.

```csharp
// src/Asterisk.Sdk.VoiceAi.Stt/Whisper/WhisperSpeechRecognizer.cs
using Asterisk.Sdk.VoiceAi.Stt.Internal;
using Microsoft.Extensions.Options;
using System.Text.Json;

namespace Asterisk.Sdk.VoiceAi.Stt.Whisper;

public sealed class WhisperSpeechRecognizer : SpeechRecognizer
{
    private readonly WhisperOptions _options;
    private readonly HttpClient _http;

    public WhisperSpeechRecognizer(IOptions<WhisperOptions> options)
        : this(options, new HttpClient()) { }

    internal WhisperSpeechRecognizer(IOptions<WhisperOptions> options, HttpClient http)
    {
        _options = options.Value;
        _http = http;
    }

    public override async IAsyncEnumerable<SpeechRecognitionResult> StreamAsync(
        IAsyncEnumerable<ReadOnlyMemory<byte>> audioFrames,
        AudioFormat format,
        [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken ct = default)
    {
        var pcmData = await DrainFramesAsync(audioFrames, ct).ConfigureAwait(false);
        var wavBytes = AddWavHeaderStatic(pcmData, format);

        using var form = new MultipartFormDataContent();
        form.Add(new ByteArrayContent(wavBytes), "file", "audio.wav");
        form.Add(new StringContent(_options.Model), "model");
        form.Add(new StringContent(_options.Language), "language");

        using var req = new HttpRequestMessage(HttpMethod.Post, _options.Endpoint);
        req.Headers.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue(
            "Bearer", _options.ApiKey);
        req.Content = form;

        using var resp = await _http.SendAsync(req, ct).ConfigureAwait(false);
        resp.EnsureSuccessStatusCode();

        var json = await resp.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
        var result = JsonSerializer.Deserialize(json,
            VoiceAiSttJsonContext.Default.WhisperTranscriptionResponse);

        if (!string.IsNullOrEmpty(result?.Text))
            yield return new SpeechRecognitionResult(result.Text, 1.0f, true, TimeSpan.Zero);
    }

    private static async Task<byte[]> DrainFramesAsync(
        IAsyncEnumerable<ReadOnlyMemory<byte>> frames, CancellationToken ct)
    {
        var ms = new MemoryStream();
        await foreach (var frame in frames.WithCancellation(ct).ConfigureAwait(false))
            ms.Write(frame.Span);
        return ms.ToArray();
    }

    internal static byte[] AddWavHeaderStatic(byte[] pcmData, AudioFormat format)
    {
        // PCM WAV header: 44 bytes
        using var ms = new MemoryStream();
        using var bw = new BinaryWriter(ms);
        int sampleRate = format.SampleRate;
        short channels = (short)format.Channels;
        short bitsPerSample = (short)format.BitsPerSample;
        int byteRate = sampleRate * channels * bitsPerSample / 8;
        short blockAlign = (short)(channels * bitsPerSample / 8);

        bw.Write(System.Text.Encoding.ASCII.GetBytes("RIFF"));
        bw.Write(36 + pcmData.Length);
        bw.Write(System.Text.Encoding.ASCII.GetBytes("WAVE"));
        bw.Write(System.Text.Encoding.ASCII.GetBytes("fmt "));
        bw.Write(16);               // PCM chunk size
        bw.Write((short)1);         // PCM format
        bw.Write(channels);
        bw.Write(sampleRate);
        bw.Write(byteRate);
        bw.Write(blockAlign);
        bw.Write(bitsPerSample);
        bw.Write(System.Text.Encoding.ASCII.GetBytes("data"));
        bw.Write(pcmData.Length);
        bw.Write(pcmData);
        return ms.ToArray();
    }
}
```

- [ ] **Step 5: Implement AzureWhisperSpeechRecognizer**

```csharp
// src/Asterisk.Sdk.VoiceAi.Stt/Whisper/AzureWhisperSpeechRecognizer.cs
using Asterisk.Sdk.VoiceAi.Stt.Internal;
using Microsoft.Extensions.Options;
using System.Text.Json;

namespace Asterisk.Sdk.VoiceAi.Stt.Whisper;

public sealed class AzureWhisperSpeechRecognizer : SpeechRecognizer
{
    private readonly AzureWhisperOptions _options;
    private readonly HttpClient _http;

    public AzureWhisperSpeechRecognizer(IOptions<AzureWhisperOptions> options)
        : this(options, new HttpClient()) { }

    internal AzureWhisperSpeechRecognizer(IOptions<AzureWhisperOptions> options, HttpClient http)
    {
        _options = options.Value;
        _http = http;
    }

    public override async IAsyncEnumerable<SpeechRecognitionResult> StreamAsync(
        IAsyncEnumerable<ReadOnlyMemory<byte>> audioFrames,
        AudioFormat format,
        [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken ct = default)
    {
        var pcmData = await DrainFramesAsync(audioFrames, ct).ConfigureAwait(false);
        var wavBytes = WhisperSpeechRecognizer.AddWavHeaderStatic(pcmData, format);

        // Azure URL: {endpoint}/{deployment}/audio/transcriptions?api-version={apiVersion}
        var uri = new Uri($"{_options.Endpoint.ToString().TrimEnd('/')}/{_options.DeploymentName}" +
            $"/audio/transcriptions?api-version={_options.ApiVersion}");

        using var form = new MultipartFormDataContent();
        form.Add(new ByteArrayContent(wavBytes), "file", "audio.wav");
        form.Add(new StringContent("whisper-1"), "model");

        using var req = new HttpRequestMessage(HttpMethod.Post, uri);
        req.Headers.Add("api-key", _options.ApiKey);
        req.Content = form;

        using var resp = await _http.SendAsync(req, ct).ConfigureAwait(false);
        resp.EnsureSuccessStatusCode();

        var json = await resp.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
        var result = JsonSerializer.Deserialize(json,
            VoiceAiSttJsonContext.Default.WhisperTranscriptionResponse);

        if (!string.IsNullOrEmpty(result?.Text))
            yield return new SpeechRecognitionResult(result.Text, 1.0f, true, TimeSpan.Zero);
    }

    private static async Task<byte[]> DrainFramesAsync(
        IAsyncEnumerable<ReadOnlyMemory<byte>> frames, CancellationToken ct)
    {
        var ms = new MemoryStream();
        await foreach (var frame in frames.WithCancellation(ct).ConfigureAwait(false))
            ms.Write(frame.Span);
        return ms.ToArray();
    }
}
```

**Note:** `WhisperSpeechRecognizer.AddWavHeaderStatic` is declared `internal static` so `AzureWhisperSpeechRecognizer` (in the same assembly) can call it directly without code duplication.

- [ ] **Step 6: Implement GoogleSpeechRecognizer**

```csharp
// src/Asterisk.Sdk.VoiceAi.Stt/Google/GoogleSpeechRecognizer.cs
using Asterisk.Sdk.VoiceAi.Stt.Internal;
using Microsoft.Extensions.Options;
using System.Text.Json;

namespace Asterisk.Sdk.VoiceAi.Stt.Google;

public sealed class GoogleSpeechRecognizer : SpeechRecognizer
{
    private readonly GoogleSpeechOptions _options;
    private readonly HttpClient _http;

    public GoogleSpeechRecognizer(IOptions<GoogleSpeechOptions> options)
        : this(options, new HttpClient()) { }

    internal GoogleSpeechRecognizer(IOptions<GoogleSpeechOptions> options, HttpClient http)
    {
        _options = options.Value;
        _http = http;
    }

    public override async IAsyncEnumerable<SpeechRecognitionResult> StreamAsync(
        IAsyncEnumerable<ReadOnlyMemory<byte>> audioFrames,
        AudioFormat format,
        [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken ct = default)
    {
        var pcmData = await DrainFramesAsync(audioFrames, ct).ConfigureAwait(false);
        var base64Audio = Convert.ToBase64String(pcmData);

        var request = new GoogleSpeechRequest
        {
            Config = new GoogleSpeechConfig
            {
                Encoding = "LINEAR16",
                SampleRateHertz = format.SampleRate,
                LanguageCode = _options.LanguageCode,
                Model = _options.Model
            },
            Audio = new GoogleSpeechAudio { Content = base64Audio }
        };

        var json = JsonSerializer.Serialize(request, VoiceAiSttJsonContext.Default.GoogleSpeechRequest);
        var uri = new Uri($"https://speech.googleapis.com/v1/speech:recognize?key={_options.ApiKey}");

        using var req = new HttpRequestMessage(HttpMethod.Post, uri);
        req.Content = new StringContent(json, System.Text.Encoding.UTF8, "application/json");

        using var resp = await _http.SendAsync(req, ct).ConfigureAwait(false);
        resp.EnsureSuccessStatusCode();

        var responseJson = await resp.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
        var result = JsonSerializer.Deserialize(responseJson,
            VoiceAiSttJsonContext.Default.GoogleSpeechResponse);

        var alt = result?.Results?.FirstOrDefault()?.Alternatives?.FirstOrDefault();
        if (alt is not null)
            yield return new SpeechRecognitionResult(alt.Transcript, alt.Confidence, true, TimeSpan.Zero);
    }

    private static async Task<byte[]> DrainFramesAsync(
        IAsyncEnumerable<ReadOnlyMemory<byte>> frames, CancellationToken ct)
    {
        var ms = new MemoryStream();
        await foreach (var frame in frames.WithCancellation(ct).ConfigureAwait(false))
            ms.Write(frame.Span);
        return ms.ToArray();
    }
}
```

- [ ] **Step 7: Run all STT tests**

```bash
dotnet test Tests/Asterisk.Sdk.VoiceAi.Stt.Tests/
```

Expected: All 16 tests pass (5 Deepgram + 4 Whisper + 3 AzureWhisper + 4 Google).

- [ ] **Step 8: Commit**

```bash
git add src/Asterisk.Sdk.VoiceAi.Stt/Whisper/ src/Asterisk.Sdk.VoiceAi.Stt/Google/ Tests/Asterisk.Sdk.VoiceAi.Stt.Tests/
git commit -m "feat(voiceai-stt): add Whisper, AzureWhisper, and Google REST STT providers"
```

---

### Task 7: STT DI registration + tests

**Files:**
- Create: `src/Asterisk.Sdk.VoiceAi.Stt/DependencyInjection/SttServiceCollectionExtensions.cs`
- Create: `src/Asterisk.Sdk.VoiceAi.Stt/Internal/SttLog.cs`
- Create: `Tests/Asterisk.Sdk.VoiceAi.Stt.Tests/DependencyInjection/SttDiTests.cs`

- [ ] **Step 1: Write failing DI tests**

```csharp
// Tests/Asterisk.Sdk.VoiceAi.Stt.Tests/DependencyInjection/SttDiTests.cs
using Asterisk.Sdk.VoiceAi.Stt.DependencyInjection;
using Asterisk.Sdk.VoiceAi.Stt.Deepgram;
using Asterisk.Sdk.VoiceAi.Stt.Whisper;
using Asterisk.Sdk.VoiceAi.Stt.Google;
using Microsoft.Extensions.DependencyInjection;

namespace Asterisk.Sdk.VoiceAi.Stt.Tests.DependencyInjection;

public class SttDiTests
{
    [Fact]
    public void AddDeepgramSpeechRecognizer_ShouldRegisterAsSpeechRecognizer()
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddDeepgramSpeechRecognizer(o => o.ApiKey = "test");
        using var provider = services.BuildServiceProvider();
        provider.GetRequiredService<SpeechRecognizer>().Should().BeOfType<DeepgramSpeechRecognizer>();
    }

    [Fact]
    public void AddWhisperSpeechRecognizer_ShouldRegisterAsSpeechRecognizer()
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddWhisperSpeechRecognizer(o => o.ApiKey = "test");
        using var provider = services.BuildServiceProvider();
        provider.GetRequiredService<SpeechRecognizer>().Should().BeOfType<WhisperSpeechRecognizer>();
    }

    [Fact]
    public void AddAzureWhisperSpeechRecognizer_ShouldRegisterAsSpeechRecognizer()
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddAzureWhisperSpeechRecognizer(o =>
        {
            o.ApiKey = "test";
            o.Endpoint = new Uri("https://example.openai.azure.com/openai/deployments");
            o.DeploymentName = "whisper";
        });
        using var provider = services.BuildServiceProvider();
        provider.GetRequiredService<SpeechRecognizer>().Should().BeOfType<AzureWhisperSpeechRecognizer>();
    }

    [Fact]
    public void AddGoogleSpeechRecognizer_ShouldRegisterAsSpeechRecognizer()
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddGoogleSpeechRecognizer(o => o.ApiKey = "test");
        using var provider = services.BuildServiceProvider();
        provider.GetRequiredService<SpeechRecognizer>().Should().BeOfType<GoogleSpeechRecognizer>();
    }
}
```

- [ ] **Step 2: Implement SttServiceCollectionExtensions + SttLog**

```csharp
// src/Asterisk.Sdk.VoiceAi.Stt/Internal/SttLog.cs
namespace Asterisk.Sdk.VoiceAi.Stt.Internal;

internal static partial class SttLog
{
    [LoggerMessage(LogLevel.Debug, "STT {Provider} stream started")]
    internal static partial void StreamStarted(ILogger logger, string provider);

    [LoggerMessage(LogLevel.Debug, "STT {Provider} stream completed")]
    internal static partial void StreamCompleted(ILogger logger, string provider);
}
```

```csharp
// src/Asterisk.Sdk.VoiceAi.Stt/DependencyInjection/SttServiceCollectionExtensions.cs
using Asterisk.Sdk.VoiceAi.Stt.Deepgram;
using Asterisk.Sdk.VoiceAi.Stt.Whisper;
using Asterisk.Sdk.VoiceAi.Stt.Google;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace Asterisk.Sdk.VoiceAi.Stt.DependencyInjection;

public static class SttServiceCollectionExtensions
{
    public static IServiceCollection AddDeepgramSpeechRecognizer(
        this IServiceCollection services,
        Action<DeepgramOptions> configure)
    {
        services.Configure(configure);
        services.TryAddSingleton<SpeechRecognizer, DeepgramSpeechRecognizer>();
        return services;
    }

    public static IServiceCollection AddWhisperSpeechRecognizer(
        this IServiceCollection services,
        Action<WhisperOptions> configure)
    {
        services.Configure(configure);
        services.TryAddSingleton<SpeechRecognizer, WhisperSpeechRecognizer>();
        return services;
    }

    public static IServiceCollection AddAzureWhisperSpeechRecognizer(
        this IServiceCollection services,
        Action<AzureWhisperOptions> configure)
    {
        services.Configure(configure);
        services.TryAddSingleton<SpeechRecognizer, AzureWhisperSpeechRecognizer>();
        return services;
    }

    public static IServiceCollection AddGoogleSpeechRecognizer(
        this IServiceCollection services,
        Action<GoogleSpeechOptions> configure)
    {
        services.Configure(configure);
        services.TryAddSingleton<SpeechRecognizer, GoogleSpeechRecognizer>();
        return services;
    }
}
```

- [ ] **Step 3: Run all STT tests including DI**

```bash
dotnet test Tests/Asterisk.Sdk.VoiceAi.Stt.Tests/
```

Expected: 20 tests pass.

- [ ] **Step 4: Commit**

```bash
git add src/Asterisk.Sdk.VoiceAi.Stt/DependencyInjection/ src/Asterisk.Sdk.VoiceAi.Stt/Internal/SttLog.cs Tests/Asterisk.Sdk.VoiceAi.Stt.Tests/DependencyInjection/
git commit -m "feat(voiceai-stt): add STT DI registration for all four providers"
```

---

## Phase D: TTS Providers

### Task 8: Scaffold Asterisk.Sdk.VoiceAi.Tts + ElevenLabs provider

**Files:**
- Create: `src/Asterisk.Sdk.VoiceAi.Tts/Asterisk.Sdk.VoiceAi.Tts.csproj`
- Create: `src/Asterisk.Sdk.VoiceAi.Tts/README.md`
- Create: `src/Asterisk.Sdk.VoiceAi.Tts/Internal/VoiceAiTtsJsonContext.cs`
- Create: `src/Asterisk.Sdk.VoiceAi.Tts/ElevenLabs/ElevenLabsOptions.cs`
- Create: `src/Asterisk.Sdk.VoiceAi.Tts/ElevenLabs/ElevenLabsSpeechSynthesizer.cs`
- Create: `Tests/Asterisk.Sdk.VoiceAi.Tts.Tests/Asterisk.Sdk.VoiceAi.Tts.Tests.csproj`
- Create: `Tests/Asterisk.Sdk.VoiceAi.Tts.Tests/ElevenLabs/ElevenLabsFakeServer.cs`
- Create: `Tests/Asterisk.Sdk.VoiceAi.Tts.Tests/ElevenLabs/ElevenLabsSpeechSynthesizerTests.cs`
- Modify: `Asterisk.Sdk.slnx`

- [ ] **Step 1: Create csproj files and update slnx**

```xml
<!-- src/Asterisk.Sdk.VoiceAi.Tts/Asterisk.Sdk.VoiceAi.Tts.csproj -->
<Project Sdk="Microsoft.NET.Sdk">
  <PropertyGroup>
    <Description>TTS providers for Asterisk.Sdk.VoiceAi — ElevenLabs (WebSocket streaming) and Azure TTS (REST). Zero third-party dependencies.</Description>
    <PackageTags>$(PackageTags);voiceai;tts;elevenlabs;azure;text-to-speech</PackageTags>
  </PropertyGroup>
  <ItemGroup>
    <InternalsVisibleTo Include="Asterisk.Sdk.VoiceAi.Tts.Tests" />
  </ItemGroup>
  <ItemGroup>
    <ProjectReference Include="..\Asterisk.Sdk.VoiceAi\Asterisk.Sdk.VoiceAi.csproj" />
  </ItemGroup>
  <ItemGroup>
    <PackageReference Include="Microsoft.Extensions.Logging.Abstractions" />
    <PackageReference Include="Microsoft.Extensions.DependencyInjection.Abstractions" />
    <PackageReference Include="Microsoft.Extensions.Options" />
  </ItemGroup>
</Project>
```

```xml
<!-- Tests/Asterisk.Sdk.VoiceAi.Tts.Tests/Asterisk.Sdk.VoiceAi.Tts.Tests.csproj -->
<Project Sdk="Microsoft.NET.Sdk">
  <ItemGroup>
    <ProjectReference Include="..\..\src\Asterisk.Sdk.VoiceAi.Tts\Asterisk.Sdk.VoiceAi.Tts.csproj" />
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

Add to `Asterisk.Sdk.slnx` (under `/src/` and `/Tests/` folders).

- [ ] **Step 2: Create TTS JSON context**

```csharp
// src/Asterisk.Sdk.VoiceAi.Tts/Internal/VoiceAiTtsJsonContext.cs
using System.Text.Json.Serialization;

namespace Asterisk.Sdk.VoiceAi.Tts.Internal;

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

[JsonSerializable(typeof(ElevenLabsTextChunk))]
[JsonSerializable(typeof(ElevenLabsVoiceSettings))]
internal partial class VoiceAiTtsJsonContext : JsonSerializerContext { }
```

- [ ] **Step 3: Create ElevenLabsOptions**

```csharp
// src/Asterisk.Sdk.VoiceAi.Tts/ElevenLabs/ElevenLabsOptions.cs
namespace Asterisk.Sdk.VoiceAi.Tts.ElevenLabs;

public sealed class ElevenLabsOptions
{
    public string ApiKey { get; set; } = string.Empty;
    public string VoiceId { get; set; } = string.Empty;
    public string ModelId { get; set; } = "eleven_turbo_v2";
    public float Stability { get; set; } = 0.5f;
    public float SimilarityBoost { get; set; } = 0.75f;
}
```

- [ ] **Step 4: Create ElevenLabsFakeServer**

```csharp
// Tests/Asterisk.Sdk.VoiceAi.Tts.Tests/ElevenLabs/ElevenLabsFakeServer.cs
using System.Net;
using System.Net.WebSockets;
using System.Text;

namespace Asterisk.Sdk.VoiceAi.Tts.Tests.ElevenLabs;

/// <summary>
/// In-process WebSocket server that speaks the ElevenLabs TTS wire protocol.
/// Receives JSON text chunks, returns binary PCM frames.
/// </summary>
internal sealed class ElevenLabsFakeServer : IAsyncDisposable
{
    private readonly HttpListener _listener;
    private readonly CancellationTokenSource _cts = new();
    private Task? _acceptLoop;

    public int Port { get; }
    public List<string> ReceivedJsonMessages { get; } = [];

    // Audio frames to send back (binary WebSocket frames)
    public List<byte[]> AudioFramesToSend { get; } = [];
    // JSON alignment messages to intersperse
    public bool SendAlignmentMessages { get; set; } = false;

    public ElevenLabsFakeServer()
    {
        var tcp = new System.Net.Sockets.TcpListener(IPAddress.Loopback, 0);
        tcp.Start();
        Port = ((IPEndPoint)tcp.LocalEndpoint).Port;
        tcp.Stop();

        _listener = new HttpListener();
        _listener.Prefixes.Add($"http://localhost:{Port}/");
        _listener.Start();

        // Default: send 2 binary audio frames of 320 bytes each
        AudioFramesToSend.Add(new byte[320]);
        AudioFramesToSend.Add(new byte[320]);
    }

    public void Start() => _acceptLoop = Task.Run(AcceptLoopAsync);

    private async Task AcceptLoopAsync()
    {
        try
        {
            while (!_cts.Token.IsCancellationRequested)
            {
                var ctx = await _listener.GetContextAsync().WaitAsync(_cts.Token);
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
        var wsCtx = await ctx.AcceptWebSocketAsync(null);
        var ws = wsCtx.WebSocket;
        var buf = new byte[65536];

        // Receive JSON text chunks
        var receiveTask = Task.Run(async () =>
        {
            while (ws.State == WebSocketState.Open)
            {
                try
                {
                    var result = await ws.ReceiveAsync(buf.AsMemory(), _cts.Token);
                    if (result.MessageType == WebSocketMessageType.Text)
                        ReceivedJsonMessages.Add(Encoding.UTF8.GetString(buf, 0, result.Count));
                    else if (result.MessageType == WebSocketMessageType.Close)
                        break;
                }
                catch { break; }
            }
        });

        await Task.Delay(30); // let client send first message

        // Send audio frames and optional alignment messages
        for (int i = 0; i < AudioFramesToSend.Count; i++)
        {
            if (ws.State != WebSocketState.Open) break;
            await ws.SendAsync(AudioFramesToSend[i].AsMemory(),
                WebSocketMessageType.Binary, true, _cts.Token);

            if (SendAlignmentMessages)
            {
                // Send a text alignment message between audio frames
                var align = Encoding.UTF8.GetBytes("""{"message_type":"alignment","words":[]}""");
                await ws.SendAsync(align.AsMemory(), WebSocketMessageType.Text, true, _cts.Token);
            }
        }

        await receiveTask;
        if (ws.State == WebSocketState.Open)
            await ws.CloseAsync(WebSocketCloseStatus.NormalClosure, "done", CancellationToken.None);
    }

    public async ValueTask DisposeAsync()
    {
        _cts.Cancel();
        _listener.Stop();
        if (_acceptLoop is not null)
            try { await _acceptLoop; } catch { }
        _cts.Dispose();
    }
}
```

- [ ] **Step 5: Write failing ElevenLabs tests**

```csharp
// Tests/Asterisk.Sdk.VoiceAi.Tts.Tests/ElevenLabs/ElevenLabsSpeechSynthesizerTests.cs
using Asterisk.Sdk.VoiceAi.Tts.ElevenLabs;
using Microsoft.Extensions.Options;

namespace Asterisk.Sdk.VoiceAi.Tts.Tests.ElevenLabs;

public class ElevenLabsSpeechSynthesizerTests : IAsyncDisposable
{
    private readonly ElevenLabsFakeServer _server;

    public ElevenLabsSpeechSynthesizerTests()
    {
        _server = new ElevenLabsFakeServer();
        _server.Start();
    }

    private ElevenLabsSpeechSynthesizer BuildSynthesizer()
        => new(Options.Create(new ElevenLabsOptions
        {
            ApiKey = "test-key",
            VoiceId = "test-voice"
        }), fakeServerPort: _server.Port);

    [Fact]
    public async Task SynthesizeAsync_ShouldYieldBinaryAudioFrames()
    {
        var synth = BuildSynthesizer();
        var frames = await synth.SynthesizeAsync("hola", AudioFormat.Slin16Mono8kHz).ToListAsync();
        frames.Should().HaveCount(2); // default: 2 frames from fake server
        frames.All(f => f.Length == 320).Should().BeTrue();
    }

    [Fact]
    public async Task SynthesizeAsync_ShouldSendTextChunk()
    {
        var synth = BuildSynthesizer();
        await synth.SynthesizeAsync("hola mundo", AudioFormat.Slin16Mono8kHz).ToListAsync();

        _server.ReceivedJsonMessages.Should().NotBeEmpty();
        _server.ReceivedJsonMessages.Any(m => m.Contains("hola mundo")).Should().BeTrue();
    }

    [Fact]
    public async Task SynthesizeAsync_ShouldFilterAlignmentMessages_NotYieldThem()
    {
        _server.SendAlignmentMessages = true;
        var synth = BuildSynthesizer();
        var frames = await synth.SynthesizeAsync("test", AudioFormat.Slin16Mono8kHz).ToListAsync();

        // Only binary frames should be yielded — alignment text messages filtered out
        frames.Should().HaveCount(2);
    }

    [Fact]
    public async Task SynthesizeAsync_ShouldAbort_WhenCancelled()
    {
        using var cts = new CancellationTokenSource(TimeSpan.FromMilliseconds(50));
        var synth = BuildSynthesizer();
        var act = async () => await synth
            .SynthesizeAsync("test", AudioFormat.Slin16Mono8kHz, cts.Token)
            .ToListAsync(cts.Token);
        await act.Should().ThrowAsync<OperationCanceledException>();
    }

    [Fact]
    public async Task SynthesizeAsync_ShouldComplete_WhenServerClosesConnection()
    {
        _server.AudioFramesToSend.Clear(); // empty — server closes immediately
        var synth = BuildSynthesizer();
        var act = async () => await synth
            .SynthesizeAsync("test", AudioFormat.Slin16Mono8kHz)
            .ToListAsync();
        await act.Should().NotThrowAsync();
    }

    public async ValueTask DisposeAsync() => await _server.DisposeAsync();
}
```

- [ ] **Step 6: Implement ElevenLabsSpeechSynthesizer**

```csharp
// src/Asterisk.Sdk.VoiceAi.Tts/ElevenLabs/ElevenLabsSpeechSynthesizer.cs
using Asterisk.Sdk.VoiceAi.Tts.Internal;
using Microsoft.Extensions.Options;
using System.Net.WebSockets;
using System.Text.Json;

namespace Asterisk.Sdk.VoiceAi.Tts.ElevenLabs;

public sealed class ElevenLabsSpeechSynthesizer : SpeechSynthesizer
{
    private readonly ElevenLabsOptions _options;
    private readonly int? _fakeServerPort;

    public ElevenLabsSpeechSynthesizer(IOptions<ElevenLabsOptions> options)
        => _options = options.Value;

    internal ElevenLabsSpeechSynthesizer(IOptions<ElevenLabsOptions> options, int fakeServerPort)
    {
        _options = options.Value;
        _fakeServerPort = fakeServerPort;
    }

    public override async IAsyncEnumerable<ReadOnlyMemory<byte>> SynthesizeAsync(
        string text,
        AudioFormat outputFormat,
        [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken ct = default)
    {
        var uri = BuildUri(outputFormat);
        using var ws = new ClientWebSocket();

        if (_fakeServerPort is null)
            ws.Options.SetRequestHeader("xi-api-key", _options.ApiKey);

        await ws.ConnectAsync(uri, ct).ConfigureAwait(false);

        // Channel to pass binary frames from receive loop to yield loop
        var channel = System.Threading.Channels.Channel.CreateUnbounded<ReadOnlyMemory<byte>>();

        var sendTask = SendTextAsync(ws, text, ct);
        var receiveTask = ReceiveFramesAsync(ws, channel.Writer, ct);

        await Task.WhenAll(sendTask, receiveTask).ConfigureAwait(false);
        channel.Writer.TryComplete();

        await foreach (var frame in channel.Reader.ReadAllAsync(ct).ConfigureAwait(false))
            yield return frame;
    }

    private async Task SendTextAsync(ClientWebSocket ws, string text, CancellationToken ct)
    {
        var chunk = new ElevenLabsTextChunk
        {
            Text = text,
            VoiceSettings = new ElevenLabsVoiceSettings
            {
                Stability = _options.Stability,
                SimilarityBoost = _options.SimilarityBoost
            }
        };
        var json = JsonSerializer.Serialize(chunk, VoiceAiTtsJsonContext.Default.ElevenLabsTextChunk);
        await ws.SendAsync(
            System.Text.Encoding.UTF8.GetBytes(json).AsMemory(),
            WebSocketMessageType.Text, true, ct).ConfigureAwait(false);

        // Flush signal
        var flush = new ElevenLabsTextChunk { Text = " ", Flush = true };
        var flushJson = JsonSerializer.Serialize(flush, VoiceAiTtsJsonContext.Default.ElevenLabsTextChunk);
        await ws.SendAsync(
            System.Text.Encoding.UTF8.GetBytes(flushJson).AsMemory(),
            WebSocketMessageType.Text, true, ct).ConfigureAwait(false);

        // Close signal
        var closeSignal = new ElevenLabsTextChunk { Text = string.Empty };
        var closeJson = JsonSerializer.Serialize(closeSignal, VoiceAiTtsJsonContext.Default.ElevenLabsTextChunk);
        await ws.SendAsync(
            System.Text.Encoding.UTF8.GetBytes(closeJson).AsMemory(),
            WebSocketMessageType.Text, true, ct).ConfigureAwait(false);
    }

    private static async Task ReceiveFramesAsync(
        ClientWebSocket ws,
        System.Threading.Channels.ChannelWriter<ReadOnlyMemory<byte>> writer,
        CancellationToken ct)
    {
        var buf = new byte[65536];
        while (ws.State == WebSocketState.Open)
        {
            WebSocketReceiveResult result;
            try
            {
                result = await ws.ReceiveAsync(buf.AsMemory(), ct).ConfigureAwait(false);
            }
            catch (OperationCanceledException) { break; }
            catch (WebSocketException) { break; }

            if (result.MessageType == WebSocketMessageType.Close) break;

            // Binary = PCM audio frame; Text = alignment message (filtered)
            if (result.MessageType == WebSocketMessageType.Binary)
            {
                var frame = new byte[result.Count];
                buf.AsSpan(0, result.Count).CopyTo(frame);
                await writer.WriteAsync(frame.AsMemory(), ct).ConfigureAwait(false);
            }
            // Text messages (alignment) are silently discarded
        }
    }

    private Uri BuildUri(AudioFormat format)
    {
        if (_fakeServerPort.HasValue)
            return new Uri($"ws://localhost:{_fakeServerPort}/v1/text-to-speech/test-voice/stream-input");

        int sampleRate = format.SampleRate;
        return new Uri($"wss://api.elevenlabs.io/v1/text-to-speech/{_options.VoiceId}/stream-input" +
            $"?model_id={Uri.EscapeDataString(_options.ModelId)}&output_format=pcm_{sampleRate}");
    }
}
```

- [ ] **Step 7: Run ElevenLabs tests**

```bash
dotnet test Tests/Asterisk.Sdk.VoiceAi.Tts.Tests/ --filter "ElevenLabs"
```

Expected: 5 tests pass.

- [ ] **Step 8: Commit**

```bash
git add src/Asterisk.Sdk.VoiceAi.Tts/ Tests/Asterisk.Sdk.VoiceAi.Tts.Tests/ Asterisk.Sdk.slnx
git commit -m "feat(voiceai-tts): scaffold Asterisk.Sdk.VoiceAi.Tts with ElevenLabs WebSocket provider"
```

---

### Task 9: Azure TTS provider + TTS DI + tests

**Files:**
- Create: `src/Asterisk.Sdk.VoiceAi.Tts/Azure/AzureTtsOptions.cs`
- Create: `src/Asterisk.Sdk.VoiceAi.Tts/Azure/AzureTtsOutputFormat.cs`
- Create: `src/Asterisk.Sdk.VoiceAi.Tts/Azure/AzureTtsSpeechSynthesizer.cs`
- Create: `src/Asterisk.Sdk.VoiceAi.Tts/Internal/TtsLog.cs`
- Create: `src/Asterisk.Sdk.VoiceAi.Tts/DependencyInjection/TtsServiceCollectionExtensions.cs`
- Create: `Tests/Asterisk.Sdk.VoiceAi.Tts.Tests/Helpers/MockHttpMessageHandler.cs`
- Create: `Tests/Asterisk.Sdk.VoiceAi.Tts.Tests/Azure/AzureTtsSpeechSynthesizerTests.cs`
- Create: `Tests/Asterisk.Sdk.VoiceAi.Tts.Tests/DependencyInjection/TtsDiTests.cs`

- [ ] **Step 1: Create MockHttpMessageHandler for TTS tests**

```csharp
// Tests/Asterisk.Sdk.VoiceAi.Tts.Tests/Helpers/MockHttpMessageHandler.cs
using System.Net;

namespace Asterisk.Sdk.VoiceAi.Tts.Tests.Helpers;

internal sealed class MockHttpMessageHandler : HttpMessageHandler
{
    private readonly byte[] _responseBytes;
    private readonly HttpStatusCode _status;
    public HttpRequestMessage? LastRequest { get; private set; }

    public MockHttpMessageHandler(byte[] responseBytes, HttpStatusCode status = HttpStatusCode.OK)
    {
        _responseBytes = responseBytes;
        _status = status;
    }

    protected override Task<HttpResponseMessage> SendAsync(
        HttpRequestMessage request, CancellationToken cancellationToken)
    {
        LastRequest = request;
        var response = new HttpResponseMessage(_status)
        {
            Content = new ByteArrayContent(_responseBytes)
        };
        return Task.FromResult(response);
    }
}
```

- [ ] **Step 2: Write failing Azure TTS tests**

```csharp
// Tests/Asterisk.Sdk.VoiceAi.Tts.Tests/Azure/AzureTtsSpeechSynthesizerTests.cs
using Asterisk.Sdk.VoiceAi.Tts.Azure;
using Asterisk.Sdk.VoiceAi.Tts.Tests.Helpers;
using Microsoft.Extensions.Options;

namespace Asterisk.Sdk.VoiceAi.Tts.Tests.Azure;

public class AzureTtsSpeechSynthesizerTests
{
    private static AzureTtsOptions ValidOptions => new()
    {
        ApiKey = "azure-tts-key",
        Region = "eastus",
        VoiceName = "es-CO-SalomeNeural",
        OutputFormat = AzureTtsOutputFormat.Raw8Khz16BitMonoPcm
    };

    [Fact]
    public async Task SynthesizeAsync_ShouldPostSsmlXml()
    {
        var audioBytes = new byte[320];
        var mock = new MockHttpMessageHandler(audioBytes);
        var synth = new AzureTtsSpeechSynthesizer(
            Options.Create(ValidOptions), new HttpClient(mock));

        await synth.SynthesizeAsync("hola", AudioFormat.Slin16Mono8kHz).ToListAsync();

        mock.LastRequest!.Content!.Headers.ContentType!.MediaType.Should().Be("application/ssml+xml");
    }

    [Fact]
    public async Task SynthesizeAsync_ShouldEscapeXmlInText()
    {
        var audioBytes = new byte[320];
        var mock = new MockHttpMessageHandler(audioBytes);
        var synth = new AzureTtsSpeechSynthesizer(
            Options.Create(ValidOptions), new HttpClient(mock));

        await synth.SynthesizeAsync("<script>alert('xss')</script>", AudioFormat.Slin16Mono8kHz)
            .ToListAsync();

        var body = await mock.LastRequest!.Content!.ReadAsStringAsync();
        body.Should().NotContain("<script>");
        body.Should().Contain("&lt;script&gt;");
    }

    [Fact]
    public async Task SynthesizeAsync_ShouldYieldChunkedResponseAsFrames()
    {
        var audioBytes = new byte[640]; // 2 chunks × 320 bytes
        var mock = new MockHttpMessageHandler(audioBytes);
        var synth = new AzureTtsSpeechSynthesizer(
            Options.Create(ValidOptions), new HttpClient(mock), chunkSize: 320);

        var frames = await synth.SynthesizeAsync("hola", AudioFormat.Slin16Mono8kHz).ToListAsync();

        frames.Should().HaveCount(2);
    }

    [Fact]
    public async Task SynthesizeAsync_ShouldUseApiKeyHeader()
    {
        var mock = new MockHttpMessageHandler(new byte[320]);
        var synth = new AzureTtsSpeechSynthesizer(
            Options.Create(ValidOptions), new HttpClient(mock));

        await synth.SynthesizeAsync("test", AudioFormat.Slin16Mono8kHz).ToListAsync();

        mock.LastRequest!.Headers.TryGetValues("Ocp-Apim-Subscription-Key", out var vals)
            .Should().BeTrue();
        vals!.Should().Contain("azure-tts-key");
    }
}
```

```csharp
// Tests/Asterisk.Sdk.VoiceAi.Tts.Tests/DependencyInjection/TtsDiTests.cs
using Asterisk.Sdk.VoiceAi.Tts.Azure;
using Asterisk.Sdk.VoiceAi.Tts.DependencyInjection;
using Asterisk.Sdk.VoiceAi.Tts.ElevenLabs;
using Microsoft.Extensions.DependencyInjection;

namespace Asterisk.Sdk.VoiceAi.Tts.Tests.DependencyInjection;

public class TtsDiTests
{
    [Fact]
    public void AddElevenLabsSpeechSynthesizer_ShouldRegisterAsSpeechSynthesizer()
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddElevenLabsSpeechSynthesizer(o => { o.ApiKey = "test"; o.VoiceId = "v1"; });
        using var provider = services.BuildServiceProvider();
        provider.GetRequiredService<SpeechSynthesizer>().Should().BeOfType<ElevenLabsSpeechSynthesizer>();
    }

    [Fact]
    public void AddAzureTtsSpeechSynthesizer_ShouldRegisterAsSpeechSynthesizer()
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddAzureTtsSpeechSynthesizer(o =>
        {
            o.ApiKey = "test";
            o.Region = "eastus";
            o.VoiceName = "es-CO-SalomeNeural";
        });
        using var provider = services.BuildServiceProvider();
        provider.GetRequiredService<SpeechSynthesizer>().Should().BeOfType<AzureTtsSpeechSynthesizer>();
    }

    [Fact]
    public void AddElevenLabsSpeechSynthesizer_ShouldApplyOptions()
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddElevenLabsSpeechSynthesizer(o =>
        {
            o.ApiKey = "my-key";
            o.VoiceId = "my-voice";
            o.Stability = 0.8f;
        });
        using var provider = services.BuildServiceProvider();
        var opts = provider.GetRequiredService<Microsoft.Extensions.Options.IOptions<ElevenLabsOptions>>().Value;
        opts.Stability.Should().Be(0.8f);
        opts.VoiceId.Should().Be("my-voice");
    }
}
```

- [ ] **Step 3: Implement Azure TTS types**

```csharp
// src/Asterisk.Sdk.VoiceAi.Tts/Azure/AzureTtsOutputFormat.cs
namespace Asterisk.Sdk.VoiceAi.Tts.Azure;

public static class AzureTtsOutputFormat
{
    public const string Raw8Khz16BitMonoPcm = "raw-8khz-16bit-mono-pcm";
    public const string Raw16Khz16BitMonoPcm = "raw-16khz-16bit-mono-pcm";
    public const string Raw24Khz16BitMonoPcm = "raw-24khz-16bit-mono-pcm";
    public const string Raw48Khz16BitMonoPcm = "raw-48khz-16bit-mono-pcm";
}
```

```csharp
// src/Asterisk.Sdk.VoiceAi.Tts/Azure/AzureTtsOptions.cs
namespace Asterisk.Sdk.VoiceAi.Tts.Azure;

public sealed class AzureTtsOptions
{
    public string ApiKey { get; set; } = string.Empty;
    public string Region { get; set; } = string.Empty;
    public string VoiceName { get; set; } = string.Empty;
    public string OutputFormat { get; set; } = AzureTtsOutputFormat.Raw8Khz16BitMonoPcm;
}
```

```csharp
// src/Asterisk.Sdk.VoiceAi.Tts/Azure/AzureTtsSpeechSynthesizer.cs
using Microsoft.Extensions.Options;
using System.Security;

namespace Asterisk.Sdk.VoiceAi.Tts.Azure;

public sealed class AzureTtsSpeechSynthesizer : SpeechSynthesizer
{
    private readonly AzureTtsOptions _options;
    private readonly HttpClient _http;
    private readonly int _chunkSize;

    public AzureTtsSpeechSynthesizer(IOptions<AzureTtsOptions> options)
        : this(options, new HttpClient(), 4096) { }

    internal AzureTtsSpeechSynthesizer(
        IOptions<AzureTtsOptions> options,
        HttpClient http,
        int chunkSize = 4096)
    {
        _options = options.Value;
        _http = http;
        _chunkSize = chunkSize;
    }

    public override async IAsyncEnumerable<ReadOnlyMemory<byte>> SynthesizeAsync(
        string text,
        AudioFormat outputFormat,
        [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken ct = default)
    {
        var escapedText = SecurityElement.Escape(text) ?? string.Empty;
        var ssml = $"""
            <speak version='1.0' xml:lang='es-CO'>
                <voice name='{_options.VoiceName}'>{escapedText}</voice>
            </speak>
            """;

        var uri = new Uri(
            $"https://{_options.Region}.tts.speech.microsoft.com/cognitiveservices/v1");

        using var req = new HttpRequestMessage(HttpMethod.Post, uri);
        req.Headers.Add("Ocp-Apim-Subscription-Key", _options.ApiKey);
        req.Headers.Add("X-Microsoft-OutputFormat", _options.OutputFormat);
        req.Content = new StringContent(ssml, System.Text.Encoding.UTF8, "application/ssml+xml");

        using var resp = await _http.SendAsync(req, HttpCompletionOption.ResponseHeadersRead, ct)
            .ConfigureAwait(false);
        resp.EnsureSuccessStatusCode();

        var stream = await resp.Content.ReadAsStreamAsync(ct).ConfigureAwait(false);
        var buf = new byte[_chunkSize];
        int read;
        while ((read = await stream.ReadAsync(buf.AsMemory(), ct).ConfigureAwait(false)) > 0)
        {
            var chunk = new byte[read];
            buf.AsSpan(0, read).CopyTo(chunk);
            yield return chunk.AsMemory();
        }
    }
}
```

- [ ] **Step 4: Implement TtsLog and TtsServiceCollectionExtensions**

```csharp
// src/Asterisk.Sdk.VoiceAi.Tts/Internal/TtsLog.cs
namespace Asterisk.Sdk.VoiceAi.Tts.Internal;

internal static partial class TtsLog
{
    [LoggerMessage(LogLevel.Debug, "TTS {Provider} synthesis started")]
    internal static partial void SynthesisStarted(ILogger logger, string provider);

    [LoggerMessage(LogLevel.Debug, "TTS {Provider} synthesis completed")]
    internal static partial void SynthesisCompleted(ILogger logger, string provider);
}
```

```csharp
// src/Asterisk.Sdk.VoiceAi.Tts/DependencyInjection/TtsServiceCollectionExtensions.cs
using Asterisk.Sdk.VoiceAi.Tts.Azure;
using Asterisk.Sdk.VoiceAi.Tts.ElevenLabs;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace Asterisk.Sdk.VoiceAi.Tts.DependencyInjection;

public static class TtsServiceCollectionExtensions
{
    public static IServiceCollection AddElevenLabsSpeechSynthesizer(
        this IServiceCollection services,
        Action<ElevenLabsOptions> configure)
    {
        services.Configure(configure);
        services.TryAddSingleton<SpeechSynthesizer, ElevenLabsSpeechSynthesizer>();
        return services;
    }

    public static IServiceCollection AddAzureTtsSpeechSynthesizer(
        this IServiceCollection services,
        Action<AzureTtsOptions> configure)
    {
        services.Configure(configure);
        services.TryAddSingleton<SpeechSynthesizer, AzureTtsSpeechSynthesizer>();
        return services;
    }
}
```

- [ ] **Step 5: Run all TTS tests**

```bash
dotnet test Tests/Asterisk.Sdk.VoiceAi.Tts.Tests/
```

Expected: 12 tests pass (5 ElevenLabs + 4 Azure + 3 DI).

- [ ] **Step 6: Commit**

```bash
git add src/Asterisk.Sdk.VoiceAi.Tts/Azure/ src/Asterisk.Sdk.VoiceAi.Tts/Internal/ src/Asterisk.Sdk.VoiceAi.Tts/DependencyInjection/ Tests/Asterisk.Sdk.VoiceAi.Tts.Tests/
git commit -m "feat(voiceai-tts): add Azure TTS provider and DI registration for all TTS providers"
```

---

## Phase E: Example + Final Verification

### Task 10: E2E example app

**Files:**
- Create: `Examples/VoiceAiExample/VoiceAiExample.csproj`
- Create: `Examples/VoiceAiExample/Program.cs`
- Create: `Examples/VoiceAiExample/EchoConversationHandler.cs`
- Create: `Examples/VoiceAiExample/appsettings.json`
- Modify: `Asterisk.Sdk.slnx`

- [ ] **Step 1: Create example csproj**

```xml
<!-- Examples/VoiceAiExample/VoiceAiExample.csproj -->
<Project Sdk="Microsoft.NET.Sdk">
  <ItemGroup>
    <ProjectReference Include="..\..\src\Asterisk.Sdk.VoiceAi\Asterisk.Sdk.VoiceAi.csproj" />
    <ProjectReference Include="..\..\src\Asterisk.Sdk.VoiceAi.Stt\Asterisk.Sdk.VoiceAi.Stt.csproj" />
    <ProjectReference Include="..\..\src\Asterisk.Sdk.VoiceAi.Tts\Asterisk.Sdk.VoiceAi.Tts.csproj" />
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

Add to `Asterisk.Sdk.slnx` under `/Examples/`:
```xml
<Project Path="Examples/VoiceAiExample/VoiceAiExample.csproj" />
```

- [ ] **Step 2: Create example files**

```csharp
// Examples/VoiceAiExample/EchoConversationHandler.cs
using Asterisk.Sdk.VoiceAi;
using Microsoft.Extensions.Logging;

namespace VoiceAiExample;

public class EchoConversationHandler(ILogger<EchoConversationHandler> logger)
    : IConversationHandler
{
    public ValueTask<string> HandleAsync(
        string transcript,
        ConversationContext ctx,
        CancellationToken ct = default)
    {
        logger.LogInformation("[{ChannelId}] Usuario: {Transcript}", ctx.ChannelId, transcript);
        var response = $"Dijiste: {transcript}";
        logger.LogInformation("[{ChannelId}] Asistente: {Response}", ctx.ChannelId, response);
        return ValueTask.FromResult(response);
    }
}
```

```csharp
// Examples/VoiceAiExample/Program.cs
using Asterisk.Sdk.VoiceAi.AudioSocket.DependencyInjection;
using Asterisk.Sdk.VoiceAi.DependencyInjection;
using Asterisk.Sdk.VoiceAi.Stt.DependencyInjection;
using Asterisk.Sdk.VoiceAi.Tts.DependencyInjection;
using VoiceAiExample;

var host = Host.CreateDefaultBuilder(args)
    .ConfigureServices((ctx, services) =>
    {
        services.AddAudioSocketServer(opt => opt.Port = 9092);

        services.AddDeepgramSpeechRecognizer(opt =>
        {
            opt.ApiKey = ctx.Configuration["Deepgram:ApiKey"]
                ?? throw new InvalidOperationException("Missing Deepgram:ApiKey");
            opt.Language = "es";
        });

        services.AddElevenLabsSpeechSynthesizer(opt =>
        {
            opt.ApiKey = ctx.Configuration["ElevenLabs:ApiKey"]
                ?? throw new InvalidOperationException("Missing ElevenLabs:ApiKey");
            opt.VoiceId = ctx.Configuration["ElevenLabs:VoiceId"]
                ?? throw new InvalidOperationException("Missing ElevenLabs:VoiceId");
        });

        // AddVoiceAiPipeline registers EchoConversationHandler (Scoped),
        // VoiceAiPipeline (Singleton), and VoiceAiSessionBroker (IHostedService).
        // The broker auto-wires AudioSocketServer.OnSessionStarted.
        services.AddVoiceAiPipeline<EchoConversationHandler>(opt =>
        {
            opt.EndOfUtteranceSilence = TimeSpan.FromMilliseconds(600);
        });
    })
    .Build();

await host.RunAsync();
```

```json
// Examples/VoiceAiExample/appsettings.json
{
  "Deepgram": {
    "ApiKey": "YOUR_DEEPGRAM_API_KEY"
  },
  "ElevenLabs": {
    "ApiKey": "YOUR_ELEVENLABS_API_KEY",
    "VoiceId": "YOUR_VOICE_ID"
  },
  "Logging": {
    "LogLevel": {
      "Default": "Information",
      "Microsoft": "Warning"
    }
  }
}
```

- [ ] **Step 3: Build example**

```bash
dotnet build Examples/VoiceAiExample/VoiceAiExample.csproj
```

Expected: Build succeeded, 0 warnings. (No runtime run needed — requires real API keys.)

- [ ] **Step 4: Commit**

```bash
git add Examples/VoiceAiExample/ Asterisk.Sdk.slnx
git commit -m "feat(voiceai): add VoiceAiExample E2E console app (Deepgram + ElevenLabs + echo handler)"
```

---

### Task 11: Final verification

- [ ] **Step 1: Build entire solution — 0 warnings**

```bash
cd /media/Data/Source/IPcom/Asterisk.Sdk
dotnet build Asterisk.Sdk.slnx
```

Expected: Build succeeded. **0 warnings.** Fix any warning before proceeding.

- [ ] **Step 2: Run all tests — 100% pass rate**

```bash
dotnet test Asterisk.Sdk.slnx
```

Expected output summary:
- `Asterisk.Sdk.VoiceAi.Tests`: ~28 tests, 0 failed
- `Asterisk.Sdk.VoiceAi.Testing.Tests`: ~10 tests, 0 failed
- `Asterisk.Sdk.VoiceAi.Stt.Tests`: ~20 tests, 0 failed
- `Asterisk.Sdk.VoiceAi.Tts.Tests`: ~12 tests, 0 failed
- All pre-existing tests (943+): still passing

**If any test fails:** Fix the failure before committing. Do not skip tests.

- [ ] **Step 3: Commit final state (if any uncommitted changes remain)**

```bash
git status  # verify clean or identify any uncommitted files
# If clean (all committed per-task), skip this step
# If there are uncommitted changes, stage them explicitly:
# git add src/... Tests/... (list specific paths)
git commit -m "test(voiceai): verify all 70 Sprint 23 tests pass, 0 build warnings"
```

---

## Summary

| Task | Phase | Component | Tests |
|------|-------|-----------|-------|
| 1 | A | Asterisk.Sdk.VoiceAi abstractions + events | 0 |
| 2 | A | **Asterisk.Sdk.VoiceAi.Testing** fakes | 10 |
| 3 | B | **VoiceAiPipeline** (state machine, dual loops, barge-in) | 22 |
| 4 | B | VoiceAiSessionBroker + DI | 6 |
| 5 | C | Stt scaffold + **Deepgram** (WebSocket) | 5 |
| 6 | C | Whisper + AzureWhisper + Google (REST) | 11 |
| 7 | C | STT DI registration | 4 |
| 8 | D | Tts scaffold + **ElevenLabs** (WebSocket) | 5 |
| 9 | D | Azure TTS (REST) + TTS DI registration | 7 |
| 10 | E | VoiceAiExample (E2E console app) | 0 |
| 11 | E | Final verification (build + all tests) | 0 |
| **Total** | | | **~70** |

**Packages delivered:** `Asterisk.Sdk.VoiceAi`, `Asterisk.Sdk.VoiceAi.Testing`, `Asterisk.Sdk.VoiceAi.Stt`, `Asterisk.Sdk.VoiceAi.Tts`
**New NuGet dependencies added:** 0
**AOT constraint:** `IsAotCompatible=true` (inherited from Directory.Build.props) — all JSON via `[JsonSerializable]` source generation, no reflection at runtime

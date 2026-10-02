# Verbara.Sdk.VoiceAi

Voice AI pipeline for Verbara.Sdk — orchestration layer for STT, TTS, and conversation with turn-taking and barge-in detection.

## Installation

```bash
dotnet add package Verbara.Sdk.VoiceAi
```

## Quick Start

```csharp
// Implement your conversation handler (called once per user utterance)
public class MyHandler : IConversationHandler
{
    public ValueTask<string> HandleAsync(
        string transcript, ConversationContext context, CancellationToken ct)
    {
        return ValueTask.FromResult($"You said: {transcript}");
    }
}

// Register in DI
services.AddAudioSocketServer(opts => opts.Port = 9092);
services.AddVoiceAiPipeline<MyHandler>(opts =>
{
    opts.InputFormat = AudioFormat.Slin16Mono8kHz;
    opts.OutputFormat = AudioFormat.Slin16Mono8kHz;
    opts.EndOfUtteranceSilence = TimeSpan.FromMilliseconds(600);
});

// Subscribe to pipeline events
var pipeline = app.Services.GetRequiredService<VoiceAiPipeline>();
pipeline.Events.Subscribe(evt => Console.WriteLine(evt));
```

## Features

- `VoiceAiPipeline` — full VAD → STT → `IConversationHandler` → TTS loop per AudioSocket session
- Barge-in detection: cancels TTS playback when the caller speaks
- `IConversationHandler` — scoped per session; implement to plug in any LLM or business logic
- `ISessionHandler` — low-level interface; implement for fully custom session handling
- `VoiceAiSessionBroker` — hosted service that hands each `AudioSocketSession` to the active `ISessionHandler`, once
  - **The session ends when its handler does.** As soon as `HandleSessionAsync` returns, throws or is cancelled, the broker calls the session's `HangupAsync`: one hangup frame if the line is still live, then the close. On Asterisk 20 and later the call goes on in the dialplan after `AudioSocket()`; an ARI `externalMedia` channel leaves Stasis with the caller still in your bridge. A handler keeps its line exactly as long as it runs, so await the conversation rather than leaving it to a background task. A session the caller or the handler already ended gets nothing more, and one the broker ends while still live is logged once at Information.
  - **A graceful stop ends the calls, then waits.** While the token passed to `StopAsync` is not cancelled, the stop ends every live session it handed out with a hangup frame and waits for the handlers to return; it does not cancel their token. Once that token is cancelled (the host's shutdown budget ran out), the stop cancels the handlers' token and returns without waiting further. A direct `StopAsync(CancellationToken.None)` therefore waits until every handler has returned: pass a token you cancel after a bound of your own.
  - **Disposal does not wait.** `Dispose` cancels the handlers' token and returns.
  - The handler's `CancellationToken` belongs to the broker: only a stop that is no longer graceful, or `Dispose`, cancels it. Once its stop has been called, or once it is disposed, the broker hands no new session on; the AudioSocket server keeps such a session and releases it when it stops. The broker is not restartable: a start after a stop subscribes nothing, a start after `Dispose` throws `ObjectDisposedException`, and a stop after `Dispose` does nothing.
  - Register `AddAudioSocketServer` before `AddVoiceAiPipeline`, as above: the host stops services in reverse order, so the broker then stops first and every call ends with a hangup frame. A server stopped first closes its sessions without one.
- Observable `Events` stream (`SpeechStartedEvent`, `TranscriptReceivedEvent`, `BargInDetectedEvent`, etc.)
- Native AOT compatible

## Custom STT / TTS Providers

When writing your own `SpeechRecognizer` or `SpeechSynthesizer` subclass, override `ProviderName` with a stable literal to avoid the per-utterance `GetType().Name` allocation on the pipeline hot path (used as a tag on STT/TTS activities):

```csharp
public sealed class MyCustomRecognizer : SpeechRecognizer
{
    public override string ProviderName => "MyCustom";

    public override IAsyncEnumerable<SpeechRecognitionResult> StreamAsync(
        IAsyncEnumerable<ReadOnlyMemory<byte>> audioFrames,
        AudioFormat format,
        CancellationToken ct = default)
    {
        // ...
    }
}
```

If you don't override `ProviderName` the default falls back to `GetType().Name` — functional, but incurs one reflection call per utterance.

## Observability

- **Metrics:** `VoiceAiMetrics` (sessions started/completed/failed, session duration), `SpeechRecognitionMetrics` (transcriptions started/completed/failed/cancelled, latency), `SpeechSynthesisMetrics` (syntheses started/completed/failed/cancelled/silent, latency, characters). Every recognition and synthesis the pipeline starts ends in exactly one of `completed`, `failed` or `cancelled`; `cancelled` is tagged `voiceai.ending` with what cut it short (`session-cancelled`, and for syntheses also `barge-in`, `disposal`, `far-end`), and `tts.syntheses.completed` counts only a synthesis whose audio was all written.
- **Tracing:** `VoiceAiActivitySource` — session / recognition / synthesis spans.
- **Health:** `VoiceAiHealthCheck`, `SttHealthCheck`, `TtsHealthCheck` auto-registered by `AddVoiceAiPipeline<THandler>()`.
- Discover names via `VerbaraTelemetry.ActivitySourceNames` / `MeterNames` from `Verbara.Sdk.Hosting`.

## Documentation

See the [main README](../../README.md) for full documentation. Upgrading from 2.6.1: [voice-session-ending-migration.md](../../docs/guides/voice-session-ending-migration.md).

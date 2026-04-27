# Asterisk.Sdk.VoiceAi.Testing

Test fakes for [Asterisk.Sdk.VoiceAi](https://www.nuget.org/packages/Asterisk.Sdk.VoiceAi) — exercise your turn-based pipeline, conversation handlers, and AudioSocket plumbing without touching real STT/TTS APIs (no API keys, no network, no flakiness). Native AOT, zero reflection, MIT licensed.

## What it does

Three fakes, all implementing the same interfaces as the real providers:

| Fake | Replaces | Behavior |
|------|----------|----------|
| `FakeSpeechRecognizer` | `ISpeechRecognizer` | Emits a configured sequence of transcripts on cue. Supports per-turn delay simulation. |
| `FakeSpeechSynthesizer` | `ISpeechSynthesizer` | Returns canned PCM16 byte arrays. Configurable byte count + delivery cadence to simulate streaming TTFA. |
| `FakeConversationHandler` | `IConversationHandler` | Echoes input or returns scripted responses. Useful when testing Stt + Tts wiring without business logic. |

Drop-in replacements: same DI shape, same lifecycle, same telemetry surface — your wiring code stays unchanged between unit tests and production.

## Install

```sh
dotnet add package Asterisk.Sdk.VoiceAi.Testing
```

Typically referenced from your test project only.

## Quick start

```csharp
using Asterisk.Sdk.VoiceAi.Testing;
using Asterisk.Sdk.VoiceAi.DependencyInjection;
using Microsoft.Extensions.DependencyInjection;

// Stub the recognizer with a fixed transcript sequence
var fakeStt = new FakeSpeechRecognizer();
fakeStt.QueueTranscript("hello world");
fakeStt.QueueTranscript("goodbye");

// Stub the synthesizer with a 16 kHz / 200 ms PCM clip
var fakeTts = new FakeSpeechSynthesizer { OutputSampleCount = 3200 };

var services = new ServiceCollection();
services.AddSingleton<ISpeechRecognizer>(fakeStt);
services.AddSingleton<ISpeechSynthesizer>(fakeTts);
services.AddVoiceAiPipeline<MyConversationHandler>();

var provider = services.BuildServiceProvider();
var pipeline = provider.GetRequiredService<IVoiceAiPipeline>();

await pipeline.ProcessTurnAsync(audioInput, ct);

Assert.Equal(2, fakeStt.TranscriptsConsumed);
Assert.True(fakeTts.SynthesizeInvocations >= 1);
```

## Why use it

- **No API keys in CI** — your test pipeline runs offline against deterministic fakes.
- **Deterministic timing** — pin TTFA / latency assertions to fake delays, not provider variance.
- **Failure injection** — every fake exposes hooks to throw on the next call, simulate slow responses, or report partial transcripts.
- **Fast** — fakes return synchronously where possible; full pipeline turns finish in microseconds.

## Examples

See `Tests/Asterisk.Sdk.VoiceAi.Tests/` and `Tests/Asterisk.Sdk.VoiceAi.Testing.Tests/` for end-to-end usage patterns including barge-in, turn-taking, and pipeline-level integration tests.

## License

MIT. Part of the [Asterisk.Sdk](https://github.com/Harol-Reina/Asterisk.Sdk) project.

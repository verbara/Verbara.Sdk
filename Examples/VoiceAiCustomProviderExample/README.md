# VoiceAiCustomProviderExample

Shows how to run the Asterisk.Sdk Voice AI pipeline with your own `SpeechRecognizer` and `SpeechSynthesizer` implementations instead of one of the built-in STT/TTS provider packages.

## What it demonstrates

- Subclassing `SpeechRecognizer` (→ `MyEchoRecognizer`) and `SpeechSynthesizer` (→ `MySilenceSynthesizer`) directly — no `Asterisk.Sdk.VoiceAi.Stt` / `.Tts` package required.
- Overriding the `ProviderName` virtual property with a stable literal so the pipeline's STT/TTS activity tags don't fall back to the (slightly slower, reflective) `GetType().Name` default.
- Registering the custom types as the `SpeechRecognizer` / `SpeechSynthesizer` DI services consumed by `AddVoiceAiPipeline<THandler>()`.

## Why the ProviderName override matters

Before v1.10.0 the pipeline tagged every utterance with `_stt.GetType().Name` / `_tts.GetType().Name` — a virtual dispatch per utterance. v1.10.0 introduced the `ProviderName` virtual property; the default preserves the old behavior (`=> GetType().Name`) for compatibility, but overriding with a literal makes the access effectively free (the JIT inlines the const string).

Measured on a Ryzen 9 9900X: override `0.012 ns` vs fallback `1.11 ns` (`~92x`). The absolute delta is small, but it keeps a hot-path instruction free of reflection and is explicit — consumers reading your code can see which provider is active without inspecting runtime types.

See `docs/research/benchmark-analysis.md` §1b for the full VoiceAiBenchmarks report.

## Run

```sh
dotnet run --project Examples/VoiceAiCustomProviderExample/
```

The AudioSocket server binds to `localhost:9092`. Point an Asterisk `ExternalMedia` channel at it (or run `VoiceAiExample`'s AudioSocket test client) to drive a conversation. `MyEchoRecognizer` will yield `"received N frame(s)"` as the transcript, `EchoConversationHandler` echoes it, and `MySilenceSynthesizer` plays 1 second of silence.

This is intentionally inert — replace `MyEchoRecognizer.StreamAsync` and `MySilenceSynthesizer.SynthesizeAsync` with calls to your real STT/TTS backend (gRPC, WebSocket, REST, on-device model, regional cloud, …).

## Files

| File | Role |
|------|------|
| `Program.cs` | Host setup: registers the custom STT/TTS + pipeline |
| `MyEchoRecognizer.cs` | Custom `SpeechRecognizer` with `ProviderName => "MyEcho"` |
| `MySilenceSynthesizer.cs` | Custom `SpeechSynthesizer` with `ProviderName => "MySilence"` |
| `EchoConversationHandler.cs` | Trivial `IConversationHandler` — echoes the transcript |

## See also

- [`src/Asterisk.Sdk.VoiceAi/README.md`](../../src/Asterisk.Sdk.VoiceAi/README.md) — `ProviderName` doc + observability stack
- [`Examples/VoiceAiExample/`](../VoiceAiExample/) — the built-in-provider version (Deepgram STT + ElevenLabs TTS)
- [`docs/guides/high-load-tuning.md`](../../docs/guides/high-load-tuning.md) — provider identification on the hot path

# Tasks — deflake-loopback-address-ambiguity

## 1. Production test seams (`src/`, test-only URI branches)

- [x] 1.1 `src/Verbara.Sdk.VoiceAi.Stt/Deepgram/DeepgramSpeechRecognizer.cs` — `_fakeServerPort` branch dials `ws://127.0.0.1:{port}`
- [x] 1.2 `src/Verbara.Sdk.VoiceAi.Stt/AssemblyAi/AssemblyAiSpeechRecognizer.cs` — same
- [x] 1.3 `src/Verbara.Sdk.VoiceAi.Stt/Cartesia/CartesiaSpeechRecognizer.cs` — same
- [x] 1.4 `src/Verbara.Sdk.VoiceAi.Stt/Speechmatics/SpeechmaticsSpeechRecognizer.cs` — same
- [x] 1.5 `src/Verbara.Sdk.VoiceAi.Tts/Deepgram/DeepgramSpeechSynthesizer.cs` — same
- [x] 1.6 `src/Verbara.Sdk.VoiceAi.Tts/Cartesia/CartesiaSpeechSynthesizer.cs` — same
- [x] 1.7 `src/Verbara.Sdk.VoiceAi.Tts/ElevenLabs/ElevenLabsSpeechSynthesizer.cs` — same
- [x] 1.8 `src/Verbara.Sdk.VoiceAi.Tts/Lmnt/LmntSpeechSynthesizer.cs` — `_fakeWsPort` branch (the seam behind the flaking `LmntSpeechSynthesizerWsTests`)
- [x] 1.9 Confirm no public API surface moved (`PublicAPI.Unshipped.txt` untouched) — the edits sit in branches reachable only from `internal` test-only constructors

## 2. Test fake servers and dial sites

- [x] 2.1 `Tests/Verbara.Sdk.VoiceAi.Stt.Tests/Deepgram/DeepgramFakeServer.cs` — `HttpListener` prefix on `127.0.0.1`, with a comment recording why the literal is required
- [x] 2.2 `Tests/Verbara.Sdk.VoiceAi.Tts.Tests/ElevenLabs/ElevenLabsFakeServer.cs` — prefix
- [x] 2.3 `Tests/Verbara.Sdk.VoiceAi.Tts.Tests/Speechmatics/SpeechmaticsFakeServer.cs` — prefix + `BaseUri`
- [x] 2.4 `Tests/Verbara.Sdk.VoiceAi.Tts.Tests/Lmnt/LmntFakeServer.cs` — prefix + `BaseUri`
- [x] 2.5 `Tests/Verbara.Sdk.VoiceAi.OpenAiRealtime.Tests/Internal/RealtimeFakeServer.cs` — prefix
- [x] 2.6 `Tests/Verbara.Sdk.VoiceAi.OpenAiRealtime.Tests/{Bridge/OpenAiRealtimeBridgeTests.cs,FunctionCalling/FunctionCallTests.cs}` — `OpenAiRealtimeBridge.BaseUri` dials `ws://127.0.0.1:{port}/`
- [x] 2.7 Leave the per-fake port-allocation retry loops in place — with the literal, a lost race now surfaces as `EADDRINUSE`, which is exactly what those loops already handle

## 3. Regression guard (`Tests/Verbara.Sdk.Governance.Tests/`)

- [x] 3.1 Add `LoopbackSeamScanner` — deterministic source scan (no network, no timing, no Docker), following the `SyncFenceScanner` idiom
- [x] 3.2 Add the guard test that fails the build when a fake-server bind site or a test-only dialling seam reintroduces `localhost`, naming the offending file in the failure message
- [x] 3.3 Detector unit tests: true positives — interpolated-port `ws://localhost:{port}` seams, `BaseUri` properties and `HttpListener` prefixes are reported, with a 1-based line number
- [x] 3.4 Detector unit tests: false-positive immunity — `http://localhost:8088` (ARI), `http://localhost:4317` (OTel), the Toxiproxy API default, and `localhost` in comments / XML doc / plain string literals are NOT reported
- [x] 3.5 Liveness self-test — the scan must walk more than a conservative floor of files, so an empty enumeration cannot read as green

## 4. Verification

- [x] 4.1 `dotnet build Verbara.Sdk.slnx` — zero warnings (`TreatWarningsAsErrors`) → 0 warnings, 0 errors
- [x] 4.2 `dotnet test Verbara.Sdk.slnx` green on the unit lane → **3,007 passed, 0 failed, 0 warnings**.
      Note the filter: CI uses `Category!=Functional&Category!=Integration&Category!=Realtime&Category!=Spike`
      (`ci.yml`), **four** exclusions. `CLAUDE.md` documents only the first two; running the documented
      command pulls the Docker-dependent `Realtime` tests into the unit lane
- [x] 4.3 Repeat-run-under-load protocol: the TTS suite run repeatedly while the CPU is saturated no longer produces `WebSocketException: The server returned status code '200' when status code '101' was expected`, and `DeepgramSpeechSynthesizerTests.SynthesizeAsync_ShouldSendRequestToCorrectPath` / `LmntSpeechSynthesizerWsTests.SynthesizeAsync_ShouldSendTextMessage_WithCorrectText` stay green
      → **20/20 green** under 32 spinners on 24 cores. The same harness reproduced the fault
      **1 time in 15** before the fix, which is how the root cause was found
- [x] 4.4 `Tests/Verbara.Sdk.Governance.Tests` green — the new guard reports zero violations on the converted tree
      → 58/58 green. Negative-tested independently: reverting the `LmntSpeechSynthesizer` seam to
      `localhost` fails the guard with `[LoopbackInterpolation]` naming the exact file and line;
      restoring it returns the suite to green
- [x] 4.5 `openspec validate deflake-loopback-address-ambiguity --type change --strict` clean
- [ ] 4.6 CI green on the PR, zero warnings

# Tasks — wiremock-http-provider-substrate

Execution order is load-bearing: the decision (§1), the substrate (§2) and the recording protocol
(§3) all land **before** any provider suite moves (§4–§6). Migrating a provider onto an unpinned
substrate, or committing a capture before the redaction rule exists, is the failure mode this
sequencing exists to prevent.

Per the repo convention, execution uses Subagent-Driven Development with FCM batching:
Phase A = §1–§3 (foundation, batched), Phase B = §4 first provider + §5 (focused),
Phase C = §4 remaining providers + §6–§8 (batched).

## 1. Decision and dependency admission

- [ ] 1.1 Land `docs/decisions/0041-wiremock-as-http-provider-test-substrate.md` and move it from
      `Proposed` to `Accepted` (or supersede it) before any suite migrates
- [x] 1.2 Confirm WireMock.NET's license clears the `dependency-review` deny-list
      (AGPL / GPL / SSPL, `.github/workflows/dependency-review.yml`) — a denied license kills the
      change here, not after six migrations
      — **CLEARED.** `WireMock.Net` 2.13.0 declares SPDX `Apache-2.0`. No node in the resolved graph
      is AGPL/GPL/SSPL. Non-SPDX outliers, none denied: `XPath2` 1.1.5 is MS-PL; `Fare`,
      `SimMetrics.Net`, the `dotnet/corefx` shims and the ASP.NET Core 2.x packages carry a
      `licenseUrl` rather than an expression, so `dependency-review` sees them as *unknown*, not
      denied. `dotnet list package --vulnerable --include-transitive` reports **0 vulnerable
      packages**, so `fail-on-severity: high` also passes.
- [x] 1.3 Add the `PackageVersion` pin to `Directory.Packages.props` (ADR-0004: every version pinned
      centrally); do **not** add a `PackageReference` to any `src/**` project
      — pinned `WireMock.Net` 2.13.0 in the test-dependency group. **The full metapackage is
      deliberate over `WireMock.Net.Minimal`:** "Minimal" is a misnomer — it pulls
      OpenApiParser/NSwag/NJsonSchema/Scriban/TinyMapper (115 packages against the metapackage's 125
      at lower bound) and resolves `Newtonsoft.Json` 9.0.1 + `System.Text.Encodings.Web` 4.7.1, which
      carry 2 HIGH and 1 CRITICAL advisory respectively and would fail `dependency-review`.
- [x] 1.4 Verify the transitive graph WireMock.NET pulls in does not collide with existing pins
      (`NU1605` is fatal here — `TreatWarningsAsErrors=true`, and `NU1605` is not in `NoWarn`)
      — **no collision.** Restore and build are clean (0 warnings). Every central pin is at or above
      what WireMock requests, so no downgrade is possible: `Microsoft.Extensions.*` 10.0.10 vs 10.0.0,
      `Microsoft.CodeAnalysis.CSharp` 5.6.0 vs 4.8.0, `OpenTelemetry` 1.17.0 vs 1.15.3. Unification
      raises `Newtonsoft.Json` to 13.0.4. **Cost of record: 156 transitive packages.** Reachable from
      the 4 projects that reference `TestInfrastructure` — the two target suites plus
      `FunctionalTests`/`IntegrationTests`, which are already the Docker-bound slow lane.

## 2. Shared substrate in `Tests/Verbara.Sdk.TestInfrastructure`

- [x] 2.1 Add the WireMock `PackageReference` to `Tests/Verbara.Sdk.TestInfrastructure` only, and
      confirm `IsPackable=false` / `IsAotCompatible=false` already apply from `Directory.Build.props`
      — added. Both flags are set explicitly in the project itself, so D7's confinement holds.
      Reachability audited: exactly 4 projects reference `TestInfrastructure` — the two target suites
      (`VoiceAi.Stt.Tests`, `VoiceAi.Tts.Tests`) plus `FunctionalTests` and `IntegrationTests`.
      `dotnet build Verbara.Sdk.slnx` is green at 0 warnings.
- [ ] 2.2 Add a shared `HttpProviderMockServer` fixture: loopback-bound, free-port allocation with
      retry (mirroring the existing fakes' port-conflict handling under parallel test execution),
      deterministic dispose
- [ ] 2.3 Make request matching strict by default — method + path + query + required headers — so a
      misrouted or unauthenticated request fails to match instead of receiving a canned response
- [ ] 2.4 Add a recording loader that reads a capture from the suite's `Recordings/` tree and
      registers it as a stub response (JSON body for STT, byte stream for TTS)
- [ ] 2.5 Add helpers for the shapes the current handler cannot express: error-status responses,
      status-code sequences, chunked/streamed bodies
- [ ] 2.6 Measure fixture setup/teardown cost against the socket-less `MockHttpMessageHandler`
      baseline and record the per-suite delta (ADR-0038 CI wall-clock budget)

## 3. Recording capture and redaction protocol

- [ ] 3.1 Write the capture procedure into `docs/guides/` — how a real provider response is captured,
      what is stripped, and how provenance (provider, endpoint, capture date, source-audio origin) is
      recorded next to the capture
- [ ] 3.2 Fix the redaction rule: no API keys, bearer tokens or signed URLs; no account/tenant/
      project/billing identifiers; no request/session identifiers that correlate to a real account
- [ ] 3.3 Fix the source-audio rule: synthetic or public-domain audio only — never customer audio,
      never an identifiable person's voice (public MIT repo, verbara-meta/ADR-0005)
- [ ] 3.4 Confirm each provider's terms of service permit committing a captured response, and record
      the finding per provider
- [ ] 3.5 Decide the per-file size cap for binary TTS captures and add the Git-LFS rule to
      `.gitattributes` if the cap is exceeded (only `*.onnx` is LFS-tracked today)
- [ ] 3.6 Add a repo check that fails if a file under a `Recordings/` tree matches a
      credential-shaped pattern, so the redaction rule is enforced and not merely documented

## 4. HTTP-transport provider migrations — 6 surfaces

Each item: capture → redact → commit recording → port the suite to the shared fixture → keep the
existing `*_ShouldAbort_WhenCancelled` assertion verbatim → confirm no coverage-floor regression.

- [ ] 4.1 **OpenAI Whisper** (STT, `Tests/Verbara.Sdk.VoiceAi.Stt.Tests/Whisper/WhisperSpeechRecognizerTests.cs`)
      — multipart POST; **first migration: it establishes the pattern the other five copy**
- [ ] 4.2 **Azure OpenAI Whisper** (STT, `Tests/Verbara.Sdk.VoiceAi.Stt.Tests/Whisper/AzureWhisperSpeechRecognizerTests.cs`)
      — deployment-path URL + `api-key` header (not bearer)
- [ ] 4.3 **Google Speech-to-Text** (STT, `Tests/Verbara.Sdk.VoiceAi.Stt.Tests/Google/`) — JSON POST to
      `speech:recognize`; the API key rides in the query string, so it MUST be placeholdered in both
      the stub and any recorded request metadata
- [ ] 4.4 **Azure TTS** (`Tests/Verbara.Sdk.VoiceAi.Tts.Tests/Azure/`) — SSML POST returning a real
      audio stream; replaces `new byte[320]` zeros with recorded codec bytes, exercising the
      frame-chunking path for the first time
- [ ] 4.5 **Speechmatics TTS** (`Tests/Verbara.Sdk.VoiceAi.Tts.Tests/Speechmatics/`) — retires
      `SpeechmaticsFakeServer` (`HttpListener`)
- [ ] 4.6 **LMNT HTTP path** (`Tests/Verbara.Sdk.VoiceAi.Tts.Tests/Lmnt/`, `LmntTransport.Http`) —
      retires `LmntHttpFakeServer` only; `LmntWsFakeServer` stays (see 5.4)

## 5. WebSocket-transport providers — explicitly NOT migrated, recordings only

WireMock.NET is an HTTP/1.1 request-matching server; bidirectional WebSocket framing is not its
contract. These 8 surfaces keep `Verbara.Sdk.TestInfrastructure`'s `WebSocketTestServer` and their
per-provider protocol fakes. Only the **payloads** change: hand-authored minimal JSON is replaced by
recorded provider frames.

- [ ] 5.1 STT **Deepgram** (`Deepgram/`) — re-seed `DeepgramFakeServer` from a recorded `Results`
      frame carrying the full field set (`speech_final`, `channel_index`, `duration`, `start`,
      `metadata`, word arrays), replacing `BuildResultJson`'s five-field hand-authored object
- [ ] 5.2 STT **AssemblyAI** (`AssemblyAi/`) — re-seed `AssemblyAiFakeServer` from recorded turn frames
- [ ] 5.3 STT **Cartesia** (`Cartesia/`) — re-seed `CartesiaFakeServer` from recorded frames
- [ ] 5.4 STT **Speechmatics** (`Speechmatics/`) — re-seed `SpeechmaticsFakeServer` from recorded
      `AddPartialTranscript` / `AddTranscript` frames
- [ ] 5.5 TTS **Cartesia** (`Cartesia/`) — re-seed `CartesiaFakeServer` with recorded binary frames
- [ ] 5.6 TTS **Deepgram** (`Deepgram/`) — re-seed `DeepgramTtsFakeServer`, including the real
      `warning` / `metadata` / `flushed` control frames the suite already filters
- [ ] 5.7 TTS **ElevenLabs** (`ElevenLabs/`) — re-seed `ElevenLabsFakeServer`, including real
      alignment messages
- [ ] 5.8 TTS **LMNT WebSocket path** (`Lmnt/`, default `LmntTransport.WebSocket`) — `LmntWsFakeServer`
      stays; only its frames become recorded. The suite ends up **split across both substrates by
      transport**, in one file
- [ ] 5.9 Record the not-migrated verdict per provider in the suite (a one-line comment naming the
      transport) so the omission cannot later be read as an oversight

## 6. Convergence and cleanup

- [ ] 6.1 Delete `Tests/Verbara.Sdk.VoiceAi.Stt.Tests/Helpers/MockHttpMessageHandler.cs` once no STT
      suite references it
- [ ] 6.2 Delete `Tests/Verbara.Sdk.VoiceAi.Tts.Tests/Helpers/MockHttpMessageHandler.cs` once no TTS
      suite references it (it is a divergent second copy — different constructor signature)
- [ ] 6.3 Delete the retired `HttpListener` HTTP fakes (`SpeechmaticsFakeServer`, `LmntHttpFakeServer`)
- [ ] 6.4 Confirm `WebSocketTestServer` and every WebSocket protocol fake are untouched in behaviour

## 7. Documentation

- [ ] 7.1 Update `docs/guides/` with the provider-suite testing convention: which substrate a new
      provider uses, chosen by transport
- [ ] 7.2 `CHANGELOG.md` entry under the test/tooling section (no `src/**` change, so no
      `Directory.Build.props` `PackageVersion` bump and no release task in this change)
- [ ] 7.3 Confirm every artifact is free of absolute machine paths, credentials and private-repo
      content before the PR (verbara-meta/ADR-0005)

## 8. Verification

- [ ] 8.1 `dotnet test Tests/Verbara.Sdk.VoiceAi.Stt.Tests` — green
- [ ] 8.2 `dotnet test Tests/Verbara.Sdk.VoiceAi.Tts.Tests` — green
- [ ] 8.3 `dotnet test Verbara.Sdk.slnx --filter "Category!=Functional&Category!=Integration"` — green,
      **zero warnings** (`TreatWarningsAsErrors=true`, `WarningLevel=9999`)
- [ ] 8.4 `dotnet build Verbara.Sdk.slnx` — zero warnings; the `BanDapperPackageReferences` guard and
      the BannedApi analyzers still pass
- [ ] 8.5 Every `StreamAsync_ShouldAbort_WhenCancelled` / `SynthesizeAsync_ShouldAbort_WhenCancelled`
      test still green and still deterministic under the repeat-run protocol — the `test-determinism`
      contract is unchanged by the substrate swap
- [ ] 8.6 Coverage floor holds (`scripts/check-coverage-floor.py`) — no provider loses coverage when
      its fake is deleted
- [ ] 8.7 `dotnet pack -c Release` — no produced `.nupkg` declares WireMock.NET as a dependency
- [ ] 8.8 `aot-validate` workflow green — the test-only substrate never enters an AOT publish graph
- [ ] 8.9 CI green end to end (`pull_request` + `merge_group`), with the wall-clock delta versus the
      pre-change baseline recorded and judged acceptable under ADR-0038
- [ ] 8.10 `openspec validate --change wiremock-http-provider-substrate --strict` passes

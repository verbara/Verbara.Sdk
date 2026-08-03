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
- [x] 2.2 Add a shared `HttpProviderMockServer` fixture: loopback-bound, free-port allocation with
      retry (mirroring the existing fakes' port-conflict handling under parallel test execution),
      deterministic dispose
      — `Tests/Verbara.Sdk.TestInfrastructure/Http/HttpProviderMockServer.cs`. TcpListener probe on
      `IPAddress.Loopback` then bind `http://127.0.0.1:{port}/`, 5 attempts. **`StartTimeout` is
      lowered to 5 s on purpose:** WireMock only surfaces a bind failure once the budget expires, so
      the 10 s default would cost 10 s per lost port race (measured). Cold start is ~80 ms.
      `BaseAddress` is built from the IPv4 literal — WireMock's own `Url` property reports the host
      as `localhost` (verified), which is exactly the ADR-0044 ambiguity, so it is never surfaced.
      Dispose = `Stop()` + `Dispose()`; the port is rebindable in ~7 ms (measured).
- [x] 2.3 Make request matching strict by default — method + path + query + required headers — so a
      misrouted or unauthenticated request fails to match instead of receiving a canned response
      — `HttpProviderRequest`. Method + path use `ExactMatcher` (case-sensitive, no wildcards);
      declared query params and headers must match exactly; **undeclared query params break the
      match** (exhaustive, via a params predicate), with `AllowingUndeclaredQueryParameters()` as the
      explicit opt-out. Headers are inclusive, not exhaustive — `HttpClient` always adds `Host` /
      `Content-Length`, so an exact header set would assert the transport, not the provider contract.
      `AllowPartialMapping=false` keeps an unmatched request a 404.
- [x] 2.4 Add a recording loader that reads a capture from the suite's `Recordings/` tree and
      registers it as a stub response (JSON body for STT, byte stream for TTS)
      — `ProviderRecordings` + `HttpProviderMockServer.StubRecordedJson` / `StubRecordedBytes`.
      Discovery walks up from `AppContext.BaseDirectory` to the suite's project directory, so it
      works whether captures are copied to the output directory or left in the source tree.
      `[CallerFilePath]` is deliberately avoided (CI sets `ContinuousIntegrationBuild=true`, which
      remaps it to `/_/…`). Resolution is lazy, so suites with no `Recordings/` folder still start.
- [x] 2.5 Add helpers for the shapes the current handler cannot express: error-status responses,
      status-code sequences, chunked/streamed bodies
      — `HttpProviderResponse.Status(...)`, `HttpProviderMockServer.StubSequence(...)` (WireMock
      scenario states; the **last response is sticky** via a self-loop, so a retry policy running
      past the end of the sequence sees the terminal outcome instead of wrapping to the first), and
      `HttpProviderResponse.ChunkedBytes(...)`. The chunked shape uses a custom `IResponseProvider`
      writing straight to the Kestrel response stream: WireMock's own streaming body (`WithSseBody`)
      is UTF-8 text and corrupts every byte above 0x7F (verified), which is fatal for codec bytes.
- [ ] 2.6 Measure fixture setup/teardown cost against the socket-less `MockHttpMessageHandler`
      baseline and record the per-suite delta (ADR-0038 CI wall-clock budget)

## 3. Recording capture and redaction protocol

- [x] 3.1 Write the capture procedure into `docs/guides/` — how a real provider response is captured,
      what is stripped, and how provenance (provider, endpoint, capture date, source-audio origin) is
      recorded next to the capture
      — `docs/guides/provider-recording-protocol.md` (indexed in `docs/guides/README.md`). Layout:
      `Tests/<Project>/Recordings/<provider-slug>/<scenario-slug>.<ext>` plus a sibling
      `<scenario-slug>.provenance.json`. **Provenance format: a per-capture JSON sidecar**, schema
      `verbara.recording-provenance/1`, required keys `schema`/`class`/`provider`/`product`/
      `endpoint`/`api_version`/`captured_utc`/`source_audio`/`redaction`/`terms`. A sidecar (not
      front-matter, not a directory manifest) because a binary `.wav` cannot carry front-matter, and
      a manifest drifts from the files it lists. `class` is `recorded` | `synthetic`, which is where
      D4's synthetic labelling lands.
- [x] 3.2 Fix the redaction rule: no API keys, bearer tokens or signed URLs; no account/tenant/
      project/billing identifiers; no request/session identifiers that correlate to a real account
      — guide §4. Adds the enforced part the rule needed: a placeholder table (`REDACTED-API-KEY`,
      `Bearer REDACTED-TOKEN`, the nil GUID for any correlating id, `<region>`/`<resource>` host
      segments) that the §3.6 guard's allowlist recognizes, so a correctly redacted capture is green
      by construction. Also bans "redaction by truncation".
- [x] 3.3 Fix the source-audio rule: synthetic or public-domain audio only — never customer audio,
      never an identifiable person's voice (public MIT repo, verbara-meta/ADR-0005)
      — guide §6. Extends the rule to two adjacent cases the one-liner left open: the *spoken text*
      of a TTS capture must be fictional (no real names / numbers / references), and the ban on an
      identifiable voice explicitly includes the capturer's own — a public Git history cannot honour
      a withdrawal of biometric consent.
- [x] 3.4 Confirm each provider's terms of service permit committing a captured response, and record
      the finding per provider
      — guide §7, checked 2026-08-02, verdict + clause + residual uncertainty per provider.
      **Azure OpenAI Whisper** and **Azure TTS**: `permitted` — Microsoft Product Terms, read
      first-hand, "Output Content is Customer Data. Microsoft does not own Customer's Output
      Content"; Azure TTS is `permitted-with-conditions` for the AI Code of Conduct v4.0 (2026-05-01)
      synthetic-voice disclosure duty, which the sidecar discharges. **Google STT**: `permitted` —
      Service Specific Terms AI/ML "Generated Output is Customer Data … Google does not assert any
      ownership rights", plus GCP ToS §5.1; flagged: the AI/ML Services *enumeration* could not be
      read verbatim, so confirm Speech-to-Text is listed before the first capture. **OpenAI
      Whisper**: `permitted-with-conditions` (customer owns Output; publication policy wants
      attribution + disclosure) — flagged: `openai.com` returns HTTP 403 to automated fetchers, so
      the clause text came from search indexing, not a direct read; a human must confirm on the live
      page. **Speechmatics TTS**: `permitted-with-conditions` — ToS §10.3 assigns output IP to the
      customer but is written about *Transcripts*; synthesized audio rides only on §10.5's
      "derivatives of your content", so it is permitted by inference, not by express grant.
      **LMNT HTTP**: **`not-cleared`** — the ToS (2023-06-12) has no output-rights clause at all and
      the AUP (2023-08-28) restricts sharing synthesized speech outside the entity. Conservative
      fallback specified: commit the response *envelope* only (status, headers, media type, content
      length, chunk boundaries) and pair it with a locally built `synthetic` body — §4.6 still gets
      strict matching and real frame-chunking without redistributing LMNT audio.
- [x] 3.5 Decide the per-file size cap for binary TTS captures and add the Git-LFS rule to
      `.gitattributes` if the cap is exceeded (only `*.onnx` is LFS-tracked today)
      — **cap: 256 KiB (262 144 bytes) per binary capture** (guide §8). 819 × 320-byte frames ≈ 16 s
      of 8 kHz PCM — an order of magnitude past what the frame-chunking assertion needs, while the
      whole surface stays ~3 MiB at the ceiling in a public repo where every clone carries every
      version forever. It is also a compliance instrument: Azure forbids generating Output Content as
      synthetic training data for a similar service and Google forbids using Generated Output to
      improve similar models, so seconds-not-minutes keeps the tree a fixture set, not a voice
      corpus. Text captures: 64 KiB advisory smell threshold. **LFS is deliberately not the default**
      — a job that does not fetch LFS gets a pointer instead of the fixture — so at/below the cap
      captures are ordinary blobs and only the reviewed over-cap path `.../Recordings/large/` is
      LFS-tracked. `.gitattributes` also marks capture codecs `binary` and JSON captures `text
      eol=lf`; the LFS line is written last so last-match-wins keeps the canonical
      `filter/diff/merge=lfs -text` set (verified with `git check-attr` against the `*.onnx` row).
- [x] 3.6 Add a repo check that fails if a file under a `Recordings/` tree matches a
      credential-shaped pattern, so the redaction rule is enforced and not merely documented
      — `scripts/check-recording-redaction.py` (stdlib only, `[repo-root]` positional, `::error::`
      annotations, exit 0/1 — the `scripts/check-*.py` convention). 14 patterns; scans **binary as
      well as text** (an API key fits in a WAV `LIST` chunk); **never prints the matched value**;
      placeholder-aware; refuses to scan an unfetched Git-LFS pointer instead of false-greening; and
      runs a **self-check** first — every pattern must match its canary and stay silent on its
      redacted counterpart, so a regex edited into uselessness fails loudly (the coverage guards'
      `min_scanned_files` liveness posture). 16 unit tests in
      `scripts/tests/test_check_recording_redaction.py`, picked up by the existing
      `python3 -m unittest discover scripts/tests` CI step. Wired as a step in the **`audit-test-asserts`**
      job — already the required `Tests/**`-tree hygiene context; a new job would report as
      non-required, i.e. non-blocking, which is the one thing a credential guard must not be.

## 4. HTTP-transport provider migrations — 6 surfaces

Each item: capture → redact → commit recording → port the suite to the shared fixture → keep the
existing `*_ShouldAbort_WhenCancelled` assertion verbatim → confirm no coverage-floor regression.

- [ ] 4.1 **OpenAI Whisper** (STT, `Tests/Verbara.Sdk.VoiceAi.Stt.Tests/Whisper/WhisperSpeechRecognizerTests.cs`)
      — multipart POST; **first migration: it establishes the pattern the other five copy**.
      **⚠ Human terms read required before committing the capture (ADR-0041 D11):** `openai.com`
      serves HTTP 403 to automated fetchers, so §3.4's clause text is search-indexed, not read
      first-hand. Open the live Services Agreement / Business Terms / sharing-and-publication policy,
      confirm the wording, and record the confirmation in the sidecar's `terms.checked_utc`.
- [ ] 4.2 **Azure OpenAI Whisper** (STT, `Tests/Verbara.Sdk.VoiceAi.Stt.Tests/Whisper/AzureWhisperSpeechRecognizerTests.cs`)
      — deployment-path URL + `api-key` header (not bearer)
- [ ] 4.3 **Google Speech-to-Text** (STT, `Tests/Verbara.Sdk.VoiceAi.Stt.Tests/Google/`) — JSON POST to
      `speech:recognize`; the API key rides in the query string, so it MUST be placeholdered in both
      the stub and any recorded request metadata.
      **⚠ Verify one clause before committing the capture (ADR-0041 D11):** §3.4 read the AI/ML
      "Generated Output is Customer Data" grant verbatim but could not retrieve the enumeration of
      which products count as *AI/ML Services*. Confirm Speech-to-Text is listed there; if it is not,
      the verdict drops to `not-cleared` and D11's envelope fallback applies. Separately, never put
      comparative accuracy or latency numbers in a sidecar or `Recordings` README — that engages the
      Service Specific Terms §7 benchmarking clause.
- [ ] 4.4 **Azure TTS** (`Tests/Verbara.Sdk.VoiceAi.Tts.Tests/Azure/`) — SSML POST returning a real
      audio stream; replaces `new byte[320]` zeros with recorded codec bytes, exercising the
      frame-chunking path for the first time
- [ ] 4.5 **Speechmatics TTS** (`Tests/Verbara.Sdk.VoiceAi.Tts.Tests/Speechmatics/`) — retires
      `SpeechmaticsFakeServer` (`HttpListener`)
- [ ] 4.6 **LMNT HTTP path** (`Tests/Verbara.Sdk.VoiceAi.Tts.Tests/Lmnt/`, `LmntTransport.Http`) —
      retires `LmntHttpFakeServer` only; `LmntWsFakeServer` stays (see 5.4).
      **⚠ Envelope capture only — ADR-0041 D11.** The §3.4 terms review returned `not-cleared` for
      LMNT: its ToS has no clause on rights in generated audio and its AUP restricts sharing
      synthesized speech outside the capturing entity. Do **not** commit LMNT audio bytes. Capture the
      response envelope (status, headers, media type, content length, chunk boundaries) as the
      `recorded` artifact and pair it with a locally built `synthetic` body in the same codec. The
      migration still lands — strict matching, real status/headers, real byte lengths through
      frame-chunking — it just does not redistribute LMNT's speech.

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

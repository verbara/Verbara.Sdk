# Tasks — wiremock-http-provider-substrate

Execution order is load-bearing: the decision (§1), the substrate (§2) and the recording protocol
(§3) all land **before** any provider suite moves (§4–§6). Migrating a provider onto an unpinned
substrate, or committing a capture before the redaction rule exists, is the failure mode this
sequencing exists to prevent.

Per the repo convention, execution uses Subagent-Driven Development with FCM batching:
Phase A = §1–§3 (foundation, batched), Phase B = §4 first provider + §5 (focused),
Phase C = §4 remaining providers + §6–§8 (batched).

## 1. Decision and dependency admission

- [x] 1.1 Land `docs/decisions/0041-wiremock-as-http-provider-test-substrate.md` and move it from
      `Proposed` to `Accepted` (or supersede it) before any suite migrates
      — **Accepted 2026-08-09.** The ordering this task exists to enforce was violated: §4.4 migrated
      on 2026-08-03 while the ADR was still `Proposed`. That turned out to be the useful accident —
      the migration disproved the proposal's "no `src/**` change" claim, and because the ADR was still
      editable, the correction landed **in** it as **D12** (a provider that builds its own URL takes an
      `internal` origin-only seam) instead of needing a superseding ADR. Had 1.1 been done in order,
      ADR-0041 would have frozen a false consequence. Also folded in before freezing: an acceptance
      note recording D10 cleared, D9 measured at +30 ms projected, and the
      `TestInfrastructure.Http` split with the coverlet 80.42% → 61.96% evidence. Index updated in
      `docs/decisions/README.md`. **The lesson is the ordering rule, not an excuse to repeat it:**
      §4.1–§4.6 now migrate under a frozen ADR, so any further correction costs a new ADR.
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
      raises `Newtonsoft.Json` to 13.0.4. **Cost of record: 156 transitive packages**, confined to
      the two suites that reference `Verbara.Sdk.TestInfrastructure.Http` (`VoiceAi.Stt.Tests`,
      `VoiceAi.Tts.Tests`). They are deliberately kept out of `FunctionalTests`/`IntegrationTests` —
      see 2.1: reaching those projects breaks coverlet instrumentation, and that is a coverage-gate
      failure, not a preference.

## 2. Shared substrate in `Tests/Verbara.Sdk.TestInfrastructure`

- [x] 2.1 Add the WireMock `PackageReference` to `Tests/Verbara.Sdk.TestInfrastructure` only, and
      confirm `IsPackable=false` / `IsAotCompatible=false` already apply from `Directory.Build.props`
      — **the task text's placement is wrong and CI proved it.** WireMock lives in a new
      `Tests/Verbara.Sdk.TestInfrastructure.Http` project referenced only by `VoiceAi.Stt.Tests` and
      `VoiceAi.Tts.Tests`. Both flags are set explicitly there, so D7's confinement holds.

      **Why the split is load-bearing, not cosmetic.** WireMock pulls ASP.NET Core dependencies, so
      any project referencing it acquires a `FrameworkReference` to `Microsoft.AspNetCore.App`. From
      that point ~30 `Microsoft.Extensions.*` assemblies become framework-provided and stop being
      copied to the output directory (measured: `FunctionalTests` output went 85 → 122 DLLs, losing
      all 29 `Microsoft.Extensions.*`). Coverlet's Mono.Cecil resolver only searches the module's own
      directory, so it then throws `CecilAssemblyResolutionException` on
      `Microsoft.Extensions.Logging.Abstractions` and **silently skips instrumenting** every module
      that references it — emitted as a warning, with the test run still green.

      Because `TestInfrastructure` is referenced by `FunctionalTests` and `IntegrationTests`, whose
      output carries `Ami`/`Ari`/`Sessions`/`AudioSocket`, putting WireMock there cost those
      assemblies their instrumentation: measured line coverage fell **80.42% → 61.96%** with all
      3 020 tests still passing, and the `Coverage Ratchet` gate failed on PR #149. Bisected by
      removing the package reference (`FunctionalTests` coverage returned to `Ami=5936, Live=114`)
      and confirmed by the split (same `Ami=5936, Live=114` with WireMock still in the tree).

      The split also confines the 156-package graph to the two suites that need it.
      `[*.TestInfrastructure.Http]*` is added to `coverlet.runsettings`'s exclude list so the
      fixture itself is not measured. `dotnet build Verbara.Sdk.slnx` is green at 0 warnings.
- [x] 2.2 Add a shared `HttpProviderMockServer` fixture: loopback-bound, free-port allocation with
      retry (mirroring the existing fakes' port-conflict handling under parallel test execution),
      deterministic dispose
      — `Tests/Verbara.Sdk.TestInfrastructure.Http/HttpProviderMockServer.cs` (see 2.1 for why it is not
      in `TestInfrastructure`). TcpListener probe on
      `IPAddress.Loopback` then bind `http://127.0.0.1:{port}/`, 5 attempts. **`StartTimeout` is
      lowered to 5 s on purpose:** WireMock only surfaces a bind failure once the budget expires, so
      the 10 s default would cost 10 s per lost port race (measured). Cold start is ~80 ms.
      `BaseAddress` is built from the IPv4 literal — WireMock's own `Url` property reports the host
      as `localhost` (verified), which is exactly the ADR-0044 ambiguity, so it is never surfaced.
      Dispose = `Stop()` + `Dispose()`, guarded by a `_disposed` flag so it is idempotent and the
      mutating members throw `ObjectDisposedException` afterwards.

      **Nothing reached through the port is assertable, and it took two CI failures to accept that.**
      The dispose test went through three versions. (1) "the freed port is immediately rebindable" —
      failed ~5% under multi-process load; measured over 720 cycles in 6 processes, every refused
      rebind became bindable 25 ms later (never-recovered = 0) and one refusal was a provable
      cross-process steal, so there is no leak — Kestrel's dispose returns before the kernel finishes
      releasing the socket. (2) "a post-dispose request throws" — failed in `merge_group`: the
      pre-dispose client reuses a drained keep-alive connection (1 in 900 measured), and a *fresh*
      client still fails because `HttpClient` does not throw on 4xx, so a sibling fixture that grabs
      the freed port and answers 404 is a valid response. (3) the shipped version asserts this
      object's own state, with no network involved. The port is a shared OS resource — the
      resource-identity lesson of ADR-0044 — and the acquire side already absorbs the same latency
      via `PortAllocationAttempts`.
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
- [x] 2.6 Measure fixture setup/teardown cost against the socket-less `MockHttpMessageHandler`
      baseline and record the per-suite delta (ADR-0038 CI wall-clock budget)
      — measured on an otherwise idle machine, 40 iterations after 5 warm-ups:

      | Shape | `MockHttpMessageHandler` | `HttpProviderMockServer` | delta |
      |---|---|---|---|
      | construct + dispose | 0.000 ms | 0.610 ms | +0.610 ms |
      | construct + 1 request + dispose | 0.001 ms | 1.325 ms | +1.324 ms |

      **The ratio is meaningless and the absolute number is the answer.** The baseline never opens a
      socket, so the multiplier is ~1000x against a denominator of one microsecond. What ADR-0038
      budgets is wall-clock: at one fixture per test across the 23 tests on the six migrating
      surfaces (Whisper 4, AzureWhisper 4, Google 6, Azure TTS 4, Speechmatics 3, LMNT HTTP 2),
      **the projected total is +30 ms**. That is far below the noise floor of the unit lane, which
      runs 3 020 tests. Caveat for honesty: this is the steady-state cost — the *first* fixture in an
      assembly additionally pays WireMock/Kestrel init, ~80 ms once per assembly, and both suites
      already pay it. The rollout stop-condition in D9 is not approached.

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

      **Amended 2026-08-09 during §4.1/§4.2:** the guide described the procedure but nothing
      executed it, so the Azure TTS capture was done by hand and the request shape had to be
      re-derived. `scripts/capture-provider-recording.py` now performs steps 4–8 for the Whisper
      surfaces — it issues the exact multipart the SDK issues, redacts, normalizes, writes the
      sidecar and enforces the cap; steps 1–3 and 9–10 stay human, since no tool can re-read a
      terms page or revoke a key. 36 unit tests, picked up by the existing
      `python3 -m unittest discover scripts/tests` CI step. Adding a provider is one `*_plan`
      function.

      **Amended again 2026-08-09 — guide step §3.3 (the credential rule).** It read "use a throwaway
      credential, revoked immediately afterwards", which is not what this repository does: captures
      run against a standing capture-only account, and will keep doing so. A written rule nobody
      follows protects nothing, so the step was rewritten to state the actual invariant — *capture
      with a credential that has never seen production*, because a key with no access to production
      data cannot leak production identifiers through a response body — and then to accept **two**
      ways of meeting it: a throwaway key, or a standing capture-only account holding no production
      or customer data. The standing account is now the recommended route when captures recur, on the
      explicit grounds that per-capture create-and-revoke friction is what eventually tempts someone
      to reach for a production key. Its obligations are spelled out (credentials in a local env file
      outside the working tree, distinguishing variable prefix, rotate on suspected exposure) and §4
      is restated as unchanged: redaction is what makes a capture safe to publish, whichever
      credential produced it. The amendment names no path to any local secrets file (§7.3).
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
      Whisper**: `permitted-with-conditions` — **upgraded to a first-hand read on 2026-08-03** via
      OpenAI's own CDN PDF (`cdn.openai.com/osa/openai-services-agreement.pdf`, `ONLINE v.010126`),
      which does not 403: §4.1 assigns all right/title/interest in Output to the customer and §3.3
      carries no publication restriction. Residual: the incorporated Sharing and Publication Policy
      still 403s, but it imposes conditions the sidecar already discharges, not a prohibition. **Speechmatics TTS**: `permitted-with-conditions` — ToS §10.3 assigns output IP to the
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

- [x] 4.1 **OpenAI Whisper** (STT, `Tests/Verbara.Sdk.VoiceAi.Stt.Tests/Whisper/WhisperSpeechRecognizerTests.cs`)
      — multipart POST. **No longer the first migration** — Azure TTS (4.4) took that role while
      this provider's terms gate was open, and the pattern is established there.
      — **done.** Capture: `Recordings/openai-whisper/transcribe-short-es-co.json`, 124 bytes.
      **The capture immediately paid for itself:** the real response is
      `{"text": …, "usage": {"type": "duration", "seconds": 4}}` — the `usage` object is a field
      the SDK does not model, and the hand-authored fixture was a bare `{"text":"hola mundo"}`.
      A parser that threw on an unmodelled sibling passed before and fails now. No `src/**` seam
      needed: `WhisperOptions.Endpoint` is the full request URI, exactly as D12 predicted.
      Suite 4 tests → 7, adding the unmatched-request shape (wrong bearer token), an error-status
      response, and an assertion that the multipart body really carries a RIFF/WAVE payload —
      untestable before, because a canned handler never transports the body.
      **Terms gate CLEARED 2026-08-03 — read first-hand.** `openai.com/policies/*` 403s to automated
      fetchers, but the same contract is published as a PDF on OpenAI's own CDN, which does not:
      `https://cdn.openai.com/osa/openai-services-agreement.pdf`, version `ONLINE v.010126`. §4.1
      assigns to the customer all of OpenAI's right, title and interest in Output, and §3.3's nine
      restrictions contain none about publishing or redistributing it. Binding going forward: §3.3(e)
      — Output may not be used to develop competing AI models outside the Permitted Exception.
      Residual: the *Sharing and Publication Policy*, incorporated by reference, still 403s; it
      imposes attribution/disclosure conditions rather than a prohibition and the sidecar discharges
      both. Record `terms.checked_utc` as the capture date.
- [x] 4.2 **Azure OpenAI Whisper** (STT, `Tests/Verbara.Sdk.VoiceAi.Stt.Tests/Whisper/AzureWhisperSpeechRecognizerTests.cs`)
      — deployment-path URL + `api-key` header (not bearer)
      — **done.** Capture: `Recordings/azure-openai-whisper/transcribe-short-es-co.json`, 65 bytes
      — a bare `{"text": …}` with **no `usage` object**, so the same model behind two vendors
      returns two different envelopes. That divergence is now recorded rather than assumed away by
      a shared hand-authored fixture, which is the D4 argument in one line. No `src/**` seam
      needed. Suite 4 tests → 6: exhaustive query matching now asserts `api-version` (a dropped or
      added parameter breaks the match instead of being answered anyway), plus the wrong-`api-key`
      unmatched shape and an error-status response.

      **Both captures share one source-audio artifact.** §6 bans an identifiable person's voice —
      including the capturer's — and there is no offline TTS in this repo, so the STT input is the
      already-committed Azure TTS capture (`azure-tts/synthesize-short-es-co.raw`, prebuilt neural
      voice, fictional sentence) wrapped in a canonical RIFF header. One cleared audio artifact
      instead of two. Both providers transcribed it back verbatim, accent included, which is also
      a UTF-8 round-trip proof. A single transcription is inference, not training, so neither
      Azure's synthetic-training-data bar nor OpenAI §3.3(e) is engaged; recorded in each sidecar.

      **Neither test hard-codes the transcript** — each reads it from its own capture with
      `JsonDocument`, so two independent readers must agree on the vendor's bytes and a re-capture
      does not force a test edit.
- [ ] 4.3 **Google Speech-to-Text** (STT, `Tests/Verbara.Sdk.VoiceAi.Stt.Tests/Google/`) — JSON POST to
      `speech:recognize`; the API key rides in the query string, so it MUST be placeholdered in both
      the stub and any recorded request metadata.
      **⚠ Verify one clause before committing the capture (ADR-0041 D11):** §3.4 read the AI/ML
      "Generated Output is Customer Data" grant verbatim but could not retrieve the enumeration of
      which products count as *AI/ML Services*. Confirm Speech-to-Text is listed there; if it is not,
      the verdict drops to `not-cleared` and D11's envelope fallback applies. Separately, never put
      comparative accuracy or latency numbers in a sidecar or `Recordings` README — that engages the
      Service Specific Terms §7 benchmarking clause.
- [x] 4.4 **Azure TTS** (`Tests/Verbara.Sdk.VoiceAi.Tts.Tests/Azure/`) — SSML POST returning a real
      audio stream; replaces `new byte[320]` zeros with recorded codec bytes, exercising the
      frame-chunking path for the first time
      — **done, and this is the migration that establishes the pattern** (see 4.1: OpenAI was
      originally slated for that role and was overtaken while its terms gate was open). Capture:
      `Recordings/azure-tts/synthesize-short-es-co.raw`, 60 200 bytes of raw 8 kHz 16-bit mono PCM
      (3.76 s) from the prebuilt `es-CO-SalomeNeural` voice over a fictional sentence, 23% of the
      256 KiB cap, with a full provenance sidecar. Redaction guard green; the response carried no
      account or correlation identifier to strip.

      **Required a `src/**` seam — see the corrected Impact bullet in `proposal.md`.**
      `AzureTtsSpeechSynthesizer` composed its URL from `Region`, so it ignored `HttpClient.
      BaseAddress` entirely. Added the `internal` test-only `fakeOrigin` parameter following the
      existing `SpeechmaticsSpeechSynthesizer` / `LmntSpeechSynthesizer` precedent, substituting the
      **origin only** so the route stays in production code and the strict matcher asserts the real
      path.

      Suite went 4 tests → 6, adding the two shapes the canned handler could not express: an
      unmatched request when the API-key header is wrong (strict matching, D1) and an error-status
      response. **A wrong assertion was caught and removed in the process:** the first version
      asserted the final frame was exactly `length % chunkSize`, which failed — frames are whatever
      `Stream.ReadAsync` returns and a real chunked response does not align to the buffer. What is
      assertable, and now asserted, is the byte-exact round trip plus the presence of a partial
      frame, guaranteed because the capture's length is deliberately not chunk-aligned.
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

> **Inventory finding (2026-08-09) — there is a ninth WebSocket fake, and it is out of this change's
> scope.** `Tests/Verbara.Sdk.VoiceAi.OpenAiRealtime.Tests/Internal/RealtimeFakeServer.cs` hand-authors
> its frames (`{"type":"session.created","session":{}}`), which is exactly the shape D4 exists to
> retire — yet the word *realtime* appears nowhere in this change. It is **not** being folded in here:
> `Verbara.Sdk.VoiceAi.OpenAiRealtime` is a separate package with its own suite, not one of the
> `VoiceAi.Stt` / `VoiceAi.Tts` provider surfaces this change enumerates, and widening the scope
> mid-change to absorb it would make the delta unreviewable. Recorded so the omission is a decision
> rather than an oversight; it needs its own proposal. Worth noting for whoever writes it: it is the
> only WebSocket surface for which a capture credential is already on hand.

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
- [x] 5.9 Record the not-migrated verdict per provider in the suite (a one-line comment naming the
      transport) so the omission cannot later be read as an oversight
      — **done.** A `<summary>` on each of the 8 test classes naming the transport, the D2 reason
      (WireMock.NET cannot hold a duplex session) and the D4 remedy (fidelity comes from recorded
      frames, not a different server). Additions only — 50 lines across 8 files, no behaviour touched.
      Two carry an extra clause: Speechmatics STT points at its own HTTP-transport TTS sibling that
      *does* migrate, and LMNT WS states the D3 split against the HTTP class further down the file.

## 6. Convergence and cleanup

- [ ] 6.1 Delete `Tests/Verbara.Sdk.VoiceAi.Stt.Tests/Helpers/MockHttpMessageHandler.cs` once no STT
      suite references it
- [x] 6.2 Delete `Tests/Verbara.Sdk.VoiceAi.Tts.Tests/Helpers/MockHttpMessageHandler.cs` once no TTS
      suite references it (it is a divergent second copy — different constructor signature)
      — **done.** The Azure TTS migration was its last consumer; grep found no remaining reference
      and no dangling `using`, and `Helpers/` was left empty and removed with it. Build stays at
      0 warnings. §6.1's STT copy is **not** free yet — six `GoogleSpeechRecognizerTests` cases still
      construct it, so it goes with §4.3.
- [ ] 6.3 Delete the retired `HttpListener` HTTP fakes (`SpeechmaticsFakeServer`, `LmntHttpFakeServer`)
- [x] 6.4 Confirm `WebSocketTestServer` and every WebSocket protocol fake are untouched in behaviour
      — **confirmed by diff against `origin/main`: zero changes** to `WebSocketTestServer` or to any
      of the ten `*FakeServer*.cs` files. Re-confirm at the end of the change; this holds as of the
      §4.1/§4.2/§4.4 scope.

## 7. Documentation

- [x] 7.1 Update `docs/guides/` with the provider-suite testing convention: which substrate a new
      provider uses, chosen by transport
      — **done:** `docs/guides/provider-test-substrate.md`, indexed in `docs/guides/README.md`. Its
      own guide rather than a section of the recording protocol, because "which substrate?" is the
      question that comes *before* capturing anything. Covers the transport rule, the two-project
      split and the coverlet reason for it, D3 dual-transport suites, strict matching (including
      asserting the *unmatched* shape), the D12 origin seam with the check-for-an-existing-endpoint-
      option-first caveat, a six-step checklist for adding a suite, and §5 naming the drift gap the
      substrate does not close.
      Also fixed while in there: the recording protocol's intro said "thirteen speech APIs" while its
      own §1 table enumerates fourteen surfaces (6 HTTP + 8 WebSocket).
- [x] 7.2 `CHANGELOG.md` entry under the test/tooling section (no `src/**` change, so no
      `Directory.Build.props` `PackageVersion` bump and no release task in this change)
      — **done**, as `### Changed — Tests & tooling` under `[Unreleased]`. Phase A (#149) had shipped
      without one, so the entry is cumulative for the whole change to date. **The parenthetical above
      is now wrong and the entry says so plainly:** there *is* one `src/**` change (the D12 seam). It
      is `internal`, moves no public API and changes no production behaviour, so the conclusion —
      no `PackageVersion` bump, no release task — still holds, but for a reason that had to be stated
      rather than assumed.
- [x] 7.3 Confirm every artifact is free of absolute machine paths, credentials and private-repo
      content before the PR (verbara-meta/ADR-0005)
      — **done**, swept across all 31 files this branch adds or modifies: zero absolute machine paths,
      zero private-repo references, zero credential-shaped strings, redaction guard green over 8
      recording files in 2 trees. The §3.3 amendment was written to this standard too — it describes
      the standing capture-account practice without naming the local file that holds it.

## 8. Verification

**This block is per-PR, not end-of-change.** Phase A already shipped as PR #149, so the change lands
across several PRs and every one of them re-runs §8. The results recorded below were measured on
2026-08-09 against the scope delivered so far (§4.1, §4.2, §4.4, §1–§3). **Nine of the ten items run
locally** — only 8.9 needs a PR — which was worth discovering: treating §8 as an end-of-change
formality left the D8 parity gate unmeasured while three provider suites were being rewritten.

- [x] 8.1 `dotnet test Tests/Verbara.Sdk.VoiceAi.Stt.Tests` — green (covered by the 8.3 lane)
- [x] 8.2 `dotnet test Tests/Verbara.Sdk.VoiceAi.Tts.Tests` — green (covered by the 8.3 lane)
- [x] 8.3 `dotnet test Verbara.Sdk.slnx --filter "Category!=Functional&Category!=Integration"` — green,
      **zero warnings** (`TreatWarningsAsErrors=true`, `WarningLevel=9999`) — 3 027 passed, 0 failed
- [x] 8.4 `dotnet build Verbara.Sdk.slnx` — zero warnings; the `BanDapperPackageReferences` guard and
      the BannedApi analyzers still pass
- [x] 8.5 Every `StreamAsync_ShouldAbort_WhenCancelled` / `SynthesizeAsync_ShouldAbort_WhenCancelled`
      test still green and still deterministic under the repeat-run protocol — the `test-determinism`
      contract is unchanged by the substrate swap
      — **30 consecutive runs of both suites' cancellation filters (3 TTS + 7 STT tests), 0 failures**
      — 30× is this repo's established repeat-run protocol (CHANGELOG v2.3.2), not an arbitrary count.

      > **Amendment (2026-08-09) — 30× green locally was not enough, and that is the finding.**
      > CI still failed `LmntSpeechSynthesizerWsTests.SynthesizeAsync_WsInit_ShouldIncludeFlushAndEof_InSubsequentMessages`
      > on the first PR run. The defect was **pre-existing** (`LmntFakeServer.cs` and `src/**/Lmnt/`
      > were byte-identical to `main`), but it is worth recording here because it exposes a limit of
      > the protocol itself: the repeat-run count multiplies *runs*, not *machines*. Both LMNT races
      > were decided by a fixed 30 ms server timer versus client-side work, so a fast dev box wins
      > all 30 rounds and a loaded CI runner does not. Repeat-runs cannot find that class of bug —
      > only removing the timer can.
      >
      > Three defects, all in the fakes, none in shipped code:
      > 1. **LMNT answered on a timer instead of on the client's request.** `Task.Delay(30)` → send
      >    audio → `CloseAsync`. `CloseAsync` drains and discards peer frames to complete the close
      >    handshake, so `flush` could vanish between `text` and `eof`. Now the session waits for the
      >    client's terminal `eof` frame (2 s bounded, so cancelled/aborted clients still get answered)
      >    — which is also what the real LMNT server does.
      > 2. **`HoldOpenUntilDisposed` did not hold open.** It awaited the receive loop, and that loop
      >    ends the moment the client half-closes (`CloseOutputAsync` after EOF) — so the session tore
      >    down and completed the client's stream. `SynthesizeAsync_ShouldAbort_WhenCancelled` never
      >    set the flag at all; it passed because the 30 ms delay happened to outlast the 5 ms cancel
      >    poll. Both are fixed: the flag now holds until dispose, and the test sets it.
      > 3. **Five fakes handed out the live `List<string>` the receive loop was appending to** — a torn
      >    read under concurrent mutation. All five now expose an `IReadOnlyList<string>` snapshot
      >    under the same lock the writer takes (LMNT, ElevenLabs, Deepgram TTS, Cartesia TTS,
      >    Cartesia STT).
      >
      > Re-verified after the fix: **30 iterations × 2 suites = 60 runs, 0 failures**, and the full
      > unit lane at 3 027 tests / 30 assemblies green.
      >
      > **Follow-up sweep (2026-08-09) — the same defect classes, looked for rather than waited for.**
      > Fixing LMNT only closes the instance CI happened to catch; the classes were then swept across
      > every fake under `Tests/**` (three lenses — timer-based answering, hold-open flags,
      > live-collection reads — each finding independently refuted before being accepted). One more
      > real defect: **`DeepgramTtsFakeServer` had the identical Class A timer**, and a *wider*
      > exposure than LMNT — `DeepgramSpeechSynthesizer` never sends a WebSocket close frame, so the
      > fake's `CloseAsync` stayed pending, and draining peer frames, for the rest of the session.
      > Confirmed by forcing the interleaving (delay → 0): `SynthesizeAsync_ShouldSendSpeakMessageWithText`
      > and `SynthesizeAsync_ShouldComplete_WhenServerAbortsAfterSend` both fail. Fixed by waiting on
      > the client's `Flush` frame — the last unconditional request frame (`Close` is guarded by
      > `ws.State == Open`) and the one a real Deepgram server answers. Its orphaned `HangForever`
      > flag carried the Class B defect with zero consumers; corrected rather than left as a trap.
      >
      > Cost of the fix: **none measurable.** A controlled A/B (5 runs each, same build) puts the TTS
      > suite at 10 s with the fix and 9–10 s without it. Worth stating because the suite had appeared
      > to jump 7 s → 10 s across the change — machine state, not the fix, and the same
      > duration-is-a-noisy-signal lesson as §8.9's CI wall-clock measurement.
      >
      > Twelve further candidates were reported and **refuted** on the same standard used here — a
      > defect must produce an observably wrong assertion in a test that exists today, not a
      > theoretical race. Three are worth recording as *latent* hazards rather than defects, for
      > whoever migrates these suites next (they need their own proposal, not this change):
      > `RealtimeFakeServer` (the ninth WebSocket surface, already out of scope per §5) has all three
      > classes at once; `WebSocketTestServer` never joins its session handlers, so dispose is not a
      > barrier and a fake's own failure has no reporting channel; and no fake honours
      > `result.EndOfMessage`, which is safe only because every payload in this tree is far below the
      > 4 KiB internal receive buffer.
- [x] 8.6 Coverage floor holds (`scripts/check-coverage-floor.py`) — no provider loses coverage when
      its fake is deleted
      — **line 80.4% inside the band [78, 81]; branch 66.05% ≥ 64; 12 967 lines measured ≥ 12 315.**
      ⚠ **0.6 pp of headroom to the ceiling.** The band is two-sided: a migration that lifts line
      coverage past 81.0% fails CI as a *stale floor* and must raise `line` to `floor(measured)` in
      the same PR. The remaining migrations should expect to do that rather than be surprised by it.
- [x] 8.6a **`scripts/check-patch-coverage.py`** — coverage-gate-v2's *primary* gate (ADR-0013 clause a),
      missing from this list until 2026-08-09. **100% (6/6 changed executable lines), floor 85%.**
      The denominator is one `src/**` file (`AzureTtsSpeechSynthesizer.cs`, the D12 seam), so a single
      uncovered line would have sunk it — a sharp gate hiding behind a tiny diff.
- [x] 8.6b **`scripts/check-exclusion-baseline.py`** — denominator guard (ADR-0013), also missing from
      this list until 2026-08-09. 0 markers against a baseline of 0, 863 files scanned.
- [x] 8.7 `dotnet pack -c Release` — no produced `.nupkg` declares WireMock.NET as a dependency
      — **29 packages packed, zero `wiremock` / `TestInfrastructure` references in any `.nuspec`.**
- [x] 8.8 `aot-validate` workflow green — the test-only substrate never enters an AOT publish graph
      — the workflow is a single `bash tools/verify-aot.sh`, so it runs locally: **0 trim warnings,
      AotCanary published and smoke-run for `linux-x64`.**
- [~] 8.9 CI green end to end (`pull_request` + `merge_group`), with the wall-clock delta versus the
      pre-change baseline recorded and judged acceptable under ADR-0038
      — **the only item that cannot be closed locally; it requires an open PR.**
      — **`pull_request` green** on [#159](https://github.com/verbara/Verbara.Sdk/pull/159)
      (run `31335450414`, 2026-08-09): 14 checks pass, 0 fail — AOT Trim Check, Analyze (C#), Audit
      Test Asserts, CodeQL, **Coverage Ratchet**, Coverage Script Tests, Dependency Review, both
      Docs-only gates, Functional Tests (Testcontainers) (23), OpenSpec Validate, Pack Warnings Gate,
      **Unit Tests**, aot-check. The Coverage Ratchet result is the one that had never actually run
      before — it was `skipped` on the previous attempt because `needs: unit-tests` failed (ADR-0038
      D2), so §8.6's locally-computed floor is only now confirmed by CI itself.
      — **`merge_group` still open by construction:** that trigger only fires on queue entry, so it
      cannot be observed from an un-enqueued PR. Per ADR-0038 D3 the queue run adds the Asterisk 22
      variant, which never reports on `pull_request`. This item closes on landing, not here.

      **Wall-clock delta (D9 — measured, not assumed).** `Unit Tests` job duration, 29 successful CI
      runs sampled across `pull_request` and `merge_group`, split at PR #149 (the first WireMock code
      on `main`, 2026-08-03 11:44Z):

      | | n | min | median | mean | max | spread |
      |---|---|---|---|---|---|---|
      | before WireMock | 15 | 382 s | 660.0 s | 607.4 s | 694 s | 312 s (**1.82×**) |
      | with WireMock | 14 | 414 s | 629.0 s | 591.9 s | 686 s | 272 s (**1.66×**) |

      Median **−31 s**, min **+32 s** — opposite signs, which is the finding. The pre-change window
      alone spans 382 s → 694 s on *code that did not change*, so GitHub runner variance is ±150 s
      and the WireMock signal is not resolvable at job granularity. It is not merely unresolved but
      unresolvable **by construction**: ADR-0041 measured the substrate directly at **+0.6 ms** per
      construct/dispose and **+1.3 ms** with one request served; the suites instantiate **32**
      fixtures today, and even the absurd upper bound of every one of the 3 027 unit tests taking a
      fixture would be ≈ 3.9 s — still ~40× under the noise floor. Full migration of all six HTTP
      surfaces stays in the same order of magnitude.

      **Judgment under ADR-0038: acceptable.** That ADR treats CI wall-clock as the scarce resource
      and bought its headroom structurally (one functional variant on PR, coverage collected once).
      This change spends none of that headroom: its cost is three orders of magnitude below the
      measurement floor of the pipeline it runs in. The honest form of D9 is therefore that job
      duration *cannot* answer the question and the per-fixture measurement is what carries the
      verdict — recording the job numbers anyway is what makes that distinction checkable rather
      than asserted.
- [x] 8.10 `openspec validate --change wiremock-http-provider-substrate --strict` passes

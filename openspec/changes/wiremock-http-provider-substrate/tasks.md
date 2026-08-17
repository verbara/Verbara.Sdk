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

**Tooling for the three remaining surfaces is ready (2026-08-14); only the credentials are not.**
`scripts/capture-provider-recording.py` grew from the two Whisper surfaces to five, and the step
that took the work was not the three request plans but generalizing a tool that was hard-wired for
"STT → JSON" end to end: fixed recordings tree, fixed `.json` extension, one scenario slug,
`normalize_json()` on every body, and `json.loads(body)["text"]` as the liveness check — which is
OpenAI's response shape and wrong for all three new surfaces. It now carries an `artifact` mode
(`json` | `binary` | `envelope`), a per-provider `verify` callable, and a parameterized sidecar.
Suite: 81 → **150 tests**, with the two existing surfaces proven byte-identical against the prior
script (capture file, sidecar, and request URL/headers/body) rather than merely re-run.

Two properties worth naming because they are structural, not procedural:

- **LMNT's envelope mode cannot leak audio by mistake.** `send(..., retain_body=False)` counts and
  drops each read, so no whole payload ever exists in memory for a later line of code to write out.
  D11 asks for a promise; this makes it a property of the control flow.
- **The binary cap raises before any write**, so an oversized capture leaves nothing on disk to
  half-review. (§8's 64 KiB text threshold stays advisory — the 256 KiB binary cap is not.)

Google takes one of two credentials: `GOOGLE_SPEECH_API_KEY` sends the SDK-faithful `?key=`
request, `GOOGLE_ACCESS_TOKEN` sends a bearer token and stamps the sidecar with the fact that the
capture's auth differs from production. Setting both, or neither, is an error. The second path was
added expecting the first to fail; it does not (see 4.3), so it is now an alternative rather than a
workaround — kept because a token is what a service-account capture would use.

**One more redaction gap the first real capture exposed.** Google's response carries
`requestId: "8702164082194047156"`, which protocol §4 bans outright ("request IDs, trace IDs …
tie a public artifact to a real, billed account") and which `check-recording-redaction.py` passed
without complaint: a bare 19-digit number is not credential-shaped, so nothing flagged it.
`redact()` could not have caught it either — it removes values known in advance, and a request ID
is minted by the provider. Fixed with the opposite mechanism, `redact_correlation_fields`: name the
*field*, replace whatever is in it, at any depth. The key is kept and only its value placeholdered,
because an unmodelled sibling is precisely what this fixture holds the parser against.
**The guard's blind spot is not closed** — it still cannot recognize an identifier it was not told
about, and the per-provider field list is a human judgement about which field is data and which is
an identifier. That belongs in a proposal of its own, not here.

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
- [x] 4.3 **Google Speech-to-Text** (STT, `Tests/Verbara.Sdk.VoiceAi.Stt.Tests/Google/`) — JSON POST to
      `speech:recognize`; the API key rides in the query string, so it MUST be placeholdered in both
      the stub and any recorded request metadata.
      **⚠ Verify one clause before committing the capture (ADR-0041 D11):** §3.4 read the AI/ML
      "Generated Output is Customer Data" grant verbatim but could not retrieve the enumeration of
      which products count as *AI/ML Services*. Confirm Speech-to-Text is listed there; if it is not,
      the verdict drops to `not-cleared` and D11's envelope fallback applies. Separately, never put
      comparative accuracy or latency numbers in a sidecar or `Recordings` README — that engages the
      Service Specific Terms §7 benchmarking clause.

      > **A predicted defect that turned out not to exist — recorded because the retraction is the
      > useful part.** While scoping the capture (2026-08-14) the REST reference for
      > `v1/speech:recognize` was read as documenting exactly one authorization mechanism —
      > *"Requires the following OAuth scope: `https://www.googleapis.com/auth/cloud-platform`"* —
      > with no mention of API keys anywhere on the page. Since `GoogleSpeechRecognizer.cs:54`
      > builds `…/v1/speech:recognize?key={ApiKey}` and `GoogleSpeechOptions` exposes `ApiKey` as
      > its only credential, this was written up as a fourth shipped defect: a client that cannot
      > authenticate at all.
      >
      > **It authenticates fine.** The capture ran on 2026-08-15 with a plain API key against a
      > fresh project and Google returned HTTP 200 with a real transcript. The documentation's
      > silence on API keys is not a prohibition, and an absence in a reference page is not
      > evidence of a behaviour.
      >
      > Worth keeping in the record for two reasons. First, the same reasoning style produced the
      > Cartesia/ElevenLabs finding in §5, which *is* real — the difference is that there the docs
      > made a positive statement (audio arrives base64-in-JSON) rather than merely omitting one.
      > **A vendor asserting X is evidence; a vendor not mentioning Y is not.** Second, the design
      > that settled it was right even though the prediction was wrong: the capture was built as
      > the deciding experiment rather than as a confirmation of the write-up, so one command
      > overturned it. `GOOGLE_ACCESS_TOKEN` survives as a documented alternative path, no longer
      > as the workaround for a defect.

      **Done 2026-08-15, and it is the only one of the three remaining §4 surfaces that was ever
      completable** — the other two turned out to be blocked on shipped code, not on credentials.
      Capture: `Recordings/google-stt/transcribe-short-es-co.json`, 342 bytes of Google's real
      HTTP 200 body with a provenance sidecar. Suite 6 tests → 9; the STT assembly goes 77 → 80 and
      the unit lane 3 048 → 3 051.

      **The `src/**` seam, same shape as §4.4's.** `GoogleSpeechRecognizer` built one absolute URL,
      so `HttpClient.BaseAddress` was dead and the client could not be pointed at a loopback stub.
      It takes the `internal` `fakeOrigin` parameter following the existing
      `SpeechmaticsSpeechSynthesizer` / `LmntSpeechSynthesizer` / `AzureTtsSpeechSynthesizer`
      precedent (D12), with the route and the `key` query parameter left in production code so the
      strict matcher asserts the request the provider really builds. Production output is
      byte-identical to the old interpolation; `PublicAPI.*.txt` does not move.

      **What the capture bought, asserted rather than noted.** The recorded body carries four
      fields the DTOs do not model — `results[].resultEndTime`, `results[].languageCode`,
      `totalBilledTime` and `requestId` — so
      `StreamAsync_ShouldTolerateUnmodelledSiblingFields_WhenResponseCarriesFullVendorEnvelope`
      asserts they are present *in the file* and that the recognizer still yields the recorded
      transcript. Shrinking the fixture back to the hand-authored shape turns it red. Two more
      shapes the canned handler could not express: a wrong `key` in the query string is now an
      unmatched request rather than a silent pass (D1), and an error status (429) is exercised.

      **Two things deliberately not claimed.** `es-CO` was requested and Google answered
      `languageCode: "es-us"`, which is recorded but not asserted as a contract. And the transcript
      comes back lowercase, unaccented and unpunctuated, so **this fixture does not demonstrate a
      UTF-8 round trip** — a comment in the suite says so, to stop a later reader adding an
      assertion the evidence does not support.

      **The §5.1 mutation proof was not repeated here.** That fence was verified by temporarily
      shrinking the fixture and watching the test fail; doing the same to a *captured* file
      conflicts with the rule that a recording is never edited. Substituted a weaker,
      non-destructive check: sha256 and byte count matched against the sidecar after all work.
      Stated because it is a real difference in evidence strength, not an equivalent.
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

      > **⚠⚠ BLOCKED ON A `src/**` DEFECT, confirmed against the live API 2026-08-15 UTC — the same
      > shape as §4.6, found the same way.** The capture at the shipped `SpeechmaticsOptions`
      > defaults returned `HTTP 404 {"detail":"Not Found"}`. A three-way controlled comparison,
      > same key, seconds apart, isolates route from voice:
      >
      > | request | result |
      > |---|---|
      > | `POST /generate`, voice in the JSON body — **what `SpeechmaticsSpeechSynthesizer` sends** | **404** |
      > | `POST /generate/eleanor`, voice in the path, no voice field in the body | **200, `audio/wav`, 81 452 bytes** |
      > | `POST /generate/sarah`, voice in the path, no voice field in the body | **200, `audio/wav`, 110 636 bytes** |
      >
      > **One delta, not three.** The credential (`Authorization: Bearer`), the request content
      > type, the response media type (`audio/wav`) and the 16 kHz default are all what the client
      > already assumes. What is wrong is solely *where the voice goes*: the API selects the voice
      > by path segment, and `SpeechmaticsTtsRequest` carries it as a body field against a path
      > that therefore does not exist.
      >
      > **The middle row exists to kill a plausible wrong answer.** Speechmatics' quickstart lists
      > four voices — `sarah`, `theo`, `megan`, `jack` — and the SDK defaults to `eleanor`, which
      > made "the default voice is stale" the obvious pre-capture hypothesis. It is false:
      > `eleanor` returns 200. The published list is incomplete, not the option default. Absence
      > from a vendor's enumeration is not evidence of absence — the same rule that retracted
      > §4.3's finding, applied here *before* it could become a second wrong finding. Had the
      > comparison run only rows one and three, route and voice would have moved together and the
      > wrong one could have taken the blame.
      >
      > **Not established, and not to be assumed at fix time:** whether `/generate/{voice}` accepts
      > the `language` and `sample_rate` body fields the client also sends. Rows two and three
      > omitted them, so their acceptance is untested either way.
      >
      > **The fix is not a one-line edit**, which is why it does not belong in this change.
      > `SpeechmaticsOptions.BaseUri` ships as the *complete* endpoint
      > (`https://preview.tts.speechmatics.com/generate`) and is a public, caller-settable option;
      > making the voice a path segment means either appending `/{voice}` to whatever a caller
      > supplied — silently changing what an existing `BaseUri` value means — or redefining the
      > option. That is a public-surface decision. Note also that the `<see href>` on
      > `SpeechmaticsOptions.cs:23`, `https://docs.speechmatics.com/tts-api-ref`, is itself a dead
      > link (404) — the shipped XML doc points at a page that no longer exists.
      >
      > **Consequence: §4.5 cannot be completed as a test-only migration**, for the reason §4.6
      > gives verbatim — under strict matching (D1) a fixture pinning the current request would
      > encode a 404, and one pinning the working request would not match what the client sends.
      > Reclassified from credential-blocked to **`src/**`-blocked**. No artifact was written: the
      > capture script raised before any file was created, so the working tree carries nothing from
      > the 404, and nothing from rows two and three either — those were probes, not captures.
- [ ] 4.6 **LMNT HTTP path** (`Tests/Verbara.Sdk.VoiceAi.Tts.Tests/Lmnt/`, `LmntTransport.Http`) —
      retires `LmntHttpFakeServer` only; `LmntWsFakeServer` stays (see 5.4).
      **⚠ Envelope capture only — ADR-0041 D11.** The §3.4 terms review returned `not-cleared` for
      LMNT: its ToS has no clause on rights in generated audio and its AUP restricts sharing
      synthesized speech outside the capturing entity. Do **not** commit LMNT audio bytes. Capture the
      response envelope (status, headers, media type, content length, chunk boundaries) as the
      `recorded` artifact and pair it with a locally built `synthetic` body in the same codec. The
      migration still lands — strict matching, real status/headers, real byte lengths through
      frame-chunking — it just does not redistribute LMNT's speech.

      > **⚠⚠ BLOCKED ON A `src/**` DEFECT, confirmed against the live API 2026-08-15. The HTTP path
      > this task migrates does not work at all.** The first capture attempt returned
      > `HTTP 404 {"detail":"Not Found"}` — a route-level 404, not an auth failure. A controlled
      > comparison with the same key, seconds apart, settles it:
      >
      > | request | result |
      > |---|---|
      > | `POST /v1/ai/speech/generate`, form-encoded — **what `LmntSpeechSynthesizer` sends** | **404** |
      > | `POST /v1/ai/speech/bytes`, JSON — what `docs.lmnt.com` documents | **200, `audio/mpeg`, 31 104 bytes** |
      >
      > Three deltas, not one: the **path** (`/generate` → `/bytes`), the **body encoding**
      > (`FormUrlEncodedContent` → JSON), and the **response media type** (the SDK's HTTP path
      > assumes raw PCM it can chunk; LMNT returns MP3 by default). The `X-API-Key` header is the
      > one thing that was right. Evidence is both documentary *and* behavioural, which is the
      > distinction §4.3's retraction turned on — Google's docs merely omitted API keys, whereas
      > here the vendor positively documents a different route and the SDK's route observably 404s.
      >
      > **The code left its own confession.** `LmntSpeechSynthesizer.cs:280` carries
      > `// Field names verified from LMNT REST API docs (https://docs.lmnt.com); confirm at
      > integration test time.` That confirmation never happened, and could not have: the only
      > thing the HTTP path was ever exercised against is `LmntHttpFakeServer`, which answers
      > whatever route it is handed. A fake cannot refuse a request the real server refuses — which
      > is the entire argument of ADR-0041 D4, here as a comment written by someone who saw the gap
      > and trusted a later step that the test substrate had already made impossible.
      >
      > **Consequence for this task: it cannot be completed as a test-only migration.** A fixture
      > pinning the current request would encode a 404, and one pinning the documented request
      > would not match what the client sends — strict matching (D1) makes that contradiction
      > explicit rather than papering over it. §4.6 stays open and is **reclassified from
      > credential-blocked to `src/**`-blocked**: fix the client, then capture. Note that the fix
      > also moots the D11 envelope fallback in the paragraph above, because a JSON request
      > returning `audio/mpeg` is a different capture shape than the one specified there — re-read
      > §7 before capturing rather than reusing that plan.

### Where the deferred defects go — decided 2026-08-15, so the deferrals point somewhere

This change surfaced six defects it deliberately does not fix — two in §4 (the `LMNT` and
`Speechmatics` TTS routes, above) and four in §5 (`Cartesia` and `ElevenLabs` TTS cannot receive
audio, `Speechmatics` STT transcript assembly, and `Cartesia` TTS's missing cancellation test).
Each was written up as "needs its own change", which is a deferral, not a destination. The
destination is now decided: **one new change, `provider-wire-protocol-conformance`, under a new
`decision_ref` `Sdk/ADR-0048`** — to be written after this change lands, with the two route fixes
as its first tasks so §4.5, §4.6 and §6.3 unblock before the harder work starts.

**The two open changes that looked like homes rule themselves out, in their own text.**
`provider-dto-robustness-fences` (`Sdk/ADR-0046`) fences the **parse layer of the read path, on
DTOs that exist**: its six ADDED requirements are receive-loop resilience, read-path nullability
enforcement, `[JsonRequired]` placement, unknown-sibling tolerance, per-DTO mutation matrices and
two governance guards. Walked defect by defect, none fires. The Cartesia and ElevenLabs frames
never reach a DTO at all — `src/Verbara.Sdk.VoiceAi.Tts/ElevenLabs/` declares no server-response
type, so the mutation matrix and the reachable-DTO guard have nothing to act on. The Speechmatics
STT bug is post-parse string assembly over fields the SDK never modelled, and that change's fourth
requirement **bans `UnmappedMemberHandling.Disallow`** — it enshrines ignoring exactly
`attaches_to` and `word_delimiter`. The two route defects are request-side, which it excludes
verbatim ("Not in scope. The 45 request-side members"). Attaching any of them would also falsify
its Impact line, "nothing cascades to `Sdk.Pro` or `Platform`."
`provider-schema-drift-train` (`Sdk/ADR-0047`) states "No `src/` change, no public API change" —
categorical — and its instrument reaches none of these vendors anyway.

**Two corrections to earlier notes in this file.** §6.2's suggestion that the Cartesia
cancellation-test gap belongs to `websocket-fake-protocol-contract` does not survive that
proposal's own Not-in-scope — "the other eight WebSocket surfaces … follow-up work, not this
change", plus "No production code changes". And the gap is entangled with the Cartesia audio fix
regardless: correcting the client rewrites `CartesiaFakeServer` onto the base64-JSON protocol, so
a cancellation test authored elsewhere against today's binary protocol would be written twice and
put two open changes in contention over one file.

**Why one change and not two.** The five `src/**` defects share one root cause, which is this
change's own thesis landing: the client does not speak the vendor's actual wire protocol, and the
hand-authored fake agreed with the client, so no test could notice (ADR-0041 D4). Splitting the
route fixes out would buy earlier approval of the easy half — except owner and approver are the
same person here, and this repo already runs one change across many PRs, so per-provider task
staging inside one proposal gives the same sequencing without a second proposal, a second ADR and
a second risk section.

**Why it needs its own ADR rather than riding one.** `SpeechmaticsOptions.BaseUri` is public and
caller-settable and ships as the *complete* endpoint; making the voice a path segment either
appends `/{voice}` to whatever a caller supplied — silently redefining an existing value — or
redefines the option. That is a durable public-surface decision. Note the asymmetry with LMNT,
whose route is hardcoded in `LmntSpeechSynthesizer` with no option exposed at all, and whose HTTP
path is opt-in (`LmntTtsOptions.Transport` defaults to `WebSocket`): the LMNT half is contained,
the Speechmatics half is not.

**What would flip this.** If, at proposal time, the base64 + frame-chunking redesign for Cartesia
and ElevenLabs turns out to need a public API change on the synthesizer surface, or to exceed a
MEDIANO envelope, bundling stops paying: the route unblock would become hostage to a large design,
and the right move is to extract the two route fixes into a small standalone change — still under
`Sdk/ADR-0048` for the `BaseUri` decision.

**One thing to verify before that proposal is written.** Four of the SDK's six TTS providers are
now confirmed unable to deliver audio from their real vendor, and `Azure` is the only one
positively demonstrated working. `Deepgram` TTS is the sixth and is untested against the live
service — its receive loop has the same shape as Cartesia's and ElevenLabs' (binary frames are
audio, text frames are control), and the reason to expect it is *correct* is a fact about
Deepgram's published protocol rather than anything about this code. Given the class has hit four
of six, confirm it rather than assume it.

## 5. WebSocket-transport providers — explicitly NOT migrated, recordings only

WireMock.NET is an HTTP/1.1 request-matching server; bidirectional WebSocket framing is not its
contract. These 8 surfaces keep `Verbara.Sdk.TestInfrastructure`'s `WebSocketTestServer` and their
per-provider protocol fakes. Only the **payloads** change: hand-authored minimal JSON is replaced by
recorded provider frames.

> **⚠ Terms review (2026-08-03) — 5 of these 8 surfaces cannot take a payload recording.** The four
> WebSocket-only vendors had no finding when this change was written; §3.4 covered the six HTTP
> providers only. They now do (`docs/guides/provider-recording-protocol.md` §7), and the result
> reshapes this section:
>
> | Surface | Verdict | Consequence here |
> |---|---|---|
> | 5.1 Deepgram STT · 5.6 Deepgram TTS | `not-cleared` | documentation-derived (§7) |
> | 5.2 AssemblyAI STT | `not-cleared` | documentation-derived (§7) |
> | 5.3 Cartesia STT · 5.5 Cartesia TTS | `permitted-with-conditions` | full recording, commercial tier |
> | 5.4 Speechmatics STT | `permitted` | full recording — the STT direction is the *better* covered one (§10.3 assigns IP in **Transcripts**) |
> | 5.7 ElevenLabs TTS | `not-cleared` | documentation-derived (§7); §7's audio-payload-swap variant also works |
> | 5.8 LMNT WS | `not-cleared` | documentation-derived (§7), already known |
>
> **Correction (2026-08-14, during §5.1) — the "envelope only (D11)" this column used to say was
> wrong twice over.** First, *envelope* is an HTTP concept: for an HTTP response it means status,
> headers and content length, with the body held back. A WebSocket STT text frame has no such
> layer — the frame **is** its JSON body — so "envelope only" reduced to frame type, ordering and
> byte length, which is close to nothing for the field-set gap D4 exists to close. Second, an
> envelope is still *captured*, so it needs a credential and a permitted call, and for a
> `not-cleared` vendor the call itself is the fraught part (Deepgram's console terms bar
> benchmarking outright). The route actually taken is the one the paragraph below already
> preferred and that `docs/guides/provider-recording-protocol.md` §7 now spells out:
> **frames authored to the vendor's published protocol documentation**, `class: "synthetic"`,
> `terms.verdict: "not-applicable"`, plus a `source_schema` block naming the page and its revision.
> It carries no vendor Output, needs no credential, and is checked against the vendor's own stated
> interface. Its cost is stated in each sidecar: it cannot detect a vendor that does not honour its
> own docs.
>
> **This is a real reduction in what §5 can deliver, and it should not be papered over.** ADR-0041
> D4 asks every provider for at least one replay of a recorded real response; for five of these
> eight, the honest artifact is a recorded *envelope* — frame type, order, byte length, control-frame
> sequence — paired with a locally built `synthetic` body. That still closes part of the gap the
> change exists for: frame sequencing, chunk boundaries and byte lengths are real, and for the STT
> surfaces the *shape* of the schema is what a shared-misreading bug hides in, not the transcript
> strings. It does not close the field-set half of the gap for those five. Say so plainly in the
> capture's sidecar rather than implying a fidelity the artifact does not have.
>
> A second, cheaper source is legitimate and preferred where it exists: **frames hand-authored from
> the vendor's own published protocol documentation**, labelled `class: "synthetic"`. That is the
> vendor's published authority rather than its Output, so no terms question arises, and it is
> precisely the authority a parser should be checked against.

> **Inventory finding (2026-08-09) — there is a ninth WebSocket fake, and it is out of this change's
> scope.** `Tests/Verbara.Sdk.VoiceAi.OpenAiRealtime.Tests/Internal/RealtimeFakeServer.cs` hand-authors
> its frames (`{"type":"session.created","session":{}}`), which is exactly the shape D4 exists to
> retire — yet the word *realtime* appears nowhere in this change. It is **not** being folded in here:
> `Verbara.Sdk.VoiceAi.OpenAiRealtime` is a separate package with its own suite, not one of the
> `VoiceAi.Stt` / `VoiceAi.Tts` provider surfaces this change enumerates, and widening the scope
> mid-change to absorb it would make the delta unreviewable. Recorded so the omission is a decision
> rather than an oversight; it needs its own proposal. Worth noting for whoever writes it: it is the
> only WebSocket surface for which a capture credential is already on hand — and per the §8.5
> follow-up sweep it carries all three fake-server defect classes at once, so that proposal has more
> to fix than payloads.

- [x] 5.1 STT **Deepgram** (`Deepgram/`) — re-seed `DeepgramFakeServer` from a recorded `Results`
      frame carrying the full field set (`speech_final`, `channel_index`, `duration`, `start`,
      `metadata`, word arrays), replacing `BuildResultJson`'s five-field hand-authored object
      — **done, documentation-derived (§7), and this is the migration that establishes the §5
      pattern.** Three fixtures under `Recordings/deepgram-stt/` with sidecars:
      `results-frame-interim.json`, `results-frame-final.json` (adds `entities[]`, which the
      reference documents as present only on `is_final` messages) and `metadata-frame.json`.
      Source: `developers.deepgram.com/reference/speech-to-text/listen-streaming`, **`revision:
      "undated"`** — the page publishes no revision marker, corroborated across two sibling doc
      pages that publish none either. Recorded as the §5 finding it is: a silent breaking edit
      there is indistinguishable from no edit at all, so that URL is the first thing to re-read
      when this suite starts failing.

      **The measured finding: `DeepgramResultMessage` models four values** — `type`, `is_final`,
      and each alternative's `transcript` and `confidence`. Everything else Deepgram documents is
      unmodelled: `channel_index[]`, `duration`, `start`, `speech_final`, `from_finalize`,
      `entities[]`, `metadata.request_id`, `metadata.model_info.{name,version,arch}`,
      `metadata.model_uuid`, `alternatives[].languages[]`, and the whole `words[]` array
      (`word`, `start`, `end`, `confidence`, `language`, `punctuated_word`, `speaker`). The
      five-field object this task retires never exercised one of them.

      **The fence was mutation-tested, not asserted.** Shrinking `results-frame-final.json` back to
      the old five-value shape fails
      `StreamAsync_ShouldTolerateUnmodelledSiblingFields_WhenFrameCarriesFullDocumentedFieldSet`;
      file restored and `sha256` re-verified against the sidecar. Without that fixture-integrity
      test someone can quietly shrink a fixture and every test still passes — it belongs in every
      sibling suite.

      Shape the other seven copy: static `Lazy<ProviderRecordings>` + path constants + `ReadFrame`,
      constructor seeded with the frames **verbatim**, and the existing `Build*Json(...)` helper kept
      at its current signature but re-implemented as parse → patch the driven fields via `JsonNode`
      → `ToJsonString()`. Zero call-site churn and the full field set survives every test that only
      cares about a transcript. Suite 5 tests → 8; `StreamAsync_ShouldAbort_WhenCancelled` is
      byte-identical (verified by diff). Build 0 warnings, unit lane 3 030 passed / 0 failed,
      redaction guard green over 14 files in 2 trees.

      **Inventory correction:** `DeepgramFakeServer` does **not** ride `WebSocketTestServer` — it
      owns an `HttpListener` and its own port-retry loop. `proposal.md`'s substrate table and this
      task's §5.9 `<summary>` both said otherwise. The `<summary>` was corrected in place; no
      behaviour changed. See §6.4 for the consequence.

      **Widened on re-check — it is two fakes, not one.** The finding above came from looking at the
      STT suite only. Grepping all ten `*FakeServer*.cs` files instead shows **ElevenLabs TTS**
      carries the same shape, so of the eight WebSocket surfaces six ride `WebSocketTestServer`
      (AssemblyAI STT, Cartesia STT, Speechmatics STT, Cartesia TTS, Deepgram TTS, LMNT) and two do
      not (Deepgram STT, ElevenLabs TTS). Recorded as a dated correction in `proposal.md`. The
      general lesson is the cheap one: a claim about ten files is worth a grep over ten files, and
      the first pass here inferred the other six from a sample of four.
- [x] 5.2 STT **AssemblyAI** (`AssemblyAi/`) — re-seed `AssemblyAiFakeServer` from ~~recorded~~
      **documentation-derived** turn frames (§7 route; AssemblyAI is `not-cleared`, so no capture is
      available on any credential)
      — **done.** `Recordings/assemblyai-stt/`: `begin-frame`, `turn-frame-interim`,
      `turn-frame-final`, `termination-frame` (+ 4 sidecars). Doc:
      `assemblyai.com/docs/api-reference/streaming-api/streaming-api`, `revision: "undated"`.
      **Finding: `AssemblyAiTurnMessage` models 4 documented fields** (`type`, `transcript`,
      `end_of_turn`, `turn_is_formatted`); unmodelled are `turn_order`, `end_of_turn_confidence`,
      `utterance`, `language_code`, `language_confidence`, `speaker_label` and the whole `words[]`
      array. `Begin` and `Termination` are not modelled at all, including `Begin`'s nested
      `configuration` object carrying JSON nulls.
      **Sub-finding worth keeping:** the SDK hard-codes `Confidence = 0f` with the comment *"v3 Turn
      messages do not include a per-turn confidence scalar"*, while the documented schema carries
      **two** (`end_of_turn_confidence`, `words[].confidence`). Neither is a transcript-accuracy
      score, so `0f` stands — but the comment overstates the vendor's silence, and nothing in the
      suite made that visible before. Pinned by
      `StreamAsync_ShouldSurfaceZeroConfidence_WhenTurnCarriesOnlyEndOfTurnConfidence`.
- [x] 5.3 STT **Cartesia** (`Cartesia/`) — re-seed `CartesiaFakeServer` from ~~recorded~~
      **documentation-derived** frames (terms would have cleared a capture; the blocker is the
      absence of a capture credential, not the terms — see the sidecars)
      — **done.** `Recordings/cartesia-stt/`: `transcript-frame-interim`, `transcript-frame-final`,
      `flush-done-frame` (+ 3 sidecars). Doc: `docs.cartesia.ai/api-reference/stt/websocket`,
      `revision: "undated"`.
      **Finding, and it runs the other way for once: the SDK models a field the vendor does not
      document.** `CartesiaSttTranscriptMessage` carries `confidence`; Cartesia documents none on
      the transcript message and none per word. The schema-faithful fixtures omit it, which makes
      the `msg.Confidence ?? 0f` fallback reachable for the first time
      (`StreamAsync_ShouldSurfaceZeroConfidence_WhenVendorSchemaCarriesNoConfidenceField`).
      Unmodelled documented fields: `request_id`, `duration`, `language`, `words[]{word,start,end}`.
      `BuildTranscriptJson`'s `confidence` argument therefore now *adds* an out-of-schema property
      rather than patching one — said so in the helper's `<remarks>` so the fixture is not misread
      as vendor-shaped.
      Two doc-hygiene notes carried in the sidecars: `language` appears in Cartesia's response
      *example* but not in that page's schema property list; and `flush_done`'s
      documented-deprecated `is_final: true` is kept deliberately, because the recognizer
      deserializes every text frame into the transcript DTO *before* filtering on `type` — that
      frame is precisely what a broken filter would leak through as an empty final result.
- [x] 5.4 STT **Speechmatics** (`Speechmatics/`) — re-seed `SpeechmaticsFakeServer` from ~~recorded~~
      **documentation-derived** `AddPartialTranscript` / `AddTranscript` frames (terms are
      `permitted` here; again the blocker is credential availability, not terms)
      — **done.** `Recordings/speechmatics-stt/`: `recognition-started-frame`,
      `add-partial-transcript-frame`, `add-transcript-frame`, `end-of-transcript-frame`
      (+ 4 sidecars). Doc: `docs.speechmatics.com/api-ref/realtime-transcription-websocket`,
      `revision: "undated"` (only a message-level `format`, e.g. `"2.1"`). Method note recorded in
      `source_schema`: two readings of the rendered API-ref page disagreed on whether `transcript`
      sits top-level or inside `metadata`; settled against the page's raw Markdown source (inside
      `metadata`).
      **This is the migration that paid for the whole §5 exercise — see the deferred defect below.**
      `SpeechmaticsTranscriptMessage` models `message` + `results[].alternatives[].{content,
      confidence}`. Unmodelled: `format`, the entire `metadata` object (`start_time`, `end_time`,
      **`transcript`**), `results[].{type,start_time,end_time,attaches_to,is_eos,volume}`,
      `alternatives[].{language,display.direction,speaker,tags}`, plus `channel` and `forced`.
      Second finding: Speechmatics documents that `confidence` **has no meaning on
      `AddPartialTranscript`**, yet the SDK averages and surfaces it identically to a final. The
      partial fixture carries deliberately low values so that stays visible.

      > **⚠ DEFERRED DEFECT IN SHIPPED CODE — `src/Verbara.Sdk.VoiceAi.Stt/Speechmatics/
      > SpeechmaticsSpeechRecognizer.cs:170`. Not fixed here; it needs its own change.**
      > The recognizer assembles the transcript by space-joining tokens
      > (`if (sb.Length > 0 && !string.IsNullOrEmpty(alt.Content)) sb.Append(' ')`), unconditionally.
      > Speechmatics publishes two things that say otherwise and the SDK reads neither: a
      > **`word_delimiter`** on `RecognitionStarted`, and **`results[].attaches_to`**, which marks a
      > punctuation token as belonging to the previous one. It also publishes the already-assembled
      > segment at **`metadata.transcript`**. So a final segment ending in punctuation comes out as
      > `"… correctamente ."` where the vendor's own text reads `"… correctamente."`.
      >
      > **Why it is deferred rather than fixed in this change.** This change's contract is a test
      > substrate: `proposal.md` scopes it to `Tests/**` with one `internal` D12 seam as the sole
      > `src/**` exception, and it declares `test-determinism` the only capability it may touch.
      > Correcting transcript assembly changes shipped behaviour for every Speechmatics consumer,
      > cascades to Sdk.Pro and Platform, and needs its own risk section and a version decision —
      > folding it in here would make this delta unreviewable, which is the same argument §5 already
      > used to keep the ninth WebSocket fake out.
      >
      > **It is asserted, not merely noted.**
      > `StreamAsync_ShouldSpaceJoinTokens_WhenFrameCarriesPunctuationAttachedToPrevious` pins the
      > divergence as *current* behaviour, so the fix will announce itself by turning that test red
      > rather than by going unnoticed. The final fixture deliberately ends on an
      > `attaches_to: "previous"` token so the case is always in the tree.
      >
      > **This is the D4 thesis landing.** `proposal.md` argues that a fixture hand-authored by the
      > same person who wrote the parser cannot expose a shared misreading of a vendor's schema. The
      > old fixture and the parser agreed that a transcript is a space-joined token list. The
      > vendor's documentation says it is not. Nothing in this repository could have caught that
      > until the fixture stopped being authored from the parser's assumptions — and note it took the
      > *documentation-derived* route to do it, not a capture: a capture of this same segment would
      > have shown the right text in `metadata.transcript` and still not told anyone the SDK ignores
      > the field.
> **⚠⚠ THE FINDING THIS CHANGE EXISTS FOR — two shipped TTS clients cannot receive audio from
> their vendor at all (surfaced 2026-08-14 by §5.5 and §5.7). Not fixed here; each needs its own
> change.**
>
> `CartesiaSpeechSynthesizer` and `ElevenLabsSpeechSynthesizer` both yield **only**
> `WebSocketMessageType.Binary` frames as audio. ElevenLabs says so in a comment —
> *"Only yield binary frames; skip text messages (alignment, metadata)"*
> (`ElevenLabsSpeechSynthesizer.cs:134`) — and Cartesia treats a text frame purely as a `done`/`error`
> terminator (`CartesiaSpeechSynthesizer.cs:155-167`).
>
> **Both vendors deliver audio as base64 inside JSON text frames, and neither publishes a raw-binary
> mode at all.** Read first-hand 2026-08-14, not inferred:
>
> | Vendor | Audio arrives as | Binary mode? |
> |---|---|---|
> | ElevenLabs `…/v-1-text-to-speech-voice-id-stream-input` | `AudioOutput.audio` — *"a generated partial audio chunk, encoded using the selected `output_format`, by default MP3 encoded as a base64 string"*, alongside `alignment` / `normalizedAlignment`; `FinalOutput.isFinal` ends it | **none documented** |
> | Cartesia `docs.cartesia.ai/api-reference/stt/websocket` (TTS WS) | `chunk.data` — base64-encoded audio, with `done` / `status_code` / `step_time` / `context_id` siblings | **none documented** |
>
> So against a real server both clients drain the socket and emit **nothing**. Cartesia additionally
> reaches its `done` terminator correctly, which means it completes successfully having produced zero
> audio — a silent empty success, the worst failure shape available.
>
> **Why the suites are green and always were.** The fakes send binary because they were hand-authored
> to match the clients. `proposal.md`'s Why section predicted this exact failure in the abstract —
> *"every fixture in the repo is hand-authored by the same person who wrote the parser it feeds; a
> shared misreading of a vendor's schema is invisible"* — and D4 is the decision taken to expose it.
> This is that prediction landing on two production clients at once. **No amount of coverage would
> have found it**: every test passes, the branch is exercised, and the assertion is against a fixture
> that shares the defect.
>
> **Why it is not fixed here.** The fix is not a patch: it needs new server DTOs registered in
> `VoiceAiTtsJsonContext` (AOT source-gen, so no reflection fallback), a base64 decode on the receive
> path, and a re-think of frame chunking, since a base64 JSON message is not a codec frame boundary.
> That is shipped-behaviour change for every consumer, cascading to Sdk.Pro and Platform. This change
> is scoped to `Tests/**` with one `internal` seam as its sole `src/**` exception.
>
> **What was deliberately NOT done, and why it is the uncomfortable call.** The most honest fake
> would speak the documented protocol — base64 JSON — which would turn both suites **red** and prove
> the defect. That cannot land on `main`. The fixtures therefore record the vendor-faithful frames
> (`elevenlabs-tts/audio-output-frame.json`, `cartesia-tts/done-frame.json`) *and* keep the fakes
> sending binary so the suite stays green, with each sidecar stating the divergence in full. **Anyone
> reading those two suites should know the tests assert what the client does, not what the vendor
> sends.** The follow-up change should start by flipping the fakes to the documented protocol and
> watching them fail.

- [x] 5.5 TTS **Cartesia** (`Cartesia/`) — re-seed `CartesiaFakeServer` with ~~recorded~~
      **documentation-derived** binary frames (terms would clear a capture; the blocker is credential
      availability)
      — **done.** `Recordings/cartesia-tts/`: `audio-chunk-pcm-s16le-8khz.raw` (2 008 B) and
      `done-frame.json` (+ sidecars). Doc: `docs.cartesia.ai/2024-11-13/api-reference/tts/tts`,
      pinned to the `Cartesia-Version` value `CartesiaOptions` actually sends, `revision: "undated"`.
      **Finding:** `CartesiaTtsControlMessage` models `type` and nothing else, so `done`,
      `status_code` and `context_id` all arrive as unmodelled siblings. **See the base64 finding
      above — this is the more serious half.**
- [x] 5.6 TTS **Deepgram** (`Deepgram/`) — re-seed `DeepgramTtsFakeServer`, including the real
      `warning` / `metadata` / `flushed` control frames the suite already filters
      — **done.** `Recordings/deepgram-tts/`: `audio-linear16-16khz.raw` (2 408 B),
      `metadata-frame.json`, `warning-frame.json`, `flushed-frame.json` (+ sidecars). Doc:
      `developers.deepgram.com/reference/text-to-speech-api/speak-streaming`, `revision: "undated"`.
      **Finding:** on `Metadata`, `model_uuid` and `additional_model_uuids` are unmodelled;
      `Warning` and `Flushed` are fully modelled. **Deepgram TTS is the one vendor of the four whose
      documented WS protocol really is raw binary audio**, so the base64 finding above does not
      touch it. The §8.5 `Flush`-triggered answer and `RequestDrainTimeout` are untouched.
- [x] 5.7 TTS **ElevenLabs** (`ElevenLabs/`) — re-seed `ElevenLabsFakeServer`, including real
      alignment messages
      — **done.** `Recordings/elevenlabs-tts/`: `audio-pcm-16khz.raw` (2 808 B) and
      `audio-output-frame.json` (2 602 B, carrying base64 audio + both alignment objects) with
      sidecars. Doc: `elevenlabs.io/docs/api-reference/text-to-speech/v-1-text-to-speech-voice-id-stream-input`,
      `revision: "undated"`. Alignment arrays describe the fictional sentence *"Su solicitud quedó
      registrada."*
      **Finding: the SDK models none of it.** There is no ElevenLabs *server* DTO in
      `VoiceAiTtsJsonContext` at all — the client skips every text frame — so `audio`,
      `alignment.{charStartTimesMs,charDurationsMs,chars}`, the same three under
      `normalizedAlignment`, and `FinalOutput.isFinal` are all unmodelled. **This is the other half
      of the base64 finding above.** §7's audio-payload-swap variant still needs a real capture and
      remains the named upgrade path.
- [x] 5.8 TTS **LMNT WebSocket path** (`Lmnt/`, default `LmntTransport.WebSocket`) — `LmntWsFakeServer`
      stays; only its frames become recorded. The suite ends up **split across both substrates by
      transport**, in one file
      — **done.** `Recordings/lmnt-ws/`: `audio-raw-16khz.raw` (1 808 B) and `finish-frame.json`
      (+ sidecars). `LmntHttpFakeServer` untouched (it is §4.6's to retire), confirmed by diff. Doc:
      `docs.lmnt.com/api/speech-sessions/create`, `revision: "undated"`.
      **Finding:** `LmntServerNotification` models `type`/`error`/`message`; of the documented server
      messages (`ready`, `timestamps`, `flush_complete`, `reset_complete`, `error`) only `error`
      overlaps, and `message` appears in **no** documented LMNT server payload. All `timestamps`
      fields are unmodelled.
      **Second finding, recorded rather than papered over:** `finish` is documented as a
      *client→server* message, not a server one — yet `LmntSpeechSynthesizer` terminates on it and a
      test asserts that. The fake still sends it, and `lmnt-ws/finish-frame.provenance.json` says
      plainly that the frame has no documented server-side existence.

> **Gap discovered, not closed — `CartesiaSpeechSynthesizerTests` has no
> `SynthesizeAsync_ShouldAbort_WhenCancelled`.** The three that exist in this suite are ElevenLabs,
> Deepgram and LMNT, which is why §8.5's repeat-run protocol reports "3 TTS" and always has. The
> `test-determinism` capability is written as if the cancellation contract were fenced on every
> streaming surface; on this one it is not, so §8.5's 30× green says nothing about Cartesia TTS.
> Adding it is new scope and is deliberately not taken here — it belongs with
> `websocket-fake-protocol-contract`, whose subject is exactly what every WebSocket fake must
> answer. Recorded so the omission is a decision rather than an oversight, per the same rule §5.9
> applies to the not-migrated verdicts.

**Shared infrastructure for §5.5–5.8.** `Tests/Verbara.Sdk.VoiceAi.Tts.Tests/SyntheticPcm.cs` —
`Triangle(sampleCount, periodSamples, amplitude)`, **integer-only on purpose**: `Math.Sin` is not
guaranteed bit-identical across platforms and these files are asserted byte-for-byte. Every provider
gets two fence tests — one asserting the fixture still carries the documented field names and its
exact byte length, one **regenerating** the `.raw` from the three generator parameters recorded in
its sidecar and comparing. Every audio length is deliberately not chunk-aligned (2 008 = 6×320+88,
2 408 = 7×320+168, 2 808 = 8×320+248, 1 808 = 5×320+208) so a partial final frame is always
exercised, using §4.4's valid assertion shape rather than the `length % chunkSize` one that was
disproved there. Largest fixture is 2 808 B — **1.1% of the 256 KiB cap**.
- [x] 5.9 Record the not-migrated verdict per provider in the suite (a one-line comment naming the
      transport) so the omission cannot later be read as an oversight
      — **done.** A `<summary>` on each of the 8 test classes naming the transport, the D2 reason
      (WireMock.NET cannot hold a duplex session) and the D4 remedy (fidelity comes from recorded
      frames, not a different server). Additions only — 50 lines across 8 files, no behaviour touched.
      Two carry an extra clause: Speechmatics STT points at its own HTTP-transport TTS sibling that
      *does* migrate, and LMNT WS states the D3 split against the HTTP class further down the file.

## 6. Convergence and cleanup

- [x] 6.1 Delete `Tests/Verbara.Sdk.VoiceAi.Stt.Tests/Helpers/MockHttpMessageHandler.cs` once no STT
      suite references it
      — **done** with §4.3, its last consumer. Three mentions of the name survive and are correct:
      all three are XML doc comments explaining what the substrate replaced
      (`WhisperSpeechRecognizerTests`, `AzureTtsSpeechSynthesizerTests`, `HttpProviderMockServer`).
      **§6.2's precedent does not transfer, and the difference matters:** the TTS copy left
      `Helpers/` empty so the directory went with it, but the STT `Helpers/` still holds
      `HttpProviderMockServerTests.cs` and `SttFrameGenerators.cs` — the latter is the shared
      cancellation-frame generator four sibling suites consume (#144), so the directory stays and
      every other suite keeps its `using`. Google's file dropped that import; no dangling `using`
      anywhere, build stays at 0 warnings.
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

      **Restated 2026-08-14, because the original wording cannot survive §5 and would have been
      re-confirmed by rote.** "Zero changes to any of the ten `*FakeServer*.cs` files" was a valid
      check while only §4 was in flight, but §5 is *defined* as editing exactly those files — so
      read literally this task would fail the moment the change does what it exists to do. The
      invariant it is actually protecting is narrower and still worth holding: **the fakes change
      only in where their payloads come from, never in how they answer.** Concretely, what must
      stay zero-diff through §5:

      - `Tests/Verbara.Sdk.TestInfrastructure/WebSocket/WebSocketTestServer.cs` and
        `WebSocketTestSession.cs` — literally untouched.
      - Every fake's accept/receive/close sequencing, its hold-open flag semantics, and its
        snapshot-under-lock accessors — i.e. none of the three §8.5 defect classes reintroduced.

      What legitimately changes: the frames a fake sends, and the mechanics of loading them.
      Re-confirm on that basis, per fake, not by diff-stat.

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
      **Extended 2026-08-14 for §5** (the entry is cumulative, so §5 lands in the same bullet rather
      than a second one): the eight documentation-derived WebSocket fixture sets, the
      `not-applicable` verdict and `source_schema` block the protocol grew to allow them, the
      `"undated"` finding that all eight vendor pages publish no revision marker, the fixture-integrity
      fences and why `SyntheticPcm` is integer-only — and, as its own sub-list, **the three shipped
      defects §5 surfaced and did not fix.** Two of those are user-facing (`Cartesia`/`ElevenLabs`
      TTS cannot receive audio from their vendors at all), so they belong in a consumer-visible
      changelog even though nothing about them is *changed* by this PR. Stating a defect this change
      is choosing not to fix is the honest form of D4: the ADR was adopted to make exactly this
      class of bug visible, and a fixture route that surfaces three defects and reports none would
      have wasted its own evidence.
      **Extended 2026-08-15 for §4.3** (still one cumulative bullet): the Google migration and its
      four unmodelled sibling fields, the second D12 seam, the surface count going three → four —
      and **two more shipped defects, both consumer-visible**, which is now five in this entry. The
      LMNT and Speechmatics TTS HTTP routes are stated with the live evidence that settled them,
      including the disproved `eleanor` hypothesis, because a changelog that reported only the
      confirmed half would suggest the vendor's published voice list is authoritative when this
      work showed it is not.
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

**Re-run 2026-08-14 for the §5 scope (WebSocket recordings).** All nine local items green; 8.9 still
open pending the PR. Deltas worth carrying forward rather than a bare "green":

| item | 2026-08-09 | 2026-08-14 | note |
|---|---|---|---|
| 8.3 unit lane | 3 027 passed / 30 asm | **3 048 passed / 30 asm**, 0 failed, 0 warnings | +21 tests, all fixture-integrity fences |
| 8.6 line | 80.4% | **80.41%** (band [78, 81]) | headroom to the stale-floor ceiling still **0.59 pp** |
| 8.6 branch | 66.05% | **66.08%** (floor 64) | |
| 8.6 lines measured | 12 967 | **12 967** (min 12 315) | unchanged — §5 touched no `src/**` |
| 8.6a patch | 100% (6/6 lines) | **n/a — 0 measurable lines**, pass | the D12 seam merged with Phase A, so this phase's diff is test+docs only |
| 8.6b exclusions | 0 / baseline 0 | **0 / baseline 0**, 863 files | |
| 8.7 pack | 29 pkgs, 0 refs | **29 pkgs, 0 `wiremock`/`TestInfrastructure` refs in any `.nuspec`** | |
| 8.8 AOT | 0 trim warnings | **0 trim warnings**, canary smoke-run `linux-x64` | |
| redaction (D5) | 8 files / 2 trees | **56 files / 2 trees** | the §5 fixtures are the growth |
| assert audit | — | **405 files, ~2 587 tests, 0 violations** | |

**Re-run 2026-08-15 for the §4.3 scope (Google STT migration + §6.1).** All nine local items green;
8.9 still open pending the PR.

| item | 2026-08-14 | 2026-08-15 | note |
|---|---|---|---|
| 8.3 unit lane | 3 048 passed / 30 asm | **3 051 passed / 30 asm**, 0 failed, 0 warnings | Google suite 6 → 9 |
| 8.6 line | 80.41% | **80.97%** (band [78, 81]) | **see the denominator finding below — this is not a gain this branch made** |
| 8.6 branch | 66.08% | **65.99%** (floor 64) | |
| 8.6 lines measured | 12 967 | **15 810** (min 12 315) | +2 843, and **+10 of them are this branch's** |
| 8.6a patch | n/a — 0 measurable lines | **100% (11/11 lines)**, floor 85 | measurable again — see the note below on how it first read as `n/a` |
| 8.6b exclusions | 0 / baseline 0 | **0 / baseline 0**, 863 files | |
| redaction (D5) | 56 files / 2 trees | **58 files / 2 trees** | the Google capture + sidecar |
| assert audit | 405 files, ~2 587 tests | **404 files, ~2 590 tests, 0 violations** | one file fewer: `MockHttpMessageHandler.cs` (§6.1) |

> **⚠ The coverage denominator moved by 22% and it was not this change.** The prior two §8 runs
> recorded 12 967 measurable lines, matching `coverage-floor.json`'s recorded 2026-07-20 baseline of
> 12 964. This run measures **15 810** against the same 25 assemblies. That is far too large to come
> from one `src/**` file gaining 29 lines, so it was measured rather than reasoned about: the same
> command was run against a clean `origin/main` worktree.
>
> | | `origin/main` @ `1542002b` | this branch | delta |
> |---|---|---|---|
> | lines measurable | 15 800 | 15 810 | **+10** |
> | lines covered | 12 792 | 12 802 | **+10** |
> | line coverage | 80.96% | 80.97% | +0.01 pp |
>
> **The jump is already on `main` and this branch contributes ten lines, all of them covered.** The
> likely mechanism is `#167`, which enabled `CentralPackageTransitivePinningEnabled` and collapsed
> 28 lower duplicate resolutions — the same class of packaging change that Phase A already showed
> can silently change what coverlet instruments (a `FrameworkReference` once cost ~19 pp with every
> test still green). Recorded here rather than fixed, because attributing it firmly means bisecting
> `main`, which is not this change's work.
>
> **The consequence is a live one:** the two-sided band's stale-floor ceiling is `line + slack` =
> **81.0**, and `main` now sits at 80.96%. The **0.59 pp of headroom this file claimed on
> 2026-08-14 is gone — the real figure is 0.03 pp**, and the next PR that improves coverage at all
> will trip the backstop. That is the ratchet working as designed, and the prescribed remedy is in
> `coverage-floor.json`'s own comment: raise `line` to `floor(measured)` = **80** in the same PR.
> Not done here. Raising a gate is a deliberate act, this change's diff does not force it, and
> doing it inside a test-substrate PR would bury a repo-wide gating decision in an unrelated
> review. It needs its own PR.

> **8.6a read `n/a` on the first attempt and that was a measurement error, not a result.**
> `check-patch-coverage.py` drives `diff-cover` off `git merge-base origin/main HEAD`, which reads
> **committed** history; the first run happened with the work still uncommitted, so it saw an empty
> diff and reported "docs/config-only change — n/a, pass". Re-run after committing, it reports
> **100% (11/11 changed executable lines)** against the floor of 85. Worth recording because the
> previous phase's `n/a` was *genuine* (the D12 seam had merged with Phase A, leaving a test-only
> diff), which makes this failure mode read as a repeat of a known-benign result — a gate that
> passes for the wrong reason, in the exact place this change spent §8 arguing that a green run is
> not the same as a verified one. **Run this gate after the commit, never before.**

**Twenty-one new tests moved line coverage by 0.01 pp** (2026-08-14), and that is the expected
shape, not an
anticlimax: §5 added *tests over test fixtures*, and `coverlet.runsettings` excludes test assemblies
from the denominator. A fixture-integrity fence cannot raise `src/**` coverage — it defends the
*inputs* the existing tests run on. Recorded because the opposite reading ("21 tests bought us
nothing") is the one a reviewer reaches for, and because it means §5 spends none of the 0.59 pp the
remaining HTTP migrations will need.

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
      >
      > **Second amendment (2026-08-17) — two of the twelve refutations did not hold, and not for the
      > same reason.** The sweep's standard was right and so was its technique: forcing the
      > interleaving with delay → 0. Applying that same technique to the two Class A timers it left in
      > place fires on both — `CartesiaFakeServer` **10/10** on
      > `SynthesizeAsync_ShouldSendADistinctContextId_PerRequest`, `ElevenLabsFakeServer` **5/5** on
      > `SynthesizeAsync_ShouldSendTextChunk`. One defect, two different mistakes:
      >
      > | Fake | Observing test | Existed at the sweep? | The refutation was |
      > |---|---|---|---|
      > | Cartesia TTS | `…ShouldSendADistinctContextId_PerRequest` | **no** — added 2026-08-16 by #180 | correct then, and **expired** |
      > | ElevenLabs TTS | `SynthesizeAsync_ShouldSendTextChunk` | **yes** — since 2026-05-05, asserting on `ReceivedJsonMessages` in the same words | **wrong** |
      >
      > Cartesia is the instructive one precisely because nothing was done badly. A per-instance race
      > needs two sessions to be visible, and #180 wrote the first test that issues two requests
      > against one fake. **A refutation resting on "no test observes it today" has a shelf life, and
      > nothing re-runs it when a test is added.** ElevenLabs has no such defence: the observing test
      > predates the sweep by three months and its assertion is unchanged since.
      >
      > The price of the class, on the record rather than inferred: CI failed
      > `…ShouldSendADistinctContextId_PerRequest` on the merge-queue ref for #184, GitHub ejected the
      > PR, and the queue run was lost. The same test had passed on the PR ref minutes earlier — same
      > commit, same suite, runner load the only variable. That is this item's original lesson arriving
      > a second time.
      >
      > **Fixed by removing the timers, not by lengthening them.** Cartesia waits for the request (its
      > client opens one `ClientWebSocket` per `SynthesizeAsync`, so one request per session is the
      > entire signal); ElevenLabs waits for the empty-`text` end-of-input, because that client sends
      > three messages and the assertions are on the ones after the first. Both take the LMNT/Deepgram
      > shape: a causal arm, a receive-loop-ended arm so a client that sends nothing is still answered,
      > and a generous ceiling whose only job is to keep a fake from hanging a suite.
      >
      > `RealtimeFakeServer` was checked with the same control and **passes 5/5 at delay 0** — its
      > timer orders no assertion of any test that exists, so it is left alone. It stays on the
      > latent-hazard list above; that listing is unchanged, not upgraded, and it is not unowned —
      > `websocket-fake-protocol-contract` (Sdk/ADR-0045) §3.2 already scopes replacing that timer, on
      > the strength of the suite's 25 s of pure timeout rather than of a failing assertion. The point
      > of recording the negative result is that this control discriminated: it fired on two fakes and
      > refused the third.
      >
      > What to carry forward: a Class A timer costs less to **remove** than to refute, because the
      > causal wait is a few lines and the refutation has to be re-earned every time a test is added.
      > Verified after the fix: full TTS suite green **20/20 idle and 15/15 with twice as many spinners
      > as cores**, and the unit lane at **3 081 tests / 30 assemblies, 0 warnings**.

      > **Third amendment (2026-08-17) — the class swept beyond the two fakes, and one verdict above
      > turns out to be narrower than it reads.** Same technique throughout: force the interleaving,
      > see whether an assertion moves, and record the negative results as results.
      >
      > **The Realtime negative result does not generalise to the fake.** `RealtimeFakeServer` has
      > three timers, not one — 30 ms before the first event, 5 ms between events, 100 ms before
      > closing — and the amendment above controlled only the first. Controlled one at a time, all
      > three pass **59/59**. Controlled **together**, the suite fails: 3 tests in one run and 1 in the
      > next, and that inconsistency is itself the finding, because a race is what varies between two
      > runs of the same build. The assertion that falls is
      > `Bridge_ExecutesFunction_AndSendsResultToServer` waiting on a `response.create` the client
      > never got to send, because the fake closed first — so the load-bearing timer is the
      > **pre-close** one, and no single timer holds the suite up. The *sum* of the slack does. The
      > earlier "passes 5/5 at delay 0" was true of the timer it tested and is left standing rather
      > than edited, because the pair is the lesson: **an individually-refuted timer can still be
      > load-bearing in company, so a per-timer control does not clear a fake.** The fix still belongs
      > to `websocket-fake-protocol-contract` (Sdk/ADR-0045) §3.2 and is deliberately not attempted
      > here — the causal close condition is "the client has sent everything it is going to", which
      > the fake cannot derive; the vendor keeps the session open instead, so §3.2's redesign across
      > 59 tests is the fix and a fourth timer would not be.
      >
      > **CI's other known flake had the same defect with the seam already in hand.**
      > `WebSocketAudioSessionTests.ReadPump_ShouldTransitionToDisconnected_WhenCloseFrameReceived`
      > slept 100 ms and then asserted the state; at 0 it fails. It now waits on `StateChanges`, the
      > observable it was already subscribed to — the same file's `DtmfControlMessage_…` test has
      > waited that way all along, so this was an inconsistency inside one file rather than a missing
      > capability. Checked in the other direction too: with the close frame removed the wait throws
      > `TimeoutException` in 5 s instead of asserting on an empty list. Two sibling barriers were
      > controlled and **refuted** — `…ShouldReturnEmpty_WhenChannelCompleted`'s 50 ms and
      > `AudioSocketServerTests`' 300 ms process-and-drop both pass at 0 — and left alone, now with
      > the shelf-life caveat attached to them by name.
      >
      > **Form 2 — a test resting on a fixed global resource — has exactly one defect in the tree, and
      > not where a literal search puts it.** `FastAgiIntegrationTests` binds 4573 because the dialplan
      > dials back to it (`docker/functional/asterisk-config/extensions.conf`); that port is
      > contractual and cannot move. The *other* holder was invisible: `AddVerbara` registers
      > `AgiHostedService` unconditionally, so every started host binds `AgiPort`, and two
      > `GracefulShutdownTests` tests that never speak AGI left it at its **4573 default**. No file in
      > the test tree contains the string.
      >
      > From the queue run's own log rather than from inference: `Verbara.Sdk.IntegrationTests` ran
      > 10:52:42.6 → 10:53:20.5 and `Verbara.Sdk.FunctionalTests` 10:52:44.9 → 11:09:11.7 — **a 36 s
      > overlap** — with the failing bind at 10:53:20.1 while one of those two tests was live.
      > `RunConfiguration.MaxCpuCount=1` does not separate them: the comment in `ci.yml` claiming it
      > serialises the projects states an intent, and the same log shows four other assemblies
      > interleaving. Two plausible mechanisms were **refuted** before the real one was accepted — an
      > accepted socket in `TIME_WAIT` does not block the re-bind (4/4 rounds), and neither does an
      > accepted leg still open when the listener stops (4/4), even though `FastAgiServer.StopAsync`
      > awaits the accept loop but not its fire-and-forget handlers so that leg really does outlive it.
      > Only two live **listeners** conflict, wildcard against loopback included. Fixed by taking 4573
      > away from the side that never needed it. Verified as a property rather than as a green test:
      > polling `ss` while those two tests run shows 4573 listening on unfixed `main` and **never**
      > with the fix.
      >
      > Refuted in the same pass and left alone: every `AudioSocketServer` in the tree already binds
      > `Port = 0`; the `Di/` and `Hosting/` registration tests build a provider and never start a
      > host, so their `AddVerbara` binds nothing; `HealthCheckEdgeCaseTests` says as much in its own
      > comment; and `AgiHealthCheckIntegrationTests` was already on port 0 — the outlier was one file,
      > never a convention. One **latent** hazard was fixed anyway because refuting it costs more than
      > changing it: `HostShutdown_ShouldStopAgiServer` hard-coded 14573 under a comment reading "pick
      > a free ephemeral port", which it was not.
      >
      > **Also closed here:** the two ceilings the second amendment introduced were themselves unmarked
      > `Task.Delay` calls, so `sync-fence-baseline.json` still grandfathered both files at 1 — the fix
      > removed the barriers but not the allowance. Both now carry
      > `// fence-allow: GUARD-TIMEOUT` and both entries are **0**, which is what makes the next timer
      > in those files fail the build rather than inherit room to exist.
      >
      > Two things found in passing and deliberately **not** changed, recorded so they are not
      > rediscovered as new. `AudioSocketServerTests.HandleConnection_ShouldDisposeSession_WhenNoUuidReceived`
      > waits 600 ms for a 300 ms idle timeout and then asserts `ActiveStreamCount == 0` — but the
      > server only registers a stream after a non-empty `ChannelId` (`AudioSocketServer.cs:110-123`),
      > and the test never sends a UUID, so the assertion holds whether or not the idle timeout fires.
      > That is a test that cannot fail, which is a different defect from a test that fails on load and
      > wants its own fix. And `AddVerbara` opens a listener on `IPAddress.Any:4573` for every
      > consumer, AGI or not, with no opt-out — shipped behaviour and a product question, not a test
      > defect, so nothing here touches it.
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
- [x] 8.9 CI green end to end (`pull_request` + `merge_group`), with the wall-clock delta versus the
      pre-change baseline recorded and judged acceptable under ADR-0038
      — **the only item that cannot be closed locally; it requires an open PR.**
      — **`pull_request` green** on [#159](https://github.com/verbara/Verbara.Sdk/pull/159)
      (run `31335450414`, 2026-08-09): 14 checks pass, 0 fail — AOT Trim Check, Analyze (C#), Audit
      Test Asserts, CodeQL, **Coverage Ratchet**, Coverage Script Tests, Dependency Review, both
      Docs-only gates, Functional Tests (Testcontainers) (23), OpenSpec Validate, Pack Warnings Gate,
      **Unit Tests**, aot-check. The Coverage Ratchet result is the one that had never actually run
      before — it was `skipped` on the previous attempt because `needs: unit-tests` failed (ADR-0038
      D2), so §8.6's locally-computed floor is only now confirmed by CI itself.
      — **`merge_group` green** (run `31347774014`, 2026-08-10; merged as `fac22bce`): all 10 jobs
      pass, including **`Functional Tests (Testcontainers) (22)`** — the variant that per ADR-0038 D3
      never reports on `pull_request` and therefore could not be observed before queue entry. Worth
      being precise about what that buys: under this repo's classic branch protection the (22)
      context is *observed, not enforced* (ADR-0038 addendum), so its green is evidence, not a gate
      that would have refused the merge. It was watched deliberately for that reason.

      **A trap this PR hit, worth recording because the symptom is misleading.** After the second
      fix commit, CI reported *nothing at all* for ~45 min — no pending checks, no queued runs. The
      cause was not an Actions outage or latency: five PRs (#151, #153, #154, #157, #158) had landed
      on `main` in the interim and the PR had gone `CONFLICTING`. **With no computable merge ref,
      GitHub creates zero check-suites** (`total_count: 0` on the head SHA), so "no checks reported"
      is a *conflict* symptom that looks exactly like an infrastructure stall. Resolved by merging
      `main` in rather than rebasing — the branch was already published, and the queue squashes
      anyway. One real conflict, in this file: #154's WebSocket terms review landed on the same §5
      anchor as the ninth-fake inventory finding; both kept.

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

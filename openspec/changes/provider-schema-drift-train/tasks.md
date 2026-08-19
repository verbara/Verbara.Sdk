# Tasks — provider-schema-drift-train

Execution follows Subagent-Driven Development with FCM batching:
**Phase A (batch)** = §1 license clearance + §2 vendoring · **Phase B (focused)** = §3 sidecar schema
and validator + §4 derived fixtures + §5 the drift job · **Phase C (batch)** = §6 the documentation-derived
route in the recording protocol + §7 records + §8 verification.

§1 gates everything: nothing is vendored before its license is read from the actual file. §5 is
accepted only when it has been demonstrated failing on an in-scope **rename**, failing on an in-scope
**addition**, and passing on an out-of-scope edit — a drift job that has only ever been seen green
proves nothing.

## 1. License clearance — read the file, never the label

- [ ] 1.1 For each candidate source, fetch the raw license file at the commit to be pinned and record
      its first identifying line. The hosting service's sidebar label is not evidence. Three of these
      would be wrongly dismissed by a naive `/main/LICENSE` fetch — `speechmatics/docs` uses
      `LICENSE.md`, `speechmatics/speechmatics-python` uses `LICENSE.txt` on branch `master`, and
      `lmnt-com/lmnt-python` is on `master` — so record the resolved path and branch per source
- [ ] 1.2 Record the SPDX identifier and the **obligation**, not just the name. CC-BY-4.0 (Deepgram)
      is the strongest in the set: credit plus an indication of changes, wherever the material or a
      derivative is distributed. MIT requires notice retention. Apache-2.0 requires notice retention,
      **a copy of the license alongside the material**, and a change notice on modified files. None of
      the Apache sources ships a `NOTICE` file, so confirm there is none to propagate rather than
      assuming
- [ ] 1.3 **Resolve the CC-BY-into-MIT boundary before vendoring Deepgram.** Whether CC-BY-4.0
      material inside a public MIT repository needs more than per-file attribution — a note in the
      root `LICENSE`, a carve-out in any packaging manifest that enumerates repository content — is
      open. Answer it in writing and record the answer; do not vendor on the assumption that
      per-file attribution suffices. If the answer is unclear, Deepgram's contract is the one item
      that waits, and the change ships without it
- [ ] 1.4 Confirm each source's upstream cadence and who authors it — a bot on a schedule, a bot on
      release, or humans through review. This sets what a diff means. Known at proposal time:
      Deepgram hourly by bot, OpenAI ~daily by bot, ElevenLabs on regeneration by bot, Cartesia on
      release by bot, Speechmatics by reviewed human PRs, **LMNT manually and ~2 months stale**
- [ ] 1.5 Record the exclusions with their reason, one line each, as the answer rather than as an
      absence — and read the client in `src/` before writing the reason:
      **AssemblyAI** publishes no specification repository (the whole org was enumerated; its MIT node
      SDK's generated types describe the retired v2 Realtime surface while the live v3 types are
      hand-written) — the one genuinely uncovered surface.
      Two entries from an earlier draft are **withdrawn**: "Google STT is bidirectional gRPC" is
      wrong — `GoogleSpeechRecognizer` holds an `HttpClient` and POSTs to
      `https://speech.googleapis.com/v1/speech:recognize`, so it is a REST surface with an exact
      Apache-2.0 contract (§2.4); and "classic Azure Speech realtime WebSocket" describes **no client
      that exists in this repo** — a grep for VoiceLive, `voice-live` and
      `stt.speech.microsoft.com` across `src/` returns nothing. Azure TTS and AzureWhisper STT do
      exist and already have recorded captures, so contract coverage is not their binding constraint
- [ ] 1.6 Confirm that adding these files does not disturb the repo's own license posture — this is a
      public MIT repository and the vendored material is not MIT in every case. State where the
      boundary is drawn and check it against `LICENSE` and any packaging manifest that enumerates
      repository content

## 2. Vendor the contracts, pinned

- [ ] 2.1 Create `contracts/<provider-slug>/` using the same slug vocabulary as
      `docs/guides/provider-recording-protocol.md` §2, so a contract and a capture for the same
      surface sort together
- [ ] 2.2 Vendor Tier 1 first — the sources that model the exact surface the SDK speaks:
      `speechmatics/docs` `spec/realtime.yaml` (AsyncAPI 3.0.0, MIT) and
      `deepgram/deepgram-api-specs` `asyncapi.yml` (AsyncAPI 2.6.0, CC-BY-4.0, five channels),
      subject to §1.3. These two carry the change; if only these land it is still worth shipping
- [ ] 2.3 Vendor Tier 2 — `openai/openai-openapi` `openapi.yaml`, which covers both the Realtime
      WebSocket message schemas and the Whisper REST endpoint. It declares message schemas without
      channel declarations, so record that limitation in the sidecar: the payloads are authoritative,
      the transport framing is not described and must not be inferred from them.
      **Do not vendor Azure Voice Live** — there is no client for it in this repo, and a contract with
      no surface to model is coverage debt disguised as coverage
- [ ] 2.4 Vendor the Google REST contract: `googleapis/googleapis`
      `google/cloud/speech/v1/cloud_speech.proto` (Apache-2.0), which carries
      `post: "/v1/speech:recognize"` — the exact endpoint `GoogleSpeechRecognizer` calls. Record the
      measured limit in the sidecar: its six `(google.api.field_behavior) = REQUIRED` annotations are
      all on **request** messages (`RecognizeRequest.config`, `RecognizeRequest.audio`,
      `RecognitionConfig.language_code` and their long-running twins), and every response message
      (`RecognizeResponse`, `SpeechRecognitionResult`, `SpeechRecognitionAlternative`, `WordInfo`)
      carries none — as does the live Discovery document (`speech:v1` rev 20260708, 31 schemas,
      `required` on zero of them). So it supplies field set and shape for §5, and **no** authority for
      `provider-dto-robustness-fences` §6
- [ ] 2.5 Vendor Tier 3 — LMNT, Cartesia, ElevenLabs. These are **generated client code, not a
      specification**; the sidecar must say so, because their authority is derivative. For
      ElevenLabs, read `.fernignore` first and record which paths are generated and which are
      hand-written: the realtime STT and ConvAI clients are excluded from generation and carry no
      more authority than any other hand-written source
- [ ] 2.6 OpenAI's `openapi.yaml` is ~2.85 MB. Vendor only the subtree the SDK models rather than the
      whole document, and record in the sidecar exactly what was extracted and how, so the extraction
      is reproducible and the omission is not mistaken for the vendor's full contract
- [ ] 2.7 Every vendored file gets a sidecar per §3 and a scope manifest per §5.3 before it is
      committed. A contract without both does not land

## 3. The `spec-derived` class, and the validator that enforces it

- [ ] 3.1 `docs/guides/provider-recording-protocol.md` §5 — add `"spec-derived"` as a third `class`
      value beside `"recorded"` and `"synthetic"`, with its meaning stated in the same register as
      the existing two: the field set, optionality and shape are the vendor's; the values are locally
      authored
- [ ] 3.2 Add the keys the new class requires — `spec_source` (repository), `spec_commit` (the pin),
      `spec_license` (SPDX) — and mark them required **when and only when** `class` is
      `"spec-derived"`, so the table stays readable for the other two classes
- [ ] 3.3 Add a worked example sidecar for a `spec-derived` fixture, matching the shape and length of
      the existing `azure-tts` example
- [ ] 3.4 §7 — add a sentence distinguishing a terms verdict on a vendor's **Output** from the
      license on its **published specification**. This distinction is the whole reason five surfaces
      were believed blocked; leaving it implicit invites the same conclusion again
- [ ] 3.5 **Write the sidecar validator — there is nothing to extend.** §9 of that guide enforces
      exactly one rule today, `scripts/check-recording-redaction.py`, which scans for
      credential-shaped strings; nothing checks that a sidecar exists, parses, or carries the keys its
      class requires. Add `scripts/check-provenance-sidecars.py` beside it, covering captures **and**
      vendored contracts: every artifact has a sidecar, every sidecar parses, every sidecar carries
      the keys its `class` requires, and every `spec_license` matches the SPDX in the vendored license
      file it names
- [ ] 3.6 Give it the same liveness posture as its neighbour: a built-in positive and negative canary
      run before the scan, so a validator broken by a careless edit fails loudly instead of silently
      finding nothing. Unit tests in `scripts/tests/`, wired into the lane that already runs the
      other guard-script tests
- [ ] 3.7 Document it in §9 alongside the redaction guard, including what it deliberately does not
      check — it verifies that provenance is *recorded*, never that the recorded provenance is *true*

## 4. Fixtures derived from the contracts

- [ ] 4.1 Derive, per reachable surface, the frames the fake servers currently hand-author. Start
      with Deepgram STT `Results` — the case the archived `wiremock-http-provider-substrate` §5.1 named,
      where the
      hand-authored object carries five fields and the real one carries `speech_final`,
      `channel_index`, `duration`, `start`, `metadata` and word arrays
- [ ] 4.2 Take the **field set and optionality** from the contract and author values locally. Do not
      copy example values out of a contract without checking whether the license and the surrounding
      obligations cover them; where they do, say so in the sidecar
- [ ] 4.3 Each derived fixture gets a `spec-derived` sidecar naming the contract, the pin and the
      license, and each carries the attribution its license demands — for Deepgram, credit plus an
      indication of changes; for Apache-2.0 sources, the license copy travels too
- [ ] 4.4 Feed the optionality back into `provider-dto-robustness-fences` §6 as a **per-message**
      required-set, not a per-surface one. That change may only place `[JsonRequired]` on a DTO
      modelling exactly one message type, or on a union DTO where every message it decodes agrees —
      so the artifact this change delivers must be structured per message for that rule to be
      checkable. Deliver it explicitly; it is the deliverable the other change consumes, and
      delivering it is the point of doing this one first
- [ ] 4.5 Record, per surface, where the contract supplies **no** required-set at all — Google above
      all — so the other change reads an explicit "no authority here" rather than an empty list it
      might mistake for "nothing is required"
- [ ] 4.6 Where the contract contradicts what the SDK models today, that is a **finding**, not a
      fixture problem. Record each one and route it; do not silently reshape the fixture to match the
      current DTO, which would hide exactly the bug this change exists to surface

## 5. The scheduled drift job

- [ ] 5.1 `.github/workflows/provider-schema-drift.yml` — `schedule` in the repo's Sunday slot
      (`perf-regression.yml` uses `'0 4 * * 0'`, `codeql.yml` `'0 6 * * 0'`; pick a third that does
      not collide) plus `workflow_dispatch`. Match `perf-regression.yml`'s house style: a header
      comment stating **why scheduled and not per-PR**, `timeout-minutes`, and an artifact upload
      with `retention-days`
- [ ] 5.2 **No `pull_request` trigger and no `merge_group` trigger.** ADR-0043 (`Proposed`) sets the
      precedent: evidence rides the scheduled train. A vendor's release cadence must never turn a
      contributor's unrelated PR red
- [ ] 5.3 Write one **scope manifest** per contract — a checked-in data file naming the message and
      schema **subtrees** the SDK models, as document paths a YAML/JSON reader can resolve. Subtrees,
      not leaf fields: a vendor adding a field to a modelled message must be in scope, and a leaf list
      would make additions invisible by construction. This is a data file, not a computation —
      deriving the scope from CLR types at job time would need Roslyn or reflection plus a
      type-name-to-schema-node mapping that does not exist, which is not something a `curl`-and-diff
      job can do
- [ ] 5.4 A governance test keeps the manifest honest **in both directions**: every type reachable
      from a provider `JsonSerializerContext` maps to at least one manifest entry, and every manifest
      entry resolves in the vendored contract. Reachability, not `[JsonSerializable]` registration —
      `VoiceAiSttJsonContext` declares 19 types and registers 17, and the two it omits
      (`DeepgramChannel`, `DeepgramAlternative`) hold `transcript` and `confidence`
- [ ] 5.5 Fetch each contract at upstream head, diff **only** the manifest subtrees against the pin,
      and upload the diff as an artifact
- [ ] 5.6 Fail the scheduled job on in-scope drift so the failure notifies. Never fail on out-of-scope
      churn — a bot-regenerated 3980-line document accumulates edits in parts the SDK does not read,
      and a job that reports every one of them is muted within a week and then reports nothing while
      appearing green
- [ ] 5.7 Negative-test the scoping in **three** directions before accepting the job: an in-scope
      rename must fail it, an in-scope **addition** to a modelled message must fail it, and an
      out-of-scope edit (touch a channel the SDK does not use) must leave it green. Record all three
      observations. A drift job only ever seen green proves nothing
- [ ] 5.8 Handle upstream unavailability as a distinct outcome from drift: a fetch that 404s or times
      out reports "could not check", never "no drift". A silent skip is the failure mode that makes
      the whole train worthless
- [ ] 5.9 Document the response procedure in the workflow header — what a human does when it fails:
      read the diff artifact, decide whether the SDK must change, and re-pin deliberately. An alarm
      with no documented response is an alarm that gets muted

## 6. Amend the documentation-derived route in `docs/guides/provider-recording-protocol.md` §7

> **Re-pointed 2026-08-19.** These four tasks were written against `wiremock-http-provider-substrate`
> §5, which archived on 2026-08-17 as
> `openspec/changes/archive/2026-08-17-wiremock-http-provider-substrate/`. An archive is history and
> is never edited, so the amendment lands where that material lives and is still maintained: §7's
> *"Documentation-derived fixtures — the route that needs no verdict"* and the per-provider findings
> beneath it. The substance is unchanged; only the file it is written into moved. Left pointing at
> the archive, this whole section would have been unexecutable — a task that cannot be done is
> indistinguishable from one nobody got to.

- [ ] 6.1 Amend the §7 preamble that blesses *"frames authored to the vendor's published protocol
      documentation"*: keep the credential-free path, but make its source the machine-readable
      contract wherever one exists. Prose is the fallback, not the default — transcribing prose is
      the shared-misreading mechanism the programme exists to retire
- [ ] 6.2 Re-point all **seven** reachable surfaces at their contracts in their per-provider §7
      sections, noting the tier of each so the difference in authority is visible per surface:
      Deepgram STT, Cartesia STT / TTS, Speechmatics STT, Deepgram TTS, ElevenLabs TTS, LMNT WS
- [ ] 6.3 AssemblyAI STT keeps the prose-derived path and gains the recorded reason: no specification
      repository exists and the generated types describe a retired API version. This is the one
      surface the change genuinely cannot reach
- [ ] 6.4 Amend §7's *"How to read the verdicts"* table so the `not-cleared` verdicts read as
      verdicts on **Output**, with the contract's license in a second column. As written they imply
      the surface is unreachable, which is what stalled it

## 7. Decision record and docs

- [ ] 7.1 Write `docs/decisions/0047-vendor-contracts-as-evidence-class.md`: the evidence-class
      taxonomy (recorded · spec-derived · synthetic, and what each does and does not prove), the tier
      ranking on *"does the contract model the surface the SDK actually speaks"*, the
      declared-subtree diff rule and why an unscoped diff is worse than none, and the
      Output-versus-specification distinction. Related: ADR-0041 (**amended, not edited** — its D4
      treats recordings as the evidence class; this ADR adds a second class and records that a
      recording detects drift only when someone re-captures, which nothing in this repo does),
      ADR-0043 (evidence on a scheduled train, off the PR path — `Proposed`, and cited as the
      precedent this job follows rather than as a settled rule), ADR-0046 (the `[JsonRequired]` rule
      whose authority this change supplies)
- [ ] 7.2 Record the two withdrawn exclusions in the ADR, not just in this change's tasks: Google STT
      was excluded on a protocol the SDK does not use, and "classic Azure Speech realtime" named a
      client that does not exist here. Both were caught by reading `src/`, which is the durable
      lesson worth recording
- [ ] 7.3 Add the ADR-0047 row to `docs/decisions/README.md` in numeric order, matching the existing
      row format
- [ ] 7.4 `CHANGELOG.md` — one `[Unreleased]` entry. No version bump: no `src/` change
- [ ] 7.5 A `contracts/README.md` stating what the directory is, that its contents are third-party
      material under their own licenses, how a contract is re-pinned, what a scope manifest is for,
      and what to do when the drift job fails. This directory is the most likely thing in the repo for
      an outside reader to misunderstand
- [ ] 7.6 While in `CHANGELOG.md`: `[Unreleased]` currently carries two separate `### Changed — CI`
      headings. Merge them, or leave them and say why — but do not add a third alongside them

## 8. Verification

- [ ] 8.1 `dotnet build Verbara.Sdk.slnx` — 0 warnings, 0 errors (`TreatWarningsAsErrors`)
- [ ] 8.2 `dotnet test Verbara.Sdk.slnx --filter "Category!=Functional&Category!=Integration&Category!=Realtime&Category!=Spike"`
      green — the four-exclusion filter `ci.yml` actually uses
- [ ] 8.3 `scripts/check-provenance-sidecars.py` green over every capture **and** every vendored
      contract, and its own unit tests green
- [ ] 8.4 The §5.4 manifest/reachability governance test green, and negative-tested: add a DTO with no
      manifest entry, watch it fail; add a manifest entry that resolves to nothing, watch it fail
- [ ] 8.5 `gh workflow run` the drift job manually and confirm it passes against the pins it was
      created with — a job whose first real run is a scheduled one is a job nobody has run
- [ ] 8.6 §5.7's three negative tests recorded with their observed output
- [ ] 8.7 Confirm the PR-path cost is genuinely zero: the new workflow appears in no
      `pull_request` or `merge_group` check-suite
- [ ] 8.8 License compliance re-read end to end by someone who did not vendor the files: every
      sidecar's SPDX matches its vendored license file, every CC-BY derivative carries credit and an
      indication of changes, every Apache-2.0 source's license copy travels with it, and §1.3's
      CC-BY-into-MIT answer is recorded rather than assumed
- [ ] 8.9 `openspec validate provider-schema-drift-train --type change --strict` clean, and
      `openspec validate --all --strict` still clean after the §6 amendments to a sibling change
- [ ] 8.10 CI green on the PR, zero warnings; enqueue with `gh pr merge <pr> --auto` (merge queue —
      never `--squash` / `--delete-branch`)

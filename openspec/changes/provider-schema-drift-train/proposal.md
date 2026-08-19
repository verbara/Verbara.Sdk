---
tier: MEDIANO
owner: Harol
approver: Harol
stakeholder: Anyone who will find out that a provider changed its wire format — the question is only whether they find out from a scheduled job or from a production incident
decision_ref: Sdk/ADR-0047
---

# Proposal: provider-schema-drift-train

## Why

ADR-0041 names the gap this change closes and does not close it:

> *"And there is no drift detector: when a vendor changes a response shape, nothing in this repo turns
> red."*

That is still true. `.github/workflows/` contains no job that fetches or compares any vendor contract.
Neither does the recording programme change it: a recording is a photograph. Re-capture after a vendor
renames a field and the existing tests, which assert on parsed values, would fail — but nothing in
this repository ever re-captures, so between captures nothing looks at the vendor again.

### The blocker for five surfaces was the wrong blocker

The archived `wiremock-http-provider-substrate` §5 recorded that five of its eight WebSocket surfaces
cannot take a payload recording — Deepgram STT and TTS, AssemblyAI STT, ElevenLabs TTS, LMNT — because the terms
review returned `not-cleared`. That verdict is about the vendor's **Output**: the bytes their service
returns. It says nothing about the vendor's **published specification**, which is a separate artifact
under its own license.

Nearly every blocked vendor publishes one. Licenses below were read from the raw `LICENSE` bytes at
the repository, not from the GitHub sidebar label — three of them would have been wrongly dismissed
otherwise, because the file is `LICENSE.md`, or `LICENSE.txt`, or lives on a `master` branch.

Ranked on the decisive property: **does the contract model the transport and the messages this SDK
actually speaks on that surface** — which is a WebSocket for the eight §5 surfaces and plain REST for
Google and Whisper.

| Tier | Vendor · surface | Source | License | Models the surface? |
|---|---|---|---|---|
| **1** | Speechmatics STT/TTS · WS | `speechmatics/docs` → `spec/realtime.yaml` — AsyncAPI **3.0.0**, `wss`, 1368 lines | **MIT** | ✅ real channels |
| **1** | Deepgram STT/TTS · WS | `deepgram/deepgram-api-specs` → `asyncapi.yml` — AsyncAPI **2.6.0**, 3980 lines, five channels (`/v1/listen`, `/v2/listen`, `/v1/speak`, `/v2/speak`, `/v1/agent/converse`) | **CC-BY-4.0** | ✅ real channels |
| **1** | Google STT · **REST** | `googleapis/googleapis` → `google/cloud/speech/v1/cloud_speech.proto` — carries `post: "/v1/speech:recognize"`, the exact endpoint `GoogleSpeechRecognizer` calls | **Apache-2.0** | ✅ exact endpoint, ⚠ see below |
| **2** | OpenAI Realtime · WS · and Whisper · REST | `openai/openai-openapi` → `openapi.yaml` — 167 `Realtime*` schemas, `RealtimeClientEvent` / `RealtimeServerEvent` discriminated unions | **MIT** | ⚠ message schemas only |
| **3** | LMNT TTS · WS | `lmnt-com/lmnt-python` → eleven `speech_session_*.py`, each citing `Source: asyncapi.yaml#/components/messages/…` | **Apache-2.0** | generated code; spec unpublished |
| **3** | Cartesia STT/TTS · WS | `cartesia-ai/cartesia-python` · `-js` → `websocket_client_event.py`, `websocket_response.py`, four `stt/*_websocket_*` types | **Apache-2.0** | generated code; spec unpublished |
| **3** | ElevenLabs TTS · WS | `elevenlabs/elevenlabs-python` · `-js` → `…stream_input/types/*`, `speech_engine_upstream/types/*` | **MIT** | ⚠ split-brain — the realtime STT and ConvAI clients are `.fernignore`d and hand-written |
| **4** | AssemblyAI STT · WS | spec repository **does not exist** (the whole org was enumerated). The MIT node SDK's generated types model only the **retired v2** Realtime API; the current v3 streaming types are hand-written | — | ❌ diverged |

**Correcting an earlier reading of the Google surface.** It was previously excluded as "bidirectional
gRPC, wrong shape for a WebSocket fixture". That describes Google's *streaming* API, not this SDK:
`GoogleSpeechRecognizer` holds an `HttpClient` and POSTs JSON to
`https://speech.googleapis.com/v1/speech:recognize`. It is a one-shot REST call, so it is not a §5
WebSocket surface at all — and its contract is both openly licensed and exact. Two artifacts describe
it: the Apache-2.0 proto above, and the live Discovery document (`speech:v1`, revision 20260708,
81 KB, 31 schemas including `RecognizeRequest`, `RecognizeResponse`, `RecognitionConfig`,
`SpeechRecognitionResult`, `SpeechRecognitionAlternative`).

The limit on both, measured: **neither carries a response-side required-set.** The Discovery document
declares `required` on zero of its 31 schemas; the proto's six `(google.api.field_behavior) =
REQUIRED` annotations all sit on *request* messages (`RecognizeRequest.config`,
`RecognizeRequest.audio`, `RecognitionConfig.language_code` and their long-running twins), and every
response message — `RecognizeResponse`, `SpeechRecognitionResult`, `SpeechRecognitionAlternative`,
`WordInfo` — carries none. So Google's contract supplies field set and shape for drift detection, but
supplies **no authority for `[JsonRequired]`** on the members the SDK reads. That distinction is
exactly the kind this change exists to make explicit rather than assume.

**Two Azure surfaces are in the SDK — Azure TTS and AzureWhisper STT — and neither appears above,**
because both already have recorded captures and contract coverage is not their binding constraint. An
earlier draft listed "classic Azure Speech realtime WebSocket" as a Tier-4 gap; no such client exists
anywhere in `src/`, and Azure Voice Live likewise has no surface in this repo. Both are removed rather
than left as phantom coverage debt.

So the artifact §5 needs exists for **seven of its eight** WebSocket surfaces — AssemblyAI is the only
one it cannot reach — costs nothing, and raises no terms question. And it is **strictly better than
the hand-authored-from-prose fallback §5 currently blesses**. Prose has to be read and transcribed by
a human, which is the same shared-misreading mechanism the whole programme exists to retire. A
machine-readable contract is diffable, and being diffable is the entire point: it is what a photograph
is not.

### It is also the missing authority for the other change

`provider-dto-robustness-fences` establishes that `[JsonRequired]` is the parse-time instrument that
catches a renamed field, that it may only be placed where the vendor's own contract says the field is
always sent, and that on a union DTO it may only be placed where *every* message on that socket
agrees. AsyncAPI and OpenAI's OpenAPI carry a `required:` list per message, which is exactly that
authority — at the granularity that makes the union-DTO constraint checkable rather than guessed.
**This change is that change's data source.** They are one instrument and its input, and the scheduled
diff is what notices when a vendor's `required:` list moves under an attribute already placed on it.

### Why now, and why not on the PR path

Because a scheduled diff is cheap and an incident is not, and because ADR-0043 (`Proposed`) sets the
precedent for where this kind of evidence belongs: on a scheduled train, never on the PR path. A
vendor pushing a spec change must not turn someone else's unrelated pull request red.

## What Changes

- **A vendored, pinned copy of each usable vendor contract** under `contracts/<provider-slug>/`, each
  with a provenance sidecar recording the source repository, the pinned commit SHA, the license SPDX
  identifier, the license file's actual path, and the attribution the license demands.
- **A third `class` value in the recording-provenance schema — `"spec-derived"`** — for a fixture
  built from a vendor's published contract rather than from its traffic. It sits between `recorded`
  and `synthetic` in fidelity and is honest about which: the field set and optionality are the
  vendor's, the values are ours. `docs/guides/provider-recording-protocol.md` §5 gains the value and
  the extra keys it requires (`spec_source`, `spec_commit`, `spec_license`).
- **A sidecar validator, which does not exist today.** §9 of that guide enforces exactly one rule —
  `scripts/check-recording-redaction.py`, which scans for credential-shaped strings. Nothing checks
  that a sidecar is present, parses, or carries the keys its class requires. This change writes that
  checker rather than extending a non-existent one, and it covers captures and vendored contracts
  alike.
- **Fixtures for the seven reachable WebSocket surfaces are derived from the contract**, not
  hand-authored from prose — the set the archived `wiremock-http-provider-substrate` §5 enumerated.
  The documentation-derived route in `docs/guides/provider-recording-protocol.md` §7 is amended
  accordingly — the cheaper path it blesses stays, but its source becomes the machine-readable
  contract wherever one exists.
- **A weekly `provider-schema-drift.yml`** — `cron` in the repo's established Sunday slot plus
  `workflow_dispatch`, matching `perf-regression.yml`'s shape — that re-fetches each contract at
  `HEAD`, diffs it against the vendored pin, uploads the diff as an artifact and fails **the scheduled
  job** on drift. It never runs on `pull_request` and never gates the merge queue.
- **The diff is scoped by a checked-in scope manifest, one per contract**, naming the message and
  schema **subtrees** the SDK models — as document paths, resolvable with a YAML/JSON reader. Scoping
  by subtree rather than by leaf field is deliberate: a vendor **adding** a field to a message the SDK
  reads is at least as common a shape change as a rename, and a leaf-list scope would make additions
  invisible by construction. A governance test keeps the manifest honest in both directions — every
  DTO reachable from a provider `JsonSerializerContext` maps to at least one manifest entry, and every
  manifest entry resolves in the vendored contract — so the scope cannot silently drift away from what
  the SDK reads. The manifest is a data file, not a computation: deriving the scope from CLR types at
  job time would require Roslyn or reflection and a type-name-to-schema-node mapping that does not
  exist, which is not something a `curl`-and-diff job can do.
- **Attribution is discharged where the license demands it.** Deepgram's CC-BY-4.0 is the only strong
  obligation in the set — credit and an indication of changes wherever the material or a derivative is
  distributed, which for a public MIT repo means the vendored file, the sidecar and the derived
  fixtures. MIT requires notice retention; Apache-2.0 requires notice retention, a copy of the license
  itself alongside the material, and a change notice on modified files.
- **`Sdk/ADR-0047`** records the evidence-class taxonomy, amends ADR-0041 D4 without editing it, and
  states the tier ranking and the drift-scoping rule.

**Not in scope.** AssemblyAI gets no contract and no drift check, and the change says so rather than
leaving a silent hole: it has no specification repository at all and its generated types describe a
retired API version. Google's contract is vendored for drift detection but is recorded as supplying
**no** response-side required-set, so it authorises nothing in `provider-dto-robustness-fences` §6. No
production code changes. No new runtime dependency — the diff job uses `curl`, a YAML reader and the
checked-in files, nothing that ships.

## Capabilities

### New Capabilities

None. The requirements land in `provider-contract-fidelity`, alongside the ones
`wiremock-http-provider-substrate` contributed there before it archived (where the bytes come from)
and `provider-dto-robustness-fences` (what the parser must survive when they change).

### Modified Capabilities

- `provider-contract-fidelity`: four ADDED requirements — a vendor's published contract is a
  first-class evidence class with its own provenance and license obligations; a fixture derived from
  one declares that provenance; drift is detected on a schedule against a manifest-declared subset;
  and a surface with no obtainable contract records the gap rather than substituting a guess.

## Impact

- New `contracts/` directory at the repo root: one vendored contract, one sidecar and one scope
  manifest per usable provider, checked in.
- `docs/guides/provider-recording-protocol.md`: §5 gains the `spec-derived` class and its keys; §7's
  terms table gains a sentence distinguishing a verdict on **Output** from the license on the
  **specification**, which is the confusion that produced the original blocker; §9 gains the new
  sidecar validator beside the existing redaction guard.
- `docs/guides/provider-recording-protocol.md` §7 (again, and for a different reason): the
  documentation-derived route's preamble amended and its seven reachable surfaces re-pointed at the
  contract. This was written as an edit to `wiremock-http-provider-substrate`'s tasks file; that
  change archived on 2026-08-17 and an archive is not edited, so the amendment lands in the guide
  that carries the material now.
- `.github/workflows/provider-schema-drift.yml`: new, scheduled only.
- `scripts/`: the sidecar validator and its unit tests, beside the existing guard scripts.
- `Tests/`: the derived fixtures, and the governance test that the scope manifests and the reachable
  DTO set agree.
- `docs/decisions/`: ADR-0047 + index row. `CHANGELOG.md`: one `[Unreleased]` entry.
- **No `src/` change, no public API change, no package version bump.** Nothing cascades to `Sdk.Pro`
  or `Platform`.
- CI: one more weekly job, plus the sidecar validator on the existing guard-script lane. Zero added
  cost in the merge queue.

## Architectural Risk

**Level:** LOW-MEDIUM. No production code and no PR-path gate, but the change checks third-party
licensed material into a public MIT repository, which is a compliance surface rather than a technical
one.

**Affected:** the repo's license posture and one scheduled job. The compliance risk is concrete and
bounded: CC-BY-4.0 requires attribution and an indication of changes, so the Deepgram contract and
anything derived from it must carry both, in the vendored file's sidecar and wherever a derived
fixture is distributed; Apache-2.0 additionally requires a copy of the license to travel with the
material. Getting either wrong in a public repo is a licensing defect, not a bug. Whether CC-BY-4.0
material sitting inside an MIT repository requires anything beyond per-file attribution — a note in
the root `LICENSE`, a carve-out in packaging manifests — is **unresolved and is a gating item**, not
an assumed answer. It is mitigated by making the sidecar mandatory and machine-checked, which requires
writing that checker: no such rule exists today, and stating otherwise would be the exact mistake this
change is about.

**The failure mode that would make this worthless is noise.** A drift job that reports every unrelated
edit in a continuously-regenerated 3980-line document gets muted within a week and then reports
nothing forever, while appearing green. The mechanism is not the re-sync cadence itself — a pinned
diff is silent when the content is unchanged — but the volume of *content* change a bot-regenerated
document accumulates in the parts the SDK does not read, and the fact that an unscoped diff cannot
tell a reader which of those changes mattered. Scoping to a declared subset is therefore not an
optimisation but the requirement that makes the alarm mean something, and the change is not done until
a deliberate out-of-scope edit is shown **not** to trigger it, an in-scope rename is shown to, and an
in-scope **addition** is shown to.

**Mitigation:** the job is scheduled-only, so a misfire cannot block a merge. Pins are explicit commit
SHAs, so the vendored copy cannot silently change under the fixtures derived from it. The scope
manifest is machine-checked against the reachable DTO set in both directions, so it cannot rot into a
scope that excludes what the SDK reads. And the gaps are enumerated rather than implied — AssemblyAI
gets no coverage from this instrument, Google's contract carries no response-side required-set, and
both are recorded per surface so a later reader can tell an unobtainable authority apart from one
nobody looked for.

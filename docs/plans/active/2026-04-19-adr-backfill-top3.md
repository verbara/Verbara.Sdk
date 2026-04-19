# ADR backfill top-3 — VoiceAi narrative + AmiStringPool (v1.11.1)

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Write 3 retrospective ADRs (0013, 0014, 0015) capturing load-bearing decisions identified in the 2026-04-19 product alignment audit, bundle with the perf fix at commit `41fff67`, and release as v1.11.1 (docs + perf patch, no API changes).

**Architecture:** Each ADR follows the template at `docs/decisions/0001-native-aot-first.md` (Context / Decision / Consequences / Alternatives considered). All 3 are `Accepted` and retrospectively dated because they record decisions already made and already in production code. The `Related:` link-graph between ADRs should weave the new entries into the existing catalog (0013 → 0003; 0014 → 0001, 0003; 0015 → 0003, 0008).

**Spec:** [docs/research/2026-04-19-product-alignment-audit.md](../../research/2026-04-19-product-alignment-audit.md) — section §4 (missing ADR catalog) items #1, #3, #4, plus §5 (narrative coherence) and §6 (recommendations).

**Scope boundaries:**
- DO write 3 ADRs (0013, 0014, 0015).
- DO update `docs/decisions/README.md` with entries for all three.
- DO NOT write the 9 other ADR candidates from §4 — those are deferred to a v1.12.x window.
- DO NOT modify any existing ADR (0001–0012).
- DO NOT cut the release tag from this plan — tagging v1.11.1 is a separate ship step after this plan is complete.

---

### Task 1: Write ADR-0013 — `ISessionHandler` abstraction for VoiceAi dispatch

- [ ] **Step 1: Create `docs/decisions/0013-isessionhandler-abstraction.md`.** Follow the template at `docs/decisions/0001-native-aot-first.md`. Draft skeleton to fill in:

  ```markdown
  # ADR-0013: `ISessionHandler` abstraction for VoiceAi dispatch

  - **Status:** Accepted
  - **Date:** 2026-03-19 (retrospective — decision made during Sprint 24)
  - **Deciders:** Harol A. Reina H.
  - **Related:** ADR-0003 (source generators)

  ## Context

  VoiceAi supports two fundamentally different inference patterns: turn-based
  (STT → LLM → TTS pipeline) and streaming bidirectional (OpenAI Realtime).
  The `AudioSocketSession` acceptor needs a single dispatch point regardless
  of which pattern a consumer picks...

  ## Decision

  Introduce `ISessionHandler`, a single interface with
  `Task HandleSessionAsync(IAudioSocketSession session, CancellationToken ct)`,
  and have both the turn-based pipeline and the OpenAI Realtime bridge
  implement it. The acceptor dispatches to a single registered handler; the
  consumer picks which by DI registration alone...

  ## Consequences

  - **Positive:** Consumers swap providers by changing DI registration; no compile-time branch; acceptor code is provider-agnostic; AOT-friendly (no reflection, no dynamic dispatch beyond one virtual call).
  - **Negative:** Two very different pipelines share one interface — the contract has to be generic enough to accommodate both, which limits what the acceptor can assume about session progress.
  - **Trade-off:** We accept a slightly looser contract (the acceptor can't observe per-turn state) in exchange for a single dispatch path that both patterns honor.

  ## Alternatives considered

  - **Monolithic `VoiceAiPipeline` with internal branch on provider type** — rejected because it couples turn-based and streaming into one class, forces every consumer to take the full dependency graph of both, and is AOT-hostile (branching on runtime type of handler).
  - **Two separate top-level APIs (one per pattern)** — rejected because callers would have to pick at compile time, breaking changes in one pattern would cascade to unrelated consumers, and the acceptor would need two code paths.
  - **Strategy via delegate (`Func<Session, Task>`)** — rejected because it loses the semantic type (handlers need lifecycle hooks the DI container wires up) and obscures the extension point in source-gen output.
  ```

- [ ] **Step 2: Verify code-reference links render.** Every path mentioned in Context / Evidence must resolve from `docs/decisions/` using relative paths. Expected files referenced:
  - `../../src/Asterisk.Sdk.VoiceAi/ISessionHandler.cs`
  - `../../src/Asterisk.Sdk.VoiceAi/Pipeline/VoiceAiPipeline.cs`
  - `../../src/Asterisk.Sdk.VoiceAi.OpenAiRealtime/OpenAiRealtimeBridge.cs`
  - `../../src/Asterisk.Sdk.VoiceAi.AudioSocket/AudioSocketSession.cs` (or equivalent acceptor entry point)

- [ ] **Step 3: Add entry to `docs/decisions/README.md`.** One-line row following the existing table format (see Task 4 for the shared update step).

- [ ] **Step 4: Commit** with message:

  ```
  docs(decisions): add ADR-0013 ISessionHandler abstraction for VoiceAi
  ```

---

### Task 2: Write ADR-0014 — Raw HTTP / `ClientWebSocket` VoiceAi providers

- [ ] **Step 1: Create `docs/decisions/0014-raw-http-websocket-voiceai-providers.md`.** Draft skeleton:

  ```markdown
  # ADR-0014: Raw HTTP / `ClientWebSocket` for VoiceAi providers (no vendor SDKs)

  - **Status:** Accepted
  - **Date:** 2026-03-19 (retrospective)
  - **Deciders:** Harol A. Reina H.
  - **Related:** ADR-0001 (Native AOT first), ADR-0003 (source generators over reflection)

  ## Context

  VoiceAi ships 4 STT providers (Deepgram, Google, Whisper, AzureWhisper)
  and 2 TTS providers (Azure, ElevenLabs). The writer should re-count
  before drafting (`ls src/Asterisk.Sdk.VoiceAi.Stt/` + `ls src/Asterisk.Sdk.VoiceAi.Tts/`)
  — the count may have grown between this plan date and execution date.
  Every official vendor SDK we surveyed carries reflection-based
  serialization, logging adapters, or unbounded dependency graphs that
  break Native AOT publish...

  ## Decision

  Each provider package is hand-rolled on `HttpClient` or `ClientWebSocket`
  with JSON source-generated contracts. No vendor SDK is referenced...

  ## Consequences

  - **Positive:** Zero trim warnings across all 6 providers; total provider code is small enough to audit line-by-line (writer should re-count via `find src/Asterisk.Sdk.VoiceAi.Stt src/Asterisk.Sdk.VoiceAi.Tts -name '*.cs' -not -path '*/bin/*' -not -path '*/obj/*' | xargs wc -l` before ADR draft); vendor breaking changes surface as isolated patches, not framework-wide ripples; consumers pay only for the providers they register.
  - **Negative:** We carry the maintenance burden of wire-protocol parity — when a vendor ships a new feature, we implement it ourselves instead of bumping a NuGet.
  - **Trade-off:** We trade "free" vendor SDK upgrades for AOT cleanliness, startup size, and dependency minimalism. Given the SDK targets PBX operators running 100K+ concurrent call fleets where AOT binary size and GC pressure matter, this trade is net-positive.

  ## Alternatives considered

  - **Official vendor SDKs** — rejected because every candidate (OpenAI, Azure Cognitive Services, Google Cloud Speech) introduces reflection, `System.Text.Json` polymorphism, or logging abstractions incompatible with Native AOT.
  - **Single unified abstraction over all vendor SDKs** — rejected because a vendor breaking change would cascade into the abstraction layer, and AOT would still be broken at the leaf.
  - **Generated clients from OpenAPI specs (NSwag, Kiota)** — rejected because many providers (especially streaming STT) use bespoke WebSocket framing not captured by OpenAPI, and generated clients typically emit reflection-based serializers.
  ```

- [ ] **Step 2: Verify code-reference links render** from `docs/decisions/`. Expected references:
  - `../../src/Asterisk.Sdk.VoiceAi.Stt.Deepgram/`
  - `../../src/Asterisk.Sdk.VoiceAi.Stt.Whisper/`
  - `../../src/Asterisk.Sdk.VoiceAi.Tts.ElevenLabs/`
  - `../../src/Asterisk.Sdk.VoiceAi.Tts.OpenAi/`
  - (and any remaining Stt/Tts provider dirs the writer enumerates)

- [ ] **Step 3: Add entry to `docs/decisions/README.md`** (bundled with Task 4 unless the writer chooses per-ADR README commits).

- [ ] **Step 4: Commit** with message:

  ```
  docs(decisions): add ADR-0014 raw HTTP/WebSocket VoiceAi providers
  ```

---

### Task 3: Write ADR-0015 — AMI string interning pool (FNV-1a)

- [ ] **Step 1: Create `docs/decisions/0015-ami-string-interning-pool.md`.** Draft skeleton:

  ```markdown
  # ADR-0015: AMI string interning pool (FNV-1a)

  - **Status:** Accepted
  - **Date:** 2026-03-19 (retrospective — optimization lives in production since v1.5.x)
  - **Deciders:** Harol A. Reina H.
  - **Related:** ADR-0003 (source generators over reflection), ADR-0008 (AMI exponential backoff)

  ## Context

  AMI parses 100K+ events per second on high-traffic Asterisk deployments.
  Each event carries 5–15 header keys, and the key set is small and bounded
  (roughly 150 distinct canonical keys across the 278 shipped events).
  Naive parsing allocates a fresh `string` per header per event —
  ~1M allocations/second at sustained load, measurable GC heap pressure...

  ## Decision

  Introduce `AmiStringPool` (`src/Asterisk.Sdk.Ami/Internal/AmiStringPool.cs`,
  344 LOC): a fixed-capacity 2048-bucket intern table keyed by an FNV-1a
  hash over the raw `ReadOnlySpan<byte>` header bytes, returning a
  pre-computed canonical `string` for known keys and short-circuiting
  allocation for the hot path...

  ## Consequences

  - **Positive:** ~8–12% reduction in AMI parsing GC pressure at sustained 100K events/sec; ~20%+ throughput uplift on the `ParseSingleEvent` benchmark under load; complements `System.IO.Pipelines` (which hands us heap-allocated `string`s that the JIT's own interning never covers); keeps the AMI fast path on the stack.
  - **Negative:** 2048 buckets × entry overhead = ~64 KB of process-lifetime memory per process regardless of workload; the FNV-1a table is pre-computed from the generator output, so a new canonical header requires a regen to get pooled (worst case: an uncommon header allocates normally, which is acceptable).
  - **Trade-off:** We accept a bounded upfront memory cost and a constrained set of "pooled" strings in exchange for near-zero allocation on the hottest AMI path.

  ## Follow-up

  The v1.11.1 perf fix at commit
  [`41fff67`](https://github.com/Harol-Reina/Asterisk.Sdk/commit/41fff67)
  adds a fast-path length check on `Output` header accumulation. That
  optimization is meaningful **because** `AmiStringPool` already removed
  allocation from the surrounding parse path; without pooling, the length
  check would be invisible under GC noise.

  ## Alternatives considered

  - **Generic `ConcurrentDictionary<string, string>`** — rejected because lookup requires already having a `string` (the allocation we're trying to avoid), LRU/eviction adds lock contention, and it's not specialized for the `ReadOnlySpan<byte>` hot path.
  - **Rely on the JIT's internal string interning** — rejected because JIT interning covers only string literals embedded in IL, not heap-allocated strings produced at runtime from `System.IO.Pipelines` buffers. Measurement confirmed zero hits from JIT interning on AMI header strings.
  - **`string.Intern`** — rejected for the same reason plus the added cost of always allocating the string first (intern is a lookup, not a decoder).
  - **`FrozenDictionary<string, string>`** (.NET 8+) — rejected because it still requires a `string` key at lookup time.
  ```

- [ ] **Step 2: Verify code-reference links render** from `docs/decisions/`. Expected references:
  - `../../src/Asterisk.Sdk.Ami/Internal/AmiStringPool.cs`
  - Commit link to `41fff67` (external GitHub URL, no relative path).

- [ ] **Step 3: Add entry to `docs/decisions/README.md`** (bundled with Task 4).

- [ ] **Step 4: Commit** with message:

  ```
  docs(decisions): add ADR-0015 AMI string interning pool
  ```

---

### Task 4: Update `docs/decisions/README.md`

- [ ] **Step 1: Append 3 rows to the ADR table** for 0013, 0014, 0015. Use the same column count and status vocabulary as existing rows.
- [ ] **Step 2: Verify the existing table format is preserved.** Same date format (`YYYY-MM-DD`), same status values (`Accepted`), no emoji, no trailing whitespace.
- [ ] **Step 3: Commit** with message:

  ```
  docs(decisions): register ADRs 0013-0015 in README
  ```

**Option:** the writer may instead fold each README row into the same commit as its corresponding ADR (Tasks 1–3). If so, skip this commit entirely. The guiding rule: each ADR lands with its README row in a single atomic commit, OR all three README rows land together after the three ADR commits — never leave a README out of sync with the ADR set.

---

### Task 5: Draft CHANGELOG entry for v1.11.1

- [ ] **Step 1: Open `CHANGELOG.md` at the repo root**, locate the `Unreleased` section or the header above the latest `v1.11.x` entry.
- [ ] **Step 2: Insert a v1.11.1 entry** above v1.11.0:

  ```markdown
  ## [1.11.1] — 2026-04-XX

  ### Performance
  - AMI: fast-path length check on `Output` header accumulation. Recovers ~35 ns of the v1.0 → v1.11 regression in `ParseSingleEvent`. Throughput 1.53M → 1.62M events/s single-thread. ([41fff67](https://github.com/Harol-Reina/Asterisk.Sdk/commit/41fff67))

  ### Documentation
  - ADR-0013: `ISessionHandler` abstraction for VoiceAi dispatch.
  - ADR-0014: Raw HTTP/WebSocket VoiceAi providers (no vendor SDKs).
  - ADR-0015: AMI string interning pool (FNV-1a).

  No API changes. No breaking changes. 0-warning build preserved.
  ```

- [ ] **Step 3: Do NOT tag v1.11.1.** Tagging is a separate shipping step. This plan leaves a tag-ready CHANGELOG.
- [ ] **Step 4: Commit** with message:

  ```
  docs(changelog): draft v1.11.1 entry
  ```

---

### Final verification

- [ ] Run `ls docs/decisions/ | wc -l` — returns 16 (12 existing ADRs + 3 new ADR files + `README.md`). If the existing count was different, confirm that `new_count == old_count + 3`.
- [ ] Run `grep -c '^- \*\*Status:\*\* Accepted$' docs/decisions/0013-*.md docs/decisions/0014-*.md docs/decisions/0015-*.md` — returns 3.
- [ ] Manually confirm each relative code-reference link in 0013 / 0014 / 0015 resolves to a file under `src/` (open at least one from each ADR).
- [ ] Run `dotnet build Asterisk.Sdk.slnx` — still 0 warnings (ADRs don't touch build, but confirm the repo is clean before handing off).
- [ ] Open `CHANGELOG.md` and confirm the `[1.11.1]` entry contains both `### Performance` and `### Documentation` subsections.
- [ ] Run `git log --oneline -10` — shows 3 or 4 `docs(decisions):` commits (one per ADR, optionally one README update, optionally one `docs(changelog):` commit).

---

## Sequencing note

The 3 ADRs are independent and can be written in any order or in parallel using `superpowers:subagent-driven-development`. Suggested execution: dispatch 3 subagents in sequence, one per ADR, each reviewing against the audit report §4 for its item number (#1 = 0015, #3 = 0013, #4 = 0014).

After all ADRs land, the release tag is a separate ~10-minute step: update `Directory.Build.props` `VersionPrefix` to `1.11.1`, build, pack to the local feed, push to nuget.org, `git tag v1.11.1 && git push --tags`, create GitHub release notes from the CHANGELOG entry.

# Tasks — longevity-soak-and-chaos

Three independent workstreams, sequenced cheap-and-certain first. **A** (chaos) and **B** (perf hot
paths) extend fixtures that already run and can land in either order. **C** (soak) is the expensive,
uncertain one and is deliberately last — it needs a call driver that does not exist yet. Section 1
gates all three; sections 6–7 close the change.

## 1. Foundation

- [ ] 1.1 Land `docs/decisions/0043-longevity-evidence-off-the-pr-path.md` (Proposed → Accepted at
      merge), recording D1 (never a required check), D3 (weekly ceiling), D6 (Issue, not a gate),
      D8 (observe-only warm-up), D9 (four consecutive green soaks), D10 (this change owns hot-path
      coverage; `enforce-unguarded-public-claims` owns the gate + baseline)
- [ ] 1.2 Confirm the sequencing agreement with `enforce-unguarded-public-claims` before either
      touches `.github/workflows/perf-regression.yml` — both edit that file; land them in series,
      never concurrently
- [ ] 1.3 Record the declared PR-path wall-clock budget for workstream A: measure the current
      `Functional Tests (Testcontainers)` job duration on `pull_request` as the pre-change baseline,
      so §2's budget check has a number to compare against

## 2. Workstream A — extended chaos suite

Substrate hardening first (2.1–2.4): every scenario below depends on a control handle or attribute
that is currently missing or broken.

- [ ] 2.1 Fix `ToxiproxyFactAttribute`: its availability probe hard-codes `localhost:8474` and
      ignores `TOXIPROXY_API_URL`, so it always reports unavailable under Testcontainers' random
      port mapping — which is why it is applied to zero tests today. Fix the probe, then apply the
      attribute to the existing Toxiproxy tests currently using plain `[Fact]`
- [ ] 2.2 Extend `ToxiproxyControl` with the primitives the new scenarios need but that no test can
      reach today: `upstream`-stream toxics (all 10 existing injections are `downstream`) and
      configurable `toxicity` (hard-coded to `1.0` inside `AddToxicAsync`)
- [ ] 2.3 Add a Toxiproxy proxy and/or container control handle for the Sessions Postgres backend —
      Postgres runs in the functional stack today only as Asterisk's realtime config backend: it is
      unproxied, has no `DockerControl` handle, and the Sessions store tests use a different fixture
- [ ] 2.4 Add a stop/start control handle to the NATS integration fixture (the container is
      currently started and disposed only, never faulted)
- [ ] 2.5 Scenario — **transport reset midstream**: inject `reset_peer` while a multi-line AMI
      response or event burst is in flight. Scoped deliberately *narrower* than the existing
      `ConnectionCutTests.TcpReset_ShouldTriggerReconnect`, which resets an **idle** connection and
      needs a follow-up `PingAction` to notice. Assert a deterministic failure for the pending
      action, successful reconnect, and no partial frame leaking into the next message
- [ ] 2.6 Scenario — **half-open socket**: one direction alive, peer silently gone. Use `slow_close`
      and/or `DockerControl.PauseContainerAsync` (both present in the tree, both never called). This
      is distinct from the existing `timeout: 0` full blackhole already covered by
      `SilentDrop_ShouldDetectViaHeartbeat` against the ADR-0021 heartbeat contract — assert the
      half-open case independently, do not restate the blackhole result
- [ ] 2.6a Scenario — **silent provider socket on a VoiceAi STT session**: the vendor's WebSocket
      stays open and stops sending. Routed here from `websocket-fake-class-ab-sweep` §5.7, which
      measured it rather than predicted it: neither side of an STT session carries a read bound, so
      suppressing a single frame (`DeepgramSpeechRecognizer.SendLoopAsync`'s terminator sent as
      `Binary` instead of `Text`, so the fake's `Text` branch never fires) left one test running past
      a 90 s kill and its whole class past 600 s, against 101 ms for the same tests restored. That
      sweep bounded the *fake* side only (`SessionReceiveCeiling`, 10 s, all four STT fakes) because
      its scope forbids touching `src/`. The client side is untouched and is the real exposure:
      the four `ReceiveLoopAsync` methods (`AssemblyAiSpeechRecognizer` and its three siblings) have
      no receive timeout, so against a live vendor that goes silent without closing `StreamAsync`
      never returns. Same shape as 2.6 on a different transport — assert a deterministic, named
      failure rather than a park
- [ ] 2.7 Scenario — **`core reload` with ~50 active channels**. No test issues `core reload` today;
      the only reloads are `pjsip reload` in the realtime suite with zero active channels. Assert
      the client stays usable or recovers, and that tracked channel state matches the PBX afterwards
- [ ] 2.8 Scenario — **Postgres restart mid-Sessions-transaction** (needs 2.3). Assert deterministic
      failure or recovery, and that the store serves reads and writes again afterwards with no
      leaked connection or half-written session
- [ ] 2.9 Scenario — **NATS broker disconnect mid-publish** (needs 2.4). Assert the publisher
      surfaces the failure deterministically and resumes once the broker returns
- [ ] 2.10 Assertion strength: every scenario in 2.5–2.9 asserts a *specific* recovered state. Do
      not use a `BeOneOf` spanning the whole connection-state enum — four existing chaos tests
      (`RestoreAfterPartition_ShouldReconnectCleanly`, `LatencySpike_ShouldRecoverAfterRestore`,
      `ActionDuringPartition_ShouldTimeoutCleanly`, `Connection_ShouldRespectMaxReconnectAttempts`)
      end that way and can never fail. `tools/audit-test-asserts.sh` will not catch this
- [ ] 2.11 Repeat-run each new scenario under load before merge (mirror the 30× protocol used by
      `ci-pipeline-slimming`) — zero flakes; a scenario that needs a retry is fixed or withdrawn
- [ ] 2.12 Measure the added PR-path wall-clock against the §1.3 baseline and the declared budget.
      Relocate any over-budget scenario to the scheduled train — do NOT discharge the budget by
      relaxing anything in the `ci-gating` capability

## 3. Workstream B — extended perf hot paths

Note the audit's corrections: three of the four originally-listed hot paths already have benchmarks
and needed reshaping or a naming fix, not new coverage.

- [ ] 3.1 **Naming defect** — `ChannelManager.GetById` does not exist. `ChannelManager` exposes
      `GetByUniqueId` and `GetByName`, and both are already benchmarked
      (`ChannelManagerBenchmark.LookupByUniqueId` / `.LookupByName`). Correct the
      `perf-regression.yml` step name to the member actually measured. **Do not** edit the README
      performance table here — a false public claim is `enforce-unguarded-public-claims` territory;
      hand it over with the same finding (that change also owns the stale "BenchmarkDotNet v0.14.0"
      citation, since `Directory.Packages.props` pins 0.15.8)
- [ ] 3.2 **Reshape ARI channel deserialize** — `AriJsonBenchmark.DeserializeChannel` exists, but its
      fixture sets only `Id`/`Name`/`State`, leaving `Caller`, `Connected`, `Dialplan` and
      `ChannelVars` null, so it understates a real payload. Make the fixture representative of what
      a live PBX emits. No new benchmark class needed
- [ ] 3.3 **Reshape observer dispatch** — `ObserverDispatchBenchmark` re-implements the fan-out loop
      locally over a no-op observer; the shipped path additionally timestamps, wraps each observer in
      `try/catch`, records two metrics and invokes an event. Measure through the public `Subscribe`
      seam over a `Pipe`-backed connection (the pattern `AmiConnectionTests` already uses) so the
      number describes shipped code
- [ ] 3.4 **New — in-process session correlate-by-LinkedId.** Only the storage-backend level is
      measured today (`RedisGetByLinkedId` / `PostgresGetByLinkedId`, Testcontainers-backed and
      already flagged flaky in the suite README). Benchmark the in-memory index lookup and the
      join-or-create branch in `CallSessionManager`, independently of any backend. Also worth a
      number: the in-memory store's `GetByLinkedIdAsync`, which is an O(n) scan over all values
- [ ] 3.5 If 3.4 needs `internal` access, add `InternalsVisibleTo Verbara.Sdk.Benchmarks` to
      `Verbara.Sdk.Sessions` (it exists today only on `Verbara.Sdk.Ami` and `Verbara.Sdk.Ari`).
      Confirm this leaves `PublicAPI.Unshipped.txt` empty — it is a csproj-only change with no
      public-API delta
- [ ] 3.6 Wire the new/renamed benchmarks into `perf-regression.yml` as additional steps, keeping
      the existing shape (one filter per step, separate `--artifacts` dir, `|| true`)
- [ ] 3.7 **Boundary check** — this workstream creates no `Tests/Verbara.Sdk.Benchmarks/baseline.json`,
      adds no comparison step, and changes no failure semantics. Confirm `gates.yaml` G6 still reads
      `status: na` after this workstream lands (ADR-0043 D10)

## 4. Workstream C — weekly soak

- [ ] 4.1 **Author the first SIPp scenario file(s)** in `docker/functional/sipp-scenarios/` — the
      directory contains only a `.gitkeep` today, so there is literally nothing to run
- [ ] 4.2 Make the SIPp driver rate-capable: both wrappers hard-code a bounded call count
      (`SippContainer.RunScenarioAsync` → `-m 1`; `SippControl.RunScenarioAsync` → `-m {calls}
      -l {calls} -r 1`). Sustained calls-per-minute needs `-r`/`-rp` plumbed through. Also decide
      the fate of `SippControl`, which has zero call sites and probes a container name
      (`functional-sipp`) that no compose file or fixture creates — fix it or delete it, do not
      leave a second dead driver
- [ ] 4.3 Build the long-lived host process the soak observes (a sustained AMI + Live + Sessions
      consumer) and expose its PID for descriptor sampling
- [ ] 4.4 Sampling loop: at a fixed interval capture managed heap size and the process's
      open-file-descriptor count; append to a structured trail file
- [ ] 4.5 `scripts/check-soak-snapshots.py` — reads the trail and evaluates the acceptance shape
      (heap delta bounded after warm-up, descriptor delta bounded, zero unhandled exceptions).
      Thresholds in a committed data file, never inline literals
- [ ] 4.6 `scripts/tests/test_check_soak_snapshots.py` — unit tests for the analyzer covering at
      minimum the stable, leaking, and malformed-input cases. Every gate script in this repo ships
      with tests (`check-coverage-floor.py`, `classify-docs-only.sh`); this one is no exception
- [ ] 4.7 `.github/workflows/soak.yml` — weekly `schedule:` + `workflow_dispatch` **only**. No
      `pull_request`, no `merge_group`, explicit `timeout-minutes`, never added to the required-check
      set (ADR-0043 D1/D3/D4). Reuse the existing Asterisk 22/23 stack and images — do not reinvent
      the fixture
- [ ] 4.8 Ship it **observe-only** (ADR-0043 D8): publish the trail as a retained artifact, never
      fail the job, until enough completed runs exist to calibrate thresholds from measured data
- [ ] 4.9 On-failure Issue creation carrying the trail (or artifact link) and the run link, with
      enough idempotency that repeated identical failures do not accumulate duplicates (ADR-0043 D6)
- [ ] 4.10 Calibrate thresholds from the observe-only runs, commit them, and promote the job to
      fail-on-threshold as a deliberate separate step

## 5. Verification

- [ ] 5.1 `dotnet test Verbara.Sdk.slnx` green locally with **zero warnings**
      (`TreatWarningsAsErrors` is on repo-wide)
- [ ] 5.2 CI green on both `pull_request` and `merge_group`, zero warnings, and the required-check
      set unchanged in name and number
- [ ] 5.3 `python3 -m unittest discover scripts/tests` green (picks up the new soak-analyzer tests)
- [ ] 5.4 `openspec validate --all --strict` green
- [ ] 5.5 Assert the negative: no soak/chaos-matrix/benchmark job is triggered by `pull_request` or
      `merge_group`, and none appears in `main`'s required status checks
- [ ] 5.6 PR-path functional wall-clock is within the §1.3 budget — the ADR-0038 obligation, checked
      with a number rather than asserted
- [ ] 5.7 One green `workflow_dispatch` soak run end to end, with the trail artifact downloadable
- [ ] 5.8 Deliberately fail a soak (inject a synthetic leak or lower a threshold on a dispatch run)
      and confirm the Issue is created with the trail attached — an alerting path that has never
      fired once is not known to work

## 6. Release

- [ ] 6.1 **No release.** This change touches workflows, tests, benchmarks, scripts and docs only —
      no `src/` behaviour change and no public-API delta — so no `Directory.Build.props`
      `PackageVersion` bump, no CHANGELOG release entry, and no downstream pin cascade
      (Sdk/ADR-0040) is triggered. Record a CHANGELOG `[Unreleased]` note under a CI/testing heading
      instead. If 3.5 adds `InternalsVisibleTo`, re-confirm `PublicAPI.Unshipped.txt` is still empty
      before closing this task

## 7. Phase closure

- [ ] 7.1 **Four consecutive green weekly soaks** with no Issue auto-created by a leak (ADR-0043 D9,
      carried unchanged from the original Fase 3 criterion — four weeks is the minimum that
      distinguishes a stable signal from a lucky one at weekly cadence). Note this is a floor, not a
      guarantee of four calendar weeks: GitHub's `schedule:` delivery on public repos is best-effort,
      and a dropped trigger is recovered with `workflow_dispatch`
- [ ] 7.2 Close out `docs/plans/active/2026-05-23-test-audit-and-expansion.md`'s Fase 3 line and
      remove its `doctor:stale-exempt` marker if no other parked phase remains
- [ ] 7.3 Archive the change (`openspec archive`), promoting the `longevity-validation` delta into
      `openspec/specs/`

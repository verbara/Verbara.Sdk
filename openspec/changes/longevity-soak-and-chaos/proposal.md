---
tier: GRANDE
owner: Harol
approver: Harol
stakeholder: Operators running the SDK as a 24/7 process — and the downstream consumers (Sdk.Pro, Platform) that inherit any leak from this repo, the root of the dependency chain
decision_ref: Sdk/ADR-0043
---

# Proposal: longevity-soak-and-chaos

## Why

This repo ships a telephony SDK whose consumers hold an AMI connection open for weeks, mutate a
channel/session index millions of times, and run an event pump that never drains to idle. The
failures that matter in that regime — a file-descriptor leak on reconnect, heap creep of a few
hundred KB per hour, an unobserved-task exception that only appears on the tenth reconnect — are
**invisible to every gate this repo runs today**. Unit tests finish in milliseconds; the functional
suite tears its stack down after minutes. Nothing observes the SDK over hours. The 2026-05
test-audit recorded this as finding 5, *"Longevidad ZERO"*, and scoped a Fase 3 that was never
started. This change is that Fase 3, re-scoped as an OpenSpec change now that `ci-pipeline-slimming`
(Sdk/ADR-0038) has landed and settled the CI-cost premises it depends on.

Three specific gaps, each verified against the tree rather than assumed:

1. **No long-running observation exists at all.** There is no soak workflow, no resource-sampling
   trail, and no long-lived host process to observe. The two files named `*SoakTests.cs` under
   `Tests/Verbara.Sdk.FunctionalTests/Layer2_UnitProtocol/Soak/` are in-process, seconds-long loops
   — useful, but they do not measure descriptor or heap drift over hours.
2. **Chaos coverage is real but single-axis.** Toxiproxy is already in the functional stack and
   already exercised: 10 toxic injections and 11 container kill/restart injections across 6 files.
   **Every one of them targets the AMI TCP connection.** ARI, AGI, AudioSocket, Sessions/Postgres,
   Sessions/Redis and NATS have zero fault-injection coverage. Only one proxy exists (`ami-proxy`);
   every toxic is injected `downstream`; `toxicity` is hard-coded to `1.0`; the `slow_close` toxic
   and `DockerControl.PauseContainerAsync`/`UnpauseContainerAsync` are built but never called.
3. **Benchmark coverage does not match what the repo publicly quantifies.** 13 benchmark classes /
   44 `[Benchmark]` methods exist, and `perf-regression.yml` already runs five of them weekly — but
   two of its five steps are mislabelled or measure a stand-in rather than the real path, and the
   in-process session-correlation hot path is not measured at all (details under *What Changes*).

The counterweight is explicit and non-negotiable. **Sdk/ADR-0038** deliberately removed work from
the PR path (single-collection coverage; representative single-version functional matrix on
`pull_request`, full matrix reserved for `merge_group`) because Sdk CI was the ecosystem's slowest
and most-failing gate. **Sdk/ADR-0039** then cut bot-authored PR load for the same reason and
deferred self-hosted runners with a named trigger. A 24-hour soak is roughly **1,440 runner-minutes
per execution** — about sixteen times the ≈90 compute-min ADR-0039 measured for an *entire merged
Dependabot PR*. Put on the PR path, this program would reverse both ADRs outright; worse, a required
check that cannot report inside the merge queue's 60-minute `check_response_timeout` would hang every
landing — the exact never-reporting-context failure ADR-0038's addendum documented empirically and
ADR-0039's addendum had to repair a second time. Sdk/ADR-0043 records the resulting policy: this
evidence is produced on a **scheduled train, read by a human, and gates nothing**.

## What Changes

Three workstreams, deliberately ordered cheap-and-certain first, expensive-and-uncertain last. Each
ships independently.

**A. Extended chaos suite** (cheapest — incremental work on a fixture that already runs). Five fault
shapes, each verified absent today. Two of the five candidates from the original Fase 3 list are
**reshaped rather than taken as written**, because the audit found adjacent coverage:

- *TCP RST midstream during an AMI command response* — **narrowed**. `reset_peer` is already
  injected by `ConnectionCutTests.TcpReset_ShouldTriggerReconnect`, but only on an **idle**
  connection: the test has to send a `PingAction` afterwards to force I/O so the reset is even
  noticed. RST arriving while a multi-line response or event burst is in flight — the case that
  exercises partial-frame handling in the pipeline reader — is genuinely uncovered.
- *Half-open socket* — **kept, and sharpened**. The existing `timeout` toxic (`timeout: 0`) is a
  **full blackhole**, not a half-open socket: both directions die together, and
  `SilentDrop_ShouldDetectViaHeartbeat` already covers that against the Sdk/ADR-0021 heartbeat
  contract. A true half-open — one direction still alive, the peer silently gone — has never been
  injected. Two unused primitives already in the tree reach it: the `slow_close` toxic and
  `DockerControl.PauseContainerAsync` (a frozen peer). This also requires the first-ever `upstream`
  toxic in this repo.
- *`core reload` while ~50 channels are active* — **kept as written**. No test issues `core reload`;
  the only reloads are `pjsip reload` in the realtime suite, with zero active channels.
- *Postgres failover mid-Sessions-transaction* — **kept, with its real prerequisite exposed**.
  Postgres is in the functional stack, but **only as Asterisk's realtime config backend**: it is not
  proxied, has no `DockerControl` handle, and the Sessions Postgres store tests run against a
  different fixture entirely. The scenario therefore needs a kill/restart handle wired into the
  Sessions Postgres fixture before the fault can be injected at all.
- *NATS broker disconnect mid-publish* — **kept as written**. NATS lives in its own isolated
  integration project; its six tests are all happy-path and the container is never stopped.

Nothing on the original list turned out to be a straight duplicate, so nothing is dropped — but the
first two are scoped against what already exists rather than re-testing it.

Two hardening items fall out of the same audit and ride along, because they decide whether the new
scenarios are worth anything:

- Four existing chaos tests end in `Should().BeOneOf(...)` over **every** value of the connection-state
  enum, which can never fail. `tools/audit-test-asserts.sh` checks that an assertion is *present*,
  not that it can *fail*. New scenarios must assert a specific recovered state.
- `ToxiproxyFactAttribute` exists but is applied to zero tests, because its availability probe
  hard-codes `localhost:8474` and ignores `TOXIPROXY_API_URL` — so it always reports unavailable
  under Testcontainers' random port mapping. Every toxic test uses a plain `[Fact]` instead.

**B. Extended perf hot paths** (cheap, and partly a correctness fix). The audit changes this list
substantially:

- *ARI Channel deserialize* — **already benchmarked** (`AriJsonBenchmark.DeserializeChannel`,
  `Deserialize100Channels`, wired as the `*AriJson*` workflow step). **Dropped as new work**;
  reshaped instead: the fixture sets only `Id`, `Name` and `State`, leaving `Caller`, `Connected`,
  `Dialplan` and `ChannelVars` null, so the measured payload is far smaller than a real Asterisk
  channel. Make the fixture representative.
- *`ChannelManager.GetById`* — **that method does not exist.** `ChannelManager` exposes
  `GetByUniqueId` and `GetByName`, and **both are already benchmarked**. So this is not a missing
  benchmark; it is a naming defect that has propagated into the `perf-regression.yml` step name and
  into the public README's performance table. **Dropped as new work**, replaced by a naming
  correction (see the boundary note below for who owns the README half).
- *Observer dispatch* — **benchmarked, but not the real path.** `ObserverDispatchBenchmark`
  re-implements the fan-out loop locally over a no-op observer; the production path additionally
  takes a timestamp, wraps each observer in `try/catch`, records two metrics and invokes an event.
  **Reshaped**: measure through the public `Subscribe` seam so the numbers describe shipped code.
- *Session correlate-by-LinkedId* — **genuinely missing, and kept.** What exists is store-level only
  (`RedisGetByLinkedId` / `PostgresGetByLinkedId`, Testcontainers-backed and already flagged flaky).
  The in-process correlation hot path — the `_byLinkedId` index and the join-or-create branch in
  `CallSessionManager` — is not measured. The in-memory store's `GetByLinkedIdAsync` is additionally
  an O(n) scan over all values, which is worth having a number for.

**C. Weekly soak** (most expensive, most uncertain — sequenced last on purpose). A scheduled-only
workflow that runs the stack under sustained call load, samples heap and open file descriptors
periodically, and publishes the trail as an artifact; on failure it opens an Issue carrying that
trail. Two prerequisites the original plan assumed away and the audit found missing:
`docker/functional/sipp-scenarios/` contains only a `.gitkeep` — **there are zero SIPp scenarios** —
and both SIPp wrappers hard-code a bounded call count (`-m 1`, and `-m {calls} -l {calls} -r 1`), so
sustained calls-per-minute is not a configuration change but a driver that must be built. Per
Sdk/ADR-0043 D8 the job starts **observe-only** (collect and publish, never fail) until enough runs
exist to calibrate thresholds from data rather than from guesswork.

**Boundary with `enforce-unguarded-public-claims` (in flight, separate).** That change owns the
**perf-claim gate** and `Tests/Verbara.Sdk.Benchmarks/baseline.json` — which, confirmed by this
audit, **does not exist yet**: `perf-regression.yml` runs every filter with `|| true`, nothing reads
the emitted JSON, and `gates.yaml` records G6 as `status: na`. This change owns only the
**additional hot paths and their correct labelling**; it MUST NOT create `baseline.json`, add a
comparison step, or change the workflow's failure semantics. The two README defects this audit
surfaced — the `ChannelManager.GetById` row naming a method that does not exist, and the cited
BenchmarkDotNet version (v0.14.0) trailing the pinned one (0.15.8) — are **public claims**, so they
are handed to that change rather than fixed here. Both changes touch `perf-regression.yml`; sequence
them, do not land them concurrently.

## Capabilities

### New Capabilities

- `longevity-validation`: the durable capability that this repo produces evidence of surviving
  long-running, production-shaped operation — a periodic resource-stability trail, a fault-recovery
  matrix, and a hot-path timing series — on a scheduled train that gates no pull request, with a
  failed run producing a triage Issue rather than a merge block. Recorded by Sdk/ADR-0043.

### Modified Capabilities

None — deliberately. `ci-gating` is **not** modified: Sdk/ADR-0043 D1 keeps every longevity job off
`pull_request` and `merge_group`, so which validation runs on which event, the required-check set,
and the single-collection coverage rule are all untouched. The extended chaos scenarios join the
existing functional suite as ordinary functional tests and inherit ADR-0038's representative-PR /
full-queue matrix policy unchanged; the one way they could disturb `ci-gating` — growing the
PR-path functional job's wall-clock — is bounded by an explicit budget requirement in the
`longevity-validation` spec rather than by relaxing anything in `ci-gating`.

## Impact

- `.github/workflows/soak.yml` — **new**, scheduled + `workflow_dispatch` only, never on
  `pull_request`/`merge_group`, with an explicit `timeout-minutes`.
- `.github/workflows/perf-regression.yml` — step names corrected; steps added for the new hot paths.
  Failure semantics unchanged (see the boundary note above).
- `scripts/` + `scripts/tests/` — the soak snapshot analyzer and its unit tests, following the
  established precedent that every gate script in this repo ships with tests
  (`check-coverage-floor.py`, `classify-docs-only.sh`).
- `docker/functional/sipp-scenarios/` — the first actual SIPp scenario file(s).
- `Tests/Verbara.Sdk.TestInfrastructure/` — a rate-capable SIPp driver; container handles for the
  containers chaos needs to kill (Sessions Postgres, NATS).
- `Tests/Verbara.Sdk.FunctionalTests/` — new fault-injection scenarios; `ToxiproxyFactAttribute`
  availability probe fixed; an `upstream` toxic used for the first time.
- `Tests/Verbara.Sdk.Push.Nats.IntegrationTests/`, `Tests/Verbara.Sdk.Sessions.Postgres.Tests/` —
  broker/database disconnect scenarios.
- `Tests/Verbara.Sdk.Benchmarks/` — reshaped ARI fixture, real-path observer dispatch, new
  session-correlation benchmarks. May require `InternalsVisibleTo Verbara.Sdk.Benchmarks` on
  `Verbara.Sdk.Sessions` (it exists today only on `Verbara.Sdk.Ami` and `Verbara.Sdk.Ari`).
- `docs/decisions/0043-longevity-evidence-off-the-pr-path.md` — new ADR.
- **No `src/` behaviour change and no release.** Nothing here alters shipped behaviour or public API,
  so no `Directory.Build.props` `PackageVersion` bump and no downstream pin cascade
  (Sdk/ADR-0040) are triggered.

## Architectural Risk

**Level:** MEDIUM.

**Affected:** (1) the `Functional Tests (Testcontainers)` job's wall-clock, which sits on the PR path
for the representative Asterisk variant — the one place this change can silently undo Sdk/ADR-0038;
(2) the merge queue, if any longevity job were ever promoted to a required check, since a job that
can run for hours cannot report inside the 60-minute `check_response_timeout` and would strand every
landing; (3) `.github/workflows/perf-regression.yml`, shared with the in-flight
`enforce-unguarded-public-claims` change; (4) GitHub's best-effort `schedule:` delivery on public
repos, which makes "four consecutive weekly soaks" a floor rather than a guarantee; (5) new chaos
fixtures that kill databases and brokers, a well-known source of flake in a suite whose reliability
ADR-0038 was written to protect.

**Mitigation:** Sdk/ADR-0043 D1 makes the PR-path cost of the soak exactly zero by construction —
scheduled trigger only, no required check, ever — and the `longevity-validation` spec adds an
explicit wall-clock budget for the chaos additions, with any scenario exceeding it relocated to the
scheduled train rather than absorbed into the PR path. Flake risk is contained by requiring each new
scenario to assert a *specific* recovered state (never an all-values `BeOneOf`, the defect found in
four existing tests) and to prove itself under a repeat-run protocol before landing, mirroring the
30× protocol `ci-pipeline-slimming` used. Threshold-driven Issue noise is contained by D8's
observe-only warm-up: the soak publishes its trail but cannot fail until thresholds are calibrated
from real runs. The shared-workflow collision is contained by D10's ownership split — this change
adds hot paths and fixes labels, `enforce-unguarded-public-claims` owns `baseline.json` and the gate
— and by sequencing the two changes rather than landing them concurrently. Workstreams ship
independently and in cost order, so the cheap, certain chaos and benchmark value lands even if the
soak driver proves harder than scoped.

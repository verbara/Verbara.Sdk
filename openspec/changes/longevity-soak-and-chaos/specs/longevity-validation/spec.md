# longevity-validation — Delta

## ADDED Requirements

### Requirement: Longevity validation never gates a pull request

Longevity jobs — soak, extended chaos matrices, and benchmark runs — SHALL NOT be added to `main`'s
required status checks, and SHALL NOT be triggered by `pull_request` or `merge_group`. They SHALL be
triggered by `schedule:` and `workflow_dispatch` only. Every scheduled longevity job MUST declare an
explicit `timeout-minutes`. This is a hard constraint, not a preference: a check that can run for
hours cannot report inside the merge queue's 60-minute `check_response_timeout`, so promoting one to
a required context would strand every landing — the never-reporting-context failure recorded by
ADR-0038's addendum and ADR-0039's addendum. The PR-path runner-minute budget established by
ADR-0038 and ADR-0039 MUST be unchanged by this program.

#### Scenario: A soak run is invisible to the merge path

- **GIVEN** a pull request targeting `main`
- **WHEN** CI runs on the `pull_request` event and again on `merge_group`
- **THEN** no soak, chaos-matrix or benchmark job is triggered by either event, and the set of required status checks is identical to what it was before this capability existed

#### Scenario: An operator reproduces a soak on demand

- **GIVEN** a maintainer who does not want to wait for the next scheduled run
- **WHEN** they dispatch the soak workflow manually
- **THEN** it runs the same observation it would run on schedule, and still gates nothing

### Requirement: The repo publishes a periodic resource-stability trail

A scheduled soak SHALL exercise the SDK against a live stack under sustained call load and SHALL
sample, at a fixed interval, at least managed heap size and the process's open-file-descriptor
count. The samples SHALL be published as a retained, downloadable artifact — the trail is the
deliverable, and it MUST be produced whether the run passes or fails. The acceptance shape SHALL be:
heap delta bounded after a declared warm-up period, descriptor-count delta bounded, and zero
unhandled exceptions. The observation window and the numeric bounds are tunable operational
parameters; the acceptance *shape* is the contract (ADR-0043 D7).

#### Scenario: A clean run still leaves evidence

- **GIVEN** a scheduled soak that completes with every threshold satisfied
- **WHEN** the run finishes
- **THEN** the sampled heap and descriptor trail is uploaded as a retained artifact, so the "production-grade 24/7" posture has a dated, inspectable record behind it

#### Scenario: A descriptor leak is caught by the trail

- **GIVEN** a regression that leaks one file descriptor per AMI reconnect
- **WHEN** the soak runs long enough to reconnect many times
- **THEN** the descriptor samples show monotonic growth beyond the declared bound and the run is reported as failed

#### Scenario: Sustained load is driven at a configured rate

- **GIVEN** the soak's call driver
- **WHEN** it is asked for a steady-state call rate over the observation window
- **THEN** it sustains that rate for the duration rather than placing a fixed, bounded number of calls and exiting

### Requirement: Soak thresholds begin observe-only and are committed data

A newly introduced soak SHALL run in observe-only mode — collecting and publishing its trail, never
failing the job — until enough completed runs exist to calibrate its thresholds from measured data.
Promotion to fail-on-threshold SHALL be a deliberate, separate step. Once active, thresholds MUST
live in a committed data file rather than as inline literals in the workflow, and the analyzer that
evaluates them MUST ship with its own unit tests, matching the established precedent that every gate
script in this repo is itself tested.

#### Scenario: A brand-new soak cannot cry wolf

- **GIVEN** a soak workflow that has just been introduced and has no calibration history
- **WHEN** it runs and observes heap growth of an unknown-but-plausible magnitude
- **THEN** it publishes the trail and reports success, because uncalibrated thresholds would produce Issues that train the reader to ignore Issues

#### Scenario: The analyzer is tested like a gate

- **GIVEN** the script that reads a snapshot trail and decides pass or fail
- **WHEN** the unit-test suite runs
- **THEN** the analyzer's own tests execute and cover at least the stable, leaking, and malformed-input cases

### Requirement: A failed longevity run opens a triage Issue and blocks nothing

When a scheduled longevity run fails, the workflow SHALL create a GitHub Issue containing the
snapshot trail (or a link to the artifact) and a link to the run. That Issue is the only enforcement
mechanism: there SHALL be no automated revert, no bisect, no release block, and no branch-protection
consequence. The repo maintainer is the reader and triages it as ordinary repo work. Issue creation
MUST be idempotent enough that repeated failures of the same kind do not accumulate duplicates
faster than they can be triaged.

#### Scenario: A red soak reaches a human with evidence attached

- **GIVEN** a soak run whose descriptor-count delta exceeds its calibrated bound
- **WHEN** the run fails
- **THEN** an Issue is opened carrying the snapshot trail and the run link, and no merge, release, or required check is affected in any way

#### Scenario: A release is not held hostage by longevity infrastructure

- **GIVEN** an open Issue from a failed soak
- **WHEN** a release is cut from `main`
- **THEN** the release proceeds — the Issue is triage input, never a gate

### Requirement: Fault-injection scenarios assert a specific recovered state

Every fault-injection scenario added under this capability SHALL assert a *specific* expected
outcome — a named connection or health state, a delivered event, a completed action — and SHALL NOT
satisfy itself with an assertion that admits every possible value (for example a `BeOneOf` over the
full connection-state enum, which cannot fail). Presence of an assertion is insufficient: the
existing `tools/audit-test-asserts.sh` guard checks that an assertion exists, not that it can fail.
Each new scenario MUST additionally demonstrate stability under a repeat-run protocol before it
lands, so chaos coverage does not become a new flake source in a suite ADR-0038 was written to make
more reliable.

#### Scenario: An all-values assertion is rejected in review

- **GIVEN** a proposed chaos test whose final assertion accepts every member of the state enum
- **WHEN** the scenario is reviewed against this capability
- **THEN** it is rejected as unfalsifiable and reworked to assert the specific state the SDK is contracted to reach

#### Scenario: A new chaos scenario proves itself before landing

- **GIVEN** a newly written fault-injection scenario
- **WHEN** it is executed repeatedly under load prior to merge
- **THEN** it passes every repetition, and a scenario that cannot is fixed or withdrawn rather than merged with a retry

### Requirement: The fault-recovery matrix extends beyond the AMI TCP connection

The fault-recovery matrix SHALL cover fault shapes that today have no coverage, and MUST NOT
re-test shapes already covered. Specifically it SHALL add: a transport reset delivered **while a
response or event stream is in flight** (distinct from the existing reset on an idle connection,
which requires subsequent traffic before it is even observed); a genuine **half-open** socket where
one direction remains alive (distinct from the existing full-blackhole toxic, which the heartbeat
contract of ADR-0021 already covers); an Asterisk **`core reload` with a substantial number of
active channels** (distinct from the existing module-scoped reload performed with no active
channels); a **database restart during an in-flight Sessions transaction**; and a **message-broker
disconnect during publish**. Because the database and broker are not reachable for fault injection
from the fixtures that exercise them today, the matrix SHALL first establish the missing control
handles rather than assert recovery it cannot actually provoke.

#### Scenario: A reset mid-response exercises partial-frame handling

- **GIVEN** an AMI connection that has issued a command producing a multi-line response
- **WHEN** the transport is reset before the response is fully delivered
- **THEN** the client surfaces a deterministic failure for the pending action, reconnects, and serves a subsequent action successfully — with no partial frame leaking into the next message

#### Scenario: A half-open socket is distinguishable from a blackhole

- **GIVEN** a connection where the peer is silently gone in one direction while the other remains open
- **WHEN** the connection is exercised
- **THEN** the client detects the condition and recovers, and the scenario is asserted independently of the existing full-blackhole partition test

#### Scenario: A reload under load does not lose the client

- **GIVEN** a live stack with a substantial number of active channels
- **WHEN** the PBX performs a full `core reload`
- **THEN** the client remains usable or recovers to a serving state, and the tracked channel state is consistent with what the PBX reports afterwards

#### Scenario: A database restart mid-transaction leaves no orphaned state

- **GIVEN** a Sessions store write in flight against its database
- **WHEN** the database is restarted underneath it
- **THEN** the operation fails deterministically or recovers, and once the database is back the store serves reads and writes again with no leaked connection or half-written session

#### Scenario: A broker disconnect mid-publish is survivable

- **GIVEN** a publish in flight to the message broker
- **WHEN** the broker becomes unavailable and later returns
- **THEN** the publisher surfaces the failure deterministically and resumes publishing once the broker is back, without leaking a connection or wedging the bus

### Requirement: Extended chaos MUST NOT grow the PR-path validation budget

The chaos scenarios added under this capability run inside the existing functional suite, part of
which executes on `pull_request`. Their combined added wall-clock to the PR-path functional job MUST
be measured and MUST stay within a declared budget. A scenario that exceeds the budget SHALL be
relocated to the scheduled longevity train rather than absorbed into the PR path, and the budget
SHALL NOT be discharged by relaxing any requirement of the `ci-gating` capability. ADR-0038's cost
regime is a constraint on this change, not a variable it may trade against.

#### Scenario: An expensive scenario is relocated, not absorbed

- **GIVEN** a proposed fault-injection scenario that adds several minutes to the functional job
- **WHEN** its cost is measured against the declared budget and exceeds it
- **THEN** it moves to the scheduled longevity train, and the PR-path functional job's wall-clock stays within budget

#### Scenario: The required-check set survives the chaos additions

- **GIVEN** the extended chaos suite has landed
- **WHEN** the required status checks on `main` are inspected
- **THEN** they are unchanged in name and number, and the representative-PR / full-queue matrix policy is exactly as `ci-gating` specifies

### Requirement: Benchmark coverage matches the hot paths the repo publicly quantifies

Every operation this repo publicly quantifies SHALL be backed by a benchmark that measures the
**shipped code path** under a **representative input**, and the benchmark's label in the workflow
MUST name a member that actually exists. A benchmark that re-implements a production loop locally,
or that measures a fixture materially smaller than the real payload, does not satisfy this
requirement. Coverage SHALL additionally extend to the in-process session-correlation path, which is
measured today only at the storage-backend level. This requirement owns benchmark **coverage and
labelling only**: the regression gate, the committed performance baseline, and the workflow's
failure semantics are owned by the separate `enforce-unguarded-public-claims` change and MUST NOT be
introduced here (ADR-0043 D10).

#### Scenario: A benchmark label names a real member

- **GIVEN** a scheduled benchmark step labelled after an SDK lookup method
- **WHEN** the named member is resolved against the source
- **THEN** it exists, and the benchmark behind the label measures that member

#### Scenario: Event fan-out is measured through the shipped path

- **GIVEN** the observer fan-out benchmark
- **WHEN** it executes
- **THEN** it drives the real subscription seam — including the per-observer error containment and metric recording the production path performs — rather than a local re-implementation over a no-op observer

#### Scenario: The deserialization fixture resembles a real payload

- **GIVEN** the ARI channel deserialization benchmark
- **WHEN** its input fixture is inspected
- **THEN** the populated fields resemble what a live PBX emits, rather than a minimal subset that understates the work

#### Scenario: In-process session correlation has a number

- **GIVEN** the session-correlation hot path that resolves and joins a session by its linked identifier in memory
- **WHEN** the benchmark suite runs
- **THEN** both the index lookup and the join-or-create branch are measured, independently of any storage backend

#### Scenario: This change adds no perf gate

- **GIVEN** the benchmark additions have landed
- **WHEN** the scheduled benchmark workflow runs and a hot path is slower than before
- **THEN** the workflow still reports success and publishes its results, because the comparison gate and its baseline belong to a separate change

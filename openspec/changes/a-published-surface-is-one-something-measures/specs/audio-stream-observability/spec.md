# audio-stream-observability Delta

## ADDED Requirements

### Requirement: A published instrument SHALL be emitted by the code it names
Every instrument this SDK publishes on a `Meter` SHALL have at least one production call site that
records it, in the component its name describes, and an instrument with no production call site MUST
NOT remain published. Removing it and wiring it are both acceptable answers; leaving it is not,
because a published instrument is read as a statement that the component reports that quantity.

Where the published documentation tells a reader to watch the meter with a named tool, that
instruction SHALL be true of a running process. A reader who follows it and sees nothing move has
been told something false by a tracked file.

The choice between wiring and removal SHALL be recorded with the cost of the removal branch stated —
the rows it drops from `PublicAPI.Shipped.txt`, the ApiCompat codes it raises and the migration note
it obliges — so that the cheaper branch is chosen knowingly rather than by default.

#### Scenario: An instrument nobody records
- **GIVEN** an instrument declared on a published meter
- **WHEN** no source file outside its own declaration and its tests records a measurement to it
- **THEN** it is either recorded by the component it names, or removed from the published surface

#### Scenario: The documented way to observe the meter
- **GIVEN** documentation instructing a reader to monitor the meter by name
- **WHEN** a process runs the component the instruments describe
- **THEN** the instruments the documentation names move

### Requirement: A test for an instrument SHALL fail when the production emission is removed
A test cited as evidence that a component is instrumented SHALL drive the component and assert the
measurement the component emitted, and MUST NOT supply the measurement itself. A test that records to
an instrument and then asserts a listener observed that record measures the metrics library, not this
SDK, and SHALL NOT be counted as coverage of the instrument.

An assertion that an instrument field is non-null SHALL NOT be counted as coverage of anything: a
`static readonly` field initialised at its declaration cannot be null, so the assertion holds in every
possible state of the program, including the state where nothing is instrumented at all.

#### Scenario: The witness fails when the call site goes
- **GIVEN** a test cited as evidence that a component records an instrument
- **WHEN** the production call site is removed
- **THEN** that test fails

#### Scenario: A self-supplied measurement is not a witness
- **GIVEN** a test that calls `Add` or `Record` on the instrument itself and asserts a listener saw it
- **WHEN** the instrument has no production call site
- **THEN** the test still passes, and is therefore not evidence for the instrument

## Architectural Risk

**Level:** LOW on the wiring branch, MEDIUM on the removal branch.

**Affected:** `Verbara.Sdk.Ari` — `Diagnostics/AudioStreamMetrics.cs` and the sessions that would
emit, `Audio/AudioSocketSession.cs` and `Audio/WebSocketAudioSession.cs` — plus
`Tests/Verbara.Sdk.Ari.Tests/Diagnostics/AudioStreamMetricsTests.cs`, which is rewritten on either
branch. Nothing downstream consumes these instruments today, for the reason this requirement exists:
they have never emitted.

Wiring is additive and breaks nothing. Removal drops twelve rows from
`src/Verbara.Sdk.Ari/PublicAPI.Shipped.txt` and raises `CP0002`, which cascades to `Verbara.Sdk.Pro`
and `Verbara.Platform` as a recompile even though no consumer can be using a counter that never
moved. The asymmetry is the reason the decision is a task rather than an assumption.

**Mitigation:** the rewritten tests are written against the branch chosen, before it lands, and the
negative control is the removal of a production call site rather than the breaking of an expected
value — the distinction the sibling change measured to be the difference between a test that is wired
and a test that can see the defect. If removal is chosen, the break is declared in a committed
suppressions entry with each line read before it is kept, and carries a migration note.

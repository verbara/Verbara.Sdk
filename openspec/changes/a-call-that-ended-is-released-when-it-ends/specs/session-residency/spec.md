# Spec Delta

## Purpose

What the SDK guarantees about how long it holds a call after that call has ended: that the hold is
bounded by both time and count, that the release cannot be switched off by the state of its own
bookkeeping, that it does not wait for another call to end, and that it never touches a call that is
still live.

## ADDED Requirements

### Requirement: A call that has ended SHALL be released under a bound of both time and count

State held for a call that has reached a terminal state SHALL be released once it is older than the
configured retention, and SHALL also be released, oldest first, once the number of retained ended
calls exceeds the configured maximum. Time is the floor: an ended call SHALL NOT be released before
the retention period has passed, whatever the count says.

Both bounds are already part of the configuration surface. A maximum that is declared and never
applied is not a bound.

**BREAKING**: a consumer that set the maximum high and relied on it having no effect will now see
ended calls released at that number.

#### Scenario: The retention period bounds the hold

- **GIVEN** a retention period and a number of calls that have ended
- **WHEN** more than that period has passed since a call ended
- **THEN** that call is no longer retained

#### Scenario: The maximum bounds the hold

- **GIVEN** a maximum number of retained ended calls
- **WHEN** more ended calls are retained than that maximum, all of them older than the retention period
- **THEN** the oldest are released until the count is within the maximum

#### Scenario: Time wins over count

- **GIVEN** more ended calls retained than the maximum allows
- **WHEN** none of them is older than the retention period
- **THEN** none is released

### Requirement: The release MUST NOT be disabled by the state of its own bookkeeping

Release SHALL make progress regardless of the condition of the records it walks. An entry that
cannot be evaluated — because the call it names is no longer retained, or because it carries no
completion time — SHALL be discarded rather than left in place, and SHALL NOT prevent any other
entry from being released.

This is stated as a requirement rather than left to implementation care because its failure mode is
silent and permanent: nothing reports it, and every call retained afterwards is retained for the life
of the process.

#### Scenario: An entry naming a call that is no longer retained

- **GIVEN** a release queue whose oldest entry names a call that is no longer held
- **WHEN** release runs
- **THEN** that entry is discarded
- **AND** every other entry old enough to be released is released

#### Scenario: An entry with no completion time

- **GIVEN** a release queue whose oldest entry carries no completion time
- **WHEN** release runs
- **THEN** that entry is discarded
- **AND** release continues past it

#### Scenario: A call is queued for release once

- **GIVEN** a call that has already reached a terminal state
- **WHEN** a further ending is reported for it
- **THEN** it is not queued for release a second time

### Requirement: Release SHALL NOT wait for another call to end

Release SHALL be evaluated when calls arrive as well as when they end. A process that stops
completing calls while still accepting them SHALL still release what it already holds.

#### Scenario: Arrivals without completions

- **GIVEN** retained ended calls older than the retention period
- **WHEN** new calls arrive and none of them ends
- **THEN** the ended calls are released

#### Scenario: No traffic at all

- **GIVEN** retained ended calls older than the retention period
- **WHEN** no call arrives and none ends
- **THEN** nothing is required to happen, and nothing grows

### Requirement: A call that is still live SHALL NOT be released by this bound

Release SHALL consider only calls in a terminal state. A call that has not ended — whatever its age,
and whatever has or has not been observed about it — SHALL NOT be released, counted towards the
maximum, or altered by this bound.

Calls that appear stuck are a separate problem with a separate cause; ageing them out by a clock has
been measured to mark healthy calls dead.

#### Scenario: An old call that is still connected

- **GIVEN** a connected call older than the retention period
- **WHEN** release runs
- **THEN** that call is untouched and still reported as active

#### Scenario: A live call does not consume the maximum

- **GIVEN** more live calls than the configured maximum
- **WHEN** release runs
- **THEN** no live call is released, and no ended call is released early because of them

### Requirement: The default store MUST NOT retain a call the manager has released

The store the SDK resolves when a consumer registers none SHALL release a call when the manager
does. A store that keeps a reference to the same call the manager released frees nothing, so the
bound would be reported as working while the memory stays held.

A store that provides its own durability and its own retention SHALL keep it. This requirement is
about the default in-memory store, not about durable ones.

#### Scenario: The in-memory default follows the manager

- **GIVEN** the SDK resolving its default store
- **WHEN** the manager releases an ended call
- **THEN** the store no longer retains it

#### Scenario: A durable store keeps its own retention

- **GIVEN** a consumer that registered a durable store with its own retention
- **WHEN** the manager releases an ended call
- **THEN** the store's own retention decides what it keeps

### Requirement: What the process retains SHALL be observable

The number of calls retained SHALL be published as a measurement an operator can read, separating
live calls from ended ones still held. A bound that cannot be observed is indistinguishable from a
bound that has stopped working — which is the failure this capability exists to make impossible.

#### Scenario: The counts are published

- **WHEN** an operator reads the SDK's published measurements
- **THEN** the number of live calls and the number of retained ended calls are both available

#### Scenario: The counts move with the bound

- **GIVEN** ended calls being released
- **WHEN** the operator reads the measurements again
- **THEN** the retained count reflects the release

## Architectural Risk

**Level:** MEDIUM.

**Affected:** `Verbara.Sdk.Sessions` and `Verbara.Sdk.Live`, and through them every consumer that
holds call state — including the closed-source cluster layer and the product above it. No public API
is removed; one published option starts taking effect.

**Mitigation:** the dangerous direction here is releasing something a consumer still needs, not
holding too long, so the requirements are written to fail towards holding: time is a floor the count
cannot undercut, and only terminal calls are eligible at all. A separate change already established,
with a measurement, that ageing non-terminal calls by a clock marks healthy calls dead — that path is
excluded here by requirement rather than by convention. The residual risk is a consumer that set the
maximum high while relying on it being ignored; it is a published option with a documented meaning,
so the change is to honour the contract rather than to alter it, and it is called out as breaking.
The store requirement exists because the opposite mistake — a bound that appears to work while the
default store pins everything it released — is the one this area has already made once.

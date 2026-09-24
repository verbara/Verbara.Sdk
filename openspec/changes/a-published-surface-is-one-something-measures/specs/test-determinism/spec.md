# test-determinism Delta

## ADDED Requirements

### Requirement: A functional test SHALL fail when the call it originates never happens
A functional test SHALL treat the absence of the channel, event or call it originated as a failure
with a reason, and MUST NOT return from the test body without asserting. An originate that does not
land is the condition the test exists to detect; a test that exits quietly on it reports success for
the one case it was written for.

This applies to every spelling of the early exit, not to one: a null check on the awaited event, an
emptiness check on a collected event list, a predicate over collected events, and a comparison of
`Task.WhenAny` against the task that was supposed to win are the same construction, and a sweep that
matches only the first finds a fraction of them. The sweep's result SHALL be a hand-verified count,
because the mechanical scan cannot tell a test-body exit from a filter inside an event-observer
lambda, and the lambda form is correct.

A comment describing the exit as skipping gracefully SHALL NOT stand in for a skip: a skipped test
is reported as skipped, and a test that returns is reported as passed.

#### Scenario: The originate does not land
- **GIVEN** a functional test that originates a call and waits for the event it asserts on
- **WHEN** the event does not arrive within the test's timeout
- **THEN** the test fails, naming the call it originated and the event it waited for

#### Scenario: Every spelling of the exit is found
- **GIVEN** a suite in which the unasserting exit is written several different ways
- **WHEN** the suite is swept for it
- **THEN** the sweep covers the shape rather than one phrasing, and its count is verified by reading each site

### Requirement: The functional dialplan SHALL define every extension the suite dials
Every extension the functional suite dials into a dialplan context SHALL be defined in that context,
or the test that dials it SHALL be changed to dial one that is. The reconciliation SHALL be performed
by something that executes — a guard, a test, or a check on the validation run — and MUST NOT be
recorded only as a comment in a configuration file, because a comment does not fail.

The set of dialled extensions SHALL be collected from every form the suite uses to reach the
dialplan, including both a channel string naming an extension and a context-plus-extension pair on an
originate or a redirect. A reconciliation derived from one form under-reports, and an under-report
here reads exactly like a clean result.

A context with no pattern-match extension SHALL NOT be assumed to absorb an undefined number: an
extension that is not listed in such a context is not reachable, and the originate fails.

#### Scenario: An extension dialled by the suite and absent from the context
- **GIVEN** a functional test dialling an extension into a context that does not define it
- **WHEN** the reconciliation runs
- **THEN** it fails, naming the extension and the test that dials it

#### Scenario: The second dialling form is counted
- **GIVEN** a suite that reaches the dialplan both by a channel string and by a context-plus-extension pair
- **WHEN** the dialled set is collected
- **THEN** both forms are included, and the reconciliation is over their union

#### Scenario: Defining the missing extension turns a vacuous pass into a real result
- **GIVEN** tests that have been passing by exiting early because their originate never landed
- **WHEN** the extension is defined and the early exit is replaced by an assertion
- **THEN** those tests run their bodies for the first time, and whatever they then report is the first real result they have ever produced

## Architectural Risk

**Level:** LOW on the shipped surface, MEDIUM on the validation run.

**Affected:** `docker/functional/asterisk-config/extensions.conf` and
`Tests/Verbara.Sdk.FunctionalTests/`. No `src/` file and no published signature changes, so nothing
cascades to `Verbara.Sdk.Pro` or `Verbara.Platform`.

The risk is not to consumers, it is to the validation run: tests that have been reporting success
without executing their bodies will execute them, and some will fail. That red is pre-existing and is
being surfaced, not caused — the sibling change measured exactly this when it defined one of the
missing extensions, and renumbered its own work to 710/711 specifically so that it would not carry
someone else's failure. This change is where that failure is carried, and the tasks budget for it
rather than meeting it by surprise.

**Mitigation:** the sweep produces a hand-verified list before any edit, so the size of the red is
known before it is provoked. The work runs behind the `ci:functional` label on the pull request, since
without it the functional job reports success in about sixteen seconds having started no Asterisk, and
the merge queue is the only place both Asterisk versions run. The reconciliation lands as an executed
guard, so the class cannot return as a comment.

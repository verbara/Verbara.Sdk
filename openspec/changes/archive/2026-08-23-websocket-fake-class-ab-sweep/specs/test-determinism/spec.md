# test-determinism — Delta

## ADDED Requirements

### Requirement: A protocol fence in a fake server is evidenced by a recorded failure, not by its shape
Every protocol sentinel and hold-open path in an in-process fake server SHALL be negative-tested
before it is claimed to hold: the fence is removed, a dependent test is observed to fail, and the
observed failure is recorded; the fence is then restored and the test observed to pass. A fence that
has only been read MUST NOT be counted as satisfying the sentinel or hold-open requirements, because
the defect those requirements exist for is precisely a fence whose shape is right and whose effect is
absent — the converted suite's hold-open flag was `await receiveTask` for months while every test
over it stayed green.

#### Scenario: A fence with no recorded failure is not evidence
- **GIVEN** a fake server whose session handler waits on a protocol sentinel
- **WHEN** no one has observed a dependent test fail with that wait removed
- **THEN** the sentinel is treated as unverified regardless of how it reads, because a wait that is never reached and a wait that always completes early are indistinguishable from the source

#### Scenario: The recorded failure names what actually broke
- **GIVEN** a hold-open flag being negative-tested
- **WHEN** the flag is cleared and the cancellation test runs
- **THEN** the recorded failure states the observed condition — the live server-side socket state at the moment of the cancel — rather than only that the test went red, so a later reader can tell a genuine fence failure from an unrelated break

### Requirement: Fake-server coverage is enumerated per surface, never claimed by class
A claim that a rule holds across this repo's in-process fake servers SHALL enumerate the surfaces it
covers and name those it does not, with each surface's actual state rather than a single state
attributed to the group. Where a rule is enforced by a guard, the claim MAY rest on the guard; where
it is enforced only by inspection or by negative-testing, the claim MUST be per surface. A closed
list of provider names under an open contract hides the surfaces nobody looked at.

#### Scenario: A rule enforced by a guard may be claimed for the whole set
- **GIVEN** a rule that a source-scanning Governance guard fails the build on
- **WHEN** the guard walks every test source and its liveness self-test passes
- **THEN** the rule may be claimed across all fake servers, because the guard's coverage is the enumeration

#### Scenario: A rule verified by hand is claimed only where it was verified
- **GIVEN** a rule that no guard can reach, such as a sentinel proven by negative-testing
- **WHEN** one surface has been swept and others have not
- **THEN** the record names the swept surface, names the unswept ones individually, and states each one's actual state — rather than reporting a uniform status the sweep did not establish

#### Scenario: Non-uniform state is reported as non-uniform
- **GIVEN** a set of fake servers where some carry a sentinel, some carry a hold-open flag, and some carry neither
- **WHEN** the set's status is written down
- **THEN** the differences are preserved, because flattening them turns "not checked" and "checked and absent" into the same sentence

## Architectural Risk

Low, and confined to test infrastructure. The rules, the shared substrate and both enforcement
idioms already exist and were exercised on one suite. The requirements added here are about
evidence and about how coverage is reported, so the way they fail is a sweep that changes eight
fakes without ever watching a fence break — which would leave the tree in the exact state the
converting change was written to end.

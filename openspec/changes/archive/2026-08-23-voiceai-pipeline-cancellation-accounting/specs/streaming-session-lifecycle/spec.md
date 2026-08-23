# streaming-session-lifecycle — Delta

## ADDED Requirements

### Requirement: A cancellation source shared between loops has exactly one owner
A cancellation source reachable from more than one concurrently running loop SHALL have a single
owner responsible for releasing it, and MUST NOT be released by two paths that are unordered with
respect to each other. A loop that only ever *cancels* MUST NOT be able to observe the source after
another path has disposed it, and a null-check followed by an `await` is not an ordering.

#### Scenario: A barge-in that lands as synthesis completes
- **GIVEN** a synthesis in progress and a caller who starts speaking over it
- **WHEN** the barge-in and the synthesis's own completion land together
- **THEN** the barge-in takes effect or is harmlessly late, and in neither case does the session fault or count as failed

#### Scenario: Disposing the session while a synthesis is running
- **GIVEN** a pipeline being disposed while synthesis is under way
- **WHEN** the disposal releases the session's resources
- **THEN** the synthesis path cannot observe a released cancellation source

### Requirement: All session handlers agree on what a cancelled session counts as
Every implementation of the session-handler interface SHALL classify a requested cancellation the
same way in telemetry. A cancelled session MUST NOT be a failure in one implementation and a
completion in another, because the caller cannot see which handler produced the number.

#### Scenario: The same shutdown, two handlers
- **GIVEN** two session handlers behind one interface
- **WHEN** each is cancelled by its caller
- **THEN** both record the ending under the same classification, and that classification is stated in a decision record rather than inferred from whichever file is opened first

## Architectural Risk

Medium. The fix is small and local; the reproduction is the hard part. A barge-in racing a synthesis
completion is a genuine race, and a test that reproduces it with a delay would reintroduce, inside
the change that fixes it, the timing dependency ADR-0045 and ADR-0053 exist about.

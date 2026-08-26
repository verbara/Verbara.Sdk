# streaming-session-lifecycle — Delta

## ADDED Requirements

### Requirement: A requested cancellation outranks an empty-input shortcut
A streaming producer that short-circuits on empty or whitespace input SHALL observe the caller's
cancellation **before** taking that shortcut. A caller who cancels MUST receive an
`OperationCanceledException`, not an empty sequence, whatever the input was — an empty sequence and a
cancelled one are different answers and the consumer has no other way to tell them apart.

#### Scenario: Blank text handed to an already-cancelled enumeration still faults
- **GIVEN** a speech synthesizer asked for blank or whitespace-only text
- **AND** a token that is already cancelled when the caller starts enumerating
- **WHEN** the caller enumerates
- **THEN** an `OperationCanceledException` is raised, rather than the enumeration ending quietly with zero frames because the empty-input branch ran first

#### Scenario: The shortcut still applies when nothing was cancelled
- **GIVEN** the same synthesizer asked for blank or whitespace-only text
- **AND** a token that is not cancelled
- **WHEN** the caller enumerates
- **THEN** the enumeration ends with zero frames and no provider session is opened, unchanged — moving the guard must not turn a routine empty request into a fault

#### Scenario: Every synthesizer in the package answers the same way
- **GIVEN** the blank-text-plus-cancelled-token input
- **WHEN** it is put to each speech synthesizer the package ships
- **THEN** all of them raise `OperationCanceledException`, asserted per surface rather than assumed from one, so a synthesizer added later inherits the assertion instead of the convention

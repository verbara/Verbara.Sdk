# streaming-session-lifecycle — Delta

## ADDED Requirements

### Requirement: A hangup that overtakes the first read ends the audio stream, it does not fault it
A streaming audio session SHALL treat the far end hanging up as termination of the audio sequence,
whatever the ordering between the hangup and the consumer's first read. The consumer MUST NOT
observe an exception that is an artefact of that ordering. Because the read method is an async
iterator, its body runs on the first `MoveNextAsync` rather than at call time, so any session state
it reads MUST be captured while the session is known to be alive rather than read at iteration time.

#### Scenario: The hangup arrives before the consumer's first read
- **GIVEN** a session whose read loop has already handled a hangup frame and disposed the session
- **WHEN** the consumer calls the read method and begins enumerating
- **THEN** the sequence ends the way a hangup always ends it, rather than the enumerator throwing `ObjectDisposedException` from a cancellation source the session disposed

#### Scenario: The hangup arrives mid-enumeration
- **GIVEN** a consumer enumerating audio frames
- **WHEN** the far end hangs up
- **THEN** the sequence ends at that point with the same observable outcome as the pre-read ordering, so the consumer's handling does not depend on when the hangup landed

#### Scenario: Disposed-state behaviour is uniform across the session's public surface
- **GIVEN** a session the consumer has explicitly disposed
- **WHEN** any public member is called
- **THEN** the outcome follows one stated rule for the whole type, rather than one member behaving differently because it never had a disposed-state guard

### Requirement: A cancellation anywhere in a streaming session's lifetime ends it cleanly
A streaming session SHALL handle a requested cancellation identically wherever it lands — during
connection, during setup handshakes, or during the running loops. The session MUST NOT surface a
cancellation it was asked for as a fault. Its clean-close path and its terminal telemetry MUST run on
the cancelled path, so a session cancelled during setup is accounted for exactly as one cancelled
mid-stream.

#### Scenario: A cancel during connection or setup is not a fault
- **GIVEN** a session whose token is cancelled while it is connecting, acquiring its write lock, or sending its setup frame
- **WHEN** the cancellation is observed
- **THEN** the session ends the way a cancellation ends it, rather than an `OperationCanceledException` escaping to the caller because the handler guarded only the loops

#### Scenario: The protocol close is sent on the cancelled path
- **GIVEN** an open transport at the moment a setup-window cancellation is observed
- **WHEN** the session ends
- **THEN** a clean protocol-level close is sent, rather than the connection being torn down by disposal alone

#### Scenario: A setup-window cancellation is visible in telemetry
- **GIVEN** a session cancelled before its loops start
- **WHEN** it ends
- **THEN** its completion counter, duration measurement and end-of-session log entry are recorded as they are for any other ending, so the session is not silently absent from the telemetry that accounts for it

## Architectural Risk

Medium, concentrated in the choice of termination semantics rather than in the code. Both fixes are
small and local, but the hangup contract is public behaviour in an MIT SDK: a consumer catching
`ObjectDisposedException` around the read today stops seeing it, and reversing that choice later is
a second breaking change. The second risk is the regression tests — a race reproduced by sleeping
would reintroduce, inside the change that fixes two races, exactly the timing dependency ADR-0045
exists about. Both orderings must be arranged by construction.

# streaming-session-lifecycle Specification

## Purpose

How a streaming audio session ends, and how the ending is classified. A session can stop for many
reasons — the far end hangs up, the application hangs up, the host disposes it, the socket reaches
EOF, the consumer cancels, the connection never opens — and almost all of them are ordinary. Exactly
two are faults, and what separates them is not *what* happened but *who ended it*: the consumer's own
cancellation, and a read issued after the owner disposed the session. Everything else ends the
sequence quietly and is accounted for as a completed session (ADR-0053).

Those two faults are properties of the **stream** — what the enumeration hands its consumer. The
**session** above it counts differently, and the distinction is the one most easily lost: a
cancellation the caller asked for still throws at the iteration boundary, because the caller asked
and is owed the answer, while the session it ended is recorded as *completed* and is not rethrown a
second time at the handler's caller (ADR-0054). Same ending, two layers, and only one of them is a
number an operator reads.

These are one capability because they failed as one, at two layers, for the same reason. An
`async` iterator does not run its body until the first `MoveNextAsync`, so a session that read its
own lifetime state at iteration time raced its own teardown; two hundred lines away, a bridge
consuming that session wrapped only its running loops in the block that owned the terminal
telemetry, so an ending that landed in the setup window escaped as a fault with no accounting at
all. Both are the same mistake — inferring the *kind* of ending from *where in the code* it was
observed — and neither is fixable without the other, because a session that ends quietly is only
correct if the layer above it stops counting that quiet ending as a crash.

The consequence the requirements below exist to protect is a telemetry one. `sessions.failed` is
what an operator pages on; a counter that fires on every hangup is noise, and one that stays silent
through a genuinely refused connection is worse. Widening a handler so a cancelled connect stops
being a failure is therefore only correct alongside the converse — a handshake the far end rejects
must still be counted, logged and rethrown — which is why both endings of every window are pinned
here rather than only the one that was broken.

Two further requirements joined later, and they belong here for the same reason the first pair does.
**Ownership of a cancellation source reachable from two concurrently running loops** is a lifecycle
question, not a threading detail: when disposal and a loop's own teardown can both release it, the
loop that merely *cancels* observes a disposed source and throws — so a barge-in, a feature working
exactly as designed, was booked as a failed session. **Agreement between handlers** is the same
telemetry consequence read from the other end: two implementations behind one interface classified
an identical shutdown as a failure and as a completion respectively, and a caller reading the number
cannot see which handler produced it. Both are cases of the ending being inferred from where it was
observed rather than from who caused it (ADR-0054).
## Requirements
### Requirement: A hangup that overtakes the first read ends the audio stream, it does not fault it
A streaming audio session SHALL treat every ending that the consumer did not ask for — a hangup or
error frame from the far end, an application-initiated hangup, an owner disposal, or a transport EOF
— as termination of the audio sequence, whatever the ordering between that ending and the consumer's
reads. Frames already received before the ending MUST still be delivered. Because the read method is
an async iterator, its body runs on the first `MoveNextAsync` rather than at call time; it therefore
MUST NOT read session lifetime state at iteration time, and MUST NOT enumerate on a token derived
from a source the session may already have disposed.

Exactly two outcomes are faults, and they are separated by *who* ended the session: the consumer's
own cancellation, and a read issued after the owner disposed the session.

#### Scenario: The hangup arrives before the consumer's first read
- **GIVEN** a session whose read loop has already handled a hangup frame and torn the transport down
- **WHEN** the consumer calls the read method and begins enumerating
- **THEN** audio buffered before the hangup is delivered and the sequence then ends, rather than the enumerator throwing `ObjectDisposedException` from a cancellation source the session disposed

#### Scenario: The hangup arrives mid-enumeration
- **GIVEN** a consumer enumerating audio frames
- **WHEN** the far end hangs up
- **THEN** the sequence ends at that point with the same observable outcome as the pre-read ordering, so the consumer's handling does not depend on when the hangup landed

#### Scenario: The owner disposes the session mid-enumeration
- **GIVEN** a host shutting a session down while a consumer is enumerating its audio
- **WHEN** the session is disposed
- **THEN** the enumeration ends and the consumer observes no exception, so a routine shutdown is not accounted for as a failed session

#### Scenario: A read issued after the owner disposed the session throws, from the call
- **GIVEN** a session the owner has explicitly disposed
- **WHEN** the consumer calls the read method
- **THEN** an `ObjectDisposedException` naming the session type is thrown by the call itself, matching every other member of the type, rather than surfacing later from an enumerator frame with no object name

#### Scenario: The consumer's own cancellation still faults
- **GIVEN** a consumer enumerating audio with a token it controls
- **WHEN** that token is cancelled
- **THEN** an `OperationCanceledException` is raised at the next iteration boundary, and this takes precedence over the sequence ending quietly

#### Scenario: A session that ends without a hangup frame still releases its transport
- **GIVEN** a session whose socket reaches EOF, or whose read loop ends on a transport error, with no hangup frame ever received
- **WHEN** the read loop returns
- **THEN** the transport and the session's cancellation source are released and the session no longer reports itself as connected, so a session already removed from its owner's registry cannot outlive it

### Requirement: A cancellation anywhere in a streaming session's lifetime ends it cleanly
A streaming session SHALL handle a requested cancellation identically wherever it lands — during
connection, during setup handshakes, or during the running loops. The session MUST NOT surface a
cancellation it was asked for as a fault, and MUST NOT surface the far end's ordinary departure as a
fault either. Its terminal telemetry MUST run on every path, so a session cancelled during setup is
accounted for exactly as one cancelled mid-stream.

#### Scenario: A cancel during connection or setup is not a fault
- **GIVEN** a session whose token is cancelled while it is connecting, acquiring its write lock, or sending its setup frame
- **WHEN** the cancellation is observed
- **THEN** the session ends the way a cancellation ends it, rather than an `OperationCanceledException` escaping to the caller because the handler guarded only the loops

#### Scenario: The ending is routed through one teardown, and the protocol close is attempted only when it can be
- **GIVEN** a session ending from anywhere in its lifetime
- **WHEN** it ends
- **THEN** the ending is routed through the same teardown as any other ending, and a clean protocol-level close is attempted when — and only when — the transport is still open, because cancelling a transport operation aborts it and leaves nothing to close politely

#### Scenario: A setup-window cancellation is visible in telemetry
- **GIVEN** a session cancelled before its loops start
- **WHEN** it ends
- **THEN** its completion counter, duration measurement and end-of-session log entry are recorded as they are for any other ending, so the session is not silently absent from the telemetry that accounts for it

#### Scenario: The far end departing mid-playback is not a fault
- **GIVEN** a session writing audio back to a caller who hangs up while the assistant is speaking
- **WHEN** the write finds the audio session already ended
- **THEN** playback stops and the session ends normally, rather than the ordinary ending being counted as a failure two hundred lines from where the read side handles the same event

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


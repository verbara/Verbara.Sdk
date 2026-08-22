# streaming-session-lifecycle Specification

## Purpose

How a streaming audio session ends, and how the ending is classified. A session can stop for many
reasons — the far end hangs up, the application hangs up, the host disposes it, the socket reaches
EOF, the consumer cancels, the connection never opens — and almost all of them are ordinary. Exactly
two are faults, and what separates them is not *what* happened but *who ended it*: the consumer's own
cancellation, and a read issued after the owner disposed the session. Everything else ends the
sequence quietly and is accounted for as a completed session (ADR-0053).

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


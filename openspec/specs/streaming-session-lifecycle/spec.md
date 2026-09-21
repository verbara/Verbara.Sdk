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

A later requirement joined from one layer **earlier** than the rest — the server's accept path — and
it belongs here for the same reason, not despite arriving before the session exists. A connection is
a resource with an owner from the moment it is accepted, and the rule ADR-0053 states for a session's
transport (exactly one owner releases it) is that rule read before there is a session to state it
about. A server that hands an accepted connection to its handler through a work item gated on its own
stopping token can have that hand-off skipped, and because every dispose site lives inside the
handler, nothing closes the connection and nothing counts it: no counter moves, no gauge moves, no
session is ever registered, and the far end holds a connection the server has forgotten. That is this
capability's failure mode arriving one layer early — an ending unaccounted for because of *where* the
code stopped running rather than *who* ended it — which is why the accept path files here rather than
under a capability of its own (ADR-0058).

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

### Requirement: A synthesizer's own cancellation is a synthesis failure, not a barge-in
A voice pipeline SHALL report a synthesis as failed when the synthesizer ends it with its own
`OperationCanceledException` while neither the caller's token nor the synthesis's own source is
cancelled. It SHALL NOT report as failed a synthesis that ends with an `OperationCanceledException`
while a requested cancellation (a barge-in, disposing the pipeline, or the caller's token) is visible.
Among `OperationCanceledException`s the ending is classified by which source is cancelled when the
pipeline classifies it, never by the exception's concrete type, message, inner exception or token.
Exceptions of any other type are outside this requirement.

#### Scenario: A synthesizer's own deadline elapses while nobody asked it to stop
- **GIVEN** a pipeline session whose caller token is never cancelled, with no barge-in and no disposal
- **WHEN** the synthesizer raises an `OperationCanceledException` of its own, such as a `TaskCanceledException` whose inner exception is a `TimeoutException` and whose token belongs to a source the synthesizer cancelled, before yielding any audio or after yielding some
- **THEN** the synthesis is reported as failed: the pipeline publishes one `PipelineErrorEvent` with source `Tts` carrying that exception, increments `tts.syntheses.failed`, logs the error at Warning and sets the synthesis activity to `Error`
- **AND** it publishes no `SynthesisEndedEvent` for that turn and does not increment `tts.syntheses.completed`

#### Scenario: The session outlives the failed synthesis
- **GIVEN** a synthesis that failed because the synthesizer cancelled itself
- **WHEN** the session later ends normally
- **THEN** the failure was not rethrown to the caller of the session handler, and the session is counted in `voiceai.sessions.completed`, not in `voiceai.sessions.failed`
- **AND** the pipeline goes on to recognise, handle and synthesise the caller's next utterance

#### Scenario: A barge-in is not a synthesis failure
- **GIVEN** a synthesis in flight
- **WHEN** the caller speaks over it and the synthesis ends with an `OperationCanceledException` because its own source was cancelled
- **THEN** `tts.syntheses.failed` does not move, no `PipelineErrorEvent` is published, nothing is logged at Warning, and the synthesis activity is not set to `Error`

#### Scenario: Disposing the pipeline during a synthesis is not a synthesis failure
- **GIVEN** a synthesis in flight
- **WHEN** the pipeline is disposed and the synthesis ends with an `OperationCanceledException` because its own source was cancelled
- **THEN** `tts.syntheses.failed` does not move, nothing is logged at Warning, the synthesis activity is not set to `Error`, and the session is not counted as failed

#### Scenario: The caller's cancellation during a synthesis is not a synthesis failure
- **GIVEN** a synthesis in flight
- **WHEN** the caller cancels the token it handed the session handler and the synthesis ends with an `OperationCanceledException`
- **THEN** `tts.syntheses.failed` does not move, no `PipelineErrorEvent` is published, nothing is logged at Warning, and the synthesis activity is not set to `Error`
- **AND** the session handler returns without throwing and the session is counted in `voiceai.sessions.completed`

#### Scenario: A requested cancellation outranks the provider's own while it unwinds
- **GIVEN** a synthesizer that has raised its own `OperationCanceledException`, and whose sequence the pipeline is still disposing
- **WHEN** a barge-in, disposing the pipeline, or the caller's token cancels before that disposal completes
- **THEN** the synthesis is not reported as failed: `tts.syntheses.failed` does not move, no `PipelineErrorEvent` is published, nothing is logged at Warning, and the synthesis activity is not set to `Error`
- **AND** the session handler returns without throwing and the session is not counted in `voiceai.sessions.failed`

### Requirement: A connection the server accepts is closed even when it is never served
An audio server SHALL close every connection it accepts, including one accepted in the moment it is
asked to stop and therefore never served. Handing an accepted connection to the code that owns its
disposal MUST NOT be gated on the server's stopping token: a hand-off a cancelled token skips leaves
that connection with no owner — nothing closes it, no counter or gauge moves, and the far end keeps a
connection the server has forgotten. The connection handler MUST still receive that token, so a
connection it takes over while the server is stopping is closed at once rather than at the end of the
wait for the identifying frame. Accepting and serving a connection while the server is running is
unchanged, and so is the wait between failed accepts.

#### Scenario: The server is asked to stop between the accept and the hand-off
- **GIVEN** an accept loop whose stopping token is cancelled in the same moment an accept returns a connection
- **WHEN** the loop hands that connection to the connection handler
- **THEN** the connection is closed — the far end's read reaches end of stream — rather than being left open with no owner and released only if the socket handle is eventually finalized
- **AND** the loop then ends without attempting another accept

#### Scenario: Closing a connection the server never served is quiet
- **GIVEN** a connection closed because the server was already stopping when it was accepted
- **WHEN** the server finishes stopping
- **THEN** nothing is logged at Warning or above for that connection: it missed no deadline and failed in no way

#### Scenario: A connection accepted while the server is running is still served
- **GIVEN** a running server whose stopping token is not cancelled
- **WHEN** it accepts a connection
- **THEN** the connection handler takes it over and begins its bounded wait for the identifying frame, and the connection stays open for as long as that wait lasts

#### Scenario: The stopping token still reaches the handler
- **GIVEN** a connection the handler took over and is waiting on for the identifying frame
- **WHEN** the server is asked to stop before that frame arrives and before the wait's deadline
- **THEN** the connection is closed on the stop rather than at the deadline, so a shutdown does not wait out the connection timeout

#### Scenario: The wait between failed accepts is unchanged
- **GIVEN** a run of consecutive failed accepts
- **WHEN** the loop backs off between them
- **THEN** the wait starts at 100 ms, doubles with each consecutive failure up to 5 s, starts over after an accept succeeds, and ends at once when the stopping token is cancelled during it

### Requirement: The far end leaving mid-playback is not a synthesis failure
A voice pipeline SHALL NOT report a synthesis as failed when writing its audio finds the audio session
already ended. An ending nobody in the process asked for is a termination of the playback, not a fault
of the synthesizer (ADR-0053 R3), and the pipeline MUST NOT increment `tts.syntheses.failed`, publish a
`PipelineErrorEvent`, log at Warning or above, or set the synthesis activity to `Error` for it. It
SHALL account that turn exactly as it accounts a barge-in, so that one future instrument separating
"the caller heard the answer" from "the caller heard part of it" moves both endings together; what
that accounting counts today is recorded as debt (ADR-0050 E9) and is not required here. A write that
fails while the session is **still connected** is untouched by this requirement and remains a
synthesis failure, as does any failure raised by the synthesizer itself.

This is the synthesis-layer companion to the session-layer scenario already in this capability. That
one says the session ends normally when the far end departs mid-playback; it says nothing about the
synthesis counters, and both are now pinned.

#### Scenario: The caller hangs up while the assistant is speaking
- **GIVEN** a pipeline session writing synthesized audio back to a caller, with no barge-in, no disposal and a caller token that is never cancelled
- **WHEN** the caller hangs up, the session tears its transport down, and the next chunk's write finds the session already ended
- **THEN** `tts.syntheses.failed` does not move, no `PipelineErrorEvent` is published, nothing is logged at Warning or above, and the synthesis activity is not set to `Error`
- **AND** the ending is recorded in the same instruments a barge-in is recorded in, and is visible in the log below Warning

#### Scenario: Playback stops rather than draining the rest of the answer
- **GIVEN** a synthesizer with more audio to yield for the current turn
- **WHEN** a write finds the audio session already ended
- **THEN** the pipeline stops pulling audio from the synthesizer for that turn and releases the provider sequence, rather than synthesizing an answer nobody can hear

#### Scenario: The session ends normally after the abandoned playback
- **GIVEN** a turn whose playback was abandoned because the far end left
- **WHEN** the session handler returns
- **THEN** nothing is rethrown to its caller, and the session is counted in `voiceai.sessions.completed`, not in `voiceai.sessions.failed`

#### Scenario: A failure the synthesizer itself raises is still a synthesis failure
- **GIVEN** a session that is still connected
- **WHEN** the synthesizer ends the turn with a failure of its own, including one raised because a provider client was used after its own disposal
- **THEN** the turn is reported as failed — `tts.syntheses.failed` increments, one `PipelineErrorEvent` with source `Tts` carries that exception, the failure is logged at Warning and the synthesis activity is set to `Error` — because the guard for a departed session covers the write and nothing else

#### Scenario: A requested cancellation of the write still belongs to whoever asked for it
- **GIVEN** a write in flight for the current turn
- **WHEN** the caller's token, a barge-in or disposing the pipeline cancels it
- **THEN** the ending is classified exactly as it is when the synthesizer observes that cancellation, rather than being absorbed as a departed session

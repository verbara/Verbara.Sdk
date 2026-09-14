# streaming-session-lifecycle — Delta

## ADDED Requirements

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

## Architectural Risk

- **Level:** LOW.
- **Affected:** the accept path of `AudioSocketServer` in `Verbara.Sdk.VoiceAi.AudioSocket`. No public
  API change, so nothing downstream recompiles, and no counter, gauge, activity or event moves — the
  only observable difference is at shutdown, where a connection that used to be left open is now
  closed.
- **Mitigation:** the connection now reaches the branch that has always closed a connection for a
  stopping server, so the fix removes a gate rather than adding a path. Regression tests pin both
  sides, ordered by construction and on a clock that never moves: a stop landing between the accept
  and the hand-off closes the connection quietly and attempts no further accept, while a connection
  accepted by a running server stays open until its handler's wait ends. Restoring the gate, deleting
  the handler's dispose, and handing the handler a token that is not the server's each fail at least
  one of them; the three accept-backoff tests hold the wait sequence unchanged.

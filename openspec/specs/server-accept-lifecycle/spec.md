# server-accept-lifecycle Specification

## Purpose

What a server that accepts connections in a loop does when an accept fails, and how it tells that
failure apart from its own shutdown. The two endings look identical from inside the loop — both arrive
as an exception from the same await — and the whole capability is the discipline of not confusing them.

A stop ends the loop, because that is what the stop asked for. **A failure the server can survive does
not**, and must not end it in silence: descriptor exhaustion, a kernel out of buffers, a connection
aborted in the backlog. Left uncaught, such a failure faults the loop task, which nothing awaits until
shutdown and which the runtime discards unobserved — so the server stops accepting with no log line,
its socket still bound, still reporting itself as running. The failure is therefore reported at Error
and the loop goes on accepting, after a wait that grows with each consecutive failure up to a cap so
that a persistent failure can neither spin the loop nor flood the log, and that returns to its initial
value on the next successful accept.

The discriminator is the server's **own published running state**, never the loop's cancellation token.
That is not a style preference: a stop clears its state before it aborts the pending accept, so the
state cannot be late, while the token is cancelled only afterwards and can be. A server whose stop
lowers that flag last therefore cannot classify its own shutdown, and must be reordered before the
classification can be correct. The state is published and read through a memory barrier, because the
loop and the stop run on different threads, and it doubles as the guard that makes a second start a
no-op rather than a second listener.

A failure the server survives is **not** a terminal state. The listener is still bound and the loop is
still accepting, so a server that marked itself faulted would refuse a later start over a loop that is
still working. What an operator gains is the Error line, not a state transition — and a health check
built on that state is unchanged, which is a limit this capability states rather than leaves to be
discovered (ADR-0056 R6).

## Requirements

### Requirement: An accept failure the server can survive does not end its accept loop in silence
A server that accepts connections in a loop SHALL distinguish an accept that ended because the server
is stopping from one that failed while the server was still meant to be running, and MUST NOT let the
second end the loop without saying so. A stopping server ends its loop, which is the ending its stop
path asks for. A running server that fails to accept — because the process is out of file descriptors,
because the kernel is out of buffers, or because a connection was aborted in the backlog — SHALL report
the failure at Error and SHALL go on accepting, after a wait that grows with each consecutive failure
up to a cap so that a persistent failure can neither spin the loop nor flood the log.

The discriminator SHALL be the server's own published running state, never the loop's cancellation
token: a stop clears that state before it aborts the pending accept, so the state cannot be late, while
the token is cancelled afterwards and can be. The wait SHALL be driven by an injected time source so it
is observable in a test without a real delay.

The loop MUST NOT write a terminal state for a failure it survives. A server whose loop is still bound
and still accepting is not faulted, and marking it so would refuse a later start over a loop that is
still working.

#### Scenario: An accept fails while the server is running
- **GIVEN** a running server whose next accept fails for a reason that is not its stop
- **WHEN** the failure reaches the accept loop
- **THEN** it is reported once at Error, the loop waits, and the server accepts the next connection

#### Scenario: Consecutive failures back off, and a success starts the wait over
- **GIVEN** a running server whose accepts keep failing
- **WHEN** each failure follows the last
- **THEN** each wait is longer than the one before it, up to a cap it never exceeds
- **AND** an accept that succeeds between two failures returns the next wait to its initial value

#### Scenario: A stopping server ends its loop rather than backing off
- **GIVEN** a running server with a connection accepted and its loop parked on the next accept
- **WHEN** the server is stopped
- **THEN** the loop ends, the stop runs to completion, and nothing is reported as an accept failure —
  because an accept aborted by the stop is the stop

#### Scenario: A survived failure is not a terminal state
- **GIVEN** a server that has reported an accept failure and gone on accepting
- **WHEN** its published state is read
- **THEN** it still reads as listening, because the listener is bound and the loop is running — the
  failure is reported through the log, and a health check built on that state is unchanged

### Requirement: The flag that classifies an accept failure is cleared before the stop that causes one
A server whose accept loop tells a stop from a failure by reading its own published running state SHALL
clear that state **before** it does anything that can abort a pending accept, and SHALL publish it
through a read that cannot observe a stale value. A stop that aborts the accept first and lowers the
flag afterwards leaves the loop classifying its own shutdown as a failure, which turns every ordinary
stop into a reported error and makes the report worthless precisely when a real failure needs to stand
out.

The same state SHALL make a second start a no-op rather than a second listener: a server that binds
again while already bound leaks the listener, the cancellation source and the loop task it replaces, and
leaves the first loop running against a source nothing can cancel.

#### Scenario: A stop is not reported as an accept failure
- **GIVEN** a running server with a connection accepted and its loop parked on the next accept
- **WHEN** the server is stopped and the pending accept is aborted
- **THEN** the loop ends, the stop completes, and nothing is reported as an accept failure — because the
  running state was already false when the abort reached the loop

#### Scenario: The running state is readable from the accept loop's thread
- **GIVEN** a stop running on one thread and an accept loop reading the running state on another
- **WHEN** the stop clears that state
- **THEN** the loop's next read observes the clear rather than a cached value, because the state is
  published and read through a memory barrier rather than a plain field

#### Scenario: Starting an already-started server changes nothing
- **GIVEN** a server that is already bound and accepting
- **WHEN** it is started again
- **THEN** the call returns without binding a second listener, without replacing the cancellation source
  or the loop task, and the server goes on accepting on the one it already had

#### Scenario: A stop that begins is a stop, for anyone reading the state
- **GIVEN** a consumer reading the server's published running state
- **WHEN** a stop begins but has not yet finished releasing connections
- **THEN** the state already reads as not running, because it describes the server's intent rather than
  the completion of its teardown

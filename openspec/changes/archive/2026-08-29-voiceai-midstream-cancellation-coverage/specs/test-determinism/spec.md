# test-determinism — Delta

## ADDED Requirements

### Requirement: Cancellation coverage on a WebSocket surface includes a session with frames in flight
Every in-process WebSocket fake SHALL carry a cancellation test whose token fires while the session
is mid-delivery — the socket open, and at least one server frame already observed by the caller —
in addition to any pre-cancelled-token test. A pre-cancelled token is a necessary case and not a
sufficient one: it throws at iterator entry, before the socket is opened, so it exercises neither
the session teardown path nor any fence that exists to keep a session alive. A hold-open capability
on a fake MUST have at least one test that fails when the hold is removed; a hold-open flag with no
assignment anywhere in the repository, or one whose only consumer stays green when the hold is
swapped for `await receiveTask`, does not satisfy this and MUST be recorded as unconsumed rather
than counted as covered.

#### Scenario: The pre-cancelled test cannot stand in for the mid-flight one
- **GIVEN** a fake whose cancellation test cancels the token before enumeration and asserts the fake received nothing
- **WHEN** that suite's cancellation coverage is enumerated
- **THEN** the surface counts as covered for iterator entry only, because the socket was never opened and no teardown, hold-open path or in-flight read was reached

#### Scenario: The token fires with a frame already delivered
- **GIVEN** a fake that has sent a frame and a caller that has observed it
- **WHEN** the token fires at that moment
- **THEN** the cancellation propagates out of the caller's own enumeration, and the test states what held at the cancel — the live server-side socket state — rather than only that it threw

#### Scenario: A hold-open with no falsifier is reported as unconsumed
- **GIVEN** a hold-open flag on a fake server
- **WHEN** no test sets it, or its only consumer passes identically with the hold replaced by `await receiveTask`
- **THEN** it is recorded as unconsumed, because a flag nothing can falsify is indistinguishable from one that does nothing

#### Scenario: Every WebSocket surface has a cancellation test at all
- **GIVEN** the set of in-process WebSocket fakes in this repo
- **WHEN** their cancellation coverage is enumerated per surface
- **THEN** a surface with no cancellation test of any shape is named as absent rather than absorbed into a per-class count, because a count taken over fakes rather than over tests hides the surface that has none

### Requirement: A fence is not witnessed by an assertion that would hold with the fence deleted
A test cited as evidence for a fence SHALL fail when the fence is removed, and a fence whose cited
assertion stays green without it MUST be recorded as unwitnessed. Where the production client cannot
produce the condition the fence handles, the witness MUST be a test that produces that condition
directly — driving the fake with a raw protocol client — rather than a test that never reaches the
fence and passes vacuously. Reinstating a defect in shipped source to make a fence fire is a
measurement technique and MUST NOT be committed as a test.

#### Scenario: A vacuous assertion is not a witness
- **GIVEN** a fake tolerating a client half-close, and an assertion that the client did not half-close
- **WHEN** no shipped client ever sends a close frame on that surface
- **THEN** the assertion is not evidence for the fence, because deleting the fence leaves it green — it records what the client does, not what the fake tolerates

#### Scenario: A raw protocol client supplies the condition the product never sends
- **GIVEN** a fence handling a protocol event no shipped client emits
- **WHEN** a test drives the fake with a raw client that emits it
- **THEN** the fence is exercised on its own terms, with the production client and shipped source untouched

#### Scenario: A defect reinstated to prove a fence stays out of the tree
- **GIVEN** a fence proven live by temporarily restoring a removed behaviour in shipped source
- **WHEN** that evidence is written down
- **THEN** the measurement is recorded and the modification reverted, and the committed test reaches the fence some other way — a suite that carries a reinstated defect is asserting the defect

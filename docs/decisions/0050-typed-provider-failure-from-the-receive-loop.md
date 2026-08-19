# ADR-0050 — Provider failure reaches the caller as a typed exception thrown from the receive loop

- **Status:** Accepted
- **Date:** 2026-08-17
- **Deciders:** Harol A. Reina H.
- **Related:** ADR-0049 (its **D1** and **D2** state the requirements this ADR implements, and its
  Consequences section already records that D1 "creates and does not fund" the remediation work.
  ADR-0049 is `Accepted` and is **not edited here**: its five-site count and its D2 discriminator were
  what the evidence supported on 2026-08-15, and this ADR supersedes both with measurement rather than
  rewriting the record). ADR-0048 (the probe method with two controls, used for every measurement
  below). ADR-0014 (the provider clients are hand-rolled `ClientWebSocket` code, so this repo owns the
  receive loop and the remedy has somewhere to live).

## Context

ADR-0049 decided **what** must happen — a receive loop must not silently discard a frame carrying a
failure (D1), and completing successfully with zero output is a failure (D2) — and deliberately left
**how** open. Every task deferred to "the D1 remedy" has been waiting on that mechanism, and three
shipped source comments say so in as many words: a synthesis that silently yields nothing "would start
throwing", which "belongs to the D1 remedy and its own decision, not inside a frame-format fix".

This ADR is that decision. Re-measuring the population first changed three things about what the
remedy has to cover.

### The population is 8 clients, not 5 sites

Thirteen provider clients ship across the TTS and STT packages. Eight hold a `ClientWebSocket` and
therefore own a receive loop; five reach their vendor over HTTP and have no loop to audit — recorded
as examined-and-clean, because a clean loop is evidence and an unexamined one is not.

| Package | WebSocket clients (audited) | HTTP clients (no receive loop) |
|---|---|---|
| TTS | Cartesia, ElevenLabs, LMNT, Deepgram | Azure, Speechmatics |
| STT | Speechmatics, AssemblyAI, Cartesia, Deepgram | Google, Whisper, Azure Whisper |

ADR-0049 named five sites. The difference is not five becoming eight by re-counting the same shape: it
is that two further **doors** exist through which a failure leaves without reaching the caller, and
both are open at **every one** of the eight.

### Three doors, not one

1. **Frame meaning** — the door ADR-0049 D1 names. An allow-list of content message types, so every
   unanticipated type, including every error the vendor defines, falls into the discard branch.
2. **The close code.** All eight loops contain `if (result.MessageType == WebSocketMessageType.Close)
   break;` and **`CloseStatus` is read nowhere in either package.** Yet the close code *is* an in-band
   failure signal on measured surfaces: Speechmatics answers a rejected credential with `101` then
   close `4001 not_authorised`, and ElevenLabs closes `1008` behind its error frame. A remedy scoped
   to text frames re-ships D1 through this door unchanged.
3. **Socket death.** All eight loops contain `catch (WebSocketException) { break; }`. A socket that
   dies mid-stream therefore ends the enumeration **normally** — empty, or worse, silently
   **truncated** after real audio was delivered. Same class as D1 one layer down: a failure converted
   into successful completion.

Closing only the first door would satisfy D1's letter on three surfaces and leave the guarantee false
on all eight.

### Why this ADR cites files and constructs, not line numbers

ADR-0049 cited three line numbers. All three were wrong within two days — one file was rewritten
wholesale by an unrelated fix and the other two shifted — while the defects themselves stayed exactly
where they were. Line numbers made a correct record read as a stale one. This ADR names the file and
the construct instead, which is what a reader greps for anyway.

### D2's discriminator cannot be implemented once D1 is fixed

ADR-0049 D2 states the discriminator as *"frames arrived and were discarded" versus "no frames
arrived"*. Once D1 is satisfied, **nothing is discarded** — the first branch is empty by construction,
so the test cannot separate anything. The case that actually needs separating is the one the
discriminator does not reach: a session that terminated cleanly having produced no content, where the
open question is whether the vendor failed without saying so or legitimately had nothing to produce.

The code also carries a **third** outcome that ADR-0049 does not model: the caller cancelled. A
barge-in cancels the synthesis token, the stream ends early with zero bytes, and that is correct
behaviour — the pipeline already publishes `BargInDetectedEvent` for it.

### What the surface already is

- The TTS and STT packages declare **no exception type and contain no `throw` statement** — the only
  layers of this SDK without one. Every other subsystem signals failure through a typed hierarchy
  (`AmiException` with `AmiAuthenticationException`, `AmiConnectionException`, `AmiProtocolException`,
  `AmiTimeoutException` and others; `AriException` with `AriNotFoundException`, `AriConflictException`;
  `AgiException` with `AgiHangupException`, `AgiNetworkException`; plus `LiveException`,
  `SessionException`, `ActivityException`, `ConfigParseException`, `CircuitBreakerOpenException`).
- **The surface already throws, untyped.** `ConnectAsync` is unguarded in every WebSocket client, so a
  vendor that rejects at the handshake — Cartesia answers `401`, measured in ADR-0049's table — already
  surfaces a `WebSocketException` at the caller's first `MoveNextAsync`. The question this ADR settles
  is therefore *which* failures throw and *as what*, not whether the API throws at all.
- **The delivery channel already ships and is dead.** `VoiceAiPipeline` exposes a public
  `IObservable<VoiceAiPipelineEvent>`, and `PipelineErrorEvent` already carries an `Exception?` and a
  source discriminator. It is fed only from `catch` blocks, so with no client throwing, the provider
  branch is unreachable. A throwing client lights up a channel that is already built, shipped and
  documented, at zero new delivery surface.
- **The clients are singletons.** All are registered `TryAddSingleton`, so one instance serves every
  concurrent session and no per-call state can live on the client.
- **`SpeechSynthesizer` and `SpeechRecognizer` are public and abstract**, so any change to the element
  type of the returned stream is a break for third-party implementors as well as for callers.

## Decision

**E1 — A provider failure reaches the caller as a thrown, typed exception, raised from the receive
loop.** This is the mechanism D1 left open. It is not optional for the caller to observe, it needs no
new delivery surface, and the receive loop is the only layer that holds the evidence D1 and D2 speak
about. The existing `Channel`-based plumbing carries it without restructuring: completing the writer
with the exception surfaces it at the reader, and therefore at `MoveNextAsync`.

**E2 — All three doors are closed, not just the first.** A remediated loop (a) discriminates on the
frame's meaning rather than on membership of a content allow-list, (b) reads the close code and
reason and treats a failure close as a failure, and (c) does not convert a mid-stream
`WebSocketException` into normal completion. Deliberate lifecycle filtering stays legal, exactly as
ADR-0049 D1 permits.

**E3 — The hierarchy roots at `System.Exception`.** It cannot mirror `AmiException : AsteriskException`
literally: the core VoiceAi package references only the audio packages, so the SDK's root exception
type is not reachable from here. Adding that dependency to reach it would be wrong on its own terms —
a rejected TTS credential is not an Asterisk error, and this package family is deliberately
independent of the PBX layer. The deviation from the SDK's otherwise uniform shape is recorded here
rather than left to look like an oversight.

**E4 — Two types, and the split carries evidence, not retry policy.** One type for *the vendor said
why* — carrying the provider name, the vendor's code and its message, whether that arrived as an error
frame, a close code, or a wrapped handshake rejection. A second for *nothing was said* — a session
that ended cleanly having produced no content. The two are different operational events: the first is
an alert with a cause attached, the second is the trigger to run an ADR-0048 probe series against that
surface. The type boundary is explicitly **not** a retryability boundary — a rate-limit rejection is
retryable and an invalid-credential rejection is not, and both are the same type — so policy reads the
vendor code, never the type.

**E5 — D2 fires per surface, because one blanket rule produces false positives that are structural.**

- **Synthesis:** zero audio, no failure reported, not cancelled → failure. Input that is empty or
  whitespace is guarded at the client and is not a provider failure. Whether a vendor legitimately
  produces no audio for punctuation-only input is **measured per vendor** under the ADR-0048
  instrument before it is assumed either way.
- **Recognition:** a failure only where **no vendor frames arrived at all**, or where the session
  closed abnormally. A recognition session that received lifecycle frames and produced zero
  transcripts completes empty and legitimately: voice activity detection flushes an utterance on any
  turn trigger, so noise with no speech is a healthy zero-transcript session, and on at least one
  vendor it presents as lifecycle frames with no content frames — indistinguishable, under a blanket
  rule, from a rejected session.

**E6 — Cancellation is never a failure.** Zero output caused by the caller's own cancellation does not
throw and is not counted as a failure. This is the third outcome ADR-0049 does not model, and it needs
no new discriminator: it is the cancellation token, which .NET already types and which the pipeline
already handles as a distinct arm.

**E7 — A handshake rejection is wrapped, not left raw.** Where a vendor validates the credential in
the upgrade, the resulting transport exception is wrapped in the same *vendor said why* type as an
in-band rejection. Otherwise a caller's `catch` would have to depend on which validation regime the
vendor happens to use — precisely the property ADR-0049 **D3** establishes cannot be inferred, and a
property that can change under the caller with no line of this repository changing. This retypes an
exception that existing callers may already catch, which is part of the behavioural break below.

**E8 — This ADR substitutes an operational discriminator for D2's, and says so.** D2's stated test is
unimplementable once D1 is satisfied (see Context). The operative test is: *did the vendor report a
failure* → the first type; *did the session end clean and empty without being cancelled* → the second,
subject to E5's per-surface rule; *was it cancelled* → neither. ADR-0049 remains `Accepted` and
unedited; this is a recorded substitution, not a silent reinterpretation.

**E9 — The zero-output counter is additive, and scoped to what does not throw.** Once clients throw,
the pipeline's existing failure counters absorb every provider failure. Those counters change meaning
for anyone already listening — unavoidably, and correctly, because they were reporting sessions as
successful that had failed. A new counter is added for the residual the clients cannot reach: a
zero-output completion from an implementation that does not throw, which includes any third-party
implementation of the public abstract base. It carries the discriminator as a tag. That the cancelled
case is today counted as a completed synthesis is recorded as adjacent debt, not fixed under this ADR.

## Consequences

- **Negative, and the headline: this is a behavioural break.** Code that compiles unchanged behaves
  differently — a session that used to go quiet now throws. It ships in a minor with an explicit
  callout, never in a patch. It is also the point of the change: the defect being fixed is that
  nothing was raised.
- Negative: eight receive loops are rewritten along three axes each, not one. E2 makes the work
  larger than ADR-0049's five-site count implied, and larger than a text-frame fix would have been.
- Negative: E5 replaces one rule with two, and the synthesis rule depends on a probe series that has
  not been run. Until it is, the punctuation-only case is stated as unmeasured rather than assumed.
- Negative: three public types plus a counter are permanent public surface, and E3 records a
  deliberate inconsistency with the exception rooting used everywhere else in the SDK.
- Positive: the guarantee becomes unconditional. There is no configuration in which a caller silently
  receives a rejected session, and no opt-in step a caller must know to take.
- Positive: no new delivery machinery. The shipped-but-unreachable error event becomes truthful, and
  the failure counters start counting failures.
- Positive: the remedy is one shape across eight clients, so the next receive loop written in this
  repository has a rule to follow rather than three precedents to interpret.
- Neutral: parsing the failure frame's fields beyond provider, code and message is out of scope here,
  as it was in ADR-0049 — the DTO shape belongs to the change that reserves ADR-0046.
- Neutral: the LMNT synthesizer's remaining test-only constructors are not addressed by this ADR;
  they are a test-seam concern, tracked separately from the failure contract.

## Alternatives considered

- **A typed terminal signal the caller consults, or a per-call callback.** Rejected: both make
  noticing a failure opt-in, and a caller who does not consult is exactly as blind as before. ADR-0049
  is titled *in-band failure must reach the caller*; an opt-in signal does not reach anyone who has
  not already been told to look.
- **A discriminated union as the stream element (`Audio | Failure`).** Rejected, and the usual
  greenfield intuition for it does not survive the language: C# performs no exhaustiveness checking on
  matches over a class hierarchy, so a caller handling only the audio case ignores the failure case
  **silently**. The union is therefore also opt-in consultation, at the price of breaking every
  caller *and* every third-party implementor of a public abstract base. It buys a weaker guarantee for
  a larger break.
- **Log-only, or observability-only (activity status plus a counter).** Rejected on D1: a log line is
  not the caller and a counter is not the caller. Both are worth having in addition, neither
  discharges the requirement.
- **A mutable "last failure" property on the client.** Rejected on the registration: the clients are
  singletons, so two concurrent sessions would overwrite one another's failure. Unsafe by
  construction at the concurrency this SDK is sized for.
- **An option to enable throwing, defaulting to off, flipped in the next major.** Rejected. The
  defect is that nobody notices; a defence that must be switched on by the people who do not know they
  need it defends no one, and every existing deployment would keep the defect until someone read a
  changelog. It also doubles the behavioural test matrix across eight clients.
- **The same option defaulting to on, as an operator rollback lever.** Also rejected, and separately:
  its off-position has no coherent meaning — suppressing the exception returns the caller to the empty
  stream, which is the defect reproduced on request — and it would be permanent public surface on
  every options class, bought against a false-positive risk that E5's per-surface rule and a probe
  series address directly.
- **Detect it in the pipeline instead of the clients.** Rejected on decidability: the pipeline sees a
  chunk count and a cancellation token, never frames, so it can implement neither D1's cause nor D2's
  discriminator. It also leaves every direct user of the public abstract classes — the sample
  applications among them — with no signal at all. A weak pipeline-side backstop is kept under E9 for
  implementations that do not throw, which is all the pipeline can honestly do.
- **An additional richer stream surface alongside the existing one.** Not rejected on merit — it is
  additive and would compose with this decision — but it does not substitute for it: callers of the
  existing method would still receive silence unless that method also throws, at which point this
  decision is already in force. Recorded as a possible future complement, not an alternative.
- **Fix the sites with a live symptom and leave the rest.** Rejected for the reason ADR-0049 rejected
  it one site earlier: the same construction exists where no symptom has appeared yet, each instance
  was written by someone who believed the filtering was harmless, and a per-incident fix leaves the
  belief intact for the next loop.

## Addendum (2026-08-19) — E6 and the `test-determinism` spec disagree, and no test can tell

Harvested while closing `provider-wire-protocol-conformance`. This is not a defect report; it is a
conflict between two `Accepted` artifacts, recorded here because it is currently tracked nowhere and
because the tests that should catch it structurally cannot.

**The conflict.** E6 says cancellation "does not throw". The living spec `test-determinism`
→ *TTS synthesis observes cancellation deterministically* says a token cancelled "before or **during**
`SynthesizeAsync` enumeration SHALL surface `OperationCanceledException` at the next iteration
boundary". Two synthesizers implement E6 on the yielding path and therefore contradict the SHALL:

| Site | Enclosing method | Behaviour on cancel mid-read |
|---|---|---|
| `SpeechmaticsSpeechSynthesizer.cs:118` | `SynthesizeAsync` (public) | `yield break` — stream ends, no throw |
| `LmntSpeechSynthesizer.cs:431` | `SynthesizeHttpAsync` | `yield break` — stream ends, no throw |

The sibling `catch` sites are not in this position: ElevenLabs `:133`, Deepgram `:168`/`:185` and
Cartesia `:174` sit on send or teardown paths, where swallowing ends no caller-visible sequence.

**Why the suite is green anyway, and why that is the load-bearing part.** Every
`SynthesizeAsync_ShouldAbort_WhenCancelled` test enumerates with `ToListAsync(cts.Token)`. `ToListAsync`
checks that token itself at each iteration boundary, so it throws whether or not the synthesizer does.
The assertion passes over a `yield break` identically to a propagated throw — the test is blind to the
exact distinction the requirement exists to pin. A caller who enumerates with a plain
`await foreach` and no `WithCancellation`, which is the ordinary shape, gets a silently truncated
stream: the same silent-failure shape this ADR was written to retire, arriving through the one door
E6 left open.

**Not decided here.** Which artifact yields is a real choice, not a typo. E6's reasoning — that the
caller asking to stop is not a provider failure — is sound for the *pipeline*, which already handles
cancellation as its own arm; the spec's reasoning — that a truncated stream is indistinguishable
from a complete one unless something throws — is sound for a *library caller*. `IAsyncEnumerable`'s
own convention favours the spec: a cancelled enumeration is expected to throw, and .NET's
`WithCancellation`/`[EnumeratorCancellation]` plumbing exists so it can.

**Acceptance, whichever way it goes.** (a) One of the two artifacts is amended so they agree, by a
new ADR if E6 moves. (b) The three TTS cancellation tests are rewritten to enumerate with a token
that is *not* the cancelled one — passing `cts.Token` to `SynthesizeAsync` and enumerating plainly —
so the assertion targets the synthesizer instead of `ToListAsync`. Until (b) exists, no run of this
suite is evidence about either behaviour, and (a) cannot be verified.

**Resolved 2026-08-19 by [ADR-0052](0052-cancellation-throws-at-the-iteration-boundary.md).** E6 is
the artifact that moved, narrowed to the loops that yield nothing: cancellation is still not a
provider failure — not counted, not wrapped — but it may not end a caller's sequence silently.
Acceptance (a) is met by that ADR; (b) is met by twelve tests rather than the three named above, and
the correction is worth recording. **Three was an undercount and the wrong shape.** Ten cancellation
tests carried the `ToListAsync(cts.Token)` blindness, not three, and all ten turned out to enumerate
*compliant* paths — so rewriting them proved nothing about the defect. The two defective sites,
Speechmatics TTS and LMNT over `LmntTransport.Http`, had **no cancellation test at all**: the
scenario's provider list closes at "(Deepgram, ElevenLabs, Lmnt)" under a normative sentence binding
every synthesizer. The evidence that (a) is real therefore comes from two tests written from nothing
and confirmed red against the defect, not from any test this addendum could name.

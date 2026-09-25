# ADR-0062: A reload is a reconciliation, and an unobserved ending is unknown

- **Status:** Accepted
- **Date:** 2026-09-25
- **Deciders:** Harol A. Reina H.
- **Related:** ADR-0048 (wire conformance is established by a live probe with a negative control,
  never by a green suite — written for provider wire *formats*, and this ADR is the same rule turned
  on a **behaviour**: what Asterisk does with events that occurred while nobody was listening),
  ADR-0060 (turned that same rule on a protocol this repository implements twice; the convergence is
  the argument — see *Why this is ADR-0048's rule again*), ADR-0053 (an ending is classified by who
  ended it — this is that rule in the case where **nobody** ended it observably), ADR-0061 D1 (the
  release tier: a `Fixed — BREAKING` does not force a minor, so this may ship in a patch), ADR-0028
  (the migration-guide obligation that attaches to a minor, and therefore does not attach here),
  ADR-0055 (the version is cut at release time, so a tier ruling binds the release and not this
  diff), ADR-0012 (`Verbara.Sdk.Live` owns domain state and AMI is a data source — why the repair
  belongs on this side of the dependency chain), ADR-0018 (the `SessionReconciliationService` sweep,
  rejected here as the repair), ADR-0023 (`PublicAPI.*.txt`, which is what keeps D5 from adding four
  public members quietly), ADR-0051 and ADR-0043 (the functional lane that carries both measurements
  runs off the PR path), ADR-0005 (Testcontainers is the functional substrate, so "a real Asterisk"
  is a container this repository builds), ADR-0008 (the deterministic AMI reconnection this ADR is
  about). Harvested from the change `a-reconnect-reload-is-a-diff-not-a-wipe`, tasks 1.1, 1.2 and
  3.3.

## Context

An AMI reconnect discards every channel the SDK is tracking and then re-adds the survivors as if
they had just appeared. Nothing is told that the channels went away, so a call that ended during the
outage stays "in progress" for the life of the process, and a call that survived the outage is split
into several calls.

### The mechanism, read line by line

1. `VerbaraServer.OnReconnected` (`src/Verbara.Sdk.Live/Server/VerbaraServer.cs:122`) calls
   `Channels.Clear()` (`:130`) before it reloads.
2. `ChannelManager.Clear()` (`src/Verbara.Sdk.Live/Channels/ChannelManager.cs:229`) empties its two
   dictionaries and raises **no** `ChannelRemoved`. `CallSessionManager` subscribes to that event
   (`src/Verbara.Sdk.Sessions/Manager/CallSessionManager.cs:112`) and therefore hears nothing: its
   sessions are untouched by the wipe.
3. `RequestInitialStateAsync` (`:153`) then re-adds the survivors through
   `ChannelManager.OnNewChannel` (`:163-169`) **without passing the status event's `LinkedId`**,
   although `StatusEvent.LinkedId` exists (`src/Verbara.Sdk.Ami/Events/StatusEvent.cs:19`). Each leg
   defaults to `linkedId = uniqueId` and becomes a call of its own.
4. `OnNewChannel` is unconditionally destructive by construction: it builds a fresh
   `AsteriskChannel` and assigns `_channelsByUniqueId[uniqueId] = channel` (`ChannelManager.cs:77`),
   so a held channel's `LinkedChannel`, `IsOnHold`, `DialedChannel`, `ExtensionHistory` and
   `CreatedAt` are discarded even when the channel is the one already being tracked.

Both registration paths are affected. This is not a clustered-deployment problem.

### What was measured, not inferred

Two regression tests, written against the unfixed code on 2026-09-24 and committed red
(`Tests/Verbara.Sdk.Sessions.FunctionalTests/ReconnectReloadTests.cs`):

| Scenario | Measured on the unfixed code |
|---|---|
| The call hung up during the outage (`Status` returns nothing) | `1 active session [linked=linked-001 state=Connected participants=2]`, **0** `CallEndedEvent` |
| Both legs survived (`Status` returns both, carrying `Linkedid`) | **3** active sessions: the stale original, plus one `Created` session per leg |

The same investigation produced a second finding worth recording, because it explains why the defect
survived: **no test in this repository had ever driven a reconnect and a session at the same time.**
The one pre-existing file that constructs a `VerbaraServer` and lets a reconnect happen
(`Tests/Verbara.Sdk.FunctionalTests/Layer5_Integration/Reconnection/LiveStateRecoveryTests.cs`)
asserts the channel table and never attaches a `CallSessionManager`; every other file that mentions
both sets `AutoReconnect = false`. And the test named for the scenario,
`ReconciliationTests.Reconnection_ShouldCleanSessions_WhenServerReconnects`, substitutes a
detach/re-attach of the session manager for a reconnect — it raises no `Reconnected` at all, and the
shared fixture it runs on never calls `VerbaraServer.StartAsync`, which is the only place
`Reconnected` is subscribed (`VerbaraServer.cs:91`). A suite can name a behaviour it cannot reach.

### What Asterisk does with events nobody was listening for, measured

The repair rests on one premise about Asterisk: that the reload is the **last** notification a
reconnect-lost call will ever produce. If Asterisk replayed the `Hangup` events emitted while the
AMI connection was down, the ending would arrive on its own and the requirement would have to
narrow. The premise was measured rather than assumed (change task 3.3, 2026-09-24):

- **Asterisk replays nothing.** A witness AMI session, connected directly to Asterisk and never cut,
  observed all four `Hangup` events during the outage — proving Asterisk emitted them. The
  reconnected session under test received exactly two packets, the banner and login response, plus
  one `FullyBooted` carrying no channel information, and **zero** `Hangup` events in a 20 s window.
  The `Status` that followed returned `ListItems: 0`. Both outage shapes were driven, because
  Toxiproxy documents its `timeout` toxic as delaying rather than closing: with `timeout(0)` on both
  streams the socket reported EOF the instant the toxics were removed, so the stall variant produces
  no proxy-buffered replay either.
- **`Linkedid` is present, non-empty and identical across every leg of one call.** Measured on a
  real bridge with `BridgeID` populated as well as on a simple two-leg pair, and the `Status` header
  set is byte-for-byte identical across the four versions.

Measured on **Asterisk 18.26.4, 20.20.1, 22.9.0 and 23.4.1**, and the scope of the negative is
stated as narrowly as the measurement was:

- **Raw TCP AMI transport only.** AMI over HTTP keeps a server-side session queue and was **not**
  measured, because the SDK does not use that transport.
- **`Local` channels only.** The functional dialplan has no PJSIP endpoint that answers without
  SIPp, so no other channel technology was exercised.

**The witness session is what makes the negative a measurement rather than an absence, and that is
the reusable part of this record.** "No `Hangup` arrived" is compatible with two very different
worlds: Asterisk did not send one, or the observer could not hear one. A second session that saw the
four `Hangup` events live separates them. The kept test,
`ReloadPremiseTests.Reconnect_ShouldNotReplayTheMissedHangups_WhenTheCallEndedDuringTheOutage`,
carries the same idea forward as a positive control on the other side of the reconnect: after
asserting that no missed `Hangup` was replayed, it originates a second call, hangs it up while the
link is **up**, and requires the post-reconnect subscription to deliver that `Hangup`. Without it, a
deaf observer would make "Asterisk replayed nothing" pass for the wrong reason. Both tests
re-measure against whichever version the container fixture builds, so a future Asterisk that starts
replaying turns a test red instead of silently invalidating a requirement.

### The reload reads two headers no supported version sends

The same task measured the `Status` frame itself, and it contradicted the code.
`RequestInitialStateAsync` reads `se.State` (`VerbaraServer.cs:162`) and `se.CallerId` (`:167`).
**No `Status` frame on any supported version carries a `State:` or a `CallerID:` header** — not
18.26.4, not 20.20.1, not 22.9.0, not 23.4.1. What every version does carry is `ChannelState`
(numeric) with `ChannelStateDesc` (text), and `CallerIDNum` with `CallerIDName`. Every reloaded
channel therefore lands as `ChannelState.Unknown` with a null caller id, today, on every supported
version — not occasionally, and not on some versions.

This is a defect of `StatusEvent` before it is a defect of the reload. `ChannelEventBase` declares
`ChannelState`/`ChannelStateDesc`/`CallerIdNum`/`CallerIdName` for every other channel-bearing
event; `StatusEvent` extends `ResponseEvent` and never got them, which is why it has a `State` and a
`CallerId` that nothing on the wire populates. Both of those are in
`src/Verbara.Sdk.Ami/PublicAPI.Shipped.txt`, so the shape affects every consumer that reads
`StatusEvent` directly, not only the reload.

### A restart multiplies calls exactly as a reconnect does

`RequestInitialStateAsync` serves the **initial** load as well as the post-reconnect reload, and it
drops `StatusEvent.LinkedId` through the same three lines in both cases. So two bridged legs of one
live call, which Asterisk reports with a shared `Linkedid`, become **two** sessions on a first load
today and become one after D4. A process restart during live traffic multiplies sessions exactly as
a reconnect does, and D4 fixes both. This is written down because the first wording of the change's
own design said the opposite — that a diff against an empty table behaves exactly as today's load —
and the measurement corrected it: that is true of the channel table and false of the session table.

### Why this is ADR-0048's rule again

ADR-0048 says a claim about a third party's wire behaviour is established against that third party,
by a live probe carrying a negative control, and never by a green suite driving our own fixtures.
ADR-0060 turned that rule on a protocol this repository implements twice, and found that neither
implementation had ever completed a handshake while both suites stayed green for six months, because
every test built its input with the codec under test.

This ADR turns the same rule one step further out: not on a format, but on a **behaviour** — what
Asterisk does with events that occurred while nobody was listening. A fake that is asked to replay
nothing will replay nothing, and a fake that is asked to replay will replay; either way the suite
agrees with whatever the author believed. The premise is only knowable from an instance of the thing
that decides it, and it needs a control for the same reason a route claim does. Three different
layers — a provider's API, a protocol's frame, an implementation's event-retention behaviour — and
one failure mode. That convergence, not the individual finding, is the strongest form of the
argument for keeping the rule.

## Decision

**D1 — The reload is buffered, then reconciled; it is not streamed into a cleared table.** The
snapshot is read into a local set first, and only a snapshot that was read to completion is applied.
Reconciliation then compares it against what is held.

Buffering is what makes the difference knowable: "absent from the snapshot" cannot be computed while
streaming into the structure being compared against. It also delivers "a reload that cannot be
trusted ends nothing" structurally rather than by care — if the enumeration throws, the buffer is
discarded and nothing was mutated. `OnReconnected` already wraps everything in a `try`/`catch` that
swallows into a log line (`VerbaraServer.cs:143-146`), so a failure path that mutates nothing is the
only one that stays safe under it.

*Rejected: mark-and-sweep in place* — tag every held channel, clear tags as the snapshot arrives,
remove the still-tagged ones at the end. It avoids the buffer and leaves the table half-updated if
the enumeration fails midway, which is exactly the failure direction this change forbids.

**D2 — Reconciliation lives in `ChannelManager`, not in `VerbaraServer`.** `ChannelManager` gains a
reconcile entry point that takes the snapshot and raises `ChannelAdded` / `ChannelRemoved` as the
difference requires. `OnReconnected` stops calling `Channels.Clear()` and hands the snapshot over
instead. `Clear()` stays public and untouched; this change removes one of its callers, not the
method.

The table and its events are one thing. A diff computed in `VerbaraServer` would have to reach into
the manager's state to learn what is held and then ask it to raise events for entries it did not
decide — two components sharing one invariant. `ChannelManager` already owns both halves.

*Rejected: leave the diff in `VerbaraServer` and add a public removal-raising method to
`ChannelManager`.* A wider public surface for a narrower benefit, in a repository whose public API
is a contract with two downstream consumers.

**D3 — A reload-produced ending carries a marker and no hangup cause.** Ruled by the owner on
2026-09-24. A call ended because the reload proved it gone is marked as such, and its departing
participants are left **without** a hangup cause rather than being given `NotDefined`.

The reason matters more than the rule. `NotDefined` is not neutral downstream. Today
`CallSessionManager.OnChannelRemoved` derives the ending entirely from `channel.HangupCause`
(`CallSessionManager.cs:218-220`), and `AsteriskChannel.HangupCause` is a non-nullable enum whose
default is `NotDefined = 0` (`ChannelManager.cs:252`, `src/Verbara.Sdk/Enums/HangupCause.cs:8`), so
a reload-driven removal that says nothing says `Failed` with cause zero. A consumer classifier that
treats anything other than `NormalClearing` as an abnormal ending would then read **every**
reconnect-lost call as an abnormal hangup and act on it — and in a contact-center product built on
this SDK, that path ends in an automated call back to the customer. Asserting `NormalClearing`
instead would be the opposite lie: it records as clean an ending nobody observed. **The marker is
the only option that does not claim knowledge the SDK does not have.**

*Consequence for the implementation:* the reload-driven removal must reach `OnChannelRemoved`
carrying "no cause", distinct from "cause zero", and the resulting session state must follow from
what the session already was rather than from a cause that does not exist. The published surface
already admits this: `CallEndedEvent.Cause` and `SessionParticipant.HangupCause` are both
`HangupCause?` in `PublicAPI.Shipped.txt`, so "unknown" has a spelling that needs no new public
member.

*Rejected: end as `Failed` with `NotDefined`* — the cheapest change, and the one that produces the
spurious-callback path above. *Rejected: end as `Completed` with `NormalClearing`* — silent and
tidy, and it records an unobserved ending as normal.

**D4 — The reload passes the correlation identifier it already receives.**
`RequestInitialStateAsync` passes `StatusEvent.LinkedId` to `OnNewChannel`, and a channel already
held is reconciled rather than re-added as new. The value is already on the wire and already parsed
by this SDK's own parser; not passing it is the whole reason one call becomes three. This is the
smallest half of the change and the one with the clearest evidence.

The residual this decision was written with — that some supported version might not populate
`Linkedid` — is **closed, favourably**, by the measurement above: present, non-empty and identical
across every leg on all four versions, with the header set byte-for-byte identical. So D4 is
implementable exactly as written, with no mapping work first and no version needing a fallback.

**The requirement "a reload without correlation does not invent calls" stays anyway, and it is now a
defensive path with no measured triggering version among 18, 20, 22 and 23** — not a workaround for
a known version difference. It costs nothing (an empty `Linkedid` degrades to today's
`linkedId = uniqueId`) and it covers what the measurement did not reach: `Local` channels were the
only technology exercised. That sentence is here so a later reader does not reopen the question
hunting for the version that drops the header.

*Rejected: hold the correlation identifier in a side table keyed by unique id.* Two structures with
one invariant, when `AsteriskChannel.LinkedId` already exists and the wire already supplies the
value. Note that `LinkedId` is `{ get; init; }` (`ChannelManager.cs:253`), so it **cannot** be
mutated on a held channel — which is a constraint on how reconciliation preserves identity, not a
reason to duplicate the field.

**D5 — The reload reads the headers Asterisk sends, through `RawFields`, and adds no public API.**
`RequestInitialStateAsync` reads `ChannelStateDesc` (or the numeric `ChannelState`) and
`CallerIDNum` from `se.RawFields` — the same route `Context` already travels at
`VerbaraServer.cs:168` — instead of `se.State` and `se.CallerId`, which no supported version
populates.

The enum needs no work: `ChannelState` maps 1:1 onto Asterisk's numeric header (`Down = 0` …
`Up = 6` … `PreRing = 9`) and the measured frame carries both `ChannelState: 6` and
`ChannelStateDesc: Up`, so `Enum.TryParse` resolves either spelling. The bug was never the parse; it
was the field handed to it.

*Why not give `StatusEvent` the four properties it is missing:* they are a real defect, and a
different one. It affects every consumer reading `StatusEvent` directly, so it belongs to
`Verbara.Sdk.Ami` and to its own change. Fixing it here would add four public members — an addition,
not a break, but still a move under ADR-0023 — and would falsify this change's own claim that no
public API moves, for a benefit the reload does not need: `RawFields` already carries the values,
verified on the wire. Removing the two dead properties is a separate and harder question, because
removing published surface is `CP0002`.

*Failure direction:* a channel for which Asterisk sends no state header at all still defaults to
`Unknown` and is still admitted. Defaulting is correct when a value is genuinely absent; what was
wrong was defaulting a value that could never arrive.

## Consequences

- **A call the reload proves is gone now ends, through the path consumers already observe.**
  `OnChannelRemoved` → `OnSessionCompleted` → `CallEndedEvent` (`CallSessionManager.cs:403`) is the
  only ending a consumer of this SDK has ever been able to see. A repair that marked internal state
  and stopped there would be invisible to every consumer already written against it, which is why
  the ending travels the existing route rather than a new signal nobody subscribes to.
- **This is a behavioural break in two directions, and both are `Fixed`.** A consumer that today
  sees a session stay connected for the life of the process will now see it end; a consumer counting
  calls across a reconnect will see a different, lower, correct number. Neither behaviour was ever
  promised: holding a stranded session forever is not a contract anyone chose, and `LinkedId`
  correlation is this SDK's declared design.
- **A consumer may have grown a workaround that now fires alongside the real ending** — its own
  timeout over a session that never ended, for instance. That is the honest cost of the fix, and it
  is called out as `BREAKING` in the CHANGELOG rather than left for a consumer to discover.
- **The failure direction is a contract, not implementation carefulness.** A reload that fails, is
  interrupted, or cannot be shown to have completed ends nothing, and a test binds it. Ending a live
  call by mistake is worse than the defect being repaired, so the direction is stated as a
  requirement that a mutation turns red.
- **Nothing is added to or removed from the public API.** `ChannelManager.Clear()` stays public;
  `CallEndedEvent.Cause` and `SessionParticipant.HangupCause` were already nullable. The change is
  to who calls `Clear()`, what the reload does around it, and which headers it reads.
- **The four other live managers keep their clear-and-reload.** `Queues`, `Agents`, `MeetMe` and
  `Bridges` hold no session identity and nothing subscribes to their removals for lifecycle
  purposes. Recorded so a later reader sees a boundary rather than an oversight.
- **The downstream consumers inherit the repair without a code change**, which is why it belongs on
  this side of the dependency chain (ADR-0012): `Verbara.Sdk.Live` owns the domain state, AMI is a
  data source, and the defect is in the owner.
- **Buffering the snapshot costs memory proportional to the estate for the duration of the reload.**
  Bounded by what the reload already materialises event by event, so the delta is the retained set,
  not the stream.
- **Two premises of this ADR are now bound by tests that re-measure them**, not by prose: the replay
  negative and the `Linkedid` positive both run against whichever Asterisk the container fixture
  builds. They run in the merge queue and on the scheduled matrix, not on every push (ADR-0051,
  ADR-0043), so a version change is caught on the train rather than on the PR.
- **`StatusEvent`'s missing properties are left unrepaired, deliberately, and they have no open
  change of their own.** So are two other findings the investigation produced: whether the session
  table and the in-memory store grow without bound, and how long a healthy inbound call legitimately
  sits in `Created`. Each is recorded in the change's `tasks.md` section 4 with the reason it is not
  here. A finding deferred without a home is how the previous one was lost.
- **One existing functional assertion becomes right for the wrong reason.**
  `LiveStateRecoveryTests.ChannelManager_ShouldClearOnReconnect` asserts an empty channel table
  after a reconnect and states the wipe as the mechanism — "only the clear on reconnect can remove
  the channels" (`LiveStateRecoveryTests.cs:129-132`, the file named above). Its assertion survives
  the fix — an empty snapshot still empties the table — so it is not a red test waiting to happen;
  it is a green test whose stated reason this ADR falsifies, and whose name advertises the banned
  mechanism rather than the guarantee.

## The release tier, already ruled

Under **ADR-0061 D1** only a `Changed — BREAKING` entry forces a minor; a release whose breaking
entries are all `Fixed — BREAKING` may ship as a patch. This change's entry is a `Fixed — BREAKING`
under ADR-0061 D3: the SDK never promised to hold a stranded session for the life of the process,
nor to turn one call into several, so the entry restores documented behaviour rather than
withdrawing a kept promise. **It therefore does not force a minor, and it may ship in a patch.**

ADR-0028's migration-guide obligation attaches to a minor that carries a breaking change, so it does
**not** attach here — which is why this change's migration plan says there is nothing for a consumer
to migrate: no public API moves, no persisted shape changes, and the ending arrives on the event the
consumer already handles. A consumer that wants to distinguish a reload-produced ending reads D3's
marker; one that does not care needs no change. Rollback is a revert.

Per ADR-0055 the version is cut at release time, so this ruling binds the release that carries the
entry and changes nothing in the diff that introduces it. It is recorded here so it is not
re-litigated: the question was asked and answered on 2026-09-24.

## Alternatives considered

**Register `SessionReconciliationService` on the registrations that lack it, and let its sweep clean
up after the reconnect.** Rejected, with the measurement as the argument. The reloaded legs land in
`Created`, and `Created` past `DialingTimeout` (60 s) is exactly what that service's orphan branch
marks `Failed` (`src/Verbara.Sdk.Hosting/SessionReconciliationService.cs:72-74` →
`SessionReconciler.TryMarkOrphaned`, `src/Verbara.Sdk.Sessions/Manager/SessionReconciler.cs:11`;
ADR-0018). Registering the sweep where it does not run today would therefore mark **healthy calls
dead after every reconnect** — a worse outcome than the defect, delivered by the component that
looks like the repair. The measurement that would make a clock-based sweep safe (how long a healthy
inbound call legitimately sits in `Created`) is a Platform measurement and is owed only if anyone
reopens this.

**Raise `ChannelRemoved` from `Clear()` itself.** Rejected. `Clear()` is a public method with other
callers and no snapshot to compare against; making it announce removals would turn every legitimate
reset into a wave of endings, and it still could not tell a channel that is gone from a channel that
is about to be re-added a millisecond later. The comparison, not the clearing, is what carries the
information.

**Fix the correlation (D4) and leave the wipe alone.** Rejected, and it is worth stating why the
cheaper half is not enough: it repairs the "one call becomes three" measurement and leaves the "a
dead call stays connected forever" measurement exactly as it is. The two findings have one cause in
the same three lines, and shipping half of it would leave the stranded session as the residual with
nothing recording that it was seen.

**Leave the behaviour and document it.** Rejected. A session that never ends is not a documentable
contract; it is an unbounded leak of state in the consumer, and the reload is the only moment at
which the SDK will ever know.

**Take the replay premise from the documentation.** Rejected on ADR-0048 D4's rule, applied
verbatim: a vendor asserting X is evidence, a vendor not mentioning Y is not. Whatever Asterisk's
documentation does or does not say about retaining events across a manager session, a silence
licenses nothing — and no such retention was looked for there, because the answer would not have
counted. The witness session is the evidence; the absence of a documented queue would not have
been.

**Use a fake connection to establish that nothing is replayed.** Rejected — this is ADR-0060 R3 one
layer out. A fake replays exactly what its author told it to, so a suite built on one agrees with
whatever was believed about Asterisk and cannot fail for the premise it appears to cover. The
premise is a claim about Asterisk, so only Asterisk can settle it.

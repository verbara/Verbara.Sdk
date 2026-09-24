---
tier: MEDIANO
owner: Harol
approver: Harol
stakeholder: Anyone reading this SDK's audio documentation, metrics or functional results as a statement of what works — and the Pro AgentAssist deployment that consumes the ARI audio path
decision_ref: Sdk/ADR-0060
---

# Proposal: a-published-surface-is-one-something-measures

## Why

ADR-0060 recorded two residual defects with the words "tracked separately" and no link, and the
close-out archived it anyway. `externalmedia-returns-a-channel-id-that-finds-its-stream` exists
because of that, fixes the third defect, and hands three more findings forward. This is the change
that carries them, opened before that one archives rather than after, because
`openspec/config.yaml` requires a deferred finding to land in an open change and a label such as
*tracked separately* with no link does not exempt it.

The three are not a grab-bag. They are one failure repeated on three different kinds of surface:

> **The repository publishes a claim, nothing connects the claim to the code, and the test that
> should tell the difference would stay green if the code were deleted.**

- The **documentation** claims `WebSocketAudioServer` is keyed by a channel id. It is keyed by a URL
  path segment, and nothing has ever measured what Asterisk puts in that path.
- The **metrics surface** claims ten instruments' worth of audio observability. Nothing under `src/`
  emits any of them. The tests emit the measurements themselves and then assert they were observed.
- The **functional suite** claims to exercise ConfBridge, Parking, Transfer, DTMF, CDR and Stasis.
  Ten of the extensions it dials are not defined in the context it dials them in, and the tests
  return without asserting when the call does not happen.

Every number below was measured in this worktree with `git grep` and by reading the files, because
this change's parent measured that the session's default `grep` honours `.gitignore` and undercounts.
Where a measurement contradicts the note that handed the finding over, the correction is stated here
rather than quietly absorbed.

### F1 — `WebSocketAudioServer` keys on a URL path segment, and nobody has measured that path

The key is computed at `src/Verbara.Sdk.Ari/Audio/WebSocketAudioServer.cs:341`:

```csharp
var channelId = path.TrimStart('/').Split('/').LastOrDefault()?.Split('?').FirstOrDefault();
```

and registered at `:266` (`_streams.TryAdd(channelId, session)`). So the table's key is whatever the
HTTP upgrade request's last path segment happens to be. This SDK's own example puts a literal there:

```text
Examples/WebSocketMediaExample/Program.cs:10  //    same => n,WebSocket(ws://127.0.0.1:9093/audio,slin16)
Examples/WebSocketMediaExample/Program.cs:78  Console.WriteLine("Configure Asterisk ... WebSocket(ws://host:9093/audio,slin16) ...");
Examples/WebSocketMediaExample/README.md:17    same => n,WebSocket(ws://127.0.0.1:9093/audio,slin16)
```

Follow that example and every concurrent call registers under the key `"audio"`: the second
connection's `TryAdd` fails, the second stream is never in the table, and `ActiveStreamCount`
disagrees with reality. `CompositeAudioServer.GetStream` hands one string to both servers, which do
not agree on what a key means — its own `<remarks>` now says so, which is a warning, not a fix.

**The first task of this change is a probe, not a design.** Nothing in this repository has measured
what Asterisk actually puts in that request path, and there are two candidate producers that the code
does not distinguish between:

- `WebSocketAudioServer`'s class summary says *"Listens for incoming WebSocket connections from
  Asterisk ExternalMedia channels"* (`:34`) — the ARI `externalMedia` endpoint with
  `transport=websocket`;
- the example and `ChanWebSocketControlMessage` / `IChanWebSocketSession` describe the `WebSocket()`
  dialplan application of `chan_websocket`, an entirely different Asterisk feature that this repo also
  supports.

Those two put different things on the wire, and `AudioChannelVars` already names a third candidate
identifier for the second of them — `WEBSOCKET_GUID` — that the server never reads. Designing a fix
from a reading of the code is precisely how the AudioSocket wire format survived six months of green
tests. The probe comes first and its capture is committed beside this change, in the shape
`probe-capture.txt` takes for its parent.

Three pieces of prose in the same file assert the identity the code does not hold, all of them
carried over rather than fixed by the parent change's B3, which deliberately stopped at published
contracts:

- `:312` — "extract Sec-WebSocket-Key and channel ID from URL path";
- `:313` — "Expected URL: `/ws/{channelId}` or `/{channelId}`", naming the placeholder `{channelId}`;
- `:34` — the class summary's "from Asterisk ExternalMedia channels", which may simply be false.

And incidentally, `:35` cites "(ADR-1)" for the TcpListener + manual-upgrade design. This repository's
ADRs are four digits and `ADR-0001` is *Native AOT first*, which is not that decision. It is a
citation to nothing, in the one file this change opens anyway.

**Corrections to the note that handed F1 over:** the key is computed at `:341`, not `:337`, and
registered at `:266`, not `:262`. The literal `/audio` appears in three places, not one.

### F2 — ten instruments, zero production call sites

`src/Verbara.Sdk.Ari/Diagnostics/AudioStreamMetrics.cs` declares a `Meter` and ten instruments:
`StreamsOpened`, `StreamsClosed`, `FramesReceived`, `FramesSent`, `BytesReceived`, `BytesSent`,
`BufferUnderruns`, `HangupFrames`, `ErrorFrames`, `FrameLatency`. Verified with `git grep`, every
reference to the type outside its own declaration is one of:

- `Tests/Verbara.Sdk.Ari.Tests/Diagnostics/AudioStreamMetricsTests.cs` — 13 references;
- `src/Verbara.Sdk.Ari/PublicAPI.Shipped.txt` — 12 rows;
- the two OpenSpec documents that record the finding.

`git grep -n "AudioStreamMetrics\.<name>"` returns exactly two hits for nine of the ten instruments
and three for `StreamsClosed` — in every case the `PublicAPI.Shipped.txt` row plus the test file, and
never a call site under `src/`. The declaration itself does not appear in that count, because it does
not use the qualified name; there is nothing else to count. The class-level doc tells a reader to run `dotnet-counters monitor --process-id <pid> Verbara.Sdk.Ari.Audio`
and watch. They will watch nothing move, for the lifetime of any process.

**Its test file is the closed loop in miniature, which is why this was never caught.** Eleven `[Fact]`s:
six assert only `.Should().NotBeNull()` on a `static readonly` field initialised inline — which cannot
be null and would be a compile error if it could; four construct a `MeterListener`, call
`AudioStreamMetrics.X.Add(1)` *from the test itself*, and assert the listener observed 1; one asserts
the meter's name. Delete every production call site and all eleven stay green. That is not a
hypothetical: every production call site is already deleted, and they are green today.

The fix is a decision, not a foregone conclusion, and this change makes the decision explicitly:
**wire the instruments to the sessions that own the events, or remove them.** Removal is breaking —
twelve rows in `PublicAPI.Shipped.txt`, `CP0002`/`CP0006`, a suppressions entry, a migration note.
Wiring is not breaking and is the likelier answer, but it is the *answer to a question*, and asking it
is the task.

### F3 — the functional suite passes on calls that never happened

`docker/functional/asterisk-config/extensions.conf` defines nine extensions in `[test-functional]`:
100, 150, 155, 500, 600, 710, 711, 900, 950. There is no pattern-match extension in that context — the
`_X.` catch-alls live in `[default]`, `[stasis-test]` and `[queue-test]` — so an extension that is not
listed is not reachable.

The suite dials seventeen distinct extensions into that context, in **two** forms that a single grep
does not both catch:

| form | distinct extensions |
|---|---|
| `Channel = "Local/N@test-functional"` | 100, 150, 155, 160, 300, 500, 600, 700, 900, 950, 999, 9998 |
| `Exten = "N"` with `Context = "test-functional"` (Originate and Redirect) | 100, 150, 155, 160, 161, 162, 163, 500, 750, 950, 999, 9998, 9999 |

Subtracting the nine defined leaves **ten** undefined, not five:
**160, 161, 162, 163, 300, 700, 750, 999, 9998, 9999.** The note that handed this finding over said
five, and so does the comment already sitting in the dialplan at `:111`; both were derived from the
`Local/N@` form alone. `Exten =` appears 53 times in the functional suite and every one of those 53
sites carries `Context = "test-functional"`, so the second form is not an edge case.

**What keeps it green is an early return, and it has more than one spelling.**
`Tests/Verbara.Sdk.FunctionalTests/Layer5_Integration/ConfBridge/ConfBridgeAdvancedTests.cs` dials the
undefined 700 from ten call sites across eight `[Fact]`s, and every one of the eight ends in an
unasserting return written three different ways:

```text
:55, :104, :162   if (confJoin is null) return;
:211, :273        if (!joinEvents.Any(e => e.Conference == confName)) return;
:339, :385, :445  if (joinEvents.IsEmpty) return;
```

So the shape to hunt is the early return, and hunting one spelling of it finds three of eight tests.
The parent change's own dialplan comment (`extensions.conf:104-111`) says *"those five tests"* and
names one spelling; measured, it is eight tests and three spellings. That comment also ends *"the hole
is filed separately"* and links nothing — the ADR-0060 failure reproduced, in the very commit that
was fixing ADR-0060's failure. This change is the link.

**The shape is not confined to ConfBridge.** A brace-tracking scan of the functional suite for a bare
`return;` in a test-method body — excluding returns inside lambdas and observer callbacks — reports
**71 candidate sites across 11 files**. Hand-checked samples confirm the shape outside ConfBridge:

```csharp
// Tests/Verbara.Sdk.FunctionalTests/Layer5_Integration/Parking/ParkingTests.cs:61-65
var channelResult = await Task.WhenAny(originatedChannel.Task, Task.Delay(TimeSpan.FromSeconds(10)));
if (channelResult != originatedChannel.Task)
{
    // Channel did not appear — skip gracefully
    return;
}
```

`ParkingTests` redirects to the undefined 750 from four sites, so "skip gracefully" is its normal path,
not its safety net. `DtmfDetectionTests:123` and `AriStasisTests:101,:111` carry the same construction.
The scan is a **candidate list and not a verdict**: `BridgeLifecycleTests:315` is a false positive —
a legitimate event filter inside a `ConfbridgeJoinObserver` lambda. Producing the true count by hand is
a task of this change, not a claim of this proposal.

## What Changes

**F1 — measure, then decide.**
- A probe, run against both Asterisk images the lane builds, capturing the literal HTTP request line
  Asterisk sends for (a) `externalMedia` with `transport=websocket` and (b) the `WebSocket()` dialplan
  application, plus the value of `WEBSOCKET_GUID` where one exists. The capture is committed.
- Only then: either the server keys on a measured identifier, or its contract says plainly that it is
  not addressable by any Asterisk id and `CompositeAudioServer`'s ambiguity is resolved by one of them
  refusing the call.
- The three wrong sentences at `WebSocketAudioServer.cs:34`, `:312`, `:313` are corrected against the
  capture, and the dangling "(ADR-1)" at `:35` is resolved or removed.

**F2 — wire the instruments or remove them.** One decision, recorded, with the public-API cost of the
removal branch priced before it is chosen. Whichever branch is taken, the test file is rewritten so it
fails when the production call sites are removed — a test that supplies its own measurement is not
evidence that anything is instrumented.

**F3 — define, assert, and sweep.**
- Every extension the suite dials into `[test-functional]` is defined there, or the test that dials it
  is changed to dial one that is.
- The unasserting early return is replaced by a failing assertion with a reason, everywhere the sweep
  finds it — and the sweep produces a hand-verified count, not the heuristic's 71.
- A guard so the class cannot come back: the suite's dialled set and the dialplan's defined set are
  reconciled by something that runs, rather than by a comment in a `.conf` file.

## Decision: one change, with a written split condition

These are three findings on three different parts of the tree, and the honest answer is not obvious.
Both readings are set out here, and the change picks one with a condition attached rather than leaving
the choice to whoever opens the file next.

**For one change.** They are one failure family, and the family is the point. Splitting them into three
tickets produces three fixes and loses the sentence that connects them — which is the sentence a
reviewer needs in order to catch the fourth instance. `openspec/config.yaml` requires the harvest to
land in *an* open change; one change satisfies it and names the family once. And the parent change
already paid for the discovery; re-deriving the shared thesis three times is waste.

**Against one change.** F1 blocks on a measurement nobody has taken and may end in a breaking API
change; F2 is a shipped-public-API decision; F3 touches no shipped surface at all. Binding two findings
that are ready to fix today behind an open-ended probe is the *tracked separately* failure in a new
costume: a change that cannot start is not much better than prose that nobody links.

**The decision: one change, ordered F3 → F2 → F1, with this split condition written into `tasks.md`.**
If the F1 probe has not produced a committed capture after one honest attempt against both Asterisk
images the lane builds, F1 splits into its own change — carrying the probe task verbatim — and this
change archives with F2 and F3 complete. The condition is concrete enough to be checked rather than
argued, which is what the parent change's post-mortem asked for. The ordering is deliberate: the two
findings with no unknowns land first, so the unknown cannot hold them.

## Impact

- **F1:** `src/Verbara.Sdk.Ari/Audio/WebSocketAudioServer.cs`, `Audio/CompositeAudioServer.cs`,
  `Examples/WebSocketMediaExample/`, and the `IAudioServer` / `IAudioStream` contract text in
  `src/Verbara.Sdk/IAriClient.cs` that the parent change has just rewritten. A new probe and its
  capture. Whether any shipped signature moves is the probe's to decide; if it does, the cost is the
  parent change's cost again — `CP0002`, `PublicAPI` rows, a suppressions entry, a migration note.
- **F2:** `src/Verbara.Sdk.Ari/Diagnostics/AudioStreamMetrics.cs` and the sessions that would emit —
  `Audio/AudioSocketSession.cs`, `Audio/WebSocketAudioSession.cs`. **Potentially breaking**: the removal
  branch drops twelve rows from `src/Verbara.Sdk.Ari/PublicAPI.Shipped.txt` and takes `CP0002`. The
  wiring branch is additive and takes none. `Tests/Verbara.Sdk.Ari.Tests/Diagnostics/AudioStreamMetricsTests.cs`
  is rewritten either way.
- **F3:** `docker/functional/asterisk-config/extensions.conf` and the functional suite. No shipped
  surface. **Expect new red.** The parent change measured that defining 700 makes the originate succeed
  and the ConfBridge test bodies then fail; that red belongs to this change and is the reason the
  parent renumbered to 710/711 rather than carrying it. Budget for it in the task, rather than
  discovering it.
- **CI:** F3's work is invisible on a `pull_request` without the `ci:functional` label (ADR-0051), and
  only `merge_group` runs both Asterisk versions. A sixteen-second green functional job means no
  Asterisk started. F1's probe depends on the same images.
- **Depends on** `externalmedia-returns-a-channel-id-that-finds-its-stream`: its delta creates the
  `external-media-stream-routing` capability and explicitly excludes the WebSocket transport "until its
  key has been measured". F1's delta adds to that capability and removes the exclusion, so this change
  archives after its parent.

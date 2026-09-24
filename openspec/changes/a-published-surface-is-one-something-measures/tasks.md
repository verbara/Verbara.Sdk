# Tasks: a-published-surface-is-one-something-measures

Three phases. A batched, B one focused subagent per task, C batched. Never inline in the main session.

**Order is F3 → F2 → F1, deliberately.** F3 and F2 have no unknowns; F1 blocks on a measurement nobody
has taken. The two that can finish are not held behind the one that might not — see the split gate at
the end of Phase A.

Two findings here are bug fixes, so **the failing assertion is written first, against the unfixed
tree, and its failure is pasted here verbatim.** A task is not checked off until the thing it claims
actually ran. Where a measurement contradicts this file, the correction is written here rather than
quietly absorbed — this change exists because three documents in a row asserted things nobody had
measured.

## Stale-green traps already measured in this worktree

Every one of these produced a green number that meant nothing. Respect all six.

1. The session's default `grep` honours `.gitignore`; tracked files that were force-added are missed.
   **Any "every occurrence" claim uses `git grep`.**
2. `mv` preserves mtime, MSBuild then skips the rebuild, and the run reports a green number **from the
   previous assembly**. `touch` anything restored, before building.
3. `dotnet pack` prints nothing to a redirected stdout on success — not even with `-tl:off -v m`. A
   zero-line log with exit 0 is indistinguishable from a run that did nothing.
4. `PackageValidation` is incremental. Delete the ApiCompat semaphores **and** the nupkgs, or a repeat
   pack validates nothing and reports success. Deleting only the semaphores skips packaging entirely.
5. On a `pull_request` the functional job short-circuits without the `ci:functional` label, and only
   `merge_group` runs both Asterisk versions. **A sixteen-second functional "pass" started no Asterisk.**
6. An unasserting early return and a real pass are reported identically. That is this change's subject
   and it applies to this change's own verification.

## Phase A — measure, before any edit (batched)

- [ ] A1 **F3: sweep the functional suite for the unasserting exit, and hand-verify every hit.** A
      brace-tracking scan for a bare `return;` in a test-method body reports **71 candidate sites
      across 11 files**; it cannot tell a test-body exit from a filter inside an event-observer
      lambda, and at least one hit is the latter
      (`Tests/Verbara.Sdk.FunctionalTests/Layer5_Integration/Bridge/BridgeLifecycleTests.cs:315`,
      inside a `ConfbridgeJoinObserver`, which is correct as written). Read every candidate and
      produce a verified list: file, line, which test, and whether the exit is on the normal path or
      a genuine filter. **Cover all four spellings**, not the one the hand-over note named —
      `x is null`, `collection.IsEmpty`, `!collection.Any(pred)`, and
      `await Task.WhenAny(t, Task.Delay(...)) != t`. Paste the verified count and say how it differs
      from 71.
- [ ] A2 **F3: reconcile the dialled set against the defined set, from the running container.**
      `docker/functional/asterisk-config/extensions.conf` defines nine extensions in
      `[test-functional]` — 100, 150, 155, 500, 600, 710, 711, 900, 950 — and that context has **no**
      pattern-match extension (the `_X.` catch-alls are in `[default]`, `[stasis-test]` and
      `[queue-test]`), so an unlisted number is unreachable. Static analysis of the suite says
      seventeen distinct extensions are dialled, in **two** forms that one grep does not both catch:
      a `Local/N@test-functional` channel string, and an `Exten = "N"` paired with
      `Context = "test-functional"` on an Originate or a Redirect — the second form appears 53 times
      and every one of them carries that context. The difference leaves **ten** undefined:
      **160, 161, 162, 163, 300, 700, 750, 999, 9998, 9999.**
      The hand-over note and the comment already in the dialplan at `:111` both say five; both were
      derived from the first form alone.
      **Do not stop at the static count.** Bring the container up and settle it with
      `dialplan show test-functional`, because a module can register an extension a `.conf` file does
      not list — `750` is a parking extension and `res_parking.conf` is in this tree, so it is the
      obvious candidate for a number that is dialled, absent from `extensions.conf`, and reachable
      anyway. Paste the `dialplan show` output and the final undefined set.
- [ ] A3 **F2: decide wire-or-remove, with the removal branch's cost measured rather than estimated.**
      `src/Verbara.Sdk.Ari/Diagnostics/AudioStreamMetrics.cs` declares ten instruments. Confirm with
      `git grep` that every reference outside the declaration is in
      `Tests/Verbara.Sdk.Ari.Tests/Diagnostics/AudioStreamMetricsTests.cs` (13) or
      `src/Verbara.Sdk.Ari/PublicAPI.Shipped.txt` (12) — nine instruments return exactly two hits for
      `git grep -n "AudioStreamMetrics\.<name>"` and `StreamsClosed` returns three. In every case the
      two are the API-tracker row and the test file; the declaration does not match that pattern
      because it does not use the qualified name, so there is no call site under `src/` to miss.
      Then price removal for real: drop the twelve `PublicAPI.Shipped.txt` rows on a scratch branch
      and run `dotnet pack -c Release` with validation **forced** (trap 4), and paste the exact
      ApiCompat codes. Compare against wiring — which sessions would emit which instrument, and
      whether any of them can reach the data the instrument names. Record the decision and the reason.
      Note that `Verbara.Sdk.Ari.Audio` is named in the class doc as something a reader can watch with
      `dotnet-counters`; whichever branch wins, that sentence becomes true or goes.
- [ ] A4 **F1: probe what Asterisk actually puts in the WebSocket upgrade request. SPLIT GATE.**
      Nothing has ever measured this. The key is computed at
      `src/Verbara.Sdk.Ari/Audio/WebSocketAudioServer.cs:341` and registered at `:266`; the
      hand-over note said `:337` and `:262` and is wrong on both.
      Capture the **literal HTTP request line** for each producer that can reach this server, because
      the code does not distinguish between them and they need not agree:
      - the ARI `externalMedia` endpoint with `transport=websocket`, which the class summary at `:34`
        claims is the producer;
      - the `WebSocket()` dialplan application of `chan_websocket`, which
        `Examples/WebSocketMediaExample/` and `Audio/IChanWebSocketSession.cs` describe.
      Also capture `WEBSOCKET_GUID` where it exists — `Audio/AudioChannelVars.cs` already names it and
      the server never reads it, so it is a candidate key that costs nothing to record now and cannot
      be recovered later.
      Run against **both** Asterisk images the lane builds, exactly as `ci.yml` builds them,
      `CODEC_OPUS_VERSION` included — a sibling change measured that omitting it builds a 23 image
      carrying the 22 opus argument. Commit the capture beside this change in the shape
      `probe-capture.txt` takes for the sibling change: the full request recorded, never described.
      **The gate.** If this task has not produced a committed capture after one honest attempt against
      both images, F1 splits into its own change carrying A4 and B5 verbatim, this file records the
      split with the new change's name, and Phases B and C continue with F3 and F2 alone. State the
      outcome either way — "the probe landed" or "the probe did not land and F1 is now `<name>`".

## Phase B — the fixes (one focused subagent each)

- [ ] B1 **F3, red first: replace the unasserting exits in `ConfBridgeAdvancedTests` and paste the
      failures.** That file dials the undefined `700` from ten call sites across eight `[Fact]`s, and
      every one of the eight ends in an unasserting return written three ways:
      `if (confJoin is null) return;` at `:55, :104, :162`;
      `if (!joinEvents.Any(e => e.Conference == confName)) return;` at `:211, :273`;
      `if (joinEvents.IsEmpty) return;` at `:339, :385, :445`.
      Replace each with a failing assertion carrying a reason, **against the tree as it stands**, with
      700 still undefined. All eight must go red, and the failures are pasted here verbatim. This is
      the red the dialplan comment at `extensions.conf:104-111` predicted and declined to carry; it is
      carried here.
- [ ] B2 **F3: define the missing extensions, or change the tests that dial them.** Work from A2's
      verified set, not from this file's ten. For each: define it in `[test-functional]` with a
      comment naming the tests that dial it — the file's existing entries do this and the convention
      is worth keeping — or change the test to dial a defined one, whichever is honest for that test.
      `750` is a redirect target, not an originate, and may already be reachable; A2 settles it.
      Then **land the reconciliation as something that executes.** A guard, a unit test over the two
      file sets, or a check on the validation run — not a comment in a `.conf` file, because the
      comment that already documents this hole is the reason the hole is still here. The guard reads
      both dialling forms, or it under-reports and an under-report reads exactly like a clean result.
- [ ] B3 **F3: sweep the rest of the suite against A1's verified list.** Every site A1 classed as an
      exit on the normal path becomes a failing assertion with a reason; every site it classed as an
      observer filter is left exactly as it is, and the task says how many of each. Expect new red
      here too: a test that has never run its body has never been checked. Report it as a result, do
      not fix it under this task — a pre-existing product failure surfaced by this sweep is a finding
      for Phase C's harvest, not scope creep into this one.
- [ ] B4 **F2: implement A3's decision, and rewrite the tests so they can see it.** Whichever branch
      won, `Tests/Verbara.Sdk.Ari.Tests/Diagnostics/AudioStreamMetricsTests.cs` is rewritten: today
      six of its eleven `[Fact]`s assert only `.Should().NotBeNull()` on a `static readonly` field
      initialised at its declaration — true in every possible state of the program — and four supply
      their own measurement through a `MeterListener` and assert they observed it. All eleven pass
      with zero production call sites, which is the current state.
      **The negative control is the removal of a production call site, not the breaking of an expected
      value.** Breaking the expected value proves the assertion is wired; only removing the emission
      proves the test can see the defect. Paste that failure.
      On the removal branch: `*REMOVED*` rows in `PublicAPI.Unshipped.txt` (ADR-0023), a
      `CompatibilitySuppressions.xml` entry generated to a scratch path and **read line by line before
      it is kept** (ADR-0055), and a migration note (ADR-0028). On the wiring branch: none of that,
      and say so explicitly rather than leaving a reader to infer it.
- [ ] B5 **F1: act on A4's capture — and only on it.** If the capture says the request path carries a
      usable identifier, key on it and say in the contract where a caller gets it. If it says the path
      carries nothing a caller controls, the contract says the path is not addressable by an Asterisk
      identifier, and `CompositeAudioServer.GetStream` stops reporting "no such stream" and "wrong
      keyspace" as the same `null`.
      Either way, four pieces of prose in
      `src/Verbara.Sdk.Ari/Audio/WebSocketAudioServer.cs` are corrected against the capture:
      `:34` "Listens for incoming WebSocket connections from Asterisk ExternalMedia channels", which
      may simply be false; `:312` "extract Sec-WebSocket-Key and channel ID from URL path"; `:313`
      "Expected URL: `/ws/{channelId}` or `/{channelId}`", which names the placeholder `{channelId}`;
      and `:35`'s dangling "(ADR-1)", which cites nothing — this repository's ADRs are four digits and
      `ADR-0001` is *Native AOT first*, not a TcpListener decision. Resolve it to the real ADR or
      delete it.
      And correct the example, in **all three** places it repeats the collision-producing dialplan
      line: `Examples/WebSocketMediaExample/Program.cs:10`, `:78`, and
      `Examples/WebSocketMediaExample/README.md:17`. A literal `/audio` there gives every concurrent
      call the key `"audio"`.

## Phase C — integration and verification (batched)

- [ ] C1 `CHANGELOG.md [Unreleased]`. F3 takes a `### Fixed` entry; F2 takes `### Fixed` on the wiring
      branch and `### Removed — BREAKING` on the removal branch; F1 takes whatever A4's capture makes
      true. Give this change its **own insertion anchor**, distinct from every other in-flight entry —
      a shared anchor ejected a sibling PR from the merge queue with fourteen checks green. Leave
      `(#N)` for close-out.
- [ ] C2 Apply the **`ci:functional`** label to the PR and record the **PR-time** result, not only the
      queue's. Without it the functional job reports success in about sixteen seconds having started
      no Asterisk (ADR-0051), and the matrix is `[23]` on a PR against `[22, 23]` in the queue. This
      change edits the functional dialplan and the functional suite; an unlabelled green here would be
      this change's own subject, committed.
- [ ] C3 Coverage measured **after committing**, never before. The unit lane excludes
      `Category=Functional`, so all of F3 contributes **zero** patch coverage against the 85% floor —
      F2's rewritten unit tests are what must carry it, and B5's contract edits carry none either.
      Read the changed-line count and check it against the size of the diff before believing the
      percentage: a sibling change accepted `100% (6/6)` on a five-file diff and the `6` was the tell.
- [ ] C4 `dotnet build` and the **full unit lane** green with zero warnings
      (`TreatWarningsAsErrors`, `WarningLevel 9999`), Governance green, and `dotnet pack` clean with
      validation **proven** to have run — state how it was forced (trap 4), because a green pack that
      validated nothing is this repository's most expensive lie. Derive the rest of the job list from
      `.github/workflows/ci.yml` rather than from memory; "the tests of the projects I touched" misses
      the tree-scanning guards, and F3 edits a file the sync-fence guard scans.
- [ ] C5 `openspec validate --all --strict` green; paste the totals line. CI green on the PR
      **including the `merge_group` build** — the only place the functional suite runs both Asterisk
      versions, and the place a dialplan change is most likely to differ.
- [ ] C6 Harvest. Anything B3 surfaced as a pre-existing product failure, anything A4's capture
      measured and this change did not act on, and F1 itself if the split gate fired — each goes into
      an open change or an ADR addendum **with a link**, before this change archives.
      `openspec/config.yaml` requires it, and *tracked separately* with no link is the exact label that
      produced this change and its parent. Write the name of every change opened here, in this task.

## Findings this change corrects in the notes that handed it over

Recorded so the next reader trusts the measurement rather than the prose.

- **F1's line numbers.** The key is computed at `WebSocketAudioServer.cs:341` and registered at
  `:266` — not `:337` and `:262`.
- **F1's example.** The collision-producing dialplan line appears in three places, not one.
- **F1 has a fourth and fifth piece of wrong prose**, beyond the `{channelId}` placeholder the
  hand-over note named: the class summary at `:34` asserting the ExternalMedia producer, and the
  dangling "(ADR-1)" at `:35`.
- **F3's count is ten, not five** — 160, 161, 162, 163, 300, 700, 750, 999, 9998, 9999 — because the
  suite reaches the dialplan by two forms and the five came from one of them.
- **F3's early return has three spellings in one file and four across the suite**, and
  `ConfBridgeAdvancedTests` has eight affected tests, not five. The dialplan comment at
  `extensions.conf:104-111` states both wrong numbers, and ends "the hole is filed separately" with no
  link — the ADR-0060 failure reproduced inside the commit that was fixing it.

# Tasks: externalmedia-returns-a-channel-id-that-finds-its-stream

Three phases. A batched, B one focused subagent per task, C batched. Never inline in the main session.

This is a bug fix, so **the failing regression test is written first, against the unfixed code, and its
failure is pasted here verbatim.** A task is not checked off until the thing it claims actually ran.

## Phase A — foundation (batched)

- [x] A1 Run `probe-externalmedia.py` and diff its output against `probe-capture.txt`. It prints the
      full query string of every request; an earlier capture recorded parameters in prose and a run
      labelled "what the SDK sends" turned out to carry an extra one nobody could see. Then run it
      against **the lane's own image, for both versions**, not only the local one:
      `docker build -f docker/Dockerfile.asterisk docker/ --build-arg ASTERISK_VERSION=23`.
      Asterisk 23 has never been probed, and the merge queue would otherwise be the first place the
      new test meets it. Paste both outputs.

      **Done 2026-09-24.** Three builds probed: `verbara/asterisk-local:22` (22.9.0), the lane's own
      `verbara-probe:22` (22.9.0) and the lane's own `verbara-probe:23` (**23.4.1, never probed
      before**). Images built exactly as `ci.yml:387` builds them, `CODEC_OPUS_VERSION` included —
      omitting it would have built a 23 image carrying the 22 opus argument.

      **22 and 23 behave identically on every run**: same status, same error strings verbatim, same
      parameter on the wire, same byte order, same 500 on a dead port. There is no version difference
      to carry into C1.

      Three things the earlier capture asserted without measuring, now measured on both versions:

      - **RUN H's "no AudioSocket connection is ever made"** was inferred from the channel name,
        because the shipped probe runs H with the listener down. Run with the listener up: HTTP 200,
        UnicastRTP, nothing connects. The sentence was right and had not been earned.
      - **The non-canonical identifier miss is real.** An UPPERCASE UUID returns HTTP 200 with
        `Channel.Id` in the spelling sent, while the wire bytes render through `ParseUuid` as
        canonical lowercase into an ordinal dictionary. B2's negative control has a fixture now
        instead of an argument.
      - **Correction #2 of the capture was itself incomplete — the third time this file has been
        wrong.** "HTTP 500 means nothing is listening" names one cause. A malformed `data` returns the
        same 500 with the listener **up**: `channelId` is free-form and echoed verbatim, `data` must
        parse as a UUID. Recorded as correction #4.

      **This bears on C2.** Its third reversion — "pass `channelId` and `data` different values" —
      must use two **well-formed** UUIDs, or the reversion fails at the create with a 500 instead of
      at the lookup, and proves nothing.

      Stated plainly rather than dressed up: `probe-capture.txt` is **not** reproducible verbatim by
      `probe-externalmedia.py` and never could be. The capture is a narrative of an earlier hand-run
      with fixed UUIDs; the script mints uuid4 per run. A1's verdict is claim-by-claim, not a text
      diff.
- [x] A2 Read the surface that changes: `AriChannelsResource.CreateExternalMediaAsync`,
      `IAriChannelsResource`, `ExternalMediaActivity`, `AudioSocketServer.GetStream`,
      `CompositeAudioServer.GetStream`, and the XML docs on `IAudioServer` / `IAudioStream` in
      `src/Verbara.Sdk/IAriClient.cs`. Record the exact current signatures and doc text, so the diff
      is against what is there rather than what is remembered.

      **Done 2026-09-24.** Full transcription with file:line is in the workflow record; the decisions
      B1 depends on are below, each measured rather than reasoned.

      **RS0016 is globally suppressed, so the PublicAPI tracker does NOT catch a new public API here.**
      `Directory.Build.props:15` carries `<NoWarn>$(NoWarn);CS1591;RS0016;RS0037;RS0041</NoWarn>`, and
      that project-level NoWarn defeats `.editorconfig:71`. Measured: a brand-new public type with no
      tracker row built clean, `0 Warning(s)`. What **is** enforced is RS0017 — a Shipped row whose
      symbol no longer exists — which fires as an error.

      So for B1 the `*REMOVED*` row is mechanically forced and **the new row is not**. Forgetting the
      new row leaves a green build. B1 cannot lean on the compiler for that half.

      **The ApiCompat prediction is now measured**, with the change applied and `dotnet pack -c
      Release`: `src/Verbara.Sdk` → CP0002 + CP0006; `src/Verbara.Sdk.Ari` → CP0002 only. Exactly what
      the proposal predicted.

      **CA1068 applies to internal methods too**, measured — any helper B1 or B2 adds puts the token
      last as well.

      **An in-tree precedent argues the other way on position.** `IAriClient.cs:141` declares
      `CreateWithoutDialAsync(string endpoint, string app, string? channelId = null, ...)` — `channelId`
      as the **first** optional parameter. Appending after `data` is still the recommendation, but B1
      makes that call knowingly rather than discovering the inconsistency later.

      **A trap that "move to named arguments" does not solve by itself:** if a substitute moves to
      named arguments but omits `channelId:`, the compiler fills in `null` and NSubstitute
      equality-matches it. After B2 the activity sends a real UUID, the setup stops matching, the call
      returns `default(ValueTask<AriChannel>)`, and the test dies on a null `Channel` rather than on an
      argument mismatch. Both setups need an explicit `channelId: Arg.Any<string?>()`.

      **`ExternalMediaActivity` has one constructor, not overloads** — `tasks.md` A2 asked for the
      plural and there is exactly one, with two optional parameters, at `:41`.
- [x] A3 **Write the failing regression test first, as an argument-capture test.** A test that mocks
      `IAriChannelsResource`, constructs `ExternalMediaActivity` with `Encapsulation = "audiosocket"`
      and an `AudioSocketServer`, runs it, and asserts on the arguments the activity passed:
      `data` is non-null and `transport == "tcp"`. It compiles against the **unfixed** code (it names
      no new parameter) and fails on `data == null`. Paste the failure verbatim.
      Do **not** write it as "assert `GetStream(Channel.Id)` hits": with a mocked resource the test
      chooses both the returned `Channel.Id` and the UUID its own client sends, so it can be made to
      pass today — the closed loop this whole change is about.

      **Done 2026-09-24.** Written as an argument-capture test, in
      `Tests/Verbara.Sdk.Activities.Tests/Activities/ActivityTests.cs`, named
      `StartAsync_ShouldSendDataAndTcpTransport_WhenEncapsulationIsAudioSocket`.

      **The failure, verbatim, against unfixed code:**

      ```text
      Failed Verbara.Sdk.Activities.Tests.Activities.ActivityTests.
             StartAsync_ShouldSendDataAndTcpTransport_WhenEncapsulationIsAudioSocket [117 ms]
      Error Message:
       Expected sentData not to be <null> because an audiosocket create with no data is HTTP 400
       "data can not be empty" (probe-capture.txt RUN D), so the activity must supply the
       identification uuid.
      Total tests: 1
           Failed: 1
      ```

      It builds clean and fails on its assertion, not on a compile error.

      **Three things the task brief did not anticipate, all measured:**

      - **The prescribed shape would not have compiled after B1.** The natural NSubstitute setup ends
        in a ninth **positional** `Arg.Any<CancellationToken>()`, which after B1 binds to
        `string? channelId` — CS1503. The same trap A4 names for the two existing substitutes applies
        to the new test. Fixed by naming `cancellationToken:`, the one name that exists both before
        and after the fix.
      - **`.Returns(...)` would have broken it silently rather than loudly**, for the reason A2
        records. `ReturnsForAnyArgs` is required, not stylistic.
      - **The assertion on `transport` is not proven by the red run.** Both `data` and `transport` are
        null today and FluentAssertions stops at the first, so `transport` is only exercised by the
        forward control (simulated fix → green).

      **A near-miss of this change's own failure family, recorded because it nearly shipped.**
      Restoring a scratch file with `mv` preserved its old mtime, MSBuild judged the source older than
      its output, skipped the rebuild, and the run reported `Passed! 49/49` **from the fixed
      assembly** while the tree held unfixed code. It was caught only because 49/49 contradicted a
      filtered run. `touch` plus a re-run gave the true `Failed: 1, Passed: 48`. A green number from a
      stale build is indistinguishable from a green number from a correct one.
- [x] A4 Enumerate every call site and every substitute of `CreateExternalMediaAsync` and say which
      one B1 changes. There are four today: `ExternalMediaActivity.cs:51`,
      `Tests/…/ActivityTests.cs:328`, `Tests/…/ActivityTests.cs:392`, and
      `Tests/Verbara.Sdk.Ari.Tests/Resources/AriResourceTests.cs:161`. The two `ActivityTests`
      substitutes enumerate nine positional `Arg.Any` ending in a `CancellationToken` and will not
      compile after B1. Also confirm against the tree — do not trust this sentence — that the
      **two `GetStream` branches** at `ExternalMediaActivity.cs:65-75` are the unexecuted code. The
      polling `while` loop itself does run, in two tests.


      **Done 2026-09-24.** Call sites, and three findings that change B1.

      **The proposal's unexecuted-lines claim is right in substance and wrong in its range.** Lines 65
      and 71 — the `if` conditions — are HITS=2 at 50% branch coverage: evaluated every iteration,
      always false. Only **67, 68, 73 and 74** are HITS=0.

      **And the unexecuted set is materially larger than either document says:** lines 80-82 (the
      second `TimeoutException` throw, 0/2 branches — the loop is never left through its own condition
      because `Task.Delay(200, token)` always throws first), line 87 (`ExecuteAsync` has never
      returned normally), line 95, and 100-104 (`DisposeAsync` in its entirety). If B2 restructures
      the loop, these become patch lines under C5's 85% floor.

      **B1 produces about thirty compiler errors, not three.** Once CS1503 kills overload resolution
      the NSubstitute analyzer stops seeing an interface member and fires NS1004 on every `Arg.Any` in
      the failed call — nine per setup, promoted to errors by `TreatWarningsAsErrors`. Measured: 27
      unique NS1004 plus 3 unique CS1503. The 27 that dominate the log are false and point at the
      wrong diagnosis; all twenty-seven vanish when the three real ones are fixed.

      **A3's own new substitute is the third CS1503 site**, at working-copy `ActivityTests.cs:453`,
      and it carries a comment asserting the opposite — that the captured positions are stable across
      the fix. They are not.

      **`AriResourceTests.cs:161` keeps compiling and keeps passing after B1 while asserting nothing
      about `channelId`.** Worse: across all 477 Ari unit tests, `AriChannelsResource.cs:90` — the
      `data=` appender — sits at 50% branch coverage and **has never executed**, along with
      `connection_type` and `direction`. B1 adds `channelId` in exactly that shape, so asserting the
      literal `channelId=` is worthless unless the same test actually passes one.

      **The session's default `grep` honours `.gitignore`.** `docs/plans/` is gitignored but its files
      were force-added and are tracked, so grep returned 14 hits where `git grep` returns 17, silently
      omitting three. **Any "I found every call site" claim made with the default grep in this repo is
      unsound** — use `git grep`.

      **`Examples/ContactCenterSupervisionExample/Program.cs:78`** is a fifth
      `Substitute.For<IAriChannelsResource>()`. It does not configure `CreateExternalMediaAsync`, so it
      keeps compiling — but example projects are in the build.

      **Not verified, and stated rather than assumed:** the proposal's claim that Pro's fakes and
      decorators break. Pro lives outside this worktree and this task may not leave it. Within the
      worktree `AriChannelsResource` is the only implementer, so nothing here breaks on CS0535.
## Phase B — critical components (one focused subagent each)

- [ ] B1 Add the `channelId` parameter to `CreateExternalMediaAsync` on **both**
      `IAriChannelsResource` and `AriChannelsResource`, positioned **before** `cancellationToken`
      (CT-last is the SDK's convention and CA1068 is on under `TreatWarningsAsErrors`). Then:
      - the two `ActivityTests` substitutes move to **named arguments**;
      - `AriResourceTests` extends its URL assertion to the **literal `channelId=`** — Asterisk
        spells it camelCase among snake_case siblings and ignores an unknown parameter silently, so a
        typo becomes a no-op that only the queue would catch;
      - `*REMOVED*` + new rows in `PublicAPI.Unshipped.txt` for both packages (ADR-0023);
      - `CompatibilitySuppressions.xml` generated deliberately and **read entry by entry before it is
        kept** — expect `CP0002` in both packages and `CP0006` on the interface, and `src/Verbara.Sdk`
        has no such file today. ADR-0055 records what happens when the flag is left to the build
        machine: validation runs, reports green, and compares nothing.
      - a migration note (ADR-0028 requires one for a minor carrying a break).
      Confirm with the exact command CI runs — `dotnet pack` — not with a clean build. A green build
      says nothing about `PackageValidation`; that mistake cost #302 a CI failure.
- [ ] B2 `ExternalMediaActivity` derives its request from the server it was handed:
      `encapsulation = Encapsulation ?? (_audioSocketServer is not null ? "audiosocket" : null)`; when
      the encapsulation is AudioSocket (compare ordinal-ignore-case — Asterisk uses `strcasecmp`) then
      `transport = Transport ?? "tcp"` and one `Guid.NewGuid().ToString()` goes to **both** `channelId`
      and `data`. Canonical lowercase is not incidental: `AudioSocketSession.ParseUuid` keys the table
      with `new Guid(bytes, bigEndian: true).ToString()` into an ordinal comparer, so any other
      spelling creates a channel whose stream cannot be found. Write both reasons in the code.
      Decide and implement what happens when an `AudioSocketServer` is supplied with a non-AudioSocket
      encapsulation: today that combination creates an RTP channel and waits out a 30-second timeout,
      which is the shape the delta spec forbids. A unit test pins whichever behaviour is chosen, plus
      one negative control using a non-canonical identifier spelling.
- [ ] B3 Fix the contract text. `IAudioServer.GetStream` says "Get an active stream by channel ID" and
      `IAudioStream.ChannelId` says "Unique ID of the external media channel in Asterisk" — both
      assert an identity the code does not hold. Say for each implementation what the key is and in
      what form: for `AudioSocketServer`, the UUID Asterisk sent in its identification frame in
      canonical lowercase hyphenated form, which for the ARI route is the value the creator supplied
      as `channelId` and `data`. Say in `ExternalMediaActivity`'s remarks that the WebSocket branch is
      **not** routed — `WebSocketAudioServer` keys on the last segment of the request URL (F1).

## Phase C — integration (batched)

- [ ] C1 Functional test beside `AudioSocketWireFunctionalTests`. Three things it must get right:
      - **Subscribe a Stasis application first.** `externalMedia` validates that `app` is non-empty
        and never checks it is registered, so the create returns 200 against a bare listener — but the
        channel then runs Stasis with no subscriber, Asterisk hangs it up, and the entry is removed
        within milliseconds of a 200 ms poll. A test written from the probe alone times out and looks
        like the fix not working.
      - Construct the activity with `Encapsulation = "audiosocket"` and **nothing else**, so the test
        measures the class's own path rather than a configuration the test supplied.
      - A fixed port that is **not** 19092 or 19093 — those belong to extensions 710 and 711.
      Assert with a reason on every path; never `return` early. Assert
      `activity.AudioStream!.ChannelId == activity.Channel!.Id`.
- [ ] C2 **Negatively control the functional test by reverting the fix, not by breaking the
      assertion.** Breaking the expected value proves the assertion is wired; it does not prove the
      test can see the defect. Three reversions, each failure pasted: drop `data`; drop the `tcp`
      transport; pass `channelId` and `data` different values. ADR-0060's own control was a revert.
- [ ] C3 Apply the **`ci:functional`** label to the PR. `docker/Dockerfile.asterisk` work and the
      functional suite are skipped on `pull_request` unless that label is present (ADR-0051,
      `.github/workflows/ci.yml`), and the matrix is `[23]` on a PR against `[22, 23]` in the queue.
      Without the label the job reports `pass` in about sixteen seconds having started no Asterisk —
      which is exactly how a broken dialplan reached the merge queue in #302. Record the PR-time
      result, not only the queue's.
- [ ] C4 `CHANGELOG.md [Unreleased]`: a `### Fixed — BREAKING` entry. Give it its **own insertion
      anchor** distinct from any other in-flight entry — a shared anchor ejected #300 from the merge
      queue with fourteen checks green. Leave `(#N)` for close-out.
- [ ] C5 Coverage measured **after committing**, never before. The unit lane excludes
      `Category=Functional`, so C1 contributes **zero** patch coverage against the 85% floor: A3 and
      B2's unit tests are what must carry `ExternalMediaActivity`'s new lines. Read the changed-line
      count and check it against the size of the diff before believing the percentage — a sibling
      change accepted `100% (6/6)` on a five-file diff and the `6` was the tell.
- [ ] C6 `openspec validate --all --strict` green, full unit lane green, Governance green, `dotnet
      pack` clean, and CI green on the PR **including the `merge_group` build** — the only place the
      functional suite runs both Asterisk versions.
- [ ] C7 Open a change for the follow-ups below and write its link back into this file. They are
      carried as prose, not as unchecked boxes: `openspec/config.yaml` requires every deferred finding
      to be harvested into an open change or an ADR addendum before archiving, and three `- [ ]` boxes
      inside this change would defer that harvest to close-out — the exact failure ADR-0060 committed
      and this change exists to stop repeating.

## Follow-ups this change does NOT fix

ADR-0060 wrote "tracked separately" with no link, and the close-out archived it anyway. C7 opens the
change that carries these; the lines below are the evidence it starts from.

- **F1 — `WebSocketAudioServer` keys on a URL path segment.** The key is computed at
  `WebSocketAudioServer.cs:337` (`path.TrimStart('/').Split('/').LastOrDefault()`) and registered at
  `:262`. The SDK's own example puts a literal `/audio` there
  (`Examples/WebSocketMediaExample/Program.cs:10`), so every concurrent call would register under the
  key `"audio"`. `CompositeAudioServer` hands the same string to both servers, which do not agree on
  what it means. **No probe has measured what `externalMedia` with `transport=websocket` puts in the
  request path**, which is why this stays out of the present change rather than being designed from a
  reading of the code.
- **F2 — `AudioStreamMetrics`** declares ten instruments with zero production call sites.
- **F3 — five dialplan extensions are dialed and never defined.** `[test-functional]` is dialed at
  160, 300, 700, 999 and 9998 and defines none of them. `ConfBridgeAdvancedTests` alone dials the
  undefined 700 from ten call sites and passes by taking its `if (confJoin is null) return;` branch
  without asserting anything. The shape to hunt is that early return, not the extension numbers.

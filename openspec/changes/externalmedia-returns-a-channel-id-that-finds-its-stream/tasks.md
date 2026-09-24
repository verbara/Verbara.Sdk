# Tasks: externalmedia-returns-a-channel-id-that-finds-its-stream

Three phases. A batched, B one focused subagent per task, C batched. Never inline in the main session.

This is a bug fix, so **the failing regression test is written first, against the unfixed code, and its
failure is pasted here verbatim.** A task is not checked off until the thing it claims actually ran.

## Phase A — foundation (batched)

- [ ] A1 Run `probe-externalmedia.py` and diff its output against `probe-capture.txt`. It prints the
      full query string of every request; an earlier capture recorded parameters in prose and a run
      labelled "what the SDK sends" turned out to carry an extra one nobody could see. Then run it
      against **the lane's own image, for both versions**, not only the local one:
      `docker build -f docker/Dockerfile.asterisk docker/ --build-arg ASTERISK_VERSION=23`.
      Asterisk 23 has never been probed, and the merge queue would otherwise be the first place the
      new test meets it. Paste both outputs.
- [ ] A2 Read the surface that changes: `AriChannelsResource.CreateExternalMediaAsync`,
      `IAriChannelsResource`, `ExternalMediaActivity`, `AudioSocketServer.GetStream`,
      `CompositeAudioServer.GetStream`, and the XML docs on `IAudioServer` / `IAudioStream` in
      `src/Verbara.Sdk/IAriClient.cs`. Record the exact current signatures and doc text, so the diff
      is against what is there rather than what is remembered.
- [ ] A3 **Write the failing regression test first, as an argument-capture test.** A test that mocks
      `IAriChannelsResource`, constructs `ExternalMediaActivity` with `Encapsulation = "audiosocket"`
      and an `AudioSocketServer`, runs it, and asserts on the arguments the activity passed:
      `data` is non-null and `transport == "tcp"`. It compiles against the **unfixed** code (it names
      no new parameter) and fails on `data == null`. Paste the failure verbatim.
      Do **not** write it as "assert `GetStream(Channel.Id)` hits": with a mocked resource the test
      chooses both the returned `Channel.Id` and the UUID its own client sends, so it can be made to
      pass today — the closed loop this whole change is about.
- [ ] A4 Enumerate every call site and every substitute of `CreateExternalMediaAsync` and say which
      one B1 changes. There are four today: `ExternalMediaActivity.cs:51`,
      `Tests/…/ActivityTests.cs:328`, `Tests/…/ActivityTests.cs:392`, and
      `Tests/Verbara.Sdk.Ari.Tests/Resources/AriResourceTests.cs:161`. The two `ActivityTests`
      substitutes enumerate nine positional `Arg.Any` ending in a `CancellationToken` and will not
      compile after B1. Also confirm against the tree — do not trust this sentence — that the
      **two `GetStream` branches** at `ExternalMediaActivity.cs:65-75` are the unexecuted code. The
      polling `while` loop itself does run, in two tests.

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

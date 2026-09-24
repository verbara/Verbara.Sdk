# Tasks: externalmedia-returns-a-channel-id-that-finds-its-stream

Three phases. A batched, B one focused subagent per task, C batched. Never inline in the main session.

This is a bug fix, so **the failing regression test is written first, against the unfixed code, and its
failure is pasted here verbatim.** A task is not checked off until the thing it claims actually ran.

## Phase A — foundation (batched)

- [ ] A1 Re-run `probe-capture.txt` end to end and confirm it still reproduces on this machine:
      `data` on the wire, `channelId` as `Channel.Id`, HTTP 400 `data can not be empty` for the
      parameters `ExternalMediaActivity` sends today, and HTTP 200 with both equal. Use a **fresh**
      UUID per run — a reused `channelId` returns HTTP 500 and is not a finding. Paste the output.
- [ ] A2 Read the surface that changes: `AriChannelsResource.CreateExternalMediaAsync`,
      `ExternalMediaActivity`, `AudioSocketServer.GetStream`, `CompositeAudioServer.GetStream`, and the
      XML docs on `IAudioServer` / `IAudioStream` in `src/Verbara.Sdk/IAriClient.cs`. Record the exact
      current signatures and doc text, so the diff is against what is there rather than what is
      remembered.
- [ ] A3 **Write the failing regression test first.** A unit test that constructs
      `ExternalMediaActivity` with an AudioSocket server and asserts the stream is found by
      `Channel.Id`. It must fail against the unfixed code, and the failure text goes in this file
      verbatim — not a summary of it. Note which of the two defects it catches; if it only catches
      one, say which and add the second test rather than widening the first.
- [ ] A4 Check whether any existing test would change meaning. All four current constructions of
      `ExternalMediaActivity` use the one-argument overload, so the polling block never runs — confirm
      that against the tree rather than trusting this sentence, and list any test that starts
      executing code it did not execute before.

## Phase B — critical components (one focused subagent each)

- [ ] B1 Add the optional `channelId` parameter to `CreateExternalMediaAsync`. Place it so no existing
      positional call site changes meaning, append the row to `PublicAPI.Unshipped.txt`, and confirm
      `dotnet pack` reports no `CP0002`/`CP0011` — an additive optional parameter should need no
      suppression entry, and if it does, that is a finding, not a formality.
- [ ] B2 Make `ExternalMediaActivity` mint one UUID and pass it as both `channelId` and `data` when the
      encapsulation is AudioSocket. Decide explicitly what happens for other encapsulations and write
      the reason in the code: RTP does not carry a caller-supplied identifier, so forcing one there
      would change behaviour for callers this change has no business touching.
- [ ] B3 Fix the contract text. `IAudioServer.GetStream` currently says "Get an active stream by
      channel ID" and `IAudioStream.ChannelId` says "Unique ID of the external media channel in
      Asterisk" — both assert an identity the code does not hold. Say what the key actually is for
      each implementation, including that `WebSocketAudioServer` keys on something else entirely.

## Phase C — integration (batched)

- [ ] C1 Functional test: originate a real `externalMedia` against the real Asterisk container and
      assert the stream is found by `Channel.Id`. Put it beside `AudioSocketWireFunctionalTests`. It
      must run in the `merge_group` lane — a pull request short-circuits that job and reports `pass`
      in 16 seconds without starting Asterisk, which is how a broken dialplan reached the queue in
      #302.
- [ ] C2 **Mutation-check the new functional test rather than trusting it.** Break the expected value
      and confirm it fails. A fixture that passes while measuring nothing is the exact failure this
      repository has now shipped twice.
- [ ] C3 Pick an extension number for any new dialplan entry that no test already dials.
      `[test-functional]` is dialed at **160, 300, 700, 999 and 9998** and defines none of them; 710
      and 711 are this change's neighbours and are taken.
- [ ] C4 `CHANGELOG.md [Unreleased]`: a `### Fixed` entry. Give it its **own insertion anchor**
      distinct from any other in-flight entry — a shared anchor ejected #300 from the merge queue with
      fourteen checks green. Leave `(#N)` for close-out.
- [ ] C5 Coverage measured **after committing**, never before. Read the changed-line count and check it
      against the size of the diff before believing the percentage: a sibling change accepted
      `100% (6/6)` on a five-file diff and the `6` was the tell.
- [ ] C6 `openspec validate --all --strict` green, full unit lane green, Governance green, and CI green
      on the PR — including the `merge_group` build, which is the only place the functional suite
      actually runs.

## Follow-ups this change does NOT fix, recorded so they are not lost again

ADR-0060 wrote "tracked separately" with no link, and the close-out archived it anyway. That is what
these lines exist to prevent repeating.

- [ ] F1 `WebSocketAudioServer` keys `_streams` by the **last segment of the request URL**
      (`WebSocketAudioServer.cs:328`), and the SDK's own example puts a literal `/audio` there — so
      every concurrent call would register under the key `"audio"`. `CompositeAudioServer` hands the
      same string to both servers, which do not agree on what it means. Needs its own change.
- [ ] F2 `AudioStreamMetrics` declares ten instruments with zero production call sites.
- [ ] F3 `[test-functional]` is dialed at 160, 300, 700, 999 and 9998 and defines none of them.
      `ConfBridgeAdvancedTests` alone dials the undefined 700 from ten call sites and passes by taking
      its `if (confJoin is null) return;` branch without asserting anything. The shape to hunt is that
      early return, not the extension numbers.

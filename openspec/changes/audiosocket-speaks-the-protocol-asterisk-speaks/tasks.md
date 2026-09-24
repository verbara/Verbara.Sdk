# Tasks: audiosocket-speaks-the-protocol-asterisk-speaks

## Phase A — the fixture, before any production edit

- [x] A1 Record the head commit. Re-read both codecs and both enums as they stand:
      `src/Verbara.Sdk.Ari/Audio/AudioSocketProtocol.cs`, `Audio/AudioSocketSession.cs`, the frame-type
      enum on `src/Verbara.Sdk/IAriClient.cs`, and
      `src/Verbara.Sdk.VoiceAi.AudioSocket/Internal/AudioSocketFrameCodec.cs`,
      `AudioSocketFrameType.cs`, `AudioSocketClient.cs`. Confirm both use a 4-byte header and that the
      VoiceAi enum carries `Audio = 0x01` alongside `AudioSlin12 = 0x11`. If either is already correct,
      stop and say so.
      - Head `6005b29ce73480fc73960e4acd0586305435a57b`. Baseline before any edit:
        `dotnet build Verbara.Sdk.slnx -c Release` -> **0 Warning(s), 0 Error(s)**.
      - **Neither is correct; the gate does not stop the change.** `AudioSocketProtocol.cs:12`
        `HeaderSize = 4` with a 3-byte length read at `:31`; `AudioSocketFrameCodec.cs:9`
        `HeaderSize = 4 // 1 type + 3 length` with the same 3-byte length read at `:30`.
        `AudioSocketFrameType.cs` has `Uuid = 0x00`, `Audio = 0x01`, `Silence = 0x02`, `Error = 0x04`,
        `Hangup = 0xFF` beside `AudioSlin12 = 0x11 .. AudioSlin192 = 0x18`.
      - **Two corrections to this task's own text**, both reported rather than worked around:
        1. The ARI frame-type enum is **not** on `src/Verbara.Sdk/IAriClient.cs`. It is
           `AudioFrameType` in `src/Verbara.Sdk.Ari/Audio/IAudioStream.cs:4`. `grep -rn "enum AudioFrameType" src/`
           returns that one hit. The proposal's Impact section inherits the same wrong path.
        2. That enum's current values are `Uuid = 0x00`, `Audio = 0x01`, `Silence = 0x02`,
           **`Error = 0x10`**, `Hangup = 0xFF`. So ARI today maps the byte Asterisk uses for 8 kHz
           audio onto `Error` — every real audio frame would be reported as an error frame and would
           end the read pump. B2 must move `Error` off `0x10` in the ARI enum, which neither the
           proposal nor B2's text mentions; both describe the VoiceAi enum's `Error = 0x04` only.
      - `Audio/AudioSocketSession.cs:184` reads `new Guid(bytes)` with no `bigEndian:` argument,
        as B3 states.
- [x] A2 **Write the captured-bytes fixture first, and make it fail.** The measured identification frame
      is `01 00 10` followed by the sixteen bytes of
      `11111111-2222-3333-4444-555555555555` in RFC 4122 order — taken from a real Asterisk 22 with
      `res_audiosocket.so`, originating into
      `AudioSocket(11111111-2222-3333-4444-555555555555,127.0.0.1:9092)`. Put the bytes in **one** shared
      fixture that both packages' tests read; a per-package copy rebuilds the closed loop this change
      exists to break. Assert both parsers decode it as an identification frame carrying that UUID.
      **Both must be RED before anything in `src/` changes.** Paste the verbatim failures here.
      - **Where the fixture lives, and why.** New project `Tests/Verbara.Sdk.TestInfrastructure.Wire`
        (zero `PackageReference`), holding `AudioSocketWireCapture.cs`, referenced by both
        `Verbara.Sdk.Ari.Tests` and `Verbara.Sdk.VoiceAi.AudioSocket.Tests`, and added to
        `Verbara.Sdk.slnx`. One file, one compiled assembly, two readers.
      - Checked the two alternatives the task named. **`Verbara.Sdk.TestInfrastructure` is not
        referenced by either test project today** — only by `FunctionalTests`, `IntegrationTests` and
        `TestInfrastructure.Http` — and it carries `Testcontainers`, so referencing it would hand two
        unit suites the whole Docker client graph to read nineteen bytes. The repo has already paid
        for that mistake once and wrote it down: `Verbara.Sdk.TestInfrastructure.Http` exists solely
        because WireMock's transitive graph spreading into suites that did not need it dropped
        measured line coverage 80.42% -> 61.96% with every test still passing (ADR-0041 D7). A
        dependency-free sibling is that same rule applied one project earlier, and it is the repo's
        own established sharing mechanism rather than a new one.
      - A **linked `<Compile Include=... Link=...>`** was the other candidate and was rejected: it
        compiles two copies of the source into two assemblies, it is invisible from either test file,
        and `grep -rn "Compile Include" --include="*.csproj"` finds no precedent anywhere in the repo.
      - **Verbatim RED, ARI** (`dotnet test ... --filter FullyQualifiedName~AudioSocketCapturedWireTests`):

        ```text
        Failed Verbara.Sdk.Ari.Tests.Audio.AudioSocketCapturedWireTests.TryParseFrame_ShouldReportUuidFrameOfSixteenBytes_WhenReadingCapturedIdentificationFrame [< 1 ms]
        Error Message:
         Expected parsed to be True because the capture holds one whole frame Asterisk sent — 19 bytes, header included, but found False.

        Failed Verbara.Sdk.Ari.Tests.Audio.AudioSocketCapturedWireTests.Session_ShouldReportTheDialplanUuidAsChannelId_WhenFedTheCapturedIdentificationFrame [1 ms]
        Error Message:
         Expected channelId to be "11111111-2222-3333-4444-555555555555" with a length of 36 because Asterisk identified the call with the UUID the dialplan named, but "" has a length of 0, differs near "" (index 0).
        ```

      - **Verbatim RED, VoiceAi:**

        ```text
        Failed Verbara.Sdk.VoiceAi.AudioSocket.Tests.AudioSocketCapturedWireTests.TryReadFrame_ShouldReportUuidFrameCarryingTheDialplanUuid_WhenReadingCapturedIdentificationFrame [< 1 ms]
        Error Message:
         Expected read to be True because the capture holds one whole frame Asterisk sent — 19 bytes, header included, but found False.
        ```

      - Both parsers fail the same way and for the same reason: the 4-byte header reads the type as
        `0x01` and the length as `0x111111` (ARI) / `0x011111` shifted (VoiceAi), so neither ever sees
        a complete frame in nineteen bytes.
      - Nothing under `src/` was touched. Build after the fixture: **0 Warning(s), 0 Error(s)**.
      - **Fixture weakness found and closed in A3, worth recording here because it bites B5.** Every
        field of `11111111-2222-3333-4444-555555555555` reads the same in either byte order, so this
        frame alone **cannot** distinguish `new Guid(bytes)` from `new Guid(bytes, bigEndian: true)`.
        B5's "`bigEndian: true` dropped from the ARI parse" mutation would have passed against this
        entry. A3 captured a second identification frame with a UUID that is not byte-order symmetric,
        and that is the entry B5 must mutate against.
- [x] A3 Capture the remaining frame kinds and add them to the fixture: an audio frame and a hangup.
      Bring up the Asterisk image with an `AudioSocket()` extension, keep the socket open long enough
      for audio to flow, and record type, length and the first bytes of payload. If a kind cannot be
      captured, say so and leave it out rather than hand-writing it — a hand-written fixture entry is
      the closed loop again.
      - Rig: `verbara/asterisk-local:22` (**Asterisk 22.9.0**; `app_audiosocket.so`,
        `res_audiosocket.so`, `chan_audiosocket.so` all Running), `--network host`, a mounted
        `extensions.conf` with `AudioSocket(01234567-89ab-cdef-0123-456789abcdef,127.0.0.1:9092)`, and
        a bare listener on 9092. Full record in `probe-capture-a3.txt` beside this file.
      - **Audio — CAPTURED.** `10 01 40` then 320 bytes: type `0x10`, length `0x0140` = 320, i.e. 160
        samples of 8 kHz signed-linear, 20 ms. First payload bytes `00 00 ff ff 00 00 00 00 ...`. The
        run recorded **97,888 bytes / 304 frames / 0 trailing** (1 identification + 303 audio); a
        longer run of the same shape gave 701 audio frames, again 0 trailing. `Audio = 0x10` is now
        **measured**, not inferred from `AudioSlin12 = 0x11`.
      - **DTMF — CAPTURED, unasked for and directly useful.** Whole frame: `03 00 01 31` — type
        `0x03`, length 1, payload the ASCII digit `'1'`. `SendDTMF(1234,250,250)` on the far leg gave
        four frames with payloads `31 32 33 34`. This is the first evidence in the repo for
        `Dtmf = 0x03`, which B2 adds to both enums, and it satisfies the delta spec's fourth scenario
        (a frame type the SDK does not act on is still recognised and skipped by its declared length).
      - **A second identification frame — CAPTURED**, with the non-symmetric UUID
        `01234567-89ab-cdef-0123-456789abcdef`: `01 00 10 01 23 45 67 89 ab cd ef 01 23 45 67 89 ab cd ef`.
        RFC 4122 order confirmed against a UUID that can actually show it; a little-endian read of
        those bytes yields `67452301-ab89-efcd-...`. This is the entry B3 and B5 hang on.
      - **Hangup — NOT CAPTURED, and that is the finding.** Two runs ended the call two different ways
        — `channel request hangup all` from the CLI, and the far leg reaching `Hangup()` in the
        dialplan on its own. Both ended identically: the listener saw the peer close the TCP
        connection with **zero trailing bytes** after the last complete frame. **Asterisk 22.9.0's
        `app_audiosocket` closes the socket; it does not send a `0x00` hangup frame inbound.** No
        hangup entry was written into the fixture. `Hangup = 0x00` stays in B2's target table for the
        **outbound** direction (the SDK sends it to ask Asterisk to hang up), on the strength of the
        type table and not of a measurement, and the fixture says so in as many words.
      - Header confirmed as 1 byte type + 2 bytes big-endian length across **1,411 frames**, 0 trailing
        bytes in every run.
      - Fixture extended with all three captured entries; both suites' captured-wire tests grew to
        **5 RED (ARI) and 4 RED (VoiceAi)**, still with nothing under `src/` touched. Rest of both
        suites unaffected: `Failed: 5, Passed: 471` (Ari) and `Failed: 4, Passed: 106` (AudioSocket).
      - Guards re-run after the additions: `Verbara.Sdk.Governance.Tests` **129/129 passed**,
        `tools/audit-test-asserts.sh` **0 violations** (449 files, ~2,975 tests),
        `git diff --stat sync-fence-baseline.json` empty, no `PublicAPI.*.txt` touched in Phase A.
      - Container cleaned up: `docker rm -f as-probe`, confirmed gone.

## Phase B — the fix

- [x] B1 Header to three bytes in both codecs: one type, two of big-endian length. Both the read and the
      write paths, including `AudioSocketClient.cs`, whose frame building must move with the codec it
      feeds.
      - `src/Verbara.Sdk.Ari/Audio/AudioSocketProtocol.cs`: `HeaderSize` 4 -> **3**, the read is now
        `(b0 << 8) | b1`, the write `BinaryPrimitives.WriteUInt16BigEndian(span[1..3], ...)`.
      - `src/Verbara.Sdk.VoiceAi.AudioSocket/Internal/AudioSocketFrameCodec.cs`: `HeaderSize` 4 -> **3**,
        read `BinaryPrimitives.ReadUInt16BigEndian(header[1..])`, write
        `WriteUInt16BigEndian(buffer[1..3], ...)`.
      - **`AudioSocketClient.cs` needed no edit**, contrary to what this task and the proposal both
        expect: it never lays out a header — every one of its three sends goes through
        `AudioSocketFrameCodec.WriteFrame`, so its frames moved with the codec on their own. Its UUID
        frame was already `TryWriteBytes(..., bigEndian: true)`, so with `Uuid = 0x01` it now emits
        exactly the nineteen bytes the probe captured.
      - **Added beyond the task text, and reported rather than slipped in:** a two-byte length caps a
        frame at 65,535 bytes where the old three-byte length reached 16 MiB. Left alone, the new
        write path would silently truncate a longer payload and put a frame on the wire whose declared
        length is not its own — the same class of defect this change exists to remove, newly
        introduced by it. Both `WriteFrame`s now throw `ArgumentOutOfRangeException`
        (`MaxPayloadLength = ushort.MaxValue`), with one test each. That is the +1 test in each suite.
- [x] B2 Both enums take the values Asterisk sends: `Hangup = 0x00`, `Uuid = 0x01`, `Dtmf = 0x03`,
      `Audio = 0x10`, `Error = 0xFF`. Remove `Silence = 0x02` from the VoiceAi enum — Asterisk has no
      such frame. `AudioSlin12..AudioSlin192` are already right and do not move. Cite
      `res_audiosocket.h` in a comment on each enum, so the next reader can check rather than inherit.
      - Both enums now carry exactly that table, and both cite
        `res_audiosocket.h` / `enum ast_audiosocket_msg_kind` in their remarks, alongside a pointer to
        the capture. `AudioSlin12..192` untouched.
      - The ARI enum is in **`src/Verbara.Sdk.Ari/Audio/IAudioStream.cs`**, not in
        `src/Verbara.Sdk/IAriClient.cs` as A1's text and the proposal's Impact section say. (The
        *interface* `IAudioStream` really is in `IAriClient.cs:665`; the file named after it holds only
        the enum. That is how the wrong path got written down.)
      - **`Silence` was removed from BOTH enums, not just VoiceAi's** — the ARI enum had
        `Silence = 0x02` too, and its read pump had a `case` for it that enqueued an empty buffer.
      - **ARI's `Error` moved off `0x10`**, which neither the proposal nor this task mentions: ARI read
        every real 8 kHz audio frame as `Error` and its read pump *returns* on `Error`, so it would
        have torn the session down on the first audio frame even with a correct header.
      - **Consequence not in the task text: `AudioSocketSession.WriteSilenceAsync` is gone.** It is
        public and shipped, and its only job was to write a `Silence` frame. Keeping it would mean
        keeping a member Asterisk does not define or casting a magic `0x02` onto the wire; the task
        says plainly not to keep a member Asterisk does not send. `AudioSocketFrameCodec.ParseSilenceDuration`
        (internal) went with it.
      - **`PublicAPI` diff, exactly.** Both `PublicAPI.Shipped.txt` files are **untouched**; every entry
        below is added to the package's `PublicAPI.Unshipped.txt`, which is the analyser's documented
        route for a breaking change to shipped API and the form already used in
        `src/Verbara.Sdk.Push/PublicAPI.Unshipped.txt`. `Verbara.Sdk.Ari`: five `*REMOVED*` lines
        (`AudioFrameType` `Audio = 1`, `Error = 16`, `Hangup = 255`, `Silence = 2`, `Uuid = 0`) and five
        additions (`Audio = 16`, `Dtmf = 3`, `Error = 255`, `Hangup = 0`, `Uuid = 1`).
        `Verbara.Sdk.VoiceAi.AudioSocket`: the same five `*REMOVED*`/five added for
        `AudioSocketFrameType` (its old `Error` was `4`, not `16`), plus one more `*REMOVED*` line for
        `AudioSocketSession.WriteSilenceAsync(...)`. `AudioSlin12..192` do **not** appear: their values
        are unchanged, so touching them would have been churn. Net: **11 removals, 10 additions**, and
        the only member that leaves the surface without a replacement is `WriteSilenceAsync`.
- [x] B3 `bigEndian: true` on the ARI UUID parse (`Audio/AudioSocketSession.cs:184`). VoiceAi already
      does this; the probe confirms RFC 4122 order on the wire.
      - Done, now at `Audio/AudioSocketSession.cs:183` (the method `ParseUuid` starts at :178). Pinned
        by `Session_ShouldReportTheDialplanUuidAsChannelId_WhenTheCapturedUuidDistinguishesByteOrder`,
        the only test in the repository that can tell the two orders apart — B5's M4 confirms it.
- [x] B4 Fix every test frame builder that encodes the old format, in both packages. These are the
      tests that passed for six months against the wrong bytes; each one that changes is evidence of
      what the closed loop hid, so note how many there were.
      - **21 frame builders in the two packages.** ARI **12**: ten laid out inline in
        `AudioSocketProtocolTests.cs`, one per test (eight that build the parser's input, two that
        assert the bytes `WriteFrame` produces), plus the shared `BuildFrame` helper in
        `AudioSocketSessionTests.cs` and the one in `AudioSocketServerTests.cs`. VoiceAi **9**: all
        inline in `AudioSocketFrameCodecTests.cs` (seven inputs, two output-byte assertions).
      - Three more encoding sites in the same two packages: **2 UUID payload builders** that wrote the
        `Guid` little-endian (`uuid.ToByteArray()` in `AudioSocketSessionTests.cs` and in
        `AudioSocketServerTests.cs`'s `BuildUuidFrame`) and **1 type-byte table** of 13 rows in
        `AudioSocketFrameTypeTests.cs`. And **1 outside both packages**, in
        `Tests/Verbara.Sdk.Benchmarks/AudioSocketBenchmark.cs`, whose `_incompleteFrame` was hand-laid
        with a four-byte header. **25 sites in all.**
      - Those builders fed **39 tests** that changed the bytes they assert on: 26 in
        `Verbara.Sdk.Ari.Tests` (10 + 5 + 11) and 13 in `Verbara.Sdk.VoiceAi.AudioSocket.Tests`
        (10 + 3). Every one of them was green for six months against a header no Asterisk sends.
      - **4 tests deleted, 2 added.** Deleted: `ParseSilenceDuration_ShouldDecodeBigEndianUInt16`,
        `ParseSilenceDuration_ShouldReturnZero_WhenPayloadTooShort`,
        `WriteSilenceAsync_ShouldSendSilenceFrame`, `WriteSilenceAsync_ShouldThrow_WhenDisposed` — all
        four tested a frame kind Asterisk has no code for. Added: the two `WriteFrame` overflow guards
        from B1. Suite totals: Ari 476 -> **477**, AudioSocket 110 -> **107**.
      - Two `Silence` test cases became `Dtmf` rather than disappearing
        (`TryParseFrame_ShouldParseDtmfFrame`, `TryReadFrame_ShouldParseDtmfFrame_WithOneAsciiDigitOfPayload`),
        which is the delta spec's fourth scenario at the unit level.
- [x] B5 Mutations, each alone and reverted, verbatim: header back to 4 bytes; length read as 3 bytes;
      `Uuid` and `Audio` swapped back; `bigEndian: true` dropped from the ARI parse. Each must fail the
      fixture from A2. Any that does not is a fixture that is not doing its job — say so rather than
      adding a test, which is A2's closed territory.
      - **Re-run independently of the first pass, and it changed three of the answers.** Method: each
        mutation applied alone from a `sha256`-verified byte copy; **the build's exit code captured on
        its own line before any test ran**; and the **whole CI unit filter**
        (`Category!=Functional&Category!=Integration&Category!=Realtime&Category!=Spike`, 30
        assemblies) run each time rather than only `FullyQualifiedName~AudioSocketCapturedWireTests`.
        Running the whole filter is what turns "caught only by the fixture" into a measurement instead
        of an assumption. Mutation builds used `-p:TreatWarningsAsErrors=false` so the PublicAPI
        analyser's complaint about a deliberately wrong enum could not stand in for a test result; the
        final rebuild used no property overrides at all.
      - **Baseline measured here, and it is not the one the brief carries.** Build **0 Warning(s),
        0 Error(s)**, exit 0; CI unit filter **Failed: 0, Passed: 3638** across 30 assemblies. The
        brief states `Failed: 0, Passed: 3631`. **3638** is what the tree gives, both before this task
        and after it. The brief's figure predates B1's two overflow guards and B4's net test changes.
      - **A revert trap that produced a wrong verdict before it was caught, and that the brief's own
        warning does not cover.** Restoring a mutated file with `cp -p` restores its **mtime** too,
        moving it *backwards* relative to the `obj/` outputs of the mutation build. MSBuild's
        up-to-date check then skips that project, the build still reports exit 0 and `0 Error(s)`, and
        `--no-build` runs the **previous mutation's binary**. That is the failure the brief warns about
        — a test run reporting the previous mutation's verdict — arriving through a door it does not
        name: not a failed build, a *skipped* one. It hit M4's first run, where
        `Verbara.Sdk.VoiceAi.AudioSocket` still held M3's enum and an ARI-only mutation appeared to
        fail six VoiceAi tests. Every revert afterwards is `cp` + `touch`, every restore is checked
        with `sha256sum -c`, and M4 was rerun from a clean tree. Only the clean figures are below.
      - **M1 — header back to 4 bytes. Two different edits, two different answers.**
        - *Minimal, the literal edit: only the `HeaderSize` constant, `3` -> `4`, in both codecs.*
          Build exit 0, 0 Warning(s), 0 Error(s). Total **Failed: 16, Passed: 3622** — but of the 16,
          **only 4 are fixture tests and all 4 are VoiceAi's. All five ARI fixture tests PASSED.**
          (ARI reported `Failed: 5, Passed: 472`; none of those five is a captured-wire test.) ARI's
          `TryParseFrame` consumes exactly three bytes with `TryRead` whatever `HeaderSize` says — it
          uses the constant only for the `reader.Remaining < HeaderSize` guard and for `WriteFrame`'s
          offsets — so the constant alone never moves ARI's read position and the captured frames
          still parse correctly. **This contradicts the first pass's `Failed: 5, Passed: 0` (ARI) for
          this mutation**; that figure is not reachable from the constant alone.
        - *Coherent: the constant plus the fourth header byte actually consumed on ARI's read path.*
          Build exit 0, 0/0. **All 9 fixture tests fail** (5 ARI + 4 VoiceAi), plus 29 non-fixture
          tests. Total `Failed: 38, Passed: 3600`.

          ```text
          Failed Verbara.Sdk.Ari.Tests.Audio.AudioSocketCapturedWireTests.TryParseFrame_ShouldReportUuidFrameOfSixteenBytes_WhenReadingCapturedIdentificationFrame [1 ms]
          Error Message:
           Expected parsed to be True because the capture holds one whole frame Asterisk sent — 19 bytes, header included, but found False.

          Failed Verbara.Sdk.Ari.Tests.Audio.AudioSocketCapturedWireTests.Session_ShouldReportTheDialplanUuidAsChannelId_WhenFedTheCapturedIdentificationFrame [46 ms]
          Error Message:
           Expected channelId to be "11111111-2222-3333-4444-555555555555" with a length of 36 because Asterisk identified the call with the UUID the dialplan named, but "" has a length of 0, differs near "" (index 0).

          Failed Verbara.Sdk.VoiceAi.AudioSocket.Tests.AudioSocketCapturedWireTests.TryReadFrame_ShouldReportDtmfFrameOfOneByte_WhenReadingCapturedDtmfFrame [< 1 ms]
          Error Message:
           Expected read to be True because the capture holds one whole DTMF frame Asterisk sent, but found False.
          ```

        - The lesson the two variants carry: `HeaderSize` is load-bearing in the VoiceAi codec (it
          sizes the header span and the payload slice) and is **not** load-bearing on ARI's read path.
          A mutation defined as "change the constant" therefore tests two different things in the two
          packages, which is only visible because one fixture reads both.
      - **M2 — length read as 3 bytes instead of 2**, header offset left at 3 so only the length
        arithmetic changes (ARI reads a fourth byte into `b2` for `(b0 << 16) | (b1 << 8) | b2`;
        VoiceAi copies four bytes and reads `(header[1] << 16) | (header[2] << 8) | header[3]`).
        Build exit 0, 0 Warning(s), 0 Error(s). **All 9 fixture tests fail**, plus 123 non-fixture
        tests. Total `Failed: 132, Passed: 3506` — the blast radius reaches
        `Verbara.Sdk.VoiceAi.Tests` (39) and `Verbara.Sdk.VoiceAi.OpenAiRealtime.Tests` (28), which
        hang on a session that never completes a frame.

        ```text
        Failed Verbara.Sdk.Ari.Tests.Audio.AudioSocketCapturedWireTests.TryParseFrame_ShouldReportUuidFrameOfSixteenBytes_WhenReadingCapturedIdentificationFrame [1 ms]
        Error Message:
         Expected parsed to be True because the capture holds one whole frame Asterisk sent — 19 bytes, header included, but found False.

        Failed Verbara.Sdk.VoiceAi.AudioSocket.Tests.AudioSocketCapturedWireTests.TryReadFrame_ShouldReportUuidFrameCarryingTheDialplanUuid_WhenReadingCapturedIdentificationFrame [< 1 ms]
        Error Message:
         Expected read to be True because the capture holds one whole frame Asterisk sent — 19 bytes, header included, but found False.
        ```

      - **M3 — `Uuid` and `Audio` swapped back to `0x00`/`0x01`: as literally worded it DOES NOT
        COMPILE, and that is a finding the first pass does not record.** `Uuid = 0x00` collides with
        `Hangup = 0x00`, which B2 moved there, and both read pumps switch on `Uuid` and `Hangup` in the
        same `switch`. **Build exit 1, 10 Warning(s), 2 Error(s); no test was run.**

        ```text
        <repo>/src/Verbara.Sdk.VoiceAi.AudioSocket/AudioSocketSession.cs(168,25): error CS0152: The switch statement contains multiple cases with the label value '0'
        <repo>/src/Verbara.Sdk.Ari/Audio/AudioSocketSession.cs(118,25): error CS0152: The switch statement contains multiple cases with the label value '0'
        ```

        This is precisely why the build's exit code has to be captured before the test run: a
        `--no-build` run here reports M2's verdict, and M2 fails everything, so the mutation would have
        been scored "caught" without a single line of it ever executing. It also says something about
        the fix — with `Hangup = 0x00` in place, the compiler now refuses the old `Uuid = 0x00`
        outright.
      - **M3, re-run as a true swap of the two members' values** (`Uuid = 0x10`, `Audio = 0x01`), which
        is the only form of this mutation that builds. Build exit 0, **6 Warning(s)** (PublicAPI
        RS0016/RS0017 on the deliberately wrong enum), 0 Error(s). **4 fixture tests fail** — 3 of
        ARI's 5, 1 of VoiceAi's 4 — plus 8 non-fixture. Total `Failed: 12, Passed: 3626`. This
        reproduces the first pass's M3 figures exactly (`Failed: 3, Passed: 2` ARI, `Failed: 1,
        Passed: 3` VoiceAi).

        ```text
        Failed Verbara.Sdk.Ari.Tests.Audio.AudioSocketCapturedWireTests.TryParseFrame_ShouldReportUuidFrameOfSixteenBytes_WhenReadingCapturedIdentificationFrame [< 1 ms]
        Error Message:
         Expected frameType to be AudioFrameType.Uuid {value: 16}, but found AudioFrameType.Audio {value: 1}.

        Failed Verbara.Sdk.VoiceAi.AudioSocket.Tests.AudioSocketCapturedWireTests.TryReadFrame_ShouldReportUuidFrameCarryingTheDialplanUuid_WhenReadingCapturedIdentificationFrame [101 ms]
        Error Message:
         Expected frame.Type to be AudioSocketFrameType.Uuid {value: 16}, but found AudioSocketFrameType.Audio {value: 1}.

        Failed Verbara.Sdk.Ari.Tests.Audio.AudioSocketCapturedWireTests.Session_ShouldReportTheDialplanUuidAsChannelId_WhenFedTheCapturedIdentificationFrame [8 ms]
        Error Message:
         Expected trailing.IsEmpty to be True because an identification frame carries no audio, but found False.
        ```

        The five survivors are not a hole, and the first pass's reading of them holds: the fixture
        asserts frame types as the **raw byte the wire carried**, and renaming which member owns a
        value does not change that byte, so only the assertions that name an enum member can see it.
        Both packages have at least one, and both fire.
      - **M4 — `bigEndian: true` dropped from the ARI UUID parse.** Build exit 0, 0 Warning(s),
        0 Error(s). **1 fixture test fails**, plus 9 non-fixture, all ARI; VoiceAi is untouched and
        green, which is correct for an ARI-only mutation. Total `Failed: 10, Passed: 3628`.

        ```text
        Failed Verbara.Sdk.Ari.Tests.Audio.AudioSocketCapturedWireTests.Session_ShouldReportTheDialplanUuidAsChannelId_WhenTheCapturedUuidDistinguishesByteOrder [224 ms]
        Error Message:
         Expected channelId to be the same string because the sixteen bytes arrive in RFC 4122 order, most significant first, but they differ at index 0:
         ↓ (actual)
        "67452301-ab89-efcd-0…"
        ```

        The one failure is exactly the entry A3 captured for this purpose. The headline
        `11111111-2222-3333-4444-555555555555` frame passes the mutation, as A2 predicted and A3
        closed — demonstrated here rather than argued.
      - **Which mutations the shared fixture catches ALONE: none against today's suite — and that
        number is not the argument it appears to be.** Every mutation above is also caught outside the
        fixture (29, 123, 8 and 9 non-fixture tests respectively). But those tests only catch it
        because **B4 hand-edited their literals to the correct three-byte header, and the correct
        header came from this fixture.** `AudioSocketProtocolTests` says so in its own summary: "until
        it existed this file agreed happily with a four-byte header no Asterisk has ever put on a
        socket." They are downstream of the capture, not independent evidence of it: a future wrong
        "fix" would arrive with the same hand-edit to the same literals and the suite would go green
        again — which is the six months A2 documented. The fixture is the only artifact in either
        package that **cannot be edited into agreement with a wrong parser**, because its content is a
        recording rather than a claim. Measured against the suite as it stood **before** this change,
        which is the real sense of "pre-existing", the answer is **all four**: A2 and A3 recorded the
        entire pre-change suite green against the wrong format with the captured-wire tests the only
        red.
      - **What argues specifically for ONE shared fixture rather than one per package, measured rather
        than asserted:** under M1-minimal the *same one-line edit, to a constant of the same name in
        both packages*, was caught in VoiceAi (4 of 4 red) and completely invisible in ARI (0 of 5
        red), because the two parsers use that constant differently. A per-package fixture is
        maintained by whoever owns that package and drifts toward what that package's parser accepts;
        the single shared object is what put both verdicts on the same nineteen bytes and made the
        asymmetry visible at all. M4 is the same property from the other side: an ARI-only mutation
        leaves VoiceAi's four entries green and turns exactly one ARI entry red, on the same capture.
      - **Tree proved back after the last revert.** All five mutated files restored from byte copies
        and `sha256sum -c` clean; `git diff --stat` and `git status --porcelain` **byte-identical** to
        the state before this task; `sync-fence-baseline.json` untouched; no `PublicAPI.*.txt` touched
        by B5. Strict rebuild with no property overrides: **exit 0, 0 Warning(s), 0 Error(s)**. Full
        CI unit filter: **Failed: 0, Passed: 3638** across 30 assemblies, exit 0.
        `tools/audit-test-asserts.sh`: **0 violations** (449 files, ~2,973 tests).

## Phase C — close the loop that let this happen

- [x] C1 Add an `AudioSocket()` extension to `docker/functional/asterisk-config/extensions.conf` and a
      `[Trait("Category", "Functional")]` test that originates a call into it and asserts a session
      starts carrying the dialplan's UUID. This lane is Docker-gated and off the PR path (Sdk/ADR-0051),
      so run it locally and record the result with real numbers.
      - **Two extensions and two tests, not one of each — reported rather than slipped in.** The task
        text is singular; the change's whole thesis is that this repository has *two* parsers of one
        wire and that a fixture serving only one of them lets the other drift. A functional test that
        exercised a single package would leave the other's real-Asterisk round trip unproven, which is
        the same asymmetry B5 measured (`HeaderSize` back to 4: VoiceAi 4 of 4 red, ARI 0 of 5). Cost of
        the second: one more dialplan extension, one more port, one more `[Fact]`.
      - `docker/functional/asterisk-config/extensions.conf`, context `[test-functional]`:
        `exten => 700` -> `AudioSocket(4f1d9c60-7a2b-4e55-9f3d-2c6a8b0e1d47,host.docker.internal:19092)`
        for the `Verbara.Sdk.Ari` server, `exten => 701` ->
        `AudioSocket(6b3e2a18-5c94-4d07-8ae1-93f5c7204b6e,host.docker.internal:19093)` for the
        `Verbara.Sdk.VoiceAi.AudioSocket` one. `host.docker.internal` because the server under test runs
        on the host and `AsteriskContainer` already maps that name to the Docker host gateway (it is how
        the FastAGI extension at `exten => 200` reaches the host). Ports are fixed and high because a
        bind-mounted dialplan file cannot learn an ephemeral one.
      - New test: `Tests/Verbara.Sdk.FunctionalTests/Layer5_Integration/Audio/AudioSocketWireFunctionalTests.cs`,
        `[Collection("Functional")] [Trait("Category", "Functional")]`, following the fixture pattern of
        `Layer5_Integration/Dtmf/DtmfDetectionTests.cs` and `Layer5_Integration/Ari/AriStasisTests.cs`
        (`FunctionalTestBase`, `AmiConnectionFactory`, an async `OriginateAction`).
      - **It does not skip gracefully, and that is deliberate.** The neighbouring functional tests
        `return;` on a timeout, so they can pass vacuously; this one turns a timeout into a failed
        assertion with a reason. A test that can pass without reaching Asterisk is the closed loop in
        another costume.
      - **No wall-clock barrier in the file.** The handshake budget is `Task.WaitAsync(TimeSpan)` on the
        completion source, not a `Task.Delay` race, so `sync-fence-baseline.json` is untouched and
        needed no `fence-allow` marker.
      - **Rig:** the suite's own `FunctionalFixture` (Postgres + Asterisk + PSTN emulator + Toxiproxy +
        SIPp via Testcontainers, ADR-0005), Asterisk built from `<repo>/docker/Dockerfile.asterisk`
        (`FROM andrius/asterisk:22`). `app_audiosocket.so`, `res_audiosocket.so` and
        `chan_audiosocket.so` are all present in that image; `modules.conf` is `autoload = yes` and
        noloads none of them, so no module change was needed.
      - **GREEN, measured three times.** `dotnet test Tests/Verbara.Sdk.FunctionalTests/... --no-build -c Release --filter "FullyQualifiedName~AudioSocketWireFunctionalTests"`:

        ```text
        Passed Verbara.Sdk.FunctionalTests.Layer5_Integration.Audio.AudioSocketWireFunctionalTests.VoiceAiAudioSocketServer_ShouldStartASessionCarryingTheDialplanUuid_WhenAsteriskDialsIt [556 ms]
        Passed Verbara.Sdk.FunctionalTests.Layer5_Integration.Audio.AudioSocketWireFunctionalTests.AriAudioSocketServer_ShouldStartASessionCarryingTheDialplanUuid_WhenAsteriskDialsIt [527 ms]
        Passed!  - Failed:     0, Passed:     2, Skipped:     0, Total:     2
        ```

        Exit 0. **17 s wall** for the whole run including container start-up (images cached locally);
        the two handshakes themselves are 523-527 ms and 554-556 ms across runs. The fixture really ran
        — `testcontainers/ryuk` was up seconds after the run and nothing is listening on the host's
        5038/8088, so the AMI connection was the container's.
      - **Negative control, because a green functional test proves nothing until it has been made
        red.** Both enums swapped back to `Uuid = 0x10` / `Audio = 0x01` — the shape of the original
        defect — as the single mutation. Build exit captured on its own line first:
        **exit 0, 6 Warning(s)** (PublicAPI RS0050 on the deliberately wrong enum, so
        `-p:TreatWarningsAsErrors=false`), **0 Error(s)**. Then:

        ```text
        Failed Verbara.Sdk.FunctionalTests.Layer5_Integration.Audio.AudioSocketWireFunctionalTests.VoiceAiAudioSocketServer_ShouldStartASessionCarryingTheDialplanUuid_WhenAsteriskDialsIt [45 s]
        Error Message:
         Expected channelId not to be <null> because a real Asterisk dialled this server and sent its identification frame; a null here is the handshake never completing.

        Failed Verbara.Sdk.FunctionalTests.Layer5_Integration.Audio.AudioSocketWireFunctionalTests.AriAudioSocketServer_ShouldStartASessionCarryingTheDialplanUuid_WhenAsteriskDialsIt [45 s]
        Error Message:
         Expected session not to be <null> because a real Asterisk dialled this server and sent its identification frame; a null here is the handshake never completing, which is exactly the state this repository shipped for six months.
        ```

        **Both red, each after waiting out its full 45 s budget with no session ever arriving** —
        against 0.5 s to a session when the values are right. That is the pre-change state reproduced on
        demand, and it is the measurement that says these two tests are wire-sensitive rather than
        decorative.
      - Mutation reverted with `cp` + `touch` (B5's skipped-build trap), both files verified byte-identical
        by `sha256sum` against the pre-mutation copies, strict rebuild **exit 0, 0 Warning(s),
        0 Error(s)**, and the two tests re-run **Passed: 2, Failed: 0** from the clean tree.
      - Minor mismatch, recorded for whoever reads the raw capture next: `probe-capture.txt` prints
        `BYTES RECIBIDOS`, not the `BYTES RECEIVED` this change's prose quotes. Same 19 bytes, same hex.
- [x] C2 Write the ADR that supersedes ADR-0017: the protocol's source is Asterisk's `res_audiosocket.h`,
      the wire format is pinned by captured bytes, and no parser is held to its own encoder. Mark
      ADR-0017 superseded rather than editing it — it records a design that was never built, and that
      history is the point. Bump the `**N ADRs**` figure in `README.md` **and** move its row in
      `docs/claim-registry.md` in the same PR (ADR-0042 D1), keyed to the figure and never to a line.
      - **ADR-0060** — `docs/decisions/0060-the-wire-format-is-asterisks-and-no-parser-is-held-to-its-own-encoder.md`,
        "The AudioSocket wire format is Asterisk's, and no parser is held to its own encoder". Accepted,
        2026-09-23. Six rules: R1 the source is `res_audiosocket.h` and is cited where the format is
        defined; R2 one byte of type plus two of big-endian length, with Asterisk's values and no
        `Silence`; R3 no parser may be held to its own encoder; R4 one captured fixture for every parser
        in the repository; R5 the fixture grows only from a capture (which is why it holds no hangup
        frame); R6 a real Asterisk dials both servers in the functional lane. Voice and structure taken
        from `0056-*.md` and `0058-*.md` — bolded rule headings, a Consequences list that states what
        breaks, and Alternatives that name why each was refused.
      - **ADR-0017 marked superseded, body verbatim.** Exactly one line changed:
        `- **Status:** Accepted` -> `- **Status:** Superseded by [ADR-0060](...)`. Nothing else in the
        file was touched, which is both the catalog's own rule ("Once `Accepted`, never edit the body")
        and the point the task makes: it is the only record of how a design nobody built came to carry
        the authority of an accepted decision, in the exact place a reader checking the wire format
        would look.
      - **The guard couples FOUR edits in this PR, not three — this is the task text not matching the
        tree.** `StatusBlockCoherenceTests` holds a *second* ADR guard the brief does not name,
        `TheDecisionCatalog_ShouldListEveryAdrOnDisk`, which compares the **set** of `docs/decisions/*.md`
        ids against the link targets in `docs/decisions/README.md`. An ADR that lands without its row in
        that catalog is invisible to the count guard (the file is excluded by name) but fails this one.
        So: (1) the new ADR file, (2) the `**N ADRs**` figure in `README.md`, (3) the row in
        `docs/claim-registry.md`, **(4) the catalog row in `docs/decisions/README.md`** — added for
        ADR-0060, and ADR-0017's existing row annotated with its supersession, since that test's own
        message says "a superseded one keeps both".
      - **Counted, not trusted.** `find docs/decisions -maxdepth 1 -name '*.md' ! -name 'README.md' | wc -l`
        gave **57** before and **58** after; catalog rows `grep -c '^- \[ADR-'` gave 57 before and 58
        after. `0046` and `0047` are still absent on disk and that is fine — the guard is a count and a
        set, never contiguity.
      - **Both figure edits keyed to the figure.** A script asserted exactly one occurrence of
        `**57 ADRs**` in `README.md` and of `| **57 ADRs** |` in `docs/claim-registry.md` before
        replacing it with the counted number; no line number was used anywhere. The claim-registry row's
        own first column (`67`) is a pointer at `README.md`'s line and was re-verified as still correct
        after the edit, so it did not move.
      - The brief's location for the guard is right: it is in `Tests/Verbara.Sdk.OpenTelemetry.Tests/StatusBlockCoherenceTests.cs`,
        not in `Verbara.Sdk.Governance.Tests`.
      - **Verification.** `dotnet build Verbara.Sdk.slnx -c Release` -> exit 0 captured on its own line,
        **0 Warning(s), 0 Error(s)**. All three guards green:

        ```text
        Passed Verbara.Sdk.OpenTelemetry.Tests.StatusBlockCoherenceTests.TheHeadlineVersion_ShouldMatchTheVersionThePackagesShipWith [4 ms]
        Passed Verbara.Sdk.OpenTelemetry.Tests.StatusBlockCoherenceTests.ThePublishedAdrCount_ShouldMatchTheDecisionsOnDisk [< 1 ms]
        Passed Verbara.Sdk.OpenTelemetry.Tests.StatusBlockCoherenceTests.TheDecisionCatalog_ShouldListEveryAdrOnDisk [3 ms]
        ```

        Full CI unit filter (`Category!=Functional&Category!=Integration&Category!=Realtime&Category!=Spike`):
        **Failed: 0, Passed: 3638** across **30** assemblies, exit 0 — identical to B5's post-fix
        baseline, as it must be, since C1 adds only `Category=Functional` tests and C2 adds only
        documents. `Verbara.Sdk.Governance.Tests` **129/129**. `tools/audit-test-asserts.sh`
        **0 violations** (450 files, ~2,975 tests — 449 files before C1's new test).
        `git diff --stat -- sync-fence-baseline.json` empty. `PublicAPI.*.txt` unchanged by C1 and C2:
        the only PublicAPI movement in this branch is still B2's, 10 added lines in
        `src/Verbara.Sdk.Ari/PublicAPI.Unshipped.txt` and 11 in
        `src/Verbara.Sdk.VoiceAi.AudioSocket/PublicAPI.Unshipped.txt`.
- [x] C3 `dotnet build Verbara.Sdk.slnx -c Release`: 0 warnings, 0 errors, exit code captured on its own
      line before any test runs.

      **`0 Warning(s), 0 Error(s)`**, exit 0, captured on its own line before any test ran.
- [x] C4 Unit lane green under the CI filter with `Verbara.Sdk.Governance.Tests` included,
      `tools/audit-test-asserts.sh` at zero, and `git diff --stat` showing `sync-fence-baseline.json`
      unchanged. `PublicAPI.*.txt` **will** change if a member is added or removed — say exactly what
      moved and why, rather than assuming it is untouched.

      **Failed: 0, Passed: 3638** across 30 assemblies. `Verbara.Sdk.Governance.Tests` **129/129**.
      `tools/audit-test-asserts.sh` **0 violations**. `sync-fence-baseline.json` unchanged.

      **`PublicAPI.Shipped.txt` is untouched in both packages** — verified, not assumed. The changes go
      into `PublicAPI.Unshipped.txt` as removals plus replacements, which is the analyser's documented
      route and the form `src/Verbara.Sdk.Push` already uses. Net 11 removals, 10 additions: the five
      invented values in each enum out, the five measured ones in, plus `WriteSilenceAsync` removed
      because `res_audiosocket.h` defines no silence frame. `AudioSlin12..192` appear nowhere, because
      their values did not move and listing them would be churn.
- [x] C5 Coverage after committing, never before. Read the changed-line count and check it against the
      size of the diff before believing the percentage.

      **Measured after committing**, which is what this task exists to force.

      | gate | result |
      |---|---|
      | patch coverage | **100.0%** — 15/15 changed executable lines, floor 85.0% |
      | line coverage | **84.31%**, band `[83.0, 86.0]` |
      | branch coverage | **68.57%**, floor 64.0% |
      | exclusion markers | **0**, baseline 0 |

      **The changed-line count was sanity-checked rather than believed**, because a sibling change
      accepted `100% (6/6)` on a five-file diff and the `6` was the tell. Here 15 is right and the
      reason is structural: the two enums are 35 lines each of **declarations**, which carry no
      executable statements, so `diff-cover` does not list those files at all. What remains is 6 lines
      of code in `AudioSocketProtocol.cs`, the equivalent in `AudioSocketFrameCodec.cs`, the
      big-endian UUID line in `AudioSocketSession.cs`, and the fixture — all four files at 100%. The
      126-line insertion count in `git diff --stat` is enum members, comments and PublicAPI rows.
- [x] C6 `CHANGELOG.md` `[Unreleased]`: a `### Changed — BREAKING` entry stating that the wire format
      was wrong, that neither server could complete a handshake, and what a consumer must do. Give it
      its **own insertion anchor** distinct from any other in-flight entry — a shared anchor is what
      ejected #300 from the merge queue. Leave `(#N)` for close-out.

      **`### Changed — BREAKING`**, and anchored on the **first existing entry heading** rather than on
      `## [Unreleased]`. That is deliberate: #299 and #300 both inserted at the top of `[Unreleased]`
      and collided, which ejected #300 from the merge queue as `DIRTY` with all fourteen checks green.
      Resolving it also left a duplicated heading that had to be spotted by hand. A distinct anchor
      costs nothing and removes the whole class.

      The entry leads with the measurement rather than the diagnosis — the captured bytes, the frame
      counts, and Asterisk's own timeout line — because a reader who doubts the claim should be able to
      check it rather than take it. It states what a consumer must do (recompile), and marks the one
      claim that is reasoned rather than measured: that nobody can have depended on the old values for
      anything that worked. `(#N)` left for close-out.
- [x] C7 `openspec validate --all --strict` green, and CI green on the PR.

      `openspec validate --all --strict` -> **Totals: 13 passed, 0 failed**, exit 0. CI on the PR is
      recorded at close-out.
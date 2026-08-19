"""Unit tests for probe-provider-conformance.py (the committed conformance instrument).

Stdlib unittest only -- NO pip deps, matching the other guard-script tests, and no network: every
rule tested here is one that can be wrong without touching a vendor, which is exactly why it is
worth gating on every PR.

Each test names the failure it exists to prevent. All three rules were broken by hand first.
"""
import contextlib
import importlib.util
import io
import json
import os
import unittest
import unittest.mock

_HERE = os.path.dirname(os.path.abspath(__file__))
_SCRIPT = os.path.join(_HERE, os.pardir, "probe-provider-conformance.py")

_spec = importlib.util.spec_from_file_location("probe_provider_conformance", _SCRIPT)
probe = importlib.util.module_from_spec(_spec)
_spec.loader.exec_module(probe)


class RedactionTests(unittest.TestCase):
    """§5.4 -- the rule said 'never echoed'; the code said otherwise."""

    def test_ShouldRedactAnArrayValuedIdentifierField_WhenItHoldsTheIdentifiers(self):
        # THE regression. `additional_model_uuids` is an array of identifiers, and the previous
        # redactor tested the value's type before the key, so it walked straight past this and a
        # raw identifier reached the operator's screen on 2026-08-15.
        payload = {"additional_model_uuids": ["1111-a", "2222-b"], "type": "Metadata"}

        out = probe.redact(payload)

        self.assertEqual(probe.REDACTED, out["additional_model_uuids"])
        self.assertNotIn("1111-a", json.dumps(out))
        self.assertEqual("Metadata", out["type"], "non-correlating fields must survive verbatim")

    def test_ShouldRedactTheField_WhateverTheValuesType(self):
        # A redactor keyed on the value's type has one blind spot per type it forgot. Keying on the
        # field name has none, so this asserts across the shapes a vendor actually sends.
        for value in ("a-string", ["in", "a", "list"], {"nested": "object"}, 12345, None, True):
            with self.subTest(value=value):
                self.assertEqual(probe.REDACTED, probe.redact({"request_id": value})["request_id"])

    def test_ShouldRedactAtAnyDepth_WhenTheIdentifierIsNested(self):
        payload = {"results": [{"meta": {"context_id": "leak-me", "text": "keep-me"}}]}

        out = probe.redact(payload)

        self.assertNotIn("leak-me", json.dumps(out))
        self.assertIn("keep-me", json.dumps(out))

    def test_ShouldMatchTheKeyCaseInsensitively_WhenTheVendorCapitalisesDifferently(self):
        # Header-style keys arrive in whatever case the vendor chose; the rule is about the field,
        # not about its spelling.
        self.assertEqual(probe.REDACTED, probe.redact({"Request-Id": "x"})["Request-Id"])

    def test_ShouldTruncateAndRedact_WhenRenderingAPayloadForPrinting(self):
        out = probe.render({"request_id": "secret", "pad": "y" * 900})

        self.assertNotIn("secret", out)
        self.assertTrue(out.endswith("…"))


class ControlContractTests(unittest.TestCase):
    """§5.3 / ADR-0049 D4 -- two controls answer two different questions."""

    def _control(self, kind):
        return probe.Control(kind, "deliberately wrong", "measured answer")

    def test_ShouldRefuseToConstruct_WhenTheInvalidCredentialControlIsMissing(self):
        # The failure this prevents: a run with only a wrong-path control proves the probe can tell
        # routes apart and says NOTHING about whether it can tell credentials apart -- which is
        # precisely the inference ADR-0049 D3 was written to forbid.
        with self.assertRaises(ValueError) as caught:
            probe.ProbeSpec(name="s", origin="https://example.invalid", route="/", transport="http",
                            controls=(self._control(probe.ROUTE),))

        self.assertIn(probe.CREDENTIAL, str(caught.exception))

    def test_ShouldRefuseToConstruct_WhenTheWrongPathControlIsMissing(self):
        with self.assertRaises(ValueError) as caught:
            probe.ProbeSpec(name="s", origin="https://example.invalid", route="/", transport="http",
                            controls=(self._control(probe.CREDENTIAL),))

        self.assertIn(probe.ROUTE, str(caught.exception))

    def test_ShouldRefuseToConstruct_WhenThereAreNoControlsAtAll(self):
        with self.assertRaises(ValueError):
            probe.ProbeSpec(name="s", origin="https://example.invalid", route="/", transport="http")

    def test_ShouldConstruct_WhenBothControlsArePresent(self):
        spec = probe.ProbeSpec(
            name="s", origin="https://example.invalid", route="/", transport="http",
            controls=(self._control(probe.ROUTE), self._control(probe.CREDENTIAL)))

        self.assertEqual(2, len(spec.controls))

    def test_ShouldRejectAControl_WhenItDoesNotSayWhatTheVendorAnswered(self):
        # `expected` holds a MEASURED answer. A control with no recorded answer cannot go loud when
        # the vendor's behaviour changes underneath it.
        with self.assertRaises(ValueError):
            probe.Control(probe.ROUTE, "wrong path", "")


class DepthRuleTests(unittest.TestCase):
    """§5.11 -- a handshake is not a measurement."""

    def _ws(self, validation_point):
        return probe.ProbeSpec(
            name="s", origin="wss://example.invalid", route="/", transport="ws",
            validation_point=validation_point,
            controls=(probe.Control(probe.ROUTE, "wrong path", "404"),
                      probe.Control(probe.CREDENTIAL, "bad key", "close 4001")))

    def test_ShouldRefuseAVerdict_WhenAWsRunStoppedAtTheUpgradeAndValidationIsInBand(self):
        # The Speechmatics case: `101` is returned to a REJECTED credential and the close code
        # arrives afterwards. Stopping at the upgrade would have recorded an unusable provider as
        # verified-good -- the strongest single argument for this whole instrument.
        allowed, reason = self._ws(probe.IN_BAND).verdict_allowed(reached_first_exchange=False)

        self.assertFalse(allowed)
        self.assertIn("4001", reason)

    def test_ShouldRefuseAVerdict_WhenTheValidationPointHasNotBeenMeasured(self):
        # Unmeasured is not "probably handshake". It is unmeasured.
        allowed, _ = self._ws(probe.UNMEASURED).verdict_allowed(reached_first_exchange=False)

        self.assertFalse(allowed)

    def test_ShouldAllowAVerdict_WhenValidationWasMeasuredToBeAtTheHandshake(self):
        # Deepgram: a malformed key is refused with 401 at the upgrade, measured on both routes,
        # so for THIS surface the upgrade genuinely carries the answer.
        allowed, _ = self._ws(probe.HANDSHAKE).verdict_allowed(reached_first_exchange=False)

        self.assertTrue(allowed)

    def test_ShouldAllowAVerdict_WhenTheRunReachedTheFirstProtocolExchange(self):
        allowed, _ = self._ws(probe.IN_BAND).verdict_allowed(reached_first_exchange=True)

        self.assertTrue(allowed)

    def test_ShouldAllowAVerdict_WhenTheSurfaceIsHttp_BecauseTheResponseIsTheExchange(self):
        spec = probe.ProbeSpec(
            name="s", origin="https://example.invalid", route="/", transport="http",
            controls=(probe.Control(probe.ROUTE, "wrong path", "404"),
                      probe.Control(probe.CREDENTIAL, "bad key", "403")))

        allowed, _ = spec.verdict_allowed(reached_first_exchange=False)

        self.assertTrue(allowed)


class WorkedExampleTests(unittest.TestCase):
    """§5.3 / §5.6 -- the reference run is encoded, not remembered."""

    def test_ShouldCarryBothControls_ForEveryEncodedExample(self):
        for spec in probe.WORKED_EXAMPLES:
            with self.subTest(surface=spec.name):
                self.assertEqual({probe.ROUTE, probe.CREDENTIAL}, {c.kind for c in spec.controls})

    def test_ShouldRecordTheDeepgramFrameShape_SoTheClassBAbsenceStaysStated(self):
        # Deepgram is the one TTS surface measured NOT to hide base64 audio in a text frame. That
        # negative finding is as load-bearing as the positive ones and must not decay into silence.
        deepgram = next(s for s in probe.WORKED_EXAMPLES if s.name == "deepgram-tts")

        self.assertIn("1920", deepgram.notes)
        self.assertIn("Class B", deepgram.notes)

    def test_ShouldRecordSpeechmaticsAsInBand_BecauseItsUpgradeProvesNothing(self):
        sm = next(s for s in probe.WORKED_EXAMPLES if s.name == "speechmatics-stt")

        self.assertEqual(probe.IN_BAND, sm.validation_point)


class SelfCheckTests(unittest.TestCase):
    """The liveness fence, mirroring check-recording-redaction.py's."""

    def test_ShouldPass_WhenEveryRuleStillRefusesWhatItExistsToRefuse(self):
        self.assertEqual(0, probe.self_check())


class _Recorder:
    """Stands in for the transport so the request SHAPE can be gated without sending anything.

    The runner functions are the part of section 4 that can be wrong offline: a wrong path, a
    credential on the wrong arm, a frame the client never sends. Those are exactly the mistakes
    that would make a live run agree with a broken client, so they are tested here rather than
    discovered by a vendor's 400.
    """

    def __init__(self):
        self.calls = []

    def http(self, arm, url, headers, body=None, method="GET", timeout=30):
        self.calls.append({"arm": arm, "url": url, "headers": dict(headers),
                           "body": body, "method": method, "sends": []})
        return probe.Exchange(arm=arm, status="200")

    def ws(self, arm, url, headers, sends, first_exchange, audio_of, terminator=None,
           idle_timeout=20):
        self.calls.append({"arm": arm, "url": url, "headers": dict(headers),
                           "body": None, "method": "GET", "sends": list(sends),
                           "first_exchange": first_exchange, "audio_of": audio_of,
                           "terminator": terminator})
        return probe.Exchange(arm=arm, status="101", reached_first_exchange=True)

    @property
    def last(self):
        return self.calls[-1]

    def text_frames(self):
        return [json.loads(payload) for opcode, payload in self.last["sends"]
                if opcode == probe.OPCODE_TEXT and payload.strip().startswith(b"{")]

    def wire(self):
        """Everything that would go on the wire for the last call, as one searchable blob."""
        parts = [self.last["url"], json.dumps(self.last["headers"])]
        parts += [repr(payload) for _, payload in self.last["sends"]]
        if self.last["body"]:
            parts.append(repr(self.last["body"]))
        return " ".join(parts)


#: A value no vendor issued, distinguishable from BAD_KEY, so a test can tell "sent the real
#: credential" apart from "sent the deliberately invalid one".
FAKE_REAL_KEY = "sentinel-real-key-value"


def _env_with_every_key(**extra):
    env = {name: FAKE_REAL_KEY for name in probe.KEY_ENV.values()}
    env.update(extra)
    return env


def _run(surface, arm, recorder, voice="voice-id-under-test"):
    runner, _ = probe.LIVE_SURFACES[surface]
    kwargs = {"voice_id": voice} if surface in ("cartesia-tts", "elevenlabs-tts") else {}
    with unittest.mock.patch.object(probe, "probe_http", recorder.http), \
            unittest.mock.patch.object(probe, "probe_ws", recorder.ws), \
            unittest.mock.patch.dict(os.environ, _env_with_every_key(), clear=False):
        return runner(arm, probe._key_for(surface, arm), **kwargs)


class LiveSurfaceContractTests(unittest.TestCase):
    """The eight surfaces this change fixed, gated for shape without sending a byte."""

    def test_ShouldCarryBothControls_ForEveryLiveSurface(self):
        # Same rule as the worked examples, asserted again over the runnable table: a surface added
        # later with one control would otherwise ship silent about the question it never asked.
        for name, (_, spec) in probe.LIVE_SURFACES.items():
            with self.subTest(surface=name):
                self.assertEqual({probe.ROUTE, probe.CREDENTIAL},
                                 {c.kind for c in spec.controls})

    def test_ShouldKnowWhichCredentialEachSurfaceNeeds_ForEveryLiveSurface(self):
        # A surface missing from KEY_ENV raises KeyError mid-run, after the earlier arms have
        # already been billed.
        self.assertEqual(set(probe.LIVE_SURFACES), set(probe.KEY_ENV))

    def test_ShouldSendADifferentPath_WhenRunningTheRouteArm(self):
        # The wrong-path control IS the different path. A route arm that reuses the shipped URL
        # measures nothing and reports 'verified'.
        for name in sorted(probe.LIVE_SURFACES):
            with self.subTest(surface=name):
                rec = _Recorder()
                _run(name, probe.SHIPPED, rec)
                shipped_url = rec.last["url"]
                _run(name, probe.ROUTE, rec)

                self.assertNotEqual(shipped_url, rec.last["url"])

    def test_ShouldNeverSendTheRealCredential_WhenRunningTheCredentialArm(self):
        # The failure this prevents is silent and expensive: the credential arm authenticates, the
        # vendor answers 200, and the run records 'the invalid credential was accepted' -- an
        # inverted finding, paid for at full rate.
        for name in sorted(probe.LIVE_SURFACES):
            with self.subTest(surface=name):
                rec = _Recorder()
                _run(name, probe.CREDENTIAL, rec)

                self.assertNotIn(FAKE_REAL_KEY, rec.wire())
                self.assertIn(probe.BAD_KEY, rec.wire())

    def test_ShouldSendTheRealCredential_WhenRunningTheShippedArm(self):
        for name in sorted(probe.LIVE_SURFACES):
            with self.subTest(surface=name):
                rec = _Recorder()
                _run(name, probe.SHIPPED, rec)

                self.assertIn(FAKE_REAL_KEY, rec.wire())
                self.assertNotIn(probe.BAD_KEY, rec.wire())

    def test_ShouldAddressTheSameOriginOnEveryArm_SoTheControlIsAControl(self):
        # ADR-0049 D4: a control on a different host compares two unknowns. Same host, same run.
        for name, (_, spec) in sorted(probe.LIVE_SURFACES.items()):
            with self.subTest(surface=name):
                host = probe.urllib.parse.urlsplit(spec.origin).netloc
                for arm in (probe.SHIPPED, probe.ROUTE, probe.CREDENTIAL):
                    rec = _Recorder()
                    _run(name, arm, rec)
                    self.assertEqual(host, probe.urllib.parse.urlsplit(rec.last["url"]).netloc)


class ShippedRequestShapeTests(unittest.TestCase):
    """Per-surface shapes read out of the shipped client, each pinning a specific defect."""

    def test_ShouldCarryTheLmntKeyInTheFirstFrame_NotAsAHeader(self):
        # LMNT WS is the one surface whose credential does not ride the upgrade. A probe that sent
        # it as a header would be rejected in band and the run would be read as a route problem.
        rec = _Recorder()
        _run("lmnt-ws", probe.SHIPPED, rec)

        self.assertEqual({}, rec.last["headers"])
        self.assertEqual(FAKE_REAL_KEY, rec.text_frames()[0]["X-API-Key"])

    def test_ShouldOmitTheLmntModelMember_BecauseSendingItNullIsATotalOutage(self):
        # Measured: `"model":null` draws 1002 and zero audio. Omitted, not nulled.
        rec = _Recorder()
        _run("lmnt-ws", probe.SHIPPED, rec)

        self.assertNotIn("model", rec.text_frames()[0])

    def test_ShouldSendCartesiaTtsContinueAsNull_BecauseTheClientDoes(self):
        # An A/B refuted the theory that `continue: null` caused rejection, so the probe keeps the
        # shape the client actually sends. A tidier probe would be testing a client nobody ships.
        rec = _Recorder()
        _run("cartesia-tts", probe.SHIPPED, rec)
        frame = rec.text_frames()[0]

        self.assertIn("continue", frame)
        self.assertIsNone(frame["continue"])

    def test_ShouldSendNoOpeningJsonFrame_WhenProbingCartesiaStt(self):
        # This socket takes its four session parameters in the query string and treats an opening
        # JSON frame as a protocol error. That asymmetry with Cartesia TTS is the defect this
        # surface fixed, so the probe has to reproduce it exactly.
        rec = _Recorder()
        _run("cartesia-stt", probe.SHIPPED, rec)

        self.assertEqual([], rec.text_frames())
        query = probe.urllib.parse.parse_qs(probe.urllib.parse.urlsplit(rec.last["url"]).query)
        self.assertEqual(["ink-whisper"], query["model"])
        self.assertEqual(["pcm_s16le"], query["encoding"])
        self.assertEqual([str(probe.SOURCE_SAMPLE_RATE)], query["sample_rate"])

    def test_ShouldTerminateCartesiaSttWithTheBareWordDone_NotJson(self):
        rec = _Recorder()
        _run("cartesia-stt", probe.SHIPPED, rec)

        self.assertEqual((probe.OPCODE_TEXT, b"done"), rec.last["sends"][-1])

    def test_ShouldCoalesceAssemblyAiAudio_SoTheDurationWindowIsNotViolated(self):
        # The vendor enforces 50-1000 ms against the DECLARED rate: at 8 kHz a 320-byte frame is
        # 20 ms and draws 3007. A probe sending raw frames would measure its own bug and report it
        # as the client's.
        rec = _Recorder()
        _run("assemblyai-stt", probe.SHIPPED, rec)
        audio = [payload for opcode, payload in rec.last["sends"]
                 if opcode == probe.OPCODE_BINARY]

        self.assertTrue(audio, "the probe must actually send audio")
        for chunk in audio:
            self.assertGreaterEqual(len(chunk), 800)

    def test_ShouldSendAssemblyAiKeyRaw_WithoutABearerPrefix(self):
        rec = _Recorder()
        _run("assemblyai-stt", probe.SHIPPED, rec)

        self.assertEqual(FAKE_REAL_KEY, rec.last["headers"]["Authorization"])

    def test_ShouldWaitForRecognitionStarted_WhenProbingSpeechmaticsStt(self):
        # §5.11: the 101 proves nothing here -- a rejected key gets one too, then close 4001. The
        # first exchange is RecognitionStarted, and 7.5 names it as the thing to measure.
        rec = _Recorder()
        _run("speechmatics-stt", probe.SHIPPED, rec)

        self.assertTrue(rec.last["first_exchange"](probe.OPCODE_TEXT,
                                                   b'{"message":"RecognitionStarted"}'))
        self.assertFalse(rec.last["first_exchange"](probe.OPCODE_TEXT, b'{"message":"Info"}'))
        self.assertEqual("StartRecognition", rec.text_frames()[0]["message"])

    def test_ShouldCountAudioFromTheVendorsOwnCarrier_ForEveryTtsSurface(self):
        # Each vendor names the audio member differently and two of them base64 it. Counting the
        # wrong member reports 'no audio' against a surface that produced plenty -- which is the
        # exact shape of the Cartesia silent-success defect.
        cases = [("cartesia-tts", b'{"type":"chunk","data":"AAAA"}'),
                 ("elevenlabs-tts", b'{"audio":"AAAA"}')]
        for name, message in cases:
            with self.subTest(surface=name):
                rec = _Recorder()
                _run(name, probe.SHIPPED, rec)

                self.assertEqual(3, rec.last["audio_of"](probe.OPCODE_TEXT, message))
                self.assertEqual(0, rec.last["audio_of"](probe.OPCODE_TEXT, b'{"type":"done"}'))

    def test_ShouldStopWhereTheShippedClientStops_ForTheTwoVendorsThatDoNotClose(self):
        # LMNT WS breaks on `finish`, Cartesia TTS on `type:"done"`. Both hold the socket open
        # afterwards, so a probe without a terminator sits there and then reports the wait as
        # "the vendor sent nothing further" -- an anomaly it manufactured itself (2026-08-19).
        cases = [("lmnt-ws", b'{"type":"finish"}'), ("cartesia-tts", b'{"type":"done"}')]
        for name, terminal in cases:
            with self.subTest(surface=name):
                rec = _Recorder()
                _run(name, probe.SHIPPED, rec)

                self.assertIsNotNone(rec.last["terminator"])
                self.assertTrue(rec.last["terminator"](probe.OPCODE_TEXT, terminal))

    def test_ShouldNotMistakeAChunksDoneMemberForTheTerminator(self):
        # Every Cartesia chunk carries `"done": false`. A substring test matches it and declares
        # the synthesis complete on the first frame, reporting a few hundred bytes of a 72 KB
        # stream as the whole answer.
        rec = _Recorder()
        _run("cartesia-tts", probe.SHIPPED, rec)
        chunk = b'{"type":"chunk","data":"AAAA","done":false,"status_code":206}'

        self.assertFalse(rec.last["terminator"](probe.OPCODE_TEXT, chunk))

    def test_ShouldReturnAtTheTerminator_WithoutWaitingOutTheIdleTimeout(self):
        # The behaviour, not just the predicate: proven against a fake socket so no vendor is paid
        # to demonstrate that the loop stops.
        ex = probe.Exchange(arm="shipped")
        self.assertFalse(ex.saw_terminator)
        self.assertNotIn("terminator", ex.line())

        ex.saw_terminator = True
        self.assertIn("vendor's terminator", ex.line())

    def test_ShouldCutTheCommittedCaptureIntoTwentyMillisecondFrames(self):
        frames = probe.source_frames()

        self.assertTrue(frames, "the committed 8 kHz capture must be readable")
        self.assertEqual({320}, {len(f) for f in frames})


@contextlib.contextmanager
def _quiet():
    """The runner prints for an operator; a test report is not an operator."""
    buffer = io.StringIO()
    with contextlib.redirect_stdout(buffer), contextlib.redirect_stderr(buffer):
        yield buffer


class HttpAccountingTests(unittest.TestCase):
    """What counts as audio, and what merely has a length."""

    def _urlopen_raising(self, code, reason, body):
        def opener(request, timeout=None):
            raise probe.urllib.error.HTTPError(
                request.full_url, code, reason, {}, io.BytesIO(body))
        return opener

    def test_ShouldNotCountAnErrorBodyAsAudio_WhenTheVendorRejectsTheCredential(self):
        # Measured on 2026-08-19: Speechmatics TTS answers a bad Bearer with 401 and a 172-byte
        # plain-text body. Counting that as audio makes the credential control read as though the
        # invalid key had produced speech.
        with unittest.mock.patch.object(probe.urllib.request, "urlopen",
                                        self._urlopen_raising(401, "Unauthorized", b"no" * 86)):
            ex = probe.probe_http(probe.CREDENTIAL, "https://example.invalid/x", {})

        self.assertEqual("401 Unauthorized", ex.status)
        self.assertEqual(0, ex.audio_bytes)
        self.assertIn("172 B body", ex.messages[0])

    def test_ShouldCountTheBodyAsAudio_WhenTheVendorAnswersTwoHundred(self):
        response = unittest.mock.MagicMock()
        response.__enter__.return_value = response
        response.read.return_value = b"\x00\x01" * 50
        response.status, response.reason = 200, "OK"
        with unittest.mock.patch.object(probe.urllib.request, "urlopen",
                                        lambda *a, **k: response):
            ex = probe.probe_http(probe.SHIPPED, "https://example.invalid/x", {})

        self.assertEqual(100, ex.audio_bytes)


class NotCharacterisedTests(unittest.TestCase):
    """§7.7 -- a surface that cannot be reached is recorded as unreached, never as verified."""

    def test_ShouldRefuseToProbe_WhenTheCredentialIsNotInTheEnvironment(self):
        with unittest.mock.patch.dict(os.environ, {probe.KEY_ENV["lmnt-http"]: ""}, clear=False):
            with _quiet() as out:
                self.assertIsNone(probe.run_surface("lmnt-http", {}))
        self.assertIn(probe.KEY_ENV["lmnt-http"], out.getvalue(),
                      "the operator must be told which name to set")

    def test_ShouldRefuseToProbe_WhenTheSurfaceNeedsAVoiceIdAndNoneWasGiven(self):
        # Cartesia TTS and ElevenLabs TTS have no default voice in the shipped client. Guessing one
        # turns a missing parameter into a vendor 400 recorded as a conformance finding.
        for name in ("cartesia-tts", "elevenlabs-tts"):
            with self.subTest(surface=name):
                env = _env_with_every_key()
                with unittest.mock.patch.dict(os.environ, env, clear=False):
                    with _quiet():
                        self.assertIsNone(probe.run_surface(name, {name: ""}))

    def test_ShouldUseTheDocumentedPublicVoice_WhenElevenLabsHasNoOverride(self):
        env = {"VERBARA_ELEVENLABS_VOICE_ID": "", "VERBARA_CARTESIA_VOICE_ID": ""}
        with unittest.mock.patch.dict(os.environ, env, clear=False):
            voices = probe.resolve_voices()

        self.assertEqual("EXAVITQu4vr4xnSDxMaL", voices["elevenlabs-tts"])
        self.assertEqual("", voices["cartesia-tts"],
                         "Cartesia publishes no sample voice, so none may be invented")

    def test_ShouldRecordAssemblyAiRouteControlAsNonDiscriminating_NotAsAbsent(self):
        # The honest encoding of a control whose measured answer is 'this host does not tell paths
        # apart'. Dropping the control would have read as 'not applicable'; keeping it records that
        # a route defect on this host is undetectable by path.
        _, spec = probe.LIVE_SURFACES["assemblyai-stt"]
        route = next(c for c in spec.controls if c.kind == probe.ROUTE)

        self.assertIn("does not discriminate", route.expected)


class ProbeCliTests(unittest.TestCase):
    """The entry point is hand-run, so its guardrails are the only ones a tired operator gets."""

    def test_ShouldExitTwo_WhenTheSurfaceIsMisspelled(self):
        with _quiet() as out:
            self.assertEqual(2, probe.main(["--probe", "cartesia-ttts"]))
        self.assertIn("cartesia-ttts", out.getvalue())

    def test_ShouldNotProbeAnything_WhenOnlyListingOrSelfChecking(self):
        # A --list that reached the network would bill the operator for reading a table.
        def explode(*_args, **_kwargs):
            raise AssertionError("no network on --list/--self-check")

        with unittest.mock.patch.object(probe, "probe_http", explode), \
                unittest.mock.patch.object(probe, "probe_ws", explode):
            with _quiet():
                self.assertEqual(0, probe.main(["--list"]))
                self.assertEqual(0, probe.main(["--self-check"]))


if __name__ == "__main__":
    unittest.main()

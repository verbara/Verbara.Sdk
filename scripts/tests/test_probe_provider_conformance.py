"""Unit tests for probe-provider-conformance.py (the committed conformance instrument).

Stdlib unittest only -- NO pip deps, matching the other guard-script tests, and no network: every
rule tested here is one that can be wrong without touching a vendor, which is exactly why it is
worth gating on every PR.

Each test names the failure it exists to prevent. All three rules were broken by hand first.
"""
import importlib.util
import json
import os
import unittest

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


if __name__ == "__main__":
    unittest.main()

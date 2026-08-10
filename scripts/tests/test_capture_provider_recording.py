"""Unit tests for capture-provider-recording.py (the STT fixture capture tool).

Covers the pure helpers only -- request shaping, redaction, normalization and the provenance
sidecar. The network path is deliberately untested: it needs a live provider credential, and a
tool whose tests demand a secret is a tool nobody runs. Stdlib unittest only, matching the
other script tests.

The module name carries hyphens, so it is loaded by path rather than imported.
"""
import hashlib
import importlib.util
import json
import os
import unittest
import wave
import io

_HERE = os.path.dirname(os.path.abspath(__file__))
_SCRIPT = os.path.join(_HERE, os.pardir, "capture-provider-recording.py")

_spec = importlib.util.spec_from_file_location("capture_provider_recording", _SCRIPT)
capture = importlib.util.module_from_spec(_spec)
_spec.loader.exec_module(capture)


class WavFromPcmTests(unittest.TestCase):
    def test_ShouldProduceCanonicalRiffWaveHeader(self):
        pcm = b"\x01\x02" * 100

        result = capture.wav_from_pcm(pcm)

        self.assertEqual(b"RIFF", result[:4])
        self.assertEqual(b"WAVE", result[8:12])
        self.assertEqual(44, len(result) - len(pcm), "header must be the canonical 44 bytes")

    def test_ShouldPreserveSampleFormatOfTheSourceCapture(self):
        pcm = b"\x00\x00" * 4000

        with wave.open(io.BytesIO(capture.wav_from_pcm(pcm)), "rb") as handle:
            self.assertEqual(1, handle.getnchannels())
            self.assertEqual(2, handle.getsampwidth())
            self.assertEqual(8000, handle.getframerate())
            self.assertEqual(4000, handle.getnframes())

    def test_ShouldRoundTripPcmBytesExactly(self):
        # Bytes above 0x7F are where a text-mode bug would show up.
        pcm = bytes(range(256)) * 2

        with wave.open(io.BytesIO(capture.wav_from_pcm(pcm)), "rb") as handle:
            self.assertEqual(pcm, handle.readframes(handle.getnframes()))


class BuildMultipartTests(unittest.TestCase):
    def _body(self, **overrides):
        kwargs = {
            "boundary": "BOUND",
            "file_field": "file",
            "filename": "audio.wav",
            "file_bytes": b"RIFFdata",
            "text_fields": {"model": "whisper-1", "language": "es"},
        }
        kwargs.update(overrides)
        return capture.build_multipart(**kwargs)

    def test_ShouldOmitContentType_WhenPartIsTheAudioFile(self):
        # The SDK adds ByteArrayContent without a Content-Type; the capture must match, or it
        # is taken against a request shape production never sends.
        body = self._body().decode("latin-1")
        file_part = body.split("--BOUND")[1]

        self.assertIn('name="file"; filename="audio.wav"', file_part)
        self.assertNotIn("Content-Type", file_part)

    def test_ShouldDeclareTextPlainUtf8_WhenPartIsAStringField(self):
        body = self._body().decode("latin-1")

        self.assertIn(
            'Content-Disposition: form-data; name="model"\r\n'
            "Content-Type: text/plain; charset=utf-8\r\n\r\nwhisper-1",
            body,
        )

    def test_ShouldTerminateWithTheClosingBoundary(self):
        self.assertTrue(self._body().endswith(b"--BOUND--\r\n"))

    def test_ShouldEmbedFileBytesVerbatim(self):
        payload = bytes(range(256))

        self.assertIn(payload, self._body(file_bytes=payload))

    def test_ShouldRaise_WhenBoundaryIsEmpty(self):
        with self.assertRaises(ValueError):
            self._body(boundary="")


class RedactTests(unittest.TestCase):
    def test_ShouldReplaceSecretWithThePlaceholder(self):
        secret = "sk-" + "0123456789abcdef"

        result = capture.redact(f"leaked {secret} here", [secret])

        self.assertNotIn(secret, result)
        self.assertEqual("leaked REDACTED-API-KEY here", result)

    def test_ShouldReplaceEveryOccurrence(self):
        secret = "tok" + "-abc"

        result = capture.redact(f"{secret} and {secret}", [secret])

        self.assertEqual(2, result.count(capture.PLACEHOLDER_API_KEY))

    def test_ShouldLeaveTextIntact_WhenSecretIsEmpty(self):
        # Guard against str.replace("") splicing the placeholder between every character.
        self.assertEqual("untouched", capture.redact("untouched", [""]))

    def test_ShouldApplyEverySecret_WhenSeveralAreKnown(self):
        result = capture.redact("a=KEY1 b=DEPLOY", ["KEY1", "DEPLOY"])

        self.assertNotIn("KEY1", result)
        self.assertNotIn("DEPLOY", result)


class DeploymentsBaseTests(unittest.TestCase):
    def test_ShouldAppendTheDeploymentsPath_WhenGivenTheResourceRoot(self):
        self.assertEqual(
            "https://res.openai.azure.com/openai/deployments",
            capture.deployments_base("https://res.openai.azure.com/"))

    def test_ShouldLeaveTheEndpointAlone_WhenItAlreadyCarriesThePath(self):
        endpoint = "https://res.openai.azure.com/openai/deployments"

        self.assertEqual(endpoint, capture.deployments_base(endpoint + "/"))

    def test_ShouldRaise_WhenEndpointIsBlank(self):
        with self.assertRaises(ValueError):
            capture.deployments_base("   ")


class AssertNoAccountTokenLeakTests(unittest.TestCase):
    def test_ShouldPass_WhenBodyDoesNotContainTheToken(self):
        capture.assert_no_account_token_leak('{"text":"hola"}', {"deployment": "whisper"})

    def test_ShouldRaise_WhenBodyContainsTheToken(self):
        # Neither silent leaking nor silent corruption is acceptable, so this stops the capture.
        with self.assertRaises(capture.CaptureError) as caught:
            capture.assert_no_account_token_leak(
                '{"text":"deploy acme-prod now"}', {"deployment": "acme-prod"})

        self.assertIn("deployment", str(caught.exception))

    def test_ShouldIgnoreEmptyTokens(self):
        capture.assert_no_account_token_leak('{"text":"hola"}', {"deployment": ""})


class NormalizeJsonTests(unittest.TestCase):
    def test_ShouldIndentWithTwoSpacesAndEndWithNewline(self):
        result = capture.normalize_json('{"text":"hola"}')

        self.assertEqual('{\n  "text": "hola"\n}\n', result)

    def test_ShouldPreserveNonAsciiCharacters(self):
        # Escaping to \\uXXXX would hide whether the client decodes UTF-8 correctly, which is
        # part of what the fixture exists to prove.
        result = capture.normalize_json('{"text":"registr\\u00f3"}')

        self.assertIn("registró", result)

    def test_ShouldPreserveUnmodelledSiblingFields(self):
        # A recorded response carrying fields the SDK does not model is the fidelity gain.
        result = json.loads(capture.normalize_json('{"text":"x","usage":{"seconds":3}}'))

        self.assertEqual({"seconds": 3}, result["usage"])

    def test_ShouldRaise_WhenBodyIsNotJson(self):
        with self.assertRaises(json.JSONDecodeError):
            capture.normalize_json("<html>gateway timeout</html>")


class DescribeHeadersTests(unittest.TestCase):
    def test_ShouldRecordNameOnly_WhenHeaderCarriesAnAccountIdentifier(self):
        result = capture.describe_headers([("openai-organization", "org-secret")])

        self.assertEqual("openai-organization", result)
        self.assertNotIn("org-secret", result)

    def test_ShouldRecordValue_WhenHeaderIsOnTheSafeList(self):
        result = capture.describe_headers([("Content-Type", "application/json")])

        self.assertEqual("content-type: application/json", result)

    def test_ShouldSortByNameForAStableSidecar(self):
        result = capture.describe_headers([("z-last", "1"), ("a-first", "2")])

        self.assertEqual("a-first, z-last", result)


class BuildSidecarTests(unittest.TestCase):
    def _sidecar(self, payload=b'{"text":"hola"}\n'):
        return capture.build_sidecar(
            provider="openai-whisper",
            product="OpenAI — audio transcriptions (Whisper)",
            endpoint="POST https://api.openai.com/v1/audio/transcriptions",
            api_version="n/a",
            captured_utc="2026-08-09",
            payload=payload,
            redaction_applied=["authorization bearer request header"],
            redaction_notes="notes",
            terms_verdict="permitted-with-conditions",
            terms_basis="section 7",
            notes="n",
        )

    def test_ShouldCarryEveryRequiredSchemaKey(self):
        sidecar = self._sidecar()

        for key in ("schema", "class", "provider", "product", "endpoint", "api_version",
                    "captured_utc", "source_audio", "redaction", "terms"):
            self.assertIn(key, sidecar)
        for key in ("origin", "description", "license"):
            self.assertIn(key, sidecar["source_audio"])

    def test_ShouldDeclareTheCaptureAsRecorded(self):
        self.assertEqual("recorded", self._sidecar()["class"])

    def test_ShouldDeclareSourceAudioSynthetic(self):
        # Protocol section 6: never an identifiable person's voice, including the capturer's.
        self.assertEqual("synthetic", self._sidecar()["source_audio"]["origin"])

    def test_ShouldDigestThePayloadWithSha256(self):
        payload = b'{"text":"hola"}\n'

        sidecar = self._sidecar(payload)

        self.assertEqual(hashlib.sha256(payload).hexdigest(), sidecar["sha256"])
        self.assertEqual(len(payload), sidecar["bytes"])

    def test_ShouldSerializeToJsonWithoutLosingAccents(self):
        rendered = json.dumps(self._sidecar(), indent=2, ensure_ascii=False)

        self.assertIn("audio transcriptions", rendered)
        self.assertEqual(capture.SCHEMA, json.loads(rendered)["schema"])


class ProviderPlanTests(unittest.TestCase):
    def setUp(self):
        self._saved = {k: os.environ.get(k) for k in (
            "OPENAI_API_KEY", "AZURE_OPENAI_API_KEY", "AZURE_OPENAI_ENDPOINT",
            "AZURE_OPENAI_DEPLOYMENT", "AZURE_OPENAI_API_VERSION")}
        for key in self._saved:
            os.environ.pop(key, None)

    def tearDown(self):
        for key, value in self._saved.items():
            if value is None:
                os.environ.pop(key, None)
            else:
                os.environ[key] = value

    def test_ShouldRaise_WhenCredentialIsMissing(self):
        with self.assertRaises(capture.CaptureError) as caught:
            capture.openai_whisper_plan(b"wav")

        self.assertIn("OPENAI_API_KEY", str(caught.exception))

    def test_ShouldSendBearerAuthorization_WhenProviderIsOpenAi(self):
        os.environ["OPENAI_API_KEY"] = "test-key"

        plan = capture.openai_whisper_plan(b"wav")

        self.assertEqual("Bearer test-key", plan["headers"]["Authorization"])
        self.assertNotIn("api-key", plan["headers"])

    def test_ShouldSendApiKeyHeader_WhenProviderIsAzure(self):
        os.environ.update({
            "AZURE_OPENAI_API_KEY": "azure-key",
            "AZURE_OPENAI_ENDPOINT": "https://res.openai.azure.com/openai/deployments",
            "AZURE_OPENAI_DEPLOYMENT": "whisper-dep",
        })

        plan = capture.azure_openai_whisper_plan(b"wav")

        self.assertEqual("azure-key", plan["headers"]["api-key"])
        self.assertNotIn("Authorization", plan["headers"])
        self.assertIn("whisper-dep/audio/transcriptions", plan["url"])

    def test_ShouldPlaceholderAccountSegmentsInTheRecordedEndpoint(self):
        os.environ.update({
            "AZURE_OPENAI_API_KEY": "azure-key",
            "AZURE_OPENAI_ENDPOINT": "https://myresource.openai.azure.com/openai/deployments",
            "AZURE_OPENAI_DEPLOYMENT": "prod-whisper",
        })

        plan = capture.azure_openai_whisper_plan(b"wav")

        self.assertNotIn("myresource", plan["endpoint_template"])
        self.assertNotIn("prod-whisper", plan["endpoint_template"])
        self.assertIn("<resource>", plan["endpoint_template"])
        self.assertIn("<deployment>", plan["endpoint_template"])

    def test_ShouldTrackDeploymentAsAnAccountTokenNotABlanketSecret(self):
        # Protocol section 4 names "deployment names that encode an account", but a blanket
        # string replace would corrupt a transcript that happens to contain the same word.
        os.environ.update({
            "AZURE_OPENAI_API_KEY": "azure-key",
            "AZURE_OPENAI_ENDPOINT": "https://res.openai.azure.com/openai/deployments",
            "AZURE_OPENAI_DEPLOYMENT": "whisper",
        })

        plan = capture.azure_openai_whisper_plan(b"wav")

        self.assertEqual({"deployment": "whisper"}, plan["account_tokens"])
        self.assertNotIn("whisper", plan["secrets"])

    def test_ShouldAcceptTheResourceRootAsEndpoint(self):
        # The portal shows the resource root; the SDK option documents the deployments base.
        os.environ.update({
            "AZURE_OPENAI_API_KEY": "azure-key",
            "AZURE_OPENAI_ENDPOINT": "https://res.openai.azure.com/",
            "AZURE_OPENAI_DEPLOYMENT": "whisper-dep",
        })

        plan = capture.azure_openai_whisper_plan(b"wav")

        self.assertIn("/openai/deployments/whisper-dep/audio/transcriptions", plan["url"])


if __name__ == "__main__":
    unittest.main()

"""Unit tests for capture-provider-recording.py (the provider fixture capture tool).

Covers request shaping, redaction, normalization, the provenance sidecar and what each artifact
mode writes. No test reaches the network: `send` is replaced by a stub wherever a whole capture
is exercised, because a live provider credential is what a real request needs and a tool whose
tests demand a secret is a tool nobody runs. Stdlib unittest only, matching the other script
tests.

The module name carries hyphens, so it is loaded by path rather than imported.
"""
import base64
import contextlib
import hashlib
import importlib.util
import json
import os
import tempfile
import unittest
import wave
import io
from pathlib import Path

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


class RedactCorrelationFieldsTests(unittest.TestCase):
    """The counterpart to redact(): the value is unknowable, so the *field* is named instead.

    Motivated by a real capture — Google's first committed response carried
    `"requestId": "8702164082194047156"`, which protocol §4 bans outright and which
    check-recording-redaction.py let through because a bare number is not credential-shaped.
    """

    GOOGLE_SHAPE = json.dumps(
        {
            "results": [{"alternatives": [{"transcript": "hola", "confidence": 0.9}]}],
            "totalBilledTime": "4s",
            "requestId": "8702164082194047156",
        }
    )

    def test_ShouldReplaceTheValueWithTheProtocolPlaceholder(self):
        result, applied = capture.redact_correlation_fields(self.GOOGLE_SHAPE, ("requestId",))

        self.assertNotIn("8702164082194047156", result)
        self.assertEqual(
            capture.PLACEHOLDER_CORRELATION_ID, json.loads(result)["requestId"]
        )
        self.assertEqual(["requestId"], applied)

    def test_ShouldKeepTheKey_SoTheUnmodelledSiblingSurvives(self):
        # The whole point of the fixture is that the SDK's DTO does not model requestId. Deleting
        # the key would silently remove the property the capture was taken to hold the parser to.
        result, _ = capture.redact_correlation_fields(self.GOOGLE_SHAPE, ("requestId",))

        self.assertIn("requestId", json.loads(result))

    def test_ShouldLeaveEveryOtherFieldByteIdentical(self):
        result, _ = capture.redact_correlation_fields(self.GOOGLE_SHAPE, ("requestId",))
        parsed = json.loads(result)

        self.assertEqual("4s", parsed["totalBilledTime"])
        self.assertEqual("hola", parsed["results"][0]["alternatives"][0]["transcript"])

    def test_ShouldReachNestedOccurrences(self):
        raw = json.dumps({"outer": {"inner": [{"traceId": "abc-123"}]}})

        result, applied = capture.redact_correlation_fields(raw, ("traceId",))

        self.assertNotIn("abc-123", result)
        self.assertEqual(["traceId"], applied)

    def test_ShouldReportEachFieldOnce_WhenItRecursThroughTheDocument(self):
        raw = json.dumps({"a": {"reqId": "1"}, "b": {"reqId": "2"}})

        result, applied = capture.redact_correlation_fields(raw, ("reqId",))

        self.assertEqual(["reqId"], applied)
        self.assertNotIn('"1"', result)
        self.assertNotIn('"2"', result)

    def test_ShouldReturnTheBodyUntouched_WhenNoFieldsAreNamed(self):
        # The default for every provider that has no known correlation field. Returning the raw
        # string rather than a re-serialized one keeps the two existing surfaces byte-identical.
        result, applied = capture.redact_correlation_fields(self.GOOGLE_SHAPE, ())

        self.assertEqual(self.GOOGLE_SHAPE, result)
        self.assertEqual([], applied)

    def test_ShouldNameTheFieldInTheSidecar_WhenGoogleIsCaptured(self):
        os.environ.pop("GOOGLE_ACCESS_TOKEN", None)
        os.environ["GOOGLE_SPEECH_API_KEY"] = "dummy-key"
        try:
            plan = capture.build_plan("google-speech", Path("."))
        finally:
            os.environ.pop("GOOGLE_SPEECH_API_KEY", None)

        self.assertEqual(("requestId",), plan["correlation_fields"])


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


class BuildSidecarShapeTests(unittest.TestCase):
    """The sidecar grew three parameters; the surfaces that predate them must not notice."""

    OLD_SHAPE_KEYS = ["schema", "class", "provider", "product", "endpoint", "api_version",
                      "captured_utc", "media_type", "bytes", "sha256", "source_audio",
                      "redaction", "terms", "notes"]

    def _sidecar(self, **overrides):
        kwargs = {
            "provider": "openai-whisper",
            "product": "OpenAI — audio transcriptions (Whisper)",
            "endpoint": "POST https://api.openai.com/v1/audio/transcriptions",
            "api_version": "n/a",
            "captured_utc": "2026-08-09",
            "payload": b'{"text":"hola"}\n',
            "redaction_applied": ["authorization bearer request header"],
            "redaction_notes": "notes",
            "terms_verdict": "permitted-with-conditions",
            "terms_basis": "section 7",
            "notes": "n",
        }
        kwargs.update(overrides)
        return capture.build_sidecar(**kwargs)

    def test_ShouldEmitTheOldKeysInTheOldOrder_WhenTheNewParametersAreOmitted(self):
        # Key order is the file's diff-stability: a reordered sidecar is a whole-file diff on
        # every re-capture, which is how a review stops reading them.
        self.assertEqual(self.OLD_SHAPE_KEYS, list(self._sidecar()))

    def test_ShouldDefaultToTheSttJsonAnswers_WhenTheNewParametersAreOmitted(self):
        sidecar = self._sidecar()

        self.assertEqual("application/json", sidecar["media_type"])
        self.assertEqual("recorded", sidecar["class"])
        self.assertEqual("synthetic", sidecar["source_audio"]["origin"])
        self.assertEqual(capture.SOURCE_AUDIO_DESCRIPTION, sidecar["source_audio"]["description"])

    def test_ShouldCarryTheGivenMediaType_WhenTheCaptureIsNotJson(self):
        self.assertEqual("audio/wav", self._sidecar(media_type="audio/wav")["media_type"])

    def test_ShouldCarryTheGivenSourceAudio_WhenTheSurfaceSubmitsText(self):
        block = capture.tts_source_audio("eleanor")

        self.assertEqual(block, self._sidecar(source_audio=block)["source_audio"])

    def test_ShouldCopyTheSourceAudio_SoTwoCapturesInOneRunCannotShareIt(self):
        block = capture.tts_source_audio("leah")

        sidecar = self._sidecar(source_audio=block)
        sidecar["source_audio"]["origin"] = "mutated"

        self.assertEqual("not-applicable", block["origin"])


class TtsSourceAudioTests(unittest.TestCase):
    def test_ShouldDeclareOriginNotApplicable(self):
        # Protocol section 6 has no "input text" field; the azure-tts capture is the precedent.
        self.assertEqual("not-applicable", capture.tts_source_audio("eleanor")["origin"])

    def test_ShouldNameTheSentenceAndTheVoice(self):
        block = capture.tts_source_audio("eleanor")

        self.assertIn(capture.TTS_INPUT_TEXT, block["description"])
        self.assertIn("'eleanor'", block["description"])
        self.assertIn("no custom or cloned voice", block["description"])


class AssertNoSecretBytesTests(unittest.TestCase):
    def test_ShouldPass_WhenTheBytesCarryNoCredential(self):
        capture.assert_no_secret_bytes(b"RIFF\x00\x01\x02", ["sk-live"])

    def test_ShouldRaise_WhenACredentialAppearsInBytesThatCannotBeRewritten(self):
        # Redacting inside a codec stream would corrupt the vendor bytes the capture preserves.
        with self.assertRaises(capture.CaptureError) as caught:
            capture.assert_no_secret_bytes(b"RIFF sk-live tail", ["sk-live"])

        self.assertIn("cannot be redacted", str(caught.exception))

    def test_ShouldIgnoreEmptyValues(self):
        # Same guard as redact(): an empty needle matches everywhere.
        capture.assert_no_secret_bytes(b"anything", [""])


class ResponseMediaTypeTests(unittest.TestCase):
    def test_ShouldStripParametersAndLowercase(self):
        self.assertEqual(
            "audio/wav",
            capture.response_media_type([("Content-Type", "Audio/WAV; codecs=1")]))

    def test_ShouldReturnNone_WhenTheResponseDeclaredNothing(self):
        self.assertIsNone(capture.response_media_type([("Date", "today")]))


class BuildEnvelopeTests(unittest.TestCase):
    def _envelope(self, **overrides):
        kwargs = {
            "status": 200,
            "headers": [("Content-Type", "audio/mpeg"), ("x-request-id", "req-secret")],
            "chunk_sizes": [8192, 8192, 100],
            "body_omitted": "because the terms do not permit it",
        }
        kwargs.update(overrides)
        return capture.build_envelope(**kwargs)

    def test_ShouldRecordEverythingSection7Asks(self):
        envelope = self._envelope()

        self.assertEqual(200, envelope["status"])
        self.assertEqual("audio/mpeg", envelope["media_type"])
        self.assertEqual(16484, envelope["content_length"])
        self.assertEqual([8192, 8192, 100], envelope["chunk_sizes"])
        self.assertIn("content-type: audio/mpeg", envelope["headers"])

    def test_ShouldSumTheReads_RatherThanTrustTheContentLengthHeader(self):
        # A chunked response carries no Content-Length at all, and the reads are what the SDK's
        # own loop observes.
        envelope = self._envelope(
            headers=[("Content-Type", "audio/mpeg"), ("Content-Length", "999999")])

        self.assertEqual(16484, envelope["content_length"])

    def test_ShouldWithholdHeaderValuesThatCorrelateToAnAccount(self):
        self.assertNotIn("req-secret", json.dumps(self._envelope()))

    def test_ShouldSayWhyTheBodyIsAbsent(self):
        self.assertEqual(
            "because the terms do not permit it", self._envelope()["body_omitted"])

    def test_ShouldReportUnknown_WhenTheVendorDeclaredNoMediaType(self):
        self.assertEqual("unknown", self._envelope(headers=[("Date", "today")])["media_type"])


class VerifyTests(unittest.TestCase):
    """Each verifier decides whether a response is worth committing at all."""

    def test_ShouldReturnTheTranscript_WhenWhisperAnswered(self):
        self.assertEqual(
            "Transcript: 'hola'.", capture.whisper_verify('{"text":"hola"}'))

    def test_ShouldRaise_WhenWhisperTranscribedNothing(self):
        with self.assertRaises(capture.CaptureError):
            capture.whisper_verify('{"text":"   "}')

    def test_ShouldReadGooglesOwnResponseShape(self):
        body = json.dumps(
            {"results": [{"alternatives": [{"transcript": "hola", "confidence": 0.9}]}]})

        self.assertEqual("Transcript: 'hola'.", capture.google_verify(body))

    def test_ShouldRaise_WhenGoogleRecognizedNothing(self):
        # speech:recognize answers {} rather than an empty transcript field.
        with self.assertRaises(capture.CaptureError) as caught:
            capture.google_verify("{}")

        self.assertIn("no transcript", str(caught.exception))

    def test_ShouldNotAcceptTheOpenAiShape_WhenTheProviderIsGoogle(self):
        with self.assertRaises(capture.CaptureError):
            capture.google_verify('{"text":"hola"}')

    def test_ShouldDescribeTheAudio_WhenSpeechmaticsReturnedAWavBody(self):
        payload = b"RIFF\x00\x00\x00\x00WAVE" + b"\x01\x02" * 8

        result = capture.speechmatics_verify(payload)

        self.assertIn("RIFF/WAVE container", result)
        self.assertIn(str(len(payload)), result)

    def test_ShouldRaise_WhenSpeechmaticsReturnedAnEmptyBody(self):
        with self.assertRaises(capture.CaptureError):
            capture.speechmatics_verify(b"")

    def test_ShouldRaise_WhenSpeechmaticsReturnedJsonUnderASuccessStatus(self):
        with self.assertRaises(capture.CaptureError) as caught:
            capture.speechmatics_verify(b'{"error":"quota"}')

        self.assertIn("JSON, not audio", str(caught.exception))

    def test_ShouldDescribeTheEnvelope_WhenLmntAnswered(self):
        result = capture.lmnt_verify(
            {"status": 200, "media_type": "audio/raw", "content_length": 300,
             "chunk_sizes": [200, 100]})

        self.assertIn("300 bytes of audio/raw", result)
        self.assertIn("2 reads", result)
        self.assertIn("discarded, not written", result)

    def test_ShouldRaise_WhenLmntReturnedNoBytes(self):
        with self.assertRaises(capture.CaptureError):
            capture.lmnt_verify(
                {"status": 200, "media_type": "audio/raw", "content_length": 0,
                 "chunk_sizes": []})

    def test_ShouldRaise_WhenLmntAnsweredANonSuccessStatus(self):
        with self.assertRaises(capture.CaptureError):
            capture.lmnt_verify(
                {"status": 402, "media_type": "application/json", "content_length": 9,
                 "chunk_sizes": [9]})


class _EnvScopedTestCase(unittest.TestCase):
    """Base for plan tests: credentials come from the environment, so each test owns its own.

    A developer's real shell must neither leak into a test (a set GOOGLE_ACCESS_TOKEN would flip
    the auth branch under them) nor survive one.
    """

    ENV_KEYS = ()

    def setUp(self):
        self._saved = {key: os.environ.get(key) for key in self.ENV_KEYS}
        for key in self.ENV_KEYS:
            os.environ.pop(key, None)

    def tearDown(self):
        for key, value in self._saved.items():
            if value is None:
                os.environ.pop(key, None)
            else:
                os.environ[key] = value


class GoogleSpeechPlanTests(_EnvScopedTestCase):
    ENV_KEYS = ("GOOGLE_SPEECH_API_KEY", "GOOGLE_ACCESS_TOKEN")

    PCM = b"\x01\x02" * 40

    def test_ShouldRaise_WhenNeitherCredentialIsSet(self):
        with self.assertRaises(capture.CaptureError) as caught:
            capture.google_speech_plan(self.PCM)

        self.assertIn("GOOGLE_SPEECH_API_KEY", str(caught.exception))
        self.assertIn("GOOGLE_ACCESS_TOKEN", str(caught.exception))

    def test_ShouldRaise_WhenBothCredentialsAreSet(self):
        # Which one is live decides the request that gets captured, so it cannot be implicit.
        os.environ.update({"GOOGLE_SPEECH_API_KEY": "key", "GOOGLE_ACCESS_TOKEN": "token"})

        with self.assertRaises(capture.CaptureError) as caught:
            capture.google_speech_plan(self.PCM)

        self.assertIn("exactly one", str(caught.exception))

    def test_ShouldPutTheKeyInTheQueryString_WhenTheApiKeyIsSet(self):
        # SDK fidelity: GoogleSpeechRecognizer authenticates with ?key=, defect and all.
        os.environ["GOOGLE_SPEECH_API_KEY"] = "live-google-key"

        plan = capture.google_speech_plan(self.PCM)

        self.assertEqual(
            "https://speech.googleapis.com/v1/speech:recognize?key=live-google-key", plan["url"])
        self.assertNotIn("Authorization", plan["headers"])

    def test_ShouldKeepTheLiveKeyOutOfTheRecordedEndpoint(self):
        # The key rides in the URL, so the endpoint template is a place it can leak into a
        # committed file; redact() only ever sees bodies.
        os.environ["GOOGLE_SPEECH_API_KEY"] = "live-google-key"

        plan = capture.google_speech_plan(self.PCM)

        self.assertNotIn("live-google-key", plan["endpoint_template"])
        self.assertIn("?key=REDACTED-API-KEY", plan["endpoint_template"])
        self.assertEqual(["live-google-key"], plan["secrets"])

    def test_ShouldSendBearerAuthorization_WhenAnAccessTokenIsSet(self):
        os.environ["GOOGLE_ACCESS_TOKEN"] = "ya29-token"

        plan = capture.google_speech_plan(self.PCM)

        self.assertEqual("Bearer ya29-token", plan["headers"]["Authorization"])
        self.assertNotIn("key=", plan["url"])
        self.assertNotIn("ya29-token", plan["endpoint_template"])

    def test_ShouldRecordThatAuthDiffersFromProduction_WhenCapturedWithAToken(self):
        os.environ["GOOGLE_ACCESS_TOKEN"] = "ya29-token"

        notes = capture.google_speech_plan(self.PCM)["notes"]

        self.assertIn("Auth differs from production", notes)
        self.assertIn("?key=", notes)

    def test_ShouldNotClaimAnAuthDifference_WhenCapturedTheWayTheSdkAuthenticates(self):
        os.environ["GOOGLE_SPEECH_API_KEY"] = "live-google-key"

        self.assertNotIn(
            "Auth differs from production", capture.google_speech_plan(self.PCM)["notes"])

    def test_ShouldFlagTheOpenTermsUncertainty(self):
        # Protocol section 7: the verdict drops to not-cleared if Speech-to-Text turns out not
        # to be enumerated as an "AI/ML Service".
        os.environ["GOOGLE_SPEECH_API_KEY"] = "live-google-key"

        plan = capture.google_speech_plan(self.PCM)

        self.assertEqual("permitted", plan["terms_verdict"])
        self.assertIn("AI/ML Service", plan["notes"])
        self.assertIn("not-cleared", plan["notes"])

    def test_ShouldSendTheDtoFieldsCompactAndInDeclarationOrder(self):
        os.environ["GOOGLE_SPEECH_API_KEY"] = "live-google-key"

        plan = capture.google_speech_plan(self.PCM)

        self.assertTrue(plan["body"].startswith(b'{"config":{"encoding":"LINEAR16"'))
        self.assertEqual(
            {"encoding": "LINEAR16", "sampleRateHertz": 8000,
             "languageCode": "es-CO", "model": "default"},
            json.loads(plan["body"])["config"])
        self.assertEqual("application/json; charset=utf-8", plan["headers"]["Content-Type"])

    def test_ShouldNotClaimAWavHeaderItNeverSent_WhenDescribingTheSourceAudio(self):
        # The same .raw file, a different submission: a sidecar claiming a RIFF wrapper here
        # would misdescribe the very request the capture exists to pin down.
        os.environ["GOOGLE_SPEECH_API_KEY"] = "live-google-key"

        source_audio = capture.google_speech_plan(self.PCM)["source_audio"]
        description = source_audio["description"]

        self.assertEqual("synthetic", source_audio["origin"])
        self.assertNotIn("RIFF", description)
        self.assertIn("raw LINEAR16 with no container", description)
        self.assertIn("es-CO-SalomeNeural", description)

    def test_ShouldSubmitRawLinear16_NotAWavWrappedPayload(self):
        # The recognizer base64-encodes the drained frames; a RIFF header would decode as 44
        # bytes of samples and capture a request production never sends.
        os.environ["GOOGLE_SPEECH_API_KEY"] = "live-google-key"

        content = json.loads(capture.google_speech_plan(self.PCM)["body"])["audio"]["content"]

        self.assertEqual(self.PCM, base64.b64decode(content))

    def test_ShouldWriteIntoTheSttTreeUnderTheProtocolSlug(self):
        # The CLI names the SDK surface; protocol section 2 names the directory. The tree itself
        # is the STT default, which this plan takes by not overriding it.
        os.environ["GOOGLE_SPEECH_API_KEY"] = "live-google-key"

        plan = capture.google_speech_plan(self.PCM)

        self.assertEqual("google-stt", plan["provider_slug"])
        self.assertNotIn("recordings_dir", plan)
        self.assertEqual(capture.STT_RECORDINGS, capture.PLAN_DEFAULTS["recordings_dir"])
        self.assertEqual(capture.SCENARIO_SLUG, capture.PLAN_DEFAULTS["scenario_slug"])
        self.assertIs(capture.google_verify, plan["verify"])


class SpeechmaticsTtsPlanTests(_EnvScopedTestCase):
    ENV_KEYS = ("SPEECHMATICS_API_KEY",)

    def test_ShouldRaise_WhenCredentialIsMissing(self):
        with self.assertRaises(capture.CaptureError) as caught:
            capture.speechmatics_tts_plan(b"")

        self.assertIn("SPEECHMATICS_API_KEY", str(caught.exception))

    def test_ShouldSendBearerAuthorization(self):
        os.environ["SPEECHMATICS_API_KEY"] = "sm-key"

        plan = capture.speechmatics_tts_plan(b"")

        self.assertEqual("Bearer sm-key", plan["headers"]["Authorization"])

    def test_ShouldSelectTheVoiceByPathSegment_NotByBodyField(self):
        # This assertion used to live inside the authorization test, pinned to `/generate` — the
        # route the API answers 404 on. A capture run would have recorded that 404 as though it
        # were the surface, which is the defect one level up from the client.
        os.environ["SPEECHMATICS_API_KEY"] = "sm-key"

        plan = capture.speechmatics_tts_plan(b"")

        self.assertEqual(
            "https://preview.tts.speechmatics.com/generate/eleanor", plan["url"])

    def test_ShouldPostTheShippedOptionDefaults(self):
        # A capture taken at non-default options is a capture of a request production never sends.
        os.environ["SPEECHMATICS_API_KEY"] = "sm-key"

        plan = capture.speechmatics_tts_plan(b"")

        self.assertTrue(plan["body"].startswith(b'{"text":'))
        self.assertEqual(
            {"text": capture.TTS_INPUT_TEXT, "language": "en", "sample_rate": 16000},
            json.loads(plan["body"]))
        self.assertNotIn(b"voice", plan["body"])

    def test_ShouldCaptureTheVendorBytesIntoTheTtsTree(self):
        os.environ["SPEECHMATICS_API_KEY"] = "sm-key"

        plan = capture.speechmatics_tts_plan(b"")

        self.assertEqual("binary", plan["artifact"])
        self.assertEqual("wav", plan["extension"])
        self.assertEqual("audio/wav", plan["media_type"])
        self.assertEqual(capture.TTS_RECORDINGS, plan["recordings_dir"])
        self.assertEqual("synthesize-short-en-us", plan["scenario_slug"])

    def test_ShouldDeclareTheInputAsTextNotAsSourceAudio(self):
        os.environ["SPEECHMATICS_API_KEY"] = "sm-key"

        plan = capture.speechmatics_tts_plan(b"")

        self.assertEqual("not-applicable", plan["source_audio"]["origin"])
        self.assertIn("'eleanor'", plan["source_audio"]["description"])

    def test_ShouldCarryTheSection7VerdictAndItsCondition(self):
        os.environ["SPEECHMATICS_API_KEY"] = "sm-key"

        plan = capture.speechmatics_tts_plan(b"")

        self.assertEqual("permitted-with-conditions", plan["terms_verdict"])
        self.assertIn("section 7 (Speechmatics TTS)", plan["terms_basis"])
        self.assertIn("Transcripts", plan["notes"])


class LmntHttpPlanTests(_EnvScopedTestCase):
    ENV_KEYS = ("LMNT_API_KEY",)

    def test_ShouldRaise_WhenCredentialIsMissing(self):
        with self.assertRaises(capture.CaptureError) as caught:
            capture.lmnt_http_plan(b"")

        self.assertIn("LMNT_API_KEY", str(caught.exception))

    def test_ShouldAuthenticateWithTheApiKeyHeader_NotBearer(self):
        os.environ["LMNT_API_KEY"] = "lmnt-key"

        plan = capture.lmnt_http_plan(b"")

        self.assertEqual("lmnt-key", plan["headers"]["X-API-Key"])
        self.assertEqual("1.0", plan["headers"]["lmnt-version"])
        self.assertNotIn("Authorization", plan["headers"])

    def test_ShouldPostToTheBytesRoute_NotGenerate(self):
        # No test asserted the route at all, which is how the plan kept `/v1/ai/speech/generate`
        # — a route the API answers 404 on — through every green run of this suite.
        os.environ["LMNT_API_KEY"] = "lmnt-key"

        self.assertEqual(
            "https://api.lmnt.com/v1/ai/speech/bytes", capture.lmnt_http_plan(b"")["url"])

    def test_ShouldFormEncodeTheShippedOptionDefaults(self):
        os.environ["LMNT_API_KEY"] = "lmnt-key"

        plan = capture.lmnt_http_plan(b"")

        self.assertEqual(
            "application/x-www-form-urlencoded", plan["headers"]["Content-Type"])
        self.assertTrue(plan["body"].startswith(b"voice=leah&text="))
        # pcm_s16le, not `raw`: over this transport `raw` is an MP3 frame stream (measured
        # 2026-08-15), so a capture at `raw` would record MP3 under a Slin16 label.
        self.assertIn(b"&format=pcm_s16le&sample_rate=16000&language=en&speed=1.00", plan["body"])

    def test_ShouldOmitTheModelField_WhenTheSdkOptionLeavesItUnset(self):
        # LmntTtsOptions.Model defaults to null and the synthesizer only adds it when set.
        os.environ["LMNT_API_KEY"] = "lmnt-key"

        self.assertNotIn(b"model=", capture.lmnt_http_plan(b"")["body"])

    def test_ShouldCaptureAnEnvelopeInsteadOfTheAudio(self):
        os.environ["LMNT_API_KEY"] = "lmnt-key"

        plan = capture.lmnt_http_plan(b"")

        self.assertEqual("envelope", plan["artifact"])
        self.assertEqual("json", plan["extension"])
        self.assertEqual("application/json", plan["media_type"])
        self.assertEqual(capture.TTS_RECORDINGS, plan["recordings_dir"])

    def test_ShouldSayPlainlyWhyTheAudioIsNotCommitted(self):
        os.environ["LMNT_API_KEY"] = "lmnt-key"

        plan = capture.lmnt_http_plan(b"")

        self.assertEqual("not-cleared", plan["terms_verdict"])
        self.assertIn("section 7 (LMNT HTTP)", plan["terms_basis"])
        self.assertIn("deliberately not committed", plan["notes"])
        self.assertIn("never the bytes", plan["body_omitted"])


class BuildPlanTests(_EnvScopedTestCase):
    ENV_KEYS = ("LMNT_API_KEY", "SPEECHMATICS_API_KEY", "OPENAI_API_KEY")

    def test_ShouldFillTheSharedDefaults_WhenAPlanOmitsThem(self):
        os.environ["SPEECHMATICS_API_KEY"] = "sm-key"

        with tempfile.TemporaryDirectory() as root:
            plan = capture.build_plan("speechmatics-tts", Path(root))

        self.assertEqual("recorded", plan["capture_class"])
        self.assertEqual({}, plan["account_tokens"])

    def test_ShouldDefaultTheSlugToTheCliName_WhenThePlanDoesNotOverrideIt(self):
        os.environ["LMNT_API_KEY"] = "lmnt-key"

        with tempfile.TemporaryDirectory() as root:
            plan = capture.build_plan("lmnt-http", Path(root))

        self.assertEqual("lmnt-http", plan["provider_slug"])

    def test_ShouldNotDemandSourceAudio_WhenTheSurfaceSubmitsText(self):
        # The committed .raw is absent from this temp root; a TTS capture never reads it.
        os.environ["LMNT_API_KEY"] = "lmnt-key"

        with tempfile.TemporaryDirectory() as root:
            capture.build_plan("lmnt-http", Path(root))

    def test_ShouldRaise_WhenAPlanIsIncomplete(self):
        # Caught before the request, because a plan that fails after `send` has already spent a
        # capture — and the reflex when a run dies is to run it again.
        capture.PROVIDERS["broken-for-test"] = lambda source_pcm: {"artifact": "json"}
        self.addCleanup(capture.PROVIDERS.pop, "broken-for-test", None)

        with tempfile.TemporaryDirectory() as root:
            with self.assertRaises(capture.CaptureError) as caught:
                capture.build_plan("broken-for-test", Path(root))

        self.assertIn("Nothing was sent", str(caught.exception))
        self.assertIn("verify", str(caught.exception))

    def test_ShouldRaise_WhenAnEnvelopePlanCannotSayWhyTheBodyIsAbsent(self):
        # An envelope whose capture file does not explain the missing payload reads, to the next
        # person, like a truncated capture.
        os.environ["LMNT_API_KEY"] = "lmnt-key"
        incomplete = capture.lmnt_http_plan(b"")
        incomplete.pop("body_omitted")
        capture.PROVIDERS["envelope-for-test"] = lambda source_pcm: incomplete
        self.addCleanup(capture.PROVIDERS.pop, "envelope-for-test", None)

        with tempfile.TemporaryDirectory() as root:
            with self.assertRaises(capture.CaptureError) as caught:
                capture.build_plan("envelope-for-test", Path(root))

        self.assertIn("body_omitted", str(caught.exception))

    def test_ShouldRaise_WhenSourceAudioIsMissingForASurfaceThatSubmitsIt(self):
        os.environ["OPENAI_API_KEY"] = "sk-test"

        with tempfile.TemporaryDirectory() as root:
            with self.assertRaises(capture.CaptureError) as caught:
                capture.build_plan("openai-whisper", Path(root))

        self.assertIn("does not invent audio", str(caught.exception))


class _FakeHeaders:
    def __init__(self, pairs):
        self._pairs = pairs

    def items(self):
        return list(self._pairs)


class _FakeResponse:
    """Just enough of http.client.HTTPResponse for send() to read it."""

    def __init__(self, body, headers, status=200):
        self._stream = io.BytesIO(body)
        self.headers = _FakeHeaders(headers)
        self.status = status

    def read(self, size=-1):
        return self._stream.read(size)

    def __enter__(self):
        return self

    def __exit__(self, *exc_info):
        return False


class SendTests(unittest.TestCase):
    """The one place the tool touches the network — stubbed at urlopen, never dialled."""

    PLAN = {
        "url": "https://example.invalid/generate",
        "body": b"x=1",
        "headers": {"X-API-Key": "k"},
        "secrets": ["k"],
    }

    def setUp(self):
        self._saved = capture.urllib.request.urlopen
        self._response = None
        capture.urllib.request.urlopen = lambda request, timeout=None: self._response

    def tearDown(self):
        capture.urllib.request.urlopen = self._saved

    def _send(self, body, **kwargs):
        self._response = _FakeResponse(body, [("Content-Type", "audio/raw")])
        return capture.send(self.PLAN, 5, **kwargs)

    def test_ShouldReturnTheWholeBody_WhenRetainBodyIsTrue(self):
        status, headers, body, sizes = self._send(b"abcdef")

        self.assertEqual(200, status)
        self.assertEqual([("Content-Type", "audio/raw")], headers)
        self.assertEqual(b"abcdef", body)
        self.assertEqual([6], sizes)

    def test_ShouldDiscardTheBody_WhenRetainBodyIsFalse(self):
        # Envelope mode's promise is structural: there is never a whole payload in memory for a
        # later line of code to write out.
        _, _, body, sizes = self._send(b"audio-bytes", retain_body=False)

        self.assertEqual(b"", body)
        self.assertEqual([11], sizes)

    def test_ShouldReadAtTheSdksBufferSize_SoChunkBoundariesAreObserved(self):
        payload = bytes(capture.READ_CHUNK_BYTES * 2 + 100)

        _, _, _, sizes = self._send(payload, retain_body=False)

        self.assertEqual(
            [capture.READ_CHUNK_BYTES, capture.READ_CHUNK_BYTES, 100], sizes)


class CaptureArtifactTests(_EnvScopedTestCase):
    """What each artifact mode actually writes, driven end to end with `send` stubbed out."""

    ENV_KEYS = ("SPEECHMATICS_API_KEY", "LMNT_API_KEY")

    AUDIO_MARKER = b"\xde\xadLMNT-AUDIO-MARKER\xbe\xef"

    def setUp(self):
        super().setUp()
        os.environ.update({"SPEECHMATICS_API_KEY": "sm-key", "LMNT_API_KEY": "lmnt-key"})
        self._saved_send = capture.send
        self.addCleanup(setattr, capture, "send", self._saved_send)

    def _root(self):
        temporary = tempfile.TemporaryDirectory()
        self.addCleanup(temporary.cleanup)
        return Path(temporary.name)

    def _stub_send(self, body, content_type, status=200):
        # The stub hands back the body even when capture() asked for none: the envelope
        # assertions are only worth something if the bytes were available to be written.
        chunks = [len(body[i:i + 8192]) for i in range(0, len(body), 8192)]
        capture.send = lambda plan, timeout, retain_body=True: (
            status, [("Content-Type", content_type)], body, chunks)

    def _run(self, provider, body, *, content_type="audio/wav", status=200):
        self._stub_send(body, content_type, status)
        root = self._root()

        with contextlib.redirect_stdout(io.StringIO()), contextlib.redirect_stderr(io.StringIO()):
            capture.capture(provider, root, False, 5)

        return {
            path.relative_to(root).as_posix(): path.read_bytes()
            for path in root.rglob("*") if path.is_file()
        }

    def test_ShouldWriteTheVendorBytesVerbatim_WhenArtifactIsBinary(self):
        audio = b"RIFF\x00\x00\x00\x00WAVE" + bytes(range(256)) * 4

        written = self._run("speechmatics-tts", audio)

        capture_file = (
            "Tests/Verbara.Sdk.VoiceAi.Tts.Tests/Recordings/speechmatics-tts/"
            "synthesize-short-en-us.wav")
        self.assertEqual(audio, written[capture_file])

    def test_ShouldRecordTheDeclaredMediaTypeAndDigest_WhenArtifactIsBinary(self):
        audio = b"RIFF\x00\x00\x00\x00WAVE" + b"\x7f\x80" * 64

        written = self._run("speechmatics-tts", audio)
        sidecar = json.loads(written[
            "Tests/Verbara.Sdk.VoiceAi.Tts.Tests/Recordings/speechmatics-tts/"
            "synthesize-short-en-us.provenance.json"])

        self.assertEqual("audio/wav", sidecar["media_type"])
        self.assertEqual(len(audio), sidecar["bytes"])
        self.assertEqual(hashlib.sha256(audio).hexdigest(), sidecar["sha256"])
        self.assertEqual("not-applicable", sidecar["source_audio"]["origin"])

    def test_ShouldFollowTheVendorsDeclaration_WhenTheMediaTypeIsNotTheExpectedOne(self):
        # A response that stops being WAV must not land in a file that still claims to be one.
        written = self._run("speechmatics-tts", b"ID3\x03\x00mp3-bytes", content_type="audio/mpeg")

        self.assertIn(
            "Tests/Verbara.Sdk.VoiceAi.Tts.Tests/Recordings/speechmatics-tts/"
            "synthesize-short-en-us.mp3", written)

    def test_ShouldRaise_WhenABinaryCaptureExceedsTheHardCap(self):
        # Protocol section 8's 256 KiB is a cap, not the text path's advisory threshold.
        oversized = b"RIFF\x00\x00\x00\x00WAVE" + bytes(capture.BINARY_SIZE_CAP_BYTES)

        with self.assertRaises(capture.CaptureError) as caught:
            self._run("speechmatics-tts", oversized)

        self.assertIn(str(capture.BINARY_SIZE_CAP_BYTES), str(caught.exception))
        self.assertIn("nothing was written", str(caught.exception))

    def test_ShouldWriteNothing_WhenTheBinaryCapIsExceeded(self):
        oversized = b"RIFF\x00\x00\x00\x00WAVE" + bytes(capture.BINARY_SIZE_CAP_BYTES)
        self._stub_send(oversized, "audio/wav")
        root = self._root()

        with contextlib.redirect_stdout(io.StringIO()):
            with self.assertRaises(capture.CaptureError):
                capture.capture("speechmatics-tts", root, False, 5)

        self.assertEqual([], [p for p in root.rglob("*") if p.is_file()])

    def test_ShouldNeverWriteTheAudioBytes_WhenArtifactIsEnvelope(self):
        written = self._run(
            "lmnt-http", self.AUDIO_MARKER * 400, content_type="audio/raw")

        self.assertTrue(written, "the capture wrote no files at all")
        for name, content in written.items():
            self.assertNotIn(self.AUDIO_MARKER, content, f"{name} carries LMNT audio")

    def test_ShouldRecordTheObservedBoundaries_WhenArtifactIsEnvelope(self):
        body = self.AUDIO_MARKER * 400  # 9200 bytes: one full 8 KiB read plus a partial

        written = self._run("lmnt-http", body, content_type="audio/raw")
        envelope = json.loads(written[
            "Tests/Verbara.Sdk.VoiceAi.Tts.Tests/Recordings/lmnt-http/"
            "synthesize-short-en-us.json"])

        self.assertEqual(200, envelope["status"])
        self.assertEqual("audio/raw", envelope["media_type"])
        self.assertEqual(len(body), envelope["content_length"])
        self.assertEqual([8192, len(body) - 8192], envelope["chunk_sizes"])
        self.assertIn("not-cleared", envelope["body_omitted"])

    def test_ShouldDescribeTheEnvelopeAsJsonInTheSidecar_WhenArtifactIsEnvelope(self):
        written = self._run("lmnt-http", self.AUDIO_MARKER * 400, content_type="audio/raw")
        sidecar = json.loads(written[
            "Tests/Verbara.Sdk.VoiceAi.Tts.Tests/Recordings/lmnt-http/"
            "synthesize-short-en-us.provenance.json"])

        self.assertEqual("application/json", sidecar["media_type"])
        self.assertEqual("recorded", sidecar["class"])
        self.assertEqual("not-cleared", sidecar["terms"]["verdict"])
        self.assertIn("deliberately not committed", sidecar["notes"])


@contextlib.contextmanager
def _env(**values):
    """Set env vars for the duration of a plan build, restoring whatever was there."""
    saved = {key: os.environ.get(key) for key in values}
    os.environ.update(values)
    try:
        yield
    finally:
        for key, value in saved.items():
            if value is None:
                os.environ.pop(key, None)
            else:
                os.environ[key] = value


class WebSocketCodecTests(unittest.TestCase):
    """The frame codec, which is where a hand-written WebSocket client goes silently wrong.

    Every case here is an RFC 6455 requirement the vendor would enforce by closing the connection
    with a code that says nothing useful, so getting it wrong looks like "the provider hung up"
    rather than like a bug in this file.
    """

    def test_ShouldDeriveTheAcceptToken_WhenGivenTheRfcExampleKey(self):
        # RFC 6455 §1.3's own worked example — the one value in this whole file that is not ours.
        self.assertEqual(
            "s3pPLMBiTxaQ9kYGzzhZRbK+xOo=",
            capture.ws_accept_token("dGhlIHNhbXBsZSBub25jZQ=="),
        )

    def test_ShouldCarryTheQueryString_WhenRenderingTheHandshake(self):
        host, port, request = capture.ws_handshake_request(
            "wss://api.example.com/stt/websocket?model=ink&sample_rate=8000",
            {"X-API-Key": "k"},
            "dGhlIHNhbXBsZSBub25jZQ==",
        )

        self.assertEqual("api.example.com", host)
        self.assertEqual(443, port)
        text = request.decode("ascii")
        # The query is where every session parameter of this surface travels, and a client that
        # dropped it would be answered 1008 rather than refused — see the Cartesia STT close.
        self.assertIn("GET /stt/websocket?model=ink&sample_rate=8000 HTTP/1.1\r\n", text)
        self.assertIn("Sec-WebSocket-Version: 13\r\n", text)
        self.assertIn("X-API-Key: k\r\n", text)
        self.assertTrue(text.endswith("\r\n\r\n"))

    def test_ShouldRejectTheUrl_WhenTheSchemeIsNotWebSocket(self):
        with self.assertRaises(capture.CaptureError):
            capture.ws_handshake_request("https://api.example.com/", {}, "k")

    def test_ShouldMaskThePayload_WhenEncodingAClientFrame(self):
        mask = b"\x01\x02\x03\x04"

        frame = capture.ws_encode_frame(capture.WS_OPCODE_TEXT, b"done", mask)

        self.assertEqual(0x81, frame[0], "final bit plus the text opcode")
        self.assertEqual(0x84, frame[1], "mask bit plus a 4-byte length")
        self.assertEqual(mask, frame[2:6])
        self.assertEqual(b"done", bytes(b ^ mask[i % 4] for i, b in enumerate(frame[6:])))

    def test_ShouldUseTheExtendedLength_WhenThePayloadExceedsTheShortForm(self):
        for length, marker, width in ((126, 126, 2), (65536, 127, 8)):
            with self.subTest(length=length):
                frame = capture.ws_encode_frame(
                    capture.WS_OPCODE_BINARY, b"\x00" * length, b"\x00" * 4
                )
                self.assertEqual(0x80 | marker, frame[1])
                self.assertEqual(length, int.from_bytes(frame[2:2 + width], "big"))

    def test_ShouldReturnTheTail_WhenAFrameArrivesSplitAcrossReads(self):
        # An unmasked frame, because this is the server direction — a masked one is the case the
        # test below rejects. Splitting it is the ordinary case on a socket, not an edge case.
        whole = bytes([0x81, 0x05]) + b"hello"

        frames, tail = capture.ws_decode_frames(whole[:4])
        self.assertEqual([], frames, "a partial frame decodes to nothing, never to a truncated one")
        self.assertEqual(whole[:4], tail)

        frames, tail = capture.ws_decode_frames(tail + whole[4:])
        self.assertEqual([(capture.WS_OPCODE_TEXT, b"hello", True)], frames)
        self.assertEqual(b"", tail)

    def test_ShouldReportFinality_WhenTheMessageArrivesAcrossContinuationFrames(self):
        # Finality is reported rather than resolved, so a capture can observe that the vendor
        # fragmented instead of having reassembly hide it.
        first = bytes([0x01, 0x03]) + b"abc"          # text, not final
        last = bytes([0x80, 0x03]) + b"def"           # continuation, final

        frames, tail = capture.ws_decode_frames(first + last)

        self.assertEqual(
            [
                (capture.WS_OPCODE_TEXT, b"abc", False),
                (capture.WS_OPCODE_CONTINUATION, b"def", True),
            ],
            frames,
        )
        self.assertEqual(b"", tail)

    def test_ShouldRefuseTheFrame_WhenTheServerMasksIt(self):
        masked = bytes([0x81, 0x81, 0, 0, 0, 0, 0x41])

        with self.assertRaises(capture.CaptureError):
            capture.ws_decode_frames(masked)


class SessionPlanTests(unittest.TestCase):
    def test_ShouldReproduceTheShippedRequest_WhenPlanningTheCartesiaSttSession(self):
        with _env(CARTESIA_API_KEY="secret-key"):
            plan = capture.cartesia_stt_session_plan(b"\x00" * 16)

        self.assertIn("wss://api.cartesia.ai/stt/websocket?", plan["url"])
        for expected in ("model=ink-whisper", "encoding=pcm_s16le", "sample_rate=8000"):
            self.assertIn(expected, plan["url"])
        self.assertEqual("2024-11-13", plan["headers"]["Cartesia-Version"])
        # The three words the service names in its own rejection; `done` is the client's.
        self.assertEqual("done", plan["terminator"])
        self.assertEqual("finalize", plan["pre_terminator"])
        self.assertEqual(("request_id",), plan["correlation_fields"])

    def test_ShouldSelectEachFrame_WhenTheSessionProducedTheWholeSet(self):
        with _env(CARTESIA_API_KEY="secret-key"):
            plan = capture.cartesia_stt_session_plan(b"")
        observed = [
            {"type": "transcript", "is_final": False},
            {"type": "transcript", "is_final": True},
            {"type": "flush_done", "is_final": False},
            {"type": "done", "is_final": False},
        ]

        picked = {
            spec["slug"]: next((m for m in observed if spec["select"](m)), None)
            for spec in plan["frames"]
        }

        self.assertEqual(observed[0], picked["transcript-frame-interim"])
        self.assertEqual(observed[1], picked["transcript-frame-final"])
        self.assertEqual(observed[2], picked["flush-done-frame"])
        self.assertEqual(observed[3], picked["done-frame"])

    def test_ShouldSelectNothing_WhenTheServiceDidNotSendTheFrame(self):
        # The case that actually happened: ink-whisper answered a short utterance with one final
        # transcript and no interim one, in both a paced and an unpaced session. A plan that
        # substituted an authored frame here would have published a fiction as a recording.
        with _env(CARTESIA_API_KEY="secret-key"):
            plan = capture.cartesia_stt_session_plan(b"")
        interim = next(s for s in plan["frames"] if s["slug"] == "transcript-frame-interim")

        self.assertIsNone(
            next((m for m in [{"type": "transcript", "is_final": True}] if interim["select"](m)), None)
        )

    def test_ShouldDropTheAudioAndPacing_WhenPlanningTheErrorSession(self):
        with _env(CARTESIA_API_KEY="secret-key"):
            plan = capture.cartesia_stt_error_session_plan(b"\x00" * 16)

        self.assertEqual(b"", plan["audio"])
        self.assertIsNone(plan["pre_terminator"], "finalize would be a second, valid message")
        self.assertEqual(("error-frame",), tuple(s["slug"] for s in plan["frames"]))


if __name__ == "__main__":
    unittest.main()

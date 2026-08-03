"""Unit tests for check-recording-redaction.py (the recording credential guard).

Builds tmp Recordings/ trees and runs the script as a subprocess. Stdlib
unittest only -- NO pip deps, matching the other guard-script tests.

Secret-shaped literals are assembled from fragments so this file never contains
a contiguous string a secret scanner would flag.
"""
import os
import shutil
import subprocess
import sys
import tempfile
import unittest

_HERE = os.path.dirname(os.path.abspath(__file__))
_SCRIPT = os.path.join(_HERE, os.pardir, "check-recording-redaction.py")

_AIZA = "AI" + "za"
_EYJ = "ey" + "J"


class CheckRecordingRedactionTests(unittest.TestCase):
    def setUp(self):
        self._root = tempfile.mkdtemp()
        self._recordings = os.path.join(
            self._root, "Tests", "Verbara.Sdk.VoiceAi.Stt.Tests",
            "Recordings", "openai-whisper")
        os.makedirs(self._recordings)

    def tearDown(self):
        shutil.rmtree(self._root, ignore_errors=True)

    def _write(self, name, content, directory=None):
        path = os.path.join(directory or self._recordings, name)
        mode = "wb" if isinstance(content, bytes) else "w"
        kwargs = {} if isinstance(content, bytes) else {"encoding": "utf-8"}
        with open(path, mode, **kwargs) as handle:
            handle.write(content)
        return path

    def _run(self):
        return subprocess.run(
            [sys.executable, _SCRIPT, self._root],
            capture_output=True, text=True,
        )

    def test_ShouldPass_WhenNoRecordingsTreeExists(self):
        empty = tempfile.mkdtemp()
        try:
            result = subprocess.run(
                [sys.executable, _SCRIPT, empty],
                capture_output=True, text=True,
            )
            self.assertEqual(result.returncode, 0, result.stdout + result.stderr)
            self.assertIn("nothing to scan", result.stdout)
        finally:
            shutil.rmtree(empty, ignore_errors=True)

    def test_ShouldPass_WhenCaptureIsProperlyRedacted(self):
        self._write("transcribe-short-en-us.json", (
            '{\n'
            '  "request": {\n'
            '    "authorization": "Bearer REDACTED-TOKEN",\n'
            '    "x-request-id": "00000000-0000-0000-0000-000000000000"\n'
            '  },\n'
            '  "text": "the quick brown fox"\n'
            '}\n'))
        self._write("transcribe-short-en-us.provenance.json", (
            '{\n'
            '  "schema": "verbara.recording-provenance/1",\n'
            '  "class": "recorded",\n'
            '  "provider": "openai-whisper",\n'
            '  "endpoint": "POST https://api.openai.com/v1/audio/transcriptions",\n'
            '  "sha256": "9f2c1d4e6a8b0c2d4e6f8a0b1c3d5e7f'
            '9a1b3c5d7e9f0a2b4c6d8e0f2a4b6c8d"\n'
            '}\n'))
        result = self._run()
        self.assertEqual(result.returncode, 0, result.stdout + result.stderr)
        self.assertIn("Recording redaction OK.", result.stdout)
        self.assertIn("scanned 2 file(s)", result.stdout)

    def test_ShouldFail_WhenBearerTokenIsPresent(self):
        self._write("transcribe-short-en-us.json",
                    '{"authorization": "Bearer a1b2c3d4e5f6g7h8i9j0k1l2"}\n')
        result = self._run()
        self.assertEqual(result.returncode, 1, result.stdout + result.stderr)
        self.assertIn("bearer-token", result.stdout)
        self.assertIn("credential-shaped match", result.stdout)

    def test_ShouldFail_WhenApiKeyRidesInTheQueryString(self):
        # The Google STT shape: the key is in the URL, not a header.
        self._write("recognize.json",
                    '{"url": "https://speech.googleapis.com/v1/speech:recognize'
                    '?key=' + _AIZA + 'SyA0000BBBB1111CCCC2222DDDD3333EEEE"}\n')
        result = self._run()
        self.assertEqual(result.returncode, 1, result.stdout + result.stderr)
        self.assertIn("google-api-key", result.stdout)

    def test_ShouldFail_WhenCorrelatingRequestGuidIsPresent(self):
        self._write("synthesize.json",
                    '{"x-requestid": "3f2a1b4c-5d6e-7f80-9012-3456789abcde"}\n')
        result = self._run()
        self.assertEqual(result.returncode, 1, result.stdout + result.stderr)
        self.assertIn("correlating-guid", result.stdout)

    def test_ShouldFail_WhenAccountIdentifierIsPresent(self):
        self._write("recognize.json", '{"project_id": "prod-448122"}\n')
        result = self._run()
        self.assertEqual(result.returncode, 1, result.stdout + result.stderr)
        self.assertIn("account-identifier", result.stdout)

    def test_ShouldFail_WhenJwtIsPresent(self):
        self._write("capture.txt",
                    _EYJ + "hbGciOiJIUzI1NiJ9.eyJzdWIiOiIxMjM0NSJ9"
                           ".c2lnbmF0dXJlLXZhbHVl\n")
        result = self._run()
        self.assertEqual(result.returncode, 1, result.stdout + result.stderr)
        self.assertIn("jwt", result.stdout)

    def test_ShouldFail_WhenPrivateKeyBlockIsPresent(self):
        self._write("capture.txt", "-----BEGIN RSA PRIVATE KEY-----\n")
        result = self._run()
        self.assertEqual(result.returncode, 1, result.stdout + result.stderr)
        self.assertIn("private-key-block", result.stdout)

    def test_ShouldFail_WhenSecretHidesInsideBinaryCapture(self):
        # An API key in a WAV LIST/INFO chunk is invisible to a .json-only scan.
        payload = (b"RIFF\x24\x08\x00\x00WAVEfmt \x10\x00\x00\x00"
                   + b"\x01\x00\x01\x00\x40\x1f\x00\x00"
                   + b"LISTINFOICMT"
                   + b'Ocp-Apim-Subscription-Key=4d7b2e9a1c6f8035be24a917d05c3f6e'
                   + b"\x00\xff\xfe\xfd" * 8)
        self._write("synthesize-short-en-us.wav", payload)
        result = self._run()
        self.assertEqual(result.returncode, 1, result.stdout + result.stderr)
        self.assertIn("synthesize-short-en-us.wav", result.stdout)

    def test_ShouldNeverPrintTheMatchedValue(self):
        secret = "a1b2c3d4e5f6g7h8i9j0k1l2"
        self._write("capture.json", '{"authorization": "Bearer %s"}\n' % secret)
        result = self._run()
        self.assertEqual(result.returncode, 1, result.stdout + result.stderr)
        self.assertNotIn(secret, result.stdout)
        self.assertNotIn(secret, result.stderr)

    def test_ShouldIgnoreFilesOutsideARecordingsTree(self):
        self._write("Notes.md", '{"authorization": "Bearer a1b2c3d4e5f6g7h8i9j0"}',
                    directory=self._root)
        self._write("clean.json", '{"text": "hello"}\n')
        result = self._run()
        self.assertEqual(result.returncode, 0, result.stdout + result.stderr)

    def test_ShouldIgnoreBuildOutputUnderARecordingsTree(self):
        obj_dir = os.path.join(self._recordings, "obj")
        os.makedirs(obj_dir)
        self._write("Generated.json",
                    '{"authorization": "Bearer a1b2c3d4e5f6g7h8i9j0k1l2"}\n',
                    directory=obj_dir)
        self._write("clean.json", '{"text": "hello"}\n')
        result = self._run()
        self.assertEqual(result.returncode, 0, result.stdout + result.stderr)

    def test_ShouldFail_WhenCaptureIsAnUnfetchedLfsPointer(self):
        self._write("large-capture.wav", (
            "version https://git-lfs.github.com/spec/v1\n"
            "oid sha256:9f2c1d4e6a8b0c2d4e6f8a0b1c3d5e7f"
            "9a1b3c5d7e9f0a2b4c6d8e0f2a4b6c8d\n"
            "size 402653\n"))
        result = self._run()
        self.assertEqual(result.returncode, 1, result.stdout + result.stderr)
        self.assertIn("Git-LFS pointer", result.stdout)

    def test_ShouldPass_WhenSha256DigestLooksHexButIsNotAKey(self):
        # 64 hex chars must not trip the 32-hex Azure-key pattern.
        self._write("capture.provenance.json",
                    '{"sha256": "9f2c1d4e6a8b0c2d4e6f8a0b1c3d5e7f'
                    '9a1b3c5d7e9f0a2b4c6d8e0f2a4b6c8d"}\n')
        result = self._run()
        self.assertEqual(result.returncode, 0, result.stdout + result.stderr)

    def test_ShouldFail_WhenBare32HexKeyIsPresent(self):
        self._write("capture.json",
                    '{"header": "4d7b2e9a1c6f8035be24a917d05c3f6e"}\n')
        result = self._run()
        self.assertEqual(result.returncode, 1, result.stdout + result.stderr)
        self.assertIn("bare-32-hex-key", result.stdout)

    def test_ShouldReportEveryHit_WhenSeveralCapturesLeak(self):
        self._write("a.json", '{"authorization": "Bearer a1b2c3d4e5f6g7h8i9j0k1l2"}\n')
        self._write("b.json", '{"x-request-id": "3f2a1b4c-5d6e-7f80-9012-3456789abcde"}\n')
        result = self._run()
        self.assertEqual(result.returncode, 1, result.stdout + result.stderr)
        self.assertIn("2 credential-shaped match(es)", result.stdout)


if __name__ == "__main__":
    unittest.main()

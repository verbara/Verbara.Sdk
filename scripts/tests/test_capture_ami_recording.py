"""Unit tests for capture-ami-recording.py (the AMI capture driver and its reducer).

No test starts a container or reaches the network: the reducer is a pure function of bytes, and
the tap is driven against a scripted fake PBX on a socket pair. The live half of the tool -- one
capture per image -- is exercised by running it, and its output is held to the same reducer and
redaction check these tests pin. Stdlib unittest only, matching the other script tests.

The module name carries hyphens, so it is loaded by path rather than imported.
"""
import contextlib
import hashlib
import importlib.util
import io
import json
import os
import socket
import tempfile
import threading
import unittest
from pathlib import Path

_HERE = os.path.dirname(os.path.abspath(__file__))
_SCRIPT = os.path.join(_HERE, os.pardir, "capture-ami-recording.py")

_spec = importlib.util.spec_from_file_location("capture_ami_recording", _SCRIPT)
cap = importlib.util.module_from_spec(_spec)
_spec.loader.exec_module(cap)

_BANNER = b"Asterisk Call Manager/11.0.0\r\n"
_BRIDGE = "5c1f2a9e-7b3d-4e8a-9f01-23456789abcd"
_OTHER_BRIDGE = "0a1b2c3d-4e5f-4a6b-8c7d-0e1f2a3b4c5d"
_DIGEST = "sha256:" + "ab" * 32


def frame(*pairs):
    return "".join(f"{k}: {v}\r\n" for k, v in pairs).encode("latin-1") + b"\r\n"


def sample_tap():
    return _BANNER + b"".join([
        frame(("Response", "Success"), ("ActionID", "cap-1"), ("Message", "Authentication accepted")),
        frame(("Event", "FullyBooted"), ("Privilege", "system,all"), ("Uptime", "3")),
        frame(("Event", "SuccessfulAuth"), ("Privilege", "security,all"), ("AccountID", "capture"),
              ("RemoteAddress", "IPV4/TCP/172.17.0.1/40312"), ("SessionID", "0x7f00")),
        frame(("Response", "Success"), ("ActionID", "cap-2"), ("Message", "Event Sent")),
        frame(("Event", "UserEvent"), ("UserEvent", "CaptureProvenance"),
              ("AsteriskVersion", "Asterisk 22.10.1"), ("Image", f"vendor/asterisk:22.10.1@{_DIGEST}"),
              ("ActionID", "cap-2")),
        frame(("Event", "UserEvent"), ("UserEvent", "CaptureMarker"), ("Step", "bridge"),
              ("ActionID", "cap-3")),
        frame(("Event", "BridgeCreate"), ("BridgeUniqueid", _BRIDGE), ("BridgeType", "basic")),
        frame(("Event", "VarSet"), ("Channel", "PJSIP/far-00000001"), ("Variable", "SIPDOMAIN"),
              ("Value", "172.18.0.5")),
        frame(("Event", "BridgeEnter"), ("BridgeUniqueid", _BRIDGE), ("Channel", "Local/a@x-1;1")),
        frame(("Event", "BridgeCreate"), ("BridgeUniqueid", _OTHER_BRIDGE)),
        frame(("Event", "UserEvent"), ("UserEvent", "CaptureMarker"), ("Step", "conference"),
              ("ActionID", "cap-4")),
        frame(("Event", "ContactStatus"), ("URI", "sip:agent@10.1.2.3:5060"), ("Bind", "127.0.0.1"),
              ("Key", "4d7b2e9a1c6f8035be24a917d05c3f6e")),
        frame(("Event", "BridgeDestroy"), ("BridgeUniqueid", _BRIDGE)),
    ])


class SplitStreamTests(unittest.TestCase):
    def test_split_stream_ShouldReturnBannerFramesAndPartial_WhenStreamEndsMidFrame(self):
        data = _BANNER + frame(("Event", "A")) + b"Event: B\r\nPriv"

        banner, frames, partial = cap.split_stream(data)

        self.assertEqual(_BANNER, banner)
        self.assertEqual([frame(("Event", "A"))], frames)
        self.assertEqual(b"Event: B\r\nPriv", partial)

    def test_split_stream_ShouldRefuse_WhenStreamHasNoBanner(self):
        with self.assertRaises(cap.CaptureError):
            cap.split_stream(frame(("Event", "A")))

    def test_headers_ShouldKeepFirstValue_WhenHeaderRepeats(self):
        h = cap.headers(frame(("Event", "Status"), ("Variable", "A=1"), ("Variable", "B=2")))

        self.assertEqual("A=1", h["Variable"])


class ReduceStreamTests(unittest.TestCase):
    def test_reduce_ShouldDropLoginAnswerAndAddressCarryingFrames(self):
        recording, report = cap.reduce_stream(sample_tap())

        self.assertNotIn(b"Authentication accepted", recording)
        self.assertNotIn(b"SuccessfulAuth", recording)
        self.assertNotIn(b"FullyBooted", recording)
        self.assertNotIn(b"SIPDOMAIN", recording)
        self.assertNotIn(b"172.17.0.1", recording)
        self.assertNotIn(b"172.18.0.5", recording)
        self.assertEqual({"FullyBooted": 1, "SuccessfulAuth": 1, "login Response": 1,
                          "VarSet SIPDOMAIN": 1}, report["removed"])

    def test_reduce_ShouldKeepWholeFramesByteExact_WhenNothingInThemIsReplaced(self):
        recording, _ = cap.reduce_stream(sample_tap())

        self.assertTrue(recording.startswith(_BANNER))
        self.assertIn(frame(("Response", "Success"), ("ActionID", "cap-2"), ("Message", "Event Sent")),
                      recording)
        self.assertIn(frame(("Event", "UserEvent"), ("UserEvent", "CaptureMarker"), ("Step", "bridge"),
                            ("ActionID", "cap-3")), recording)

    def test_reduce_ShouldFillEachUuidConsistentlyInOrderOfFirstAppearance(self):
        recording, report = cap.reduce_stream(sample_tap())

        self.assertNotIn(_BRIDGE.encode(), recording)
        self.assertEqual(3, recording.count(b"11111111-1111-1111-1111-111111111111"))
        self.assertEqual(1, recording.count(b"22222222-2222-2222-2222-222222222222"))
        self.assertEqual(2, report["replaced"]["uuid"])

    def test_reduce_ShouldFillBareHexAndAddresses_AndLeaveTheImageDigestAndLoopback(self):
        recording, report = cap.reduce_stream(sample_tap())

        self.assertIn(b"Key: " + b"1" * 32, recording)
        self.assertIn(b"sip:agent@192.0.2.1:5060", recording)
        self.assertIn(b"Bind: 127.0.0.1", recording)
        self.assertIn(_DIGEST.encode(), recording, "a 64-hex digest is provenance, not a key")
        self.assertEqual({"uuid": 2, "hex32": 1, "ipv4": 1}, report["replaced"])

    def test_reduce_ShouldReturnTheSameBytes_WhenGivenTheSameTapTwice(self):
        first, first_report = cap.reduce_stream(sample_tap(), ("BridgeEnter",), ())
        second, second_report = cap.reduce_stream(sample_tap(), ("BridgeEnter",), ())

        self.assertEqual(first, second)
        self.assertEqual(first_report, second_report)

    def test_reduce_ShouldKeepOnlyNamedStepsAndProvenance_WhenKeepStepsIsGiven(self):
        recording, report = cap.reduce_stream(sample_tap(), (), ("conference",))

        self.assertIn(b"CaptureProvenance", recording)
        self.assertNotIn(b"Step: bridge", recording)
        self.assertNotIn(b"BridgeEnter", recording)
        self.assertIn(b"Step: conference", recording)
        self.assertIn(b"BridgeDestroy", recording)
        self.assertGreater(report["removed"]["outside --keep-steps"], 0)

    def test_reduce_ShouldDropNamedTypes_WhenDropEventsIsGiven(self):
        recording, report = cap.reduce_stream(sample_tap(), ("BridgeCreate",), ())

        self.assertNotIn(b"BridgeCreate", recording)
        self.assertEqual(2, report["removed"]["BridgeCreate"])

    def test_reduce_ShouldPassTheRepositoryRedactionCheck(self):
        recording, _ = cap.reduce_stream(sample_tap())

        cap.check_recording(recording)  # raises on any finding

    def test_reduce_ShouldUsePlaceholder_WhenDistinctUuidsOutnumberTheFills(self):
        frames = [frame(("Event", "BridgeCreate"), ("BridgeUniqueid", f"{n:08x}-0000-4000-8000-00000000{n:04x}"))
                  for n in range(1, 40)]
        recording, report = cap.reduce_stream(_BANNER + b"".join(frames))

        self.assertIn(b"redacted-uuid-0035", recording)
        self.assertEqual(39, report["replaced"]["uuid"])
        cap.check_recording(recording)


class CheckRecordingTests(unittest.TestCase):
    def test_check_ShouldRefuseAndNameTheHeaviestTypes_WhenOverTheCap(self):
        big = _BANNER + frame(("Event", "VarSet"), ("Value", "x" * (cap.SIZE_CAP_BYTES + 1)))

        with self.assertRaises(cap.CaptureError) as caught:
            cap.check_recording(big)

        self.assertIn("VarSet", str(caught.exception))
        self.assertIn("--drop-events", str(caught.exception))

    def test_check_ShouldRefuse_WhenTheRunSecretSurvives(self):
        recording = _BANNER + frame(("Event", "UserEvent"), ("Note", "s3cr3tvalue"))

        with self.assertRaises(cap.CaptureError):
            cap.check_recording(recording, forbidden=["s3cr3tvalue"])

    def test_check_ShouldRefuse_WhenTheRedactionCheckFindsACredential(self):
        recording = _BANNER + frame(("Event", "UserEvent"), ("Detail", "password=K1x9Qz7Lm3Pw5Rt8Vb"))

        with self.assertRaises(cap.CaptureError) as caught:
            cap.check_recording(recording)

        self.assertIn("credential-keyed-value", str(caught.exception))
        self.assertNotIn("K1x9Qz7Lm3Pw5Rt8Vb", str(caught.exception))


def _meta(tap):
    return {
        "workload": "bridges-and-conference", "image": f"vendor/asterisk:22.10.1@{_DIGEST}",
        "image_id": _DIGEST, "asterisk_version": "Asterisk 22.10.1",
        "ami_banner": "Asterisk Call Manager/11.0.0", "captured_utc": "2026-10-17T00:00:00Z",
        "steps": ["bridge", "conference"], "tap_bytes": len(tap),
        "tap_sha256": hashlib.sha256(tap).hexdigest(),
    }


class WriteRecordingTests(unittest.TestCase):
    def test_write_ShouldWriteRecordingAndSidecarNamedAfterTheBuild(self):
        tap = sample_tap()
        with tempfile.TemporaryDirectory() as out:
            raw, side, _report, size = cap.write_recording(tap, _meta(tap), Path(out))

            self.assertEqual("bridges-and-conference-asterisk-22.10.1.raw", raw.name)
            data = raw.read_bytes()
            sidecar = json.loads(side.read_text(encoding="utf-8"))
            self.assertEqual(size, len(data))
            self.assertEqual(hashlib.sha256(data).hexdigest(), sidecar["sha256"])
            self.assertEqual("Asterisk 22.10.1", sidecar["asterisk_version"])
            self.assertEqual(_DIGEST, sidecar["image_id"])
            self.assertEqual(cap.SCHEMA, sidecar["schema"])

    def test_write_ShouldRefuse_WhenTheTapDoesNotMatchItsMetadata(self):
        tap = sample_tap()
        meta = _meta(tap)
        with tempfile.TemporaryDirectory() as out:
            with self.assertRaises(cap.CaptureError):
                cap.write_recording(tap + frame(("Event", "Extra")), meta, Path(out))

    def test_reduceCommand_ShouldWriteIdenticalBytes_WhenRunTwiceOnTheSameTap(self):
        tap = sample_tap()
        with tempfile.TemporaryDirectory() as work:
            tap_path = Path(work) / "x.tap.raw"
            tap_path.write_bytes(tap)
            (Path(work) / "x.tap.json").write_text(json.dumps(_meta(tap)), encoding="utf-8")
            outputs = []
            for n in (1, 2):
                out = Path(work) / f"out{n}"
                with contextlib.redirect_stdout(io.StringIO()):
                    self.assertEqual(0, cap.main(["reduce", str(tap_path), "--out", str(out)]))
                outputs.append(((out / "bridges-and-conference-asterisk-22.10.1.raw").read_bytes(),
                                (out / "bridges-and-conference-asterisk-22.10.1.provenance.json").read_bytes()))

            self.assertEqual(outputs[0], outputs[1])


class ImageAndNameTests(unittest.TestCase):
    def test_parse_image_ShouldRefuse_WhenTheReferenceHasNoDigest(self):
        with self.assertRaises(cap.CaptureError):
            cap.parse_image("andrius/asterisk:22.10.1_debian-trixie")

    def test_parse_image_ShouldSplitRepoTagAndDigest(self):
        repo, tag, digest = cap.parse_image(f"andrius/asterisk:24.0.0-rc2_debian-trixie@{_DIGEST}")

        self.assertEqual(("andrius/asterisk", "24.0.0-rc2_debian-trixie", _DIGEST), (repo, tag, digest))

    def test_version_check_ShouldRefuse_WhenTheTagNamesAnotherBuild(self):
        with self.assertRaises(cap.CaptureError):
            cap._check_version_matches_tag("22.11.0_debian-trixie", "Asterisk 22.10.1")
        cap._check_version_matches_tag("22.10.1_debian-trixie-6bb254a", "Asterisk 22.10.1")
        cap._check_version_matches_tag(None, "Asterisk 24.0.0-rc2")

    def test_recording_name_ShouldRefuse_WhenTheVersionIsNotAFileName(self):
        with self.assertRaises(cap.CaptureError):
            cap.recording_name("w", "Asterisk 22/10")


class _FakePbx(threading.Thread):
    """Answers each action from a script: {Action: [frames to send after it]} with ActionID filled."""

    def __init__(self, sock, script):
        super().__init__(daemon=True)
        self.sock = sock
        self.script = script
        self.sent = bytearray()

    def _send(self, data):
        self.sent += data
        self.sock.sendall(data)

    def run(self):
        self._send(_BANNER)
        buf = b""
        try:
            while True:
                chunk = self.sock.recv(65536)
                if not chunk:
                    return
                buf += chunk
                while b"\r\n\r\n" in buf:
                    raw, buf = buf.split(b"\r\n\r\n", 1)
                    h = cap.headers(raw + b"\r\n\r\n")
                    for template in self.script.get(h.get("Action"), []):
                        self._send(template.replace(b"{id}", h["ActionID"].encode()))
        except OSError:
            return


class TapTests(unittest.TestCase):
    def _pair(self, script):
        ours, theirs = socket.socketpair()
        pbx = _FakePbx(theirs, script)
        pbx.start()
        return ours, theirs, pbx

    def test_tap_ShouldRecordEveryByteThePbxSent_AndWaitForTheEndOfAList(self):
        script = {
            "Login": [b"Response: Success\r\nActionID: {id}\r\nMessage: Authentication accepted\r\n\r\n"],
            "BridgeList": [
                b"Response: Success\r\nActionID: {id}\r\nEventList: start\r\n\r\n",
                b"Event: BridgeListItem\r\nActionID: {id}\r\nBridgeUniqueid: x\r\n\r\n",
                b"Event: BridgeListComplete\r\nActionID: {id}\r\nEventList: Complete\r\n\r\n",
            ],
            "ConfbridgeListRooms": [
                b"Response: Error\r\nActionID: {id}\r\nMessage: No active conferences.\r\n\r\n"],
        }
        ours, theirs, pbx = self._pair(script)
        try:
            tap = cap.Tap(ours)
            tap.login("capture", "secret")
            end = tap.list_action([("Action", "BridgeList")])
            refused = tap.list_action([("Action", "ConfbridgeListRooms")])
        finally:
            ours.close()
            theirs.close()
            pbx.join(2)

        self.assertEqual("Asterisk Call Manager/11.0.0", tap.banner)
        self.assertEqual("BridgeListComplete", end["Event"])
        self.assertEqual("Error", refused["Response"])
        self.assertEqual(bytes(pbx.sent), bytes(tap.transcript))

    def test_tap_ShouldRaise_WhenALoginIsRefused(self):
        script = {"Login": [b"Response: Error\r\nActionID: {id}\r\nMessage: Authentication failed\r\n\r\n"]}
        ours, theirs, pbx = self._pair(script)
        try:
            tap = cap.Tap(ours)
            with self.assertRaises(cap.CaptureError):
                tap.login("capture", "wrong")
        finally:
            ours.close()
            theirs.close()
            pbx.join(2)

    def test_tap_ShouldTimeOut_WhenTheAwaitedFrameNeverArrives(self):
        ours, theirs, pbx = self._pair({})
        clock = iter(range(0, 1000, 5))
        try:
            tap = cap.Tap(ours, clock=lambda: next(clock))
            with self.assertRaises(cap.CaptureError) as caught:
                tap.wait_for(lambda f: False, 10, 0, "a frame that never comes")
        finally:
            ours.close()
            theirs.close()
            pbx.join(2)

        self.assertIn("a frame that never comes", str(caught.exception))


if __name__ == "__main__":
    unittest.main()

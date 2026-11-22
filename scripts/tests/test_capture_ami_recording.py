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
    """Answers each action from a script: {Action: [frames to send after it]} with ActionID filled.

    A script value may also be a callable that takes the action's headers and returns the frames,
    so a fake can answer the same action differently by its arguments. Every action received is
    kept in `received`, in order.
    """

    def __init__(self, sock, script):
        super().__init__(daemon=True)
        self.sock = sock
        self.script = script
        self.sent = bytearray()
        self.received = []

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
                    self.received.append(h)
                    entry = self.script.get(h.get("Action"), [])
                    for template in (entry(h) if callable(entry) else entry):
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


def _ev(*pairs):
    return frame(*pairs)


def _hangups(*channels):
    return [_ev(("Event", "Hangup"), ("Channel", c), ("Cause", "16")) for c in channels]


class _ParkingPbx:
    """The answers a PBX gives the dialplan-and-parking workload, in a fake's script shape.

    The parking space is 705, not the first one of the lot, so a workload that hard-codes the
    space it picks up from fails here.
    """

    SPACE = "705"
    FLASH_OUT = "PJSIP/flash-00000007"
    FLASH_IN = "PJSIP/flash-00000008"
    HOLD = "Local/hold@capture-00000003;2"

    def __init__(self):
        self.parks = 0

    def ok(self, message="Success"):
        return b"Response: Success\r\nActionID: {id}\r\nMessage: " + message.encode() + b"\r\n\r\n"

    def script(self):
        return {
            "Login": [self.ok("Authentication accepted")],
            "UserEvent": lambda h: [self.ok("Event Sent"),
                                    _ev(("Event", "UserEvent"), ("UserEvent", h["UserEvent"]),
                                        ("Step", h.get("Step", "")), ("ActionID", h["ActionID"]))],
            "Originate": self.originate,
            "Redirect": lambda h: [self.ok("Redirect successful"),
                                   _ev(("Event", "Newexten"), ("Channel", self.HOLD), ("Context", "capture"),
                                       ("Exten", "redirected"), ("Priority", "1"), ("Application", "NoOp")),
                                   *_hangups(self.HOLD, "Local/hold@capture-00000003;1")],
            "ParkedCalls": [b"Response: Success\r\nActionID: {id}\r\nEventList: start\r\n\r\n",
                            b"Event: ParkedCallsComplete\r\nActionID: {id}\r\nEventList: Complete\r\n\r\n"],
            "Parkinglots": [b"Response: Success\r\nActionID: {id}\r\nEventList: start\r\n\r\n",
                            b"Event: ParkinglotsComplete\r\nActionID: {id}\r\nEventList: Complete\r\n\r\n"],
            "Command": [self.ok("Command output follows")],
            "SendFlash": self.send_flash,
            "Logoff": [b"Response: Goodbye\r\nActionID: {id}\r\n\r\n"],
        }

    def send_flash(self, h):
        """As 22.11.0 answers: the Receive form flashes the target leg itself, the plain form the
        far leg, and both answer the same Success, so only the Flash event's channel tells them apart."""
        if h.get("Channel") != self.FLASH_OUT:
            return [b"Response: Error\r\nActionID: {id}\r\nMessage: No such channel\r\n\r\n"]
        flashed = self.FLASH_OUT if h.get("Receive") == "true" else self.FLASH_IN
        return [self.ok("Flash successfully queued"), _ev(("Event", "Flash"), ("Channel", flashed))]

    def originate(self, h):
        channel = h["Channel"]
        queued = [self.ok("Originate successfully queued")]
        if channel == "Local/callerid@capture/n":
            return queued + [
                _ev(("Event", "Newexten"), ("Channel", "Local/callerid@capture-00000001;2"),
                    ("Context", "capture"), ("Exten", "callerid"), ("Priority", "1"), ("Application", "Answer")),
                _ev(("Event", "NewCallerid"), ("Channel", "Local/callerid@capture-00000001;2"),
                    ("CallerIDNum", "7105"), ("CID-CallingPres", "0 (Presentation Allowed, Not Screened)")),
                *_hangups("Local/callerid@capture-00000001;2", "Local/callerid@capture-00000001;1")]
        if channel == "Local/hold@capture/n":
            return queued + [_ev(("Event", "Newexten"), ("Channel", self.HOLD), ("Context", "capture"),
                                 ("Exten", "hold"), ("Priority", "2"), ("Application", "Wait"))]
        if channel in ("Local/park@capture/n", "Local/park-short@capture/n"):
            self.parks += 1
            n = f"{self.parks:08d}"
            stem = channel[: -len("/n")].replace("@capture", "@capture-" + n)
            parked = queued + [_ev(("Event", "ParkedCall"), ("ParkeeChannel", stem + ";2"),
                                   ("ParkingSpace", self.SPACE), ("Parkinglot", "default"))]
            if channel.startswith("Local/park-short"):
                return parked + [_ev(("Event", "ParkedCallTimeOut"), ("ParkeeChannel", stem + ";2"),
                                     ("ParkingSpace", self.SPACE))] + _hangups(stem + ";2", stem + ";1")
            if self.parks == 2:  # the give-up park: its caller leaves while it is parked
                return parked + [_ev(("Event", "ParkedCallGiveUp"), ("ParkeeChannel", stem + ";2"),
                                     ("ParkingSpace", self.SPACE))] + _hangups(stem + ";1", stem + ";2")
            return parked
        if channel == f"Local/{self.SPACE}@capture/n":
            return queued + [_ev(("Event", "UnParkedCall"), ("ParkeeChannel", "Local/park@capture-00000001;2"),
                                 ("ParkingSpace", self.SPACE))] + _hangups(
                f"Local/{self.SPACE}@capture-00000009;2", f"Local/{self.SPACE}@capture-00000009;1",
                "Local/park@capture-00000001;2", "Local/park@capture-00000001;1")
        if channel == "PJSIP/flash":
            return queued + [
                _ev(("Event", "Newchannel"), ("Channel", self.FLASH_OUT), ("ChannelStateDesc", "Down")),
                _ev(("Event", "Newchannel"), ("Channel", self.FLASH_IN), ("ChannelStateDesc", "Ring")),
                _ev(("Event", "Newstate"), ("Channel", self.FLASH_IN), ("ChannelStateDesc", "Up")),
                _ev(("Event", "Newstate"), ("Channel", self.FLASH_OUT), ("ChannelStateDesc", "Up")),
                _ev(("Event", "Newexten"), ("Channel", self.FLASH_IN), ("Context", "capture-flash"),
                    ("Exten", "flash-in"), ("Priority", "2"), ("Application", "Wait")),
                *_hangups(self.FLASH_IN, self.FLASH_OUT)]
        return [b"Response: Error\r\nActionID: {id}\r\nMessage: Originate failed\r\n\r\n"]


class DialplanAndParkingWorkloadTests(unittest.TestCase):
    _NAME = "dialplan-and-parking"

    def _run(self, instrument=None):
        pbx_state = _ParkingPbx()
        ours, theirs = socket.socketpair()
        pbx = _FakePbx(theirs, pbx_state.script())
        pbx.start()
        try:
            tap = cap.Tap(ours)
            tap.pump = lambda seconds: cap.Tap.pump(tap, min(seconds, 0.02))
            if instrument:
                instrument(tap)
            tap.login("capture", "secret")
            steps = cap.WORKLOADS[self._NAME]["run"](tap)
            tap.logoff()
        finally:
            ours.close()
            theirs.close()
            pbx.join(2)
        return steps, pbx.received, tap

    def test_workloads_ShouldOfferDialplanAndParking_NextToBridgesAndConference(self):
        self.assertIn("bridges-and-conference", cap.WORKLOADS)
        self.assertIn(self._NAME, cap.WORKLOADS)
        out = io.StringIO()
        with contextlib.redirect_stdout(out):
            self.assertEqual(0, cap.main(["workloads"]))

        self.assertIn(self._NAME + ":", out.getvalue())

    def test_files_ShouldConfigureParkingCallerIdChangeAndALoopbackPjsipChannel(self):
        files = cap.WORKLOADS[self._NAME]["files"]

        self.assertEqual({"extensions.conf", "res_parking.conf", "pjsip.conf"}, set(files))
        extensions = files["extensions.conf"]
        self.assertIn("include => parkedcalls", extensions)
        self.assertIn("Park(default,s)", extensions)
        self.assertIn("Park(default,st(3))", extensions)
        self.assertIn("Set(CALLERID(num)=7105)", extensions)
        parking = files["res_parking.conf"]
        self.assertIn("comebacktoorigin = no", parking, "a timed-out parkee must not be dialled back")
        self.assertIn("context = parkedcalls", parking)
        pjsip = files["pjsip.conf"]
        self.assertIn("bind = 127.0.0.1:5060", pjsip, "SIP stays inside the container")
        self.assertNotIn("0.0.0.0", pjsip)
        for text in files.values():
            text.encode("ascii")  # capture() writes them as ascii

    def test_run_ShouldWalkEveryStepInOrder_WhenThePbxAnswersEachShape(self):
        steps, _received, _tap = self._run()

        self.assertEqual(["dialplan", "redirect", "park", "parked-list", "unpark", "giveup", "timeout",
                          "send-flash-help", "send-flash-receive", "send-flash-plain", "send-flash-missing",
                          "parked-empty", "done"], steps)

    def test_run_ShouldPickUpTheSpaceTheParkedCallEventNamed(self):
        _steps, received, _tap = self._run()

        originated = [h["Channel"] for h in received if h.get("Action") == "Originate"]
        self.assertIn(f"Local/{_ParkingPbx.SPACE}@capture/n", originated)
        self.assertNotIn("Local/701@capture/n", originated)

    def test_run_ShouldRedirectTheChannelThatRunsTheHoldExtension(self):
        _steps, received, _tap = self._run()

        redirect = [h for h in received if h.get("Action") == "Redirect"]
        self.assertEqual(1, len(redirect))
        self.assertEqual(_ParkingPbx.HOLD, redirect[0]["Channel"])
        self.assertEqual(("capture", "redirected", "1"),
                         (redirect[0]["Context"], redirect[0]["Exten"], redirect[0]["Priority"]))

    def test_run_ShouldSendFlashWithReceiveToTheOriginatedPjsipLeg_AndOnceWithoutReceive(self):
        _steps, received, tap = self._run()

        flashes = [h for h in received if h.get("Action") == "SendFlash"]
        self.assertEqual([(_ParkingPbx.FLASH_OUT, "true"), (_ParkingPbx.FLASH_OUT, None)],
                         [(h["Channel"], h.get("Receive")) for h in flashes[:2]])
        self.assertEqual(3, len(flashes), "the third targets a channel that does not exist")
        self.assertNotEqual(_ParkingPbx.FLASH_OUT, flashes[2]["Channel"])
        commands = [h.get("Command") for h in received if h.get("Action") == "Command"]
        self.assertEqual(["manager show command SendFlash"], commands)
        errors = [f for f in tap.frames if f.get("Response") == "Error"]
        self.assertEqual(1, len(errors))

    def test_run_ShouldRecordEachFlashInsideItsOwnStep_SoTheChannelTellsWhetherReceiveWasApplied(self):
        _steps, received, tap = self._run()

        step, flashes = None, []
        for f in tap.frames:
            if f.get("Event") == "UserEvent" and f.get("UserEvent") == "CaptureMarker":
                step = f.get("Step")
            elif f.get("Event") == "Flash":
                flashes.append((step, f.get("Channel")))
        self.assertEqual([("send-flash-receive", _ParkingPbx.FLASH_OUT),
                          ("send-flash-plain", _ParkingPbx.FLASH_IN)], flashes)
        sent_in = {}
        step = None
        for h in received:
            if h.get("Action") == "UserEvent":
                step = h.get("Step")
            elif h.get("Action") == "SendFlash":
                sent_in[step] = h.get("Receive")
        self.assertEqual({"send-flash-receive": "true", "send-flash-plain": None, "send-flash-missing": None},
                         sent_in)

    def test_run_ShouldSendTheFirstFlashOnlyOnceTheFarLegLeftAnswer(self):
        log = []

        def instrument(tap):
            send, wait_for = tap.send, tap.wait_for

            def logged_send(fields):
                log.append(("send", dict(fields).get("Action")))
                return send(fields)

            def logged_wait_for(*args, **kwargs):
                frame = wait_for(*args, **kwargs)
                log.append(("awaited", frame.get("Event"), frame.get("Exten"), frame.get("Application")))
                return frame

            tap.send, tap.wait_for = logged_send, logged_wait_for

        self._run(instrument)

        first_flash = log.index(("send", "SendFlash"))
        self.assertIn(("awaited", "Newexten", "flash-in", "Wait"), log[:first_flash],
                      "on 22.11.0 a far-leg Flash that arrived during Answer() was reported again after it")

    def test_files_ShouldKeepTheFarLegUpLongEnoughForBothFlashes(self):
        extensions = cap.WORKLOADS[self._NAME]["files"]["extensions.conf"]

        flash_in = extensions[extensions.index("exten => flash-in,1,Answer()"):]
        self.assertIn(" same => n,Wait(8)", flash_in.split("\n\n")[0])


class _RenamePbx:
    """The answers a PBX gives the renamed-caller-and-participant workload, in a fake's script shape.

    Each originate that lands in the queue or the room joins with the originate's caller number;
    the regex Hangup ends whoever is waiting, with a leave under the name and id the fake holds
    for it. `bridge_error`, `no_join_for` and `close_on` let a test refuse, starve or cut a step.
    """

    def __init__(self, bridge_error=False, no_join_for=(), close_on=None, transfer_status="SUCCESS",
                 transferer_bridged=True):
        self.bridge_error = bridge_error
        self.no_join_for = set(no_join_for)
        self.close_on = close_on
        self.transfer_status = transfer_status
        self.transferer_bridged = transferer_bridged
        self.n = 0
        self.waiting = []  # (surface, channel, uid, cid)
        self.variables = {}  # (channel, variable) -> value, from Setvar
        self.sock = None  # set by the test, so `close_on` can hang up the manager connection

    def ok(self, message="Success"):
        return b"Response: Success\r\nActionID: {id}\r\nMessage: " + message.encode() + b"\r\n\r\n"

    def script(self):
        return {
            "Login": [self.ok("Authentication accepted")],
            "UserEvent": lambda h: [self.ok("Event Sent"),
                                    _ev(*[(k, v) for k, v in h.items() if k != "Action"],
                                        ("Event", "UserEvent"))],
            "Originate": self.originate,
            "Bridge": lambda h: [b"Response: Error\r\nActionID: {id}\r\nMessage: Unable to add Channel1\r\n\r\n"]
            if self.bridge_error else [self.ok("Channels have been bridged")],
            "Getvar": lambda h: [b"Response: Success\r\nActionID: {id}\r\nVariable: " + h["Variable"].encode()
                                 + b"\r\nValue: " + {"CHANNEL(pjsip,call-id)": b"cid-1",
                                                     "CHANNEL(pjsip,local_tag)": b"lt-2",
                                                     "CHANNEL(pjsip,remote_tag)": b"rt-3"}[h["Variable"]]
                                 + b"\r\n\r\n"],
            "QueueStatus": lambda h: [self.ok("Queue status will follow")] + [
                _ev(("Event", "QueueEntry"), ("Queue", "capture-q"), ("Channel", c), ("Uniqueid", u),
                    ("ActionID", h["ActionID"])) for s, c, u, _ in self.waiting if s == "queue"] + [
                b"Event: QueueStatusComplete\r\nActionID: {id}\r\nEventList: Complete\r\n\r\n"],
            "ConfbridgeList": self.confbridge_list,
            "ConfbridgeMute": [self.ok("User muted")],
            "Hangup": self.hangup,
            "Setvar": self.setvar,
            "Redirect": self.redirect,
            "CoreShowChannels": [b"Response: Success\r\nActionID: {id}\r\nEventList: start\r\n\r\n",
                                 b"Event: CoreShowChannelsComplete\r\nActionID: {id}\r\nEventList: Complete"
                                 b"\r\nListItems: 0\r\n\r\n"],
            "Logoff": [b"Response: Goodbye\r\nActionID: {id}\r\n\r\n"],
        }

    def confbridge_list(self, h):
        rows = [(c, u) for s, c, u, _ in self.waiting if s == "conference"]
        if not rows:
            return [b"Response: Error\r\nActionID: {id}\r\nMessage: No active conferences.\r\n\r\n"]
        return [self.ok("Confbridge user list will follow")] + [
            _ev(("Event", "ConfbridgeList"), ("Conference", "capture-room"), ("Channel", c), ("Uniqueid", u),
                ("ActionID", h["ActionID"])) for c, u in rows] + [
            b"Event: ConfbridgeListComplete\r\nActionID: {id}\r\nEventList: Complete\r\n\r\n"]

    def hangup(self, h):
        out = [self.ok("Channels hung up will follow")]
        for surface, channel, uid, cid in self.waiting:
            event = "QueueCallerLeave" if surface == "queue" else "ConfbridgeLeave"
            out.append(_ev(("Event", event), ("Channel", channel), ("Uniqueid", uid), ("CallerIDNum", cid)))
        self.waiting = []
        return out

    def setvar(self, h):
        self.variables[(h["Channel"], h["Variable"])] = h["Value"]
        return [self.ok("Variable Set")]

    def redirect(self, h):
        """A waiting target leaves normally; the transferer's leg runs `refer-go` (its Transfer())."""
        out = [self.ok("Redirect successful")]
        for item in [w for w in self.waiting if w[1] == h["Channel"]]:
            surface, channel, uid, cid = item
            self.waiting.remove(item)
            event = "QueueCallerLeave" if surface == "queue" else "ConfbridgeLeave"
            out.append(_ev(("Event", event), ("Channel", channel), ("Uniqueid", uid), ("CallerIDNum", cid)))
        if h.get("Exten") == "refer-go":
            out += [_ev(("Event", "AttendedTransfer"), ("Result", "Success"),
                        ("OrigTransfererChannel", "PJSIP/loop-000000ff"), ("OrigTransfererUniqueid", "1700000000.99"))]
            out += [_ev(("Event", "UserEvent"), ("UserEvent", "CaptureApp"), ("App", "Transfer"),
                        ("Result", self.transfer_status), ("Channel", h["Channel"]))]
        return out

    def originate(self, h):
        if self.close_on and h["Channel"].startswith(self.close_on):
            self.sock.shutdown(socket.SHUT_RDWR)
            raise OSError("the fake PBX hangs up the manager connection")
        self.n += 1
        channel, cid = h["Channel"], h["CallerID"].split("<")[1].rstrip(">")
        queued = [self.ok("Originate successfully queued")]
        stem = channel.split("/")[1].split("@")[0]
        uid = f"1700000000.{self.n}"
        if channel.startswith("PJSIP/"):
            name = f"PJSIP/loop-{self.n:08x}"
        else:
            name = f"Local/{stem}@capture-{self.n:08x};2"
        surface = {"queue": "queue", "queue-in": "queue", "queue-seq": "queue",
                   "conf": "conference", "conf-in": "conference", "conf-seq": "conference"}.get(stem)
        if surface and cid not in self.no_join_for:
            event = "QueueCallerJoin" if surface == "queue" else "ConfbridgeJoin"
            join = _ev(("Event", event), ("Channel", name), ("Uniqueid", uid), ("CallerIDNum", cid))
            if stem.endswith("-seq"):  # one second in, then gone
                leave = "QueueCallerLeave" if surface == "queue" else "ConfbridgeLeave"
                return queued + [join, _ev(("Event", leave), ("Channel", name), ("Uniqueid", uid),
                                           ("CallerIDNum", cid))]
            self.waiting.append((surface, name, uid, cid))
            return queued + [join]
        if stem in ("peer", "dialring"):
            exten = "peer" if stem == "peer" else "ring"
            ring = f"Local/ring@capture-{self.n:08x};1"
            frames = [_ev(("Event", "Newchannel"), ("Channel", ring), ("Uniqueid", uid + "1"))] \
                if stem == "dialring" else []
            return queued + frames + [_ev(("Event", "Newexten"), ("Channel", name), ("Uniqueid", uid),
                                          ("Exten", exten), ("Application", "Wait"))]
        if stem == "lopt":  # the control pair: its far leg dials the tone
            return queued + [_ev(("Event", "Newexten"), ("Channel", f"Local/tone@capture-{self.n:08x};2"),
                                 ("Uniqueid", uid + "2"), ("Exten", "tone"), ("Application", "Milliwatt"))]
        if stem == "hold-in":  # the transferer's first call: answered, then its answering leg is bridged
            answered = [_ev(("Event", "OriginateResponse"), ("ActionID", h["ActionID"]),
                            ("Response", "Success"), ("Channel", name), ("Uniqueid", uid))]
            bridged = [_ev(("Event", "BridgeEnter"), ("Channel", f"PJSIP/loop-{self.n:08x}b"), ("Uniqueid", uid + "b"),
                           ("Context", "capture-sip"), ("Exten", "hold-in"))] if self.transferer_bridged else []
            return queued + answered + bridged
        if stem in ("pickchan", "pickup"):
            app = "PickupChan" if stem == "pickchan" else "Pickup"
            return queued + [_ev(("Event", "UserEvent"), ("UserEvent", "CaptureApp"), ("App", app),
                                 ("Result", "returned"), ("Channel", name), ("Uniqueid", uid))]
        return queued


class _FakeAri:
    def __init__(self, status=409, body='{"message":"Channel not in Stasis application"}'):
        self.status, self.body, self.calls = status, body, []

    def post(self, path, query=()):
        self.calls.append((path, list(query)))
        return self.status, self.body


class RenamedCallerAndParticipantWorkloadTests(unittest.TestCase):
    _NAME = "renamed-caller-and-participant"

    def _run(self, pbx_state=None, options=None, ari=None):
        pbx_state = pbx_state or _RenamePbx()
        ours, theirs = socket.socketpair()
        pbx_state.sock = theirs
        pbx = _FakePbx(theirs, pbx_state.script())
        pbx.start()
        try:
            tap = cap.Tap(ours)
            tap.pump = lambda seconds: cap.Tap.pump(tap, min(seconds, 0.02))
            tap.options = dict(options or {})
            tap.ari = ari
            tap.login("capture", "secret")
            steps = cap.WORKLOADS[self._NAME]["run"](tap)
            tap.logoff()
        finally:
            ours.close()
            theirs.close()
            pbx.join(2)
        return steps, pbx.received, tap

    @staticmethod
    def _outcomes(tap):
        return {f["Step"]: f for f in tap.frames
                if f.get("Event") == "UserEvent" and f.get("UserEvent") == "CaptureOutcome"}

    def test_workloads_ShouldOfferRenamedCallerAndParticipant_WithAri(self):
        out = io.StringIO()
        with contextlib.redirect_stdout(out):
            self.assertEqual(0, cap.main(["workloads"]))

        self.assertIn(self._NAME + ":", out.getvalue())
        self.assertTrue(cap.WORKLOADS[self._NAME]["ari"])
        self.assertFalse(cap.WORKLOADS["dialplan-and-parking"].get("ari"))

    def test_files_ShouldConfigureAQueueARoomTheReplacesHeaderAndALoopbackPjsipChannel(self):
        files = cap.WORKLOADS[self._NAME]["files"]

        self.assertEqual({"extensions.conf", "queues.conf", "confbridge.conf", "pjsip.conf"}, set(files))
        self.assertIn("joinempty = yes", files["queues.conf"], "a caller must wait in a queue with no members")
        self.assertIn("leavewhenempty = no", files["queues.conf"])
        extensions = files["extensions.conf"]
        for app in ("Queue(capture-q)", "ConfBridge(capture-room)", "PickupChan(${TARGET})", "Pickup(${TARGET})",
                    "Set(PJSIP_HEADER(add,Replaces)=${RH})", "b(capture-replaces^s^1)",
                    "Dial(Local/peer@capture/n,10)"):
            self.assertIn(app, extensions)
        pjsip = files["pjsip.conf"]
        self.assertIn("bind = 127.0.0.1:5060", pjsip, "SIP stays inside the container")
        self.assertNotIn("0.0.0.0", pjsip)
        for text in files.values():
            text.encode("ascii")  # capture() writes them as ascii
        self.assertIn("password = s3cr3t", cap._ARI_CONF.format(user="capture", secret="s3cr3t"))
        self.assertIn("bindport = 8088", cap._HTTP_CONF.format(port=cap.ARI_PORT))

    def test_tap_ShouldLeaveTheEventFilterOpen_ForRenameMasqueradeAndLocalOptimization(self):
        self.assertFalse(set(cap.RENAME_EVENTS) & set(cap.ALWAYS_DROPPED))
        self.assertNotIn("eventfilter", cap._MANAGER_CONF)
        tap_bytes = _BANNER + b"".join(frame(("Event", e), ("Channel", "Local/x@y-1;2"), ("Uniqueid", "1.1"))
                                       for e in cap.RENAME_EVENTS)
        recording, _ = cap.reduce_stream(tap_bytes)
        for event in cap.RENAME_EVENTS:
            self.assertIn(f"Event: {event}\r\n".encode(), recording)
        _steps, received, _tap = self._run(ari=_FakeAri())
        self.assertEqual("on", received[0].get("Events"))
        self.assertNotIn("Filter", [h.get("Action") for h in received])

    def test_run_ShouldWalkTheControlsThenEveryTriggerOnBothSurfaces_InTheSearchOrder(self):
        steps, _received, _tap = self._run(ari=_FakeAri())

        expected = ["negative-queue", "negative-conference", "positive-bridge", "positive-pickupchan"]
        for trigger in ("bridge", "pickupchan", "pickup", "local-optimization", "replaces", "refer",
                        "ari-move", "redirect"):
            if trigger == "local-optimization":
                expected.append("local-optimization-control")
            for surface in ("queue", "conference"):
                expected += [f"{trigger}-{surface}", f"{trigger}-{surface}-waiting"]
        self.assertEqual(expected + ["done"], steps)

    def test_run_ShouldSendEachTriggerAtTheWaitingChannel(self):
        ari = _FakeAri()
        _steps, received, tap = self._run(ari=ari)

        joins = {f["Channel"]: f["Uniqueid"] for f in tap.frames
                 if f.get("Event") in ("QueueCallerJoin", "ConfbridgeJoin")}
        bridged = [h["Channel1"] for h in received if h.get("Action") == "Bridge"]
        self.assertEqual(3, len(bridged), "the positive control, then the caller and the participant")
        self.assertTrue(all(c in joins for c in bridged[1:]))
        targets = [h["Variable"] for h in received if h.get("Action") == "Originate"
                   and h.get("Variable", "").startswith("__TARGET=")]
        self.assertEqual(5, len(targets))  # the ringing control, two PickupChan, two Pickup()
        self.assertTrue(targets[0].endswith(";1"), "the dialled leg rings; ;2 runs the dialplan")
        self.assertEqual(["__TARGET=queue@capture", "__TARGET=conf@capture"], targets[3:])
        no_n = [(h["Channel"], h["Exten"]) for h in received if h.get("Action") == "Originate"
                and h.get("Exten") in ("dialpeer", "dialtone")]
        self.assertEqual([("Local/lopt@capture", "dialpeer"), ("Local/queue@capture", "dialtone"),
                          ("Local/conf@capture", "dialtone")], no_n,
                         "the control pair, then a waiting caller and a participant whose near leg hears a tone")
        getvars = [(h["Channel"], h["Variable"]) for h in received if h.get("Action") == "Getvar"]
        self.assertEqual(12, len(getvars), "Replaces and REFER read the waiting leg's dialog, on each surface")
        self.assertTrue(all(c.startswith("PJSIP/loop-") and c in joins for c, _ in getvars))
        replaces = [h["Variable"] for h in received if h.get("Variable", "").startswith("__RH=")]
        self.assertEqual(["__RH=cid-1;to-tag=lt-2;from-tag=rt-3"] * 2, replaces,
                         "to-tag is the waiting leg's own tag, from-tag the far end's")
        self.assertEqual(2, len(ari.calls))
        for path, query in ari.calls:
            self.assertIn(path.split("/")[3], joins.values())
            self.assertEqual([("app", cap.ARI_APP)], query)

    def test_run_ShouldListAndMuteWhileTheTargetWaits(self):
        _steps, received, _tap = self._run(ari=_FakeAri())

        step, seen = None, []
        for h in received:
            if h.get("Action") == "UserEvent" and h.get("UserEvent") == "CaptureMarker":
                step = h["Step"]
            elif h.get("Action") in ("QueueStatus", "ConfbridgeList", "ConfbridgeMute"):
                seen.append((step, h["Action"]))
        self.assertIn(("bridge-queue-waiting", "QueueStatus"), seen)
        self.assertIn(("replaces-conference-waiting", "ConfbridgeList"), seen)
        self.assertIn(("replaces-conference-waiting", "ConfbridgeMute"), seen)
        self.assertNotIn(("bridge-queue-waiting", "ConfbridgeMute"), seen)

    def test_run_ShouldRecordEachOutcome_AndCountRefusals(self):
        _steps, _received, tap = self._run(_RenamePbx(bridge_error=True), ari=_FakeAri())

        outcomes = self._outcomes(tap)
        self.assertEqual("1", outcomes["bridge-queue"]["Refusals"])
        self.assertIn("Unable to add Channel1", outcomes["bridge-queue"]["Detail"])
        self.assertEqual("1", outcomes["pickupchan-conference"]["Refusals"], "PickupChan came back")
        self.assertEqual("1", outcomes["ari-move-queue"]["Refusals"])
        self.assertIn("HTTP 409", outcomes["ari-move-queue"]["Detail"])
        self.assertEqual("0", outcomes["negative-queue"]["Refusals"])
        self.assertEqual("1", outcomes["negative-conference"]["Leaves"])
        negative = outcomes["negative-queue"]
        self.assertEqual(negative["TargetId"], negative["LeaveIds"])
        self.assertEqual("", negative["RenamedIds"])

    def test_run_ShouldRecordARefusal_WhenAriIsUnavailable(self):
        _steps, _received, tap = self._run(ari=None)

        self.assertEqual("ARI unavailable", self._outcomes(tap)["ari-move-conference"]["Detail"])

    def test_run_ShouldGoOn_WhenATriggerStepTimesOut(self):
        # 7206 is the caller number of the first trigger's target (bridge-queue; the controls took 7200-7205)
        _steps, _received, tap = self._run(_RenamePbx(no_join_for={"7206"}), options={"join_timeout": 0.3},
                                           ari=_FakeAri())

        outcomes = self._outcomes(tap)
        self.assertIn("error: timed out", outcomes["bridge-queue"]["Detail"])
        self.assertNotIn("error", outcomes["bridge-conference"].get("Detail", ""))
        self.assertIn("ari-move-conference", outcomes)

    def test_run_ShouldStop_WhenThePbxClosesTheConnection(self):
        with self.assertRaises(cap.ConnectionClosed):
            self._run(_RenamePbx(close_on="Local/peer"), ari=_FakeAri())

    def test_run_ShouldAddSequentialCallsPerSurface_OnlyWhenAsked(self):
        steps, received, _tap = self._run(options={"sequential_calls": 3}, ari=_FakeAri())

        self.assertEqual(["sequential-queue", "sequential-conference", "done"], steps[-3:])
        seq = [h["Channel"] for h in received if h.get("Action") == "Originate" and "-seq@" in h["Channel"]]
        self.assertEqual(["Local/queue-seq@capture/n"] * 3 + ["Local/conf-seq@capture/n"] * 3, seq)
        steps, received, _tap = self._run(ari=_FakeAri())
        self.assertFalse([s for s in steps if s.startswith("sequential")])


class RenamedWorkloadAddedTriggersTests(unittest.TestCase):
    """Audio through the Local pair, the REFER attended transfer and Redirect."""

    _run = RenamedCallerAndParticipantWorkloadTests._run
    _outcomes = staticmethod(RenamedCallerAndParticipantWorkloadTests._outcomes)
    _NAME = RenamedCallerAndParticipantWorkloadTests._NAME

    def test_files_ShouldSendATonePastTheLocalPair_AndOfferTransferAndRedirect(self):
        extensions = cap.WORKLOADS[self._NAME]["files"]["extensions.conf"]

        for line in ("exten => tone,1,Answer()", " same => n,Milliwatt()",
                     "exten => dialtone,1,Dial(Local/tone@capture/n,30)",
                     "exten => lopt,1,Dial(Local/tone@capture/n,30)",
                     "exten => refer-go,1,Transfer(PJSIP/${RT})",
                     " same => n,UserEvent(CaptureApp,App: Transfer,Result: ${TRANSFERSTATUS})",
                     "exten => redirected,1,Answer()",
                     "exten => hold-in,1,Answer()", " same => n,Dial(Local/peer@capture/n,30)"):
            self.assertIn(line, extensions)
        self.assertNotIn("Milliwatt", extensions.split("exten => peer,1")[1].split("exten =>")[0],
                         "the Bridge triggers keep their silent peer")

    def test_run_ShouldAskTheTransferersLegToReferTheWaitingLegsDialog(self):
        _steps, received, tap = self._run(ari=_FakeAri())

        holds = [h for h in received if h.get("Action") == "Originate" and h["Channel"] == "PJSIP/hold-in@loop"]
        self.assertEqual(2, len(holds), "one transferer call per surface")
        setvars = [h for h in received if h.get("Action") == "Setvar"]
        self.assertEqual(["RT", "RT"], [h["Variable"] for h in setvars])
        self.assertEqual("<sip:queue-in@127.0.0.1:5060?Replaces=cid-1%3Bto-tag%3Dlt-2%3Bfrom-tag%3Drt-3>",
                         setvars[0]["Value"])
        self.assertTrue(setvars[1]["Value"].startswith("<sip:conf-in@127.0.0.1:5060?Replaces="))
        redirects = [h for h in received if h.get("Action") == "Redirect" and h.get("Exten") == "refer-go"]
        self.assertEqual([h["Channel"] for h in setvars], [h["Channel"] for h in redirects],
                         "the leg that holds RT is the one sent to Transfer()")
        joins = {f["Channel"] for f in tap.frames if f.get("Event") in ("QueueCallerJoin", "ConfbridgeJoin")}
        self.assertFalse(joins & {h["Channel"] for h in redirects}, "the transferer is not the waiting leg")
        outcome = self._outcomes(tap)["refer-queue"]
        self.assertEqual("0", outcome["Refusals"])
        self.assertIn("AttendedTransfer frames: 1", outcome["Detail"])
        self.assertIn("Transfer: SUCCESS", outcome["Detail"])

    def test_run_ShouldNotRefer_UntilTheTransferersAnsweringLegIsBridged(self):
        # an unbridged transferee leg makes Asterisk answer the REFER with `Result: Invalid`
        _steps, received, tap = self._run(_RenamePbx(transferer_bridged=False), options={"join_timeout": 0.3},
                                          ari=_FakeAri())

        self.assertFalse([h for h in received if h.get("Action") == "Setvar"])
        self.assertIn("error: timed out", self._outcomes(tap)["refer-queue"]["Detail"])
        self.assertIn("the transferer's bridge", self._outcomes(tap)["refer-queue"]["Detail"])

    def test_run_ShouldCountARefusal_WhenTransferDoesNotSucceed(self):
        _steps, _received, tap = self._run(_RenamePbx(transfer_status="FAILURE"),
                                           ari=_FakeAri())

        self.assertEqual("1", self._outcomes(tap)["refer-conference"]["Refusals"])

    def test_run_ShouldRedirectTheWaitingChannel_OnBothSurfaces(self):
        _steps, received, tap = self._run(ari=_FakeAri())

        joins = {f["Channel"] for f in tap.frames if f.get("Event") in ("QueueCallerJoin", "ConfbridgeJoin")}
        redirects = [h for h in received if h.get("Action") == "Redirect" and h.get("Exten") == "redirected"]
        self.assertEqual(2, len(redirects))
        self.assertTrue(all(h["Channel"] in joins and h["Context"] == "capture" and h["Priority"] == "1"
                            for h in redirects))
        outcomes = self._outcomes(tap)
        self.assertIn("Redirect: Success", outcomes["redirect-queue"]["Detail"])
        self.assertEqual("1", outcomes["redirect-conference"]["Leaves"])

    def test_run_ShouldRecordTheControlPairsOptimization_OutsideBothSurfaces(self):
        _steps, _received, tap = self._run(ari=_FakeAri())

        control = self._outcomes(tap)["local-optimization-control"]
        self.assertEqual("none", control["Surface"])
        self.assertIn("LocalOptimizationBegin frames: 0", control["Detail"])
        self.assertIn("unreachable by construction", self._outcomes(tap)["local-optimization-queue"]["Detail"])


class RenameDetectionTests(unittest.TestCase):
    def test_renamed_ShouldIgnoreSentinelUniqueids_LikeAFailedOriginatesUnknown(self):
        frames = [
            {"Event": "OriginateResponse", "Response": "Failure", "Channel": "Local/pickchan@capture/n",
             "Uniqueid": "<unknown>"},
            {"Event": "OriginateResponse", "Response": "Failure", "Channel": "Local/pickup@capture/n",
             "Uniqueid": "<unknown>"},
            {"Event": "Newchannel", "Channel": "Local/a;1", "Uniqueid": ""},
            {"Event": "Newchannel", "Channel": "Local/b;1", "Uniqueid": ""},
        ]

        self.assertEqual({}, cap.renamed_uniqueids(frames))
        self.assertEqual({}, dict(cap.channel_names_by_uniqueid(frames)))

    def test_renamed_ShouldReportANameChangePerUniqueid_WithOrWithoutARenameFrame(self):
        frames = [
            {"Event": "QueueCallerJoin", "Channel": "PJSIP/a-1", "Uniqueid": "1.1"},
            {"Event": "Newstate", "Channel": "PJSIP/b-2", "Uniqueid": "1.1"},  # no Rename frame
            {"Event": "Rename", "Channel": "PJSIP/c-3", "Newname": "PJSIP/d-4", "Uniqueid": "1.2"},
            {"Event": "DialBegin", "DestChannel": "Local/x;1", "DestUniqueid": "1.3",
             "Channel": "PJSIP/e-5", "Uniqueid": "1.4"},
            {"Event": "Hangup", "Channel": "Local/x;1", "Uniqueid": "1.3"},
        ]

        self.assertEqual({"1.1": ["PJSIP/a-1", "PJSIP/b-2"], "1.2": ["PJSIP/c-3", "PJSIP/d-4"]},
                         cap.renamed_uniqueids(frames))
        self.assertEqual(["Local/x;1"], cap.channel_names_by_uniqueid(frames)["1.3"])

    def test_renamed_ShouldReportNothing_WhenANameMovesWithItsUniqueid(self):
        frames = [{"Event": "Newchannel", "Channel": "Local/a;2", "Uniqueid": "1.1"},
                  {"Event": "Hangup", "Channel": "Local/a;2", "Uniqueid": "1.1"},
                  {"Event": "Newchannel", "Channel": "Local/a;2", "Uniqueid": "1.9"}]

        self.assertEqual({}, cap.renamed_uniqueids(frames))


class AriClientTests(unittest.TestCase):
    def test_post_ShouldSendBasicAuthAndTheQuery_AndReturnAnHttpErrorAsAResult(self):
        import urllib.error
        seen = []

        def opener(request, timeout):
            seen.append((request.full_url, request.get_method(), request.get_header("Authorization"), timeout))
            raise urllib.error.HTTPError(request.full_url, 409, "Conflict", {},
                                         io.BytesIO(b'{"message":"Channel not in Stasis application"}'))

        client = cap.AriClient("http://127.0.0.1:49152/", "capture", "pw", opener=opener)
        status, body = client.post("/ari/channels/1.2/move", [("app", "capture-ari")])

        self.assertEqual(409, status)
        self.assertIn("not in Stasis", body)
        self.assertEqual("http://127.0.0.1:49152/ari/channels/1.2/move?app=capture-ari", seen[0][0])
        self.assertEqual("POST", seen[0][1])
        self.assertEqual("Basic Y2FwdHVyZTpwdw==", seen[0][2])



class _RevisitPbx:
    """The answers a PBX gives the queue-revisit workload, in a fake's script shape: each originated caller is
    connected in q-first to PJSIP/agent1 and, after a BlindTransfer of that member's leg, in q-second to PJSIP/agent2.
    With `refuse_transfer`, BlindTransfer answers an Error and the caller stays with the first member."""

    def __init__(self, refuse_transfer=False):
        self.refuse_transfer = refuse_transfer
        self.calls = 0
        self.cid = None

    def ok(self, message="Success"):
        return b"Response: Success\r\nActionID: {id}\r\nMessage: " + message.encode() + b"\r\n\r\n"

    def connect(self, queue, member):
        return _ev(("Event", "AgentConnect"), ("Channel", f"PJSIP/loop-{self.calls:08d}"),
                   ("Uniqueid", f"1700000000.{self.calls}"), ("CallerIDNum", self.cid), ("Queue", queue),
                   ("Interface", member), ("MemberName", member),
                   ("DestChannel", f"{member}-{self.calls:08d}"))

    def originate(self, h):
        self.calls += 1
        self.cid = h["CallerID"].split("<")[1].rstrip(">")
        return [self.ok("Originate successfully queued"), self.connect("q-first", "PJSIP/agent1")]

    def blind_transfer(self, h):
        if self.refuse_transfer:
            return [b"Response: Error\r\nActionID: {id}\r\nMessage: No such channel\r\n\r\n"]
        return [self.ok("Transfer succeeded"), self.connect("q-second", "PJSIP/agent2")]

    def script(self):
        return {
            "Login": [self.ok("Authentication accepted")],
            "UserEvent": lambda h: [self.ok("Event Sent"),
                                    _ev(("Event", "UserEvent"), ("UserEvent", h["UserEvent"]),
                                        ("Step", h.get("Step", "")), ("ActionID", h["ActionID"]))],
            "Originate": self.originate,
            "BlindTransfer": self.blind_transfer,
            "Hangup": [self.ok("Channel Hungup")],
            "CoreShowChannels": [b"Response: Success\r\nActionID: {id}\r\nEventList: start\r\n\r\n",
                                 b"Event: CoreShowChannelsComplete\r\nActionID: {id}\r\nEventList: Complete\r\n"
                                 b"ListItems: 0\r\n\r\n"],
            "Logoff": [b"Response: Goodbye\r\nActionID: {id}\r\n\r\n"],
        }


class QueueRevisitWorkloadTests(unittest.TestCase):
    _NAME = "queue-revisit"

    def _run(self, calls, refuse_transfer=False):
        pbx_state = _RevisitPbx(refuse_transfer)
        ours, theirs = socket.socketpair()
        pbx = _FakePbx(theirs, pbx_state.script())
        pbx.start()
        try:
            tap = cap.Tap(ours)
            tap.pump = lambda seconds: cap.Tap.pump(tap, min(seconds, 0.02))
            tap.options = {"sequential_calls": calls, "join_timeout": 2}
            tap.login("capture", "secret")
            steps = cap.WORKLOADS[self._NAME]["run"](tap)
            tap.logoff()
        finally:
            ours.close()
            theirs.close()
            pbx.join(2)
        return steps, pbx.received

    @staticmethod
    def _outcomes(received):
        return [(h["Step"], h["CallerId"], h["Connects"], h["Members"], h.get("Detail", ""))
                for h in received if h.get("Action") == "UserEvent" and h.get("UserEvent") == "CaptureOutcome"]

    def test_workloads_ShouldOfferQueueRevisit(self):
        out = io.StringIO()
        with contextlib.redirect_stdout(out):
            self.assertEqual(0, cap.main(["workloads"]))

        self.assertIn(self._NAME + ":", out.getvalue())

    def test_files_ShouldConfigureTwoQueuesWithOneEndpointMemberEachOnALoopback(self):
        files = cap.WORKLOADS[self._NAME]["files"]

        self.assertEqual({"extensions.conf", "queues.conf", "pjsip.conf"}, set(files))
        queues = files["queues.conf"]
        self.assertIn("[q-first](revisit)\nmember => PJSIP/agent1", queues)
        self.assertIn("[q-second](revisit)\nmember => PJSIP/agent2", queues)
        self.assertIn("exten => second,1,Queue(q-second)", files["extensions.conf"])
        self.assertIn("exten => revisit-in,1,Answer()", files["extensions.conf"])
        pjsip = files["pjsip.conf"]
        self.assertIn("bind = 127.0.0.1:5060", pjsip, "SIP stays inside the container")
        self.assertIn("contact = sip:agent1@127.0.0.1:5060", pjsip)
        self.assertIn("contact = sip:agent2@127.0.0.1:5060", pjsip)
        self.assertNotIn("0.0.0.0", pjsip)

    def test_run_ShouldTransferEachRevisitCallsFirstMemberIntoTheSecondQueue_ThenRunTheControls(self):
        steps, received = self._run(2)

        self.assertEqual(["revisit-01", "revisit-02", "once-01", "once-02", "done"], steps)
        transfers = [(h["Channel"], h["Context"], h["Exten"]) for h in received if h.get("Action") == "BlindTransfer"]
        self.assertEqual([("PJSIP/agent1-00000001", "capture", "second"),
                          ("PJSIP/agent1-00000002", "capture", "second")], transfers,
                         "only the revisit calls are transferred, each from its first member's own leg")
        originates = [(h["Channel"], h["CallerID"]) for h in received if h.get("Action") == "Originate"]
        self.assertEqual([("PJSIP/revisit-in@loop", f"Capture <{cid}>") for cid in ("9101", "9102", "9201", "9202")],
                         originates)
        self.assertEqual([("revisit-01", "9101", "2", "PJSIP/agent1,PJSIP/agent2", ""),
                          ("revisit-02", "9102", "2", "PJSIP/agent1,PJSIP/agent2", ""),
                          ("once-01", "9201", "1", "PJSIP/agent1", ""),
                          ("once-02", "9202", "1", "PJSIP/agent1", "")], self._outcomes(received))

    def test_run_ShouldRecordARefusedTransferAndGoOn_WhenThePbxRefusesIt(self):
        steps, received = self._run(1, refuse_transfer=True)

        self.assertEqual(["revisit-01", "once-01", "done"], steps)
        self.assertEqual([("revisit-01", "9101", "1", "PJSIP/agent1", "BlindTransfer: Error: No such channel"),
                          ("once-01", "9201", "1", "PJSIP/agent1", "")], self._outcomes(received))

    def test_run_ShouldPlaceOneCallOfEachKind_WhenNoCountIsGiven(self):
        pbx_state = _RevisitPbx()
        ours, theirs = socket.socketpair()
        pbx = _FakePbx(theirs, pbx_state.script())
        pbx.start()
        try:
            tap = cap.Tap(ours)
            tap.pump = lambda seconds: cap.Tap.pump(tap, min(seconds, 0.02))
            tap.login("capture", "secret")
            steps = cap.WORKLOADS[self._NAME]["run"](tap)
        finally:
            ours.close()
            theirs.close()
            pbx.join(2)

        self.assertEqual(["revisit-01", "once-01", "done"], steps)



class _RingNoAnswerPbx:
    """The answers a PBX gives the queue-ring-no-answer workload, in a fake's script shape: an originated caller of
    rna-in or busy-in is reported not answered by its queue's first member, then connected to PJSIP/agent2; one of
    answer-in is connected to PJSIP/agent2 at once. With `never_connect`, no AgentConnect follows."""

    _FIRST = {"rna-in": ("q-rna", "PJSIP/agent1"), "busy-in": ("q-busy", "PJSIP/agent3"), "answer-in": ("q-answer", None)}

    def __init__(self, never_connect=False):
        self.never_connect = never_connect
        self.calls = 0

    def ok(self, message="Success"):
        return b"Response: Success\r\nActionID: {id}\r\nMessage: " + message.encode() + b"\r\n\r\n"

    def member_event(self, event, cid, queue, member):
        return _ev(("Event", event), ("Channel", f"PJSIP/loop-{self.calls:08d}"),
                   ("Uniqueid", f"1700000000.{self.calls}"), ("CallerIDNum", cid), ("Queue", queue),
                   ("Interface", member), ("MemberName", member))

    def originate(self, h):
        self.calls += 1
        cid = h["CallerID"].split("<")[1].rstrip(">")
        queue, first = self._FIRST[h["Channel"].split("/")[1].split("@")[0]]
        frames = [self.ok("Originate successfully queued")]
        if first:
            frames.append(self.member_event("AgentRingNoAnswer", cid, queue, first))
        if not self.never_connect:
            frames.append(self.member_event("AgentConnect", cid, queue, "PJSIP/agent2"))
        return frames

    def script(self):
        return {
            "Login": [self.ok("Authentication accepted")],
            "UserEvent": lambda h: [self.ok("Event Sent"),
                                    _ev(("Event", "UserEvent"), ("UserEvent", h["UserEvent"]),
                                        ("Step", h.get("Step", "")), ("ActionID", h["ActionID"]))],
            "Originate": self.originate,
            "Hangup": [self.ok("Channel Hungup")],
            "CoreShowChannels": [b"Response: Success\r\nActionID: {id}\r\nEventList: start\r\n\r\n",
                                 b"Event: CoreShowChannelsComplete\r\nActionID: {id}\r\nEventList: Complete\r\n"
                                 b"ListItems: 0\r\n\r\n"],
            "Logoff": [b"Response: Goodbye\r\nActionID: {id}\r\n\r\n"],
        }


class QueueRingNoAnswerWorkloadTests(unittest.TestCase):
    _NAME = "queue-ring-no-answer"

    def _run(self, calls, never_connect=False):
        pbx_state = _RingNoAnswerPbx(never_connect)
        ours, theirs = socket.socketpair()
        pbx = _FakePbx(theirs, pbx_state.script())
        pbx.start()
        try:
            tap = cap.Tap(ours)
            tap.pump = lambda seconds: cap.Tap.pump(tap, min(seconds, 0.02))
            tap.options = {"sequential_calls": calls, "join_timeout": 0.3}
            tap.login("capture", "secret")
            steps = cap.WORKLOADS[self._NAME]["run"](tap)
            tap.logoff()
        finally:
            ours.close()
            theirs.close()
            pbx.join(2)
        return steps, pbx.received

    @staticmethod
    def _outcomes(received):
        return [(h["Step"], h["CallerId"], h["RingNoAnswers"], h["Missed"], h["Connects"], h["Members"],
                 h.get("Detail", ""))
                for h in received if h.get("Action") == "UserEvent" and h.get("UserEvent") == "CaptureOutcome"]

    def test_workloads_ShouldOfferQueueRingNoAnswer(self):
        out = io.StringIO()
        with contextlib.redirect_stdout(out):
            self.assertEqual(0, cap.main(["workloads"]))

        self.assertIn(self._NAME + ":", out.getvalue())

    def test_files_ShouldConfigureLinearQueuesWhoseFirstMemberDoesNotAnswer_OnALoopback(self):
        files = cap.WORKLOADS[self._NAME]["files"]

        self.assertEqual({"extensions.conf", "queues.conf", "pjsip.conf"}, set(files))
        queues = files["queues.conf"]
        self.assertIn("strategy = linear", queues)
        self.assertIn("timeout = 2", queues)
        self.assertIn("[q-rna](rna)\nmember => PJSIP/agent1\nmember => PJSIP/agent2", queues)
        self.assertIn("[q-busy](rna)\nmember => PJSIP/agent3\nmember => PJSIP/agent2", queues)
        self.assertIn("[q-answer](rna)\nmember => PJSIP/agent2\n", queues)
        extensions = files["extensions.conf"]
        self.assertIn("exten => agent1,1,Ringing()\n same => n,Wait(60)", extensions, "agent1 never answers")
        self.assertIn("exten => agent3,1,Busy(5)", extensions)
        for extension, queue in (("rna-in", "q-rna"), ("busy-in", "q-busy"), ("answer-in", "q-answer")):
            self.assertIn(f"exten => {extension},1,Answer()", extensions)
            self.assertIn(f"Queue({queue})", extensions)
        pjsip = files["pjsip.conf"]
        self.assertIn("bind = 127.0.0.1:5060", pjsip, "SIP stays inside the container")
        for member in ("agent1", "agent2", "agent3"):
            self.assertIn(f"contact = sip:{member}@127.0.0.1:5060", pjsip)
        self.assertNotIn("0.0.0.0", pjsip)

    def test_run_ShouldPlaceEachKindInItsQueue_AndRecordTheMissedAndConnectedMembers(self):
        steps, received = self._run(2)

        self.assertEqual(["rna-01", "rna-02", "busy-01", "busy-02", "answer-01", "answer-02", "done"], steps)
        originates = [(h["Channel"], h["CallerID"]) for h in received if h.get("Action") == "Originate"]
        self.assertEqual([("PJSIP/rna-in@loop", "Capture <9301>"), ("PJSIP/rna-in@loop", "Capture <9302>"),
                          ("PJSIP/busy-in@loop", "Capture <9401>"), ("PJSIP/busy-in@loop", "Capture <9402>"),
                          ("PJSIP/answer-in@loop", "Capture <9501>"), ("PJSIP/answer-in@loop", "Capture <9502>")],
                         originates)
        self.assertEqual([("rna-01", "9301", "1", "PJSIP/agent1", "1", "PJSIP/agent2", ""),
                          ("rna-02", "9302", "1", "PJSIP/agent1", "1", "PJSIP/agent2", ""),
                          ("busy-01", "9401", "1", "PJSIP/agent3", "1", "PJSIP/agent2", ""),
                          ("busy-02", "9402", "1", "PJSIP/agent3", "1", "PJSIP/agent2", ""),
                          ("answer-01", "9501", "0", "", "1", "PJSIP/agent2", ""),
                          ("answer-02", "9502", "0", "", "1", "PJSIP/agent2", "")], self._outcomes(received))

    def test_run_ShouldRecordATimeoutAndGoOn_WhenNoMemberIsConnected(self):
        steps, received = self._run(1, never_connect=True)

        self.assertEqual(["rna-01", "busy-01", "answer-01", "done"], steps)
        outcomes = self._outcomes(received)
        self.assertEqual(["0", "0", "0"], [o[4] for o in outcomes])
        self.assertTrue(all(o[6].startswith("error: timed out") for o in outcomes), outcomes)
        self.assertEqual(["1", "1", "0"], [o[2] for o in outcomes], "the missed ring is still indexed")


# --- confbridge-exits-under-load ---------------------------------------------------------------


class _LockedPbx(_FakePbx):
    """A fake whose socket another fake may also write to (the load session's broadcasts)."""

    def __init__(self, sock, script):
        super().__init__(sock, script)
        self.lock = threading.Lock()

    def _send(self, data):
        with self.lock:
            super()._send(data)

    def broadcast(self, data):
        try:
            self._send(data)
        except OSError:
            pass


def _ok(message="Success"):
    return b"Response: Success\r\nActionID: {id}\r\nMessage: " + message.encode() + b"\r\n\r\n"


def _list(rows, item):
    """A list answer: start, one `item` event per row (each a list of header pairs), Complete."""
    return ([b"Response: Success\r\nActionID: {id}\r\nEventList: start\r\n\r\n"]
            + [frame(("Event", item), ("ActionID", "{id}"), *row) for row in rows]
            + [frame(("Event", item + "Complete"), ("ActionID", "{id}"), ("EventList", "Complete"),
                     ("ListItems", str(len(rows))))])


def _user_event(h):
    """Echo a UserEvent with every header it was sent with, stamped like timestampevents = yes."""
    pairs = [("Event", "UserEvent")] + [(k, v) for k, v in h.items() if k not in ("Action", "ActionID")]
    return frame(*pairs, ("ActionID", h["ActionID"]), ("Timestamp", f"{__import__('time').time():.6f}"))


class _LoadPbx:
    """The load session's side: counts four channels per unit originated since the last clear,
    answers the clear's Hangup, and relays each CaptureLoad UserEvent to the measured session."""

    def __init__(self):
        self.units = 0
        self.measured = None  # the measured session's fake, set once both exist
        self.events = []

    def script(self):
        def originate(h):
            self.units += 1
            return [_ok("Originate successfully queued")]

        def hangup(h):
            self.units = 0
            return [_ok("Channel Hungup")]

        def channels(h):
            rows = [[("Channel", f"Local/load-conf@capture-load-{n:08d};2"), ("Uniqueid", f"9.{n}")]
                    for n in range(self.units * 4)]
            return _list(rows, "CoreShowChannel")

        def user_event(h):
            self.events.append(h)
            if self.measured is not None:
                self.measured.broadcast(_user_event(h).replace(b"{id}", h["ActionID"].encode()))
            return [_ok("Event Sent")]

        return {"Login": [_ok("Authentication accepted")], "Originate": originate, "Hangup": hangup,
                "CoreShowChannels": channels, "UserEvent": user_event,
                "Logoff": [b"Response: Goodbye\r\nActionID: {id}\r\n\r\n"]}


class _RoomPbx:
    """The measured session's side of confbridge-exits-under-load. Each normal exit joins, leaves and
    hangs up -- except caller 8001, whose Hangup comes before its leave (the race the hangup list is
    for); a relief target is hung up with Cause 0 and no leave, and its successor is listed from then
    on; the successor and the anchor leave when hung up. The listing always shows one load channel,
    which the measurement must never hang up."""

    LOAD_ROW = [("Channel", "Local/load-conf@capture-load-00000001;2"), ("Uniqueid", "9.1")]

    def __init__(self):
        self.n = 0
        self.live = {}  # channel -> uniqueid, the measurement's channels in the room
        self.target = None
        self.hangups = []

    def _next(self, stem):
        self.n += 1
        return f"{stem}-{self.n:08d}", f"1700000000.{self.n}"

    def _join(self, channel, uid, cid):
        self.live[channel] = uid
        return _ev(("Event", "ConfbridgeJoin"), ("Conference", "capture-room"), ("Channel", channel),
                   ("Uniqueid", uid), ("CallerIDNum", cid))

    def _leave(self, channel, uid):
        return _ev(("Event", "ConfbridgeLeave"), ("Conference", "capture-room"), ("Channel", channel),
                   ("Uniqueid", uid))

    def _hangup(self, channel, uid, cause="16"):
        self.live.pop(channel, None)
        return _ev(("Event", "Hangup"), ("Channel", channel), ("Uniqueid", uid), ("Cause", cause))

    def originate(self, h):
        cid = h["CallerID"].split("<")[1].rstrip(">")
        queued = [_ok("Originate successfully queued")]
        channel = h["Channel"]
        if channel == "Local/conf-seq@capture/n":
            ch, uid = self._next("Local/conf-seq@capture")
            if cid == "8001":
                return queued + [self._join(ch, uid, cid), self._hangup(ch, uid), self._leave(ch, uid)]
            return queued + [self._join(ch, uid, cid), self._leave(ch, uid), self._hangup(ch, uid)]
        if channel in ("PJSIP/conf-in@loop", "Local/conf-anchor@capture/n"):
            ch, uid = self._next("PJSIP/loop" if channel.startswith("PJSIP") else "Local/conf-anchor@capture")
            if channel.startswith("PJSIP"):
                self.target = (ch, uid)
            return queued + [self._join(ch, uid, cid)]
        if channel == "Local/replace@capture/n":
            ch, uid = self.target
            succ, succ_uid = self._next("PJSIP/loop")
            self.live[succ] = succ_uid
            return queued + [self._hangup(ch, uid, cause="0")]
        return [b"Response: Error\r\nActionID: {id}\r\nMessage: Originate failed\r\n\r\n"]

    def hangup(self, h):
        self.hangups.append(h["Channel"])
        uid = self.live.get(h["Channel"])
        if uid is None:
            return [_ok("Channel Hungup")]
        return [_ok("Channel Hungup"), self._leave(h["Channel"], uid), self._hangup(h["Channel"], uid)]

    def script(self):
        def conf_list(h):
            return _list([[("Conference", "capture-room"), ("Channel", c), ("Uniqueid", u)]
                          for c, u in self.live.items()], "ConfbridgeList")

        def channels(h):
            rows = [self.LOAD_ROW] + [[("Channel", c), ("Uniqueid", u)] for c, u in self.live.items()]
            return _list(rows, "CoreShowChannel")

        return {
            "Login": [_ok("Authentication accepted")],
            "UserEvent": lambda h: [_ok("Event Sent"), _user_event(h)],
            "Originate": self.originate,
            "Getvar": lambda h: [b"Response: Success\r\nActionID: {id}\r\nValue: v-"
                                 + h["Variable"].encode() + b"\r\n\r\n"],
            "ConfbridgeList": conf_list,
            "ConfbridgeMute": [_ok("User muted")],
            "CoreShowChannels": channels,
            "Hangup": self.hangup,
            "Logoff": [b"Response: Goodbye\r\nActionID: {id}\r\n\r\n"],
        }


class _Sessions:
    """A measured fake and a load fake on two socket pairs, wired as capture() wires the real PBX."""

    def __init__(self):
        self.room, self.load = _RoomPbx(), _LoadPbx()
        self.ours, theirs = socket.socketpair()
        self.pbx = _LockedPbx(theirs, self.room.script())
        self.load.measured = self.pbx
        self.load_pbx = None
        self._socks = [self.ours, theirs]
        self.pbx.start()
        self.logins = []

    def open_session(self, events="off"):
        ours, theirs = socket.socketpair()
        self._socks += [ours, theirs]
        self.load_pbx = _LockedPbx(theirs, self.load.script())
        self.load_pbx.start()
        tap = cap.Tap(ours)
        tap.pump = lambda seconds: cap.Tap.pump(tap, min(seconds, 0.02))
        tap.login("capture", "secret", events)
        self.logins.append(events)
        return tap

    def close(self):
        for s in self._socks:
            s.close()
        self.pbx.join(2)
        if self.load_pbx:
            self.load_pbx.join(2)


_FAST = {"exits": 3, "exit_rate": 100, "reliefs": 1, "relief_window": 0.05, "relief_poll_after": 0.05,
         "load_sample_every": 0.05, "load_ramp_rate": 1000, "ramp_timeout": 10, "join_timeout": 2}


class LoadPlanTests(unittest.TestCase):
    def test_plan_ShouldHoldTheLevelInFourChannelUnits_HalfOfThemCallsSpreadAmongTheRooms(self):
        plan = cap.LoadPlan(40, now=0.0, rooms=3)

        self.assertEqual(10, plan.units)
        kinds = [plan.kind(j) for j in range(plan.units)]
        self.assertEqual(5, kinds.count("call"))
        self.assertNotEqual(["call"] * 5, kinds[:5], "calls are interleaved, not bunched")
        rooms = [plan.room(j) for j in range(plan.units) if plan.kind(j) == "room"]
        self.assertEqual(["load-room-01", "load-room-02", "load-room-03", "load-room-01", "load-room-02"], rooms)

    def test_due_ShouldRampAtTheRampRate_AndSpreadTheFirstLifetimes(self):
        plan = cap.LoadPlan(40, now=0.0, lifetime=20, ramp_rate=10.0)

        first = plan.due(0.0)
        rest = plan.due(0.95)

        self.assertEqual([0], [s.slot for s in first])
        self.assertEqual(list(range(1, 10)), [s.slot for s in rest])
        self.assertEqual([2, 4, 6, 8, 10, 12, 14, 16, 18, 20], [s.lifetime for s in first + rest],
                         "expiries spread over the lifetime, so the level does not saw")
        self.assertEqual([], plan.due(1.5))

    def test_due_ShouldRestartEachSlotWhenItsUnitExpires_WithTheFullLifetime(self):
        plan = cap.LoadPlan(8, now=0.0, lifetime=20, ramp_rate=100.0)
        plan.due(1.0)  # slot 0 lives 10 s, slot 1 lives 20 s

        again = plan.due(11.0)

        self.assertEqual([(0, 20)], [(s.slot, s.lifetime) for s in again])
        self.assertEqual([(1, 20)], [(s.slot, s.lifetime) for s in plan.due(21.0)])

    def test_due_ShouldStartNothingAtTheCeiling_AndRetryOnTheNextCall(self):
        plan = cap.LoadPlan(40, now=0.0, ramp_rate=1000.0)

        self.assertEqual([], plan.due(1.0, live=plan.ceiling))
        self.assertEqual(10, len(plan.due(1.0, live=plan.ceiling - 1)))

    def test_plan_ShouldStartNothing_AtTheControlLevel(self):
        plan = cap.LoadPlan(0, now=0.0)

        self.assertEqual(0, plan.units)
        self.assertEqual([], plan.due(100.0))

    def test_plan_ShouldRefuse_ImpossibleParameters(self):
        for kwargs in ({"rooms": 0}, {"lifetime": 0}, {"call_share": 1.5}, {"ramp_rate": 0}):
            with self.subTest(kwargs=kwargs), self.assertRaises(cap.CaptureError):
                cap.LoadPlan(40, now=0.0, **kwargs)

    def test_start_ShouldOriginateARoomParticipantWithATone_OrALoopbackCallEchoingOne(self):
        room = dict(cap.LoadStart(3, "room", "load-room-02", 12).fields())
        call = cap.LoadStart(4, "call", "", 15).fields()

        self.assertEqual("Local/load-conf@capture-load/n", room["Channel"])
        self.assertEqual("load-dialtone", room["Exten"])
        self.assertIn(("Variable", "__LOADT=12"), cap.LoadStart(3, "room", "load-room-02", 12).fields())
        self.assertIn(("Variable", "__LOADROOM=load-room-02"), cap.LoadStart(3, "room", "load-room-02", 12).fields())
        self.assertIn(("Channel", "PJSIP/load-call-15@loadout"), call)
        self.assertIn(("Application", "Echo"), call)
        self.assertIn(("Account", "load"), call)
        self.assertIn(("Async", "true"), call)

    def test_isLoadChannel_ShouldTellTheLoadFromTheMeasurement_ByNameOrAccountCode(self):
        self.assertTrue(cap.is_load_channel({"Channel": "Local/load-tone@capture-load-00000002;1"}))
        self.assertTrue(cap.is_load_channel({"Channel": "PJSIP/loadout-0000000a"}))
        self.assertTrue(cap.is_load_channel({"Channel": "PJSIP/loop-0000000b", "AccountCode": "load"}))
        self.assertFalse(cap.is_load_channel({"Channel": "PJSIP/loop-0000000c", "AccountCode": ""}))
        self.assertFalse(cap.is_load_channel({"Channel": "Local/conf-seq@capture-00000003;2"}))


class LoadMeasureTests(unittest.TestCase):
    def test_levelHolds_ShouldAcceptOnlyCountsWithinTenPercent_AndOnlyZeroAtTheControl(self):
        self.assertTrue(cap.level_holds([140, 150, 165], 150))
        self.assertFalse(cap.level_holds([140, 134], 150))
        self.assertFalse(cap.level_holds([150, 166], 150))
        self.assertTrue(cap.level_holds([0, 0], 0))
        self.assertFalse(cap.level_holds([0, 4], 0))
        self.assertFalse(cap.level_holds([], 150), "no sample is no evidence the level held")

    def test_parseLevels_ShouldKeepTheLadderInOrder_AndRefuseAnythingButWholeCounts(self):
        self.assertEqual([0, 150, 400], cap.parse_levels("0, 150,400"))
        for bad in ("", "0,high", "-5"):
            with self.subTest(bad=bad), self.assertRaises(cap.CaptureError):
                cap.parse_levels(bad)

    def test_parseCpuStat_ShouldReadUsageUsec_OrNoneWhenTheFileHasNone(self):
        self.assertEqual(123456, cap.parse_cpu_stat("usage_usec 123456\nuser_usec 100000\n"))
        self.assertIsNone(cap.parse_cpu_stat("OCI runtime exec failed"))
        self.assertIsNone(cap.parse_cpu_stat(None))

    def test_levelSummary_ShouldReportTheRangeAndTheCoresUsedBetweenTheTimedSamples(self):
        samples = [{"load": 148, "cpu_usec": 1_000_000, "wall": 100.0},
                   {"load": 152, "cpu_usec": None, "wall": 102.0},
                   {"load": 151, "cpu_usec": 9_000_000, "wall": 104.0}]

        summary = cap.level_summary(samples, 150)

        self.assertEqual({"samples": 3, "load_min": 148, "load_max": 152, "held": True, "cpu_cores": 2.0},
                         summary)
        self.assertIsNone(cap.level_summary(samples[:1], 150)["cpu_cores"])

    def test_readerMeter_ShouldMarkTheWindowNotValid_WhenAFrameIsParsedLateAfterItsTimestamp(self):
        now = [1000.0]
        meter = cap.ReaderMeter(limit=0.5, wall=lambda: now[0])
        meter.reset(bytes_in=0)
        meter.observe({"Event": "Newchannel", "Timestamp": "999.9"})
        now[0] = 1002.0
        meter.observe({"Event": "Hangup", "Timestamp": "1000.5"})

        report = meter.report(bytes_in=4000)

        self.assertFalse(report["valid"])
        self.assertEqual(1, report["late_frames"])
        self.assertEqual(1500.0, report["max_lag_ms"])
        self.assertEqual(2000.0, report["bytes_per_second"])
        self.assertEqual(1.0, report["events_per_second"])

    def test_readerMeter_ShouldBeValid_WhenEveryFrameKeepsUp_AndNotWhenNoFrameIsStamped(self):
        now = [10.0]
        meter = cap.ReaderMeter(limit=0.5, wall=lambda: now[0])
        meter.observe({"Event": "Newchannel"})
        self.assertFalse(meter.report(0)["valid"], "without timestampevents nothing says the reader kept up")
        meter.observe({"Event": "Hangup", "Timestamp": "9.8"})
        self.assertTrue(meter.report(0)["valid"])


class HangupListerTests(unittest.TestCase):
    @staticmethod
    def _f(event, uid, room="capture-room"):
        return {"Event": event, "Uniqueid": uid, "Conference": room}

    def test_observe_ShouldAskTheRoomsListOnce_WhenAParticipantHangsUpBeforeItsLeave(self):
        lister = cap.HangupLister("capture-room")
        lister.observe(self._f("ConfbridgeJoin", "1.1"))

        asked = lister.observe(self._f("Hangup", "1.1"))
        again = lister.observe(self._f("Hangup", "1.1"))

        self.assertEqual(("hl-1.1", [("Action", "ConfbridgeList"), ("Conference", "capture-room")]), asked)
        self.assertIsNone(again)
        self.assertEqual(["1.1"], lister.asked)

    def test_observe_ShouldNotAsk_WhenTheLeaveCameFirst(self):
        lister = cap.HangupLister("capture-room")
        lister.observe(self._f("ConfbridgeJoin", "1.2"))
        lister.observe(self._f("ConfbridgeLeave", "1.2"))

        self.assertIsNone(lister.observe(self._f("Hangup", "1.2")))
        self.assertEqual(set(), lister.pending())

    def test_observe_ShouldIgnoreOtherRoomsAndChannelsThatNeverJoined(self):
        lister = cap.HangupLister("capture-room")
        lister.observe(self._f("ConfbridgeJoin", "1.3", room="load-room-01"))

        self.assertIsNone(lister.observe(self._f("Hangup", "1.3")))
        self.assertIsNone(lister.observe(self._f("Hangup", "1.4")))
        self.assertIsNone(lister.observe(self._f("Hangup", "<unknown>")))
        self.assertEqual([], lister.asked)

    def test_pending_ShouldHoldTheParticipantsThatJoinedAndHaveNotHungUp(self):
        lister = cap.HangupLister("capture-room")
        for uid in ("1.5", "1.6"):
            lister.observe(self._f("ConfbridgeJoin", uid))
        lister.observe(self._f("Hangup", "1.5"))

        self.assertEqual({"1.6"}, lister.pending())


class LoadTapTests(unittest.TestCase):
    def test_tap_ShouldGiveObserversEachFrameInOrder_AndSendTheActionIdItIsGiven(self):
        script = {"Login": [_ok("Authentication accepted")],
                  "ConfbridgeList": _list([[("Channel", "PJSIP/loop-1")]], "ConfbridgeList")}
        ours, theirs = socket.socketpair()
        pbx = _FakePbx(theirs, script)
        pbx.start()
        seen = []
        try:
            tap = cap.Tap(ours)
            tap.observers.append(lambda f: seen.append(f.get("Event") or f.get("Response")))
            tap.login("capture", "secret", "off")
            mark = len(tap.frames)
            tap.send([("Action", "ConfbridgeList")], action_id="hl-7.1")
            end = tap.wait_for(lambda f: f.get("EventList") == "Complete", 2, mark)
        finally:
            ours.close()
            theirs.close()
            pbx.join(2)

        self.assertEqual("off", pbx.received[0]["Events"])
        self.assertEqual("hl-7.1", pbx.received[1]["ActionID"])
        self.assertEqual("hl-7.1", end["ActionID"])
        self.assertEqual(["Success", "Success", "ConfbridgeList", "ConfbridgeListComplete"], seen)

    def test_tap_ShouldStreamEveryByteToTheSink_AndKeepNoTranscript(self):
        ours, theirs = socket.socketpair()
        pbx = _FakePbx(theirs, {"Login": [_ok("Authentication accepted")]})
        pbx.start()
        sink = io.BytesIO()
        try:
            tap = cap.Tap(ours, sink=sink)
            tap.login("capture", "secret")
        finally:
            ours.close()
            theirs.close()
            pbx.join(2)

        self.assertEqual(bytes(pbx.sent), sink.getvalue())
        self.assertEqual(len(pbx.sent), tap.bytes_in)
        self.assertEqual(b"", bytes(tap.transcript))


class LoadFilesTests(unittest.TestCase):
    def test_managerConf_ShouldBeUnchangedForTheOtherWorkloads_AndRaiseWritetimeoutUnderLoad(self):
        plain = cap.manager_conf("s3cret")
        loaded = cap.manager_conf("s3cret", True, cap.WORKLOADS["confbridge-exits-under-load"]["manager_user"])

        self.assertIn("write = all\ndeny = 0.0.0.0/0.0.0.0\n", plain)
        self.assertIn("timestampevents = yes\n", plain)
        for name, workload in cap.WORKLOADS.items():
            if name != "confbridge-exits-under-load":
                self.assertNotIn("manager_user", workload)
        self.assertIn("write = all\nwritetimeout = 10000\ndeny = 0.0.0.0/0.0.0.0\n", loaded)

    def test_files_ShouldAddTheLoadDialplanAndEndpoint_AndKeepTheRenamedWorkloadsShapes(self):
        files = cap.WORKLOADS["confbridge-exits-under-load"]["files"]
        ext, pjsip = files["extensions.conf"], files["pjsip.conf"]

        for context in ("[capture]\n", "[capture-sip]\n", "[capture-load]\n", "[capture-replaces]\n"):
            self.assertEqual(1, ext.count(context), context)
        self.assertIn("exten => conf-anchor,1,Answer()", ext)
        self.assertIn("exten => _load-call-X.,1,Answer()\n same => n,Set(CHANNEL(accountcode)=load)", ext)
        self.assertIn("Set(TIMEOUT(absolute)=${EXTEN:10})", ext)
        self.assertIn("exten => load-conf,1,Answer()\n same => n,Set(TIMEOUT(absolute)=${LOADT})\n"
                      " same => n,ConfBridge(${LOADROOM})", ext)
        self.assertIn("Dial(Local/load-tone@capture-load/n)", ext)
        self.assertIn("exten => conf-seq,1,Answer()", ext)
        self.assertIn("exten => replace,1,Dial(PJSIP/replace-in@loop", ext)
        self.assertIn("[loadout]\ntype = endpoint\ntransport = transport-loopback", pjsip)
        self.assertNotIn("0.0.0.0", pjsip)


class LoadGeneratorTests(unittest.TestCase):
    def _run(self, levels, hold=0.4):
        sessions = _Sessions()
        try:
            tap = cap.Tap(sessions.ours)
            tap.login("capture", "secret")
            gen = cap.LoadGenerator(sessions.open_session,
                                    lambda level, now: cap.LoadPlan(level, now, ramp_rate=1000.0),
                                    read_cpu=lambda: 42, sample_every=0.05, tick=0.01, clear_timeout=1.0)
            gen.start()
            for level in levels:
                gen.set_level(level)
                tap.pump(hold)
            gen.stop(5)
            tap.pump(0.2)
        finally:
            sessions.close()
        return sessions, gen, tap

    def test_generator_ShouldHoldTheLevelOnAnEventsOffSession_AndAnnounceEachSampleToTheMeasuredOne(self):
        sessions, gen, tap = self._run([40])

        self.assertIsNone(gen.error)
        self.assertEqual(["off"], sessions.logins)
        self.assertEqual(10, gen.starts)
        originates = [h for h in sessions.load_pbx.received if h.get("Action") == "Originate"]
        self.assertEqual(5, sum(1 for h in originates if h["Channel"].startswith("PJSIP/load-call-")))
        loads = [f for f in tap.frames if f.get("UserEvent") == "CaptureLoad"]
        held = [f for f in loads if f["Target"] == "40" and f["LoadChannels"] == "40"]
        self.assertGreaterEqual(len(held), 2, "the samples reach the measured connection")
        self.assertEqual("42", held[0]["CpuUsec"])

    def test_generator_ShouldHangUpTheLoadBeforeANewLevel_AndWhenItStops(self):
        sessions, gen, tap = self._run([40, 0])

        hangups = [h["Channel"] for h in sessions.load_pbx.received if h.get("Action") == "Hangup"]
        self.assertEqual(["/^(Local\\/load-|PJSIP\\/loadout-)/"] * 3, hangups)
        self.assertEqual("Logoff", sessions.load_pbx.received[-1]["Action"])
        last = [f for f in tap.frames if f.get("UserEvent") == "CaptureLoad"][-1]
        self.assertEqual(("0", "0"), (last["Target"], last["LoadChannels"]))

    def test_generator_ShouldKeepTheError_WhenTheLoadSessionCannotOpen(self):
        def refused(events):
            raise cap.CaptureError("AMI login refused: no")

        gen = cap.LoadGenerator(refused, lambda level, now: None)
        gen.start()
        gen.stop(5)

        self.assertEqual("AMI login refused: no", gen.error)


class ConfbridgeExitsUnderLoadWorkloadTests(unittest.TestCase):
    _NAME = "confbridge-exits-under-load"

    def _run(self, **options):
        sessions = _Sessions()
        try:
            tap = cap.Tap(sessions.ours)
            tap.pump = lambda seconds: cap.Tap.pump(tap, min(seconds, 0.02))
            tap.options = {**_FAST, **options}
            tap.open_session = sessions.open_session
            tap.read_cpu = lambda: 7
            tap.login("capture", "secret")
            steps = cap.WORKLOADS[self._NAME]["run"](tap)
            tap.logoff()
        finally:
            sessions.close()
        return steps, sessions

    @staticmethod
    def _notes(received, prefix=""):
        return {h["Step"]: h for h in received if h.get("Action") == "UserEvent"
                and h.get("UserEvent") == "CaptureOutcome" and h["Step"].startswith(prefix)}

    def test_run_ShouldWalkTheLadder_ExitsThenReliefsAloneAndAnchored_PerLevel(self):
        steps, _ = self._run(load_levels="0,40")

        top = [s for s in steps if not s.endswith(("-hungup", "-after-window", "-successor-hangup"))]
        expected = []
        for level in (0, 40):
            p = f"level-{level}"
            expected += [f"{p}-ramp", f"{p}-exits", f"{p}-relief-alone-01", f"{p}-anchor-join",
                         f"{p}-relief-anchored-01", f"{p}-anchor-hangup"]
        self.assertEqual(expected + ["load-stop", "done"], top)

    def test_run_ShouldAskTheListTheMomentAParticipantHangsUpBeforeItsLeave_AndOnlyThen(self):
        _, sessions = self._run(load_levels="0")

        lists = [h["ActionID"] for h in sessions.pbx.received
                 if h.get("Action") == "ConfbridgeList" and h["ActionID"].startswith("hl-")]
        reliefs = self._notes(sessions.pbx.received, "level-0-relief-")
        relieved = sorted(n["TargetId"] for n in reliefs.values())
        inverted = "1700000000.2"  # caller 8001, the second exit: its Hangup came before its leave
        self.assertEqual(sorted([f"hl-{inverted}"] + [f"hl-{uid}" for uid in relieved]), sorted(lists))
        self.assertTrue(all(n["HangupList"] == "yes" for n in reliefs.values()))
        self.assertTrue(all(n["Result"] == "ok" and n["LeavesOfTarget"] == "0" for n in reliefs.values()))

    def test_run_ShouldReportEachLevelsLoadCpuAndReader_AndTheControlAtZero(self):
        _, sessions = self._run(load_levels="0,40")

        notes = self._notes(sessions.pbx.received)
        zero, loaded = notes["level-0"], notes["level-40"]
        self.assertEqual(("yes", "yes", "0", "0"), (zero["RampHeld"], zero["Held"], zero["LoadMin"], zero["LoadMax"]))
        self.assertEqual(("yes", "yes", "40", "40"),
                         (loaded["RampHeld"], loaded["Held"], loaded["LoadMin"], loaded["LoadMax"]))
        self.assertEqual("yes", loaded["ReaderValid"])
        self.assertEqual("0", loaded["LateFrames"])
        self.assertEqual("3", loaded["HangupLists"], "one inverted exit and two reliefs at this level")
        self.assertEqual("0.0", loaded["CpuCores"], "the fake's CPU time never moves")
        self.assertEqual("", notes["load-stop"]["LoadError"])

    def test_run_ShouldOriginateTheExitsAtTheirRate_AndNeverHangUpALoadChannel(self):
        _, sessions = self._run(load_levels="0", exits=5)

        exits = [h["CallerID"] for h in sessions.pbx.received
                 if h.get("Action") == "Originate" and h["Channel"] == "Local/conf-seq@capture/n"]
        self.assertEqual([f"Capture <{8000 + i}>" for i in range(5)], exits)
        self.assertNotIn(_RoomPbx.LOAD_ROW[0][1], sessions.room.hangups)
        self.assertTrue(sessions.room.hangups, "the measurement's own channels are hung up")

    def test_run_ShouldRefuse_WithoutASecondSession(self):
        ours, theirs = socket.socketpair()
        try:
            tap = cap.Tap.__new__(cap.Tap)
            tap.open_session, tap.options = None, {}
            with self.assertRaises(cap.CaptureError):
                cap.WORKLOADS[self._NAME]["run"](tap)
        finally:
            ours.close()
            theirs.close()

    def test_cli_ShouldRefuseALadderThatIsNotWholeCounts_BeforeStartingAnything(self):
        err = io.StringIO()
        with contextlib.redirect_stderr(err):
            rc = cap.main(["capture", "--image", f"vendor/asterisk:22.10.1@{_DIGEST}",
                           "--workload", self._NAME, "--load-levels", "0,high", "--tap-only"])

        self.assertEqual(1, rc)
        self.assertIn("load level", err.getvalue())


if __name__ == "__main__":
    unittest.main()

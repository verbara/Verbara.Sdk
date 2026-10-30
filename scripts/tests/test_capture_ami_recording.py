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
            "SendFlash": lambda h: [self.ok() if h.get("Channel") == self.FLASH_OUT else
                                    b"Response: Error\r\nActionID: {id}\r\nMessage: No such channel\r\n\r\n"],
            "Logoff": [b"Response: Goodbye\r\nActionID: {id}\r\n\r\n"],
        }

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
                                   ("ParkingSpace", self.SPACE), ("ParkingLot", "default"))]
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
                *_hangups(self.FLASH_IN, self.FLASH_OUT)]
        return [b"Response: Error\r\nActionID: {id}\r\nMessage: Originate failed\r\n\r\n"]


class DialplanAndParkingWorkloadTests(unittest.TestCase):
    _NAME = "dialplan-and-parking"

    def _run(self):
        pbx_state = _ParkingPbx()
        ours, theirs = socket.socketpair()
        pbx = _FakePbx(theirs, pbx_state.script())
        pbx.start()
        try:
            tap = cap.Tap(ours)
            tap.pump = lambda seconds: cap.Tap.pump(tap, min(seconds, 0.02))
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
                          "send-flash-help", "send-flash", "parked-empty", "done"], steps)

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


if __name__ == "__main__":
    unittest.main()

"""Unit tests for scan-ami-capture.py (who left a queue or a ConfBridge in a call's place).

Takes are built frame by frame in the capture's own shape (banner, CaptureMarker steps, a
`-waiting` marker before the listing sent after a trigger); one test reads the committed
recordings. Stdlib unittest only. The module name carries hyphens, so it is loaded by path.
"""
import contextlib
import importlib.util
import io
import os
import tempfile
import unittest

_HERE = os.path.dirname(os.path.abspath(__file__))
_spec = importlib.util.spec_from_file_location("scan_ami_capture", os.path.join(_HERE, os.pardir, "scan-ami-capture.py"))
scan = importlib.util.module_from_spec(_spec)
_spec.loader.exec_module(scan)

_RECORDINGS = os.path.join(_HERE, os.pardir, os.pardir, "Tests", "Verbara.Sdk.Sessions.FunctionalTests",
                           "Recordings", "asterisk-ami")
_BANNER = b"Asterisk Call Manager/11.0.0\r\n"


def f(*pairs):
    return "".join(f"{k}: {v}\r\n" for k, v in pairs).encode("latin-1") + b"\r\n"


def marker(step):
    return f(("Event", "UserEvent"), ("UserEvent", "CaptureMarker"), ("Step", step), ("ActionID", "cap-1"))


def outcome(step, trigger):
    return f(("Event", "UserEvent"), ("UserEvent", "CaptureOutcome"), ("Step", step), ("Trigger", trigger),
             ("ActionID", "cap-2"))


def ev(event, channel, uid, t, *extra):
    return f(("Event", event), ("Timestamp", f"{t:.6f}"), ("Channel", channel), ("Uniqueid", uid), *extra)


_JOIN = {"queue": ("QueueCallerJoin", ("Queue", "q")), "conference": ("ConfbridgeJoin", ("Conference", "room"))}
_LEAVE = {"queue": ("QueueCallerLeave", ("Queue", "q")), "conference": ("ConfbridgeLeave", ("Conference", "room"))}
_ROW = {"queue": ("QueueEntry", ("Queue", "q")), "conference": ("ConfbridgeList", ("Conference", "room"))}
_END = {"queue": f(("Event", "QueueStatusComplete"), ("ActionID", "cap-9"), ("EventList", "Complete")),
        "conference": f(("Event", "ConfbridgeListComplete"), ("ActionID", "cap-9"), ("EventList", "Complete"))}


def row(surface, channel, uid):
    event, key = _ROW[surface]
    return f(("Event", event), key, ("Channel", channel), ("Uniqueid", uid), ("ActionID", "cap-9"))


def call(surface, step, trigger, name, uid, t, *, listed=True, leave=None, hangup=True, between=b"",
         after_trigger=b""):
    """A step whose target joins, waits through a trigger and a listing, then leaves (or not)."""
    join_ev, key = _JOIN[surface]
    leave_ev, lkey = _LEAVE[surface]
    out = marker(step) + ev("Newchannel", name, uid, t) + ev(join_ev, name, uid, t + 0.1, key) + between
    out += marker(step + "-waiting") + after_trigger + (row(surface, name, uid) if listed else b"") + _END[surface]
    if leave is not False:
        lname, luid = leave or (name, uid)
        out += ev(leave_ev, lname, luid, t + 1, lkey)
    if hangup:
        out += ev("Hangup", name, uid, t + 1.2)
    return out + outcome(step, trigger)


def take(*parts):
    return _BANNER + b"".join(parts)


def labels(data):
    return [(r["step"], r["surface"], r["label"]) for r in scan.scan_take(data)["targets"]]


def target(data, step):
    return next(r for r in scan.scan_take(data)["targets"] if r["step"] == step)


class LabelTests(unittest.TestCase):
    def test_classify_ShouldLabelNormal_WhenTheLeaveCarriesTheJoinsNameAndUniqueid(self):
        data = take(call("queue", "negative-queue", "none", "Local/q-1;2", "1.1", 10))

        rec = target(data, "negative-queue")
        self.assertEqual("normal", rec["label"])
        self.assertTrue(rec["leave_before_hangup"])
        self.assertEqual(["Local/q-1;2"], rec["names"])

    def test_classify_ShouldLabelRenamedLeaveNewName_WhenARenameFrameNamesTheJoinsUniqueid(self):
        rename = f(("Event", "Rename"), ("Channel", "Local/q-1;2"), ("Newname", "Local/q-1;2<M>"), ("Uniqueid", "1.1"))
        data = take(call("queue", "t-queue", "x", "Local/q-1;2", "1.1", 10, between=rename,
                         leave=("Local/q-1;2<M>", "1.1")))

        rec = target(data, "t-queue")
        self.assertEqual("renamed-leave-new-name", rec["label"])
        self.assertEqual(1, rec["rename_frames"])
        self.assertEqual(["Local/q-1;2<M>"], rec["rename_newnames"])
        self.assertTrue(rec["rows_disagree_with_leave"], "the row while waiting still had the old name")

    def test_classify_ShouldLabelEachRenameShape_WithOrWithoutARenameFrame(self):
        seen = ev("Newstate", "PJSIP/b-2", "1.1", 10.5)
        cases = {
            "renamed-leave-new-name-no-rename": dict(between=seen, leave=("PJSIP/b-2", "1.1")),
            "renamed-leave-old-name": dict(between=seen),
            "renamed-leave-other-uniqueid": dict(between=seen, leave=("PJSIP/b-2", "1.7")),
            "unclassified": dict(between=seen + ev("Newstate", "PJSIP/c-3", "1.1", 10.6), leave=("PJSIP/b-2", "1.1")),
        }
        for label, kw in cases.items():
            with self.subTest(label):
                data = take(call("conference", "t-conference", "x", "PJSIP/a-1", "1.1", 10, **kw))
                self.assertEqual(label, target(data, "t-conference")["label"])

    def test_classify_ShouldLabelRelieved_WithTheFactsEachReadingNeeds(self):
        # the Replaces shape: the row and the leave name a new pair; the old one hangs up first
        data = take(call("queue", "replaces-queue", "replaces", "PJSIP/loop-1", "1.61", 10, listed=False,
                         after_trigger=row("queue", "PJSIP/loop-3", "1.65"), leave=("PJSIP/loop-3", "1.65"),
                         between=ev("Hangup", "PJSIP/loop-1", "1.61", 10.4), hangup=False))

        rec = target(data, "replaces-queue")
        self.assertEqual("relieved", rec["label"])
        self.assertEqual([["PJSIP/loop-3", "1.65"]], rec["other_pairs"])
        self.assertTrue(rec["hangup_before_other_leave"])
        self.assertEqual([], rec["linking_frames"], "the capture's own outcome line is not the wire")
        self.assertFalse(rec["other_keeps_name"])

    def test_classify_ShouldReportALinkingFrameAndAKeptName_WhenTheWireJoinsTheTwoIds(self):
        link = f(("Event", "AttendedTransfer"), ("OrigTransfererUniqueid", "1.61"), ("TransfereeUniqueid", "1.65"))
        data = take(call("conference", "refer-conference", "refer", "PJSIP/loop-1", "1.61", 10, listed=False,
                         after_trigger=link + row("conference", "PJSIP/loop-1", "1.65"),
                         leave=("PJSIP/loop-1", "1.65")))

        rec = target(data, "refer-conference")
        self.assertEqual("relieved", rec["label"])
        self.assertEqual(1, len(rec["linking_frames"]))
        self.assertTrue(rec["other_keeps_name"])
        self.assertFalse(rec["hangup_before_other_leave"], "its Hangup comes after the other pair's leave")

    def test_classify_ShouldLabelLeftUnannounced_WithTheIntervalAndTheBridgeEnter(self):
        enter = ev("BridgeEnter", "Local/q-8;2", "1.19", 10.3)
        data = take(call("queue", "bridge-queue", "bridge", "Local/q-8;2", "1.19", 10, listed=False, leave=False,
                         between=enter))

        rec = target(data, "bridge-queue")
        self.assertEqual("left-unannounced", rec["label"])
        self.assertTrue(rec["bridge_enter_before_hangup"])
        self.assertIsNotNone(rec["trigger_to_hangup_s"])

    def test_classify_ShouldLeaveUnclassified_WhenNoLeaveAndStillListed(self):
        data = take(call("queue", "t-queue", "x", "Local/q;2", "1.1", 10, listed=True, leave=False))

        self.assertEqual("unclassified", target(data, "t-queue")["label"])

    def test_classify_ShouldIgnoreSentinelUniqueids(self):
        sentinel = b"".join(f(("Event", "OriginateResponse"), ("Response", "Failure"), ("Channel", c),
                              ("Uniqueid", u)) for c, u in (("Local/a@x/n", "<unknown>"), ("Local/b@x/n", "<unknown>"),
                                                            ("Local/c;1", ""), ("Local/d;1", "")))
        data = take(call("queue", "negative-queue", "none", "Local/q;2", "1.1", 10), sentinel)

        self.assertEqual([], scan.scan_take(data)["renamed_uniqueids"])

    def test_classify_ShouldReadTheFirstJoinPerUniqueid_AndSkipListRows(self):
        join_row = f(("Event", "QueueCallerJoin"), ("Queue", "q"), ("Channel", "Local/q;2"), ("Uniqueid", "1.1"),
                     ("ActionID", "cap-3"))
        data = take(call("queue", "negative-queue", "none", "Local/q;2", "1.1", 10, between=join_row))

        self.assertEqual([("negative-queue", "queue", "normal")], labels(data))


class TakeChecksTests(unittest.TestCase):
    def _pickup(self, picker="1.17", target="1.14", enter=True, hang=True):
        out = marker("positive-pickupchan")
        out += f(("Event", "Pickup"), ("Channel", "Local/pick;2"), ("Uniqueid", picker),
                 ("TargetChannel", "Local/ring;1"), ("TargetUniqueid", target))
        out += ev("BridgeEnter", "Local/pick;2", picker, 1) if enter else b""
        out += ev("Hangup", "Local/ring;1", target, 2) if hang else b""
        return out + outcome("positive-pickupchan", "pickupchan")

    def test_identityChange_ShouldBeGreen_OnlyWithPickupHangupAndBridgeEnter(self):
        self.assertTrue(scan.scan_take(take(self._pickup()))["identity_change"]["green"])
        for kw in (dict(enter=False), dict(hang=False), dict(picker="1.14"), dict(target="<unknown>")):
            with self.subTest(kw):
                self.assertFalse(scan.scan_take(take(self._pickup(**kw)))["identity_change"]["green"])

    def test_complete_ShouldNameATargetWithoutNewchannelOrHangup(self):
        data = take(call("queue", "a-queue", "x", "Local/q;2", "1.1", 10, hangup=False))

        result = scan.scan_take(data)["complete"]
        self.assertFalse(result["green"])
        self.assertEqual([{"uniqueid": "1.1", "newchannel": True, "hangup": False}], result["targets"])

    def test_executed_ShouldReadEachTriggersOwnSignal(self):
        ok_bridge = f(("Response", "Success"), ("ActionID", "cap-5"), ("Message", "Channels have been bridged"))
        ok_redirect = f(("Response", "Success"), ("ActionID", "cap-6"), ("Message", "Redirect successful"))
        status_ok = f(("Response", "Success"), ("ActionID", "cap-7"), ("Message", "Queue status will follow"))
        data = take(
            call("queue", "bridge-queue", "bridge", "L/a;2", "1.1", 10, between=ok_bridge),
            call("conference", "bridge-conference", "bridge", "L/b;2", "1.2", 20, between=status_ok),
            call("queue", "redirect-queue", "redirect", "L/c;2", "1.3", 30, between=ok_redirect),
            call("queue", "replaces-queue", "replaces", "P/l-1", "1.4", 40, listed=False, leave=False,
                 after_trigger=row("queue", "P/l-3", "1.5")),
            call("conference", "refer-conference", "refer", "P/l-6", "1.6", 50,
                 between=f(("Event", "AttendedTransfer"), ("Result", "Success"))),
            call("queue", "refer-queue", "refer", "P/l-8", "1.8", 55,
                 between=f(("Event", "AttendedTransfer"), ("Result", "Invalid"), ("DestType", "Fail"))),
            marker("local-optimization-control"), f(("Event", "LocalOptimizationBegin"), ("LocalOneUniqueid", "1.9")),
            outcome("local-optimization-control", "local-optimization"),
            call("queue", "ari-move-queue", "ari-move", "L/d;2", "1.10", 60),
        )

        got = {step: e["executed"] for step, e in scan.scan_take(data)["executed"].items()}
        self.assertEqual({"bridge-queue": True, "bridge-conference": False, "redirect-queue": True,
                          "replaces-queue": True, "refer-conference": True, "refer-queue": False,
                          "local-optimization-control": True, "ari-move-queue": None}, got)

    def test_reuse_ShouldPrintNotExercised_WithoutAReusedName_AndFindAnInvertedLeave(self):
        once = take(call("queue", "s", "x", "L/a;2", "1.1", 10), call("queue", "s2", "x", "L/b;2", "1.2", 20))
        self.assertEqual("not exercised", scan.scan_take(once)["reuse"]["queue"]["reading"])

        jq, lq = _JOIN["queue"], _LEAVE["queue"]
        inverted = take(ev(jq[0], "L/a;2", "1.1", 1, jq[1]), ev(jq[0], "L/a;2", "1.2", 2, jq[1]),
                        ev(lq[0], "L/a;2", "1.1", 3, lq[1]), ev(lq[0], "L/a;2", "1.2", 4, lq[1]))
        result = scan.scan_take(inverted)["reuse"]["queue"]
        self.assertEqual((1, 1, "inverted leave found"), (result["reused_names"], result["inverted"], result["reading"]))


def _control_take():
    return take(call("queue", "negative-queue", "none", "L/q-0;2", "1.1", 10),
                call("conference", "negative-conference", "none", "L/c-1;2", "1.3", 20),
                call("queue", "pickup-queue", "pickup", "L/q-2;2", "1.5", 30),
                call("conference", "pickup-conference", "pickup", "L/c-3;2", "1.7", 40))


class ControlTests(unittest.TestCase):
    def test_syntheticControls_ShouldFireEveryLabelAndFacet_OnBothSurfaces(self):
        result = scan.synthetic_controls(_control_take())

        missed = [(r["surface"], r["label"], r["got"]) for r in result["labels"] if not r["fired"]]
        self.assertEqual([], missed)
        self.assertEqual({(s, l) for s in scan.SURFACES for l in list(scan.LABELS[:-1]) + list(scan.FACETS)},
                         {(r["surface"], r["label"]) for r in result["labels"]})
        self.assertTrue(result["sentinel_ignored"])
        self.assertTrue(result["green"])

    def test_syntheticControls_ShouldMissLeftUnannounced_WhenNoTargetWasListedAfterATrigger(self):
        data = take(call("queue", "negative-queue", "none", "L/q;2", "1.1", 10, listed=False))

        result = {(r["surface"], r["label"]): r for r in scan.synthetic_controls(data)["labels"]}
        self.assertFalse(result[("queue", "left-unannounced")]["fired"])
        self.assertFalse(scan.synthetic_controls(data)["green"])

    def test_mutate_ShouldChangeOnlyTheCopy(self):
        data = _control_take()
        rec = target(data, "negative-queue")

        mutated = scan._mutate(data, rec, "relieved")
        self.assertNotEqual(data, mutated)
        self.assertEqual("normal", target(data, "negative-queue")["label"])

    def test_copyControl_ShouldFlagACopiedLeave_WithItsUniqueidOrItsChannelAltered(self):
        for surface in scan.SURFACES:
            with self.subTest(surface):
                result = scan.copy_control(_control_take(), surface)
                self.assertTrue(result["pass"], result)
                self.assertEqual({"uniqueid", "channel"}, set(result["cases"]))

    def test_copyControl_ShouldFail_WithoutALeaveToCopy(self):
        self.assertFalse(scan.copy_control(take(marker("x")), "conference")["pass"])


class RecordingTests(unittest.TestCase):
    def test_scan_ShouldReadEveryCommittedQueueJoin_AsANormalLeaveBeforeItsHangup(self):
        names = sorted(n for n in os.listdir(_RECORDINGS) if n.endswith(".raw")
                       and n.split("-asterisk-")[0] in ("call-shapes", "queue-exits", "queue-loop", "queue-reload",
                                                        "queue-shapes"))
        self.assertEqual(15, len(names))
        normal = before = 0
        others = []
        for name in names:
            with open(os.path.join(_RECORDINGS, name), "rb") as fh:
                result = scan.scan_take(fh.read())
            self.assertEqual([], result["renamed_uniqueids"], name)
            for r in result["targets"]:
                if r["surface"] != "queue":
                    continue
                if r["label"] == "normal":
                    normal += 1
                    before += 1 if r["leave_before_hangup"] else 0
                else:
                    others.append((name, r["label"]))
        self.assertEqual((93, 93, []), (normal, before, others))


class CliTests(unittest.TestCase):
    def test_main_ShouldPrintTheTableAndTheControls_AndExitZero(self):
        with tempfile.TemporaryDirectory() as tmp:
            path = os.path.join(tmp, "t-asterisk-22.11.0.tap.raw")
            with open(path, "wb") as fh:
                fh.write(_control_take())
            out = io.StringIO()
            with contextlib.redirect_stdout(out):
                self.assertEqual(0, scan.main([path]))
                self.assertEqual(0, scan.main(["--controls", path]))
                self.assertEqual(0, scan.main(["--json", tmp]))
        text = out.getvalue()
        self.assertIn("negative-queue queue normal", text)
        self.assertIn("(queue) reuse: reused_names 0 inverted 0: not exercised", text)
        self.assertIn("controls: PASS", text)
        self.assertIn('"label": "normal"', text)


if __name__ == "__main__":
    unittest.main()

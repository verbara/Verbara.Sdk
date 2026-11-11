#!/usr/bin/env python3
"""Read AMI recordings or capture taps and say, per call that joined a queue or a ConfBridge, who
left in its place and what the wire said about it. Read-only; stdlib only.

    python3 scripts/scan-ami-capture.py <tap-or-recording.raw | dir> [...]   # tables
    python3 scripts/scan-ami-capture.py --json <file.raw> [...]              # the same, as JSON
    python3 scripts/scan-ami-capture.py --controls <tap.raw>                 # synthetic controls

A **target** is the first join of a Uniqueid on a surface (queue: `QueueCallerJoin`, keyed by
`Queue`; conference: `ConfbridgeJoin`, keyed by `Conference`), in the step its capture marker
names (`UserEvent: CaptureMarker`; a `<step>-waiting` marker belongs to `<step>`, and marks the
part after the trigger). A file with no marker is one step. Sentinel Uniqueids (empty, `<unknown>`)
are never read.

Each target gets exactly one label:

  renamed-leave-new-name            its Uniqueid carried a second name; the leave has the newest
                                    name and the join's Uniqueid; a `Rename` frame names that id
  renamed-leave-new-name-no-rename  the same, with no `Rename` frame naming the join's Uniqueid
  renamed-leave-old-name            renamed; the leave has the join's name and Uniqueid
  renamed-leave-other-uniqueid      renamed; no leave for its Uniqueid, a leave on the surface in
                                    its step carries another Uniqueid
  relieved                          not renamed; no leave; in its step a row (`QueueEntry`,
                                    `ConfbridgeList`), a mute or a leave of that surface names a
                                    pair (N2, U2), U2 != U1, where U2 never joined that surface
  left-unannounced                  not renamed; no leave; the row requested after the trigger no
                                    longer lists it, and no other pair takes its place
  normal                            a leave with the join's name and Uniqueid
  unclassified                      none of the above (a shape these labels do not cover)

and the facts a reading needs: every name the Uniqueid carried, its `Rename` frames, its leave, its
`Hangup` (and whether it came before the leave, or before the other pair's leave), the rows and
mutes that name it or any name it carried while it waited, the frames that carry both U1 and U2,
whether the other pair kept the join's name, and for a left-unannounced target the seconds from the
trigger to its `Hangup` and whether a `BridgeEnter` of it came first. The trigger's time is the first stamped
frame from the trigger's own success frame on (the `executed` signal below); only a trigger with no
such frame falls back to the first stamped frame after the `-waiting` marker, and the record says
which (`trigger_to_hangup_from`). The rows are read under every name the Uniqueid carried, under any
Uniqueid: for a renamed target `rows_disagree_kinds` names a row with another name under the join's
Uniqueid, and a row with a carried name under another Uniqueid.

Per take it also checks:

  identity-change   outside both surfaces (`--identity-step`, default `positive-pickupchan`) a
                    `Pickup` frame whose Uniqueid differs from its `TargetUniqueid`, the target's
                    `Hangup`, and the picker's `BridgeEnter`
  complete          each target has at least one `Newchannel` and one `Hangup`
  executed          per trigger step, the trigger's own signal: Bridge and Redirect a
                    `Response: Success` with their success message; Replaces a `QueueEntry` or
                    `ConfbridgeList` row naming a pair that never joined (a leave or a mute alone
                    is not enough); REFER an `AttendedTransfer` frame with `Result: Success`;
                    PickupChan and Pickup a `Pickup` frame; ARI move a 2xx in the step's
                    `CaptureOutcome` (ARI answers over HTTP, not on the AMI wire); the Local pair a
                    `LocalOptimizationBegin` in the control step. Each step reads one of three
                    states: `executed` (its signal is in the take), `refused by Asterisk` (an
                    explicit refusal is: the `Response: Error` that the step's `CaptureOutcome`
                    quotes for Bridge or Redirect, an `AttendedTransfer` with any other `Result`, a
                    `CaptureApp` line saying PickupChan or Pickup returned without a pickup, an
                    HTTP 4xx or 5xx for ARI move), or `not reached` (neither)
  reuse             per surface, channel names joined under two or more Uniqueids and leaves that
                    arrived after a later join of the same name; `not exercised` with no reuse

`--controls` mutates a copy of the take, in memory, once per label, and checks that the label
fires on the mutated target; it also adds sentinel-Uniqueid frames under two names and checks that
nothing is renamed, and copies one leave frame with its Uniqueid altered and one with its Channel
altered and checks that both are flagged.
"""
from __future__ import annotations

import collections
import glob
import importlib.util
import json
import os
import re
import sys

_HERE = os.path.dirname(os.path.abspath(__file__))
_spec = importlib.util.spec_from_file_location("capture_ami_recording",
                                               os.path.join(_HERE, "capture-ami-recording.py"))
cap = importlib.util.module_from_spec(_spec)
_spec.loader.exec_module(cap)

SURFACES = {
    "queue": ("QueueCallerJoin", "QueueCallerLeave", "Queue", ("QueueEntry",), ()),
    "conference": ("ConfbridgeJoin", "ConfbridgeLeave", "Conference", ("ConfbridgeList",),
                   ("ConfbridgeMute", "ConfbridgeUnmute")),
}
LABELS = ("renamed-leave-new-name", "renamed-leave-new-name-no-rename", "renamed-leave-old-name",
          "renamed-leave-other-uniqueid", "relieved", "left-unannounced", "normal", "unclassified")
# the end of a list requested while waiting: the rows before it are the whole answer
LIST_ENDS = {"queue": ("QueueStatusComplete",), "conference": ("ConfbridgeListComplete",)}
SUCCESS_MESSAGES = {"bridge": "Channels have been bridged", "redirect": "Redirect successful"}
PICKUP_APPS = {"pickupchan": "PickupChan", "pickup": "Pickup"}
STATES = {"executed": "executed", "refused": "refused by Asterisk", "not reached": "not reached", None: "-"}


# --- reading ------------------------------------------------------------------------------


def read_frames(data: bytes):
    """Parsed frames of a recording or a tap (banner first), each with `_i` (its index),
    `_step` (the marker step it belongs to) and `_waiting` (after a `-waiting` marker)."""
    _banner, raw, _partial = cap.split_stream(data)
    frames = []
    step, waiting = "(all)", False
    for i, fb in enumerate(raw):
        h = cap.headers(fb)
        if h.get("Event") == "UserEvent" and h.get("UserEvent") == "CaptureMarker":
            name = h.get("Step", "")
            waiting = name.endswith("-waiting")
            step = re.sub(r"-waiting$", "", name)
        h["_i"], h["_step"], h["_waiting"] = i, step, waiting
        frames.append(h)
    return frames


def _uid_ok(uid):
    return not cap.is_sentinel_uniqueid(uid)


def _ts(h):
    try:
        return float(h.get("Timestamp", ""))
    except ValueError:
        return None


def _pairs(h):
    """(name, Uniqueid) pairs a frame carries: each `<prefix>Channel` with its `<prefix>Uniqueid`."""
    out = []
    for key, value in h.items():
        if isinstance(value, str) and key.endswith("Channel"):
            uid = h.get(key[: -len("Channel")] + "Uniqueid")
            if _uid_ok(uid) and value:
                out.append((value, uid))
    return out


def _own(h):
    """The capture's own user events (markers, outcomes): an index of the take, not the wire."""
    return h.get("Event") == "UserEvent" and h.get("UserEvent", "").startswith("Capture")


def triggers_by_step(frames):
    """step -> trigger, from the capture's own `CaptureOutcome` lines (or the step's name)."""
    out = {}
    for h in frames:
        if h.get("Event") == "UserEvent" and h.get("UserEvent") == "CaptureOutcome":
            out[h.get("Step")] = h.get("Trigger", "")
    return out


# --- per target ---------------------------------------------------------------------------


def classify(frames):
    """One record per target, in join order."""
    names = cap.channel_names_by_uniqueid([{k: v for k, v in h.items() if not k.startswith("_")}
                                           for h in frames])
    steps = collections.defaultdict(list)
    for h in frames:
        steps[h["_step"]].append(h)
    triggers = triggers_by_step(frames)
    records = []
    for surface, (join_ev, leave_ev, _room, row_evs, mute_evs) in SURFACES.items():
        joined = {}
        for h in frames:
            if h.get("Event") == join_ev and "ActionID" not in h and _uid_ok(h.get("Uniqueid")):
                joined.setdefault(h["Uniqueid"], h)
        for u1, join in joined.items():
            records.append(_target(frames, steps[join["_step"]], names, joined, surface, join,
                                   leave_ev, row_evs, mute_evs, triggers))
    records.sort(key=lambda r: r["join_index"])
    return records


def _target(frames, step_frames, names, joined, surface, join, leave_ev, row_evs, mute_evs, triggers):
    u1, n1, j = join["Uniqueid"], join.get("Channel", ""), join["_i"]
    carried = list(names.get(u1, [n1]))
    renames = [h for h in frames if h.get("Event") == "Rename" and h.get("Uniqueid") == u1]
    leave = next((h for h in frames if h["_i"] > j and h.get("Event") == leave_ev and "ActionID" not in h
                  and h.get("Uniqueid") == u1), None)
    hangup = next((h for h in frames if h.get("Event") == "Hangup" and h.get("Uniqueid") == u1), None)
    in_step = [h for h in step_frames if h["_i"] > j]
    surface_frames = [h for h in in_step if h.get("Event") in row_evs + mute_evs
                      or (h.get("Event") == leave_ev and "ActionID" not in h)]
    others = []
    for h in surface_frames:
        u2, n2 = h.get("Uniqueid"), h.get("Channel", "")
        if _uid_ok(u2) and u2 != u1 and u2 not in joined and (n2, u2) not in others:
            others.append((n2, u2))
    step_leaves = [h for h in in_step if h.get("Event") == leave_ev and "ActionID" not in h]
    rows = [h for h in in_step if h.get("Event") in row_evs + mute_evs]
    rows_naming = [(h.get("Event"), h.get("Channel", ""), h.get("Uniqueid", "")) for h in rows
                   if h.get("Uniqueid") == u1 or h.get("Channel") in carried]
    row_pairs = [(h.get("Channel", ""), h.get("Uniqueid")) for h in in_step if h.get("Event") in row_evs]
    waiting = [h for h in in_step if h["_waiting"]]
    requested_after = any(h.get("Event") in LIST_ENDS[surface] or
                          (h.get("Response") == "Error" and surface == "conference"
                           and "conference" in h.get("Message", "").lower()) for h in waiting)
    listed_after = any(h.get("Event") in row_evs and h.get("Uniqueid") == u1 for h in waiting)
    linking = []
    for n2, u2 in others:
        for h in frames:
            if _own(h):
                continue
            values = {v for k, v in h.items() if not k.startswith("_") and isinstance(v, str)}
            if u1 in values and u2 in values:
                linking.append(h["_i"])
    linking = sorted(set(linking))

    rec = {
        "step": join["_step"], "trigger": triggers.get(join["_step"], join["_step"]),
        "surface": surface, "join_index": j, "join_name": n1, "uniqueid": u1,
        "names": carried, "rename_frames": len(renames),
        "rename_newnames": [h.get("Newname", "") for h in renames],
        "leave": ({"name": leave.get("Channel", ""), "uniqueid": u1, "index": leave["_i"]} if leave else None),
        "hangup_index": hangup["_i"] if hangup else None,
        "leave_before_hangup": (leave["_i"] < hangup["_i"]) if (leave and hangup) else None,
        "rows_naming": rows_naming,
        "other_pairs": [list(p) for p in others],
        "other_pairs_in_rows": [list(p) for p in others if p in row_pairs],
        "other_keeps_name": any(n2 == n1 for n2, _ in others),
        "linking_frames": linking,
        "hangup_in_step": bool(hangup and step_frames and hangup["_i"] <= step_frames[-1]["_i"]),
        "hangup_before_other_leave": None,
        "listed_after_trigger": listed_after if requested_after else None,
        "trigger_to_hangup_s": None, "trigger_to_hangup_from": None, "bridge_enter_before_hangup": None,
        "newchannel": any(h.get("Event") == "Newchannel" and h.get("Uniqueid") == u1 for h in frames),
        "rows_disagree_with_leave": False, "rows_disagree_kinds": [],
    }
    renamed = len(carried) > 1
    if renamed:
        if leave:
            if leave.get("Channel") == n1:
                label = "renamed-leave-old-name"
            elif leave.get("Channel") == carried[-1]:
                label = "renamed-leave-new-name" if renames else "renamed-leave-new-name-no-rename"
            else:
                label = "unclassified"
        elif any(h.get("Uniqueid") != u1 for h in step_leaves):
            label = "renamed-leave-other-uniqueid"
        else:
            label = "unclassified"
        kinds = []
        if leave and any(u == u1 and c != leave.get("Channel") for _e, c, u in rows_naming):
            kinds.append("another name under the join's Uniqueid")
        if any(u != u1 and c in carried for _e, c, u in rows_naming):
            kinds.append("a carried name under another Uniqueid")
        rec["rows_disagree_kinds"] = kinds
        rec["rows_disagree_with_leave"] = bool(kinds)
    elif leave:
        label = "normal"
    elif others:
        label = "relieved"
        other_leaves = [h["_i"] for h in step_leaves if h.get("Uniqueid") != u1]
        if hangup and rec["hangup_in_step"]:
            rec["hangup_before_other_leave"] = (hangup["_i"] < min(other_leaves)) if other_leaves else True
        else:
            rec["hangup_before_other_leave"] = False
    elif rec["listed_after_trigger"] is False:
        label = "left-unannounced"
        if hangup:
            # the trigger's time: the first stamped frame from the trigger's own success frame on;
            # failing that, the first stamped frame after the `-waiting` marker; failing that, the join
            success = _success_frames(in_step, rec["trigger"], join["_step"])
            t0, rec["trigger_to_hangup_from"] = None, None
            if success:
                t0 = next((_ts(h) for h in in_step if h["_i"] >= success[0]["_i"] and _ts(h) is not None), None)
                rec["trigger_to_hangup_from"] = "trigger success" if t0 is not None else None
            if t0 is None:
                t0 = next((_ts(h) for h in waiting if _ts(h) is not None), None)
                rec["trigger_to_hangup_from"] = "waiting marker (fallback)" if t0 is not None else None
            if t0 is None:
                t0, rec["trigger_to_hangup_from"] = _ts(join), "join (fallback)"
            t1 = _ts(hangup)
            rec["trigger_to_hangup_s"] = round(t1 - t0, 3) if (t0 is not None and t1 is not None) else None
            rec["bridge_enter_before_hangup"] = any(
                h.get("Event") == "BridgeEnter" and h.get("Uniqueid") == u1 and j < h["_i"] < hangup["_i"]
                for h in frames)
    else:
        label = "unclassified"
    rec["label"] = label
    return rec


def _success_frames(frames, trigger, step):
    """The frames among `frames` that are the trigger's own success signal (see `executed`), in
    order; empty for a trigger with none, or one read elsewhere (Replaces, the Local pair)."""
    if trigger in SUCCESS_MESSAGES:
        return [h for h in frames if h.get("Response") == "Success"
                and h.get("Message", "") == SUCCESS_MESSAGES[trigger]]
    if trigger == "refer":
        return [h for h in frames if h.get("Event") == "AttendedTransfer" and h.get("Result") == "Success"]
    if trigger == "local-optimization" and step.endswith("-control"):
        return [h for h in frames if h.get("Event") == "LocalOptimizationBegin"]
    if trigger in PICKUP_APPS:
        return [h for h in frames if h.get("Event") == "Pickup" and _uid_ok(h.get("TargetUniqueid"))]
    return []


def _outcome_detail(frames, step):
    """The `Detail` of the step's own `CaptureOutcome` line, or ""."""
    return next((h.get("Detail", "") for h in frames if h.get("Event") == "UserEvent"
                 and h.get("UserEvent") == "CaptureOutcome" and h.get("Step") == step), "")


def _ari_status(detail):
    m = re.match(r"ARI move: HTTP (\d{3})\b", detail)
    return int(m.group(1)) if m else None


def _refusal(frames, trigger, step):
    """Where Asterisk explicitly refused the trigger in its step, as text; None when it did not."""
    detail = _outcome_detail(frames, step)
    if trigger in SUCCESS_MESSAGES:
        # the error the step's own record quotes, so an error to another action is not read as one
        quoted = detail.split(": Error: ", 1)[1] if ": Error: " in detail else None
        hits = [h for h in frames if h.get("Response") == "Error" and not h["_waiting"]
                and quoted is not None and h.get("Message", "") == quoted]
        return f"frame {hits[0]['_i']}: Response: Error ({quoted})" if hits else None
    if trigger == "refer":
        hits = [h for h in frames if h.get("Event") == "AttendedTransfer" and h.get("Result") != "Success"]
        return f"frame {hits[0]['_i']}: AttendedTransfer Result {hits[0].get('Result')}" if hits else None
    if trigger in PICKUP_APPS:
        hits = [h for h in frames if h.get("Event") == "UserEvent" and h.get("UserEvent") == "CaptureApp"
                and h.get("App") == PICKUP_APPS[trigger] and h.get("Result") == "returned"]
        return f"frame {hits[0]['_i']}: {PICKUP_APPS[trigger]} returned without a pickup" if hits else None
    if trigger == "ari-move":
        status = _ari_status(detail)
        return f"HTTP {status} to ARI move (the step's CaptureOutcome)" if status and status >= 400 else None
    return None


# --- per take -----------------------------------------------------------------------------


def identity_change(frames, step="positive-pickupchan"):
    """The live control: a Pickup whose picker and target differ, the target's Hangup, the
    picker's BridgeEnter, all in `step`."""
    in_step = [h for h in frames if h["_step"] == step]
    for p in (h for h in in_step if h.get("Event") == "Pickup"):
        picker, target = p.get("Uniqueid"), p.get("TargetUniqueid")
        if not (_uid_ok(picker) and _uid_ok(target)) or picker == target:
            continue
        hung = any(h.get("Event") == "Hangup" and h.get("Uniqueid") == target for h in in_step)
        entered = any(h.get("Event") == "BridgeEnter" and h.get("Uniqueid") == picker for h in in_step)
        if hung and entered:
            return {"green": True, "picker": picker, "target": target}
    return {"green": False, "picker": None, "target": None}


def executed(frames, records):
    """step -> trigger, signal seen (True / False / None for no reading), what was looked for, its
    state (`executed`, `refused`, `not reached`, or None with no reading) and the refusal seen."""
    triggers = triggers_by_step(frames)
    by_step = collections.defaultdict(list)
    for h in frames:
        by_step[h["_step"]].append(h)
    out = {}
    for step, trigger in triggers.items():
        fr = by_step.get(step, [])
        if trigger in SUCCESS_MESSAGES:
            seen = bool(_success_frames(fr, trigger, step))
            what = f"Response: Success ({SUCCESS_MESSAGES[trigger]})"
        elif trigger == "replaces":
            seen = any(r["step"] == step and r["other_pairs_in_rows"] for r in records)
            what = "a QueueEntry or ConfbridgeList row naming a pair that never joined"
        elif trigger == "refer":
            transfers = [h for h in fr if h.get("Event") == "AttendedTransfer"]
            seen = bool(_success_frames(fr, trigger, step))
            what = "AttendedTransfer with Result: Success; seen: " + (
                ", ".join(f"Result {h.get('Result')} DestType {h.get('DestType')}" for h in transfers) or "none")
        elif trigger == "local-optimization":
            if step.endswith("-control"):
                seen = bool(_success_frames(fr, trigger, step))
                what = "LocalOptimizationBegin on the control pair"
            else:
                seen = None
                what = "read from the control step"
        elif trigger in PICKUP_APPS:
            seen = bool(_success_frames(fr, trigger, step))
            what = "a Pickup frame"
        elif trigger == "ari-move":
            status = _ari_status(_outcome_detail(fr, step))
            seen = status is not None and 200 <= status < 300
            what = "an HTTP 2xx to ARI move, from the step's CaptureOutcome"
        else:
            seen, what = None, "no success signal defined"
        if trigger in ("bridge", "redirect") and step.startswith("positive-"):
            seen = None if trigger == "redirect" else seen
        refusal = None if seen is None else _refusal(fr, trigger, step)
        state = None if seen is None else "executed" if seen else "refused" if refusal else "not reached"
        out[step] = {"trigger": trigger, "executed": seen, "signal": what, "state": state, "refusal": refusal}
    return out


def reuse(frames):
    """Per surface: reused join names, inverted leaves, and the (e) reading."""
    out = {}
    for surface, (join_ev, leave_ev, room, _r, _m) in SURFACES.items():
        names = collections.defaultdict(set)
        held, joined, open_uids = {}, {}, set()
        inverted = 0
        for h in frames:
            uid, ch = h.get("Uniqueid"), h.get("Channel")
            if "ActionID" in h or not _uid_ok(uid):
                continue
            if h.get("Event") == join_ev:
                names[ch].add(uid)
                joined[uid] = ch
                held[(h.get(room), ch)] = uid
                open_uids.add(uid)
            elif h.get("Event") == leave_ev:
                key = (h.get(room), ch)
                if key in held and held[key] != uid and uid in open_uids and joined.get(uid) == ch:
                    inverted += 1
                if held.get(key) == uid:
                    held.pop(key, None)
                open_uids.discard(uid)
        reused = sum(1 for u in names.values() if len(u) > 1)
        out[surface] = {"reused_names": reused, "inverted": inverted,
                        "reading": "not exercised" if reused == 0 else
                        ("inverted leave found" if inverted else "no inverted leave")}
    return out


def scan_take(data: bytes, identity_step="positive-pickupchan"):
    frames = read_frames(data)
    records = classify(frames)
    complete = [{"uniqueid": r["uniqueid"], "newchannel": r["newchannel"], "hangup": r["hangup_index"] is not None}
                for r in records]
    return {
        "frames": len(frames),
        "targets": records,
        "identity_change": identity_change(frames, identity_step),
        "complete": {"green": all(c["newchannel"] and c["hangup"] for c in complete), "targets": complete},
        "executed": executed(frames, records),
        "reuse": reuse(frames),
        "renamed_uniqueids": sorted(u for u, n in cap.channel_names_by_uniqueid(
            [{k: v for k, v in h.items() if not k.startswith("_")} for h in frames]).items() if len(n) > 1),
    }


# --- join/leave columns (both surfaces) and the copy-mode mutation control -----------------


def surface_counts(frames, surface):
    join_ev, leave_ev, _room, _r, _m = SURFACES[surface]
    c = collections.Counter()
    joined = {}
    open_uids = set()
    for h in frames:
        if "ActionID" in h:
            continue
        uid, ch = h.get("Uniqueid"), h.get("Channel")
        if h.get("Event") == join_ev:
            c["joins"] += 1
            joined[uid] = ch
            open_uids.add(uid)
        elif h.get("Event") == leave_ev:
            c["leaves"] += 1
            if uid in joined:
                c["same_uid"] += 1
                c["same_name" if joined[uid] == ch else "name_differs"] += 1
            else:
                c["uid_not_joined"] += 1
            open_uids.discard(uid)
    c["join_without_leave"] = len(open_uids)
    return c


_FRAME = rb"(?:[^\r\n]+\r\n)*?\r\n"


def _find_frame(data: bytes, event: str, uid: str | None = None, start: int = 0):
    for m in re.finditer(rb"(?<=\r\n)Event: " + re.escape(event.encode()) + rb"\r\n" + _FRAME,
                         data[start:]):
        if uid is None or re.search(rb"\r\nUniqueid: " + re.escape(uid.encode()) + rb"\r\n", m.group(0)):
            return start + m.start(), start + m.end()
    return None


def _set(frame: bytes, key: str, value: str) -> bytes:
    pattern = rb"(\r\n|^)" + re.escape(key.encode()) + rb": [^\r\n]*\r\n"
    return re.sub(pattern, lambda m: m.group(1) + key.encode() + b": " + value.encode() + b"\r\n", frame, count=1)


def _frame(*pairs) -> bytes:
    return "".join(f"{k}: {v}\r\n" for k, v in pairs).encode("latin-1") + b"\r\n"


def copy_control(data: bytes, surface: str):
    """The leave-against-join control in copy mode: a copy of the first leave, Uniqueid altered, is
    appended after it; then (separately) a copy with its Channel altered. Both must be flagged."""
    leave_ev = SURFACES[surface][1]
    span = _find_frame(data, leave_ev)
    if span is None:
        return {"surface": surface, "pass": False, "detail": "no leave frame"}
    original = data[span[0]:span[1]]
    uid = cap.headers(original).get("Uniqueid", "")
    chan = cap.headers(original).get("Channel", "")
    base = surface_counts(read_frames(data), surface)
    cases = {"uniqueid": _set(original, "Uniqueid", uid + "9"), "channel": _set(original, "Channel", chan + "-x")}
    out = {"surface": surface, "pass": True, "cases": {}}
    for name, copy in cases.items():
        c = surface_counts(read_frames(data[: span[1]] + copy + data[span[1]:]), surface)
        key = "uid_not_joined" if name == "uniqueid" else "name_differs"
        flagged = (c[key] == base[key] + 1 and c["leaves"] == base["leaves"] + 1
                   and c["same_name"] == base["same_name"])
        out["cases"][name] = {"flagged": flagged, key: c[key], "baseline": base[key]}
        out["pass"] &= flagged
    return out


# --- synthetic controls: each label fires on its own mutation -------------------------------


def _pick(records, surface, need_waiting_row=False):
    for r in records:
        if r["surface"] == surface and r["label"] == "normal" and r["leave"]:
            if not need_waiting_row or r["listed_after_trigger"]:
                return r
    return None


def _mutate(data: bytes, target, label: str) -> bytes:
    """A copy of the take with `target` (a normal target) changed so that `label` should fire."""
    u1, n1, surface = target["uniqueid"], target["join_name"], target["surface"]
    join_ev, leave_ev, _room, row_evs, _m = SURFACES[surface]
    new = n1 + "<SYN>"
    u2, n2 = u1 + "99", "SYN/other-0001"
    js = _find_frame(data, join_ev, u1)
    ls = _find_frame(data, leave_ev, u1, js[1])
    leave = data[ls[0]:ls[1]]
    ts = cap.headers(data[js[0]:js[1]]).get("Timestamp")
    stamp = (("Timestamp", ts),) if ts else ()
    seen_new = _frame(("Event", "Newstate"), *stamp, ("Channel", new), ("Uniqueid", u1))
    rename = _frame(("Event", "Rename"), *stamp, ("Channel", n1), ("Newname", new), ("Uniqueid", u1))

    def with_leave(replacement, insert=b""):
        return data[: js[1]] + insert + data[js[1]: ls[0]] + replacement + data[ls[1]:]

    if label == "normal":
        return data
    if label == "renamed-leave-new-name":
        return with_leave(_set(leave, "Channel", new), rename)
    if label == "renamed-leave-new-name-no-rename":
        return with_leave(_set(leave, "Channel", new), seen_new)
    if label == "renamed-leave-old-name":
        return with_leave(leave, seen_new)
    if label == "renamed-leave-other-uniqueid":
        return with_leave(_set(leave, "Uniqueid", u2), seen_new)
    if label == "relieved":
        return with_leave(_set(_set(leave, "Uniqueid", u2), "Channel", n2))
    if label == "relieved-keeps-name":
        return with_leave(_set(leave, "Uniqueid", u2))
    if label == "left-unannounced":
        out = with_leave(b"")
        for ev in row_evs:  # the rows that still list it go too
            while True:
                span = _find_frame(out, ev, u1)
                if span is None:
                    break
                out = out[: span[0]] + out[span[1]:]
        return out
    raise ValueError(label)


def _drop_hangup(data: bytes, uid: str) -> bytes:
    span = _find_frame(data, "Hangup", uid)
    return data if span is None else data[: span[0]] + data[span[1]:]


def _hangup_before(data: bytes, uid: str, event: str, event_uid: str) -> bytes:
    """Move `uid`'s Hangup to just before the first `event` frame of `event_uid`."""
    span = _find_frame(data, "Hangup", uid)
    if span is None:
        return data
    hangup = data[span[0]:span[1]]
    data = data[: span[0]] + data[span[1]:]
    at = _find_frame(data, event, event_uid)
    return data[: at[0]] + hangup + data[at[0]:]


# facet -> (base label, mutation of the base label's copy, the record test that must hold)
FACETS = {
    "relieved-hangup-first-unlinked": ("relieved", lambda d, t, u2, n2: _hangup_before(d, t["uniqueid"],
                                                                                     SURFACES[t["surface"]][1], u2),
                                       lambda r: r["hangup_before_other_leave"] is True and not r["linking_frames"]),
    "relieved-linked": ("relieved", lambda d, t, u2, n2: d + _frame(("Event", "SynLink"), ("Uniqueid", t["uniqueid"]),
                                                                    ("OtherUniqueid", u2)),
                        lambda r: bool(r["linking_frames"])),
    "relieved-no-hangup": ("relieved", lambda d, t, u2, n2: _drop_hangup(d, t["uniqueid"]),
                           lambda r: r["hangup_before_other_leave"] is False and r["hangup_index"] is None),
    "relieved-keeps-name": ("relieved-keeps-name", None, lambda r: r["other_keeps_name"] is True),
    "left-unannounced-with-hangup": ("left-unannounced", None,
                                     lambda r: r["hangup_index"] is not None and r["trigger_to_hangup_s"] is not None),
    "normal-hangup-first": ("normal", lambda d, t, u2, n2: _hangup_before(d, t["uniqueid"], SURFACES[t["surface"]][1],
                                                                          t["uniqueid"]),
                            lambda r: r["leave_before_hangup"] is False),
}


def synthetic_controls(data: bytes):
    """Per surface and label: did the label fire on its mutated target? Plus the sentinel check."""
    base = scan_take(data)
    results = []
    for surface in SURFACES:
        for label in LABELS[:-1]:
            target = _pick(base["targets"], surface, need_waiting_row=(label == "left-unannounced"))
            if target is None:
                results.append({"surface": surface, "label": label, "fired": False, "got": "no normal target"})
                continue
            mutated = scan_take(_mutate(data, target, label))
            got = next((r["label"] for r in mutated["targets"]
                        if r["uniqueid"] == target["uniqueid"] and r["surface"] == surface), "missing")
            results.append({"surface": surface, "label": label, "fired": got == label, "got": got,
                            "uniqueid": target["uniqueid"], "step": target["step"]})
    for facet, (base_label, extra, holds) in FACETS.items():
        for surface in SURFACES:
            target = _pick(base["targets"], surface, need_waiting_row=(base_label == "left-unannounced"))
            if target is None:
                results.append({"surface": surface, "label": facet, "fired": False, "got": "no normal target"})
                continue
            mutated = _mutate(data, target, base_label)
            if extra is not None:
                mutated = extra(mutated, target, target["uniqueid"] + "99", "SYN/other-0001")
            rec = next((r for r in scan_take(mutated)["targets"]
                        if r["uniqueid"] == target["uniqueid"] and r["surface"] == surface), None)
            want = "relieved" if base_label == "relieved-keeps-name" else base_label
            fired = rec is not None and rec["label"] == want and holds(rec)
            results.append({"surface": surface, "label": facet, "fired": fired,
                            "got": rec["label"] if rec else "missing", "uniqueid": target["uniqueid"]})
    sentinel = data + b"".join(_frame(("Event", "OriginateResponse"), ("Response", "Failure"),
                                      ("Channel", f"Local/syn{i}@capture/n"), ("Uniqueid", uid))
                               for i, uid in enumerate(("<unknown>", "<unknown>", "", "")))
    sentinel_ok = scan_take(sentinel)["renamed_uniqueids"] == base["renamed_uniqueids"]
    return {"labels": results, "sentinel_ignored": sentinel_ok,
            "copy_mode": [copy_control(data, s) for s in SURFACES],
            "green": all(r["fired"] for r in results) and sentinel_ok}


# --- output -------------------------------------------------------------------------------


def files_of(args):
    out = []
    for a in args:
        out += sorted(glob.glob(os.path.join(a, "*.raw"))) if os.path.isdir(a) else [a]
    return out


def print_take(path, take):
    print(f"== {os.path.basename(path)}: {take['frames']} frames, renamed Uniqueids "
          f"{len(take['renamed_uniqueids'])}")
    print("step surface label join_name uniqueid names rename_frames leave leave_before_hangup "
          "other_pairs keeps_name linking hangup_before_other_leave listed_after trigger_to_hangup_s "
          "bridge_enter_first t0_from")
    for r in take["targets"]:
        leave = f"{r['leave']['name']}|{r['leave']['uniqueid']}" if r["leave"] else "-"
        others = ",".join(f"{n}|{u}" for n, u in r["other_pairs"]) or "-"
        print(r["step"], r["surface"], r["label"], r["join_name"], r["uniqueid"], ",".join(r["names"]),
              r["rename_frames"], leave, r["leave_before_hangup"], others, r["other_keeps_name"],
              len(r["linking_frames"]), r["hangup_before_other_leave"], r["listed_after_trigger"],
              r["trigger_to_hangup_s"], r["bridge_enter_before_hangup"],
              (r["trigger_to_hangup_from"] or "-").replace(" ", "-"))
    counts = collections.Counter((r["surface"], r["label"]) for r in take["targets"])
    for (surface, label), n in sorted(counts.items()):
        print(f"  {surface} {label}: {n}")
    normal = [r for r in take["targets"] if r["label"] == "normal"]
    print(f"  normal leave before its Hangup: {sum(1 for r in normal if r['leave_before_hangup'])}"
          f"/{len(normal)}; Hangup first: {sum(1 for r in normal if r['leave_before_hangup'] is False)}; "
          f"no Hangup: {sum(1 for r in normal if r['leave_before_hangup'] is None)}")
    ic = take["identity_change"]
    print(f"  identity-change: {'green' if ic['green'] else 'NOT green'}"
          + (f" (picker {ic['picker']}, target {ic['target']})" if ic["green"] else ""))
    missing = [c["uniqueid"] for c in take["complete"]["targets"] if not (c["newchannel"] and c["hangup"])]
    print(f"  complete: {'green' if take['complete']['green'] else 'NOT green, ' + ','.join(missing)}")
    for step, e in take["executed"].items():
        refusal = f"; refused at {e['refusal']}" if e["refusal"] else ""
        print(f"  executed {step} ({e['trigger']}): {STATES[e['state']]} [{e['signal']}{refusal}]")
    for surface, e in take["reuse"].items():
        print(f"  ({surface}) reuse: reused_names {e['reused_names']} inverted {e['inverted']}: {e['reading']}")


def print_controls(path, ctl):
    print(f"== controls on a copy of {os.path.basename(path)}")
    for r in ctl["labels"]:
        print(f"  {r['surface']} {r['label']}: {'FIRED' if r['fired'] else 'MISSED'} (got {r['got']})")
    print(f"  sentinel Uniqueids ignored: {'yes' if ctl['sentinel_ignored'] else 'NO'}")
    for c in ctl["copy_mode"]:
        print(f"  copy mode, {c['surface']}: {'PASS' if c['pass'] else 'FAIL'} {c.get('cases', c.get('detail'))}")
    print("controls:", "PASS" if ctl["green"] and all(c["pass"] for c in ctl["copy_mode"]) else "FAIL")


def main(argv=None):
    argv = list(sys.argv[1:] if argv is None else argv)
    if argv[:1] == ["--controls"]:
        rc = 0
        for path in files_of(argv[1:]):
            ctl = synthetic_controls(open(path, "rb").read())
            print_controls(path, ctl)
            rc |= 0 if ctl["green"] and all(c["pass"] for c in ctl["copy_mode"]) else 1
        return rc
    as_json = argv[:1] == ["--json"]
    paths = files_of(argv[1:] if as_json else argv)
    takes = {p: scan_take(open(p, "rb").read()) for p in paths}
    if as_json:
        json.dump({os.path.basename(p): t for p, t in takes.items()}, sys.stdout, indent=1)
        print()
    else:
        for p, t in takes.items():
            print_take(p, t)
    return 0


if __name__ == "__main__":
    sys.exit(main())

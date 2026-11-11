#!/usr/bin/env python3
"""Capture what a real Asterisk sends on its manager interface (AMI) into a committed recording.

One command per image: start a throwaway Asterisk from an image pinned by digest, drive a fixed
workload through a raw TCP tap, then reduce the tap to a byte-exact, redacted recording under the
256 KiB per-file cap, beside a provenance sidecar:

    python3 scripts/capture-ami-recording.py capture \\
        --image andrius/asterisk:22.10.1_debian-trixie-6bb254a@sha256:<64 hex> \\
        --workload bridges-and-conference

    python3 scripts/capture-ami-recording.py reduce <tap.raw> --out <dir>   # re-reduce a kept tap
    python3 scripts/capture-ami-recording.py workloads                      # list the workloads

What `capture` does, in order:

1. Starts one container from `<repo>@sha256:<digest>` -- the digest, never the tag, decides what
   runs -- with the workload's config files mounted read-only and a manager account whose secret
   is generated for this run and never written anywhere but the container's own `manager.conf`.
   AMI is published on an ephemeral loopback port (`127.0.0.1::5038`) only.
2. Reads `asterisk -V` inside the container and the image id Docker resolved, and refuses an image
   whose id is not the digest it was given.
3. Logs in with a raw socket (`Events: on`) and records **every byte** the PBX sends on that
   connection. Before each step of the workload it sends an AMI `UserEvent` with
   `UserEvent: CaptureMarker` and `Step: <id>`; the PBX echoes it into the stream, so a replay can
   attribute every frame to the step that was current. The first marker is
   `UserEvent: CaptureProvenance`, which carries `AsteriskVersion`, `Image` and `Workload`: the
   recording names the build that produced it in its own bytes.
4. Removes the container (always, also on failure), then reduces the tap.

The tap itself is unredacted (it holds the container's addresses and the manager account name), so
it is written outside the repository (`--raw-dir`, default a fresh temporary directory) and only the
reduced file reaches `--out`.

What the reduction does -- a pure function of the tap's bytes and the options, so the same input
always gives the same output bytes:

* Keeps the banner line and **whole frames**: a kept frame is byte-exact, CRLF framing included,
  apart from the replacements below.
* Removes the login answer and every frame of a type that carries an address or the account:
  `SuccessfulAuth`, `FullyBooted`, `RTCPSent`, `RTCPReceived`, `ChallengeSent`, `ChallengeResponseFailed`,
  `InvalidAccountID`, `InvalidPassword`, and a `VarSet` of `SIPDOMAIN`; plus any type named in
  `--drop-events`. `--keep-steps` keeps only the frames of the named steps (the provenance marker is
  always kept).
* Replaces each distinct UUID, and each bare 32-hex run, by a single-character fill of the same
  length in order of first appearance (`11111111-1111-...`, then `22222222-...`), and each IPv4
  address by `192.0.2.<n>` (RFC 5737 documentation range), consistently within the file.
* Refuses to write a result over 256 KiB (it prints the bytes per event type instead, so the caller
  can choose `--drop-events`), one that still contains the run's secret, or one that the repository's
  recording redaction check (`scripts/check-recording-redaction.py`) would reject.

Docker is driven through its CLI; there is no third-party Python dependency (stdlib only, by the
same rule as the other `scripts/*.py`).

Exit codes: 0 = written; 1 = refused or failed (the reason is printed).
"""

from __future__ import annotations

import argparse
import base64
import collections
import datetime as _dt
import hashlib
import importlib.util
import json
import os
import re
import secrets
import socket
import subprocess
import sys
import tempfile
import time
import urllib.error
import urllib.parse
import urllib.request
from pathlib import Path

SCHEMA = "verbara.ami-capture-provenance/1"
SIZE_CAP_BYTES = 256 * 1024
AMI_PORT = 5038
MANAGER_USER = "capture"

_HERE = Path(__file__).resolve().parent
_REPO = _HERE.parent
DEFAULT_OUT = _REPO / "Tests" / "Verbara.Sdk.Sessions.FunctionalTests" / "Recordings" / "asterisk-ami"

# Frame types that carry a container address, the manager account or a manager session id.
ALWAYS_DROPPED = (
    "SuccessfulAuth", "FullyBooted", "RTCPSent", "RTCPReceived", "ChallengeSent",
    "ChallengeResponseFailed", "InvalidAccountID", "InvalidPassword",
)

_IMAGE_REF = re.compile(r"^(?P<repo>[a-z0-9][a-z0-9._/-]*)(?::(?P<tag>[A-Za-z0-9._-]+))?"
                        r"@(?P<digest>sha256:[0-9a-f]{64})$")
_UUID = re.compile(rb"\b[0-9a-fA-F]{8}-[0-9a-fA-F]{4}-[0-9a-fA-F]{4}-[0-9a-fA-F]{4}-[0-9a-fA-F]{12}\b")
_HEX32 = re.compile(rb"\b[0-9a-fA-F]{32}\b")
_IPV4 = re.compile(rb"(?<![0-9.])(?:\d{1,3}\.){3}\d{1,3}(?![0-9.])")
_FILL = "123456789abcdefghijklmnopqrstuvwxyz"
_KEPT_ADDRESSES = (b"0.0.0.0", b"127.0.0.1")


class CaptureError(Exception):
    """A refusal or failure with a message meant for the operator."""


class ConnectionClosed(CaptureError):
    """The PBX closed the manager connection: no later step of a workload can run."""


# --- Frames -------------------------------------------------------------------------------


def split_stream(data: bytes):
    """Split an AMI byte stream into (banner_line, [frame_bytes], trailing_partial).

    The banner is the first CRLF-terminated line. Each frame is its header lines plus the blank
    line that ends it (`...\\r\\n\\r\\n`), byte-exact.
    """
    end = data.find(b"\r\n")
    if end < 0 or not data.startswith(b"Asterisk Call Manager/"):
        raise CaptureError("the stream does not start with an AMI banner line")
    banner = data[: end + 2]
    frames = []
    i = end + 2
    while i < len(data):
        j = data.find(b"\r\n\r\n", i)
        if j < 0:
            break
        frames.append(data[i: j + 4])
        i = j + 4
    return banner, frames, data[i:]


def headers(frame: bytes):
    """First value of each header name in a frame (latin-1, so every byte round-trips)."""
    out = {}
    for line in frame[:-4].split(b"\r\n"):
        key, sep, value = line.partition(b": ")
        if sep:
            k = key.decode("latin-1")
            if k not in out:
                out[k] = value.decode("latin-1")
    return out


def frame_kind(h) -> str:
    if "Event" in h:
        return h["Event"]
    if "Response" in h:
        return "Response"
    return "(other)"


# --- Reduction ----------------------------------------------------------------------------


def _filler(kind: str):
    seen = {}

    def replace(match):
        value = match.group(0)
        if value not in seen:
            n = len(seen)
            if n < len(_FILL):
                seen[value] = re.sub(rb"[0-9a-fA-F]", _FILL[n].encode(), value)
            else:  # more distinct values than fills: a placeholder the redaction check accepts
                seen[value] = f"redacted-{kind}-{n:04d}".encode()
        return seen[value]

    return seen, replace


def _address_filler():
    seen = {}

    def replace(match):
        value = match.group(0)
        if value in _KEPT_ADDRESSES:
            return value
        if value not in seen:
            seen[value] = f"192.0.2.{len(seen) + 1}".encode()
        return seen[value]

    return seen, replace


def reduce_stream(data: bytes, drop_events=(), keep_steps=()):
    """Reduce a tap to a recording. Pure: the same arguments always return the same bytes.

    Returns (recording_bytes, report_dict).
    """
    banner, frames, partial = split_stream(data)
    drop = set(ALWAYS_DROPPED) | set(drop_events)
    keep_steps = set(keep_steps)
    kept, kept_count, removed_count = [], collections.Counter(), collections.Counter()
    step = None
    for frame in frames:
        h = headers(frame)
        kind = frame_kind(h)
        if kind == "UserEvent" and h.get("UserEvent") == "CaptureMarker":
            step = h.get("Step")
        is_provenance = kind == "UserEvent" and h.get("UserEvent") == "CaptureProvenance"
        reason = None
        if kind == "Response" and h.get("Message") == "Authentication accepted":
            reason = "login Response"
        elif kind in drop:
            reason = kind
        elif kind == "VarSet" and h.get("Variable") == "SIPDOMAIN":
            reason = "VarSet SIPDOMAIN"
        elif keep_steps and not is_provenance and step not in keep_steps:
            reason = "outside --keep-steps"
        if reason:
            removed_count[reason] += 1
            continue
        kept.append(frame)
        kept_count[kind] += 1

    body = b"".join(kept)
    uuids, uuid_sub = _filler("uuid")
    body = _UUID.sub(uuid_sub, body)
    hexes, hex_sub = _filler("hex")
    body = _HEX32.sub(hex_sub, body)
    addresses, address_sub = _address_filler()
    body = _IPV4.sub(address_sub, body)
    recording = banner + body
    report = {
        "frames_in": len(frames),
        "frames_kept": sum(kept_count.values()),
        "trailing_partial_bytes": len(partial),
        "kept": dict(sorted(kept_count.items())),
        "removed": dict(sorted(removed_count.items())),
        "replaced": {"uuid": len(uuids), "hex32": len(hexes), "ipv4": len(addresses)},
    }
    return recording, report


def bytes_by_kind(recording: bytes):
    _, frames, _ = split_stream(recording)
    sizes = collections.Counter()
    for frame in frames:
        sizes[frame_kind(headers(frame))] += len(frame)
    return sizes.most_common()


def _load_redaction_check():
    path = _HERE / "check-recording-redaction.py"
    spec = importlib.util.spec_from_file_location("check_recording_redaction", path)
    module = importlib.util.module_from_spec(spec)
    spec.loader.exec_module(module)
    return module


def check_recording(recording: bytes, forbidden=()):
    """Raise CaptureError when the recording breaks the size cap, leaks, or fails the repo check."""
    if len(recording) > SIZE_CAP_BYTES:
        top = ", ".join(f"{k} {n}" for k, n in bytes_by_kind(recording)[:8])
        raise CaptureError(
            f"the reduced recording is {len(recording)} bytes, over the {SIZE_CAP_BYTES}-byte cap. "
            f"Bytes per frame type: {top}. Re-run `reduce` with --drop-events or --keep-steps.")
    for value in forbidden:
        if value and value.encode() in recording:
            raise CaptureError("the reduced recording still contains the run's manager secret")
    findings = _load_redaction_check().scan_text(recording.decode("utf-8", errors="replace"))
    if findings:
        names = ", ".join(f"{name} (line {line})" for name, line in findings[:10])
        raise CaptureError(f"the recording redaction check rejects the result: {names}")


def provenance(meta, recording: bytes, report, drop_events, keep_steps, recording_name):
    """The sidecar: everything a reader needs to know which build sent these bytes."""
    return {
        "schema": SCHEMA,
        "recording": recording_name,
        "workload": meta["workload"],
        "image": meta["image"],
        "image_id": meta["image_id"],
        "asterisk_version": meta["asterisk_version"],
        "ami_banner": meta["ami_banner"],
        "captured_utc": meta["captured_utc"],
        "driver": "scripts/capture-ami-recording.py",
        "driver_sha256": meta.get("driver_sha256", ""),
        "steps": meta.get("steps", []),
        "tap": {"bytes": meta["tap_bytes"], "sha256": meta["tap_sha256"]},
        "reduction": {
            "drop_events": sorted(set(ALWAYS_DROPPED) | set(drop_events)),
            "keep_steps": sorted(keep_steps),
            **report,
        },
        "bytes": len(recording),
        "sha256": hashlib.sha256(recording).hexdigest(),
    }


def recording_name(workload: str, asterisk_version: str) -> str:
    version = asterisk_version.split()[-1] if asterisk_version else "unknown"
    if not re.fullmatch(r"[0-9A-Za-z._-]+", version):
        raise CaptureError(f"cannot name a recording after the version string {asterisk_version!r}")
    return f"{workload}-asterisk-{version}"


def write_recording(tap: bytes, meta, out_dir: Path, drop_events=(), keep_steps=(), forbidden=()):
    recording, report = reduce_stream(tap, drop_events, keep_steps)
    check_recording(recording, forbidden)
    if meta.get("tap_sha256") != hashlib.sha256(tap).hexdigest():
        raise CaptureError("the tap does not match the sha256 its metadata records")
    name = recording_name(meta["workload"], meta["asterisk_version"])
    out_dir.mkdir(parents=True, exist_ok=True)
    raw_path = out_dir / f"{name}.raw"
    side_path = out_dir / f"{name}.provenance.json"
    raw_path.write_bytes(recording)
    sidecar = provenance(meta, recording, report, drop_events, keep_steps, raw_path.name)
    side_path.write_text(json.dumps(sidecar, indent=2, ensure_ascii=False) + "\n", encoding="utf-8")
    return raw_path, side_path, report, len(recording)


# --- The tap ------------------------------------------------------------------------------


class Tap:
    """A raw AMI client that records every byte it receives and parses frames as they arrive."""

    def __init__(self, sock: socket.socket, clock=time.monotonic):
        self.sock = sock
        self.clock = clock
        self.transcript = bytearray()
        self.buf = b""
        self.frames = []  # parsed header dicts, in arrival order
        self.n = 0
        self.options = {}  # workload options from the command line (e.g. sequential_calls)
        self.ari = None  # an AriClient when the workload asked for ARI
        self.banner = self._read_banner()

    def _recv(self, timeout: float) -> bool:
        self.sock.settimeout(max(0.01, timeout))
        try:
            chunk = self.sock.recv(65536)
        except socket.timeout:
            return False
        if not chunk:
            raise ConnectionClosed("the PBX closed the manager connection")
        self.transcript += chunk
        self.buf += chunk
        return True

    def _read_banner(self) -> str:
        deadline = self.clock() + 15
        while b"\r\n" not in self.buf:
            if self.clock() > deadline:
                raise CaptureError("no AMI banner within 15 s")
            self._recv(deadline - self.clock())
        line, self.buf = self.buf.split(b"\r\n", 1)
        return line.decode("latin-1")

    def _parse(self):
        while True:
            j = self.buf.find(b"\r\n\r\n")
            if j < 0:
                return
            self.frames.append(headers(self.buf[: j + 4]))
            self.buf = self.buf[j + 4:]

    def pump(self, seconds: float):
        deadline = self.clock() + seconds
        while self.clock() < deadline:
            self._recv(deadline - self.clock())
            self._parse()

    def send(self, fields) -> str:
        self.n += 1
        action_id = f"cap-{self.n}"
        payload = "".join(f"{k}: {v}\r\n" for k, v in list(fields) + [("ActionID", action_id)])
        self.sock.sendall((payload + "\r\n").encode("latin-1"))
        return action_id

    def wait_for(self, predicate, timeout: float, start: int = 0, what: str = "a frame"):
        """Return the first frame from index `start` on that satisfies `predicate`."""
        deadline = self.clock() + timeout
        index = start
        while True:
            self._parse()
            while index < len(self.frames):
                if predicate(self.frames[index]):
                    return self.frames[index]
                index += 1
            if self.clock() > deadline:
                raise CaptureError(f"timed out after {timeout:.0f} s waiting for {what}")
            self._recv(deadline - self.clock())

    def count(self, predicate, start: int = 0) -> int:
        self._parse()
        return sum(1 for f in self.frames[start:] if predicate(f))

    def wait_count(self, predicate, n: int, timeout: float, start: int = 0, what: str = "frames"):
        deadline = self.clock() + timeout
        while self.count(predicate, start) < n:
            if self.clock() > deadline:
                raise CaptureError(f"timed out after {timeout:.0f} s waiting for {n} {what}")
            self._recv(deadline - self.clock())

    def action(self, fields, timeout: float = 10.0):
        """Send an action and wait for its Response frame; return it."""
        mark = len(self.frames)
        action_id = self.send(fields)
        return self.wait_for(lambda f: f.get("ActionID") == action_id and "Response" in f,
                             timeout, mark, f"the response to {dict(fields).get('Action')}")

    def list_action(self, fields, timeout: float = 10.0):
        """Send a list action and wait until Asterisk ends it (EventList: Complete, or an Error)."""
        mark = len(self.frames)
        action_id = self.send(fields)

        def ended(f):
            return f.get("ActionID") == action_id and (
                f.get("Response") == "Error" or f.get("EventList") == "Complete")

        return self.wait_for(ended, timeout, mark, f"the end of {dict(fields).get('Action')}")

    def login(self, user: str, secret: str):
        response = self.action([("Action", "Login"), ("Username", user), ("Secret", secret),
                                ("Events", "on")])
        if response.get("Response") != "Success":
            raise CaptureError(f"AMI login refused: {response.get('Message')}")

    def marker(self, step: str):
        self.action([("Action", "UserEvent"), ("UserEvent", "CaptureMarker"), ("Step", step)])

    def logoff(self):
        try:
            self.send([("Action", "Logoff")])
            self.pump(0.5)
        except CaptureError:
            pass  # Logoff closes the connection


# --- Workloads ----------------------------------------------------------------------------

_MANAGER_CONF = """[general]
enabled = yes
bindaddr = 0.0.0.0
port = 5038
timestampevents = {timestamps}

[{user}]
secret = {secret}
read = all
write = all
deny = 0.0.0.0/0.0.0.0
permit = 0.0.0.0/0.0.0.0
"""

_BRIDGES_EXTENSIONS = """[general]
static = yes
writeprotect = no

[capture]
; a Local call that is answered, talks 2 s and is hung up by the PBX
exten => talk,1,Answer()
 same => n,Wait(2)
 same => n,Hangup()
; the calling side of a two-party bridge: dials the agent, who answers
exten => caller,1,Dial(Local/agent@capture/n,10)
 same => n,Hangup()
exten => agent,1,Answer()
 same => n,Wait(6)
 same => n,Hangup()
; a conference participant: joins the room and is hung up 6 s later (TIMEOUT(absolute))
exten => conf,1,Answer()
 same => n,Set(TIMEOUT(absolute)=6)
 same => n,ConfBridge(capture-room)
 same => n,Hangup()
"""

_BRIDGES_CONFBRIDGE = """[general]

[default_bridge]
type = bridge

[default_user]
type = user
quiet = yes
announce_join_leave = no
announce_user_count = no
"""


def _is(event, **match):
    def predicate(f):
        return f.get("Event") == event and all(f.get(k, "").startswith(v) for k, v in match.items())
    return predicate


def _bridges_and_conference(tap: Tap):
    """A Local call; a two-party bridge with BridgeList, BridgeInfo and Status while it is up; a
    two-participant ConfBridge with ConfbridgeListRooms, ConfbridgeList and BridgeList while it is
    up; then the empty listings once everything ended."""
    steps = []

    def step(name):
        steps.append(name)
        tap.marker(name)
        return len(tap.frames)

    mark = step("local-call")
    tap.send([("Action", "Originate"), ("Channel", "Local/talk@capture/n"),
              ("Application", "Wait"), ("Data", "4"), ("CallerID", "Capture <7000>"),
              ("Async", "true")])
    tap.wait_count(_is("Hangup", Channel="Local/talk@capture"), 2, 20, mark, "Local talk hangups")
    tap.pump(1.0)

    mark = step("bridge")
    tap.send([("Action", "Originate"), ("Channel", "Local/caller@capture/n"),
              ("Application", "Wait"), ("Data", "12"), ("CallerID", "Capture <7001>"),
              ("Async", "true")])
    tap.wait_count(_is("BridgeEnter"), 2, 20, mark, "BridgeEnter frames")
    tap.pump(0.5)
    step("bridge-list")
    tap.list_action([("Action", "BridgeList")])
    enter = tap.wait_for(_is("BridgeEnter"), 1, mark, "a BridgeEnter")
    tap.list_action([("Action", "BridgeInfo"), ("BridgeUniqueid", enter["BridgeUniqueid"])])
    tap.list_action([("Action", "Status")])
    tap.wait_for(_is("BridgeDestroy"), 20, mark, "the BridgeDestroy")
    tap.wait_count(_is("Hangup"), 4, 20, mark, "the bridge's four hangups")
    tap.pump(1.0)

    mark = step("confbridge")
    for cid in ("7002", "7003"):
        tap.send([("Action", "Originate"), ("Channel", "Local/conf@capture/n"),
                  ("Application", "Wait"), ("Data", "12"), ("CallerID", f"Capture <{cid}>"),
                  ("Async", "true")])
        tap.wait_for(lambda f, c=cid: f.get("Event") == "ConfbridgeJoin" and f.get("CallerIDNum") == c,
                     20, mark, f"the ConfbridgeJoin of {cid}")
    tap.pump(0.5)
    step("confbridge-list")
    tap.list_action([("Action", "ConfbridgeListRooms")])
    tap.list_action([("Action", "ConfbridgeList"), ("Conference", "capture-room")])
    tap.list_action([("Action", "BridgeList")])
    tap.wait_for(_is("ConfbridgeEnd"), 20, mark, "the ConfbridgeEnd")
    tap.wait_count(_is("Hangup"), 4, 20, mark, "the participants' four hangups")
    tap.pump(1.0)

    step("empty-lists")
    tap.list_action([("Action", "ConfbridgeListRooms")])
    tap.list_action([("Action", "BridgeList")])
    tap.pump(0.5)
    step("done")
    return steps


_PARKING_EXTENSIONS = """[general]
static = yes
writeprotect = no

[capture]
; the parking spaces (701-720) are extensions of the lot's context
include => parkedcalls
; a caller ID change in mid-call, then a jump into another context
exten => callerid,1,Answer()
 same => n,Wait(1)
 same => n,Set(CALLERID(num)=7105)
 same => n,Set(CALLERID(name)=Capture Changed)
 same => n,Goto(capture-hop,hop,1)
; a call that waits until the tap redirects it to `redirected`
exten => hold,1,Answer()
 same => n,Wait(8)
 same => n,Hangup()
exten => redirected,1,NoOp(redirected)
 same => n,Wait(1)
 same => n,Hangup()
; parks itself in the default lot, silently: picked up, or given up by its caller
exten => park,1,Answer()
 same => n,Park(default,s)
 same => n,Hangup()
; the same, with a 3 s parking timeout instead of the lot's 20 s
exten => park-short,1,Answer()
 same => n,Park(default,st(3))
 same => n,Hangup()

[capture-hop]
exten => hop,1,NoOp(hop)
 same => n,Wait(1)
 same => n,Hangup()

[capture-flash]
; the answering leg of the PJSIP loopback call, up long enough for both flashes to land
exten => flash-in,1,Answer()
 same => n,Wait(8)
 same => n,Hangup()

[capture-parked-timeout]
; where a timed-out parkee goes (comebacktoorigin = no): the parker's flattened name, else s
exten => s,1,Hangup()
exten => _[A-Za-z0-9].,1,Hangup()
"""

_PARKING_RES_PARKING = """[general]

[default]
parkext = 700
parkpos = 701-720
context = parkedcalls
parkingtime = 20
comebacktoorigin = no
comebackcontext = capture-parked-timeout
"""

# A PJSIP channel without leaving the container: the endpoint's contact is the PBX itself, and the
# INVITE that comes back in is identified by its source address and answered in `capture-flash`.
_PARKING_PJSIP = """[transport-loopback]
type = transport
protocol = udp
bind = 127.0.0.1:5060

[flash]
type = aor
contact = sip:flash-in@127.0.0.1:5060

[flash]
type = endpoint
transport = transport-loopback
context = capture-flash
disallow = all
allow = ulaw
aors = flash
direct_media = no

[flash]
type = identify
endpoint = flash
match = 127.0.0.1
"""


def _dialplan_and_parking(tap: Tap):
    """A mid-call caller ID change and a context jump (NewCallerid, Newexten); a Redirect of a
    waiting channel; a call parked with Park(), listed with ParkedCalls and Parkinglots and picked
    up by dialling its space; one whose caller gives up while it is parked; one that times out; the
    SendFlash help text, then SendFlash with Receive, without it, and to a channel that does not
    exist, on a PJSIP loopback call; and the empty parked-call listing.

    Both SendFlash forms answer the same Success, because AMI ignores a header it does not know, so
    each form gets its own step and waits for its Flash event: the channel it lands on tells whether
    Receive was applied (the target leg itself) or ignored (the far leg). Any channel is accepted,
    so a build that ignores Receive still finishes the capture. The first flash waits until the far
    leg has left Answer(): on 22.11.0 a Flash it received during Answer() was reported a second
    time afterwards, in the next step. Each form then settles for a second so a late Flash still
    falls inside its own step."""
    steps = []

    def step(name):
        steps.append(name)
        tap.marker(name)
        return len(tap.frames)

    def originate(channel, wait, cid):
        tap.send([("Action", "Originate"), ("Channel", channel), ("Application", "Wait"),
                  ("Data", str(wait)), ("CallerID", f"Capture <{cid}>"), ("Async", "true")])

    mark = step("dialplan")
    originate("Local/callerid@capture/n", 8, "7100")
    tap.wait_for(_is("NewCallerid", Channel="Local/callerid@capture", CallerIDNum="7105"), 20, mark,
                 "the NewCallerid of 7105")
    tap.wait_count(_is("Hangup", Channel="Local/callerid@capture"), 2, 20, mark, "caller ID hangups")
    tap.pump(1.0)

    mark = step("redirect")
    originate("Local/hold@capture/n", 12, "7101")
    held = tap.wait_for(lambda f: f.get("Event") == "Newexten" and f.get("Exten") == "hold"
                        and f.get("Application") == "Wait", 20, mark, "the hold extension's Wait")
    tap.action([("Action", "Redirect"), ("Channel", held["Channel"]), ("Context", "capture"),
                ("Exten", "redirected"), ("Priority", "1")])
    tap.wait_for(_is("Newexten", Channel=held["Channel"], Exten="redirected"), 20, mark,
                 "the Newexten of the redirect")
    tap.wait_count(_is("Hangup", Channel="Local/hold@capture"), 2, 20, mark, "the held call's hangups")
    tap.pump(1.0)

    mark = step("park")
    originate("Local/park@capture/n", 12, "7102")
    parked = tap.wait_for(_is("ParkedCall", ParkeeChannel="Local/park@capture"), 20, mark,
                          "the ParkedCall")
    space = parked["ParkingSpace"]
    tap.pump(0.5)
    step("parked-list")
    tap.list_action([("Action", "ParkedCalls")])
    tap.list_action([("Action", "Parkinglots")])
    mark = step("unpark")
    originate(f"Local/{space}@capture/n", 2, "7103")
    tap.wait_for(lambda f: f.get("Event") == "UnParkedCall" and f.get("ParkingSpace") == space, 20, mark,
                 f"the UnParkedCall of space {space}")
    tap.wait_count(_is("Hangup", Channel="Local/park@capture"), 2, 20, mark, "the parkee's hangups")
    tap.wait_count(_is("Hangup", Channel=f"Local/{space}@capture"), 2, 20, mark, "the retriever's hangups")
    tap.pump(1.0)

    mark = step("giveup")
    originate("Local/park@capture/n", 2, "7104")
    tap.wait_for(_is("ParkedCallGiveUp", ParkeeChannel="Local/park@capture"), 20, mark,
                 "the ParkedCallGiveUp")
    tap.wait_count(_is("Hangup", Channel="Local/park@capture"), 2, 20, mark, "the given-up call's hangups")
    tap.pump(1.0)

    mark = step("timeout")
    originate("Local/park-short@capture/n", 15, "7106")
    tap.wait_for(_is("ParkedCallTimeOut", ParkeeChannel="Local/park-short@capture"), 20, mark,
                 "the ParkedCallTimeOut")
    tap.wait_count(_is("Hangup", Channel="Local/park-short@capture"), 2, 20, mark,
                   "the timed-out call's hangups")
    tap.pump(1.0)

    step("send-flash-help")
    tap.action([("Action", "Command"), ("Command", "manager show command SendFlash")])

    def is_flash(f):
        return f.get("Event") == "Flash"

    call = mark = step("send-flash-receive")
    originate("PJSIP/flash", 10, "7107")
    tap.wait_count(_is("Newstate", Channel="PJSIP/flash-", ChannelStateDesc="Up"), 2, 20, mark,
                   "both PJSIP legs up")
    outbound = tap.wait_for(_is("Newchannel", Channel="PJSIP/flash-"), 1, mark, "the PJSIP Newchannel")
    tap.wait_for(_is("Newexten", Channel="PJSIP/flash-", Exten="flash-in", Application="Wait"), 20, mark,
                 "the far leg's Wait")
    tap.action([("Action", "SendFlash"), ("Channel", outbound["Channel"]), ("Receive", "true")])
    tap.wait_for(is_flash, 5, mark, "a Flash event")
    tap.pump(1.0)
    mark = step("send-flash-plain")
    tap.action([("Action", "SendFlash"), ("Channel", outbound["Channel"])])
    tap.wait_for(is_flash, 5, mark, "a Flash event")
    tap.pump(1.0)
    step("send-flash-missing")
    tap.action([("Action", "SendFlash"), ("Channel", "PJSIP/capture-no-such-channel")])
    tap.pump(0.5)
    tap.wait_count(_is("Hangup", Channel="PJSIP/flash-"), 2, 20, call, "the PJSIP legs' hangups")
    tap.pump(1.0)

    step("parked-empty")
    tap.list_action([("Action", "ParkedCalls")])
    tap.pump(0.5)
    step("done")
    return steps


_RENAMED_EXTENSIONS = """[general]
static = yes
writeprotect = no

[capture]
; a queue caller: waits in capture-q (no members) until the tap ends it; 60 s is a safety net only
exten => queue,1,Answer()
 same => n,Set(TIMEOUT(absolute)=60)
 same => n,Queue(capture-q)
 same => n,Hangup()
; a conference participant: stays in capture-room until the tap ends it
exten => conf,1,Answer()
 same => n,Set(TIMEOUT(absolute)=60)
 same => n,ConfBridge(capture-room)
 same => n,Hangup()
; a channel that only waits: Bridge's second channel, and the far leg of `dialpeer`
exten => peer,1,Answer()
 same => n,Wait(30)
 same => n,Hangup()
; the near leg of a Local pair without /n: dials a peer, so that leg is bridged
exten => dialpeer,1,Dial(Local/peer@capture/n,10)
 same => n,Hangup()
; a channel that sends audio (a 1 kHz tone) for as long as it lives, and the legs that dial it, so
; voice frames cross the Local pair in between: `dialtone` is the near leg of a waiting caller or
; participant, `lopt` the far leg of the control pair outside both surfaces
exten => tone,1,Answer()
 same => n,Milliwatt()
 same => n,Hangup()
exten => dialtone,1,Dial(Local/tone@capture/n,30)
 same => n,Hangup()
exten => lopt,1,Dial(Local/tone@capture/n,30)
 same => n,Hangup()
; the transferer's leg of a SIP attended transfer: RT holds the Refer-To, with the Replaces of
; the waiting leg's dialog
exten => refer-go,1,Transfer(PJSIP/${RT})
 same => n,UserEvent(CaptureApp,App: Transfer,Result: ${TRANSFERSTATUS})
 same => n,Wait(5)
 same => n,Hangup()
; where Redirect sends a waiting caller or a participant
exten => redirected,1,Answer()
 same => n,Wait(30)
 same => n,Hangup()
; a channel that rings and never answers, and the call that dials it (the positive control's target)
exten => ring,1,Ringing()
 same => n,Wait(30)
 same => n,Hangup()
exten => dialring,1,Dial(Local/ring@capture/n,30)
 same => n,Hangup()
; PickupChan and Pickup against TARGET; the UserEvent fires only when the app came back without a pickup
exten => pickchan,1,PickupChan(${TARGET})
 same => n,UserEvent(CaptureApp,App: PickupChan,Result: returned)
 same => n,Hangup()
exten => pickup,1,Pickup(${TARGET})
 same => n,UserEvent(CaptureApp,App: Pickup,Result: returned)
 same => n,Hangup()
; an INVITE on the PJSIP loopback whose Replaces header (RH) names an existing dialog
exten => replace,1,Dial(PJSIP/replace-in@loop,10,b(capture-replaces^s^1))
 same => n,Hangup()
; the sequential calls for the inverted-leave search: one second in the queue or the room, then gone
exten => queue-seq,1,Answer()
 same => n,Set(TIMEOUT(absolute)=1)
 same => n,Queue(capture-q)
 same => n,Hangup()
exten => conf-seq,1,Answer()
 same => n,Set(TIMEOUT(absolute)=1)
 same => n,ConfBridge(capture-room)
 same => n,Hangup()

[capture-replaces]
exten => s,1,Set(PJSIP_HEADER(add,Replaces)=${RH})
 same => n,Return()

[capture-sip]
; the answering legs of the PJSIP loopback calls
exten => queue-in,1,Answer()
 same => n,Set(TIMEOUT(absolute)=60)
 same => n,Queue(capture-q)
 same => n,Hangup()
exten => conf-in,1,Answer()
 same => n,Set(TIMEOUT(absolute)=60)
 same => n,ConfBridge(capture-room)
 same => n,Hangup()
; the transferee's call: its answering leg (the leg the REFER arrives on) is bridged with a peer
exten => hold-in,1,Answer()
 same => n,Dial(Local/peer@capture/n,30)
 same => n,Hangup()
; reached only when an INVITE with Replaces was not taken as a replacement
exten => replace-in,1,Answer()
 same => n,UserEvent(CaptureApp,App: Replaces,Result: not-replaced)
 same => n,Wait(10)
 same => n,Hangup()
"""

_RENAMED_QUEUES = """[general]
persistentmembers = no

[capture-q]
strategy = ringall
joinempty = yes
leavewhenempty = no
announce-frequency = 0
periodic-announce-frequency = 0
announce-position = no
announce-holdtime = no
"""

# The PJSIP loopback of `dialplan-and-parking`, under its own endpoint name: every INVITE the PBX
# sends to 127.0.0.1 comes back in as `loop` and lands in `capture-sip`.
_RENAMED_PJSIP = """[transport-loopback]
type = transport
protocol = udp
bind = 127.0.0.1:5060

[loop]
type = aor
contact = sip:127.0.0.1:5060

[loop]
type = endpoint
transport = transport-loopback
context = capture-sip
disallow = all
allow = ulaw
aors = loop
direct_media = no

[loop]
type = identify
endpoint = loop
match = 127.0.0.1
"""

_HTTP_CONF = """[general]
enabled = yes
bindaddr = 0.0.0.0
bindport = {port}
"""

_ARI_CONF = """[general]
enabled = yes

[{user}]
type = user
read_only = no
password = {secret}
password_format = plain
"""

ARI_PORT = 8088
ARI_USER = "capture"
ARI_APP = "capture-ari"

# Frame types the renamed-caller workload must see; the tap sets no event filter and the reduction
# never drops them by default.
RENAME_EVENTS = ("Rename", "Masquerade", "LocalOptimizationBegin", "LocalOptimizationEnd")

RENAME_TRIGGERS = ("bridge", "pickupchan", "pickup", "local-optimization", "replaces", "refer", "ari-move",
                   "redirect")
RENAME_SURFACES = ("queue", "conference")


class AriClient:
    """The few ARI requests the workload sends, with HTTP basic auth; an HTTP error is a result."""

    def __init__(self, base_url: str, user: str, secret: str, opener=None):
        self.base_url = base_url.rstrip("/")
        self._auth = "Basic " + base64.b64encode(f"{user}:{secret}".encode()).decode()
        self._opener = opener or urllib.request.urlopen

    def post(self, path: str, query=()):
        url = self.base_url + path + ("?" + urllib.parse.urlencode(list(query)) if query else "")
        request = urllib.request.Request(url, data=b"", method="POST",
                                         headers={"Authorization": self._auth})
        try:
            with self._opener(request, timeout=10) as response:
                return response.status, response.read().decode("utf-8", "replace")
        except urllib.error.HTTPError as error:
            return error.code, error.read().decode("utf-8", "replace")
        except OSError as error:
            return 0, str(error)


def is_sentinel_uniqueid(uid) -> bool:
    """An empty Uniqueid, or a placeholder such as the `<unknown>` of a failed OriginateResponse:
    it names no channel, so two frames that carry it are not one channel under two names."""
    return not uid or (uid.startswith("<") and uid.endswith(">"))


def channel_names_by_uniqueid(frames):
    """Every (Uniqueid -> [distinct names, in order]) the frames show.

    A name is read from each `<prefix>Channel` header whose `<prefix>Uniqueid` sits in the same
    frame (`Channel`/`Uniqueid`, `DestChannel`/`DestUniqueid`, ...), and from a `Rename` frame's
    `Channel` then `Newname`. List rows count like events. A sentinel Uniqueid
    (`is_sentinel_uniqueid`) is never read.
    """
    names = collections.OrderedDict()

    def see(uid, name):
        if not is_sentinel_uniqueid(uid) and name:
            seen = names.setdefault(uid, [])
            if name not in seen:
                seen.append(name)

    for f in frames:
        for key, value in f.items():
            if key.endswith("Channel"):
                see(f.get(key[: -len("Channel")] + "Uniqueid"), value)
        if f.get("Event") == "Rename":
            see(f.get("Uniqueid"), f.get("Newname"))
    return names


def renamed_uniqueids(frames):
    """The Uniqueids whose name changed in these frames, with or without a Rename frame."""
    return {uid: seen for uid, seen in channel_names_by_uniqueid(frames).items() if len(seen) > 1}


def _hangup_all(tap: Tap, timeout: float = 15.0):
    """Hang up every Local and PJSIP channel, then wait until CoreShowChannels lists none."""
    tap.action([("Action", "Hangup"), ("Channel", "/^(Local|PJSIP)\\//")])
    deadline = tap.clock() + timeout
    while True:
        end = tap.list_action([("Action", "CoreShowChannels")])
        if end.get("ListItems", "0") == "0" or tap.clock() > deadline:
            return end.get("ListItems", "0")
        tap.pump(0.5)


def _renamed_caller_and_participant(tap: Tap):
    """A queue caller and a ConfBridge participant that nobody renames (the negative control); a
    rename of a channel outside any queue or room (the positive control: Action Bridge between two
    waiting channels, and PickupChan of a ringing one); then each trigger of the search, in order,
    on a caller waiting in `capture-q` and on a participant of `capture-room`: Action Bridge,
    PickupChan, Pickup(), a Local pair without /n whose far leg is bridged, an INVITE with Replaces
    naming the waiting PJSIP loopback leg, and ARI move. While the target still waits, a QueueStatus
    or a ConfbridgeList and a ConfbridgeMute of each listed participant; then everything is hung up
    and the leave awaited. With `sequential_calls` = N, N queue calls and N participants one after
    another, each gone before the next joins.

    The tap sets no event filter, so `Rename`, `Masquerade` and `LocalOptimization*` reach it. A
    trigger that fails or times out is recorded, never fatal: the run is a search. Each trigger
    ends with `UserEvent: CaptureOutcome` (its surface, trigger, the waiting channel's name and id
    at its join, the refusals counted, the leaves and their Uniqueids, and the Uniqueids whose name
    changed within the step); the tap's bytes stay the evidence, the outcome line only indexes them."""
    steps = []
    counter = iter(range(7200, 7999))

    def step(name):
        steps.append(name)
        tap.marker(name)
        return len(tap.frames)

    def originate(channel, cid, app="Wait", data="30", variables=(), exten=None):
        fields = [("Action", "Originate"), ("Channel", channel)]
        if exten:
            fields += [("Context", "capture"), ("Exten", exten), ("Priority", "1")]
        else:
            fields += [("Application", app), ("Data", data)]
        fields += [("CallerID", f"Capture <{cid}>"), ("Async", "true")]
        fields += [("Variable", v) for v in variables]
        return tap.send(fields)

    def join_event(surface):
        return "QueueCallerJoin" if surface == "queue" else "ConfbridgeJoin"

    def leave_event(surface):
        return "QueueCallerLeave" if surface == "queue" else "ConfbridgeLeave"

    def start_target(surface, mark, how="local"):
        """Put a caller in the queue or a participant in the room; return its join frame."""
        cid = str(next(counter))
        exten = "queue" if surface == "queue" else "conf"
        if how == "pjsip":
            originate(f"PJSIP/{exten}-in@loop", cid)
        elif how == "local-no-n":  # the near leg dials a tone, so audio crosses the pair into the target
            originate(f"Local/{exten}@capture", cid, exten="dialtone")
        else:
            originate(f"Local/{exten}@capture/n", cid)
        return tap.wait_for(lambda f: f.get("Event") == join_event(surface) and f.get("CallerIDNum") == cid,
                            float(tap.options.get("join_timeout", 20)), mark, f"the {join_event(surface)} of {cid}")

    def refused(response):
        return 1 if response.get("Response") == "Error" else 0

    def while_waiting(surface, join):
        """The listing and the mute, sent while the target is (still) in the queue or the room."""
        if surface == "queue":
            tap.list_action([("Action", "QueueStatus"), ("Queue", "capture-q")])
            return
        mark = len(tap.frames)
        end = tap.list_action([("Action", "ConfbridgeList"), ("Conference", "capture-room")])
        rows = [f for f in tap.frames[mark:] if f.get("Event") == "ConfbridgeList"
                and f.get("ActionID") == end.get("ActionID")]
        for row in rows:
            tap.action([("Action", "ConfbridgeMute"), ("Conference", "capture-room"),
                        ("Channel", row.get("Channel", ""))])
        if not rows:  # nobody listed: mute the name the join gave, so the refusal is on the wire
            tap.action([("Action", "ConfbridgeMute"), ("Conference", "capture-room"),
                        ("Channel", join.get("Channel", ""))])

    def outcome(name, surface, trigger, mark, join, refusals, detail=""):
        frames = tap.frames[mark:]
        leaves = [f for f in frames if f.get("Event") == leave_event(surface)] if surface else []
        renamed = renamed_uniqueids(frames)
        fields = [("Action", "UserEvent"), ("UserEvent", "CaptureOutcome"), ("Step", name),
                  ("Surface", surface or "none"), ("Trigger", trigger),
                  ("TargetName", (join or {}).get("Channel", "")),
                  ("TargetId", (join or {}).get("Uniqueid", "")),
                  ("Refusals", str(refusals)), ("Leaves", str(len(leaves))),
                  ("LeaveIds", ",".join(f.get("Uniqueid", "") for f in leaves)),
                  ("RenamedIds", ",".join(renamed)),
                  ("RenameFrames", str(sum(1 for f in frames if f.get("Event") == "Rename"))),
                  ("Masquerades", str(sum(1 for f in frames if f.get("Event") == "Masquerade"))),
                  ("LocalOptimizations", str(sum(1 for f in frames
                                                 if f.get("Event") == "LocalOptimizationBegin")))]
        if detail:
            fields.append(("Detail", re.sub(r"[\r\n]+", " ", detail)[:200]))
        tap.action(fields)

    def finish(surface, mark):
        """Hang everything up and wait for the target's leave (once is enough: it may have left)."""
        tap.pump(0.5)
        _hangup_all(tap)
        if surface and not tap.count(_is(leave_event(surface)), mark):
            try:
                tap.wait_for(_is(leave_event(surface)), 5, mark, f"a {leave_event(surface)}")
            except ConnectionClosed:
                raise
            except CaptureError:
                pass
        tap.pump(0.5)

    def app_returned(mark, app):
        return tap.count(lambda f: f.get("Event") == "UserEvent" and f.get("UserEvent") == "CaptureApp"
                         and f.get("App") == app and f.get("Result") in ("returned", "not-replaced"), mark)

    # -- the trigger bodies: each returns (refusals, detail) ------------------------------------

    def do_bridge(target, mark):
        cid = str(next(counter))
        originate("Local/peer@capture/n", cid)
        peer = tap.wait_for(lambda f: f.get("Event") == "Newexten" and f.get("Exten") == "peer"
                            and f.get("Application") == "Wait", 20, mark, "the peer's Wait")
        response = tap.action([("Action", "Bridge"), ("Channel1", target["Channel"]),
                               ("Channel2", peer["Channel"]), ("Tone", "no")])
        tap.pump(3.0)
        return refused(response), f"Bridge: {response.get('Response')}: {response.get('Message', '')}"

    def do_pickup(app, value, mark):
        cid = str(next(counter))
        exten = "pickchan" if app == "PickupChan" else "pickup"
        originate(f"Local/{exten}@capture/n", cid, data="10", variables=[f"__TARGET={value}"])
        tap.pump(3.0)
        back = app_returned(mark, app)
        return back, f"{app}({value}): " + ("returned without a pickup" if back else "did not return")

    def do_local_optimization(surface, mark):
        tap.pump(3.0)  # the trigger is the shape itself, with a tone crossing the pair
        n = tap.count(_is("LocalOptimizationBegin"), mark)
        note = "; queue unreachable by construction (Queue() is not a bridge)" if surface == "queue" else ""
        return 0, f"LocalOptimizationBegin frames: {n}{note}"

    def dialog_of(target):
        """The waiting PJSIP leg's dialog as Replaces names it: (refusals, header or None)."""
        values = {}
        refusals = 0
        for key in ("call-id", "local_tag", "remote_tag"):
            response = tap.action([("Action", "Getvar"), ("Channel", target["Channel"]),
                                   ("Variable", f"CHANNEL(pjsip,{key})")])
            refusals += refused(response)
            values[key] = response.get("Value", "")
        if not all(values.values()):
            return refusals or 1, None
        return refusals, f"{values['call-id']};to-tag={values['local_tag']};from-tag={values['remote_tag']}"

    def do_replaces(target, mark):
        refusals, header = dialog_of(target)
        if header is None:
            return refusals, "the dialog of the waiting leg could not be read"
        originate("Local/replace@capture/n", str(next(counter)), data="20", variables=[f"__RH={header}"])
        tap.pump(4.0)
        back = app_returned(mark, "Replaces")
        return refusals + back, "INVITE with Replaces: " + ("not taken" if back else "no fallback seen")

    def do_refer(target, mark, surface):
        """A SIP attended transfer whose consultation leg is the waiting target: a second loopback
        call (`hold-in`, its answering leg bridged with a peer) is the transferer's first call; once
        that leg is bridged, its calling leg sends a REFER whose Refer-To carries the Replaces of
        the target's dialog."""
        refusals, header = dialog_of(target)
        if header is None:
            return refusals, "the dialog of the waiting leg could not be read"
        action_id = originate("PJSIP/hold-in@loop", str(next(counter)))
        answered = tap.wait_for(lambda f: f.get("Event") == "OriginateResponse"
                                and f.get("ActionID") == action_id, 20, mark, "the transferer's call")
        if answered.get("Response") != "Success":
            return refusals + 1, f"the transferer's call was not answered: {answered.get('Reason', '')}"
        leg = answered["Channel"]
        # the answer comes before `hold-in` dials its peer; a REFER sent then finds no bridge on the
        # transferee's side and Asterisk reports the transfer `Invalid`, so wait for that bridge
        tap.wait_for(lambda f: f.get("Event") == "BridgeEnter" and f.get("Context") == "capture-sip"
                     and f.get("Exten") == "hold-in", float(tap.options.get("join_timeout", 20)), mark,
                     "the transferer's bridge")
        exten = "queue-in" if surface == "queue" else "conf-in"
        refer_to = f"<sip:{exten}@127.0.0.1:5060?Replaces={urllib.parse.quote(header, safe='')}>"
        refusals += refused(tap.action([("Action", "Setvar"), ("Channel", leg), ("Variable", "RT"),
                                        ("Value", refer_to)]))
        response = tap.action([("Action", "Redirect"), ("Channel", leg), ("Context", "capture"),
                               ("Exten", "refer-go"), ("Priority", "1")])
        refusals += refused(response)
        tap.pump(4.0)
        transferred = tap.count(_is("AttendedTransfer"), mark)
        status = [f.get("Result") for f in tap.frames[mark:] if f.get("Event") == "UserEvent"
                  and f.get("UserEvent") == "CaptureApp" and f.get("App") == "Transfer"]
        if not transferred or (status and status[-1] != "SUCCESS"):
            refusals += 1
        return refusals, (f"Transfer: {status[-1] if status else 'no result'}; "
                          f"AttendedTransfer frames: {transferred}")

    def do_redirect(target, _mark):
        response = tap.action([("Action", "Redirect"), ("Channel", target["Channel"]), ("Context", "capture"),
                               ("Exten", "redirected"), ("Priority", "1")])
        tap.pump(2.0)
        return refused(response), f"Redirect: {response.get('Response')}: {response.get('Message', '')}"

    def do_ari_move(target, _mark):
        if tap.ari is None:
            return 1, "ARI unavailable"
        status, body = tap.ari.post(f"/ari/channels/{target['Uniqueid']}/move", [("app", ARI_APP)])
        tap.pump(2.0)
        return (0 if 200 <= status < 300 else 1), f"ARI move: HTTP {status} {body.strip()[:120]}"

    def local_optimization_control():
        """A Local pair without /n outside both surfaces, bridged on both sides, with a tone
        crossing it: whether this image raises LocalOptimizationBegin for the shape at all."""
        name = "local-optimization-control"
        mark = step(name)
        try:
            originate("Local/lopt@capture", str(next(counter)), exten="dialpeer")
            tap.wait_for(lambda f: f.get("Event") == "Newexten" and f.get("Exten") == "tone"
                         and f.get("Application") == "Milliwatt", 20, mark, "the control pair's tone")
            refusals, detail = do_local_optimization(None, mark)
            finish(None, mark)
            outcome(name, None, "local-optimization", mark, None, refusals, detail)
        except ConnectionClosed:
            raise
        except CaptureError as error:
            _hangup_all(tap)
            outcome(name, None, "local-optimization", mark, None, 0, f"error: {error}")

    # -- negative control -------------------------------------------------------------------------

    for surface in RENAME_SURFACES:
        name = f"negative-{surface}"
        mark = step(name)
        join = None
        try:
            join = start_target(surface, mark)
            tap.pump(1.0)
            while_waiting(surface, join)
            finish(surface, mark)
            outcome(name, surface, "none", mark, join, 0)
        except ConnectionClosed:
            raise
        except CaptureError as error:
            _hangup_all(tap)
            outcome(name, surface, "none", mark, join, 0, f"error: {error}")

    # -- positive control: a rename outside any queue or conference -------------------------------

    name = "positive-bridge"
    mark = step(name)
    try:
        originate("Local/peer@capture/n", str(next(counter)))
        first = tap.wait_for(lambda f: f.get("Event") == "Newexten" and f.get("Exten") == "peer"
                             and f.get("Application") == "Wait", 20, mark, "the first peer's Wait")
        second_mark = len(tap.frames)
        originate("Local/peer@capture/n", str(next(counter)))
        second = tap.wait_for(lambda f: f.get("Event") == "Newexten" and f.get("Exten") == "peer"
                              and f.get("Application") == "Wait", 20, second_mark, "the second peer's Wait")
        response = tap.action([("Action", "Bridge"), ("Channel1", first["Channel"]),
                               ("Channel2", second["Channel"]), ("Tone", "no")])
        tap.pump(3.0)
        finish(None, mark)
        outcome(name, None, "bridge", mark, first, refused(response),
                f"Bridge: {response.get('Response')}: {response.get('Message', '')}")
    except ConnectionClosed:
        raise
    except CaptureError as error:
        _hangup_all(tap)
        outcome(name, None, "bridge", mark, None, 0, f"error: {error}")

    name = "positive-pickupchan"
    mark = step(name)
    try:
        originate("Local/dialring@capture/n", str(next(counter)))
        # the dialled leg (;1) rings and runs no dialplan, so it can be picked up; ;2 runs `ring`
        ringing = tap.wait_for(lambda f: f.get("Event") == "Newchannel"
                               and f.get("Channel", "").startswith("Local/ring@capture-")
                               and f.get("Channel", "").endswith(";1"), 20, mark, "the ringing channel")
        tap.wait_for(lambda f: f.get("Event") == "Newexten" and f.get("Exten") == "ring"
                     and f.get("Application") == "Wait", 20, mark, "the ringing leg's Wait")
        refusals, detail = do_pickup("PickupChan", ringing["Channel"], mark)
        finish(None, mark)
        outcome(name, None, "pickupchan", mark, ringing, refusals, detail)
    except ConnectionClosed:
        raise
    except CaptureError as error:
        _hangup_all(tap)
        outcome(name, None, "pickupchan", mark, None, 0, f"error: {error}")

    # -- the search -------------------------------------------------------------------------------

    for trigger in RENAME_TRIGGERS:
        if trigger == "local-optimization":
            local_optimization_control()
        for surface in RENAME_SURFACES:
            name = f"{trigger}-{surface}"
            mark = step(name)
            join = None
            try:
                how = {"local-optimization": "local-no-n", "replaces": "pjsip", "refer": "pjsip"}.get(trigger, "local")
                join = start_target(surface, mark, how)
                tap.pump(0.5)
                if trigger == "bridge":
                    refusals, detail = do_bridge(join, mark)
                elif trigger == "pickupchan":
                    refusals, detail = do_pickup("PickupChan", join["Channel"], mark)
                elif trigger == "pickup":
                    exten = "queue" if surface == "queue" else "conf"
                    refusals, detail = do_pickup("Pickup", f"{exten}@capture", mark)
                elif trigger == "local-optimization":
                    refusals, detail = do_local_optimization(surface, mark)
                elif trigger == "replaces":
                    refusals, detail = do_replaces(join, mark)
                elif trigger == "refer":
                    refusals, detail = do_refer(join, mark, surface)
                elif trigger == "redirect":
                    refusals, detail = do_redirect(join, mark)
                else:
                    refusals, detail = do_ari_move(join, mark)
                step(f"{name}-waiting")
                while_waiting(surface, join)
                finish(surface, mark)
                outcome(name, surface, trigger, mark, join, refusals, detail)
            except ConnectionClosed:
                raise
            except CaptureError as error:
                _hangup_all(tap)
                outcome(name, surface, trigger, mark, join, 0, f"error: {error}")

    # -- (e): sequential calls, behind --sequential-calls -----------------------------------------

    calls = int(tap.options.get("sequential_calls", 0) or 0)
    if calls:
        for surface in RENAME_SURFACES:
            mark = step(f"sequential-{surface}")
            exten = "queue-seq" if surface == "queue" else "conf-seq"
            for i in range(calls):
                cid = str(8000 + i)
                originate(f"Local/{exten}@capture/n", cid, data="5")
                tap.wait_for(lambda f, c=cid: f.get("Event") == leave_event(surface)
                             and f.get("CallerIDNum") == c, 20, mark, f"the leave of sequential call {cid}")
            _hangup_all(tap)
            tap.pump(0.5)

    step("done")
    return steps


WORKLOADS = {
    "bridges-and-conference": {
        "description": "Local call; two-party bridge + BridgeList/BridgeInfo/Status; two-participant "
                       "ConfBridge + ConfbridgeListRooms/ConfbridgeList/BridgeList; empty listings.",
        "files": {"extensions.conf": _BRIDGES_EXTENSIONS, "confbridge.conf": _BRIDGES_CONFBRIDGE},
        "run": _bridges_and_conference,
    },
    "dialplan-and-parking": {
        "description": "Mid-call CALLERID change + Goto (NewCallerid, Newexten); Redirect; Park() picked "
                       "up by its space, given up and timed out + ParkedCalls/Parkinglots; SendFlash "
                       "help, with Receive, without, and to a missing channel on a PJSIP loopback call.",
        "files": {"extensions.conf": _PARKING_EXTENSIONS, "res_parking.conf": _PARKING_RES_PARKING,
                  "pjsip.conf": _PARKING_PJSIP},
        "run": _dialplan_and_parking,
    },
    "renamed-caller-and-participant": {
        "description": "A queue caller and a ConfBridge participant nobody renames; a rename outside "
                       "both (Bridge, PickupChan); then Bridge, PickupChan, Pickup(), a Local pair "
                       "without /n, an INVITE with Replaces and ARI move on a waiting caller and on a "
                       "participant, each with QueueStatus or ConfbridgeList and ConfbridgeMute while "
                       "it waits; --sequential-calls N adds N calls per surface.",
        "files": {"extensions.conf": _RENAMED_EXTENSIONS, "queues.conf": _RENAMED_QUEUES,
                  "confbridge.conf": _BRIDGES_CONFBRIDGE, "pjsip.conf": _RENAMED_PJSIP},
        "ari": True,
        "run": _renamed_caller_and_participant,
    },
}


# --- Docker -------------------------------------------------------------------------------


def _docker(*args, check=True, timeout=120):
    result = subprocess.run(["docker", *args], capture_output=True, text=True, timeout=timeout)
    if check and result.returncode != 0:
        raise CaptureError(f"docker {args[0]} failed (rc={result.returncode}): {result.stderr.strip()}")
    return result


def parse_image(ref: str):
    match = _IMAGE_REF.match(ref)
    if not match:
        raise CaptureError(f"--image must be <repo>[:<tag>]@sha256:<64 hex>, got {ref!r}")
    return match.group("repo"), match.group("tag"), match.group("digest")


def resolve_image(ref: str):
    """Return (run_ref, image_id). The run reference is repo@digest; the id must be that digest."""
    repo, _tag, digest = parse_image(ref)
    run_ref = f"{repo}@{digest}"
    inspected = _docker("image", "inspect", run_ref, "--format",
                        "{{.Id}}|{{range .RepoDigests}}{{.}} {{end}}", check=False)
    if inspected.returncode != 0:
        raise CaptureError(f"image {run_ref} is not in the local store; pull it first "
                           f"(`docker pull {run_ref}`)")
    image_id, _, repo_digests = inspected.stdout.strip().partition("|")
    if image_id != digest and f"{repo}@{digest}" not in repo_digests.split():
        raise CaptureError(f"image {run_ref} resolves to {image_id}, not the pinned {digest}")
    return run_ref, image_id


def _check_version_matches_tag(tag, asterisk_version):
    if not tag:
        return
    version = asterisk_version.split()[-1]
    if not tag.startswith(version + "_") and tag != version and not tag.startswith(version + "-"):
        raise CaptureError(f"the tag {tag!r} does not name the running build ({asterisk_version!r})")


def capture(image: str, workload_name: str, out_dir: Path, raw_dir: Path, timestamps=True,
            drop_events=(), keep_steps=(), name_prefix="labc", options=None):
    workload = WORKLOADS.get(workload_name)
    if workload is None:
        raise CaptureError(f"unknown workload {workload_name!r}; see `workloads`")
    _repo, tag, _digest = parse_image(image)
    run_ref, image_id = resolve_image(image)
    secret = secrets.token_hex(12)
    container = f"{name_prefix}-{workload_name}-{os.getpid()}-{secrets.token_hex(3)}"
    conf_dir = Path(tempfile.mkdtemp(prefix="ami-capture-conf-"))
    files = dict(workload["files"])
    files["manager.conf"] = _MANAGER_CONF.format(
        user=MANAGER_USER, secret=secret, timestamps="yes" if timestamps else "no")
    ports = ["-p", f"127.0.0.1::{AMI_PORT}"]
    ari_secret = None
    if workload.get("ari"):  # ARI on its own ephemeral loopback port, with a password made for this run
        ari_secret = secrets.token_hex(12)
        files["http.conf"] = _HTTP_CONF.format(port=ARI_PORT)
        files["ari.conf"] = _ARI_CONF.format(user=ARI_USER, secret=ari_secret)
        ports += ["-p", f"127.0.0.1::{ARI_PORT}"]
    mounts = []
    for filename, text in files.items():
        path = conf_dir / filename
        path.write_text(text, encoding="ascii")
        path.chmod(0o644)
        mounts += ["-v", f"{path}:/etc/asterisk/{filename}:ro"]
    started = False
    try:
        _docker("run", "-d", "--name", container, *ports, *mounts, run_ref)
        started = True
        booted = _docker("exec", container, "asterisk", "-rx", "core waitfullybooted",
                         check=False, timeout=90)
        deadline = time.monotonic() + 60
        while booted.returncode != 0 and time.monotonic() < deadline:
            time.sleep(1)
            booted = _docker("exec", container, "asterisk", "-rx", "core waitfullybooted",
                             check=False, timeout=90)
        if booted.returncode != 0:
            raise CaptureError(f"Asterisk did not boot: {booted.stderr.strip()}")
        asterisk_version = _docker("exec", container, "asterisk", "-V").stdout.strip()
        _check_version_matches_tag(tag, asterisk_version)
        running_image = _docker("inspect", "-f", "{{.Image}}", container).stdout.strip()
        if running_image != image_id:
            raise CaptureError(f"the container runs {running_image}, not {image_id}")
        port = _docker("port", container, f"{AMI_PORT}/tcp").stdout.strip().splitlines()[0]
        host, _, port_number = port.rpartition(":")
        sock = socket.create_connection((host.strip("[]"), int(port_number)), timeout=15)
        try:
            tap = Tap(sock)
            tap.options = dict(options or {})
            if ari_secret:
                ari = _docker("port", container, f"{ARI_PORT}/tcp").stdout.strip().splitlines()[0]
                ari_host, _, ari_port = ari.rpartition(":")
                tap.ari = AriClient(f"http://{ari_host.strip('[]')}:{ari_port}", ARI_USER, ari_secret)
            tap.login(MANAGER_USER, secret)
            tap.action([("Action", "UserEvent"), ("UserEvent", "CaptureProvenance"),
                        ("AsteriskVersion", asterisk_version), ("Image", image),
                        ("Workload", workload_name)])
            steps = workload["run"](tap)
            tap.logoff()
        finally:
            sock.close()
        tap_bytes = bytes(tap.transcript)
    finally:
        if started:
            _docker("rm", "-f", container, check=False)
        for path in conf_dir.iterdir():
            path.unlink()
        conf_dir.rmdir()

    captured = _dt.datetime.now(_dt.timezone.utc).strftime("%Y-%m-%dT%H:%M:%SZ")
    meta = {
        "workload": workload_name,
        "image": image,
        "image_id": image_id,
        "asterisk_version": asterisk_version,
        "ami_banner": tap.banner,
        "captured_utc": captured,
        "driver_sha256": hashlib.sha256(Path(__file__).read_bytes()).hexdigest(),
        "steps": steps,
        "tap_bytes": len(tap_bytes),
        "tap_sha256": hashlib.sha256(tap_bytes).hexdigest(),
    }
    raw_dir.mkdir(parents=True, exist_ok=True)
    stem = recording_name(workload_name, asterisk_version)
    tap_path = raw_dir / f"{stem}.tap.raw"
    tap_path.write_bytes(tap_bytes)
    (raw_dir / f"{stem}.tap.json").write_text(json.dumps(meta, indent=2) + "\n", encoding="utf-8")
    written = write_recording(tap_bytes, meta, out_dir, drop_events, keep_steps,
                              forbidden=[v for v in (secret, ari_secret) if v])
    return tap_path, written


# --- CLI ----------------------------------------------------------------------------------


def _csv(value: str):
    return tuple(v for v in (s.strip() for s in value.split(",")) if v) if value else ()


def main(argv=None):
    parser = argparse.ArgumentParser(description=__doc__.split("\n\n")[0])
    sub = parser.add_subparsers(dest="command", required=True)

    cap = sub.add_parser("capture", help="run a workload on an image and write the recording")
    cap.add_argument("--image", action="append", required=True,
                     help="<repo>[:<tag>]@sha256:<digest>; repeat for several images")
    cap.add_argument("--workload", required=True, choices=sorted(WORKLOADS))
    cap.add_argument("--out", type=Path, default=DEFAULT_OUT)
    cap.add_argument("--raw-dir", type=Path, default=None,
                     help="where the unredacted tap is kept (default: a new temporary directory)")
    cap.add_argument("--no-timestamps", action="store_true",
                     help="timestampevents = no (default: yes, every event carries Timestamp)")
    cap.add_argument("--drop-events", default="")
    cap.add_argument("--keep-steps", default="")
    cap.add_argument("--sequential-calls", type=int, default=0,
                     help="renamed-caller-and-participant only: N sequential calls per surface (default 0)")

    red = sub.add_parser("reduce", help="reduce a kept tap again (deterministic)")
    red.add_argument("tap", type=Path, help="<name>.tap.raw; its <name>.tap.json must be beside it")
    red.add_argument("--out", type=Path, default=DEFAULT_OUT)
    red.add_argument("--drop-events", default="")
    red.add_argument("--keep-steps", default="")

    sub.add_parser("workloads", help="list the workloads")

    args = parser.parse_args(argv)
    try:
        if args.command == "workloads":
            for name, w in sorted(WORKLOADS.items()):
                print(f"{name}: {w['description']}")
            return 0
        if args.command == "reduce":
            meta_path = args.tap.with_name(args.tap.name[: -len(".raw")] + ".json") \
                if args.tap.name.endswith(".tap.raw") else None
            if meta_path is None or not meta_path.exists():
                raise CaptureError("reduce needs <name>.tap.raw with <name>.tap.json beside it")
            meta = json.loads(meta_path.read_text(encoding="utf-8"))
            raw, side, report, size = write_recording(
                args.tap.read_bytes(), meta, args.out, _csv(args.drop_events), _csv(args.keep_steps))
            print(f"wrote {raw.name} ({size} bytes, {report['frames_kept']}/{report['frames_in']} "
                  f"frames) and {side.name} in {args.out}")
            return 0
        raw_dir = args.raw_dir or Path(tempfile.mkdtemp(prefix="ami-capture-tap-"))
        for image in args.image:
            tap_path, (raw, side, report, size) = capture(
                image, args.workload, args.out, raw_dir, not args.no_timestamps,
                _csv(args.drop_events), _csv(args.keep_steps),
                options={"sequential_calls": args.sequential_calls})
            print(f"{image}: tap {tap_path} ({tap_path.stat().st_size} bytes, unredacted -- keep it "
                  f"out of the repository)")
            print(f"  wrote {raw.name} ({size} bytes, {report['frames_kept']}/{report['frames_in']} "
                  f"frames) and {side.name} in {args.out}")
        return 0
    except CaptureError as exc:
        print(f"capture-ami-recording: {exc}", file=sys.stderr)
        return 1


if __name__ == "__main__":
    sys.exit(main())

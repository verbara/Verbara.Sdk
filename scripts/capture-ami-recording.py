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

A take under load (`--workload confbridge-exits-under-load`) is too large to reduce: `--tap-only`
streams the unredacted tap to `--raw-dir` as it arrives and writes no recording. That workload holds
a ladder of background load levels (`--load-levels 0,150,400`; 0 is the control) on a second
manager session with `Events: off`, so the measured connection carries none of the control traffic,
and announces each load sample (channels, the container's CPU time) as `UserEvent: CaptureLoad` into
the measured stream; `--cpus` pins the container's CPU share so a level is reproducible.

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
import queue
import re
import secrets
import socket
import subprocess
import sys
import tempfile
import threading
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

    def __init__(self, sock: socket.socket, clock=time.monotonic, sink=None):
        self.sock = sock
        self.clock = clock
        self.sink = sink  # a binary file: every byte goes there instead of `transcript`
        self.transcript = bytearray()
        self.bytes_in = 0
        self.buf = b""
        self.frames = []  # parsed header dicts, in arrival order
        self.observers = []  # callables given each parsed frame, in arrival order
        self.n = 0
        self.options = {}  # workload options from the command line (e.g. sequential_calls)
        self.ari = None  # an AriClient when the workload asked for ARI
        self.open_session = None  # capture(): opens another logged-in Tap on the same PBX
        self.read_cpu = None  # capture(): the container's cgroup CPU time, in microseconds
        self.banner = self._read_banner()

    def _recv(self, timeout: float) -> bool:
        self.sock.settimeout(max(0.01, timeout))
        try:
            chunk = self.sock.recv(65536)
        except socket.timeout:
            return False
        if not chunk:
            raise ConnectionClosed("the PBX closed the manager connection")
        self.bytes_in += len(chunk)
        if self.sink is not None:
            self.sink.write(chunk)
        else:
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
            frame = headers(self.buf[: j + 4])
            self.buf = self.buf[j + 4:]
            self.frames.append(frame)
            for observe in self.observers:
                observe(frame)

    def pump(self, seconds: float):
        deadline = self.clock() + seconds
        while self.clock() < deadline:
            self._recv(deadline - self.clock())
            self._parse()

    def send(self, fields, action_id=None) -> str:
        self.n += 1
        action_id = action_id or f"cap-{self.n}"
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

    def login(self, user: str, secret: str, events: str = "on"):
        response = self.action([("Action", "Login"), ("Username", user), ("Secret", secret),
                                ("Events", events)])
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
{user_extra}deny = 0.0.0.0/0.0.0.0
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


# --- queue-revisit: one call answered on two queue visits by two members ----------------------

_REVISIT_EXTENSIONS = """[general]
static = yes
writeprotect = no

[capture]
; where the first member's blind transfer sends the caller: the second queue
exten => second,1,Queue(q-second)
 same => n,Hangup()

[capture-sip]
; the answering leg of the caller's PJSIP loopback call: the caller the PBX sees, in the first queue
exten => revisit-in,1,Answer()
 same => n,Set(TIMEOUT(absolute)=90)
 same => n,Queue(q-first)
 same => n,Hangup()
; the member phones: each rings 1 s, answers and stays until it is hung up
exten => agent1,1,Ringing()
 same => n,Wait(1)
 same => n,Answer()
 same => n,Wait(60)
 same => n,Hangup()
exten => agent2,1,Ringing()
 same => n,Wait(1)
 same => n,Answer()
 same => n,Wait(60)
 same => n,Hangup()
"""

_REVISIT_QUEUES = """[general]
persistentmembers = no

[revisit](!)
strategy = ringall
timeout = 15
retry = 1
wrapuptime = 0
joinempty = yes
leavewhenempty = no
ringinuse = yes
announce-frequency = 0
periodic-announce-frequency = 0
announce-position = no
announce-holdtime = no

[q-first](revisit)
member => PJSIP/agent1

[q-second](revisit)
member => PJSIP/agent2
"""

# The PJSIP loopback of `renamed-caller-and-participant`, plus one endpoint per member: a member's
# INVITE goes to the PBX itself, comes back in as `loop` and is answered in `capture-sip` under the
# member's own extension, so app_queue reports the member as `PJSIP/agent1` or `PJSIP/agent2`.
_REVISIT_PJSIP = _RENAMED_PJSIP + """
[agent1]
type = aor
contact = sip:agent1@127.0.0.1:5060

[agent1]
type = endpoint
transport = transport-loopback
context = capture-sip
disallow = all
allow = ulaw
aors = agent1
direct_media = no

[agent2]
type = aor
contact = sip:agent2@127.0.0.1:5060

[agent2]
type = endpoint
transport = transport-loopback
context = capture-sip
disallow = all
allow = ulaw
aors = agent2
direct_media = no
"""


def _queue_revisit(tap: Tap):
    """N calls (`--sequential-calls`, default 1), each answered in `q-first` by `PJSIP/agent1`, moved
    by an AMI `BlindTransfer` of that member's leg into `q-second` and answered there by
    `PJSIP/agent2`; then N negative controls, each answered once in `q-first` and hung up. One step
    per call (`revisit-NN`, `once-NN`), one call at a time, each gone before the next starts.

    A call that fails or times out is recorded, never fatal. Each call ends with
    `UserEvent: CaptureOutcome` (the caller's id, the members app_queue reported connecting it, in
    order, and the error if any); the tap's bytes stay the evidence, the outcome line only indexes them."""
    steps = []
    calls = max(1, int(tap.options.get("sequential_calls", 0) or 0))
    timeout = float(tap.options.get("join_timeout", 20))

    def step(name):
        steps.append(name)
        tap.marker(name)
        return len(tap.frames)

    def connect_of(cid, queue, mark):
        return tap.wait_for(lambda f: f.get("Event") == "AgentConnect" and f.get("Queue") == queue
                            and f.get("CallerIDNum") == cid, timeout, mark, f"the AgentConnect of {cid} in {queue}")

    def outcome(name, mark, cid, detail=""):
        connects = [f for f in tap.frames[mark:] if f.get("Event") == "AgentConnect" and f.get("CallerIDNum") == cid]
        fields = [("Action", "UserEvent"), ("UserEvent", "CaptureOutcome"), ("Step", name),
                  ("CallerId", cid), ("Connects", str(len(connects))),
                  ("Members", ",".join(f.get("Interface", "") for f in connects)),
                  ("CallerIds", ",".join(f.get("Uniqueid", "") for f in connects))]
        if detail:
            fields.append(("Detail", re.sub(r"[\r\n]+", " ", detail)[:200]))
        tap.action(fields)

    def call(name, cid, transfer):
        mark = step(name)
        detail = ""
        try:
            tap.send([("Action", "Originate"), ("Channel", "PJSIP/revisit-in@loop"), ("Application", "Wait"),
                      ("Data", "60"), ("CallerID", f"Capture <{cid}>"), ("Async", "true")])
            first = connect_of(cid, "q-first", mark)
            tap.pump(2.0)
            if transfer:
                response = tap.action([("Action", "BlindTransfer"), ("Channel", first.get("DestChannel", "")),
                                       ("Context", "capture"), ("Exten", "second")])
                if response.get("Response") != "Success":
                    detail = f"BlindTransfer: {response.get('Response')}: {response.get('Message', '')}"
                else:
                    connect_of(cid, "q-second", mark)
                    tap.pump(2.0)
        except ConnectionClosed:
            raise
        except CaptureError as error:
            detail = f"error: {error}"
        _hangup_all(tap)
        tap.pump(1.0)
        outcome(name, mark, cid, detail)

    for i in range(1, calls + 1):
        call(f"revisit-{i:02d}", str(9100 + i), transfer=True)
    for i in range(1, calls + 1):
        call(f"once-{i:02d}", str(9200 + i), transfer=False)

    step("done")
    return steps


_RNA_EXTENSIONS = """[general]
static = yes
writeprotect = no

[capture-sip]
; the answering legs of the caller's PJSIP loopback calls: the callers the PBX sees, one queue each
exten => rna-in,1,Answer()
 same => n,Set(TIMEOUT(absolute)=90)
 same => n,Queue(q-rna)
 same => n,Hangup()
exten => busy-in,1,Answer()
 same => n,Set(TIMEOUT(absolute)=90)
 same => n,Queue(q-busy)
 same => n,Hangup()
exten => answer-in,1,Answer()
 same => n,Set(TIMEOUT(absolute)=90)
 same => n,Queue(q-answer)
 same => n,Hangup()
; the member phones: agent1 rings and never answers, agent2 rings 1 s and answers, agent3 is busy
exten => agent1,1,Ringing()
 same => n,Wait(60)
 same => n,Hangup()
exten => agent2,1,Ringing()
 same => n,Wait(1)
 same => n,Answer()
 same => n,Wait(60)
 same => n,Hangup()
exten => agent3,1,Busy(5)
"""

_RNA_QUEUES = """[general]
persistentmembers = no

[rna](!)
strategy = linear
timeout = 2
retry = 1
wrapuptime = 0
joinempty = yes
leavewhenempty = no
ringinuse = yes
autopause = no
announce-frequency = 0
periodic-announce-frequency = 0
announce-position = no
announce-holdtime = no

[q-rna](rna)
member => PJSIP/agent1
member => PJSIP/agent2

[q-busy](rna)
member => PJSIP/agent3
member => PJSIP/agent2

[q-answer](rna)
member => PJSIP/agent2
"""

# The queue-revisit loopback and its two members, plus `agent3`, the member that answers busy.
_RNA_PJSIP = _REVISIT_PJSIP + """
[agent3]
type = aor
contact = sip:agent3@127.0.0.1:5060

[agent3]
type = endpoint
transport = transport-loopback
context = capture-sip
disallow = all
allow = ulaw
aors = agent3
direct_media = no
"""


def _queue_ring_no_answer(tap: Tap):
    """N calls (`--sequential-calls`, default 1) of each kind, one at a time, each gone before the next starts:
    `rna-NN` waits in `q-rna`, where `PJSIP/agent1` rings for the member timeout and never answers, then
    `PJSIP/agent2` answers; `busy-NN` waits in `q-busy`, where `PJSIP/agent3` answers busy, then `PJSIP/agent2`
    answers; `answer-NN` waits in `q-answer`, where `PJSIP/agent2` answers at once (the negative control).

    A call that fails or times out is recorded, never fatal. Each call ends with `UserEvent: CaptureOutcome` (the
    caller's id, the members app_queue reported not answering and connecting, in order, and the error if any); the
    tap's bytes stay the evidence, the outcome line only indexes them."""
    steps = []
    calls = max(1, int(tap.options.get("sequential_calls", 0) or 0))
    timeout = float(tap.options.get("join_timeout", 20))

    def step(name):
        steps.append(name)
        tap.marker(name)
        return len(tap.frames)

    def of_caller(mark, event, cid):
        return [f for f in tap.frames[mark:] if f.get("Event") == event and f.get("CallerIDNum") == cid]

    def outcome(name, mark, cid, detail=""):
        missed = of_caller(mark, "AgentRingNoAnswer", cid)
        connects = of_caller(mark, "AgentConnect", cid)
        fields = [("Action", "UserEvent"), ("UserEvent", "CaptureOutcome"), ("Step", name),
                  ("CallerId", cid), ("RingNoAnswers", str(len(missed))),
                  ("Missed", ",".join(f.get("Interface", "") for f in missed)),
                  ("Connects", str(len(connects))),
                  ("Members", ",".join(f.get("Interface", "") for f in connects))]
        if detail:
            fields.append(("Detail", re.sub(r"[\r\n]+", " ", detail)[:200]))
        tap.action(fields)

    def call(name, cid, extension, queue):
        mark = step(name)
        detail = ""
        try:
            tap.send([("Action", "Originate"), ("Channel", f"PJSIP/{extension}@loop"), ("Application", "Wait"),
                      ("Data", "60"), ("CallerID", f"Capture <{cid}>"), ("Async", "true")])
            tap.wait_for(lambda f: f.get("Event") == "AgentConnect" and f.get("Queue") == queue
                         and f.get("CallerIDNum") == cid, timeout, mark, f"the AgentConnect of {cid} in {queue}")
            tap.pump(2.0)
        except ConnectionClosed:
            raise
        except CaptureError as error:
            detail = f"error: {error}"
        _hangup_all(tap)
        tap.pump(1.0)
        outcome(name, mark, cid, detail)

    for kind, base, extension, queue in (("rna", 9300, "rna-in", "q-rna"), ("busy", 9400, "busy-in", "q-busy"),
                                         ("answer", 9500, "answer-in", "q-answer")):
        for i in range(1, calls + 1):
            call(f"{kind}-{i:02d}", str(base + i), extension, queue)

    step("done")
    return steps


# --- confbridge-exits-under-load: room exits and reliefs while the PBX carries a fixed load ----

LOAD_ACCOUNT = "load"
LOAD_TARGET_ROOM = "capture-room"
CHANNELS_PER_UNIT = 4

# The background load: a room participant is a Local pair whose far leg sits in one of the
# background rooms for LOADT seconds while its near leg dials a tone, so audio crosses the pair into
# the room's mixing; a call is two PJSIP loopback legs bridged with a tone on the answering side and
# Echo() on the calling side, so RTP flows both ways. Each is four channels. Every load channel is
# named `Local/load-...` or `PJSIP/loadout-...`, or carries the account code `load`, so a listing
# tells it from the channels under measurement.
_LOAD_CAPTURE = """; the relief probe's anchor: stays in capture-room until the tap ends it (600 s is a safety net only)
exten => conf-anchor,1,Answer()
 same => n,Set(TIMEOUT(absolute)=600)
 same => n,ConfBridge(capture-room)
 same => n,Hangup()
"""

_LOAD_SIP = """; a background call's answering leg: up for the seconds its extension names, bridged with a tone
exten => _load-call-X.,1,Answer()
 same => n,Set(CHANNEL(accountcode)=load)
 same => n,Set(TIMEOUT(absolute)=${EXTEN:10})
 same => n,Dial(Local/load-tone@capture-load/n)
 same => n,Hangup()
"""

_LOAD_CONTEXT = """
[capture-load]
; a background participant: the far leg of a Local pair, in room LOADROOM for LOADT seconds
exten => load-conf,1,Answer()
 same => n,Set(TIMEOUT(absolute)=${LOADT})
 same => n,ConfBridge(${LOADROOM})
 same => n,Hangup()
; its near leg: dials a tone, so audio crosses the pair into the room
exten => load-dialtone,1,Dial(Local/load-tone@capture-load/n)
 same => n,Hangup()
exten => load-tone,1,Answer()
 same => n,Milliwatt()
 same => n,Hangup()
"""

_LOAD_EXTENSIONS = (_RENAMED_EXTENSIONS
                    .replace("[capture]\n", "[capture]\n" + _LOAD_CAPTURE, 1)
                    .replace("[capture-sip]\n", "[capture-sip]\n" + _LOAD_SIP, 1)
                    + _LOAD_CONTEXT)

# The calling leg of a background call: its own endpoint name on the loopback's address of record,
# so it is `PJSIP/loadout-...`; the INVITE comes back in as `loop` (by its source address).
_LOAD_PJSIP = _RENAMED_PJSIP + """
[loadout]
type = endpoint
transport = transport-loopback
context = capture-sip
disallow = all
allow = ulaw
aors = loop
direct_media = no
"""

# Asterisk drops a manager client that does not read a frame within `writetimeout` ms (100 by
# default); under load the reader must not be cut for a pause the measurement then cannot see.
_LOAD_MANAGER_USER = "writetimeout = 10000\n"


def is_load_channel(row) -> bool:
    """A CoreShowChannel row (or any frame) of the background load, not of the measurement."""
    channel = row.get("Channel", "")
    return (channel.startswith("Local/load-") or channel.startswith("PJSIP/loadout-")
            or row.get("AccountCode", "") == LOAD_ACCOUNT)


def parse_cpu_stat(text):
    """`usage_usec` from a cgroup v2 `cpu.stat`, or None when it is not there."""
    for line in (text or "").splitlines():
        key, _, value = line.partition(" ")
        if key == "usage_usec" and value.strip().isdigit():
            return int(value)
    return None


def parse_levels(value) -> list:
    """`0,150,400` -> [0, 150, 400]: the ladder of live load channels, in the order it is walked."""
    levels = []
    for part in str(value).split(","):
        part = part.strip()
        if not part:
            continue
        if not part.isdigit():
            raise CaptureError(f"a load level is a whole number of channels, got {part!r}")
        levels.append(int(part))
    if not levels:
        raise CaptureError("the load ladder is empty")
    return levels


def level_holds(loads, target: int, tolerance: float = 0.10) -> bool:
    """Whether every sampled load channel count sits within +-tolerance of the target (0 at 0)."""
    if not loads:
        return False
    if target == 0:
        return max(loads) == 0
    low, high = target * (1 - tolerance), target * (1 + tolerance)
    return all(low <= n <= high for n in loads)


class LoadStart:
    """One background unit to originate now: slot, kind (`room` or `call`), room, lifetime in s."""

    def __init__(self, slot, kind, room, lifetime):
        self.slot, self.kind, self.room, self.lifetime = slot, kind, room, lifetime

    def fields(self):
        if self.kind == "call":
            fields = [("Channel", f"PJSIP/load-call-{self.lifetime}@loadout"), ("Application", "Echo")]
        else:
            fields = [("Channel", "Local/load-conf@capture-load/n"), ("Context", "capture-load"),
                      ("Exten", "load-dialtone"), ("Priority", "1"),
                      ("Variable", f"__LOADT={self.lifetime}"), ("Variable", f"__LOADROOM={self.room}")]
        return [("Action", "Originate"), *fields, ("CallerID", f"Load <{9000 + self.slot}>"),
                ("Account", LOAD_ACCOUNT), ("Async", "true")]


class LoadPlan:
    """When to start which background unit, so that `level` channels stay up. Pure: the caller
    passes the clock.

    The level is held as round(level / 4) units of four channels; `call_share` of them are calls,
    spread among the room participants, which go round-robin into `rooms` background rooms. Slot j
    first starts at `ramp_rate` starts per second, with a lifetime spread over (0, lifetime] so the
    expiries do not bunch, and then again every `lifetime` seconds: units / lifetime starts per
    second once the ramp is over. While the measured load is at or over `ceiling` nothing starts,
    and the slot is tried again on the next call."""

    def __init__(self, level, now, rooms=20, lifetime=20, call_share=0.5, ramp_rate=10.0, ceiling=None):
        if level < 0 or rooms < 1 or lifetime < 1 or not 0 <= call_share <= 1 or ramp_rate <= 0:
            raise CaptureError("a load plan needs level >= 0, rooms >= 1, lifetime >= 1 s, "
                               "0 <= call share <= 1 and a positive ramp rate")
        self.level = level
        self.units = round(level / CHANNELS_PER_UNIT)
        self.rooms = rooms
        self.lifetime = int(lifetime)
        self.call_share = call_share
        self.ceiling = ceiling if ceiling is not None else int(level * 1.2) + CHANNELS_PER_UNIT
        self.next_at = [now + j / ramp_rate for j in range(self.units)]
        self.started = [0] * self.units

    def kind(self, slot) -> str:
        share = self.call_share
        return "call" if int((slot + 1) * share) > int(slot * share) else "room"

    def room(self, slot) -> str:
        rooms_before = sum(1 for j in range(slot) if self.kind(j) == "room")
        return f"load-room-{rooms_before % self.rooms + 1:02d}"

    def due(self, now, live=None):
        """The units to start at `now`, given the load channels last measured (None: unknown)."""
        if live is not None and live >= self.ceiling:
            return []
        out = []
        for slot in range(self.units):
            if self.next_at[slot] > now:
                continue
            if self.started[slot]:
                lifetime = self.lifetime
            else:
                lifetime = max(1, round(self.lifetime * (slot + 1) / self.units))
            self.started[slot] += 1
            self.next_at[slot] = now + lifetime
            out.append(LoadStart(slot, self.kind(slot), self.room(slot), lifetime))
        return out


class LoadGenerator(threading.Thread):
    """Holds the load on a second manager session with `Events: off`, so the measured connection
    carries no control traffic of its own. `set_level` moves it to a new level: every load channel
    is hung up first, then the new plan ramps. Every `sample_every` seconds it counts the channels
    (`CoreShowChannels`) and the container's CPU time and announces them as
    `UserEvent: CaptureLoad`, which reaches the measured connection in wire order: the tap's bytes
    hold the load it was taken under. `stop` hangs the load up and logs off; an error ends the thread
    and is kept in `error`."""

    def __init__(self, open_session, plan_for, read_cpu=None, sample_every=2.0, tick=0.05,
                 clear_timeout=30.0, clock=time.monotonic):
        super().__init__(daemon=True)
        self._open_session = open_session
        self._plan_for = plan_for
        self._read_cpu = read_cpu
        self._sample_every = sample_every
        self._tick = tick
        self._clear_timeout = clear_timeout
        self._clock = clock
        self._levels = queue.Queue()
        self._halt = threading.Event()
        self.error = None
        self.starts = 0

    def set_level(self, level: int):
        self._levels.put(level)

    def stop(self, timeout: float = 60.0):
        self._halt.set()
        self.join(timeout)

    def _rows(self, tap):
        mark = len(tap.frames)
        end = tap.list_action([("Action", "CoreShowChannels")])
        return [f for f in tap.frames[mark:] if f.get("Event") == "CoreShowChannel"
                and f.get("ActionID") == end.get("ActionID")]

    def _sample(self, tap, plan):
        rows = self._rows(tap)
        load = sum(1 for r in rows if is_load_channel(r))
        cpu = self._read_cpu() if self._read_cpu else None
        tap.action([("Action", "UserEvent"), ("UserEvent", "CaptureLoad"),
                    ("Target", str(plan.level if plan else 0)), ("Units", str(plan.units if plan else 0)),
                    ("LoadChannels", str(load)), ("AllChannels", str(len(rows))),
                    ("CpuUsec", "" if cpu is None else str(cpu)), ("Starts", str(self.starts))])
        return load

    def _clear(self, tap):
        tap.action([("Action", "Hangup"), ("Channel", "/^(Local\\/load-|PJSIP\\/loadout-)/")])
        deadline = self._clock() + self._clear_timeout
        while any(is_load_channel(r) for r in self._rows(tap)) and self._clock() < deadline:
            tap.pump(0.5)

    def run(self):
        tap = None
        plan = None
        try:
            tap = self._open_session("off")
            live = None
            next_sample = self._clock()
            while not self._halt.is_set():
                try:
                    level = self._levels.get_nowait()
                except queue.Empty:
                    level = None
                if level is not None:
                    self._clear(tap)
                    plan = self._plan_for(level, self._clock())
                    live = None
                    next_sample = self._clock()
                now = self._clock()
                if plan is not None:
                    for start in plan.due(now, live):
                        tap.send(start.fields())
                        self.starts += 1
                if now >= next_sample:
                    live = self._sample(tap, plan)
                    next_sample = now + self._sample_every
                tap.pump(self._tick)
                del tap.frames[:]  # the answers are drained; nothing waits on an old index
            self._clear(tap)
            self._sample(tap, None)
            tap.logoff()
        except (CaptureError, OSError) as error:
            self.error = str(error) or type(error).__name__
        finally:
            if tap is not None:
                try:
                    tap.sock.close()
                except OSError:
                    pass


class HangupLister:
    """Asks for a room's ConfbridgeList the moment one of its participants hangs up before its
    ConfbridgeLeave was seen: once per participant, never once its leave arrived, never for a
    channel that did not join that room. `observe` returns (action_id, fields) to send, or None; the
    ActionID is `hl-<Uniqueid>`, so the tap ties each list to the hangup that asked for it."""

    def __init__(self, room: str):
        self.room = room
        self.joined = set()
        self.left = set()
        self.hung_up = set()
        self.asked = []  # Uniqueids, in the order their lists were asked

    def observe(self, f):
        event, uid = f.get("Event"), f.get("Uniqueid", "")
        if is_sentinel_uniqueid(uid):
            return None
        if event == "ConfbridgeJoin" and f.get("Conference") == self.room:
            self.joined.add(uid)
        elif event == "ConfbridgeLeave" and f.get("Conference") == self.room:
            self.left.add(uid)
        elif event == "Hangup" and uid in self.joined:
            self.hung_up.add(uid)
            if uid not in self.left and uid not in self.asked:
                self.asked.append(uid)
                return f"hl-{uid}", [("Action", "ConfbridgeList"), ("Conference", self.room)]
        return None

    def pending(self) -> set:
        """Participants that joined and have not hung up yet."""
        return self.joined - self.hung_up


class ReaderMeter:
    """Whether the reader keeps up: for each frame with a `Timestamp` (timestampevents = yes), the
    wall-clock time it was parsed minus that Timestamp; host and container share one clock. A window
    in which any frame was parsed more than `limit` seconds after Asterisk stamped it is not valid:
    the order on the wire is still exact, but a time the measurement reports is the reader's."""

    def __init__(self, limit: float = 0.5, wall=time.time):
        self.limit = limit
        self.wall = wall
        self.reset(0)

    def reset(self, bytes_in: int):
        self.started = self.wall()
        self.bytes_at_start = bytes_in
        self.frames = self.stamped = self.late = 0
        self.max_lag = 0.0

    def observe(self, f):
        self.frames += 1
        stamp = f.get("Timestamp")
        if not stamp:
            return
        try:
            lag = self.wall() - float(stamp)
        except ValueError:
            return
        self.stamped += 1
        self.max_lag = max(self.max_lag, lag)
        if lag > self.limit:
            self.late += 1

    def report(self, bytes_in: int):
        seconds = max(self.wall() - self.started, 1e-9)
        return {"frames": self.frames, "seconds": round(seconds, 3),
                "events_per_second": round(self.frames / seconds, 1),
                "bytes_per_second": round((bytes_in - self.bytes_at_start) / seconds, 1),
                "max_lag_ms": round(self.max_lag * 1000, 3), "late_frames": self.late,
                "valid": self.stamped > 0 and self.late == 0}


class LoadSamples:
    """The `UserEvent: CaptureLoad` frames the measured connection received, in order."""

    def __init__(self):
        self.samples = []

    def observe(self, f):
        if f.get("Event") == "UserEvent" and f.get("UserEvent") == "CaptureLoad":
            cpu = f.get("CpuUsec", "")
            stamp = f.get("Timestamp", "")
            self.samples.append({"target": int(f.get("Target", "0") or 0),
                                 "load": int(f.get("LoadChannels", "0") or 0),
                                 "all": int(f.get("AllChannels", "0") or 0),
                                 "cpu_usec": int(cpu) if cpu.isdigit() else None,
                                 "wall": float(stamp) if stamp else None})


def level_summary(samples, target: int, tolerance: float = 0.10):
    """What a level's window measured: its samples' load range, whether it held, and the CPU
    cores the container used between the first and last sample that carry CPU time and a stamp."""
    loads = [s["load"] for s in samples]
    timed = [s for s in samples if s["cpu_usec"] is not None and s["wall"] is not None]
    cores = None
    if len(timed) >= 2 and timed[-1]["wall"] > timed[0]["wall"]:
        cores = round((timed[-1]["cpu_usec"] - timed[0]["cpu_usec"]) / 1e6
                      / (timed[-1]["wall"] - timed[0]["wall"]), 3)
    return {"samples": len(samples), "load_min": min(loads) if loads else None,
            "load_max": max(loads) if loads else None, "held": level_holds(loads, target, tolerance),
            "cpu_cores": cores}


def _confbridge_exits_under_load(tap: Tap):
    """For each level of the load ladder (`load_levels`, default `0`; `0` is the control): ramp
    the background load on a second session and wait until it holds; `exits` normal exits from
    `capture-room` (`conf-seq`, one second each, started at `exit_rate` per second); then the relief
    probe: a PJSIP participant relieved by an INVITE with Replaces of its own dialog, `reliefs`
    times alone in the room and `reliefs` times beside an anchor that stays, each with
    ConfbridgeList every ~50 ms from the INVITE to `relief_poll_after` s after the relieved
    channel's Hangup, a `relief_window` s wait for a late leave, then the successor listed, muted
    and hung up.

    Throughout, the moment a participant of `capture-room` hangs up before its ConfbridgeLeave was
    seen, the room's ConfbridgeList is asked on the measured connection (ActionID `hl-<Uniqueid>`).
    Each level ends with `UserEvent: CaptureLevel`: the load range sampled during the window and
    whether it held within 10 %, the container's CPU cores, the frames, events and bytes per second
    the reader took, its largest lag behind Asterisk's Timestamp and whether it stayed within
    `max_reader_lag` s, and the lists asked on a hangup. The measurement itself never hangs up a
    load channel. A relief that fails or times out is recorded, never fatal."""
    opts = tap.options
    if tap.open_session is None:
        raise CaptureError("confbridge-exits-under-load needs a second manager session (capture() opens it)")
    levels = parse_levels(opts.get("load_levels", "0"))
    room = LOAD_TARGET_ROOM
    timeout = float(opts.get("join_timeout", 20))
    tolerance = float(opts.get("load_tolerance", 0.10))
    steps = []

    lister = HangupLister(room)
    meter = ReaderMeter(float(opts.get("max_reader_lag", 0.5)))
    loads = LoadSamples()

    def ask_list(f):
        asked = lister.observe(f)
        if asked:
            try:
                tap.send(asked[1], action_id=asked[0])
            except OSError as error:
                raise ConnectionClosed(f"the manager connection failed: {error}") from error

    tap.observers += [ask_list, meter.observe, loads.observe]

    def step(name, clear=False):
        steps.append(name)
        if clear:  # nothing still waits on an index taken before this step
            del tap.frames[:]
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

    def note(name, **kv):
        fields = [("Action", "UserEvent"), ("UserEvent", "CaptureOutcome"), ("Step", name)]
        fields += [(k, re.sub(r"[\r\n]+", " ", str(v))[:200]) for k, v in kv.items()]
        tap.action(fields)

    def channels():
        mark = len(tap.frames)
        end = tap.list_action([("Action", "CoreShowChannels")])
        return [f for f in tap.frames[mark:] if f.get("Event") == "CoreShowChannel"
                and f.get("ActionID") == end.get("ActionID")]

    def cleanup(keep_uids, wait=15.0):
        """Hang up every channel of the measurement but `keep_uids`; never a load channel."""
        deadline = tap.clock() + wait
        while True:
            rest = [c for c in channels() if c.get("Uniqueid") not in keep_uids and not is_load_channel(c)]
            if not rest or tap.clock() > deadline:
                return len(rest)
            for c in rest:
                tap.send([("Action", "Hangup"), ("Channel", c.get("Channel", ""))])
            tap.pump(0.5)

    def conf_list():
        mark = len(tap.frames)
        end = tap.list_action([("Action", "ConfbridgeList"), ("Conference", room)])
        return [f for f in tap.frames[mark:] if f.get("Event") == "ConfbridgeList"
                and f.get("ActionID") == end.get("ActionID")]

    def dialog_of(target):
        values = {}
        for key in ("call-id", "local_tag", "remote_tag"):
            response = tap.action([("Action", "Getvar"), ("Channel", target["Channel"]),
                                   ("Variable", f"CHANNEL(pjsip,{key})")])
            values[key] = response.get("Value", "")
        if not all(values.values()):
            return None
        return f"{values['call-id']};to-tag={values['local_tag']};from-tag={values['remote_tag']}"

    def wait_level(level):
        """Pump until the last three samples taken at this level hold it, or the ramp times out."""
        deadline = tap.clock() + float(opts.get("ramp_timeout", 180))
        first = len(loads.samples)
        while tap.clock() < deadline:
            mine = [s["load"] for s in loads.samples[first:] if s["target"] == level]
            if len(mine) >= 3 and level_holds(mine[-3:], level, tolerance):
                return True
            tap.pump(0.2)
        return False

    def exits(prefix):
        n = int(opts.get("exits", 0))
        if not 0 <= n <= 1000:
            raise CaptureError("exits is a count from 0 to 1000 (caller IDs 8000-8999)")
        interval = 1.0 / float(opts.get("exit_rate", 2.0))
        for i in range(n):
            originate("Local/conf-seq@capture/n", str(8000 + i), data="5")
            tap.pump(interval)
        deadline = tap.clock() + timeout + 10
        while lister.pending() and tap.clock() < deadline:
            tap.pump(0.2)
        tap.pump(float(opts.get("relief_window", 5.0)))  # a late leave still lands in this step
        left = cleanup(set())
        note(f"{prefix}-exits", Exits=n, Pending=len(lister.pending()), LeftOver=left)

    def relief(prefix, counter):
        n = int(opts.get("reliefs", 10))
        window = float(opts.get("relief_window", 5.0))
        after = float(opts.get("relief_poll_after", 1.0))
        for shape in ("alone", "anchored"):
            if not n:
                break
            anchor, keep = None, set()
            if shape == "anchored":
                mark = step(f"{prefix}-anchor-join", clear=True)
                cid = str(next(counter))
                originate("Local/conf-anchor@capture/n", cid, data="600")
                anchor = tap.wait_for(lambda f, c=cid: f.get("Event") == "ConfbridgeJoin"
                                      and f.get("CallerIDNum") == c, timeout, mark, "the anchor's join")
                tap.pump(0.5)
                keep = {c.get("Uniqueid") for c in channels() if not is_load_channel(c)}
            for i in range(1, n + 1):
                name = f"{prefix}-relief-{shape}-{i:02d}"
                mark = step(name, clear=True)
                try:
                    cid = str(next(counter))
                    originate("PJSIP/conf-in@loop", cid)
                    join = tap.wait_for(lambda f, c=cid: f.get("Event") == "ConfbridgeJoin"
                                        and f.get("CallerIDNum") == c, timeout, mark, "the target's join")
                    u1, ch1 = join["Uniqueid"], join["Channel"]
                    tap.pump(0.5)
                    conf_list()  # L0: before the relief, the target is listed (the list's own control)
                    header = dialog_of(join)
                    if header is None:
                        note(name, Shape=shape, Result="no-dialog", TargetId=u1)
                        cleanup(keep)
                        continue
                    hmark = len(tap.frames)
                    originate("Local/replace@capture/n", str(next(counter)), data="20",
                              variables=[f"__RH={header}"])
                    polls, t_hang = 0, None
                    deadline = tap.clock() + 8.0
                    while tap.clock() < deadline:
                        conf_list()
                        polls += 1
                        if t_hang is None and tap.count(lambda f: f.get("Event") == "Hangup"
                                                        and f.get("Uniqueid") == u1, hmark):
                            t_hang = tap.clock()
                            step(f"{name}-hungup")
                        if t_hang is not None and tap.clock() - t_hang >= after:
                            break
                        tap.pump(0.05)
                    if t_hang is not None:
                        rest = window - (tap.clock() - t_hang)
                        if rest > 0:
                            tap.pump(rest)
                    step(f"{name}-after-window")
                    rows = conf_list()
                    succ = [r for r in rows if r.get("Uniqueid") != u1 and r.get("Channel") != ch1
                            and (anchor is None or r.get("Channel") != anchor.get("Channel"))]
                    for r in succ:
                        tap.action([("Action", "ConfbridgeMute"), ("Conference", room),
                                    ("Channel", r.get("Channel", ""))])
                    tap.pump(0.3)
                    step(f"{name}-successor-hangup")
                    for r in succ:
                        tap.action([("Action", "Hangup"), ("Channel", r.get("Channel", ""))])
                    tap.pump(1.5)
                    left = cleanup(keep)
                    tap.pump(0.5)
                    leaves = tap.count(lambda f: f.get("Event") == "ConfbridgeLeave"
                                       and f.get("Uniqueid") == u1, mark)
                    note(name, Shape=shape, Result="ok" if t_hang is not None else "no-hangup",
                         TargetId=u1, TargetName=ch1, Polls=polls, LeavesOfTarget=leaves,
                         HangupList="yes" if u1 in lister.asked else "no",
                         Successors=",".join(r.get("Channel", "") for r in succ), LeftOver=left)
                except ConnectionClosed:
                    raise
                except CaptureError as error:
                    cleanup(keep)
                    note(name, Shape=shape, Result="error", Detail=str(error))
            if anchor is not None:
                step(f"{prefix}-anchor-hangup")
                cleanup(set())
                tap.pump(1.0)

    def plan_for(level, now):
        return LoadPlan(level, now, rooms=int(opts.get("load_rooms", 20)),
                        lifetime=int(opts.get("load_lifetime", 20)),
                        call_share=float(opts.get("load_call_share", 0.5)),
                        ramp_rate=float(opts.get("load_ramp_rate", 10.0)))

    generator = LoadGenerator(tap.open_session, plan_for, tap.read_cpu,
                              sample_every=float(opts.get("load_sample_every", 2.0)))
    generator.start()
    try:
        for level in levels:
            prefix = f"level-{level}"
            step(f"{prefix}-ramp", clear=True)
            generator.set_level(level)
            ramp_held = wait_level(level)
            first_sample = len(loads.samples)
            asked_before = len(lister.asked)
            meter.reset(tap.bytes_in)
            step(f"{prefix}-exits", clear=True)
            exits(prefix)
            relief(prefix, iter(range(7200, 7999)))
            window = [s for s in loads.samples[first_sample:] if s["target"] == level]
            summary = level_summary(window, level, tolerance)
            reader = meter.report(tap.bytes_in)
            note(prefix, Level=level, RampHeld="yes" if ramp_held else "no",
                 Held="yes" if summary["held"] else "no", Samples=summary["samples"],
                 LoadMin=summary["load_min"], LoadMax=summary["load_max"], CpuCores=summary["cpu_cores"],
                 Frames=reader["frames"], EventsPerSecond=reader["events_per_second"],
                 BytesPerSecond=reader["bytes_per_second"], MaxLagMs=reader["max_lag_ms"],
                 LateFrames=reader["late_frames"], ReaderValid="yes" if reader["valid"] else "no",
                 HangupLists=len(lister.asked) - asked_before, LoadStarts=generator.starts,
                 LoadError=generator.error or "")
            if generator.error:
                break
        step("load-stop", clear=True)
    finally:
        generator.stop()
    note("load-stop", LoadError=generator.error or "", LoadStarts=generator.starts)
    step("done")
    return steps


WORKLOADS = {
    "bridges-and-conference": {
        "description": "Local call; two-party bridge + BridgeList/BridgeInfo/Status; two-participant "
                       "ConfBridge + ConfbridgeListRooms/ConfbridgeList/BridgeList; empty listings.",
        "files": {"extensions.conf": _BRIDGES_EXTENSIONS, "confbridge.conf": _BRIDGES_CONFBRIDGE},
        "run": _bridges_and_conference,
    },
    "confbridge-exits-under-load": {
        "description": "Under each level of a load ladder (--load-levels, 0 = control) held on a second "
                       "Events-off session: --exits normal ConfBridge exits, then --reliefs reliefs by an "
                       "INVITE with Replaces alone and beside an anchor; ConfbridgeList asked on every "
                       "participant Hangup whose leave was not seen; load, CPU and reader lag per level.",
        "files": {"extensions.conf": _LOAD_EXTENSIONS, "queues.conf": _RENAMED_QUEUES,
                  "confbridge.conf": _BRIDGES_CONFBRIDGE, "pjsip.conf": _LOAD_PJSIP},
        "manager_user": _LOAD_MANAGER_USER,
        "run": _confbridge_exits_under_load,
    },
    "dialplan-and-parking": {
        "description": "Mid-call CALLERID change + Goto (NewCallerid, Newexten); Redirect; Park() picked "
                       "up by its space, given up and timed out + ParkedCalls/Parkinglots; SendFlash "
                       "help, with Receive, without, and to a missing channel on a PJSIP loopback call.",
        "files": {"extensions.conf": _PARKING_EXTENSIONS, "res_parking.conf": _PARKING_RES_PARKING,
                  "pjsip.conf": _PARKING_PJSIP},
        "run": _dialplan_and_parking,
    },
    "queue-ring-no-answer": {
        "description": "N calls (--sequential-calls, default 1) of each kind: in q-rna PJSIP/agent1 rings out "
                       "unanswered, then PJSIP/agent2 answers; in q-busy PJSIP/agent3 answers busy, then "
                       "PJSIP/agent2 answers; in q-answer PJSIP/agent2 answers at once (the negative control).",
        "files": {"extensions.conf": _RNA_EXTENSIONS, "queues.conf": _RNA_QUEUES, "pjsip.conf": _RNA_PJSIP},
        "run": _queue_ring_no_answer,
    },
    "queue-revisit": {
        "description": "N calls (--sequential-calls, default 1) each answered in q-first by PJSIP/agent1, "
                       "blind-transferred by that member into q-second and answered there by PJSIP/agent2; "
                       "then N calls answered once in q-first (the negative control).",
        "files": {"extensions.conf": _REVISIT_EXTENSIONS, "queues.conf": _REVISIT_QUEUES,
                  "pjsip.conf": _REVISIT_PJSIP},
        "run": _queue_revisit,
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


def manager_conf(secret: str, timestamps=True, user_extra="") -> str:
    """The manager.conf of a run: one account, with `user_extra` lines (e.g. `writetimeout`)."""
    return _MANAGER_CONF.format(user=MANAGER_USER, secret=secret,
                                timestamps="yes" if timestamps else "no", user_extra=user_extra)


def _open_session_on(host: str, port: int, secret: str):
    """A factory of further logged-in Taps on the run's PBX (`events` = the Login's Events)."""

    def open_session(events="off"):
        sock = socket.create_connection((host, port), timeout=15)
        try:
            tap = Tap(sock)
            tap.login(MANAGER_USER, secret, events)
        except BaseException:
            sock.close()
            raise
        return tap

    return open_session


def capture(image: str, workload_name: str, out_dir: Path, raw_dir: Path, timestamps=True,
            drop_events=(), keep_steps=(), name_prefix="labc", options=None, docker_args=(),
            tap_only=False):
    """Run one workload on one image. With `tap_only` the tap streams to `<raw_dir>/<name>.tap.raw`
    as it arrives (a take too large to hold in memory or to reduce) and nothing is reduced: the
    return value is (tap_path, None). `docker_args` go to `docker run` (e.g. `--cpus 4`)."""
    workload = WORKLOADS.get(workload_name)
    if workload is None:
        raise CaptureError(f"unknown workload {workload_name!r}; see `workloads`")
    _repo, tag, _digest = parse_image(image)
    run_ref, image_id = resolve_image(image)
    secret = secrets.token_hex(12)
    container = f"{name_prefix}-{workload_name}-{os.getpid()}-{secrets.token_hex(3)}"
    conf_dir = Path(tempfile.mkdtemp(prefix="ami-capture-conf-"))
    files = dict(workload["files"])
    files["manager.conf"] = manager_conf(secret, timestamps, workload.get("manager_user", ""))
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
        _docker("run", "-d", "--name", container, *docker_args, *ports, *mounts, run_ref)
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
        sink = None
        try:
            if tap_only:
                raw_dir.mkdir(parents=True, exist_ok=True)
                tap_path = raw_dir / f"{recording_name(workload_name, asterisk_version)}.tap.raw"
                sink = open(tap_path, "wb")
            tap = Tap(sock, sink=sink)
            tap.options = dict(options or {})
            tap.open_session = _open_session_on(host.strip("[]"), int(port_number), secret)
            tap.read_cpu = lambda: parse_cpu_stat(_docker(
                "exec", container, "cat", "/sys/fs/cgroup/cpu.stat", check=False, timeout=15).stdout)
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
            if sink is not None:
                sink.close()
        tap_bytes = b"" if tap_only else bytes(tap.transcript)
    finally:
        if started:
            _docker("rm", "-f", container, check=False)
        for path in conf_dir.iterdir():
            path.unlink()
        conf_dir.rmdir()

    captured = _dt.datetime.now(_dt.timezone.utc).strftime("%Y-%m-%dT%H:%M:%SZ")
    if tap_only:
        digest = hashlib.sha256()
        with open(tap_path, "rb") as kept:
            for block in iter(lambda: kept.read(1 << 20), b""):
                digest.update(block)
        tap_sha256, tap_size = digest.hexdigest(), tap_path.stat().st_size
    else:
        tap_sha256, tap_size = hashlib.sha256(tap_bytes).hexdigest(), len(tap_bytes)
    meta = {
        "workload": workload_name,
        "image": image,
        "image_id": image_id,
        "asterisk_version": asterisk_version,
        "ami_banner": tap.banner,
        "captured_utc": captured,
        "driver_sha256": hashlib.sha256(Path(__file__).read_bytes()).hexdigest(),
        "steps": steps,
        "tap_bytes": tap_size,
        "tap_sha256": tap_sha256,
        "docker_args": list(docker_args),
        "options": {k: v for k, v in sorted((options or {}).items())},
    }
    raw_dir.mkdir(parents=True, exist_ok=True)
    stem = recording_name(workload_name, asterisk_version)
    tap_path = raw_dir / f"{stem}.tap.raw"
    if not tap_only:
        tap_path.write_bytes(tap_bytes)
    (raw_dir / f"{stem}.tap.json").write_text(json.dumps(meta, indent=2) + "\n", encoding="utf-8")
    if tap_only:
        return tap_path, None
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
                     help="renamed-caller-and-participant: N sequential calls per surface (default 0); "
                          "queue-revisit, queue-ring-no-answer: N calls of each kind (default 1)")
    cap.add_argument("--tap-only", action="store_true",
                     help="stream the unredacted tap to --raw-dir and reduce nothing (a take over the cap)")
    cap.add_argument("--cpus", default="",
                     help="docker run --cpus for the container (e.g. 4), so a load level is reproducible")
    load = cap.add_argument_group("confbridge-exits-under-load")
    load.add_argument("--load-levels", default="0", help="live load channels per level, walked in order")
    load.add_argument("--load-rooms", type=int, default=20, help="background rooms")
    load.add_argument("--load-lifetime", type=int, default=20, help="seconds each background unit lives")
    load.add_argument("--load-call-share", type=float, default=0.5, help="share of units that are calls")
    load.add_argument("--load-ramp-rate", type=float, default=10.0, help="unit starts per second while ramping")
    load.add_argument("--load-sample-every", type=float, default=2.0, help="seconds between load samples")
    load.add_argument("--exits", type=int, default=300, help="normal room exits per level")
    load.add_argument("--exit-rate", type=float, default=2.0, help="normal exits started per second")
    load.add_argument("--reliefs", type=int, default=10, help="reliefs per shape (alone, anchored) per level")
    load.add_argument("--relief-window", type=float, default=5.0, help="seconds to wait for a late leave")
    load.add_argument("--max-reader-lag", type=float, default=0.5,
                      help="seconds a frame may be parsed after its Timestamp before the level is not valid")

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
        options = {"sequential_calls": args.sequential_calls}
        if args.workload == "confbridge-exits-under-load":
            parse_levels(args.load_levels)
            options.update({
                "load_levels": args.load_levels, "load_rooms": args.load_rooms,
                "load_lifetime": args.load_lifetime, "load_call_share": args.load_call_share,
                "load_ramp_rate": args.load_ramp_rate, "load_sample_every": args.load_sample_every,
                "exits": args.exits, "exit_rate": args.exit_rate, "reliefs": args.reliefs,
                "relief_window": args.relief_window, "max_reader_lag": args.max_reader_lag})
        docker_args = ("--cpus", args.cpus) if args.cpus else ()
        for image in args.image:
            tap_path, written = capture(
                image, args.workload, args.out, raw_dir, not args.no_timestamps,
                _csv(args.drop_events), _csv(args.keep_steps), options=options,
                docker_args=docker_args, tap_only=args.tap_only)
            print(f"{image}: tap {tap_path} ({tap_path.stat().st_size} bytes, unredacted -- keep it "
                  f"out of the repository)")
            if written is None:
                continue
            raw, side, report, size = written
            print(f"  wrote {raw.name} ({size} bytes, {report['frames_kept']}/{report['frames_in']} "
                  f"frames) and {side.name} in {args.out}")
        return 0
    except CaptureError as exc:
        print(f"capture-ami-recording: {exc}", file=sys.stderr)
        return 1


if __name__ == "__main__":
    sys.exit(main())

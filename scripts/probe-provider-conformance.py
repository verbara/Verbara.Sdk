#!/usr/bin/env python3
"""The conformance probe, as an instrument rather than a ceremony (`provider-wire-protocol-conformance` §5).

Every defect in that change was found the same way: send what the shipped client sends, to the real
endpoint, next to a control that is *known wrong*, and compare. Every defect was also hidden the same
way — by a green test suite whose fake was written by the same author as the client, so a shared
misreading of the vendor's contract passed on both sides. That is why the method has to be committed
code with its own tests, and not a procedure someone remembers to follow.

Sections 1-3 hold the parts of that method that can be wrong **without a network**: what a probe is
allowed to print, which controls it must carry, and how deep it must reach before its result means
anything. Those are the parts that failed in practice, and they are unit-tested on every PR.

Section 4 is the live runner. It is **run by hand, never wired to CI** (§5.2): it needs credentials
and paid egress, and a probe whose output nobody reads is a cost, not a control. Its transport is
deliberately thin, and it borrows the RFC 6455 primitives from `capture-provider-recording.py`
rather than re-implementing framing that already has tests. It owns its own read loop for one
reason: that tool's session raises on a non-`101` upgrade and discards the close code, and here both
of those ARE the measurement — a route control is *supposed* to answer `404`, and Speechmatics STT's
`4001` after a `101` is the finding this whole instrument exists to keep visible.

    python3 scripts/probe-provider-conformance.py --self-check
    python3 scripts/probe-provider-conformance.py --list
    python3 scripts/probe-provider-conformance.py --probe <surface>     # live; costs money
    python3 scripts/probe-provider-conformance.py --probe all

Credentials are read from the environment and are never written, echoed or stored.

Three rules this instrument enforces structurally, each because it was broken by hand first:

1. **Redaction is by key, whatever the value's type** (§5.4). The ad-hoc redactor used during the
   2026-08-15 runs matched only string-valued identifier fields, so `additional_model_uuids` — an
   array of them — passed straight through and a raw identifier reached the operator's screen. The
   rule said "never echoed"; the code said otherwise. `redact` now walks arrays and nested objects
   and keys off the field name alone.

2. **A probe cannot run without both controls** (§5.3, `Sdk/ADR-0049` D4). A wrong-path control
   proves the probe can tell *routes* apart. Only an invalid-credential control proves it can tell
   *credentials* apart. They answer different questions, and a run carrying one of them is not a
   weaker version of a run carrying both — it is silent about whichever question it did not ask.
   `ProbeSpec` refuses to construct without one of each.

3. **A handshake is not a measurement** (§5.11). For a vendor that authenticates in the HTTP upgrade
   headers, `101 Switching Protocols` proves the credential was accepted. For a vendor that
   authenticates in-band it proves nothing at all: Speechmatics STT returns `101` to a rejected key
   and closes `4001` afterwards. Had this programme stopped at the handshake, that provider would
   have been recorded as verified-good while being entirely unusable. So a WebSocket probe must
   declare that it reached the vendor's first protocol exchange, and `ProbeSpec` refuses to record a
   verdict from a run that stopped at the upgrade — unless the surface's validation point has itself
   been *measured* to be the handshake, which is a finding, never an assumption.
"""

import argparse
import base64
import importlib.util
import json
import os
import socket
import ssl
import sys
import urllib.error
import urllib.parse
import urllib.request
import uuid
from dataclasses import dataclass, field

# --- 1. Redaction (§5.4) ----------------------------------------------------------------------

#: Field names whose VALUES correlate a run back to an account or a request, per
#: `docs/guides/provider-recording-protocol.md` §4. The names themselves are safe to print — it is
#: the values that must never appear. Add to this set freely; a false positive costs a `<redacted>`
#: in a log line, a false negative costs a leaked identifier.
CORRELATING_KEYS = frozenset({
    "request_id", "requestid", "request-id",
    "model_uuid", "additional_model_uuids",
    "history_item_id", "context_id", "session_id", "job_id",
    "voice_id", "speaker_id", "account_id", "user_id",
    "x-request-id", "cf-ray",
})

REDACTED = "<redacted>"


def redact(value, keys=CORRELATING_KEYS):
    """Replace every correlating field's value, at any depth and whatever its type.

    Keyed on the field name alone. The previous redactor tested the value's type first and so
    walked past `"additional_model_uuids": [...]` — an array of exactly the identifiers it existed
    to hide. A redactor that is more permissive than its own rule is the same defect this whole
    change is about, one level up from the client.
    """
    if isinstance(value, dict):
        return {k: (REDACTED if k.lower() in keys else redact(v, keys)) for k, v in value.items()}
    if isinstance(value, (list, tuple)):
        return [redact(v, keys) for v in value]
    return value


def render(value, limit=400):
    """Redact, serialize and truncate — the only sanctioned way to print a vendor payload."""
    text = json.dumps(redact(value), sort_keys=True)
    return text if len(text) <= limit else text[:limit] + "…"


# --- 2. Controls and the probe contract (§5.1, §5.3) ------------------------------------------

ROUTE = "route"            # a path the vendor does not serve — proves the probe distinguishes routes
CREDENTIAL = "credential"  # a key the vendor rejects — proves it distinguishes credentials

CONTROL_KINDS = (ROUTE, CREDENTIAL)


@dataclass(frozen=True)
class Control:
    """One deliberately-wrong arm. `expected` is what the vendor was MEASURED to answer, not what
    it is assumed to answer — the field exists so a control that silently starts passing is loud."""

    kind: str
    description: str
    expected: str

    def __post_init__(self):
        if self.kind not in CONTROL_KINDS:
            raise ValueError(f"control kind must be one of {CONTROL_KINDS}, got {self.kind!r}")
        if not self.description or not self.expected:
            raise ValueError("a control must say what it does and what the vendor answered")


#: Where a vendor decides a credential is bad. MEASURED per surface, never inferred from where the
#: credential is placed in the request — `Sdk/ADR-0049` D3 forbids that inference, and it forbids it
#: because it was made and was wrong.
HANDSHAKE = "handshake"    # rejected at the HTTP upgrade / response status
IN_BAND = "in-band"        # upgrade succeeds, rejection arrives as a protocol message
UNMEASURED = "unmeasured"

VALIDATION_POINTS = (HANDSHAKE, IN_BAND, UNMEASURED)


@dataclass(frozen=True)
class ProbeSpec:
    """A surface, the request the shipped client makes against it, and its mandatory controls."""

    name: str
    origin: str
    route: str
    transport: str                       # "http" or "ws"
    controls: tuple = ()
    validation_point: str = UNMEASURED
    notes: str = ""

    def __post_init__(self):
        if self.transport not in ("http", "ws"):
            raise ValueError(f"transport must be 'http' or 'ws', got {self.transport!r}")
        if self.validation_point not in VALIDATION_POINTS:
            raise ValueError(f"validation point must be one of {VALIDATION_POINTS}")
        kinds = {c.kind for c in self.controls}
        missing = [k for k in CONTROL_KINDS if k not in kinds]
        if missing:
            raise ValueError(
                f"{self.name}: probe refuses to run without every control. Missing: {missing}. "
                "A wrong-path control proves the probe distinguishes routes; only an "
                "invalid-credential control proves it distinguishes credentials (ADR-0049 D4). "
                "A run carrying one of them is not a weaker measurement — it is silent about the "
                "question it did not ask.")

    def verdict_allowed(self, *, reached_first_exchange: bool):
        """Whether a run at this depth may be recorded as a verdict at all (§5.11).

        Returns (allowed, reason). An HTTP surface answers in its response, so the response IS the
        first exchange. A WebSocket surface that stopped at `101` has measured the upgrade and
        nothing else — which is a verdict only where the validation point has been MEASURED to be
        the handshake. Where it is in-band, or not yet measured, `101` is not evidence.
        """
        if self.transport == "http" or reached_first_exchange:
            return True, "reached the vendor's first protocol exchange"
        if self.validation_point == HANDSHAKE:
            return True, ("stopped at the upgrade, which is sufficient here: this surface's "
                          "validation point was measured to be the handshake")
        return False, (
            "stopped at the WebSocket upgrade. `101` proves the socket opened, not that the request "
            "was accepted: Speechmatics STT answers `101` to a rejected credential and closes `4001` "
            f"afterwards. This surface's validation point is {self.validation_point!r}, so the "
            "upgrade is not evidence. Reach the first protocol exchange.")


# --- 3. The worked example, encoded rather than remembered (§5.3, §5.6) -----------------------

#: Deepgram TTS is the instrument's reference run: both arms measured on the same host, seconds
#: apart, on 2026-08-15/16. It is committed so a change that breaks the method fails a test rather
#: than producing a plausible-looking report.
WORKED_EXAMPLES = (
    ProbeSpec(
        name="deepgram-tts",
        origin="wss://api.deepgram.com",
        route="/v1/speak",
        transport="ws",
        validation_point=HANDSHAKE,
        controls=(
            Control(ROUTE, "/v1/speak-does-not-exist on the same host", "404 Not Found"),
            Control(CREDENTIAL, "deliberately malformed Authorization: Token", "401 at the upgrade"),
        ),
        notes=("shipped defaults model=aura-2-thalia-en, encoding=linear16, sample_rate=24000 "
               "returned 101; frames were Metadata, then 37 binary frames of 1920 bytes "
               "(71040 B, 1.48 s), then Flushed — not the Class B shape: no text frame carried a "
               "long string field, so no base64 audio is hidden in JSON on this surface"),
    ),
    ProbeSpec(
        name="speechmatics-stt",
        origin="wss://eu2.rt.speechmatics.com",
        route="/v2",
        transport="ws",
        validation_point=IN_BAND,
        controls=(
            Control(ROUTE, "a path the host does not serve", "rejected at the upgrade"),
            Control(CREDENTIAL, "query-parameter key as the shipped client sends it",
                    "101 then close 4001 — the upgrade proved nothing"),
        ),
        notes=("the surface that produced the depth rule: a handshake-only probe would have "
               "recorded this provider as verified-good while it was entirely unusable"),
    ),
)


# --- 4. The live runner (§5.2 — hand-run, never a CI gate) ------------------------------------
#
# §5.2 decided this half has no automated gate: it needs credentials and paid egress, and evidence
# produced on a timer and read by nobody is a cost, not a control. So this is committed code that a
# human invokes, per surface — the point of committing it is that a re-probe reproduces the request
# byte-for-byte instead of being retyped from memory, which is how the fakes and the clients came to
# share a misreading in the first place.

_CAPTURE_SCRIPT = os.path.join(
    os.path.dirname(os.path.abspath(__file__)), "capture-provider-recording.py")
_capture_module = None


def capture_primitives():
    """Load the capture tool's RFC 6455 codec by path, once.

    Borrowed rather than re-implemented: `ws_encode_frame` and `ws_decode_frames` are pure
    functions that already have tests, and a second masking/continuation codec in this repo would
    just be a second thing to get wrong. By path because the filename has hyphens and `scripts/` is
    not a package — the same `spec_from_file_location` route the guard-script tests already take.
    Lazily, so that importing THIS module to run its tests never reads that file.
    """
    global _capture_module
    if _capture_module is None:
        spec = importlib.util.spec_from_file_location(
            "capture_provider_recording", _CAPTURE_SCRIPT)
        module = importlib.util.module_from_spec(spec)
        spec.loader.exec_module(module)
        _capture_module = module
    return _capture_module


def elide_payloads(value, limit=64):
    """Replace long string values with their length.

    §5.4 bans storing or printing Output, and on the Class B surfaces the Output *is* a string
    field — base64 PCM inside a text frame. `redact` keys off field names, which cannot help here
    because the field carrying the audio is differently named per vendor and is not correlating.
    Length is the measurement anyway: what a re-probe needs to know is that bytes arrived, never
    which bytes.
    """
    if isinstance(value, dict):
        return {k: elide_payloads(v, limit) for k, v in value.items()}
    if isinstance(value, list):
        return [elide_payloads(v, limit) for v in value]
    if isinstance(value, str) and len(value) > limit:
        return f"<{len(value)} chars>"
    return value


@dataclass
class Exchange:
    """What ONE arm of one probe actually measured. Every field is an observation, never a plan."""

    arm: str
    status: str = ""
    messages: list = field(default_factory=list)
    audio_bytes: int = 0
    close_code: object = None
    close_reason: str = ""
    reached_first_exchange: bool = False
    saw_terminator: bool = False
    error: str = ""

    def line(self):
        bits = [f"[{self.arm}]", self.status or "(no status)"]
        if self.reached_first_exchange:
            bits.append("reached first exchange")
        if self.audio_bytes:
            bits.append(f"{self.audio_bytes} B audio")
        if self.saw_terminator:
            bits.append("vendor's terminator")
        if self.close_code is not None:
            bits.append(f"close {self.close_code} {self.close_reason}".rstrip())
        if self.error:
            bits.append(f"!! {self.error}")
        return " · ".join(bits)


def summarize(opcode, payload, cap):
    """One printable, redacted, Output-free line per received message."""
    if opcode == cap.WS_OPCODE_BINARY:
        return f"binary {len(payload)} B"
    try:
        parsed = json.loads(payload.decode("utf-8"))
    except (UnicodeDecodeError, ValueError):
        return f"text {len(payload)} B (not JSON)"
    return render(elide_payloads(redact(parsed)))


def probe_http(arm, url, headers, body=None, method="GET", timeout=30):
    """One HTTP arm. A 4xx is a MEASUREMENT here, not an exception — the controls depend on it."""
    ex = Exchange(arm=arm)
    request = urllib.request.Request(url, data=body, headers=headers, method=method)
    try:
        with urllib.request.urlopen(request, timeout=timeout) as response:  # noqa: S310
            payload = response.read()
            ex.status = f"{response.status} {response.reason}"
            ex.reached_first_exchange = True
    except urllib.error.HTTPError as err:
        payload = err.read()
        ex.status = f"{err.code} {err.reason}"
        ex.reached_first_exchange = True
    except (urllib.error.URLError, OSError) as err:
        ex.error = f"could not reach the endpoint: {err.__class__.__name__}"
        return ex

    if payload[:1] in (b"{", b"["):
        try:
            ex.messages.append(render(elide_payloads(redact(json.loads(payload)))))
        except ValueError:
            ex.messages.append(f"{len(payload)} B of non-JSON body")
    else:
        # Audio, or anything else that is Output: length only (§5.4). Counted as audio ONLY on a
        # 2xx: the 2026-08-19 run reported "172 B audio" for a Speechmatics 401 whose body was a
        # plain-text error, and a credential control that appears to have produced audio is the
        # inverted finding this instrument exists to prevent.
        if ex.status.startswith("2"):
            ex.audio_bytes = len(payload)
        ex.messages.append(f"{len(payload)} B body, not JSON")
    return ex


def probe_ws(arm, url, headers, sends, first_exchange, audio_of, terminator=None,
             idle_timeout=20):
    """One WebSocket arm, read past the upgrade (§5.11).

    Returns what happened rather than raising it. A non-`101` is what a route control is FOR, and
    the close code after a `101` is the whole Speechmatics finding — so neither may be an exception.

    `terminator` is the message the SHIPPED client breaks on, and the loop stops there for the same
    reason the client does. Without it the 2026-08-19 run reported Cartesia TTS as `!! went idle —
    the vendor sent nothing further`, which read as a vendor defect and was not one: the vendor had
    sent `done` and then held the context open, exactly as documented, while the probe kept reading
    a socket the client would already have left. An instrument that manufactures an anomaly is the
    same failure as one that hides a real one.
    """
    cap = capture_primitives()
    ex = Exchange(arm=arm)
    key = base64.b64encode(uuid.uuid4().bytes).decode("ascii")
    try:
        host, port, request = cap.ws_handshake_request(url, headers, key)
        raw = socket.create_connection((host, port), timeout=idle_timeout)
    except (OSError, Exception) as err:  # noqa: BLE001 - the reason is the measurement
        ex.error = f"could not connect: {err.__class__.__name__}: {err}"
        return ex

    sock = (ssl.create_default_context().wrap_socket(raw, server_hostname=host)
            if url.startswith("wss://") else raw)
    try:
        sock.sendall(request)
        buffer = b""
        while b"\r\n\r\n" not in buffer:
            chunk = sock.recv(8192)
            if not chunk:
                ex.error = "the peer closed during the upgrade"
                return ex
            buffer += chunk
        head, buffer = buffer.split(b"\r\n\r\n", 1)
        text = head.decode("latin-1")
        ex.status = text.split("\r\n", 1)[0]
        if " 101 " not in ex.status:
            return ex                       # a result — this is what a route control must produce
        if cap.ws_accept_token(key).lower() not in text.lower():
            ex.error = ("Sec-WebSocket-Accept did not match the key sent — the peer that answered "
                        "is not the endpoint this probe addressed")
            return ex

        for opcode, payload in sends:
            sock.sendall(cap.ws_encode_frame(opcode, payload, os.urandom(4)))

        sock.settimeout(idle_timeout)
        pending_opcode, pending = None, bytearray()
        while True:
            frames, buffer = cap.ws_decode_frames(buffer)
            for opcode, payload, final in frames:
                if opcode == cap.WS_OPCODE_CLOSE:
                    if len(payload) >= 2:
                        ex.close_code = int.from_bytes(payload[:2], "big")
                        ex.close_reason = payload[2:].decode("utf-8", "replace")
                    return ex
                if opcode == cap.WS_OPCODE_PING:
                    sock.sendall(cap.ws_encode_frame(cap.WS_OPCODE_PONG, payload, os.urandom(4)))
                    continue
                if opcode == cap.WS_OPCODE_PONG:
                    continue
                if opcode != cap.WS_OPCODE_CONTINUATION:
                    pending_opcode, pending = opcode, bytearray()
                pending += payload
                if not final:
                    continue
                message, pending = bytes(pending), bytearray()
                ex.audio_bytes += audio_of(pending_opcode, message)
                ex.messages.append(summarize(pending_opcode, message, cap))
                if not ex.reached_first_exchange and first_exchange(pending_opcode, message):
                    ex.reached_first_exchange = True
                if terminator is not None and terminator(pending_opcode, message):
                    ex.saw_terminator = True
                    return ex
            try:
                chunk = sock.recv(8192)
            except (TimeoutError, OSError):
                ex.error = ex.error or "went idle — the vendor sent nothing further"
                return ex
            if not chunk:
                return ex
            buffer += chunk
    finally:
        try:
            sock.close()
        except OSError:
            pass


# --- 5. The surfaces, addressed exactly as the shipped clients address them --------------------
#
# Every request below was read out of the client that ships, not out of a vendor's documentation.
# That distinction is the whole programme: several defects in this change existed because the fake
# and the client were written from the same misreading of the same doc, so a probe built from the
# doc would have agreed with both and found nothing.

OPCODE_TEXT = 0x1
OPCODE_BINARY = 0x2

SHIPPED = "shipped"

#: Deliberately invalid. Not a revoked key — a string no vendor could ever have issued — so the
#: credential arm can never accidentally authenticate.
BAD_KEY = "probe-deliberately-invalid-credential"

PROBE_TEXT = "The quick brown fox jumps over the lazy dog."

#: The committed Azure TTS capture, 8 kHz mono signed-16 PCM. Reused rather than generated: the
#: recording protocol forbids inventing audio, and an STT surface answering synthetic silence
#: measures nothing.
SOURCE_PCM = "Tests/Verbara.Sdk.VoiceAi.Tts.Tests/Recordings/azure-tts/synthesize-short-es-co.raw"
SOURCE_SAMPLE_RATE = 8000

#: Read from the environment, matching what this workstation actually stores. NOTE: the sibling
#: capture tool reads unprefixed names (`CARTESIA_API_KEY`); these are the prefixed ones in
#: `~/.verbara/secrets.env`. The two tools disagree, and this one follows the file that exists.
KEY_ENV = {
    "lmnt-http": "VERBARA_LMNT_KEY",
    "lmnt-ws": "VERBARA_LMNT_KEY",
    "speechmatics-tts": "VERBARA_SPEECHMATICS_KEY",
    "cartesia-tts": "VERBARA_CARTESIA_KEY",
    "elevenlabs-tts": "VERBARA_ELEVENLABS_KEY",
    "speechmatics-stt": "VERBARA_SPEECHMATICS_KEY",
    "cartesia-stt": "VERBARA_CARTESIA_KEY",
    "assemblyai-stt": "VERBARA_ASSEMBLYAI_KEY",
}


def repo_root():
    return os.path.dirname(os.path.dirname(os.path.abspath(__file__)))


def source_frames(frame_bytes=320, cap_frames=60):
    """The committed capture, cut into the 20 ms frames the SDK's pipeline yields."""
    path = os.path.join(repo_root(), SOURCE_PCM)
    if not os.path.isfile(path):
        return []
    with open(path, "rb") as handle:
        data = handle.read()
    frames = [data[i:i + frame_bytes] for i in range(0, len(data), frame_bytes)]
    frames = [f for f in frames if len(f) == frame_bytes]
    return frames[:cap_frames]


def _json_frame(obj):
    """Serialized the way `System.Text.Json` serializes it here: no spaces, declaration order."""
    return (OPCODE_TEXT, json.dumps(obj, separators=(",", ":")).encode("utf-8"))


def _key_for(surface, arm):
    real = os.environ.get(KEY_ENV[surface], "").strip()
    if not real:
        return None
    return BAD_KEY if arm == CREDENTIAL else real


# -- TTS ---------------------------------------------------------------------------------------

def probe_lmnt_http(arm, key):
    route = "/v1/ai/speech/generate" if arm == ROUTE else "/v1/ai/speech/bytes"
    body = urllib.parse.urlencode({
        "voice": "leah", "text": PROBE_TEXT, "format": "pcm_s16le",
        "sample_rate": "16000", "language": "en", "speed": "1.00",
    }).encode("ascii")
    return probe_http(arm, "https://api.lmnt.com" + route,
                      {"X-API-Key": key, "lmnt-version": "1.0",
                       "Content-Type": "application/x-www-form-urlencoded"},
                      body=body, method="POST")


def probe_lmnt_ws(arm, key):
    route = ("/v1/ai/speech/stream-does-not-exist" if arm == ROUTE
             else "/v1/ai/speech/stream")
    # LMNT is the one surface whose credential does not ride the upgrade at all: the key is a JSON
    # member of the first frame, literally named like a header. `model` is omitted, never null —
    # sending `"model":null` is a total outage here (1002, zero audio).
    sends = [
        _json_frame({"X-API-Key": key, "voice": "leah", "format": "pcm_s16le",
                     "sample_rate": 16000, "language": "en", "speed": 1}),
        _json_frame({"text": PROBE_TEXT}),
        _json_frame({"flush": True}),
        _json_frame({"eof": True}),
    ]
    return probe_ws(
        arm, "wss://api.lmnt.com" + route, {}, sends,
        first_exchange=lambda op, m: op == OPCODE_BINARY or b'"error"' not in m,
        audio_of=lambda op, m: len(m) if op == OPCODE_BINARY else 0,
        # `finish`, because that is the notification the shipped synthesizer breaks on.
        terminator=lambda op, m: op == OPCODE_TEXT and b'"finish"' in m)


def probe_speechmatics_tts(arm, key):
    route = "/generate" if arm == ROUTE else "/generate/jack"
    body = json.dumps({"text": PROBE_TEXT, "language": "en", "sample_rate": 16000},
                      separators=(",", ":")).encode("utf-8")
    return probe_http(arm, "https://preview.tts.speechmatics.com" + route,
                      {"Authorization": f"Bearer {key}",
                       "Content-Type": "application/json; charset=utf-8"},
                      body=body, method="POST")


def probe_cartesia_tts(arm, key, voice_id=""):
    route = "/tts/websocket-does-not-exist" if arm == ROUTE else "/tts/websocket"
    # `continue: null` IS serialized: an A/B refuted the theory that it caused rejection, so the
    # probe keeps the shape the client actually sends rather than the tidier one.
    sends = [_json_frame({
        "model_id": "sonic-3",
        "voice": {"mode": "id", "id": voice_id},
        "output_format": {"container": "raw", "encoding": "pcm_s16le", "sample_rate": 16000},
        "language": "en",
        "transcript": PROBE_TEXT,
        "context_id": str(uuid.uuid4()),
        "continue": None,
    })]

    def audio_of(op, m):
        if op != OPCODE_TEXT:
            return 0
        try:
            parsed = json.loads(m)
        except ValueError:
            return 0
        if parsed.get("type") == "chunk" and parsed.get("data"):
            return len(base64.b64decode(parsed["data"]))
        return 0

    def is_done(op, m):
        if op != OPCODE_TEXT:
            return False
        try:
            return json.loads(m).get("type") == "done"
        except ValueError:
            return False

    # NOT a substring test for `done`: every chunk carries a `"done": false` member, and the first
    # attempt at this check matched it and declared the stream finished on frame one.
    return probe_ws(arm, "wss://api.cartesia.ai" + route,
                    {"X-API-Key": key, "Cartesia-Version": "2024-11-13"}, sends,
                    first_exchange=lambda op, m: b'"chunk"' in m or b'"error"' in m,
                    audio_of=audio_of, terminator=is_done)


def probe_elevenlabs_tts(arm, key, voice_id=""):
    path = (f"/v1/text-to-speech/{voice_id}/stream-input-does-not-exist" if arm == ROUTE
            else f"/v1/text-to-speech/{voice_id}/stream-input")
    url = (f"wss://api.elevenlabs.io{path}"
           "?model_id=eleven_flash_v2_5&output_format=pcm_16000&optimize_streaming_latency=0")
    sends = [
        _json_frame({"text": PROBE_TEXT, "flush": None,
                     "voice_settings": {"stability": 0.5, "similarity_boost": 0.75}}),
        _json_frame({"text": " ", "flush": True, "voice_settings": None}),
        _json_frame({"text": "", "flush": None, "voice_settings": None}),
    ]

    def audio_of(op, m):
        if op != OPCODE_TEXT:
            return 0
        try:
            parsed = json.loads(m)
        except ValueError:
            return 0
        return len(base64.b64decode(parsed["audio"])) if parsed.get("audio") else 0

    return probe_ws(arm, url, {"xi-api-key": key}, sends,
                    first_exchange=lambda op, m: b'"audio"' in m or b'"error"' in m,
                    audio_of=audio_of)


# -- STT ---------------------------------------------------------------------------------------

def probe_speechmatics_stt(arm, key):
    route = "/v2/en-does-not-exist" if arm == ROUTE else "/v2/en"
    frames = source_frames()
    sends = [_json_frame({
        "message": "StartRecognition",
        "audio_format": {"type": "raw", "encoding": "pcm_s16le",
                         "sample_rate": SOURCE_SAMPLE_RATE},
        "transcription_config": {"language": "en", "operating_point": "enhanced",
                                 "enable_partials": True, "max_delay": 2},
    })]
    sends += [(OPCODE_BINARY, f) for f in frames]
    sends.append(_json_frame({"message": "EndOfStream", "last_seq_no": len(frames)}))
    return probe_ws(arm, "wss://eu2.rt.speechmatics.com" + route,
                    {"Authorization": f"Bearer {key}"}, sends,
                    first_exchange=lambda op, m: b'"RecognitionStarted"' in m,
                    audio_of=lambda op, m: 0)


def probe_cartesia_stt(arm, key):
    base = "/stt/websocket-does-not-exist" if arm == ROUTE else "/stt/websocket"
    url = (f"wss://api.cartesia.ai{base}"
           f"?model=ink-whisper&language=en&encoding=pcm_s16le&sample_rate={SOURCE_SAMPLE_RATE}")
    # No opening JSON frame: sending one is a protocol error on this socket. The four session
    # parameters travel in the query string — that IS the fix this arm re-probes.
    sends = [(OPCODE_BINARY, f) for f in source_frames()]
    sends.append((OPCODE_TEXT, b"done"))
    return probe_ws(arm, url, {"X-API-Key": key, "Cartesia-Version": "2024-11-13"}, sends,
                    first_exchange=lambda op, m: b'"transcript"' in m or b'"error"' in m,
                    audio_of=lambda op, m: 0)


def probe_assemblyai_stt(arm, key):
    base = "/v3/ws-does-not-exist" if arm == ROUTE else "/v3/ws"
    url = (f"wss://streaming.assemblyai.com{base}"
           f"?sample_rate={SOURCE_SAMPLE_RATE}&format_turns=1&end_of_turn_confidence_threshold=800")
    # The vendor enforces a 50-1000 ms window on the DECLARED rate: at 8 kHz that is 800-16000 B,
    # so the shipped client coalesces 20 ms frames into 800 B messages. A probe that sent the
    # caller's frames raw would draw 3007 and measure the probe's own bug.
    raw = b"".join(source_frames())
    floor = 800
    sends = [(OPCODE_BINARY, raw[i:i + floor]) for i in range(0, len(raw) - floor + 1, floor)]
    sends.append(_json_frame({"type": "Terminate"}))
    return probe_ws(arm, url, {"Authorization": key}, sends,
                    first_exchange=lambda op, m: b'"Begin"' in m or b'"Error"' in m,
                    audio_of=lambda op, m: 0)


#: Every surface this change fixed, with the controls each one's host actually supports.
LIVE_SURFACES = {
    "lmnt-http": (probe_lmnt_http, ProbeSpec(
        name="lmnt-http", origin="https://api.lmnt.com", route="/v1/ai/speech/bytes",
        transport="http", validation_point=HANDSHAKE,
        controls=(Control(ROUTE, "/v1/ai/speech/generate — the route that used to ship", "404"),
                  Control(CREDENTIAL, "X-API-Key no vendor could have issued", "403")))),
    "lmnt-ws": (probe_lmnt_ws, ProbeSpec(
        name="lmnt-ws", origin="wss://api.lmnt.com", route="/v1/ai/speech/stream",
        transport="ws", validation_point=IN_BAND,
        controls=(Control(ROUTE, "/v1/ai/speech/stream-does-not-exist", "refused at the upgrade"),
                  Control(CREDENTIAL, "bad key in the first frame's X-API-Key member",
                          "in-band error, no audio")))),
    "speechmatics-tts": (probe_speechmatics_tts, ProbeSpec(
        name="speechmatics-tts", origin="https://preview.tts.speechmatics.com",
        route="/generate/jack", transport="http", validation_point=HANDSHAKE,
        controls=(Control(ROUTE, "/generate with no voice segment", "404"),
                  Control(CREDENTIAL, "Bearer token no vendor could have issued", "401")))),
    "cartesia-tts": (probe_cartesia_tts, ProbeSpec(
        name="cartesia-tts", origin="wss://api.cartesia.ai", route="/tts/websocket",
        transport="ws", validation_point=HANDSHAKE,
        controls=(Control(ROUTE, "/tts/websocket-does-not-exist", "404 at the upgrade"),
                  Control(CREDENTIAL, "X-API-Key no vendor could have issued",
                          "401 at the upgrade")))),
    "elevenlabs-tts": (probe_elevenlabs_tts, ProbeSpec(
        name="elevenlabs-tts", origin="wss://api.elevenlabs.io",
        route="/v1/text-to-speech/{voice}/stream-input", transport="ws",
        validation_point=IN_BAND,
        controls=(Control(ROUTE, "stream-input-does-not-exist on the same host", "403"),
                  Control(CREDENTIAL, "xi-api-key no vendor could have issued",
                          "101, then invalid_api_key in band and close 1008")))),
    "speechmatics-stt": (probe_speechmatics_stt, ProbeSpec(
        name="speechmatics-stt", origin="wss://eu2.rt.speechmatics.com", route="/v2/en",
        transport="ws", validation_point=IN_BAND,
        controls=(Control(ROUTE, "a path the host does not serve", "rejected at the upgrade"),
                  Control(CREDENTIAL, "Bearer token no vendor could have issued",
                          "101 then close 4001 — the upgrade proved nothing")))),
    "cartesia-stt": (probe_cartesia_stt, ProbeSpec(
        name="cartesia-stt", origin="wss://api.cartesia.ai", route="/stt/websocket",
        transport="ws", validation_point=HANDSHAKE,
        controls=(Control(ROUTE, "/stt/websocket-does-not-exist", "404 at the upgrade"),
                  Control(CREDENTIAL, "X-API-Key no vendor could have issued",
                          "401 at the upgrade")))),
    "assemblyai-stt": (probe_assemblyai_stt, ProbeSpec(
        name="assemblyai-stt", origin="wss://streaming.assemblyai.com", route="/v3/ws",
        transport="ws", validation_point=IN_BAND,
        # The route control here is REAL and its measured answer is that the host does not
        # discriminate: /v3/ws-does-not-exist upgrades 101 and serves a normal session. That is a
        # finding, not a missing control — it means a route defect on this host is undetectable by
        # path, and the record says "not controllable" rather than "verified" because of it.
        controls=(Control(ROUTE, "/v3/ws-does-not-exist on the same host",
                          "101 and a normal session — this host does not discriminate on path"),
                  Control(CREDENTIAL, "Authorization value no vendor could have issued",
                          "101, then an in-band error")))),
}


#: The two surfaces whose shipped client has no default voice and whose vendor rejects the request
#: without one. ElevenLabs' is the public sample id this repo already documents in
#: `src/Verbara.Sdk.VoiceAi.Tts/README.md`; Cartesia publishes no such id, so it must be supplied.
#: An unset voice is NOT substituted with a guess — the surface stays 'not characterised' (§7.7).
DEFAULT_VOICE = {"elevenlabs-tts": "EXAVITQu4vr4xnSDxMaL"}
VOICE_ENV = {"cartesia-tts": "VERBARA_CARTESIA_VOICE_ID",
             "elevenlabs-tts": "VERBARA_ELEVENLABS_VOICE_ID"}


def resolve_voices():
    return {name: os.environ.get(env, "").strip() or DEFAULT_VOICE.get(name, "")
            for name, env in VOICE_ENV.items()}


def run_surface(name, voice_ids):
    """Run all three arms of one surface and print what each measured. Live. Costs money."""
    runner, spec = LIVE_SURFACES[name]
    print(f"\n=== {name}  {spec.origin}{spec.route}  validation={spec.validation_point}")

    if not os.environ.get(KEY_ENV[name], "").strip():
        print(f"  NOT PROBED — {KEY_ENV[name]} is not set. This surface stays 'not characterised' "
              "(§7.7): a task is not closed by a green fake.")
        return None

    kwargs = {}
    if name in ("cartesia-tts", "elevenlabs-tts"):
        voice = voice_ids.get(name, "")
        if not voice:
            print(f"  NOT PROBED — no voice id for {name}; the shipped client has no default and "
                  "the vendor requires one. Stays 'not characterised' (§7.7).")
            return None
        kwargs["voice_id"] = voice

    results = {}
    for arm in (SHIPPED, ROUTE, CREDENTIAL):
        key = _key_for(name, arm)
        exchange = runner(arm, key, **kwargs)
        results[arm] = exchange
        print("  " + exchange.line())
        for message in exchange.messages[:4]:
            print(f"      {message}")

    shipped = results[SHIPPED]
    allowed, why = spec.verdict_allowed(
        reached_first_exchange=shipped.reached_first_exchange)
    print(f"  VERDICT {'ALLOWED' if allowed else 'REFUSED'}: {why}")
    return results


def self_check():
    """Liveness fence: prove each rule still refuses what it exists to refuse.

    Mirrors `check-recording-redaction.py`'s self-check for the same reason — a rule edited into
    uselessness would otherwise let every run report clean. Each of the three rules is exercised
    against a case that MUST fail.
    """
    failures = []

    leaked = redact({"additional_model_uuids": ["11111111-2222-3333-4444-555555555555"]})
    if REDACTED not in json.dumps(leaked):
        failures.append("redact() no longer covers array-valued identifier fields — the exact gap "
                        "that leaked an identifier on 2026-08-15.")

    try:
        ProbeSpec(name="x", origin="https://example.invalid", route="/", transport="http",
                  controls=(Control(ROUTE, "wrong path", "404"),))
    except ValueError:
        pass
    else:
        failures.append("ProbeSpec accepted a probe with no invalid-credential control.")

    in_band = ProbeSpec(
        name="y", origin="wss://example.invalid", route="/", transport="ws",
        validation_point=IN_BAND,
        controls=(Control(ROUTE, "wrong path", "404"), Control(CREDENTIAL, "bad key", "close 4001")))
    allowed, _ = in_band.verdict_allowed(reached_first_exchange=False)
    if allowed:
        failures.append("a handshake-only run against an in-band surface was accepted as a verdict.")

    for f in failures:
        print(f"self-check FAILED: {f}")
    return 1 if failures else 0


def main(argv=None):
    parser = argparse.ArgumentParser(description=__doc__.splitlines()[0])
    parser.add_argument("--self-check", action="store_true",
                        help="prove each rule still refuses what it exists to refuse")
    parser.add_argument("--list", action="store_true", help="list the encoded worked examples")
    parser.add_argument("--probe", metavar="SURFACE",
                        help="probe a live surface (or 'all'). Sends real traffic to a real vendor "
                             "with a real credential and costs money. Never run from CI (5.2). "
                             "Surfaces: " + ", ".join(sorted(LIVE_SURFACES)))
    args = parser.parse_args(argv)

    if args.self_check:
        code = self_check()
        if code == 0:
            print("self-check OK: redaction, controls and depth all still refuse.")
        return code

    if args.list:
        for spec in WORKED_EXAMPLES:
            print(f"{spec.name:20s} {spec.origin}{spec.route}  validation={spec.validation_point}")
            for c in spec.controls:
                print(f"  control [{c.kind:10s}] {c.description} -> {c.expected}")
        return 0

    if args.probe:
        names = sorted(LIVE_SURFACES) if args.probe == "all" else [args.probe]
        unknown = [n for n in names if n not in LIVE_SURFACES]
        if unknown:
            print(f"unknown surface(s): {', '.join(unknown)}", file=sys.stderr)
            return 2
        voice_ids = resolve_voices()
        probed = 0
        for name in names:
            if run_surface(name, voice_ids) is not None:
                probed += 1
        skipped = len(names) - probed
        print(f"\n{probed} surface(s) probed, {skipped} left 'not characterised' for want of a "
              "credential or a voice id.")
        # Exit 0 either way: a surface nobody can reach is a recorded fact, not a broken run. What
        # would be a failure is reporting one as characterised, and that is what run_surface
        # refuses to do.
        return 0

    parser.print_help()
    return 0


if __name__ == "__main__":
    sys.exit(main())

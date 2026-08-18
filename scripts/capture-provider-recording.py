#!/usr/bin/env python3
"""Capture a real speech-provider response into a committed fixture (ADR-0041 D4).

Implements steps 3-8 of `docs/guides/provider-recording-protocol.md` §3 for six provider
surfaces — four STT, two TTS — so a capture is reproducible instead of being a one-off ceremony
performed by hand: it sends the same request the SDK sends, redacts, normalizes, writes the
provenance sidecar and enforces the size cap.

    python3 scripts/capture-provider-recording.py openai-whisper
    python3 scripts/capture-provider-recording.py azure-openai-whisper
    python3 scripts/capture-provider-recording.py google-speech
    python3 scripts/capture-provider-recording.py speechmatics-tts
    python3 scripts/capture-provider-recording.py lmnt-http
    python3 scripts/capture-provider-recording.py cartesia-stt
    python3 scripts/capture-provider-recording.py cartesia-stt-error

Credentials come from the environment and are never written, echoed or stored:

    openai-whisper         OPENAI_API_KEY
    azure-openai-whisper   AZURE_OPENAI_API_KEY, AZURE_OPENAI_ENDPOINT,
                           AZURE_OPENAI_DEPLOYMENT, [AZURE_OPENAI_API_VERSION]
    google-speech          GOOGLE_SPEECH_API_KEY *or* GOOGLE_ACCESS_TOKEN, exactly one —
                           see google_speech_plan for why the choice exists at all
    speechmatics-tts       SPEECHMATICS_API_KEY
    lmnt-http              LMNT_API_KEY
    cartesia-stt           CARTESIA_API_KEY
    cartesia-stt-error     CARTESIA_API_KEY

**Use a throwaway credential and revoke it afterwards** (protocol §3.3). A key that never had
access to production data cannot leak production identifiers through a response body.

Source audio (protocol §6): the committed Azure TTS capture — synthetic speech from a prebuilt
neural voice over a fictional sentence. No microphone is involved and no identifiable person's
voice is ever submitted. See SOURCE_AUDIO_DESCRIPTION for the terms note this carries. The TTS
surfaces submit *text*, not audio, and record `origin: "not-applicable"` instead.

What reaches disk depends on the surface, because a TTS response is not a JSON document and one
provider's audio may not be committed at all:

    json      the vendor's JSON, redacted and pretty-printed   — both Whisper surfaces, Google
    binary    the vendor's bytes verbatim, under the hard cap  — Speechmatics TTS
    envelope  status, headers, media type, length and observed chunk boundaries, and never the
              audio bytes (protocol §7's conservative fallback) — LMNT HTTP

The last two surfaces are WebSocket sessions rather than requests, and they are shaped
differently for a reason that is not incidental. A request has one response; a session has
several frames of interest, and which frame is which is decided by reading them. So a session
plan names its frames by a predicate and the capture writes one fixture per frame it actually
saw — **a frame the service did not send produces no file and says so**. That silence is the
finding. Filling the gap from the vendor's documentation is precisely what this protocol exists
to prevent, and it is how a fixture ends up asserting a shape nobody ever received (measured:
the authored Cartesia `flush_done` carried `is_final` true; the service sends false).

stdlib only, by the same rule as `scripts/check-*.py`: a fixture tool that needs `pip install`
is a fixture tool that stops being run.
"""

from __future__ import annotations

import argparse
import base64
import datetime as _dt
import hashlib
import io
import json
import os
import socket
import ssl
import sys
import time
import urllib.error
import urllib.parse
import urllib.request
import uuid
import wave
from pathlib import Path

# --- Protocol constants -------------------------------------------------------------------

SCHEMA = "verbara.recording-provenance/1"
SCENARIO_SLUG = "transcribe-short-es-co"
TTS_SCENARIO_SLUG = "synthesize-short-en-us"

STT_RECORDINGS = Path("Tests/Verbara.Sdk.VoiceAi.Stt.Tests/Recordings")
TTS_RECORDINGS = Path("Tests/Verbara.Sdk.VoiceAi.Tts.Tests/Recordings")
SOURCE_PCM = Path(
    "Tests/Verbara.Sdk.VoiceAi.Tts.Tests/Recordings/azure-tts/synthesize-short-es-co.raw"
)

# The source capture's format, from its own provenance sidecar.
SOURCE_SAMPLE_RATE = 8000
SOURCE_CHANNELS = 1
SOURCE_SAMPLE_WIDTH = 2

# What a TTS surface says. Fictional by protocol §6, which governs a capture's spoken text as
# strictly as it governs submitted audio: no real name, number or booking reference. English
# because both TTS surfaces here ship an English voice and language as their default, and a
# capture taken against non-default options is a capture of a request production never sends.
TTS_INPUT_TEXT = "The system recorded the request successfully."

# Advisory ceiling for a text capture (protocol §8). The hard 256 KiB cap is for binary.
TEXT_SIZE_SMELL_BYTES = 64 * 1024

# The binary cap (protocol §8) is a limit, not a hint: every clone of this public repo carries
# every version of every committed blob forever, and an oversized speech sample also erodes the
# "these are test fixtures, not a voice corpus" reading the vendor terms in §7 rest on.
BINARY_SIZE_CAP_BYTES = 256 * 1024

# Both HTTP-streaming providers read their response through an 8 KiB buffer
# (SpeechmaticsSpeechSynthesizer.ChunkSize, LmntSpeechSynthesizer.HttpChunkSize). Reading at the
# same size is what makes the envelope's chunk boundaries an observation of what the SDK sees
# rather than an artifact of this script's own buffer choice.
READ_CHUNK_BYTES = 8192

# The extension a capture takes, keyed by what the vendor said it sent (protocol §2 fixes the
# vocabulary). Consulted only for binary captures: a response labelled audio/mpeg must not land
# in a file called .wav just because the SDK's tests expect WAV.
MEDIA_TYPE_EXTENSIONS = {
    "application/json": "json",
    "audio/basic": "raw",
    "audio/l16": "raw",
    "audio/mp3": "mp3",
    "audio/mpeg": "mp3",
    "audio/ogg": "ogg",
    "audio/opus": "opus",
    "audio/raw": "raw",
    "audio/vnd.wave": "wav",
    "audio/wav": "wav",
    "audio/wave": "wav",
    "audio/x-wav": "wav",
    "text/plain": "txt",
}

ARTIFACT_MODES = ("json", "binary", "envelope")

# Checked before the request goes out, not after: a plan missing a key would otherwise fail
# somewhere past `send`, having already spent a capture — and the operator's answer to that is
# usually to re-run it, spending another.
REQUIRED_PLAN_KEYS = (
    "product",
    "url",
    "endpoint_template",
    "api_version",
    "terms_verdict",
    "terms_basis",
    "secrets",
    "redaction_applied",
    "redaction_notes",
    "notes",
    "verify",
    "request_summary",
    "headers",
    "body",
)

PLACEHOLDER_API_KEY = "REDACTED-API-KEY"

# Protocol §4's placeholder for "any GUID-shaped request/session/trace ID". Google's requestId is
# a 19-digit string rather than a GUID, but the placeholder table's purpose is a form a reviewer
# recognizes and the guard's allowlist accepts — inventing a second numeric form for the same
# meaning would weaken both.
PLACEHOLDER_CORRELATION_ID = "00000000-0000-0000-0000-000000000000"

# One source recording, but not one description: what a provider is *sent* differs by surface,
# and a sidecar claiming a RIFF header where the SDK sent headerless PCM would misdescribe the
# very request the capture exists to pin down. The halves that are true either way are shared.
SOURCE_AUDIO_PREFIX = (
    "Synthetic speech, not a recording of any person: the committed Azure TTS capture "
    "Tests/Verbara.Sdk.VoiceAi.Tts.Tests/Recordings/azure-tts/synthesize-short-es-co.raw "
    "(prebuilt neural voice es-CO-SalomeNeural, 8 kHz 16-bit mono PCM, 3.76 s) reading the "
    "fictional sentence 'El sistema registró la solicitud correctamente.', "
)

SOURCE_AUDIO_REUSE = (
    " Reusing that capture keeps one cleared, reviewed audio artifact in the repo instead of "
    "adding a second. It is submitted for a single transcription, which is inference, not "
    "training: "
)

SOURCE_AUDIO_DESCRIPTION = (
    SOURCE_AUDIO_PREFIX
    + "wrapped in a 44-byte canonical RIFF/WAVE header identical to the one "
    "WhisperSpeechRecognizer builds."
    + SOURCE_AUDIO_REUSE
    + "neither Azure's bar on using Output Content as synthetic training data for a "
    "similar service nor OpenAI Services Agreement 3.3(e) on developing competing models is "
    "engaged."
)

GOOGLE_SOURCE_AUDIO_DESCRIPTION = (
    SOURCE_AUDIO_PREFIX
    + "submitted as raw LINEAR16 with no container at all, base64-encoded into audio.content "
    "exactly as GoogleSpeechRecognizer submits the frames it drained."
    + SOURCE_AUDIO_REUSE
    + "Google's restriction on using Generated Output to create or improve a similar model is "
    "not engaged."
)

SOURCE_AUDIO_SYNTHETIC = {
    "origin": "synthetic",
    "description": SOURCE_AUDIO_DESCRIPTION,
    "license": "n/a",
}


def tts_source_audio(voice: str) -> dict:
    """The `source_audio` block for a surface whose input is text, not a recording.

    Modelled on the one committed Azure TTS capture rather than invented: protocol §5 has no
    field for "input text", so the direction is expressed as `origin: "not-applicable"` with the
    sentence and the voice named in the description. The voice matters as much as the sentence —
    §7's conditions turn on the capture having used a prebuilt catalogue voice and never one
    built from a real person.
    """
    return {
        "origin": "not-applicable",
        "description": (
            f"TTS input is a fixed English sentence authored for this fixture "
            f"('{TTS_INPUT_TEXT}') — no source recording exists, and the sentence names no real "
            f"person, organisation or number. Rendered by the prebuilt catalogue voice "
            f"'{voice}'; no custom or cloned voice was used."
        ),
        "license": "n/a",
    }

# Headers whose *values* are safe to record in the sidecar. Everything else contributes its
# name only: `openai-organization`, `x-request-id`, `azureml-model-session` and friends are
# account or correlation identifiers (protocol §4) and must not reach a committed file.
HEADER_VALUES_SAFE_TO_RECORD = frozenset({"content-type"})


class CaptureError(RuntimeError):
    """A capture could not be produced. Carries a message meant for the operator."""


# --- Pure helpers (unit-tested in scripts/tests/) -----------------------------------------


def wav_from_pcm(
    pcm: bytes,
    sample_rate: int = SOURCE_SAMPLE_RATE,
    channels: int = SOURCE_CHANNELS,
    sample_width: int = SOURCE_SAMPLE_WIDTH,
) -> bytes:
    """Wrap raw PCM in a canonical 44-byte RIFF/WAVE header.

    Mirrors `WhisperSpeechRecognizer.AddWavHeaderStatic` so the bytes the provider sees during
    capture are the bytes it sees in production.
    """
    buffer = io.BytesIO()
    with wave.open(buffer, "wb") as handle:
        handle.setnchannels(channels)
        handle.setsampwidth(sample_width)
        handle.setframerate(sample_rate)
        handle.writeframes(pcm)
    return buffer.getvalue()


def build_multipart(
    boundary: str,
    file_field: str,
    filename: str,
    file_bytes: bytes,
    text_fields: dict[str, str],
) -> bytes:
    """Build a multipart/form-data body shaped like .NET's `MultipartFormDataContent`.

    The file part carries no Content-Type (the SDK adds `ByteArrayContent` without one) and the
    text parts carry `text/plain; charset=utf-8` (the `StringContent` default). Fidelity here is
    the point: a capture taken against a differently-shaped request proves less than it claims.
    """
    if not boundary:
        raise ValueError("boundary must be a non-empty string")

    out = bytearray()
    marker = f"--{boundary}\r\n".encode()

    out += marker
    out += (
        f'Content-Disposition: form-data; name="{file_field}"; filename="{filename}"\r\n\r\n'
    ).encode()
    out += file_bytes
    out += b"\r\n"

    for name, value in text_fields.items():
        out += marker
        out += f'Content-Disposition: form-data; name="{name}"\r\n'.encode()
        out += b"Content-Type: text/plain; charset=utf-8\r\n\r\n"
        out += value.encode()
        out += b"\r\n"

    out += f"--{boundary}--\r\n".encode()
    return bytes(out)


def redact(text: str, secrets: list[str]) -> str:
    """Replace every occurrence of a known secret with the protocol §4 placeholder.

    Defence in depth, not the primary control: a well-behaved provider never echoes the key. The
    empty-string guard matters — replacing "" would splice the placeholder between every
    character of the body.
    """
    for secret in secrets:
        if secret:
            text = text.replace(secret, PLACEHOLDER_API_KEY)
    return text


def deployments_base(endpoint: str) -> str:
    """Normalize an Azure OpenAI endpoint to the deployments base the SDK expects.

    `AzureWhisperOptions.Endpoint` is documented as `https://<resource>.openai.azure.com/openai/
    deployments`, but the resource root is what the portal shows and what operators keep on hand.
    Accept either rather than making the capture fail on a trailing path segment.
    """
    base = endpoint.strip().rstrip("/")
    if not base:
        raise ValueError("endpoint must be a non-empty string")
    return base if base.endswith("/openai/deployments") else base + "/openai/deployments"


def assert_no_account_token_leak(body: str, account_tokens: dict[str, str]) -> None:
    """Fail if an account-scoped name reached the response body.

    These cannot be blanket-replaced the way a credential can: a deployment named `whisper` is
    also an ordinary word, and substituting it inside a transcript would corrupt the vendor's
    bytes that the capture exists to preserve. Silently leaking and silently corrupting are both
    unacceptable, so the tool stops and hands the decision to a human (protocol §4).
    """
    for kind, value in account_tokens.items():
        if value and value in body:
            raise CaptureError(
                f"The response body contains the {kind} name. It cannot be replaced "
                f"automatically without risking corruption of the vendor's payload. Redact it by "
                f"hand into the <{kind}> placeholder, record it under redaction.applied, and "
                "re-run the redaction guard."
            )


def assert_no_secret_bytes(payload: bytes, values: list[str]) -> None:
    """Fail if a credential or account name reached a payload whose bytes cannot be rewritten.

    The text path can redact, because pretty-printed JSON survives a substring replacement. A
    binary payload cannot: rewriting bytes inside a codec stream corrupts exactly the thing a
    binary capture exists to preserve (protocol §3.6). So the tool stops and hands the decision
    to a human, as it does for account tokens in a text body.
    """
    for value in values:
        if value and value.encode("utf-8") in payload:
            raise CaptureError(
                "A credential or account name appears in the response bytes. A binary payload "
                "cannot be redacted without corrupting the vendor's bytes the capture exists to "
                "preserve, so nothing was written. Re-capture with a different credential, or "
                "commit an envelope instead (protocol §7)."
            )


def response_media_type(headers: list[tuple[str, str]]) -> str | None:
    """The media type the vendor declared, parameters stripped and lowercased."""
    for name, value in headers:
        if name.lower() == "content-type":
            return value.split(";")[0].strip().lower() or None
    return None


def redact_correlation_fields(raw: str, fields: tuple[str, ...]) -> tuple[str, list[str]]:
    """Replace correlation identifiers the vendor puts in its *body*, at any depth.

    `redact()` can only remove values we already knew — the credential we sent. A request ID is
    minted by the provider and is unknowable in advance, so it needs the opposite treatment:
    name the *field*, replace whatever is in it. Protocol §4 bans these outright ("request IDs,
    trace IDs, session IDs … tie a public artifact to a real, billed account"), and Google's
    `requestId` is exactly one.

    The field is kept and only its value replaced. That is deliberate: an unmodelled sibling is
    precisely what these fixtures exist to hold a parser against, so deleting the key would
    destroy the property the capture was taken for.
    """
    if not fields:
        return raw, []

    applied: list[str] = []

    def walk(node: object) -> object:
        if isinstance(node, dict):
            scrubbed = {}
            for key, value in node.items():
                if key in fields:
                    applied.append(key)
                    scrubbed[key] = PLACEHOLDER_CORRELATION_ID
                else:
                    scrubbed[key] = walk(value)
            return scrubbed
        if isinstance(node, list):
            return [walk(item) for item in node]
        return node

    scrubbed = walk(json.loads(raw))
    return json.dumps(scrubbed, ensure_ascii=False), sorted(set(applied))


def normalize_json(raw: str) -> str:
    """Pretty-print with 2-space indent, LF endings and a trailing newline (protocol §3.6).

    `ensure_ascii=False` keeps the vendor's UTF-8 intact — the Spanish transcript carries
    accented characters, and escaping them to \\uXXXX would hide whether the client decodes
    UTF-8 correctly, which is one of the things this fixture exists to prove.
    """
    parsed = json.loads(raw)
    return json.dumps(parsed, indent=2, ensure_ascii=False, sort_keys=False) + "\n"


def describe_headers(headers: list[tuple[str, str]]) -> str:
    """Summarize response headers for the sidecar: names always, values only when safe."""
    parts = []
    for name, value in sorted(headers, key=lambda pair: pair[0].lower()):
        lowered = name.lower()
        if lowered in HEADER_VALUES_SAFE_TO_RECORD:
            parts.append(f"{lowered}: {value}")
        else:
            parts.append(lowered)
    return ", ".join(parts)


def build_envelope(
    *,
    status: int,
    headers: list[tuple[str, str]],
    chunk_sizes: list[int],
    body_omitted: str,
) -> dict:
    """Describe a response *without* recording its body (protocol §7, LMNT fallback).

    Everything ADR-0041 wanted from an HTTP capture except the payload itself: a real status, a
    real header set, a real content length and real chunk boundaries, so the strict matcher and
    the frame-chunking path are still driven by something the vendor actually sent.

    `content_length` is summed from the reads rather than copied from the header, because the
    header is absent on a chunked response while the reads are what the SDK's own loop observes.
    """
    return {
        "status": status,
        "media_type": response_media_type(headers) or "unknown",
        "content_length": sum(chunk_sizes),
        "chunk_sizes": chunk_sizes,
        "headers": describe_headers(headers),
        "body_omitted": body_omitted,
    }


def build_sidecar(
    *,
    provider: str,
    product: str,
    endpoint: str,
    api_version: str,
    captured_utc: str,
    payload: bytes,
    redaction_applied: list[str],
    redaction_notes: str,
    terms_verdict: str,
    terms_basis: str,
    notes: str,
    media_type: str = "application/json",
    capture_class: str = "recorded",
    source_audio: dict | None = None,
) -> dict:
    """Assemble the provenance sidecar (protocol §5).

    The last three arguments are what a non-STT-JSON capture varies: a TTS response is not
    `application/json`, and a surface whose input is text carries a different `source_audio`
    block. They default to the STT-JSON answers so the two Whisper surfaces keep emitting the
    byte-identical sidecar they emitted before there was anything else to emit.
    """
    return {
        "schema": SCHEMA,
        "class": capture_class,
        "provider": provider,
        "product": product,
        "endpoint": endpoint,
        "api_version": api_version,
        "captured_utc": captured_utc,
        "media_type": media_type,
        "bytes": len(payload),
        "sha256": hashlib.sha256(payload).hexdigest(),
        # Copied, not aliased: a sidecar the caller can mutate through is a fixture that changes
        # between two captures in the same run.
        "source_audio": dict(source_audio or SOURCE_AUDIO_SYNTHETIC),
        "redaction": {"applied": redaction_applied, "notes": redaction_notes},
        "terms": {
            "verdict": terms_verdict,
            "basis": terms_basis,
            "checked_utc": captured_utc,
        },
        "notes": notes,
    }


# --- Minimal WebSocket client (RFC 6455, stdlib only) --------------------------------------
#
# Written rather than imported because of the module docstring's rule: this tool is stdlib only,
# and `websockets` would put it behind a `pip install`. Only what a capture needs is here — a
# client handshake, masked text and binary sends, message reassembly across continuation frames,
# and a close. No extensions, no compression, no server role.
#
# The codec is split from the socket deliberately, so the parts that can be wrong in a way a test
# can catch are pure functions and the part that cannot be tested without a network is four lines.

WS_GUID = "258EAFA5-E914-47DA-95CA-C5AB0DC85B11"
WS_OPCODE_CONTINUATION = 0x0
WS_OPCODE_TEXT = 0x1
WS_OPCODE_BINARY = 0x2
WS_OPCODE_CLOSE = 0x8
WS_OPCODE_PING = 0x9
WS_OPCODE_PONG = 0xA


def ws_accept_token(key: str) -> str:
    """The `Sec-WebSocket-Accept` value a conforming server must return for `key` (RFC 6455 §4.1).

    Checking it is not ceremony: it is the one thing that distinguishes a real WebSocket peer from
    any HTTP endpoint that happens to answer `101`, and a capture that skipped it could record a
    proxy's idea of the session as though it were the vendor's.
    """
    digest = hashlib.sha1((key + WS_GUID).encode("ascii")).digest()  # noqa: S324 - protocol-mandated
    return base64.b64encode(digest).decode("ascii")


def ws_handshake_request(url: str, headers: dict[str, str], key: str) -> tuple[str, int, bytes]:
    """Split `url` into host/port and render the upgrade request. Pure — no socket is opened."""
    parts = urllib.parse.urlsplit(url)
    if parts.scheme not in ("ws", "wss"):
        raise CaptureError(f"WebSocket URL must be ws:// or wss://, got {parts.scheme!r}.")
    port = parts.port or (443 if parts.scheme == "wss" else 80)
    target = parts.path or "/"
    if parts.query:
        target = f"{target}?{parts.query}"

    lines = [
        f"GET {target} HTTP/1.1",
        f"Host: {parts.hostname}" + (f":{parts.port}" if parts.port else ""),
        "Upgrade: websocket",
        "Connection: Upgrade",
        f"Sec-WebSocket-Key: {key}",
        "Sec-WebSocket-Version: 13",
    ]
    lines += [f"{name}: {value}" for name, value in headers.items()]
    return parts.hostname or "", port, ("\r\n".join(lines) + "\r\n\r\n").encode("ascii")


def ws_encode_frame(opcode: int, payload: bytes, mask: bytes) -> bytes:
    """Encode one final, masked client frame. `mask` is a parameter so this stays deterministic."""
    if len(mask) != 4:
        raise CaptureError("A client frame mask must be exactly 4 bytes.")
    header = bytearray([0x80 | opcode])
    length = len(payload)
    if length < 126:
        header.append(0x80 | length)
    elif length < 65536:
        header.append(0x80 | 126)
        header += length.to_bytes(2, "big")
    else:
        header.append(0x80 | 127)
        header += length.to_bytes(8, "big")
    header += mask
    masked = bytes(byte ^ mask[i % 4] for i, byte in enumerate(payload))
    return bytes(header) + masked


def ws_decode_frames(buffer: bytes) -> tuple[list[tuple[int, bytes, bool]], bytes]:
    """Decode whole frames out of `buffer` into `(opcode, payload, final)`, plus the leftover tail.

    Frames, not messages: reassembly across continuations is the caller's job, which is what lets
    a capture observe that a message *was* fragmented rather than having that fact hidden from it.
    A partial frame decodes to nothing and stays in the tail — never to a truncated payload, which
    would reach the caller as a JSON parse error blamed on the vendor.
    """
    frames: list[tuple[int, bytes, bool]] = []
    offset = 0
    while True:
        if len(buffer) - offset < 2:
            break
        first, second = buffer[offset], buffer[offset + 1]
        opcode = first & 0x0F
        final = bool(first & 0x80)
        length = second & 0x7F
        cursor = offset + 2
        if length == 126:
            if len(buffer) - cursor < 2:
                break
            length = int.from_bytes(buffer[cursor:cursor + 2], "big")
            cursor += 2
        elif length == 127:
            if len(buffer) - cursor < 8:
                break
            length = int.from_bytes(buffer[cursor:cursor + 8], "big")
            cursor += 8
        if second & 0x80:
            raise CaptureError("Server frames must not be masked (RFC 6455 §5.1).")
        if len(buffer) - cursor < length:
            break
        frames.append((opcode, buffer[cursor:cursor + length], final))
        offset = cursor + length
    return frames, buffer[offset:]


class WebSocketSession:
    """A client session over `ssl`/`socket`, holding only what a capture needs."""

    def __init__(self, url: str, headers: dict[str, str], timeout: int) -> None:
        key = base64.b64encode(uuid.uuid4().bytes).decode("ascii")
        host, port, request = ws_handshake_request(url, headers, key)
        raw = socket.create_connection((host, port), timeout=timeout)
        self._sock = (
            ssl.create_default_context().wrap_socket(raw, server_hostname=host)
            if url.startswith("wss://")
            else raw
        )
        self._sock.sendall(request)
        self._buffer = b""
        self._read_handshake(key)

    def _read_handshake(self, key: str) -> None:
        while b"\r\n\r\n" not in self._buffer:
            chunk = self._sock.recv(READ_CHUNK_BYTES)
            if not chunk:
                raise CaptureError("The provider closed the connection during the upgrade.")
            self._buffer += chunk
        head, self._buffer = self._buffer.split(b"\r\n\r\n", 1)
        text = head.decode("latin-1")
        status = text.split("\r\n", 1)[0]
        if " 101 " not in status:
            raise CaptureError(f"The provider refused the upgrade: {status}")
        accept = ws_accept_token(key)
        if accept.lower() not in text.lower():
            raise CaptureError(
                "The provider's Sec-WebSocket-Accept did not match the key sent — the peer that "
                "answered is not the WebSocket endpoint this capture addressed."
            )

    def send(self, opcode: int, payload: bytes) -> None:
        self._sock.sendall(ws_encode_frame(opcode, payload, os.urandom(4)))

    def messages(self, idle_timeout: int):
        """Yield `(opcode, payload)` per reassembled message until the peer closes or goes idle."""
        self._sock.settimeout(idle_timeout)
        pending_opcode: int | None = None
        pending = bytearray()
        while True:
            frames, self._buffer = ws_decode_frames(self._buffer)
            for opcode, payload, final in frames:
                if opcode == WS_OPCODE_CLOSE:
                    return
                if opcode == WS_OPCODE_PING:
                    self.send(WS_OPCODE_PONG, payload)
                    continue
                if opcode == WS_OPCODE_PONG:
                    continue
                if opcode != WS_OPCODE_CONTINUATION:
                    pending_opcode = opcode
                    pending = bytearray()
                pending += payload
                if final:
                    yield (pending_opcode or WS_OPCODE_TEXT, bytes(pending))
                    pending = bytearray()
            try:
                chunk = self._sock.recv(READ_CHUNK_BYTES)
            except (TimeoutError, OSError):
                return
            if not chunk:
                return
            self._buffer += chunk

    def close(self) -> None:
        try:
            self.send(WS_OPCODE_CLOSE, (1000).to_bytes(2, "big"))
        except OSError:
            pass
        finally:
            self._sock.close()


# --- Provider definitions -----------------------------------------------------------------

# Every plan is merged over these, so a plan states only what its surface does differently. The
# defaults are the STT-JSON answers this tool started with, which is also why adding the other
# three surfaces changed no byte either Whisper surface writes.
PLAN_DEFAULTS = {
    "recordings_dir": STT_RECORDINGS,
    "scenario_slug": SCENARIO_SLUG,
    "artifact": "json",
    "media_type": "application/json",
    "extension": "json",
    "capture_class": "recorded",
    "source_audio": SOURCE_AUDIO_SYNTHETIC,
    "account_tokens": {},
    "correlation_fields": (),
}

# The surfaces that submit audio. The TTS ones submit text, so requiring the committed source
# capture for them would fail a capture over a file it never reads.
AUDIO_INPUT_PROVIDERS = frozenset({"openai-whisper", "azure-openai-whisper", "google-speech"})

JSON_REDACTION_NOTES = (
    "Body is the vendor's JSON, unmodified apart from pretty-printing to 2-space indent with a "
    "trailing newline (protocol §3.6); no field was removed."
)

GOOGLE_REDACTION_NOTES = (
    "Body is the vendor's JSON, pretty-printed to 2-space indent with a trailing newline "
    "(protocol §3.6). No field was removed, but one value was replaced: Google returns a "
    "`requestId` correlation identifier, which protocol §4 bans from a committed file. The key is "
    "kept — an unmodelled sibling is exactly what this fixture holds the parser against — and only "
    "its value carries the §4 placeholder."
)

MULTIPART_FIDELITY_NOTE = (
    "Captured by scripts/capture-provider-recording.py, which sends the same multipart shape the "
    "SDK sends (file part without Content-Type, text parts as text/plain; charset=utf-8) so the "
    "fixture matches production traffic."
)


def _require_env(name: str) -> str:
    value = os.environ.get(name, "").strip()
    if not value:
        raise CaptureError(
            f"Environment variable {name} is not set. See this script's docstring; use a "
            "throwaway credential and revoke it after capturing (protocol §3.3)."
        )
    return value


# --- Response verifiers -------------------------------------------------------------------
#
# One per response shape, not one per provider: what makes a capture meaningful differs by what
# came back. Each raises when the response asserts nothing, and otherwise returns the sentence
# the sidecar opens its `notes` with — so the check and the record of what was checked cannot
# drift apart.


def whisper_verify(body: str) -> str:
    """OpenAI-shaped transcription response: `{"text": "…"}`."""
    transcript = json.loads(body).get("text", "")
    if not transcript.strip():
        raise CaptureError(
            "The provider returned an empty transcript. A capture that transcribes to nothing "
            "asserts nothing — check the source audio reached the request intact."
        )
    return f"Transcript: {transcript!r}."


def google_verify(body: str) -> str:
    """Google-shaped response: `{"results":[{"alternatives":[{"transcript": "…"}]}]}`.

    Reads the same path `GoogleSpeechRecognizer` reads — first result, first alternative — so a
    response the SDK would surface as silence fails here too. Google answers `{}` when it
    recognizes nothing, which is a 200 that proves the round trip and nothing else.
    """
    parsed = json.loads(body)
    results = parsed.get("results") or []
    alternatives = (results[0].get("alternatives") if results else None) or []
    transcript = alternatives[0].get("transcript", "") if alternatives else ""
    if not transcript.strip():
        raise CaptureError(
            "Google returned no transcript (an empty object is what speech:recognize sends when "
            "it recognizes nothing). A capture that transcribes to nothing asserts nothing — "
            "check the audio was submitted as raw LINEAR16 at the sample rate the config "
            "declares."
        )
    return f"Transcript: {transcript!r}."


def speechmatics_verify(payload: bytes) -> str:
    """Synthesized audio: the check is that it is audio at all.

    A vendor that answers 200 with a JSON error envelope, or with an empty body, would otherwise
    be committed as a "recording" of silence — and a binary capture nobody can play is the one
    kind of fixture no reviewer catches by reading the diff.
    """
    if not payload:
        raise CaptureError(
            "Speechmatics returned an empty body. A zero-byte capture drives no frame boundary "
            "and asserts nothing."
        )
    if payload.lstrip()[:1] in (b"{", b"["):
        raise CaptureError(
            "Speechmatics returned JSON, not audio, under a success status. Read the body "
            "manually before re-capturing — it is most likely an error envelope."
        )
    riff = payload[:4] == b"RIFF" and payload[8:12] == b"WAVE"
    container = "a RIFF/WAVE container" if riff else "no RIFF/WAVE header"
    return f"{len(payload)} bytes of synthesized audio with {container}."


def lmnt_verify(envelope: dict) -> str:
    """Envelope-only capture: verified against the envelope, because that *is* the capture."""
    if envelope["status"] != 200:
        raise CaptureError(
            f"LMNT answered HTTP {envelope['status']}. Only a success envelope is worth "
            "committing as the shape the SDK's happy path meets."
        )
    if not envelope["chunk_sizes"]:
        raise CaptureError(
            "LMNT returned no body bytes. An envelope with a zero content length records no "
            "frame boundary, which is the one thing this capture exists to record."
        )
    return (
        f"{envelope['content_length']} bytes of {envelope['media_type']} observed in "
        f"{len(envelope['chunk_sizes'])} reads; the audio itself was discarded, not written."
    )


# --- Request plans ------------------------------------------------------------------------


def openai_whisper_plan(source_pcm: bytes) -> dict:
    """Request plan for the OpenAI Whisper surface (§4.1)."""
    api_key = _require_env("OPENAI_API_KEY")
    boundary = uuid.uuid4().hex
    wav = wav_from_pcm(source_pcm)

    return {
        "product": "OpenAI — audio transcriptions (Whisper)",
        "url": "https://api.openai.com/v1/audio/transcriptions",
        "endpoint_template": "POST https://api.openai.com/v1/audio/transcriptions",
        "api_version": "n/a",
        "terms_verdict": "permitted-with-conditions",
        "terms_basis": "docs/guides/provider-recording-protocol.md section 7 (OpenAI Whisper)",
        "secrets": [api_key],
        # OpenAI's URL carries no account-scoped segment, unlike Azure's.
        "account_tokens": {},
        "redaction_applied": ["authorization bearer request header"],
        "redaction_notes": (
            f"{JSON_REDACTION_NOTES} The credential rode in a request header and never appeared "
            "in the response."
        ),
        "notes": MULTIPART_FIDELITY_NOTE,
        "verify": whisper_verify,
        "request_summary": f"multipart, {len(wav)} bytes of WAV",
        "headers": {
            "Authorization": f"Bearer {api_key}",
            "Content-Type": f"multipart/form-data; boundary={boundary}",
        },
        "body": build_multipart(
            boundary,
            "file",
            "audio.wav",
            wav,
            {"model": "whisper-1", "language": "es"},
        ),
    }


def azure_openai_whisper_plan(source_pcm: bytes) -> dict:
    """Request plan for the Azure OpenAI Whisper surface (§4.2)."""
    api_key = _require_env("AZURE_OPENAI_API_KEY")
    endpoint = deployments_base(_require_env("AZURE_OPENAI_ENDPOINT"))
    deployment = _require_env("AZURE_OPENAI_DEPLOYMENT")
    api_version = os.environ.get("AZURE_OPENAI_API_VERSION", "2024-02-01").strip()
    boundary = uuid.uuid4().hex
    wav = wav_from_pcm(source_pcm)

    return {
        "product": "Azure OpenAI Service — audio transcriptions (Whisper)",
        "url": f"{endpoint}/{deployment}/audio/transcriptions?api-version={api_version}",
        "endpoint_template": (
            "POST https://<resource>.openai.azure.com/openai/deployments/<deployment>"
            "/audio/transcriptions?api-version=" + api_version
        ),
        "api_version": api_version,
        "terms_verdict": "permitted",
        "terms_basis": (
            "docs/guides/provider-recording-protocol.md section 7 (Azure OpenAI Whisper)"
        ),
        "secrets": [api_key],
        # Not a secret to blanket-replace: protocol §4 calls out "deployment names that encode
        # an account", and the endpoint template below already carries <deployment> instead of
        # the real one. Blanket-replacing it in the body would be actively harmful — a
        # deployment named `whisper` or `es` occurs in ordinary prose, and substituting it
        # inside a transcript would corrupt the vendor's bytes the capture exists to preserve.
        # It is checked against the body instead; see assert_no_account_token_leak.
        "account_tokens": {"deployment": deployment},
        "redaction_applied": [
            "api-key request header",
            "resource-scoped host segment",
            "deployment name",
        ],
        "redaction_notes": (
            f"{JSON_REDACTION_NOTES} The credential rode in a request header and never appeared "
            "in the response."
        ),
        "notes": MULTIPART_FIDELITY_NOTE,
        "verify": whisper_verify,
        "request_summary": f"multipart, {len(wav)} bytes of WAV",
        "headers": {
            "api-key": api_key,
            "Content-Type": f"multipart/form-data; boundary={boundary}",
        },
        "body": build_multipart(
            boundary, "file", "audio.wav", wav, {"model": "whisper-1"}
        ),
    }


# Google's REST reference for speech:recognize lists exactly one authorization requirement —
# "Requires the following OAuth scope: https://www.googleapis.com/auth/cloud-platform" — and
# documents no API-key support, so the `?key=` auth GoogleSpeechRecognizer sends is expected to
# come back 401/403. That is a defect in shipped code, it is known, and fixing it is not this
# script's job; capturing what the SDK actually sends is. Hence two credentials: the key path is
# the default because it is the SDK's request, and the token path exists so a success-path body
# is obtainable at all — at the cost, recorded in the sidecar, of a capture whose auth is not the
# one production uses.
GOOGLE_CREDENTIAL_RULE = (
    "Set exactly one of GOOGLE_SPEECH_API_KEY or GOOGLE_ACCESS_TOKEN. The SDK authenticates with "
    "?key=, which speech:recognize does not document as supported (its reference lists only the "
    "cloud-platform OAuth scope), so the SDK-faithful request is expected to fail with 401/403; "
    "GOOGLE_ACCESS_TOKEN captures the same request under Bearer auth so a success path can be "
    "recorded at all, and the sidecar then says so."
)

GOOGLE_TERMS_UNCERTAINTY_NOTE = (
    "Terms caveat carried from protocol §7: the Service Specific Terms' enumeration of which "
    "products count as an \"AI/ML Service\" could not be read first-hand, and Speech-to-Text's "
    "presence in it is expected rather than confirmed. If it is not enumerated, the output-"
    "ownership clause this capture's `permitted` verdict rests on does not apply and the verdict "
    "drops to not-cleared. Confirm the enumeration before relying on this fixture."
)

GOOGLE_BEARER_AUTH_NOTE = (
    "Auth differs from production, deliberately: this capture was taken with an OAuth access "
    "token (Authorization: Bearer) while GoogleSpeechRecognizer authenticates with the ?key= "
    "query parameter, which the endpoint's reference does not document as supported and which "
    "returns 401/403. The request line, body and response shape are the SDK's; only the "
    "credential differs, and a fixture asserting the SDK's own auth mechanism cannot be captured "
    "until that is fixed in src/."
)


def google_speech_plan(source_pcm: bytes) -> dict:
    """Request plan for the Google Cloud Speech-to-Text v1 surface.

    Reproduces `GoogleSpeechRecognizer.StreamAsync`: compact JSON, the DTO's own field order and
    names, and **raw** LINEAR16 in `audio.content` — the recognizer base64-encodes the frames it
    drained without adding a RIFF header, so wrapping the PCM the way the Whisper surfaces do
    would decode 44 bytes of header as samples and capture a request production never sends.
    """
    api_key = os.environ.get("GOOGLE_SPEECH_API_KEY", "").strip()
    access_token = os.environ.get("GOOGLE_ACCESS_TOKEN", "").strip()
    if bool(api_key) == bool(access_token):
        raise CaptureError(GOOGLE_CREDENTIAL_RULE)

    body = json.dumps(
        {
            "config": {
                "encoding": "LINEAR16",
                "sampleRateHertz": SOURCE_SAMPLE_RATE,
                "languageCode": "es-CO",
                "model": "default",
            },
            "audio": {"content": base64.b64encode(source_pcm).decode("ascii")},
        },
        separators=(",", ":"),
        ensure_ascii=False,
    ).encode("utf-8")

    # StringContent(json, Encoding.UTF8, "application/json") emits the charset parameter; a
    # matcher configured from a capture taken without it would not match production.
    headers = {"Content-Type": "application/json; charset=utf-8"}
    notes = [
        "Captured by scripts/capture-provider-recording.py, which sends the same compact JSON "
        "body the SDK sends — the DTO's field names and order, and raw LINEAR16 in "
        "audio.content with no RIFF header.",
        GOOGLE_TERMS_UNCERTAINTY_NOTE,
    ]

    if access_token:
        headers["Authorization"] = f"Bearer {access_token}"
        url = "https://speech.googleapis.com/v1/speech:recognize"
        endpoint_template = f"POST {url}"
        redaction_applied = ["authorization bearer request header"]
        redaction_notes = (
            f"{GOOGLE_REDACTION_NOTES} The credential rode in a request header and never appeared "
            "in the response."
        )
        notes.append(GOOGLE_BEARER_AUTH_NOTE)
    else:
        url = f"https://speech.googleapis.com/v1/speech:recognize?key={api_key}"
        # The key rides in the query string, so the *endpoint* is a place it can leak. Protocol
        # §4's placeholder table names this exact case; redact() only ever sees bodies.
        endpoint_template = (
            f"POST https://speech.googleapis.com/v1/speech:recognize?key={PLACEHOLDER_API_KEY}"
        )
        redaction_applied = ["api key query-string parameter"]
        redaction_notes = (
            f"{GOOGLE_REDACTION_NOTES} The credential rode in the request's query string and never "
            f"appeared in the response; the recorded endpoint carries ?key={PLACEHOLDER_API_KEY} "
            "in its place."
        )

    return {
        # Protocol §2 fixes this tree's directory name as `google-stt`, while the CLI names the
        # SDK surface being captured. They are allowed to differ; the sidecar follows §2.
        "provider_slug": "google-stt",
        "product": "Google Cloud Speech-to-Text — v1 speech:recognize",
        # Google returns a 19-digit `requestId` in the response body. Protocol §4 bans committing
        # request/trace identifiers outright, and no guard catches it: a bare number is not
        # credential-shaped, so `check-recording-redaction.py` reads it as ordinary payload.
        # Named here rather than detected, because only the vendor's own schema says which field
        # is an identifier and which is data.
        "correlation_fields": ("requestId",),
        "source_audio": {
            "origin": "synthetic",
            "description": GOOGLE_SOURCE_AUDIO_DESCRIPTION,
            "license": "n/a",
        },
        "url": url,
        "endpoint_template": endpoint_template,
        "api_version": "v1",
        "terms_verdict": "permitted",
        "terms_basis": (
            "docs/guides/provider-recording-protocol.md section 7 (Google Speech-to-Text)"
        ),
        "secrets": [api_key or access_token],
        "account_tokens": {},
        "redaction_applied": redaction_applied,
        "redaction_notes": redaction_notes,
        # No accuracy or latency figure belongs in this file or any sidecar: protocol §7's
        # Google row conditions public benchmark results on replication data and reciprocity.
        "notes": " ".join(notes),
        "verify": google_verify,
        "request_summary": f"JSON, {len(source_pcm)} bytes of LINEAR16 PCM base64-encoded",
        "headers": headers,
        "body": body,
    }


def speechmatics_tts_plan(source_pcm: bytes) -> dict:
    """Request plan for the Speechmatics preview TTS surface.

    Reproduces `SpeechmaticsSpeechSynthesizer.SynthesizeAsync` at the shipped `SpeechmaticsOptions`
    defaults — voice `jack`, language `en`, 16 kHz — because those are the values a caller who
    configures nothing sends. `source_pcm` is ignored: this surface's input is text.
    """
    api_key = _require_env("SPEECHMATICS_API_KEY")

    # The voice travels in the PATH, not the body: /generate/{voice} returns 200 audio/wav while
    # /generate returns 404 (probed 2026-08-16 with a wrong-path control on the same host).
    # This plan previously carried the body-field form, i.e. the capture instrument reproduced the
    # very defect it existed to detect and could only ever have recorded a 404. Keep it matching
    # what SpeechmaticsSpeechSynthesizer actually sends.
    #
    # The voice was `eleanor` until 2026-08-18, mirroring the SDK default of the day. Live pitch
    # measurement then showed `eleanor` is not a voice at all: the service answers 200 for any
    # segment and synthesises the fallback speaker `jack`. The SDK default moved to `jack`; this
    # plan follows it, because its contract is to send what a caller who configures nothing sends.
    voice = "jack"

    # Field names and order from SpeechmaticsTtsRequest; compact, as System.Text.Json writes it.
    body = json.dumps(
        {
            "text": TTS_INPUT_TEXT,
            "language": "en",
            "sample_rate": 16000,
        },
        separators=(",", ":"),
        ensure_ascii=False,
    ).encode("utf-8")

    url = (
        "https://preview.tts.speechmatics.com/generate/"
        + urllib.parse.quote(voice, safe="")
    )

    return {
        "product": "Speechmatics — text to speech (preview)",
        "url": url,
        "endpoint_template": "POST https://preview.tts.speechmatics.com/generate/{voice}",
        "api_version": "preview",
        "recordings_dir": TTS_RECORDINGS,
        "scenario_slug": TTS_SCENARIO_SLUG,
        "artifact": "binary",
        # The expectation, not the finding: the media type actually written to the sidecar is the
        # one the vendor declares on the response, and the extension follows it.
        "media_type": "audio/wav",
        "extension": "wav",
        "source_audio": tts_source_audio(voice),
        "terms_verdict": "permitted-with-conditions",
        "terms_basis": (
            "docs/guides/provider-recording-protocol.md section 7 (Speechmatics TTS)"
        ),
        "secrets": [api_key],
        "account_tokens": {},
        "redaction_applied": ["authorization bearer request header"],
        "redaction_notes": (
            "Body bytes are the vendor's, unmodified — not transcoded, trimmed or re-encoded "
            "(protocol §3.6). The credential rode in a request header."
        ),
        "notes": (
            "Captured by scripts/capture-provider-recording.py, which sends the same compact "
            "JSON body the SDK sends at its shipped defaults (voice jack, language en, 16 kHz "
            "— SpeechmaticsTtsRequest's field names and order). One short sentence, per protocol "
            "§7's condition that synthesized-audio captures stay minimal: §10.3's express IP "
            "assignment is written about Transcripts, and the TTS direction rests on §10.5's "
            "weaker derivatives language, so this fixture is permitted by inference rather than "
            "by an express grant. Re-read §10 before re-capturing."
        ),
        "verify": speechmatics_verify,
        "request_summary": "JSON",
        "headers": {
            "Authorization": f"Bearer {api_key}",
            "Content-Type": "application/json; charset=utf-8",
        },
        "body": body,
    }


LMNT_BODY_OMITTED = (
    "LMNT's synthesized audio is deliberately not recorded here. Protocol §7 rates the LMNT HTTP "
    "surface not-cleared — its ToS grants no rights in generated output and its AUP restricts "
    "sharing synthesized speech — so the conservative fallback applies: commit the envelope, "
    "never the bytes."
)


def lmnt_http_plan(source_pcm: bytes) -> dict:
    """Request plan for the LMNT HTTP surface — envelope only (protocol §7).

    Reproduces the form-encoded POST in `LmntSpeechSynthesizer.SynthesizeHttpAsync` at the
    shipped `LmntTtsOptions` defaults, field order included. `Model` is left out because the
    option defaults to null and the synthesizer omits the field entirely when it is unset —
    the live endpoint rejects an explicit `"model": null`.
    `source_pcm` is ignored: this surface's input is text.

    Mirroring the client is the whole contract here, and it cuts both ways: while the client
    posted to `/v1/ai/speech/generate`, so did this plan, and a capture run would have recorded
    a 404 envelope as though it were the surface. The route and format below track the fix.
    """
    api_key = _require_env("LMNT_API_KEY")

    body = urllib.parse.urlencode(
        {
            "voice": "leah",
            "text": TTS_INPUT_TEXT,
            "format": "pcm_s16le",
            "sample_rate": "16000",
            "language": "en",
            "speed": "1.00",
        }
    ).encode("ascii")

    return {
        "product": "LMNT — text to speech (HTTP)",
        "url": "https://api.lmnt.com/v1/ai/speech/bytes",
        "endpoint_template": "POST https://api.lmnt.com/v1/ai/speech/bytes",
        "api_version": "1.0",
        "recordings_dir": TTS_RECORDINGS,
        "scenario_slug": TTS_SCENARIO_SLUG,
        "artifact": "envelope",
        # The capture file is JSON describing an audio response; the audio's own media type is
        # recorded inside the envelope, where it belongs.
        "media_type": "application/json",
        "extension": "json",
        "source_audio": tts_source_audio("leah"),
        "terms_verdict": "not-cleared",
        "terms_basis": "docs/guides/provider-recording-protocol.md section 7 (LMNT HTTP)",
        "secrets": [api_key],
        "account_tokens": {},
        "redaction_applied": ["x-api-key request header"],
        "redaction_notes": (
            "No vendor payload was recorded at all — only the response's status, header names, "
            "media type, length and chunk boundaries. Header values are recorded solely where "
            "they carry no account or correlation identifier. The credential rode in a request "
            "header."
        ),
        "body_omitted": LMNT_BODY_OMITTED,
        "notes": (
            "LMNT's audio bytes are deliberately not committed: protocol §7 (LMNT HTTP) rates "
            "the surface not-cleared and prescribes committing the response envelope instead. "
            "This capture is that envelope. To finish the fixture pair §7 asks for, add a "
            "same-codec body built from synthetic or public-domain audio as a separate "
            "class: \"synthetic\" file — this script does not synthesize one. chunk_sizes are "
            "the lengths successive 8 KiB reads returned, matching LmntSpeechSynthesizer's own "
            "HttpChunkSize buffer; they are read boundaries, not TCP frames."
        ),
        "verify": lmnt_verify,
        "request_summary": "form-encoded",
        "headers": {
            "X-API-Key": api_key,
            "lmnt-version": "1.0",
            "Content-Type": "application/x-www-form-urlencoded",
        },
        "body": body,
    }


PROVIDERS = {
    "openai-whisper": openai_whisper_plan,
    "azure-openai-whisper": azure_openai_whisper_plan,
    "google-speech": google_speech_plan,
    "speechmatics-tts": speechmatics_tts_plan,
    "lmnt-http": lmnt_http_plan,
}


# --- Session plans (WebSocket surfaces) ----------------------------------------------------
#
# A request plan is one request and one artifact. A session is neither: several frames of
# interest arrive on one connection, and which frame is which is decided by reading them, not by
# addressing them. So a session plan names the frames it wants by a predicate over the parsed
# message, and the capture writes one fixture per frame it actually saw. A frame the vendor did
# not send produces no file and says so — silence here is the finding, and inventing the missing
# frame from the docs is the thing this whole protocol exists to stop.

REQUIRED_SESSION_PLAN_KEYS = (
    "product",
    "url",
    "endpoint_template",
    "api_version",
    "terms_verdict",
    "terms_basis",
    "secrets",
    "redaction_applied",
    "redaction_notes",
    "notes",
    "headers",
    "frames",
)

CARTESIA_STT_SOURCE_AUDIO = {
    "origin": "reused-committed-capture",
    "description": SOURCE_AUDIO_DESCRIPTION,
    "license": "n/a",
}


def cartesia_stt_session_plan(source_pcm: bytes) -> dict:
    """Session plan for the Cartesia realtime speech-to-text WebSocket surface.

    Reproduces `CartesiaSpeechRecognizer` at the shipped `CartesiaOptions` defaults: the four
    query parameters it builds, both headers it sets, audio as binary messages, and the bare word
    `done` as the terminator — the value the service names in its own rejection message alongside
    `finalize` and `close`.
    """
    api_key = _require_env("CARTESIA_API_KEY")

    query = urllib.parse.urlencode(
        {
            "model": "ink-whisper",
            # `es`, not the shipped CartesiaOptions default of `en`, because the submitted audio is
            # the Spanish committed capture and this plan reproduces the request a caller makes for
            # THIS scenario. Worth recording that the two disagree harmlessly: a first capture sent
            # `en` against the same Spanish audio, and the service transcribed it correctly anyway
            # while echoing `"language": "en"` back — so the parameter is echoed, not enforced.
            "language": "es",
            "encoding": "pcm_s16le",
            "sample_rate": SOURCE_SAMPLE_RATE,
        }
    )

    return {
        "product": "Cartesia — speech to text, realtime (Ink WebSocket)",
        "url": f"wss://api.cartesia.ai/stt/websocket?{query}",
        "endpoint_template": "GET wss://api.cartesia.ai/stt/websocket",
        "api_version": "2024-11-13",
        "recordings_dir": STT_RECORDINGS,
        "headers": {"X-API-Key": api_key, "Cartesia-Version": "2024-11-13"},
        "audio": source_pcm,
        # Submitted at the rate it would be spoken. Sending the whole buffer at once is what a
        # first capture did, and the service answered with one final transcript and no interim
        # ones — so an unpaced capture cannot record the interim shape at all. The pacing is what
        # makes this a recording of a streaming session rather than of a batch upload.
        "chunk_bytes": SOURCE_SAMPLE_RATE * SOURCE_SAMPLE_WIDTH // 5,
        "pace_seconds": 0.2,
        # `finalize` before `done`: the flush_done frame is the service's answer to `finalize`
        # specifically, so a session that only ever sends the terminator can never observe it.
        # Both words are the service's own, named in the rejection its error frame carries.
        "pre_terminator": "finalize",
        "terminator": "done",
        "source_audio": CARTESIA_STT_SOURCE_AUDIO,
        "terms_verdict": "permitted-with-conditions",
        "terms_basis": (
            "docs/guides/provider-recording-protocol.md section 7 (Cartesia)"
        ),
        "secrets": [api_key],
        "correlation_fields": ("request_id",),
        "redaction_applied": ["X-API-Key request header (never written)"],
        "redaction_notes": JSON_REDACTION_NOTES,
        "notes": (
            "Captured from the live service over one WebSocket session at the shipped "
            "CartesiaOptions defaults. Replaces a documentation-derived fixture: what the vendor "
            "publishes is what it says it sends, and only a capture can say what it sent."
        ),
        "frames": (
            {
                "slug": "transcript-frame-interim",
                "select": lambda m: m.get("type") == "transcript" and not m.get("is_final"),
                "notes": (
                    "An interim transcript, the shape the client must NOT surface as a result. "
                    "Recorded rather than authored, so the is_final=false discriminator is the "
                    "vendor's own and not our reading of its documentation."
                ),
            },
            {
                "slug": "transcript-frame-final",
                "select": lambda m: m.get("type") == "transcript" and bool(m.get("is_final")),
                "notes": "A final transcript — the only frame shape the client may surface.",
            },
            {
                "slug": "flush-done-frame",
                "select": lambda m: m.get("type") == "flush_done",
                "notes": (
                    "The non-transcript control frame. CartesiaSpeechRecognizer deserializes "
                    "EVERY text frame into its transcript DTO and only then filters on "
                    "type != \"transcript\", so this is the frame a broken filter leaks through "
                    "as an empty final result."
                ),
            },
            {
                "slug": "done-frame",
                "select": lambda m: m.get("type") == "done",
                "notes": (
                    "The end-of-session acknowledgement the service sends before closing. Nothing "
                    "in the suite could send this frame while it existed only in a run log."
                ),
            },
        ),
    }


def cartesia_stt_error_session_plan(source_pcm: bytes) -> dict:
    """The same surface, driven to its error frame instead of its transcript frames.

    A second session rather than a second predicate on the first: the error is provoked by sending
    a text message the service does not accept, and a session that has been told something invalid
    is not a session whose transcript frames should be trusted as ordinary output.
    """
    plan = cartesia_stt_session_plan(source_pcm)
    plan.update(
        {
            "audio": b"",
            "pre_terminator": None,
            # The recognizer's own comment records this rejection verbatim: the service answers an
            # unrecognized text message with `Expected one of: "finalize", "done", "close"`.
            "terminator": "{}",
            "frames": (
                {
                    "slug": "error-frame",
                    "select": lambda m: m.get("type") == "error",
                    "notes": (
                        "The service's rejection of an unrecognized client text message. Provoked "
                        "deliberately: no audio was sent in this session, so the frame carries no "
                        "transcript and the error text is the vendor's own protocol message."
                    ),
                },
            ),
            "notes": (
                "Captured from the live service over a WebSocket session deliberately driven to "
                "its error path by sending a text message the protocol does not accept."
            ),
        }
    )
    return plan


SESSION_PROVIDERS = {
    "cartesia-stt": cartesia_stt_session_plan,
    "cartesia-stt-error": cartesia_stt_error_session_plan,
}


def build_plan(provider: str, repo_root: Path) -> dict:
    """Load whatever the surface submits, build its plan, and fill in the shared defaults."""
    source_pcm = b""
    if provider in AUDIO_INPUT_PROVIDERS:
        source = repo_root / SOURCE_PCM
        if not source.is_file():
            raise CaptureError(
                f"Source audio {SOURCE_PCM} not found. It is the committed Azure TTS capture; "
                "this script does not invent audio (protocol §6)."
            )
        source_pcm = source.read_bytes()

    plan = {**PLAN_DEFAULTS, "provider_slug": provider, **PROVIDERS[provider](source_pcm)}
    if plan["artifact"] not in ARTIFACT_MODES:
        raise CaptureError(
            f"Plan for {provider} declares artifact {plan['artifact']!r}; expected one of "
            f"{', '.join(ARTIFACT_MODES)}."
        )

    required = REQUIRED_PLAN_KEYS + (("body_omitted",) if plan["artifact"] == "envelope" else ())
    missing = [key for key in required if key not in plan]
    if missing:
        raise CaptureError(
            f"Plan for {provider} is missing {', '.join(missing)}. Nothing was sent."
        )
    return plan


# --- Capture ------------------------------------------------------------------------------


def send(
    plan: dict, timeout: int, *, retain_body: bool = True
) -> tuple[int, list[tuple[str, str]], bytes, list[int]]:
    """Issue the capture request, returning status, headers, body and read boundaries.

    The body is read in `READ_CHUNK_BYTES` reads whatever the artifact mode, so the chunk sizes
    an envelope records are observations rather than arithmetic. `retain_body=False` is how the
    envelope mode keeps its promise structurally: the audio is counted and dropped one read at a
    time, so there is never a whole payload in memory for a later line of code to write out.
    """
    request = urllib.request.Request(  # noqa: S310 - fixed https provider endpoints
        plan["url"], data=plan["body"], headers=plan["headers"], method="POST"
    )

    try:
        with urllib.request.urlopen(request, timeout=timeout) as response:  # noqa: S310
            buffered = bytearray()
            chunk_sizes: list[int] = []
            while True:
                chunk = response.read(READ_CHUNK_BYTES)
                if not chunk:
                    break
                chunk_sizes.append(len(chunk))
                if retain_body:
                    buffered += chunk
            return (
                response.status,
                list(response.headers.items()),
                bytes(buffered),
                chunk_sizes,
            )
    except urllib.error.HTTPError as error:
        detail = redact(error.read().decode("utf-8", "replace"), plan["secrets"])
        raise CaptureError(
            f"Provider returned HTTP {error.code}. Response body (redacted): {detail}"
        ) from error
    except urllib.error.URLError as error:
        raise CaptureError(f"Could not reach the provider: {error.reason}") from error


def refuse_overwrite(path: Path, repo_root: Path, force: bool) -> None:
    """Stop before replacing a fixture a human already reviewed."""
    if path.exists() and not force:
        raise CaptureError(
            f"{path.relative_to(repo_root)} already exists. Re-capturing replaces a "
            "reviewed fixture — pass --force if that is what you mean."
        )


def warn_if_oversized_text(size: int) -> None:
    """Advisory only (protocol §8) — a large JSON capture is a smell, not a violation."""
    if size > TEXT_SIZE_SMELL_BYTES:
        print(
            f"  WARNING: {size} bytes exceeds the {TEXT_SIZE_SMELL_BYTES}-byte advisory "
            "threshold for a text capture (protocol §8). Consider pruning unbounded arrays and "
            "recording the pruning in redaction.notes.",
            file=sys.stderr,
        )


def capture(provider: str, repo_root: Path, force: bool, timeout: int) -> int:
    plan = build_plan(provider, repo_root)
    artifact = plan["artifact"]

    target_dir = repo_root / plan["recordings_dir"] / plan["provider_slug"]
    slug = plan["scenario_slug"]
    capture_path = target_dir / f"{slug}.{plan['extension']}"
    sidecar_path = target_dir / f"{slug}.provenance.json"

    refuse_overwrite(capture_path, repo_root, force)

    print(f"POST {plan['endpoint_template'].split(' ', 1)[1]}")
    print(f"  request body: {len(plan['body'])} bytes — {plan['request_summary']}")

    status, headers, raw_body, chunk_sizes = send(
        plan, timeout, retain_body=artifact != "envelope"
    )

    media_type = plan["media_type"]
    text: str | None = None
    # Only the json branch can carry vendor-minted identifiers in a structure we parse; binary
    # bytes and the envelope (which never holds the body) have nowhere to hide one. Initialized
    # here because the sidecar below is shared by all three branches.
    correlation_applied: list[str] = []

    if artifact == "json":
        scrubbed, correlation_applied = redact_correlation_fields(
            redact(raw_body.decode("utf-8"), plan["secrets"]), plan["correlation_fields"]
        )
        text = normalize_json(scrubbed)
        assert_no_account_token_leak(text, plan["account_tokens"])
        payload = text.encode("utf-8")
        description = plan["verify"](text)
        warn_if_oversized_text(len(payload))
        if correlation_applied:
            print(
                "  redacted correlation identifiers in the response body: "
                + ", ".join(correlation_applied)
            )

    elif artifact == "binary":
        payload = raw_body
        assert_no_secret_bytes(payload, [*plan["secrets"], *plan["account_tokens"].values()])
        description = plan["verify"](payload)
        if len(payload) > BINARY_SIZE_CAP_BYTES:
            raise CaptureError(
                f"The response is {len(payload)} bytes, over the {BINARY_SIZE_CAP_BYTES}-byte "
                "cap protocol §8 places on a binary capture, so nothing was written. Shorten the "
                "input text and re-capture. The cap has an exception path — §8's three "
                "conditions, Git-LFS included — but it is deliberately narrow, and a short "
                "sentence proves the frame-chunking path exactly as well as a long one."
            )
        # The vendor's own declaration decides both, so a response that stops being WAV cannot
        # land in a file that still claims to be one.
        media_type = response_media_type(headers) or plan["media_type"]
        extension = MEDIA_TYPE_EXTENSIONS.get(media_type, plan["extension"])
        if extension != plan["extension"]:
            print(
                f"  WARNING: the response declared {media_type}, not the expected "
                f"{plan['media_type']}. Writing .{extension} and recording the declared type; "
                "check whether the SDK's expectations have gone stale.",
                file=sys.stderr,
            )
            capture_path = target_dir / f"{slug}.{extension}"
            refuse_overwrite(capture_path, repo_root, force)

    else:  # envelope
        envelope = build_envelope(
            status=status,
            headers=headers,
            chunk_sizes=chunk_sizes,
            body_omitted=plan["body_omitted"],
        )
        description = plan["verify"](envelope)
        text = json.dumps(envelope, indent=2, ensure_ascii=False) + "\n"
        payload = text.encode("utf-8")
        warn_if_oversized_text(len(payload))

    sidecar = build_sidecar(
        provider=plan["provider_slug"],
        product=plan["product"],
        endpoint=plan["endpoint_template"],
        api_version=plan["api_version"],
        captured_utc=_dt.datetime.now(_dt.timezone.utc).strftime("%Y-%m-%d"),
        payload=payload,
        redaction_applied=[
            *plan["redaction_applied"],
            *(f"{field} correlation identifier (response body)" for field in correlation_applied),
        ],
        redaction_notes=(
            f"{plan['redaction_notes']} Observed response headers, values recorded only where "
            f"they carry no account or correlation identifier: {describe_headers(headers)}."
        ),
        terms_verdict=plan["terms_verdict"],
        terms_basis=plan["terms_basis"],
        notes=f"{description} {plan['notes']}",
        media_type=media_type,
        capture_class=plan["capture_class"],
        source_audio=plan["source_audio"],
    )

    target_dir.mkdir(parents=True, exist_ok=True)
    if text is None:
        capture_path.write_bytes(payload)
    else:
        capture_path.write_text(text, encoding="utf-8", newline="\n")
    sidecar_path.write_text(
        json.dumps(sidecar, indent=2, ensure_ascii=False) + "\n",
        encoding="utf-8",
        newline="\n",
    )

    print(f"  wrote {capture_path.relative_to(repo_root)} ({len(payload)} bytes)")
    print(f"  wrote {sidecar_path.relative_to(repo_root)}")
    print(f"  {description}")
    print()
    print("Next: python3 scripts/check-recording-redaction.py .")
    print("Then REVOKE the credential you just used (protocol §3.3).")
    return 0


def build_session_plan(provider: str, repo_root: Path) -> dict:
    """Load the submitted audio and build a session plan, refusing an incomplete one."""
    source = repo_root / SOURCE_PCM
    if not source.is_file():
        raise CaptureError(
            f"Source audio {SOURCE_PCM} not found. It is the committed Azure TTS capture; "
            "this script does not invent audio (protocol §6)."
        )

    plan = {
        **PLAN_DEFAULTS,
        "provider_slug": provider,
        **SESSION_PROVIDERS[provider](source.read_bytes()),
    }
    missing = [key for key in REQUIRED_SESSION_PLAN_KEYS if key not in plan]
    if missing:
        raise CaptureError(
            f"Session plan for {provider} is missing {', '.join(missing)}. Nothing was sent."
        )
    return plan


def run_session(plan: dict, timeout: int) -> list[dict]:
    """Drive one WebSocket session and return every text message it produced, parsed.

    Binary messages are counted and dropped. This surface carries its results as text, and a
    capture that buffered whatever else arrived would be holding provider audio it has no verdict
    for.
    """
    session = WebSocketSession(plan["url"], plan["headers"], timeout)
    try:
        audio = plan["audio"]
        chunk = plan.get("chunk_bytes") or READ_CHUNK_BYTES
        pace = plan.get("pace_seconds") or 0.0
        for offset in range(0, len(audio), chunk):
            session.send(WS_OPCODE_BINARY, audio[offset:offset + chunk])
            if pace:
                time.sleep(pace)
        if plan.get("pre_terminator"):
            session.send(WS_OPCODE_TEXT, plan["pre_terminator"].encode("utf-8"))
        session.send(WS_OPCODE_TEXT, plan["terminator"].encode("utf-8"))

        messages: list[dict] = []
        binary_count = 0
        for opcode, payload in session.messages(timeout):
            if opcode != WS_OPCODE_TEXT:
                binary_count += 1
                continue
            raw = payload.decode("utf-8", "replace")
            try:
                parsed = json.loads(raw)
            except json.JSONDecodeError:
                raise CaptureError(
                    "The service sent a text message that is not JSON; this plan assumes it is. "
                    f"First 120 characters (redacted): {redact(raw, plan['secrets'])[:120]}"
                ) from None
            if isinstance(parsed, dict):
                messages.append(parsed)
    finally:
        session.close()

    print(f"  session produced {len(messages)} text messages, {binary_count} binary")
    print("  message types observed: " + ", ".join(
        sorted({str(m.get("type", "<untyped>")) for m in messages}) or ["<none>"]
    ))
    return messages


def capture_session(provider: str, repo_root: Path, force: bool, timeout: int) -> int:
    """Capture one WebSocket session into one fixture per frame of interest."""
    plan = build_session_plan(provider, repo_root)
    target_dir = repo_root / plan["recordings_dir"] / "cartesia-stt"

    print(f"CONNECT {plan['endpoint_template'].split(' ', 1)[1]}")
    print(f"  submitting {len(plan['audio'])} bytes of audio, terminator {plan['terminator']!r}")

    for spec in plan["frames"]:
        refuse_overwrite(target_dir / f"{spec['slug']}.json", repo_root, force)

    messages = run_session(plan, timeout)
    captured_utc = _dt.datetime.now(_dt.timezone.utc).strftime("%Y-%m-%d")
    written = 0

    for spec in plan["frames"]:
        match = next((m for m in messages if spec["select"](m)), None)
        if match is None:
            # Not an error. The absence of a frame the plan asked for is an observation about the
            # service on this run, and writing an authored stand-in is what this protocol forbids.
            print(f"  {spec['slug']}: NOT SENT in this session — no fixture written")
            continue

        scrubbed, correlation_applied = redact_correlation_fields(
            redact(json.dumps(match, ensure_ascii=False), plan["secrets"]),
            plan["correlation_fields"],
        )
        text = normalize_json(scrubbed)
        assert_no_account_token_leak(text, plan["account_tokens"])
        payload = text.encode("utf-8")
        warn_if_oversized_text(len(payload))

        sidecar = build_sidecar(
            provider="cartesia-stt",
            product=plan["product"],
            endpoint=plan["endpoint_template"],
            api_version=plan["api_version"],
            captured_utc=captured_utc,
            payload=payload,
            redaction_applied=[
                *plan["redaction_applied"],
                *(
                    f"{field} correlation identifier (response body)"
                    for field in correlation_applied
                ),
            ],
            redaction_notes=plan["redaction_notes"],
            terms_verdict=plan["terms_verdict"],
            terms_basis=plan["terms_basis"],
            notes=f"{spec['notes']} {plan['notes']}",
            source_audio=plan["source_audio"],
        )

        target_dir.mkdir(parents=True, exist_ok=True)
        (target_dir / f"{spec['slug']}.json").write_text(text, encoding="utf-8", newline="\n")
        (target_dir / f"{spec['slug']}.provenance.json").write_text(
            json.dumps(sidecar, indent=2, ensure_ascii=False) + "\n",
            encoding="utf-8",
            newline="\n",
        )
        print(f"  wrote {spec['slug']}.json ({len(payload)} bytes) + sidecar")
        written += 1

    print()
    print(f"{written} of {len(plan['frames'])} requested frames captured.")
    print("Next: python3 scripts/check-recording-redaction.py .")
    return 0


def main(argv: list[str] | None = None) -> int:
    parser = argparse.ArgumentParser(
        description=(
            "Capture a real speech-provider response (STT or TTS) into a committed fixture."
        ),
        epilog=(
            "Credentials come from the environment; see the module docstring. What lands on "
            "disk depends on the surface: the vendor's JSON, the vendor's bytes, or — where the "
            "terms do not permit committing the payload — a response envelope without it."
        ),
    )
    parser.add_argument("provider", choices=sorted({*PROVIDERS, *SESSION_PROVIDERS}))
    parser.add_argument(
        "repo_root", nargs="?", default=".", help="repository root (default: .)"
    )
    parser.add_argument(
        "--force", action="store_true", help="replace an existing reviewed capture"
    )
    parser.add_argument(
        "--timeout", type=int, default=120, help="request timeout in seconds (default: 120)"
    )
    args = parser.parse_args(argv)

    run = capture_session if args.provider in SESSION_PROVIDERS else capture
    try:
        return run(args.provider, Path(args.repo_root).resolve(), args.force, args.timeout)
    except CaptureError as error:
        print(f"error: {error}", file=sys.stderr)
        return 1


if __name__ == "__main__":
    sys.exit(main())

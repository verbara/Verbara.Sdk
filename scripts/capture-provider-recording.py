#!/usr/bin/env python3
"""Capture a real STT provider response into a committed fixture (ADR-0041 D4).

Implements steps 3-8 of `docs/guides/provider-recording-protocol.md` §3 for the two Whisper
surfaces, so a capture is reproducible instead of being a one-off ceremony performed by hand:
it sends the same multipart request the SDK sends, redacts, normalizes, writes the provenance
sidecar and enforces the size cap.

    python3 scripts/capture-provider-recording.py openai-whisper
    python3 scripts/capture-provider-recording.py azure-openai-whisper

Credentials come from the environment and are never written, echoed or stored:

    openai-whisper         OPENAI_API_KEY
    azure-openai-whisper   AZURE_OPENAI_API_KEY, AZURE_OPENAI_ENDPOINT,
                           AZURE_OPENAI_DEPLOYMENT, [AZURE_OPENAI_API_VERSION]

**Use a throwaway credential and revoke it afterwards** (protocol §3.3). A key that never had
access to production data cannot leak production identifiers through a response body.

Source audio (protocol §6): the committed Azure TTS capture — synthetic speech from a prebuilt
neural voice over a fictional sentence. No microphone is involved and no identifiable person's
voice is ever submitted. See SOURCE_AUDIO_DESCRIPTION for the terms note this carries.

stdlib only, by the same rule as `scripts/check-*.py`: a fixture tool that needs `pip install`
is a fixture tool that stops being run.
"""

from __future__ import annotations

import argparse
import datetime as _dt
import hashlib
import io
import json
import os
import sys
import urllib.error
import urllib.request
import uuid
import wave
from pathlib import Path

# --- Protocol constants -------------------------------------------------------------------

SCHEMA = "verbara.recording-provenance/1"
SCENARIO_SLUG = "transcribe-short-es-co"

STT_RECORDINGS = Path("Tests/Verbara.Sdk.VoiceAi.Stt.Tests/Recordings")
SOURCE_PCM = Path(
    "Tests/Verbara.Sdk.VoiceAi.Tts.Tests/Recordings/azure-tts/synthesize-short-es-co.raw"
)

# The source capture's format, from its own provenance sidecar.
SOURCE_SAMPLE_RATE = 8000
SOURCE_CHANNELS = 1
SOURCE_SAMPLE_WIDTH = 2

# Advisory ceiling for a text capture (protocol §8). The hard 256 KiB cap is for binary.
TEXT_SIZE_SMELL_BYTES = 64 * 1024

PLACEHOLDER_API_KEY = "REDACTED-API-KEY"

SOURCE_AUDIO_DESCRIPTION = (
    "Synthetic speech, not a recording of any person: the committed Azure TTS capture "
    "Tests/Verbara.Sdk.VoiceAi.Tts.Tests/Recordings/azure-tts/synthesize-short-es-co.raw "
    "(prebuilt neural voice es-CO-SalomeNeural, 8 kHz 16-bit mono PCM, 3.76 s) reading the "
    "fictional sentence 'El sistema registró la solicitud correctamente.', wrapped in a "
    "44-byte canonical RIFF/WAVE header identical to the one WhisperSpeechRecognizer builds. "
    "Reusing that capture keeps one cleared, reviewed audio artifact in the repo instead of "
    "adding a second. It is submitted for a single transcription, which is inference, not "
    "training: neither Azure's bar on using Output Content as synthetic training data for a "
    "similar service nor OpenAI Services Agreement 3.3(e) on developing competing models is "
    "engaged."
)

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
) -> dict:
    """Assemble the provenance sidecar (protocol §5)."""
    return {
        "schema": SCHEMA,
        "class": "recorded",
        "provider": provider,
        "product": product,
        "endpoint": endpoint,
        "api_version": api_version,
        "captured_utc": captured_utc,
        "media_type": "application/json",
        "bytes": len(payload),
        "sha256": hashlib.sha256(payload).hexdigest(),
        "source_audio": {
            "origin": "synthetic",
            "description": SOURCE_AUDIO_DESCRIPTION,
            "license": "n/a",
        },
        "redaction": {"applied": redaction_applied, "notes": redaction_notes},
        "terms": {
            "verdict": terms_verdict,
            "basis": terms_basis,
            "checked_utc": captured_utc,
        },
        "notes": notes,
    }


# --- Provider definitions -----------------------------------------------------------------


def _require_env(name: str) -> str:
    value = os.environ.get(name, "").strip()
    if not value:
        raise CaptureError(
            f"Environment variable {name} is not set. See this script's docstring; use a "
            "throwaway credential and revoke it after capturing (protocol §3.3)."
        )
    return value


def openai_whisper_plan(wav: bytes) -> dict:
    """Request plan for the OpenAI Whisper surface (§4.1)."""
    api_key = _require_env("OPENAI_API_KEY")
    boundary = uuid.uuid4().hex

    return {
        "provider": "openai-whisper",
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


def azure_openai_whisper_plan(wav: bytes) -> dict:
    """Request plan for the Azure OpenAI Whisper surface (§4.2)."""
    api_key = _require_env("AZURE_OPENAI_API_KEY")
    endpoint = deployments_base(_require_env("AZURE_OPENAI_ENDPOINT"))
    deployment = _require_env("AZURE_OPENAI_DEPLOYMENT")
    api_version = os.environ.get("AZURE_OPENAI_API_VERSION", "2024-02-01").strip()
    boundary = uuid.uuid4().hex

    return {
        "provider": "azure-openai-whisper",
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
        "headers": {
            "api-key": api_key,
            "Content-Type": f"multipart/form-data; boundary={boundary}",
        },
        "body": build_multipart(
            boundary, "file", "audio.wav", wav, {"model": "whisper-1"}
        ),
    }


PROVIDERS = {
    "openai-whisper": openai_whisper_plan,
    "azure-openai-whisper": azure_openai_whisper_plan,
}


# --- Capture ------------------------------------------------------------------------------


def send(plan: dict, timeout: int) -> tuple[str, list[tuple[str, str]]]:
    """Issue the capture request, returning the body and the observed response headers."""
    request = urllib.request.Request(  # noqa: S310 - fixed https provider endpoints
        plan["url"], data=plan["body"], headers=plan["headers"], method="POST"
    )

    try:
        with urllib.request.urlopen(request, timeout=timeout) as response:  # noqa: S310
            return response.read().decode("utf-8"), list(response.headers.items())
    except urllib.error.HTTPError as error:
        detail = redact(error.read().decode("utf-8", "replace"), plan["secrets"])
        raise CaptureError(
            f"Provider returned HTTP {error.code}. Response body (redacted): {detail}"
        ) from error
    except urllib.error.URLError as error:
        raise CaptureError(f"Could not reach the provider: {error.reason}") from error


def capture(provider: str, repo_root: Path, force: bool, timeout: int) -> int:
    source = repo_root / SOURCE_PCM
    if not source.is_file():
        raise CaptureError(
            f"Source audio {SOURCE_PCM} not found. It is the committed Azure TTS capture; "
            "this script does not invent audio (protocol §6)."
        )

    target_dir = repo_root / STT_RECORDINGS / provider
    capture_path = target_dir / f"{SCENARIO_SLUG}.json"
    sidecar_path = target_dir / f"{SCENARIO_SLUG}.provenance.json"

    if capture_path.exists() and not force:
        raise CaptureError(
            f"{capture_path.relative_to(repo_root)} already exists. Re-capturing replaces a "
            "reviewed fixture — pass --force if that is what you mean."
        )

    wav = wav_from_pcm(source.read_bytes())
    plan = PROVIDERS[provider](wav)

    print(f"POST {plan['endpoint_template'].split(' ', 1)[1]}")
    print(f"  request body: {len(plan['body'])} bytes multipart ({len(wav)} bytes of WAV)")

    raw_body, headers = send(plan, timeout)
    body = normalize_json(redact(raw_body, plan["secrets"]))
    assert_no_account_token_leak(body, plan.get("account_tokens", {}))
    payload = body.encode("utf-8")

    transcript = json.loads(body).get("text", "")
    if not transcript.strip():
        raise CaptureError(
            "The provider returned an empty transcript. A capture that transcribes to nothing "
            "asserts nothing — check the source audio reached the request intact."
        )

    if len(payload) > TEXT_SIZE_SMELL_BYTES:
        print(
            f"  WARNING: {len(payload)} bytes exceeds the {TEXT_SIZE_SMELL_BYTES}-byte advisory "
            "threshold for a text capture (protocol §8). Consider pruning unbounded arrays and "
            "recording the pruning in redaction.notes.",
            file=sys.stderr,
        )

    sidecar = build_sidecar(
        provider=provider,
        product=plan["product"],
        endpoint=plan["endpoint_template"],
        api_version=plan["api_version"],
        captured_utc=_dt.datetime.now(_dt.timezone.utc).strftime("%Y-%m-%d"),
        payload=payload,
        redaction_applied=plan["redaction_applied"],
        redaction_notes=(
            "Body is the vendor's JSON, unmodified apart from pretty-printing to 2-space indent "
            "with a trailing newline (protocol §3.6); no field was removed. The credential rode "
            "in a request header and never appeared in the response. Observed response headers, "
            "values recorded only where they carry no account or correlation identifier: "
            f"{describe_headers(headers)}."
        ),
        terms_verdict=plan["terms_verdict"],
        terms_basis=plan["terms_basis"],
        notes=(
            f"Transcript: {transcript!r}. Captured by scripts/capture-provider-recording.py, "
            "which sends the same multipart shape the SDK sends (file part without Content-Type, "
            "text parts as text/plain; charset=utf-8) so the fixture matches production traffic."
        ),
    )

    target_dir.mkdir(parents=True, exist_ok=True)
    capture_path.write_text(body, encoding="utf-8", newline="\n")
    sidecar_path.write_text(
        json.dumps(sidecar, indent=2, ensure_ascii=False) + "\n",
        encoding="utf-8",
        newline="\n",
    )

    print(f"  wrote {capture_path.relative_to(repo_root)} ({len(payload)} bytes)")
    print(f"  wrote {sidecar_path.relative_to(repo_root)}")
    print(f"  transcript: {transcript!r}")
    print()
    print("Next: python3 scripts/check-recording-redaction.py .")
    print("Then REVOKE the credential you just used (protocol §3.3).")
    return 0


def main(argv: list[str] | None = None) -> int:
    parser = argparse.ArgumentParser(
        description="Capture a real STT provider response into a committed fixture.",
        epilog="Credentials come from the environment; see the module docstring.",
    )
    parser.add_argument("provider", choices=sorted(PROVIDERS))
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

    try:
        return capture(args.provider, Path(args.repo_root).resolve(), args.force, args.timeout)
    except CaptureError as error:
        print(f"error: {error}", file=sys.stderr)
        return 1


if __name__ == "__main__":
    sys.exit(main())

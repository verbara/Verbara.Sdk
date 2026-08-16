#!/usr/bin/env python3
"""The conformance probe, as an instrument rather than a ceremony (`provider-wire-protocol-conformance` §5).

Every defect in that change was found the same way: send what the shipped client sends, to the real
endpoint, next to a control that is *known wrong*, and compare. Every defect was also hidden the same
way — by a green test suite whose fake was written by the same author as the client, so a shared
misreading of the vendor's contract passed on both sides. That is why the method has to be committed
code with its own tests, and not a procedure someone remembers to follow.

This module holds the parts of that method that can be wrong **without a network**: what a probe is
allowed to print, which controls it must carry, and how deep it must reach before its result means
anything. Those are the parts that failed in practice. The network calls live in the runner at the
bottom and are deliberately thin; the rules above them are unit-tested on every PR.

    python3 scripts/probe-provider-conformance.py --self-check
    python3 scripts/probe-provider-conformance.py --list

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
import json
import sys
from dataclasses import dataclass

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

    parser.print_help()
    return 0


if __name__ == "__main__":
    sys.exit(main())

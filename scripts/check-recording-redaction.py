#!/usr/bin/env python3
"""Fail CI when a checked-in provider recording carries credential-shaped content.

Usage: check-recording-redaction.py [repo-root]

Enforces the recording redaction rule of ADR-0041 (D5): no API keys, bearer
tokens or signed URLs; no account / tenant / project / billing identifiers; no
request or session identifiers that correlate to a real account. Recordings of
real third-party API traffic are committed to a PUBLIC repository, so the rule
cannot live in documentation alone -- "a repo check enforces the credential
rule; documentation alone does not".

Scope: every directory named `Recordings` anywhere in the tree (build output and
VCS metadata pruned), and every file underneath it -- text AND binary. Binary is
deliberate: an API key fits comfortably in a WAV LIST/INFO chunk or an MP3 ID3
tag, and a scanner that only reads .json would never see it. Bytes are decoded
with errors="replace", which preserves ASCII runs inside otherwise-binary files.

The capture procedure, the placeholder forms this script accepts, the provenance
sidecar format and the per-provider terms-of-service findings live in
docs/guides/provider-recording-protocol.md.

Three deliberate behaviours:

  * Placeholder-aware. The documented placeholder forms (REDACTED-*, the nil
    GUID, single-character fills, <angle-bracket> templates) pass, so a properly
    redacted capture stays green.
  * Self-checking. Every pattern is run against a built-in positive canary and a
    negative one BEFORE the tree is scanned. A regex broken by a careless edit
    fails the run loudly instead of silently matching nothing -- the same
    liveness posture as the coverage guards' `min_scanned_files` fence.
  * LFS-pointer aware. An unfetched Git-LFS pointer fails the run rather than
    reading as clean. Over-cap captures live under `.../Recordings/large/` and
    are LFS-tracked, so a job that reads them must check out with `lfs: true`.

The matched value is NEVER printed. A CI log must not become the second place a
secret leaked; the pattern name plus file and line are enough to act on.

Dependency-free (stdlib only). Exit codes: 0 = clean, 1 = hit / self-check trip.
"""
import collections
import os
import re
import sys

# Directory names never scanned: build output, VCS metadata, tool caches.
_SKIP_DIRS = frozenset({
    ".git", ".vs", ".idea", "bin", "obj", "node_modules", "artifacts",
    "TestResults", "__pycache__",
})

_RECORDINGS_DIR = "Recordings"

# A file this large under Recordings/ is a mis-wiring, not a fixture (the
# per-file cap is 256 KiB). Refuse rather than read it into memory.
_MAX_FILE_BYTES = 32 * 1024 * 1024

_LFS_POINTER_PREFIX = b"version https://git-lfs.github.com/spec/v1"

# Substrings that mark a value as a documented placeholder rather than a live
# secret. Compared case-insensitively against the captured value only.
_PLACEHOLDER_TOKENS = (
    "redacted", "placeholder", "example", "sample", "dummy", "fake",
    "sanitized", "scrubbed", "removed", "changeme", "xxxx", "n/a",
    "<", "${", "{{",
)

# Canary fragments are concatenated at runtime so this file never contains a
# contiguous literal that a secret scanner would flag as a real credential.
_SK = "sk-"
_AIZA = "AI" + "za"
_AKIA = "AK" + "IA"
_GHP = "gh" + "p_"
_XOXB = "xo" + "xb-"
_EYJ = "ey" + "J"

_Rule = collections.namedtuple("_Rule", "name regex group canary antigen")

_RULES = (
    _Rule(
        "openai-style-key",
        re.compile(r"\bsk-[A-Za-z0-9_-]{20,}"),
        0,
        _SK + "proj-A1b2C3d4E5f6G7h8I9j0K1l2M3n4",
        _SK + "REDACTED-PLACEHOLDER-000000000000",
    ),
    _Rule(
        "bearer-token",
        re.compile(r"(?i)\bbearer\s+([A-Za-z0-9._~+/=-]{16,})"),
        1,
        "Authorization: Bearer a1b2c3d4e5f6g7h8i9j0k1l2",
        "Authorization: Bearer REDACTED-TOKEN-PLACEHOLDER",
    ),
    _Rule(
        "jwt",
        re.compile(r"\beyJ[A-Za-z0-9_-]{8,}\.[A-Za-z0-9_-]{8,}\.[A-Za-z0-9_-]{8,}"),
        0,
        _EYJ + "hbGciOiJIUzI1NiJ9.eyJzdWIiOiIxMjM0NSJ9.c2lnbmF0dXJlLXZhbHVl",
        _EYJ + "REDACTEDHEADER.REDACTEDCLAIMS.REDACTEDSIGNATURE",
    ),
    _Rule(
        "google-api-key",
        re.compile(r"\bAIza[0-9A-Za-z_-]{35}\b"),
        0,
        _AIZA + "SyA0000BBBB1111CCCC2222DDDD3333EEEE",
        _AIZA + "REDACTED-PLACEHOLDER-KEY-0000000000",
    ),
    _Rule(
        "aws-access-key-id",
        re.compile(r"\b(?:AKIA|ASIA|ABIA|ACCA)[0-9A-Z]{16}\b"),
        0,
        _AKIA + "Q1W2E3R4T5Y6U7I8",
        _AKIA + "REDACTED00000000",
    ),
    _Rule(
        "github-token",
        re.compile(r"\bgh[pousr]_[A-Za-z0-9]{36}\b"),
        0,
        _GHP + "0123456789abcdefghijABCDEFGHIJ012345",
        _GHP + "REDACTED" + "0" * 28,
    ),
    _Rule(
        "slack-token",
        re.compile(r"\bxox[abprs]-[A-Za-z0-9-]{16,}"),
        0,
        _XOXB + "1111111111-2222222222-abcdefghij",
        _XOXB + "REDACTED-PLACEHOLDER-TOKEN",
    ),
    _Rule(
        "private-key-block",
        re.compile(r"-----BEGIN (?:[A-Z0-9 ]+ )?PRIVATE KEY-----"),
        0,
        "-----BEGIN RSA PRIVATE KEY-----",
        "a private key must never reach a recording",
    ),
    _Rule(
        "credential-keyed-value",
        re.compile(
            r"(?i)\b(?:api[_-]?key|apikey|subscription[_-]?key|access[_-]?token"
            r"|refresh[_-]?token|id[_-]?token|client[_-]?secret|secret[_-]?key"
            r"|auth[_-]?token|x-api-key|x-goog-api-key|password|passwd)\b"
            r"[\"']?\s*[:=]\s*[\"']?([^\"'\s,&}\]]{8,})"),
        1,
        '"api_key": "K1x9Qz7Lm3Pw5Rt8Vb"',
        '"api_key": "REDACTED-API-KEY"',
    ),
    _Rule(
        "query-string-secret",
        re.compile(
            r"(?i)[?&](?:key|api[_-]?key|access[_-]?token|token|subscription-key"
            r"|auth)=([^&\s\"'<>]{8,})"),
        1,
        "https://speech.googleapis.com/v1/speech:recognize?key=Zq7Xw3Pm9Ld1Tv5Nb2Hj",
        "https://speech.googleapis.com/v1/speech:recognize?key=REDACTED-API-KEY",
    ),
    _Rule(
        "signed-url-signature",
        re.compile(
            r"(?i)[?&](?:x-amz-signature|x-goog-signature|x-amz-credential"
            r"|signature|sig)=([^&\s\"'<>]{8,})"),
        1,
        "https://blob.invalid/capture?sv=2021-08-06&sig=aB3dE5gH7jK9mN1pQ3rS5tU7",
        "https://blob.invalid/capture?sig=REDACTED-SIGNATURE",
    ),
    _Rule(
        # A bare GUID under Recordings/ is a request / session / trace / resource
        # id -- exactly the "correlates to a real account" class D5 bans. The nil
        # GUID is the documented placeholder and is allowlisted below.
        "correlating-guid",
        re.compile(r"\b[0-9a-fA-F]{8}-[0-9a-fA-F]{4}-[0-9a-fA-F]{4}"
                   r"-[0-9a-fA-F]{4}-[0-9a-fA-F]{12}\b"),
        0,
        '"x-request-id": "3f2a1b4c-5d6e-7f80-9012-3456789abcde"',
        '"x-request-id": "00000000-0000-0000-0000-000000000000"',
    ),
    _Rule(
        # 32 hex characters is the Azure AI Speech / Cognitive Services key
        # shape. A 64-char SHA-256 digest cannot match: there is no word
        # boundary 32 characters into a longer hex run.
        "bare-32-hex-key",
        re.compile(r"\b[0-9a-fA-F]{32}\b"),
        0,
        "subscription key value 4d7b2e9a1c6f8035be24a917d05c3f6e",
        "subscription key value " + "0" * 32,
    ),
    _Rule(
        "account-identifier",
        re.compile(
            r"(?i)\b(?:tenant|subscription|account|project|billing|organization"
            r"|customer)[_-]?id\b[\"']?\s*[:=]\s*[\"']?([^\"'\s,&}\]]{4,})"),
        1,
        '"project_id": "prod-448122"',
        '"project_id": "REDACTED-PROJECT"',
    ),
)


def fail(message):
    print(f"::error::recording-redaction: {message}")
    sys.exit(1)


def is_placeholder(value):
    """True when a matched value is one of the documented redaction placeholders."""
    lowered = value.lower()
    if any(token in lowered for token in _PLACEHOLDER_TOKENS):
        return True
    # Single-character fills: the nil GUID, 0*32, xxxx-style masks.
    stripped = re.sub(r"[^0-9A-Za-z]", "", lowered)
    return bool(stripped) and len(set(stripped)) == 1


def scan_text(text):
    """Return [(rule_name, line_number)] for every non-placeholder hit in `text`."""
    findings = []
    for rule in _RULES:
        for match in rule.regex.finditer(text):
            value = match.group(rule.group)
            if value is None or is_placeholder(value):
                continue
            line = text.count("\n", 0, match.start()) + 1
            findings.append((rule.name, line))
    return findings


def self_check():
    """Liveness fence: prove every pattern still detects, and still forgives.

    A pattern edited into uselessness would otherwise scan a tree full of
    secrets and report a clean run. Each rule must (a) report itself on its
    canary and (b) stay silent on its redacted counterpart.
    """
    for rule in _RULES:
        hits = {name for name, _ in scan_text(rule.canary)}
        if rule.name not in hits:
            fail(f"self-check FAILED: pattern '{rule.name}' no longer matches its "
                 f"own canary. The detector is broken -- refusing to report a "
                 f"clean scan.")
        misses = {name for name, _ in scan_text(rule.antigen)}
        if rule.name in misses:
            fail(f"self-check FAILED: pattern '{rule.name}' flags its own redacted "
                 f"placeholder. It would fail every correctly redacted capture.")


def find_recording_dirs(repo_root):
    """Every directory named `Recordings`, build output pruned."""
    found = []
    for base, dirs, _files in os.walk(repo_root):
        dirs[:] = sorted(d for d in dirs if d not in _SKIP_DIRS)
        for name in dirs:
            if name == _RECORDINGS_DIR:
                found.append(os.path.join(base, name))
    return sorted(found)


def iter_files(recordings_dir):
    for base, dirs, files in os.walk(recordings_dir):
        dirs[:] = sorted(d for d in dirs if d not in _SKIP_DIRS)
        for name in sorted(files):
            yield os.path.join(base, name)


def read_text(path):
    try:
        size = os.path.getsize(path)
    except OSError as exc:
        fail(f"cannot stat '{path}': {exc}")

    if size > _MAX_FILE_BYTES:
        fail(f"'{path}' is {size} bytes -- refusing to scan. A recording that "
             f"large is a mis-wiring; the per-file cap is 256 KiB "
             f"(docs/guides/provider-recording-protocol.md).")

    try:
        with open(path, "rb") as handle:
            data = handle.read()
    except OSError as exc:
        fail(f"cannot read '{path}': {exc}")

    if data.startswith(_LFS_POINTER_PREFIX):
        fail(f"'{path}' is an unfetched Git-LFS pointer. The scan cannot see the "
             f"real bytes and would read as a false green -- check out with "
             f"`lfs: true` and re-run.")

    # errors="replace" keeps ASCII runs inside binary payloads (WAV LIST chunks,
    # ID3 tags) visible to the patterns.
    return data.decode("utf-8", errors="replace")


def main():
    if len(sys.argv) > 2:
        fail("usage: check-recording-redaction.py [repo-root]")

    repo_root = sys.argv[1] if len(sys.argv) == 2 else "."
    if not os.path.isdir(repo_root):
        fail(f"repo-root '{repo_root}' is not a directory.")

    self_check()

    recording_dirs = find_recording_dirs(repo_root)
    if not recording_dirs:
        print(f"Recording redaction: no {_RECORDINGS_DIR}/ tree under "
              f"'{repo_root}' -- nothing to scan (expected until the first "
              f"provider capture lands).")
        print("Recording redaction OK.")
        return

    scanned = 0
    findings = 0
    for recordings_dir in recording_dirs:
        for path in iter_files(recordings_dir):
            scanned += 1
            relative = os.path.relpath(path, repo_root)
            for name, line in scan_text(read_text(path)):
                findings += 1
                # The value is deliberately absent: the annotation must not
                # republish the secret it is reporting.
                print(f"::error file={relative},line={line}::recording-redaction: "
                      f"'{relative}' line {line} matches the '{name}' pattern.")

    print(f"Recording redaction: scanned {scanned} file(s) across "
          f"{len(recording_dirs)} {_RECORDINGS_DIR}/ tree(s).")

    if findings:
        fail(f"{findings} credential-shaped match(es) under {_RECORDINGS_DIR}/. "
             f"Recordings ship in a PUBLIC repo (ADR-0041 D5). Replace each value "
             f"with the documented placeholder "
             f"(docs/guides/provider-recording-protocol.md) -- and if a real "
             f"secret was ever committed, rotate it: removing it from the working "
             f"tree does not remove it from history.")

    print("Recording redaction OK.")


if __name__ == "__main__":
    main()

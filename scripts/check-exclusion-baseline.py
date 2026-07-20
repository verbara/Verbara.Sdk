#!/usr/bin/env python3
"""Fail CI on net-new coverage-exclusion markers (denominator guard).

Usage: check-exclusion-baseline.py <coverage-exclusion-baseline.json> [repo-root]

Coverage-gate-v2 denominator guard (verbara-meta/ADR-0013) — a direct copy of the
Sdk/ADR-0004 sync-fence-baseline pattern. Recounts coverage-exclusion markers across
the baseline's source glob and fails if the current total EXCEEDS the committed
baseline. Net-new exclusions fail the build; the baseline is lowered by hand only
(when an exclusion is deleted). This stops the "shrink the coverage denominator to
dodge both coverage gates" laundering vector.

Baseline schema (coverage-exclusion-baseline.json):
  "source_glob"       glob (relative to repo-root) of files to scan, e.g. "src/**/*.cs"
  "count"             committed baseline count of markers
  "min_scanned_files" liveness floor: the scan MUST see at least this many files
  "exclude_globs"     OPTIONAL list of globs (relative to repo-root) whose matches
                      are removed from the scan set — e.g. build output
                      ["src/**/obj/**", "src/**/bin/**"]. Keeps generated code out
                      of the count without any repo-specific logic in this script.
  "marker"            OPTIONAL regex for the marker; defaults to the .NET attribute
                      [ExcludeFromCodeCoverage]. Web sets this to the c8 ignore form,
                      keeping THIS script byte-identical across repos.

Liveness self-test (the SyncFence `MinimumScannedFiles` defense): if the glob
matches fewer than "min_scanned_files" files, the scan is mis-wired (wrong root /
wrong glob) and would false-green a wide-open tree — exit 1, loud.

Dependency-free (stdlib only). Exit codes: 0 = pass, 1 = over baseline / liveness trip.
"""
import glob
import json
import os
import re
import sys

# Default marker: the .NET [ExcludeFromCodeCoverage] attribute, with or without
# the redundant `Attribute` suffix and with optional namespace qualification.
# Web overrides via the baseline's "marker" key (e.g. the c8-ignore form).
_DEFAULT_MARKER = r"ExcludeFromCodeCoverage(Attribute)?"


def fail(message):
    print(f"::error::exclusion-baseline: {message}")
    sys.exit(1)


def main():
    if len(sys.argv) not in (2, 3):
        fail("usage: check-exclusion-baseline.py "
             "<coverage-exclusion-baseline.json> [repo-root]")

    baseline_path = sys.argv[1]
    repo_root = sys.argv[2] if len(sys.argv) == 3 else "."

    try:
        with open(baseline_path, encoding="utf-8") as handle:
            baseline = json.load(handle)
    except (OSError, json.JSONDecodeError) as exc:
        fail(f"cannot read baseline '{baseline_path}': {exc}")

    for key in ("source_glob", "count", "min_scanned_files"):
        if key not in baseline:
            fail(f"baseline '{baseline_path}' has no \"{key}\" key.")

    source_glob = baseline["source_glob"]
    committed = int(baseline["count"])
    min_scanned = int(baseline["min_scanned_files"])
    marker = re.compile(baseline.get("marker", _DEFAULT_MARKER))
    exclude_globs = baseline.get("exclude_globs", [])

    pattern = os.path.join(repo_root, source_glob)
    files = {p for p in glob.glob(pattern, recursive=True) if os.path.isfile(p)}
    for excl in exclude_globs:
        excluded = glob.glob(os.path.join(repo_root, excl), recursive=True)
        files.difference_update(excluded)
    files = sorted(files)

    # Liveness: a scan that sees almost no files would false-green a wide tree.
    if len(files) < min_scanned:
        fail(f"scanned only {len(files)} files under '{pattern}' "
             f"(min {min_scanned}) — the scan is mis-wired (wrong root or glob). "
             f"Refusing to read false-green.")

    total = 0
    for path in files:
        try:
            with open(path, encoding="utf-8", errors="replace") as handle:
                text = handle.read()
        except OSError as exc:
            fail(f"cannot read source file '{path}': {exc}")
        total += len(marker.findall(text))

    print(f"Coverage-exclusion markers: {total}  "
          f"(baseline {committed}, {len(files)} files scanned under '{source_glob}')")

    if total > committed:
        fail(f"{total} coverage-exclusion markers exceed the committed baseline "
             f"{committed} (net-new {total - committed}). New exclusions shrink "
             f"the coverage denominator. If this is intentional, justify it in "
             f"review and raise \"count\" to {total}; otherwise remove the "
             f"[ExcludeFromCodeCoverage] you added.")
    if total < committed:
        print(f"Note: current {total} is below baseline {committed}; you may lower "
              f"\"count\" to {total} to tighten the ratchet.")
    print("Exclusion baseline OK.")


if __name__ == "__main__":
    main()

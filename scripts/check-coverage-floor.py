#!/usr/bin/env python3
"""Backstop coverage gate: a two-sided band floor over merged aggregate coverage.

Usage: check-coverage-floor.py <merged-cobertura.xml> <coverage-floor.json>

Coverage-gate-v2 backstop (verbara-meta/ADR-0013, clause b — the aggregate must
not silently rot OR go stale). Reads the ReportGenerator-merged Cobertura
`line-rate` / `branch-rate` (0..1) and `lines-valid`, compares to the committed
floor (percent, 0..100), and fails on ANY of:

  * line-band FLOOR breach   : line_pct < floor["line"]            (regression)
  * line-band CEILING breach : line_pct > floor["line"] + floor["slack"]
                               (the committed floor has gone stale — the remedy
                               is printed: raise "line" to floor(measured) in
                               this PR. The build forces the hand-raise that
                               operator habit used to be trusted for.)
  * branch  FLOOR breach     : branch_pct < floor["branch"]        (BLOCKING now)
  * denominator collapse     : lines-valid < floor["lines_valid_min"]
                               (a shrunk measurement can't false-green)

Dependency-free (stdlib only) by design, like the fail-below-only predecessor.
This file is BYTE-IDENTICAL across all repos that adopt ADR-0013.
Exit codes: 0 = pass (inside every bound), 1 = any breach or a malformed report.
"""
import json
import math
import sys
import xml.etree.ElementTree as ET


def fail(message):
    print(f"::error::coverage-floor: {message}")
    sys.exit(1)


def _pct(root, attr):
    raw = root.get(attr)
    if raw is None:
        fail(f"Cobertura report has no '{attr}' attribute — the merged report is "
             f"empty or malformed. Refusing to read false-green.")
    try:
        return round(float(raw) * 100, 2)
    except ValueError:
        fail(f"Cobertura '{attr}'='{raw}' is not a number — malformed report.")


def _int(root, attr):
    raw = root.get(attr)
    if raw is None:
        fail(f"Cobertura report has no '{attr}' attribute — the merged report is "
             f"empty or malformed. Refusing to read false-green.")
    try:
        return int(raw)
    except ValueError:
        fail(f"Cobertura '{attr}'='{raw}' is not an integer — malformed report.")


def _required(floor, key):
    if key not in floor:
        fail(f"floor file has no \"{key}\" key — coverage-gate-v2 schema requires "
             f"line, slack, branch, lines_valid_min (and patch for the patch gate).")
    return floor[key]


def main():
    if len(sys.argv) != 3:
        fail("usage: check-coverage-floor.py <merged-cobertura.xml> <coverage-floor.json>")

    cobertura, floor_file = sys.argv[1], sys.argv[2]

    try:
        root = ET.parse(cobertura).getroot()
    except (OSError, ET.ParseError) as exc:
        fail(f"cannot parse Cobertura report '{cobertura}': {exc}")

    try:
        with open(floor_file, encoding="utf-8") as handle:
            floor = json.load(handle)
    except (OSError, json.JSONDecodeError) as exc:
        fail(f"cannot read floor file '{floor_file}': {exc}")

    line_floor = float(_required(floor, "line"))
    slack = float(_required(floor, "slack"))
    branch_floor = float(_required(floor, "branch"))
    lines_valid_min = int(_required(floor, "lines_valid_min"))
    ceiling = line_floor + slack

    line_pct = _pct(root, "line-rate")
    branch_pct = _pct(root, "branch-rate")
    lines_valid = _int(root, "lines-valid")

    print(f"Line coverage:   {line_pct}%  (band [{line_floor}, {ceiling}])")
    print(f"Branch coverage: {branch_pct}%  (floor {branch_floor}%, blocking)")
    print(f"Lines measured:  {lines_valid}  (min {lines_valid_min})")

    breached = False

    # Denominator collapse first: a shrunk measurement makes every % suspect.
    if lines_valid < lines_valid_min:
        print(f"::error::Measured executable lines {lines_valid} < floor "
              f"{lines_valid_min}. The coverage denominator collapsed — a "
              f"shrunk measurement can read false-green. Investigate what "
              f"stopped being measured (new exclusion, dropped assembly).")
        breached = True

    if line_pct < line_floor:
        print(f"::error::Line coverage {line_pct}% is below the floor "
              f"{line_floor}% (regression).")
        breached = True
    elif line_pct > ceiling:
        print(f"::error::Line coverage {line_pct}% exceeds the band ceiling "
              f"{ceiling}% (floor {line_floor}% + slack {slack}%): the committed "
              f"floor is stale. Remedy: raise \"line\" to floor(measured)="
              f"{math.floor(line_pct)} in this PR.")
        breached = True

    if branch_pct < branch_floor:
        print(f"::error::Branch coverage {branch_pct}% is below the floor "
              f"{branch_floor}% (regression).")
        breached = True

    if breached:
        sys.exit(1)
    print("Coverage band OK.")


if __name__ == "__main__":
    main()

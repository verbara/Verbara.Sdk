#!/usr/bin/env python3
"""Backstop coverage gate: a two-sided band floor over merged aggregate coverage.

Usage: check-coverage-floor.py <report> <coverage-floor.json>

  <report>  the merged coverage report. Format is auto-detected by file name /
            content:
              *.xml                      -> ReportGenerator-merged Cobertura
              *summary*.json / *.json    -> vitest v8 coverage-summary.json (Web)

Coverage-gate-v2 backstop (verbara-meta/ADR-0013, clause b — the aggregate must
not silently rot OR go stale). Reads line% / branch% / lines-valid from whichever
report format is present, compares to the committed floor (percent, 0..100), and
fails on ANY of:

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
The Cobertura reading path + the comparison core are BYTE-IDENTICAL to the .NET
repos that adopt ADR-0013; Web ONLY adds a coverage-summary.json reader and a
format dispatch in front of the shared core (so a later copy of this file back
onto Sdk/Pro/Platform is a no-op on their behavior).
Exit codes: 0 = pass (inside every bound), 1 = any breach or a malformed report.
"""
import json
import math
import os
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


def read_cobertura(report_path):
    """The .NET path: parse ReportGenerator's merged Cobertura and return
    (line_pct, branch_pct, lines_valid). BYTE-IDENTICAL to the shared reader —
    a later re-sync of this file onto Sdk/Pro/Platform is a no-op here."""
    try:
        root = ET.parse(report_path).getroot()
    except (OSError, ET.ParseError) as exc:
        fail(f"cannot parse Cobertura report '{report_path}': {exc}")

    line_pct = _pct(root, "line-rate")
    branch_pct = _pct(root, "branch-rate")
    lines_valid = _int(root, "lines-valid")
    return line_pct, branch_pct, lines_valid


def _summary_pct(total, group, report_path):
    section = total.get(group)
    if not isinstance(section, dict) or "pct" not in section:
        fail(f"coverage-summary.json '{report_path}' total.{group}.pct is missing "
             f"— the vitest report is empty or malformed. Refusing to read "
             f"false-green.")
    try:
        return round(float(section["pct"]), 2)
    except (TypeError, ValueError):
        fail(f"coverage-summary.json total.{group}.pct='{section.get('pct')}' is "
             f"not a number — malformed report.")


def _summary_int(total, group, field, report_path):
    section = total.get(group)
    if not isinstance(section, dict) or field not in section:
        fail(f"coverage-summary.json '{report_path}' total.{group}.{field} is "
             f"missing — the vitest report is empty or malformed. Refusing to "
             f"read false-green.")
    try:
        return int(section[field])
    except (TypeError, ValueError):
        fail(f"coverage-summary.json total.{group}.{field}="
             f"'{section.get(field)}' is not an integer — malformed report.")


def read_coverage_summary(report_path):
    """The Web path: read vitest v8's coverage-summary.json and return
    (line_pct, branch_pct, lines_valid). Maps total.lines.pct -> line%,
    total.branches.pct -> branch%, total.lines.total -> lines_valid, then hands
    those three numbers to the SAME comparison core the Cobertura path uses."""
    try:
        with open(report_path, encoding="utf-8") as handle:
            doc = json.load(handle)
    except (OSError, json.JSONDecodeError) as exc:
        fail(f"cannot parse coverage-summary.json '{report_path}': {exc}")

    total = doc.get("total")
    if not isinstance(total, dict):
        fail(f"coverage-summary.json '{report_path}' has no 'total' object — the "
             f"vitest report is empty or malformed. Refusing to read false-green.")

    line_pct = _summary_pct(total, "lines", report_path)
    branch_pct = _summary_pct(total, "branches", report_path)
    lines_valid = _summary_int(total, "lines", "total", report_path)
    return line_pct, branch_pct, lines_valid


def detect_format(report_path):
    """Autodetect by file name, falling back to content sniffing. *.xml -> the
    Cobertura path; *summary*.json / any *.json holding a 'total' object -> the
    vitest coverage-summary path. Extend here; the comparison core is untouched."""
    lower = os.path.basename(report_path).lower()
    if lower.endswith(".xml"):
        return "cobertura"
    if lower.endswith(".json"):
        return "coverage-summary"
    # Content sniff for an unfamiliar extension: XML declaration -> Cobertura.
    try:
        with open(report_path, encoding="utf-8", errors="replace") as handle:
            head = handle.read(4096).lstrip()
    except OSError as exc:
        fail(f"cannot read coverage report '{report_path}': {exc}")
    if head.startswith("<"):
        return "cobertura"
    if head.startswith("{"):
        return "coverage-summary"
    fail(f"cannot autodetect coverage format from report '{report_path}' "
         f"(expected a *.xml Cobertura or a *.json vitest coverage-summary).")


def main():
    if len(sys.argv) != 3:
        fail("usage: check-coverage-floor.py <report> <coverage-floor.json>")

    report_path, floor_file = sys.argv[1], sys.argv[2]

    if not os.path.isfile(report_path):
        fail(f"coverage report '{report_path}' not found.")

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

    fmt = detect_format(report_path)
    if fmt == "cobertura":
        line_pct, branch_pct, lines_valid = read_cobertura(report_path)
    else:
        line_pct, branch_pct, lines_valid = read_coverage_summary(report_path)

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

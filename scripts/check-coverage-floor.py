#!/usr/bin/env python3
"""Fail CI if merged line coverage drops below the committed ratchet floor.

Usage: check-coverage-floor.py <merged-cobertura.xml> <coverage-floor.json>

Reads the ReportGenerator-merged Cobertura `line-rate` (0..1), compares it to
the floor (percent, 0..100) from the floor file, and exits non-zero if below.
Branch coverage is printed as advisory only (non-blocking).
"""
import json
import sys
import xml.etree.ElementTree as ET

cobertura, floor_file = sys.argv[1], sys.argv[2]
root = ET.parse(cobertura).getroot()
line_pct = round(float(root.get("line-rate")) * 100, 2)
branch_pct = round(float(root.get("branch-rate")) * 100, 2)
floor = float(json.load(open(floor_file))["line"])

print(f"Line coverage:   {line_pct}%  (floor {floor}%)")
print(f"Branch coverage: {branch_pct}%  (advisory, non-blocking)")
if line_pct < floor:
    print(f"::error::Line coverage {line_pct}% is below the ratchet floor {floor}%.")
    sys.exit(1)
print("Coverage floor OK.")

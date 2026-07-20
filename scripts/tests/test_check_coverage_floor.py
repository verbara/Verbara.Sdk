"""Unit tests for check-coverage-floor.py (the two-sided band backstop).

Runs the script as a subprocess against tmp Cobertura + floor fixtures and asserts
exit codes + failure grammar. Stdlib unittest only — NO pip deps (ADR-0013).
"""
import json
import os
import subprocess
import sys
import tempfile
import unittest

_HERE = os.path.dirname(os.path.abspath(__file__))
_SCRIPT = os.path.join(_HERE, os.pardir, "check-coverage-floor.py")

# A floor that puts the band at [78, 81], branch floor 64, lines_valid_min 12315.
_FLOOR = {
    "line": 78,
    "slack": 3,
    "branch": 64,
    "lines_valid_min": 12315,
    "patch": 85,
}


def _cobertura(line_rate, branch_rate, lines_valid):
    return (
        f'<?xml version="1.0"?>\n'
        f'<coverage line-rate="{line_rate}" branch-rate="{branch_rate}" '
        f'lines-valid="{lines_valid}" lines-covered="1" '
        f'branches-valid="1" branches-covered="1" version="1" timestamp="0">\n'
        f'  <sources><source>/repo</source></sources>\n'
        f'  <packages/>\n'
        f'</coverage>\n'
    )


class CheckCoverageFloorTests(unittest.TestCase):
    def setUp(self):
        self._tmp = tempfile.mkdtemp()
        self.floor_path = os.path.join(self._tmp, "floor.json")
        with open(self.floor_path, "w", encoding="utf-8") as handle:
            json.dump(_FLOOR, handle)

    def tearDown(self):
        for name in os.listdir(self._tmp):
            os.unlink(os.path.join(self._tmp, name))
        os.rmdir(self._tmp)

    def _write_report(self, text):
        path = os.path.join(self._tmp, "Cobertura.xml")
        with open(path, "w", encoding="utf-8") as handle:
            handle.write(text)
        return path

    def _run(self, report_path, floor_path=None):
        return subprocess.run(
            [sys.executable, _SCRIPT, report_path, floor_path or self.floor_path],
            capture_output=True, text=True,
        )

    def test_ShouldPass_WhenLineInsideBand(self):
        # line 80.42% (in [78,81]), branch 66% (>=64), lines-valid 12964 (>=12315)
        report = self._write_report(_cobertura(0.8042, 0.66, 12964))
        result = self._run(report)
        self.assertEqual(result.returncode, 0, result.stdout + result.stderr)
        self.assertIn("Coverage band OK.", result.stdout)

    def test_ShouldFail_WhenLineBelowFloor(self):
        # line 77% < floor 78%
        report = self._write_report(_cobertura(0.77, 0.66, 12964))
        result = self._run(report)
        self.assertEqual(result.returncode, 1)
        self.assertIn("below the floor", result.stdout)

    def test_ShouldFailWithRemedy_WhenLineAboveCeiling(self):
        # line 85% > ceiling 81% -> stale floor; remedy must name floor(measured)=85
        report = self._write_report(_cobertura(0.85, 0.66, 12964))
        result = self._run(report)
        self.assertEqual(result.returncode, 1)
        self.assertIn("exceeds the band ceiling", result.stdout)
        self.assertIn('raise "line" to floor(measured)=85', result.stdout)

    def test_ShouldFail_WhenBranchBelowFloor(self):
        # branch 60% < floor 64% (line + lines-valid fine)
        report = self._write_report(_cobertura(0.80, 0.60, 12964))
        result = self._run(report)
        self.assertEqual(result.returncode, 1)
        self.assertIn("Branch coverage", result.stdout)
        self.assertIn("below the floor", result.stdout)

    def test_ShouldFail_WhenLinesValidBelowMinimum(self):
        # lines-valid 100 < min 12315 (denominator collapse) even with fine %s
        report = self._write_report(_cobertura(0.80, 0.66, 100))
        result = self._run(report)
        self.assertEqual(result.returncode, 1)
        self.assertIn("denominator collapsed", result.stdout)

    def test_ShouldFail_WhenReportEmptyOrMissingAttributes(self):
        report = self._write_report(
            '<?xml version="1.0"?><coverage version="1"></coverage>'
        )
        result = self._run(report)
        self.assertEqual(result.returncode, 1)
        self.assertIn("malformed", result.stdout.lower() + result.stderr.lower())

    def test_ShouldFail_WhenReportIsGarbage(self):
        report = self._write_report("this is not xml { at all")
        result = self._run(report)
        self.assertEqual(result.returncode, 1)
        self.assertIn("cannot parse", result.stdout)


def _summary(line_pct, branch_pct, lines_total):
    """A minimal vitest v8 coverage-summary.json 'total' object. Only the fields
    the band reader consumes are asserted-on; the rest mirror vitest's shape."""
    return json.dumps({
        "total": {
            "lines": {"total": lines_total, "covered": 1, "skipped": 0,
                      "pct": line_pct},
            "statements": {"total": lines_total, "covered": 1, "skipped": 0,
                           "pct": line_pct},
            "functions": {"total": 1, "covered": 1, "skipped": 0, "pct": 100},
            "branches": {"total": 1, "covered": 1, "skipped": 0,
                         "pct": branch_pct},
        }
    })


class CheckCoverageFloorSummaryTests(unittest.TestCase):
    """The Web-only json path: check-coverage-floor.py reading vitest v8's
    coverage-summary.json instead of Cobertura. Same band + branch-blocking +
    lines_valid_min logic, same failure-message grammar — the report format is
    the only difference. total.lines.pct -> line%, total.branches.pct -> branch%,
    total.lines.total -> lines_valid."""

    def setUp(self):
        self._tmp = tempfile.mkdtemp()
        self.floor_path = os.path.join(self._tmp, "floor.json")
        with open(self.floor_path, "w", encoding="utf-8") as handle:
            json.dump(_FLOOR, handle)

    def tearDown(self):
        for name in os.listdir(self._tmp):
            os.unlink(os.path.join(self._tmp, name))
        os.rmdir(self._tmp)

    def _write_report(self, text, name="coverage-summary.json"):
        path = os.path.join(self._tmp, name)
        with open(path, "w", encoding="utf-8") as handle:
            handle.write(text)
        return path

    def _run(self, report_path):
        return subprocess.run(
            [sys.executable, _SCRIPT, report_path, self.floor_path],
            capture_output=True, text=True,
        )

    def test_ShouldPass_WhenLineInsideBand(self):
        # line 80.42% (in [78,81]), branch 66% (>=64), lines-total 12964 (>=12315)
        report = self._write_report(_summary(80.42, 66, 12964))
        result = self._run(report)
        self.assertEqual(result.returncode, 0, result.stdout + result.stderr)
        self.assertIn("Coverage band OK.", result.stdout)

    def test_ShouldFail_WhenLineBelowFloor(self):
        # line 77% < floor 78%
        report = self._write_report(_summary(77, 66, 12964))
        result = self._run(report)
        self.assertEqual(result.returncode, 1)
        self.assertIn("below the floor", result.stdout)

    def test_ShouldFailWithRemedy_WhenLineAboveCeiling(self):
        # line 85% > ceiling 81% -> stale floor; remedy must name floor(measured)=85
        report = self._write_report(_summary(85, 66, 12964))
        result = self._run(report)
        self.assertEqual(result.returncode, 1)
        self.assertIn("exceeds the band ceiling", result.stdout)
        self.assertIn('raise "line" to floor(measured)=85', result.stdout)

    def test_ShouldFail_WhenBranchBelowFloor(self):
        # branch 60% < floor 64% (line + lines-total fine)
        report = self._write_report(_summary(80, 60, 12964))
        result = self._run(report)
        self.assertEqual(result.returncode, 1)
        self.assertIn("Branch coverage", result.stdout)
        self.assertIn("below the floor", result.stdout)

    def test_ShouldFail_WhenLinesValidBelowMinimum(self):
        # lines-total 100 < min 12315 (denominator collapse) even with fine %s
        report = self._write_report(_summary(80, 66, 100))
        result = self._run(report)
        self.assertEqual(result.returncode, 1)
        self.assertIn("denominator collapsed", result.stdout)

    def test_ShouldPass_WhenNamePlainJsonButHasTotal(self):
        # A *.json without 'summary' in the name still dispatches to the json path
        # (extension-based detection): band OK.
        report = self._write_report(_summary(80.42, 66, 12964),
                                    name="coverage.json")
        result = self._run(report)
        self.assertEqual(result.returncode, 0, result.stdout + result.stderr)
        self.assertIn("Coverage band OK.", result.stdout)

    def test_ShouldFail_WhenSummaryMissingTotal(self):
        report = self._write_report('{"src/x.ts": {"lines": {"pct": 80}}}')
        result = self._run(report)
        self.assertEqual(result.returncode, 1)
        self.assertIn("no 'total' object", result.stdout)

    def test_ShouldFail_WhenSummaryMissingMetric(self):
        # total present but no lines.pct -> malformed, refuse false-green
        report = self._write_report('{"total": {"branches": {"pct": 66}}}')
        result = self._run(report)
        self.assertEqual(result.returncode, 1)
        self.assertIn("malformed", result.stdout.lower())

    def test_ShouldFail_WhenSummaryIsGarbage(self):
        report = self._write_report("this is not json { at all")
        result = self._run(report)
        self.assertEqual(result.returncode, 1)
        self.assertIn("cannot parse coverage-summary.json", result.stdout)


if __name__ == "__main__":
    unittest.main()
